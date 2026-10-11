# Live vessel research loop

Continuum can capture a bounded, changing vessel workload once and replay it without booting KSP. KSP owns the simulation. The workbench owns diagnostic presentation, and portable C# and native Rust can analyze the observed loads independently. This boundary currently observes forces; it never publishes replacement forces.

## Capture and identity

Enable `CONTINUUM_VESSEL_RUNTIME_RECORDING` with a new absolute `.cvr` path and request the existing multiscale scene capture. The recorder consumes the validated in-process cold manifest after the scene commits. Its final path, exact UTF-8 file digest, vessel reference and run identity must match. This avoids Unity 2019's external-assembly custom-array JSON decoding problem without relaxing the binding.

The little-endian version-one binary records ordered part/draw identities once, then simulation time, root orientation, rigid renderer poses, visibility, environment and observed root-local part forces. Capacity and numerical checks reject incomplete, changed, stale or foreign data. A topology change aborts the session; valid interrupted data uses a distinct partial path. Persistence happens outside the flight measurement window.

The force resultant is in newtons; the moment is in newton-metres about the vessel origin. Positions use body centre of mass, including shared bodies for physicsless parts. This is an aerodynamic observation of stored fields. It does not prove field freshness, all mod providers, actual application-point moments, intrinsic torque, thrust or gravity. Frozen skinned geometry does not capture changing bone deformation.

## Independent consumers

`tools/vessel-replay` reads and validates the recording with a native Rust implementation and computes all frame resultants. Its output JSON is an offline receipt, outside the runtime state-transfer path.

The focused C# harness supports `--compare RECORDING KSP_REPORT NATIVE_REPORT NEW_OUTPUT`. It binds the captured report to the binary, compares every sequence/time and force/moment component, and writes a new comparison receipt. Frozen tolerance is the larger of absolute `1e-6` and relative `1e-9`. The analytic force-couple and origin-translation checks provide independent invariants.

## Interactive replay

The existing loopback workbench socket accepts:

- `replay.open ABS_RECORDING`
- `replay.seek INDEX`
- `replay.play RATE` and `replay.pause`
- `replay.checkpoint NEW_ABS_PATH`
- `replay.resume ABS_CHECKPOINT`

Frames stage poses, inverses, bounds and visibility before GPU publication and cursor commit. Cold meshes, textures and draw identities remain resident. Retained presentation updates the mutable state texture; visibility changes rebuild commands, while pose-only updates retain commands. GPU publication failure attempts rollback and blocks drawing if rollback fails. Checkpoints bind recording/scene/manifest digests, session, topology and cursor. Shader hot reload preserves the replay session; scene replacement and static benchmarking are rejected while a recording is bound.

The first real workload contains 76 frames of a 512-part craft during three simulated seconds of atmospheric flight. All recorded resultant components agreed exactly across KSP, portable C# and Rust. Changing geometry, seek, playback, checkpoint/resume, foreign binding rejection and shader hot reload were observed in the Unity 2019 workbench. This is diagnostic workload qualification, not gameplay or replacement-physics qualification.

## Modern Unity probe

The private builder pins Unity `6000.3.26f1`, Mono, Metal and the Built-in render pipeline, with explicit ARM64 or x64 architecture. A matching-version mapped shader bundle is built from project-owned shader source. `CONTINUUM_MODERN_UNITY_PROBE=1` permits that exact consumer to ingest the captured Unity 2019 diagnostic scene while preserving its provenance. Ordinary consumers retain their same-engine admission rule.

Both modern architectures consumed the same changing recording. This proves an independently hosted presentation/analysis slice. Whole-game startup, scenes, serialization, DLC/mod interfaces, physics and missions still require separate reconstruction proofs.

## Startup finding

The default-off terrain observer separates actual vertex work, mesh callbacks and native mesh publication. A compiled ordered dispatch experiment uses canonical method identities, preserves original calculations and falls back to the original loops. It removed roughly 11.9 million exact empty base calls in the initial workload without a meaningful complete-startup gain. Leave it disabled. Counters establish eliminated work, not geometric equivalence.

The next startup investigation should explain the measured PartLoader construction and real terrain calculations before choosing a larger replacement. Full mesh equivalence, other modifiers and other bodies remain separate obligations. Capture validation/copying and workbench decode/upload costs also remain explicit; the small resultant kernel is not a demonstrated KSP bottleneck.
