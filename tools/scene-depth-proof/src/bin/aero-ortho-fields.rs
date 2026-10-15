use serde_json::{json,Value};
use sha2::{Digest,Sha256};
use std::{env,fs::{self,OpenOptions},io::Write,path::Path};
type Result<T>=std::result::Result<T,Box<dyn std::error::Error>>;
const W:usize=960;const H:usize=540;
fn require(ok:bool,s:&str)->Result<()> {if ok {Ok(())}else{Err(s.into())}}
fn n(v:&Value)->Result<f64>{v.as_f64().filter(|x|x.is_finite()).ok_or("Finite scalar required".into())}
fn near(a:f64,b:f64)->Result<()> {require((a-b).abs()<=1e-5,"Frozen recipe/matrix differs")}
fn sha(b:&[u8])->String {format!("{:x}",Sha256::digest(b))}
fn array(v:&Value,expected:&[f64])->Result<()> {let a=v.as_array().filter(|a|a.len()==expected.len()).ok_or("Recipe array missing")?;for (x,y) in a.iter().zip(expected){near(n(x)?,*y)?;}Ok(())}
fn read(v:&Value,channels:usize,parent:&Path)->Result<(Vec<f32>,Value)> {
 require(v["width"]==W&&v["height"]==H&&v["channels"]==channels,"Raw field dimensions differ")?;
 require(v["format"]==if channels==4{"RGBAFloat"}else{"RFloat"},"Raw texture format differs")?;
 let declared=Path::new(v["path"].as_str().ok_or("Raw path missing")?);let path=if declared.is_absolute(){declared.to_path_buf()}else{parent.join(declared)};
 let bytes_expected=W*H*channels*4;require(fs::metadata(&path)?.len()==bytes_expected as u64&&v["byteLength"].as_u64()==Some(bytes_expected as u64),"Raw length differs")?;
 let b=fs::read(&path)?;require(b.len()==bytes_expected&&v["sha256"]==sha(&b),"Raw digest/length differs")?;
 let floats:Vec<_>=b.chunks_exact(4).map(|b|f32::from_le_bytes([b[0],b[1],b[2],b[3]])).collect();require(floats.iter().all(|x|x.is_finite()),"Raw nonfinite value")?;
 Ok((floats,json!({"path":path,"sha256":sha(&b),"bytes":b.len()})))
}
// Orthographic axis-aligned +Z rays; nearest original unit-box front plane.
fn oracle(x:usize,y:usize)->(Option<f64>,bool) {
 let wx=((x as f64+0.5)/W as f64-0.5)*8.0*W as f64/H as f64;
 let wy=-0.5+((y as f64+0.5)/H as f64-0.5)*8.0;
 let boxes=[[-1.0,0.0,0.0],[1.0,0.0,0.0],[0.0,-1.0,0.0],[0.0,0.0,2.0]];
 let margin=2.0*8.0/H as f64;let mut edge=false;let mut nearest:Option<f64>=None;
 for c in boxes {let dx=(wx-c[0]).abs();let dy=(wy-c[1]).abs();
  // Exclude either side of every projected box edge, conservatively including rear hidden edges.
  if ((dx-0.5).abs()<=margin&&dy<=1.5+margin)||((dy-1.5).abs()<=margin&&dx<=0.5+margin){edge=true;}
  if dx<0.5&&dy<1.5 {let z=c[2]-0.25+11.0;nearest=Some(nearest.map_or(z,|a|a.min(z)));}
 }(nearest,edge)
}
fn proof(r:&Value,parent:&Path)->Result<Value> {
 require(r["schema"]=="continuum-aero-field-readback/v1"&&r["status"]=="completed"&&r["formatVersion"]==1,"Readback admission failed")?;
 require(r["renderer"]=="Metal"&&r["orthographic"]==true&&r["reversedZ"]==true&&r["graphicsUVStartsAtTop"]==true,"Frozen Metal projection differs")?;
 require(r["geometrySource"]=="analytic-concave-shielded-not-KSP","Actual analytic fixture required")?;
 require(r["rawRowOrder"]=="Unity ReadPixels bottom-left origin; rows bottom-to-top; columns left-to-right"&&r["rawEncoding"]=="IEEE754 binary32 little-endian; no header; tightly packed channel order RGBA or R"&&r["matrixLayout"]=="row-major 16 floats","Raw layout differs")?;
 require(r["processId"].as_u64().is_some_and(|v|v>0)&&n(&r["endedTicks"])? >= n(&r["startedTicks"])?&&n(&r["clockFrequency"])? >0.0,"Capture provenance missing")?;
 for k in ["assemblySha256","bundleSha256"]{require(r[k].as_str().is_some_and(|s|s.len()==64&&s.bytes().all(|b|b.is_ascii_hexdigit())),"Source hash missing")?;}
 near(n(&r["verticalSpanMeters"])?,8.0)?;near(n(&r["near"])?,0.1)?;near(n(&r["far"])?,100.0)?;
 array(&r["cameraPosition"],&[0.0,-0.5,-11.0])?;array(&r["cameraRotation"],&[0.0,0.0,0.0,1.0])?;
 array(&r["geometryCenter"],&[0.0,-0.5,1.0])?;array(&r["geometryExtents"],&[1.5,2.0,1.25])?;
 array(&r["worldToView"],&[1.0,0.0,0.0,0.0,0.0,1.0,0.0,0.5,0.0,0.0,-1.0,-11.0,0.0,0.0,0.0,1.0])?;
 let sx=2.0/(8.0*W as f64/H as f64);let sy=0.25;let dz=99.9;
 array(&r["cameraProjection"],&[sx,0.0,0.0,0.0,0.0,sy,0.0,0.0,0.0,0.0,-2.0/dz,-100.1/dz,0.0,0.0,0.0,1.0])?;
 // Metal GPU RT projection flips Y and maps view -Z to reversed device [1,0]. Logical ReadPixels still bottom-left.
 array(&r["gpuProjection"],&[sx,0.0,0.0,0.0,0.0,-sy,0.0,0.0,0.0,0.0,1.0/dz,100.0/dz,0.0,0.0,0.0,1.0])?;
 let abi=if let Some(a)=r.get("orthographicShaderValues"){array(a,&[1.0,1.0,1.0])?;"receipt-exact-three"}else{"not-in-readback-receipt; external source/status gate required"};
 let (field,fi)=read(&r["field"],4,parent)?;let (scene,si)=read(&r["scene"],1,parent)?;
 let mut background=0usize;let mut front=0usize;let mut rear=0usize;let mut skipped=0usize;let mut max_field=0f64;let mut max_scene=0f64;let mut max_normal=0f64;let mut bad=0usize;let mut flipped_bad=0usize;let mut first=Value::Null;
 for y in 0..H {for x in 0..W {let (expect,edge)=oracle(x,y);if edge {skipped+=1;continue;}let i=y*W+x;let fa=field[i*4+3] as f64;let sd=scene[i] as f64;let expected=expect.unwrap_or(0.0);
  let ef=(fa-expected).abs();let es=(sd-expected).abs();max_field=max_field.max(ef);max_scene=max_scene.max(es);
  let en=if expect.is_some(){[0.5,0.5,0.0]}else{[0.0,0.0,0.0]};let mut normal_error=0f64;for c in 0..3{normal_error=normal_error.max((field[i*4+c] as f64-en[c]).abs());}max_normal=max_normal.max(normal_error);
  let failed=ef>1e-3||es>1e-3||normal_error>1e-5;if failed {bad+=1;if first.is_null(){first=json!({"pixelBottomLeft":[x,y],"expectedDepth":expected,"actualFieldDepth":fa,"actualSceneDepth":sd,"actualNormal":&field[i*4..i*4+3]});}}
  let (opposite,opposite_edge)=oracle(x,H-1-y);if !opposite_edge&&((fa-opposite.unwrap_or(0.0)).abs()>1e-3||(sd-opposite.unwrap_or(0.0)).abs()>1e-3){flipped_bad+=1;}
  match expect {None=>background+=1,Some(z) if z<11.0=>front+=1,Some(_)=>rear+=1}
 }}
 require(background>0&&front>0&&rear>0,"Both near and exposed rear regions required")?;
 let report=json!({"scope":"Frozen four opaque boxes, orthographic Metal logical ReadPixels orientation, positive scene/field eye depth and front normals only; no gameplay/general occlusion qualification","status":if bad==0{"passed"}else{"failed"},"badPixels":bad,"firstMismatch":first,"oppositeRowMappingMismatchPixels":flipped_bad,"backgroundPixels":background,"nearFacePixels":front,"exposedRearFacePixels":rear,"edgeSkippedPixels":skipped,"maxFieldDepthErrorMeters":max_field,"maxSceneDepthErrorMeters":max_scene,"maxEncodedNormalError":max_normal,"depthToleranceMeters":0.001,"normalTolerance":0.00001,"nearFaceDepthMeters":10.75,"rearFaceDepthMeters":12.75,"rawInputs":[fi,si],"shaderAbiAdmission":abi,"processId":r["processId"],"assemblySha256":r["assemblySha256"],"bundleSha256":r["bundleSha256"]});
 Ok(report)
}
fn main()->Result<()> {
 let a:Vec<_>=env::args().skip(1).collect();require(a.len()==2,"Usage: aero-ortho-fields RECEIPT NEW_OUTPUT")?;require(!Path::new(&a[1]).exists(),"Output must be new")?;
 let bytes=fs::read(&a[0])?;require(bytes.len()<=1024*1024,"Receipt bound exceeded")?;let r:Value=serde_json::from_slice(&bytes)?;
 let result=proof(&r,Path::new(&a[0]).parent().ok_or("Receipt parent missing")?);let output=match &result {Ok(p)=>json!({"schema":"continuum-independent-aero-ortho-fields/v1","receiptSha256":sha(&bytes),"proof":p,"status":p["status"]}),Err(e)=>json!({"schema":"continuum-independent-aero-ortho-fields/v1","receiptSha256":sha(&bytes),"status":"failed","error":e.to_string()})};
 let mut f=OpenOptions::new().write(true).create_new(true).open(&a[1])?;f.write_all(&serde_json::to_vec_pretty(&output)?)?;
 let p=result?;require(p["status"]=="passed","Independent raw field pixel proof failed")
}
