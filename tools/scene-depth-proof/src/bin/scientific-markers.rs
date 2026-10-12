use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{env, fs::{self, OpenOptions}, io::Write};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
fn check(ok: bool, why: &str) -> Result<()> { if ok { Ok(()) } else { Err(why.into()) } }
fn n(v: &Value) -> Result<f64> { v.as_f64().filter(|v| v.is_finite()).ok_or("Finite scalar missing".into()) }
fn vec3(v: &Value) -> Result<[f64;3]> { let a=v.as_array().filter(|a|a.len()==3).ok_or("Three-vector missing")?; Ok([n(&a[0])?,n(&a[1])?,n(&a[2])?]) }
fn hash(b: &[u8]) -> String { format!("{:x}",Sha256::digest(b)) }
fn close(a:f64,b:f64,t:f64)->Result<()> {check((a-b).abs()<=t,"Frozen numeric readback differs")}
fn digest(v:&Value)->Result<()> {check(v.as_str().is_some_and(|s|s.len()==64&&s.bytes().all(|b|b.is_ascii_hexdigit())),"Digest missing")}
fn dot(a:[f64;3],b:[f64;3])->f64 {a.iter().zip(b).map(|(x,y)|x*y).sum()}
fn pixels(bytes:&[u8],w:usize,h:usize)->Result<Vec<[u8;3]>> {
 let mut d=png::Decoder::new(bytes);d.set_transformations(png::Transformations::EXPAND);let mut r=d.read_info()?;
 check(r.info().width as usize==w&&r.info().height as usize==h,"PNG dimensions differ")?;
 let mut b=vec![0;r.output_buffer_size()];let i=r.next_frame(&mut b)?;
 check(i.bit_depth==png::BitDepth::Eight,"Eight-bit image required")?;
 let c=match i.color_type {png::ColorType::Rgb=>3,png::ColorType::Rgba=>4,_=>return Err("RGB image required".into())};
 Ok(b[..i.buffer_size()].chunks_exact(c).map(|v|[v[0],v[1],v[2]]).collect())
}
fn cyan(p:[u8;3])->bool {p[0]<=155&&p[1]>=185&&p[2]>=210&&p[1].saturating_sub(p[0])>=65&&p[2].saturating_sub(p[0])>=80}
fn cross(image:&[[u8;3]],w:usize,h:usize,x:f64,y:f64)->Result<Value> {
 let mut horizontal=0;let mut vertical=0;let mut hits=0;
 for yy in (y.floor() as i32-7)..=(y.ceil() as i32+7) {for xx in (x.floor() as i32-7)..=(x.ceil() as i32+7) {
  if xx<0||yy<0||xx>=w as i32||yy>=h as i32 {continue;} let dx=xx as f64+0.5-x;let dy=yy as f64+0.5-y;
  if cyan(image[yy as usize*w+xx as usize]) {if dx.abs()<=6.&&dy.abs()<=3. {horizontal+=1;}if dy.abs()<=6.&&dx.abs()<=3. {vertical+=1;}if dx.abs()<=3.&&dy.abs()<=3. {hits+=1;}}
 }}
 check(horizontal>=4&&vertical>=4&&hits>=2,"Cyan cross absent near independent center")?;
 Ok(json!({"center":[x,y],"centerRadiusPixels":3,"horizontalCyanPixels":horizontal,"verticalCyanPixels":vertical,"centralCyanPixels":hits}))
}
fn proof(b:&[Vec<u8>])->Result<Value> {
 let o:Value=serde_json::from_slice(&b[1])?;let p:Value=serde_json::from_slice(&b[3])?;let plan:Value=serde_json::from_slice(&b[4])?;
 check(plan["schema"]=="continuum-scientific-native-plan/v1","Plan schema differs")?;
 for k in ["sceneSha256","sourceRecordingSha256"] {digest(&plan[k])?;}
 let w=n(&plan["width"])? as usize;let h=n(&plan["height"])? as usize;
 check(w==1920&&h==1080,"Frozen viewport differs")?;
 let yaw=n(&plan["yawDegrees"])?;let pitch=n(&plan["pitchDegrees"])?;let distance=n(&plan["distanceMeters"])?;let span=n(&plan["verticalSpanMeters"])?;let fov=n(&plan["perspectiveFovDegrees"])?;
 close(yaw,-41.,0.)?;close(pitch,0.,0.)?;close(distance,398.10708313250577,1e-10)?;close(span,400.,0.)?;close(fov,60.,0.)?;
 let focus=vec3(&plan["focus"])?;let yr=yaw.to_radians();let pr=pitch.to_radians();
 let forward=[pr.cos()*yr.sin(),-pr.sin(),pr.cos()*yr.cos()];let right=[yr.cos(),0.,-yr.sin()];let up=[forward[1]*right[2]-forward[2]*right[1],forward[2]*right[0]-forward[0]*right[2],forward[0]*right[1]-forward[1]*right[0]];
 let camera=[focus[0]-distance*forward[0],focus[1]-distance*forward[1],focus[2]-distance*forward[2]];
 let native_basis_position_bound=4.*f32::EPSILON as f64*distance;
 for (s,kind) in [(&o,"ortho"),(&p,"perspective")] {
  check(s["schema"]=="continuum-multiscale-workbench/v1"&&s["status"]=="resident"&&(s["error"].is_null()||s["error"]==""),"Native status failed")?;
  check(s["projection"]==kind&&s["width"]==w&&s["height"]==h,"Native lens/viewport differs")?;
  for (k,v,t) in [("yaw",yaw,1e-5),("pitch",pitch,1e-5),("distance",distance,1e-8),("fov",fov,1e-5)] {close(n(&s[k])?,v,t)?;}
  if kind=="ortho" {close(n(&s["verticalSpanMeters"])?,span,1e-5)?;}
  for (a,e) in vec3(&s["cameraAnchorMeters"])?.iter().zip(camera) {close(*a,e,native_basis_position_bound)?;}
  for (a,e) in vec3(&s["focusTargetMeters"])?.iter().zip(focus) {close(*a,e,1e-5)?;}
  check(s["sceneSha256"]==plan["sceneSha256"]&&s["trailInstalled"]==true&&s["trail"]["paused"]==true&&s["trail"]["recordingSha256"]==plan["sourceRecordingSha256"],"Source/history admission differs")?;
  check(s["scientificOverlay"]["schema"]=="continuum-scientific-overlay/v1"&&s["scientificOverlay"]["viewportWidth"]==w&&s["scientificOverlay"]["viewportHeight"]==h,"Overlay viewport differs")?;
  check(s["scientificOverlay"]["depthPolicy"]=="overlay-first-unoccluded","Overlay depth scope differs")?;
 }
 check(o["processId"].as_u64().is_some_and(|v|v>0),"PID missing")?;
 for k in ["processId","hostId","assemblySha256","unity","backend","sceneSha256","wholeCraftSha256","wholeGeometrySha256","bodyGeometrySha256","bodyTextureSha256","vesselFixtureSha256","focus","focusInstance","vesselGeneration","view","wholeRenderMode","debugBundleSha256","preset","context"] {check(!o[k].is_null()||p[k].is_null(),"Native identity absent")?;check(o[k]==p[k],"Native identity/control changed")?;}
 for k in ["hostId","assemblySha256"] {check(o[k].as_str().is_some_and(|s|!s.is_empty()),"Host/assembly missing")?;}
 digest(&o["assemblySha256"])?;
 for k in ["simulationTime","recordingSha256","recordingCursor","generation","live","emitted","recordedRootScene","oldestPositionMeters","newestPositionMeters","representation","visible","windX","windY","windZ","emission"] {check(!o["trail"][k].is_null()&&o["trail"][k]==p["trail"][k],"Paused trail/source changed")?;}
 check(o["scientificOverlay"]["rows"]==p["scientificOverlay"]["rows"]&&o["scientificOverlay"]["generation"]==p["scientificOverlay"]["generation"],"Annotation state changed")?;
 let markers=plan["markers"].as_array().filter(|a|a.len()==4).ok_or("Four frozen markers required")?;
 let images=[pixels(&b[0],w,h)?,pixels(&b[2],w,h)?];let mut centers=[Vec::new(),Vec::new()];let mut receipts=Vec::new();let mut ids=std::collections::HashSet::new();
 for m in markers {
  let id=m["id"].as_str().ok_or("Marker ID missing")?;check(ids.insert(id),"Duplicate marker ID")?;let pos=vec3(&m["position"])?;
  let rows=o["scientificOverlay"]["rows"].as_array().ok_or("Annotation readback missing")?;let matches:Vec<_>=rows.iter().filter(|r|r["id"]==id).collect();check(matches.len()==1,"Marker readback ambiguous/missing")?;let row=matches[0];
  check(row["kind"]=="Marker"&&row["label"]==m["label"]&&row["valid"]==true&&row["validity"]=="valid","Marker semantics differ")?;
  for (a,e) in vec3(&row["a"])?.iter().zip(pos) {close(*a,e,1e-8)?;}
  let rel=[pos[0]-camera[0],pos[1]-camera[1],pos[2]-camera[2]];let x=dot(rel,right);let y=dot(rel,up);let z=dot(rel,forward);check(z>0.,"Marker behind camera")?;
  let mut views=Vec::new();for arm in 0..2 {let scale=if arm==0 {h as f64/span} else {h as f64/(2.*(fov.to_radians()/2.).tan()*z)};let center=[w as f64/2.+x*scale,h as f64/2.-y*scale];let key=if arm==0 {"expectedOrthoPixel"} else {"expectedPerspectivePixel"};let expected=m[key].as_array().filter(|a|a.len()==2).ok_or("Frozen pixel pair missing")?;
   for i in 0..2 {close(center[i],n(&expected[i])?,1e-6)?;}check(center[0]>=8.&&center[0]<(w-8) as f64&&center[1]>=8.&&center[1]<(h-8) as f64,"Marker outside viewport")?;centers[arm].push(center);views.push(cross(&images[arm],w,h,center[0],center[1])?);
  }receipts.push(json!({"id":id,"label":m["label"],"position":pos,"eyeDepth":z,"views":views}));
 }
 let spans=|c:&Vec<[f64;2]>|[(c[1][0]-c[0][0]).hypot(c[1][1]-c[0][1]),(c[3][0]-c[2][0]).hypot(c[3][1]-c[2][1])];let os=spans(&centers[0]);let ps=spans(&centers[1]);close(os[0],os[1],1e-6)?;check((ps[0]-ps[1]).abs()>10.,"Perspective spans are not independently unequal")?;
 Ok(json!({"status":"passed","scope":"Authored scientific overlay placement only; no depth, simulation, gameplay or predictor qualification","processId":o["processId"],"hostId":o["hostId"],"assemblySha256":o["assemblySha256"],"sceneSha256":plan["sceneSha256"],"sourceRecordingSha256":plan["sourceRecordingSha256"],"camera":camera,"nativeBasisPositionBoundMeters":native_basis_position_bound,"nativeBasisPositionBoundDerivation":"4 float epsilons times camera distance; Unity quaternion basis is float, frozen analytic basis is double","basis":{"right":right,"up":up,"forward":forward},"markers":receipts,"orthographicSpansPixels":os,"perspectiveSpansPixels":ps,"pixelCriterion":"Cyan cross central radius3px, >=2 central pixels and >=4 pixels on each 6px arm; RGB R<=155,G>=185,B>=210,G-R>=65,B-R>=80; allows GUI gamma but requires cyan contrast"}))
}
fn main()->Result<()> {
 let a:Vec<_>=env::args().skip(1).collect();check(a.len()==6,"Usage: scientific-markers ORTHO_PNG ORTHO_STATUS PERSPECTIVE_PNG PERSPECTIVE_STATUS FROZEN_PLAN NEW_OUTPUT")?;
 check(!std::path::Path::new(&a[5]).exists(),"Output must be new")?;
 let mut b=Vec::new();for path in &a[..5] {let meta=fs::metadata(path)?;check(meta.len()<=128*1024*1024,"Input bound exceeded")?;b.push(fs::read(path)?);}
 let result=proof(&b);let inputs:Vec<_>=a[..5].iter().zip(&b).map(|(p,b)|json!({"path":p,"sha256":hash(b)})).collect();
 let receipt=match &result {Ok(v)=>json!({"schema":"continuum-independent-scientific-markers/v1","inputs":inputs,"proof":v,"status":"passed"}),Err(e)=>json!({"schema":"continuum-independent-scientific-markers/v1","inputs":inputs,"status":"failed","error":e.to_string()})};
 let mut f=OpenOptions::new().write(true).create_new(true).open(&a[5])?;f.write_all(&serde_json::to_vec_pretty(&receipt)?)?;result.map(|_|())
}
