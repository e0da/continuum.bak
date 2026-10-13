# Actual-input owned flight mission

```text
dotnet run --project tools/KspContinuum.OwnedFlightLab -c Release -- --native /ABS/native.json --parts /ABS/parts.json --context /ABS/world.json --aero /ABS/aero.json --output /NEW/ABS/mission [--resource-policy acquired-surplus]
```

Inputs must be actual matching acquired packets. The consumer hashes the four
JSON inputs and collider geometry, rejects symlink paths, and rechecks originals
after the mission. It never launches KSP, edits an installation or renders pixels.

The receipt separates captured placement/coast admission from explicitly
engineered Sunward circular orbit and atmospheric passage. Preparation uses the
captured central body's gravity/radius/atmosphere and the complete acquired craft;
it declares zero chassis spin and preserves resources. No fabricated substitute
craft or stock trajectory equivalence is used.

The mission executes local orbital physics, passive vacuum prediction, an atomic
coast-to-node impulse, bounded real wheel/RCS guidance with gross resource draws,
an acquired-table atmospheric forecast/passage, origin rebase, combined binary
checkpoint and exact continuation, executable field-code replacement/failure
rollback, and split/re-lock of the original authored edge with retained history.
This topology case does not create a new docking connection. Impulses are declared engineering
events, not propellant-consuming finite burns. Vacuum coast explicitly freezes
passive thermal/resource state. Guidance authority is a resource-available
nominal upper bound; actual shared-resource grants remain the systems owner's job.

Before node execution, the consumer writes and decodes a combined pending-node
checkpoint, verifies the restored event/source/code/configuration bindings, and
compares exact post-node continuation of original and restored owners. Failed
schedule rollback hashes the full typed combined checkpoint; arbitrary runtime
objects are not serialized and no cycle-ignore setting drops state.

Predictions have step/wall bounds, immutable source stamps and independent cold
fork state. A changed authoritative epoch/code/configuration stales the result.
Costs include observed preparation, mission work, forecasts and coarse phases;
there is no FPS or whole-game performance comparison claim. Native visual and
contact/terrain/articulation/mod-fidelity qualification remain separate.
