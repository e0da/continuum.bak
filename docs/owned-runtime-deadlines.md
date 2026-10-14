# Retained actuator deadlines

The retained deadline strategy forecasts the next event in the existing owned scalar actuator model. It does not replace resource allocation, wheel dynamics or stock KSP resource semantics. `OwnedRuntimeDeadlineStrategy.ExactOracle` is the default and retains the bounded reference search; `Retained` is an explicit cold configuration selection through `SetDeadlineStrategy(expected, strategy)`. `FindActuatorBoundaryExactOracle` remains directly callable independently of that selection.

## Rate plan and live inventory

A retained plan binds definition/topology identity, system configuration and every control component/enable bit exactly. It preserves authored actuator order and admitted resource routes. Requests use the existing region-local actuator axes and control orientation. Changing world pose alone does not change these local requests; topology/geometry re-expression, definition, configuration or control changes rebuild the plan. A restored or trial systems instance owns independent plan scratch.

Before the first scalar boundary, fully supplied unsaturated consumers deliver their full requested rate. The seed calculation assigns each mixture demand to the first currently positive eligible resource in its ordered route, adds shared electrical demand cumulatively, and includes each active wheel's directional momentum headroom. A positive row becoming empty is an event even when later tanks keep the pool supplied. Initially unavailable mixtures, zero electrical supply or directionally saturated wheels are inactive; they do not create recurring zero-time events.

Actual quantities and wheel momentum are refreshed from the pinned current bank for every search. The cache retains dependency/rate terms, never an absolute future deadline across committed intervals. Repeated subtraction and partitioned intervals can change the exact floating-point crossing. State revision/time progression therefore need not rebuild structural rates, but every returned observation binds its complete current source stamp and configuration. The host also admits schedule/code ownership before using it.

## Certificate and fallback

The analytic prediction is a search seed, not a qualified frontier. Wheel seeds use the inward rounding cell at the target capacity, including the compensated headroom sum and half of the inward capacity gap. This matters when a large stored momentum leaves only a few capacity ULPs of headroom: the rounded target can be reached substantially before the continuous headroom ratio. Resource seeds retain ordered cumulative row rates. Operation-count roundoff windows and outward rounding select a bounded local interval; these windows are not independently accepted physical-error certificates. Both interval endpoints are checked by the original scalar predicate. A positive-double-bit bisection then narrows a nonreached/reached pair to adjacent representable times, with a final reached replay. A reached upper and nonreached immediately preceding time produce `full-rate-adjacent-original-predicate`. An unreached requested horizon produces `full-rate-horizon-unreached`. No approximate-zero threshold replaces the original exact-zero depletion test.

Nonrepresentable rates, subnormal inventory/root cases, invalid provider capacity, overflowing horizon demands or failed local endpoint/replay verification fall back visibly to the original bounded search. Certification is then `fallback-bounded-64-bisection-upper`, with its reason and all work charged. The direct reference search reports `bounded-64-bisection-upper`.

The bounded 64-iteration reference search is not universally adjacent or mathematically earliest. An extremely small root relative to its horizon may yield a later positive upper plateau. A normal tiny inventory can give the retained local certificate a genuinely earlier crossing; that difference stays observable. Subnormal fallback can retain the reference limitation. Neither label promises a global trajectory-error bound or silently enlarges a state tolerance.

## Ownership, observation and cost

Adaptive hosts using retained deadlines require an explicit
`IOwnedRuntimeScalarActuatorSchedule` contract: the schedule performs the original
ordered scalar resource/wheel fold once for the supplied interval and controls.
Trial safety alone does not establish that model. Active HMR and checkpoint
restoration enforce the same admission before work or publication. This is a
trusted authored contract, not automatic verification of arbitrary marked code;
replacement allocators/providers need their own forecasting model and proof.

Searches hold cold admission and a source read lease. Compact probes copy resource quantities into retained scratch and borrow immutable source geometry/providers only for that lease. They do not publish resources, providers, pose, thermal intervals or histories. Borrowed references are removed before lease release. Original scalar arithmetic, mixtures, priority and predicate remain the verification oracle.

`HasActuatorBoundary` and `LastActuatorBoundary` expose the last completed source-checked observation. The value includes requested controls/horizon, strategy, certification, fallback reason, probe count and source/configuration. Optional predicted delta/window and event identity describe the continuous candidate separately from the certified actual result; they survive a fallback and never substitute for its predicate gate. It is a past observation, not proof that a later bank still satisfies that certificate. Native consumers must copy/flatten it on the writer; no extra per-frame search is needed to display status.

Counters distinguish rate-plan builds/hits, local probes, fallback searches grouped by admission/lower-endpoint/upper-endpoint/replay failure, compact resource copies and optional whole boundary time. Plan construction and inventory scanning remain real costs; source-bound cheap refresh is not a zero-cost cache hit. The independent deadline suite covers first-tank events, cumulative shared EC, missing mixture species, saturation/unwind, a large-base near-capacity rounding-cell case, exact ordinary frontiers, tiny reference-upper differences, explicit subnormal fallback, live partitioned inventory, flow/topology invalidation, source immutability and canceled-transaction recovery. Build/test/native qualification is owned by root; no speed or complete-game claim follows from source acceptance.
