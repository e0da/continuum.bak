# Aligned flight process diagnostics

`--flight-native-sample true` is an intrusive diagnostic of the owned game PID,
not an unprofiled performance arm. Native sample receipt v3 requests two seconds
at a one-millisecond interval. Historical five-second receipts are unchanged.

Selecting native sampling now automatically enables the existing process capture diagnostics in the owned player, supplying its Mach clock/timebase boundaries. Unrequested runs retain their ambient process-capture behavior. A failed controlled sample exposed the missing request wiring; its original failed receipt remains preserved. The repaired run passes conservative enclosure without changing the validator or assuming Stopwatch/UTC equivalence.

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

## Selected native witness

The 512-part frontlit atmospheric comparison `1791763411-48980` completed
10.02 simulated seconds with 501 physics steps and 362 rendered frames. The
owned Rosetta player reported a `1/1` Mach timebase; the native runner reported
`125/3`. All eight normalized boundary intervals passed independent integer
recomputation. The complete sampler process envelope was 3.423362 seconds,
enclosed with 6.377 ms start slack and 7.228836 seconds end slack. This includes
attachment/report processing; requested sample collection was two seconds.

Boundary reads captured 19.685853 CPU seconds across a 10.659143-second
bracket-midpoint interval: 1.846851 average occupied cores, with validated bounds
1.846752–1.846951. Snapshot read tax totaled 1.1843 ms. Endpoint working set was
5,532,688,384 -> 5,541,998,592 bytes. These are all-thread values including
observers; endpoint growth cannot characterize peak memory or a leak.

The overlapping phase brackets were 11.4786 ms managed fixed work per rendered
frame (8.2939 ms per physics step), 3.2568 ms native physics per frame (2.3532 ms
per step), 5.7749 ms script Update per frame and 4.5422 ms FinishFrameRendering
per frame. FixedUpdate's parent is 14.8033 ms per frame; do not add it again.
Render-end remains a wall bracket, not GPU execution time.

The statistical main-thread context had 37 recognized wait occurrences among
1,346; remaining categories were 467 Mono, 545 unsymbolicated Unity and 297
unresolved. The graphics-worker context had 839 recognized waits among 1,304,
306 driver and 159 unsymbolicated Unity occurrences. The same physical graphics
thread also appears in other queue contexts; those are retained separately.
Inherited classification is incomplete. Waits cannot distinguish producer
starvation from a GPU fence, and unrecognized spin waits may appear elsewhere.

The evidence favors investigating main-thread production next, especially the
remaining managed fixed owners and script Update/Unity preparation subtree. It
does not prove an exclusively CPU-bound frame or absent GPU cost. Resolve a
coherent owner from existing stack/callback evidence, then replay its immutable
inputs and charge capture, preparation, computation and publication. No new
identical renderer or rejected timestamp run is warranted.

The mixed, sampled flight's 33.99 FPS and 0.94036x simulation rate are retained
diagnostics, not clean candidate performance or a regression verdict. Its local
drag transaction comparison remains a bounded positive under the separate
[aero contract](aero-step-context.md). Both original-resolution images were
inspected: real craft, atmospheric background, native UI and portraits appear;
booms extend outside the frame. Original helper/settings and all 21 pinned
ordinary inputs were restored; owned player/sampler processes exited.
