use crate::{hash, launch::ResolvedLaunch, read, Result};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AssetGeneration {
    pub schema: String,
    pub scope: String,
    pub components: BTreeMap<String, String>,
    pub pending_preparation: Vec<String>,
    pub admission_policy: String,
}
impl AssetGeneration {
    pub fn identity(&self) -> Result<String> {
        let bytes = serde_json::to_vec(self).map_err(|e| e.to_string())?;
        Ok(format!("{:x}", Sha256::digest(bytes)))
    }
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FrozenLaunch {
    pub schema: String,
    pub instance_root: PathBuf,
    pub selection: ResolvedLaunch,
    pub inputs: BTreeMap<PathBuf, String>,
    pub asset_generation: AssetGeneration,
}

fn canonical_file(path: &Path) -> Result<()> {
    if !path.is_absolute() || path.canonicalize().map_err(|e| e.to_string())? != path {
        return Err(format!(
            "launch input must be canonical and absolute: {}",
            path.display()
        ));
    }
    for ancestor in path.ancestors() {
        if fs::symlink_metadata(ancestor)
            .map_err(|e| e.to_string())?
            .file_type()
            .is_symlink()
        {
            return Err("launch inputs reject symlinks".into());
        }
    }
    if !path.is_file() {
        return Err("launch input is not a file".into());
    }
    Ok(())
}

fn bind(path: &Path, inputs: &mut BTreeMap<PathBuf, String>) -> Result<String> {
    canonical_file(path)?;
    if let Some(digest) = inputs.get(path) {
        return Ok(digest.clone());
    }
    let before = fs::metadata(path).map_err(|e| e.to_string())?;
    let digest = hash(path)?;
    let after = fs::metadata(path).map_err(|e| e.to_string())?;
    if before.len() != after.len()
        || before.modified().map_err(|e| e.to_string())?
            != after.modified().map_err(|e| e.to_string())?
    {
        return Err("launch input changed during binding".into());
    }
    inputs.insert(path.to_path_buf(), digest.clone());
    Ok(digest)
}

impl FrozenLaunch {
    pub fn load(path: &Path) -> Result<Self> {
        canonical_file(path)?;
        if fs::metadata(path).map_err(|e| e.to_string())?.len() > 4 * 1024 * 1024 {
            return Err("launch profile too large".into());
        }
        let raw: serde_json::Value = read(path)?;
        let selection = ResolvedLaunch::parse_from_json(&raw["selection"].to_string())?;
        let mut profile: Self = serde_json::from_value(raw).map_err(|e| e.to_string())?;
        if profile.schema != "continuum-frozen-launch/v1" {
            return Err("unsupported frozen launch schema".into());
        }
        profile.selection = selection;
        Ok(profile)
    }

    pub fn capture(instance: &Path, selection: &ResolvedLaunch) -> Result<Self> {
        selection.validate()?;
        let instance = instance.canonicalize().map_err(|e| e.to_string())?;
        let owner: serde_json::Value = read(&instance.join("continuum-lab.local.json"))?;
        if owner["scope"] != "disposable-lab" {
            return Err("resolved launch requires disposable laboratory ownership".into());
        }
        let mut inputs = BTreeMap::new();
        let managed = instance.join("KSP.app/Contents/Resources/Data/Managed");
        for path in [
            instance.join("KSP.app/Contents/MacOS/KSP"),
            managed.join("Assembly-CSharp.dll"),
            managed.join("KSPAssets.dll"),
            managed.join("UnityEngine.CoreModule.dll"),
            instance.join("settings.cfg"),
        ] {
            bind(&path, &mut inputs)?;
        }
        fn plugins(dir: &Path, inputs: &mut BTreeMap<PathBuf, String>) -> Result<()> {
            for entry in fs::read_dir(dir).map_err(|e| e.to_string())? {
                let entry = entry.map_err(|e| e.to_string())?;
                let kind = entry.file_type().map_err(|e| e.to_string())?;
                if kind.is_symlink() {
                    return Err("launch plugin inputs reject symbolic links".into());
                }
                if kind.is_dir() && entry.file_name() != "PluginData" {
                    plugins(&entry.path(), inputs)?;
                }
                if kind.is_file()
                    && entry
                        .path()
                        .extension()
                        .and_then(|v| v.to_str())
                        .is_some_and(|v| v.eq_ignore_ascii_case("dll"))
                {
                    bind(&entry.path(), inputs)?;
                }
                if inputs.len() > 10000 {
                    return Err("launch input bound exceeded".into());
                }
            }
            Ok(())
        }
        plugins(&instance.join("GameData"), &mut inputs)?;
        if let Some(flight) = &selection.flight {
            bind(
                &instance
                    .join("saves")
                    .join(&flight.save)
                    .join(format!("{}.sfs", flight.checkpoint)),
                &mut inputs,
            )?;
            for path in [
                &flight.part_shader_bundle,
                &flight.aero_field_bundle,
                &flight.gpu_timing_library,
            ]
            .into_iter()
            .flatten()
            {
                bind(path, &mut inputs)?;
            }
        }
        if let Some(path) = &selection.diagnostics.backend_pixel_proof_bundle {
            bind(path, &mut inputs)?;
            bind(
                &instance.join("KSP.app/Contents/Resources/Data/globalgamemanagers"),
                &mut inputs,
            )?;
        }
        if let Some(path) = &selection.diagnostics.backend_menu_presentation {
            if fs::metadata(path).map_err(|e| e.to_string())?.len() > 1024 * 1024 {
                return Err("menu presentation recipe exceeds metadata bound".into());
            }
            bind(path, &mut inputs)?;
            bind(
                &instance.join("KSP.app/Contents/Resources/Data/globalgamemanagers"),
                &mut inputs,
            )?;
            let recipe: serde_json::Value = read(path)?;
            if recipe["schema"] != "continuum-metal-menu-presentation/v1"
                || recipe["unity"] != "2019.4.18f1"
            {
                return Err("menu presentation recipe identity differs".into());
            }
            let files = recipe["files"].as_array().ok_or("menu files absent")?;
            let shaders = recipe["requiredShaders"]
                .as_array()
                .ok_or("menu shaders absent")?;
            if files.is_empty()
                || files.len() > 16
                || shaders.is_empty()
                || shaders.len() > 64
                || shaders.iter().any(|v| v.as_str().is_none_or(str::is_empty))
            {
                return Err("menu presentation closure exceeds bounded scope".into());
            }
            let mut targets = std::collections::BTreeSet::new();
            for row in files {
                let relative = Path::new(
                    row["relativeDataPath"]
                        .as_str()
                        .ok_or("menu target absent")?,
                );
                if relative.components().count() == 0
                    || relative
                        .components()
                        .any(|c| !matches!(c, std::path::Component::Normal(_)))
                    || !targets.insert(relative.to_path_buf())
                {
                    return Err("menu target must be unique and contained in Data".into());
                }
                let digest = row["derivedSha256"]
                    .as_str()
                    .ok_or("menu derived digest absent")?;
                let source_digest = row["sourceSha256"]
                    .as_str()
                    .ok_or("menu source digest absent")?;
                if [digest, source_digest].iter().any(|v| {
                    v.len() != 64
                        || !v
                            .bytes()
                            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
                }) {
                    return Err("menu file digest invalid".into());
                }
                let derived = Path::new(
                    row["derivedPath"]
                        .as_str()
                        .ok_or("menu derivative absent")?,
                );
                let target = instance
                    .join("KSP.app/Contents/Resources/Data")
                    .join(relative);
                if bind(derived, &mut inputs)? != digest || bind(&target, &mut inputs)? != digest {
                    return Err("menu derived or installed file identity differs".into());
                }
            }
        }
        if let Some(path) = &selection.native_asset_installation {
            bind(path, &mut inputs)?;
        }
        let mut generation = AssetGeneration {
            schema: "continuum-prepared-asset-generation/v1".into(),
            scope: "selected-prepared-startup-metadata; excludes-unindexed-GameData-and-payload-reverification".into(),
            components: BTreeMap::new(), pending_preparation: Vec::new(),
            admission_policy: "private-reserved-immutable-profile; provider-content-and-effective-graphics-admission-required".into(),
        };
        generation.components.insert(
            "game-assembly".into(),
            inputs[&managed.join("Assembly-CSharp.dll")].clone(),
        );
        generation.components.insert(
            "asset-assembly".into(),
            inputs[&managed.join("KSPAssets.dll")].clone(),
        );
        let startup = &selection.startup;
        if startup.catalog_mode.as_str() == "reuse" {
            let path = startup.catalog.as_ref().ok_or("catalog path absent")?;
            generation
                .components
                .insert("catalog-metadata".into(), bind(path, &mut inputs)?);
        } else if startup.catalog_mode.is_preparation() {
            generation
                .pending_preparation
                .push("catalog-metadata".into());
        }
        if startup.resource_image_mode.as_str() == "reuse" {
            let directory = startup.resource_image.as_ref().ok_or("image path absent")?;
            for (name, file) in [
                ("texture-manifest", "manifest.xml"),
                ("texture-owner", "owner.json"),
                ("texture-producer", "producer.dll"),
            ] {
                generation
                    .components
                    .insert(name.into(), bind(&directory.join(file), &mut inputs)?);
            }
        } else if startup.resource_image_mode.as_str() == "prepare" {
            generation.pending_preparation.push("texture-image".into());
        }
        if startup.cached_bundles {
            let directory = startup.cache.as_ref().ok_or("cache path absent")?;
            for (name, file) in [
                ("bundle-manifest", "manifest.json"),
                ("bundle-owner", "continuum-bundle-cache.local.json"),
            ] {
                generation
                    .components
                    .insert(name.into(), bind(&directory.join(file), &mut inputs)?);
            }
        } else if startup.build_cache {
            generation.pending_preparation.push("dlc-bundles".into());
        }
        Ok(Self {
            schema: "continuum-frozen-launch/v1".into(),
            instance_root: instance,
            selection: selection.clone(),
            inputs,
            asset_generation: generation,
        })
    }

    pub fn require_matches(&self, expected: &Self) -> Result<()> {
        if serde_json::to_value(self).map_err(|e| e.to_string())?
            != serde_json::to_value(expected).map_err(|e| e.to_string())?
        {
            return Err("frozen launch differs from current required inputs or selection; resolve a new profile".into());
        }
        Ok(())
    }
    pub fn unchanged(&self) -> bool {
        self.inputs.iter().all(|(path, expected)| {
            canonical_file(path).is_ok() && hash(path).is_ok_and(|actual| actual == *expected)
        })
    }
}

pub fn retain_generation(
    directory: &Path,
    output: &Path,
    run_id: &str,
    pid: u32,
    instance: &Path,
    launch_sha: &str,
    generation_sha: &str,
) -> Result<serde_json::Value> {
    let mut retained = serde_json::Map::new();
    for phase in ["menu", "shutdown"] {
        let source = directory.join(format!("asset-generation-{pid}-{phase}.json"));
        if !source.exists() {
            retained.insert(phase.into(), serde_json::json!({"status":"missing"}));
            continue;
        }
        let before = hash(&source)?;
        let report: serde_json::Value = read(&source)?;
        if report["schema"] != "continuum-startup-asset-generation/v1"
            || report["runId"] != run_id
            || report["processId"] != pid
            || report["instanceRoot"] != instance.to_string_lossy().as_ref()
            || report["launchProfileSha256"] != launch_sha
            || report["assetGenerationSha256"] != generation_sha
            || report["phase"] != phase
        {
            return Err("startup generation receipt identity differs".into());
        }
        let target = output.join(format!("asset-generation-{phase}.json"));
        fs::copy(&source, &target).map_err(|e| e.to_string())?;
        if hash(&source)? != before || hash(&target)? != before {
            return Err("generation receipt changed during retention".into());
        }
        retained.insert(
            phase.into(),
            serde_json::json!({"sha256":before,"report":report}),
        );
    }
    Ok(serde_json::Value::Object(retained))
}

pub fn validate_generation(evidence: &serde_json::Value, selection: &ResolvedLaunch) -> Result<()> {
    let preparation = selection.startup.build_cache
        || selection.startup.catalog_mode.is_preparation()
        || selection.startup.resource_image_mode.as_str() == "prepare";
    let expected: Vec<&str> = [
        (
            selection.startup.catalog_mode.as_str() == "reuse",
            "catalog-metadata",
        ),
        (selection.startup.prepared_bundles, "dlc-bundles"),
        (
            selection.startup.resource_image_mode.as_str() == "reuse",
            "texture-image",
        ),
    ]
    .into_iter()
    .filter_map(|(enabled, name)| enabled.then_some(name))
    .collect();
    for phase in if preparation {
        vec!["shutdown"]
    } else {
        vec!["menu", "shutdown"]
    } {
        let report = &evidence[phase]["report"];
        if report["declared"] != true || report["recordingFailures"] != 0 {
            return Err("generation observation missing or recorder failed".into());
        }
        let capabilities = report["capabilities"]
            .as_array()
            .ok_or("generation capabilities absent")?;
        let names: Vec<_> = capabilities
            .iter()
            .map(|c| c["name"].as_str().unwrap_or(""))
            .collect();
        if names != expected {
            return Err("generation capabilities differ from launch selection".into());
        }
        if !expected.is_empty()
            && (report["admitted"] != true
                || report["ready"] != true
                || capabilities.iter().any(|c| {
                    c["state"] != "ready"
                        || c["admissionPassed"] != true
                        || c["fallbacks"] != 0
                        || c["publications"].as_u64().unwrap_or(0) == 0
                        || c["publications"] != c["expectedPublications"]
                }))
        {
            return Err(
                "requested prepared generation did not reach complete ready publication".into(),
            );
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> (tempfile::TempDir, ResolvedLaunch) {
        let root = tempfile::tempdir().unwrap();
        for (path, text) in [
            ("continuum-lab.local.json", "{\"scope\":\"disposable-lab\"}"),
            ("KSP.app/Contents/MacOS/KSP", "player"),
            (
                "KSP.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll",
                "game",
            ),
            (
                "KSP.app/Contents/Resources/Data/Managed/KSPAssets.dll",
                "assets",
            ),
            (
                "KSP.app/Contents/Resources/Data/Managed/UnityEngine.CoreModule.dll",
                "unity",
            ),
            ("settings.cfg", "settings"),
            ("GameData/Example/plugin.dll", "mod"),
        ] {
            let file = root.path().join(path);
            fs::create_dir_all(file.parent().unwrap()).unwrap();
            fs::write(file, text).unwrap();
        }
        (root, ResolvedLaunch::parse(&BTreeMap::new()).unwrap())
    }
    #[test]
    fn replay_cannot_omit_a_required_input_or_accept_drift() {
        let (root, selection) = fixture();
        let original = FrozenLaunch::capture(root.path(), &selection).unwrap();
        let mut omitted = original.clone();
        omitted.inputs.pop_first();
        assert!(original.require_matches(&omitted).is_err());
        fs::write(root.path().join("GameData/Example/plugin.dll"), "changed").unwrap();
        assert!(!original.unchanged());
        assert!(FrozenLaunch::capture(root.path(), &selection)
            .unwrap()
            .require_matches(&original)
            .is_err());
    }
    #[test]
    fn metadata_change_changes_generation_but_diagnostic_change_does_not() {
        let (root, mut selection) = fixture();
        let catalog = root.path().canonicalize().unwrap().join("catalog.xml");
        fs::write(&catalog, "recipe-one").unwrap();
        selection.startup.catalog_mode = crate::launch::CatalogMode::Reuse;
        selection.startup.catalog = Some(catalog.clone());
        let first = FrozenLaunch::capture(root.path(), &selection).unwrap();
        selection.diagnostics.world_timing = true;
        let diagnostic = FrozenLaunch::capture(root.path(), &selection).unwrap();
        assert_eq!(
            first.asset_generation.identity().unwrap(),
            diagnostic.asset_generation.identity().unwrap()
        );
        assert_ne!(
            first.selection.identity_sha256().unwrap(),
            diagnostic.selection.identity_sha256().unwrap()
        );
        fs::write(catalog, "recipe-two").unwrap();
        assert_ne!(
            first.asset_generation.identity().unwrap(),
            FrozenLaunch::capture(root.path(), &selection)
                .unwrap()
                .asset_generation
                .identity()
                .unwrap()
        );
    }
    #[test]
    fn preparation_does_not_borrow_completed_metadata() {
        let (root, mut selection) = fixture();
        selection.startup.resource_image_mode = crate::launch::ResourceImageMode::Prepare;
        selection.startup.resource_image =
            Some(root.path().canonicalize().unwrap().join("new-image"));
        let profile = FrozenLaunch::capture(root.path(), &selection).unwrap();
        assert_eq!(
            profile.asset_generation.pending_preparation,
            ["texture-image"]
        );
        assert!(!profile
            .asset_generation
            .components
            .contains_key("texture-manifest"));
    }

    #[test]
    fn ready_claim_requires_complete_publication_and_no_recorder_failure() {
        let (_, mut selection) = fixture();
        selection.startup.catalog_mode = crate::launch::CatalogMode::Reuse;
        let report = serde_json::json!({"declared":true,"admitted":true,"ready":true,"recordingFailures":0,
            "capabilities":[{"name":"catalog-metadata","state":"ready","admissionPassed":true,"fallbacks":0,"publications":1,"expectedPublications":1}]});
        let mut evidence =
            serde_json::json!({"menu":{"report":report.clone()},"shutdown":{"report":report}});
        assert!(validate_generation(&evidence, &selection).is_ok());
        evidence["menu"]["report"]["capabilities"][0]["publications"] = serde_json::json!(0);
        assert!(validate_generation(&evidence, &selection).is_err());
        evidence["menu"] = evidence["shutdown"].clone();
        evidence["shutdown"]["report"]["recordingFailures"] = serde_json::json!(1);
        assert!(validate_generation(&evidence, &selection).is_err());
    }
}
