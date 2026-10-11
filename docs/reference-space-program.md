# Reference space program

The existing [space program](space-program.md) owns missions and their history. This catalog adds a selectable hierarchy: **program → mission → situation → micro-situation**. Full missions generate checkpoints and integration evidence; short situations measure a named workload without replaying the entire mission.

A mission outcome, runnable recipe, successful native observation and proven improvement are distinct states. Minmus Pathfinder's sideways touchdown and Survey1's missed 100 m target remain unchanged historical outcomes in [the mission documentation](minmus-mission.md). No catalog row upgrades them.

## Catalog and execution

`tools/research-lab/src/reference_program.rs` provides `catalog()`, `list_json()` and `resolve(recipe_id, &Inputs)`. The research-lab CLI exposes `program-catalog`, `program-plan` and `program-prepare`, using the existing frozen-launch and run operations.

The catalog includes station assembly, intact vacuum normal/4x, an off-center pulse, atmospheric flight and a 45 km entry window. Their executable mappings require explicit private pins. Diagnostic stock docking/undocking and [checkpoint save/reload](checkpoint-reload.md) have owned drivers. Rendezvous, multi-vessel loading transitions and landing/contact remain planned. Existing Minmus mission/checkpoint modes are recorded, but catalog resolution rejects them until an owned-run adapter is present.

Each row names its parent, boundary, scientific/performance objective, fixed and varied inputs, behavior checks, measurements, checkpoint prerequisites, lighting recommendation and next resolving action. Parent links are acyclic and must resolve to catalog identities.

Resolution requires an absolute private launch-profile path and matching SHA256, owned instance, save/checkpoint leaf names and matching checkpoint SHA256, expected vessel.parts.Count (including physicsless parts), parent receipt path/identity/digest, reservation, run output and a distinct derived-profile destination. It checks exact profile/checkpoint/parent receipt bytes. Known native continuum-lab-process-smoke/v1 receipts require the supplied parent identity to match runId; unknown schemas retain explicitly declared lineage. Expected vessel parts are forwarded to native admission and checked throughout capture. Rigidbodies, joints and modules are separate measurements.

The returned plan contains a normalized existing `flight::Request`, a derived `ResolvedLaunch`, exact player arguments and exact existing `run` arguments. `program-plan` writes a plan without launching KSP or writing a launch profile. `program-prepare` uses `FrozenLaunch::capture` to bind and persist the **new** selection before running, and emits its plan and frozen-profile digest. It cannot reuse the old profile's identity after changing flight options. Existing source safeguards, reservation, process ownership, admission and receipt validation remain authoritative.

Nine recipes are selectable with private pins: station-vacuum, station-vacuum-4x, station-pulse, entry-normal, entry-45km, entry-frontlit, entry-side-lit, station-docking and scene-save-reload. The two visual entry recipes prepare a declared Sunward orbital rotation, target 60 km, capture 3 simulated seconds after 0.5 wall-clock seconds of settling, and select stock rendering with the physical frontlit/side-lit camera contract. They inspect actual pixels, geometry and cleanup; they do not establish improved performance. The 45 km recipe remains conditional on intact admission; historical breakup is preserved. Docking and reload use their own lifecycle event/behavior evidence rather than generic ten-second frame captures.

Other short windows are 10 simulated seconds after 2 wall-clock seconds of settling at 1920×1080. Atmospheric entry permits 180 wall-clock seconds of approach plus 60 wall-clock seconds for capture inside a 360-second process deadline. Warp 4 is its own declared treatment; atmospheric 4x is not synthesized. Actual topology, effective timestep, capture completion and cleanup must pass the current runner. Lighting recommendations outside those two recipes are descriptive until a separately pinned physical preset is selected. Frontlit/side-lit inspection and backlit stress are different workloads.

Use two resolving observations, then advance. Every new native run should change the implementation or answer a named uncertainty. Short measurements cannot establish maneuver execution, contact correctness or a complete mission.

## Player-derived workload boundaries

A player building a modular 250 km station reported repeated alignment and drift problems, making station assembly a useful integration mission. A paired docking trainer allows practising docking without first completing rendezvous. This motivates separate mission and short-approach checkpoints; it does not establish an automated driver. [Player report and trainer discussion](https://forum.kerbalspaceprogram.com/topic/175134-any-veteran-tips-for-the-docking-inept/).

An authored rendezvous guide separates orbital plane/circularization prerequisites from phasing and the intercept burn. That supports distinct situations for orbit alignment, intercept and close approach rather than one opaque docking benchmark. [Orbital Rendezvous Made Easy](https://forum.kerbalspaceprogram.com/topic/8903-tutorial-orbital-rendezvous-made-easy/).

A practice recipe preserves quicksaves before rendezvous and final docking, then repeats the short approach. We borrow checkpoint decomposition, while retaining our current two-observation protocol and exact native success criteria. [Checkpoint practice discussion](https://forum.kerbalspaceprogram.com/topic/73877-rendezvous-for-dummies-who-inexplicably-dont-get-it/).

## Qualification limits

One frontlit and one side-lit entry capture have passed on the private 512-part checkpoint. Both preserved part count, met their physical Sun/camera geometry contract, exited successfully and restored the camera. Actual images were inspected: central parts and panels are visible in the frontlit view; the side-lit view shows the craft against Kerbin's limb and starfield. The long booms extend beyond the frame and the diagnostic panel remains visible. These observations establish usable lighting recipes for that checkpoint, not ideal composition, visual fidelity or a performance improvement.

Private checkpoint availability is not assumed or committed. The diagnostic docking driver executes its explicit approach/merge/split scope; ordinary rendezvous, landing, maneuvers and Minmus autopilot handoffs remain separate. Existing live modules/controllers stay authoritative. Source/checkpoint byte equality does not prove identical running autopilot memory, physics state or deterministic replay.

## Specific research objectives

Docking and undocking measure actual joint/vessel merge and split, resource/crew conservation and control ownership. Loading transitions measure packed ranges and duplicate physics owners. Impulse situations measure force publication, compliance, angular response and intact topology. Atmospheric situations separate convection/heating and effect rendering from structural physics; 4x pressure is a different workload.

Planned launch/burn/stage work records thrust/fuel accounting, executed stage topology and plumes. A lunar sortie produces transfer, contact, science/EVA and return checkpoints with crew/science persistence. Map inspection isolates zoom, scaled/local cameras, labels and orbital geometry. DLC robotics records servo limits, articulation/contact and electrical use. These rows have no runnable driver yet.

Maneuver planning and execution are separate objectives: plane match, transfer and relative-velocity matching need actual burn/arrival evidence. Resonant-orbit constellation deployment is a useful later satellite mission, not an available recipe. [Primary MechJeb maneuver documentation](https://github.com/MuMech/MechJeb2/wiki/Maneuver-Planner).
