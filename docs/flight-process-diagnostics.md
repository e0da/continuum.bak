# Aligned flight process diagnostics

`--flight-native-sample true` is an intrusive diagnostic of the owned game PID,
not an unprofiled performance arm. Native sample receipt v2 requests two seconds
at a one-millisecond interval. Historical five-second receipts are unchanged.

The runner retains capture signals, sampler PID/exit/hash, and native
`mach_absolute_time` readings before/after spawning and observing completion.
The game must publish `machClockStatus=available`,
`machAbsoluteBeforeTicks`, `machAbsoluteAfterTicks`,
`machTimebaseNumerator` and `machTimebaseDenominator` at both capture boundaries.
The game's two readings bracket its existing Stopwatch boundary timestamp.

Qualification requires matching ownership, successful capture/completion,
identical nonzero native timebases, and this conservative ordering:

```
capture-start after <= spawn before <= spawn after
    <= completion-observation before <= completion-observation after
    <= capture-end before
```

Only this proof produces `status=completed`. Missing clocks, ambiguous ordering,
failed or interrupted sampling, and missed capture remain explicit failures.
Delayed signal-file publication cannot establish enclosure. No Mono Stopwatch
clock equivalence or wall-clock stability is assumed. A roughly ten-second
capture comfortably admits the two-second sampler; shorter windows can reject
because launch, report processing or completion observation extends beyond them.

Raw `ps` snapshots retain process RSS and lifetime CPU accounting. They are not
capture CPU deltas. The game's boundary CPU/RSS observer has its own timestamp
brackets. Sampling tree occurrences describe statistical thread occupancy, not
exclusive CPU milliseconds, GPU duration or proof of why a thread waited.
PlayerLoop parents overlap children and cannot be added together. Sampling
perturbs scheduling and its flight cannot be compared as an uninstrumented arm.
