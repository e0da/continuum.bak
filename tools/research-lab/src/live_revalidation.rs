use crate::{hash, play, read, write_new, Result};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{fs, path::Path};

fn regular(path: &Path) -> Result<()> {
    let metadata = fs::symlink_metadata(path).map_err(|e| e.to_string())?;
    if !metadata.is_file() || metadata.file_type().is_symlink() {
        return Err("offline live proof requires regular nonsymlink files".into());
    }
    Ok(())
}

pub fn retain(run_directory: &Path, output: &Path) -> Result<Value> {
    let run = run_directory.canonicalize().map_err(|e| e.to_string())?;
    if output.exists() || !output.is_absolute() {
        return Err("new absolute offline validation directory required".into());
    }
    let receipt_path = run.join("receipt.json");
    let launch_path = run.join("resolved-launch.json");
    regular(&receipt_path)?;
    regular(&launch_path)?;
    let receipt_sha = hash(&receipt_path)?;
    let launch_sha = hash(&launch_path)?;
    let original: Value = read(&receipt_path)?;
    let frozen: Value = read(&launch_path)?;
    if original["schema"] != "continuum-lab-process-smoke/v1"
        || original["scope"] != "ordinary-private-flight"
        || original["status"] != "failed"
        || original["exitCode"].as_i64() != Some(0)
        || original.get("timedOut").is_some_and(|v| v != false)
        || original.get("killed").is_some_and(|v| v != false)
        || original["sourceUnchanged"] != true
        || original["flightSourcesUnchanged"] != true
        || original["livePlay"]["flightReadyObserved"] != true
        || original["livePlay"]["error"] != "invalid live session directory"
        || !original["livePlay"]["evidence"].is_null()
        || original["resolvedLaunchSha256"] != launch_sha
        || frozen["schema"] != "continuum-frozen-launch/v1"
        || frozen["selection"]["livePlay"] != true
    {
        return Err("original failure is not the isolated live directory-selection defect".into());
    }
    let object = original
        .as_object()
        .ok_or("original receipt object absent")?;
    if object
        .iter()
        .any(|(key, value)| key.ends_with("Error") && !value.is_null())
    {
        return Err("original receipt has another failed gate".into());
    }
    let run_id = original["runId"]
        .as_str()
        .filter(|id| !id.is_empty())
        .ok_or("original run absent")?;
    let pid = original["ownedPid"]
        .as_u64()
        .and_then(|id| u32::try_from(id).ok())
        .filter(|id| *id != 0)
        .ok_or("original PID absent")?;
    if !play::ready(&run, run_id, pid)? {
        return Err("original owned readiness evidence absent".into());
    }
    let proof = play::retain(&run, run_id, pid)?;
    let proof_path = run.join("live-play-proof.json");
    regular(&proof_path)?;
    let proof_sha = hash(&proof_path)?;
    if hash(&receipt_path)? != receipt_sha || hash(&launch_path)? != launch_sha {
        return Err("original frozen records changed during validation".into());
    }
    fs::create_dir(output).map_err(|e| e.to_string())?;
    write_new(&output.join("live-play-proof.json"), &proof)?;
    let result = json!({
        "schema":"continuum-live-play-offline-validation/v1",
        "status":"passed", "scope":"lifecycle-only; no-new-native-run",
        "qualification":"Historical ordinary-flight ownership and cleanup; visual fidelity, performance, mission and endurance remain open",
        "runId":run_id,"processId":pid,
        "originalReceipt":receipt_path,"originalReceiptSha256":receipt_sha,
        "originalStatus":original["status"],"originalExitCode":original["exitCode"],
        "resolvedLaunch":launch_path,"resolvedLaunchSha256":launch_sha,
        "originalFlightReadyObserved":original["livePlay"]["flightReadyObserved"],
        "historicalSourceUnchanged":original["sourceUnchanged"],
        "historicalFlightSourcesUnchanged":original["flightSourcesUnchanged"],
        "historicalOperatorWritableReadbacks":original["livePlay"]["operatorWritableReadbacks"],
        "liveProofSha256":proof_sha,"liveProof":proof,
        "validatorRecipe":"isolated-directory-selection-repair/v1; original inputs are historical records, not current mutable-profile reconstruction"
        ,"validatorSourceSha256":format!("{:x}",Sha256::digest(include_bytes!("live_revalidation.rs")))
        ,"lifecycleValidatorSourceSha256":format!("{:x}",Sha256::digest(include_bytes!("play.rs")))
        ,"terminationEvidence":"Historical exitCode zero, failed rather than interrupted status, and owned stock-stopped journal; explicit killed/timedOut flags must be false when present"
    });
    write_new(&output.join("receipt.json"), &result)?;
    Ok(result)
}
