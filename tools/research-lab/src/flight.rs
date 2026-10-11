use crate::{hash, read, write_new, Result};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

fn is_false(value: &bool) -> bool { !*value }

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Request {
    #[serde(default)]
    pub research_panel: ResearchPanelMode,
    #[serde(default)]
    pub local_camera: LocalCameraMode,
    pub save: String,
    pub checkpoint: String,
    pub maximum_delta: f64,
    pub seconds: f64,
    pub settle_seconds: f64,
    pub warp: u32,
    pub atmosphere: bool,
    pub resolution: Option<String>,
    pub no_shadows: bool,
    pub no_aa: bool,
    pub native_sample: bool,
    pub gpu_timing_library: Option<PathBuf>,
    pub perturbation: String,
    pub pulse_delta_v: f64,
    pub callback_attribution: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub mechjeb_provider_plan: bool,
    #[serde(default)]
    pub camera_chain: bool,
    #[serde(default)]
    pub world_draw: Option<String>,
    pub hide_vessel: bool,
    pub no_aero_fx: bool,
    pub aero_quality: Option<u32>,
    pub aero_batch: bool,
    #[serde(default)]
    pub aero_field: bool,
    #[serde(default)]
    pub aero_field_bundle: Option<PathBuf>,
    pub part_batch: bool,
    #[serde(default)]
    pub part_mapped_only: bool,
    #[serde(default)]
    pub part_reference: bool,
    #[serde(default)]
    pub part_retained: bool,
    pub part_shader_bundle: Option<PathBuf>,
    pub part_standard_fallback: bool,
    pub screenshot: bool,
    pub entry_target_altitude: Option<f64>,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ResearchPanelMode { #[default] Visible, Hidden }
impl ResearchPanelMode {
    pub fn as_str(self) -> &'static str { match self { Self::Visible => "visible", Self::Hidden => "hidden" } }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum LocalCameraMode { #[default] Stock, Merged }
impl LocalCameraMode {
    pub fn as_str(self) -> &'static str { match self { Self::Stock => "stock", Self::Merged => "merged" } }
}
impl Request {
    pub fn local_camera_applicable(&self) -> bool {
        !self.atmosphere && self.entry_target_altitude.is_none() && self.warp == 1 && self.perturbation == "quiet"
            && !self.part_retained && !self.camera_chain && self.gpu_timing_library.is_none()
    }
    pub fn parse(options: &BTreeMap<String, String>) -> Result<Option<Self>> {
        if !options.keys().any(|key| key.starts_with("--flight-")) {
            return Ok(None);
        }
        let research_panel = match options.get("--flight-research-panel").map(String::as_str).unwrap_or("visible") {
            "visible" => ResearchPanelMode::Visible,
            "hidden" => ResearchPanelMode::Hidden,
            _ => return Err("research panel mode must be visible or hidden".into()),
        };
        let local_camera = match options.get("--flight-local-camera").map(String::as_str).unwrap_or("stock") {
            "stock" => LocalCameraMode::Stock,
            "merged" => LocalCameraMode::Merged,
            _ => return Err("local camera mode must be stock or merged".into()),
        };
        let name = |key: &str| -> Result<String> {
            let value = options.get(key).ok_or(format!("missing {key}"))?;
            if value.is_empty()
                || value.len() > 128
                || value == "."
                || value == ".."
                || !value
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || b"._- ".contains(&byte))
            {
                return Err(format!("{key} must be a single local name"));
            }
            Ok(value.clone())
        };
        let number = |key: &str, default: f64, low: f64, high: f64| -> Result<f64> {
            let value = options
                .get(key)
                .map(|value| value.parse::<f64>())
                .transpose()
                .map_err(|error| error.to_string())?
                .unwrap_or(default);
            if !value.is_finite() || value < low || value > high {
                return Err(format!("{key} outside {low}..{high}"));
            }
            Ok(value)
        };
        let boolean = |key: &str| -> Result<bool> {
            match options.get(key).map(String::as_str) {
                None | Some("false") => Ok(false),
                Some("true") => Ok(true),
                _ => Err(format!("{key} must be true or false")),
            }
        };
        let warp = match options.get("--flight-warp").map(String::as_str) {
            None | Some("1") | Some("normal") => 1,
            Some("4") | Some("4x") => 4,
            _ => return Err("flight-warp supports normal/1 or LOW physics 4/4x".into()),
        };
        let resolution = options.get("--flight-resolution").cloned();
        if let Some(value) = &resolution {
            let (width, height) = value
                .split_once('x')
                .ok_or("flight-resolution requires WIDTHxHEIGHT")?;
            for (dimension, minimum) in [(width, 320), (height, 240)] {
                let pixels = dimension
                    .parse::<u32>()
                    .map_err(|error| error.to_string())?;
                if !(minimum..=8192).contains(&pixels) {
                    return Err(
                        "flight-resolution requires width >=320, height >=240, maximum 8192".into(),
                    );
                }
            }
        }
        let entry_target_altitude = if options.contains_key("--flight-entry-target-altitude") {
            Some(number(
                "--flight-entry-target-altitude",
                45000.0,
                1000.0,
                60000.0,
            )?)
        } else {
            None
        };
        if warp == 4 && (boolean("--flight-atmosphere")? || entry_target_altitude.is_some()) {
            return Err("timed 4x atmosphere capture is not supported".into());
        }
        let perturbation = options
            .get("--flight-perturbation")
            .map(String::as_str)
            .unwrap_or("quiet");
        if !matches!(perturbation, "quiet" | "off-center-pulse") {
            return Err("flight-perturbation supports quiet or off-center-pulse".into());
        }
        let world_draw = options.get("--flight-world-draw").cloned();
        if world_draw.as_deref().is_some_and(|mode| !matches!(mode, "stock" | "suppressed")) {
            return Err("flight-world-draw supports stock or suppressed".into());
        }
        let aero_quality = options
            .get("--flight-aero-quality")
            .map(|value| value.parse::<u32>().map_err(|error| error.to_string()))
            .transpose()?;
        if aero_quality.map(|quality| quality > 3).unwrap_or(false) {
            return Err("flight-aero-quality must be integer0..3".into());
        }
        if aero_quality.is_some() && boolean("--flight-no-aero-fx")? {
            return Err("flight-aero-quality and flight-no-aero-fx are separate treatments".into());
        }
        if boolean("--flight-aero-batch")?
            && (boolean("--flight-no-aero-fx")?
                || boolean("--flight-hide-vessel")?
                || aero_quality == Some(0))
        {
            return Err(
                "aero batch requires active aero effects and visible vessel renderers".into(),
            );
        }
        let part_batch = boolean("--flight-part-batch")?;
        let aero_field = boolean("--flight-aero-field")?;
        let aero_field_bundle = options.get("--flight-aero-field-bundle").map(PathBuf::from);
        if aero_field != aero_field_bundle.is_some() {
            return Err("aero field requires exactly one declared shader bundle".into());
        }
        if aero_field && (boolean("--flight-aero-batch")? || boolean("--flight-no-aero-fx")?
            || boolean("--flight-hide-vessel")? || aero_quality.is_some() || boolean("--flight-part-retained")?) {
            return Err("aero field owns an exclusive visible effects pipeline with stock visual-state production".into());
        }
        if let Some(bundle) = &aero_field_bundle {
            if !bundle.is_absolute() || !bundle.is_file()
                || bundle.canonicalize().map_err(|error| error.to_string())? != *bundle {
                return Err("aero field bundle requires a canonical absolute file".into());
            }
        }
        let part_retained = boolean("--flight-part-retained")?;
        let part_mapped_only = boolean("--flight-part-mapped-only")?;
        let part_reference = boolean("--flight-part-reference")?;
        if (part_mapped_only || part_reference) && !part_batch {
            return Err("part family/reference selections require part batch".into());
        }
        if part_reference && part_retained {
            return Err("reference part renderer excludes retained ownership".into());
        }
        if part_retained && (!part_batch || !boolean("--flight-aero-batch")?) {
            return Err("retained part renderer requires both owned part and aero providers".into());
        }
        let part_standard_fallback = boolean("--flight-part-standard-fallback")?;
        let part_shader_bundle = options.get("--flight-part-shader-bundle").map(PathBuf::from);
        let gpu_timing_library = options.get("--flight-gpu-timing-library").map(PathBuf::from);
        if let Some(library) = &gpu_timing_library {
            if !library.is_absolute() || !library.is_file()
                || library.canonicalize().map_err(|error| error.to_string())? != *library
                || library.extension().and_then(|value| value.to_str()) != Some("dylib") {
                return Err("GPU timing requires a canonical absolute dylib".into());
            }
        }
        if part_batch && boolean("--flight-hide-vessel")? {
            return Err("part batch requires visible vessel renderers".into());
        }
        if part_standard_fallback && !part_batch {
            return Err("part standard fallback requires part batch".into());
        }
        if local_camera == LocalCameraMode::Merged && (boolean("--flight-atmosphere")? || entry_target_altitude.is_some()
            || warp != 1 || perturbation != "quiet" || part_retained || boolean("--flight-camera-chain")? || gpu_timing_library.is_some()) {
            return Err("merged local camera requires quiet vacuum normal-time flight without entry, retained rendering, camera-chain or GPU observers".into());
        }
        if let Some(bundle) = &part_shader_bundle {
            if !part_batch || !bundle.is_absolute() || !bundle.is_file()
                || bundle.canonicalize().map_err(|error| error.to_string())? != *bundle {
                return Err("part shader bundle requires part batch and a canonical absolute file".into());
            }
        }
        Ok(Some(Self {
            research_panel,
            local_camera,
            save: name("--flight-save")?,
            checkpoint: name("--flight-checkpoint")?,
            maximum_delta: number("--flight-max-delta", 0.04, 0.001, 1.0)?,
            seconds: number("--flight-seconds", 10.0, 0.1, 60.0)?,
            settle_seconds: number("--flight-settle-seconds", 2.0, 0.0, 60.0)?,
            warp,
            atmosphere: boolean("--flight-atmosphere")? || entry_target_altitude.is_some(),
            entry_target_altitude,
            resolution,
            no_shadows: boolean("--flight-no-shadows")?,
            no_aa: boolean("--flight-no-aa")?,
            native_sample: boolean("--flight-native-sample")?,
            gpu_timing_library,
            perturbation: perturbation.to_string(),
            pulse_delta_v: number("--flight-pulse-delta-v", 0.25, 0.001, 100.0)?,
            callback_attribution: boolean("--flight-callback-attribution")?,
            mechjeb_provider_plan: boolean("--flight-mechjeb-provider-plan")?,
            camera_chain: boolean("--flight-camera-chain")?,
            world_draw,
            hide_vessel: boolean("--flight-hide-vessel")?,
            no_aero_fx: boolean("--flight-no-aero-fx")?,
            aero_quality,
            aero_batch: boolean("--flight-aero-batch")?,
            aero_field,
            aero_field_bundle,
            part_batch,
            part_mapped_only,
            part_reference,
            part_retained,
            part_shader_bundle,
            part_standard_fallback,
            screenshot: boolean("--flight-screenshot")?,
        }))
    }
    pub fn arguments(&self) -> Vec<String> {
        let mut args = vec![
            format!("--continuum-research-panel={}", self.research_panel.as_str()),
            format!("--continuum-flight-local-camera={}", self.local_camera.as_str()),
            "--continuum-scale-profile".into(),
            "--continuum-playerloop".into(),
            "--continuum-flight-diagnostic".into(),
            "--continuum-flight-perturbation".into(),
            self.perturbation.clone(),
            format!("--continuum-flight-pulse-delta-v={}", self.pulse_delta_v),
            "--continuum-scale-save".into(),
            self.save.clone(),
            "--continuum-scale-checkpoint".into(),
            self.checkpoint.clone(),
            format!("--continuum-flight-seconds={}", self.seconds),
            "--continuum-flight-timeout=60".into(),
            format!("--continuum-flight-settle={}", self.settle_seconds),
            format!("--continuum-flight-catchup={}", self.maximum_delta),
        ];
        if let Some(mode) = &self.world_draw {
            args.push(format!("--continuum-flight-world-draw={mode}"));
        }
        if self.camera_chain {
            args.push("--continuum-flight-camera-chain".into());
        }
        if let Some(target) = self.entry_target_altitude {
            args.push(format!("--continuum-flight-entry-target={target}"));
        }
        if self.warp == 4 {
            args.push("--continuum-physics-warp-pressure".into());
        }
        if self.atmosphere {
            args.push("--continuum-normal-atmosphere".into());
        }
        if let Some(value) = &self.resolution {
            args.push(format!("--continuum-flight-resolution={value}"));
        }
        if self.no_shadows {
            args.push("--continuum-flight-no-shadows".into());
        }
        if self.no_aa {
            args.push("--continuum-flight-no-aa".into());
        }
        if self.callback_attribution {
            args.push("--continuum-callback-attribution".into());
        }
        if self.mechjeb_provider_plan {
            args.push("--continuum-mechjeb-provider-plan".into());
        }
        if self.hide_vessel {
            args.push("--continuum-flight-hide-vessel".into());
        }
        if self.no_aero_fx {
            args.push("--continuum-flight-no-aero-fx".into());
        }
        if let Some(quality) = self.aero_quality {
            args.push(format!("--continuum-flight-aero-quality={quality}"));
        }
        if self.aero_batch {
            args.push("--continuum-flight-aero-batch".into());
        }
        if self.aero_field {
            args.push("--continuum-flight-aero-field".into());
        }
        if let Some(bundle) = &self.aero_field_bundle {
            args.push(format!("--continuum-flight-aero-field-bundle={}", bundle.display()));
        }
        if self.part_batch {
            args.push("--continuum-flight-part-batch".into());
        }
        if self.part_mapped_only { args.push("--continuum-flight-part-mapped-only".into()); }
        if self.part_reference { args.push("--continuum-flight-part-reference".into()); }
        if self.part_retained {
            args.push("--continuum-flight-part-retained".into());
        }
        if let Some(bundle) = &self.part_shader_bundle {
            args.push(format!("--continuum-flight-part-shader-bundle={}", bundle.display()));
        }
        if self.part_standard_fallback {
            args.push("--continuum-flight-part-standard-fallback".into());
        }
        if self.screenshot {
            args.push("--continuum-flight-screenshot".into());
        }
        if let Some(library) = &self.gpu_timing_library {
            args.push(format!("--continuum-flight-gpu-timing-library={}", library.display()));
        }
        args
    }
    pub fn snapshot(&self, instance: &Path) -> Result<Value> {
        let checkpoint = instance
            .join("saves")
            .join(&self.save)
            .join(format!("{}.sfs", self.checkpoint));
        if checkpoint
            .canonicalize()
            .map_err(|error| error.to_string())?
            != checkpoint
        {
            return Err("checkpoint must be a canonical owned file".into());
        }
        let mut files = BTreeMap::new();
        for path in [checkpoint, instance.join("settings.cfg")] {
            files.insert(path.to_string_lossy().into_owned(), hash(&path)?);
        }
        fn plugins(directory: &Path, files: &mut BTreeMap<String, String>) -> Result<()> {
            for entry in fs::read_dir(directory).map_err(|error| error.to_string())? {
                let entry = entry.map_err(|error| error.to_string())?;
                let kind = entry.file_type().map_err(|error| error.to_string())?;
                if kind.is_symlink() {
                    return Err("flight plugin snapshot rejects symbolic links".into());
                }
                if kind.is_dir() && entry.file_name() != "PluginData" {
                    plugins(&entry.path(), files)?;
                }
                if kind.is_file()
                    && entry
                        .path()
                        .extension()
                        .and_then(|value| value.to_str())
                        .map(|value| value.eq_ignore_ascii_case("dll"))
                        .unwrap_or(false)
                {
                    files.insert(
                        entry.path().to_string_lossy().into_owned(),
                        hash(&entry.path())?,
                    );
                }
                if files.len() > 10000 {
                    return Err("flight source snapshot file bound exceeded".into());
                }
            }
            Ok(())
        }
        plugins(&instance.join("GameData"), &mut files)?;
        if let Some(bundle) = &self.part_shader_bundle {
            files.insert(bundle.to_string_lossy().into_owned(), hash(bundle)?);
        }
        if let Some(bundle) = &self.aero_field_bundle {
            files.insert(bundle.to_string_lossy().into_owned(), hash(bundle)?);
        }
        if let Some(library) = &self.gpu_timing_library {
            files.insert(library.to_string_lossy().into_owned(), hash(library)?);
        }
        Ok(
            json!({"schema":"continuum-flight-inputs/v1","request":self,"files":files,
            "scope":"selected-checkpoint-settings-GameData-DLLs-requested-shader-bundles-and-GPU-diagnostic-library; native-assets-and-stock-bundles-guarded-by-process-receipt"}),
        )
    }
}
pub fn unchanged(inputs: &Value) -> bool {
    inputs["files"]
        .as_object()
        .map(|files| {
            files.iter().all(|(path, expected)| {
                hash(Path::new(path))
                    .map(|actual| expected.as_str() == Some(actual.as_str()))
                    .unwrap_or(false)
            })
        })
        .unwrap_or(false)
}
pub fn capture_directory(directory: &Path, run_id: &str, pid: u32) -> Result<Option<PathBuf>> {
    owned_sidecar_directory(
        directory,
        "scale-profile-",
        "capture-start.json",
        run_id,
        pid,
    )
}
fn owned_sidecar_directory(
    directory: &Path,
    family: &str,
    sidecar: &str,
    run_id: &str,
    pid: u32,
) -> Result<Option<PathBuf>> {
    let mut found = None;
    for entry in fs::read_dir(directory).map_err(|error| error.to_string())? {
        let entry = entry.map_err(|error| error.to_string())?;
        if !entry.file_name().to_string_lossy().starts_with(family)
            || !entry
                .file_type()
                .map_err(|error| error.to_string())?
                .is_dir()
        {
            continue;
        }
        let start = entry.path().join(sidecar);
        if !start.exists() {
            continue;
        }
        let value: Value = match read(&start) {
            Ok(value) => value,
            Err(_) => continue,
        };
        if value["runId"] == run_id && value["processId"].as_u64() == Some(pid as u64) {
            if found.is_some() {
                return Err("duplicate owned flight evidence directory".into());
            }
            found = Some(entry.path());
        }
    }
    Ok(found)
}
fn finite(value: &Value, key: &str) -> Result<f64> {
    let number = value[key]
        .as_f64()
        .ok_or(format!("missing numeric {key}"))?;
    if !number.is_finite() {
        return Err(format!("nonfinite {key}"));
    }
    Ok(number)
}
fn distribution(mut values: Vec<f64>) -> Result<Value> {
    if values.is_empty()
        || values
            .iter()
            .any(|value| !value.is_finite() || *value < 0.0)
    {
        return Err("invalid or empty distribution".into());
    }
    values.sort_by(f64::total_cmp);
    let percentile = |fraction: f64| {
        let at = (values.len() - 1) as f64 * fraction;
        let low = at.floor() as usize;
        let high = at.ceil() as usize;
        values[low] + (values[high] - values[low]) * (at - low as f64)
    };
    Ok(
        json!({"count":values.len(),"mean":values.iter().sum::<f64>()/values.len() as f64,"p50":percentile(0.5),"p95":percentile(0.95),"p99":percentile(0.99),"minimum":values[0],"maximum":values[values.len()-1]}),
    )
}
fn research_panel_summary(request: &Request, report: &Value, frames: &[Value]) -> Result<Value> {
    let panel = &report["researchPanel"];
    let visible = request.research_panel == ResearchPanelMode::Visible;
    if panel["schema"] != "continuum-research-panel/v1" || panel["status"] != "complete"
        || panel["mode"] != request.research_panel.as_str()
        || panel["startVisible"].as_bool() != Some(visible) || panel["endVisible"].as_bool() != Some(visible)
        || panel["startComponentPresent"].as_bool() != Some(visible) || panel["endComponentPresent"].as_bool() != Some(visible)
        || panel["modeChanges"].as_u64() != Some(0) || panel["errors"].as_u64() != Some(0)
        || panel["serviceUpdateCalls"].as_u64().unwrap_or(0) == 0 || panel["serviceFixedCalls"].as_u64().unwrap_or(0) == 0
        || panel["captureStartClockTimestamp"] != report["captureStartClockTimestamp"]
        || panel["captureEndClockTimestamp"] != report["captureEndClockTimestamp"]
        || panel["clockFrequency"] != report["clockFrequency"] {
        return Err("research panel mode/window/effective state absent or invalid".into());
    }
    let first = frames.first().ok_or("research panel frame window empty")?["contextFrame"].as_u64().ok_or("research panel first frame absent")?;
    let after_last = frames.last().unwrap()["contextFrame"].as_u64().and_then(|id| id.checked_add(1)).ok_or("research panel final frame invalid")?;
    if panel["captureStartFrame"].as_u64() != Some(first) || panel["captureEndFrame"].as_u64() != Some(after_last) {
        return Err("research panel frame bounds differ from capture".into());
    }
    let count = |key: &str| -> Result<u64> { panel[key].as_u64().ok_or(format!("research panel counter absent/invalid: {key}")) };
    let gui_calls = count("guiCalls")?; let gui_ticks = count("guiTicks")?;
    let mut calls = 0u64; let mut ticks = 0u64;
    for (call_key, tick_key) in [("layoutCalls","layoutTicks"),("repaintCalls","repaintTicks"),("otherCalls","otherTicks")] {
        calls = calls.checked_add(count(call_key)?).ok_or("research panel call sum overflow")?;
        ticks = ticks.checked_add(count(tick_key)?).ok_or("research panel tick sum overflow")?;
    }
    if calls != gui_calls || ticks != gui_ticks || (!visible && (gui_calls != 0 || gui_ticks != 0))
        || (visible && (gui_calls == 0 || count("layoutCalls")? == 0 || count("repaintCalls")? == 0)) {
        return Err("research panel GUI witness/count conservation differs".into());
    }
    let frequency = panel["clockFrequency"].as_u64().filter(|value| *value > 0).ok_or("research panel frequency invalid")? as f64;
    let start = panel["captureStartClockTimestamp"].as_u64().ok_or("research panel start timestamp invalid")?;
    let end = panel["captureEndClockTimestamp"].as_u64().filter(|value| *value > start).ok_or("research panel end timestamp invalid")?;
    if gui_ticks > end-start { return Err("research panel enclosed costs exceed window".into()); }
    let to_ms = |ticks: u64| ticks as f64 * 1000.0 / frequency;
    Ok(json!({"report":panel,"completedFrameDenominator":frames.len(),"guiMilliseconds":to_ms(gui_ticks),
        "guiMillisecondsPerCompletedFrame":to_ms(gui_ticks)/frames.len() as f64,
        "layoutMilliseconds":to_ms(count("layoutTicks")?),"repaintMilliseconds":to_ms(count("repaintTicks")?),"otherMilliseconds":to_ms(count("otherTicks")?),
        "eventMillisecondsPerCompletedFrame":{"layout":to_ms(count("layoutTicks")?)/frames.len() as f64,"repaint":to_ms(count("repaintTicks")?)/frames.len() as f64,"other":to_ms(count("otherTicks")?)/frames.len() as f64},
        "scope":"Enclosed owned research-panel OnGUI wall scopes; event classes partition GUI total. Already included in full frames/PlayerLoop; do not add again. Not GPU duration."}))
}
pub fn analyze(
    request: &Request,
    run_id: &str,
    pid: u32,
    report: &Value,
    start: &Value,
    end: &Value,
) -> Result<Value> {
    if report["schema"] != "ksp-continuum-markers/v2"
        || report["status"] != "complete"
        || report["captureMode"] != "simulation-duration"
        || report["completionReason"] != "simulation-duration-reached"
        || report["runId"] != run_id
        || start["runId"] != run_id
        || end["runId"] != run_id
        || start["schema"] != "continuum-flight-capture-signal/v1"
        || end["schema"] != "continuum-flight-capture-signal/v1"
        || start["status"] != "capturing"
        || end["status"] != "complete"
        || start["processId"].as_u64() != Some(pid as u64)
        || end["processId"].as_u64() != Some(pid as u64)
    {
        return Err("flight capture incomplete or owned identity differs".into());
    }
    let frames = report["frames"].as_array().ok_or("flight frames absent")?;
    if frames.len() < 2 || report["completedFrames"].as_u64() != Some(frames.len() as u64) {
        return Err("incomplete frame array".into());
    }
    let research_panel = research_panel_summary(request, report, frames)?;
    let requested = finite(report, "requestedSimulatedSeconds")?;
    let simulation = finite(report, "actualSimulatedSeconds")?;
    let wall = finite(report, "captureWallSeconds")?;
    let frequency = finite(start, "clockFrequency")?;
    let signal_wall =
        (finite(end, "clockTimestamp")? - finite(start, "clockTimestamp")?) / frequency;
    if frequency <= 0.0
        || end["clockFrequency"] != start["clockFrequency"]
        || wall <= 0.0
        || signal_wall <= 0.0
        || (wall - signal_wall).abs() > 0.1
        || (requested - request.seconds).abs() > 1e-6
        || simulation < requested
        || (finite(end, "universalTime")? - finite(start, "universalTime")? - simulation).abs()
            > 0.1
    {
        return Err("capture duration or monotonic window differs".into());
    }
    let vessel = &frames[0]["vesselId"];
    let topology: Vec<_> = [
        "parts",
        "rigidbodies",
        "joints",
        "colliders",
        "loadedVessels",
    ]
    .iter()
    .map(|key| frames[0][key].clone())
    .collect();
    if report["vSyncCount"] != 0 || report["targetFrameRate"] != -1 {
        return Err("uncapped flight controls not effective".into());
    }
    let first_mode = &frames[0]["warpMode"];
    let mut intervals = Vec::new();
    let mut steps = Vec::new();
    let mut last_frame = None;
    for frame in frames {
        let id = frame["contextFrame"]
            .as_i64()
            .ok_or("frame identifier absent")?;
        if last_frame
            .map(|previous| id != previous + 1)
            .unwrap_or(false)
            || frame["contextAligned"] != true
            || frame["scene"] != "FLIGHT"
            || frame["paused"] != false
            || frame["packed"] != false
            || frame["loaded"] != true
            || frame["vesselId"] != *vessel
            || vessel.as_str().map(str::is_empty).unwrap_or(true)
        {
            return Err("flight frame gaps, misalignment, or state changed".into());
        }
        for (index, key) in [
            "parts",
            "rigidbodies",
            "joints",
            "colliders",
            "loadedVessels",
        ]
        .iter()
        .enumerate()
        {
            if frame[key] != topology[index] || frame[key].as_i64().unwrap_or(-1) < 0 {
                return Err("flight topology changed or unavailable".into());
            }
        }
        if (finite(frame, "warpRate")? - request.warp as f64).abs() > 1e-5
            || frame["warpMode"] != *first_mode
            || !matches!(frame["warpMode"].as_str(), Some("LOW") | Some("HIGH"))
            || (request.warp == 4 && frame["warpMode"] != "LOW")
            || (finite(frame, "maximumDeltaSeconds")?
                - request
                    .maximum_delta
                    .max(finite(frame, "fixedDeltaSeconds")?))
            .abs()
                > 1e-5
            || (finite(frame, "physicsFrameLimitSeconds")? - request.maximum_delta).abs() > 1e-5
        {
            return Err("effective warp or catchup controls differ".into());
        }
        let interval = finite(frame, "wallMilliseconds")?;
        if interval <= 0.0 {
            return Err("invalid frame interval".into());
        }
        intervals.push(interval);
        let callbacks = finite(frame, "fixedCallbacksPerFrame")?;
        if callbacks < 0.0 || callbacks.fract() != 0.0 {
            return Err("invalid owned fixed callback count".into());
        }
        let in_atmosphere =
            finite(frame, "altitudeMeters")? < finite(frame, "atmosphereDepthMeters")?;
        if in_atmosphere != request.atmosphere {
            return Err("flight environment differs from request".into());
        }
        steps.push(callbacks);
        last_frame = Some(id);
    }
    let sampled_wall = intervals.iter().sum::<f64>() / 1000.0;
    if (sampled_wall - wall).abs() > wall * 0.01 + 0.005 {
        return Err("frame intervals do not span capture window".into());
    }
    let loop_report = &report["playerLoop"];
    if loop_report["schema"] != "ksp-continuum-playerloop/v2"
        || loop_report["status"] != "observed"
        || loop_report["integrityStatus"] != "verified-at-boundaries"
        || loop_report["cleanupStatus"] != "removed-owned-hooks"
    {
        return Err("PlayerLoop capture incomplete".into());
    }
    let clock = finite(loop_report, "clockFrequency")?;
    if clock <= 0.0 {
        return Err("PlayerLoop clock invalid".into());
    }
    let window_start = finite(report, "captureStartClockTimestamp")?;
    let window_end = finite(report, "captureEndClockTimestamp")?;
    if window_end <= window_start || finite(report, "clockFrequency")? != clock {
        return Err("PlayerLoop capture clock differs".into());
    }
    let mut scopes = BTreeMap::new();
    let mut costs = BTreeMap::new();
    for scope in loop_report["scopes"]
        .as_array()
        .ok_or("PlayerLoop scopes absent")?
    {
        if scope["status"] != "observed"
            || scope["droppedSamples"].as_u64() != Some(0)
            || scope["sequenceErrors"].as_u64() != Some(0)
        {
            return Err("PlayerLoop samples dropped or invalid".into());
        }
        let name = scope["name"]
            .as_str()
            .ok_or("PlayerLoop scope name absent")?;
        let samples = scope["samples"]
            .as_array()
            .ok_or("PlayerLoop samples absent")?;
        let mut straddlers = 0;
        let mut straddler_ticks = 0.0;
        let mut durations = Vec::new();
        let mut by_frame = BTreeMap::<i64, f64>::new();
        for sample in samples {
            let frame = sample["frame"]
                .as_i64()
                .ok_or("PlayerLoop sample frame absent")?;
            let began = finite(sample, "startedTicks")?;
            let ended = finite(sample, "endedTicks")?;
            if ended < began {
                return Err("PlayerLoop timestamp reversed".into());
            }
            if began < window_start || ended > window_end {
                if ended > window_start && began < window_end {
                    straddlers += 1;
                    straddler_ticks += ended - began;
                }
                continue;
            }
            let elapsed = finite(sample, "elapsedTicks")? / clock * 1000.0;
            if elapsed < 0.0 {
                return Err("negative PlayerLoop duration".into());
            }
            durations.push(elapsed);
            *by_frame.entry(frame).or_default() += elapsed;
        }
        let total = durations.iter().sum::<f64>();
        costs.insert(name.to_string(), total);
        scopes.insert(name.to_string(),json!({"measurementScope":scope["overlap"],"timeDomain":scope["timeDomain"],
            "omittedStraddlers":straddlers,"omittedStraddlerWholeMilliseconds":straddler_ticks/clock*1000.0,
            "enclosedInvocations":durations.len(),"totalMilliseconds":total,"millisecondsPerRenderedFrame":total/frames.len() as f64,
            "perInvocationMilliseconds":distribution(durations)?,"byFrameMilliseconds":by_frame}));
    }
    let fixed = "UnityEngine.PlayerLoop.FixedUpdate";
    let native = "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
    let managed = "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate";
    let update = "UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate";
    let late = "UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate";
    for required in [fixed, native, managed, update, late] {
        if !costs.contains_key(required) {
            return Err(format!("required PlayerLoop scope absent: {required}"));
        }
    }
    let whole = sampled_wall * 1000.0;
    let renderer = costs
        .get("UnityEngine.PlayerLoop.PostLateUpdate")
        .copied()
        .unwrap_or(0.0);
    let early_name = "UnityEngine.PlayerLoop.EarlyUpdate";
    let pre_name = "UnityEngine.PlayerLoop.PreUpdate";
    let prelate_name = "UnityEngine.PlayerLoop.PreLateUpdate";
    let early = costs.get(early_name).copied().unwrap_or(0.0);
    let pre = costs.get(pre_name).copied().unwrap_or(0.0);
    let prelate = costs.get(prelate_name).copied().unwrap_or(costs[late]);
    let scoped = costs[fixed] + costs[update] + prelate + renderer + early + pre;

    let mut summary = json!({"schema":"continuum-flight-profile-summary/v1","status":"capture-validated","runId":run_id,"request":request,
        "intrusiveDiagnosticsRequested":request.native_sample || request.callback_attribution || request.camera_chain || request.gpu_timing_library.is_some(),
        "cameraChainRequested":request.camera_chain,
        "worldDrawModeRequested":request.world_draw,
        "worldDrawObserverRequested":request.world_draw.is_some(),
        "worldDrawingTreatmentRequested":request.world_draw.as_deref() == Some("suppressed"),
        "worldDrawComparisonEligibility":if request.world_draw.is_some() {Value::String("matched-world-owner-observer-only-with-declared-visual-treatment; not-playable-improvement".into())} else {Value::Null},
        "vesselRenderingTreatmentRequested":request.hide_vessel,
        "aeroEffectsTreatmentRequested":request.no_aero_fx || request.aero_quality.is_some() || request.aero_batch || request.aero_field,
        "aeroBatchRequested":request.aero_batch,
        "aeroFieldRequested":request.aero_field,
        "partBatchRequested":request.part_batch,
        "partMappedOnlyRequested":request.part_mapped_only,
        "partReferenceRequested":request.part_reference,
        "partRetainedRequested":request.part_retained,
        "aeroEffectsQualityRequested":request.aero_quality,
        "nonOverlappingEnclosedMilliseconds":scoped,
        "optionalPhaseAccounting":{"earlyUpdateMilliseconds":costs.get(early_name),"preUpdateMilliseconds":costs.get(pre_name),
            "preLateUpdateParentMilliseconds":costs.get(prelate_name),"preLatePhaseAccountedMilliseconds":prelate,
            "preLatePhaseUsesParent":costs.contains_key(prelate_name)},
        "comparisonEligibility":if request.native_sample || request.callback_attribution || request.camera_chain || request.gpu_timing_library.is_some() {"intrusive-diagnostic-perturbation; prohibited-clean-comparison-to-uninstrumented"} else if request.world_draw.is_some() {"world-draw-diagnostic-only-with-matched-world-owner-observer-and-declared-visual-treatment; not-playable-improvement"} else {"unprofiled-flight-only-with-matched-perturbation-checkpoint-controls-and-declared-rendering-treatment"},
        "scope":"diagnostic-flight-capture-only-not-gameplay-qualification","frames":frames.len(),"wallSeconds":wall,"simulatedSeconds":simulation,
        "achievedSimulationRate":simulation/wall,"requestedRate":request.warp,"normalizedSimulationRate":simulation/wall/request.warp as f64,
        "meanFps":frames.len() as f64/sampled_wall,"frameMilliseconds":distribution(intervals)?,"fixedCallbacksPerRenderedFrame":distribution(steps)?,
        "playerLoop":scopes,"fullFrameMilliseconds":whole,"unattributedWallMilliseconds":whole-scoped,
        "renderPostLateParentMilliseconds":renderer,
        "fixedParentOtherMilliseconds":costs[fixed]-costs[native]-costs[managed],
        "physicsStepScope":"Fully enclosed PhysicsFixedUpdate invocations; owned FixedUpdate callbacks are separate coroutine-boundary cadence observations.",
        "effectiveFirstFrame":frames[0],"effectiveLastFrame":frames.last(),
        "accounting":"FixedUpdate parent contains native and managed children; use Fixed parent+script Update+PreLateUpdate parent (otherwise script Late)+PostLateUpdate parent+optional EarlyUpdate/PreUpdate once; PreLate and PostLate children overlap their parents. Missing enclosing phases remain in the remainder. Remainder is wall waiting/rendering/other work and overhead, not GPU duration or exclusive CPU attribution."});
    summary["researchPanelRequested"] = Value::String(request.research_panel.as_str().into());
    summary["localCameraRequested"] = Value::String(request.local_camera.as_str().into());
    summary["researchPanel"] = research_panel;
    if !report["flightIntegratorUpdateAttribution"].is_null() {
        summary["flightIntegratorUpdateAttribution"] = crate::integrator_summary::summarize(report)?;
        summary["intrusiveDiagnosticsRequested"] = Value::Bool(true);
    }
    if !report["convectionCapture"].is_null() {
        summary["convectionCapture"] = report["convectionCapture"].clone();
        summary["intrusiveDiagnosticsRequested"] = Value::Bool(true);
    }
    Ok(summary)
}
pub fn retain(directory: &Path, output: &Path) -> Result<(Value, Value, Value)> {
    let mut values = Vec::new();
    for name in ["capture-start.json", "capture-end.json", "markers.json"] {
        let path = directory.join(name);
        let before = hash(&path)?;
        let value: Value = read(&path)?;
        if hash(&path)? != before {
            return Err("flight evidence changed while copying".into());
        }
        write_new(&output.join(format!("flight-{name}")), &value)?;
        values.push(value);
    }
    for name in [
        "local-camera.json",
        "diagnostic-controls.json",
        "diagnostic-controls-end.json",
        "aero-batch.json",
        "aero-field.json",
        "part-batch.json",
        "flight-view.json",
        "perturbation.json",
        "render-census.json",
        "material-census.json",
        "gpu-timing.json",
        "camera-chain.json",
        "world-draw.json",
    ] {
        let path = directory.join(name);
        if path.exists() {
            let before = hash(&path)?;
            let value: Value = read(&path)?;
            if hash(&path)? != before {
                return Err("flight control evidence changed while copying".into());
            }
            write_new(&output.join(name), &value)?;
        }
    }
    retain_aero_fixture(directory, output)?;
    let view = directory.join("flight-view.png");
    if view.exists() {
        let before = hash(&view)?;
        fs::copy(&view, output.join("flight-view.png")).map_err(|error| error.to_string())?;
        if hash(&view)? != before || hash(&output.join("flight-view.png"))? != before {
            return Err("flight screenshot changed while retaining".into());
        }
    }
    fs::copy(
        directory.join("status.txt"),
        output.join("flight-status.txt"),
    )
    .map_err(|error| error.to_string())?;
    if !fs::read_to_string(output.join("flight-status.txt"))
        .map_err(|error| error.to_string())?
        .starts_with("complete\n")
    {
        return Err("flight qualification status incomplete".into());
    }
    Ok((values.remove(0), values.remove(0), values.remove(0)))
}

fn retain_aero_fixture(directory: &Path, output: &Path) -> Result<()> {
    let receipt = directory.join("aero-field.json");
    if !receipt.exists() { return Ok(()); }
    let field: Value = read(&receipt)?;
    let Some(declared) = field["fixturePath"].as_str().filter(|p| !p.is_empty()) else { return Ok(()); };
    let source = directory.join("aero-field-fixture");
    let manifest = source.join("manifest.json");
    if Path::new(declared).canonicalize().map_err(|e| e.to_string())?
        != manifest.canonicalize().map_err(|e| e.to_string())? {
        return Err("field fixture escaped owned capture directory".into());
    }
    let metadata = fs::symlink_metadata(&manifest).map_err(|e| e.to_string())?;
    if !metadata.is_file() || metadata.len() > 1_048_576 { return Err("field fixture manifest type/bound".into()); }
    let manifest_hash = hash(&manifest)?;
    let fixture: Value = read(&manifest)?;
    if fixture["schema"] != "continuum-aero-field-fixture/v1" || fixture["geometry"] != "geometry.bin"
        || fixture["runId"] != field["runId"] || fixture["processId"] != field["processId"]
        || fixture["vesselId"] != field["vesselId"] { return Err("field fixture identity differs".into()); }
    let geometry = source.join("geometry.bin");
    let metadata = fs::symlink_metadata(&geometry).map_err(|e| e.to_string())?;
    if !metadata.is_file() || metadata.len() > 300_000_000 { return Err("field fixture geometry type/bound".into()); }
    let geometry_hash = hash(&geometry)?;
    if fixture["geometrySha256"] != geometry_hash { return Err("field fixture geometry bytes differ".into()); }
    let destination = output.join("aero-field-fixture");
    if destination.exists() { return Err("field fixture destination already exists".into()); }
    fs::create_dir(&destination).map_err(|e| e.to_string())?;
    fs::copy(&geometry, destination.join("geometry.bin")).map_err(|e| e.to_string())?;
    fs::copy(&manifest, destination.join("manifest.json")).map_err(|e| e.to_string())?;
    if hash(&manifest)? != manifest_hash || hash(&destination.join("manifest.json"))? != manifest_hash
        || hash(&geometry)? != geometry_hash || hash(&destination.join("geometry.bin"))? != geometry_hash {
        return Err("field fixture changed while retaining".into());
    }
    Ok(())
}

fn validate_world_draw(world: &Value, capture: &Value, run_id: &str, pid: u32, mode: &str) -> Result<()> {
    if !matches!(mode, "stock" | "suppressed")
        || world["schema"] != "continuum-flight-world-draw/v1"
        || world["runId"] != run_id || world["processId"].as_u64() != Some(pid as u64)
        || world["vesselId"] != capture["frames"][0]["vesselId"] || world["mode"] != mode
        || world["status"] != "completed"
        || world["cleanupStatus"] != "removed-owned-hooks-restored-survivors"
        || world["hooksRemoved"] != true || world["coverageComplete"] != true
        || world["restorationComplete"] != true || world["faults"].as_u64() != Some(0)
        || world["parts"].as_u64().filter(|n| *n > 0) != capture["frames"][0]["parts"].as_u64().filter(|n| *n > 0)
        || world["parts"].as_u64().filter(|n| *n > 0).is_none()
    {
        return Err("world-draw identity, mode, coverage or lifecycle differs".into());
    }
    let count = |key: &str| world[key].as_u64().ok_or_else(|| format!("world-draw {key} absent or negative"));
    let admitted = count("admitted")?;
    let initial = count("initialEligible")?;
    let dynamic = count("dynamicAdmissions")?;
    let restored = count("restored")?;
    let destroyed = count("destroyed")?;
    let already_off = count("alreadyOff")?;
    let initial_active = count("initialActive")?;
    let end_active = count("endActive")?;
    if initial == 0 || admitted > 50_000 || admitted < initial || dynamic != admitted - initial
        || initial_active > initial || end_active > admitted
        || restored.checked_add(destroyed) != Some(admitted)
        || count("coverageChecks")? == 0 || count("observedFrames")? == 0
    { return Err("world-draw lease counters or observed coverage differ".into()); }
    let inventory = world["inventoryMilliseconds"].as_f64().ok_or("world inventory cost absent")?;
    if !inventory.is_finite() || inventory < 0.0 || world["observerTicks"].as_u64().is_none()
        || world["clockFrequency"].as_i64().filter(|n| *n > 0).is_none()
        || world["clockFrequency"] != capture["clockFrequency"]
    { return Err("world-draw observer measurement or clock differs".into()); }
    let owners = world["owners"].as_array().ok_or("world owner leases absent")?;
    if owners.len() as u64 != admitted { return Err("world-draw lease inventory differs".into()); }
    let mut observed_initial = 0; let mut observed_active = 0; let mut observed_off = 0; let mut observed_restored = 0; let mut observed_destroyed = 0;
    for (generation, owner) in owners.iter().enumerate() {
        if owner["rendererId"].as_i64().filter(|n| *n != 0).is_none()
            || owner["pqsId"].as_i64().filter(|n| *n != 0).is_none()
            || owner["pqsName"].as_str().filter(|s| !s.is_empty()).is_none()
            || !matches!(owner["ownershipKind"].as_str(), Some("native-quad-sphere-root" | "pqs-ancestry"))
        { return Err("world-draw owner identity absent".into()); }
        let original = owner["originalForceRenderingOff"].as_bool().ok_or("world original render flag absent")?;
        let initial = owner["initial"].as_bool().ok_or("world initial lease flag absent")?;
        let active = owner["initialActive"].as_bool().ok_or("world initial active flag absent")?;
        if owner["leaseGeneration"].as_u64() != Some(generation as u64) {
            return Err("world-draw lease generation differs".into());
        }
        let restored = owner["restored"].as_bool().ok_or("world restored lease flag absent")?;
        let destroyed = owner["destroyed"].as_bool().ok_or("world destroyed lease flag absent")?;
        if restored == destroyed { return Err("world-draw lease has no unique terminal outcome".into()); }
        observed_initial += u64::from(initial); observed_off += u64::from(initial && original);
        observed_active += u64::from(initial && active);
        observed_restored += u64::from(restored); observed_destroyed += u64::from(destroyed);
    }
    if observed_initial != initial || observed_active != initial_active || observed_off != already_off || observed_restored != restored || observed_destroyed != destroyed {
        return Err("world-draw counters do not match lease outcomes".into());
    }
    let hooks = world["hooks"].as_array().filter(|h| h.len() == 6).ok_or("world-draw six hook identities absent")?;
    let expected = [("PQS.AssignQuad",100676842), ("PQSCache.DestroyQuad",100676966), ("PQSLandControl+LandClassScatter.CreateScatterMesh",100706694),
        ("LandClassROC.CreateScatterMesh",100664069), ("PQSMod_MeshScatter.BuildCache",100676581), ("PQS.DestroyQuad",100676844)];
    let mut methods = std::collections::BTreeSet::new();
    for hook in hooks {
        let method = hook["method"].as_str().filter(|s| !s.is_empty()).ok_or("world hook method absent")?;
        let identity = hook["identity"].as_str().ok_or("world hook identity absent")?;
        let pieces: Vec<_> = identity.split(':').collect();
        let token = expected.iter().find_map(|(name, token)| (*name == method).then_some(*token));
        if token.is_none() || !methods.insert(method) || pieces.len() != 3
            || pieces[0] != "10657063-2fc3-43a7-84fa-d39e75e877bf" || pieces[1].parse::<u32>().ok() != token
            || pieces[2].len() != 64 || !pieces[2].bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err("world-draw hook identities empty or duplicated".into());
        }
    }
    Ok(())
}

fn camera_chain_summary(chain: &Value, capture: &Value, run_id: &str, pid: u32) -> Result<Value> {
    if chain["schema"] != "continuum-flight-camera-chain/v1"
        || chain["runId"] != run_id || chain["processId"].as_u64() != Some(pid as u64)
        || chain["vesselId"] != capture["frames"][0]["vesselId"]
        || chain["status"] != "completed" || chain["hooksRemoved"] != true
        || chain["cleanupStatus"] != "removed-owned-hooks"
    {
        return Err("camera-chain identity, native lifecycle or cleanup differs".into());
    }
    let frequency = chain["clockFrequency"].as_i64().filter(|n| *n > 0).ok_or("camera clock absent")?;
    if capture["clockFrequency"].as_i64() != Some(frequency) {
        return Err("camera and capture clocks differ".into());
    }
    let begin = capture["captureStartClockTimestamp"].as_i64().ok_or("capture start absent")?;
    let end = capture["captureEndClockTimestamp"].as_i64().ok_or("capture end absent")?;
    if end <= begin { return Err("camera capture window reversed".into()); }
    let cameras = chain["cameras"].as_array().filter(|r| !r.is_empty() && r.len() <= 64).ok_or("camera inventory bound")?;
    let scene = chain["scene"].as_array().filter(|r| r.len() <= 50_000).ok_or("camera scene inventory bound")?;
    let mut camera_ids = std::collections::BTreeSet::new();
    for row in cameras {
        let id = row["instanceId"].as_i64().filter(|id| *id != 0).ok_or("camera instance identity absent")?;
        if !camera_ids.insert(id) { return Err("duplicate camera identity".into()); }
    }
    let mut rows = Vec::new();
    let mut observed = 0;
    for row in cameras {
        let n = row["sampleCount"].as_u64().filter(|n| *n <= 8192).ok_or("camera sample bound")? as usize;
        let names = ["frames", "startTicks", "preRenderTicks", "endTicks", "childTicks", "parentCameraIds", "depths", "stages"];
        let mut arrays = Vec::new();
        for name in names {
            let values = row[name].as_array().filter(|a| a.len() == n).ok_or("camera sample arrays differ")?;
            arrays.push(values.iter().map(|v| v.as_i64().ok_or_else(|| "camera witness is not an integer".to_string())).collect::<Result<Vec<_>>>()?);
        }
        let mut durations = Vec::new();
        let mut child_ms = 0.0;
        let mut cull_to_render_ms = 0.0;
        let mut omitted = 0;
        let mut previous_end = 0;
        for i in 0..n {
            let frame = arrays[0][i]; let start = arrays[1][i]; let pre = arrays[2][i]; let finish = arrays[3][i];
            let child = arrays[4][i]; let parent = arrays[5][i]; let depth = arrays[6][i]; let stage = arrays[7][i];
            if frame < 0 || start < previous_end || pre < start || finish < pre || child < 0 || child > finish - start
                || !(0..64).contains(&depth) || stage != 2 || (depth == 0) != (parent == 0)
                || (parent != 0 && (!camera_ids.contains(&parent) || parent == row["instanceId"].as_i64().unwrap())) {
                return Err("camera interval ordering, native pairing or parent witness differs".into());
            }
            previous_end = finish;
            if start < begin || finish > end { omitted += 1; continue; }
            durations.push((finish-start) as f64 * 1000.0 / frequency as f64);
            child_ms += child as f64 * 1000.0 / frequency as f64;
            cull_to_render_ms += (pre-start) as f64 * 1000.0 / frequency as f64;
        }
        observed += durations.len();
        let total = durations.iter().sum::<f64>();
        rows.push(json!({"instanceId":row["instanceId"],"role":row["role"],"name":row["name"],"rawSamples":n,
            "enclosedIntervals":durations.len(),"omittedOutsideWindowOrStraddlers":omitted,
            "inclusiveCallbackMilliseconds":total,"observedNestedChildMilliseconds":child_ms,
            "remainingCallbackEnvelopeMilliseconds":total-child_ms,"cullToPreRenderMilliseconds":cull_to_render_ms,
            "perInvocationMilliseconds":if durations.is_empty() {Value::Null} else {distribution(durations)?}}));
    }
    if observed == 0 { return Err("no complete native camera intervals inside capture".into()); }
    Ok(json!({"schema":"continuum-flight-camera-chain-summary/v1","runId":run_id,"processId":pid,
        "vesselId":chain["vesselId"],"cameraCount":cameras.len(),"sceneRendererCount":scene.len(),"cameras":rows,
        "scope":"Intrusive camera callback wall envelopes, with only observed nested child intervals removed; not exclusive native CPU, GPU duration, draw counts or proof of world ownership. Parent/child rows overlap. Scene inventory and layer membership describe potential coverage only.",
        "captureStartTicks":begin,"captureEndTicks":end,"clockFrequency":frequency}))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn world_draw_rejects_incomplete_or_foreign_ownership() {
        let mut options = BTreeMap::from([
            ("--flight-save".into(), "Lab".into()),
            ("--flight-checkpoint".into(), "Station".into()),
            ("--flight-world-draw".into(), "suppressed".into()),
        ]);
        let request = Request::parse(&options).unwrap().unwrap();
        assert!(request.arguments().contains(&"--continuum-flight-world-draw=suppressed".into()));
        let launch = crate::launch::ResolvedLaunch::parse(&options).unwrap();
        let restored = crate::launch::ResolvedLaunch::parse_from_json(&launch.identity_json().unwrap()).unwrap();
        assert_eq!(restored.flight.unwrap().world_draw.as_deref(), Some("suppressed"));
        options.insert("--flight-world-draw".into(), "stock".into());
        assert_eq!(Request::parse(&options).unwrap().unwrap().world_draw.as_deref(), Some("stock"));
        options.insert("--flight-world-draw".into(), "hidden".into());
        assert!(Request::parse(&options).is_err());
        let mut legacy = serde_json::to_value(&request).unwrap();
        legacy.as_object_mut().unwrap().remove("worldDraw");
        assert!(serde_json::from_value::<Request>(legacy).unwrap().world_draw.is_none());
        let capture = json!({"clockFrequency":1000,"frames":[{"vesselId":"v","parts":196}]});
        let row = json!({"rendererId":1,"pqsId":2,"pqsName":"Kerbin","ownershipKind":"native-quad-sphere-root","originalForceRenderingOff":false,
            "initial":true,"initialActive":true,"leaseGeneration":0,"restored":true,"destroyed":false});
        let mut pooled = row.clone(); pooled["initial"] = json!(false); pooled["leaseGeneration"] = json!(1);
        let hooks = [("PQS.AssignQuad",100676842),("PQSCache.DestroyQuad",100676966),("PQSLandControl+LandClassScatter.CreateScatterMesh",100706694),
            ("LandClassROC.CreateScatterMesh",100664069),("PQSMod_MeshScatter.BuildCache",100676581),("PQS.DestroyQuad",100676844)].iter()
            .map(|(method,token)| json!({"method":method,"identity":format!("10657063-2fc3-43a7-84fa-d39e75e877bf:{token}:{}", "a".repeat(64))})).collect::<Vec<_>>();
        let world = json!({"schema":"continuum-flight-world-draw/v1","runId":"r","processId":7,"vesselId":"v",
            "mode":"suppressed","status":"completed","cleanupStatus":"removed-owned-hooks-restored-survivors",
            "parts":196,"initialEligible":1,"admitted":2,"dynamicAdmissions":1,"destroyed":0,"restored":2,
            "alreadyOff":0,"initialActive":1,"endActive":0,"faults":0,"coverageChecks":8,"observedFrames":4,
            "hooksRemoved":true,"coverageComplete":true,"restorationComplete":true,"inventoryMilliseconds":1.2,
            "observerTicks":42,"clockFrequency":1000,"owners":[row,pooled],
            "hooks":hooks});
        validate_world_draw(&world, &capture, "r", 7, "suppressed").unwrap();
        for (key,value) in [("runId",json!("foreign")),("processId",json!(8)),("vesselId",json!("other")),("mode",json!("stock"))] {
            let mut bad = world.clone(); bad[key] = value;
            assert!(validate_world_draw(&bad, &capture, "r", 7, "suppressed").is_err(), "reject foreign {key}");
        }
        for key in ["runId","processId","vesselId","mode","status","parts","cleanupStatus","coverageComplete",
            "restorationComplete","hooksRemoved","initialEligible","admitted","dynamicAdmissions","restored",
            "destroyed","alreadyOff","initialActive","endActive","faults","coverageChecks","observedFrames",
            "inventoryMilliseconds","observerTicks","clockFrequency","owners","hooks"] {
            let mut bad = world.clone(); bad[key] = Value::Null;
            assert!(validate_world_draw(&bad, &capture, "r", 7, "suppressed").is_err(), "reject {key}");
        }
        for (key,value) in [("dynamicAdmissions",json!(0)),("restored",json!(1)),("alreadyOff",json!(1)),
            ("faults",json!(1)),("coverageChecks",json!(0)),("observedFrames",json!(0)),("inventoryMilliseconds",json!(-1))] {
            let mut bad = world.clone(); bad[key] = value;
            assert!(validate_world_draw(&bad, &capture, "r", 7, "suppressed").is_err(), "reject {key}");
        }
        for key in ["rendererId","pqsId","pqsName","originalForceRenderingOff","initial","initialActive","leaseGeneration","restored","destroyed"] {
            let mut bad = world.clone(); bad["owners"][0][key] = Value::Null;
            assert!(validate_world_draw(&bad, &capture, "r", 7, "suppressed").is_err(), "reject owner {key}");
        }
        let mut bad = world.clone(); bad["owners"][0]["destroyed"] = json!(true);
        assert!(validate_world_draw(&bad, &capture, "r", 7, "suppressed").is_err());
        let mut bad = world.clone(); bad["hooks"][0]["identity"] = json!("");
        assert!(validate_world_draw(&bad, &capture, "r", 7, "suppressed").is_err());
    }

    #[test]
    fn camera_chain_rejects_unpaired_or_foreign_native_evidence() {
        let mut options = BTreeMap::from([
            ("--flight-save".into(), "Lab".into()),
            ("--flight-checkpoint".into(), "Station".into()),
            ("--flight-camera-chain".into(), "true".into()),
        ]);
        let request = Request::parse(&options).unwrap().unwrap();
        assert!(request.arguments().contains(&"--continuum-flight-camera-chain".into()));
        assert!(crate::launch::ResolvedLaunch::parse(&options).unwrap().flight.unwrap().camera_chain);
        let mut old_profile = serde_json::to_value(&request).unwrap();
        old_profile.as_object_mut().unwrap().remove("cameraChain");
        assert!(!serde_json::from_value::<Request>(old_profile).unwrap().camera_chain);
        options.insert("--flight-camera-chain".into(), "maybe".into());
        assert!(Request::parse(&options).is_err());
        let capture = json!({"clockFrequency":1000,"captureStartClockTimestamp":100,"captureEndClockTimestamp":200,"frames":[{"vesselId":"v"}]});
        let row = json!({"instanceId":-2,"name":"Main","role":"flight-main","sampleCount":2,
            "frames":[1,2],"startTicks":[90,120],"preRenderTicks":[100,125],"endTicks":[110,140],
            "childTicks":[0,5],"parentCameraIds":[0,0],"depths":[0,0],"stages":[2,2]});
        let chain = json!({"schema":"continuum-flight-camera-chain/v1","runId":"r","processId":7,"vesselId":"v",
            "status":"completed","hooksRemoved":true,"cleanupStatus":"removed-owned-hooks","clockFrequency":1000,"cameras":[row],"scene":[]});
        let summary = camera_chain_summary(&chain, &capture, "r", 7).unwrap();
        assert_eq!(summary["cameras"][0]["enclosedIntervals"], 1);
        assert_eq!(summary["cameras"][0]["inclusiveCallbackMilliseconds"], 20.0);
        assert_eq!(summary["cameras"][0]["remainingCallbackEnvelopeMilliseconds"], 15.0);
        for key in ["runId", "processId", "vesselId", "status", "cleanupStatus", "hooksRemoved", "clockFrequency"] {
            let mut bad = chain.clone(); bad[key] = Value::Null;
            assert!(camera_chain_summary(&bad, &capture, "r", 7).is_err(), "reject {key}");
        }
        for (key, bad_value) in [("stages",json!([2,1])),("childTicks",json!([0,21])),("frames",json!([1])),("preRenderTicks",json!([100,141])),("parentCameraIds",json!([0,-9]))] {
            let mut bad = chain.clone(); bad["cameras"][0][key] = bad_value;
            assert!(camera_chain_summary(&bad, &capture, "r", 7).is_err(), "reject {key}");
        }
    }

    #[test]
    fn aero_field_rejects_incomplete_and_competing_ownership() {
        let mut options = BTreeMap::from([
            ("--flight-save".into(), "Lab".into()),
            ("--flight-checkpoint".into(), "entry".into()),
            ("--flight-aero-field".into(), "true".into()),
        ]);
        assert!(Request::parse(&options).is_err(), "field without a cooked bundle must reject");
        let bundle = std::env::temp_dir().join(format!("continuum-field-{}-{}.bundle", std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        fs::write(&bundle, b"test artifact").unwrap();
        options.insert("--flight-aero-field-bundle".into(), bundle.canonicalize().unwrap().to_string_lossy().into_owned());
        assert!(Request::parse(&options).unwrap().unwrap().arguments().contains(&"--continuum-flight-aero-field".into()));
        for (key, value) in [("--flight-aero-batch", "true"), ("--flight-no-aero-fx", "true"),
            ("--flight-hide-vessel", "true"), ("--flight-aero-quality", "1"), ("--flight-part-retained", "true")] {
            options.insert(key.into(), value.into());
            assert!(Request::parse(&options).is_err(), "competing field treatment {key}");
            options.remove(key);
        }
        options.insert("--flight-aero-field".into(), "false".into());
        assert!(Request::parse(&options).is_err(), "unused field bundle must reject");
        fs::remove_file(bundle).unwrap();
    }

    #[test]
    fn retained_ownership_requires_explicit_counter_evidence() {
        let complete = json!({"eligibleRenderers":9,"retiredRenderers":7,"restoredRetiredRenderers":7,
            "rigidRetainedSources":4,"dynamicRetainedSources":3,"sharedPartPoseSnapshots":20,
            "dynamicPoseSnapshots":15,"animationCullingLeases":2,"restoredAnimationCullingLeases":2,
            "maxInitialRecoveredPositionErrorMeters":0.0001});
        assert!(validate_retained_part_batch(&complete).is_ok());
        let mut missing_leases = complete.clone();
        missing_leases.as_object_mut().unwrap().remove("animationCullingLeases");
        missing_leases.as_object_mut().unwrap().remove("restoredAnimationCullingLeases");
        assert!(validate_retained_part_batch(&missing_leases).is_err());
        let mut unpublished_dynamic = complete;
        unpublished_dynamic["dynamicPoseSnapshots"] = json!(0);
        assert!(validate_retained_part_batch(&unpublished_dynamic).is_err());
    }
    #[test]
    fn aero_field_requires_current_pixels_exclusive_ownership_and_restoration() {
        let bundle = std::env::temp_dir().join(format!("continuum-field-proof-{}-{}.bundle", std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        fs::write(&bundle, b"shader witness").unwrap();
        let bundle = bundle.canonicalize().unwrap();
        let options = BTreeMap::from([
            ("--flight-save".into(), "Lab".into()), ("--flight-checkpoint".into(), "entry".into()),
            ("--flight-aero-field".into(), "true".into()), ("--flight-aero-field-bundle".into(), bundle.to_string_lossy().into_owned()),
        ]);
        let request = Request::parse(&options).unwrap().unwrap();
        let mut field = json!({"schema":"continuum-flight-aero-field/v1","runId":"owned","processId":42,"vesselId":"vessel",
            "bundlePath":bundle,"bundleSha256":hash(&bundle).unwrap(),"status":"complete-experimental","faults":0,
            "stockRestored":true,"hooksRemoved":true,"geometryRestored":true,"pixelProofComplete":true,
            "stockDepthPre":0,"stockDepthPost":0,"nonFiniteSamples":0,"invalidNormalSamples":0,"invalidFlowSamples":0,"invalidSceneDepthSamples":0});
        for key in ["viewPositiveSamples", "flowPositiveSamples", "scenePositiveSamples", "geometryPackets", "eligibleRenderers", "producerSnapshots",
            "sceneDepthCallbacks", "suppressedDepth", "geometryRestoredOriginals", "coveragePre", "coveragePost", "flowPre", "flowPost",
            "compositorPre", "compositorPost", "createdTextures", "releasedTextures", "createdMaterials", "destroyedMaterials",
            "proofTemporaryCreated", "proofTemporaryReleased", "geometryDestroyedProxies"] { field[key] = json!(2); }
        for key in ["lastCoverageFrame", "lastFlowFrame", "lastSceneDepthFrame", "lastProducerFrame", "lastCompositeFrame"] { field[key] = json!(9); }
        assert!(validate_aero_field(&request, &field, "owned", 42, &json!("vessel")).is_ok());
        for (key, counterexample) in [("viewPositiveSamples",json!(0)), ("scenePositiveSamples",json!(0)), ("invalidSceneDepthSamples",json!(1)), ("lastFlowFrame",json!(8)), ("stockDepthPre",json!(1)),
            ("releasedTextures",json!(1)), ("pixelProofComplete",json!(false)), ("bundleSha256",json!("wrong"))] {
            let mut rejected = field.clone(); rejected[key] = counterexample;
            assert!(validate_aero_field(&request, &rejected, "owned", 42, &json!("vessel")).is_err(), "must reject {key}");
        }
        fs::remove_file(bundle).unwrap();
    }
    #[test]
    fn part_replacement_requires_observed_draws_and_declared_shader() {
        let options = BTreeMap::from([
            ("--flight-save".into(), "Lab".into()),
            ("--flight-checkpoint".into(), "vacuum".into()),
            ("--flight-part-batch".into(), "true".into()),
        ]);
        let mut request = Request::parse(&options).unwrap().unwrap();
        assert!(request.arguments().contains(&"--continuum-flight-part-batch".into()));
        let valid = json!({"schema":"continuum-flight-part-batch/v1","runId":"run","processId":123,"vesselId":"vessel",
            "status":"complete-experimental","faults":0,"eligibleRenderers":36,"packets":1,"cameraInvocations":10,"drawSubmissions":10,
            "restoredOriginals":36,"cleanupStatus":"restored-owned-renderers-removed-owned-hooks-destruction-requested",
            "shaderSource":"shipped-standard-diagnostic","family":"supported-specular-families","mode":"replacement","retainedProvider":false});
        assert!(validate_part_batch(&request, &valid, "run", 123, &json!("vessel")).is_err());
        request.part_standard_fallback = true;
        assert!(validate_part_batch(&request, &valid, "run", 123, &json!("vessel")).is_ok());
        for key in ["drawSubmissions", "eligibleRenderers", "restoredOriginals"] {
            let mut invalid = valid.clone(); invalid[key] = json!(0);
            assert!(validate_part_batch(&request, &invalid, "run", 123, &json!("vessel")).is_err());
        }
        let mut fallback = valid.clone(); fallback["status"] = json!("stock-fallback");
        assert!(validate_part_batch(&request, &fallback, "run", 123, &json!("vessel")).is_err());
        request.part_reference = true; request.part_mapped_only = true;
        let mut reference = valid.clone(); reference["mode"] = json!("reference"); reference["family"] = json!("KSP/Emissive/Bumped Specular (Mapped)");
        for key in ["cameraInvocations", "drawSubmissions", "suppressedRenderers", "restoredOriginals"] { reference[key] = json!(0); }
        assert!(validate_part_batch(&request, &reference, "run", 123, &json!("vessel")).is_ok());
        for key in ["suppressedRenderers", "restoredOriginals", "drawSubmissions"] {
            let mut mutated = reference.clone(); mutated[key] = json!(1);
            assert!(validate_part_batch(&request, &mutated, "run", 123, &json!("vessel")).is_err());
        }
        reference["mode"] = json!("replacement");
        assert!(validate_part_batch(&request, &reference, "run", 123, &json!("vessel")).is_err());
        let mut missing_provider = valid.clone(); missing_provider.as_object_mut().unwrap().remove("retainedProvider");
        request.part_reference = false; request.part_mapped_only = false;
        assert!(validate_part_batch(&request, &missing_provider, "run", 123, &json!("vessel")).is_err());
    }
    #[test]
    fn local_camera_contract_rejects_conflicts_drift_and_missing_restore() {
        let mut options=BTreeMap::from([("--flight-save".into(),"Lab".into()),("--flight-checkpoint".into(),"vacuum".into())]);
        let mut request=Request::parse(&options).unwrap().unwrap(); assert_eq!(request.local_camera,LocalCameraMode::Stock);
        let mut historical=serde_json::to_value(&request).unwrap(); historical.as_object_mut().unwrap().remove("localCamera");
        assert_eq!(serde_json::from_value::<Request>(historical).unwrap().local_camera,LocalCameraMode::Stock);
        options.insert("--flight-local-camera".into(),"typo".into()); assert!(Request::parse(&options).is_err());
        options.insert("--flight-local-camera".into(),"merged".into()); options.insert("--flight-atmosphere".into(),"true".into()); assert!(Request::parse(&options).is_err());
        options.remove("--flight-atmosphere"); request=Request::parse(&options).unwrap().unwrap();
        assert!(request.arguments().contains(&"--continuum-flight-local-camera=merged".into()));
        let state=json!({"enabled":true,"activeSelf":true,"activeInHierarchy":true,"isPQS":false,"nearClip":0.1,"farClip":1000.0,"fieldOfView":60.0,"depth":0.0,"cullingMask":-1,"clearFlags":1,"depthTextureMode":1,"renderingPath":1,"targetId":0});
        let row=|id,index|json!({"instanceId":id,"name":"native-local","flightCameraIndex":index,"original":state,"effective":state,"restored":state,"componentTypes":["Camera"],"commandNames":[],"preCullCount":2,"preRenderCount":2,"postRenderCount":2});
        let mut camera=json!({"schema":"continuum-flight-local-camera/v1","mode":"merged","runId":"run","processId":123,"vesselId":"v","parts":16,"status":"completed-experimental","faults":0,"hooksRemoved":true,"restorationComplete":true,"cleanupStatus":"removed-owned-hooks-restored-original-camera-state","observedFrames":2,"coverageChecks":2,"observerTicks":2,"clockFrequency":1000,"windowStartFrame":1,"windowEndFrame":3,"windowStartClockTimestamp":100,"windowEndClockTimestamp":200,"camera00":row(10,0),"camera01":row(11,1)});
        camera["globalsUnchanged"]=json!(true);
        for key in ["originalLocalCameraPosBits","originalLocalCameraDirBits","endLocalCameraPosBits","endLocalCameraDirBits"] { camera[key]=json!([0x3f800000,0x3f800000,0x3f800000,if key.contains("Pos") {0x7f800000} else {0}]); }
        camera["maxProjectionError"]=json!(0.0);
        for name in ["camera00","camera01"] { for key in ["originalProjection","effectiveProjection","restoredProjection"] { camera[name][key]=json!([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]); } }
        camera["camera01"]["original"]["isPQS"]=json!(true); camera["camera01"]["restored"]["isPQS"]=json!(true);
        camera["camera00"]["effective"]["farClip"]=json!(750000); camera["camera00"]["effective"]["isPQS"]=json!(true);
        for key in ["activeSelf","activeInHierarchy","isPQS"] { camera["camera01"]["effective"][key]=json!(false); }
        for key in ["preCullCount","preRenderCount","postRenderCount"] { camera["camera01"][key]=json!(0); }
        let report=json!({"captureStartClockTimestamp":100,"captureEndClockTimestamp":200,"clockFrequency":1000,"frames":[{"contextFrame":1,"parts":16,"vesselId":"v"},{"contextFrame":2,"parts":16,"vesselId":"v"}]});
        assert!(local_camera_summary(&request,&camera,&report,"run",123).is_ok());
        let mut drift=camera.clone(); drift["camera00"]["effective"]["fieldOfView"]=json!(70); assert!(local_camera_summary(&request,&drift,&report,"run",123).is_err());
        let mut missing=camera.clone(); missing["camera01"].as_object_mut().unwrap().remove("restored"); assert!(local_camera_summary(&request,&missing,&report,"run",123).is_err());
        let mut unpaired=camera.clone(); unpaired["camera01"]["preCullCount"]=json!(1); assert!(local_camera_summary(&request,&unpaired,&report,"run",123).is_err());
        request.local_camera=LocalCameraMode::Stock; request.warp=4;
        let skipped=json!({"schema":"continuum-flight-local-camera/v1","mode":"stock","runId":"run","processId":123,"vesselId":"v","parts":16,"status":"not-applicable","reason":"declared-unsupported-stock-workload","cleanupStatus":"not-applicable-no-owned-hooks-or-mutations","hooksRemoved":true,"restorationComplete":true,"camera00":null,"camera01":null,"faults":0,"observedFrames":0,"coverageChecks":0,"observerTicks":0,"windowStartFrame":0,"windowEndFrame":0,"windowStartClockTimestamp":0,"windowEndClockTimestamp":0});
        assert!(local_camera_summary(&request,&skipped,&report,"run",123).is_ok());
        request.warp=1; assert!(local_camera_summary(&request,&skipped,&report,"run",123).is_err());
    }
    #[test]
    fn research_panel_contract_rejects_drift_missing_and_hidden_work() {
        let mut options = BTreeMap::from([("--flight-save".into(),"Lab".into()),("--flight-checkpoint".into(),"vacuum".into())]);
        let mut request = Request::parse(&options).unwrap().unwrap();
        assert_eq!(request.research_panel, ResearchPanelMode::Visible);
        let mut historical = serde_json::to_value(&request).unwrap(); historical.as_object_mut().unwrap().remove("researchPanel");
        assert_eq!(serde_json::from_value::<Request>(historical).unwrap().research_panel, ResearchPanelMode::Visible);
        options.insert("--flight-research-panel".into(),"typo".into()); assert!(Request::parse(&options).is_err());
        options.insert("--flight-research-panel".into(),"hidden".into()); request = Request::parse(&options).unwrap().unwrap();
        assert!(request.arguments().contains(&"--continuum-research-panel=hidden".into()));
        let frames = vec![json!({"contextFrame":5}),json!({"contextFrame":6})];
        let mut report = json!({"captureStartClockTimestamp":100,"captureEndClockTimestamp":200,"clockFrequency":1000,"researchPanel":{
            "schema":"continuum-research-panel/v1","status":"complete","mode":"hidden","startVisible":false,"endVisible":false,"startComponentPresent":false,"endComponentPresent":false,
            "captureStartFrame":5,"captureEndFrame":7,"captureStartClockTimestamp":100,"captureEndClockTimestamp":200,"clockFrequency":1000,
            "modeChanges":0,"errors":0,"serviceUpdateCalls":2,"serviceFixedCalls":1,"guiCalls":0,"layoutCalls":0,"repaintCalls":0,"otherCalls":0,"guiTicks":0,"layoutTicks":0,"repaintTicks":0,"otherTicks":0}});
        assert!(research_panel_summary(&request,&report,&frames).is_ok());
        for (key,value) in [("endVisible",json!(true)),("captureEndFrame",json!(8)),("repaintCalls",json!(1))] {
            let mut bad=report.clone(); bad["researchPanel"][key]=value; assert!(research_panel_summary(&request,&bad,&frames).is_err());
        }
        let mut missing=report.clone(); missing["researchPanel"].as_object_mut().unwrap().remove("errors"); assert!(research_panel_summary(&request,&missing,&frames).is_err());
        request.research_panel=ResearchPanelMode::Visible;
        report["researchPanel"]["mode"]=json!("visible");
        for key in ["startVisible","endVisible","startComponentPresent","endComponentPresent"] { report["researchPanel"][key]=json!(true); }
        report["researchPanel"]["guiCalls"]=json!(2); report["researchPanel"]["layoutCalls"]=json!(1); report["researchPanel"]["repaintCalls"]=json!(1);
        report["researchPanel"]["guiTicks"]=json!(3); report["researchPanel"]["layoutTicks"]=json!(1); report["researchPanel"]["repaintTicks"]=json!(2);
        let summary=research_panel_summary(&request,&report,&frames).unwrap(); assert_eq!(summary["guiMillisecondsPerCompletedFrame"],json!(1.5));
    }
    #[test]
    fn mapped_reference_selection_requires_provider_and_excludes_retirement() {
        let mut options = BTreeMap::from([("--flight-save".into(),"Lab".into()),("--flight-checkpoint".into(),"vacuum".into()),("--flight-part-reference".into(),"true".into())]);
        assert!(Request::parse(&options).is_err());
        options.insert("--flight-part-batch".into(),"true".into()); options.insert("--flight-part-mapped-only".into(),"true".into());
        let request = Request::parse(&options).unwrap().unwrap();
        assert!(request.arguments().contains(&"--continuum-flight-part-reference".into()));
        assert!(request.arguments().contains(&"--continuum-flight-part-mapped-only".into()));
        options.insert("--flight-part-retained".into(),"true".into()); options.insert("--flight-aero-batch".into(),"true".into());
        assert!(Request::parse(&options).is_err());
    }
    #[test]
    fn camera_contract_rejects_unobserved_or_drifting_views() {
        let valid = json!({"cameraFrozen":true,"cameraContract":"continuum-root-relative-camera/v1: fixture",
            "cameraFailure":null,"cameraDistance":50.0,"cameraFieldOfView":60.0,
            "cameraObservedInvocations":10,"cameraMaxPositionErrorMeters":0.001,"cameraMaxAngleErrorDegrees":0.001});
        assert!(validate_frozen_camera(&valid, true).is_ok());
        let mut missing = valid.clone(); missing["cameraObservedInvocations"] = json!(0);
        assert!(validate_frozen_camera(&missing, true).is_err());
        let mut drift = valid.clone(); drift["cameraMaxPositionErrorMeters"] = json!(0.1);
        assert!(validate_frozen_camera(&drift, true).is_err());
    }
    #[test]
    fn interpolation_uses_whole_distribution_and_rejects_missing_values() {
        let result = distribution(vec![40.0, 10.0, 30.0, 20.0]).unwrap();
        assert_eq!(result["p50"], 25.0);
        assert_eq!(result["p95"], 38.5);
        assert!(distribution(vec![]).is_err());
        assert!(distribution(vec![f64::NAN]).is_err());
    }
    #[test]
    fn flight_flags_bind_names_and_preserve_requested_scope() {
        let options = BTreeMap::from([
            ("--flight-save".into(), "Lab".into()),
            ("--flight-checkpoint".into(), "vacuum-128".into()),
            ("--flight-warp".into(), "4".into()),
            ("--flight-perturbation".into(), "off-center-pulse".into()),
            ("--flight-callback-attribution".into(), "true".into()),
            ("--flight-hide-vessel".into(), "true".into()),
            ("--flight-no-aero-fx".into(), "true".into()),
            ("--flight-aero-batch".into(), "false".into()),
            ("--flight-screenshot".into(), "true".into()),
            ("--flight-pulse-delta-v".into(), "25".into()),
        ]);
        let request = Request::parse(&options).unwrap().unwrap();
        assert!(request
            .arguments()
            .contains(&"--continuum-physics-warp-pressure".to_string()));
        assert!(!request
            .arguments()
            .contains(&"--continuum-native-boundary-bench".to_string()));
        assert!(request.screenshot);
        assert!(request
            .arguments()
            .contains(&"--continuum-flight-screenshot".to_string()));
        let mut batch_options = options.clone();
        batch_options.insert("--flight-aero-batch".into(), "true".into());
        assert!(Request::parse(&batch_options).is_err());
        batch_options.insert("--flight-no-aero-fx".into(), "false".into());
        assert!(Request::parse(&batch_options).is_err());
        batch_options.insert("--flight-hide-vessel".into(), "false".into());
        batch_options.insert("--flight-aero-quality".into(), "0".into());
        assert!(Request::parse(&batch_options).is_err());
        batch_options.insert("--flight-aero-quality".into(), "1".into());
        let batch_request = Request::parse(&batch_options).unwrap().unwrap();
        assert!(batch_request.aero_batch);
        assert!(batch_request
            .arguments()
            .contains(&"--continuum-flight-aero-batch".to_string()));
        let mut lifecycle = json!({"schema":"continuum-flight-aero-batch/v1","runId":"run","processId":123,"vesselId":"vessel",
            "status":"complete-experimental","faults":0,"eligibleRenderers":12,"batches":2,"cameraInvocations":3,"destroyedProxies":2,
            "cleanupStatus":"removed-owned-hooks-restored-owned-renderers-destruction-requested",
            "cameras":[{"role":"fx","invocations":1},{"role":"fx-depth","invocations":1},{"role":"velocity-occlusion","invocations":1}]});
        assert!(validate_batch(&lifecycle, "run", 123, &json!("vessel")).is_ok());
        lifecycle["cameras"][2]["invocations"] = json!(0);
        assert!(validate_batch(&lifecycle, "run", 123, &json!("vessel")).is_err());
        lifecycle["cameras"][2]["invocations"] = json!(1);
        lifecycle["destroyedProxies"] = json!(1);
        assert!(validate_batch(&lifecycle, "run", 123, &json!("vessel")).is_err());
        assert!(request.no_aero_fx);
        assert!(request
            .arguments()
            .contains(&"--continuum-flight-no-aero-fx".to_string()));
        assert_eq!(request.perturbation, "off-center-pulse");
        assert_eq!(request.pulse_delta_v, 25.0);
        assert!(request
            .arguments()
            .contains(&"--continuum-flight-pulse-delta-v=25".to_string()));
        assert!(request
            .arguments()
            .contains(&"--continuum-callback-attribution".to_string()));
        assert!(request
            .arguments()
            .contains(&"--continuum-flight-hide-vessel".to_string()));
        assert!(request
            .arguments()
            .windows(2)
            .any(|pair| pair == ["--continuum-flight-perturbation", "off-center-pulse"]));
        assert!(validate_aero_boundary(&request, &json!({"aeroFxQuality":0,"aeroFxInstances":1,"enabledAeroFxCameras":0,"enabledAeroDepthCameras":0})).is_ok());
        assert!(validate_aero_boundary(&request, &json!({"aeroFxQuality":0,"aeroFxInstances":1,"enabledAeroFxCameras":1,"enabledAeroDepthCameras":0})).is_err());
        let mut quality_options = options.clone();
        quality_options.insert("--flight-aero-quality".into(), "1".into());
        assert!(Request::parse(&quality_options).is_err());
        quality_options.insert("--flight-no-aero-fx".into(), "false".into());
        let quality_request = Request::parse(&quality_options).unwrap().unwrap();
        assert_eq!(quality_request.aero_quality, Some(1));
        assert!(quality_request
            .arguments()
            .contains(&"--continuum-flight-aero-quality=1".to_string()));
        assert!(validate_aero_boundary(&quality_request, &json!({"aeroFxQuality":1})).is_ok());
        assert!(validate_aero_boundary(&quality_request, &json!({"aeroFxQuality":3})).is_err());
        quality_options.insert("--flight-aero-quality".into(), "4".into());
        assert!(Request::parse(&quality_options).is_err());
        let mut entry_options = options.clone();
        entry_options.insert("--flight-entry-target-altitude".into(), "15000".into());
        assert!(Request::parse(&entry_options).is_err());
        entry_options.insert("--flight-warp".into(), "1".into());
        let entry_request = Request::parse(&entry_options).unwrap().unwrap();
        assert!(entry_request.atmosphere);
        assert!(entry_request
            .arguments()
            .contains(&"--continuum-flight-entry-target=15000".to_string()));
        let mut escaped = options;
        escaped.insert("--flight-checkpoint".into(), "../persistent".into());
        assert!(Request::parse(&escaped).is_err());
    }
    #[test]
    fn menu_observation_and_partial_capture_never_pass() {
        let request = Request {
            research_panel: ResearchPanelMode::Visible,
            local_camera: LocalCameraMode::Stock,
            save: "Lab".into(),
            checkpoint: "vacuum".into(),
            maximum_delta: 0.04,
            seconds: 10.0,
            settle_seconds: 2.0,
            warp: 1,
            atmosphere: false,
            resolution: None,
            no_shadows: false,
            no_aa: false,
            native_sample: false,
            gpu_timing_library: None,
            perturbation: "quiet".into(),
            pulse_delta_v: 0.25,
            callback_attribution: false,
            mechjeb_provider_plan: false,
            camera_chain: false,
            world_draw: None,
            hide_vessel: false,
            no_aero_fx: false,
            aero_quality: None,
            aero_batch: false,
            aero_field: false,
            aero_field_bundle: None,
            part_batch: false,
            part_mapped_only: false,
            part_reference: false,
            part_retained: false,
            part_shader_bundle: None,
            part_standard_fallback: false,
            screenshot: false,
            entry_target_altitude: None,
        };
        assert!(analyze(
            &request,
            "run",
            1,
            &json!({"status":"complete"}),
            &json!({}),
            &json!({})
        )
        .is_err());
    }
    #[test]
    fn entry_evidence_prefers_profile_and_retains_loader_failures() {
        let directory = std::env::temp_dir().join(format!(
            "continuum-entry-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let loader = directory.join("scale-loader-owned");
        let profile = directory.join("scale-profile-owned");
        fs::create_dir_all(&loader).unwrap();
        let evidence = json!({"schema":"continuum-flight-entry/v1","runId":"run","processId":123,"status":"invalid","reason":"early-load-failure"});
        write_new(&loader.join("entry.json"), &evidence).unwrap();
        assert_eq!(
            entry_directory(&directory, "run", 123).unwrap(),
            Some(loader.clone())
        );
        fs::create_dir_all(&profile).unwrap();
        write_new(&profile.join("entry.json"), &evidence).unwrap();
        assert_eq!(
            entry_directory(&directory, "run", 123).unwrap(),
            Some(profile.clone())
        );
        assert!(entry_directory(&directory, "other-run", 123)
            .unwrap()
            .is_none());
        assert!(entry_directory(&directory, "run", 456).unwrap().is_none());
        write_new(&directory.join("flight-view.json"),&json!({"schema":"continuum-flight-view/v1","runId":"run","processId":123,"status":"captured-after-window","width":1,"height":1})).unwrap();
        assert!(validate_view(&directory, "run", 123).is_err());
        assert!(validate_view(&directory, "foreign-run", 123).is_err());
        let failed_batch = json!({"schema":"continuum-flight-aero-batch/v1","runId":"run","processId":123,"status":"stock-fallback","reason":"preparation-rejected"});
        write_new(&profile.join("aero-batch.json"), &failed_batch).unwrap();
        let recovered = directory.join("recovered");
        fs::create_dir_all(&recovered).unwrap();
        recover_failed_aero_batch(&directory, "foreign-run", 123, &recovered).unwrap();
        assert!(!recovered.join("aero-batch.json").exists());
        recover_failed_aero_batch(&directory, "run", 123, &recovered).unwrap();
        assert_eq!(
            read::<Value>(&recovered.join("aero-batch.json")).unwrap(),
            failed_batch
        );
        recover_failed_aero_batch(&directory, "run", 123, &recovered).unwrap();
        let failed_admission = json!({"schema":"continuum-flight-admission/v1","runId":"run","processId":123,"status":"invalid","reason":"admission-wall-timeout","globalsReady":false,"loaded":true,"normalAtmosphere":false,"situation":"ORBITING","eligible":false});
        write_new(&profile.join("flight-admission.json"), &failed_admission).unwrap();
        recover_failed_admission(&directory, "foreign-run", 123, &recovered).unwrap();
        recover_failed_admission(&directory, "run", 456, &recovered).unwrap();
        assert!(!recovered.join("flight-admission.json").exists());
        recover_failed_admission(&directory, "run", 123, &recovered).unwrap();
        assert_eq!(read::<Value>(&recovered.join("flight-admission.json")).unwrap(), failed_admission);
        let duplicate = directory.join("scale-profile-duplicate");
        fs::create_dir_all(&duplicate).unwrap();
        write_new(&duplicate.join("entry.json"), &evidence).unwrap();
        assert!(entry_directory(&directory, "run", 123).is_err());
        fs::remove_dir_all(directory).unwrap();
    }
    #[test]
    fn timed_window_excludes_warmup_and_straddlers_and_accepts_normal_high_mode() {
        let request = Request {
            research_panel: ResearchPanelMode::Visible,
            local_camera: LocalCameraMode::Stock,
            save: "Lab".into(),
            checkpoint: "vacuum".into(),
            maximum_delta: 0.04,
            seconds: 0.1,
            settle_seconds: 2.0,
            warp: 1,
            atmosphere: false,
            resolution: None,
            no_shadows: false,
            no_aa: false,
            native_sample: false,
            gpu_timing_library: None,
            perturbation: "quiet".into(),
            pulse_delta_v: 0.25,
            callback_attribution: false,
            mechjeb_provider_plan: false,
            camera_chain: false,
            world_draw: None,
            hide_vessel: false,
            no_aero_fx: false,
            aero_quality: None,
            aero_batch: false,
            aero_field: false,
            aero_field_bundle: None,
            part_batch: false,
            part_mapped_only: false,
            part_reference: false,
            part_retained: false,
            part_shader_bundle: None,
            part_standard_fallback: false,
            screenshot: false,
            entry_target_altitude: None,
        };
        let frame = |id| json!({"contextFrame":id,"contextAligned":true,"scene":"FLIGHT","paused":false,"packed":false,"loaded":true,"vesselId":"vessel","parts":16,"rigidbodies":16,"joints":15,"colliders":16,"loadedVessels":1,"warpRate":1.0,"warpMode":"HIGH","fixedDeltaSeconds":0.02,"maximumDeltaSeconds":0.04,"physicsFrameLimitSeconds":0.04,"wallMilliseconds":50.0,"fixedCallbacksPerFrame":2,"altitudeMeters":100000.0,"atmosphereDepthMeters":70000.0});
        let scopes:Vec<_> = ["UnityEngine.PlayerLoop.FixedUpdate","UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate","UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate","UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate","UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate"].iter().map(|name|json!({"name":name,"status":"observed","droppedSamples":0,"sequenceErrors":0,"samples":[{"frame":0,"startedTicks":1,"endedTicks":5,"elapsedTicks":4},{"frame":1,"startedTicks":95,"endedTicks":105,"elapsedTicks":10},{"frame":2,"startedTicks":120,"endedTicks":121,"elapsedTicks":1}]})).collect();
        let mut report = json!({"schema":"ksp-continuum-markers/v2","status":"complete","captureMode":"simulation-duration","completionReason":"simulation-duration-reached","runId":"run","completedFrames":2,"requestedSimulatedSeconds":0.1,"actualSimulatedSeconds":0.1,"captureWallSeconds":0.1,"vSyncCount":0,"targetFrameRate":-1,"captureStartClockTimestamp":100,"captureEndClockTimestamp":200,"clockFrequency":1000,"frames":[frame(1),frame(2)],"playerLoop":{"schema":"ksp-continuum-playerloop/v2","status":"observed","integrityStatus":"verified-at-boundaries","cleanupStatus":"removed-owned-hooks","clockFrequency":1000,"scopes":scopes}});
        report["researchPanel"]=json!({"schema":"continuum-research-panel/v1","status":"complete","mode":"visible","startVisible":true,"endVisible":true,"startComponentPresent":true,"endComponentPresent":true,
            "captureStartFrame":1,"captureEndFrame":3,"captureStartClockTimestamp":100,"captureEndClockTimestamp":200,"clockFrequency":1000,"modeChanges":0,"errors":0,"serviceUpdateCalls":2,"serviceFixedCalls":1,
            "guiCalls":2,"layoutCalls":1,"repaintCalls":1,"otherCalls":0,"guiTicks":2,"layoutTicks":1,"repaintTicks":1,"otherTicks":0});
        let start = json!({"schema":"continuum-flight-capture-signal/v1","status":"capturing","runId":"run","processId":1,"clockFrequency":1000,"clockTimestamp":100,"universalTime":1.0});
        let end = json!({"schema":"continuum-flight-capture-signal/v1","status":"complete","runId":"run","processId":1,"clockFrequency":1000,"clockTimestamp":200,"universalTime":1.1});
        let summary = analyze(&request, "run", 1, &report, &start, &end).unwrap();
        let fixed = &summary["playerLoop"]["UnityEngine.PlayerLoop.FixedUpdate"];
        assert_eq!(fixed["totalMilliseconds"], 1.0);
        assert_eq!(fixed["omittedStraddlers"], 1);
        assert_eq!(summary["nonOverlappingEnclosedMilliseconds"], 3.0);
        for (name, ticks) in [
            ("UnityEngine.PlayerLoop.EarlyUpdate", 2),
            ("UnityEngine.PlayerLoop.PreUpdate", 4),
            ("UnityEngine.PlayerLoop.PreLateUpdate", 3),
        ] {
            report["playerLoop"]["scopes"].as_array_mut().unwrap().push(json!({"name":name,"status":"observed","droppedSamples":0,"sequenceErrors":0,
                "samples":[{"frame":2,"startedTicks":120,"endedTicks":120+ticks,"elapsedTicks":ticks}]}));
        }
        let with_parents = analyze(&request, "run", 1, &report, &start, &end).unwrap();
        assert_eq!(with_parents["nonOverlappingEnclosedMilliseconds"], 11.0);
        assert_eq!(
            with_parents["optionalPhaseAccounting"]["preLatePhaseUsesParent"],
            true
        );
        assert_eq!(with_parents["unattributedWallMilliseconds"], 89.0);
        report["actualSimulatedSeconds"] = json!(0.09);
        assert!(analyze(&request, "run", 1, &report, &start, &end).is_err());
        report["actualSimulatedSeconds"] = json!(0.1);
        report["frames"][1]["contextFrame"] = json!(3);
        assert!(analyze(&request, "run", 1, &report, &start, &end).is_err());
    }
}

/// Sampling owns only the process created by this laboratory run. Polling never
/// waits for the sampler while the game is capturing.
pub struct NativeSample {
    requested: bool,
    attempted: bool,
    child: Option<std::process::Child>,
    receipt: Value,
}
impl NativeSample {
    pub fn new(requested: bool) -> Self {
        Self {
            requested,
            attempted: false,
            child: None,
            receipt: json!({"requested":requested,"status":if requested {"waiting-for-owned-capture"} else {"not-requested"},
                "perturbation":"Native sampling attaches to the owned game process; this variant is diagnostic and unsuitable as an unprofiled performance comparison.",
                "durationSeconds":5,"intervalMilliseconds":1,"captureDetectionResolutionSeconds":0.1}),
        }
    }
    pub fn poll(&mut self, directory: &Path, run_id: &str, pid: u32, output: &Path) {
        if !self.requested {
            return;
        }
        if let Some(child) = self.child.as_mut() {
            match child.try_wait() {
                Ok(Some(status)) => {
                    self.receipt["samplerExitCode"] = json!(status.code());
                    self.receipt["status"] = json!(if status.success() {
                        "completed"
                    } else {
                        "failed"
                    });
                    self.receipt["samplerEndUnixSeconds"] = json!(unix_seconds());
                    if self.receipt["psEnd"].is_null() {
                        self.receipt["psEnd"] = owned_ps(pid);
                    }
                    self.child = None;
                }
                Ok(None) => {}
                Err(error) => {
                    self.receipt["error"] = json!(error.to_string());
                }
            }
            return;
        }
        if self.attempted {
            return;
        }
        let capture = match capture_directory(directory, run_id, pid) {
            Ok(Some(path)) => path,
            Ok(None) => return,
            Err(error) => {
                self.attempted = true;
                self.receipt["status"] = json!("failed");
                self.receipt["error"] = json!(error);
                return;
            }
        };
        self.attempted = true;
        self.receipt["ownedPid"] = json!(pid);
        self.receipt["captureStartSignal"] =
            read::<Value>(&capture.join("capture-start.json")).unwrap_or(Value::Null);
        if capture.join("capture-end.json").exists() {
            self.receipt["status"] = json!("capture-ended-before-sampling");
            return;
        }
        let result = (|| -> Result<std::process::Child> {
            let log = fs::File::create(output.join("native-flight-sample-process.log"))
                .map_err(|error| error.to_string())?;
            let stderr = log.try_clone().map_err(|error| error.to_string())?;
            let file = output.join("native-flight-sample.txt");
            self.receipt["samplerBeginUnixSeconds"] = json!(unix_seconds());
            self.receipt["psBegin"] = owned_ps(pid);
            std::process::Command::new("/usr/bin/sample")
                .args([
                    pid.to_string(),
                    "5".into(),
                    "1".into(),
                    "-file".into(),
                    file.to_string_lossy().into_owned(),
                ])
                .stdout(std::process::Stdio::from(log))
                .stderr(std::process::Stdio::from(stderr))
                .spawn()
                .map_err(|error| error.to_string())
        })();
        match result {
            Ok(child) => {
                self.receipt["samplerPid"] = json!(child.id());
                self.receipt["status"] = json!("sampling");
                self.child = Some(child);
            }
            Err(error) => {
                self.receipt["status"] = json!("failed");
                self.receipt["error"] = json!(error);
            }
        }
    }
    pub fn finish(mut self, pid: u32, output: &Path) -> Value {
        if let Some(mut child) = self.child.take() {
            match child.try_wait() {
                Ok(Some(status)) => {
                    self.receipt["status"] = json!(if status.success() {
                        "completed"
                    } else {
                        "failed"
                    });
                    self.receipt["samplerExitCode"] = json!(status.code());
                }
                _ => {
                    let killed = child.kill().map_err(|error| error.to_string());
                    let joined = child
                        .wait()
                        .map(|status| status.code())
                        .map_err(|error| error.to_string());
                    self.receipt["status"] = json!("interrupted-owned-game-ended");
                    self.receipt["stopResult"] = json!(killed);
                    self.receipt["joinResult"] = json!(joined);
                }
            }
            self.receipt["samplerEndUnixSeconds"] = json!(unix_seconds());
        }
        if self.requested {
            if self.receipt["psEnd"].is_null() {
                self.receipt["psEnd"] = owned_ps(pid);
            }
            self.receipt["outputSha256"] =
                json!(hash(&output.join("native-flight-sample.txt")).ok());
            self.receipt["psScope"]=json!("Raw process snapshots only; end may be unavailable after owned game exit. Percent CPU is ps process accounting, not capture-window exclusive CPU.");
        }
        std::mem::take(&mut self.receipt)
    }
}
impl Drop for NativeSample {
    fn drop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}
fn unix_seconds() -> f64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|time| time.as_secs_f64())
        .unwrap_or(0.0)
}
fn owned_ps(pid: u32) -> Value {
    match std::process::Command::new("/bin/ps")
        .args(["-p", &pid.to_string(), "-o", "pid=,pcpu=,rss=,etime="])
        .output()
    {
        Ok(output) => {
            json!({"unixSeconds":unix_seconds(),"exitCode":output.status.code(),"raw":String::from_utf8_lossy(&output.stdout)})
        }
        Err(error) => json!({"unixSeconds":unix_seconds(),"error":error.to_string()}),
    }
}

pub fn local_camera_summary(request: &Request, camera: &Value, report: &Value, run_id: &str, pid: u32) -> Result<Value> {
    let frames = report["frames"].as_array().filter(|frames| !frames.is_empty()).ok_or("local camera capture frames absent")?;
    let first = frames[0]["contextFrame"].as_u64().ok_or("local camera first frame absent")?;
    let after_last = frames.last().unwrap()["contextFrame"].as_u64().and_then(|id| id.checked_add(1)).ok_or("local camera final frame invalid")?;
    let parts = frames[0]["parts"].as_u64().filter(|value| *value > 0).ok_or("local camera captured parts absent")?;
    if frames[0]["vesselId"].as_str().filter(|value| !value.is_empty()).is_none() { return Err("local camera captured vessel absent".into()); }
    if camera["schema"] != "continuum-flight-local-camera/v1" || camera["mode"] != request.local_camera.as_str() || camera["runId"] != run_id
        || camera["processId"].as_u64() != Some(pid as u64) || camera["vesselId"] != frames[0]["vesselId"] || camera["parts"].as_u64() != Some(parts) {
        return Err("local camera declared identity differs".into());
    }
    if request.local_camera == LocalCameraMode::Stock && !request.local_camera_applicable() {
        if camera["status"] != "not-applicable" || camera["reason"] != "declared-unsupported-stock-workload"
            || camera["cleanupStatus"] != "not-applicable-no-owned-hooks-or-mutations" || camera["hooksRemoved"].as_bool() != Some(true) || camera["restorationComplete"].as_bool() != Some(true)
            || !camera["camera00"].is_null() || !camera["camera01"].is_null() { return Err("unsupported stock camera fallback did not abstain cleanly".into()); }
        for key in ["faults","observedFrames","coverageChecks","observerTicks","windowStartFrame","windowEndFrame","windowStartClockTimestamp","windowEndClockTimestamp"] {
            if camera[key].as_u64() != Some(0) { return Err("unsupported stock camera acquired ownership or observations".into()); }
        }
        return Ok(json!({"report":camera,"proof":"not-observed-declared-unsupported-stock-workload","scope":"Stock rendering unchanged; local-camera diagnostic not applicable to declared workload."}));
    }
    if camera["schema"] != "continuum-flight-local-camera/v1" || camera["mode"] != request.local_camera.as_str()
        || camera["runId"] != run_id || camera["processId"].as_u64() != Some(pid as u64) || camera["vesselId"] != frames[0]["vesselId"]
        || camera["parts"].as_u64() != Some(parts)
        || camera["status"] != "completed-experimental" || camera["faults"].as_u64() != Some(0)
        || camera["globalsUnchanged"].as_bool() != Some(true)
        || camera["hooksRemoved"].as_bool() != Some(true) || camera["restorationComplete"].as_bool() != Some(true)
        || camera["cleanupStatus"] != "removed-owned-hooks-restored-original-camera-state"
        || camera["windowStartClockTimestamp"] != report["captureStartClockTimestamp"] || camera["windowEndClockTimestamp"] != report["captureEndClockTimestamp"]
        || camera["clockFrequency"] != report["clockFrequency"] || camera["windowStartFrame"].as_u64() != Some(first) || camera["windowEndFrame"].as_u64() != Some(after_last)
        || camera["observedFrames"].as_u64().unwrap_or(0) == 0 || camera["coverageChecks"].as_u64().unwrap_or(0) == 0 {
        return Err("local camera owned lifecycle/window proof absent or invalid".into());
    }
    for key in ["originalLocalCameraPosBits","originalLocalCameraDirBits","endLocalCameraPosBits","endLocalCameraDirBits"] {
        let bits = camera[key].as_array().filter(|bits| bits.len() == 4).ok_or("local camera fog bit vector absent")?;
        let w = if key.contains("Pos") { 0x7f800000 } else { 0 };
        if bits.iter().enumerate().any(|(index,value)| value.as_i64() != Some(if index == 3 { w } else { 0x3f800000 })) {
            return Err("local camera inactive fog global changed or not at pinned defaults".into());
        }
    }
    let zero = request.local_camera == LocalCameraMode::Merged;
    let mut ids = Vec::new();
    for (index, name) in [(0,"camera00"),(1,"camera01")] {
        let row = &camera[name]; let id = row["instanceId"].as_i64().filter(|value| *value != 0).ok_or("local camera identity absent")?;
        if ids.contains(&id) || row["flightCameraIndex"].as_u64() != Some(index) || row["name"].as_str().filter(|name| !name.is_empty()).is_none()
            || row["componentTypes"].as_array().filter(|rows| !rows.is_empty() && rows.iter().all(|value| value.as_str().is_some())).is_none()
            || row["commandNames"].as_array().filter(|rows| rows.iter().all(|value| value.as_str().is_some())).is_none() { return Err("local camera inventory identity invalid".into()); }
        ids.push(id);
        for key in ["originalProjection","effectiveProjection","restoredProjection"] {
            let projection=row[key].as_array().filter(|values| values.len()==16).ok_or("local camera actual projection absent")?;
            if projection.iter().any(|value| value.as_f64().filter(|value| value.is_finite()).is_none()) { return Err("local camera projection nonfinite".into()); }
        }
        if row["originalProjection"] != row["restoredProjection"] { return Err("local camera projection not restored".into()); }
        for axis in 0..16 {
            if (!zero || index==1 || (axis!=10 && axis!=14)) && row["originalProjection"][axis] != row["effectiveProjection"][axis] { return Err("local camera projection changed outside far-depth entries".into()); }
        }
        for state_name in ["original","effective","restored"] {
            let state = &row[state_name];
            for field in ["enabled","activeSelf","activeInHierarchy","isPQS"] { if state[field].as_bool().is_none() { return Err("local camera state boolean absent".into()); } }
            for field in ["cullingMask","clearFlags","depthTextureMode","renderingPath","targetId"] { if state[field].as_i64().is_none() { return Err("local camera integer state absent".into()); } }
            for field in ["nearClip","farClip","fieldOfView","depth"] { finite(state,field)?; }
            if finite(state,"nearClip")? <= 0.0 || finite(state,"farClip")? <= finite(state,"nearClip")? || finite(state,"fieldOfView")? <= 0.0 || finite(state,"fieldOfView")? >= 180.0 { return Err("local camera clipping/FOV invalid".into()); }
        }
        for field in ["enabled","activeSelf","activeInHierarchy"] { if row["original"][field] != true { return Err("stock local camera was not active at admission".into()); } }
        if row["original"]["isPQS"].as_bool() != Some(index == 1) || row["original"]["targetId"].as_i64() != Some(0) { return Err("stock local camera fog/screen ownership differs".into()); }
        if row["original"] != row["restored"] { return Err("local camera original state not restored exactly".into()); }
        let mut expected = row["original"].clone();
        if zero {
            if index == 0 {
                if finite(&row["effective"],"farClip")? != 750000.0 { return Err("merged camera far clip differs".into()); }
                expected["farClip"] = row["effective"]["farClip"].clone(); expected["isPQS"] = json!(true);
            }
            else { expected["activeSelf"] = json!(false); expected["activeInHierarchy"] = json!(false); expected["isPQS"] = json!(false); }
        }
        if row["effective"] != expected { return Err("local camera changed undeclared state or missed requested treatment".into()); }
        let before = row["preCullCount"].as_u64().ok_or("local camera preCull witness absent")?;
        let pre = row["preRenderCount"].as_u64().ok_or("local camera preRender witness absent")?;
        let after = row["postRenderCount"].as_u64().ok_or("local camera postRender witness absent")?;
        if before != pre || before != after || (zero && index == 1 && before != 0) || ((!zero || index == 0) && before == 0) { return Err("local camera native callback ownership incomplete".into()); }
    }
    let frequency = camera["clockFrequency"].as_u64().filter(|value| *value > 0).ok_or("local camera clock invalid")? as f64;
    if finite(camera,"maxProjectionError")? < 0.0 || finite(camera,"maxProjectionError")? > 0.00001 { return Err("local camera native projection guard differs".into()); }
    let ticks = camera["observerTicks"].as_u64().ok_or("local camera observer cost absent")?;
    let start = camera["windowStartClockTimestamp"].as_u64().ok_or("local camera start timestamp absent")?;
    camera["windowEndClockTimestamp"].as_u64().filter(|value| *value > start).ok_or("local camera end timestamp invalid")?;
    Ok(json!({"report":camera,"observerMilliseconds":ticks as f64*1000.0/frequency,"observerAmortizedMillisecondsPerCompletedFrame":ticks as f64*1000.0/frequency/frames.len() as f64,
        "observerCostScope":"Lifecycle and capture audit aggregate, amortized over completed capture frames; not exact capture-window exclusive CPU.",
        "scope":"Explicit local-camera draw counterfactual with same stock computation; whole-flight wall timings include observer. No GPU duration or visual-equivalence claim."}))
}
fn validate_panel_controls(request: &Request, controls: &Value) -> Result<()> {
    let visible = request.research_panel == ResearchPanelMode::Visible;
    if controls["researchPanelMode"] != request.research_panel.as_str()
        || controls["researchPanelVisible"].as_bool() != Some(visible)
        || controls["researchPanelComponentPresent"].as_bool() != Some(visible) {
        return Err("research panel control mode/effective visibility differs".into());
    }
    Ok(())
}
fn validate_frozen_camera(controls: &Value, end: bool) -> Result<()> {
    if controls["cameraFrozen"] != true
        || !controls["cameraContract"].as_str().unwrap_or("").starts_with("continuum-root-relative-camera/v1:")
        || !controls["cameraFailure"].is_null()
        || controls["cameraDistance"].as_f64() != Some(50.0)
        || controls["cameraFieldOfView"].as_f64() != Some(60.0)
        || (end && controls["cameraObservedInvocations"].as_u64().unwrap_or(0) == 0)
    {
        return Err("frozen camera contract or observation absent".into());
    }
    for (field, limit) in [("cameraMaxPositionErrorMeters", 0.005), ("cameraMaxAngleErrorDegrees", 0.005)] {
        let error = controls[field].as_f64().ok_or("camera pose error absent")?;
        if !error.is_finite() || error < 0.0 || error > limit {
            return Err(format!("frozen camera {field} outside tolerance"));
        }
    }
    Ok(())
}

pub fn validate_controls(
    request: &Request,
    run_id: &str,
    pid: u32,
    output: &Path,
    report: &Value,
) -> Result<()> {
    let aero_treatment = request.no_aero_fx || request.aero_quality.is_some();
    let controls_path = output.join("diagnostic-controls.json");
    if controls_path.exists() {
        let controls: Value = read(&controls_path)?;
        if controls["schema"] != "continuum-flight-controls/v1"
            || controls["runId"] != run_id
            || controls["vSyncCount"] != 0
            || controls["targetFrameRate"] != -1
        {
            return Err("diagnostic control evidence identity or effective controls differ".into());
        }
        validate_frozen_camera(&controls, false)?;
        validate_panel_controls(request, &controls)?;
        if aero_treatment {
            if controls["phase"] != "capture-start" {
                return Err("aero start control phase differs".into());
            }
            validate_aero_boundary(request, &controls)?;
        }
    } else {
        return Err("requested diagnostic control evidence absent".into());
    }
    let end_path = output.join("diagnostic-controls-end.json");
    if end_path.exists() {
        let end: Value = read(&end_path)?;
        if end["schema"] != "continuum-flight-controls/v1"
            || end["runId"] != run_id
            || end["phase"] != "capture-end"
        {
            return Err("end diagnostic control evidence identity differs".into());
        }
        validate_frozen_camera(&end, true)?;
        validate_panel_controls(request, &end)?;
        if aero_treatment {
            validate_aero_boundary(request, &end)?;
        }
        if request.hide_vessel {
            let start: Value = read(&controls_path)?;
            let count = start["hiddenVesselRenderers"]
                .as_u64()
                .ok_or("hidden renderer admission count absent")?;
            if start["phase"] != "capture-start"
                || count == 0
                || end["hiddenVesselRenderers"].as_u64() != Some(count)
                || end["stillHiddenVesselRenderers"].as_u64() != Some(count)
            {
                return Err("vessel renderer suppression boundary witness incomplete".into());
            }
        }
    } else {
        return Err("requested rendering treatment end witness absent".into());
    }
    let batch_path = output.join("aero-batch.json");
    if batch_path.exists() {
        let batch: Value = read(&batch_path)?;
        if batch["runId"] != run_id || batch["processId"].as_u64() != Some(pid as u64) {
            return Err("aero batch lifecycle evidence identity differs".into());
        }
        if request.aero_batch {
            validate_batch(&batch, run_id, pid, &report["frames"][0]["vesselId"])?;
        }
    } else if request.aero_batch {
        return Err("requested aero batch lifecycle evidence absent".into());
    }
    if request.aero_field {
        let field: Value = read(&output.join("aero-field.json"))?;
        validate_aero_field(request, &field, run_id, pid, &report["frames"][0]["vesselId"])?;
    }
    if request.camera_chain {
        let chain: Value = read(&output.join("camera-chain.json"))?;
        let summary = camera_chain_summary(&chain, report, run_id, pid)?;
        write_new(&output.join("camera-chain-summary.json"), &summary)?;
    }
    if let Some(mode) = &request.world_draw {
        let world: Value = read(&output.join("world-draw.json"))?;
        validate_world_draw(&world, report, run_id, pid, mode)?;
    }
    let part_path = output.join("part-batch.json");
    if request.part_batch {
        let part: Value = read(&part_path)?;
        validate_part_batch(request, &part, run_id, pid, &report["frames"][0]["vesselId"])?;
        if part["captureStartTicks"] != report["captureStartClockTimestamp"] || part["captureEndTicks"] != report["captureEndClockTimestamp"] || part["clockFrequency"] != report["clockFrequency"] {
            return Err("part cost window differs from full flight capture".into());
        }
        let start_frame = part["captureStartFrame"].as_u64().ok_or("part start frame absent")?;
        if part["captureEndFrame"].as_u64().filter(|frame| *frame >= start_frame).is_none() { return Err("part frame bounds invalid".into()); }
        for key in ["windowPublicationTicks", "windowMainCameraTicks", "windowRestorationTicks", "windowPropertyBlockCaptures", "windowProbeCalls", "windowUpdateCalls", "windowPropertyBlockTicks", "windowProbeTicks", "windowMatrixBoundsTicks"] {
            if part[key].as_u64().is_none() { return Err(format!("part window counter absent or invalid: {key}")); }
            if request.part_reference && part[key].as_u64() != Some(0) { return Err("reference part rendering unexpectedly performed hot work".into()); }
        }
        if !request.part_reference && part["windowUpdateCalls"].as_u64().unwrap_or(0) == 0 { return Err("replacement part publication not observed in capture".into()); }
    }
    if request.screenshot {
        validate_view(output, run_id, pid)?;
    }
    let path = output.join("perturbation.json");
    if !path.exists() {
        return if request.perturbation == "quiet" {
            Ok(())
        } else {
            Err("requested pulse evidence absent".into())
        };
    }
    let pulse: Value = read(&path)?;
    if pulse["schema"] != "continuum-flight-perturbation/v1"
        || pulse["runId"] != run_id
        || pulse["processId"].as_u64() != Some(pid as u64)
        || pulse["mode"] != request.perturbation
        || pulse["status"] != "complete"
    {
        return Err("perturbation evidence identity, mode, or completion differs".into());
    }
    let nested = &report["perturbation"];
    for key in ["mode", "status", "appliedCount"] {
        if pulse[key] != nested[key] {
            return Err("retained pulse and marker evidence differ".into());
        }
    }
    if request.perturbation == "quiet" {
        if pulse["appliedCount"].as_u64() != Some(0) {
            return Err("quiet run applied a perturbation".into());
        }
        return Ok(());
    }
    let count = pulse["appliedCount"].as_u64().ok_or("pulse count absent")?;
    let duration = finite(&pulse, "requestedDurationSeconds")?;
    let applied_duration = finite(&pulse, "appliedDurationSeconds")?;
    let force = finite(&pulse, "requestedForceUnityUnits")?;
    let mass = finite(&pulse, "targetMassUnityUnits")?;
    let delta_v = finite(&pulse, "nominalDeltaVMetersPerSecond")?;
    let requested_impulse = finite(&pulse, "requestedImpulseUnityUnits")?;
    let applied_impulse = finite(&pulse, "appliedImpulseUnityUnits")?;
    if count == 0
        || duration <= 0.0
        || force <= 0.0
        || mass <= 0.0
        || (delta_v - request.pulse_delta_v).abs() > request.pulse_delta_v * 1e-6
        || requested_impulse <= 0.0
        || applied_impulse <= 0.0
        || (duration - applied_duration).abs() > 1e-5
        || (force * duration - requested_impulse).abs() > requested_impulse * 1e-4
        || (mass * request.pulse_delta_v - requested_impulse).abs() > requested_impulse * 1e-4
        || (requested_impulse - applied_impulse).abs() > requested_impulse * 1e-4
    {
        return Err("pulse duration or mass-scaled impulse coverage incomplete".into());
    }
    Ok(())
}

pub fn retain_entry(
    directory: &Path,
    run_id: &str,
    pid: u32,
    output: &Path,
) -> Result<Option<Value>> {
    let Some(directory) = entry_directory(directory, run_id, pid)? else {
        return Ok(None);
    };
    let source = directory.join("entry.json");
    let before = hash(&source)?;
    let value: Value = read(&source)?;
    if hash(&source)? != before {
        return Err("entry evidence changed while retaining".into());
    }
    write_new(&output.join("flight-entry.json"), &value)?;
    for name in ["status.txt", "error.txt"] {
        let path = directory.join(name);
        if path.exists() {
            let before = hash(&path)?;
            fs::copy(&path, output.join(format!("flight-entry-{name}")))
                .map_err(|error| error.to_string())?;
            if hash(&path)? != before {
                return Err("entry status changed while retaining".into());
            }
        }
    }
    Ok(Some(json!({"sha256":before,"report":value})))
}

pub fn validate_entry(
    request: &Request,
    run_id: &str,
    pid: u32,
    retained: Option<&Value>,
    report: &Value,
    inputs: &Value,
) -> Result<()> {
    let Some(target) = request.entry_target_altitude else {
        return Ok(());
    };
    let entry = &retained.ok_or("requested entry approach evidence absent")?["report"];
    if entry["schema"] != "continuum-flight-entry/v1"
        || entry["runId"] != run_id
        || entry["processId"].as_u64() != Some(pid as u64)
        || entry["status"] != "capture-admitted"
        || (finite(entry, "targetAltitudeMeters")? - target).abs() > 1e-6
        || finite(entry, "admittedAltitudeMeters")? > target + 1.0
    {
        return Err("entry approach did not admit the requested deep-atmosphere capture".into());
    }
    let original = entry["originalParts"]
        .as_u64()
        .ok_or("entry original topology absent")?;
    if original == 0
        || entry["actualParts"].as_u64() != Some(original)
        || report["frames"][0]["parts"].as_u64() != Some(original)
        || entry["vesselId"] != report["frames"][0]["vesselId"]
    {
        return Err("entry vessel or original part topology changed before capture".into());
    }
    let suffix = format!("/saves/{}/{}.sfs", request.save, request.checkpoint);
    let sources: Vec<_> = inputs["files"]
        .as_object()
        .ok_or("flight input hashes absent")?
        .iter()
        .filter(|(path, _)| path.ends_with(&suffix))
        .collect();
    if sources.len() != 1 || &entry["sourceSha256"] != sources[0].1 {
        return Err("entry source differs from frozen checkpoint".into());
    }
    Ok(())
}

fn validate_aero_boundary(request: &Request, controls: &Value) -> Result<()> {
    let expected = request.aero_quality.unwrap_or(0) as u64;
    if controls["aeroFxQuality"].as_u64() != Some(expected) {
        return Err("effective aero-effects quality differs from treatment".into());
    }
    if request.no_aero_fx
        && (controls["aeroFxInstances"].as_u64().unwrap_or(0) == 0
            || controls["enabledAeroFxCameras"].as_u64() != Some(0)
            || controls["enabledAeroDepthCameras"].as_u64() != Some(0))
    {
        return Err("aero-effects suppression boundary witness incomplete".into());
    }
    Ok(())
}

fn entry_directory(directory: &Path, run_id: &str, pid: u32) -> Result<Option<PathBuf>> {
    match owned_sidecar_directory(directory, "scale-profile-", "entry.json", run_id, pid)? {
        Some(path) => Ok(Some(path)),
        None => owned_sidecar_directory(directory, "scale-loader-", "entry.json", run_id, pid),
    }
}

fn validate_batch(batch: &Value, run_id: &str, pid: u32, vessel: &Value) -> Result<()> {
    if batch["schema"] != "continuum-flight-aero-batch/v1"
        || batch["runId"] != run_id
        || batch["processId"].as_u64() != Some(pid as u64)
        || batch["vesselId"] != *vessel
        || vessel.as_str().map(str::is_empty).unwrap_or(true)
        || batch["status"] != "complete-experimental"
        || batch["faults"].as_u64() != Some(0)
        || batch["eligibleRenderers"].as_u64().unwrap_or(0) == 0
        || batch["batches"].as_u64().unwrap_or(0) == 0
        || batch["cameraInvocations"].as_u64().unwrap_or(0) == 0
        || batch["destroyedProxies"] != batch["batches"]
        || batch["cleanupStatus"]
            != "removed-owned-hooks-restored-owned-renderers-destruction-requested"
    {
        return Err("requested aero batch inactive, invalid, or cleanup incomplete".into());
    }
    let cameras = batch["cameras"]
        .as_array()
        .ok_or("aero batch camera lifecycle absent")?;
    for role in ["fx", "fx-depth", "velocity-occlusion"] {
        if !cameras
            .iter()
            .any(|camera| camera["role"] == role && camera["invocations"].as_u64().unwrap_or(0) > 0)
        {
            return Err(format!("aero batch camera role not exercised: {role}"));
        }
    }
    Ok(())
}
fn validate_aero_field(request: &Request, field: &Value, run_id: &str, pid: u32, vessel: &Value) -> Result<()> {
    let bundle = request.aero_field_bundle.as_ref().ok_or("field bundle declaration absent")?;
    if field["schema"] != "continuum-flight-aero-field/v1" || field["runId"] != run_id
        || field["processId"].as_u64() != Some(pid as u64) || &field["vesselId"] != vessel
        || field["bundlePath"].as_str() != bundle.to_str() || field["bundleSha256"] != hash(bundle)? {
        return Err("field renderer identity or shader bytes differ".into());
    }
    if field["status"] != "complete-experimental" || field["faults"] != 0
        || field["stockRestored"] != true || field["hooksRemoved"] != true || field["geometryRestored"] != true
        || field["pixelProofComplete"] != true || field["stockDepthPre"] != 0 || field["stockDepthPost"] != 0 {
        return Err("field renderer ownership, stock suppression or cleanup incomplete".into());
    }
    for key in ["viewPositiveSamples", "flowPositiveSamples", "scenePositiveSamples", "geometryPackets", "eligibleRenderers",
        "producerSnapshots", "sceneDepthCallbacks", "suppressedDepth", "geometryRestoredOriginals"] {
        if field[key].as_u64().unwrap_or(0) == 0 { return Err(format!("field renderer witness absent: {key}")); }
    }
    for key in ["nonFiniteSamples", "invalidNormalSamples", "invalidFlowSamples", "invalidSceneDepthSamples"] {
        if field[key] != 0 { return Err(format!("field renderer pixel proof rejected: {key}")); }
    }
    for (a, b) in [("coveragePre", "coveragePost"), ("flowPre", "flowPost"), ("compositorPre", "compositorPost"),
        ("createdTextures", "releasedTextures"), ("createdMaterials", "destroyedMaterials"),
        ("proofTemporaryCreated", "proofTemporaryReleased"), ("geometryPackets", "geometryDestroyedProxies")] {
        let actual = field[a].as_u64().ok_or(format!("field counter absent: {a}"))?;
        if actual == 0 || field[b].as_u64() != Some(actual) { return Err(format!("field counter mismatch: {a}/{b}")); }
    }
    for key in ["lastCoverageFrame", "lastFlowFrame", "lastSceneDepthFrame", "lastProducerFrame"] {
        if field[key].as_u64().is_none() || field[key] != field["lastCompositeFrame"] {
            return Err(format!("field final view is stale: {key}"));
        }
    }
    Ok(())
}

fn validate_part_batch(request: &Request, batch: &Value, run_id: &str, pid: u32, vessel: &Value) -> Result<()> {
    if batch["schema"] != "continuum-flight-part-batch/v1"
        || batch["runId"] != run_id
        || batch["processId"].as_u64() != Some(pid as u64)
        || batch["vesselId"] != *vessel
        || vessel.as_str().map(str::is_empty).unwrap_or(true)
        || batch["status"] != "complete-experimental"
        || batch["faults"].as_u64() != Some(0)
        || batch["eligibleRenderers"].as_u64().unwrap_or(0) == 0
        || batch["packets"].as_u64().unwrap_or(0) == 0
        || batch["cleanupStatus"] != "restored-owned-renderers-removed-owned-hooks-destruction-requested"
    {
        return Err("requested part batch inactive, invalid, or cleanup incomplete".into());
    }
    if batch["family"] != if request.part_mapped_only { "KSP/Emissive/Bumped Specular (Mapped)" } else { "supported-specular-families" }
        || batch["mode"] != if request.part_reference { "reference" } else { "replacement" } {
        return Err("part rendering family/mode differs from declared request".into());
    }
    if request.part_reference {
        for key in ["cameraInvocations", "drawSubmissions", "suppressedRenderers", "restoredOriginals"] {
            if batch[key].as_u64() != Some(0) { return Err("reference part rendering mutated incumbent ownership".into()); }
        }
    } else if batch["cameraInvocations"].as_u64().unwrap_or(0) == 0 || batch["drawSubmissions"].as_u64().unwrap_or(0) == 0 || batch["restoredOriginals"] != batch["eligibleRenderers"] {
        return Err("replacement part draws or restoration incomplete".into());
    }
    if batch["retainedProvider"].as_bool() != Some(request.part_retained) {
        return Err("retained renderer treatment differs from declared request".into());
    }
    if request.part_retained { validate_retained_part_batch(batch)?; }
    match batch["shaderSource"].as_str() {
        Some("custom-bundle") => {
            let bundle = request.part_shader_bundle.as_ref().ok_or("undeclared custom part shader bundle")?;
            if batch["bundleSha256"].as_str() != Some(hash(bundle)?.as_str()) {
                return Err("part shader bundle identity differs".into());
            }
        },
        Some("shipped-standard-diagnostic") if request.part_standard_fallback => {},
        _ => return Err("part shader source absent or fallback undeclared".into()),
    }
    Ok(())
}
fn validate_retained_part_batch(batch: &Value) -> Result<()> {
    let count = |key: &str| batch[key].as_u64().ok_or_else(|| format!("retained renderer counter missing or invalid: {key}"));
    let retired = count("retiredRenderers")?;
    let restored = count("restoredRetiredRenderers")?;
    let eligible = count("eligibleRenderers")?;
    let rigid = count("rigidRetainedSources")?;
    let dynamic = count("dynamicRetainedSources")?;
    let rigid_publications = count("sharedPartPoseSnapshots")?;
    let dynamic_publications = count("dynamicPoseSnapshots")?;
    let leases = count("animationCullingLeases")?;
    let restored_leases = count("restoredAnimationCullingLeases")?;
    if retired == 0 || retired > eligible || restored != retired
        || rigid.checked_add(dynamic) != Some(retired)
        || (rigid > 0 && rigid_publications == 0)
        || (dynamic > 0 && dynamic_publications == 0)
        || leases != restored_leases
        || !batch["maxInitialRecoveredPositionErrorMeters"].as_f64().is_some_and(|v| v.is_finite() && (0.0..=0.005).contains(&v)) {
        return Err("retained renderer ownership or shared pose publication incomplete".into());
    }
    Ok(())
}
fn validate_view(output: &Path, run_id: &str, pid: u32) -> Result<()> {
    let metadata: Value = read(&output.join("flight-view.json"))?;
    if metadata["schema"] != "continuum-flight-view/v1"
        || metadata["runId"] != run_id
        || metadata["processId"].as_u64() != Some(pid as u64)
        || metadata["status"] != "captured-after-window"
    {
        return Err("requested post-window screenshot identity or status differs".into());
    }
    let path = output.join("flight-view.png");
    let length = fs::metadata(&path)
        .map_err(|error| error.to_string())?
        .len();
    if !(24..=134217728).contains(&length) {
        return Err("flight PNG absent or outside size bound".into());
    }
    use std::io::Read;
    let mut header = [0u8; 24];
    fs::File::open(path)
        .map_err(|error| error.to_string())?
        .read_exact(&mut header)
        .map_err(|error| error.to_string())?;
    let width = u32::from_be_bytes(header[16..20].try_into().unwrap());
    let height = u32::from_be_bytes(header[20..24].try_into().unwrap());
    if &header[..8] != b"\x89PNG\r\n\x1a\n"
        || &header[12..16] != b"IHDR"
        || width == 0
        || height == 0
        || metadata["width"].as_u64() != Some(width as u64)
        || metadata["height"].as_u64() != Some(height as u64)
    {
        return Err("retained screenshot PNG header or dimensions differ".into());
    }
    Ok(())
}

pub fn recover_failed_aero_batch(
    directory: &Path,
    run_id: &str,
    pid: u32,
    output: &Path,
) -> Result<()> {
    recover_failed_render_batch(directory, run_id, pid, output, "aero-batch.json", "continuum-flight-aero-batch/v1")
}
pub fn recover_failed_aero_field(directory: &Path, run_id: &str, pid: u32, output: &Path) -> Result<()> {
    recover_failed_render_batch(directory, run_id, pid, output, "aero-field.json", "continuum-flight-aero-field/v1")?;
    if !output.join("aero-field-fixture").exists() {
        if let Some(owned) = owned_sidecar_directory(directory, "scale-profile-", "aero-field.json", run_id, pid)? {
            retain_aero_fixture(&owned, output)?;
        }
    }
    Ok(())
}
pub fn recover_failed_part_batch(directory: &Path, run_id: &str, pid: u32, output: &Path) -> Result<()> {
    recover_failed_render_batch(directory, run_id, pid, output, "part-batch.json", "continuum-flight-part-batch/v1")
}
pub fn recover_failed_admission(directory: &Path, run_id: &str, pid: u32, output: &Path) -> Result<()> {
    recover_failed_render_batch(directory, run_id, pid, output, "flight-admission.json", "continuum-flight-admission/v1")
}
pub fn recover_failed_local_camera(directory: &Path, run_id: &str, pid: u32, output: &Path) -> Result<()> {
    recover_failed_render_batch(directory, run_id, pid, output, "local-camera.json", "continuum-flight-local-camera/v1")
}
fn recover_failed_render_batch(directory: &Path, run_id: &str, pid: u32, output: &Path, name: &str, schema: &str) -> Result<()> {
    let destination = output.join(name);
    if destination.exists() {
        return Ok(());
    }
    let Some(directory) =
        owned_sidecar_directory(directory, "scale-profile-", name, run_id, pid)?
    else {
        return Ok(());
    };
    let source = directory.join(name);
    let before = hash(&source)?;
    let report: Value = read(&source)?;
    if report["schema"] != schema
        || report["runId"] != run_id
        || report["processId"].as_u64() != Some(pid as u64)
    {
        return Err("failed render batch sidecar identity or schema differs".into());
    }
    if hash(&source)? != before {
        return Err("failed render batch evidence changed while retaining".into());
    }
    write_new(&destination, &report)
}
