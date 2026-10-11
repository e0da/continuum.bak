use continuum_research_lab::{hash, read, write_new, Result};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{fs, path::Path};

fn regular_canonical(path: &Path) -> Result<std::path::PathBuf> {
    if !path.is_absolute() {
        return Err("backend proof path must be absolute".into());
    }
    for ancestor in path.ancestors() {
        if fs::symlink_metadata(ancestor)
            .map_err(|e| e.to_string())?
            .file_type()
            .is_symlink()
        {
            return Err("backend proof path rejects symbolic links".into());
        }
    }
    let canonical = path.canonicalize().map_err(|e| e.to_string())?;
    if !fs::symlink_metadata(&canonical)
        .map_err(|e| e.to_string())?
        .is_file()
    {
        return Err("backend proof requires a regular file".into());
    }
    Ok(canonical)
}

pub fn retain(
    directory: &Path,
    output: &Path,
    run: &str,
    pid: u32,
    bundle: &Path,
    bundle_sha: &str,
) -> Result<Value> {
    let path = directory.join(format!("backend-admission-pixel-{run}.json"));
    if !fs::symlink_metadata(&path)
        .map_err(|e| format!("requested backend pixel proof absent: {e}"))?
        .is_file()
        || fs::metadata(&path).map_err(|e| e.to_string())?.len() > 65536
    {
        return Err("backend pixel report is not a bounded regular file".into());
    }
    let report_sha = hash(&path)?;
    fs::copy(&path, output.join("backend-pixel-proof.json")).map_err(|e| e.to_string())?;
    if hash(&path)? != report_sha || hash(&output.join("backend-pixel-proof.json"))? != report_sha {
        return Err("backend pixel report changed while retaining".into());
    }
    let report: Value = read(&output.join("backend-pixel-proof.json"))?;
    let png = path.with_extension("png");
    let declared_png = Path::new(
        report["pngPath"]
            .as_str()
            .ok_or("backend pixel PNG path absent")?,
    );
    if regular_canonical(declared_png)? != regular_canonical(&png)?
        || !fs::symlink_metadata(&png)
            .map_err(|e| e.to_string())?
            .is_file()
        || fs::metadata(&png).map_err(|e| e.to_string())?.len() > 262144
    {
        return Err("backend pixel PNG absent or outside owned report pair".into());
    }
    let png_sha = hash(&png)?;
    fs::copy(&png, output.join("backend-pixel-proof.png")).map_err(|e| e.to_string())?;
    if hash(&png)? != png_sha || hash(&output.join("backend-pixel-proof.png"))? != png_sha {
        return Err("backend pixel PNG changed while retaining".into());
    }
    validate(&report, run, pid, bundle, bundle_sha, &png_sha)?;
    let bytes = fs::read(output.join("backend-pixel-proof.png")).map_err(|e| e.to_string())?;
    if bytes.len() < 33
        || &bytes[..8] != b"\x89PNG\r\n\x1a\n"
        || &bytes[12..16] != b"IHDR"
        || u32::from_be_bytes(bytes[8..12].try_into().unwrap()) != 13
        || u32::from_be_bytes(bytes[16..20].try_into().unwrap()) != 64
        || u32::from_be_bytes(bytes[20..24].try_into().unwrap()) != 64
        || bytes[24] != 8
        || bytes[25] != 6
    {
        return Err("backend pixel PNG dimensions/format invalid".into());
    }
    Ok(
        json!({"reportSha256":report_sha,"pngSha256":png_sha,"bundleSha256":bundle_sha,"report":report,
        "scope":"Native offscreen analytic shader and full readback pixel proof; PNG header and hashes checked by runner, not an independent pixel decoder or gameplay/rendering qualification."}),
    )
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct OriginalRun {
    schema: String,
    status: String,
    run_id: String,
    owned_pid: u32,
    graphics_api_requested: String,
    exit_code: i32,
    source_unchanged: bool,
    flight_sources_unchanged: bool,
    assembly_sha256: String,
    player_sha256: String,
    resolved_launch_sha256: String,
    selection_sha256: String,
    asset_generation_sha256: String,
    backend_pixel_proof_bundle: std::path::PathBuf,
    backend_pixel_proof_bundle_sha256: String,
    backend_pixel_proof_bundle_after_sha256: String,
    backend_pixel_proof_error: String,
    asset_generation_error: Option<String>,
    flight_capture_error: Option<String>,
    startup_deferred_terrain_error: Option<String>,
    startup_site_proof_error: Option<String>,
}

pub fn verify_offline(receipt: &Path, output: &Path) -> Result<()> {
    let receipt = regular_canonical(receipt)?;
    if !output.is_absolute() || output.exists() || fs::symlink_metadata(output).is_ok() {
        return Err("offline backend verification requires a new absolute output directory".into());
    }
    let parent = output.parent().ok_or("offline output parent absent")?;
    for ancestor in parent.ancestors() {
        if fs::symlink_metadata(ancestor)
            .map_err(|e| e.to_string())?
            .file_type()
            .is_symlink()
        {
            return Err("offline output rejects symbolic links".into());
        }
    }
    if fs::metadata(&receipt).map_err(|e| e.to_string())?.len() > 2 * 1024 * 1024 {
        return Err("original native receipt exceeds bounded metadata scope".into());
    }
    let receipt_sha = hash(&receipt)?;
    let original: OriginalRun = read(&receipt)?;
    if original.schema != "continuum-lab-process-smoke/v1"
        || original.status != "failed"
        || original.exit_code != 0
        || !original.source_unchanged
        || !original.flight_sources_unchanged
        || original.owned_pid == 0
        || original.run_id.is_empty()
        || original.run_id.len() > 96
        || !original
            .run_id
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'-' || c == b'_')
        || original.graphics_api_requested != "metal"
        || original.asset_generation_error.is_some()
        || original.flight_capture_error.is_some()
        || original.startup_deferred_terrain_error.is_some()
        || original.startup_site_proof_error.is_some()
        || original.backend_pixel_proof_error
            != "backend pixel PNG absent or outside owned report pair"
        || original.backend_pixel_proof_bundle_sha256
            != original.backend_pixel_proof_bundle_after_sha256
    {
        return Err(
            "original native run is not the declared lexical-path-only proof rejection".into(),
        );
    }
    let directory = receipt.parent().ok_or("original receipt parent absent")?;
    let frozen_path = regular_canonical(&directory.join("resolved-launch.json"))?;
    if fs::metadata(&frozen_path).map_err(|e| e.to_string())?.len() > 2 * 1024 * 1024 {
        return Err("frozen launch exceeds metadata bound".into());
    }
    if hash(&frozen_path)? != original.resolved_launch_sha256 {
        return Err("original frozen launch digest differs".into());
    }
    let frozen: continuum_research_lab::profile::FrozenLaunch = read(&frozen_path)?;
    frozen.selection.validate()?;
    let bundle = regular_canonical(&original.backend_pixel_proof_bundle)?;
    let input_hash = |path: &Path| -> Result<String> {
        frozen
            .inputs
            .get(path)
            .cloned()
            .ok_or("original frozen input identity absent".into())
    };
    let helper = frozen
        .instance_root
        .join("GameData/KspContinuum/Plugins/KspContinuum.dll");
    let helper_sha = input_hash(&helper)?;
    let backend_settings = frozen
        .instance_root
        .join("KSP.app/Contents/Resources/Data/globalgamemanagers");
    let backend_settings_sha = input_hash(&backend_settings)?;
    if frozen.schema != "continuum-frozen-launch/v1"
        || !frozen.instance_root.is_absolute()
        || frozen.selection.identity_sha256()? != original.selection_sha256
        || frozen.asset_generation.identity()? != original.asset_generation_sha256
        || frozen.selection.graphics_api.as_str() != "metal"
        || frozen.selection.flight.is_some()
        || frozen
            .selection
            .diagnostics
            .backend_pixel_proof_bundle
            .as_deref()
            != Some(bundle.as_path())
        || input_hash(&bundle)? != original.backend_pixel_proof_bundle_sha256
        || hash(&bundle)? != original.backend_pixel_proof_bundle_sha256
        || input_hash(&frozen.instance_root.join("KSP.app/Contents/MacOS/KSP"))?
            != original.player_sha256
        || input_hash(
            &frozen
                .instance_root
                .join("KSP.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll"),
        )? != original.assembly_sha256
    {
        return Err("original launch/helper/backend/bundle provenance invalid".into());
    }
    let observation_path = regular_canonical(&directory.join("runtime-observation.json"))?;
    if fs::metadata(&observation_path)
        .map_err(|e| e.to_string())?
        .len()
        > 65536
    {
        return Err("runtime observation exceeds bound".into());
    }
    let observation_sha = hash(&observation_path)?;
    let observation: Value = read(&observation_path)?;
    if observation["schema"] != "continuum-managed-runtime-observation/v1"
        || observation["runId"] != original.run_id
        || observation["graphicsDeviceType"] != "Metal"
        || observation["observerAssemblySha256"] != helper_sha
        || observation["assemblySha256"] != original.assembly_sha256
    {
        return Err("original runtime observer identity invalid".into());
    }
    let retained_path = regular_canonical(&directory.join("backend-pixel-proof.json"))?;
    if fs::metadata(&retained_path)
        .map_err(|e| e.to_string())?
        .len()
        > 65536
    {
        return Err("original retained pixel report exceeds bound".into());
    }
    let retained_sha = hash(&retained_path)?;
    let owned = frozen
        .instance_root
        .join("GameData/KspContinuum/PluginData");
    let source_report = regular_canonical(
        &owned.join(format!("backend-admission-pixel-{}.json", original.run_id)),
    )?;
    if hash(&source_report)? != retained_sha {
        return Err("owned native report differs from originally retained report".into());
    }
    fs::create_dir(output).map_err(|e| e.to_string())?;
    let evidence = retain(
        &owned,
        output,
        &original.run_id,
        original.owned_pid,
        &bundle,
        &original.backend_pixel_proof_bundle_sha256,
    )?;
    for (source, destination, expected) in [
        (&receipt, "original-native-receipt.json", &receipt_sha),
        (
            &frozen_path,
            "original-resolved-launch.json",
            &original.resolved_launch_sha256,
        ),
        (
            &observation_path,
            "original-runtime-observation.json",
            &observation_sha,
        ),
    ] {
        fs::copy(source, output.join(destination)).map_err(|e| e.to_string())?;
        if hash(source)? != *expected || hash(&output.join(destination))? != *expected {
            return Err("original provenance changed during offline retention".into());
        }
    }
    if hash(&retained_path)? != retained_sha
        || hash(&output.join("backend-pixel-proof.json"))? != retained_sha
        || hash(&bundle)? != original.backend_pixel_proof_bundle_sha256
    {
        return Err("offline proof inputs changed".into());
    }
    write_new(
        &output.join("receipt.json"),
        &json!({
            "schema":"continuum-offline-backend-proof/v1", "status":"passed", "scope":"Offline revalidation of preserved native analytic pixel proof; no new native run; original failed native receipt remains unchanged. Current installed helper/backend files are not requalified.",
            "originalReceipt":receipt,"originalReceiptSha256":receipt_sha,"originalNativeStatus":original.status,
            "runId":original.run_id,"ownedPid":original.owned_pid,"instanceRoot":frozen.instance_root,
            "originalResolvedLaunchSha256":original.resolved_launch_sha256,"originalHelperSha256":helper_sha,
            "originalBackendSettingsSha256":backend_settings_sha,"runtimeObservationSha256":observation_sha,
            "originalRetainedReportSha256":retained_sha,"backendPixelProofEvidence":evidence
        }),
    )
}

fn validate(
    report: &Value,
    run: &str,
    pid: u32,
    bundle: &Path,
    bundle_sha: &str,
    png_sha: &str,
) -> Result<()> {
    if report["schema"] != "continuum-backend-admission-pixel/v1"
        || report["runId"] != run
        || report["processId"].as_u64() != Some(pid as u64)
        || report["status"] != "complete"
        || report["actualBackend"] != "Metal"
        || report["colorSpace"] != "Gamma"
        || report["shaderName"] != "Hidden/Continuum/BackendAdmissionProof"
        || report["shaderSupported"] != true
        || report["bundlePath"].as_str() != bundle.to_str()
        || report["bundleSha256"] != bundle_sha
        || report["pngSha256"] != png_sha
        || report["checkedPixels"].as_u64() != Some(4096)
        || report["mismatchedPixels"].as_u64() != Some(0)
        || report["width"].as_u64() != Some(64)
        || report["height"].as_u64() != Some(64)
        || report["cleanupStatus"] != "released-owned-resources-restored-target"
        || report["originalRenderTargetRestored"] != true
        || report["unity"] != "2019.4.18f1"
        || !(report["error"].is_null() || report["error"] == "")
    {
        return Err("backend pixel identity, execution or cleanup proof invalid".into());
    }
    for key in [
        "releasedMaterials",
        "releasedTextures",
        "releasedTargets",
        "releasedBundles",
    ] {
        if report[key].as_u64() != Some(1) {
            return Err(format!("backend pixel {key} ownership incomplete"));
        }
    }
    let tolerance = (1.0f32 / 255.0f32) as f64;
    // JsonUtility writes float fields with decimal rounding; this allowance is less than one millionth of a channel step.
    const FLOAT_JSON_ROUNDING: f64 = 1e-9;
    if !report["tolerance"]
        .as_f64()
        .is_some_and(|v| v.is_finite() && (v - tolerance).abs() <= FLOAT_JSON_ROUNDING)
    {
        return Err("backend pixel tolerance drift".into());
    }
    let expected = [0.125, 0.5, 0.875, 1.0];
    let values = |key: &str, count: usize| -> Result<Vec<f64>> {
        let rows = report[key]
            .as_array()
            .ok_or(format!("backend pixel {key} missing"))?;
        if rows.len() != count {
            return Err(format!("backend pixel {key} shape invalid"));
        }
        rows.iter()
            .map(|v| {
                v.as_f64()
                    .filter(|v| v.is_finite())
                    .ok_or(format!("backend pixel {key} nonfinite"))
            })
            .collect()
    };
    if values("expected", 4)? != expected {
        return Err("backend pixel expected recipe drift".into());
    }
    if values("maximumChannelErrors", 4)?
        .iter()
        .any(|v| *v < 0.0 || *v > tolerance + FLOAT_JSON_ROUNDING)
        || values("centerPixels", 16)?
            .iter()
            .enumerate()
            .any(|(i, v)| (*v - expected[i % 4]).abs() > tolerance + FLOAT_JSON_ROUNDING)
    {
        return Err("backend pixel actual readback mismatch".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn exit_success_cannot_substitute_for_actual_pixels_or_owned_cleanup() {
        let mut report = json!({"schema":"continuum-backend-admission-pixel/v1","runId":"r","processId":42,"status":"complete","actualBackend":"Metal","colorSpace":"Gamma","unity":"2019.4.18f1","shaderName":"Hidden/Continuum/BackendAdmissionProof","shaderSupported":true,"bundlePath":"/bundle","bundleSha256":"b","pngSha256":"p","width":64,"height":64,"checkedPixels":4096,"mismatchedPixels":0,"cleanupStatus":"released-owned-resources-restored-target","originalRenderTargetRestored":true,"releasedMaterials":1,"releasedTextures":1,"releasedTargets":1,"releasedBundles":1,"tolerance":0.003921569,"expected":[0.125,0.5,0.875,1.0],"maximumChannelErrors":[0.0,0.0,0.0,0.0],"centerPixels":[0.125,0.5,0.875,1.0,0.125,0.5,0.875,1.0,0.125,0.5,0.875,1.0,0.125,0.5,0.875,1.0],"error":null});
        assert!(validate(&report, "r", 42, Path::new("/bundle"), "b", "p").is_ok());
        report["maximumChannelErrors"][0] = json!(0.1);
        assert!(validate(&report, "r", 42, Path::new("/bundle"), "b", "p").is_err());
        report["maximumChannelErrors"][0] = json!(0.0);
        report.as_object_mut().unwrap().remove("releasedTargets");
        assert!(validate(&report, "r", 42, Path::new("/bundle"), "b", "p").is_err());
    }
}
