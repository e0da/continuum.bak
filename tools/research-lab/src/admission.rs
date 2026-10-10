use crate::{hash, read, Result};
use md5::{Digest, Md5};
use serde_json::{json, Value};
use sha2::Sha256;
use std::{
    collections::BTreeMap,
    fs,
    io::Read,
    path::{Path, PathBuf},
    thread,
    time::SystemTime,
};

pub struct Admission {
    pub proof: Value,
    pub files: BTreeMap<PathBuf, String>,
}
impl Admission {
    pub fn unchanged(&self) -> bool {
        let entries: Vec<_> = self.files.iter().collect();
        thread::scope(|scope| {
            let workers: Vec<_> = (0..entries.len().min(3))
                .map(|worker| {
                    let entries = &entries;
                    scope.spawn(move || {
                        entries
                            .iter()
                            .skip(worker)
                            .step_by(3)
                            .all(|(path, digest)| {
                                hash(path).map(|actual| actual == **digest).unwrap_or(false)
                            })
                    })
                })
                .collect();
            let results: Vec<_> = workers.into_iter().map(|worker| worker.join()).collect();
            results.into_iter().all(|result| result.unwrap_or(false))
        })
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
fn source_digests(path: &Path) -> Result<(String, String)> {
    let before = fs::metadata(path).map_err(|e| e.to_string())?;
    let mut stream = fs::File::open(path).map_err(|e| e.to_string())?;
    let mut digest = Md5::new();
    let mut sha = Sha256::new();
    let mut buffer = [0u8; 65536];
    loop {
        let length = stream.read(&mut buffer).map_err(|e| e.to_string())?;
        if length == 0 {
            break;
        }
        digest.update(&buffer[..length]);
        sha.update(&buffer[..length]);
    }
    let after = fs::metadata(path).map_err(|e| e.to_string())?;
    if before.len() != after.len()
        || before.modified().map_err(|e| e.to_string())?
            != after.modified().map_err(|e| e.to_string())?
    {
        return Err("original changed during dual-digest admission".into());
    }
    Ok((
        format!("{:x}", sha.finalize()),
        format!("{:x}", digest.finalize()),
    ))
}
#[derive(PartialEq, Eq)]
struct FileSnapshot {
    length: u64,
    modified: SystemTime,
}
fn snapshot(path: &Path) -> Result<FileSnapshot> {
    let metadata = fs::metadata(path).map_err(|error| error.to_string())?;
    Ok(FileSnapshot {
        length: metadata.len(),
        modified: metadata.modified().map_err(|error| error.to_string())?,
    })
}
fn derived_digest(path: &Path) -> Result<(String, FileSnapshot)> {
    let before = snapshot(path)?;
    let digest = hash(path)?;
    if before != snapshot(path)? {
        return Err("derived changed during SHA256 admission".into());
    }
    Ok((digest, before))
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
    files.insert(manifest_path.clone(), manifest_hash.clone());
    let mut proof = json!({"schema":"continuum-startup-cache-admission/v1","runId":run_id,
        "instanceRoot":instance,"cacheRoot":cache,"engine":"2019.4.18f1","manifestSha256":manifest_hash.clone(),
        "qualification":"private-immutable-profile-admission-not-decoded-content-equivalence"});
    let mut jobs = Vec::new();
    let mut derived_paths = std::collections::BTreeSet::new();
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
        if row["sourceSha256"] != *source_hash {
            return Err("source hash drift".into());
        }
        let derived = owned_path(Path::new(
            row["derivedPath"].as_str().ok_or("derived path absent")?,
        ))?;
        if !derived.starts_with(&cache)
            || derived == cache
            || files.contains_key(&derived)
            || !derived_paths.insert(derived.clone())
            || row["derivedLength"].as_u64()
                != Some(fs::metadata(&derived).map_err(|e| e.to_string())?.len())
        {
            return Err("derived cache binding differs".into());
        }
        jobs.push((
            name,
            source,
            source_hash.clone(),
            row["sourceMd5"].clone(),
            derived,
            row["derivedSha256"].clone(),
        ));
    }
    let completed: Vec<_> = thread::scope(|scope| {
        let workers: Vec<_> = jobs
            .into_iter()
            .map(
                |(name, source, expected_source, expected_md5, derived, expected_derived)| {
                    scope.spawn(move || -> Result<_> {
                        let (source_hash, source_md5) = source_digests(&source)?;
                        if source_hash != expected_source {
                            return Err("source hash drift".into());
                        }
                        if expected_md5 != source_md5 {
                            return Err("source MD5 provenance differs".into());
                        }
                        let (derived_hash, metadata) = derived_digest(&derived)?;
                        if expected_derived != derived_hash {
                            return Err("derived hash drift".into());
                        }
                        Ok((
                            name,
                            source_hash,
                            source_md5,
                            derived,
                            derived_hash,
                            metadata,
                        ))
                    })
                },
            )
            .collect();
        workers
            .into_iter()
            .map(|worker| {
                worker
                    .join()
                    .unwrap_or_else(|_| Err("admission worker panicked".into()))
            })
            .collect()
    });
    for result in completed {
        let (name, source_hash, source_md5, derived, derived_hash, metadata) = result?;
        if snapshot(&derived)? != metadata {
            return Err("derived metadata changed during admission".into());
        }
        files.insert(derived, derived_hash.clone());
        proof[format!("{name}_sourceSha256")] = json!(source_hash);
        proof[format!("{name}_derivedSha256")] = json!(derived_hash);
        proof[format!("{name}_sourceMd5")] = json!(source_md5);
    }
    let admission = Admission { proof, files };
    if hash(&manifest_path)? != manifest_hash {
        return Err("manifest changed during admission".into());
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
            rows.push(json!({"relativePath":relative,"originalPath":source,"sourceLength":6,"sourceSha256":digest,"sourceMd5":source_digests(&source).unwrap().1,"derivedPath":derived,"derivedLength":7,"derivedSha256":hash(&derived).unwrap()}));
        }
        let path = cache.join("manifest.json");
        fs::write(&path,json!({"schema":"continuum-bundle-cache/v1","status":"complete","compression":"LZ4Runtime","engine":"2019.4.18f1","instanceRoot":instance,"bundles":rows}).to_string()).unwrap();
        let admitted = admit(&instance, &cache, "nonce", &source_hashes).unwrap();
        assert_eq!(admitted.proof["runId"], "nonce");
        assert_eq!(
            admitted.proof["makinghistory_assets_sourceMd5"],
            "36cd38f49b9afa08222c0dc9ebfe35eb"
        );
        assert!(admitted.unchanged());
        let valid_manifest = fs::read(&path).unwrap();
        let mut wrong_md5: Value = serde_json::from_slice(&valid_manifest).unwrap();
        wrong_md5["bundles"][0]["sourceMd5"] = json!("00000000000000000000000000000000");
        fs::write(&path, wrong_md5.to_string()).unwrap();
        assert!(admit(&instance, &cache, "wrong-md5", &source_hashes).is_err());
        fs::write(&path, &valid_manifest).unwrap();
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
    #[test]
    fn dual_digest_known_vectors_and_stream_boundary() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("source");
        for (bytes, expected_md5, expected_sha) in [
            (
                b"".as_slice(),
                "d41d8cd98f00b204e9800998ecf8427e",
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ),
            (
                b"abc".as_slice(),
                "900150983cd24fb0d6963f7d28e17f72",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ),
        ] {
            fs::write(&path, bytes).unwrap();
            assert_eq!(
                source_digests(&path).unwrap(),
                (expected_sha.to_string(), expected_md5.to_string())
            );
        }
        let bytes = vec![b'x'; 65537];
        fs::write(&path, &bytes).unwrap();
        assert_eq!(
            source_digests(&path).unwrap(),
            (
                format!("{:x}", Sha256::digest(&bytes)),
                format!("{:x}", Md5::digest(&bytes))
            )
        );
    }
}
