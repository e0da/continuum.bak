use continuum_research_lab::{hash, owned_report, read, Result};
use serde_json::{json, Value};
use std::{fs, path::Path};

pub fn retain(
    directory: &Path,
    output: &Path,
    run: &str,
    pid: u32,
    recipe: &Path,
    recipe_sha: &str,
) -> Result<Value> {
    let evidence = owned_report::retain(
        directory,
        output,
        &format!("menu-presentation-{run}.json"),
        "menu-presentation.json",
        "continuum-metal-menu-presentation-observation/v1",
        run,
        pid,
    )?;
    let report = &evidence["report"];
    let png = directory.join(format!("menu-presentation-{run}.png"));
    let declared = Path::new(report["pngPath"].as_str().ok_or("menu PNG path absent")?);
    if declared.canonicalize().map_err(|e| e.to_string())?
        != png.canonicalize().map_err(|e| e.to_string())?
        || !fs::symlink_metadata(&png)
            .map_err(|e| e.to_string())?
            .is_file()
        || fs::metadata(&png).map_err(|e| e.to_string())?.len() > 16 * 1024 * 1024
    {
        return Err("menu PNG is outside the owned bounded pair".into());
    }
    let png_sha = hash(&png)?;
    let target = output.join("menu-presentation.png");
    if target.exists() {
        return Err("retained menu PNG already exists".into());
    }
    let mut retained = fs::OpenOptions::new().write(true).create_new(true).open(&target).map_err(|e| e.to_string())?;
    std::io::copy(&mut fs::File::open(&png).map_err(|e| e.to_string())?, &mut retained).map_err(|e| e.to_string())?;
    if hash(&png)? != png_sha || hash(&target)? != png_sha {
        return Err("menu PNG changed during retention".into());
    }
    let bytes = fs::read(&target).map_err(|e| e.to_string())?;
    let width = report["width"].as_u64().ok_or("menu width absent")?;
    let height = report["height"].as_u64().ok_or("menu height absent")?;
    if bytes.len() < 33
        || &bytes[..8] != b"\x89PNG\r\n\x1a\n"
        || &bytes[12..16] != b"IHDR"
        || u32::from_be_bytes(bytes[8..12].try_into().unwrap()) != 13
        || u32::from_be_bytes(bytes[16..20].try_into().unwrap()) as u64 != width
        || u32::from_be_bytes(bytes[20..24].try_into().unwrap()) as u64 != height
        || !(640..=8192).contains(&width)
        || !(360..=8192).contains(&height)
        || bytes[24] != 8
        || ![2, 6].contains(&bytes[25])
    {
        return Err("menu PNG dimensions or format invalid".into());
    }
    let manifest: Value = read(recipe)?;
    if hash(recipe)? != recipe_sha
        || report["recipeSha256"] != recipe_sha
        || Path::new(
            report["recipePath"]
                .as_str()
                .ok_or("menu recipe path absent")?,
        )
        .canonicalize()
        .map_err(|e| e.to_string())?
            != recipe
    {
        return Err("menu recipe changed or foreign report recipe".into());
    }
    validate(report, &manifest, &png_sha)?;
    Ok(
        json!({"reportSha256":evidence["sha256"],"pngSha256":png_sha,"recipeSha256":recipe_sha,"report":report,
        "qualification":"structural-capture-only; actual-text-controls-background-and-interaction-require-inspected-visual-evidence"}),
    )
}

fn validate(report: &Value, recipe: &Value, png_sha: &str) -> Result<()> {
    let names = recipe["requiredShaders"]
        .as_array()
        .ok_or("recipe shader closure absent")?;
    let rows = report["requiredShaders"]
        .as_array()
        .ok_or("observed shader closure absent")?;
    if report["status"] != "captured-structural"
        || report["actualBackend"] != "Metal"
        || report["unity"] != "2019.4.18f1"
        || !(report["error"].is_null() || report["error"] == "")
        || report["pngSha256"] != png_sha
        || report["originalRenderTargetRestored"] != true
        || report["releasedReadbackTextures"] != 1
        || report["cleanupStatus"] != "released-readback-restored-target"
        || report["unsupportedMaterials"] != 0
        || report["activeRenderers"].as_u64().unwrap_or(0) == 0
        || report["backgroundCameras"].as_u64().unwrap_or(0) == 0
        || report["menuTexts"].as_array().is_none_or(Vec::is_empty)
        || rows.len() != names.len()
        || rows.iter().zip(names).any(|(row, name)| {
            row["name"] != *name || row["present"] != true || row["supported"] != true
        })
    {
        return Err(
            "actual Metal menu structural closure or cleanup failed; raw report and PNG retained"
                .into(),
        );
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn offscreen_success_does_not_qualify_a_missing_menu_dependency() {
        let recipe = json!({"requiredShaders":["menu","world"]});
        let report = json!({"status":"captured-structural","actualBackend":"Metal","unity":"2019.4.18f1","error":null,
            "pngSha256":"digest","originalRenderTargetRestored":true,"releasedReadbackTextures":1,
            "cleanupStatus":"released-readback-restored-target","unsupportedMaterials":0,"activeRenderers":4,
            "backgroundCameras":1,"menuTexts":["Start Game"],"requiredShaders":[
            {"name":"menu","present":true,"supported":true},{"name":"world","present":true,"supported":true}]});
        assert!(validate(&report, &recipe, "digest").is_ok());
        let mut serialized = report.clone(); serialized["error"] = json!("");
        assert!(validate(&serialized, &recipe, "digest").is_ok());
        let mut missing = report.clone();
        missing["requiredShaders"][1]["supported"] = json!(false);
        assert!(validate(&missing, &recipe, "digest").is_err());
        let mut incomplete = report;
        incomplete["menuTexts"] = json!([]);
        assert!(validate(&incomplete, &recipe, "digest").is_err());
    }
}
