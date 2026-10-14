# Acquired-craft mission situations

`KspContinuum.CausalFlightLab` contains two short authored mission situations in
`MissionSituationCatalog`. They use the same acquired native-body/part/provider/
aerodynamic/central-body inputs as the owned flight engine. The catalog contains
placement, controls and operation descriptions; it does not fabricate captured
facts. Each run exports its selected manifest and a source-bound receipt.

The orbital sequence lasts 1.125 physical seconds. It places the acquired craft
in a circular orbit above the captured atmosphere, then exercises an engine burn,
throttle change/cutoff, wheel and RCS reversals, a center impulse, an acquired
connection split/rejoin, and 4x/32x requested progress. Existing actuator rows are
explicitly enabled by this authored script; actual scalar capability, fuel routes,
wheel reservoirs and resource quantities remain those imported from the capture.
The receipt names every enabled slot. Missing actual engine/RCS/wheel rows or
undelivered propellant/electric use fails the situation.

The reentry sequence lasts .5 physical seconds. It uses an authored airborne
placement at 25 km (or 40% of the captured atmosphere depth when smaller), 1.8 km/s
air-relative tangential speed and 100 m/s descent. Corotation is derived from the
captured body spin. It exercises actual acquired aero surfaces, thermal state and
wheel reversals. Positive captured density and surface work are required. This is
an airborne spherical-body model: it does not implement contacts, terrain,
landing, arbitrary stock providers or stock reentry equivalence. Final endpoint
altitude/density/temperature observations are not extrema over the whole path.
The authored manifest declares `minimumMechanicalIntervalSeconds`: orbital keeps
the original default 1e-4 s; reentry permits refinement down to 1e-6 s. Its first
attempt exceeded the unchanged mechanical error budget at the previous artificial
1e-4 s floor. The failed receipt is retained. This changes the allowable refinement
domain, not the speed, density, physical model or acceptance tolerances. A smaller
permitted interval is not a numerical certificate; each accepted interval must
still satisfy the same estimator budgets, and its actual controller/refinement
state is retained in the receipt/checkpoint.

All cue offsets are binary-exact relative runtime times. Receipts map every
applied cue into the same captured universal epoch, require exact runtime time and
sequence order, and retain the timeline's original event ledger. No renderer
callback advances simulation. Warp increases the demanded horizon rather than
the mechanical step. Caller-selected per-pump commit and wall budgets bound work;
remaining demand is drained without replacing or discarding it. A final short
quantum is admitted only as an explicit authored endpoint boundary.

The orbital continuation witness saves at .015625 with pending thermal energy,
future controls and .5 seconds of simulation debt. It runs a bounded original
continuation, restores the complete execution checkpoint, and repeats that exact
continuation. All normalized-independent fields are **unmodified** in this check:
the complete binary packet must match, including resource/provider/history,
mechanical-controller, thermal, flight, pilot, controls, future events and demand
policy. This repeated functional branch is separately clocked; it is not a
performance comparator. System/bank counter differences in its restore phase are
marked `workCountersRebound`, because restoring a host rebinds observational
counters. Explicit phase `localCommits` and `eventsApplied` count actual returned
work, while `netLocalCommitChange` and `netEventChange` describe endpoint ledgers.
The worker may also drain already requested debt between acknowledgements;
returned deltas alone are not a total execution count. Without a restore/rebind,
endpoint ledger differences and the mechanical controller give that total.

The impulse witness applies impulse and controls at one already stopped physical
frontier. It requires no force fold or elapsed time, checks exact `v += impulse/m`,
and checks unchanged center angular momentum. Topology witnesses independently
sum region mass/linear/chassis angular momentum before and after each same-time
transaction, using a declared relative rounding bound of 1e-10, and check unchanged
authored identities, resource quantities, provider state and clock. Splitting an
authored connection is not a stock decoupler or physical docking-port model.
The oracle subtracts the first region's root/rotated local COM and velocity before
compensated accumulation. The common orbital velocity contribution to angular
momentum cancels analytically; it is not multiplied by a rounded absolute COM
residual. A single region returns its original chassis angular-momentum bits
directly. Every situation runs cold independent regressions for this exact
single-region identity, a two-region reduced-mass analytic case, and common
translation/bulk-velocity invariance. The original 1e-10 comparison budget is
unchanged; correcting this observation does not certify the separate production
topology projection. The first failed orbital receipt remains evidence of the
previous oracle's cancellation defect.

The partial `Program` entry is:

```csharp
MissionSituations(input, output, selector: "all", method: "embedded",
    maximumCommitsPerPump: 32, wallBudgetSeconds: .004,
    deadlineStrategy: "retained")
```

Selectors are `all|orbital|reentry`; mechanical methods are `fixed|trials|embedded`;
deadline strategies are `oracle|retained`. The root command-line owner supplies
argument dispatch. No files in `Main` or the existing compiled-execution runner
are changed by this library.

Each fresh output directory receives `mission-situations.json`, per-situation
`*-mission.json`, final `*-final.ofcb`, and the orbital `orbital-pending.ofcb`.
Receipt schemas are `continuum-mission-situations/v1` and
`continuum-mission-situation-receipt/v1`. Original input and executable hashes,
clock frequency, method/deadline treatment, initialization-to-shutdown complete
clock, per-phase complete clocks, actual event order, controller/thermal state and
worker faults are retained. Phase clocks include dispatch and attempted work.
Nested bank/system clocks are nonadditive. Codec/continuation/topology observation
costs have named phases, and initialization/shutdown remain in complete cost.
Rejected embedded attempts remain in `FailedSteps` and work counters; they are
not physical faults when refinement accepts a valid interval. Success instead
requires the final accepted mechanical error contract, no latched worker/timeline
fault, an exact debt-free endpoint, and actual worker shutdown/join. The topology
angular-momentum check is explicitly chassis-only, not a rotor-plus-chassis total
momentum proof.

Comparing the two deadline strategies requires the same input pins, method,
manifest, work budgets and complete state check; a short headless work result
does not establish native KSP FPS or a global numerical error certificate.
