# Rigid craft and bulk part systems

The primary research experience is a rigid assembled craft with preserved part identity and gameplay. Exaggerated elastic wobble is optional and has a separate fidelity/performance envelope. A slower large craft in wobble mode does not block the primary rigid experience. Career unlocks and UI policy are not decided. No live rigidity toggle or new body owner is implemented by this design.

## Compile the craft into the work it needs

An authored part is not necessarily a motion body, scheduled job or renderer submission. Retain its resources, modules, collider identity, authored transforms and interaction endpoints, then compile different execution views for motion, systems and presentation.

| Responsibility | Execution representation |
| --- | --- |
| Intact structure | One motion body per admitted locked rigid region, compound shapes retaining part identity |
| Mechanisms | Separate bodies/generalized coordinates only where actual relative motion is required |
| Part gameplay | Typed component banks, bulk system passes and explicitly ordered external callbacks |
| Resource routes, structural connections | Retained graph plans; update membership/routing when topology changes |
| Visual parts | Fixed local poses under each region plus genuinely changing mechanisms; shared render data |
| Failure, staging and docking | Connection metadata and topology transactions, not mandatory always-solved springs |

PhysX documents a single actor with multiple shapes as cheaper than fixed joints and free of fixed-joint drift. This supports the representation choice; it supplies no measured KSP speedup or mod compatibility guarantee. Real structures can flex, but reproducing all elastic motion is not required for the selected rigid experience. [PhysX joints](https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/Joints.html).

Current lock state is separate from eventual detachability or breakability. A locked decoupler or docking connection can be internal to a rigid region until an event changes its lock state. Removing one edge does not necessarily split the region: struts and alternate locked paths may keep it connected. Unknown module/body ownership is an admission question; do not classify every unknown attachment as safe merely because rigidity is the preferred model.

Existing `SemanticClusterCompiler` supplies stable deterministic membership but cuts detachable/interface connections immediately. Existing `RigidCluster6Dof` supplies aggregate mass, rotated inertia with parallel-axis terms, momentum and reconstructed member velocities. Neither constitutes a live rigid-body replacement. Its body position/orientation assumptions also need an adapter for Unity's distinct transform pose, center-of-mass offset and principal-inertia rotation.

## Preserve control, separation and failure

Compute region mass, center of mass and inertia from nonduplicated member mass contributions; physicsless parts sharing an existing body must not be counted twice. Route actual world-point forces into a region wrench: sum forces and sum original torques plus each application offset crossed with its force. Off-center RCS/thrust still changes rotation; unbalanced controls remain meaningful. Preserve original units, frames and force/acceleration modes at the adapter. A per-member acceleration command cannot simply be replayed once per alias onto the aggregate.

Resource-mass updates invalidate mass properties without rebuilding unrelated module membership. Apply gravity/integration once per admitted motion owner. The existing FlightIntegrator writes per-body mass/COM and acceleration, so assigning every `part.rb` to one object is insufficient. Angular damping, contacts, wheels and direct mod body writes also require explicit ownership.

Prepare topology changes before committing them: recompute locked connected components, region properties and collider/member mapping, then commit one current generation. Split member velocities inherit `v + omega cross r`; preserve total mass and linear/angular momentum with explicit separation/contact impulses. Docking likewise transfers authority and accounts for capture impulses/dissipation. Publish all participating state or preserve the prior owner; reject stale work. A live flexible-to-rigid toggle can remove elastic energy and requires an explicit transition policy rather than a claim of lossless switching.

Removing joint constraints also removes their constraint-force reports. A separate failure model must decide overload and impacts. Start with declared load/impulse thresholds and a bounded connection-load calculation where needed; a loop's internal load sharing needs a selected model. Retain fun breakage without inventing equivalent joint stresses. Failure can change topology without simulating visible bending beforehand.

The next rigid owner should use native PhysX compound bodies for an admitted stock assembly while keeping native integration active. Prove aggregate properties, an off-center force, one resource-mass change and one real decoupling with retained IDs and inherited velocities. This attacks body/joint counts without waiting for a replacement global engine. Direct-provider and collider ownership remain real implementation work.

## Replace callback execution with bulk systems

ECS is a concrete candidate, not a deferred slogan. Use an entity's stable identity to locate its typed state, and execute one system over matching components rather than an arbitrary virtual callback per part. A scheduler can expose disjoint read/write sets; typed loops, graph algorithms and specialized kernels do the numeric work. Query composition and specialized execution can coexist. Rust is useful for ownership and compilation, but wrapping existing callbacks in ECS does not change their acquisition, side effects or native traffic.

Compile membership and dependencies when modules/topology change. Share vessel/frame/atmosphere derivations at their proper validity boundary. Resource networks use compiled connectivity and current quantities; aerodynamic passes use compact inputs and actual provider dependencies; dormant science/UI work can respond to changes rather than poll every physics step. Thermal/control work keeps its declared time/accuracy contract. Frequent flags need not cause archetype migration. Do not decimate coupled physics or hide stale state under interpolation.

Authoritative writers produce mutation records/revisions automatically. Dirty discovery is not a free scan. Unknown external mod writers initially require a conservative refresh or an adapted lease. Preserve reached callback order where observable; independent batches may run in parallel, and dependent passes have explicit barriers. Start with a cheap sequential CPU pass when scheduling costs exceed useful work; parallelism, SIMD and GPU residency remain interchangeable execution choices.

Native state remains resident when it owns a subsystem. Transfer changed inputs/coarse command blocks at meaningful boundaries, not per-component JSON or a synchronous RPC per part. Charge acquisition, conversion, synchronization and publication before making an integrated cost claim. Rigid motion may reduce body reads and pose publication substantially; it does not remove every part-level force/provider or resource calculation.

## Executable ECS comparison

`tools/part-systems` compares retained typed Rust arrays with a real **hecs 0.10.5** archetype world. Both execute the same dry-aero calculation over two previously recorded original 512-part roots. Entity construction and query membership persist; ECS entities spawn in reverse ordinal order and output is gathered in captured ordinal order. There is no entity churn or JSON in the measured pass. hecs itself permits arbitrary query order, so original command ordering is an explicit output contract. [hecs World](https://docs.rs/hecs/0.10.5/hecs/struct.World.html).

The existing research-lab validator first checks provenance and the independent original-tape numerical oracle. Each backend then checks all scalars, conditional writes and actual aero-tail commands including recipients, modes and exact application-point bits. Preparation decodes once and computes bit-exact changed-row lists. Timing separately charges refresh, compute, ordered output materialization and their directly measured total. Validation/export are outside timed samples. Allocator calls are not measured; retained source storage is not an allocation-counter claim.

Run against a private original recording and its enclosing marker report, writing a fresh result:

```console
cargo test --locked --manifest-path tools/part-systems/Cargo.toml
cargo run --release --locked --manifest-path tools/part-systems/Cargo.toml -- \
  --recording ORIGINAL-RECORDING.json --probe ORIGINAL-MARKERS.json --output NEW-RESULT.json
```

Two observations per backend used the original roots, alternating which backend ran first. Dense complete totals were **11.833 and 10.792 microseconds**; hecs **13.667 and 14.292 microseconds**. Compute alone was 7.750–10.333 microseconds. Root two refreshed all 512 input rows; root one changed none after preparation. Both matched **1,024 rows and 3,648 actual aero-tail commands**. The original tape also contains queued commands outside this calculation.

This is a useful cheap-execution witness, not a universal ECS ranking, clean flight comparison or a full part-system replacement. Both input rows are AoS; this does not compare SoA/SIMD or an ECS parallel scheduler. The capture's two roots supply screening observations rather than a confidence interval. Retain both implementations and move on to remaining provider/acquisition/queue/publication ownership; do not repeat these timings to manufacture a winner.
