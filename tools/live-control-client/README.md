# Native live control client

This Rust client controls the existing localhost experiment server. Build with
`cargo build --release --manifest-path tools/live-control-client/Cargo.toml`.
An output receipt must be a new path; existing files are never overwritten.

```text
tools/live-control-client/target/release/continuum-live-control-client --address 127.0.0.1:18541 --clock status --output /ABSOLUTE/NEW/status.json
```

Clock actions are `status`, `enable`, `disable`, `normal`, `warp32`, `warp128`,
`pause`, `resume`, `save`, `restore` and `lighting`. Save and restore leave
simulation paused. Lighting returns an absolute path to instantaneous native
camera/light facts; it does not capture pixels. Enable, disable, save and restore
default to a 65 second client timeout for the server's 60 second operation budget.
Other actions default to five seconds. `--timeout-ms` may override these within
the documented bounds. A timeout is not cancellation or completion evidence.

The client requires the `owned-clock-control` capability and protocol 1.4.0. It
checks the exact request, session, epoch and protocol before accepting a reply;
server operation failures are errors. Clock actions cannot be mixed with rigid,
snapshot, sweep or quit actions. The raw report remains in the receipt and stdout;
redirect stdout to a private log when a report contains a large method ledger.

Existing rigid controls, snapshot collection and `--quit-when-idle` remain
available. Quit is a separate operation and must be used only for the owned
disposable laboratory instance after verifying handback. A reply is not process
exit proof: join the launched process afterward.
