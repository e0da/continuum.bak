use crate::Result;
use serde_json::{json, Value};

pub fn validate(mode: &str, receipt: &Value, capture: &Value, run_id: &str, pid: u32, entry: &Value) -> Result<Value> {
    let phase = match mode { "frontlit" => 0.0, "three-quarter-lit" | "three-quarter-overview" => 45.0, "side-lit" => 75.0, "backlit" => 150.0, _ => return Err("unknown physical lighting preset".into()) };
    if receipt["schema"] != "continuum-flight-lighting/v1" || receipt["mode"] != mode
        || receipt["runId"] != run_id || receipt["processId"].as_u64() != Some(pid as u64)
        || receipt["vesselId"] != capture["frames"][0]["vesselId"] || receipt["parts"] != capture["frames"][0]["parts"]
        || receipt["status"] != "complete-experimental" || receipt["faults"] != 0 || !receipt["reason"].is_null()
        || receipt["restorationComplete"] != true || receipt["cleanupStatus"] != "restored-camera-removed-owned-hooks"
        || receipt["vesselAdjusted"] != false || receipt["frozenDuringCapture"] != true
        || receipt["captureCallbacks"].as_u64().unwrap_or(0) == 0 || receipt["captureGeometryChecks"].as_u64().unwrap_or(0) == 0
        || receipt["captureStartTicks"] != capture["captureStartClockTimestamp"] || receipt["captureEndTicks"] != capture["captureEndClockTimestamp"]
        || receipt["clockFrequency"] != capture["clockFrequency"]
    { return Err("physical lighting identity, capture ownership or restoration differs".into()); }
    let number = |value: &Value, field: &str| -> Result<f64> { value[field].as_f64().filter(|x| x.is_finite()).ok_or_else(|| format!("lighting numerical witness absent: {field}")) };
    if (number(receipt, "requestedPhaseDegrees")? - phase).abs() > 1e-6
        || (number(receipt, "actualPhaseDegrees")? - phase).abs() > 1.0
        || !(0.0..=1.0).contains(&number(receipt, "maxCapturePhaseErrorDegrees")?)
        || !(0.0..=1.0).contains(&number(receipt, "maxCaptureLightAgreementDegrees")?)
        || !(0.0..=0.005).contains(&number(receipt, "maxPositionErrorMeters")?)
        || !(0.0..=0.005).contains(&number(receipt, "maxAngleErrorDegrees")?)
        || receipt["illuminatedCriterionRequired"] != (mode != "backlit")
        || (mode != "backlit" && (receipt["shadowed"] != false || receipt["captureShadowed"] != false || number(receipt, "minCaptureHorizonClearanceMeters")? <= 0.0))
        || (["frontlit", "three-quarter-overview"].contains(&mode) && !(0.0..=28.0).contains(&number(receipt, "maxCaptureBodyCenterViewAngleDegrees")?))
    { return Err("physical lighting phase, sunlight or rendered pose failed".into()); }
    if mode == "three-quarter-lit" {
        if number(receipt, "cameraDistanceMeters")? != 50.0
            || !(0.0..=28.0).contains(&number(receipt, "maxCaptureBodyLimbViewAngleDegrees")?)
            || !(0.0..90.0).contains(&number(receipt, "bodyAngularRadiusDegrees")?)
            || !receipt["framingPolicy"].as_str().unwrap_or("").starts_with("Root detail:") {
            return Err("three-quarter detail framing or planetary limb witness absent or invalid".into());
        }
    }
    if mode == "three-quarter-overview" {
        let distance = number(receipt, "cameraDistanceMeters")?;
        let radius = number(receipt, "framingRadiusMeters")?;
        if !(150.0..=750000.0).contains(&distance) || radius <= 0.0
            || receipt["framingRenderers"].as_u64().is_none_or(|n| n == 0 || n > 50000)
            || !receipt["framingPolicy"].as_str().unwrap_or("").starts_with("One-time active visible Part model") {
            return Err("three-quarter lighting framing admission absent or invalid".into());
        }
    }
    if entry["lightingPlacement"] != "sunward-entry-at-source-epoch/v1"
        || !(0.0..=0.0001).contains(&number(entry, "preparedSolarAlignmentDegrees")?)
        || number(entry, "placementSpeedErrorMetersPerSecond")?.abs() > 0.0001
        || number(entry, "placementRadialSpeedErrorMetersPerSecond")?.abs() > 0.0001
    { return Err("lighting entry preparation did not preserve declared orbital invariants".into()); }
    for field in ["toSun", "bodyRadial", "cameraOffset", "lightRayDirection"] {
        let vector = receipt[field].as_array().filter(|v| v.len() == 3).ok_or("lighting direction witness absent")?;
        let mut square = 0.0;
        for x in vector { square += x.as_f64().filter(|x| x.is_finite()).ok_or("lighting direction is nonfinite")?.powi(2); }
        if (square - 1.0).abs() > 1e-5 { return Err("lighting direction is not normalized".into()); }
    }
    Ok(json!({"schema":"continuum-flight-lighting-summary/v1","recipe":mode,"receipt":receipt,"placement":entry["lightingPlacement"],
        "qualification":"Physical preset and restoration observed for this capture; actual image inspection is a separate gate. Sunward entry is a new workload treatment; observer overhead unqualified."}))
}
