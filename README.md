# Continuum

An experimental KSP 1 mechanics and performance lab with an addon, captured workloads, and system replacements. Continuum and Gimbal share a longer-term goal of supporting Kerbal-like, toy and realism experiences. Their [runtime and repository boundary](docs/continuum-gimbal-boundary.md) remains provisional, including a possible complete KSP/Unity replacement. Rigid assembled craft is the primary motion model; optional flexibility and wobble remain research capabilities. `KspContinuum` remains the addon namespace.

**Status: experimental laboratory and opt-in replacements.** Prepared startup providers and batched flight rendering have run in an owned KSP instance. The [delivery ledger](docs/research-delivery.md) records their measured scope and remaining qualification. Complete playable performance, broad mod compatibility and a complete independent game runtime remain open outcomes.

The [owned runtime and live workbench](docs/owned-runtime.md) now execute a real captured 512-part craft through controls/resources, rigid motion, atmosphere/warp, split/join, complete checkpoints and executable schedule HMR. The same retained simulation runs in the portable laboratory and matching Unity/Metal consumer. Visible geometry, native continuation and code rejection/recovery have been checked; full KSP gameplay, contacts, broad providers and stock shader fidelity remain open.

The [owned central-body flight consumer](docs/owned-flight.md) adds captured orbital placement/gravity/rotating atmosphere, guarded Kepler coast, predictions, maneuver nodes, directional control authority and complete flight checkpoints. It renders the actual captured craft meshes/textures and planet with scientific views and retained draw packets. Original shader fidelity and a complete independent game remain open.

The [owned flight timeline](docs/owned-flight-timeline.md) separates requested wall progress from committed physical time, retains ordered input/debt, and supports bounded local work, event barriers and admitted coast. Its [independent worker](docs/owned-flight-worker.md) owns simulation and publishes coherent leased presentation state. [Thermal evolution](docs/owned-runtime-thermal.md) retains ordered forcing and pending energy on a separate bounded cadence. [Adaptive mechanics](docs/owned-runtime-adaptive.md), [typed physical events](docs/owned-flight-events.md), complete execution checkpoints and [render interpolation](docs/owned-flight-interpolation.md) now extend that ownership without a global render-driven cadence. The [captured-craft consumer](docs/owned-flight-worker-consumer.md) and [causal flight lab](tools/KspContinuum.CausalFlightLab/README.md) exercise the transfer. The [persistent KSP flight service](docs/playable-rigid-flight.md) supports ordinary controls and safe stock transitions. KSP-wide clock ownership and complete playable qualification remain part of the [whole-runtime redesign](docs/whole-runtime-redesign.md).

The selectable [compiled execution plan](docs/owned-runtime-execution-plan.md) retains physical dependencies and geometry, skips inactive vacuum aero, and probes actuator deadlines without copying the world. [Embedded mechanics](docs/owned-runtime-embedded.md) replaces repeated complete trials with two wrench samples around one scalar transaction. [Retained deadlines](docs/owned-runtime-deadlines.md) project resource/wheel events from current state and certify the frontier against original allocation. [Mission situations](docs/owned-mission-situations.md) exercise burn/cutoff, reversals, exact impulses, warp, complete continuation, airborne reentry and [local moving-frame split/join](docs/owned-topology-transfer.md). Original execution and step-doubling remain available; complete work counters distinguish physical work from render pacing. Binary v5 preserves choices with explicit v1–v4 decoding.

The [roadmap and goal index](docs/roadmap.md) connects existing research to the shared direction. The [replacement architecture](docs/research-replacement-architecture.md) targets complete workloads and avoidable work, rather than inheriting KSP's method and part boundaries. Broader gameplay dynamics qualification remains open; synthetic Rust CPU/Metal kernel results do not establish game-framerate gains.

The [local research program](docs/ksp-research-program.md) adds guarded inventory, managed indexing/recomposition, a 30-case benchmark blueprint, owned process runs, and matched receipt checks. Its [delivery ledger](docs/research-delivery.md) keeps reconstruction, performance, modern Unity, and subsystem replacement gates separate.

## What exists

- A [live rigid vessel owner](docs/live-rigid-owner.md) routes original part force queues and motion reads to one aggregate while retaining gameplay identities. Its bounded 512-part vacuum proof includes original wheel/RCS controls, 4x physics warp and return, followed by stock ownership restoration. Independent raw verification and visible capture qualify this limited transaction separately from playability and FPS.
- A main-menu benchmark compares 8, 32, and 128 aligned boxes as a chain of Unity fixed joints and as one compound rigid body. Both retain the same collider geometry. It uses separate physics scenes and does not change global simulation settings.
- The benchmark checks collider ray hits, spacing error, a compound body's contact with a floor, and momentum at separation. It reports raw repeated timings. These are synthetic bodies, not stock parts or KSP joints.
- A flight panel inventories the active vessel without modifying it. A small structural allowlist identifies inspection candidates; it does not approve them for merging.
- A read-only structural vessel census maps persistent part IDs and the parent graph to observed native body and joint IDs. It leaves semantic classifications unknown and reports omissions, abstentions or a rejected candidate rather than guessing that a live cluster is safe.
- An optional 300-frame timing capture records available Unity markers, frame context, and timing distributions. Unsupported markers are reported as unavailable, never as evidence of zero cost. An opt-in coast/powered/contact run exports a [qualification report](docs/qualification-report.md). With `--continuum-playerloop`, captures also bracket native physics and behaviour FixedUpdate subtrees. Installed writer captures and the [A023 bounded substitution](docs/live-substitution-canary.md) exercise dispatch and exact restoration; they do not qualify replacement dynamics or a speedup. See [profiling](docs/profiling.md).
- A flight input recorder exports step-keyed recordings plus event/trajectory observations; the format supports manual linear and cubic-Bezier edits. An opt-in analog player enforces a conservative control-ownership boundary; see [input timeline](docs/input-timeline.md).
- An optional MechJeb mission addon creates a separate sandbox and attempts a recorded stock Kerbal X flight to Minmus. See [mission operation and qualification](docs/minmus-mission.md). Checkpoint trials now provide repeatable setup and preserved evidence; they do not establish deterministic replay.
- An opt-in survey mode targets a sampled flat Minmus site in daylight. Two checkpoint trials landed upright with all 17 parts, approximately 120 m from the target, missing the unchanged 100 m criterion. This is a systems test fixture; see the [flight evidence](docs/validation.md).
- A pure C# model verifies aligned-box aggregation and impulse-free separation without proprietary assemblies.
- A [bounded background worker](docs/simulation-worker.md) accepts immutable batches with tick, topology, and frame identities. A reference constant-force backend and runnable serial/parallel comparison exercise the boundary without controlling KSP bodies.
- [Compiled execution views](docs/execution-views.md) preserve stable canonical entity identity while gathering system-specific dense columns, selecting eligible backends from explicit workload and measured-cost reports, and transactionally rejecting stale results.
- A portable [active-physics takeover canary](docs/active-physics-takeover.md) proves exclusive authority, before-image journaling, exact readback, compensated abort, and explicit indeterminate failure without writing to Unity or KSP. Separately, A023 proves bounded native-node interception and restoration in a live orbit. Live nonempty dynamics, body-graph takeover, and dynamic-state recovery remain unqualified.
- A portable [bounded live-dynamics candidate](docs/live-dynamics-canary.md) advances a vessel-sized six-degree-of-freedom rigid cluster under frozen acceleration. The A023 live skip receipt exposed deferred native impulse work, so the candidate is deliberately not wired to KSP until force ownership is explicit.
- A portable [program-branch kernel](docs/program-branches.md) content-addresses canonical snapshots, forks immutable histories, and atomically publishes exact-head authority transactions. Its 256-scenario toy verifies stable per-sample results under serial and parallel execution; it is in-memory infrastructure, not a durable save system or live KSP driver.
- An opt-in [read-only live control endpoint](docs/live-control.md) exposes capabilities and a bounded active-vessel snapshot over local loopback. Portable socket tests pass; installed KSP behavior and frame impact remain to be measured.
- An opt-in [flight shadow probe](docs/shadow-worker.md) captures real rigid-body arrays into the worker and rejects stale results. The flight panel can run either the independent-body baseline or a translational rigid-cluster strategy that preserves part offsets and shares mass-weighted linear motion, then compare the read-only prediction with the next matching stock observation. Rotation, aggregate native forces and a validated stock-trajectory model remain unavailable.
- The first accepted shadow snapshot also retains a bounded mapped inventory of public Unity joint links. A [structural shadow experiment](docs/structural-shadow.md) defines the controlled two-body impulse-response oracle and includes a portable held-out modal fitting kernel; complete constraint state, native excitation and trace capture remain the next increments.
- An opt-in [lifecycle trace](docs/lifecycle-trace.md) records named callback observations, body/force samples and context changes under one bounded session sequence. Its portable tests and native build qualify the source path; installed ordering and the flight-panel controls remain unverified.
- The opt-in qualification harness requests mission cancellation before process exit, preserving an `interrupted` outcome and separate cleanup receipts. Portable tests and native compilation cover this path; installed shutdown behavior still needs qualification.
- An [input comparison tool](docs/input-comparison.md) measures sampled differences between recorded control tracks without claiming equivalent physics outcomes.

Experiments run only through explicit panel actions or command-line flags. Analog replay changes flight controls. The optional mission runner creates its own save and uses MechJeb on its mission vessel; normal launches do not start a mission. Local reports/recordings go to `GameData/KspContinuum/PluginData/` and can include vessel identifiers, mod identities and mission save paths. They are runtime artifacts and are not committed to this public repo.

## Build

The private reference-program runner includes an [owned diagnostic docking roundtrip](docs/docking-situations.md), with pinned paired fixtures, stock merge/split evidence and inspected render captures.

It also includes [stock checkpoint reconstruction](docs/checkpoint-reload.md) and an opt-in [live sparse convection replacement](docs/live-convection.md), with complete boundary costs and bitwise same-state evidence.

Use a .NET 8 or newer SDK. Run the analytic tests without installing KSP:

```sh
dotnet run --project tests/KspContinuum.Tests -c Release
```

The repository owns the automated check contract through a Rust task runner:

```sh
cargo run --manifest-path tools/xtask/Cargo.toml -- portable
cargo run --manifest-path tools/xtask/Cargo.toml -- field
cargo run --manifest-path tools/xtask/Cargo.toml -- structural
cargo run --manifest-path tools/xtask/Cargo.toml -- all
```

The lane commands are independent of the CI runner. The structural lane downloads its digest-pinned Jolt
source into the ignored `artifacts/structural-ci` build directory.

To compile the addon, point `KSP_MANAGED` at the `Managed` directory in your own KSP 1.12.5 installation. On macOS this is inside `KSP.app/Contents/Resources/Data`; on Windows/Linux it is typically under `KSP_x64_Data`.

The addon compiles against Lib.Harmony for its opt-in aerodynamic recorder, but the package does not bundle
`0Harmony.dll`. The package command emits a sibling local `.ckan` file that declares the shared CKAN dependency
`Harmony2` and binds the archive by size and hashes.

```sh
export KSP_MANAGED="/path/to/your/KSP/Managed"
dotnet build src/KspContinuum.Plugin -c Release
cargo build --release --target x86_64-apple-darwin --manifest-path tools/native-boundary/Cargo.toml
dotnet run --project tools/KspContinuum.Tools -c Release -- package
```

The build reads game references but never copies them or installs anything. The local archive in `artifacts/` contains this project's plugin, the x86_64 Rust native boundary beside it for Unity Mono lookup, an internal SHA-256 manifest for that dylib, documentation and an example input track; mission packages add the optional project-owned mission DLL. Packaging rejects other Mach-O architectures because KSP 1.12.5 for macOS is x86_64. Local archive and sibling `.ckan` filenames include the archive's full SHA-256, and the metadata's default file URL points to that content-addressed archive so rebuilding cannot make CKAN reuse a stale cached URL. Its generated `.ckan` metadata uses the `0.2.0` qualification line, above the earlier `0.1.x` installed experiments. When CKAN runs in CrossOver or another environment that cannot resolve the host path, pass an absolute reachable `file`, `http`, or `https` URI with `--download-url`; an explicit URL keeps the requested `--output` filename because the caller owns that remote identity. The metadata remains local qualification metadata, not a published release or NetKAN record. The local experiment was installed through CKAN in an independent test copy; public release metadata and gameplay qualification remain incomplete. Do not deploy into an instance another process or agent is using.

For automated engine qualification in an owned test copy, launch its KSP executable with `-batchmode -nographics --continuum-bench`. At the main menu, the addon runs the isolated benchmark, writes its report, and exits with code 0 when all report checks pass or 1 on failure. An unsupported KSP version exits with code 2. Normal launches do not auto-run or exit. Headless execution does not verify the visible panels or live-vessel inspector.

## Run the experiments

The experiments are independent of the running game:

| Tool | What it measures |
| --- | --- |
| [Native structural fixture](docs/structural-benchmark.md) | Pinned Jolt spring motion against an analytic reference, substep cost/error, and a floor-contact check |
| [Contact checkpoints](docs/contact-checkpoints.md) | Uninterrupted contact/joint simulation versus full state restore and cold body reconstruction |
| [Orbital/event fixture](docs/orbital-fixture.md) | An external local Gimbal coast implementation against independent orbital states, plus bounded encounter-event adversaries |
| [Data-layout comparison](docs/layout-benchmark.md) | Object, SoA, and AoSoA captures under the same free-body and neighbor-reading kernels, with the shared six-phase performance observation contract |
| [Rust execution-view benchmark](docs/execution-view-benchmark.md) | Canonical-to-SoA packing and scalar, CPU-parallel, and Metal GPU integration crossover |
| [Native CPU throughput](docs/native-cpu-throughput.md) | Native AoS, optimized SoA, blocked, and persistent-multicore crossover with long timed windows |
| [KSP-to-Rust native boundary](docs/native-boundary-benchmark.md) | Architecture-compatible x86_64 P/Invoke cost, precision conversion, synchronous publication, and one/few-step latency |
| [Metal shader swap](docs/metal-shader-swap.md) | Matching-Unity shader substitution experiment in an isolated KSP copy; rendering and frame-time gains remain unproven |
| [Persistent execution views](docs/persistent-execution-views.md) | Generational identity, deterministic topology rebuilds, and full-pack versus dirty-row refresh crossover |
| [Hardware-conscious execution](docs/hardware-execution.md) | Cache locality, retained storage, SIMD, worker/GPU residency, prewarming and complete-cost profiling |
| [Runtime data design](docs/runtime-data-design.md) | Compiled vessel plans, replaceable execution, shared invariants, telemetry costs and CPU/GPU physics choices |
| [Rigid craft and bulk systems](docs/rigid-and-bulk-systems.md) | Rigid primary physics, optional wobble, locked-region transactions and real captured-workload Rust ECS comparison |
| [Rigid assembly motion boundary](docs/rigid-assembly-owner.md) | Aggregate mass/inertia, native compound ownership, real separation and first-command original KSP body snapshots |
| [Real craft physics import](docs/real-craft-physics.md) | Coherent native-body/part/shape packet, all-body momentum projection and compound native laboratory |
| [Compiled execution views](docs/execution-views.md) | Stable entity identity, dense per-system SoA batches, deterministic backend routing, and transactional publication |
| [Aerodynamic strategies](docs/aerodynamics.md) | Deterministic drag-cube projection with scalar and order-preserving parallel paths; a metamorphic baseline pending comparison with captured stock labels |
| [Gravitational trajectories](docs/field-trajectory.md) | Analytic and step-refined Plummer trajectories, conservation, reversibility and a potential-consistent split toy |
| [3D field gravity](docs/field-gravity.md) | Isolated softened forces via padded FFT/CIC against direct forces, with retained accuracy failures |
| [Modal rocket reduction](docs/modal-reduction.md) | Full spring-mass motion versus a gross mode and six exact flexible modes under smooth and localized loads |
| [Telemetry playback](docs/telemetry-playback.md) | Scrubbing and phase navigation over recorded mission observations, without rerunning KSP |

Native structural dependencies are fetched by pinned digest into ignored build artifacts. The orbital donor is supplied explicitly from a separate checkout; its implementation is not distributed here. Neither tool installs a backend into KSP.

## Research direction

The [Continuum and Gimbal boundary](docs/continuum-gimbal-boundary.md) now puts Gimbal's native Rust runtime and Space Race game first. This repository remains a KSP mechanics/performance reference and integration lab. KSP compatibility, Metal rendering, and live substitution are optional research paths with their own evidence gates.

The longer-term experiment is a shadow solver: capture a vessel's inputs, run a separate solver without controlling the vessel, and compare its outputs with stock. First measure whether rigid-body solving, KSP's force calculations, part callbacks, or another subsystem dominates. A native CPU solver, an external process, GPU compute, and [learned or stochastic candidates](docs/learned-compute.md) remain measurable strategies rather than assumed winners. [Aerodynamic strategies](docs/aerodynamics.md) define a stock/FAR oracle boundary, multiresolution geometry and the first truth-capture experiment. [Behavioral qualification](docs/aero-behavior.md) defines rocket, plane and reentry envelopes for stock, FAR and Realism Overhaul composition profiles.

Development advances along independent worker/backend, profiling, replay/input, and space-program/chronicle tracks. Missions provide workloads and acceptance evidence; landing precision does not gate unrelated simulation infrastructure. The current free-body worker proves a compute boundary, not complete force capture or a shadow vessel solver.

The optional `--continuum-part-forces` probe captures a bounded read-only Part census before integration, with provider-local epoch and explicit missing force channels. It is a component-observation candidate; installed ordering and mod compatibility remain unqualified.

The standalone [encounter planner](docs/encounter-scheduler.md) tests conservative stopping boundaries and separate horizons for quiet objects. A bounded [worldline tube toy](docs/worldline-tubes.md) adds physical support, approximation uncertainty, validity and expiry around those paths and compares screening with prescribed-curve oracles. Neither owns game time or resolves collisions.

See [interaction regimes and scheduling](docs/interaction-regimes.md), [integration map](docs/integration-map.md), [force-observation design](docs/force-observation.md), [experiment protocol](docs/experiment.md), [replacement architecture](docs/replacement.md), and [validation receipt](docs/validation.md), and [community integration boundaries](docs/compatibility.md). Work uses Git and GitHub on reviewable branches.

## Continuum Space Program

The [space program](docs/space-program.md) develops the toolkit through actual missions and preserves the results as a local multimedia chronicle. Its [naming conventions](docs/naming.md) distinguish missions, attempts, vehicle revisions and survey sites. Maintained [wiki templates](docs/wiki-templates.md) keep vehicle, site and experiment records consistent without rewriting old evidence.

Generate a [mission chronicle and connected website](docs/chronicle.md) from attempt telemetry, confirmed screenshots and a maintained program catalog. The site connects missions, attempts, vehicles, landing sites and experiments while preserving the original reports. Reports and game saves remain local; this public repository contains their tooling and templates.

The [local handoff fixture](docs/local-handoff.md) connects encounter planning, a local sphere solver, and atomic in-memory publication. It remains separate from live KSP ownership.

The [state-ownership contract](docs/ksp-state-ownership.md) maps integration responsibilities, proposes a read-only callback trace, and identifies recovery requirements before a live driver can publish simulation results. The [takeover canary](docs/active-physics-takeover.md) now exercises that proposed publication state machine against a portable driver.
