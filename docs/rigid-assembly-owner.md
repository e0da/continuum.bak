# Rigid assembly motion boundary

The primary model gives each locked region one native rigid body and retains authored parts, colliders and gameplay identity. Internal joints disappear from that region's motion solve. Breakage or an already-resolved graph cut changes ownership; mechanisms and optional flexible structures remain separate regions/solver workloads.

`RigidAssemblyProperties` combines member COM offsets, rotated principal tensors and parallel-axis terms into a full tensor, then diagonalizes it into the native engine's principal-axis representation. Inputs are ordered by retained ID; invalid/nonfinite/overflowing properties and unnormalized default rotations fail admission. Independent analytic tests cover 3D offsets, principal frames, degeneracy and extreme scaling. Near repeated eigenvalues the principal basis can change abruptly even while the physical tensor remains continuous; no orientation continuity guarantee is made for that basis.

`RigidAssemblyOwner` publishes aggregate mass/COM/inertia into an isolated Unity native body. Closed internal mass transfer scales the members' specific inertia and preserves P and angular momentum about a common fixed origin. Geometry stays fixed; the operation does not model fuel transport or conserve kinetic energy. Separation prepares two inert native roots, moves retained collider hierarchies, inherits point velocities and angular velocity, then retires the old body. Ordinary publication failure attempts to restore the prior owner; failure injection/rollback fidelity remains a separate gate.

## Fast native consumer

`tools/metal-shader-donor/Assets/RigidAssemblyWorkbench.cs` boots only when `CONTINUUM_RIGID_WORKBENCH_OUTPUT` names a fresh absolute private directory. Build with the existing pinned `BuildAeroFieldWorkbench.Build` command, with the multiscale/aero runtime bootstraps unset. The build receipt binds owner, host and property source hashes as well as the built managed assembly. This Unity2019 x86_64/Metal laboratory is not the modern ARM64 engine migration.

The host renders continuously with an orthographic camera and metre grid, but advances only its isolated `PhysicsScene` on explicit commands. `status.json` gives PID and ephemeral loopback port. Each connection sends one newline-terminated command:

```
status
captureframe
assembly impulse
assembly transfermass
assembly split
assembly step 10
captureframe
quit
```

The three unequal, offset and rotated members receive a known off-center impulse. Mass transfer receives a second native impulse before separation, proving the original updated owner participates in simulation. Two resulting compound owners continue for ten 0.02-second steps. Receipts retain six native states, member/collider IDs, body generations, raw body/member properties, impulses and momentum residuals. Queued requests expire before execution; an already-running request reports its ID/pending status and the caller must inspect status before issuing another mutation.

Native budgets are fixed before execution: immediate linear/angular momentum transactions <=1e-4 relative; impulse linear <=1e-4, angular <=0.005; continued linear <=1e-4, angular <=0.02, with norm denominator max(1, reference norm). Geometry position <=1e-4 metres; rotation agreement 1-abs(dot) <=1e-5. These are short native laboratory limits, not long-flight accuracy claims.

The independent receipt verifier recomputes world COM, full aggregate inertia and P/L from the native member/body fields without invoking the production property calculator:

```
dotnet run --project tests/KspContinuum.RigidAssembly.Tests -- /absolute/private/run/status.json
```

No arguments runs the portable analytic property checks, included in the existing portable CI consumer. No repeated unchanged timing observations are required for this behavior proof.

## Original KSP integration boundary

Ordered body aero capture v2 stores one snapshot per distinct reached body per original integrator root, before that body's first original command. The explicit 27-binary32-bit-word layout includes mass, pose, COM, principal inertia/frame and velocities; snapshots bind command sequence and clocks, liveness/activity/kinematic/sleep state, and the existing part/body alias ledger. Getter and bookkeeping cost is charged to the observer/root. Original call order/count is unchanged, and v1 recordings retain their original admission path.

This is a first-command snapshot, not proof that properties stay fixed for the rest of the root. Later mass/COM/inertia writers, positioned acceleration semantics, damping, providers, original direct Rigidbody consumers, full topology events and actual collider reconstruction remain live takeover gates. A synthetic native owner success does not close them. Full aero ownership and actual KSP motion replacement are the next integration outcome.

## Parallel execution alternatives

For rigid regions, sum independent loads and integrate a small number of aggregate states. For genuinely flexible regions, compare consistent-snapshot Jacobi/XPBD, graph-colored passes, implicit sparse methods and small substeps at equal behavior/error limits. Random mixed-generation neighbor reads are a distinct delayed-coupling model and need stability/conservation evidence.

Continuum already has retained WGSL compute in `tools/numerics/src/gpu_execution.rs` through wgpu, currently requesting Metal explicitly. Keep portable data and anchored-coordinate precision contracts; reuse this path when it saves iteration time. Direct Metal specialization remains an option. Resident state, cached pipelines and finite batched dispatches are the initial GPU strategy; cross-threadgroup spin barriers and indefinite kernels are not assumed valid. Charge input refresh, completion, synchronization, publication/readback and actual render consumption. Small rigid-region counts may favor CPU; larger workloads remain a measured question. Mac is the qualification target; desktop/mobile/TV backends are not qualified by dependency selection.

Primary research: [parallel constraint solving](https://mmacklin.com/uppfrta_preprint.pdf), [small substeps](https://mmacklin.com/smallsteps.pdf), [implicit sparse integration](https://publications.ri.cmu.edu/large-steps-in-cloth-simulation), [wgpu backends](https://github.com/gfx-rs/wgpu), [Metal synchronization](https://developer.apple.com/documentation/metal/resource-synchronization), [Metal timeout](https://developer.apple.com/documentation/metal/mtlcommandbuffererror-swift.struct/timeout).
