//! Mission lineage and bounded measurement recipes; execution remains the existing frozen-launch runner.
use crate::{flight, hash, launch::ResolvedLaunch, profile::FrozenLaunch, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::PathBuf,
};

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Capability {
    Catalog,
    RequiresPrivatePins,
    ExistingMissionMode,
    Planned,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Recipe {
    pub id: String,
    pub parent: Option<String>,
    pub kind: String,
    pub name: String,
    pub capability: Capability,
    pub boundary: String,
    pub objectives: Vec<String>,
    pub fixed_inputs: Vec<String>,
    pub varied_inputs: Vec<String>,
    pub behavior_proof: Vec<String>,
    pub cost_measurements: Vec<String>,
    pub checkpoint_requirements: Vec<String>,
    pub lighting_recommendation: String,
    pub next_action: String,
}
fn row(
    id: &str,
    parent: Option<&str>,
    kind: &str,
    name: &str,
    capability: Capability,
    boundary: &str,
    next: &str,
) -> Recipe {
    Recipe { id: id.into(), parent: parent.map(str::to_owned), kind: kind.into(), name: name.into(), capability,
        boundary: boundary.into(), objectives: vec!["Observe faithful state/ownership transitions and complete-workload cost".into()],
        fixed_inputs: vec!["Checkpoint bytes, parent receipt, physical parts, mod/controller/settings and admitted launch-profile contents".into()],
        varied_inputs: vec!["One declared treatment; normal versus 4x is a different situation".into()],
        behavior_proof: vec!["Actual capture completion, unchanged topology/resources where required, owned cleanup and source restoration; observation is not deterministic replay".into()],
        cost_measurements: vec!["Frame distribution, achieved simulation rate, non-overlapping PlayerLoop scopes; intrusive diagnostics separately declared".into()],
        checkpoint_requirements: vec!["Explicit private save/checkpoint/SHA256, expected physical parts, parent attempt and receipt SHA256".into()],
        lighting_recommendation: "Physically frontlit or side-lit for inspection; preserve backlit stress as a separate pinned recipe".into(), next_action: next.into() }
}
pub fn catalog() -> Vec<Recipe> {
    use Capability::*;
    vec![
        row("CSP",None,"program","Continuum Reference Space Program",Catalog,"Missions generate durable checkpoints and integration evidence","Choose a situation, then supply private pins"),
        row("CSP-STATION",Some("CSP"),"mission","Modular orbital station",Catalog,"Launch, rendezvous, repeated assembly and operation","Record a complete mission and preserve checkpoints at each transition"),
        row("station-vacuum",Some("CSP-STATION"),"situation","Intact station in vacuum",RequiresPrivatePins,"Loaded physical vessel; normal-time ten-simulation-second window","Supply intact Kerbin orbital checkpoint and launch profile"),
        row("station-vacuum-4x",Some("station-vacuum"),"micro-situation","Station under 4x physics warp",RequiresPrivatePins,"Same orbital checkpoint; LOW physics warp4; actual effective timestep recorded","Pair with normal-time situation without treating warp as a hidden difference"),
        row("station-pulse",Some("station-vacuum"),"micro-situation","Off-center impulse response",RequiresPrivatePins,"Stock physical forces; bounded pulse and topology witness","Supply the same intact checkpoint; retain quiet baseline"),
        row("station-rendezvous",Some("CSP-STATION"),"situation","Plane, phasing and intercept",Planned,"Two vessels, relative orbit and maneuver execution","Implement owned maneuver/relative-state driver and acceptance before calling this runnable"),
        row("station-docking",Some("station-rendezvous"),"micro-situation","Short approach and docking",Planned,"Port alignment, relative velocity, contact and vessel merge","Create paired-craft short-approach checkpoint and actual docking driver/proof"),
        row("station-undocking",Some("station-docking"),"micro-situation","Undock and separate",Planned,"Vessel split, resource/control ownership and relative motion","Implement undocking driver with identity/resource conservation"),
        row("station-loading",Some("CSP-STATION"),"situation","Multi-vessel packing/loading",Planned,"Packed/unpacked range transitions","Implement range driver and loaded-vessel/physics ownership ledger"),
        row("CSP-ENTRY",Some("CSP"),"mission","Intact atmospheric entry",Catalog,"Transfer from orbit through atmosphere; preserve failures","Freeze craft and approach recipe; breakup is invalid for intact measurement"),
        row("entry-normal",Some("CSP-ENTRY"),"situation","Already atmospheric normal-time flight",RequiresPrivatePins,"Kerbin atmospheric checkpoint; normal-time timed capture","Supply physically atmospheric checkpoint; no approach implied"),
        row("entry-45km",Some("CSP-ENTRY"),"micro-situation","Cross45km before capture",RequiresPrivatePins,"Existing in-memory entry preparation;180s approach and60s capture deadlines","Supply supported checkpoint; intact topology required at admission"),
        row("CSP-0001",Some("CSP"),"mission","Minmus Pathfinder",ExistingMissionMode,"Existing launch-to-landing mission; sideways touchdown is not upright qualification","Use existing mission documentation; add owned-lab adapter before catalog command execution"),
        row("CSP-0002",Some("CSP"),"mission","Minmus Survey1",ExistingMissionMode,"Existing sunny-site targeted landing; historical trials missed100m target","Preserve unchanged target gate and historical failures"),
        row("minmus-orbit",Some("CSP-0002"),"situation","Minmus orbit checkpoint descent",ExistingMissionMode,"Existing checkpoint survey mode rebuilds fresh autopilot state","Bridge existing checkpoint/attempt flags to owned runner; do not pretend generic flight capture supports descent"),
        row("landing-contact",Some("minmus-orbit"),"micro-situation","Touchdown and settle",Planned,"Leg contact, attitude, intact craft, daylight and distance","Implement bounded contact capture and actual acceptance/cleanup"),
        row("scene-save-reload",Some("CSP"),"situation","Scene transition and save/reload",Planned,"Persistence, mod initialization and ownership restoration","Implement transition driver and semantic reload comparison"),
    ]
}
pub fn validate_catalog(rows: &[Recipe]) -> Result<()> {
    let ids: BTreeSet<_> = rows.iter().map(|r| r.id.as_str()).collect();
    if ids.len() != rows.len() {
        return Err("duplicate program identity".into());
    }
    for row in rows {
        let mut seen = BTreeSet::new();
        let mut current = Some(row.id.as_str());
        while let Some(id) = current {
            if !seen.insert(id) {
                return Err("program parent cycle".into());
            }
            current = rows
                .iter()
                .find(|r| r.id == id)
                .ok_or("missing program parent")?
                .parent
                .as_deref();
        }
    }
    Ok(())
}
pub fn list_json() -> Result<String> {
    let rows = catalog();
    validate_catalog(&rows)?;
    serde_json::to_string_pretty(&rows).map_err(|e| e.to_string())
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Inputs {
    pub profile: PathBuf,
    pub profile_sha256: String,
    pub instance: PathBuf,
    pub save: String,
    pub checkpoint: String,
    pub checkpoint_sha256: String,
    pub expected_parts: u32,
    pub parent_attempt: String,
    pub parent_receipt_sha256: String,
    pub reservation: PathBuf,
    pub output: PathBuf,
    pub derived_profile: PathBuf,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Plan {
    pub schema: &'static str,
    pub recipe: Recipe,
    pub inputs: Inputs,
    pub selection: ResolvedLaunch,
    pub player_arguments: Vec<String>,
    pub run_arguments: Vec<String>,
    pub required_action: &'static str,
    pub qualification: &'static str,
}
fn digest(s: &str) -> bool {
    s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit())
}
pub fn resolve(id: &str, inputs: &Inputs) -> Result<Plan> {
    validate_inputs(inputs)?;
    if hash(&inputs.profile)? != inputs.profile_sha256 {
        return Err("private launch profile SHA256 differs".into());
    }
    let frozen = FrozenLaunch::load(&inputs.profile)?;
    if frozen.instance_root != inputs.instance {
        return Err("private profile belongs to another instance".into());
    }
    let checkpoint = inputs
        .instance
        .join("saves")
        .join(&inputs.save)
        .join(format!("{}.sfs", inputs.checkpoint));
    validate_inputs(inputs)?;
    if hash(&checkpoint)? != inputs.checkpoint_sha256 {
        return Err("private checkpoint SHA256 differs".into());
    }
    compose(id, inputs, frozen.selection)
}
fn validate_inputs(i: &Inputs) -> Result<()> {
    if ![
        &i.profile,
        &i.instance,
        &i.reservation,
        &i.output,
        &i.derived_profile,
    ]
    .iter()
    .all(|p| p.is_absolute() && p.to_str().is_some())
        || ![
            &i.profile_sha256,
            &i.checkpoint_sha256,
            &i.parent_receipt_sha256,
        ]
        .iter()
        .all(|s| digest(s))
        || !(1..=8192).contains(&i.expected_parts)
        || i.parent_attempt.trim().is_empty()
        || i.derived_profile == i.profile
        || [&i.save, &i.checkpoint].iter().any(|s| {
            s.is_empty()
                || s.as_str() == "."
                || s.as_str() == ".."
                || s.bytes()
                    .any(|b| !b.is_ascii_alphanumeric() && !b"._- ".contains(&b))
        })
    {
        return Err("recipe needs absolute private paths, distinct derived profile, checkpoint names/digests, expected parts and parent receipt identity".into());
    }
    Ok(())
}
fn compose(id: &str, i: &Inputs, mut selection: ResolvedLaunch) -> Result<Plan> {
    validate_inputs(i)?;
    let recipe = catalog()
        .into_iter()
        .find(|r| r.id == id)
        .ok_or("unknown program recipe")?;
    if !matches!(recipe.capability, Capability::RequiresPrivatePins) {
        return Err(format!(
            "{} is not executable through this runner: {}",
            id, recipe.next_action
        ));
    }
    let mut options = BTreeMap::from([
        ("--flight-save".into(), i.save.clone()),
        ("--flight-checkpoint".into(), i.checkpoint.clone()),
        ("--flight-max-delta".into(), "0.04".into()),
        ("--flight-seconds".into(), "10".into()),
        ("--flight-settle-seconds".into(), "2".into()),
        ("--flight-resolution".into(), "1920x1080".into()),
        ("--flight-screenshot".into(), "true".into()),
    ]);
    match id {
        "station-vacuum" => {}
        "station-vacuum-4x" => {
            options.insert("--flight-warp".into(), "4".into());
        }
        "station-pulse" => {
            options.insert("--flight-perturbation".into(), "off-center-pulse".into());
        }
        "entry-normal" => {
            options.insert("--flight-atmosphere".into(), "true".into());
        }
        "entry-45km" => {
            options.insert("--flight-entry-target-altitude".into(), "45000".into());
        }
        _ => return Err("catalog recipe lacks execution mapping".into()),
    }
    let request =
        flight::Request::parse(&options)?.ok_or("recipe did not compose a flight request")?;
    let player_arguments = request.arguments();
    selection.flight = Some(request);
    selection.live_play = false;
    selection.timeout_seconds = if id == "entry-45km" { 360 } else { 180 };
    selection.validate()?;
    let run_arguments = vec![
        "run".into(),
        "--instance".into(),
        i.instance.to_str().unwrap().into(),
        "--reservation".into(),
        i.reservation.to_str().unwrap().into(),
        "--output".into(),
        i.output.to_str().unwrap().into(),
        "--launch-profile".into(),
        i.derived_profile.to_str().unwrap().into(),
    ];
    Ok(Plan {schema:"continuum-reference-program-plan/v1",recipe,inputs:i.clone(),selection,player_arguments,run_arguments,
        required_action:"Caller must capture/persist a NEW FrozenLaunch from selection, recheck checkpoint/profile pins and parent receipt, then execute existing run argv. No launch or frozen-profile mutation occurs during planning.",
        qualification:"Observational recipe only. Supplied expectedParts and parent pins are declarations: runner/integration must compare native parts/parent evidence before qualification. Full mission proof and performance claims remain separate."})
}
#[cfg(test)]
mod tests {
    use super::*;
    fn pins() -> Inputs {
        Inputs {
            profile: "/private/base.json".into(),
            profile_sha256: "a".repeat(64),
            instance: "/private/instance".into(),
            save: "scenarios".into(),
            checkpoint: "Station".into(),
            checkpoint_sha256: "b".repeat(64),
            expected_parts: 256,
            parent_attempt: "CSP-STATION-A001".into(),
            parent_receipt_sha256: "c".repeat(64),
            reservation: "/private/reservation.json".into(),
            output: "/private/run-01".into(),
            derived_profile: "/private/derived.json".into(),
        }
    }
    fn base() -> ResolvedLaunch {
        ResolvedLaunch::parse(&BTreeMap::new()).unwrap()
    }
    #[test]
    fn reference_program_exact_composed_invocation() {
        let p = compose("entry-45km", &pins(), base()).unwrap();
        assert_eq!(p.selection.timeout_seconds, 360);
        assert_eq!(
            p.selection.flight.as_ref().unwrap().entry_target_altitude,
            Some(45000.)
        );
        assert!(p
            .player_arguments
            .contains(&"--continuum-flight-entry-target=45000".into()));
        assert_eq!(
            p.run_arguments,
            vec![
                "run",
                "--instance",
                "/private/instance",
                "--reservation",
                "/private/reservation.json",
                "--output",
                "/private/run-01",
                "--launch-profile",
                "/private/derived.json"
            ]
        );
        let warp = compose("station-vacuum-4x", &pins(), base()).unwrap();
        assert_eq!(warp.selection.flight.unwrap().warp, 4);
    }
    #[test]
    fn reference_program_rejects_unsupported_and_missing_pins() {
        assert!(compose("station-docking", &pins(), base())
            .err()
            .unwrap()
            .contains("driver"));
        let mut i = pins();
        i.checkpoint = "../escape".into();
        assert!(compose("station-vacuum", &i, base()).is_err());
        i = pins();
        i.parent_receipt_sha256.clear();
        assert!(compose("station-vacuum", &i, base()).is_err());
    }
    #[test]
    fn reference_program_parent_integrity() {
        let mut rows = catalog();
        validate_catalog(&rows).unwrap();
        rows[0].parent = Some("station-docking".into());
        assert!(validate_catalog(&rows).is_err());
        rows = catalog();
        rows[1].parent = Some("missing".into());
        assert!(validate_catalog(&rows).is_err());
    }
}
