# Component savings and performance gates

FPS alone cannot decide whether an implementation reduces work. Keep behavior, complete component cost, whole-workload latency and capacity as separate results. A count reduction is an execution witness, not a measured reduction of total compute. Retain a component with a demonstrated net saving when another owner masks its visible effect. Unknown local cost remains unknown; do not invent a hidden win.

## Reconsidered results

The private research receipts remain unchanged. These classifications refine the decisions, rather than rewriting the observations.

| Candidate | Evidence | Decision |
| --- | --- | --- |
| Shared Mach values | 30,828 exact-bit checks pass. Matched instrumented root comparison saves 0.2232 ms median; short complete-flight pair is inconclusive. | Retain local positive; ordinary deployment remains off. See [step context](aero-step-context.md). |
| Complete root drag transaction | 996 original-return full-output checks pass the unchanged tolerance, plus another 996 pre-capture checks. Complete paired context 2.15984 -> 1.93056 ms; all 250 pairs retained. | Retain bounded local positive. Adjacent states, wrapper tax and intrusive sampling prevent clean FPS or incremental Mach-only attribution. See [step context](aero-step-context.md). |
| Complete SetDrag arithmetic | Warmed admission-boundary capture/reduction/publication 1.034 µs vs stock 1.880 µs; repeated ownership inspection raises guarded cost to 6.779 µs. | Retain arithmetic; guarded per-part integration loses. Move admission to a larger owner. See [aerodynamics](aerodynamics.md). |
| Compiled empty terrain callbacks | About 11.9 million empty callbacks removed; terrain envelope 2.02 → 1.99 s. No matched exclusive dispatch cost; menu did not improve. | Local saving unknown, whole-workload inconclusive. Revisit inside the real terrain owner, not another unchanged menu replay. |
| Mapped DDS upload | 938 payloads, metadata/order preserved; read/copy removal observed; menu 37.324 → 36.953 s. Complete local upload cost was not isolated. | Local saving unknown, whole-workload inconclusive. Compare parsing through upload and cleanup if that owner becomes material. |
| Ordered MechJeb access plan | Original arithmetic/order passes; 812,702 cached accesses; 50.04 → 50.27 FPS. No matched complete provider cost. | Local saving unknown, whole-workload inconclusive. Measure the ordered invocation on a useful workload. |
| Prefetch, prepared image, ready-job scheduler and reflection bindings | Ordered startup behavior retained; prepared stack reduced observed menu times. Same-helper warm bindings pair 26.804 → 24.218 s. Later stack changes do not isolate every contributor. | Retain bounded startup positives. Scheduler runs with zero owned jobs are coverage failures, not negatives. |
| Convection spatial index and tangent caches | Complete offline four-state folds lose to their original comparator, including lookup/setup/output costs. No Unity cadence or render cap. | Measured local negatives for those implementations; retain evidence and algorithms as research donors. |
| Reset-aware sparse convection | Same-state original-bit proof; complete observed Mono invocation improves, including projection/admission/fold/commit. Candidate-only FPS does not supply a matched frame gain. | Retain local positive; full-frame benefit remains unqualified. See [live convection](live-convection.md). |
| Live retained renderer | Corrected retirement preserves 279 sources in 37 packets; frame gain absent, helpers differ and no matched local retirement cost exists. | Whole-workload inconclusive; do not claim a local loss. See [replacement architecture](research-replacement-architecture.md). |
| Grouped/retained workbench | Grouped enqueue 0.148 → 0.050 ms, but process CPU and tails worsen. Retained CPU/frame improves while mean frame time stays neutral. Exact textured-image gates fail. | Retain component positives and tradeoffs. Fidelity failure still blocks promotion; timing masks cannot excuse it. |
| Deferred launch-site terrain | Work moved out of startup, but actual fulfillment fails the unchanged spawn-position gate. | Correctness failure; do not promote until fulfillment is fixed. See [terrain readiness](startup-site-terrain-readiness.md). |

## Finding the gating owner

First read effective limits. Current flight diagnostics have targetFrameRate −1 and VSync 0; their fixed step is 0.02 s with 0.04 s catch-up allowance. A fixed step defines simulated advancement per tick, not a rendering FPS cap. Normal-time simulation at 1× can hide extra simulation capacity even while uncapped frame latency changes. Physics warp that changes the timestep is a different fidelity workload.

The current clean 512-part atmospheric pair has managed fixed work around 7.1–7.3 ms per step, native physics around 2.2–2.3 ms per step, script Update around 5.2 ms per frame, and FinishFrameRendering around 4.2 ms per frame. Parent/child scopes overlap. These brackets identify owners; they do not settle which thread, wait or GPU dependency gates a frame.

Use the next needed heavy-flight diagnostic to align phase clocks with owned native thread samples and process CPU/RSS. Retain active CPU versus sampled wait/driver categories and missing evidence explicitly. The prior OpenGL timestamp observer was actually rejected by the driver with zero counter bits; an unchanged retry is not a GPU measurement. A render-end wall bracket must not stand in for GPU execution. A different supported system trace or a separately qualified modern Metal workload can answer a new question, with its applicability limit retained.

The aligned diagnostic now supplies that bounded witness: 1.84685 occupied process cores including observers; little recognized main-thread waiting and substantial graphics-worker waiting. Managed fixed is the largest observed bracket, with script Update also material. This supports resolving remaining main-thread production from existing stacks/callback evidence, without claiming GPU starvation versus fence waits or assigning unsymbolicated native Unity work. The measured frame rate is from an intrusive mixed flight, not a clean candidate arm. See [process diagnostics](flight-process-diagnostics.md).

Choose one causal intervention from the resulting hypothesis: lower world-render resolution under frozen craft/tick controls for a render-path constraint, or replay a captured pure workload back to back for compute capacity. Preserve state, coverage, transfer/publication costs and quality differences. Headless throughput is separate from visible flight; do not infer that Unity-dependent work becomes portable merely because rendering is absent.

Maintain a short retained-candidate list with the tested owner, dependencies, behavior gate, net local cost, integration tax and next resolving condition. Revisit those candidates when the integrated critical path changes. Keep removing the largest unresolved owner, while composing useful local improvements into that larger boundary. Two useful observations are enough to choose the next action; no fixed repetition campaign is required.
