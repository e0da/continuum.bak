use crate::{hash, owned_report, read, Result};
use serde_json::{json, Value};
use std::{collections::BTreeSet, fs, io::Write, path::Path};

fn number(v: &Value, key: &str) -> Result<f64> {
    v[key].as_f64().filter(|n| n.is_finite()).ok_or_else(|| format!("reload number missing: {key}"))
}
fn rows<'a>(v: &'a Value, key: &str) -> Result<&'a Vec<Value>> {
    v[key].as_array().ok_or_else(|| format!("reload rows missing: {key}"))
}
fn vector_error(a: &Value, b: &Value) -> Result<f64> {
    let a = a.as_array().filter(|v| v.len() == 3).ok_or("reload state vector missing")?;
    let b = b.as_array().filter(|v| v.len() == 3).ok_or("reload state vector missing")?;
    let mut sum = 0.;
    for (a,b) in a.iter().zip(b) {
        let a = a.as_f64().filter(|n| n.is_finite()).ok_or("nonfinite reload vector")?;
        let b = b.as_f64().filter(|n| n.is_finite()).ok_or("nonfinite reload vector")?;
        sum += (a-b)*(a-b);
    }
    Ok(sum.sqrt())
}

pub fn validate(r: &Value, run: &str, pid: u32, expected: u32, source: &str) -> Result<()> {
    if r["schema"] != "continuum-checkpoint-reload/v1" || r["runId"] != run || r["processId"] != pid
        || r["sourceSha256"] != source || r["status"] != "complete-experimental" || r["faults"] != 0
        || r["expectedParts"] != expected
        || !r["reason"].is_null() || r["cleanupStatus"] != "removed-owned-hooks-locks-restored-camera"
        || ["sourceUnchanged","savedUnchanged","newVesselObject","newPartObjects","identityRestored","resourcesRestored","crewRestored","modulesRestored","fieldsRestored","orbitRestored","timeRestored","departureObserved","scenarioModulesUpdated","postLoadNotified","hooksRemoved","controlLockRemoved","cameraRestored"].iter().any(|k| r[*k] != true)
        || r["cameraCallbacks"].as_u64().unwrap_or(0) == 0
        || number(r,"positionToleranceMeters")? != 0.01 || number(r,"velocityToleranceMetersPerSecond")? != 0.00001
        || number(r,"resourceTolerance")? != 0.000001 || number(r,"fieldAbsoluteTolerance")? != 0.000001
        || number(r,"fieldRelativeTolerance")? != 0.000001 || number(r,"maximumReloadSimulationSeconds")? != 2.
    { return Err("checkpoint reload ownership or frozen behavior contract failed; raw evidence retained".into()); }
    if !rows(r,"admissionFailures")?.is_empty() || !(0.0..=60.).contains(&number(r,"settleSeconds")?) {
        return Err("reload quiet admission or declared settling failed".into());
    }
    if r["savedStartScene"]!="FLIGHT" || r["loadedStartScene"]!="FLIGHT" || r["loadedCompatible"]!=true || r["aboutToQuicksaveNotified"]!=true
        || rows(r,"memberships")?.is_empty() || r["memberships"]!=r["loadedMemberships"] {
        return Err("reload saved scene or whole-game partition differs".into());
    }
    let a = &r["before"]; let b = &r["after"];
    if a["vesselId"] != b["vesselId"] || a["bodyName"] != b["bodyName"] || a["bodyName"].as_str().is_none()
        || a["vesselId"].as_str().is_none_or(|s| s.len()!=36)
        || a["persistentId"] != b["persistentId"] || a["instanceId"] == b["instanceId"]
        || number(a,"instanceId")? == 0. || number(b,"instanceId")? == 0.
    { return Err("reload vessel identity or reconstructed owner failed".into()); }
    let elapsed = number(b,"universalTime")? - number(a,"universalTime")?;
    if !(0.0..=2.).contains(&elapsed) || (elapsed-number(r,"reloadSimulationSeconds")?).abs()>1e-9
        || vector_error(&a["orbitPosition"],&b["orbitPosition"])? > 0.01
        || vector_error(&a["orbitVelocity"],&b["orbitVelocity"])? > 0.00001
        || (number(r,"departureDeltaVMetersPerSecond")?-0.25).abs()>0.00001
    { return Err("reload orbit/time or deliberate departure witness failed".into()); }
    let ap=rows(a,"parts")?; let bp=rows(b,"parts")?;
    if ap.len()!=expected as usize || ap.len()!=bp.len() || ap.is_empty() { return Err("reload part count differs".into()); }
    let mut ids = BTreeSet::new();
    for (a,b) in ap.iter().zip(bp) {
        let id=a["flightId"].as_u64().filter(|id|*id>0 && *id<=u32::MAX as u64).ok_or("reload part identity missing")?;
        if !ids.insert(id) || ["flightId","persistentId","parentFlightId","name","crew","modules"].iter().any(|k|a[*k]!=b[*k])
            || a["instanceId"]==b["instanceId"] || number(a,"instanceId")?==0. || number(b,"instanceId")?==0.
            || a["name"].as_str().is_none_or(|s|s.is_empty()) || a["crew"].as_array().is_none() || a["modules"].as_array().is_none()
        { return Err("reload part/crew/module ownership differs".into()); }
    }
    let mut shutdowns = BTreeSet::new();
    for engine in rows(r,"shutdownEngines")? {
        let id = engine["partId"].as_u64().ok_or("reload shutdown part missing")?;
        let index = engine["moduleIndex"].as_u64().ok_or("reload shutdown module missing")? as usize;
        let part = ap.iter().find(|p|p["flightId"]==id).ok_or("reload shutdown part not in snapshot")?;
        let modules = rows(part,"modules")?;
        if !shutdowns.insert((id,index)) || engine["beforeIgnited"]!=true || engine["afterIgnited"]!=false
            || modules.get(index).and_then(Value::as_str).is_none_or(|name|!name.starts_with("ModuleEngines,") && !name.starts_with("ModuleEnginesFX,")) {
            return Err("reload stock engine shutdown readback failed".into());
        }
    }
    let af=rows(a,"fields")?; let bf=rows(b,"fields")?;
    if af.len()!=bf.len() || a["excludedFields"]!=b["excludedFields"] { return Err("reload field census differs".into()); }
    let mut fields=BTreeSet::new();
    for (a,b) in af.iter().zip(bf) {
        let key=a["key"].as_str().filter(|s|!s.is_empty()).ok_or("reload field key missing")?;
        if !fields.insert(key) || a["key"]!=b["key"] || a["numeric"]!=b["numeric"]
            || if a["numeric"]==true { (number(a,"number")?-number(b,"number")?).abs()>1e-6+1e-6*number(a,"number")?.abs() } else {a["numeric"]!=false || a["value"]!=b["value"]}
        { return Err("reload persistent field differs".into()); }
    }
    let ar=rows(a,"resources")?; let br=rows(b,"resources")?; let rate=number(r,"electricChargeRateBudget")?;
    if ar.len()!=br.len() || rate<0. { return Err("reload resource budget differs".into()); }
    let mut resources=BTreeSet::new();
    for (a,b) in ar.iter().zip(br) {
        let id=a["partId"].as_u64().ok_or("reload resource part missing")?;
        let name=a["name"].as_str().filter(|s|!s.is_empty()).ok_or("reload resource name missing")?;
        let delta=number(a,"amount")?-number(b,"amount")?;
        if !resources.insert((id,name)) || !ids.contains(&id) || a["partId"]!=b["partId"] || a["name"]!=b["name"] || number(b,"amount")?<0.
            || if name=="ElectricCharge" {delta < -1e-6 || delta>rate*elapsed+1e-4} else {delta.abs()>1e-6}
        { return Err("reload resource conservation failed".into()); }
    }
    let events=rows(r,"events")?;
    let phases=["admitted","before-image","saved","departed","reload-requested","reloaded","after-image"];
    if events.len()!=phases.len() { return Err("reload event sequence incomplete".into()); }
    let mut wall=-1.;
    for (event,phase) in events.iter().zip(phases) {
        let next=number(event,"wallSeconds")?;
        if event["phase"]!=phase || next<wall { return Err("reload event order failed".into()); }
        number(event,"universalTime")?; wall=next;
    }
    Ok(())
}

pub fn retain(observer: &Path, output: &Path, run: &str, pid: u32, expected: u32, source: &str, save_directory: &Path) -> Result<Value> {
    let mut matches=Vec::new();
    for entry in fs::read_dir(observer).map_err(|e|e.to_string())? {
        let entry=entry.map_err(|e|e.to_string())?;
        if !entry.file_type().map_err(|e|e.to_string())?.is_dir() || !entry.file_name().to_string_lossy().starts_with("checkpoint-reload-") {continue;}
        let path=entry.path().join("checkpoint-reload.json");
        let Ok(meta)=fs::symlink_metadata(&path) else {continue};
        if !meta.is_file() || meta.len()>4*1024*1024 {continue;}
        let Ok(r)=read::<Value>(&path) else {continue};
        if r["runId"]==run && r["processId"]==pid {matches.push(entry.path());}
    }
    if matches.len()!=1 {return Err(format!("expected one owned reload report, found {}",matches.len()));}
    let retained=owned_report::retain(&matches[0],output,"checkpoint-reload.json","checkpoint-reload.json","continuum-checkpoint-reload/v1",run,pid)?;
    let mut images=Vec::new();
    for name in ["before.png","after.png"] {
        let path=matches[0].join(name); let Ok(meta)=fs::symlink_metadata(&path) else {continue};
        if !meta.is_file() || meta.len()>64*1024*1024 {return Err("reload image outside bounds".into());}
        let bytes=fs::read(&path).map_err(|e|e.to_string())?;
        if bytes.len()<24 || bytes[..8]!=[137,80,78,71,13,10,26,10] {return Err("reload image is not PNG".into());}
        let target=output.join(format!("reload-{name}"));
        fs::OpenOptions::new().write(true).create_new(true).open(&target).and_then(|mut f|f.write_all(&bytes)).map_err(|e|e.to_string())?;
        images.push(json!({"path":target,"sha256":hash(&target)?,"width":u32::from_be_bytes(bytes[16..20].try_into().unwrap()),"height":u32::from_be_bytes(bytes[20..24].try_into().unwrap())}));
    }
    validate(&retained["report"],run,pid,expected,source)?;
    if images.len()!=2 || retained["report"]["screenshots"]!=json!(["before.png","after.png"]) {return Err("reload image evidence incomplete".into());}
    let saved=Path::new(retained["report"]["savedPath"].as_str().ok_or("reload saved path missing")?);
    if saved.parent()!=Some(save_directory) || saved.file_name().and_then(|n|n.to_str()).is_none_or(|n|!n.starts_with("continuum-reload-") || !n.ends_with(".sfs"))
        || saved.canonicalize().map_err(|e|e.to_string())?!=saved || !fs::symlink_metadata(saved).map_err(|e|e.to_string())?.is_file()
        || fs::metadata(saved).map_err(|e|e.to_string())?.len()>64*1024*1024 || json!(hash(saved)?)!=retained["report"]["savedSha256"]
    {return Err("reload saved artifact ownership or content differs".into());}
    let checkpoint=output.join("reload-checkpoint.sfs"); let bytes=fs::read(saved).map_err(|e|e.to_string())?;
    fs::OpenOptions::new().write(true).create_new(true).open(&checkpoint).and_then(|mut f|f.write_all(&bytes)).map_err(|e|e.to_string())?;
    if json!(hash(&checkpoint)?)!=retained["report"]["savedSha256"] || hash(saved)?!=hash(&checkpoint)? {return Err("reload artifact changed during retention".into());}
    Ok(json!({"schema":"continuum-checkpoint-reload-summary/v1","receipt":retained,"images":images,"checkpoint":{"path":checkpoint,"sha256":hash(&checkpoint)?},"qualification":"Selected live vessel reconstructed through stock save/quickload; primitive live roundtrip fields and saved whole-game membership observed. Actual image inspection separate; arbitrary mod/scenario persistence, deterministic replay and performance remain unqualified."}))
}
