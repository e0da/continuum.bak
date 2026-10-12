use crate::Result;
use serde_json::{json, Value};
use std::collections::BTreeSet;

fn integer(value: &Value, field: &str) -> Result<u64> {
    value[field]
        .as_u64()
        .filter(|n| *n <= i64::MAX as u64)
        .ok_or_else(|| format!("Update callback {field} is not a nonnegative signed integer"))
}
fn digest(value: &Value) -> bool {
    value
        .as_str()
        .is_some_and(|s| s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit()))
}
fn text<'a>(value: &'a Value, field: &str) -> Result<&'a str> {
    value[field]
        .as_str()
        .filter(|s| !s.trim().is_empty())
        .ok_or_else(|| format!("Update callback {field} absent"))
}
fn add(total: &mut u64, next: u64) -> Result<()> {
    *total = total.checked_add(next).ok_or("Update callback overflow")?;
    Ok(())
}

pub fn summarize(report: &Value) -> Result<Value> {
    let observer = &report["updateCallbackAttribution"];
    if report["status"] != "complete"
        || observer["schema"] != "continuum-update-callback-attribution/v1"
        || observer["runId"] != report["runId"]
        || observer["status"] != "observed"
        || observer["cleanupStatus"] != "removed-owned-patches"
    {
        return Err("Update attribution requires a matching completed owner and cleanup".into());
    }
    for field in ["errors", "droppedWindows", "originalExceptions"] {
        if integer(observer, field)? != 0 {
            return Err(format!("Update attribution has {field}"));
        }
    }
    let frequency = integer(report, "clockFrequency")?;
    let start = integer(report, "captureStartClockTimestamp")?;
    let end = integer(report, "captureEndClockTimestamp")?;
    let frames = integer(report, "completedFrames")?;
    if frequency == 0
        || frequency != integer(observer, "clockFrequency")?
        || start == 0
        || end <= start
        || frames == 0
    {
        return Err("Update callback capture clock or denominator differs".into());
    }
    let targets = observer["targets"]
        .as_array()
        .ok_or("Update targets absent")?;
    if targets.is_empty()
        || targets.len() > 512
        || integer(observer, "targetCount")? != targets.len() as u64
        || integer(observer, "patchedMethods")? != targets.len() as u64
    {
        return Err("Update target coverage or bound differs".into());
    }
    let mut identities = BTreeSet::new();
    for (slot, target) in targets.iter().enumerate() {
        let mvid = text(target, "assemblyMvid")?;
        let mvid_valid = mvid.len() == 36
            && mvid.bytes().enumerate().all(|(i, b)| {
                if [8, 13, 18, 23].contains(&i) {
                    b == b'-'
                } else {
                    b.is_ascii_hexdigit()
                }
            });
        let token = integer(target, "metadataToken")?;
        if integer(target, "slot")? != slot as u64
            || target["method"] != "Update"
            || !mvid_valid
            || token >> 24 != 6
            || !digest(&target["assemblySha256"])
            || !digest(&target["ilSha256"])
            || !identities.insert((text(target, "assembly")?, mvid, token))
        {
            return Err("Update target identity is invalid or duplicated".into());
        }
        text(target, "declaringType")?;
    }
    let windows = observer["windows"]
        .as_array()
        .ok_or("Update windows absent")?;
    if windows.is_empty() || windows.len() > 1024 {
        return Err("Update window bound differs".into());
    }
    let mut totals = vec![(0_u64, 0_u64, 0_u64); targets.len()];
    let (mut enclosed, mut omitted, mut previous_end, mut previous_frame, mut window_ticks) =
        (0_u64, 0_u64, 0_u64, 0_u64, 0_u64);
    for (sequence, window) in windows.iter().enumerate() {
        let before = integer(window, "startedTicks")?;
        let after = integer(window, "endedTicks")?;
        let frame = integer(window, "frame")?;
        if integer(window, "sequence")? != sequence as u64
            || frame > i32::MAX as u64
            || (sequence > 0 && frame <= previous_frame)
            || before == 0
            || after < before
            || before < previous_end
        {
            return Err("Update window order, frame or clocks differ".into());
        }
        previous_end = after;
        previous_frame = frame;
        let elapsed = after - before;
        let entries = window["entries"]
            .as_array()
            .ok_or("Update entries absent")?;
        let mut slots = BTreeSet::new();
        let mut self_ticks = 0_u64;
        let captured = before >= start && after <= end;
        for entry in entries {
            let slot = integer(entry, "slot")? as usize;
            let calls = integer(entry, "calls")?;
            let inclusive = integer(entry, "inclusiveTicks")?;
            let own = integer(entry, "selfTicks")?;
            if slot >= targets.len()
                || !slots.insert(slot)
                || calls == 0
                || calls > i32::MAX as u64
                || own > inclusive
                || inclusive as u128 > elapsed as u128 * calls as u128
            {
                return Err("Update row counters or nested clock bounds differ".into());
            }
            add(&mut self_ticks, own)?;
            if captured {
                add(&mut totals[slot].0, calls)?;
                add(&mut totals[slot].1, inclusive)?;
                add(&mut totals[slot].2, own)?;
            }
        }
        if self_ticks > elapsed {
            return Err("Update self clocks exceed their enclosing window".into());
        }
        if captured {
            enclosed += 1;
            add(&mut window_ticks, elapsed)?;
        } else {
            omitted += 1;
        }
    }
    if enclosed == 0 {
        return Err("Update has no fully enclosed capture windows".into());
    }
    let millis = 1000.0 / frequency as f64;
    let mut rows: Vec<_> = targets
        .iter()
        .zip(&totals)
        .map(|(identity, &(calls, inclusive, own))| {
            json!({"identity":identity,"calls":calls,
                "coverage":if calls==0 {"not-reached"} else {"reached"},
                "inclusiveMilliseconds":inclusive as f64*millis,
                "selfMilliseconds":own as f64*millis,
                "inclusiveMillisecondsPerRenderedFrame":inclusive as f64*millis/frames as f64,
                "selfMillisecondsPerRenderedFrame":own as f64*millis/frames as f64})
        })
        .collect();
    rows.sort_by(|a, b| {
        b["selfMilliseconds"]
            .as_f64()
            .unwrap()
            .total_cmp(&a["selfMilliseconds"].as_f64().unwrap())
    });
    Ok(
        json!({"schema":"continuum-update-callback-summary/v1","status":"capture-validated",
        "runId":report["runId"],"enclosedWindows":enclosed,"omittedStraddlerWindows":omitted,
        "renderedFrames":frames,"targetCount":targets.len(),"enclosedWindowMilliseconds":window_ticks as f64*millis,
        "rows":rows,"scope":"Capture-enclosed declared MonoBehaviour.Update method-body wall brackets. Self subtracts only nested observed methods; unobserved descendants and native work remain in self. Hook tax and phase gaps are not exclusive CPU. Zero-call targets have no cost coverage. One intrusive owner-identification flight is not a clean performance arm."}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    fn valid() -> Value {
        json!({"status":"complete","runId":"r","clockFrequency":1000,
            "captureStartClockTimestamp":100,"captureEndClockTimestamp":400,"completedFrames":2,
            "updateCallbackAttribution":{"schema":"continuum-update-callback-attribution/v1","runId":"r",
                "status":"observed","cleanupStatus":"removed-owned-patches","errors":0,"droppedWindows":0,
                "originalExceptions":0,"clockFrequency":1000,"targetCount":1,"patchedMethods":1,
                "targets":[{"slot":0,"assembly":"game","assemblyMvid":"10657063-2fc3-43a7-84fa-d39e75e877bf",
                    "assemblySha256":"a".repeat(64),"ilSha256":"b".repeat(64),"declaringType":"Owner",
                    "method":"Update","metadataToken":100677458}],
                "windows":[{"sequence":0,"frame":3,"startedTicks":120,"endedTicks":180,
                    "entries":[{"slot":0,"calls":2,"inclusiveTicks":70,"selfTicks":50}]},
                    {"sequence":1,"frame":4,"startedTicks":220,"endedTicks":260,"entries":[]}]}})
    }
    #[test]
    fn rejects_false_owner_cost_and_incomplete_windows() {
        let raw = valid();
        let summary = summarize(&raw).unwrap();
        assert_eq!(summary["rows"][0]["selfMillisecondsPerRenderedFrame"], 25.0);
        let mut nested = raw.clone();
        nested["updateCallbackAttribution"]["windows"][0]["entries"][0]["selfTicks"] = json!(61);
        assert!(summarize(&nested).is_err());
        let mut duplicate = raw.clone();
        let entry = duplicate["updateCallbackAttribution"]["windows"][0]["entries"][0].clone();
        duplicate["updateCallbackAttribution"]["windows"][0]["entries"]
            .as_array_mut()
            .unwrap()
            .push(entry);
        assert!(summarize(&duplicate).is_err());
        let mut wrong_clock = raw.clone();
        wrong_clock["updateCallbackAttribution"]["clockFrequency"] = json!(999);
        assert!(summarize(&wrong_clock).is_err());
        let mut no_capture = raw;
        no_capture["captureStartClockTimestamp"] = json!(300);
        assert!(summarize(&no_capture).is_err());
    }
}
