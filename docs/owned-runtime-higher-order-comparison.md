# Acquired higher-order comparison

The portable acquired-model comparison is `HigherOrderExecution` in the existing CausalFlightLab, selected with `--execution-only true --execution-modes higher-order`. The `higher-order-powered` mode reruns only changed powered cases. This helper does not install or launch KSP. It reuses the admitted native manifest, part specification, aerodynamic rows, central-body context and existing import/checkpoint pipeline.

Four declared fixtures retain the acquired craft, resources and provider reservoirs: circular 100km vacuum or Sunward 35km atmosphere at 300m/s relative to rotating air, each quiet and powered. Source spin and rotor reservoirs are not silently reset. Powered controls retain the existing authored request `(throttle .35; attitude .05,-.025,.02; translation .05,0,.025)`. Unsupported RK23 mass/rotor domains remain visible midpoint fallbacks. These fixtures are engineered conditions, not the original captured trajectory.

Every method starts from the same per-case binary checkpoint. The ordinary comparison keeps the frozen position/velocity `1e-4`, angle/scaled-state `1e-6` tolerances, a `1e-6` interval floor and `.1` maximum. Midpoint and RK23 each receive two sequential observations. A separately charged fine RK23 run caps intervals at `1e-4` seconds and uses the same physical tolerances; its fallbacks are retained. It is a finer execution of the same model, not an independent source-physics oracle. Independent constant-force, circular, torque and asymmetric-rotor checks reside in EmbeddedMechanics.Tests.

The first `.02` simulated seconds are reported as cold preparation/execution; the following `.23` seconds are the complete timed hot interval. Timing includes accepted and rejected physical transactions, scalar allocation, all force sampling, numerical guards, thermal publication, bank copying, property checks and validation. Source import/placement and cold work are separate. Snapshot extraction, binary checkpoint writing and exact restore are outside hot timing and explicitly verified. Allocation counts and hot observation/counter deltas accompany complete cost; timing buckets overlap and must not be added to the complete total.

Final region COM position, velocity, quaternion, world momentum, mass/full inertia, resources, providers and temperatures are retained. Comparisons expose endpoint differences and thermal frontier differences against the finer run. `withinLocalBudgetNumbers` merely reports whether three endpoint differences fit the *numbers* used for local budgets; it is not a global-error certificate. Receipt `passed` means cases completed with their original local acceptance gates and exact checkpoint restore, not that either method is more accurate or faster. Temperature forcing-quadrature differences remain separate from mechanical acceptance. Failed rows preserve their reason and make the comparison status failed.

The command rehashes acquired inputs after execution, writes fresh per-row JSON and binary files, and charges fine-reference cost separately. No renderer pacing, native PhysX cost, stock KSP equivalence or playable FPS claim follows from this comparison.

## Observed acquired-model scope

Two observations on the retained captured craft reduced complete hot cost of the
quiet atmospheric case to 7.46% and 6.95% of midpoint cost. Endpoint differences
against the separately charged finer execution were approximately 5e-9 metres,
1e-11 metres/second and 5e-12 radians. This is a scoped numerical-workload result,
not a stock-game framerate result or a general precision guarantee.

Quiet vacuum did not show a consistent benefit. Powered cases required the
variable-mass fallback. A cold preflight now selects midpoint before preparing
an unsupported RK23 scalar trial, eliminating that redundant preparation.
Changed-only powered observations remained mixed; no universal cost reduction
is claimed. The original implementation remains selectable, and all comparison
rows retained their acceptance tolerances and exact checkpoint continuation.
