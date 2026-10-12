# Hardware-conscious execution

The research target is the least costly execution of the required behavior. Hardware strategy is chosen for a complete workload, its data residency and its latency/accuracy requirements. Functional replacement coverage and performance claims remain separate.

## Existing evidence and starting choices

The qualification host was freshly identified on 2026-10-12 as Forge, Apple M4 Max, ARM64, with 128 GiB RAM. Xcode's trace tool and Metal compiler are available; this does not establish that every profiler counter is accessible in every runtime.

- [Persistent execution views](persistent-execution-views.md) already implement generational identity, deterministic topology rebuilds and writer-declared dirty refresh. Their recorded synthetic M4 Max observations favor sparse refresh at low dirty fractions, while a large fully dirty view favors repacking. Discovering changes and publishing results were excluded. Use writer-owned dirty tracking; do not infer a universal threshold or assume moving aero rows are sparse.
- [Layout comparisons](layout-benchmark.md) support testing compact columns before adding blocked layouts. The experiment allocates immutable captures and includes validation/hashing; it does not prove a live aero layout or automatic SIMD.
- [Execution backend comparisons](execution-view-benchmark.md) distinguish cold GPU transactions from resident multi-step work. In that recorded synthetic run, cold GPU never beat scalar CPU; resident GPU won at larger sizes. Readback cadence, precision, renderer contention and real dependencies must enter a new routing decision.
- [Native-boundary comparisons](native-boundary-benchmark.md) show that a cheaper Rust kernel can lose after host refresh/publication. Keep an entire useful transaction resident and batch crossings. The shipped x86_64 KSP process requires x86_64 in-process native code; native ARM64 execution requires a separate process or a migrated host.

These are existing recorded experiments, not newly rerun benchmarks or live-game speedup claims. Reuse their evidence until a changed implementation or unresolved question warrants another measurement.

## State, layout and reuse

Retain vessel topology, recipient mapping, execution storage and shared derived state under explicit generations. Separate frequently read numeric fields from cold identity, names and diagnostics. Compare compact AoS, SoA or small blocks according to the actual access pattern; avoid repeatedly repacking solely to change layouts between consumers.

Reuse storage owned by the vessel or execution transaction before introducing a general pool. Use bounded pools for transient scratch or size-classed resources when lifetimes warrant them. Return a resource only after its CPU readers/workers and any GPU completion have released it. Track retained capacity, growth and misses outside the normal fast path; reduce unused capacity at lifecycle boundaries rather than on every tick.

Data caches need writer-owned revisions and precise invalidation. Record useful hits, recomputation and invalidation work when diagnosing them. Cached data that requires a full rescan to prove validity may cost more than recomputation. Dirty refresh, immutable snapshots and resource reuse are distinct mechanisms.

Keep checkpoint freezing, JSON evidence export and diagnostic hashing outside timed execution except when explicitly measuring those operations. Current retained aero freezing allocates by design; its reference execution reuses output/command arrays but still follows individual mutable row objects. Zero allocation is useful evidence, not proof of good cache locality.

## SIMD and CPU scheduling

Expose long contiguous independent numeric loops to the compiler, inspect generated instructions, then measure the representative workload. Introduce explicit SIMD where it resolves an observed code-generation limitation. Preserve mixed float/double operations and the declared numerical contract; reassociation and fused operations need their own behavior evidence.

Numeric batches and publication order can have separate representations, but side-effecting provider reads or earlier stock publications can make later inputs dependent. Prove independence before moving acquisition or computation across those points. Preserve masks, recipients, application points and command order wherever the behavior contract requires them.

Retain workers and reusable job storage. Partition by meaningful independent work, such as admitted islands or numeric batches, and calibrate task granularity. Measure queue delay, scheduling and synchronization with compute. A tiny kernel does not become a good parallel workload merely because many cores are available.

[Unity's vectorization guidance](https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/optimization-loop-vectorization.html) demonstrates how loop structure and alias information affect SIMD generation. Burst is an option in a compatible host, not a capability assumed in shipped KSP.

## GPU residency and prewarming

Keep suitable rendering/compute resources resident, reuse pipeline states and submit enough useful work per batch. Shared physical memory permits shared Metal resources but still requires safe ownership and synchronization. Use multiple resource instances when CPU preparation overlaps GPU consumption; read back only when a CPU consumer needs the result. Choose storage and precision according to actual access. [Apple's Metal compute guidance](https://developer.apple.com/videos/play/tech-talks/10580/).

Resource prewarming prepares likely near-term needs: execution storage, workers, required methods/native entrypoints, pipelines and scene assets. Perform it at an explicit preparation boundary where feasible, bound memory/time, and cancel obsolete work. Record preparation cost and first-use latency separately from steady execution. Prewarming relocates work; charge it to startup/session cost and avoid doing the same preparation repeatedly.

## Profiling and deciding

Start with a CPU/GPU timeline and the largest expensive interval. Distinguish computation, memory traffic, allocation/GC, scheduling, waits, graphics submission and actual GPU execution. A wait label is not enough to name the upstream cause. Inspect cache/bandwidth or shader occupancy evidence when available; record unavailable counters as unavailable.

Measure acquisition, preparation/packing, compute, synchronization and publication alongside a directly timed complete transaction. Record bytes touched/copied, allocations, retained memory and resource creation when they can answer the current question without excessive instrumentation. Preserve cold/first-use and warmed observations, build/runtime/architecture, input identity and any cap or scheduling regime. Intrusive profiling and clean throughput have separate claims.

Use the actual graphics backend's profiling path. A Metal workbench capture does not characterize the shipped OpenGL KSP renderer. Sample/probe overhead and GPU instrumentation support must be qualified at the consumer being measured.

Two useful observations are the default stopping point under Justin's instruction. Repeat only to resolve a named ambiguity or verify a changed implementation. Keep complete working backends selectable even when performance evidence is inconclusive; use evidence to improve their execution strategy and identify the next limiting boundary.

## Next complete aero implementation

1. Retain topology/recipient plans and compact transaction storage. Refresh authoritative inputs at behavior-compatible points; retain shared derivations only within their valid epoch.
2. Complete queued-force handling, conditional state writes and ordered publication. Acquire provider inputs with their actual ordering and mutation semantics.
3. Use the real recorded aero rows to compare retained managed numeric execution with a bulk native alternative. Charge acquisition, any layout conversion, synchronization and publication; inspect code generation before claiming SIMD.
4. Evaluate GPU or ARM64 sidecar execution when a coherent resident workload can repay its dependency/completion costs. Keep broader native-runtime and renderer work available rather than imposing a GPU round-trip on every part.

This document defines the execution strategy. It does not add a pool, SIMD kernel, GPU path or new performance result.
