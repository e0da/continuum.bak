use crate::{hash, read, write_new, Result};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Request {
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
    pub perturbation: String,
    pub pulse_delta_v: f64,
    pub callback_attribution: bool,
    pub hide_vessel: bool,
    pub no_aero_fx: bool,
    pub aero_quality: Option<u32>,
    pub entry_target_altitude: Option<f64>,
}
impl Request {
    pub fn parse(options: &BTreeMap<String, String>) -> Result<Option<Self>> {
        if !options.keys().any(|key| key.starts_with("--flight-")) {
            return Ok(None);
        }
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
        Ok(Some(Self {
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
            perturbation: perturbation.to_string(),
            pulse_delta_v: number("--flight-pulse-delta-v", 0.25, 0.001, 100.0)?,
            callback_attribution: boolean("--flight-callback-attribution")?,
            hide_vessel: boolean("--flight-hide-vessel")?,
            no_aero_fx: boolean("--flight-no-aero-fx")?,
            aero_quality,
        }))
    }
    pub fn arguments(&self) -> Vec<String> {
        let mut args = vec![
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
        if self.hide_vessel {
            args.push("--continuum-flight-hide-vessel".into());
        }
        if self.no_aero_fx {
            args.push("--continuum-flight-no-aero-fx".into());
        }
        if let Some(quality) = self.aero_quality {
            args.push(format!("--continuum-flight-aero-quality={quality}"));
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
        Ok(
            json!({"schema":"continuum-flight-inputs/v1","request":self,"files":files,
            "scope":"selected-checkpoint-settings-and-GameData-DLLs; native-assets-and-bundles-guarded-by-process-receipt"}),
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
    owned_sidecar_directory(directory, "capture-start.json", run_id, pid)
}
fn owned_sidecar_directory(
    directory: &Path,
    sidecar: &str,
    run_id: &str,
    pid: u32,
) -> Result<Option<PathBuf>> {
    let mut found = None;
    for entry in fs::read_dir(directory).map_err(|error| error.to_string())? {
        let entry = entry.map_err(|error| error.to_string())?;
        if !entry
            .file_name()
            .to_string_lossy()
            .starts_with("scale-profile-")
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

    Ok(
        json!({"schema":"continuum-flight-profile-summary/v1","status":"capture-validated","runId":run_id,"request":request,
        "intrusiveDiagnosticsRequested":request.native_sample || request.callback_attribution,
        "vesselRenderingTreatmentRequested":request.hide_vessel,
        "aeroEffectsTreatmentRequested":request.no_aero_fx || request.aero_quality.is_some(),
        "aeroEffectsQualityRequested":request.aero_quality,
        "nonOverlappingEnclosedMilliseconds":scoped,
        "optionalPhaseAccounting":{"earlyUpdateMilliseconds":costs.get(early_name),"preUpdateMilliseconds":costs.get(pre_name),
            "preLateUpdateParentMilliseconds":costs.get(prelate_name),"preLatePhaseAccountedMilliseconds":prelate,
            "preLatePhaseUsesParent":costs.contains_key(prelate_name)},
        "comparisonEligibility":if request.native_sample || request.callback_attribution {"intrusive-diagnostic-perturbation; prohibited-clean-comparison-to-uninstrumented"} else {"unprofiled-flight-only-with-matched-perturbation-checkpoint-controls-and-declared-rendering-treatment"},
        "scope":"diagnostic-flight-capture-only-not-gameplay-qualification","frames":frames.len(),"wallSeconds":wall,"simulatedSeconds":simulation,
        "achievedSimulationRate":simulation/wall,"requestedRate":request.warp,"normalizedSimulationRate":simulation/wall/request.warp as f64,
        "meanFps":frames.len() as f64/sampled_wall,"frameMilliseconds":distribution(intervals)?,"fixedCallbacksPerRenderedFrame":distribution(steps)?,
        "playerLoop":scopes,"fullFrameMilliseconds":whole,"unattributedWallMilliseconds":whole-scoped,
        "renderPostLateParentMilliseconds":renderer,
        "fixedParentOtherMilliseconds":costs[fixed]-costs[native]-costs[managed],
        "physicsStepScope":"Fully enclosed PhysicsFixedUpdate invocations; owned FixedUpdate callbacks are separate coroutine-boundary cadence observations.",
        "effectiveFirstFrame":frames[0],"effectiveLastFrame":frames.last(),
        "accounting":"FixedUpdate parent contains native and managed children; use Fixed parent+script Update+PreLateUpdate parent (otherwise script Late)+PostLateUpdate parent+optional EarlyUpdate/PreUpdate once; PreLate and PostLate children overlap their parents. Missing enclosing phases remain in the remainder. Remainder is wall waiting/rendering/other work and overhead, not GPU duration or exclusive CPU attribution."}),
    )
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
        "diagnostic-controls.json",
        "diagnostic-controls-end.json",
        "perturbation.json",
        "render-census.json",
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

#[cfg(test)]
mod tests {
    use super::*;
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
            ("--flight-pulse-delta-v".into(), "25".into()),
        ]);
        let request = Request::parse(&options).unwrap().unwrap();
        assert!(request
            .arguments()
            .contains(&"--continuum-physics-warp-pressure".to_string()));
        assert!(!request
            .arguments()
            .contains(&"--continuum-native-boundary-bench".to_string()));
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
            perturbation: "quiet".into(),
            pulse_delta_v: 0.25,
            callback_attribution: false,
            hide_vessel: false,
            no_aero_fx: false,
            aero_quality: None,
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
    fn timed_window_excludes_warmup_and_straddlers_and_accepts_normal_high_mode() {
        let request = Request {
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
            perturbation: "quiet".into(),
            pulse_delta_v: 0.25,
            callback_attribution: false,
            hide_vessel: false,
            no_aero_fx: false,
            aero_quality: None,
            entry_target_altitude: None,
        };
        let frame = |id| json!({"contextFrame":id,"contextAligned":true,"scene":"FLIGHT","paused":false,"packed":false,"loaded":true,"vesselId":"vessel","parts":16,"rigidbodies":16,"joints":15,"colliders":16,"loadedVessels":1,"warpRate":1.0,"warpMode":"HIGH","fixedDeltaSeconds":0.02,"maximumDeltaSeconds":0.04,"physicsFrameLimitSeconds":0.04,"wallMilliseconds":50.0,"fixedCallbacksPerFrame":2,"altitudeMeters":100000.0,"atmosphereDepthMeters":70000.0});
        let scopes:Vec<_> = ["UnityEngine.PlayerLoop.FixedUpdate","UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate","UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate","UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate","UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate"].iter().map(|name|json!({"name":name,"status":"observed","droppedSamples":0,"sequenceErrors":0,"samples":[{"frame":0,"startedTicks":1,"endedTicks":5,"elapsedTicks":4},{"frame":1,"startedTicks":95,"endedTicks":105,"elapsedTicks":10},{"frame":2,"startedTicks":120,"endedTicks":121,"elapsedTicks":1}]})).collect();
        let mut report = json!({"schema":"ksp-continuum-markers/v2","status":"complete","captureMode":"simulation-duration","completionReason":"simulation-duration-reached","runId":"run","completedFrames":2,"requestedSimulatedSeconds":0.1,"actualSimulatedSeconds":0.1,"captureWallSeconds":0.1,"vSyncCount":0,"targetFrameRate":-1,"captureStartClockTimestamp":100,"captureEndClockTimestamp":200,"clockFrequency":1000,"frames":[frame(1),frame(2)],"playerLoop":{"schema":"ksp-continuum-playerloop/v2","status":"observed","integrityStatus":"verified-at-boundaries","cleanupStatus":"removed-owned-hooks","clockFrequency":1000,"scopes":scopes}});
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
        if aero_treatment {
            if controls["phase"] != "capture-start" {
                return Err("aero start control phase differs".into());
            }
            validate_aero_boundary(request, &controls)?;
        }
    } else if request.perturbation != "quiet" || request.hide_vessel || aero_treatment {
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
    } else if request.hide_vessel || aero_treatment {
        return Err("requested rendering treatment end witness absent".into());
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
    let Some(directory) = owned_sidecar_directory(directory, "entry.json", run_id, pid)? else {
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
