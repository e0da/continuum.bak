# Stock checkpoint reconstruction

`scene-save-reload` in the [reference program](reference-space-program.md) is an opt-in quiet-vacuum situation. Supply the selected private checkpoint/profile/parent receipt hashes and expected active part count to `program-prepare`, then execute its emitted run arguments. It does not enable the convection or render replacements.

The driver waits for continuous physical readiness over the declared settling interval, admits one active physical vessel with normal time and inactive SAS/RCS/autopilot, and uses stock shutdown for any armed zero-throttle engines. Their actual part/module identity and ignition readbacks are recorded before the baseline. A nonzero throttle or another physical vessel fails admission.

It captures a visible baseline, writes a unique checkpoint into the selected writable save folder with stock `SaveGame`, and deliberately changes root velocity by 0.25 m/s using stock `SetWorldVelocity`. The default coordinator follows the declared quickload completion sequence: fresh `LoadGame`, scenario update, post-load event and `StartAndFocusVessel`. `CONTINUUM_RELOAD_DRIVER=stock` instead invokes the actual pinned `QuickSaveLoad.quickLoad` producer, including its upgrade pipeline. A persistent coordinator releases the old camera/input lease before dispatch, reacquires the fresh physical vessel, and never reads the old live objects after reload.

Stock `Game.Updated` selects the space-center start scene for this game mode. The driver follows stock quicksave by notifying `onGameAboutToQuicksave`, temporarily setting the serialized scene to `FLIGHT` and restoring the live scene value in a `finally` block. The loaded scene and whole-game partition are retained as independent readbacks.

The result must show new vessel and part objects with restored persistent IDs, parent graph, part names, ordered module types, crew, resources and primitive persistent KSPFields. The saved whole-game vessel/part membership is checked. Both orbital states are evaluated at the saved epoch; fixed tolerances are 1 cm position and 0.00001 m/s velocity. Reload may advance at most two simulated seconds. Resource tolerance is 0.000001, with a separately recorded command-module electrical consumption budget. Floating field tolerances are 0.000001 absolute plus 0.000001 relative; integral and textual fields compare exactly.

Before/after PNGs, exact event order, source/saved hashes and actual hook, control-lock and camera restoration are required. The runner retains raw evidence before validation and independently checks fresh owners, numerical/resource/field witnesses, engine preparation and artifact ownership. Its private retained checkpoint does not enter Git.

This scope does not establish arbitrary mod `OnSave` coverage, full scenario equality, deterministic replay, visual equivalence, endurance or improved load time. Failed attempts retain their original receipts; fixes must address a concrete changed cause without widening the frozen gates.

## Stock-only result

The 256-part stock roundtrip recreated every vessel/part object and preserved IDs, parent links, four crew, ordered module types, 2,505 primitive persistent fields and all 64 resource rows. Seven ordered events, deliberate velocity departure, immutable source/saved bytes and actual camera/lock/hook cleanup passed. Both rendered images contain the craft, Kerbin and UI.

The overall result is **invalid**, with exactly the physical-orbit gate failed. The loaded serialized orbit reconstructs the saved-epoch state within 2.10e-10 m position and 1.14e-13 m/s velocity. After physical unpack, saved-epoch position differs by 0.106874 m, above the unchanged 0.01 m limit; velocity differs by 1.122e-6 m/s and passes. The prior observation agrees. This locates the discrepancy after logical orbit loading; it does not yet attribute it to a particular physical operation.

The images show the portrait transition overlay and a changed stage delta-V display despite exact resource equality. Stock starts a portrait with `NO_SIGNAL`, then switches to its avatar after a default three-second timer, independently of asset readiness. The avatar is a 3D camera updated at a randomized 0.10–0.15 Unity game-time interval; interior sunlight follows vessel orientation. A noisy snapshot alone is expected transition behavior, not a rendering defect. The actual overlay/avatar state and the delta-V producer's readiness were not retained, so visual equivalence remains unqualified.

Event coordinates provide lifecycle latency; this driver does not collect exclusive serialization cost or a flight FPS distribution. No unchanged roundtrip replay is needed before observing physical placement/center-of-mass reconstruction.

## Construction-order experiments

The stock driver observes the actual producer's scenario update, post-load notification and dispatch, and admits the same produced `Game` before original flight dispatch. Mission-start persistence branches are unsupported. A requested physical witness waits for the first original `TrackRigidbody` input and complete output within the existing wall and simulation limits; physical readiness alone does not establish that transition. Stock pause state remains unchanged.

The selected 256-part stock-produced quickload passed with the packed treatment: one original mass invocation, all 170 packed body publications, saved-epoch position error 0.000001718 m and velocity error 1.321e-9 m/s. Original physical tracking, fresh owners, crew, modules, fields, resources and whole-game membership passed unchanged gates, with owned hooks, camera and locks restored. An earlier attempt remains invalid because verification removed the observer before that first physical transition. This supports the selected actual stock pipeline; arbitrary mods, every entry point and ordinary enabled deployment remain unqualified.

`CONTINUUM_RELOAD_PHYSICAL_WITNESS=1` records saved/live/canonical centers and the first original `OrbitDriver.TrackRigidbody` call. It preserves the original call and all numerical gates. Its geometry and timestamps diagnose ownership; they are not deterministic replay or performance qualification.

`CONTINUUM_RELOAD_MASS_BARRIER=1` advances the complete original `FlightIntegrator.UpdateMassStats` after stock unpack/module initialization, before physical orbit mode. The observed invocation corrected native body masses and centers but left the 10.7 cm orbital error. Moving this work before physical tracking was too late for that fixture.

`CONTINUUM_RELOAD_MASS_BARRIER=packed` instead invokes that original method immediately before the fresh vessel's first mass-bearing packed statistics calculation. A scoped predicate permits its body-publication branch for the admitted still-kinematic bodies; the original packed fields remain unchanged. The exact original branch and assembly are pinned. All parts and FI dependencies must already be ready; otherwise the experiment fails rather than delaying stock statistics or inventing readiness. Original resource, thermal and mass calculations still run. No pose, orbit or velocity repair is performed.

The report retains the mode, per-body mass/center/kinematic state, live and weighted centers, epoch, original invocation clocks, reached body-publication count and actual hook removal. Constructor failures retain inner exceptions and observed cleanup. Neither experimental mode is enabled by ordinary launches; a successful selected checkpoint would still leave other vessels, mods and ordinary reload paths unqualified.

The packed treatment passed the selected 256-part checkpoint: one original invocation published all 170 packed/kinematic bodies, with zero faults and owned hooks removed. Saved-epoch position error fell to 0.000001768 m and velocity error to 1.374e-9 m/s. All frozen restoration gates passed; 256 fresh parts, four crew, ordered modules, 2,505 fields, resources and whole-game membership were preserved. Both actual images contain the craft, Kerbin and UI. This is an observed construction-order fix for the diagnostic fixture, not ordinary reload or general mod qualification.
