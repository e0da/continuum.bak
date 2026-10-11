# Stock checkpoint reconstruction

`scene-save-reload` in the [reference program](reference-space-program.md) is an opt-in quiet-vacuum situation. Supply the selected private checkpoint/profile/parent receipt hashes and expected active part count to `program-prepare`, then execute its emitted run arguments. It does not enable the convection or render replacements.

The driver waits for continuous physical readiness over the declared settling interval, admits one active physical vessel with normal time and inactive SAS/RCS/autopilot, and uses stock shutdown for any armed zero-throttle engines. Their actual part/module identity and ignition readbacks are recorded before the baseline. A nonzero throttle or another physical vessel fails admission.

It captures a visible baseline, writes a unique checkpoint into the selected writable save folder with stock `SaveGame`, and deliberately changes root velocity by 0.25 m/s using stock `SetWorldVelocity`. It then follows stock quickload completion: fresh `LoadGame`, scenario update, post-load event and `StartAndFocusVessel`. A persistent coordinator releases the old camera/input lease before dispatch, reacquires the fresh physical vessel, and never reads the old live objects after reload.

Stock `Game.Updated` selects the space-center start scene for this game mode. The driver follows stock quicksave by notifying `onGameAboutToQuicksave`, temporarily setting the serialized scene to `FLIGHT` and restoring the live scene value in a `finally` block. The loaded scene and whole-game partition are retained as independent readbacks.

The result must show new vessel and part objects with restored persistent IDs, parent graph, part names, ordered module types, crew, resources and primitive persistent KSPFields. The saved whole-game vessel/part membership is checked. Both orbital states are evaluated at the saved epoch; fixed tolerances are 1 cm position and 0.00001 m/s velocity. Reload may advance at most two simulated seconds. Resource tolerance is 0.000001, with a separately recorded command-module electrical consumption budget. Floating field tolerances are 0.000001 absolute plus 0.000001 relative; integral and textual fields compare exactly.

Before/after PNGs, exact event order, source/saved hashes and actual hook, control-lock and camera restoration are required. The runner retains raw evidence before validation and independently checks fresh owners, numerical/resource/field witnesses, engine preparation and artifact ownership. Its private retained checkpoint does not enter Git.

This scope does not establish arbitrary mod `OnSave` coverage, full scenario equality, deterministic replay, visual equivalence, endurance or improved load time. Failed attempts retain their original receipts; fixes must address a concrete changed cause without widening the frozen gates.

## Observed result

The 256-part stock roundtrip recreated every vessel/part object and preserved IDs, parent links, four crew, ordered module types, 2,505 primitive persistent fields and all 64 resource rows. Seven ordered events, deliberate velocity departure, immutable source/saved bytes and actual camera/lock/hook cleanup passed. Both rendered images contain the craft, Kerbin and UI.

The overall result is **invalid**, with exactly the physical-orbit gate failed. The loaded serialized orbit reconstructs the saved-epoch state within 2.10e-10 m position and 1.14e-13 m/s velocity. After physical unpack, saved-epoch position differs by 0.106874 m, above the unchanged 0.01 m limit; velocity differs by 1.122e-6 m/s and passes. The prior observation agrees. This locates the discrepancy after logical orbit loading; it does not yet attribute it to a particular physical operation.

The images also expose noisy crew portraits and a changed stage delta-V display despite exact resource equality. Clean presentation remains unqualified. Event coordinates provide lifecycle latency; this driver does not collect exclusive serialization cost or a flight FPS distribution. No unchanged roundtrip replay is needed before investigating physical placement/center-of-mass reconstruction and the presentation defects.
