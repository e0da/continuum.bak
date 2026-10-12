# Aligned flight process diagnostics

`--flight-native-sample true` is an intrusive diagnostic of the owned game PID,
not an unprofiled performance arm. Native sample receipt v3 requests two seconds
at a one-millisecond interval. Historical five-second receipts are unchanged.

The runner retains capture signals, sampler PID/exit/hash, and native
`mach_absolute_time` readings before/after spawning and observing completion.
The game must publish `machClockStatus=available`,
`machAbsoluteBeforeTicks`, `machAbsoluteAfterTicks`,
`machTimebaseNumerator` and `machTimebaseDenominator` at both capture boundaries.
The game's two readings bracket its existing Stopwatch boundary timestamp.

Native and Rosetta-translated processes can report different raw units. Apple's
[architecture guidance](https://developer.apple.com/documentation/apple-silicon/addressing-architectural-differences-in-your-macos-code)
explicitly requires applying each process's timebase before comparing their
`mach_absolute_time` values. The receipt preserves raw readings and normalized
nanosecond intervals `[floor(ticks*n/d), ceil(ticks*n/d)]`, computed with checked
integer arithmetic. Each domain's timebase must remain stable; game and runner
timebases need not equal. Missing units, overflow and drift reject admission.

Qualification requires matching ownership, successful capture/completion, and
this conservative ordering (upper start/finish bounds versus lower enclosing
bounds after normalization):

```
ceil(capture-start after) <= floor(spawn before)
ceil(completion-observation after) <= floor(capture-end before)
```

Raw runner readings must also preserve spawn-before/after and
completion-before/after order; game capture brackets must be ordered. Normalized
bounds are retained even when the enclosure inequality fails.

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

`CONTINUUM_PROCESS_CAPTURE=1` enables the boundary observer. It reads the owned
process's cumulative `TotalProcessorTime` and `WorkingSet64`, warms the accessors
outside capture, and retains preparation and snapshot read costs. The two CPU
read brackets define a slightly different interval from the flight boundaries;
the runner independently validates arithmetic and reports lower/upper wall and
average occupied-core estimates. These estimates include all process threads and
observers. Endpoint RSS is not peak memory, and occupied cores is not main-thread
utilization. Unsupported resource reads remain explicit. Only requested capture
observation performs native clock or resource reads.
