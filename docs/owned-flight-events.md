# Source-bound physical events

`OwnedFlightEventQueue` retains a bounded queue and exact-once application history for the portable owned runtime. This is a physical-time contract, independent of renderer frames or callback cadence. Events contain data; checkpoints never serialize callbacks or executable delegates.

Supported event kinds are a world-space impulse on an admitted rigid region, a control-state change, actuator enabled state and resource-flow enabled state. Resource quantity overwrite, arbitrary scripts, topology edits and temperature overwrite are excluded. Those operations need their own atomic admission and state contracts.

## Preparing and applying events

Construct the queue with the actual host, a stable source ID and capacity from 1 through 4096. Each `OwnedFlightEvent` has a unique stable ID, monotonically issued sequence and runtime-clock time. Enqueue validates all inputs and targets before changing the queue. Events are ordered by physical time, then sequence. Applied history consumes the same capacity budget; a full history requires a new explicitly admitted queue rather than silently forgetting prior events.

The source contract binds the actual host, schedule SHA/code generation, systems configuration, world epoch, topology, definition and coordinate frame. Ordinary committed mechanical steps may change time/state revision. An external epoch, frame, topology or configuration change rejects application without consuming the event. Each successful event rebinds only its own epoch/configuration changes, preserving future events.

After the timeline validates an explicit cold or same-time boundary, it may call `AcknowledgeBoundary(expected)` to refresh the queue's source/code/configuration/frame binding. This requires an empty pending queue and the exact current host stamp. Applied history remains retained. It cannot rebind future events across HMR, placement or a configuration change, and it does not claim to validate the timeline's boundary operation itself.

For a guarded analytic coast already verified by the timeline, `AcknowledgeCoastAdvance(before, after)` admits exactly one same-structure, same-frame world publication with forward physical time and unchanged code/configuration. It requires the current world to equal `after` and rejects passage beyond the next queued deadline. It may retain future events. This acknowledgment verifies publication shape and ownership; the timeline remains responsible for trajectory/event guard admission before committing the coast.

The timeline must cap advancement at `NextTime`. `ApplyNext(expected)` requires an exact current stamp and exact event time; early, late and repeated application reject. It applies one event, records its before/after epochs and revisions, then removes it from pending state. Failure before publication leaves the event pending. An admitted impulse publishes all region motions atomically through the existing same-time world operation. Configuration events use the existing single cold mutation. These are ordered individual transactions; a group of same-time events is not advertised as one all-or-nothing transaction.

The queue owns its last scheduled control-state value. A successful Controls event returns that value in `OwnedFlightEventApplication`; the timeline installs it into the current input segment before further simulation. Later explicitly authored input segments remain intact. If the current segment is open, the held policy also changes; live operator input retains its explicit supersession behavior. The timeline's actuator deadline prediction uses its actual current segment controls rather than the queue's historical helper value.

Impulse components are Newton-seconds. The optional application point is in retained runtime-local world axes, the same coordinates as `Motion.RootPosition`; it is not silently converted from body coordinates or another origin. The linear update is `deltaV = J / M`; an off-center impulse adds `cross(point - worldCOM, J)` to world angular momentum. Poses, physical time, resources and wheel internal momentum remain unchanged. Existing dynamics resolves angular velocity from the aggregate tensor. No second native force is applied.

## Predictable actuator boundaries

`FindActuatorBoundary(expected, maximumHorizonSeconds)` delegates to the systems owner's exact actuator boundary helper and binds its returned stamp/configuration. That helper uses the original owned actuator fold on noncommitting scratch state, including sequential resource-pool priority, fuel mixtures, enabled/flow state and wheel saturation. It is a bounded cold preparation operation, not a per-part callback or a guarantee of stock KSP resource semantics. Unsupported or unreached results stay explicit. Existing empty tanks or saturated wheels are not invented future transitions.

A deadline is conditional on the admitted control state, configuration and source state. Changing those inputs requires recomputation. Timeline integration must charge preparation cost and cap the next mechanical interval at an admitted boundary; scheduling an event cannot make an approximation to resource demand exact.

## Persistence and failure ownership

`Capture()` produces `continuum-owned-flight-events/v1` data with complete pending rows, control state, source binding and bounded application history. `Restore(host, data, expectedSourceId)` validates the source, typed rows, finite shapes, target availability, ordering, duplicate IDs/sequences and history publication shapes before returning a new queue. It does not change the world. Mutable exported arrays are cold copies and cannot edit the live queue.

Writer-thread ownership is enforced for enqueue/application. Checkpoint restoration and source admission are cold operations. Queue application itself does not alter pause or simulation debt. A timeline that encounters a stale or missed event must report the error and retain unmet demand; it must not catch it and skip the event. Renderer presentation can continue from the last published frame.

The dedicated portable tests independently derive impulse momentum and kinetic energy, verify exact frontier/same-time ordering, checkpoint continuation, capacity transactions, own configuration rebind and foreign epoch/configuration rejection. Their source is frozen before execution; root integration owns execution and actual acquired-craft qualification. This contract does not claim contact, stock module or whole-game equivalence.
