use crate::Result;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{collections::{BTreeMap, BTreeSet}, fs, io::Write, path::Path};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Selection {
    pub active_vessel: String,
    pub active_port: u32,
    pub target_vessel: String,
    pub target_port: u32,
    #[serde(default)]
    pub prepare_pair: bool,
}

fn guid(value: &str) -> Result<String> {
    let compact: String = value.chars().filter(|c| *c != '-').collect();
    if compact.len() != 32 || !compact.bytes().all(|b| b.is_ascii_hexdigit())
        || compact.bytes().all(|b| b == b'0')
        || (value.len() != 32 && (value.len() != 36
            || ![8, 13, 18, 23].into_iter().all(|i| value.as_bytes()[i] == b'-')))
    {
        return Err("docking requires nonempty vessel GUIDs".into());
    }
    let c = compact.to_ascii_lowercase();
    Ok(format!("{}-{}-{}-{}-{}", &c[..8], &c[8..12], &c[12..16], &c[16..20], &c[20..]))
}

impl Selection {
    pub fn parse(value: &str, prepare_pair: bool) -> Result<Self> {
        let fields: Vec<_> = value.split(',').collect();
        if fields.len() != 4 {
            return Err("flight-docking requires active GUID,port flight ID,target GUID,port flight ID".into());
        }
        let selection = Self {
            active_vessel: guid(fields[0])?,
            active_port: fields[1].parse().map_err(|_| "invalid active docking flight ID")?,
            target_vessel: guid(fields[2])?,
            target_port: fields[3].parse().map_err(|_| "invalid target docking flight ID")?,
            prepare_pair,
        };
        selection.validate()?;
        Ok(selection)
    }

    pub fn validate(&self) -> Result<()> {
        if guid(&self.active_vessel)? != self.active_vessel
            || guid(&self.target_vessel)? != self.target_vessel
            || self.active_vessel == self.target_vessel
            || self.active_port == 0 || self.target_port == 0
            || self.active_port == self.target_port
        {
            return Err("docking requires canonical distinct vessel and port identities".into());
        }
        Ok(())
    }

    pub fn arguments(&self) -> Vec<String> {
        let mut arguments = vec![
            "--continuum-docking=roundtrip".into(),
            format!("--continuum-docking-active-vessel={}", self.active_vessel),
            format!("--continuum-docking-target-vessel={}", self.target_vessel),
            format!("--continuum-docking-active-port={}", self.active_port),
            format!("--continuum-docking-target-port={}", self.target_port),
        ];
        if self.prepare_pair { arguments.push("--continuum-docking-prepare-pair".into()); }
        arguments
    }
}

fn number(v: &Value, key: &str) -> Result<f64> {
    v[key].as_f64().filter(|n| n.is_finite()).ok_or_else(|| format!("docking numerical witness missing: {key}"))
}
fn ids(v: &Value) -> Result<BTreeSet<u64>> {
    let values = v.as_array().ok_or("docking part IDs missing")?;
    let mut result = BTreeSet::new();
    for value in values {
        let id = value.as_u64().filter(|id| *id > 0 && *id <= u32::MAX as u64).ok_or("invalid docking part identity")?;
        if !result.insert(id) { return Err("duplicate docking part identity".into()); }
    }
    if result.is_empty() || result.len() > 8192 { return Err("docking part partition outside bounds".into()); }
    Ok(result)
}
fn resources(v: &Value) -> Result<BTreeMap<String, f64>> {
    let mut result = BTreeMap::new();
    for row in v.as_array().ok_or("docking resource rows missing")? {
        let name = row["name"].as_str().filter(|s| !s.is_empty()).ok_or("docking resource name missing")?;
        let amount = number(row, "amount")?;
        if amount < 0.0 || result.insert(name.to_owned(), amount).is_some() { return Err("invalid docking resource census".into()); }
    }
    Ok(result)
}

pub fn validate(s: &Selection, r: &Value, run: &str, pid: u32, expected_parts: u32, source_sha: &str) -> Result<()> {
    s.validate()?;
    if r["schema"] != "continuum-docking-roundtrip/v1" || r["runId"] != run || r["processId"] != pid
        || r["sourceSha256"] != source_sha || r["activeVesselId"] != s.active_vessel || r["targetVesselId"] != s.target_vessel
        || r["activePortId"] != s.active_port || r["targetPortId"] != s.target_port
        || r["preparationRequested"] != s.prepare_pair || r["status"] != "complete-experimental"
        || r["faults"] != 0 || !r["reason"].is_null() || r["cleanupStatus"] != "removed-owned-controls"
        || ["sourceUnchanged", "initialSeparate", "actualJointObserved", "exactPartPartitionsRestored", "crewConserved", "resourcesConserved", "hooksRemoved", "controlLockRemoved", "inputIngressRestored", "cameraRestored", "collisionsRestored", "schedulingRestored"].iter().any(|k| r[*k] != true)
        || r["callbackRegistrationMutations"] != 0
        || r["cameraCallbacks"].as_u64().unwrap_or(0) == 0
        || number(r, "maxCameraPositionErrorMeters")? > 0.005 || number(r, "maxCameraAngleErrorDegrees")? > 0.005
        || ["mergeCount", "undockCalls", "splitCount"].iter().any(|k| r[*k] != 1)
    { return Err("docking ownership, stock event or cleanup contract failed; raw evidence retained".into()); }
    let controls = r["controlLeases"].as_array().filter(|rows| !rows.is_empty() && rows.len() % 4 == 0 && rows.len() <= 64).ok_or("docking input-control lease census missing")?;
    let mut leased = BTreeSet::new();
    for rows in controls.chunks_exact(4) {
        let vessel = rows[0]["vesselId"].as_str().ok_or("docking control vessel missing")?;
        guid(vessel)?;
        let instance = rows[0]["vesselInstanceId"].as_i64().filter(|id| *id != 0).ok_or("docking control instance missing")?;
        for (row, chain) in rows.iter().zip(["pre-autopilot", "autopilot", "post-autopilot", "fly-by-wire"]) {
            if row["vesselId"] != vessel || row["vesselInstanceId"] != instance || row["chain"] != chain
                || row["suspendedCallbacks"].as_array().is_none_or(|v| v.iter().any(|v| v.as_str().is_none_or(|s| s.is_empty())))
                || (if row["destroyed"] == true { !row["finalCallbacks"].is_null() } else { row["destroyed"] != false || row["finalCallbacks"].as_array().is_none_or(|v| v.iter().any(|v| v.as_str().is_none_or(|s| s.is_empty()))) })
            { return Err("docking input-delivery census witness failed".into()); }
        }
        if !leased.insert((vessel, instance)) { return Err("duplicate docking input-control lease".into()); }
    }
    if !leased.iter().any(|(v, _)| *v == s.active_vessel) || !leased.iter().any(|(v, _)| *v == s.target_vessel) {
        return Err("docking seed lacks both input-control leases".into());
    }
    let a = ids(&r["activePartIds"])?; let b = ids(&r["targetPartIds"])?;
    if !a.is_disjoint(&b) || !a.contains(&(s.active_port as u64)) || !b.contains(&(s.target_port as u64))
        || r["initialActiveParts"].as_u64() != Some(expected_parts as u64) || a.len() != expected_parts as usize
        || r["initialTargetParts"].as_u64() != Some(b.len() as u64)
        || ids(&r["splitActivePartIds"])? != a || ids(&r["splitTargetPartIds"])? != b
        || r["splitActiveVesselId"] == r["splitTargetVesselId"]
    { return Err("docking merge/split lost the original part partitions".into()); }
    let events = r["events"].as_array().filter(|a| a.len() == 5).ok_or("docking event sequence incomplete")?;
    let mut last_ut = f64::NEG_INFINITY; let mut last_wall = f64::NEG_INFINITY;
    for (event, phase) in events.iter().zip(["seed", "admitted", "merged", "before-undock", "split"]) {
        let ut = number(event, "universalTime")?; let wall = number(event, "wallSeconds")?;
        if event["phase"] != phase || ut < last_ut || wall < last_wall || wall < 0.0
            || number(event, "distanceMeters")? < 0.0 || number(event, "relativeSpeedMetersPerSecond")? < 0.0
            || !(-1.000001..=1.000001).contains(&number(event, "alignmentDot")?)
            || (s.prepare_pair && number(event, "sunHorizonClearanceMeters")? <= 0.0)
        { return Err("docking event order or physical readback failed".into()); }
        last_ut = ut; last_wall = wall;
    }
    let merged = &events[2]; let split = &events[4];
    if merged["activeVesselId"] != merged["targetVesselId"] || merged["activeVesselId"] != r["mergedVesselId"]
        || merged["activeParts"].as_u64() != Some((a.len()+b.len()) as u64)
        || merged["jointId"].as_i64().is_none_or(|id| id == 0)
        || merged["activeLinkedPortId"] != s.target_port || merged["targetLinkedPortId"] != s.active_port
        || !merged["activePortState"].as_str().is_some_and(|s| s.contains("Docked"))
        || !merged["targetPortState"].as_str().is_some_and(|s| s.contains("Docked"))
        || split["activeVesselId"] != r["splitActiveVesselId"] || split["targetVesselId"] != r["splitTargetVesselId"]
        || split["activeParts"].as_u64() != Some(a.len() as u64) || split["targetParts"].as_u64() != Some(b.len() as u64)
        || split["activeLinkedPortId"] != 0 || split["targetLinkedPortId"] != 0
        || number(r, "minimumGapMeters")? < 0.0 || number(r, "minimumGapMeters")? > 0.1
        || number(r, "finalGapMeters")? < 1.1 || number(r, "finalSeparationSpeedMetersPerSecond")? <= 0.001
        || number(r, "maxAccelerationMetersPerSecondSquared")? != 0.01
        || r["ticks"].as_u64().unwrap_or(0) == 0 || r["clockFrequency"].as_u64().unwrap_or(0) == 0
    { return Err("docking lacks reciprocal stock merge/joint or physical separation".into()); }
    let initial = resources(&r["initialResources"])?; let final_r = resources(&r["finalResources"])?;
    let elapsed = last_ut - number(r, "initialUniversalTime")?;
    let ec_rate = number(r, "electricChargeRateBudget")?;
    if elapsed < 0.0 || ec_rate < 0.0 || initial.keys().ne(final_r.keys())
        || (ec_rate - number(r, "electricChargeCommandRateBudget")? - number(r, "electricChargeDockingMotorRateBudget")?).abs() > 1e-9
    { return Err("docking resource budget differs".into()); }
    for (name, amount) in &initial {
        let difference = amount - final_r[name];
        if name == "ElectricCharge" {
            if difference < -1e-6 || difference > ec_rate * elapsed + 1e-4 || (difference-number(r,"electricChargeConsumed")?).abs()>1e-6 {
                return Err("docking EC consumption exceeds its declared budget".into());
            }
        } else if difference.abs() > 1e-6 { return Err("docking changed a conserved resource".into()); }
    }
    let crew = r["crewNames"].as_array().ok_or("docking crew scope missing")?;
    if r["zeroCrewScope"] != crew.is_empty() { return Err("docking crew scope differs".into()); }
    if s.prepare_pair {
        let orbits = r["orbitalPreparation"].as_array().filter(|v| v.len() == 2).ok_or("paired Sunward orbit proof missing")?;
        for (row, id) in orbits.iter().zip([&s.active_vessel, &s.target_vessel]) {
            if row["vesselId"].as_str() != Some(id.as_str()) { return Err("paired Sunward orbit identity differs".into()); }
            for (before, after) in [("radiusBefore", "radiusAfter"), ("speedBefore", "speedAfter"), ("radialBefore", "radialAfter")] {
                if (number(row, before)? - number(row, after)?).abs() > 0.0001 { return Err("paired Sunward orbit invariant differs".into()); }
            }
        }
        let setup = r["setup"].as_array().filter(|v| v.len() == 4).ok_or("docking declared preparation witness missing")?;
        for (i, row) in setup.iter().enumerate() {
            if row["phase"] != (if i < 2 {"before"} else {"after"}) || row["vesselId"].as_str() != Some(if i%2==0 {s.active_vessel.as_str()} else {s.target_vessel.as_str()}) { return Err("docking preparation identity differs".into()); }
            for (key, len) in [("position",3),("velocity",3),("rotation",4)] {
                if row[key].as_array().is_none_or(|v| v.len()!=len || v.iter().any(|n| n.as_f64().is_none_or(|n| !n.is_finite()))) { return Err("docking preparation has nonfinite pose or velocity".into()); }
            }
        }
    }
    Ok(())
}

pub fn retain(s: &Selection, observer: &Path, output: &Path, run: &str, pid: u32, expected_parts: u32, source_sha: &str) -> Result<Value> {
    let mut matches = Vec::new();
    for entry in fs::read_dir(observer).map_err(|e|e.to_string())? {
        let entry = entry.map_err(|e|e.to_string())?;
        if !entry.file_type().map_err(|e|e.to_string())?.is_dir() || !entry.file_name().to_string_lossy().starts_with("docking-profile-") { continue; }
        let path = entry.path().join("docking.json");
        let Ok(meta) = fs::symlink_metadata(&path) else {continue};
        if !meta.is_file() || meta.len()>4*1024*1024 { continue; }
        let Ok(r) = crate::read::<Value>(&path) else {continue};
        if r["runId"] == run && r["processId"] == pid { matches.push(entry.path()); }
    }
    if matches.len()!=1 { return Err(format!("expected one owned docking receipt, found {}",matches.len())); }
    let retained = crate::owned_report::retain(&matches[0],output,"docking.json","docking.json","continuum-docking-roundtrip/v1",run,pid)?;
    let mut images=Vec::new();
    for name in ["seed.png","docked.png","split.png"] {
        let path=matches[0].join(name); let Ok(meta)=fs::symlink_metadata(&path) else {continue};
        if !meta.is_file() || meta.len()>64*1024*1024 { return Err("docking image outside bounds".into()); }
        let bytes=fs::read(&path).map_err(|e|e.to_string())?;
        if bytes.len()<24 || bytes[..8]!=[137,80,78,71,13,10,26,10] { return Err("docking image is not PNG".into()); }
        let target=output.join(format!("docking-{name}"));
        fs::OpenOptions::new().write(true).create_new(true).open(&target).and_then(|mut f|f.write_all(&bytes)).map_err(|e|e.to_string())?;
        images.push(json!({"path":target,"sha256":crate::hash(&target)?,"width":u32::from_be_bytes(bytes[16..20].try_into().unwrap()),"height":u32::from_be_bytes(bytes[20..24].try_into().unwrap())}));
    }
    validate(s,&retained["report"],run,pid,expected_parts,source_sha)?;
    if images.len()!=3 || retained["report"]["screenshots"]!=json!(["seed.png","docked.png","split.png"]) { return Err("docking images incomplete".into()); }
    Ok(json!({"schema":"continuum-docking-summary/v1","receipt":retained,"images":images,"qualification":"Stock diagnostic roundtrip observed with declared fixture preparation and forces. Actual image inspection is separate. No RCS, rendezvous, crewed mission, deterministic replay or FPS qualification."}))
}
