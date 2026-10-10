use crate::{hash, read, write_new, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::Path,
};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Catalog {
    pub schema: String,
    pub profile_sha256: String,
    pub workload_sha256: String,
    pub failure_signature: String,
    pub dependencies: BTreeMap<String, BTreeSet<String>>,
    pub verified_ring: BTreeSet<String>,
}
impl Catalog {
    fn validate(&self) -> Result<()> {
        if self.schema != "continuum-mod-recovery-catalog/v1"
            || !crate::sha(&self.profile_sha256)
            || !crate::sha(&self.workload_sha256)
            || self.failure_signature.trim().is_empty()
            || self.dependencies.keys().any(|v| v.trim().is_empty())
            || self
                .dependencies
                .values()
                .flatten()
                .chain(self.verified_ring.iter())
                .any(|v| !self.dependencies.contains_key(v))
        {
            return Err("recovery catalog has unsupported identity or missing dependencies".into());
        }
        if !self.closed(&self.verified_ring) {
            return Err("fallback ring is not dependency-complete".into());
        }
        Ok(())
    }
    fn closed(&self, enabled: &BTreeSet<String>) -> bool {
        enabled
            .iter()
            .all(|id| self.dependencies[id].is_subset(enabled))
    }
    fn without(&self, enabled: &BTreeSet<String>, id: &str) -> BTreeSet<String> {
        let mut result = enabled.clone();
        result.remove(id);
        loop {
            let removed = result
                .iter()
                .filter(|id| !self.dependencies[*id].is_subset(&result))
                .cloned()
                .collect::<Vec<_>>();
            if removed.is_empty() {
                return result;
            }
            for id in removed {
                result.remove(&id);
            }
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum Outcome {
    Passed,
    Failed,
    Invalid,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Trial {
    pub id: String,
    pub enabled: BTreeSet<String>,
    pub recovery_ring: BTreeSet<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Session {
    pub schema: String,
    pub catalog_sha256: String,
    pub catalog: Catalog,
    pub stage: String,
    pub active: Option<Trial>,
    pub minimal_failing_group: BTreeSet<String>,
    pub observed: BTreeMap<String, Outcome>,
    pub trial_budget: usize,
    cursor: usize,
    ordinal: usize,
}
fn key(enabled: &BTreeSet<String>) -> String {
    serde_json::to_string(enabled).unwrap()
}
impl Session {
    pub fn start(catalog: Catalog, catalog_sha256: String, trial_budget: usize) -> Result<Self> {
        catalog.validate()?;
        if trial_budget < 2 || !crate::sha(&catalog_sha256) {
            return Err(
                "recovery requires a catalog hash and at least baseline and full-group trials"
                    .into(),
            );
        }
        let mut session = Self {
            schema: "continuum-mod-recovery-session/v1".into(),
            catalog_sha256,
            catalog,
            stage: "verify-fallback".into(),
            active: None,
            minimal_failing_group: BTreeSet::new(),
            observed: BTreeMap::new(),
            trial_budget,
            cursor: 0,
            ordinal: 0,
        };
        session.queue(session.catalog.verified_ring.clone())?;
        Ok(session)
    }
    fn queue(&mut self, enabled: BTreeSet<String>) -> Result<()> {
        if !self.catalog.closed(&enabled) {
            return Err("trial violates mod dependencies".into());
        }
        if self.ordinal >= self.trial_budget {
            self.stage = "inconclusive-budget".into();
            self.active = None;
            return Ok(());
        }
        self.ordinal += 1;
        self.active = Some(Trial {
            id: format!("{}-{}", self.catalog_sha256, self.ordinal),
            enabled,
            recovery_ring: self.catalog.verified_ring.clone(),
        });
        Ok(())
    }
    pub fn observe(
        &mut self,
        trial_id: &str,
        outcome: Outcome,
        failure_signature: Option<&str>,
    ) -> Result<()> {
        let trial = self.active.as_ref().ok_or("no active recovery trial")?;
        if trial.id != trial_id {
            return Err("stale or unrelated recovery result".into());
        }
        if outcome == Outcome::Failed
            && self.stage != "verify-fallback"
            && failure_signature != Some(self.catalog.failure_signature.as_str())
        {
            self.stage = "inconclusive-unrelated-failure".into();
            self.active = None;
            return Ok(());
        }
        if outcome == Outcome::Invalid {
            self.stage = "inconclusive-invalid-trial".into();
            self.active = None;
            return Ok(());
        }
        let trial = self.active.take().unwrap();
        self.observed.insert(key(&trial.enabled), outcome.clone());
        match self.stage.as_str() {
            "verify-fallback" => {
                if outcome == Outcome::Failed {
                    self.stage = "fallback-failed".into();
                    return Ok(());
                }
                self.stage = "verify-full".into();
                self.queue(self.catalog.dependencies.keys().cloned().collect())?;
            }
            "verify-full" => {
                if outcome == Outcome::Passed {
                    self.stage = "failure-not-reproduced".into();
                    return Ok(());
                }
                self.stage = "minimize-failure".into();
                self.minimal_failing_group = trial.enabled;
                self.next()?;
            }
            "minimize-failure" => {
                if outcome == Outcome::Failed {
                    self.minimal_failing_group = trial.enabled;
                    self.cursor = 0;
                } else {
                    self.cursor += 1;
                }
                self.next()?;
            }
            _ => return Err("unsupported recovery transition".into()),
        }
        Ok(())
    }
    fn next(&mut self) -> Result<()> {
        let suspects = self
            .minimal_failing_group
            .difference(&self.catalog.verified_ring)
            .cloned()
            .collect::<Vec<_>>();
        while self.cursor < suspects.len() {
            let enabled = self
                .catalog
                .without(&self.minimal_failing_group, &suspects[self.cursor]);
            if self.observed.contains_key(&key(&enabled)) {
                self.cursor += 1;
                continue;
            }
            return self.queue(enabled);
        }
        self.minimal_failing_group = self
            .minimal_failing_group
            .difference(&self.catalog.verified_ring)
            .cloned()
            .collect();
        self.stage = "dependency-minimal-failing-group".into();
        self.active = None;
        Ok(())
    }
}
pub fn advance(
    catalog_path: &Path,
    state: Option<&Path>,
    trial_id: Option<&str>,
    outcome: Option<&str>,
    failure_signature: Option<&str>,
    output: &Path,
) -> Result<()> {
    let digest = hash(catalog_path)?;
    let mut session = if let Some(path) = state {
        let session: Session = read(path)?;
        session.catalog.validate()?;
        if session
            .minimal_failing_group
            .iter()
            .chain(session.active.iter().flat_map(|t| t.enabled.iter()))
            .any(|id| !session.catalog.dependencies.contains_key(id))
        {
            return Err("recovery state contains an unknown mod".into());
        }
        if session.schema != "continuum-mod-recovery-session/v1" || session.catalog_sha256 != digest
        {
            return Err("recovery catalog changed".into());
        }
        let catalog: Catalog = read(catalog_path)?;
        if serde_json::to_value(&catalog).map_err(|e| e.to_string())?
            != serde_json::to_value(&session.catalog).map_err(|e| e.to_string())?
        {
            return Err("state catalog differs from frozen catalog".into());
        }
        session
    } else {
        if trial_id.is_some() || outcome.is_some() || failure_signature.is_some() {
            return Err("initial recovery plan cannot include an outcome".into());
        }
        Session::start(read(catalog_path)?, digest, 128)?
    };
    if state.is_some() {
        let outcome = match outcome.ok_or("result required")? {
            "passed" => Outcome::Passed,
            "failed" => Outcome::Failed,
            "invalid" => Outcome::Invalid,
            _ => return Err("result must be passed, failed or invalid".into()),
        };
        session.observe(
            trial_id.ok_or("trial identity required")?,
            outcome,
            failure_signature,
        )?;
    }
    write_new(output, &session)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn catalog() -> Catalog {
        Catalog {
            schema: "continuum-mod-recovery-catalog/v1".into(),
            profile_sha256: "a".repeat(64),
            workload_sha256: "b".repeat(64),
            failure_signature: "startup:fixture-failure".into(),
            dependencies: BTreeMap::from([
                ("base".into(), BTreeSet::new()),
                ("a".into(), BTreeSet::from(["base".into()])),
                ("b".into(), BTreeSet::from(["base".into()])),
            ]),
            verified_ring: BTreeSet::from(["base".into()]),
        }
    }
    fn answer(s: &mut Session, outcome: Outcome) {
        let signature = s.catalog.failure_signature.clone();
        s.observe(
            &s.active.as_ref().unwrap().id.clone(),
            outcome,
            Some(&signature),
        )
        .unwrap();
    }
    #[test]
    fn identifies_single_failure_and_preserves_fallback() {
        let mut s = Session::start(catalog(), "a".repeat(64), 30).unwrap();
        while let Some(t) = s.active.clone() {
            assert!(t.recovery_ring.contains("base"));
            answer(
                &mut s,
                if t.enabled.contains("b") {
                    Outcome::Failed
                } else {
                    Outcome::Passed
                },
            );
        }
        assert_eq!(s.minimal_failing_group, BTreeSet::from(["b".into()]));
    }
    #[test]
    fn interacting_mods_remain_a_group() {
        let mut s = Session::start(catalog(), "a".repeat(64), 30).unwrap();
        while let Some(t) = s.active.clone() {
            answer(
                &mut s,
                if t.enabled.contains("a") && t.enabled.contains("b") {
                    Outcome::Failed
                } else {
                    Outcome::Passed
                },
            );
        }
        assert_eq!(s.minimal_failing_group.len(), 2);
    }
    #[test]
    fn dependent_trials_are_closed() {
        let mut c = catalog();
        c.dependencies.get_mut("b").unwrap().insert("a".into());
        let mut s = Session::start(c, "a".repeat(64), 30).unwrap();
        while let Some(t) = s.active.clone() {
            assert!(s.catalog.closed(&t.enabled));
            answer(
                &mut s,
                if t.enabled.contains("b") {
                    Outcome::Failed
                } else {
                    Outcome::Passed
                },
            );
        }
        assert_eq!(s.minimal_failing_group.len(), 2);
    }
    #[test]
    fn failed_base_invalid_and_stale_trials_never_blame_mods() {
        let mut s = Session::start(catalog(), "a".repeat(64), 30).unwrap();
        assert!(s.observe("stale", Outcome::Failed, None).is_err());
        answer(&mut s, Outcome::Failed);
        assert_eq!(s.stage, "fallback-failed");
        assert!(s.minimal_failing_group.is_empty());
        let mut s = Session::start(catalog(), "a".repeat(64), 30).unwrap();
        answer(&mut s, Outcome::Invalid);
        assert!(s.active.is_none());
        assert_eq!(s.stage, "inconclusive-invalid-trial");
    }
    #[test]
    fn unrelated_failure_does_not_identify_a_mod() {
        let mut s = Session::start(catalog(), "a".repeat(64), 30).unwrap();
        answer(&mut s, Outcome::Passed);
        s.observe(
            &s.active.as_ref().unwrap().id.clone(),
            Outcome::Failed,
            Some("another-stage:different-error"),
        )
        .unwrap();
        assert_eq!(s.stage, "inconclusive-unrelated-failure");
        assert!(s.minimal_failing_group.is_empty());
    }
    #[test]
    fn missing_dependencies_and_incomplete_fallback_are_rejected() {
        let mut c = catalog();
        c.dependencies
            .get_mut("a")
            .unwrap()
            .insert("missing".into());
        assert!(Session::start(c, "a".repeat(64), 30).is_err());
        let mut c = catalog();
        c.verified_ring = BTreeSet::from(["a".into()]);
        assert!(Session::start(c, "a".repeat(64), 30).is_err());
    }
    #[test]
    fn budget_exhaustion_and_nonreproduction_remain_explicit() {
        let mut s = Session::start(catalog(), "a".repeat(64), 2).unwrap();
        answer(&mut s, Outcome::Passed);
        answer(&mut s, Outcome::Failed);
        assert_eq!(s.stage, "inconclusive-budget");
        let mut s = Session::start(catalog(), "a".repeat(64), 10).unwrap();
        answer(&mut s, Outcome::Passed);
        answer(&mut s, Outcome::Passed);
        assert_eq!(s.stage, "failure-not-reproduced");
    }
}
