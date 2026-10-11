use crate::Result;
use serde_json::{json, Value};

pub fn summarize(report: &Value, run_id: &str, pid: u32) -> Result<Value> {
    let capture = &report["processCapture"];
    if capture.is_null() { return Ok(json!({"status":"not-requested"})); }
    if capture["schema"] != "continuum-process-capture/v1" || capture["runId"] != run_id
        || capture["processId"].as_u64() != Some(pid as u64) || capture["clockFrequency"] != report["clockFrequency"]
    { return Err("process capture identity/clock differs".into()); }
    if capture["status"] == "unavailable" { return Ok(json!({"status":"unavailable","raw":capture})); }
    if capture["status"] != "complete" { return Err("process capture incomplete".into()); }
    let tick = |row: &Value, key: &str| row[key].as_i64().filter(|v| *v >= 0).ok_or_else(|| format!("process {key} absent/negative"));
    let first = &capture["start"]; let last = &capture["end"];
    let sb = tick(first, "cpuReadBeforeTicks")?; let sa = tick(first, "cpuReadAfterTicks")?;
    let eb = tick(last, "cpuReadBeforeTicks")?; let ea = tick(last, "cpuReadAfterTicks")?;
    let start = tick(report, "captureStartClockTimestamp")?; let end = tick(report, "captureEndClockTimestamp")?;
    let frequency = tick(report, "clockFrequency")? as f64;
    if frequency <= 0.0 || first["status"] != "available" || last["status"] != "available"
        || capture["startBoundary"]["clockTimestamp"] != report["captureStartClockTimestamp"]
        || capture["endBoundary"]["clockTimestamp"] != report["captureEndClockTimestamp"]
        || !(start <= sb && sb <= sa && sa < end && end <= eb && eb <= ea)
    { return Err("process CPU read brackets/boundaries differ".into()); }
    let before_cpu = tick(first, "processCpuTicks")?; let after_cpu = tick(last, "processCpuTicks")?;
    if after_cpu < before_cpu { return Err("process CPU cumulative count regressed".into()); }
    for row in [first, last] {
        if tick(row, "readBeforeTicks")? > tick(row, "cpuReadBeforeTicks")?
            || tick(row, "rssReadBeforeTicks")? < tick(row, "cpuReadAfterTicks")?
            || tick(row, "rssReadAfterTicks")? < tick(row, "rssReadBeforeTicks")? || tick(row, "workingSetBytes")? == 0
        { return Err("process RSS brackets/value invalid".into()); }
    }
    let cpu = (after_cpu - before_cpu) as f64 / 10_000_000.0;
    let lower = (eb - sa) as f64 / frequency; let upper = (ea - sb) as f64 / frequency;
    let wall = (lower + upper) * 0.5;
    let observer_ms = ((tick(first, "rssReadAfterTicks")? - tick(first, "readBeforeTicks")?) +
        (tick(last, "rssReadAfterTicks")? - tick(last, "readBeforeTicks")?)) as f64 * 1000.0 / frequency;
    for (key, expected) in [("processCpuSeconds", cpu), ("cpuReadMidpointWallSeconds", wall),
        ("averageOccupiedCores", cpu / wall), ("snapshotReadMilliseconds", observer_ms)] {
        let value = capture[key].as_f64().filter(|v| v.is_finite()).ok_or_else(|| format!("process {key} invalid"))?;
        if (value - expected).abs() > 1e-8 * expected.abs().max(1.0) { return Err(format!("process {key} arithmetic differs")); }
    }
    Ok(json!({"status":"boundary-read-validated","raw":capture,
        "cpuReadWallLowerSeconds":lower,"cpuReadWallUpperSeconds":upper,
        "averageOccupiedCoresLower":cpu/upper,"averageOccupiedCoresUpper":cpu/lower,
        "scope":"All-thread owned-process CPU between bracketed reads; endpoint RSS only. Read interval slightly differs from flight boundary interval. Includes observers; no main-thread or GPU claim."}))
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn validates_resource_intervals_and_rejects_foreign_or_regressed_counters() {
        let row = |b, a, cpu| json!({"status":"available","readBeforeTicks":b,"cpuReadBeforeTicks":b,"cpuReadAfterTicks":a,
            "rssReadBeforeTicks":a,"rssReadAfterTicks":a+1,"processCpuTicks":cpu,"workingSetBytes":4096});
        let mut report = json!({"clockFrequency":1000,"captureStartClockTimestamp":100,"captureEndClockTimestamp":1100,
            "processCapture":{"schema":"continuum-process-capture/v1","status":"complete","runId":"run","processId":42,
                "clockFrequency":1000,"startBoundary":{"clockTimestamp":100},"endBoundary":{"clockTimestamp":1100},
                "start":row(101,102,1_000_000),"end":row(1101,1102,11_000_000),"processCpuSeconds":1.0,
                "cpuReadMidpointWallSeconds":1.0,"averageOccupiedCores":1.0,"snapshotReadMilliseconds":4.0}});
        assert_eq!(summarize(&report,"run",42).unwrap()["status"],"boundary-read-validated");
        assert!(summarize(&report,"run",43).is_err());
        report["processCapture"]["end"]["processCpuTicks"]=json!(0);
        assert!(summarize(&report,"run",42).is_err());
    }
}
