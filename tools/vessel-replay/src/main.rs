use std::{collections::HashSet, env, fs, io, time::Instant};

type Result<T> = std::result::Result<T, String>;
struct Cursor<'a> { bytes: &'a [u8], at: usize }
impl<'a> Cursor<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        let end=self.at.checked_add(n).ok_or("Offset overflow")?;
        let value=self.bytes.get(self.at..end).ok_or("Truncated recording")?;self.at=end;Ok(value)
    }
    fn u32(&mut self)->Result<u32>{Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))}
    fn i64(&mut self)->Result<i64>{Ok(i64::from_le_bytes(self.take(8)?.try_into().unwrap()))}
    fn number(&mut self)->Result<f64>{let v=f64::from_le_bytes(self.take(8)?.try_into().unwrap());if !v.is_finite(){return Err("Nonfinite value".into())}Ok(v)}
    fn count(&mut self,max:usize)->Result<usize>{let n=self.u32()? as usize;if n==0||n>max{return Err("Capacity invalid".into())}Ok(n)}
    fn identity(&mut self)->Result<String>{let n=self.count(2048)?;let s=std::str::from_utf8(self.take(n)?).map_err(|_|"Invalid UTF-8")?;if s.chars().any(char::is_control){return Err("Identity contains controls".into())}Ok(s.into())}
    fn matrix(&mut self,root:bool)->Result<()> {
        let mut m=[0.0;16];for v in &mut m{*v=self.number()?;}
        if m[12]!=0.0||m[13]!=0.0||m[14]!=0.0||m[15]!=1.0||root&&(m[3]!=0.0||m[7]!=0.0||m[11]!=0.0){return Err("Pose not affine or root translated".into())}
        let d=m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]);
        if !d.is_finite()||d.abs()<=1e-30{return Err("Singular pose".into())}Ok(())
    }
}
fn analyze(loads:&[[f64;6]])->Result<([f64;3],[f64;3])>{
    let mut f=[0.0;3];let mut m=[0.0;3];
    for &[x,y,z,fx,fy,fz] in loads{
        f[0]+=fx;f[1]+=fy;f[2]+=fz;m[0]+=y*fz-z*fy;m[1]+=z*fx-x*fz;m[2]+=x*fy-y*fx;
    }
    if f.iter().chain(m.iter()).any(|v|!v.is_finite()){return Err("Result overflow".into())}Ok((f,m))
}
fn execute(path:&str)->Result<String>{
    let started=Instant::now();let length=fs::metadata(path).map_err(|e|e.to_string())?.len();if length>512*1024*1024{return Err("Recording too large".into())}
    let bytes=fs::read(path).map_err(|e|e.to_string())?;if bytes.len() as u64!=length{return Err("Recording changed while reading".into())}
    let mut r=Cursor{bytes:&bytes,at:0};if r.u32()?!=0x31525643||r.u32()?!=1{return Err("Schema differs".into())}
    let _session=r.identity()?;let _vessel=r.identity()?;let digest=r.identity()?;if digest.len()!=64||!digest.bytes().all(|c|c.is_ascii_digit()||(b'a'..=b'f').contains(&c)){return Err("Invalid manifest digest".into())}
    if r.i64()?<=0{return Err("Invalid topology".into())}
    let draws=r.count(8192)?;let mut ids=HashSet::new();for _ in 0..draws{if !ids.insert(r.identity()?){return Err("Duplicate draw".into())}}
    let parts=r.count(4096)?;let mut part_ids=HashSet::new();for _ in 0..parts{let id=r.u32()?;if id==0||!part_ids.insert(id){return Err("Invalid part ID".into())}}
    let frames=r.count(512)?;let frame_bytes=168+draws*129+parts*48;if frame_bytes*frames!=bytes.len()-r.at{return Err("Encoded frame shape differs".into())}
    let mut previous=-1;let mut time=f64::NEG_INFINITY;let mut results=Vec::with_capacity(frames);let mut loads=vec![[0.0;6];parts];let mut analysis_ns=0u128;
    for _ in 0..frames {
        let sequence=r.i64()?;let ut=r.number()?;if sequence<=previous||ut<time{return Err("Stale frame".into())}previous=sequence;time=ut;
        r.matrix(true)?;let _altitude=r.number()?;let density=r.number()?;let speed=r.number()?;if density<0.0||speed<0.0{return Err("Negative environment".into())}
        for _ in 0..draws{r.matrix(false)?}if r.take(draws)?.iter().any(|&v|v>1){return Err("Invalid visibility".into())}
        for load in &mut loads{for v in load{*v=r.number()?}}
        let t=Instant::now();let (force,moment)=analyze(&loads)?;analysis_ns+=t.elapsed().as_nanos();
        results.push(format!("{{\"sequence\":{sequence},\"universalTime\":{ut},\"force\":{:?},\"moment\":{:?}}}",force,moment));
    }
    if r.at!=bytes.len(){return Err("Trailing bytes".into())}
    Ok(format!("{{\"schema\":\"continuum-vessel-native-analysis/v1\",\"status\":\"complete\",\"bytes\":{},\"draws\":{draws},\"parts\":{parts},\"frames\":{frames},\"analysisNanoseconds\":{analysis_ns},\"readValidateAnalyzeNanoseconds\":{},\"results\":[{}]}}",bytes.len(),started.elapsed().as_nanos(),results.join(",")))
}
fn main(){let args:Vec<_>=env::args().collect();if args.len()!=2{eprintln!("Usage: continuum-vessel-replay RECORDING.cvr");std::process::exit(2)}match execute(&args[1]){Ok(s)=>println!("{s}"),Err(e)=>{eprintln!("{}",io::Error::new(io::ErrorKind::InvalidData,e));std::process::exit(1)}}}
#[cfg(test)] mod tests{
    use super::*;
    #[test]fn analytic_force_couple(){let(f,m)=analyze(&[[2.0,0.0,0.0,0.0,3.0,0.0],[-2.0,0.0,0.0,0.0,-3.0,0.0]]).unwrap();assert_eq!(f,[0.0;3]);assert_eq!(m,[0.0,0.0,12.0]);}
    #[test]fn truncated_word_rejected(){let mut c=Cursor{bytes:&[0,0,0],at:0};assert!(c.u32().is_err());}
}
