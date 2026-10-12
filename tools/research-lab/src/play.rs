use crate::{flight::Request, hash, launch::ResolvedLaunch, read, write_new, Result};
use serde_json::{json, Value};
use std::{collections::BTreeMap, fs, path::Path};

pub fn validate(selection: &ResolvedLaunch) -> Result<()> {
    if !selection.live_play {
        return Ok(());
    }
    let request = selection
        .flight
        .as_ref()
        .ok_or("live play requires a selected checkpoint")?;
    let plain = BTreeMap::from([
        ("--flight-save".into(), request.save.clone()),
        ("--flight-checkpoint".into(), request.checkpoint.clone()),
    ]);
    let expected = Request::parse(&plain)?.ok_or("live checkpoint absent")?;
    if serde_json::to_value(request).map_err(|e| e.to_string())?
        != serde_json::to_value(expected).map_err(|e| e.to_string())?
    {
        return Err(
            "live play excludes timed-flight treatments; select only save and checkpoint".into(),
        );
    }
    let d = &selection.diagnostics;
    let s = &selection.startup;
    if d.checkpoint_reload
        || d.convection_replacement.is_some()
        || d.docking.is_some()
        || d.convection_capture.is_some()
        || d.cylinder_occlusion_replacement.is_some()
        || d.flight_lighting.is_some()
        || d.integrator_update_attribution
        || d.profile
        || d.texture_observe
        || d.world_timing
        || d.construction_timing
        || d.part_construction_timing
        || d.site_proof.is_some()
        || d.backend_menu_presentation.is_some()
        || d.backend_pixel_proof_bundle.is_some()
        || d.multiscale_scene.is_some()
        || d.menu_material_facts.is_some()
        || selection.native_asset_installation.is_some()
        || selection.graphics_api != crate::launch::GraphicsApi::Current
        || s.catalog_mode.is_preparation()
        || s.resource_image_mode == crate::launch::ResourceImageMode::Prepare
        || s.build_cache
        || s.deferred_terrain
        || s.stock_terrain_ready
    {
        return Err("live play excludes preparation, intrusive observers and unqualified world/backend treatments".into());
    }
    Ok(())
}

pub fn arguments(request: &Request, output: &Path) -> Result<Vec<String>> {
    let output = output.canonicalize().map_err(|e| e.to_string())?;
    Ok(vec![
        "--continuum-live-play".into(),
        "--continuum-experiment-host".into(),
        "--continuum-scale-save".into(),
        request.save.clone(),
        "--continuum-scale-checkpoint".into(),
        request.checkpoint.clone(),
        "--continuum-research-panel=hidden".into(),
        "--continuum-flight-aero-live".into(),
        format!("--continuum-flight-live-output={}", output.display()),
    ])
}

fn mutable_paths(profile: &crate::profile::FrozenLaunch) -> Vec<std::path::PathBuf> {
    let request = profile.selection.flight.as_ref().unwrap();
    vec![
        profile.instance_root.join("settings.cfg"),
        profile
            .instance_root
            .join("saves")
            .join(&request.save)
            .join(format!("{}.sfs", request.checkpoint)),
    ]
}

pub fn immutable_unchanged(profile: &crate::profile::FrozenLaunch) -> bool {
    profile.unchanged_except(&mutable_paths(profile))
}

pub fn mutable_readbacks(profile: &crate::profile::FrozenLaunch) -> Value {
    json!(mutable_paths(profile).iter().map(|path| json!({"path":path,"initialSha256":profile.inputs.get(path),"finalSha256":hash(path).ok(),"scope":"operator-writable; initial hashes remain frozen admission inputs for later launches"})).collect::<Vec<_>>())
}

fn session_name(name: &std::ffi::OsStr) -> bool {
    name.to_str()
        .and_then(|name| name.strip_prefix("live-"))
        .is_some_and(|id| {
            id.len() == 32
                && id
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        })
}

pub fn ready(output: &Path, run_id: &str, pid: u32) -> Result<bool> {
    for entry in fs::read_dir(output).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        if !session_name(&entry.file_name()) {
            continue;
        }
        let path = entry.path().join("ready.json");
        if !path.is_file() {
            continue;
        }
        let row: Value = read(&path)?;
        if row["schema"] == "continuum-flight-aero-live/v1"
            && row["sessionId"]
                .as_str()
                .is_some_and(|id| entry.file_name().to_str() == Some(format!("live-{id}").as_str()))
            && row["runId"] == run_id
            && row["processId"].as_u64() == Some(u64::from(pid))
            && row["state"] == "stock-ready"
            && row["parts"].as_u64().is_some_and(|parts| parts > 0)
            && row["vesselId"].as_str().is_some_and(|id| !id.is_empty())
        {
            return Ok(true);
        }
    }
    Ok(false)
}

pub fn retain(output: &Path, run_id: &str, pid: u32) -> Result<Value> {
    let mut sessions = Vec::new();
    for entry in fs::read_dir(output).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        if !session_name(&entry.file_name()) {
            continue;
        }
        if !entry.file_type().map_err(|e| e.to_string())?.is_dir() {
            return Err("invalid live session directory".into());
        }
        let journal = entry.path().join("transitions.jsonl");
        if fs::metadata(&journal).map_err(|e| e.to_string())?.len() > 8 * 1024 * 1024 {
            return Err("live journal too large".into());
        }
        let text = fs::read_to_string(&journal).map_err(|e| e.to_string())?;
        let rows: Vec<Value> = text
            .lines()
            .map(|line| serde_json::from_str(line).map_err(|e| e.to_string()))
            .collect::<Result<_>>()?;
        let first = rows.first().ok_or("empty live journal")?;
        let session_id = first["sessionId"]
            .as_str()
            .ok_or("live session identity absent")?;
        if entry.file_name().to_str() != Some(format!("live-{session_id}").as_str()) {
            return Err("live session directory identity differs".into());
        }
        if rows.iter().any(|row| {
            row["schema"] != "continuum-flight-aero-live/v1"
                || row["processId"].as_u64() != Some(u64::from(pid))
                || row["runId"] != run_id
                || row["sessionId"] != session_id
        }) || rows.last().unwrap()["state"] != "stock-stopped"
        {
            return Err("foreign or unclosed live session".into());
        }
        let mut batches = Vec::new();
        for epoch in fs::read_dir(entry.path()).map_err(|e| e.to_string())? {
            let epoch = epoch.map_err(|e| e.to_string())?;
            if !epoch.file_name().to_string_lossy().starts_with("epoch-") {
                continue;
            }
            let path = epoch.path().join("aero-batch.json");
            if !path.is_file() {
                return Err("live epoch has no final batch report".into());
            }
            let report: Value = read(&path)?;
            let exact_transition = rows.iter().any(|row| {
                row["epochDirectory"].as_str() == epoch.path().to_str()
                    && row["epoch"]
                        .as_u64()
                        .zip(row["attempt"].as_u64())
                        .is_some_and(|(generation, attempt)| {
                            attempt > 0
                                && report["runId"] == format!("{session_id}-{generation}-{attempt}")
                        })
                    && report["vesselId"] == row["vesselId"]
            });
            if report["schema"] != "continuum-flight-aero-batch/v1"
                || report["processId"].as_u64() != Some(u64::from(pid))
                || !exact_transition
                || report["cleanupStatus"]
                    != "removed-owned-hooks-restored-owned-renderers-destruction-requested"
            {
                return Err("foreign or unclosed live batch".into());
            }
            batches.push(json!({"sha256":hash(&path)?,"report":report}));
        }
        sessions.push(json!({"journal":journal,"journalSha256":hash(&journal)?,"transitions":rows,"batches":batches}));
    }
    if sessions.is_empty() {
        return Err("no owned live flight observed".into());
    }
    let proof = json!({"schema":"continuum-live-play-proof/v1","runId":run_id,"processId":pid,
        "qualification":"ordinary-flight-lifecycle-only; visual-fidelity-performance-mission-and-endurance-open","sessions":sessions});
    write_new(&output.join("live-play-proof.json"), &proof)?;
    Ok(proof)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn options() -> BTreeMap<String, String> {
        BTreeMap::from([
            ("--live-play".into(), "true".into()),
            ("--flight-save".into(), "scenarios".into()),
            ("--flight-checkpoint".into(), "station".into()),
        ])
    }
    #[test]
    fn session_names_exclude_runner_markers() {
        for name in [
            "live-session.json",
            "live-play-proof.json",
            "live-abc",
            "live-",
        ] {
            assert!(!session_name(std::ffi::OsStr::new(name)));
        }
        assert!(session_name(std::ffi::OsStr::new(
            "live-1ad54e928abb42e18442b5b97ebff320"
        )));
    }
    #[test]
    fn live_mode_preserves_operator_camera_and_avoids_benchmark_quit() {
        let launch = ResolvedLaunch::parse(&options()).unwrap();
        assert!(launch.live_play);
        let args = arguments(
            launch.flight.as_ref().unwrap(),
            Path::new(env!("CARGO_MANIFEST_DIR")),
        )
        .unwrap();
        assert!(args.iter().any(|arg| arg == "--continuum-experiment-host"));
        for prohibited in [
            "--continuum-scale-profile",
            "--continuum-native-boundary-bench",
            "--continuum-playerloop",
            "--continuum-flight-diagnostic",
        ] {
            assert!(!args.iter().any(|arg| arg == prohibited));
        }
    }
    #[test]
    fn live_mode_rejects_hidden_capture_and_world_treatments() {
        for (key, value) in [
            ("--flight-aero-batch", "true"),
            ("--flight-warp", "4"),
            ("--flight-seconds", "30"),
            ("--startup-world-timing", "true"),
            ("--startup-deferred-terrain", "true"),
        ] {
            let mut options = options();
            options.insert(key.into(), value.into());
            assert!(ResolvedLaunch::parse(&options).is_err(), "accepted {key}");
        }
    }
    #[test]
    fn old_launch_identity_omits_new_disabled_mode() {
        let launch = ResolvedLaunch::parse(&BTreeMap::new()).unwrap();
        assert!(serde_json::to_value(launch)
            .unwrap()
            .get("livePlay")
            .is_none());
    }
    #[test]
    fn operator_writes_do_not_relax_binary_integrity() {
        let root = std::env::temp_dir().join(format!(
            "continuum-live-writes-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir(&root).unwrap();
        let root = root.canonicalize().unwrap();
        fs::create_dir_all(root.join("saves/scenarios")).unwrap();
        let paths = [
            root.join("settings.cfg"),
            root.join("saves/scenarios/station.sfs"),
            root.join("game.dll"),
        ];
        for path in &paths {
            fs::write(path, b"initial").unwrap();
        }
        let profile = crate::profile::FrozenLaunch {
            schema: "continuum-frozen-launch/v1".into(),
            instance_root: root.clone(),
            selection: ResolvedLaunch::parse(&options()).unwrap(),
            inputs: paths
                .iter()
                .map(|path| (path.clone(), hash(path).unwrap()))
                .collect(),
            asset_generation: crate::profile::AssetGeneration {
                schema: "fixture".into(),
                scope: "fixture".into(),
                components: BTreeMap::new(),
                pending_preparation: Vec::new(),
                admission_policy: "fixture".into(),
            },
        };
        fs::write(&paths[0], b"operator settings").unwrap();
        fs::write(&paths[1], b"operator save").unwrap();
        assert!(immutable_unchanged(&profile));
        assert!(!profile.unchanged());
        assert_ne!(
            mutable_readbacks(&profile)[0]["initialSha256"],
            mutable_readbacks(&profile)[0]["finalSha256"]
        );
        fs::write(&paths[2], b"unexpected binary change").unwrap();
        assert!(!immutable_unchanged(&profile));
        #[cfg(unix)]
        {
            fs::rename(&paths[2], root.join("moved-game.dll")).unwrap();
            fs::write(root.join("moved-game.dll"), b"initial").unwrap();
            std::os::unix::fs::symlink(root.join("moved-game.dll"), &paths[2]).unwrap();
            assert!(!immutable_unchanged(&profile));
        }
        fs::remove_dir_all(root).unwrap();
    }
}
