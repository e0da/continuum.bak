# Replace expensive work, not just its implementation

Continuum's direction is to replace the systems that create avoidable work. A small optimization is useful when it reveals a boundary or supplies a component of that replacement. A smaller draw count, native implementation or asynchronous API is not the outcome by itself.

## What the current evidence says

The frozen 196-part atmospheric flight rose from 15.73 to 43.08 FPS when the reentry cameras received seven geometry packets instead of 530 original sources. The measured FinishFrameRendering interval fell from 29.63 to 8.88 ms per frame. This is strong evidence for repeated rendering/submission as a costly architectural boundary in that workload.

Adding a supported main-view shader and instancing path produced 46.08 FPS in one subsequent observation. It admitted 279 renderers into 37 packets, but FinishFrameRendering barely moved, to 8.79 ms. The shader changed and the observation is not an isolated, repeated comparison. It does not prove that extending the same batching technique will produce a large further gain.

The current candidate spends about 6.02 ms per rendered frame in the enclosing fixed phase, 2.37 in script Update, 1.31 in PreLateUpdate and 9.95 in PostLateUpdate. These are wall intervals with nested work; they are not exclusive CPU costs or GPU execution timings. The native physics child is about 1.78 ms per invocation. Physics and modules remain candidates for other workloads, particularly higher part counts and warp.

Startup already restores 1,639 prepared textures instead of repeating their original processing. A new native profile measured all 627 model reads at 0.84 seconds and 479 successful part parses at 2.86 seconds, including 0.70 seconds for icons. Internal props and spaces together took 0.13 seconds. These synchronous scopes include observer overhead and mixed native work; they do not explain most of the 28.36-second admission-to-menu run. Warm startup already uses saved drag cubes; 426 dispatches are not evidence of 426 GPU drag renders.

The stronger next targets are concrete: five uncooked PNG normal maps occupy about 2.02 seconds of texture boundary gaps, and Continuum's own startup installation spans 2.45 seconds, including six independent hashes of the same game assembly. The normal converter crosses the managed/native boundary for every pixel. Prepare its output once and consolidate established identity within the launch. The installation intervals also contain patching and JIT; the entire interval is not attributable to hashing. Planet-scene loading takes another 2.76 seconds, and launch-site setup 4.44 seconds with terrain construction and a stock wait. Deferred terrain currently fails the frozen position-parity check and remains experimental.

## Prepared assets and explicit construction

Build one content-addressed asset store with cheap catalog metadata, immutable model recipes and independently requested construction capabilities. Geometry, materials, collision, icons, runtime part behavior and interior data have different consumers and lifetimes.

Workers prepare and map immutable CPU data with private scratch. One Unity owner constructs and publishes complete engine objects. Stable recipe IDs bind source content, resolved configuration, dependencies, engine publication recipe and producer version. Share mesh payloads where stock semantics permit; keep mutable hierarchies and components separate where callers expect clones.

The active vertical slice completes prepared texture coverage and shares validated launch identity. Prepared model recipes are a useful later extension of the same store, retaining original part/module construction order, but the measured 0.84-second model total does not justify making that reader the next large optimization. Then remove eager icon construction for admitted module contracts, and separate interior metadata from interior object residency. Crew assignment also consumes interior/seat information before the player enters IVA; camera entry alone is not a sufficient readiness boundary.

Synchronous consumers receive actual ready objects. Known startup consumers get a complete dependency closure before invocation. Unknown callbacks, direct public fields and shared-prefab mutation retain explicit eager compatibility paths. A future must not masquerade as a Unity object, and the Unity thread must not wait on a worker that needs that same thread to publish its result.

Native workers or a sidecar are useful where they remove repeated parsing, permit independent preparation or preserve useful state across game launches. Start with mapped immutable artifact pages and batched descriptors. Add transient shared buffers only for measured repeated traffic. Do not transmit Unity pointers or issue a request per part per frame.

The model slice must falsify unsupported-format fallback, clone/shared-prefab identity, hierarchy/mesh/material/collider/animation/bone equivalence, changed callback order, and a complete-startup regression after admission and publication are charged.

## Retained vessel rendering

Build a vessel rendering service around shared immutable assets, stable instance IDs and contiguous changing pose/thermal/highlight state. Construct its topology once per vessel generation. Read each physical part pose once, update supported animated roots, and publish one snapshot for main view, shadows and effects.

The service must take rendering ownership for admitted sources. The current prototype leaves the old renderer graph registered and polls bindings and material properties every frame; draw batching alone does not remove that graph's maintenance. Retire admitted stock renderers from ordinary update/cull work, preserve an explicit unsupported path, and restore ownership on faults and topology changes.

First resolve whether the remaining rendering interval is CPU submission/driver waiting or GPU work. Aligned native-thread observations and available GPU evidence select the next consumer. If repeated effect work dominates, use shared vessel depth/normal/coverage and flow-axis occlusion inputs for a bounded flame renderer. If renderer management dominates, prioritize full source-renderer retirement and changed-state publication. Both advance the same retained rendering system.

Geometry, moving poses, texture/shader inputs, lighting, shadows and effect behavior require concrete fidelity checks. A fewer-pass shader with different appearance cannot establish an equivalent speedup. Full-frame measurements include state capture, conversion, synchronization and publication.

## Computation and engine ownership

Do not translate every PartModule into Rust independently. Where measurement identifies repeated graph traversal or redundant derived state, compile the relevant flight dependency graph, store persistent contiguous state and schedule work only when its inputs change. Publish compatibility views for existing modules and consume state in batches.

Keep forces, resources, physics, rendering and saving under explicit owners. Replacing one owner requires the supported equations and events, topology invalidation, cancellation, save/reload and independent numerical checks. Native physics is not the dominant measured cost in the current atmospheric case; that does not settle large-craft or warp behavior.

Modern Unity/ARM64/Metal remains a separate whole-runtime path. The matching Unity 2019 editor and actual shader build/load loop now work locally. That removes a real tooling obstacle to new rendering consumers; it does not qualify a migrated game.

## How to decide when to move on

Choose a system replacement with a stated source of avoided work and a usable first consumer. Continue within it while new evidence identifies substantial remaining avoidable work. Move to another system when the dominant cost moves, an engine boundary blocks the current route, or the next step has poor expected benefit.

Use measured costs and workload behavior to select the route. Keep FPS and startup time as outcomes rather than inventing a promised speedup. Two agreeing observations can settle a direction; additional runs need a specific resolving question. A failed behavior check remains a failure even when performance improves.
