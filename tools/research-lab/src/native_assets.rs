use continuum_research_lab::{hash, read, Result};
use serde_json::{json, Value};
use std::{
    collections::BTreeSet,
    fs,
    path::{Component, Path, PathBuf},
    time::UNIX_EPOCH,
};

pub struct Admission {
    installation: PathBuf,
    installation_sha: String,
    derivation: PathBuf,
    derivation_sha: String,
    data: PathBuf,
    document: Value,
}

pub fn install(instance: &Path, manifest: &Path, output: &Path) -> Result<()> {
    let instance = instance.canonicalize().map_err(|e| e.to_string())?;
    let owner: Value = read(&instance.join("continuum-lab.local.json"))?;
    let pointer = instance.join("continuum-native-asset-installation.local.json");
    if pointer.exists() {
        return Err(
            "native profile already installed; restore its originals before replacing it".into(),
        );
    }
    if owner["scope"] != "disposable-lab" || !manifest.is_absolute() || output.exists() {
        return Err(
            "native installation requires an owned disposable instance and new private receipt"
                .into(),
        );
    }
    if fs::metadata(manifest).map_err(|e| e.to_string())?.len() > 1048576 {
        return Err("native derivation manifest too large".into());
    }
    let derivative: Value = read(manifest)?;
    if derivative["schema"] != "continuum-native-asset-derivative/v1"
        || derivative["status"] != "complete"
    {
        return Err("native derivation incomplete".into());
    }
    let rows = derivative["files"]
        .as_array()
        .ok_or("native derivation files absent")?;
    if rows.len() != 2 {
        return Err("native installation requires one complete asset/stream pair".into());
    }
    let data = instance
        .join("KSP.app/Contents/Resources/Data")
        .canonicalize()
        .map_err(|e| e.to_string())?;
    let mut names = BTreeSet::new();
    let mut pair = Vec::new();
    for row in rows {
        let name = row["relativePath"]
            .as_str()
            .ok_or("native derivation file absent")?;
        let mut parts = Path::new(name).components();
        if !matches!(parts.next(), Some(Component::Normal(_)))
            || parts.next().is_some()
            || !names.insert(name.to_owned())
        {
            return Err("native derivation filename unsafe or duplicated".into());
        }
        let original = data.join(name);
        let prepared = manifest
            .parent()
            .ok_or("native manifest parent absent")?
            .join(name);
        if original.canonicalize().map_err(|e| e.to_string())?.parent() != Some(data.as_path())
            || Some(fs::metadata(&original).map_err(|e| e.to_string())?.len())
                != row["sourceBytes"].as_u64()
            || Some(hash(&original)?.as_str()) != row["sourceSha256"].as_str()
            || Some(fs::metadata(&prepared).map_err(|e| e.to_string())?.len())
                != row["derivedBytes"].as_u64()
            || Some(hash(&prepared)?.as_str()) != row["derivedSha256"].as_str()
        {
            return Err("native source or derivative differs before installation".into());
        }
        pair.push((name.to_owned(), original, prepared, row.clone()));
    }
    let asset = names
        .iter()
        .find(|name| name.ends_with(".assets"))
        .ok_or("native asset file absent")?;
    if !names.contains(&format!("{asset}.resS")) {
        return Err("native stream pair differs".into());
    }
    let backups = output.with_extension("originals");
    fs::create_dir(&backups).map_err(|e| e.to_string())?;
    for (name, original, _, row) in &pair {
        let backup = backups.join(name);
        fs::copy(original, &backup).map_err(|e| e.to_string())?;
        if Some(hash(&backup)?.as_str()) != row["sourceSha256"].as_str() {
            return Err("native rollback copy differs".into());
        }
    }
    let manifest_sha = hash(manifest)?;
    let pending = instance.join("continuum-native-asset-transaction.local.json");
    continuum_research_lab::write_new(
        &pending,
        &json!({"schema":"continuum-native-asset-transaction/v1","installation":output,"backupDirectory":backups}),
    )?;
    let commit = || -> Result<()> {
        for (_, installed, prepared, row) in &pair {
            fs::copy(prepared, installed).map_err(|e| e.to_string())?;
            if Some(hash(installed)?.as_str()) != row["derivedSha256"].as_str() {
                return Err("installed native derivative differs".into());
            }
        }
        let mut files = Vec::new();
        for (name, path, _, row) in &pair {
            files.push(installed_row(
                name,
                path,
                row["sourceSha256"]
                    .as_str()
                    .ok_or("native source hash absent")?,
                row["derivedSha256"]
                    .as_str()
                    .ok_or("native derived hash absent")?,
            )?);
        }
        let mut dependencies = Vec::new();
        for name in [
            "level9",
            "sharedassets0.assets",
            "sharedassets2.assets",
            "resources.assets",
            "globalgamemanagers.assets",
        ] {
            dependencies.push(name.to_owned());
            for suffix in [".resS", ".resource"] {
                let stream = format!("{name}{suffix}");
                if data.join(&stream).is_file() {
                    dependencies.push(stream);
                }
            }
            if let Some(stem) = name.strip_suffix(".assets") {
                let audio = format!("{stem}.resource");
                if data.join(&audio).is_file() {
                    dependencies.push(audio);
                }
            }
        }
        if data.join("sharedassets9.resource").is_file() {
            dependencies.push("sharedassets9.resource".into());
        }
        for name in dependencies {
            let path = data.join(&name);
            let digest = hash(&path)?;
            files.push(installed_row(&name, &path, &digest, &digest)?);
        }
        if hash(manifest)? != manifest_sha {
            return Err("native manifest changed during installation".into());
        }
        continuum_research_lab::write_new(
            output,
            &json!({"schema":"continuum-native-asset-installation/v1",
            "scope":"private-disposable-immutable","complete":true,"fullHashesVerifiedAtInstallation":true,
            "instanceRoot":instance,"derivationManifest":manifest,"derivationManifestSha256":manifest_sha,
            "backupDirectory":backups,"files":files}),
        )?;
        continuum_research_lab::write_new(
            &pointer,
            &json!({"installationReceipt":output,"installationSha256":hash(output)?}),
        )
    };
    if let Err(error) = commit() {
        let mut rollback_errors = Vec::new();
        for (name, original, _, row) in &pair {
            if let Err(e) = fs::copy(backups.join(name), original)
                .map_err(|e| e.to_string())
                .and_then(|_| {
                    if Some(hash(original)?.as_str()) == row["sourceSha256"].as_str() {
                        Ok(())
                    } else {
                        Err("rollback hash differs".into())
                    }
                })
            {
                rollback_errors.push(e);
            }
        }
        if rollback_errors.is_empty() {
            let _ = fs::remove_file(&pointer);
            if output.exists() {
                let _ = fs::rename(output, output.with_extension("failed.json"));
            }
            let _ = fs::remove_file(&pending);
        }
        return Err(format!(
            "native installation failed: {error}; rollback errors: {rollback_errors:?}"
        ));
    }
    fs::remove_file(&pending).map_err(|e| e.to_string())?;
    Ok(())
}

fn installed_row(name: &str, path: &Path, source: &str, installed: &str) -> Result<Value> {
    let metadata = fs::metadata(path).map_err(|e| e.to_string())?;
    let modified = u64::try_from(
        metadata
            .modified()
            .map_err(|e| e.to_string())?
            .duration_since(UNIX_EPOCH)
            .map_err(|e| e.to_string())?
            .as_nanos(),
    )
    .map_err(|e| e.to_string())?;
    Ok(
        json!({"relativePath":name,"sourceSha256":source,"installedSha256":installed,
        "installedBytes":metadata.len(),"mtimeUnixNanos":modified}),
    )
}

pub fn restore(instance: &Path, installation: &Path, output: &Path) -> Result<()> {
    let instance = instance.canonicalize().map_err(|e| e.to_string())?;
    if output.exists() {
        return Err("native restore receipt already exists".into());
    }
    let admitted = Admission::admit(&instance, installation)?;
    let owner: Value = read(&instance.join("continuum-lab.local.json"))?;
    if owner["scope"] != "disposable-lab" {
        return Err("native restoration requires an owned laboratory".into());
    }
    let pointer = instance.join("continuum-native-asset-installation.local.json");
    let installed_pointer: Value = read(&pointer)?;
    if installed_pointer["installationSha256"].as_str() != Some(admitted.installation_sha.as_str())
    {
        return Err("native restoration checkpoint differs from the installed profile".into());
    }
    let backup = PathBuf::from(
        admitted.document["backupDirectory"]
            .as_str()
            .ok_or("native rollback directory absent")?,
    );
    let rows = admitted.document["files"]
        .as_array()
        .ok_or("native rollback files absent")?;
    let mut changed = Vec::new();
    let mut post_run_hashes = Vec::new();
    for row in rows {
        let name = row["relativePath"]
            .as_str()
            .ok_or("native filename absent")?;
        let digest = hash(&admitted.data.join(name))?;
        if row["installedSha256"].as_str() != Some(digest.as_str()) {
            return Err("native installed content changed before restore".into());
        }
        post_run_hashes.push(json!({"relativePath":name,"sha256":digest}));
        if row["sourceSha256"] != row["installedSha256"] {
            if row["sourceSha256"].as_str() != Some(hash(&backup.join(name))?.as_str()) {
                return Err("native original rollback file changed".into());
            }
            changed.push((
                name.to_owned(),
                row["sourceSha256"]
                    .as_str()
                    .ok_or("native original hash absent")?
                    .to_owned(),
            ));
        }
    }
    let pending = instance.join("continuum-native-asset-transaction.local.json");
    continuum_research_lab::write_new(
        &pending,
        &json!({"schema":"continuum-native-asset-transaction/v1","operation":"restore","installation":installation}),
    )?;
    for (name, digest) in &changed {
        fs::copy(backup.join(name), admitted.data.join(name)).map_err(|e| e.to_string())?;
        if hash(&admitted.data.join(name))? != *digest {
            return Err("native restoration hash differs; transaction retained".into());
        }
    }
    continuum_research_lab::write_new(
        output,
        &json!({"schema":"continuum-native-asset-restoration/v1","status":"restored",
        "installationSha256":admitted.installation_sha,"postRunFullHashes":post_run_hashes,"restoredFiles":changed}),
    )?;
    fs::remove_file(pointer).map_err(|e| e.to_string())?;
    fs::remove_file(pending).map_err(|e| e.to_string())?;
    Ok(())
}

impl Admission {
    pub fn admit(instance: &Path, installation: &Path) -> Result<Self> {
        if !installation.is_absolute()
            || fs::metadata(installation).map_err(|e| e.to_string())?.len() > 131072
        {
            return Err("native installation requires a bounded private absolute receipt".into());
        }
        let document: Value = read(installation)?;
        if document["schema"] != "continuum-native-asset-installation/v1"
            || document["scope"] != "private-disposable-immutable"
            || document["complete"] != true
            || document["fullHashesVerifiedAtInstallation"] != true
            || document["instanceRoot"].as_str() != instance.to_str()
        {
            return Err(
                "native asset installation is incomplete or belongs to another instance".into(),
            );
        }
        let derivation = PathBuf::from(
            document["derivationManifest"]
                .as_str()
                .ok_or("native derivation path missing")?,
        );
        let derivation_sha = document["derivationManifestSha256"]
            .as_str()
            .ok_or("native derivation hash missing")?
            .to_owned();
        if !derivation.is_absolute()
            || fs::metadata(&derivation).map_err(|e| e.to_string())?.len() > 1048576
            || hash(&derivation)? != derivation_sha
        {
            return Err("native asset derivation differs".into());
        }
        let rows = document["files"]
            .as_array()
            .ok_or("native installation files missing")?;
        if rows.is_empty() || rows.len() > 32 {
            return Err("native installation file count outside contract".into());
        }
        let mut names = BTreeSet::new();
        for row in rows {
            let name = row["relativePath"]
                .as_str()
                .ok_or("native file name missing")?;
            let mut components = Path::new(name).components();
            if !matches!(components.next(), Some(Component::Normal(_)))
                || components.next().is_some()
                || !names.insert(name)
            {
                return Err("native file name is unsafe or duplicated".into());
            }
            for field in ["sourceSha256", "installedSha256"] {
                let digest = row[field].as_str().ok_or("native file hash missing")?;
                if digest.len() != 64
                    || !digest
                        .bytes()
                        .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
                {
                    return Err("native file hash malformed".into());
                }
            }
        }
        let admitted = Self {
            installation: installation.to_owned(),
            installation_sha: hash(installation)?,
            derivation,
            derivation_sha,
            data: instance
                .join("KSP.app/Contents/Resources/Data")
                .canonicalize()
                .map_err(|e| e.to_string())?,
            document,
        };
        if !admitted.unchanged() {
            return Err(
                "installed native asset profile differs from its verified checkpoint".into(),
            );
        }
        Ok(admitted)
    }

    pub fn unchanged(&self) -> bool {
        let check = || -> Result<bool> {
            if hash(&self.installation)? != self.installation_sha
                || hash(&self.derivation)? != self.derivation_sha
            {
                return Ok(false);
            }
            for row in self.document["files"]
                .as_array()
                .ok_or("native file list missing")?
            {
                let path = self.data.join(
                    row["relativePath"]
                        .as_str()
                        .ok_or("native file name missing")?,
                );
                let canonical = path.canonicalize().map_err(|e| e.to_string())?;
                if canonical.parent() != Some(self.data.as_path()) {
                    return Ok(false);
                }
                let metadata = fs::metadata(&path).map_err(|e| e.to_string())?;
                let modified = u64::try_from(
                    metadata
                        .modified()
                        .map_err(|e| e.to_string())?
                        .duration_since(UNIX_EPOCH)
                        .map_err(|e| e.to_string())?
                        .as_nanos(),
                )
                .map_err(|e| e.to_string())?;
                if !metadata.is_file()
                    || Some(metadata.len()) != row["installedBytes"].as_u64()
                    || Some(modified) != row["mtimeUnixNanos"].as_u64()
                {
                    return Ok(false);
                }
            }
            Ok(true)
        };
        check().unwrap_or(false)
    }

    pub fn receipt(&self) -> Value {
        json!({"installationSha256":self.installation_sha,"derivationSha256":self.derivation_sha,
            "admission":"full-hash-at-installation-immutable-length-mtime-guards", "treatment":"native-scene-texture-format",
            "installation":self.document})
    }
}
