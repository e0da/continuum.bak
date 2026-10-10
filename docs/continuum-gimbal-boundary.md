# Continuum and Gimbal: shared direction, provisional boundary

**Direction, updated 2026-10-10:** Continuum and Gimbal pursue a shared goal: a fast, expressive spaceflight and simulation foundation that can support a Kerbal-like experience alongside Gimbal's toy and realism experiences. Continuum's KSP replacement program is one development path toward that goal. It may grow into a replacement for both KSP's runtime systems and Unity, then support further Gimbal capabilities.

The repository split is provisional. Findings and original components can move in either direction; useful invariants may be extracted into shared components, and a repository or runtime merger remains available. None of these structures is selected in advance. The earlier fixed hierarchy of Gimbal as the primary path and Continuum as an optional reference lab is superseded.

This direction does not establish a working standalone runtime or compatibility with arbitrary mods. `KspContinuum` remains the existing addon namespace.

## Experience contracts

The intended experiences can share mechanisms while choosing different behavior, fidelity and cost. Their exact supported workloads and acceptance tests are defined when a capability is selected.

| Experience | Contract to establish |
| --- | --- |
| Kerbal-like | Selected craft, flight, construction, staging, save/load and game behavior; stock/DLC/mod compatibility only for explicitly qualified profiles. |
| Gimbal toy | Deliberate simplified rules, responsive interaction and readable behavior at a declared performance budget. Simplification is a product choice, not accidental numerical error. |
| Gimbal realism | Declared physical models, numerical tolerances and supported regimes, with independent checks and measured complete-workload cost. |

A Kerbal-like experience need not require exact KSP compatibility. A compatibility claim needs its own oracle and scope. Realism and toy profiles need not use the same solver, timestep or game rules. Shared engine work must preserve these choices rather than forcing all experiences through one universal configuration.

## Current working placement

| Home | Current responsibility |
| --- | --- |
| Continuum | The private KSP laboratory, reconstruction, measured system replacement, portable experiments and candidate runtime services. A useful KSP outcome can justify this work without an existing Gimbal consumer. |
| KSP adapter | Host lifecycle, state translation, mod contracts, guarded authority acquisition, publication, readback and restoration. Unity/KSP objects remain adapter concerns. |
| Gimbal | Its existing native application, simulation and experience development. Its workloads can challenge, reuse or replace Continuum designs. |
| Shared components, when justified | A component with explicit ownership and a working consumer across the programs. Place it where development and integration are simplest; extraction is not a prerequisite for experimentation. |

Authored parts need not define compute rows, rigid bodies, render instances or asset residency. Definitions, committed gameplay state, solver state, disposable predictions, presentation views and history have distinct lifetimes. Each state domain has one authority, with explicit topology, coordinate-frame and time identities at transitions. The [one-writer and transactional publication rules](active-physics-takeover.md) apply during live substitution.

These are candidate shared invariants, not a commitment to one data layout, scheduler or solver. Validate each against actual consumers. Keep complete costs of capture, conversion, synchronization and publication visible.

## Replacement and convergence paths

The addon supports observation, comparison, substitution and fixture export. Whole managed reconstruction, modern Unity migration and an independent runtime are also available paths in the [research program](ksp-research-program.md). They can compete or supply components to one another.

For each selected capability:

1. Capture the incumbent behavior and cost, or define an independent oracle when the experience intentionally differs.
2. Give the replacement owned inputs and state; establish its full behavior and performance contract.
3. Run it in shadow mode or a disposable standalone slice, then transfer authority where live integration requires it.
4. Qualify a usable checkpoint including the relevant controls, rendering, save/load and restoration behavior.
5. Reuse findings or code where another experience benefits. Extract or merge when that resolves an actual ownership or integration problem.

Subsystem independence is claimed only for behavior that runs without the incumbent. Complete KSP/Unity replacement requires a playable standalone slice and progressively broader coverage; removing one callback or assembly does not establish it. The current measured rendering replacement remains the next performance target. Long-term convergence does not require another launch or benchmark before continuing that work.

Harmony patches, Unity components, MFI integration and incumbent mod hooks remain host integration concerns described by [replacement](replacement.md), [state ownership](ksp-state-ownership.md), [integration](integration-map.md) and [compatibility](compatibility.md). Their existing shapes do not define the eventual engine architecture.

## Open questions

- Which state, asset, rendering, time and simulation contracts survive use by more than one experience?
- Which capabilities are best developed by replacing KSP systems, extending Gimbal directly, or comparing both implementations?
- Would shared components, a common runtime or a repository merger reduce actual integration cost? There is no decision yet.
- Which binary mod contracts can be supported, and which require adapters, ports or replacement implementations?
- What exact artifact could be shared? Reconstructed game code, assets and rebuilt distributions remain private; publication is a separate artifact-specific decision.
