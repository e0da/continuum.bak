use crate::Result;
use serde_json::{json, Value};

fn number(v: &Value, key: &str) -> Result<u64> {
    v[key].as_u64().ok_or_else(|| format!("cylinder field missing: {key}"))
}
fn check(value: bool, message: &str) -> Result<()> {
    if value { Ok(()) } else { Err(message.into()) }
}
fn window(w: &Value) -> Result<()> {
    let start = number(w, "startedTicks")?;
    let seam = number(w, "seamTicks")?;
    let end = number(w, "endedTicks")?;
    check(w["completed"] == true && w["fallback"].is_null() && w["mismatch"].is_null()
        && start > 0 && start <= seam && seam <= end && number(w,"completeTicks")? == end-start
        && number(w,"projectionTicks")? == seam-start, "cylinder complete window invalid")?;
    for key in ["admissionCopyTicks", "indexTicks", "validationTicks", "publicationTicks", "comparisonTicks", "finalizerTicks"] {
        check(number(w,key)? <= end-start, "cylinder phase exceeds complete envelope")?;
    }
    Ok(())
}

pub fn summarize(mode: &str, r: &Value, probe: &Value, run: &str, pid: u32) -> Result<Value> {
    check(matches!(mode,"verify"|"compare"|"query") && r["schema"] == "continuum-cylinder-occlusion-replacement/v1"
        && r["mode"] == mode && r["runId"] == run && r["processId"] == pid
        && r["status"] == "completed-experimental" && r["cleanupStatus"] == "removed-owned-hooks"
        && r["assemblySha256"] == "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27"
        && r["assemblyMvid"] == "10657063-2fc3-43a7-84fa-d39e75e877bf", "cylinder identity/lifecycle differs")?;
    for key in ["errors", "originalExceptions", "verificationMismatches", "partialPublicationFailures", "recursionStops", "fallbacks", "captureFallbacks", "droppedWindows"] {
        check(number(r,key)? == 0, "cylinder fault or incomplete evidence")?;
    }
    let frequency=number(r,"clockFrequency")?; check(frequency>0,"cylinder clock absent")?;
    let capture_start=number(probe,"captureStartClockTimestamp")?;
    let capture_end=number(probe,"captureEndClockTimestamp")?;
    let completed=number(probe,"completedFrames")? as usize;
    let frames=probe["frames"].as_array().ok_or("enclosing frames absent")?;
    check(probe["status"]=="complete" && probe["runId"]==run && number(probe,"clockFrequency")?==frequency
        && capture_start>0 && capture_end>capture_start && completed>0 && completed<=frames.len()
        && r["vesselId"].as_str().is_some_and(|s|!s.is_empty())
        && frames.iter().take(completed).all(|f|f["vesselId"]==r["vesselId"]),"cylinder enclosing capture/vessel differs")?;
    let ms=1000.0/frequency as f64;
    let passes=r["passes"].as_array().filter(|a|a.len()==2).ok_or("two cylinder passes required")?;
    let first=r["firstInvocations"].as_array().filter(|a|a.len()==2).ok_or("two original-return witnesses required")?;
    let windows=r["windows"].as_array().filter(|a|!a.is_empty()).ok_or("captured cylinder windows required")?;
    check(windows.len() as u64 == number(r,"captureCalls")?,"cylinder call/window count differs")?;
    check(r["preinstallPatchGraph"].is_array() && r["restoredPatchGraph"] == r["preinstallPatchGraph"],"cylinder graph restoration differs")?;
    let mut domains=Vec::new(); let mut replacements=0;
    for (kind,pass) in passes.iter().enumerate() {
        let name=if kind==0{"solar"}else{"body"};
        check(pass["kind"]==name && pass["metadataToken"]==100677507+kind && pass["shapeAdmissions"]==1
            && pass["verifiedPasses"]==1 && number(pass,"indexPreparations")?>=1 && pass["qualificationResets"]==0
            && pass["ilSha256"].as_str().is_some_and(|s|s.len()==64&&s.bytes().all(|b|b.is_ascii_hexdigit())),"cylinder admitted/proved domain differs")?;
        window(&first[kind])?;
        check(first[kind]["kind"]==name && first[kind]["treatment"]=="verify-original-return" && first[kind]["firstInvocation"]==true
            && first[kind]["bitwiseMatch"]==true && first[kind]["indexPrepared"]==true,"cylinder original-return proof absent")?;
        if mode!="verify" {check(number(&first[kind],"endedTicks")?<=capture_start,"cylinder qualification not before capture")?;}
        let rows=number(pass,"count")?; check(rows>0 && rows<=1024,"cylinder row count unsupported")?;
        let captured: Vec<_>=windows.iter().filter(|w|w["kind"]==name).collect();
        check(captured.len() as u64==number(pass,"captureCalls")?,"cylinder domain coverage differs")?;
        let mut controls=0; let mut queries=0; let mut control_cost=0.; let mut query_cost=0.; let mut pairs=Vec::new();
        for (sequence,w) in captured.iter().enumerate() {
            window(w)?;
            check(w["captureActiveAtEnd"]==true && number(w,"rowCount")?==rows && number(w,"acceptedCylinders")?>=1
                && number(w,"acceptedCylinders")?<=rows && number(w,"startedTicks")?>=capture_start
                && number(w,"endedTicks")?<=capture_end,"cylinder capture domain/enclosure changed")?;
            if mode=="compare" {
                check(w["treatmentSequence"]==sequence && w["treatment"]==if sequence%2==0{"control-original"}else{"query"}
                    && w["indexPrepared"]==false && w["firstInvocation"]==false && number(w,"comparisonTicks")?==0,"cylinder alternating sequence differs")?;
            }
            if w["treatment"]=="control-original" {
                check(w["scalarCalls"]==-1 && number(w,"indexTicks")?==0 && number(w,"publicationTicks")?==0,"control performed candidate work")?;
                controls+=1; control_cost+=number(w,"completeTicks")? as f64;
            } else if w["treatment"]=="query" {
                check(number(w,"scalarCalls")? <= rows*(rows-1)/2 && number(w,"indexTicks")?>0 && number(w,"publicationTicks")?>0,"query workload evidence absent")?;
                queries+=1; query_cost+=number(w,"completeTicks")? as f64;
                if mode=="compare" {
                    let control=captured[sequence-1]; let original=number(control,"completeTicks")?; let candidate=number(w,"completeTicks")?;
                    pairs.push(json!({"controlSequence":control["sequence"],"candidateSequence":w["sequence"],
                        "originalMilliseconds":original as f64*ms,"candidateMilliseconds":candidate as f64*ms,
                        "reductionFraction":1.-candidate as f64/original as f64,
                        "orderMatched":control["orderFingerprint"]==w["orderFingerprint"],"rectangleGeometryMatched":control["geometryFingerprint"]==w["geometryFingerprint"]}));
                }
            } else {check(mode=="verify" && (w["treatment"]=="verify-original-return" || w["treatment"]=="original-qualified"),"unexpected cylinder treatment")?;}
        }
        check(controls==number(pass,"controlPasses")? && queries==number(pass,"candidatePasses")? && queries==number(pass,"captureReplacements")?,"cylinder arm totals differ")?;
        if mode=="compare" {check(queries>=2 && controls>=queries && controls<=queries+1,"two complete adjacent cylinder observations required")?;}
        if mode=="query" {check(queries>0,"cylinder query coverage absent")?;}
        if mode=="verify" {check(pass["capturedVerifiedPasses"]==1,"captured original-return verification absent")?;}
        replacements+=queries;
        domains.push(json!({"kind":name,"rows":rows,"controlPasses":controls,"candidatePasses":queries,
            "originalMeanMilliseconds":if controls>0{Some(control_cost*ms/controls as f64)}else{None},
            "candidateMeanMilliseconds":if queries>0{Some(query_cost*ms/queries as f64)}else{None},"adjacentObservations":pairs}));
    }
    for (sequence,w) in windows.iter().enumerate() {check(w["sequence"]==sequence,"cylinder global window sequence differs")?;}
    check(replacements==number(r,"captureReplacements")?,"cylinder replacement total differs")?;
    Ok(json!({"schema":"continuum-live-cylinder-summary/v1","status":"completed-experimental","mode":mode,"domains":domains,
        "qualification":"Original-return bitwise proof in both domains; subsequent original-control and query invocations use adjacent evolving states, not identical inputs. Complete cost includes shared instrumentation/admission, original projection/sort and candidate copy/index/validation/original Setup/publication/finalizer; outer Harmony dispatch excluded. Comparison ticks overlap finalizer proof tax and are not additive. Control scalar calls unmeasured. No uninstrumented stock cost, paired confidence interval or whole-frame FPS gain."}))
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn refuses_missing_identity_and_truncated_cost() {
        assert!(summarize("compare",&json!({}),&json!({}),"run",1).is_err());
        let w=json!({"completed":true,"startedTicks":10,"seamTicks":20,"endedTicks":30,"completeTicks":19,"projectionTicks":10});
        assert!(window(&w).is_err());
    }
}
