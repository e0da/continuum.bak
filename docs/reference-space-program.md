# Reference space program

The existing [space program](space-program.md) owns missions and their history. This catalog adds a selectable hierarchy: **program → mission → situation → micro-situation**. Full missions generate checkpoints and integration evidence; short situations measure a named workload without replaying the entire mission.

A mission outcome, runnable recipe, successful native observation and proven improvement are distinct states. Minmus Pathfinder's sideways touchdown and Survey1's missed100m target remain unchanged historical outcomes in [the mission documentation](minmus-mission.md). No catalog row upgrades them.

## Catalog and execution

`tools/research-lab/src/reference_program.rs` provides `catalog()`, `list_json()` and `resolve(recipe_id, &Inputs)`. Root CLI integration imports this module; it does not introduce another runner.

The catalog includes station assembly, intact vacuum normal/4x, an off-center pulse, atmospheric flight and a45km entry window. Their executable mappings require explicit private pins. Rendezvous, docking/undocking, multi-vessel loading transitions, landing/contact and scene/save/reload remain planned. Existing Minmus mission/checkpoint modes are recorded, but catalog resolution rejects them until an owned-run adapter is present.

Each row names its parent, boundary, scientific/performance objective, fixed and varied inputs, behavior checks, measurements, checkpoint prerequisites, lighting recommendation and next resolving action. Parent links are acyclic and must resolve to catalog identities.

Resolution requires an absolute private launch-profile path and matching SHA256, owned instance, save/checkpoint leaf names and matching checkpoint SHA256, declared physical-part count, parent attempt/receipt digest, reservation, run output and a distinct derived-profile destination. It checks the supplied profile/checkpoint bytes. Parent digest and expected parts remain declarations until integration compares actual parent evidence and native reconstruction.

The returned plan contains a normalized existing `flight::Request`, a derived `ResolvedLaunch`, exact player arguments and exact existing `run` arguments. The caller must use `FrozenLaunch::capture` to bind and persist the **new** selection before running. It cannot reuse the old profile's identity after changing flight options. Planning neither launches KSP nor writes a profile. Existing source safeguards, reservation, process ownership, admission and receipt validation remain authoritative.

Default short windows are10 simulated seconds after2 simulated seconds of settling at1920×1080. Atmospheric entry permits180seconds approach plus60seconds capture inside a360second process deadline. Warp4 is its own declared treatment; atmospheric4x is not synthesized. Actual topology, effective timestep, capture completion and cleanup must pass the current runner. Lighting recommendations are descriptive until a separately pinned physical preset is selected. Frontlit/side-lit inspection and backlit stress are different workloads.

Use two resolving observations, then advance. Every new native run should change the implementation or answer a named uncertainty. Short measurements cannot establish maneuver execution, contact correctness or a complete mission.

## Player-derived workload boundaries

A player building a modular250km station reported repeated alignment and drift problems, making station assembly a useful integration mission. A paired docking trainer allows practising docking without first completing rendezvous. This motivates separate mission and short-approach checkpoints; it does not establish an automated driver. [Player report and trainer discussion](https://forum.kerbalspaceprogram.com/topic/175134-any-veteran-tips-for-the-docking-inept/).

An authored rendezvous guide separates orbital plane/circularization prerequisites from phasing and the intercept burn. That supports distinct situations for orbit alignment, intercept and close approach rather than one opaque docking benchmark. [Orbital Rendezvous Made Easy](https://forum.kerbalspaceprogram.com/topic/8903-tutorial-orbital-rendezvous-made-easy/).

A practice recipe preserves quicksaves before rendezvous and final docking, then repeats the short approach. We borrow checkpoint decomposition, while retaining our current two-observation protocol and exact native success criteria. [Checkpoint practice discussion](https://forum.kerbalspaceprogram.com/topic/73877-rendezvous-for-dummies-who-inexplicably-dont-get-it/).

## Qualification limits

No new native qualification is claimed here. Private checkpoint availability is not assumed or committed. Generic catalog selection does not execute docking, landing, maneuvers or Minmus autopilot handoffs. Existing live modules/controllers stay authoritative. Source/checkpoint byte equality does not prove identical running autopilot memory, physics state or deterministic replay.
