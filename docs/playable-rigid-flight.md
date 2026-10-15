# Persistent KSP rigid flight service

`CONTINUUM_PLAYABLE_RIGID` explicitly selects an existing absolute private receipt
directory. The Flight addon creates a fresh `rigid-SESSION-GUID` child and starts
in stock-disabled mode. The in-game panel and existing typed live-control service
are the operator entry points. This service does not drive pilot inputs, own a
camera, require a capture window or automatically quit the game.

The service installs pinned stock lifecycle prefixes before creating a motion
owner. Save/load, staging, coupling/decoupling, rails transitions, active-vessel
changes and scene requests synchronously return motion authority to stock before
the original producer runs. Original subscriber chains remain in place. The
prefix never replaces the stock operation. Failed handback prevents that stock
mutation and records a fault; successful handback followed by unsupported
admission leaves stock active. Admission is not retried every frame: faults latch
until explicit retry or a genuine target/topology epoch change. Stable eligible
state settles for one wall second before a fresh owner is created.

`PlayableRigidLifecycle` is the portable operator policy. It covers enable,
disable, pause/resume, target rebinding, boundary cancellation, fault/retry and
terminal shutdown without Unity or serialization dependencies. Host code owns
physical admission, restoration and actual stock calls.

## Receipts and controls

Each service session binds run ID, process ID, session GUID, stock assembly
SHA/MVID, helper digest and epochs. `ready.json` means a loaded unpacked stock
flight was observed; it does not mean the rigid owner is qualified. `status.json`
is an atomic cold readback. `transitions.jsonl` retains at most 512 transitions;
capacity exhaustion disables new ownership rather than silently dropping an
unbounded history. Each owner attempt receives a new epoch/attempt receipt.
`final.json` retains service cleanup evidence and bounded PlayerLoop observations.
No existing receipt or game save is overwritten by this service.

The shared control addon calls `Snapshot()`, `SetEnabled(bool)` and `Retry()`;
commands remain run/session/epoch-bound in that addon's typed protocol. Snapshot
resource/mass/control readbacks occur only on an explicit request. No new socket
or background simulation authority is introduced.

Physical eligibility and terrain/nearby-vessel margins use the owner's shared
`EligibilityReason` contract; the service does not invent a second threshold.
Scene changes return to stock while retaining this process session. Deferred
Unity destruction must finish before reacquisition or a successful final receipt.

Recent 4096 completed frame timestamp intervals retain current/previous ownership
and epoch. Comparisons use only intervals with matching endpoint ownership and
epoch, with overwritten counts explicit. These recent intervals and the bounded
PlayerLoop scope buffers can cover different windows.

Render frames per wall second and fixed simulation seconds per wall second are
reported separately. PlayerLoop observations are inclusive wall envelopes,
bounded to the existing 4096-row buffers; overflow is reported. They include
observer work and waits, are not GPU timing and do not establish an FPS gain.

## Remaining qualification

Private native sessions now cover normal stock/MechJeb controls, LOW4 and return,
powered resource/mass changes, original save/load/staging handback, reacquisition
and airborne atmosphere. Actual KSP pixels were inspected. Retained admission
reduces repeated proof/publication costs substantially, but the rigid backend
has not demonstrated a whole-frame FPS gain over stock. Atmospheric saved craft
and orbital station have different actual part/body counts; their results are
not interchangeable.

The optional frozen runner selection `--flight-thread-cpu true` records macOS
owner-thread cumulative CPU and wall time for four named PlayerLoop scopes.
`rigid-thread-work` independently validates/export-summarizes the retained final
report. It rejects unavailable/invalid CPU evidence, clock mismatches and corrupt
counter arithmetic. Completed rows survive explicit interruption at shutdown;
open brackets are reported and omitted. Scopes overlap, read overhead remains
included, and wall-minus-CPU is not a GPU duration or coroutine identity.

Broad qualification still requires a representative mission, human control-feel
evaluation, docking/undocking and active-vessel switching, and the one-hour play
session. Existing short native witnesses do not establish those outcomes.
Contact, arbitrary mods and replaced staging/docking physics are not admitted by
this slice. A stock transition and later reacquisition do not imply those domains
were replaced. No matching-performance or endurance claim follows from source
acceptance or a short successful control window.

## Explicit cold frame capture

`CaptureFrames(out path)` retains the current bounded frame ring in a fresh
`frames-GUID.json` inside the admitted process/session receipt directory. The
existing control addon takes its endpoint `Snapshot()` before requesting this
export, so JSON allocation and file writing fall outside that window's ending
timestamp. Capture does not append a lifecycle event, renew an audit lease,
change policy or release/reacquire the physical owner.

The receipt uses `cleanupStatus=captured-operator-window`. That is an observation,
not completed service cleanup; the separate final receipt must still prove
closure. Retained interval overwrite counts remain explicit. Capture promptly
at a window endpoint, since export cannot recover rows already overwritten by
later interaction. Export errors return an error and no successful path; partial
failed files are not qualification evidence. No automatic hot JSON export is
introduced.
