use crate::Result;
use serde_json::{json, Value};

fn ticks(v: &Value, key: &str) -> Result<u64> {
    v[key].as_u64().ok_or_else(|| format!("convection cost missing: {key}"))
}

pub fn summarize(mode: &str, r: &Value, run: &str, pid: u32) -> Result<Value> {
    if r["schema"] != "continuum-convection-replacement-v1" || r["mode"] != mode
        || r["runId"] != run || r["processId"] != pid
        || r["status"] != "completed-experimental" || r["cleanupStatus"] != "removed-owned-hooks"
        || r["hookShapeVerified"] != true || r["faults"] != 0 || r["verificationMismatches"] != 0
        || ticks(r,"captureFallbacks")? != 0 || ticks(r,"captureReplacements")? == 0
    { return Err("live convection behavior or owned cleanup failed; raw markers retained".into()); }
    let frequency=ticks(r,"clockFrequency")?;
    if frequency==0 {return Err("convection clock frequency missing".into());}
    let mut pairs=Vec::new();
    if mode=="verify" {
        let samples=r["samples"].as_array().filter(|s|s.len()==2).ok_or("two convection observations required")?;
        if r["pairedSamples"]!=2 {return Err("convection paired observation count differs".into());}
        for (i,s) in samples.iter().enumerate() {
            if s["sequence"]!=i || s["bitwiseMatch"]!=true
                || s["order"]!=if i==0 {json!("stock-candidate")} else {json!("candidate-stock")}
            {return Err("convection paired order or bitwise proof differs".into());}
            let stock=ticks(s,"stockCompleteTicks")?; let candidate=ticks(s,"candidateCompleteTicks")?;
            if stock==0 || candidate==0
                || stock!=ticks(s,"projectionTicks")?+ticks(s,"stockDirectFoldTicks")?+ticks(s,"commonFxTicks")?
                || candidate!=ticks(s,"projectionTicks")?+ticks(s,"candidateCopyTicks")?+ticks(s,"candidateFoldTicks")?+ticks(s,"candidateCommitTicks")?+ticks(s,"commonFxTicks")?
            {return Err("convection complete method cost does not reconcile".into());}
            let ms=1000.0/frequency as f64;
            pairs.push(json!({"sequence":i,"stockMilliseconds":stock as f64*ms,"candidateMilliseconds":candidate as f64*ms,"costReductionFraction":1.-candidate as f64/stock as f64,"verificationOnlyMilliseconds":(ticks(s,"snapshotTicks")?+ticks(s,"rollbackTicks")?+ticks(s,"comparisonTicks")?) as f64*ms}));
        }
    } else if mode!="query" {return Err("unknown convection mode".into());}
    Ok(json!({"schema":"continuum-live-convection-summary/v1","report":r,"pairs":pairs,"qualification":"Same-state Mono complete-method costs share original projection and one FX update; candidate copy/fold/commit included. Verification snapshot/rollback/comparison tax separate. No paired full-frame FPS, deterministic thermal evolution or broad mod/topology qualification."}))
}
