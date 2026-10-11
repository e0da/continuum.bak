use crate::Result;
use serde_json::{json, Value};

const SEAMS: [(u64, &str); 6] = [
    (100677458, "Update"),
    (100677459, "ThermoPrecalculate"),
    (100677505, "UpdateOcclusion"),
    (100677506, "UpdateOcclusionConvection"),
    (100677507, "UpdateOcclusionSolar"),
    (100677508, "UpdateOcclusionBody"),
];
const MAX_WINDOWS: usize = 4096;

#[derive(Clone, Copy, Default)]
struct Row {
    calls: u64,
    outer_calls: u64,
    ticks: u64,
    outer_ticks: u64,
}
#[derive(Default)]
struct Phase {
    windows: u64,
    ticks: u64,
    rows: [Row; 6],
}
fn number(value: &Value, field: &str) -> Result<u64> {
    value[field]
        .as_u64()
        .filter(|value| *value <= i64::MAX as u64)
        .ok_or_else(|| format!("integrator {field} must be a nonnegative signed-clock integer"))
}
fn add(total: &mut u64, value: u64) -> Result<()> {
    *total = total
        .checked_add(value)
        .ok_or("integrator total overflow")?;
    Ok(())
}
fn slots(value: &Value, field: &str) -> Result<[u64; 6]> {
    let values = value[field]
        .as_array()
        .ok_or(format!("integrator {field} absent"))?;
    if values.len() != 6 {
        return Err(format!("integrator {field} must have six slots"));
    }
    let mut result = [0; 6];
    for (index, value) in values.iter().enumerate() {
        result[index] = value
            .as_u64()
            .filter(|value| *value <= i64::MAX as u64)
            .ok_or(format!("integrator {field} has an invalid slot"))?;
    }
    Ok(result)
}
fn digest(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}
fn accumulate(phase: &mut Phase, elapsed: u64, rows: &[Row; 6]) -> Result<()> {
    add(&mut phase.windows, 1)?;
    add(&mut phase.ticks, elapsed)?;
    for (total, row) in phase.rows.iter_mut().zip(rows) {
        add(&mut total.calls, row.calls)?;
        add(&mut total.outer_calls, row.outer_calls)?;
        add(&mut total.ticks, row.ticks)?;
        add(&mut total.outer_ticks, row.outer_ticks)?;
    }
    Ok(())
}

pub fn summarize(report: &Value) -> Result<Value> {
    let observer = &report["flightIntegratorUpdateAttribution"];
    if report["status"] != "complete"
        || observer["schema"] != "continuum-flight-integrator-attribution/v1"
        || observer["status"] != "observed"
        || observer["witnessStatus"] != "passed"
        || observer["cleanupStatus"] != "removed-owned-patches"
        || number(observer, "patchedMethods")? != 6
        || !observer["assemblySha256"].as_str().is_some_and(digest)
    {
        return Err("integrator attribution requires a closed, observed six-method witness with owned cleanup".into());
    }
    for field in [
        "callbackErrors",
        "contextErrors",
        "originalExceptions",
        "droppedWindows",
    ] {
        if number(observer, field)? != 0 {
            return Err(format!("integrator attribution has {field}"));
        }
    }
    let frames = number(report, "completedFrames")?;
    let frequency = number(report, "clockFrequency")?;
    let start = number(report, "captureStartClockTimestamp")?;
    let end = number(report, "captureEndClockTimestamp")?;
    if frames == 0
        || frames > MAX_WINDOWS as u64
        || frequency == 0
        || start == 0
        || end <= start
        || number(observer, "clockFrequency")? != frequency
    {
        return Err(
            "integrator capture clock, interval or completed render denominator differs".into(),
        );
    }
    let order = observer["seamOrder"]
        .as_array()
        .ok_or("integrator seam identities absent")?;
    if order.len() != 6 {
        return Err("integrator requires exactly six seam identities".into());
    }
    let mut tokens = [0; 6];
    let mut hashes = [""; 6];
    for (index, identity) in order.iter().enumerate() {
        let fields: Vec<_> = identity
            .as_str()
            .ok_or("integrator seam identity is not text")?
            .split('|')
            .collect();
        if fields.len() != 4
            || fields[0] != "Assembly-CSharp"
            || fields[1] != "FlightIntegrator"
            || !digest(fields[3])
        {
            return Err("integrator seam identity differs".into());
        }
        let token = fields[2]
            .parse::<u64>()
            .map_err(|error| error.to_string())?;
        if !SEAMS.iter().any(|(known, _)| *known == token) || tokens[..index].contains(&token) {
            return Err("integrator seam token is unknown or duplicated".into());
        }
        tokens[index] = token;
        hashes[index] = fields[3];
    }
    let raw = observer["windows"]
        .as_array()
        .ok_or("integrator raw windows absent")?;
    if raw.is_empty() || raw.len() > MAX_WINDOWS {
        return Err("integrator raw window count outside bounds".into());
    }
    let mut lifetime = [Phase::default(), Phase::default()];
    let mut captured = [Phase::default(), Phase::default()];
    let mut previous_end = 0;
    let mut omitted = 0;
    for (sequence, window) in raw.iter().enumerate() {
        let phase = match window["phase"].as_str() {
            Some("update") => 0,
            Some("fixed") => 1,
            _ => return Err("integrator window phase differs".into()),
        };
        let before = number(window, "startedTicks")?;
        let after = number(window, "endedTicks")?;
        let elapsed = number(window, "elapsedTicks")?;
        if number(window, "sequence")? != sequence as u64
            || number(window, "frame")? > i32::MAX as u64
            || before == 0
            || after < before
            || elapsed != after - before
            || before < previous_end
            || !window["timeSeconds"]
                .as_f64()
                .is_some_and(|time| time.is_finite() && time >= 0.0)
        {
            return Err("integrator raw window sequence, context or timestamps differ".into());
        }
        previous_end = after;
        let calls = slots(window, "calls")?;
        let outer_calls = slots(window, "outermostCalls")?;
        let ticks = slots(window, "inclusiveTicks")?;
        let outer_ticks = slots(window, "outermostInclusiveTicks")?;
        let mut rows = [Row::default(); 6];
        for slot in 0..6 {
            if calls[slot] > i32::MAX as u64
                || outer_calls[slot] > calls[slot]
                || outer_ticks[slot] > ticks[slot]
                || outer_ticks[slot] > elapsed
                || (calls[slot] == 0 && (ticks[slot] != 0 || outer_ticks[slot] != 0))
                || (calls[slot] > 0 && outer_calls[slot] == 0)
            {
                return Err("integrator raw call/tick bounds differ".into());
            }
            rows[slot] = Row {
                calls: calls[slot],
                outer_calls: outer_calls[slot],
                ticks: ticks[slot],
                outer_ticks: outer_ticks[slot],
            };
        }
        accumulate(&mut lifetime[phase], elapsed, &rows)?;
        if before >= start && after <= end {
            accumulate(&mut captured[phase], elapsed, &rows)?;
        } else {
            omitted += 1;
        }
    }
    for (index, (name, count_field)) in [
        ("Update", "completedUpdateWindows"),
        ("Fixed", "completedFixedWindows"),
    ]
    .iter()
    .enumerate()
    {
        let phase = &observer[*name];
        let phase_schema = format!("continuum-flight-integrator-{}/v1", name.to_lowercase());
        if phase["schema"] != phase_schema
            || !matches!(phase["status"].as_str(), Some("observed" | "no-coverage"))
            || phase["cleanupStatus"] != "removed-owned-patches"
            || number(phase, "callbackErrors")? != 0
            || number(phase, "scanErrors")? != 0
            || number(phase, "patchedMethods")? != 6
            || number(phase, "discoveredMethods")? != 6
            || number(phase, "clockFrequency")? != frequency
            || number(phase, "activeWindowTicks")? != lifetime[index].ticks
            || number(observer, count_field)? != lifetime[index].windows
            || captured[index].windows == 0
            || captured[index].ticks == 0
        {
            return Err(format!(
                "integrator {name} phase witness differs or lacks enclosed windows"
            ));
        }
        let callbacks = phase["callbacks"]
            .as_array()
            .ok_or("integrator callback totals absent")?;
        if callbacks.len() != 6 {
            return Err("integrator phase requires six callback rows".into());
        }
        let mut seen = [false; 6];
        for callback in callbacks {
            let token = number(callback, "metadataToken")?;
            let slot = tokens
                .iter()
                .position(|known| *known == token)
                .ok_or("integrator callback token differs")?;
            let method = SEAMS.iter().find(|(known, _)| *known == token).unwrap().1;
            let totals = lifetime[index].rows[slot];
            if seen[slot]
                || callback["method"] != method
                || callback["assembly"] != "Assembly-CSharp"
                || callback["declaringType"] != "FlightIntegrator"
                || callback["ilSha256"] != hashes[slot]
                || callback["assemblyMvid"] != "10657063-2fc3-43a7-84fa-d39e75e877bf"
                || number(callback, "calls")? != totals.calls
                || number(callback, "outermostCalls")? != totals.outer_calls
                || number(callback, "inclusiveTicks")? != totals.ticks
                || number(callback, "outermostInclusiveTicks")? != totals.outer_ticks
                || (index == 0 && totals.calls == 0)
            {
                return Err("integrator raw totals or six-method callback identity differ".into());
            }
            seen[slot] = true;
        }
    }
    let ms = |ticks: u64| ticks as f64 * 1000.0 / frequency as f64;
    let phases: Vec<_> = captured.iter().enumerate().map(|(index, phase)| {
        let methods: Vec<_> = SEAMS.iter().map(|(token, method)| {
            let slot = tokens.iter().position(|known| known == token).unwrap();
            let row = phase.rows[slot];
            json!({"method":method,"metadataToken":token,"calls":row.calls,
                "coverage":if row.calls>0{"executed"}else{"no-execution-coverage"},
                "inclusiveTicks":row.ticks,"inclusiveMilliseconds":ms(row.ticks),
                "meanInclusiveMillisecondsPerCall":if row.calls>0{Some(ms(row.ticks)/row.calls as f64)}else{None},
                "meanInclusiveMillisecondsPerCompletedRenderFrame":if row.calls>0{Some(ms(row.ticks)/frames as f64)}else{None},
                "inclusiveFractionOfPhaseWindow":if row.calls>0{Some(row.ticks as f64/phase.ticks as f64)}else{None},
                "outermostCalls":row.outer_calls,"outermostInclusiveTicks":row.outer_ticks})
        }).collect();
        json!({"phase":if index==0{"update"}else{"fixed"},"enclosedWindows":phase.windows,
            "phaseWindowTicks":phase.ticks,"phaseWindowMilliseconds":ms(phase.ticks),
            "meanPhaseWindowMilliseconds":ms(phase.ticks)/phase.windows as f64,"methods":methods})
    }).collect();
    Ok(
        json!({"schema":"continuum-flight-integrator-summary/v1","status":"observed",
        "runId":report["runId"],
        "assemblySha256":observer["assemblySha256"],"seamOrder":observer["seamOrder"],
        "completedRenderFrames":frames,"clockFrequency":frequency,"captureStartClockTimestamp":start,"captureEndClockTimestamp":end,
        "rawWindows":raw.len(),"omittedOutsideOrStraddlingCaptureWindows":omitted,"phases":phases,
        "scope":"Only fully enclosed raw windows are selected. Method means per completed render frame use completedFrames; phase-window means and fractions use each phase's own denominator. Inclusive parents contain nested child rows and are nonadditive; outermost only removes same-method recursion. Zero calls mean no execution coverage, not zero cost. Intrusive observer tax remains unqualified; these are wall samples, not exclusive CPU or an uninstrumented speedup estimate."}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn excludes_straddlers_and_maps_reordered_rows_by_token() {
        let hash = "a".repeat(64);
        let window = |sequence, phase, before, after, ticks| {
            json!({"sequence":sequence,"phase":phase,"frame":9,
            "timeSeconds":1.0,"startedTicks":before,"endedTicks":after,"elapsedTicks":after-before,
            "calls":[1,1,1,1,1,1],"outermostCalls":[1,1,1,1,1,1],
            "inclusiveTicks":vec![ticks;6],"outermostInclusiveTicks":vec![ticks;6]})
        };
        let raw = vec![
            window(0, "update", 50, 150, 80),
            window(1, "fixed", 160, 170, 2),
            window(2, "update", 180, 200, 3),
        ];
        let phase = |name, count, denominator, calls, ticks| {
            let callbacks: Vec<_> = SEAMS.iter().rev().map(|(token, method)| json!({"assembly":"Assembly-CSharp",
                "declaringType":"FlightIntegrator","metadataToken":token,"method":method,"ilSha256":hash,
                "assemblyMvid":"10657063-2fc3-43a7-84fa-d39e75e877bf",
                "calls":calls,"outermostCalls":calls,"inclusiveTicks":ticks,"outermostInclusiveTicks":ticks})).collect();
            json!({"status":"observed","cleanupStatus":"removed-owned-patches","patchedMethods":6,"discoveredMethods":6,
                "callbackErrors":0,"scanErrors":0,"clockFrequency":1000,"activeWindowTicks":denominator,"callbacks":callbacks,
                "schema":format!("continuum-flight-integrator-{name}/v1"),"windowCount":count})
        };
        let mut report = json!({"status":"complete","completedFrames":4,"clockFrequency":1000,
            "captureStartClockTimestamp":100,"captureEndClockTimestamp":220,"flightIntegratorUpdateAttribution":{
                "schema":"continuum-flight-integrator-attribution/v1","status":"observed","witnessStatus":"passed",
                "assemblySha256":hash,
                "cleanupStatus":"removed-owned-patches","patchedMethods":6,"callbackErrors":0,"contextErrors":0,
                "originalExceptions":0,"droppedWindows":0,"clockFrequency":1000,"completedUpdateWindows":2,"completedFixedWindows":1,
                "seamOrder":SEAMS.iter().map(|(token,_)|format!("Assembly-CSharp|FlightIntegrator|{token}|{hash}")).collect::<Vec<_>>(),
                "windows":raw,"Update":phase("update",2,120,2,83),"Fixed":phase("fixed",1,10,1,2)}});
        let result = summarize(&report).unwrap();
        assert_eq!(result["omittedOutsideOrStraddlingCaptureWindows"], 1);
        assert_eq!(result["phases"][0]["enclosedWindows"], 1);
        assert_eq!(result["phases"][0]["methods"][0]["method"], "Update");
        assert_eq!(
            result["phases"][0]["methods"][0]["meanInclusiveMillisecondsPerCompletedRenderFrame"],
            0.75
        );
        report["flightIntegratorUpdateAttribution"]["windows"][2]["inclusiveTicks"] = json!([3, 3]);
        assert!(summarize(&report).is_err());
    }
}
