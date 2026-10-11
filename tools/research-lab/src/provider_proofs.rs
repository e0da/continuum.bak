use crate::{owned_report, Result};
use serde_json::Value;
use std::path::Path;

pub fn bindings(directory: &Path, output: &Path, run: &str, pid: u32) -> Result<Value> {
    let evidence = owned_report::retain(
        directory,
        output,
        &format!("startup-prepared-bindings-{pid}-menu.json"),
        "startup-prepared-bindings.json",
        "continuum-startup-prepared-bindings/v1",
        run,
        pid,
    )?;
    validate_bindings(&evidence["report"])?;
    Ok(evidence)
}

pub fn mechjeb(directory: &Path, output: &Path, run: &str, pid: u32) -> Result<Value> {
    let evidence = owned_report::retain(
        directory,
        output,
        &format!("mechjeb-provider-plan-{run}.json"),
        "mechjeb-provider-plan.json",
        "continuum-mechjeb-provider-plan/v1",
        run,
        pid,
    )?;
    validate_mechjeb(&evidence["report"])?;
    Ok(evidence)
}

pub fn validate_bindings(report: &Value) -> Result<()> {
    if report["status"] != "observed"
        || report["installed"] != true
        || report["hooksRemoved"] != true
        || report["cleanupStatus"] != "removed-owned-hooks"
        || report["errors"].as_u64() != Some(0)
        || !report["failure"].is_null()
        || report["constructorToken"] != 100707352
        || report["gameMvid"] != "10657063-2fc3-43a7-84fa-d39e75e877bf"
        || report["fieldEnumerationsReused"].as_u64().unwrap_or(0) == 0
        || report["attributePlansReused"].as_u64().unwrap_or(0) == 0
    {
        return Err("prepared startup bindings did not prove reuse and owned cleanup".into());
    }
    let rows = report["types"]
        .as_array()
        .ok_or("prepared binding types absent")?;
    if rows.is_empty()
        || rows
            .iter()
            .any(|row| row["constructorErrors"].as_u64() != Some(0))
    {
        return Err("prepared startup binding construction failed or was unobserved".into());
    }
    Ok(())
}

pub fn validate_mechjeb(report: &Value) -> Result<()> {
    if report["status"] != "completed-experimental"
        || report["hooksRemoved"] != true
        || report["cleanupStatus"] != "removed-owned-hooks"
        || report["ilPreservationVerified"] != true
        || report["errors"].as_u64() != Some(0)
        || report["methodToken"] != 100665304
        || report["assemblySha256"]
            != "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929"
        || report["assemblyMvid"] != "30fbe3c4-e77e-4abc-895b-a635d5924a08"
        || report["methodIlSha256"]
            != "4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b"
        || report["originalInstructions"] != 986
        || report["preservedInstructions"] != 954
        || report["calls"].as_u64().unwrap_or(0) == 0
        || report["cachedModules"].as_u64().unwrap_or(0) == 0
        || report["classificationSites"] != 15
        || report["partRetrievalSites"] != 1
        || report["moduleRetrievalSites"] != 1
    {
        return Err(
            "MechJeb provider plan did not prove admitted dispatch and owned cleanup".into(),
        );
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cached_work_cannot_substitute_for_original_execution_and_cleanup() {
        let bindings = serde_json::json!({"status":"observed","installed":true,"hooksRemoved":true,
            "cleanupStatus":"removed-owned-hooks","errors":0,"failure":null,"constructorToken":100707352,
            "gameMvid":"10657063-2fc3-43a7-84fa-d39e75e877bf","fieldEnumerationsReused":20,
            "attributePlansReused":30,"types":[{"constructorErrors":0}]});
        assert!(validate_bindings(&bindings).is_ok());
        for path in [
            "/hooksRemoved",
            "/types/0/constructorErrors",
            "/fieldEnumerationsReused",
        ] {
            let mut corrupt = bindings.clone();
            *corrupt.pointer_mut(path).unwrap() = match path {
                "/hooksRemoved" => serde_json::json!(false),
                "/fieldEnumerationsReused" => serde_json::json!(0),
                _ => serde_json::json!(1),
            };
            assert!(validate_bindings(&corrupt).is_err());
        }
        let mechjeb = serde_json::json!({"status":"completed-experimental","hooksRemoved":true,
            "cleanupStatus":"removed-owned-hooks","ilPreservationVerified":true,"errors":0,"methodToken":100665304,
            "assemblySha256":"6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929",
            "assemblyMvid":"30fbe3c4-e77e-4abc-895b-a635d5924a08","methodIlSha256":"4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b",
            "originalInstructions":986,"preservedInstructions":954,
            "calls":50,"cachedModules":100,"classificationSites":15,"partRetrievalSites":1,"moduleRetrievalSites":1});
        assert!(validate_mechjeb(&mechjeb).is_ok());
        let mut altered = mechjeb.clone();
        altered["ilPreservationVerified"] = serde_json::json!(false);
        assert!(validate_mechjeb(&altered).is_err());
        altered = mechjeb;
        altered["calls"] = serde_json::json!(0);
        assert!(validate_mechjeb(&altered).is_err());
    }
}
