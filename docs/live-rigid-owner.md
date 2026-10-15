# Live rigid vessel ownership

The operator service now has a separate persistent owner path. It is selected
explicitly through the four-argument `LiveRigidVesselOwner(..., persistentPlay:
true)` constructor; the existing three-argument constructor retains v1/v2
diagnostics. The service owns enabling, pausing, transitions, reacquisition and
presentation. `CONTINUUM_PLAYABLE_RIGID` names its existing private receipt
directory. This path does not create `LiveRigidFlightDriver`, inject a pulse,
force throttle idle, impose a simulated-duration cutoff or capture owner PNGs.
Real stock input, loaded MechJeb controls, resource flow, aero and thermal
providers continue executing. Only the qualified rigid motion/read/publication
boundary changes.

Persistent admission supports active, unpacked, airborne orbital, suborbital,
escaping and atmospheric flight at normal or LOW physics warp up to 4x, with
the actual finite native step in `(0, 0.1]` seconds. Throttle is permitted.
Surface/contact states, deployed wheels, servos, changed celestial-body frames,
topology and unsupported writers remain stock-owned. A cold whole-craft envelope
includes source body positions, enabled collider bounds and stock craft size.
Before script and native work, clearance checks add a current speed/step margin
against stock sea/terrain altitude and nearby loaded-vessel envelopes. These are
conservative handback guards, not a terrain/collision implementation or proof of
arbitrary contact prediction. The exact assembly/IL/provider boundary remains
the selected stock and MechJeb graph; it is not an arbitrary-mod adapter.

`IsOwned` reports actual admitted native authority, not merely constructor
success. `BindPlayableIdentity(sessionId, epoch, attempt)` binds once before
admission and records the actual process and helper hash cold. The public
`HandbackBeforeStockTransition(reason)` is synchronous: it restores original
parents/scales/poses, native flags and constituent point velocities, then
forwards every still-pending source force/torque and reached common acceleration
to the original receiver in original call order. A fixed 16,384-entry value slab
retains actual receiver, mode and world-point operands until native advancement
finishes. Zero operands are also retained for handback; aggregate arithmetic may
omit them. Commands are consumed only after the matched native step. There is
no second original/candidate force fold or attempt to roll back native physics.

Declared eligibility and lifecycle handbacks are ordinary transitions. Caught
order, numerical, coverage, unexpected writer and sealed-graph failures latch an
invalid epoch even when restoration succeeds; clean fallback cannot promote
such an epoch into proof. Unsupported state encountered before mutation lets
the original stock transition proceed only after logical handback completes.
Failed restoration stops that transition. Cleanup removes only owned hooks.

Unity object retirement is asynchronous. `ReadyForReacquisition` stays false
while initially waiting for native admission and until an explicit handback has
restored bodies, drained pending operands, removed owned hooks and observed the
aggregate and presentation root actually gone. The service retains a retiring
owner across that frame. Ordinary `Dispose` writes its fresh final receipt only
after closure; `FinishInterruptedRetirement` can preserve an explicitly
incomplete receipt when scene destruction or quit supplies no further frame.
No early receipt is overwritten later. A safe zero-step cancellation is
`handed-back-unobserved`, with `passed=false` and no native coverage.

The v3 receipt uses primitive lifetime counters and a last-64-completed-step
ring. It allocates no per-command `Command`/`BodyState` receipt objects or
unbounded step arrays. Mass ingress reads the same current native mass/COM/
principal inputs into `RetainedRigidIngress` value storage, including changed
rows; its accepted snapshot drives native properties directly without hot
`ToProperties` conversion. Source units remain KSP native tonnes and tonne-m².
The canonical craft frame, COM/principal mapping, retained Transform forest,
original proxy/readback budgets and exact writer checks stay active. The ring
is a tail rather than a complete force tape. Its wall envelope begins before
the script phase and ends after native physics, publication, periodic ownership
checks and sample preparation; preceding/following fixed phases and rendering
are outside. Lifetime nested counters remain nonadditive.

The pending gameplay gates are actual operator controls and throttle in vacuum
and airborne atmosphere, normal/LOW4 motion, resource/thermal continuation,
ordered pending-force handback, save/staging/switch/rails restoration and safe
reacquisition. Independent source/native pose, common-acceleration and cleanup
proofs retain their frozen budgets. A complete uncapped stock-vs-owned cost
observation is separate from functionality; this source path alone claims no
FPS gain. The diagnostic procedures below keep their existing scope.

Persistent publication now has an explicit validation lease. The four-argument
constructor selects one completed native step; a fifth `auditSteps` argument
selects exactly `1` or `16`. The service freezes that selection for the epoch.
Every script/native boundary still checks aggregate liveness, native flags,
finite motion/properties, its canonical-to-native frame and current clock;
native ingress still acquires all source mass, COM and inertia inputs. Original
inactive-wheel, force-mode/order and complete common-acceleration guards remain.
This revision does not skip or coalesce functional mass acquisition.

Cold admission, cadence, ownership invalidation and terminal handback run the
full alias, proxy pose, forest and scale checks at the original budgets. Successful
audits alone advance the lease. An overdue gap or failed audit invalidates the
epoch; cleanup still attempts restoration. Handback explicitly invalidates once
and audits the current completed step before restore, including after an expected
eligibility/warp transition. Terminal eligibility is excluded from that ownership
check so an ordinary transition does not become a protocol fault. Duplicate
requests for the same view and lease revision skip. Authoritative pose/property
updates advance the expected generation without invalidating production cadence.

Unchanged canonical pose requests are constant-time: they retain existing targets
without per-member readback or synchronization. Changed poses write the admitted
presentation pivot or cold-compiled forest roots and synchronize once. Independent
member targets are recomputed from canonical/local geometry in the full audits;
they are not reallocated or replanned on every pivot publication. Unsupported
forests retain the original parent-first eager writer list. v1/v2 retain exhaustive
publication/readback and their original diagnostics.

The receipt binds `gameplayAuditIntervalSteps`, `gameplayFullLeaseAudits`,
`gameplayAuditMaximumStepGap`, last audited/expected generations and current
terminal step. It also records overlapping cold/cadence/invalidation/handback
reason counters, full attempts/costs, critical-check costs, pose readbacks and the
maximum actual accumulated simulated-time gap. `gameplayAuditScope` states the
limit: a direct transient proxy/Transform/forest write can affect stock callbacks
or be overwritten between full observations. Cadence is not universal writer
equivalence. Native behavior and complete stock-versus-owned cost still require
the changed integrated observation; source-level removal of proof work is not an
FPS result.

The diagnostic rigid owner advances an assembled craft as one native Rigidbody in KSP's existing physics scene. Original Part and Rigidbody identities remain present as collision-disabled kinematic proxies. This is the first live integration of the rigid-primary model; stock callbacks, resource updates and rendering continue running.

Select `--flight-live-rigid-owner ABS_FRESH_RECEIPT.json` in a resolved research-lab launch. It requires a pinned part count, normal time, vacuum, quiet controls and more than two simulated seconds of capture. It rejects other physics owners, attribution/capture treatments, docking/reload probes and ordinary live-play mode. The output directory must exist and the receipt must be new. The plugin also admits actual lifecycle, topology and PlayerLoop ordering at runtime.

The owner takes over after a completed native step. Before subsequent native steps it refreshes mass, center of mass and the full inertia tensor, reduces original force queues and commits them to the aggregate. Afterward it publishes member native and Transform poses in hierarchy order. Uniform FlightIntegrator acceleration is applied once, with exactly one source call required for every owned native body. Other force modes preserve the recipient's mass or inertia conversion and each force's moment about the aggregate center.

A pinned managed IL bridge redirects stock and loaded MechJeb Rigidbody force, velocity, angular velocity, world-center and point-velocity operations. Unowned receivers retain native behavior. Vessel statistics use the aggregate authority. Vessel frame translations and velocity corrections apply once to the aggregate; translations also move staged force points. Unsupported direct velocity writers and lifecycle transactions restore stock ownership before falling through. Assembly/IL identities, selected/unrouted callsites, frame-hook cooperation and owned-hook removal are recorded.

Admission excludes servos, active wheels, contacts and changing topology. Exact stock landing gears may remain fully retracted and ungrounded with their wheel controller and collider disabled; this state is checked again before physics. The aggregate has no colliders and zero damping. All-body momentum projection, native mass weighting and rigid motion are declared model choices. This does not establish closure over arbitrary mods or every native writer.

The bounded diagnostic queues a perpendicular off-center force through an original physical Part after 0.2 simulated seconds. After 1.5 seconds it restores original body flags and constituent point velocities, retires the aggregate, and requires at least two further stock native steps. A private raw receipt includes each pre/post step, source commands, reductions, reader coverage, proxy errors, restoration readback and hook ledger. A frame is captured at end of frame while ownership is active.

Verify the private receipt independently:

```text
dotnet run --project tests/KspContinuum.LiveRigid.Tests -c Release -- ABS_RECEIPT.json
```

The reader uses raw native floats and independent tensor/wrench math. It checks force-mode conversions, lever moments, linear velocity changes, angular momentum residuals and torque response, as well as source identities, acceleration coverage, proxy errors and restoration. Its real-craft proof requires the frozen 512-part station. Frozen gates are relative `1e-4` plus absolute `1e-4` for linear velocity delta; 2% of the largest before/after/applied angular momentum plus `1e-4` for angular residual; relative `1e-4` plus absolute `1e-5 rad/s` for angular velocity response; `1e-4 m` and `0.002 degrees` for proxy pose discrepancy.

An explicit `--flight-live-rigid-scenario control-warp` extends that owner to twelve simulated seconds and requires at least fourteen seconds of capture. Launch still starts at normal time, with a quiet external perturbation and the same exclusive vacuum/topology restrictions. The v2 receipt selects original `OnFlyByWire` controls: quiet entry; reaction-wheel attitude; RCS translation; quiet transition; four simulated seconds at LOW 4x with combined controls; normal-time quiet return. The artificial diagnostic pulse is disabled. Actual controls, original queued wrenches, rate/mode/index, step duration, resources and mass-property rebuilds accompany the independent proof. LOW 4x uses the actual 0.08-second native step, with no extra multiplication by four.

The driver preserves original input subscribers, rejects a changed callback chain or competing control owner, and restores its idle inputs, action groups and original warp selection. Controlled handback transfers current motion and removes owned bridge/frame hooks before observing at least two further simulated seconds of stock native physics and actual aggregate retirement. Completion waits for that lifecycle as well as the requested capture duration. Native step durations and universal-time observations remain separate receipt fields. This intrusive proof uses a 120-second inner capture timeout and the stock local-camera observer, which explicitly admits only its declared normal/LOW4 clocks. Other scenarios retain their original timeout and camera restrictions.

Four end-of-frame images sample attitude, translation, warp and normal return. Actual original wheel resource-handler withdrawals are retained in a bounded ledger; a fully charged final electric balance can include simultaneous generation. The independent reader retains original raw draws, reports any one-ULP over-request readbacks, and permits at most one binary64 successor of a positive request in this scoped accounting proof. Gross sums and native motion budgets remain unchanged. Positive wheel withdrawals, propellant depletion, native actuator commands and warp readbacks must pass; requesting inputs alone does not qualify control. The v1 quiet-pulse receipt remains a separate supported proof.

The respective bounded fixed-topology, no-contact transaction is qualified only when its native receipt and independent reader pass. Contacts, staging/docking, fuel-flow momentum, long-flight conservation, human control feel and complete-game performance remain separate capabilities. Receipt instrumentation and broad managed bridging are diagnostic costs; an FPS claim needs a combined candidate with those costs measured or removed.

The controlled owner now retains canonical mass-property inputs by dense stable member slot, caching each member's transformed center and principal axes. Changed members update that retained input; reduction still uses the original sorted, compensated two-pass arithmetic and eigensolver. Original property scans remain at the statistics and native-ingress boundaries. The reference reducer remains available, and analytic plus deterministic bitwise differential checks protect the arithmetic.

Controlled publication snapshots the aggregate pose once, plans each proxy pose in parent-first order, writes all Transforms, calls `Physics.SyncTransforms` once, then reads every native and Transform pose back. It preserves the original `Physics.autoSyncTransforms` setting and the existing pre-script/pre-physics pose audits and numerical gates. The quiet-pulse scenario retains its explicit native-plus-Transform path. The synchronization call flushes the physics scene globally; this qualification does not establish equivalent query timing for other moving bodies or contacts.

Lifetime synchronous owner costs are recorded with a monotonic clock. Driver audit is nested in audit; combination and property-triggered publication are nested in refresh; planning, writes, global synchronization and readback are nested in publication. These counters are not additive exclusive frame costs. On the admitted 512-part, 426-body control/warp workload, publication fell from 55.85 seconds to 1.30–1.34 seconds, and mixed-clock capture wall time fell from 65.23 seconds to 11.48–11.63 seconds. The independent native motion/control/resource/hand-back proof passed. This is a measured integration-cost reduction; the mixed normal/LOW4 capture remains ineligible for clean FPS qualification.

The controlled stock-camera observer also tracks exact original depth requests at the pinned `VolumetricObjectBase.OnEnable` and `DrawDepth.CheckSupport(bool)` callsites. Native setters execute once. Only a source-witnessed additive `Depth` request can change the admitted camera state; unrelated or prior unexplained changes still fail. Cleanup removes only owned callsite hooks and preserves legitimate original requests. The passing native run installed and removed this witness but observed no depth requests; the caller responsible for the preceding failed camera-restoration run remains unproven.

Three controlled stage envelopes now measure the original active `FlightIntegrator.FixedUpdate`, root-only `Integrate(Part)` and bound MechJeb `VesselState.AnalyzeParts`. Exact assembly/body/instance and installed/removed graph evidence accompany their counters. Original exceptions propagate unchanged. Integrate is nested in FI; inclusive costs are not additive. Its timing hook still dispatches on recursive child calls even though only the root is timed, so these are intrusive diagnostic observations. Positive calls establish reached coverage; zero calls do not mean zero cost.

The first three-envelope observation reached each stage 451 times: FI averaged 1.695 ms, nested root integration 0.767 ms and MechJeb analysis 0.502 ms. These stages do not explain the largest managed-phase remainder. Sampled configurations are separate observations, not clean performance arms; their wall costs can include suspension and observer effects. The subsequent same-process Mono map identified repeated owner publication and patch-state checks; its inclusive stack presence does not establish exclusive method CPU time.

Controlled images retain at most four original end-of-frame `ReadPixels`/`Apply` results. PNG encoding and fresh-file writing happen after capture closes, using those retained pixels rather than re-rendering. All textures are released even on failure; interruption while capture remains active discards pending pixels and cannot qualify. Completion still requires all four real files. The quiet-pulse path keeps inline encoding. Readback remains in-window overhead. A qualified native observation encoded four images in 412.988 ms and wrote them in 3.420 ms after its capture-end timestamp; this removes diagnostic contamination, not production gameplay work.

Controlled geometry now has a retained craft-root frame distinct from the
native aggregate's COM/principal-axis frame. The native body has zero local
COM and identity inertia rotation; its world pose is `p_root + R_root C` and
`R_root Q_principal`. Property updates preserve the canonical root pose exactly,
remap the native mass/COM/inertia frame, and transport COM velocity by
`omega × (newCOM - oldCOM)`. After native physics the inverse mapping updates
the canonical pose. Force lever arms, point velocity, native receipts and world
inertia remain physical-frame quantities; member readers, publication and
handback use the canonical craft frame. Original frame shifts move both poses
and queued world points exactly once. Quiet-pulse v1 retains its original frame.

Publication retains exact canonical pose bits and immutable member-local poses.
Every request still validates every member's native and Transform pose with
the original budgets. Identical targets omit planning, writes and global
synchronization; materialization/reuse/generation and request-reason counters
make the distinction explicit. Native aggregate writer audits independently
check the canonical-to-physical mapping. Per-step root/COM/principal evidence
lets the independent reader verify that mapping without changing its original
world-tensor and control/motion equations. Controlled property-shift counters
now measure the canonical root; they are not the previous native-setter metric.

The bridge cold-seals the exact admitted graph after all cooperating frame
hooks. On the pinned installed Harmony 2.2.1 ABI, it compares cloned serialized
entries under Harmony's own state lock and compares current original IL bytes;
it does not deserialize unchanged graphs on each periodic check. Presence,
nullness, length, every byte and dictionary identity must match. Rejection is
latched, including in-place array changes. Unsupported ABI uses full graph/IL
checks with exact declared frame cardinality. Original IL reads still allocate.
An optional fresh absolute `CONTINUUM_PATCH_STATE_WITNESS` path runs isolated
foreign-prefix, byte-mutation and previously absent method tests cold in the
actual Unity/Mono process; owned-only cleanup and unchanged selected game state
are required before admitting the real seal.

`--flight-lighting three-quarter-inspection` is a camera-only 50m root-detail,
45-degree solar preset for the declared controlled orbital run. It permits
only its exact normal/LOW4 clocks, requires actual unshadowed sunlight and
records planetary framing without changing the orbit to force a background.
The existing entry/planet-limb presets keep their original contracts.

Controlled publication now inventories the original Transform forest. Existing
owned ancestry can compress writes only when its retained scale and local-pose
invariants hold. Flat parentless body layouts instead admit an owned, unparented,
unit-scale presentation root without a Rigidbody. Original body/Part identities
become direct leaves, retain their exact positive scales, and use the canonical
craft-local geometry. This root remains separate from the native aggregate's
COM/principal-axis frame. Unsupported layouts retain explicit eager fallback;
drift after admission invalidates the run rather than silently falling back.

Each changed pose still computes every independent member target, writes the
publication roots, synchronizes once, and reads every original native/Transform
pose. Retained parent/local-TRS checks remain exact. A separate frozen 1e-5
relative world-scale budget covers shape readback without changing pose budgets.
Restoration reinstates captured parent, sibling-index API value and scale for
every original member before restoring body ownership; the empty owned root is
then retired. Captured sibling-index equality does not establish global scene
root ordering.

The bounded 426-body native witness used one presentation root and 1,329 world
transform writes instead of 384,678 eager writes. It passed the original control,
momentum, frame, pose and stock-handback gates, including 577,230 member readbacks
and 904 synchronizations. There were 2,256 forest validation passes, each scanning
427 nodes. Timed writes fell from 97.12 to 12.28 ms, but total publication rose
from 1,079.27 to 1,245.90 ms with the additional hierarchy/scale proof work. This
qualifies the representation and restoration, not a total-speed or FPS gain.
The next boundary is ownership/invalidation and duplicated laboratory proof cost,
while preserving detection of external writers.
