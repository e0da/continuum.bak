# Actual-input independent flight timeline consumer

```text
dotnet run --project tools/KspContinuum.OwnedFlightTimelineLab -c Release -- --native /ABS/manifest.json --parts /ABS/owned-parts.json --context /ABS/flight-world.json --aero /ABS/owned-aero.json --output /NEW/ABS/output --resource-policy acquired-surplus
```

The consumer imports matching acquired native bodies, part/resource/actuator
facts, central-body context and acquired-area aero recipe. It does not acquire
KSP data, launch a player, invent substitute craft geometry or mutate inputs.
The resource-surplus policy is explicit and preserves source amounts/capacities.

Four callbacks schedules—30, 60, 144 and irregular—feed the same wall policy:
one second running, half a second paused, half a second normal, half a second
4x and half a second normal. This owes four simulation seconds. The same
captured model receives local quanta bounded to 0.02 seconds and a held small wheel
attitude input. All 200 quanta and full binary physical checkpoints must agree. Actual control
boundaries and explicit end-of-recording may shorten a terminal quantum; render
callbacks themselves cannot introduce partial physics steps.
The schedules are authored observation partitions, not measured renderer FPS.
Requested debt is drained while paused at the same terminal wall timestamp
without adding time, and terminal debt commits are reported separately.

Receipts bind original inputs, executable/Core hashes, per-case preparation,
complete scenario/checkpoint CPU clock and allocations, requested/committed time,
maximum/final debt and exact physical checkpoint digests. First-use costs are
retained; these observations are not a causal performance benchmark. A failure
remains a failure and cannot be repaired by widening physical comparisons.

## Actual visual host integration

`OwnedRuntimeWorkbench` uses the same `OwnedFlightTimeline` in central-body mode.
Stopwatch wall demand replaces its prior clamped Unity delta accumulator. Warp
scales requested duration while the physical quantum stays fixed. The pump is
bounded to eight commits/eight milliseconds per render update; all remaining
work stays visible as debt. Camera/mesh presentation reads committed state once
per Update, with no interpolation feeding physics.

Existing controls remain. `pause on|off` freezes/resumes accrual and pumping;
`step N` performs explicit paused quanta. `timeline drain` consumes at most eight
owed quanta while paused, including a final explicit partial debt step.
`render-rate -1|30|60|144` changes only presentation regulation. Status exports
actual render count/cap/viewport and timeline requested/committed/debt clocks.

Placement, topology edits, pilot configuration and physical checkpoint save/
restore require a paused debt-free boundary. A checkpoint preserves physical
flight state; it does not silently claim to serialize outstanding wall debt.
Manual `coast SECONDS freeze-passive` retains the existing strict passivity
admission and explicit frozen thermal/resource policy. `clear-node` releases an
abandoned event barrier while preserving debt. Schedule HMR is paused and uses
a same-time acknowledgement to preserve queued controls/debt; pilot feedback is
resolved per physical quantum and its configuration can change only debt-free.

Unframed legacy prototype mode remains separately labelled: its wall accumulator
also stops clipping elapsed time and retains bounded-pump debt, but it does not
claim central-body timeline policy/history. Existing original KSP and other
renderers are unaffected. Native rendered clocks, pause/warp/debt/HMR and actual
craft/planet pixels remain root-owned qualification gates.
