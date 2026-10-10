# Local KSP research program

Continuum supports local reconstruction and performance research. The milestones are a trustworthy lab, controlled managed reconstruction, faster playable KSP, private native modern-Unity KSP, and interchangeable subsystems. They are independently qualified capabilities; a full replacement runtime is an optional destination.

The laboratory is C#/Rust. Game binaries, reconstructed source, assets, saves, profiles, raw reports and generated projects stay in private runtime storage. Potential plugins or owner-installation migration tooling receive a separate artifact-specific sharing decision.

## Available commands

Build `tools/KspContinuum.Reconstruction` in Release and build `tools/research-lab`. The same Rust commands are exposed through `cargo run --manifest-path tools/xtask/Cargo.toml -- lab ...`.

Every output path must be new. Examples use operator-supplied private paths.

| Operation | Arguments | Actual capability |
| --- | --- | --- |
| `inventory` | `--instance INSTANCE --output NEW_JSON` | Hash every regular file; record symlinks without following them. |
| `prepare` | `--output NEW_JSON [--variant MANIFEST_JSON]` | Generate the 30-case **unbound blueprint**, optionally binding a validated variant manifest by hash. It does not manufacture checkpoints or claim runnable cases. |
| `index` | `--assembly DLL --ilspy PINNED_ILSPY --output NEW_DIRECTORY` | Metadata, identities, references, native bindings and decompiled project; retains failed decompilation evidence. |
| `index` source | `--project PROJECT.csproj --output NEW_JSON` | Freeze project inputs by tree hash, explicitly excluding compiler `bin`/`obj` outputs. |
| `recompose` | `--assembly DLL --sha256 INPUT_SHA --output NEW_DIRECTORY` | Unchanged unsigned-assembly rewrite with API/normalized-IL checks. |
| `recompose` probe | Add `--method TYPE.METHOD --suffix TEXT` | Append text to every return of one static parameterless string method. This is a local visible-version canary. |
| `recompose` source | Add `--project PROJECT.csproj --project-sha256 TREE_SHA` | Hash-guarded C# rebuild to private output with log, 15-minute bound and terminal receipt. Reference and runtime behavior qualification remain separate. |
| `run` | `--instance LAB_COPY --reservation PRIVATE_SESSION_JSON --output NEW_DIRECTORY [--timeout-seconds 300]` | Owned visible menu/native-boundary **process smoke**, not a flight qualification. |
| `compare` | `--baseline RECEIPT --candidate RECEIPT --treatments engine,architecture --output NEW_JSON` | Matched flight receipt comparison; only declared variant factors may differ. |
| `compare` startup | `--startup-screen SCREEN_JSON --output NEW_JSON` | Three fresh baseline/candidate startup runs with matching nonce, selected binary hashes and restored effective presentation. Screening only. |
| `qualify` | `--receipt RECEIPT --output NEW_JSON` | Per-case numeric target check, not mission/session or whole-matrix qualification. |
| `recovery` | `--catalog CATALOG_JSON --output NEW_STATE [--state PRIOR_STATE --trial-id ID --outcome passed\|failed\|invalid]` | Dependency-safe mod failure minimization and fallback-ring trial planning. Actual CKAN/crash/relaunch adapters remain unqualified. |

`index` requires a pinned local ILSpy CLI (initial version 11.1.0.9782). Game references are resolved from the actual managed directory. Rewriting is not source recompilation. Neither static check proves game behavior.

The `run` instance must contain `continuum-lab.local.json` with `scope: disposable-lab`, created only after a disposable clone is explicitly designated. Its reservation must use schema `continuum-lab-reservation/v1`, host `forge`, active `true`, and authorization `user-reserved-until-revoked`. The shared reservation acquires an exclusive run lock. A timeout kills and reaps only the child it started, retaining logs and an interrupted receipt. A stale lock after abrupt harness termination requires operator inspection; it is never silently stolen.

Updated observer builds emit nonce-bound game/asset/observer assembly hashes and effective resolution/VSync/frame cap. The parent measures launch to the first owned addon `Start` in the main menu, with 100 ms polling resolution. This boundary is narrower than rendered/interactable menu readiness. Source reconstruction smoke runs may overlap compiler work; their timings are unqualified. Timed startup experiments must run alone and declare their cache state. Reservation revocation terminates the owned run.

`run --startup-unlimited true` enables an opt-in loading-only presentation experiment. It removes VSync/frame capping during loading and restores frozen game settings before the menu observation. It changes no asset loading or mod ownership. Check requested/applied/restored fields and effective settings in the bound observation; numeric speed alone cannot qualify restoration.

Recovery catalogs use schema `continuum-mod-recovery-catalog/v1`, frozen `profile_sha256` and `workload_sha256`, a stage-specific `failure_signature`, a `dependencies` map of mod IDs to required IDs, and a dependency-complete `verified_ring`. Supply `--failure-signature` for a failed diagnosis trial. The state machine verifies fallback and full group, then removes groups with their dependents until no smaller dependency-safe reproduced failure remains. A failed fallback, invalid trial, unrelated failure, stale result, changed catalog or exhausted budget cannot identify a culprit. Each next trial specifies its fallback ring. Results are operator declarations until a hashed process/crash adapter is attached.

## Receipt and experiment contracts

`continuum-lab-flight/v1` requires run/case identity, fixture/protocol SHA-256, frozen configuration, variant factors, terminal status, complete frame intervals, requested/achieved simulated duration, requested rate, wall duration, stable topology, qualified observation, verified behavior and zero dropped samples. Frame intervals must bracket the wall duration. Configuration differences, partial simulation windows, self-comparison, unqualified evidence and undeclared treatment differences are rejected.

`continuum-lab-variant/v1` requires `id`, `code_revision`, SHA-256 maps `binaries`, `assets`, `packages`, explicit `engine`, `runtime`, `architecture`, `renderer`, and unique `enabled_replacements`. Binary and asset maps cannot be empty. Package and replacement collections may explicitly be empty. Manifests and receipts are private runtime artifacts. These are validated declarations; live binding and provenance verification remain required.

A single pair cannot establish a confidence interval or promote a replacement. Numeric success cannot replace mission, save/reload or play-session proof. Current receipt verification trusts the supplied observer/behavior attestations; binding them to live hashed evidence is a remaining qualification gate, not an authenticity guarantee.

## Frozen program protocol

Primary cells: 16/64/128/256/512 parts, Kerbin atmosphere/vacuum, low/baseline/high graphics at 1920x1080, unpowered normal-time flight. Bind checkpoint, input timeline, camera, lighting, effective settings, timestep, topology and behavior contract before launching. Record rigidbodies, joints, colliders, physicsless parts, modules and loaded vessels.

Screen three fresh processes for ten simulated seconds; confirm six alternating baseline/candidate pairs for thirty simulated seconds. Compare uncapped/VSync-off diagnostic results separately from playable presentation. Follow-ups isolate powered flight, warp, resolution, settings, complexity and mod costs.

Initial playable target through 256 parts: mean >=59.5 FPS, p95 <=20 ms, p99 <=33.3 ms, >=0.99x simulated time; representative mission, save/reload and one-hour operator play session. Retina is separate and 512 is a capacity frontier.

Modern Unity research recovers matching-2019 assets/project structure before pinned Unity 6.3 LTS Mono/Built-in x64 and ARM64 comparisons. DOTS, Burst, layout, physics and rendering are separate treatments. Full native runtime or 60 FPS capability remains unqualified until complete live evidence exists.

## Verification

`dotnet run --project tests/KspContinuum.Reconstruction.Tests -c Release`

`cargo test --manifest-path tools/research-lab/Cargo.toml`

The existing xtask portable/field lanes include these checks.
