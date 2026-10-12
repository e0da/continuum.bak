# Persistent artistic trails

This independent workbench consumer owns emitted history, not live KSP propulsion. It draws bounded soft parcels beside the actual captured craft. The authored emitter starts at the scene vessel anchor plus three metres on the body-fixed X axis and moves at eight metres per simulated second along negative Z. It is an artistic model: no atmosphere, humidity, weather, exhaust chemistry, or contrail microphysics is inferred.

`PersistentTrailOwner` stores at most 4096 births in a ring (the workbench uses 1024). Each birth retains double position, simulated birth time, and the accumulated wind at birth. Its current position is birth position plus the subsequent integrated uniform wind. A wind change leaves existing positions unchanged and affects future motion only. Width is `sqrt(initialWidth² + 2*diffusion*age)` and opacity is `exp(-decay*age)`. The workbench defaults are width 0.5 m, diffusion 0.1 m²/s, decay 0.05/s, lifetime 30 s, and emission cadence 0.1 s. Lifetime expiry runs at each birth before oldest-live capacity eviction. Cadence events use an emission epoch and integer event index, rather than a render-frame accumulator.

Emission-off preserves history. Pause preserves time; reset clears history and simulation time but retains wind. Inputs are finite and bounded; backward time, excessive advance work, and invalid wind reject before mutation. At most four capacities of births may be processed in one advance. Long emitting intervals must be split deliberately. Renderer-origin changes never mutate body-fixed history.

## Existing socket controls

Load a real multiscale scene using the existing `CONTINUUM_MULTISCALE_SCENE`, `CONTINUUM_MULTISCALE_OUTPUT`, and part shader-bundle settings. Send newline-delimited commands through the existing workbench socket:

```
trail fixture
trail pause
trail step 1
trail wind 2 0 0
trail emission on
trail play 4
trail emission off
trail reset
trail remove
status
captureframe
quit
```

`fixture` atomically replaces the previous trail with three simulated seconds of emitted history, then pauses with emission off. `play` accepts 0.01–100 simulated seconds per wall second; `step` uses explicit simulated seconds. The live wall driver caps a single wall interval at 0.25 s, so suspension or slow frames do not create a catch-up teleport. Explicit steps retain their full requested duration. Captures freeze advancement and commands; shader/fixture HMR pauses the wall clock without clearing history, changing its generation, or emitting duplicate births. Existing static-render benchmarks reject an installed trail; `remove` returns their original workload. This is not a trail-performance qualification.

## Rendering and ownership

The consumer creates one reusable billboard mesh, one material, one command buffer, and two owned full-resolution render targets. It uploads cached vertex/color arrays only when history or the rendering origin/camera orientation changes. Transparent parcels sort back-to-front by current camera eye-depth with birth ID as the exact-depth tie breaker, using cold reusable arrays; the model's history ordering remains unchanged. No parcel GameObjects or per-frame materials are created. The clear-and-draw command batch is retained until the target/viewport lease changes; mesh and material inputs update in place. `commandBuilds` counts only those recordings. Native orbit/resize checks must still qualify the command buffer's consumption of updated inputs.

`preparationTicks` measures cold runtime construction. `simulationTicks` measures requested simulation advances, `uploadTicks` includes parcel reconstruction/sort/mesh publication, and `viewSetupTicks` includes targets/projection/uniform/command setup. `submissionTicks` covers only execution enqueue. `renderTicks` encloses the entire Render call, including upload and setup; `compositionTicks` covers the separate two-blit presentation. These are CPU wall envelopes at `clockFrequency`, not GPU execution or exclusive frame costs. Upload bytes estimate vertex/color payload and exclude driver allocations.

The transparent target uses explicit GPU projection into texture and positive eye-depth checks against both existing craft and body RFloat depth targets. It overlays the normal two-domain composition without changing either opaque owner's depth. Trail rendering is visible only in `view composed`; diagnostic craft/body views remain unchanged. Native-resolution IMGUI includes trail time/live/expiry/emission status. State includes oldest/newest body-fixed positions, wind, generation, upload counters, render count, and destruction-requested cleanup. The targets preserve `RenderTexture.active` on owned operations. Destroying the host releases the trail's command and target resources and requests mesh/material destruction.

## Build and checks

Copy the canonical `src/KspContinuum.Core/PersistentTrailOwner.cs` into the private Unity project's `Assets/PersistentTrailOwner.cs`, alongside the existing canonical Core copies. Copy the owned runtime/shader/workbench/builder sources through the established project staging route. `BuildAeroFieldWorkbench.Build` with `CONTINUUM_MULTISCALE_WORKBENCH_BUILD=1` retains the original trail shader and records runtime/Core/shader SHA256s. Existing pinned Unity2019 build/output settings and player invocation remain unchanged.

The portable check command is `dotnet run --project tests/KspContinuum.Trails.Tests`. Checks cover analytical wind/width/decay, future-only wind changes, split-step birth/expiry/capacity behavior, origin invariance, emission-off survival, expiry, and atomic invalid/work-bound rejection. Source authoring did not run builds or these checks; the supervising owner runs them.

Native gates remain: actual nonempty parcel pixels next to the captured craft; occlusion by craft and body; no UV inversion under admitted Metal/OpenGL projection; correct pause/emission-off/wind/warp/rebase readbacks; preserved history across shader HMR; graceful resource cleanup. Two resolving observations in one persistent process can cover visible/depth composition and changing-history/HMR behavior. No image fidelity, weather realism, runtime performance, or live KSP exhaust claim follows from portable tests.
