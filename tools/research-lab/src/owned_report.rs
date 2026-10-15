use crate::{hash, read, Result};
use serde_json::{json, Value};
use std::{fs, io::Write, path::Path};

pub fn retain(
    directory: &Path,
    output: &Path,
    source_name: &str,
    target_name: &str,
    schema: &str,
    run_id: &str,
    pid: u32,
) -> Result<Value> {
    for name in [source_name, target_name] {
        if name.is_empty() || Path::new(name).file_name().and_then(|n| n.to_str()) != Some(name) {
            return Err("owned report requires a single file name".into());
        }
    }
    let source = directory.join(source_name);
    let metadata = fs::symlink_metadata(&source).map_err(|e| e.to_string())?;
    if !metadata.is_file() || metadata.len() > 4 * 1024 * 1024 {
        return Err("owned report is not a bounded regular file".into());
    }
    let target = output.join(target_name);
    let digest = hash(&source)?;
    let bytes = fs::read(&source).map_err(|e| e.to_string())?;
    fs::OpenOptions::new().write(true).create_new(true).open(&target)
        .and_then(|mut file| file.write_all(&bytes)).map_err(|e| e.to_string())?;
    if hash(&source)? != digest || hash(&target)? != digest {
        return Err("owned report changed during retention".into());
    }
    let report: Value = read(&target)?;
    if report["schema"] != schema || report["runId"] != run_id || report["processId"] != pid {
        return Err(
            "owned report schema, run or process identity differs; raw evidence retained".into(),
        );
    }
    Ok(json!({"sha256":digest,"report":report}))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn foreign_run_is_retained_but_cannot_supply_current_readiness() {
        let input = tempfile::tempdir().unwrap();
        let output = tempfile::tempdir().unwrap();
        let path = input.path().join("owner-17.json");
        fs::write(
            &path,
            r#"{"schema":"capability/v1","runId":"old","processId":17,"ready":true}"#,
        )
        .unwrap();
        assert!(retain(
            input.path(),
            output.path(),
            "owner-17.json",
            "evidence.json",
            "capability/v1",
            "current",
            17
        )
        .is_err());
        assert_eq!(
            hash(&path).unwrap(),
            hash(&output.path().join("evidence.json")).unwrap()
        );
        assert!(retain(
            input.path(),
            output.path(),
            "../owner-17.json",
            "other.json",
            "capability/v1",
            "old",
            17
        )
        .is_err());
        assert!(retain(
            input.path(),
            output.path(),
            "owner-17.json",
            "evidence.json",
            "capability/v1",
            "old",
            17
        )
        .is_err());
        let valid = retain(
            input.path(),
            output.path(),
            "owner-17.json",
            "accepted.json",
            "capability/v1",
            "old",
            17,
        )
        .unwrap();
        assert_eq!(valid["report"]["ready"], true);
    }
}
