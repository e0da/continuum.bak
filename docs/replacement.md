# Replacing a physics hot loop

A replacement solver is a plausible research direction. Getting code into the game is easier than preserving the contract between KSP, Unity, and other mods. Start with a normal KSP addon and a separately testable backend. Use a custom bootstrapper or native detour only after a concrete missing interception point requires it.

## What is established

The installed Mac KSP executable is x86_64. Its managed assemblies expose Unity rigid bodies, joints, forces, and local physics scenes. KSP supplies additional flight integration, mass, aerodynamic, thermal, and part behavior. Current [flight evidence](research-replacement-architecture.md) identifies repeated rendering as a major cost in the frozen 196-part atmospheric fixture and measures native physics and managed fixed work separately. The dominant layer for larger craft and physics warp remains unresolved.

Unity supports [native libraries called from managed code](https://docs.unity3d.com/2019.4/Documentation/Manual/NativePlugins.html). [Harmony](https://harmony.pardeike.net/v2/articles/patching.html) supports managed method patches; this is not a universal hook into Unity's native physics internals. [Community Fixes](https://github.com/KSPModdingLibs/KSPCommunityFixes) demonstrates substantial KSP managed-code replacement. These establish possible entry mechanisms, not a working solver handoff.

See [community integration boundaries](compatibility.md) for existing integrator, lifecycle and joint owners that must be accounted for before takeover.

## Proposed boundary

Capture immutable arrays of body IDs, poses, velocities, mass properties, shape references, joints, and external forces at a defined physics tick. Run the backend using those arrays. Return poses, velocities, contact impulses, and break events with the input tick and topology generation. Reject stale results. Cache stable geometry and topology rather than rebuilding them every tick.

Begin in shadow mode: stock remains authoritative while the alternate backend predicts. A captured state alone is insufficient for replay; the input force timeline, constraint parameters, contact geometry, timestep, and coordinate-frame changes must also be captured. Compare invariants and trajectories before any takeover.

For takeover, there must be exactly one owner of motion for each body. It must receive engine, aero, gravity, contact, and mod-applied forces and publish state before dependent gameplay reads it. Disabling Unity simulation globally or teleporting a vessel after stock already simulated it would not establish that contract. Unity collision proxies, if retained, need explicit impulse exchange to avoid doubled or missing forces.

Stage separation, docking, breakage, packing/timewarp, floating-origin shifts, scene switches, and save/load are topology or coordinate-frame transitions. They must invalidate or transform pending work. Shared-memory output from an external process must never be applied to a different generation of the vessel.

## Hardware candidates

| Candidate | Why test it | Main unresolved cost |
| --- | --- | --- |
| Native CPU library in KSP | SIMD and multicore processing without process transport; [Jolt](https://github.com/jrouwe/JoltPhysics) and [Box3D](https://github.com/erincatto/box3d) are candidates | x86_64 host ABI, solver mapping, and force interception |
| Separate native ARM process on Apple silicon | Runs outside the Intel game's process architecture | Shared-memory protocol, synchronization, crash handling, and end-to-end latency |
| Metal compute backend | Apple GPU parallelism | Kernel design, constraint scheduling, contact generation, and mandatory readback/synchronization |
| GPU PhysX on supported hosts | Existing GPU rigid-body solver | [CUDA platform requirements](https://nvidia-omniverse.github.io/PhysX/physx/5.7.0/docs/GPURigidBodies.html); not a Mac switch |

[Box3D's announcement](https://box2d.org/posts/2026/06/announcing-box3d/) describes graph coloring, SIMD contacts, multithreading hooks, double-precision positions, and recording/replay. Its [upstream repository](https://github.com/erincatto/box3d) documents a C17 implementation, a C API, Mac support, SSE2/Neon, and an MIT license. This makes it a concrete native-backend candidate. Pin a source revision before comparing it; no Box3D build, bridge, or performance result is present here. Test coupled joint chains, uneven masses, contacts, breakage and step-size sensitivity alongside transfer overhead. Local rigid-body simulation does not replace long-horizon orbital integration. The announcement described alpha maturity; qualify the revision actually selected rather than infer readiness from the feature list.

Thread-level parallelism, SIMD, better memory layout, fewer allocations, and a better solver are separate interventions. A connected rocket has dependent constraints; graph coloring or a parallel iterative method can expose work, but extra cores do not remove convergence requirements. The goal can retain flexible joints rather than eliminate them.

A GPU or sidecar measurement must include packing inputs, synchronization, solving, returning results, and publishing them into the game. Measure latency per physics tick, not just kernel throughput. Double buffering that returns results one tick late changes feedback and stability; it is not automatically an acceptable optimization.

## Evidence gates

1. Attribute representative flight cost with available markers and, if necessary, targeted managed instrumentation or a native sampler. Do not assume that PhysX is single-threaded or that all low frame rates originate there.
2. Capture a complete small-vessel replay and reproduce stock inputs offline.
3. Compare at least one alternative backend on the same trace with contacts, angular momentum, energy drift, constraint error, and break thresholds checked.
4. Run in shadow mode in a disposable game instance. Establish tick ordering and topology handling.
5. Take over one bounded vessel class, then qualify lifecycle transitions and mods before expanding.

If the replaced loop is 80% of tick time, making it 100 times faster yields about a 4.8-times whole-tick speedup, not 100 times. That is still valuable, and it identifies the next bottleneck. Orders-of-magnitude gains remain a hypothesis until profiling and end-to-end measurements support them.

## Adaptive fidelity and long time spans

The desired outcome includes a station coasting through a distant stellar encounter, a 10,000-year colonization transfer, precise docking, and stable loading. These require different spatial and temporal resolutions. They are requirements for future experiments, not implemented features.

Stock KSP already distinguishes [on-rails and physical warp](https://github.com/KSP-KOS/KOS/blob/develop/doc/source/structures/misc/timewarp.rst). [Principia](https://github.com/mockingbirdnest/Principia) is important prior art for replacing orbital dynamics with a native numerical implementation. Neither fact proves the proposed combination of structural, orbital, and background simulation.

A candidate system would maintain one authoritative simulation time, with subsystems evaluated at different step sizes or event times. A quiet vessel can retain detailed structural state while its external trajectory advances using an appropriate orbital model. Contact, thrust changes, significant tidal loading, atmosphere, structural events, and nearby interactions force refinement. A stellar flyby can require careful orbital integration while still requiring no fine structural steps. An analytic two-body trajectory is not an accurate substitute for every multi-body encounter.

Approximation regions must be selected by error and interaction bounds, not simply camera distance. Predict encounters conservatively, synchronize interacting regions to a common time, and refine before contact. Use hysteresis to prevent repeated switching. Docking requires local relative coordinates and small contact steps; deep-space travel requires suitable global coordinates and long-horizon integration. Long durations also require an explicit clock precision policy; avoid advancing a large single-precision absolute timestamp by tiny increments.

Returning to detailed simulation must reconstruct consistent poses, center-of-mass velocity, angular velocity, joint reference frames, and any retained deformation or internal motion. Projecting constraints or damping a bad reconstruction can change energy and momentum. Measure those changes; do not silently erase genuine elastic energy or use unlimited joint strength to mask them.

Background resource, thermal, production, reliability, and life-support models need event or analytic integration contracts too. An arbitrary mod that only exposes a per-tick callback cannot automatically be advanced 10,000 years accurately. The initial system would need declared supported models and explicit unsupported behavior.

“Practically lossless” must become observable tolerances: orbital position and phase error against a higher-accuracy reference, bounded invariant drift, docking contact error, and bounded impulse/energy change at every fidelity transition. Choose these tolerances before benchmarking. Chaotic long-horizon trajectories may not admit a cheap accurate prediction; adapt or report that limit rather than hide it.

Open questions include the dominant measured costs, complete force interception, supported background modules, useful orbital error tolerances, and which fidelity transitions can be implemented safely through KSP's existing lifecycle. Installed writer censuses now distinguish native internal dynamics from script-driven frame changes, and [A023](live-substitution-canary.md) proves bounded interception and exact native-node restoration. These narrow results do not establish replacement dynamics or a game-performance improvement. The [roadmap](roadmap.md) selects the next comparison.
