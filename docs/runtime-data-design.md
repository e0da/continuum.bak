# Replaceable runtime designed around data and required behavior

The outcome is an efficient playable and research runtime. KSP's object model, existing subsystem boundaries and a particular ECS or physics engine are candidates rather than requirements. Keep the selected gameplay contract stable while changing representation, ownership, algorithms and execution strategies. The user has selected rigid assembled craft as the primary model; elastic wobble is optional with secondary performance goals. Complete implementations accumulate even when their individual FPS effect is inconclusive.

## Decision and evidence

Start the next aero/shared-state implementation with compiled vessel plans, retained typed execution storage and ordered publication. Use existing generation, work-stamp and complete-cost observation contracts. Real archetype ECS and specialized Rust arrays now have an executable comparison on original captured work, described in [rigid craft and bulk systems](rigid-and-bulk-systems.md). Both complete the scoped 512-row pass in roughly 11–14 microseconds in two screening observations. These results motivate replacing callback/data-acquisition architecture; they do not rank ECS generally or qualify a full game replacement. Actual coupling, membership churn, query pattern and synchronization determine further choices.

Targeted local readings informed this decision: Richard Fabian's *Data-Oriented Design*, chapter 1, §4.4, §9.6–9.7 and §11.7; Jason Gregory's *Game Engine Architecture*, fourth edition Volume I, §6.1, §6.2.1 and §6.2.2.2; Roy Featherstone's *Rigid Body Dynamics Algorithms*, §5.1–5.2 and §8.1. These were selected passages, not complete-book readings. The local PDF hashes match the reference shelf manifest. Their application to this workload remains an engineering inference to validate.

Rust's iterators/generics can provide efficient static abstractions; dynamic trait dispatch has a different execution cost. Borrowing does not automatically remove allocations, reference counting, locking or copies from an architecture. Prefer a backend choice at the transaction boundary and specialized inner loops. Check generated code, instruction footprint and real workload costs rather than assuming a language or abstraction is free. [Rust iterator performance](https://doc.rust-lang.org/book/ch13-04-performance.html), [trait objects and dispatch](https://doc.rust-lang.org/book/ch18-02-trait-objects.html).

## Four data roles

| Role | Meaning and lifetime | Representation freedom |
|---|---|---|
| Definitions | Authored craft/assets, provider definitions, supported behavior | Versioned authoring/interchange schema; compile when definitions change |
| Authoritative state | Current physical/resource/control state and its writers | Domain owners, stable identities, explicit units/frames/time; generation-qualified mutations |
| Execution data | Topology plans, shared derivations, packed numeric views, scratch | Layout and residency specialized for each workload; retained storage and explicit leases |
| Observations and history | Telemetry, render views, predictions, checkpoint/measurement evidence | Requested projections, bounded samples or change records; materialize/export at consumer boundaries |

A save schema is not a memory layout. A diagnostic schema is not a per-part messaging requirement. Export and ABI layouts have explicit versions and meanings; internal arrays can change behind their adapters. Shared memory needs a lifetime/ownership protocol, not merely a pointer. Avoid pushing all consumers through one full-world snapshot or universal row format.

## Compile static work and route changes

Compile topology, recipient/body mapping, traversal/publication order, stable provider bindings, resource routes and dependency information when their owning definitions change. Read current numeric state at the appropriate execution points. Cache shared derivations under explicit validity, and update them at their actual writer boundaries.

Existing `EntityKey`, work stamps and stale-publication gates are donors. Existing immutable snapshots remain useful reference/interchange objects; their copying is not a required fast-path implementation. Rust's current experimental `CanonicalWorld::get_mut` and caller-supplied dirty list do not enforce data-revision tracking. A missed dirty key can leave stale inputs. New authoritative writers must produce revisions or mutation records automatically; unversioned external mod writers need conservative refresh or an admitted dependency lease.

Preserve provider side effects, conditional state writes, exact application points and required force order. Reading all inputs before earlier publications can change later provider observations. Batch only where the required behavior permits it. Backend output must be current for its invocation, topology, frame and provider dependencies before authoritative publication.

## Shared support with visible costs

Reuse narrow support for identity/generations, frames and units, ordered transaction ownership, bounded resources, backend admission/release, workload identity and independent comparison. Keep each subsystem's required numeric work concrete. A shared helper should remove actual duplication without forcing a general manager registry, per-row dynamic dispatch, automatic full copies or a task per part.

Each system exposes its required input/output roles, ownership, update triggers, latency/accuracy contract, supported backends and resolved configuration. Its measurements cover acquisition, preparation, compute, synchronization, publication and a directly timed total. Reuse `PerformanceObservation`; add counters or attributes only when a real consumer needs them.

Telemetry is designed in but its expensive collection is selectable. Default counters are bounded and aggregate at meaningful boundaries. Detailed capture uses preallocated storage where useful; export and formatting occur afterward. Observer-disabled code generation, counter/timer overhead and retention costs are measurable. Configuration changes bind a new revision; HMR admits a compatible implementation/state generation at a safe boundary and rejects old work. Runtime tuning and changing the physical model are separate operations.

Allocation follows lifetime: durable vessel state, execution scratch, asynchronous reader leases, render resources and cold history have different release conditions. Reuse owned arrays first. Introduce arenas, pools or multiple resource banks when their concrete lifetimes and measured taxes justify them. Never recycle a bank while CPU/GPU readers still use it. [Hardware execution choices](hardware-execution.md) own cache locality, SIMD, residency and prewarming guidance.

## Physics placement and vessel wobble

The latest intrusive 512-part atmospheric diagnostic at source `2392fd9` reports managed fixed-step 7.18 ms/step and native physics 2.23 ms/step. This identifies expensive regions, not a disjoint decomposition, a clean FPS result or the reason for wobble. Keep part/provider/control/resource/thermal work separate from joint/contact solving and coordinate/publication work when attributing costs.

GPU physics is a real candidate. Its complete cost includes acquisition/conversion, dispatch, dependency barriers, completion and any CPU publication. A connected craft creates coupled constraints; size supplies parallel work but also propagation dependencies. Multiple independent islands, large broad phases and persistent batches can be better GPU workloads than a small connected solve. These are hypotheses, not measured KSP routing thresholds. Rendering and physics also compete for GPU capacity.

The published PhysX GPU route targets CUDA GPUs on Linux/Windows, so it is not a switch that accelerates shipped Mac KSP. Metal-capable replacement physics or a different runtime needs implementation and qualification. The referenced PhysX version has a fully GPU-resident D6 joint pipeline while other joints involve CPU work, illustrating why actual ownership matters. [PhysX GPU simulation](https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/GPURigidBodies.html).

Wobble can involve intended compliance, numerical constraint error, timestep/iteration choices, inertia/mass ratios, loading/frame precision or controllers. None is established as this craft's dominant cause. The primary model now removes ordinary locked-structure constraints rather than requiring faithful bending. Preserve part identity, mechanisms, control effects, staging, docking and a declared failure model. A locked breakable connection can stay internal to a rigid region until an actual topology event. Optional compliant mode has separate accuracy and capacity goals. [PhysX joint behavior](https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/Joints.html).

Compare algorithms at equal declared behavior/accuracy as well as cost. Tree-aware articulated dynamics, sparse loop constraints, rigid clusters with retained flexible modes and XPBD are viable research directions for different supported models. The *Small Steps* paper supplies evidence that substeps and iterations are not interchangeable for its tested constraint systems; it does not establish a universal KSP setting or solver win. [Small Steps in Physics Simulation](https://matthias-research.github.io/pages/publications/smallsteps.pdf).

Time warp requires a regime choice: bounded coast propagation, internal resource/attitude dynamics, and coupled contacts/thrust may need different schedules. Batching many resident steps is valid only while inputs, dependencies and error limits permit it. Reuse [interaction regimes](interaction-regimes.md), rather than extrapolating a free-body GPU benchmark to an interacting ship.

## Next implementation and resolving questions

The current delivery is the [whole-runtime reconstruction and redesign](whole-runtime-redesign.md).
Compile the retained world/vessel state, topology, systems scheduling, physics,
presentation and compatibility boundaries as one coordinated implementation
package. The earlier aero-first sequence below records proven donors and
unfinished capabilities; it is superseded as an execution order.

Finish the retained ordered aero input/command transaction and its real publication owner. Build the compact bank around actual provider and publication dependencies, with enforced refresh/revision semantics. Use it as the first complete consumer of the shared support, then integrate further systems against the same ownership and measurement principles. Extract a library where repeated real use establishes the invariant.

Advance rigid aggregate motion and bulk part-system execution as connected fronts. The next rigid boundary is an admitted native compound body with real mass/force ownership and an actual separation transaction. The next systems boundary completes providers/acquisition/queued inputs and ordered publication beyond the now-executable dry ECS pass. Optional compliant solver comparisons and CPU-versus-GPU crossover remain research paths without gating the selected rigid experience. New fixtures complement real flights; they do not replace gameplay qualification. Do not rerun unchanged FPS arms to answer an algorithm question.

This is an authorized design and implementation direction. An offline retained ECS execution trial exists; no live ECS authority, new rigid physics backend, shared telemetry facility or full aero replacement is claimed by this document.
