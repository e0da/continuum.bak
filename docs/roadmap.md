# Continuum direction and goal index

Direction updated 2026-10-10 from the local research program. Continuum and Gimbal share the goal of supporting Kerbal-like, toy and realism experiences. Their runtime and repository boundaries remain open. This page preserves existing research questions and issue links; [Linear issue state](https://linear.app/e0da/project/continuum-3e8d50ef8c85) has not been reconciled by this documentation update.

The KSP addon measures and replaces host systems; its per-part callbacks and render loop need not constrain the eventual engine. Continuum may grow into a complete KSP/Unity replacement and support Gimbal capabilities. Findings and code can flow in either direction, with shared components or a merger selected when useful. [The provisional boundary](continuum-gimbal-boundary.md) preserves distinct experience contracts and explicit runtime ownership. `KspContinuum` remains the adapter namespace.

Independent coasting at requested times and a bounded KSP presentation adapter remain useful lab experiments, not prerequisites for Gimbal. Any live adapter must avoid duplicate stock propagation; presentation smoothing must not feed back into integration. The first adapter may retain KSP's global universal time while owning one vessel trajectory; that would not establish independent global warp, background resource simulation or whole-game replacement. Measure kernel throughput, synchronization cost and complete game frame/tick cost separately.

## Discoveries and limits

| Observation | Consequence |
| --- | --- |
| A023 replaced native physics with an empty callback for two cached ticks, then restored the exact node | The interception path works in this admitted orbit. No custom dynamics, trajectory parity, graph replacement or FPS improvement is established. See [live substitution](live-substitution-canary.md). |
| Internal-frame writer measurements distinguish native motion from script-driven origin/frame changes | Compare local physical motion; world-space changes alone do not identify a dynamics writer. The native node is global, so takeover must account for other loaded bodies. See [writer census](writer-census.md). |
| C# query/routing/publication contracts and Rust persistent views both exist | These are separate implementations, not an integrated Rust ECS scheduler. Rust may own engine identity; the KSP adapter owns host publication. See [compiled views](execution-views.md) and [persistent views](persistent-execution-views.md). |
| Cold Metal lost across tested sizes; 64-step resident Metal crossed scalar at 16,384 entities on M4 Max | Residency matters. These synthetic throughput results exclude the live bridge and rendering contention and compare GPU f32 with CPU f64. Measure one-step latency and a precision-matched CPU baseline before routing live work. See [benchmark](execution-view-benchmark.md). |
| Local f32 offsets plus f64 anchors reduced the fixture's accumulated error from 11.27 m to 0.00571 m | Local representations are promising; this is not proof of arbitrary large-world accuracy. Per-channel error budgets still apply. |
| Dirty refresh beat full pack through 10% changed rows for large measured views; full churn favored repacking | Preserve views, but include dirty discovery and publication in real costs. A million-row benchmark does not predict performance for a 400-part craft. |
| Powered-landing aero baseline missed magnitude while captured scalar-product reconstruction closely matched stock | Prioritize deterministic occluded-face/SetDrag reconstruction. A reconstruction consuming stock outputs is a diagnostic, not a replacement. See [aero](aerodynamics.md). |
| The opt-in full-output `SetDrag` provider preserved parity and its admission-boundary strategy measured about 45% below stock at that method seam; per-step Harmony graph reads erased the gain | Treat provider topology as a frozen configuration epoch, then measure a coarse Continuum-owned aero batch in the whole fixed tick. No FPS or tick gain is established. See [aero](aerodynamics.md). |
| A representative semantic fixture reduced 10 bodies and 9 joints to 6 bodies and 5 joints while isolating one unknown part | Populate the same facts from a real vessel, then qualify collider geometry and mass properties. The 40% body and 44.4% joint reductions are projections, not a measured KSP speedup. |
| Four-step x86 KSP-to-Rust transactions beat managed execution at 64 and 256 bodies, while one-step full-copy transactions lost everywhere | Use persistent pinned views, dirty refresh and selective publication. Native compute alone does not justify crossing the boundary. |
| The first native interval after A023 restoration showed about 1.99 times the largest ordinary normalized velocity delta | Skipping the native callback does not own deferred forces. Keep rigid-cluster publication offline until force producers are owned or isolated. |
| The repo-scoped `continuum-linux-arm64` ARC lane completed the integrated `fbba8761` main run | Owned CI is operational. Keep stale-run cleanup and bounded capacity separate from product performance claims. |
| Missions, inputs, checkpoints and the connected chronicle work as fixtures | Preserve attempts and media. Telemetry scrubbing and native save restoration are not deterministic whole-game replay. |
| PR87 merged while six checks were queued; the cause was a stale runner label after the ARC lane rename | PR92 aligned the workflow with the installed lane and the integrated main run passed. E0D-1881 is complete. |

## Continuum research lanes

These lanes preserve specific KSP research questions and issue history. Select work from the shared program's current evidence and desired capabilities. One writer owns any live KSP instance. Portable work can continue when live qualification is blocked.

| Lane | Acceptance | Linear |
| --- | --- | --- |
| Independent coast and time | Evaluate two-body trajectories from immutable seeds at requested times; stop at a declared time boundary; show cadence-independent results and bounded numerical error | [E0D-1876](https://linear.app/e0da/issue/E0D-1876) |
| KSP trajectory adapter | Identify and suppress the selected stock trajectory writer while retaining presentation; compare sampled state and release continuity in the owned instance | [E0D-1871](https://linear.app/e0da/issue/E0D-1871) |
| Live physics | Select a provider or isolated world with explicit force ownership; then run nonempty bounded dynamics with stock/candidate motion, exact restoration and full tick/frame timings | [E0D-1871](https://linear.app/e0da/issue/E0D-1871) |
| Aerodynamics | Move from the successful direct `SetDrag` probe to a coarse batched engine boundary and measure capture, compute, publication, and complete fixed-tick effect | [E0D-1872](https://linear.app/e0da/issue/E0D-1872) |
| Rust execution | Connect the measured x86 in-process boundary to one captured KSP workload through persistent pinned views, dirty refresh and selective publication | [E0D-1873](https://linear.app/e0da/issue/E0D-1873) |
| Structural reduction | Populate the conservative classifier from a real vessel; then validate one admitted compound candidate's collider and mass properties | [E0D-1874](https://linear.app/e0da/issue/E0D-1874) |
| Performance observation | Record workload identity plus capture, pack, compute, synchronization, publication and total timings in a regression-friendly report used by one existing benchmark | [E0D-1875](https://linear.app/e0da/issue/E0D-1875) |
| Metal rendering research | The matching-Unity donor and graft tools support a selected shader batch; the two-shader KSP menu capture remained black and diagnostic KSP-specific substitutions stalled. Visible flight rendering and a matched 1080p OpenGL/Metal comparison remain unqualified. See [shader experiments](metal-shader-swap.md). | [E0D-1886](https://linear.app/e0da/issue/E0D-1886) |

The smallest useful KSP replacement may be managed C#; Rust/GPU integration remains a measured strategy. Structural graph classification can support later live dynamics before geometry baking, and live graph mutation follows dynamics ownership. These experiments can donate fixtures or algorithms to Gimbal without imposing KSP's host architecture on it.

The KSP replacement path includes **useful live substitution**, **demonstrated game-performance improvement**, **simulation beyond the camera**, and potentially **complete runtime replacement**. The [research program](ksp-research-program.md) defines independently qualified outcomes and the current repetition policy. [E0D-1875](https://linear.app/e0da/issue/E0D-1875) retains the historical KSP performance outcome: frozen workload/tolerances, matched comparisons, median and tail timings, and honest negative results. More physics fidelity can be a separate opt-in benefit, but cannot be counted as stock speedup without a like-for-like comparison.

Performance is a design input for every replaceable system. Each strategy must make the whole path observable: capture, admission, packing or dirty refresh, transfer or ABI, compute, synchronization, validation, publication and total frame/tick cost. Reports retain workload and machine identity, behavioral error, allocation or transferred-byte evidence where measurable, median and tail latency, throughput and strategy choice. CPU scalar, CPU parallel, SIMD and GPU implementations compete only on workloads for which the entire measured path and precision contract are comparable.

## Full goal map

| Goal family | Preserved scope and next owner |
| --- | --- |
| Believable efficient physics | Less pathological wobble, flex/breakage, reliable rover wheels/grounding/loading, staging/docking, complex vessels and explosions. [Structural](semantic-cluster-compiler.md), [modal](modal-reduction.md), [contact](contact-checkpoints.md); E0D-1871/1874/1875. |
| Engine data and computation | Stable identities, engine-owned data, KSP reconciliation, query-selected layouts, CPU cache/SIMD/parallelism, GPU residency, contextual strategy selection. [Replacement](replacement.md), [views](execution-views.md); E0D-1873. ARM64 SoA has observed NEON code generation and repaired column skew removed a measured aliasing cliff, but SoA still loses to AoS and neither result proves a KSP gain. |
| Regimes, time and background activity | Local frames, independent islands, worldline tubes, conservative swept encounters, smooth event-aware warp, quiet long coasts, precise docking, concurrent distant descents. [Regimes](interaction-regimes.md), [tubes](worldline-tubes.md), [encounters](encounter-scheduler.md); [E0D-1876](https://linear.app/e0da/issue/E0D-1876). Separation does not remove long-range gravitational coupling; choose the orbital approximation explicitly. |
| Reproducibility and mission testing | Scripted missions, input curves/Bezier edits, autopilot isolation, checkpoints, branch histories, reproducible qualified kernels, scrubbing and eventual control handoff. [Inputs](input-timeline.md), [branches](program-branches.md), [playback](telemetry-playback.md); [E0D-1877](https://linear.app/e0da/issue/E0D-1877). Rewind through dissipative/contact events needs recorded state/history. |
| Compatibility | Stock feel first, explicit FAR/RO/MFI/KSPCF/KJR/Principia profiles, shared libraries and lifecycle cooperation; scoped multiplayer authority/warp integration. [Compatibility](compatibility.md), [integration map](integration-map.md), [aero behavior](aero-behavior.md); [E0D-1878](https://linear.app/e0da/issue/E0D-1878), Gimbal E0D-323/325. |
| Our model space program | Deliberate mission/craft/save naming, daytime flat-site survey, probe carrier and reusable scouts, visible tests when useful, maintained linked multimedia chronicle, templates, useful plots including log scale. [Program](space-program.md), [chronicle](chronicle.md), [templates](wiki-templates.md); E0D-1877. Further Minmus targeting is parked; the carrier has not been built. |
| Candidate representations | Semantic/voxel geometry, modal reduction, Jolt/Box3D comparison, small surrogates, Monte Carlo blast/risk/control, ASC/toy models. [Learned compute](learned-compute.md), [trajectories](trajectory-representations.md); [E0D-1879](https://linear.app/e0da/issue/E0D-1879). Field/N-body/Hilbert/phase experiments are optional research, not critical-path commitments. |
| Shared experience and runtime direction | Kerbal-like, Gimbal toy and realism experiences with explicit behavior/fidelity contracts. Continuum replacement work and Gimbal development can exchange findings and components; shared runtime or repository convergence remains open. Arbitrary binary plugins require qualification. [Boundary](continuum-gimbal-boundary.md); historical [E0D-1880](https://linear.app/e0da/issue/E0D-1880). |
| Delivery | Rust systems/tooling, C# KSP integration, Elixir only for a concrete OTP need; minimal unavoidable native ABI. Owned actions/ops/stack runners, no Python or added scripting runtimes. [AGENTS](../AGENTS.md); [E0D-1881](https://linear.app/e0da/issue/E0D-1881). |

## Working changes from the retrospective

- Pursue the shared experience/runtime direction through measured complete-system replacements and useful playable checkpoints. Keep the repository boundary provisional; choose extraction or convergence from actual integration needs.
- Maintain independent physics fronts; serialize shared game-state writes and integration only.
- Keep one current goal map and update the owning issue and affected docs when a milestone changes. Do not accumulate contradictory “next” instructions.
- Record measured failures and limitations alongside wins. Different precision, batching, transfer and rendering costs must remain visible in comparisons.
- Use the existing mission fixtures and chronicle for engineering feedback. A more accurate Minmus landing is not a prerequisite for engine work.

Original estimates, active time and rework cost were not consistently recorded and cannot be reconstructed precisely. This review does not invent retrospective estimates or claim a measured productivity improvement.
