# Independent flight worker and committed presentation

The acquired world-flight workbench creates its import, host, session and
timeline on `OwnedFlightWorker`. The worker advances monotonic wall demand while
Unity rendering is suspended. Unity `Update` receives operator inputs and reads
committed state; it does not pump physical work. The unframed legacy model keeps
its separately labelled main-thread accumulator and is outside this worker claim.

`presentation off` suspends craft/planet and scientific overlay updates while
retaining the last composed image. `presentation on` resumes from the latest
committed state. `status` exposes worker thread/queue/pump/fault counters, clocks,
debt, world epoch, and thermal integrated/pending intervals. `render-rate` changes
only the presentation cap. Presentation callback counts cannot establish worker
throughput or GPU cost.

The main thread owns one `OwnedFlightPresentationLease` at a time. Its bank,
origin, universal time, context and progress are a coherent committed descriptor.
Camera focus, mesh poses, planet orientation and overlay use that descriptor;
they never evaluate the mutable session scratch sampler. A publication gap
retains the prior composed image. The lease is released before cold command
execution and at teardown, allowing the retained numeric banks to be reused.

Manual socket controls and changed keyboard values use explicit live-next-commit
supersession. Uncomputed control values change without deleting time debt. The
deterministic lab uses historical `Configure` policy boundaries instead. These
are distinct input policies, not interchangeable replay claims. Pilot config
changes remain paused and debt-free; feedback resolves from committed physics
on the worker.

Physical commands (placement, pilot, prediction, topology, checkpoint, coast,
rebase and executable reload) execute as acknowledged worker operations. Unity
camera/assets/GUI and JSON presentation receipt serialization remain main-thread.
Queued requests can be cancelled before execution; started operations are not
generically rolled back or retried. A fault latches, retains debt and stops
automatic progress. Explicit same-time code recovery can preserve that debt.

The default acquired workbench declares independent thermal evolution with a
bounded lag/deferred-temperature/error policy. Accumulated forcing and pending
energy are authoritative bank state and survive binary checkpoint/HMR. Save
requires paused, debt-free wall scheduling but permits a pending thermal ledger.
Topology and passive clock jumps explicitly flush that ledger before their
original atomic operation. `thermal flush`, `thermal independent` and
`thermal reference` are cold controls. The local forecast uses the physical
quantum at warp one; warp is wall-demand scaling, not an enlarged solver step.

Render failure suspends presentation and requests one acknowledged worker
pause. A separate error record retains any pause failure; a receipt boolean
does not substitute for the worker's clock. `presentation on` retries rendering
and `pause off` resumes simulation separately. `render-fail-next` injects one
labelled failure before GPU submission to check the actual recovery path.
Shutdown joins the worker and copies its final leased bank into the receipt.

Nodes remain paused immediate-coast tools. Running with a pending node is
rejected; execute or clear it. No automatic event transaction or stale-node
relaxation is implemented.

The captured 512-part/426-body consumer passed on the matching Unity2019/Metal
x86_64 player on Apple Silicon. Simulation progressed with the craft render
counter unchanged, including about 70 simulated seconds over 17.5 wall seconds
at requested warp four. This is an owned-model operation witness, not a stock
KSP speedup or maximum-throughput benchmark. The final consumer also passed
pending-thermal binary checkpoint equality, preflight rejection without state
publication, deliberate render failure/pause/resume, failed executable schedule
and debt-preserving recovery, rebase, and observed worker/scene teardown.
Textured craft detail, a normals view and the scaled planet were inspected.
The portable observed/suspended executions had identical complete checkpoints.
Adaptive mechanical integration, whole-game clock transfer, terrain/contact,
broader stock/mod providers and faithful stock rendering remain open.

The next source package adds `mechanics adaptive|fixed [MAXIMUM_INTERVAL]` at a
paused, debt-free, event-free boundary. Repeated trials require the executable's
explicit trial-safe contract. `event impulse RUNTIME_TIME REGION_ID PX PY PZ`
queues center-applied SI momentum at an exact owned runtime frontier, separately
from the older manual coast node. Status includes event counts and controller
trials/accepted intervals/error estimates. Pending events cannot silently be
discarded by ordinary cold mutation or HMR.

Manual checkpoints include execution/event state and may capture retained debt
or a latched fault at an acknowledged pause. Physical-only pilot saves and owner
replacement still require zero debt. Existing feedback-pilot saves
remain explicitly physical/pilot-only with no pending events, restoring their
resolver from admitted pilot configuration. All candidate restore authorities
are staged before worker execution-state validation. `interpolation on|off` and
`interpolation delay SECONDS` affect displayed poses only, with a separately
identified physical frontier.

The integrated package exercised the actual captured512-part model with adaptive
intervals reaching.1 seconds, live wheel boundaries, typed impulses, identical
future-event/debt snapshots, interpolation, failed provisional computation/HMR
recovery and inspected craft/normals/planet rendering. Old binary v1/v2/v3 files
decoded. Complete costs include trials and cold work; no stock KSP speedup or
global-error certificate is inferred. Changed HMR images require a fresh Mono
assembly identity; the attested process registry rejects same-identity changed
bytes before executable load/construction and permits alternate types from the
same exact image. Full-game
clock transfer, contact/terrain, arbitrary modules and continuous interpolation
under long stalls remain separate gates.

`execution reference|compiled` selects the original ordered fold or retained
dependency plan at an acknowledged cold boundary. `mechanics embedded` selects
the builtin-only two-wrench operator; `mechanics adaptive` retains full trials.
`work-timing on|off` controls coarse instrumentation without changing physical
configuration. Status publishes copied system/bank counters from the worker;
rendering never reads a live mutable counter object. Checkpoint v4 preserves
strategy/method, while instrumentation is observation-only and must be enabled
again after restoring a new world.
