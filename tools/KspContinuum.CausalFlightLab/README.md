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

Preparation, thread coordination, checkpoint encoding and scenario work remain
in complete elapsed cost. Allocations count the worker thread after preparation,
not total process allocation. This is a functionality/continuation consumer,
not a renderer FPS or original KSP dynamics comparison. No terrain/contact or
universal stock module equivalence is implied. Failed receipts stay failed;
the root integration owner runs builds and qualification.
