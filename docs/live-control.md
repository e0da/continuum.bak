# Live experiment control slice

Protocol 1.3 adds `rigid-status`, `rigid-enable`, `rigid-disable` and `rigid-retry`. Each command is `OPERATION 1.3.0 REQUEST_ID SESSION_ID EPOCH`; stale identities reject before touching flight ownership. The commands operate on the explicitly enabled playable rigid service and retain the original loopback transport. A processed enable request means the service will attempt acquisition after its settling boundary; inspect `rigid.owned` and `rigid.state` for actual motion ownership. Failed mutations return `rejected` with a reason. Status includes current stock controls/resources and separate rendered-frame and simulation rates. JSON is an operator boundary, not a physics-step format.

The existing Rust client accepts `--rigid status|enable|disable|retry` with optional `--output` to retain a fresh observation. Service admission, scope and transition behavior are described in [playable rigid flight](playable-rigid-flight.md).

[E0D-1882](https://linear.app/e0da/issue/E0D-1882/expose-a-typed-external-control-and-observation-plane-for-live-ksp) owns the broader external control and observation plane. The opt-in loopback endpoint for KSP 1.12.5 flight exposes `hello`, an active-vessel snapshot, and a named dry-buoyancy strategy sweep. It does not pause, step, inject inputs, stream telemetry, restore saves, or confer general simulation authority.

Start KSP with `--continuum-control-port=47771` and the Continuum addon installed. The port is an example; choose a free local port. The listener binds only `127.0.0.1` and exists only while the flight addon exists. Keep a TCP connection open and send ASCII command lines ending in LF; each command receives one JSON line with the matching request ID. A subscribed connection can also receive sweep observations with no request ID while continuing to send commands. Frames are limited to 256 bytes, the accept backlog is one, pending game-thread work is bounded, and at most one request is handled per `Update`. The protocol has no remote access or authentication and should be used only with trusted local processes.

1. Send `hello 1.0.0 REQUEST_ID` to negotiate the current protocol version and read `sessionId`, `epoch`, `renderFrame`, `observedFixedCallbacks`, scene, vessel ID, and capabilities.
2. Send `snapshot 1.0.0 REQUEST_ID SESSION_ID EPOCH MINIMUM_FIXED_CALLBACKS` using the identity from `hello`. The minimum callback count can be `0` for an immediate query. A vessel or scene change, including a replacement runtime vessel with the same GUID, advances the epoch; addon restart changes the session. A stale request returns `stale-identity` and does not capture vessel state. Read `hello` again and retry only if that is still the intended vessel.

The wire grammar has no optional fields in version 1.0.0: extra tokens are rejected as `invalid-request`, unknown operations as `unknown-operation`, and unsupported versions as `unsupported-version`. IDs use up to 64 ASCII letters, digits, `_`, or `-`. Replies echo valid IDs, name typed status/reason values, and advertise the minimum and maximum supported protocol version. Future minor versions may add optional response fields and capabilities; clients should ignore unknown response fields and gate behavior on negotiated capabilities. Removing or changing a field or operation requires a new major version and an explicit deprecation period. No telemetry events exist yet. When added, telemetry may have bounded/drop policies; commands, acknowledgements, and history cannot share a droppable queue.

Protocol 1.1 adds `sweep-start` and `sweep-status` for the single named `dry-buoyancy` experiment. Both bind the request to the negotiated session and vessel epoch. Status reports the current state and window; a completed run reports its local result directory. The same KSP process can run the sweep again after completion. `quit-when-idle` asks the owned process to exit after experiments and rejects while a sweep is waiting or running.

Protocol 1.2 adds `sweep-subscribe 1.2.0 REQUEST_ID SESSION_ID EPOCH dry-buoyancy`. Its correlated reply contains the current status. Later status changes arrive as `status: "observation"` messages without a request ID. The server retains one latest unsent observation per subscribed connection, so a slow reader may skip intermediate windows; the retained terminal state remains observable. Socket writes stay on the background worker, and replacing the pending observation does not block the game thread. Command replies remain correlated by request ID and can be interleaved with observations. Subscription ends with the connection. `sweep-status` remains available to protocol 1.1 clients.

The Rust client first requires the advertised `dry-buoyancy-sweep-push` capability, then starts the sweep, subscribes, and prints pushed state changes until it receives the terminal state, without restarting KSP or polling. `--timeout-ms` remains the socket read timeout, including while awaiting an observation; a sweep window silent for longer than that bound fails the client rather than claiming an indefinitely healthy subscription.

```sh
tools/live-control-client/target/release/continuum-live-control-client \
  --address 127.0.0.1:47771 --dry-buoyancy-sweep
```

After the experiment queue is idle, close the owned host through the same endpoint:

```sh
tools/live-control-client/target/release/continuum-live-control-client \
  --address 127.0.0.1:47771 --quit-when-idle
```

The snapshot contains bounded scalar fields: vessel identity and name, body, situation, loaded/packed flags, part count, universal time, altitude, surface/orbital speed, and throttle. It does not enumerate parts or expose Unity object references. `observedFixedCallbacks` counts this addon's `FixedUpdate` calls; it is not a claim of a global deterministic physics tick. Snapshot capture and request validation run on Unity's main thread during `Update`. Socket I/O runs on a background thread. `observerNanoseconds` measures main-thread validation and capture, excluding JSON serialization, queue wait, socket I/O, and the small per-frame vessel/scene identity check. It is diagnostic overhead, not a whole-frame profile.

The portable test exercises identity invalidation, invalid data, named sweep dispatch, socket framing, the socket-to-game-thread queue, coalesced terminal delivery, and command correlation after subscription. A successful portable or addon build does not prove this endpoint works in an installed KSP process. The sweep reuses the experiment runner's own single-owner admission and cleanup; the endpoint does not grant authority to other writers.

## Live qualification

Build the Rust client before starting any timed run. Then use the binary to
negotiate one persistent connection and issue bounded,
sequential snapshot requests. It writes every round-trip duration and the
server's scoped main-thread `observerNanoseconds`; it stops on the first
rejection, identity change, timeout, malformed reply, or missing snapshot.

```sh
cargo build --release --manifest-path tools/live-control-client/Cargo.toml
tools/live-control-client/target/release/continuum-live-control-client \
  --address 127.0.0.1:47771 --samples 900 \
  --ready artifacts/live-control/loaded-ready.json \
  --output artifacts/live-control/loaded.json
```

The client creates the readiness marker only after the first accepted snapshot.
It records that snapshot's render frame and fixed-callback count. Every final
sample records the same counters, so the client receipt can prove that accepted
requests bracketed the PlayerLoop capture rather than merely that a client
process existed.

Qualify overhead from the same settled-flight checkpoint in three fresh runs:

1. Launch without the control flag and capture 300 frames as the baseline.
2. Launch with `--continuum-control-port=47771`, leave the endpoint idle, and
   capture 300 frames to isolate listener/identity-check overhead.
3. Launch with the endpoint enabled and start the prebuilt client for 900
   snapshots. Wait for the new readiness marker, inspect that it says `ready`,
   then start the 300-frame capture. Confirm the client is still running when
   the capture completes.

Keep resolution, graphics settings, timestep, warp, vessel identity and
topology unchanged. Enable `--continuum-playerloop` in all three runs. Retain
the raw marker reports, client receipt, KSP log, package hash, source commit and
checkpoint hash. The client receipt proves transport behavior and reports two
timing scopes; only the matched PlayerLoop captures can assess whole-frame
impact. Reject the qualification if any run is incomplete, context differs,
PlayerLoop integrity or cleanup fails, the client completes fewer than 900
samples, any response is non-`ok`, or teardown leaves the port accepting
connections. Also reject unless an accepted client sample has a render frame at
or before the first PlayerLoop capture frame and another has a render frame at
or after the last capture frame. The readiness marker alone is insufficient
evidence for the latter condition.

Donor decisions: [Gimbal replay](https://github.com/e0da/gimbal/blob/main/crates/replay/src/lib.rs) keeps checkpoint payloads semantic; [Gameboard's reducer](https://github.com/e0da/gameboard/blob/main/apps/gameboard/lib/gameboard/room_reducer.ex) rejects stale authority epochs and revisions; [Wildline architecture](https://github.com/e0da/wildline/blob/main/docs/design/02-architecture-draft.2.md) proposes command admission at tick boundaries. These inform identity and future control design. They do not establish that this KSP transport or future replay is qualified.
