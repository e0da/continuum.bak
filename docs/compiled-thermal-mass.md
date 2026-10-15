# Compiled thermal, sparse actuation and retained mass validity

This integrated replacement removes repeated thermal graph discovery, visits
only enabled actuators and actual wheels, and retains mass properties until a
mass-bearing resource change makes them stale. It runs inside the existing owned
runtime, standalone and in the disposable KSP consumer. It does not change the
physical equations or the reference thermal fold order.

## Ownership and execution

Compiled thermal plans contain heat capacities, ordered surface rows grouped by
part, and eligible ordered link rows grouped by independent rigid region. All
surfaces of a part retain their original order; all links of a region retain
their original order. Cross-region links remain skipped and counted. Definition,
topology and configuration changes invalidate plans. Independent parts and
regions may run concurrently, with each phase joined before the next and before
the original serial positive-temperature validation. Large workloads use bounded
parallel ranges; small workloads stay sequential. The deferred thermal solver
and Reference thermal body remain unchanged.

Sparse actuation preserves original visit order. Disabled wheels remain present:
their retained momentum still contributes to mechanical evolution. Logical
skipped-row counts and actual visited-row counts are separate.

Mass validity belongs to the retained bank as derived state. Resource setters
always preserve the original domain checks and exact writes; changed nonzero
density quantities invalidate mass properties. Transaction copies carry validity
with their data. Cold construction, definition replacement, bulk boundary reset
and topology/restore paths either invalidate or independently certify it. The
full ordered mass, COM and inertia arithmetic remains authoritative. The validity
bit is not serialized as a physical fact.

## Matched scale result

The unchanged generated recipe has 1,048,576 authored parts, 65,536 physical
bodies, 128 rigid regions, 131,072 enabled engines and 2,097,152 resources. It uses
16 steps at 0.015625 seconds, compiled execution, retained deadline probes and
three continuation steps. Costs are isolated fresh-process observations. One
retained baseline and two candidate observations resolve this change; no
confidence interval or general hardware capacity claim is inferred.

| Measurement | Retained baseline | Candidate A | Candidate B |
| --- | ---: | ---: | ---: |
| Complete lifecycle | 27.671 s | 19.962 s | 20.807 s |
| Hot 15-step interval | 3.237 s | 1.041 s | 1.025 s |
| Average hot step | 215.805 ms | 69.409 ms | 68.353 ms |
| Thermal counter, 16 steps | 1.209 s | 0.142 s | 0.142 s |
| Scalar counter, 16 steps | 0.915 s | 0.671 s | 0.664 s |
| Mass counter, mixed cold/hot | 1.304 s | 0.463 s | 0.458 s |
| Mass refreshes | 49 | 17 | 17 |
| Main actuator visits | 16,777,216 | 2,097,152 | 2,097,152 |
| Skipped actuator rows | 0 | 14,680,064 | 14,680,064 |
| Cumulative measured allocation | 7.641 GB | 7.750 GB | 7.750 GB |

Hot stepping is about 3.1 times faster, thermal work about 8.5 times cheaper and
complete lifecycle cost 25–28% lower for this fixture. Nested counters are not
additive. Retained plan storage increases measured allocation slightly. Neither
the allocation nor lifecycle measurement is a renderer FPS claim. At 68–69 ms
per step, this fixture still cannot keep up with its requested 64 Hz interval.
Grouped rigid regions are not a million independent-body collision solve.

Each run passed exact rewrite, rollback and restored future continuation.
Cross-producer streaming comparison checked the 505,433,418-byte checkpoint and
both 505,433,658-byte final continuations. Only the admitted executable image hash and
its independently verified derived envelope digest may differ. Every physical
byte matches the retained baseline. A tiny executable falsifier changes a
massless EC quantity, reseals the actual file and receipt hashes, and proves that
the production comparator rejects the physical difference.

## Real consumers and remaining gates

The acquired 512-part, 426-body craft passed Reference/Compiled normalized full
continuation in quiet and powered vacuum and atmosphere, with two observations
per case. Current Reference and Compiled both contain mass validity, so these
cases qualify behavior and strategy cost rather than isolating the mass change.
All direct net8 timings remain distinct from Unity frame performance.

The private net472/C# 7.3 helper was installed only in the owned disposable clone.
The visible nine-part ComSat acquired through the actual keypad control,
saved/restored all 25 module records, preserved its numerical frontier and
outstanding debt, then resumed. A stock autosave triggered safe handback: the
worker joined, original native/fixed-update ownership returned, and no worker
fault or partial publication was reported. The immutable source save hash was
unchanged, and the owned player exited successfully through its idle quit path.

The rendered flight was inspected. Earlier frames showed craft, planet and UI;
late frames showed a very dark craft. A fresh sunward reframe rendered starfield
and the craft silhouette but did not establish satisfactory lighting. Lighting
facts first reported no eclipse and later a spherical main-body eclipse. The
dark craft is an unresolved camera/light/scene defect; UI or starfield pixels do
not qualify craft rendering. The next native visual investigation needs a frozen
sunlit geometry and frame-bound lighting facts before attributing the cause.

Focused independent proofs cover ordered thermal folds, energy/conduction,
parallel-phase rejection/rollback, disabled wheel momentum, point-resource mass
and inertia, supply withdrawal, configuration/topology changes and restored
continuation. Portable checks include these and the actual comparator falsifier.
Existing execution, mechanics and supply checks passed. The Rust clock client
passed its ten protocol/action checks.

Two interrupted comparator prototypes are preserved: one allocated a diagnostic
string per matching byte; the next removed allocation but retained a scalar
byte loop. The qualified comparator uses reusable blocks and vectorized span
comparison. Neither interrupted prototype is physical-simulation failure or
accepted timing evidence. The falsifier's initial test-only string-chunk walker
repair is also preserved.

The next compute redesign should retain typed dirty resource/region sets and
resident reduction state, so mass and scalar work track actual change rather
than repeatedly scanning all storage. Complete checkpoint/persistence cost and
the native lighting defect are separate active costs/gates. Keep the exact
state and future-continuation proof while changing those ownership boundaries.
Raw game inputs, binaries, logs and receipts remain in private laboratory
storage; the canonical research packet binds their exact identities.
