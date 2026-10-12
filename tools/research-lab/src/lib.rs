use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, fs, io::Read, path::Path};
pub mod admission;
pub mod checkpoint_reload;
pub mod convection_replacement;
pub mod cylinder_occlusion;
pub mod fixed_owner;
pub mod ordered_body_aero;
pub mod zero_native_force;
pub mod docking;
pub mod flight;
pub mod gpu_timing;
pub mod integrator_summary;
pub mod launch;
pub mod lighting;
pub mod live_revalidation;
pub mod owned_report;
pub mod play;
pub mod profile;
pub mod process_capture;
pub mod reference_program;
pub mod provider_proofs;
pub mod recovery;
pub mod startup;
pub mod update_callbacks;

pub type Result<T> = std::result::Result<T, String>;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct VariantManifest {
    pub schema: String,
    pub id: String,
    pub code_revision: String,
    pub binaries: BTreeMap<String, String>,
    pub assets: BTreeMap<String, String>,
    pub packages: BTreeMap<String, String>,
    pub engine: String,
    pub runtime: String,
    pub architecture: String,
    pub renderer: String,
    pub enabled_replacements: Vec<String>,
}
impl VariantManifest {
    pub fn validate(&self) -> Result<()> {
        if self.schema != "continuum-lab-variant/v1"
            || [
                &self.id,
                &self.code_revision,
                &self.engine,
                &self.runtime,
                &self.architecture,
                &self.renderer,
            ]
            .iter()
            .any(|v| v.trim().is_empty())
            || self.binaries.is_empty()
            || self.assets.is_empty()
            || self
                .binaries
                .iter()
                .chain(self.assets.iter())
                .chain(self.packages.iter())
                .any(|(k, v)| k.trim().is_empty() || !sha(v))
        {
            return Err("variant requires explicit code, binary/asset/package hashes, engine, runtime, architecture and renderer".into());
        }
        let unique = self
            .enabled_replacements
            .iter()
            .collect::<std::collections::BTreeSet<_>>();
        if unique.len() != self.enabled_replacements.len()
            || unique.iter().any(|v| v.trim().is_empty())
        {
            return Err("variant replacements must be unique nonempty identities".into());
        }
        Ok(())
    }
}
pub fn hash(path: &Path) -> Result<String> {
    let mut file = fs::File::open(path).map_err(|e| e.to_string())?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 65536];
    loop {
        let size = file.read(&mut buffer).map_err(|e| e.to_string())?;
        if size == 0 {
            break;
        }
        digest.update(&buffer[..size]);
    }
    Ok(format!("{:x}", digest.finalize()))
}
pub fn source_tree_hash(root: &Path) -> Result<String> {
    fn walk(root: &Path, directory: &Path, rows: &mut Vec<(String, String)>) -> Result<()> {
        for entry in fs::read_dir(directory).map_err(|e| e.to_string())? {
            let entry = entry.map_err(|e| e.to_string())?;
            let path = entry.path();
            let kind = entry.file_type().map_err(|e| e.to_string())?;
            if kind.is_symlink() {
                return Err("source project symlinks require explicit provenance".into());
            }
            if kind.is_dir() {
                if entry.file_name() != "bin" && entry.file_name() != "obj" {
                    walk(root, &path, rows)?;
                }
            } else if kind.is_file() {
                let name = path
                    .strip_prefix(root)
                    .map_err(|e| e.to_string())?
                    .to_str()
                    .ok_or("non-UTF8 project input")?
                    .to_owned();
                rows.push((name, hash(&path)?));
            }
        }
        Ok(())
    }
    let mut rows = Vec::new();
    walk(root, root, &mut rows)?;
    rows.sort();
    let mut digest = Sha256::new();
    for (path, content) in rows {
        digest.update((path.len() as u64).to_le_bytes());
        digest.update(path.as_bytes());
        digest.update(content.as_bytes());
    }
    Ok(format!("{:x}", digest.finalize()))
}
pub fn write_new(path: &Path, value: &impl Serialize) -> Result<()> {
    use std::io::Write;
    let bytes = serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?;
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|e| e.to_string())?;
    file.write_all(&bytes).map_err(|e| e.to_string())
}
pub fn read<T: for<'a> Deserialize<'a>>(path: &Path) -> Result<T> {
    serde_json::from_slice(&fs::read(path).map_err(|e| e.to_string())?).map_err(|e| e.to_string())
}
pub fn inventory(root: &Path) -> Result<Value> {
    let root = root.canonicalize().map_err(|e| e.to_string())?;
    if !root.join("KSP.app/Contents/MacOS/KSP").is_file() {
        return Err("instance lacks the macOS KSP player".into());
    }
    fn walk(root: &Path, directory: &Path, rows: &mut Vec<Value>) -> Result<()> {
        let mut entries = fs::read_dir(directory)
            .map_err(|e| e.to_string())?
            .collect::<std::result::Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        entries.sort_by_key(|e| e.file_name());
        for entry in entries {
            let path = entry.path();
            let kind = entry.file_type().map_err(|e| e.to_string())?;
            let relative = path
                .strip_prefix(root)
                .map_err(|e| e.to_string())?
                .to_string_lossy();
            if kind.is_symlink() {
                rows.push(json!({"path":relative,"kind":"symlink-not-followed","target":fs::read_link(&path).map_err(|e|e.to_string())?}));
            } else if kind.is_dir() {
                walk(root, &path, rows)?;
            } else if kind.is_file() {
                rows.push(json!({"path":relative,"kind":"file","bytes":entry.metadata().map_err(|e|e.to_string())?.len(),"sha256":hash(&path)?}));
            }
        }
        Ok(())
    }
    let mut files = Vec::new();
    walk(&root, &root, &mut files)?;
    Ok(
        json!({"schema":"continuum-lab-inventory/v1","instance":root,"files":files,"qualification":"inventoried-not-gameplay-qualified"}),
    )
}
pub fn matrix() -> Value {
    let mut cases = Vec::new();
    for parts in [16, 64, 128, 256, 512] {
        for environment in ["kerbin-atmosphere", "kerbin-vacuum"] {
            for graphics in ["low", "baseline", "high"] {
                cases.push(json!({"id":format!("{parts}-{environment}-{graphics}"),"parts":parts,"environment":environment,"graphics":graphics,
            "resolution":[1920,1080],"mode":"unpowered-normal-time","bindingStatus":"unbound",
            "requiredBindings":["checkpoint","input-timeline","camera","lighting","graphics-readback","timestep","topology","behavior-contract"]}));
            }
        }
    }
    json!({"schema":"continuum-lab-experiment/v1","cases":cases,"screening":{"repetitions":2,"simulatedSeconds":10,
        "stopWhen":"observations-agree-enough-to-choose-next-action"},
        "confirmation":{"repetitionPolicy":"only-for-a-specific-ambiguity-or-qualification-decision",
        "simulatedSeconds":30,"order":"alternating-when-paired"},"qualification":"planned-unbound"})
}
pub fn startup_screen(baseline: &[(Value, Value)], candidate: &[(Value, Value)]) -> Result<Value> {
    if baseline.len() != 3 || candidate.len() != 3 {
        return Err("startup screening requires three fresh runs per treatment".into());
    }
    let first = &baseline[0].1;
    let mut ids = std::collections::BTreeSet::new();
    let collect = |runs: &[(Value, Value)],
                   treatment: bool,
                   ids: &mut std::collections::BTreeSet<String>|
     -> Result<Vec<f64>> {
        let mut times = Vec::new();
        for (receipt, observation) in runs {
            let id = receipt["runId"].as_str().ok_or("startup nonce absent")?;
            if receipt["schema"] != "continuum-lab-process-smoke/v1"
                || receipt["status"] != "passed"
                || receipt["sourceUnchanged"] != true
                || observation["schema"] != "continuum-managed-runtime-observation/v1"
                || observation["runId"] != id
                || !ids.insert(id.to_owned())
                || receipt["assemblySha256"] != observation["assemblySha256"]
                || receipt["startupUnlimitedRequested"] != treatment
                || observation["startupUnlimitedRequested"] != treatment
                || (treatment
                    && (observation["startupPresentationRestored"] != true
                        || observation["startupUnlimitedFrames"].as_u64().unwrap_or(0) == 0))
            {
                return Err(
                    "startup run lacks matching complete observation or restoration".into(),
                );
            }
            for field in [
                "assemblySha256",
                "assetsAssemblySha256",
                "observerAssemblySha256",
            ] {
                if !observation[field].as_str().map(sha).unwrap_or(false)
                    || observation[field] != first[field]
                {
                    return Err("startup binary inputs differ".into());
                }
            }
            for field in ["playerSha256", "reservationSha256"] {
                if !receipt[field].as_str().map(sha).unwrap_or(false)
                    || receipt[field] != baseline[0].0[field]
                {
                    return Err("startup player/session differs".into());
                }
            }
            for field in [
                "screenWidth",
                "screenHeight",
                "vSyncCount",
                "targetFrameRate",
                "gameVersion",
            ] {
                if observation[field].is_null() || observation[field] != first[field] {
                    return Err("startup effective presentation or version differs".into());
                }
            }
            let time = receipt["mainMenuStartObservedWallSeconds"]
                .as_f64()
                .ok_or("menu boundary not observed")?;
            if !time.is_finite() || time <= 0.0 {
                return Err("invalid startup time".into());
            }
            times.push(time);
        }
        Ok(times)
    };
    let before = collect(baseline, false, &mut ids)?;
    let after = collect(candidate, true, &mut ids)?;
    let mean = |values: &[f64]| values.iter().sum::<f64>() / values.len() as f64;
    let ratio = mean(&after) / mean(&before);
    Ok(
        json!({"schema":"continuum-startup-screen-comparison/v1","baselineSeconds":before,"candidateSeconds":after,
        "baselineMeanSeconds":mean(&before),"candidateMeanSeconds":mean(&after),"meanCostRatio":ratio,"screenSuggestsFivePercentGain":ratio<=0.95,
        "scope":"owned-process-launch-to-main-menu-addon-Start","qualification":"screening-only-no-confidence-or-interactive-menu-qualification"}),
    )
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FlightReceipt {
    pub schema: String,
    pub run_id: String,
    pub case_id: String,
    pub fixture_sha256: String,
    pub protocol_sha256: String,
    pub configuration: BTreeMap<String, String>,
    pub variant: BTreeMap<String, String>,
    pub status: String,
    pub frame_milliseconds: Vec<f64>,
    pub simulated_seconds: f64,
    pub requested_simulated_seconds: f64,
    pub requested_rate: f64,
    pub wall_seconds: f64,
    pub topology_stable: bool,
    pub observer_qualified: bool,
    pub behavior_verified: bool,
    pub dropped_samples: usize,
}
fn sha(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|c| c.is_ascii_hexdigit())
}
pub fn validate(receipt: &FlightReceipt) -> Result<()> {
    if receipt.schema != "continuum-lab-flight/v1"
        || receipt.run_id.is_empty()
        || receipt.case_id.is_empty()
        || !sha(&receipt.fixture_sha256)
        || !sha(&receipt.protocol_sha256)
        || receipt.configuration.is_empty()
        || receipt.variant.is_empty()
    {
        return Err("incomplete or unsupported flight identity".into());
    }
    if receipt.status != "passed"
        || !receipt.topology_stable
        || !receipt.observer_qualified
        || !receipt.behavior_verified
        || receipt.dropped_samples != 0
    {
        return Err("flight is incomplete, changed topology, has unqualified observation, or lacks behavior verification".into());
    }
    if receipt.frame_milliseconds.len() < 100
        || receipt
            .frame_milliseconds
            .iter()
            .any(|v| !v.is_finite() || *v <= 0.0)
        || [
            receipt.simulated_seconds,
            receipt.requested_simulated_seconds,
            receipt.requested_rate,
            receipt.wall_seconds,
        ]
        .iter()
        .any(|v| !v.is_finite() || *v <= 0.0)
        || receipt.simulated_seconds < receipt.requested_simulated_seconds
        || receipt.simulated_seconds > receipt.requested_simulated_seconds + 0.05
    {
        return Err("invalid samples or incomplete fixed simulation window".into());
    }
    let sampled = receipt.frame_milliseconds.iter().sum::<f64>() / 1000.0;
    if (sampled - receipt.wall_seconds).abs() > receipt.wall_seconds * 0.01 {
        return Err("frame samples do not bracket the declared wall window".into());
    }
    Ok(())
}
fn quantile(values: &[f64], fraction: f64) -> f64 {
    let mut sorted = values.to_vec();
    sorted.sort_by(f64::total_cmp);
    sorted[((sorted.len() as f64 * fraction).ceil() as usize)
        .saturating_sub(1)
        .min(sorted.len() - 1)]
}
pub fn metrics(receipt: &FlightReceipt) -> Result<Value> {
    validate(receipt)?;
    let mean =
        receipt.frame_milliseconds.iter().sum::<f64>() / receipt.frame_milliseconds.len() as f64;
    let p95 = quantile(&receipt.frame_milliseconds, 0.95);
    let p99 = quantile(&receipt.frame_milliseconds, 0.99);
    let rate = receipt.simulated_seconds / receipt.wall_seconds;
    Ok(
        json!({"meanFrameMilliseconds":mean,"meanFps":1000.0/mean,"p95Milliseconds":p95,"p99Milliseconds":p99,"achievedRate":rate,
        "meetsNumericPlayableTarget":receipt.requested_rate == 1.0 && 1000.0/mean >=59.5 && p95<=20.0 && p99<=33.3 && rate>=0.99,
        "qualification":"numeric-case-only-mission-and-play-session-required"}),
    )
}
pub fn compare(
    left: &FlightReceipt,
    right: &FlightReceipt,
    treatments: &[String],
) -> Result<Value> {
    validate(left)?;
    validate(right)?;
    if left.run_id == right.run_id
        || left.case_id != right.case_id
        || left.fixture_sha256 != right.fixture_sha256
        || left.protocol_sha256 != right.protocol_sha256
        || left.configuration != right.configuration
        || left.requested_simulated_seconds != right.requested_simulated_seconds
        || left.requested_rate != right.requested_rate
    {
        return Err("receipts are not independent matched workloads".into());
    }
    if treatments.is_empty()
        || treatments
            .iter()
            .any(|k| !left.variant.contains_key(k) || !right.variant.contains_key(k))
    {
        return Err("treatments must name present variant factors".into());
    }
    let keys = left.variant.keys().chain(right.variant.keys());
    for key in keys {
        if left.variant.get(key) != right.variant.get(key) && !treatments.contains(key) {
            return Err(format!("undeclared treatment: {key}"));
        }
    }
    let before = metrics(left)?;
    let after = metrics(right)?;
    let mean_ratio = after["meanFrameMilliseconds"].as_f64().unwrap()
        / before["meanFrameMilliseconds"].as_f64().unwrap();
    let p95_ratio =
        after["p95Milliseconds"].as_f64().unwrap() / before["p95Milliseconds"].as_f64().unwrap();
    let p99_ratio =
        after["p99Milliseconds"].as_f64().unwrap() / before["p99Milliseconds"].as_f64().unwrap();
    Ok(
        json!({"schema":"continuum-lab-comparison/v1","baseline":before,"candidate":after,"meanCostRatio":mean_ratio,"p95Ratio":p95_ratio,"p99Ratio":p99_ratio,
        "withinRegressionLimits":mean_ratio<=1.03 && p95_ratio<=1.05 && p99_ratio<=1.05,
        "qualification":"single-pair-only-confirmation-and-confidence-required"}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    fn startup(id: &str, treatment: bool, seconds: f64) -> (Value, Value) {
        (
            json!({"schema":"continuum-lab-process-smoke/v1","status":"passed","sourceUnchanged":true,"runId":id,"assemblySha256":"a".repeat(64),
            "playerSha256":"d".repeat(64),"reservationSha256":"e".repeat(64),"startupUnlimitedRequested":treatment,"mainMenuStartObservedWallSeconds":seconds}),
            json!({"schema":"continuum-managed-runtime-observation/v1","runId":id,"assemblySha256":"a".repeat(64),"assetsAssemblySha256":"b".repeat(64),
            "observerAssemblySha256":"c".repeat(64),"startupUnlimitedRequested":treatment,"startupPresentationRestored":treatment,"startupUnlimitedFrames":if treatment {10}else{0},
            "screenWidth":1920,"screenHeight":1080,"vSyncCount":1,"targetFrameRate":120,"gameVersion":"fixture"}),
        )
    }
    #[test]
    fn startup_screen_requires_fresh_bound_observations_and_effective_restoration() {
        let baseline = (0..3)
            .map(|i| startup(&format!("b{i}"), false, 55.0))
            .collect::<Vec<_>>();
        let mut candidate = (0..3)
            .map(|i| startup(&format!("c{i}"), true, 49.0))
            .collect::<Vec<_>>();
        let result = startup_screen(&baseline, &candidate).unwrap();
        assert_eq!(result["screenSuggestsFivePercentGain"], true);
        assert!(result["qualification"]
            .as_str()
            .unwrap()
            .contains("no-confidence"));
        candidate[0].1["targetFrameRate"] = json!(-1);
        assert!(startup_screen(&baseline, &candidate).is_err());
        candidate[0] = startup("c0", true, 49.0);
        candidate[0].1["runId"] = json!("stale");
        assert!(startup_screen(&baseline, &candidate).is_err());
    }
    #[test]
    fn source_tree_freezes_inputs_and_excludes_compiler_outputs() {
        let directory = tempfile::tempdir().unwrap();
        let root = directory.path();
        fs::write(root.join("source.cs"), "original").unwrap();
        let before = source_tree_hash(root).unwrap();
        fs::create_dir(root.join("obj")).unwrap();
        fs::write(root.join("obj/result"), "compiler output").unwrap();
        assert_eq!(source_tree_hash(root).unwrap(), before);
        fs::write(root.join("source.cs"), "changed").unwrap();
        assert_ne!(source_tree_hash(root).unwrap(), before);
    }
    #[test]
    fn manifest_requires_complete_runtime_identity_and_hashed_contents() {
        let mut m = VariantManifest {
            schema: "continuum-lab-variant/v1".into(),
            id: "stock".into(),
            code_revision: "original-installed".into(),
            binaries: BTreeMap::from([("game".into(), "a".repeat(64))]),
            assets: BTreeMap::from([("data".into(), "b".repeat(64))]),
            packages: BTreeMap::new(),
            engine: "2019.4.18f1".into(),
            runtime: "Mono".into(),
            architecture: "x86_64".into(),
            renderer: "OpenGL".into(),
            enabled_replacements: vec![],
        };
        assert!(m.validate().is_ok());
        m.binaries.insert("game".into(), "unknown".into());
        assert!(m.validate().is_err());
        m.binaries.insert("game".into(), "a".repeat(64));
        m.architecture.clear();
        assert!(m.validate().is_err());
    }
    fn receipt() -> FlightReceipt {
        FlightReceipt {
            schema: "continuum-lab-flight/v1".into(),
            run_id: "a".into(),
            case_id: "128-vacuum".into(),
            fixture_sha256: "a".repeat(64),
            protocol_sha256: "b".repeat(64),
            configuration: BTreeMap::from([("camera".into(), "fixed".into())]),
            variant: BTreeMap::from([
                ("engine".into(), "2019".into()),
                ("architecture".into(), "x64".into()),
            ]),
            status: "passed".into(),
            frame_milliseconds: vec![16.0; 625],
            simulated_seconds: 10.0,
            requested_simulated_seconds: 10.0,
            requested_rate: 1.0,
            wall_seconds: 10.0,
            topology_stable: true,
            observer_qualified: true,
            behavior_verified: true,
            dropped_samples: 0,
        }
    }
    #[test]
    fn thirty_unbound_cases_never_claim_ready() {
        let matrix = matrix();
        let cases = matrix["cases"].as_array().unwrap();
        assert_eq!(cases.len(), 30);
        assert!(cases.iter().all(|c| c["bindingStatus"] == "unbound"));
    }
    #[test]
    fn rejects_partial_and_hidden_topology_changes() {
        let mut r = receipt();
        r.simulated_seconds = 9.0;
        assert!(validate(&r).is_err());
        r = receipt();
        r.topology_stable = false;
        assert!(validate(&r).is_err());
    }
    #[test]
    fn rejects_missing_observer_and_fabricated_window() {
        let mut r = receipt();
        r.observer_qualified = false;
        assert!(validate(&r).is_err());
        r = receipt();
        r.wall_seconds = 1.0;
        assert!(validate(&r).is_err());
    }
    #[test]
    fn declared_engine_change_is_allowed_but_hidden_architecture_is_rejected() {
        let a = receipt();
        let mut b = a.clone();
        b.run_id = "b".into();
        b.variant.insert("engine".into(), "modern".into());
        assert!(compare(&a, &b, &["engine".into()]).is_ok());
        b.variant.insert("architecture".into(), "arm64".into());
        assert!(compare(&a, &b, &["engine".into()]).is_err());
        assert!(compare(&a, &b, &["engine".into(), "architecture".into()]).is_ok());
    }
    #[test]
    fn fixture_drift_and_self_comparison_are_rejected() {
        let a = receipt();
        assert!(compare(&a, &a, &["engine".into()]).is_err());
        let mut b = a.clone();
        b.run_id = "b".into();
        b.fixture_sha256 = "c".repeat(64);
        assert!(compare(&a, &b, &["engine".into()]).is_err());
    }
    #[test]
    fn a_case_numeric_pass_is_not_mission_qualification() {
        let m = metrics(&receipt()).unwrap();
        assert_eq!(m["meetsNumericPlayableTarget"], true);
        assert!(m["qualification"].as_str().unwrap().contains("mission"));
    }
    #[test]
    fn tails_detect_hitches_hidden_by_mean() {
        let mut r = receipt();
        r.frame_milliseconds[0..10].fill(60.0);
        r.wall_seconds = r.frame_milliseconds.iter().sum::<f64>() / 1000.0;
        assert_eq!(metrics(&r).unwrap()["meetsNumericPlayableTarget"], false);
    }
    #[test]
    fn immutable_outputs_and_streaming_hash() {
        let d = tempfile::tempdir().unwrap();
        let p = d.path().join("receipt");
        write_new(&p, &json!({"a":1})).unwrap();
        let h = hash(&p).unwrap();
        assert!(write_new(&p, &json!({"a":2})).is_err());
        assert_eq!(hash(&p).unwrap(), h);
    }
}
