use crate::{hash, read, Result};
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

pub struct Admission {
    pub proof: Value,
    pub files: BTreeMap<PathBuf, String>,
}
impl Admission {
    pub fn unchanged(&self) -> bool {
        self.files
            .iter()
            .all(|(path, digest)| hash(path).map(|actual| actual == *digest).unwrap_or(false))
    }
}
fn owned_path(path: &Path) -> Result<PathBuf> {
    let absolute = path.canonicalize().map_err(|e| e.to_string())?;
    if !path.is_absolute() || absolute != path {
        return Err("admission path must be canonical and absolute".into());
    }
    for ancestor in path.ancestors() {
        if fs::symlink_metadata(ancestor)
            .map_err(|e| e.to_string())?
            .file_type()
            .is_symlink()
        {
            return Err("admission symlinks are not permitted".into());
        }
    }
    Ok(absolute)
}
pub fn admit(
    instance: &Path,
    cache: &Path,
    run_id: &str,
    source_hashes: &BTreeMap<String, String>,
) -> Result<Admission> {
    let instance = owned_path(instance)?;
    let cache = owned_path(cache)?;
    if cache.starts_with(&instance) {
        return Err("derived cache must be outside installation".into());
    }
    let owner: Value = read(&cache.join("continuum-bundle-cache.local.json"))?;
    if owner["schema"] != "continuum-bundle-cache-owner/v1"
        || owner["scope"] != "private-derived-cache"
    {
        return Err("private derived cache ownership required".into());
    }
    let manifest_path = owned_path(&cache.join("manifest.json"))?;
    if fs::metadata(&manifest_path)
        .map_err(|e| e.to_string())?
        .len()
        > 65536
    {
        return Err("cache manifest too large".into());
    }
    let manifest_hash = hash(&manifest_path)?;
    let manifest: Value = read(&manifest_path)?;
    if manifest["schema"] != "continuum-bundle-cache/v1"
        || manifest["status"] != "complete"
        || manifest["compression"] != "LZ4Runtime"
        || manifest["engine"] != "2019.4.18f1"
        || manifest["instanceRoot"] != instance.to_string_lossy().as_ref()
    {
        return Err("cache manifest configuration differs".into());
    }
    let rows = manifest["bundles"].as_array().ok_or("cache rows absent")?;
    if rows.len() != 3 {
        return Err("three cache rows required".into());
    }
    let mut files = BTreeMap::new();
    files.insert(manifest_path, manifest_hash.clone());
    let mut proof = json!({"schema":"continuum-startup-cache-admission/v1","runId":run_id,
        "instanceRoot":instance,"cacheRoot":cache,"engine":"2019.4.18f1","manifestSha256":manifest_hash,
        "qualification":"private-immutable-profile-admission-not-decoded-content-equivalence"});
    for (relative, name) in [
        (
            "MakingHistory/AssetBundles/makinghistory_assets",
            "makinghistory_assets",
        ),
        (
            "MakingHistory/AssetBundles/makinghistory_scene",
            "makinghistory_scene",
        ),
        ("Serenity/AssetBundles/serenity_assets", "serenity_assets"),
    ] {
        let matching: Vec<_> = rows
            .iter()
            .filter(|row| row["relativePath"] == relative)
            .collect();
        if matching.len() != 1 {
            return Err("cache row identity differs or is duplicated".into());
        }
        let row = matching[0];
        let source_relative = format!("GameData/SquadExpansion/{relative}");
        let source = owned_path(&instance.join(&source_relative))?;
        if row["originalPath"] != source.to_string_lossy().as_ref()
            || row["sourceLength"].as_u64()
                != Some(fs::metadata(&source).map_err(|e| e.to_string())?.len())
        {
            return Err("source cache binding differs".into());
        }
        let source_hash = source_hashes
            .get(&source_relative)
            .ok_or("source hash absent")?;
        if row["sourceSha256"] != *source_hash || hash(&source)? != *source_hash {
            return Err("source hash drift".into());
        }
        let derived = owned_path(Path::new(
            row["derivedPath"].as_str().ok_or("derived path absent")?,
        ))?;
        if !derived.starts_with(&cache)
            || derived == cache
            || files.contains_key(&derived)
            || row["derivedLength"].as_u64()
                != Some(fs::metadata(&derived).map_err(|e| e.to_string())?.len())
        {
            return Err("derived cache binding differs".into());
        }
        let derived_hash = hash(&derived)?;
        if row["derivedSha256"] != derived_hash {
            return Err("derived hash drift".into());
        }
        files.insert(derived, derived_hash.clone());
        proof[format!("{name}_sourceSha256")] = json!(source_hash);
        proof[format!("{name}_derivedSha256")] = json!(derived_hash);
    }
    let admission = Admission { proof, files };
    if !admission.unchanged() {
        return Err("cache changed during admission".into());
    }
    Ok(admission)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn admission_rejects_drift_and_binds_current_sources() {
        let directory = tempfile::tempdir().unwrap();
        let root = directory.path().canonicalize().unwrap();
        let instance = root.join("instance");
        let cache = root.join("cache");
        fs::create_dir(&instance).unwrap();
        fs::create_dir(&cache).unwrap();
        fs::write(
            cache.join("continuum-bundle-cache.local.json"),
            json!({"schema":"continuum-bundle-cache-owner/v1","scope":"private-derived-cache"})
                .to_string(),
        )
        .unwrap();
        let mut source_hashes = BTreeMap::new();
        let mut rows = Vec::new();
        for (relative, name) in [
            ("MakingHistory/AssetBundles/makinghistory_assets", "a"),
            ("MakingHistory/AssetBundles/makinghistory_scene", "b"),
            ("Serenity/AssetBundles/serenity_assets", "c"),
        ] {
            let key = format!("GameData/SquadExpansion/{relative}");
            let source = instance.join(&key);
            let derived = cache.join(name);
            fs::create_dir_all(source.parent().unwrap()).unwrap();
            fs::write(&source, b"source").unwrap();
            fs::write(&derived, b"derived").unwrap();
            let digest = hash(&source).unwrap();
            source_hashes.insert(key, digest.clone());
            rows.push(json!({"relativePath":relative,"originalPath":source,"sourceLength":6,"sourceSha256":digest,"derivedPath":derived,"derivedLength":7,"derivedSha256":hash(&derived).unwrap()}));
        }
        let path = cache.join("manifest.json");
        fs::write(&path,json!({"schema":"continuum-bundle-cache/v1","status":"complete","compression":"LZ4Runtime","engine":"2019.4.18f1","instanceRoot":instance,"bundles":rows}).to_string()).unwrap();
        let admitted = admit(&instance, &cache, "nonce", &source_hashes).unwrap();
        assert_eq!(admitted.proof["runId"], "nonce");
        assert!(admitted.unchanged());
        fs::write(cache.join("a"), b"changed").unwrap();
        assert!(!admitted.unchanged());
        assert!(admit(&instance, &cache, "next", &source_hashes).is_err());
        fs::write(cache.join("a"), b"derived").unwrap();
        fs::write(
            instance
                .join("GameData/SquadExpansion/MakingHistory/AssetBundles/makinghistory_assets"),
            b"edited",
        )
        .unwrap();
        assert!(admit(&instance, &cache, "next", &source_hashes).is_err());
    }
}
