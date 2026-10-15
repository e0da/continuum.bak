# Acquired causal flight consumer

Run from the repository with fresh private output:

```
dotnet run --project tools/KspContinuum.CausalFlightLab -c Release -- \
  --native ABS/manifest.json --parts ABS/owned-parts.json \
  --context ABS/flight-world.json --aero ABS/owned-aero.json \
  --resource-policy acquired-surplus --output NEW_ABS_DIRECTORY \
  --legacy-checkpoint ABS/original-prethermal.ofcb
```

The original native body ledger, acquired actuator/resources and aero areas seed
the existing owned flight runtime on its actual simulation worker. Source inputs
and executable assemblies are hashed. The declared acquired-surplus policy
preserves zero-capacity positive resource facts; it is never inferred silently.

Two deterministic executions have identical semantic demand/control boundaries,
with committed presentation leases observed or suspended. Full binary physical
checkpoints must match exactly. The scenario checkpoints a positive deferred
thermal interval, restores it without flushing or dropping exposure/energy,
rejects corrupted input without mutation, swaps an executable schedule, rejects
a private-bank computation after systems execution and recovers its retained
debt, then splits/rejoins actual captured part connections. Mechanical advances
and thermal advances accepted against their declared error estimate are reported separately. A separate short
automatic-clock witness advances with no presentation/demand callbacks.

The optional original prethermal checkpoint is decoded and physically restored
against the acquired hashes. Its producer schedule identity is retained as
provenance; the old executable is not loaded or claimed continued.

The adaptive extension arms an impulse and held-control event at the exact
`.075` runtime frontier. Before either event it captures/restores the full
execution queue, adaptive controller and pending thermal ledger. A dangling
event receiver falsifier must leave both old authorities unchanged. With view
leases and render-only interpolation enabled or fully suspended, the final full
binary continuation must match exactly. A fixed-step reference executes the same
controls/events/checkpoint transaction once; complete costs and final numerical
differences are reported. Embedded local error estimates are not global
trajectory, energy or FPS guarantees. Retained isolated trials remain charged.

Preparation, thread coordination, checkpoint encoding and scenario work remain
in complete elapsed cost. Allocations count the worker thread after preparation,
not total process allocation. This is a functionality/continuation consumer,
not a renderer FPS or original KSP dynamics comparison. No terrain/contact or
universal stock module equivalence is implied. Failed receipts stay failed;
the root integration owner runs builds and qualification.

Use `--execution-only true` for the compiled/staged engine comparison. Four
captured situations cover quiet and powered vacuum and atmosphere; each compares
reference, compiled reference, full-trial adaptive and embedded mechanics with
two resolving observations. Identical reference warmup ends at.2 seconds; the
first new-method window through.3 is recorded separately from one simulated
second of retained work. Complete interval cost includes bank copying, scalar
work, force sampling, thermal evolution, numerical control and publication.
Optional stage clocks are enabled equally for the comparison and reported with
their row/work counters. Import/placement/warmup/plan cost stays separate.

Reference and compiled-reference full continuation must match bitwise after
excluding only their declared executionStrategy/systemsRevision metadata.
Numerical strategies have different step/history/error behavior; final
trajectory differences, energy and momentum are reported rather than relabelled
as stock equivalence. Every mode checks complete strategy/method checkpoint
restore. Failed rows remain preserved. These direct net8 physical measurements
do not establish Unity/Metal FPS, maximum KSP warp or universal global error.

Use `--execution-modes compiled` with `--execution-only true` for just Reference
and Compiled in the four quiet/powered vacuum/atmosphere situations. Two
observations per case retain the full normalized continuation and restore checks
without repeating unchanged adaptive strategies. This compares strategies in the
current build; an optimization shared by both strategies needs retained
cross-build evidence to establish its before/after cost.

Use `--execution-modes adaptive` with `--execution-only true` when only the
adaptive execution paths changed. It runs the two numerical strategies and
retains earlier reference/compiled evidence without repeating unchanged cases.

Use `--execution-modes deadlines` with `--execution-only true` for two powered
captured situations, comparing the bounded oracle and retained scheduler under
the same compiled embedded mechanical operator. Two observations per case
record full work/certification/fallback counters and complete checkpoint
equality, excluding only declared strategy/configuration metadata. Any physical
difference remains visible in the raw packets and numerical observations.

Use `--mission-situations all|orbital|reentry` to execute the reusable authored
[mission situations](../../docs/owned-mission-situations.md). `--mission-method
fixed|trials|embedded` and `--deadline-strategy oracle|retained` select explicit
treatments. The default mission uses compiled embedded mechanics and retained
deadlines. Orbital controls, burn/cutoff, reversals, exact impulse,4x/32x demand,
pending-debt/future-event/thermal restore and acquired split/rejoin run as one
bounded program. Reentry uses captured atmosphere and an authored airborne
state. Costs of replay/restore and cold conservation checks stay separate.
These are executable functional programs, not repeated whole-game FPS runs.

Compare retained runs without simulating again:

Use `--forecast-situations all|entry|atmosphere` for the functional
[forecast situations](../../docs/owned-forecast-situations.md): one admitted
elliptic atmosphere-entry partial coast followed by local work, and a captured
table-knot forecast with pending thermal checkpoint continuation. The runner
retains raw observations and binary checkpoints; complete elapsed costs include
validation and replay. No unchanged performance trial or speedup is inferred.

```
dotnet tools/KspContinuum.CausalFlightLab/bin/Release/net8.0/KspContinuum.CausalFlightLab.dll \
  --compare-execution OLD_ABSOLUTE_DIR NEW_ABSOLUTE_DIR FRESH_ABSOLUTE_RECEIPT
```

Both directories must contain identical top-level checkpoint filename sets.
For a deliberately smaller changed-case run, retain an explicitly selected
baseline subset before comparison. Each packet is independently decoded. Only
the builtin executable source hash, execution strategy and systems revision are
declared treatments and excluded; original values and full packet hashes remain
in the receipt. All other bank, history, resource, mechanical, thermal, flight,
pilot, control and execution/event state must match as complete normalized binary
packets. Differences and malformed packets produce failed receipts.
