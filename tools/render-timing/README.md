# Render-thread OpenGL timing diagnostic

This private-laboratory diagnostic observes GPU timestamp spans in an existing
Unity OpenGL player. It does not require a development player or Unity profiler
samplers. It does not measure exclusive GPU utilization or a complete GPU frame.
Forward-camera `BeforeForwardOpaque` → `AfterEverything` excludes earlier shadow
and depth work; queue bubbles and CPU starvation can be inside a timestamp span.
Camera roles, rendering path, capture interval and native thread samples belong
in the calling laboratory receipt.

Build the macOS player architecture independently:

```text
cargo build --release --target x86_64-apple-darwin --manifest-path tools/render-timing/Cargo.toml
```

The resulting `libcontinuum_render_timing.dylib` is not installed by the build.

## C ABI

All exports use C calling convention; integers below are signed 32-bit.

| Export | Contract |
| --- | --- |
| `crt_begin_run(const uint8_t* utf8, int length) -> int` | Copy 1–128 valid UTF-8 run-ID bytes. 0 succeeds; 1 invalid range/pointer, 2 invalid UTF-8/NUL, 3 previous ownership not cleaned. Call before INIT. |
| `crt_enqueue(int kind, int cameraId, int frame) -> int` | Copy event tags, return positive token or 0 with explicit journal failure. Nonnegative frame; negative Unity camera instance IDs are valid. Queue limit256. |
| `crt_get_render_event_func() -> void*` | Unity rendering callback `void callback(int token)` for `IssuePluginEvent`. Callback consumes each unique token once. |
| `crt_json(uint8_t* output, int capacity) -> int` | Required UTF-8 bytes including NUL; null/0 queries size. Copy only if capacity fits. -1 is serialization failure. Caller owns buffer; snapshots may grow between calls, retry if returned required size exceeds capacity. |

Event kinds: INIT=0, BEGIN=1, END=2, POLL=3, CLEANUP=4. BEGIN/END must carry
identical camera/frame tags, with actual command-stream ordering. INIT must execute
before those markers. Put POLL after camera END or in a render-thread event after
the frame. Rebuild marker buffers with fresh tokens each rendered frame; replaying
a static token is invalid. Tags remain copied even when Unity delays execution.

INIT requires a nonnull CGL context on the actual render thread. It records driver
renderer/version and requires nonzero timestamp counter bits. Absence/unsupported
driver/error gives negative admission. SDK declarations alone are not driver
support. No positive admission has been inferred from an isolated library build.
Repeated INIT on the admitted thread/context preserves queries and counters, so
nested cameras can each put INIT before BEGIN. A rejected INIT preserves its
original reason; it does not retry. A changed thread/context rejects admission.

The 64-pair ring reads results only after both availability flags are true. It never
calls `glFinish`, waits for a query, or reads a not-ready result. Missing END or
unready results older than12 tagged frames are explicitly invalid/dropped.
The 512-row bounded journal reports overwrite counts. Native mutex locking and
query overhead are diagnostic costs, not zero-cost guarantees. Snapshot copying
is bounded; JSON serialization happens outside the mutex. GL error checks consume
the context's error flag; this is an opt-in observer, not an invisible interceptor.

Remove owned buffers/hooks before shutdown. Queue POLL on subsequent render frames
to drain asynchronous readback, then CLEANUP; retain the loaded library until that
callback has completed. CLEANUP may execute after negative admission without any
queries. If queries exist it requires the original render thread/context, polls
available results, journals abandoned pairs, deletes owned queries, and records
discarded queued commands. A wrong cleanup context retains ownership and rejects
cleanup. A new run cannot replace outstanding queries or queued work. Tokens are
monotonic across resets, so callbacks from an older run cannot match new commands.

Receipt schema `continuum-render-timing/v1` includes PID/run identity, initialization
status, renderer/version/counter bits, callback frame, pending/queued/outstanding
counts, cleanup state, valid/invalid/drop/unready/overwrite totals, and tagged
samples. A sample's delay uses supplied Unity frame tags, not driver clock epochs.
Pending or invalid cleanup never implies successful native asset release.
