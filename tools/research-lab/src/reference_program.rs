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
        fixed_inputs: vec!["Checkpoint bytes, parent receipt, vessel.parts.Count (including physicsless parts), mod/controller/settings and admitted launch-profile contents".into()],
        varied_inputs: vec!["One declared treatment; normal versus 4x is a different situation".into()],
        behavior_proof: vec!["Actual capture completion, unchanged topology/resources where required, owned cleanup and source restoration; observation is not deterministic replay".into()],
        cost_measurements: vec!["Frame distribution, achieved simulation rate, non-overlapping PlayerLoop scopes; intrusive diagnostics separately declared".into()],
        checkpoint_requirements: vec!["Explicit private save/checkpoint/SHA256, expected vessel.parts.Count (including physicsless parts), parent attempt and receipt SHA256".into()],
        lighting_recommendation: "Physically frontlit or side-lit for inspection; preserve backlit stress as a separate pinned recipe".into(), next_action: next.into() }
}
pub fn catalog() -> Vec<Recipe> {
    use Capability::*;
    let mut rows = vec![
        row("CSP",None,"program","Continuum Reference Space Program",Catalog,"Missions generate durable checkpoints and integration evidence","Choose a situation, then supply private pins"),
        row("CSP-STATION",Some("CSP"),"mission","Modular orbital station",Catalog,"Launch, rendezvous, repeated assembly and operation","Record a complete mission and preserve checkpoints at each transition"),
        row("station-vacuum",Some("CSP-STATION"),"situation","Intact station in vacuum",RequiresPrivatePins,"Loaded physical vessel; normal-time ten-simulation-second window","Supply intact Kerbin orbital checkpoint and launch profile"),
        row("station-vacuum-4x",Some("station-vacuum"),"micro-situation","Station under 4x physics warp",RequiresPrivatePins,"Same orbital checkpoint; LOW physics warp4; actual effective timestep recorded","Pair with normal-time situation without treating warp as a hidden difference"),
        row("station-pulse",Some("station-vacuum"),"micro-situation","Off-center impulse response",RequiresPrivatePins,"Stock physical forces; bounded pulse and topology witness","Supply the same intact checkpoint; retain quiet baseline"),
        row("station-rendezvous",Some("CSP-STATION"),"situation","Plane, phasing and intercept",Planned,"Two vessels, relative orbit and maneuver execution","Implement owned maneuver/relative-state driver and acceptance before calling this runnable"),
        row("station-docking",Some("station-rendezvous"),"micro-situation","Diagnostic docking roundtrip",RequiresPrivatePins,"Controlled diagnostic approach, stock capture/joint merge and stock undocking/split","Supply paired checkpoint, explicit vessel/port identities and declared fixture preparation; RCS and crewed mission remain separate"),
        row("station-undocking",Some("station-docking"),"micro-situation","Undock and separate",Planned,"Vessel split, resource/control ownership and relative motion","Implement undocking driver with identity/resource conservation"),
        row("station-loading",Some("CSP-STATION"),"situation","Multi-vessel packing/loading",Planned,"Packed/unpacked range transitions","Implement range driver and loaded-vessel/physics ownership ledger"),
        row("CSP-ENTRY",Some("CSP"),"mission","Intact atmospheric entry",Catalog,"Transfer from orbit through atmosphere; preserve failures","Freeze craft and approach recipe; breakup is invalid for intact measurement"),
        row("entry-normal",Some("CSP-ENTRY"),"situation","Already atmospheric normal-time flight",RequiresPrivatePins,"Kerbin atmospheric checkpoint; normal-time timed capture","Supply physically atmospheric checkpoint; no approach implied"),
        row("entry-frontlit",Some("CSP-ENTRY"),"micro-situation","Frontlit60km physical inspection",RequiresPrivatePins,"Declared Sunward orbital rotation before loading; intact60km capture,3simseconds","Supply intact entry checkpoint; require physical sun/view/body geometry and cleanup"),
        row("entry-side-lit",Some("CSP-ENTRY"),"micro-situation","Side-lit60km physical inspection",RequiresPrivatePins,"Same Sunward placement;75degree camera phase and limb composition","Supply same intact checkpoint/profile; inspect pixels, do not claim performance improvement"),
        row("entry-45km",Some("CSP-ENTRY"),"micro-situation","Cross45km before capture",RequiresPrivatePins,"Existing in-memory entry preparation;180s approach and60s capture deadlines","Supply supported checkpoint; intact topology required at admission"),
        row("CSP-0001",Some("CSP"),"mission","Minmus Pathfinder",ExistingMissionMode,"Existing launch-to-landing mission; sideways touchdown is not upright qualification","Use existing mission documentation; add owned-lab adapter before catalog command execution"),
        row("CSP-0002",Some("CSP"),"mission","Minmus Survey1",ExistingMissionMode,"Existing sunny-site targeted landing; historical trials missed100m target","Preserve unchanged target gate and historical failures"),
        row("minmus-orbit",Some("CSP-0002"),"situation","Minmus orbit checkpoint descent",ExistingMissionMode,"Existing checkpoint survey mode rebuilds fresh autopilot state","Bridge existing checkpoint/attempt flags to owned runner; do not pretend generic flight capture supports descent"),
        row("landing-contact",Some("minmus-orbit"),"micro-situation","Touchdown and settle",Planned,"Leg contact, attitude, intact craft, daylight and distance","Implement bounded contact capture and actual acceptance/cleanup"),
        row("scene-save-reload",Some("CSP"),"situation","Scene transition and save/reload",RequiresPrivatePins,"Declared quiet/engine-shutdown preparation; stock checkpoint save, deliberate velocity change, fresh scene owners and semantic reload","Supply an intact orbital checkpoint and explicit part count; frozen physical-orbit and presentation gates remain open"),
    ];
    rows.extend([
        row(
            "launch-burn-stage",
            Some("CSP"),
            "mission",
            "Launch, finite burn and staging",
            Planned,
            "Ascent, engine/resource flow, staging and plume transitions",
            "Implement launch/control driver with stage/engine/resource witnesses",
        ),
        row(
            "lunar-sortie",
            Some("CSP"),
            "mission",
            "Lunar science sortie and return",
            Planned,
            "Transfer, landing, science/EVA and return checkpoints",
            "Author vehicle/mission and observe each actual handoff",
        ),
        row(
            "lunar-science-eva",
            Some("lunar-sortie"),
            "situation",
            "Science and EVA surface work",
            Planned,
            "Crew/experiment state, EVA vessel split and reboarding",
            "Implement science/EVA driver and inventory/crew persistence proof",
        ),
        row(
            "lunar-return",
            Some("lunar-sortie"),
            "situation",
            "Ascent, transfer and recovery",
            Planned,
            "Surface departure, SOI transitions and recovered crew/science",
            "Implement return and recovery driver; preserve before/after saves",
        ),
        row(
            "map-render",
            Some("CSP"),
            "micro-situation",
            "Map zoom and orbital render inspection",
            Planned,
            "Scaled/local camera transition, zoom, labels and orbit geometry",
            "Implement bounded map camera driver and visual/state restoration",
        ),
        row(
            "dlc-robotics",
            Some("CSP-STATION"),
            "micro-situation",
            "Robotic deployment",
            Planned,
            "DLC servo articulation, resource draw and collision/contact",
            "Supply supported DLC craft; implement actuator driver and structural proof",
        ),
    ]);
    for recipe in &mut rows {
        let (systems,proof,factors) = match recipe.id.as_str() {
            "station-vacuum" => ("PhysX joints, FI/modules, render graph and controller scheduling", "Intact pinned part identities; normal-time simulation rate; physical vessel ownership", "Part count and rendering treatment; fixed orbit, settings and control policy"),
            "station-vacuum-4x" => ("Fixed-step pressure, catch-up budget and thermal/module scheduling", "Actual LOW4x warp and effective timestep; intact topology; no stale step publication", "Warp only against same normal-time checkpoint; record Unity timestep clamp"),
            "station-pulse" => ("Force publication, joint compliance, angular response and controller reaction", "Applied impulse/duration, selected body identity, flex/rotation response and zero breakup", "Pulse versus quiet with identical witness cadence; frozen force and selected part"),
            "station-rendezvous" => ("Orbit prediction, maneuver planning/execution and relative velocity control", "Planning is not execution; actual plane/phasing/intercept errors and fuel consumed", "Relative geometry, target and burn policy; fixed two-vessel identities"),
            "station-docking" => ("Port alignment, joint creation, vessel merge, resource/control ownership", "Actual dock state; conserved parts/resources/crew; one merged owner and restored hooks", "Relative velocity/alignment and RCS policy; fixed paired-craft close-approach checkpoint"),
            "station-undocking" => ("Joint removal, vessel split and independent scheduling", "Two actual vessel owners; conserved parts/resources/crew; no ghost force/controller owner", "Separation impulse and control assignment; fixed docked source"),
            "station-loading" => ("Packed ranges, loaded vessels, floating origin and physics construction", "Actual pack/unpack events, preserved orbit/resource state and no duplicate live ownership", "Range/relative speed; fixed vessels, topology and loading thresholds"),
            "entry-normal"|"entry-45km" => ("FI convection/occlusion, aero forces, heating, effects and structural physics", "Intact admission/capture; actual altitude/density/speed/heating; stock aero/thermal authority", "Entry altitude/vehicle or replacement; freeze atmosphere/velocity and do not conflate warp"),
            "entry-frontlit"|"entry-side-lit" => ("Physical lighting, material visibility, aero-effect composition and camera publication", "Actual phase/daylight/body geometry, decoded pixels, intact parts and exact camera cleanup", "Camera phase only across same declared Sunward orbit;3simseconds at60km, stock rendering"),
            "launch-burn-stage" => ("Thrust/resource flow, aero/plume rendering, staging and joint topology", "Executed burn and stage events; impulse/fuel accounting; intended surviving assembly", "Throttle/stage recipe and altitude; fixed vehicle, fuel, engine and atmosphere"),
            "lunar-sortie"|"lunar-return" => ("Orbital/SOI transitions, landing/ascent, science persistence and recovery", "Actual encounters/contact/recovery; retained crew/science; checkpoints linked to full mission", "Target/body/vehicle; fixed maneuver and mission acceptance criteria"),
            "lunar-science-eva" => ("Science data, crew inventory and EVA/reboarding ownership", "Actual experiment results and EVA/reboard; conserved crew/items and reload persistence", "Experiment/EVA sequence; fixed surface site, sunlight and craft"),
            "map-render" => ("Scaled-space rendering, camera/label/trajectory publication", "Actual zoom/scene coverage and unchanged physical state; restore map/camera mode", "Zoom and selected overlays; fixed orbit/time/viewport"),
            "dlc-robotics" => ("Servo/control updates, articulation/contact, electricity and rendering", "Actual actuator pose, travel limits, resource use and unchanged intended topology", "Deployment schedule/load; fixed DLC version, craft and joint recipe"),
            "landing-contact" => ("Landing guidance, rigidbody contact, legs and thermal/resource state", "Actual touchdown, upright settling/daylight and unchanged100m target criterion", "Guidance policy/terrain/contact; fixed checkpoint and target"),
            "scene-save-reload" => ("Serialization, scene teardown and live mod initialization", "Semantic state/resource/crew equality after reload and removed obsolete owners", "Scene/serialization treatment; fixed source and mod graph"),
            _ => ("Mission integration and durable checkpoint production", "Retain actual mission outcomes; no automatic success from catalog presence", "One declared mission/implementation question; preserve parent lineage"),
        };
        recipe.objectives = vec![systems.into()];
        recipe.behavior_proof.push(proof.into());
        recipe.varied_inputs = vec![factors.into()];
        recipe.cost_measurements.push(format!("Named systems: {systems}; full-window wall/frame cost and event latency; nested scopes not summed"));
        if recipe.id == "scene-save-reload" {
            recipe.behavior_proof = vec!["Stock shutdown readback before baseline; saved FLIGHT scene, exact saved whole-game membership and quicksave/post-load notifications".into(),
                "New live vessel/part objects; preserved IDs, parent graph, four-domain persistence, frozen orbit/time tolerances, actual before/after images and owned cleanup".into()];
            recipe.cost_measurements = vec!["Process startup and complete wall duration, ordered lifecycle event wall/simulation/frame coordinates; no frame distribution or exclusive serialization CPU collected by this driver".into()];
            recipe.varied_inputs = vec!["Declared stock quiet preparation and reload treatment; immutable source, saved baseline and mod graph".into()];
        }
    }
    rows
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
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub docking: Option<crate::docking::Selection>,
    pub profile: PathBuf,
    pub profile_sha256: String,
    pub instance: PathBuf,
    pub save: String,
    pub checkpoint: String,
    pub checkpoint_sha256: String,
    pub expected_parts: u32,
    pub parent_attempt: String,
    pub parent_receipt_sha256: String,
    pub parent_receipt: PathBuf,
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
    pub parent_provenance: String,
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
    let parent = verify_parent(inputs)?;
    let mut plan = compose(id, inputs, frozen.selection)?;
    plan.parent_provenance = parent;
    Ok(plan)
}
fn verify_parent(i: &Inputs) -> Result<String> {
    if std::fs::metadata(&i.parent_receipt)
        .map_err(|e| e.to_string())?
        .len()
        > 4 * 1024 * 1024
    {
        return Err("parent receipt too large".into());
    }
    if hash(&i.parent_receipt)? != i.parent_receipt_sha256 {
        return Err("parent receipt SHA256 differs".into());
    }
    let value: serde_json::Value = crate::read(&i.parent_receipt)?;
    let identity = value.get("runId").or_else(|| value.get("attemptId"));
    let known =
        value.get("schema").and_then(|v| v.as_str()) == Some("continuum-lab-process-smoke/v1");
    if known && value.get("status").and_then(|v| v.as_str()).is_none() {
        return Err("native parent receipt status missing".into());
    }
    let result = match identity {
        Some(value) if value.as_str() == Some(i.parent_attempt.as_str()) => {
            if known {
                "receipt-bytes-and-native-run-identity-verified"
            } else {
                "receipt-bytes-verified-lineage-declared-unknown-schema"
            }
        }
        Some(_) => return Err("parent receipt runId/attemptId differs".into()),
        None => "receipt-bytes-verified-lineage-declared-unknown-schema",
    };
    if hash(&i.parent_receipt)? != i.parent_receipt_sha256 {
        return Err("parent receipt changed during read".into());
    }
    Ok(result.into())
}
fn validate_inputs(i: &Inputs) -> Result<()> {
    if ![
        &i.profile,
        &i.instance,
        &i.reservation,
        &i.output,
        &i.derived_profile,
        &i.parent_receipt,
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
    selection.diagnostics.flight_lighting = None;
    selection.diagnostics.docking = None;
    selection.diagnostics.checkpoint_reload = false;
    selection.diagnostics.convection_replacement = None;
    let mut options = BTreeMap::from([
        ("--flight-save".into(), i.save.clone()),
        ("--flight-checkpoint".into(), i.checkpoint.clone()),
        ("--flight-max-delta".into(), "0.04".into()),
        ("--flight-seconds".into(), "10".into()),
        ("--flight-settle-seconds".into(), "2".into()),
        ("--flight-resolution".into(), "1920x1080".into()),
        ("--flight-screenshot".into(), "true".into()),
        (
            "--flight-expected-parts".into(),
            i.expected_parts.to_string(),
        ),
    ]);
    match id {
        "scene-save-reload" => {
            selection.diagnostics.checkpoint_reload = true;
            options.insert("--flight-research-panel".into(), "hidden".into());
        }
        "station-docking" => {
            let docking = i.docking.clone().ok_or("docking recipe requires an explicit pair selection")?;
            docking.validate()?;
            selection.diagnostics.docking = Some(docking);
            options.insert("--flight-seconds".into(), "30".into());
            options.insert("--flight-research-panel".into(), "hidden".into());
        }
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
        "entry-frontlit" | "entry-side-lit" => {
            options.insert("--flight-entry-target-altitude".into(), "60000".into());
            options.insert("--flight-seconds".into(), "3".into());
            options.insert("--flight-settle-seconds".into(), "0.5".into());
            selection.diagnostics.flight_lighting = Some(
                if id == "entry-frontlit" {
                    "frontlit"
                } else {
                    "side-lit"
                }
                .into(),
            );
        }
        "entry-45km" => {
            options.insert("--flight-entry-target-altitude".into(), "45000".into());
        }
        _ => return Err("catalog recipe lacks execution mapping".into()),
    }
    let request =
        flight::Request::parse(&options)?.ok_or("recipe did not compose a flight request")?;
    let mut player_arguments = request.arguments();
    if selection.diagnostics.checkpoint_reload {
        player_arguments.push("--continuum-checkpoint-reload".into());
    }
    if let Some(mode) = &selection.diagnostics.flight_lighting {
        player_arguments.push(format!("--continuum-flight-lighting={mode}"));
    }
    if let Some(docking) = &selection.diagnostics.docking {
        player_arguments.extend(docking.arguments());
    }
    selection.flight = Some(request);
    selection.live_play = false;
    selection.timeout_seconds = if id.starts_with("entry-") && id != "entry-normal" {
        360
    } else {
        180
    };
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
        qualification:"Observational recipe only. Expected parts are forwarded to native admission. Parent receipt bytes/known identity are verified by resolve; unknown schema lineage remains explicitly declared. Full mission proof and performance claims remain separate.",parent_provenance:"not-read-by-composition-only".into()})
}
#[cfg(test)]
mod tests {
    use super::*;
    fn pins() -> Inputs {
        Inputs {
            docking: None,
            profile: "/private/base.json".into(),
            profile_sha256: "a".repeat(64),
            instance: "/private/instance".into(),
            save: "scenarios".into(),
            checkpoint: "Station".into(),
            checkpoint_sha256: "b".repeat(64),
            expected_parts: 256,
            parent_attempt: "CSP-STATION-A001".into(),
            parent_receipt_sha256: "c".repeat(64),
            parent_receipt: "/private/parent-receipt.json".into(),
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
        let reload = compose("scene-save-reload", &pins(), base()).unwrap();
        assert!(reload.selection.diagnostics.checkpoint_reload);
        assert!(reload.player_arguments.contains(&"--continuum-checkpoint-reload".into()));
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
        let lit = compose("entry-side-lit", &pins(), base()).unwrap();
        assert_eq!(
            lit.selection.diagnostics.flight_lighting.as_deref(),
            Some("side-lit")
        );
        assert_eq!(lit.selection.flight.as_ref().unwrap().seconds, 3.);
        assert_eq!(
            lit.selection.flight.as_ref().unwrap().expected_parts,
            Some(256)
        );
        assert!(lit
            .player_arguments
            .contains(&"--continuum-scale-parts=256".into()));
        let warp = compose("station-vacuum-4x", &pins(), base()).unwrap();
        let mut paired = pins();
        paired.docking = Some(crate::docking::Selection::parse(
            "11111111-1111-1111-1111-111111111111,10,22222222-2222-2222-2222-222222222222,20", true,
        ).unwrap());
        let roundtrip = compose("station-docking", &paired, base()).unwrap();
        assert!(roundtrip.player_arguments.contains(&"--continuum-docking-prepare-pair".into()));
        assert!(roundtrip.player_arguments.contains(&"--continuum-docking-target-port=20".into()));
        assert!(roundtrip.selection.diagnostics.flight_lighting.is_none());
        assert_eq!(warp.selection.flight.unwrap().warp, 4);
    }
    #[test]
    fn reference_program_rejects_unsupported_and_missing_pins() {
        assert!(compose("station-docking", &pins(), base())
            .err()
            .unwrap()
            .contains("explicit pair"));
        let mut i = pins();
        i.checkpoint = "../escape".into();
        assert!(compose("station-vacuum", &i, base()).is_err());
        i = pins();
        i.parent_receipt_sha256.clear();
        assert!(compose("station-vacuum", &i, base()).is_err());
        let temp = tempfile::tempdir().unwrap();
        i = pins();
        i.parent_receipt = temp.path().join("parent.json");
        std::fs::write(
            &i.parent_receipt,
            br#"{"schema":"continuum-lab-process-smoke/v1","runId":"foreign","status":"passed"}"#,
        )
        .unwrap();
        i.parent_receipt_sha256 = hash(&i.parent_receipt).unwrap();
        assert!(verify_parent(&i).unwrap_err().contains("differs"));
        i.parent_attempt = "foreign".into();
        assert_eq!(
            verify_parent(&i).unwrap(),
            "receipt-bytes-and-native-run-identity-verified"
        );
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
