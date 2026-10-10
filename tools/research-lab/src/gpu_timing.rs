use crate::{read, Result};
use serde_json::{json, Value};
use std::{collections::{BTreeMap, BTreeSet}, path::Path};

pub fn retain_summary(output: &Path, run: &str, pid: u32, library: &Path, inputs: &Value, flight: &Value) -> Result<Value> {
    let wrapper: Value = read(&output.join("gpu-timing.json"))?;
    let expected = inputs["files"][library.to_string_lossy().as_ref()].as_str().ok_or("GPU library was not admitted")?;
    summarize(&wrapper, run, pid, library.to_string_lossy().as_ref(), expected, flight)
}

fn summarize(wrapper: &Value, run: &str, pid: u32, library: &str, digest: &str, flight: &Value) -> Result<Value> {
    if wrapper["schema"] != "continuum-flight-gpu-timing/v1" || wrapper["runId"] != run ||
        wrapper["processId"] != pid || wrapper["libraryPath"] != library || wrapper["librarySha256"] != digest {
        return Err("GPU diagnostic wrapper identity differs".into());
    }
    let frames: BTreeSet<i64> = flight["frames"].as_array().ok_or("flight frame witnesses absent")?.iter()
        .filter(|row| row["contextAligned"] == true).filter_map(|row| row["markerFrame"].as_i64()).collect();
    if wrapper["nativeJson"].is_null() {
        return Ok(json!({"schema":"continuum-flight-gpu-summary/v1","qualifiedGpuEvidence":false,
            "outcome":"native-library-consumer-rejected","reason":wrapper["reason"],"cleanupObserved":false}));
    }
    let mut journals: Vec<Value> = wrapper["nativeSnapshots"].as_array().ok_or("GPU snapshots absent")?.iter()
        .map(|row| serde_json::from_str(row.as_str().ok_or("GPU snapshot must be JSON text")?).map_err(|error| error.to_string()))
        .collect::<Result<_>>()?;
    let final_native: Value = serde_json::from_str(wrapper["nativeJson"].as_str().ok_or("GPU final snapshot absent")?).map_err(|error| error.to_string())?;
    journals.push(final_native.clone());
    let mut samples = BTreeMap::new();
    for journal in journals {
        if journal["schema"] != "continuum-render-timing/v1" || journal["runId"] != run || journal["processId"] != pid {
            return Err("Native GPU diagnostic identity differs".into());
        }
        for sample in journal["samples"].as_array().ok_or("Native GPU sample journal absent")? {
            let sequence = sample["sequence"].as_u64().ok_or("GPU sample sequence absent")?;
            if let Some(previous) = samples.insert(sequence, sample.clone()) {
                if previous != *sample { return Err("GPU sample changed across asynchronous snapshots".into()); }
            }
        }
    }
    let mut grouped: BTreeMap<i64, Vec<f64>> = BTreeMap::new();
    let mut omitted = 0;
    let mut invalid = 0;
    for sample in samples.values() {
        let frame = sample["frame"].as_i64().ok_or("GPU enqueue frame absent")?;
        if !frames.contains(&frame) { omitted += 1; continue; }
        if sample["status"] != "valid" { invalid += 1; continue; }
        let span = sample["gpuSpanNanoseconds"].as_u64().ok_or("Valid GPU span absent")?;
        let camera = sample["cameraId"].as_i64().ok_or("GPU camera identity absent")?;
        grouped.entry(camera).or_default().push(span as f64 / 1_000_000.0);
    }
    let mut cameras = Vec::new();
    let mut observed_flight = false;
    let mut missing_forward = 0;
    for row in wrapper["cameras"].as_array().ok_or("GPU camera callbacks absent")? {
        let id = row["instanceId"].as_i64().ok_or("GPU managed camera identity absent")?;
        let costs = grouped.get(&id).cloned().unwrap_or_default();
        if row["queuedPairs"].as_u64().unwrap_or(0) > 0 {
            if row["role"].as_str().unwrap_or("").starts_with("flight-") { observed_flight = true; }
            if costs.is_empty() { missing_forward += 1; }
        }
        cameras.push(json!({"role":row["role"],"instanceId":id,"callbacks":row,"captureValidSamples":costs.len(),
            "meanMilliseconds":if costs.is_empty(){None}else{Some(costs.iter().sum::<f64>()/costs.len() as f64)},
            "maximumMilliseconds":costs.iter().copied().reduce(f64::max)}));
    }
    let valid = grouped.values().map(Vec::len).sum::<usize>();
    let published = ["validCount", "invalidCount", "droppedCount"].iter().try_fold(0u64, |total, name| {
        total.checked_add(final_native[*name].as_u64().ok_or("GPU terminal count absent")?).ok_or("GPU terminal count overflow")
    })?;
    if samples.keys().any(|sequence| *sequence == 0 || *sequence > published) || samples.len() as u64 > published {
        return Err("GPU sample sequences exceed published terminal counts".into());
    }
    let missing_sequences = published - samples.len() as u64;
    let admitted = final_native["initializationStatus"] == "admitted" && final_native["counterBits"].as_u64().unwrap_or(0) > 0;
    let cleaned = wrapper["cleanupObserved"] == true && final_native["status"] == "cleaned" &&
        final_native["outstandingQueries"] == 0 && final_native["cleanupPending"] == false;
    let qualified = admitted && cleaned && valid > 0 && invalid == 0 && observed_flight && missing_forward == 0 &&
        missing_sequences == 0 && wrapper["snapshotDrops"] == 0 && wrapper["status"] == "complete-diagnostic";
    Ok(json!({"schema":"continuum-flight-gpu-summary/v1","qualifiedGpuEvidence":qualified,
        "outcome":if qualified{"admitted-aligned-timestamp-spans"}else if !admitted{"timer-unavailable-or-admission-rejected"}else{"incomplete-no-aligned-valid-spans"},
        "cleanupObserved":cleaned,"nativeFinal":final_native,"captureFrameWitnesses":frames.len(),"deduplicatedSamples":samples.len(),
        "captureValidSamples":valid,"captureInvalidSamples":invalid,"omittedOutsideWitnessedCaptureFrames":omitted,
        "snapshotDrops":wrapper["snapshotDrops"],"cameras":cameras,
        "nativePublishedTerminalSamples":published,"missingPublishedSequences":missing_sequences,
        "observedFlightCamera":observed_flight,"observedForwardCamerasWithoutAlignedValidSamples":missing_forward,
        "scope":"Managed enqueue-frame tags selected using context-aligned captured frame witnesses; native delayed completion is separate. Camera spans overlap/nest, exclude work before opaque and after camera completion, and must not be summed. Diagnostic overhead prohibits clean comparison to uninstrumented runs."}))
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn delayed_samples_are_deduplicated_and_capture_aligned() {
        let native = json!({"schema":"continuum-render-timing/v1","runId":"r","processId":7,"status":"cleaned","initializationStatus":"admitted","counterBits":64,"outstandingQueries":0,"cleanupPending":false,
            "validCount":2,"invalidCount":0,"droppedCount":0,
            "samples":[{"sequence":1,"frame":4,"cameraId":2,"status":"valid","gpuSpanNanoseconds":9000000},
                       {"sequence":2,"frame":5,"cameraId":2,"status":"valid","gpuSpanNanoseconds":2000000}]});
        let wrapper = json!({"schema":"continuum-flight-gpu-timing/v1","runId":"r","processId":7,"libraryPath":"/lib.dylib","librarySha256":"sha",
            "nativeSnapshots":[native.to_string()],"nativeJson":native.to_string(),"status":"complete-diagnostic","cleanupObserved":true,
            "cameras":[{"role":"flight-0","instanceId":2,"queuedPairs":2}],"snapshotDrops":0});
        let flight = json!({"frames":[{"contextAligned":true,"markerFrame":5},{"contextAligned":false,"markerFrame":4}]});
        let result = summarize(&wrapper,"r",7,"/lib.dylib","sha",&flight).unwrap();
        assert_eq!(result["captureValidSamples"],1); assert_eq!(result["deduplicatedSamples"],2);
        assert_eq!(result["missingPublishedSequences"],0); assert_eq!(result["qualifiedGpuEvidence"],true);
        assert_eq!(result["cameras"][0]["meanMilliseconds"],2.0);
        assert!(summarize(&wrapper,"wrong",7,"/lib.dylib","sha",&flight).is_err());
    }
}
