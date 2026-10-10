# Replace expensive work, not just its implementation

Continuum's direction is to replace the systems that create avoidable work. A small optimization is useful when it reveals a boundary or supplies a component of that replacement. A smaller draw count, native implementation or asynchronous API is not the outcome by itself.

## What the current evidence says

The frozen 196-part atmospheric flight rose from 15.73 to 43.08 FPS when the reentry cameras received seven geometry packets instead of 530 original sources. The measured FinishFrameRendering interval fell from 29.63 to 8.88 ms per frame. This is strong evidence for repeated rendering/submission as a costly architectural boundary in that workload.

Adding a supported main-view shader and instancing path produced 46.08 FPS in one subsequent observation. It admitted 279 renderers into 37 packets, but FinishFrameRendering barely moved, to 8.79 ms. The shader changed and the observation is not an isolated, repeated comparison. It does not prove that extending the same batching technique will produce a large further gain.

The latest corrected retained candidate spends 5.90 ms per rendered frame in the enclosing fixed phase, 2.37 in script Update, 1.30 in PreLateUpdate and 10.06 in PostLateUpdate, including 8.92 in FinishFrameRendering. These are wall intervals with nested work; they are not exclusive CPU costs or GPU execution timings. Physics and modules remain candidates for other workloads, particularly higher part counts and warp.

Startup already restores 1,639 prepared textures instead of repeating their original processing. A new native profile measured all 627 model reads at 0.84 seconds and 479 successful part parses at 2.86 seconds, including 0.70 seconds for icons. Internal props and spaces together took 0.13 seconds. These synchronous scopes include observer overhead and mixed native work; they do not explain most of the 28.36-second admission-to-menu run. Warm startup already uses saved drag cubes; 426 dispatches are not evidence of 426 GPU drag renders.

The completed texture and identity slice targeted five PNG normals with about 2.02 seconds of texture boundary gaps and six independent hashes of the same game assembly. Its installation intervals also contained patching and JIT; the entire 2.45-second installation span was not attributable to hashing. Planet-scene loading takes another 2.76 seconds, and launch-site setup 4.44 seconds with terrain construction and a stock wait. Deferred terrain currently fails the frozen position-parity check and remains experimental.

## Prepared assets and explicit construction

Build one content-addressed asset store with cheap catalog metadata, immutable model recipes and independently requested construction capabilities. Geometry, materials, collision, icons, runtime part behavior and interior data have different consumers and lifetimes.

Workers prepare and map immutable CPU data with private scratch. One Unity owner constructs and publishes complete engine objects. Stable recipe IDs bind source content, resolved configuration, dependencies, engine publication recipe and producer version. Share mesh payloads where stock semantics permit; keep mutable hierarchies and components separate where callers expect clones.

Prepared texture coverage and shared validated launch identity are implemented. Prepared model recipes are a useful later extension of the same store, retaining original part/module construction order, but the measured 0.84-second model total does not justify making that reader the next large optimization. Eager icons and interior object residency remain smaller targets. Crew assignment also consumes interior/seat information before the player enters IVA; camera entry alone is not a sufficient readiness boundary.

Synchronous consumers receive actual ready objects. Known startup consumers get a complete dependency closure before invocation. Unknown callbacks, direct public fields and shared-prefab mutation retain explicit eager compatibility paths. A future must not masquerade as a Unity object, and the Unity thread must not wait on a worker that needs that same thread to publish its result.

Native workers or a sidecar are useful where they remove repeated parsing, permit independent preparation or preserve useful state across game launches. Start with mapped immutable artifact pages and batched descriptors. Add transient shared buffers only for measured repeated traffic. Do not transmit Unity pointers or issue a request per part per frame.

The model slice must falsify unsupported-format fallback, clone/shared-prefab identity, hierarchy/mesh/material/collider/animation/bone equivalence, changed callback order, and a complete-startup regression after admission and publication are charged.

The completed texture extension now prepares all 1,644 rows, including the five stock-generated normal-map mip chains, with five readability restorations and no unrepresented rows. Reuse reports 1,644 hits and zero fallbacks. An independent audit found all 1,639 previous rows unchanged, checked every distinct new-image payload digest, and verified the added RGBA mip layouts and equal mapped RGB channels. Source-channel decode and GPU sampling fidelity remain separate gates. Shared game identity performs one physical hash and five guarded reuses. The observed prepared launch fell from 28.36 to 26.16 seconds; this combined treatment is one resolving observation, not an isolated speedup for either component.

## Retained vessel rendering

The shared launch contract now resolves the existing startup and flight controls into one typed selection, then freezes actual required binaries, settings, checkpoint and prepared metadata. `resolve` and `run --launch-profile` share that envelope; replay reconstructs its required inputs and rejects drift or hidden overrides. This replaces the runner's positional startup interface while retaining legacy CLI compatibility and detailed provider reports.

The first shared asset-generation lifecycle connects catalog metadata, cooked texture restoration and prepared DLC. Its location-independent digest binds selected metadata and the frozen texture producer separately from the current helper. Provider content checks still establish admission; actual publication coverage establishes readiness. Menu/shutdown receipts bind those outcomes to the exact launch profile, run and process. Recording failure is contained outside provider ownership and marks evidence degraded. This remains scoped prepared-startup coverage, rather than a complete asset store, capability scheduler or crash-recovery supervisor.

Build a vessel rendering service around shared immutable assets, stable instance IDs and contiguous changing pose/thermal/highlight state. Construct its topology once per vessel generation. Read each physical part pose once, update supported animated roots, and publish one snapshot for main view, shadows and effects.

The service must take rendering ownership for admitted sources. The corrected prototype now disables 267 admitted originals throughout the flight, while preserving all 279 sources in 37 main-view packets. It publishes 69 rigid and 198 dynamic source poses, shares bounds with FX consumers, and owns 163 animation culling settings so disabling renderers does not suspend their animations. Twelve main-view sources outside owned FX coverage retain their original camera leases. Components remain for property-block writers; the native transform/bone graph and per-frame material/property polling remain.

Native experiments rejected renderer bookkeeping as the next large throughput win in this fixture:

| Treatment | Mean FPS | Mean frame | p95 / p99 | FinishFrameRendering |
| --- | ---: | ---: | ---: | ---: |
| Clean current batching control | 47.56 | 21.03 ms | 26.50 / 40.60 ms | 8.59 ms/frame |
| First retained provider, reduced coverage | 42.12 | 23.74 ms | 32.73 / 42.90 ms | See retained raw receipt |
| Corrected retained provider, full coverage | 46.31 | 21.59 ms | 27.29 / 41.04 ms | 8.92 ms/frame |
| Same corrected provider, shadows disabled | 52.40 | 19.08 ms | 22.72 / 39.34 ms | 7.73 ms/frame |

The first provider admitted only 69 main sources because animation exclusion accidentally discarded existing batching coverage. The correction preserved coverage, published dynamic transforms and restored all 267 renderer and 163 animation leases with zero provider faults. Its maximum initial recovered corner error was 2.19 micrometres against the frozen 5 mm gate. Correct ownership did not produce a useful speedup; retained mode remains experimental. The clean control and corrected provider differ in helper binary and declared retained treatment; this is a resolving development comparison, not an isolated same-binary speedup claim.

The shadow counterfactual uses the same helper, files, shader and request except the shadow treatment. Effective shadow distance was zero; the engine normalized the requested zero cascades to one. It reduced the rendering bracket by 1.19 ms/frame, while complete frame cost fell 2.51 ms/frame. More rendered frames share the same simulated interval, so fixed work per rendered frame also falls; this is not a physics optimization. Suppressing shadows does not qualify an equivalent playable improvement and still misses the FPS and tail goals. It does not account for most remaining rendering cost.

A capture-aligned native sample counted 2,967 graphics-worker stack occurrences: 1,147 in driver submission, 1,237 in waits/events and 583 in unresolved Unity work. These are exclusive call-tree sample counts, not CPU milliseconds or game-wide percentages. The asynchronous GPU observer exercised all five camera roles, but the actual OpenGL driver reported zero timestamp counter bits and rejected initialization. Query cleanup and archived publication accounting completed; no GPU execution evidence qualified. Do not repeat the rejected timer experiment on the unchanged backend or infer GPU utilization from the rendering bracket.

The next substantial boundary is the rendering pipeline: repeated geometry/effect passes, camera submission and backend cost. Use shared vessel depth/normal/coverage and a bounded flame consumer rather than further per-renderer tuning. Preserve the separate flow-axis occlusion projection; it cannot simply reuse screen depth. OpenGL's two flight cameras also carry near/far depth and fog responsibilities, so a merger requires explicit depth/compositing coverage. A matching-2019 Metal reconstruction is a competing backend route; choose between them with a bounded actual rendering proof, not promised FPS.

Geometry, moving poses, texture/shader inputs, lighting, shadows and effect behavior require concrete fidelity checks. A fewer-pass shader with different appearance cannot establish an equivalent speedup. Full-frame measurements include state capture, conversion, synchronization and publication.

Particles and instancing are candidate consumers of this shared rendering state. A hybrid reentry renderer can construct vessel coverage once, use a bounded surface/volume contribution for the sheath, and use instanced ribbons or quads for streaks and wisps. The same machinery could support condensation trails, exhaust and sparks after their behavior is defined. Pool emitters, share materials, retain per-instance state, and bound projected coverage and LOD; replacing shells with excessive transparent overdraw can merely move the cost.

The current OpenGL 4.1 player needs an explicit backend path. Unity 2019's built-in mesh-particle GPU instancing requires shader target 4.5, whose OpenGL support begins at 4.3. It cannot be assumed available here. Custom ordinary instanced quads with vertex-shader motion can use the established lower-feature instancing route; compute-driven particle simulation belongs to a qualified backend such as a future Metal player. Particle simulation and particle draw instancing are separate capabilities. See [Unity particle instancing](https://docs.unity3d.com/2019.4/Documentation/Manual/PartSysInstancing.html) and [shader feature requirements](https://docs.unity3d.com/2019.4/Documentation/Manual/SL-ShaderCompileTargets.html).

## Computation and engine ownership

Do not translate every PartModule into Rust independently. Where measurement identifies repeated graph traversal or redundant derived state, compile the relevant flight dependency graph, store persistent contiguous state and schedule work only when its inputs change. Publish compatibility views for existing modules and consume state in batches.

Keep forces, resources, physics, rendering and saving under explicit owners. Replacing one owner requires the supported equations and events, topology invalidation, cancellation, save/reload and independent numerical checks. Native physics is not the dominant measured cost in the current atmospheric case; that does not settle large-craft or warp behavior.

Modern Unity/ARM64/Metal remains a separate whole-runtime path. The matching Unity 2019 editor and actual shader build/load loop now work locally. That removes a real tooling obstacle to new rendering consumers; it does not qualify a migrated game.

The nearest backend proof is matching-2019 Metal startup plus a complete shader/material dependency closure for one visible part family. A prior private reconstruction reached a black menu; the current configuration rejects forced Metal before initialization. Neither establishes a usable Metal game or proves the player binary lacks that backend. Recover the bootstrap configuration, preserve serialized identities, admit/remap shader dependencies, and prove menu plus part rendering before extending terrain, shadows and effects. Modern Unity and native ARM64 remain later routes, with original managed game behavior retained for this nearer experiment.

## How to decide when to move on

Choose a system replacement with a stated source of avoided work and a usable first consumer. Continue within it while new evidence identifies substantial remaining avoidable work. Move to another system when the dominant cost moves, an engine boundary blocks the current route, or the next step has poor expected benefit.

Use measured costs and workload behavior to select the route. Keep FPS and startup time as outcomes rather than inventing a promised speedup. Two agreeing observations can settle a direction; additional runs need a specific resolving question. A failed behavior check remains a failure even when performance improves.
