use serde_json::{json,Value};
use sha2::{Digest,Sha256};
use std::{env,fs::{self,OpenOptions},io::Write};
type Result<T> = std::result::Result<T,Box<dyn std::error::Error>>;
fn require(value:bool,message:&str)->Result<()> {if value {Ok(())}else{Err(message.into())}}
fn values(value:&Value,count:usize)->Result<Vec<f64>> {
    let array=value.as_array().ok_or("Numeric array absent")?;
    require(array.len()==count,"Numeric array shape differs")?;
    array.iter().map(|v|v.as_f64().filter(|v|v.is_finite()).ok_or_else(||"Finite numeric value required".into())).collect()
}
fn project(point:&[f64],view:&[f64],fov:f64,aspect:f64)->[f64;3] {
    let x=view[0]*point[0]+view[1]*point[1]+view[2]*point[2]+view[3];
    let y=view[4]*point[0]+view[5]*point[1]+view[6]*point[2]+view[7];
    let depth=-(view[8]*point[0]+view[9]*point[1]+view[10]*point[2]+view[11]);
    let half=depth*(fov.to_radians()/2.).tan();
    [0.5+x/(2.*half*aspect),0.5+y/(2.*half),depth]
}
fn edge_distance(a:[f64;2],b:[f64;2],point:[f64;2])->f64 {
    let dx=b[0]-a[0];let dy=b[1]-a[1];
    (dx*(point[1]-a[1])-dy*(point[0]-a[0]))/(dx*dx+dy*dy).sqrt()
}
fn clip_reachable(point:&[f64],view:&[f64],projection:&[f64])->bool {
    let mut eye=[0.;4]; let mut clip=[0.;4];
    for r in 0..4 {eye[r]=view[r*4]*point[0]+view[r*4+1]*point[1]+view[r*4+2]*point[2]+view[r*4+3];}
    for r in 0..4 {clip[r]=(0..4).map(|c|projection[r*4+c]*eye[c]).sum();}
    clip.iter().all(|v|v.is_finite())&&clip[3]>0.&&clip[0].abs()<clip[3]&&clip[1].abs()<clip[3]&&clip[2]>=0.&&clip[2]<=clip[3]
}
fn main()->Result<()> {
    let args:Vec<_>=env::args().collect(); require(args.len()==4,"PNG STATUS NEW_OUTPUT required")?;
    let bytes=fs::read(&args[1])?;let status_bytes=fs::read(&args[2])?;
    let state:Value=serde_json::from_slice(&status_bytes)?;let p=&state["trail"]["probe"];
    require(p["depthSource"]=="actual-scene-rendertextures"&&state["trail"]["probeInstalled"]==true,"Actual scene probe not installed")?;
    require(state["status"]=="resident" || state["status"]=="ready","Native workbench not ready")?;
    require(state["processId"].as_u64().is_some_and(|v|v>0),"Owned native PID absent")?;
    for hash in [&state["assemblySha256"],&p["sceneSha256"],&p["wholeGeometrySha256"],&p["bodyGeometrySha256"]] {
        require(hash.as_str().is_some_and(|v|v.len()==64&&v.bytes().all(|b|b.is_ascii_hexdigit())),"Bound SHA256 absent")?;
    }
    require(p["craftTextureId"].as_i64().is_some_and(|v|v!=0)&&p["bodyTextureId"].as_i64().is_some_and(|v|v!=0),"Typed scene depth identities absent")?;
    let mut decoder=png::Decoder::new(bytes.as_slice());decoder.set_transformations(png::Transformations::EXPAND|png::Transformations::STRIP_16);
    let mut reader=decoder.read_info()?;let mut pixels=vec![0;reader.output_buffer_size()];let info=reader.next_frame(&mut pixels)?;
    let channels=match info.color_type {png::ColorType::Rgb=>3,png::ColorType::Rgba=>4,_=>return Err("RGB/RGBA PNG required".into())};
    let width=info.width as f64;let height=info.height as f64;
    require(p["width"]==info.width&&p["height"]==info.height&&p["craftTextureId"]!=p["bodyTextureId"],"Viewport or distinct producer identities differ")?;
    let view=values(&p["actualView"],16)?;let centers=values(&p["centerUvDepth"],12)?;
    let gpu_projection=values(&p["actualGpuProjection"],16)?;
    let actual=values(&p["actualCentersCameraRelative"],12)?;let depths=values(&p["actualEyeDepths"],4)?;
    let near=values(&p["producerNearFar"],4)?;
    let fov=p["actualFov"].as_f64().ok_or("FOV absent")?;let aspect=p["actualAspect"].as_f64().ok_or("Aspect absent")?;
    require(fov>0.&&fov<180.&&(aspect-width/height).abs()<0.01,"Camera controls differ")?;
    let triangles=p["triangles"].as_array().filter(|v|v.len()==2).ok_or("Two real scene triangles required")?;
    let radius=p["radiusScreenHeight"].as_f64().ok_or("Radius absent")?*height;
    require(radius>=3.99&&radius<=5.,"Resolvable disk radius differs")?;
    let mut samples=Vec::new();let mut all_matched=true;
    for domain in 0..2 {
        let triangle=&triangles[domain];let world=values(&triangle["worldVertices"],9)?;let recorded=values(&triangle["projectedUvDepth"],9)?;
        require(triangle["domain"]==if domain==0{"craft"}else{"body"},"Triangle domain differs")?;
        let projected:Vec<_>=world.chunks_exact(3).map(|point|project(point,&view,fov,aspect)).collect();
        for i in 0..3 {require(((projected[i][0]-recorded[i*3])*width).abs()<=0.5&&((projected[i][1]-recorded[i*3+1])*height).abs()<=0.5
            &&(projected[i][2]-recorded[i*3+2]).abs()<=0.001+recorded[i*3+2].abs()*2e-6,"Independent triangle projection differs")?;}
        let maximum_depth=projected.iter().map(|v|v[2]).fold(0.,f64::max);
        let points:Vec<_>=projected.iter().map(|v|[v[0]*width,v[1]*height]).collect();
        for side in 0..2 {
            let marker=domain*2+side;let base=marker*3;let center=[centers[base]*width,centers[base+1]*height];
            let edges:Vec<_>=(0..3).map(|i|edge_distance(points[i],points[(i+1)%3],center)).collect();
            require(edges.iter().all(|v|*v>radius+0.5)||edges.iter().all(|v|*v < -radius-0.5),"Marker disk not independently covered by real triangle")?;
            let projected_center=project(&actual[base..base+3],&view,fov,aspect);
            require(clip_reachable(&actual[base..base+3],&view,&gpu_projection),"Marker clipped instead of depth-tested")?;
            require(((projected_center[0]-centers[base])*width).abs()<=0.5&&((projected_center[1]-centers[base+1])*height).abs()<=0.5
                &&(projected_center[2]-depths[marker]).abs()<0.001+depths[marker].abs()*2e-6,"Actual billboard projection differs")?;
            let visible=side==0;
            require(if visible {depths[marker]>0.001&&depths[marker]<near[0]&&depths[marker]<near[2]} else {depths[marker]>maximum_depth},"Independent front/behind depth condition differs")?;
            let x=center[0].floor() as usize;let y=(height-center[1]).floor() as usize;
            require(x>=1&&y>=1&&x+1<info.width as usize&&y+1<info.height as usize,"Center sample outside image")?;
            let mut core=Vec::new();let mut matched=true;
            for yy in y-1..=y+1 {for xx in x-1..=x+1 {
                let at=(yy*info.width as usize+xx)*channels;let rgb=[pixels[at],pixels[at+1],pixels[at+2]];
                let marker_color=match marker {0=>rgb[1]>160&&rgb[0]<100&&rgb[2]<100,1=>rgb[0]>160&&rgb[1]<100&&rgb[2]<100,
                    2=>rgb[1]>160&&rgb[2]>160&&rgb[0]<100,_=>rgb[0]>160&&rgb[1]>160&&rgb[2]<100};
                matched&=if visible {marker_color}else{!marker_color};core.push(json!({"x":xx,"y":yy,"rgb":rgb}));
            }}
            all_matched&=matched;samples.push(json!({"domain":triangle["domain"],"triangle":triangle["triangle"],"marker":marker,"visibleFromGeometry":visible,
                "centerPixels":center,"radiusPixels":radius,"triangleMaximumDepth":maximum_depth,"eyeDepth":depths[marker],"matched":matched,"core":core}));
        }
    }
    require(fs::read(&args[1])?==bytes&&fs::read(&args[2])?==status_bytes,"Input changed during decoding")?;
    let result=json!({"schema":"continuum-actual-scene-trail-pixel-proof/v1","status":if all_matched{"passed"}else{"failed"},"ownedPid":state["processId"],
        "playerSha256":state["assemblySha256"],"sceneSha256":p["sceneSha256"],"wholeGeometrySha256":p["wholeGeometrySha256"],"bodyGeometrySha256":p["bodyGeometrySha256"],
        "pngSha256":format!("{:x}",Sha256::digest(&bytes)),"statusSha256":format!("{:x}",Sha256::digest(&status_bytes)),"width":info.width,"height":info.height,"samples":samples,
        "screenProjectionTolerancePixels":0.5,"scope":"Independent double projection of reported real world triangles and billboard centers, conservative whole-disk coverage/depth and actual 3x3 PNG color checks. Bound scene geometry provenance, static view, matching Unity/Metal and opaque domains only; not GPU timing or general camera/material/viewport qualification."});
    let mut file=OpenOptions::new().create_new(true).write(true).open(&args[3])?;write!(file,"{}",serde_json::to_string_pretty(&result)?)?;
    println!("Scene geometry and four actual depth marker checks: {}",if all_matched{"passed"}else{"failed"});require(all_matched,"Actual native pixels differ")
}
