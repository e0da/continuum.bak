use crate::{hash, read, write_new, Result};
use serde_json::{json, Value};
use std::{fs, path::Path};

pub fn thread_work_summary(proof: &Value) -> Result<Value> {
    let sessions = proof["sessions"].as_array().ok_or("thread CPU sessions absent")?;
    let names = ["UnityEngine.PlayerLoop.Update", "UnityEngine.PlayerLoop.Update+ScriptRunDelayedDynamicFrameRate", "UnityEngine.PlayerLoop.FixedUpdate", "UnityEngine.PlayerLoop.PostLateUpdate"];
    let mut results = Vec::new();
    for session in sessions {
        let report = &session["final"]["mainThreadWork"];
        let rows = report["rows"].as_array().ok_or("thread CPU rows absent")?;
        let frequency = report["clockFrequency"].as_u64().filter(|value| *value > 0).ok_or("thread CPU frequency absent")?;
        let dropped = report["droppedRows"].as_u64().ok_or("thread CPU dropped count absent")?;
        let total = dropped.checked_add(rows.len() as u64).ok_or("thread CPU row count overflow")?;
        if report["schema"] != "continuum-mac-thread-work/v1" || report["requested"] != true || report["status"] != "completed"
            || report["nativeInfoCount"] != 10 || report["nativeThreadPort"].as_u64().is_none_or(|value| value == 0 || value > u32::MAX as u64)
            || session["final"]["clockFrequency"].as_u64() != Some(frequency) || report["managedThreadId"].as_u64().unwrap_or(0) == 0
            || rows.is_empty() || rows.len() > 4096 || report["retainedRows"].as_u64() != Some(rows.len() as u64)
            || report["completedRows"].as_u64() != Some(total)
            || report["incompleteScopes"].as_u64().is_none_or(|value| value > 4) {
            return Err("requested native thread CPU capture is incomplete or invalid".into());
        }
        for (index, row) in rows.iter().enumerate() {
            let integer = |field: &str| row[field].as_u64().ok_or_else(|| format!("thread CPU {field} absent"));
            let user_start = integer("userBeginMicroseconds")?; let user_end = integer("userEndMicroseconds")?;
            let system_start = integer("systemBeginMicroseconds")?; let system_end = integer("systemEndMicroseconds")?;
            let cpu = user_end.checked_sub(user_start).and_then(|user| system_end.checked_sub(system_start).and_then(|system| user.checked_add(system))).ok_or("thread CPU cumulative delta invalid")?;
            if integer("sequence")? != dropped.checked_add(index as u64).ok_or("thread CPU sequence overflow")? || integer("endTicks")? <= integer("beginTicks")?
                || integer("cpuMicroseconds")? != cpu
                || !names.iter().any(|name| row["scope"] == *name) || row["frame"].as_u64().is_none() {
                return Err("thread CPU row violates native cumulative clock correspondence".into());
            }
            integer("readTaxTicks")?;
        }
        let mut scopes = Vec::new();
        for name in names {
            let selected = rows.iter().filter(|row| row["scope"] == name).collect::<Vec<_>>();
            if selected.is_empty() { return Err("thread CPU scope has no execution evidence".into()); }
            let wall = selected.iter().map(|row| (row["endTicks"].as_u64().unwrap() - row["beginTicks"].as_u64().unwrap()) as f64 / frequency as f64).sum::<f64>();
            let cpu = selected.iter().map(|row| row["cpuMicroseconds"].as_u64().unwrap() as f64 / 1e6).sum::<f64>();
            scopes.push(json!({"name":name,"samples":selected.len(),"meanWallMilliseconds":wall*1000.0/selected.len() as f64,"meanCpuMilliseconds":cpu*1000.0/selected.len() as f64,"cpuToWall":cpu/wall}));
        }
        results.push(json!({"sessionId":session["final"]["sessionId"],"droppedRows":dropped,"incompleteShutdownScopes":report["incompleteScopes"],"scopes":scopes}));
    }
    if results.is_empty() { return Err("thread CPU service session absent".into()); }
    Ok(json!({"status":"passed","sessions":results,"scope":"Completed native owner-thread CPU and wall intervals; overlapping scopes are not additive. Includes observer/native-read work. Unfinished shutdown brackets are excluded explicitly. This is neither GPU duration nor named coroutine attribution."}))
}

pub fn retain(output: &Path, run_id: &str, pid: u32, helper_sha: &str, audit_steps: u32) -> Result<Value> {
    if !matches!(audit_steps,1 | 16) { return Err("unsupported frozen audit interval".into()); }
    for ancestor in output.ancestors() {
        if fs::symlink_metadata(ancestor).map_err(|error| error.to_string())?.file_type().is_symlink() { return Err("rigid proof ancestry is symlinked".into()); }
    }
    let mut sessions = Vec::new();
    let mut owned_steps = 0u64;
    for entry in fs::read_dir(output).map_err(|error| error.to_string())? {
        let entry = entry.map_err(|error| error.to_string())?;
        let name = entry.file_name();
        let Some(id) = name.to_str().and_then(|name| name.strip_prefix("rigid-")) else { continue; };
        let kind=entry.file_type().map_err(|error|error.to_string())?;
        if kind.is_file() { continue; }
        if id.len() != 36 || !id.bytes().all(|byte| byte.is_ascii_hexdigit() || byte == b'-') || !kind.is_dir() {
            return Err("invalid rigid session directory".into());
        }
        let final_path = entry.path().join("final.json");
        regular_file(&final_path)?;
        let final_row: Value = read(&final_path)?;
        validate_final(&final_row, run_id, pid, id, helper_sha)?;
        if final_row["auditSteps"].as_u64() != Some(u64::from(audit_steps)) { return Err("service audit interval differs from frozen launch".into()); }
        let journal = entry.path().join("transitions.jsonl");
        regular_file(&journal)?;
        let metadata = fs::metadata(&journal).map_err(|error| error.to_string())?;
        if metadata.len() > 4 * 1024 * 1024 { return Err("rigid journal exceeded bound".into()); }
        let text = fs::read_to_string(&journal).map_err(|error| error.to_string())?;
        let rows: Vec<Value> = text.lines().map(|line| serde_json::from_str(line).map_err(|error| error.to_string())).collect::<Result<_>>()?;
        if rows.is_empty() || rows.len() > 512 || rows.iter().any(|row| row["schema"] != "continuum-playable-rigid-event/v1" || row["runId"] != run_id || row["processId"].as_u64() != Some(u64::from(pid)) || row["sessionId"] != id) {
            return Err("foreign or unbounded rigid event journal".into());
        }
        let mut owners = Vec::new();
        let expected_attempts = final_row["attempts"].as_u64().ok_or("service attempt cardinality absent")?;
        let starts = rows.iter().filter(|row| row["reason"] == "owner-attempt-started").collect::<Vec<_>>();
        if starts.len() as u64 != expected_attempts { return Err("rigid attempt journal cardinality differs".into()); }
        let mut attempts = std::collections::BTreeSet::new();
        for owner in fs::read_dir(entry.path()).map_err(|error| error.to_string())? {
            let owner = owner.map_err(|error| error.to_string())?;
            let name = owner.file_name();
            let name = name.to_string_lossy();
            if !name.starts_with("epoch-") || !name.ends_with(".json") { continue; }
            if !owner.file_type().map_err(|error| error.to_string())?.is_file() || fs::metadata(owner.path()).map_err(|error| error.to_string())?.len() > 16 * 1024 * 1024 { return Err("invalid rigid owner receipt".into()); }
            let row: Value = read(&owner.path())?;
            let epoch = row["playableEpoch"].as_u64().ok_or("owner epoch binding absent")?;
            let attempt = row["playableAttempt"].as_u64().ok_or("owner attempt binding absent")?;
            let bound_start = starts.iter().any(|start| start["epoch"].as_u64() == Some(epoch) && start["attempt"].as_u64() == Some(attempt));
            let step_count = row["physicsSteps"].as_u64().ok_or("owner step count absent")?;
            let safe_unobserved = step_count == 0 && row["passed"] == false && row["status"] == "handed-back-unobserved";
            if attempt == 0 || attempt > expected_attempts || !attempts.insert(attempt) || !bound_start || name != format!("epoch-{epoch}-attempt-{attempt}.json") ||
                row["schema"] != "continuum-live-rigid-owner/v3" || row["runId"] != run_id || row["scenario"] != "persistent-play" ||
                row["processId"].as_u64() != Some(u64::from(pid)) || row["helperSha256"] != helper_sha || row["playableSessionId"] != id ||
                row["gameplayGuardFailures"].as_u64() != Some(0) || row["gameplayForeignGraphChanges"].as_u64() != Some(0) ||
                !(row["passed"] == true || safe_unobserved) || row["bridgeReleased"] != true || row["originalBodiesRestored"] != true || row["aggregateRetired"] != true {
                return Err(format!("rigid owner did not close: {}", owner.path().display()));
            }
            let sites = row["bridgeSites"].as_array().ok_or("owner source bridge absent")?;
            if sites.is_empty() || sites.iter().any(|site| match site["assembly"].as_str() {
                Some("Assembly-CSharp") => site["assemblySha256"] != "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27" || site["assemblyMvid"] != "10657063-2fc3-43a7-84fa-d39e75e877bf",
                Some("MechJeb2") => site["assemblySha256"] != "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929" || site["assemblyMvid"] != "30fbe3c4-e77e-4abc-895b-a635d5924a08",
                _ => true,
            }) { return Err("owner bridge source identity differs".into()); }
            if step_count > 0 { verify_motion_witness(&row)?; verify_audit_lease(&row,audit_steps)?; }
            owned_steps = owned_steps.checked_add(step_count).ok_or("owner step count overflow")?;
            owners.push(json!({"path":owner.path(),"sha256":hash(&owner.path())?,"receipt":row}));
        }
        if attempts.len() as u64 != expected_attempts { return Err("missing rigid attempt receipt; evidence cannot omit a failure".into()); }
        sessions.push(json!({"path":entry.path(),"finalSha256":hash(&final_path)?,"final":final_row,"journalSha256":hash(&journal)?,"events":rows,"owners":owners}));
    }
    if sessions.is_empty() || owned_steps == 0 { return Err("no closed actual rigid ownership observed".into()); }
    let proof = json!({"schema":"continuum-playable-rigid-proof/v1","runId":run_id,"processId":pid,"helperSha256":helper_sha,"auditSteps":audit_steps,
        "ownedSteps":owned_steps,"qualification":"persistent-rigid-ownership-and-stock-transition-closure; mission-performance-and-endurance-require-separate-observations","sessions":sessions});
    write_new(&output.join("playable-rigid-proof.json"), &proof)?;
    Ok(proof)
}

fn verify_audit_lease(row: &Value, expected: u32) -> Result<()> {
    let interval=u64::from(expected);
    let full=row["gameplayFullLeaseAudits"].as_u64().filter(|count|*count>0).ok_or("full lease audits absent")?;
    let bodies=row["sourceBodies"].as_u64().filter(|count|*count>0).ok_or("audit source body coverage absent")?;
    if bodies.checked_mul(full)!=row["gameplayFullLeasePoseReadbacks"].as_u64() { return Err("full audit source coverage differs".into()); }
    for (field,limit) in [("maxProxyPositionError",1e-4),("maxProxyRotationError",0.002),("maxNativeFramePositionError",1e-4),("maxNativeFrameRotationError",0.002),("maxPresentationWorldScaleRelativeError",1e-5)] {
        if row[field].as_f64().is_none_or(|error|!error.is_finite() || error<0.0 || error>limit) { return Err(format!("unchanged audit budget failed: {field}")); }
    }
    if row["gameplayAuditIntervalSteps"].as_u64()!=Some(interval) ||
        row["gameplayAuditRequests"].as_u64().is_none_or(|count|count<full) ||
        row["gameplayAuditMaximumStepGap"].as_u64().is_none_or(|gap|gap>interval) ||
        row["gameplayAuditMaximumSimulatedGapSeconds"].as_f64().is_none_or(|gap|!gap.is_finite() || gap<0.0 || gap>interval as f64*0.1+1e-6) ||
        row["gameplayAuditLastStep"].as_u64()!=row["physicsSteps"].as_u64() ||
        row["gameplayAuditHandbackAudits"].as_u64().is_none_or(|count|count==0) {
        return Err("native audit lease did not close at its frozen interval".into());
    }
    Ok(())
}

fn regular_file(path: &Path) -> Result<()> {
    if !fs::symlink_metadata(path).map_err(|error| error.to_string())?.file_type().is_file() { return Err("rigid proof requires a regular owned file".into()); }
    Ok(())
}

pub fn window_bound(report: &Value, start: &Value, end: &Value, closure: Option<&Value>) -> Result<Value> {
    if report["cleanupStatus"] == "captured-operator-window" {
        let closed=closure.ok_or("frame capture requires separate actual service closure")?;
        for field in ["runId","processId","sessionId","helperSha256","clockFrequency","auditSteps"] {
            if report[field].is_null() || report[field]!=closed[field] { return Err(format!("frame capture closure differs at {field}")); }
        }
        let run=closed["runId"].as_str().ok_or("closure run absent")?;
        let pid=closed["processId"].as_u64().and_then(|value|u32::try_from(value).ok()).ok_or("closure pid absent")?;
        validate_final(closed,run,pid,closed["sessionId"].as_str().ok_or("closure session absent")?,closed["helperSha256"].as_str().ok_or("closure helper absent")?)?;
        let mut merged=report.clone(); merged["cleanupStatus"]=closed["cleanupStatus"].clone(); merged["playerLoop"]=closed["playerLoop"].clone();
        window(&merged,start,end)
    } else {
        let run=report["runId"].as_str().ok_or("closure run absent")?;
        let pid=report["processId"].as_u64().and_then(|value|u32::try_from(value).ok()).ok_or("closure pid absent")?;
        validate_final(report,run,pid,report["sessionId"].as_str().ok_or("closure session absent")?,report["helperSha256"].as_str().ok_or("closure helper absent")?)?;
        window(report,start,end)
    }
}

pub fn window(report: &Value, start: &Value, end: &Value) -> Result<Value> {
    let a = &start["status"]; let b = &end["status"];
    if start["schema"] != "continuum-rigid-control-observation/v1" || end["schema"] != start["schema"] ||
        start["operation"] != "status" || !(end["operation"] == "status" || end["operation"] == "capture") ||
        report["schema"] != "continuum-playable-rigid/v1" || report["cleanupStatus"] != "released-owned-service-hooks-loop-and-motion" ||
        a["sessionId"] != report["sessionId"] || b["sessionId"] != a["sessionId"] {
        return Err("window requires closed service and bound status endpoints".into());
    }
    for field in ["epoch", "owned", "enabled", "vesselId", "parts", "state", "vSyncCount", "targetFrameRate", "width", "height", "antialiasing", "warpMode", "warpRate", "fixedDeltaTime", "clockFrequency"] {
        if a[field].is_null() || a[field] != b[field] { return Err(format!("window changed {field}")); }
    }
    for field in ["focused","runInBackground","captureFramerate","maximumDeltaTime","timeScale"] {
        if !a[field].is_null() && a[field]!=b[field] { return Err(format!("window changed pacing observation {field}")); }
    }
    if a["vSyncCount"] != 0 || a["targetFrameRate"] != -1 || a["warpRate"] != 1 || a["throttle"] != 0 || b["throttle"] != 0 {
        return Err("diagnostic window requires uncapped normal unpowered flight".into());
    }
    let frequency = report["clockFrequency"].as_u64().filter(|value|*value>0).ok_or("clock frequency absent")?;
    if a["clockFrequency"].as_u64() != Some(frequency) { return Err("window clock differs".into()); }
    let begin = a["observationTicks"].as_u64().ok_or("window begin absent")?;
    let finish = b["observationTicks"].as_u64().filter(|value|*value>begin).ok_or("window finish absent or reversed")?;
    let rows = report["frameIntervals"].as_array().ok_or("complete frame intervals absent")?;
    let selected: Vec<&Value> = rows.iter().filter(|row| row["previousEndedTicks"].as_u64().is_some_and(|value|value>=begin) && row["endedTicks"].as_u64().is_some_and(|value|value<=finish)).collect();
    if selected.len() < 30 { return Err("window has too few retained intervals".into()); }
    let mut durations = Vec::with_capacity(selected.len());
    let mut prior_end = None;
    for row in &selected {
        let previous = row["previousEndedTicks"].as_u64().ok_or("frame start absent")?;
        let now = row["endedTicks"].as_u64().ok_or("frame end absent")?;
        let duration = row["intervalTicks"].as_u64().filter(|value|*value>0).ok_or("invalid frame duration")?;
        if now.checked_sub(previous) != Some(duration) || prior_end.is_some_and(|value|value!=previous) ||
            row["epoch"] != a["epoch"] || row["previousEpoch"] != a["epoch"] || row["owned"] != a["owned"] || row["previouslyOwned"] != a["owned"] ||
            row["diagnosticNormalUnpowered"] != true || row["previouslyDiagnosticNormalUnpowered"] != true ||
            ["width", "height", "antialiasing"].iter().any(|field|row[field]!=a[field]) {
            return Err("window contains a gap or ownership boundary".into());
        }
        for field in ["focused","runInBackground","captureFramerate","maximumDeltaTime","timeScale"] {
            if !a[field].is_null() && row[field]!=a[field] { return Err(format!("window changed interior pacing observation {field}")); }
        }
        prior_end=Some(now); durations.push(duration as f64 * 1000.0 / frequency as f64);
    }
    let retained_begin=selected[0]["previousEndedTicks"].as_u64().unwrap();
    let retained_end=selected.last().unwrap()["endedTicks"].as_u64().unwrap();
    if retained_begin-begin > frequency/4 || finish-retained_end > frequency/4 { return Err("window endpoints were overwritten or contain a large partial frame".into()); }
    if !matches!(report["auditSteps"].as_u64(), Some(1 | 16)) { return Err("actual audit mode absent or unsupported".into()); }
    let simulation_start=selected[0]["previousFixedTime"].as_f64().ok_or("simulation clock absent")?;
    let simulation_end=selected.last().unwrap()["fixedTime"].as_f64().ok_or("simulation clock absent")?;
    if !simulation_start.is_finite() || !simulation_end.is_finite() || simulation_end < simulation_start { return Err("simulation clock reversed".into()); }
    let elapsed=(retained_end-retained_begin) as f64/frequency as f64;
    let frame_ms=durations.iter().sum::<f64>()/durations.len() as f64;
    durations.sort_by(f64::total_cmp);
    let percentile=|fraction:f64| durations[((durations.len() as f64*fraction).ceil() as usize).saturating_sub(1).min(durations.len()-1)];
    Ok(json!({"schema":"continuum-rigid-window/v1","runId":report["runId"],"processId":report["processId"],"sessionId":report["sessionId"],"helperSha256":report["helperSha256"],"epoch":a["epoch"],"owned":a["owned"],"auditSteps":report["auditSteps"],"vesselId":a["vesselId"],"parts":a["parts"],"pacing":{"focused":a["focused"],"runInBackground":a["runInBackground"],"captureFramerate":a["captureFramerate"],"maximumDeltaTime":a["maximumDeltaTime"],"timeScale":a["timeScale"]},"frameCount":durations.len(),"wallSeconds":elapsed,"meanFramesPerSecond":1000.0/frame_ms,"meanFrameMilliseconds":frame_ms,"p95FrameMilliseconds":percentile(0.95),"p99FrameMilliseconds":percentile(0.99),"simulationRate":(simulation_end-simulation_start)/elapsed,"beginTicks":retained_begin,"endTicks":retained_end,"phaseAccounting":phase_accounting(report,&selected,frequency,frame_ms),"scope":"Complete retained render intervals inside explicit status endpoints; uncapped normal unpowered conditions observed at completed render boundaries. Same-session descriptive window, not randomized paired causal or mission qualification. Rendering, all callbacks, validation, synchronization and waits are included."}))
}

fn phase_accounting(report: &Value, frames: &[&Value], frequency:u64, frame_ms:f64) -> Value {
    let ids: std::collections::BTreeSet<u64>=frames.iter().filter_map(|frame|frame["frame"].as_u64()).collect();
    let Some(scopes)=report["playerLoop"]["scopes"].as_array() else { return json!({"status":"unavailable-no-complete-loop-receipt"}); };
    let mut values=Vec::new(); let mut covered=0.0; let mut complete=ids.len()==frames.len();
    for phase in ["Initialization","EarlyUpdate","PreUpdate","FixedUpdate","Update","PreLateUpdate","PostLateUpdate"] {
        let name=format!("UnityEngine.PlayerLoop.{phase}");
        let Some(scope)=scopes.iter().find(|scope|scope["name"]==name) else { complete=false; values.push(json!({"name":name,"status":"unobserved"})); continue; };
        let Some(rows)=scope["samples"].as_array() else { complete=false;continue; };
        let mut seen=std::collections::BTreeSet::new(); let mut ticks=0u64;
        for row in rows.iter().filter(|row|row["frame"].as_u64().is_some_and(|frame|ids.contains(&frame))) {
            let (Some(frame),Some(duration))=(row["frame"].as_u64(),row["elapsedTicks"].as_u64()) else { complete=false;continue; };
            if !seen.insert(frame) { complete=false; }
            if let Some(sum)=ticks.checked_add(duration) { ticks=sum; } else { complete=false; }
        }
        let mean=ticks as f64*1000.0/frequency as f64/frames.len() as f64; covered+=mean;
        if seen.len()!=frames.len() { complete=false; }
        values.push(json!({"name":name,"samples":seen.len(),"expectedFrames":frames.len(),"meanMilliseconds":mean}));
    }
    json!({"status":if complete {"complete-disjoint-top-level-frame-correspondence"} else {"partial-do-not-attribute-residual"},"phases":values,"coveredMeanMilliseconds":if complete{Some(covered)}else{None},"unassignedMeanMilliseconds":if complete{Some(frame_ms-covered)}else{None},"scope":"Same Unity frame IDs, one row per top-level parent, no nested children summed. Residual includes work outside these parents, adjacent-frame presentation alignment, observer hooks and scheduling waits; it is not a named bottleneck or GPU execution duration."})
}

fn validate_final(row: &Value, run_id: &str, pid: u32, id: &str, helper_sha: &str) -> Result<()> {
    if row["schema"] != "continuum-playable-rigid/v1" || row["runId"] != run_id || row["sessionId"] != id || row["processId"].as_u64() != Some(u64::from(pid)) || row["helperSha256"] != helper_sha || row["cleanupStatus"] != "released-owned-service-hooks-loop-and-motion" || row["ownerReleased"] != true || row["hooksRemoved"] != true || row["callbacksRemoved"] != true || row["playerLoopRemoved"] != true || row["owned"] != false {
        return Err("foreign, changed helper or unclosed playable rigid session".into());
    }
    if row["callbacksRemoved"] != true || row["playerLoopRetained"] != true || row["playerLoop"]["cleanupStatus"] != "removed-owned-hooks" ||
        row["playerLoop"]["status"] != "observed" || row["playerLoop"]["integrityStatus"] != "verified-at-boundaries" ||
        row["playerLoop"]["scopes"].as_array().is_none_or(|scopes|scopes.is_empty() || scopes.iter().any(|scope|scope["sequenceErrors"].as_u64()!=Some(0) || scope["status"]!="observed")) ||
        row["sources"].as_array().is_none_or(|sources| sources.is_empty() || sources.iter().any(|source| source["installed"] != true || source["removed"] != true)) ||
        row["frameIntervals"].as_array().is_none_or(|frames| frames.is_empty() || frames.len() > 4096) {
        return Err("playable rigid final omitted lifecycle or frame evidence".into());
    }
    Ok(())
}

fn verify_motion_witness(row: &Value) -> Result<()> {
    fn vector(value: &Value) -> Result<[f64;3]> {
        let values = value.as_array().ok_or("motion vector absent")?;
        if values.len() != 3 { return Err("motion vector dimension differs".into()); }
        let mut output = [0.0;3];
        for i in 0..3 { output[i] = (values[i].as_f64().ok_or("motion coordinate absent")? as f32) as f64; if !output[i].is_finite() { return Err("nonfinite motion witness".into()); } }
        Ok(output)
    }
    fn norm(vector:[f64;3]) -> f64 { vector.into_iter().map(|value|value*value).sum::<f64>().sqrt() }
    fn error(a:[f64;3],b:[f64;3]) -> f64 { norm(std::array::from_fn(|i|a[i]-b[i])) }
    let witnesses = row["gameplayMotionWitness"].as_array().ok_or("native motion witness absent")?;
    if witnesses.len() != 6 || row["gameplayMotionWitnessNativePointCalls"].as_u64() != Some(6) || row["gameplayMotionNativeFallbacks"].as_u64() != Some(0) {
        return Err("motion view admission incomplete or fell back to native".into());
    }
    for witness in witnesses {
        let point=vector(&witness["point"])?; let center=vector(&witness["worldCenter"])?;
        let velocity=vector(&witness["velocity"])?; let omega=vector(&witness["angularVelocity"])?;
        let offset:[f64;3]=std::array::from_fn(|i|point[i]-center[i]);
        let expected=[velocity[0]+omega[1]*offset[2]-omega[2]*offset[1],velocity[1]+omega[2]*offset[0]-omega[0]*offset[2],velocity[2]+omega[0]*offset[1]-omega[1]*offset[0]];
        let actual=vector(&witness["managedVelocity"])?; let native=vector(&witness["nativeVelocity"])?;
        if error(actual,expected) > 1e-4+1e-5*norm(expected) || error(actual,native) > 1e-4+1e-5*norm(native) || witness["localCenterBitsEqual"] != true || witness["passed"] != true {
            return Err("independent native/analytic motion witness failed".into());
        }
        if witness["sourceId"].as_i64() != Some(0) {
            let original=vector(&witness["nativeLocalCenter"])?; let retained=vector(&witness["cachedLocalCenter"])?;
            if (0..3).any(|i|(original[i] as f32).to_bits()!=(retained[i] as f32).to_bits()) { return Err("cached local center bits differ".into()); }
        }
    }
    Ok(())
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn rejects_foreign_helper_and_partial_handback() {
        let mut row = json!({"schema":"continuum-playable-rigid/v1","runId":"run","sessionId":"session","processId":42,"helperSha256":"hash","cleanupStatus":"released-owned-service-hooks-loop-and-motion","ownerReleased":true,"hooksRemoved":true,"callbacksRemoved":true,"playerLoopRemoved":true,"owned":false,"playerLoopRetained":true,"playerLoop":{"cleanupStatus":"removed-owned-hooks","status":"observed","integrityStatus":"verified-at-boundaries","scopes":[{"sequenceErrors":0,"status":"observed"}]},"sources":[{"installed":true,"removed":true}],"frameIntervals":[{}]});
        assert!(validate_final(&row,"run",42,"session","hash").is_ok());
        assert!(validate_final(&row,"run",43,"session","hash").is_err());
        assert!(validate_final(&row,"run",42,"session","other-hash").is_err());
        row["playerLoop"]["status"]=json!("invalid");
        assert!(validate_final(&row,"run",42,"session","hash").is_err());
        row["playerLoop"]["status"]=json!("observed");
        row["ownerReleased"] = json!(false);
        assert!(validate_final(&row,"run",42,"session","hash").is_err());
    }
    #[test] fn whole_window_rejects_overwrite_gaps_and_transitions() {
        let endpoint=|ticks|json!({"schema":"continuum-rigid-control-observation/v1","operation":"status","status":{"sessionId":"s","epoch":1,"owned":true,"enabled":true,"vesselId":"v","parts":512,"state":"owned","vSyncCount":0,"targetFrameRate":-1,"width":1920,"height":1080,"antialiasing":2,"warpMode":"LOW","warpRate":1,"fixedDeltaTime":0.02,"clockFrequency":1000,"throttle":0,"observationTicks":ticks}});
        let mut report=json!({"schema":"continuum-playable-rigid/v1","cleanupStatus":"released-owned-service-hooks-loop-and-motion","sessionId":"s","clockFrequency":1000,"auditSteps":16,"frameIntervals":(0..100).map(|i|json!({"previousEndedTicks":1000+i*20,"endedTicks":1020+i*20,"intervalTicks":20,"epoch":1,"previousEpoch":1,"owned":true,"previouslyOwned":true,"diagnosticNormalUnpowered":true,"previouslyDiagnosticNormalUnpowered":true,"width":1920,"height":1080,"antialiasing":2,"previousFixedTime":i as f64*0.02,"fixedTime":(i+1) as f64*0.02})).collect::<Vec<_>>()});
        let result=window(&report,&endpoint(1000),&endpoint(3000)).unwrap();
        assert_eq!(result["meanFramesPerSecond"],50.0); assert_eq!(result["simulationRate"],1.0);
        let pristine=report.clone();
        report["frameIntervals"].as_array_mut().unwrap().remove(20);
        assert!(window(&report,&endpoint(1000),&endpoint(3000)).is_err());
        report=pristine.clone();
        report["frameIntervals"][19]["owned"]=json!(false);
        assert!(window(&report,&endpoint(1000),&endpoint(3000)).is_err());
        report=pristine;
        report["frameIntervals"][35]["diagnosticNormalUnpowered"]=json!(false);
        assert!(window(&report,&endpoint(1000),&endpoint(3000)).is_err());
    }
}
