# Scale frontier and ambitious builds

The [capacity runtime](capacity-runtime.md) now provides generated consumers,
measured replacements, larger lifecycle proofs and preserved frontier failures.

The program discovers practical capacity by pushing representative builds and
adversarial workloads beyond their current limits. 256 parts is a comparison
checkpoint, not a ceiling. 4,096 authored parts is the first large stress target,
not a promised supported capacity or the end of the program. Million-part craft
and collisions between them remain explicit research workloads.

## Builds worth enabling

Use mission families with useful activities, rather than duplicating inert parts
and calling the result playable:

| Family | Activity and failure modes to expose |
| --- | --- |
| Modular station and shipyard | Approach with a tug, dock another large section, transfer fuel, rotate the assembled structure, build and service vehicles. Exercise proximity queries, topology changes, control authority and detailed rendering. |
| Interplanetary mothership | Carry many attached landers and vehicles, burn, coast, warp, release payloads and rendezvous again. Exercise providers, fuel routes, changing mass and multiple independent owners. |
| Large launcher and atmospheric vehicle | Many engines, staging, aerodynamic surfaces, reentry heating and persistent plumes. Exercise dense active updates and transient effects. |
| Surface base and nearby fleet | Construction, parked vehicles, production, background activity and nearby landings. Exercise terrain/contact, sleep/wake and active/background transitions. |
| Detailed replica or megastructure | Preserve decorative complexity and fine geometry while moving the craft and camera. Distinguish authored detail from required mechanical state. |
| Destruction experiment | Collide two vessels, break connections and follow debris. Exercise contact bursts, fragmentation, allocation, publication latency and recovery. |

These are proposed families. Current admission does not support docking, contact,
crew, arbitrary modules or powered effects; unsupported cases must remain
explicit until their owners are implemented.

Player reports motivate the families, without serving as profiler evidence. One
builder reports roughly 300 station parts and 10 FPS when controlling or
approaching, with planned sections still unbuilt. Another describes splitting a
15-vehicle Eve mothership because of lag. A published station already has 1,555
parts and multiple docked ships/tugs. These observations support researching
ambitions beyond the initial matrix, not attributing every problem to physics.

- [Station expansion and approach](https://www.reddit.com/r/KerbalSpaceProgram/comments/1sqr49t/large_space_station_optimization/)
- [Base/station limits and split mothership](https://www.reddit.com/r/KerbalSpaceProgram/comments/1u691pn/anyone_got_a_solution_for_minimizing_lag_when/)
- [A 1,555-part station](https://www.reddit.com/r/KerbalSpaceProgram/comments/ii0zyx/)

## Grow until the failure mechanism changes

Retain small comparison points, then grow through 1,024, 2,048,
4,096, 8,192 and 16,384 parts. Jump farther when measured cost and memory curves
leave room. Probe 65,536, 262,144 and 1,048,576 parts in the standalone runtime
where suitable; these are sampling points, not mandatory matrix cells or support
tiers. Once a sharp bend appears, bracket that transition instead of running
every count. Revisit it after replacing the responsible system.

Keep three explicit consumer identities: original KSP, Continuum hosted by KSP,
and the standalone owned runtime. If KSP cannot construct the next case, record
that construction failure and continue numeric or rendering investigation in
the standalone consumer. A standalone result does not prove KSP can load or play
the same craft. Avoid requiring a full game boot for every scaling probe; bring
decisive changes back into the integrated consumer.

For every generated case, record authored parts, unique/shared assets, rigid
regions, flexible joints, colliders, provider/module rows, resource edges,
visible instances, vessels, active contacts and resulting fragments. A million
authored parts aggregated into one rigid region is not a million-body solve.

Pair useful builds with controlled contrasts:

- Same authored detail, rigid versus selectively flexible mechanics; preserve
  the rigid model as the primary intended experience and optional wobble as its
  own cost/fidelity treatment.
- Same geometry, passive versus provider-heavy parts; independently vary active
  fractions and resource connectivity.
- One vessel versus several, sparse versus dense proximity, separated versus
  contacting objects, and bounded articulation versus fragmentation.
- Repeated assets versus unique assets, close detail versus distant views, and
  full rendering versus a declared diagnostic no-render treatment.

For two 1,048,576-part collision craft, count both inputs and the debris frontier.
Begin with separated objects and controlled sparse contact, then increase the
contact area, impact severity and number of independent fragments. Retain dense
overlap as an adversarial case. Spatial partitioning is a candidate mechanism,
not a guarantee against dense contact cost.

## Questions that select architectural changes

Measure construction/indexing, topology compilation, cold/warm memory,
mechanical work, providers/resources/thermal, contacts, checkpoint/restore,
bridge/publication, rendering and waits separately, plus complete consumer cost.
Track scaling slopes, throughput, control-to-committed-state latency and tail
publication/frame latency. Uncapped presentation and simulated-time throughput
must remain distinct; fixed/render caps cannot hide unused capacity.

Five resolving questions guide the first large package:

1. Does cost follow authored detail or independent mechanical state? Hold one
   fixed while changing the other; this distinguishes representation taxes from
   solver work.
2. Do provider and resource costs follow all rows, active rows or graph edges?
   Compare identical geometry with declared provider/activity/connectivity
   treatments; profile the complete transaction.
3. Does visible detail dominate after simulation becomes cheap? Compare the same
   committed state across view distance and rendering treatments, including
   publication and GPU evidence where available.
4. Do docking, contact and fragmentation cause unacceptable bursts? Inspect
   before/during/after latency, allocations and retained memory rather than only
   steady-state means.
5. Does the host or data contract fail before computation saturates? Preserve
   exact admission/codec/construction failures and reproduce the supported model
   in another consumer without dropping state.

Two concordant resolving observations normally suffice. Additional runs must
answer a changed mechanism, ambiguity or behavior gate. Preserve failed,
interrupted and incomplete results, including the stage and last coherent
checkpoint; do not substitute a smaller successful craft for a failed large one.

## Existing bounds are architectural findings

At source checkpoint `5fd1aff`, `OwnedRuntimeDefinition` admits at most 65,536
parts; `SimulationBatch` and the legacy coast path admit 4,096 bodies. Supply
capture bounds each solar/command/engine table at 4,096 rows. Runtime recording
admits 4,096 parts and 8,192 draws. Numeric checkpoint payloads and module XML are
bounded at 64 MiB. These are current admission limits, not proven capacity or
design goals. They can block different families at different sizes.

Include segmented storage, streamed/sectioned persistence, stable identities,
compiled sparse connectivity, shared immutable assets and resident instance
publication among the alternatives. Replace an insufficient contract together
with its readers/writers, overflow checks and restoration obligations. Do not
silently truncate rows or merely increase every constant and claim support.

Large-scale discovery belongs inside the next sustained-flight implementation.
Keep one integrated useful mission and one growing adversarial capacity family
advancing together. The outcome is a measured capability envelope by workload
and fidelity, with concrete bottlenecks removed and the next frontier exposed.
