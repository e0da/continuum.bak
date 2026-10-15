use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{env, fs::{self, OpenOptions}, io::Write, path::Path};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
fn require(ok: bool, message: &str) -> Result<()> { if ok { Ok(()) } else { Err(message.into()) } }
fn number(v: &Value) -> Result<f64> { v.as_f64().filter(|x| x.is_finite()).ok_or_else(|| "Finite number required".into()) }
fn vector(v: &Value) -> Result<[f64; 3]> {
    let a = v.as_array().filter(|a| a.len() == 3).ok_or("Three-vector required")?;
    Ok([number(&a[0])?, number(&a[1])?, number(&a[2])?])
}
fn hash(bytes: &[u8]) -> String { format!("{:x}", Sha256::digest(bytes)) }
fn near(a: [f64; 3], b: [f64; 3]) -> bool {
    a.iter().zip(b).map(|(x,y)| (x-y)*(x-y)).sum::<f64>().sqrt() <= 1e-5
}
fn dot(a: [f64; 3], b: [f64; 3]) -> f64 { a.iter().zip(b).map(|(x,y)| x*y).sum() }
fn decode(bytes: &[u8]) -> Result<(usize, usize, Vec<[u8; 3]>)> {
    let mut decoder = png::Decoder::new(bytes);
    decoder.set_transformations(png::Transformations::EXPAND);
    let mut reader = decoder.read_info()?;
    require(reader.info().width as u64 * reader.info().height as u64 <= 16_777_216, "PNG pixel bound exceeded")?;
    let mut buffer = vec![0; reader.output_buffer_size()];
    let info = reader.next_frame(&mut buffer)?;
    require(info.bit_depth == png::BitDepth::Eight, "Eight-bit PNG required")?;
    let channels = match info.color_type { png::ColorType::Rgb => 3, png::ColorType::Rgba => 4, _ => return Err("RGB/RGBA PNG required".into()) };
    Ok((info.width as usize, info.height as usize, buffer[..info.buffer_size()].chunks_exact(channels).map(|p| [p[0],p[1],p[2]]).collect()))
}
fn interpolate(rows: &[Value], t: f64, field: &str) -> Result<([f64; 3], usize)> {
    require(t >= number(&rows[0]["time"])? && t <= number(&rows[rows.len()-1]["time"])?, "Interpolation outside recording")?;
    let i = rows.partition_point(|r| r["time"].as_f64().unwrap() <= t).saturating_sub(1);
    let a = vector(&rows[i][field])?;
    if i+1 == rows.len() { return Ok((a,i)); }
    let b = vector(&rows[i+1][field])?;
    let ta = number(&rows[i]["time"])?;
    let u = (t-ta)/(number(&rows[i+1]["time"])?-ta);
    Ok(([a[0]+(b[0]-a[0])*u, a[1]+(b[1]-a[1])*u, a[2]+(b[2]-a[2])*u],i))
}
fn proof(paths: &[String], bytes: &[Vec<u8>]) -> Result<Value> {
    let off: Value = serde_json::from_slice(&bytes[2])?;
    let on: Value = serde_json::from_slice(&bytes[3])?;
    let wire: Value = serde_json::from_slice(&bytes[4])?;
    require(off["schema"] == "continuum-multiscale-workbench/v1" && wire["schema"] == "continuum-independent-emitter-wire-proof/v1" && wire["status"] == "passed", "Unsupported or failed source schema")?;
    require(off["processId"].as_u64().is_some_and(|p| p>0) && off["status"] == "resident" && off["error"] == "" && off["trailInstalled"] == true, "Native resident admission failed")?;
    for key in ["assemblySha256","sceneSha256","wholeCraftSha256"] {
        require(off[key].as_str().is_some_and(|s| s.len()==64 && s.bytes().all(|b| b.is_ascii_hexdigit())), "Bound native/source digest absent")?;
    }
    require(off["trail"]["visible"] == false && on["trail"]["visible"] == true, "Expected off/on visibility pair")?;
    let mut left = off.clone(); let mut right = on.clone();
    let incidental = ["bodyDraws","commands","craftDrawCalls","craftFrames","frames","lastPng","lastPngSha256","wholeSubmissionTicks"];
    let trail_incidental = ["compositionTicks","renderTicks","renderedFrames","submissionTicks","viewSetupTicks","visible"];
    for state in [&mut left,&mut right] {
        for key in incidental { state.as_object_mut().ok_or("Status object required")?.remove(key); }
        for key in trail_incidental { state["trail"].as_object_mut().ok_or("Trail object required")?.remove(key); }
    }
    require(left == right, "View, source, history or other non-incidental state changed")?;
    let trail = &on["trail"];
    for (a,b) in [("recordingSha256","recordingSha256"),("recordingRunId","runId"),("recordingProcessId","processId"),("recordingFrames","frames"),("recordingMode","mode")] {
        require(trail[a] == wire[b] && !trail[a].is_null(), "Decoded recording identity differs")?;
    }
    for key in ["sceneSha256","wholeCraftSha256"] { require(on[key] == wire[key] && !on[key].is_null(), "Scene identity differs")?; }
    require(trail["paused"] == true && trail["emission"] == true && trail["probeInstalled"] == false && trail["recordingMode"] == "trajectory-tracer", "Paused measured tracer history required")?;
    for key in ["windX","windY","windZ"] { require(number(&trail[key])? == 0., "Zero wind required for independent worldline oracle")?; }
    let rows = wire["rows"].as_array().filter(|a| !a.is_empty() && a.len()<=4096).ok_or("Bounded decoded rows required")?;
    require(wire["frames"].as_u64() == Some(rows.len() as u64), "Wire row count differs")?;
    for (i,row) in rows.iter().enumerate() {
        require(row["sequence"].as_u64() == Some(i as u64) && row["emission"] == true, "Ordered emitting trajectory rows required")?;
        let time = number(&row["time"])?; vector(&row["rootScene"])?; vector(&row["emitterScene"])?;
        require(if i==0 {time==0.} else {time>number(&rows[i-1]["time"])?}, "Non-monotonic recording")?;
    }
    let time = number(&trail["simulationTime"])?;
    let cadence = number(&trail["emissionCadenceSeconds"])?;
    let lifetime = number(&trail["lifetimeSeconds"])?;
    let width0 = number(&trail["initialWidthMeters"])?;
    let diffusion = number(&trail["diffusionSquareMetersPerSecond"])?;
    require(time>=0. && cadence>0. && lifetime>time && width0>0. && diffusion>=0., "Unsupported history parameters")?;
    let (root,cursor) = interpolate(rows,time,"rootScene")?;
    require(near(root,vector(&trail["recordedRootScene"])?) && near(root,vector(&on["focusTargetMeters"])?) && trail["recordingCursor"].as_u64()==Some(cursor as u64), "Root/cursor interpolation differs")?;
    let mut births = Vec::new();
    for index in 1..=4096 {
        let birth = index as f64 * cadence;
        if birth>time { break; }
        births.push((birth,interpolate(rows,birth,"emitterScene")?.0));
    }
    require(!births.is_empty() && time/cadence<4096. && trail["live"].as_u64()==Some(births.len() as u64) && trail["emitted"].as_u64()==Some(births.len() as u64) && trail["expired"]==0 && trail["capacityEvictions"]==0, "Derived exact cadence births differ")?;
    require(near(births[0].1,vector(&trail["oldestPositionMeters"])?) && near(births.last().unwrap().1,vector(&trail["newestPositionMeters"])?) && (number(&trail["oldestAgeSeconds"])?-(time-births[0].0)).abs()<=1e-12 && (number(&trail["newestAgeSeconds"])?-(time-births.last().unwrap().0)).abs()<=1e-12, "Birth endpoint/age witness differs")?;
    let (w,h,off_pixels)=decode(&bytes[0])?; let (ow,oh,on_pixels)=decode(&bytes[1])?;
    require((w,h)==(ow,oh) && on["width"].as_u64()==Some(w as u64) && on["height"].as_u64()==Some(h as u64), "Resolution differs")?;
    for (i,status) in [(0,&off),(1,&on)] {
        require(status["lastPngSha256"].as_str()==Some(hash(&bytes[i]).as_str()) && Path::new(&paths[i]).file_name().and_then(|p|p.to_str())==status["lastPng"].as_str(), "PNG capture identity differs")?;
    }
    let camera=vector(&on["cameraAnchorMeters"])?;
    let yaw=number(&on["yaw"] )?.to_radians(); let pitch=number(&on["pitch"] )?.to_radians();
    let fov=number(&on["fov"])?; require(fov>0. && fov<180., "Invalid camera FOV")?;
    let forward=[pitch.cos()*yaw.sin(),-pitch.sin(),pitch.cos()*yaw.cos()];
    let right=[yaw.cos(),0.,-yaw.sin()];
    let up=[forward[1]*right[2]-forward[2]*right[1],forward[2]*right[0]-forward[0]*right[2],forward[0]*right[1]-forward[1]*right[0]];
    let focal=h as f64/(2.*(fov.to_radians()/2.).tan());
    let mut markers=Vec::new(); let mut neighborhoods=Vec::new();
    for (index,(birth,point)) in births.iter().enumerate() {
        let relative=[point[0]-camera[0],point[1]-camera[1],point[2]-camera[2]];
        let depth=dot(relative,forward); let width=(width0*width0+2.*diffusion*(time-birth)).sqrt();
        if depth<=0. { markers.push(json!({"birthIndex":index+1,"birthTime":birth,"positionMeters":point,"eyeDepth":depth,"inbounds":false})); continue; }
        let x=w as f64/2.+dot(relative,right)*focal/depth;
        let y=h as f64/2.-dot(relative,up)*focal/depth;
        let radius=width*focal/depth; let neighborhood=radius+1.+0.5;
        let inbounds=x+neighborhood>=0. && y+neighborhood>=0. && x-neighborhood<w as f64 && y-neighborhood<h as f64;
        let mut count=0;
        if inbounds {
            neighborhoods.push((x,y,neighborhood));
            for yy in ((y-neighborhood).floor().max(0.) as usize)..=((y+neighborhood).ceil().min(h as f64-1.) as usize) {
                for xx in ((x-neighborhood).floor().max(0.) as usize)..=((x+neighborhood).ceil().min(w as f64-1.) as usize) {
                    if !(xx<450 && yy<410) && off_pixels[yy*w+xx]!=on_pixels[yy*w+xx] { count+=1; }
                }
            }
        }
        markers.push(json!({"birthIndex":index+1,"birthTime":birth,"ageSeconds":time-birth,"positionMeters":point,"eyeDepth":depth,"centerPixelsTopLeft":[x,y],"widthMeters":width,"radiusPixels":radius,"neighborhoodHalfExtentPixels":neighborhood,"inbounds":inbounds,"changedRgbPixelsInNeighborhood":count}));
    }
    let mut changes=Vec::new(); let mut matched=0;
    for y in 0..h { for x in 0..w {
        let at=y*w+x;
        if (x<450 && y<410) || off_pixels[at]==on_pixels[at] {continue;}
        let inside=neighborhoods.iter().any(|(cx,cy,r)| (x as f64)>=(*cx-r).floor() && (x as f64)<=(*cx+r).ceil() && (y as f64)>=(*cy-r).floor() && (y as f64)<=(*cy+r).ceil());
        if inside {matched+=1;}
        changes.push(json!({"x":x,"y":y,"offRgb":off_pixels[at],"onRgb":on_pixels[at],"inPredictedNeighborhood":inside}));
    }}
    Ok(json!({"status":if matched>0 {"passed"} else {"failed"},"error":if matched>0 {""} else {"No changed RGB pixel inside independently predicted birth neighborhoods"},"ownedPid":on["processId"],"assemblySha256":on["assemblySha256"],"sceneSha256":on["sceneSha256"],"recordingSha256":trail["recordingSha256"],"sourceRunId":wire["runId"],"sourceProcessId":wire["processId"],"simulationTime":time,"derivedBirthCount":births.len(),"predictedRootScene":root,"cursor":cursor,"width":w,"height":h,"markers":markers,"changedRgbPixelsOutsideHud":changes.len(),"changedRgbPixelsInPredictedNeighborhoods":matched,"changes":changes,"incidentalTopFieldsExcluded":incidental,"incidentalTrailFieldsExcluded":trail_incidental}))
}
fn main() -> Result<()> {
    let args:Vec<String>=env::args().skip(1).collect(); require(args.len()==6,"OFF_PNG ON_PNG OFF_STATUS ON_STATUS DECODED_WIRE NEW_OUTPUT required")?;
    let mut bytes=Vec::new();
    for path in &args[..5] {require(fs::metadata(path)?.len()<=128*1024*1024,"Input size bound exceeded")?; bytes.push(fs::read(path)?);}
    let inputs:Vec<Value>=args[..5].iter().zip(&bytes).map(|(p,b)|json!({"path":p,"sha256":hash(b),"bytes":b.len()})).collect();
    let mut report=match proof(&args,&bytes) {Ok(v)=>v,Err(e)=>json!({"status":"failed","error":e.to_string()})};
    for (path,original) in args[..5].iter().zip(&bytes) {
        if fs::read(path)? != *original {report["status"]=json!("failed");report["error"]=json!("Input changed during proof");}
    }
    report["schema"]=json!("continuum-independent-recorded-trail-pixel-proof/v1"); report["inputs"]=json!(inputs);
    report["positionToleranceMeters"]=json!(1e-5); report["subpixelProjectionAllowancePixels"]=json!(0.5);
    report["scope"]=json!("Independent decoded worldline interpolation and exact integer-cadence births; zero-wind paused tracer; conservative billboard bounding neighborhoods plus one raster pixel and half-pixel projection allowance; changed decoded RGB outside HUD. Some predicted particles may be clipped or occluded. No general occlusion, plume microphysics, deformation, CPU/GPU performance or whole-scene pixel-equivalence claim.");
    let mut output=OpenOptions::new().write(true).create_new(true).open(&args[5])?;
    writeln!(output,"{}",serde_json::to_string_pretty(&report)?)?;
    require(report["status"]=="passed","Recorded trail pixel proof failed; see new output")
}
