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
| `resolve` | `--instance OWNED_INSTANCE --output NEW_JSON` plus existing launch switches | Freeze startup/flight selection and required binary, settings, checkpoint and prepared-metadata identities without starting KSP. |
| `index` | `--assembly DLL --ilspy PINNED_ILSPY --output NEW_DIRECTORY` | Metadata, identities, references, native bindings and decompiled project; retains failed decompilation evidence. |
| `index` source | `--project PROJECT.csproj --output NEW_JSON` | Freeze project inputs by tree hash, explicitly excluding compiler `bin`/`obj` outputs. |
| `recompose` | `--assembly DLL --sha256 INPUT_SHA --output NEW_DIRECTORY` | Unchanged unsigned-assembly rewrite with API/normalized-IL checks. |
| `recompose` probe | Add `--method TYPE.METHOD --suffix TEXT` | Append text to every return of one static parameterless string method. This is a local visible-version canary. |
| `recompose` source | Add `--project PROJECT.csproj --project-sha256 TREE_SHA` | Hash-guarded C# rebuild to private output with log, 15-minute bound and terminal receipt. Reference and runtime behavior qualification remain separate. |
| `run` | `--instance LAB_COPY --reservation PRIVATE_SESSION_JSON --output NEW_DIRECTORY [--timeout-seconds 300]` | Owned visible menu/native-boundary **process smoke**, not a flight qualification. |
| `compare` | `--baseline RECEIPT --candidate RECEIPT --treatments engine,architecture --output NEW_JSON` | Matched flight receipt comparison; only declared variant factors may differ. |
| `compare` startup | `--startup-screen SCREEN_JSON --output NEW_JSON` | Three fresh baseline/candidate startup runs with matching nonce, selected binary hashes and restored effective presentation. Screening only. |
| `startup-timeline` | `--log FROZEN_KSP_LOG --output NEW_JSON` | Hashed current-launch log boundaries. Excludes nested replayed logs; rejects incomplete launches, duplicate terminal boundaries and backward clocks. Intervals are elapsed wall time, not exclusive subsystem costs. |
| `qualify` | `--receipt RECEIPT --output NEW_JSON` | Per-case numeric target check, not mission/session or whole-matrix qualification. |
| `recovery` | `--catalog CATALOG_JSON --output NEW_STATE [--state PRIOR_STATE --trial-id ID --outcome passed\|failed\|invalid]` | Dependency-safe mod failure minimization and fallback-ring trial planning. Actual CKAN/crash/relaunch adapters remain unqualified. |

`index` requires a pinned local ILSpy CLI (initial version 11.1.0.9782). Game references are resolved from the actual managed directory. Rewriting is not source recompilation. Neither static check proves game behavior.

The `run` instance must contain `continuum-lab.local.json` with `scope: disposable-lab`, created only after a disposable clone is explicitly designated. Its reservation must use schema `continuum-lab-reservation/v1`, host `forge`, active `true`, and authorization `user-reserved-until-revoked`. The shared reservation acquires an exclusive run lock. A timeout kills and reaps only the child it started, retaining logs and an interrupted receipt. A stale lock after abrupt harness termination requires operator inspection; it is never silently stolen.

Updated observer builds emit nonce-bound game/asset/observer assembly hashes and effective resolution/VSync/frame cap. The parent measures launch to the first owned addon `Start` in the main menu, with 100 ms polling resolution. This boundary is narrower than rendered/interactable menu readiness. Source reconstruction smoke runs may overlap compiler work; their timings are unqualified. Timed startup experiments must run alone and declare their cache state. Reservation revocation terminates the owned run.

## Resolved launch and asset generation

Legacy `run` switches resolve through `continuum-resolved-launch/v1`. Startup replacements and startup diagnostics have separate typed selections; preparation modes, conflicting providers and flight controls are validated before launch. The existing flight request remains the workload/control contract. `prepare --variant` still supplies a declared blueprint; it does not silently become a verified runtime variant.

`resolve` writes `continuum-frozen-launch/v1` with canonical instance root, normalized selection and required input SHA256s. Inputs cover the player, selected managed engine/game binaries, GameData DLLs, settings, selected checkpoint, shader/GPU library, prepared catalog/cache/image metadata, frozen texture producer and optional native installation receipt. It does not inventory every GameData asset or validate the effective renderer before Unity initializes. Small inputs are hashed with length/mtime guards; texture payloads retain their existing runtime guards rather than being rehashed by this step.

Replay uses `run --instance OWNED_INSTANCE --reservation ACTIVE_SESSION --launch-profile FROZEN_JSON --output NEW_DIRECTORY`. Selection override switches cannot accompany the frozen profile. The runner reconstructs the required input set and compares the entire envelope, so removing an input from the saved JSON cannot bypass drift rejection. Each run retains its normalized `resolved-launch.json` and binds that file's hash, the selection hash and a location-independent prepared-metadata generation hash in the process receipt.

The first asset-generation adoption covers catalog metadata, texture-image restoration and prepared DLC. The generation hash identifies their selected manifests/producer/game assemblies, not proof that every payload is ready. Existing provider checks establish admission. Actual publications and provider completion establish capability readiness. Preparation remains pending; unrelated diagnostics and run nonce do not change asset generation identity. Unknown synchronous mod consumers still use stock eager publication.

The startup lifecycle owns menu observation and exports `continuum-startup-asset-generation/v1` at menu and shutdown. Reports bind run, PID, instance and both launch/generation hashes, with per-capability admission, publication coverage, fallback and failure. Observer errors cannot change provider publication/fallback behavior; they degrade the receipt and prevent qualification. Missing reports, recording failures, unexpected capability sets or incomplete publication fail the new handoff gate while logs and valid partial evidence remain retained. No general dependency scheduler, arbitrary mod unloading or crash/relaunch recovery is implemented by this envelope.

`run --startup-unlimited true` enables an opt-in loading-only presentation experiment. It removes VSync/frame capping during loading and restores frozen game settings before the menu observation. It changes no asset loading or mod ownership. Check requested/applied/restored fields and effective settings in the bound observation; numeric speed alone cannot qualify restoration.

Startup is the active performance priority. First preserve a frozen `KSP.log` alongside the parent receipt and raw player log. Its origin occurs after process launch, so retain both clocks. ModuleManager embeds historical logs inside the current log; those replay blocks cannot be timed as current work. A sampled launch is attribution evidence only and cannot join an uninstrumented speed comparison. Opt-in `run --startup-profile true` requests scoped DLC bundle attribution; confirm applied/abstained markers in the raw log. Keep payload verification and asset loading behavior unchanged while profiling. Timed promotion runs disable profiling and use the same observer build for baseline and candidate.

`--startup-reuse-bundle-data true` is a reversible candidate that retains the first downloaded byte array only for one guarded DLC iterator invocation. `--startup-file-bundles true` requests a separately guarded file-backed loading candidate. These are independent experimental treatments. Keep both disabled in the baseline. Require explicit applied/fallback/cleanup evidence and original DLC verification; a menu speedup does not establish unchanged asset-loading cost or mission behavior. Any deferred load must be charged to the later asset/flight boundary before promoting an improvement.

Prepare an external private cache with `run --startup-build-cache true --startup-cache ABSOLUTE_NEW_DIRECTORY`. This launches normal loading first, then uses the installed Unity recompressor to derive three LZ4 bundles. Originals remain unchanged. The completed manifest records original SHA256/MD5, derived SHA256 and engine identity; it proves successful derivation, not independent decoded-content equivalence. Interrupted/failed outputs stay unqualified. Cache builds have their own process receipt and cannot enter timed startup comparisons.

`run --startup-cached-bundles true --startup-cache ABSOLUTE_COMPLETED_DIRECTORY` tests that cache. The original game's download, MD5 and signature path remains intact. Original signatures authenticate original bytes; they do not authenticate transformed bytes. Require independent decoded payload/node-directory equivalence and representative DLC asset/scene/mission checks before promoting a cache capability.

Adding `--startup-native-admission true` moves full source/derived hash verification to the native Rust parent. The parent binds verified hashes, engine, paths and manifest to a unique run and passes one hashed proof file. The plugin checks the small proof and keeps original MD5/signature and metadata/path guards. Post-run original and cache hashes must still match. This is admission under a reserved immutable laboratory profile, not authentication against hostile callers or writers. Receipts include admission time and admission-plus-menu time: moving a cost before process launch cannot hide it. Shared-memory transfer and a general socket/RPC worker are future measured alternatives; this startup boundary already needs only paths and a small proof.

Recovery catalogs use schema `continuum-mod-recovery-catalog/v1`, frozen `profile_sha256` and `workload_sha256`, a stage-specific `failure_signature`, a `dependencies` map of mod IDs to required IDs, and a dependency-complete `verified_ring`. Supply `--failure-signature` for a failed diagnosis trial. The state machine verifies fallback and full group, then removes groups with their dependents until no smaller dependency-safe reproduced failure remains. A failed fallback, invalid trial, unrelated failure, stale result, changed catalog or exhausted budget cannot identify a culprit. Each next trial specifies its fallback ring. Results are operator declarations until a hashed process/crash adapter is attached.

### Prepared startup boundaries

`tools/bundle-audit` independently compares UnityFS decoded payload SHA256, lengths, and normalized archive nodes between originals and derived bundles. Run it outside timed workloads; keep its output private. Lossless bytes do not establish DLC scene or mission behavior.

`run --startup-prepared-bundles true` requires cached bundles and native admission. Native admission verifies original MD5 in addition to SHA256 and binds it in the tiny proof. The guarded iterator branch skips original download/copies/hashing, then resumes original cleanup and asset/scene/bundle registration with the original source MD5. This leaves downstream original signature checks intact. Require three hits and zero fallback in the raw activation summary.

`run --startup-catalog-mode prepare --startup-catalog ABSOLUTE_NEW_XML` captures original ordered bundle metadata and its compiled graph, saves a private catalog at metadata readiness, and quits before part/planet initialization. `prepare-minimal` additionally skips only root audio/texture/model enumeration during that preparation process. It preserves assembly/mod discovery and original catalog compilation; qualify its output against an original capture for the admitted profile. It is not a gameplay launch.

`run --startup-catalog-mode reuse --startup-catalog ABSOLUTE_EXISTING_XML` verifies engine/assembly/recipe/blacklist/source inventory, rehydrates metadata, invokes original compilation/flattening, checks the compiled graph, then publishes readiness. Rejection restores state and uses original discovery. Heavy asset loading remains stock. Source inventory includes the plugin DLL; rebuilding it invalidates the catalog and needs a new preparation. These are private immutable-profile experiments, not a general lazy asset runtime.

`run --graphics-api metal` is an explicit renderer probe; default `current` preserves selection. Receipts include requested selection and observed device information. The actual shipped player rejected Metal initialization because it was not built with the required shader support. Engine/shader reconstruction is needed for that path; backend symbols alone cannot establish player support.

## Receipt and experiment contracts

`continuum-lab-flight/v1` requires run/case identity, fixture/protocol SHA-256, frozen configuration, variant factors, terminal status, complete frame intervals, requested/achieved simulated duration, requested rate, wall duration, stable topology, qualified observation, verified behavior and zero dropped samples. Frame intervals must bracket the wall duration. Configuration differences, partial simulation windows, self-comparison, unqualified evidence and undeclared treatment differences are rejected.

`continuum-lab-variant/v1` requires `id`, `code_revision`, SHA-256 maps `binaries`, `assets`, `packages`, explicit `engine`, `runtime`, `architecture`, `renderer`, and unique `enabled_replacements`. Binary and asset maps cannot be empty. Package and replacement collections may explicitly be empty. Manifests and receipts are private runtime artifacts. These are validated declarations; live binding and provenance verification remain required.

A single pair cannot establish a confidence interval or promote a replacement. Numeric success cannot replace mission, save/reload or play-session proof. Current receipt verification trusts the supplied observer/behavior attestations; binding them to live hashed evidence is a remaining qualification gate, not an authenticity guarantee.

## Frozen program protocol

Primary cells: 16/64/128/256/512 parts, Kerbin atmosphere/vacuum, low/baseline/high graphics at 1920x1080, unpowered normal-time flight. Bind checkpoint, input timeline, camera, lighting, effective settings, timestep, topology and behavior contract before launching. Record rigidbodies, joints, colliders, physicsless parts, modules and loaded vessels.

Start with two comparable fresh-process measurements and move on when they agree enough to choose the next action. Add repetitions only to resolve a concrete ambiguity or qualification decision; no fixed six-pair batch. Use ten simulated seconds for initial flight capture and longer windows when the selected behavior needs them. Compare uncapped/VSync-off diagnostic results separately from playable presentation. Follow-ups isolate powered flight, warp, resolution, settings, complexity and mod costs. The older three-run startup comparison command is retained for existing receipts, not the default experiment policy.

Initial playable target through 256 parts: mean >=59.5 FPS, p95 <=20 ms, p99 <=33.3 ms, >=0.99x simulated time; representative mission, save/reload and one-hour operator play session. Retina is separate and 512 is a capacity frontier.

Modern Unity research recovers matching-2019 assets/project structure before pinned Unity 6.3 LTS Mono/Built-in x64 and ARM64 comparisons. DOTS, Burst, layout, physics and rendering are separate treatments. Full native runtime or 60 FPS capability remains unqualified until complete live evidence exists.

## Verification

`dotnet run --project tests/KspContinuum.Reconstruction.Tests -c Release`

`cargo test --manifest-path tools/research-lab/Cargo.toml`

The existing xtask portable/field lanes include these checks.
