# Generated capacity runtime and measured replacements

`KspContinuum.CapacityLab` executes the same Core definitions, topology, banks,
systems, host and checkpoint paths as the owned flight implementation. Authored
part, physical contributor and rigid-region counts vary independently. Stage
costs cover construction, stepping, deadline queries, persistence, rollback and
exact continuation. This is a standalone numeric consumer: stock KSP hosting,
imported assets, rendering, contacts and flexible joints remain separate gates.

## Observed frontier

| Workload | Result |
| --- | --- |
| 4,096 parts, 256 physical contributors, one rigid region, 512 enabled engines | Two matched baseline/candidate observations; independent mass/center/tensor/resource/displacement checks; complete normalized checkpoint and continuation equality. |
| 65,536 parts, 4,096 contributors, 16 regions, 8,192 enabled engines | Stepping, retained deadline queries, rollback, persistence and exact continuation passed. Payload: 60,638,692 bytes. |
| 32,768 parts, bodies and independent regions | Full lifecycle passed without actuators or contacts. |
| 65,536 independent bodies/regions | Sixteen steps reached 0.25 simulated seconds; checkpoint capture rejected the 50,000-row source ledger bound. Lifecycle remains failed. |
| 131,072 requested parts | The 65,536-part definition bound refused construction before fixture allocation. |
| Two sparse-overlap grids, each with 1,048,576 AABBs | Two observations produced identical 10,280 candidates/digest, at about 0.56 seconds for generation/index/query/hash work. |
| Two collapsed dense grids, each with 1,048,576 AABBs | Explicit candidate-budget exhaustion at 2,000,000 pairs; partial work and reason preserved. |

The runtime interval is 0.015625 seconds: 16 main steps plus three continuation
steps. The hot interval contains the last 15 main steps. Thermal exchange,
selected loads and provider history remain active in the engine family.
Disconnected regions are numeric owners, not KSP vessels. A 65,536-part model
aggregated into 16 regions is not a 65,536-body coupled solve. Short probes do
not qualify endurance, global accuracy or control feel.

## Structural replacements

Fuel-kind pools and single-resource routes are indexed once and shared
immutably in canonical order. Wheel reservoir uniqueness uses an identity set.
Execution and deadline plans share a per-pool/per-region builder with current
flow/topology revisions. Deadline queries read each immutable route's ordered
mass and first nonempty resource once, rather than rediscovering them per row.

Within an actuation fold, the first availability read performs the original
complete ordered sum and finite check. Later reads can stop once a nonnegative
prefix satisfies current demand: that only replaces a ratio already at least
one. Starved routes still sum their whole remainder; withdrawal order is
unchanged. Quantities only decrease inside the fold. Production or callbacks
inserted there would require invalidating this validation latch, which clears
on every fold/probe/retry.

The streaming codec preserves exact v6 wire bytes and v1–v6 readers. Bounded
primitive buffers remove boxing and redundant full-payload staging; .NET 8
span overrides remove per-primitive adapter overhead. Unity/net472 retains
byte-array IO without a new Span/System.Memory ABI. Integrity/schema/depth/
element/byte checks and caller-owned streams remain explicit. The first
streaming version reduced allocation but regressed throughput; its raw failure
was retained before the adapter repair. Final small-case encode/decode costs
were approximately baseline parity, with lower allocation. No universal codec
throughput gain is claimed.

Two final matched 4,096-part engine lifecycle observations:

| Complete work | Baseline | Replacement |
| --- | --- | --- |
| Wall cost | 952 / 939 ms | 386 / 396 ms |
| Cumulative allocation | 723 / 723 MB | 173 / 173 MB |
| Hot 15-step interval | 60.95 / 60.36 ms | 14.97 / 14.95 ms |

About 58–59% lower complete cost, 76% lower cumulative allocation and 75% lower
hot stepping cost apply to this fixture. Allocation is not live/peak memory;
these costs are not stock FPS. The complete comparator retains all physical,
control, history and configuration fields, excluding only executable-image
hash and process identity. It requires passed receipts, matched actual recipes
and controls, and matching packet/code/process/source provenance. A valid
temperature mutation must fail comparison.

## Spatial, memory and remaining boundaries

Sparse-cell indexing, shared-cell deduplication and inclusive AABB tests use
immutable numeric inputs. Exhaustive small oracles cover signed coordinates,
boundary/zero extents, multicell overlap and deduplication. Work, entries,
queried cells, output and estimated memory have separate budgets. Candidates
are not narrowphase, impulse/contact response or fragmentation.

Working-set samples are recorded. macOS returned zero for
`Process.PeakWorkingSet64`; final receipts use unavailable/null. Earlier raw
zeros are preserved and mean unavailable. The 65,536-independent-body refusal
had a concurrent test build: its timing is not accepted as isolated cost
evidence, although the committed state and source-contract failure hold.

The next architecture is versioned columnar/segmented state interchange with
generated/numeric provenance separate from native KSP bindings. Preserve
stable identities, bounded admission and exact future-relevant continuation.
The present 64 MiB payload/element bounds remain unchanged. Thermal forcing,
snapshot copies and DTO/vector construction still amplify memory; raising
constants alone would not qualify a larger runtime. Keep useful mission growth
and adversarial contact queries together, then qualify decisive changes in the
installed flight consumer. See [scale frontier](scale-frontier.md) and
[laboratory CLI](../tools/KspContinuum.CapacityLab/README.md).
