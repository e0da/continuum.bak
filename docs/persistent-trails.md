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
viewport 1280 720
status
captureframe
quit
```

`fixture` atomically replaces the previous trail with three simulated seconds of emitted history, then pauses with emission off. `play` accepts 0.01–100 simulated seconds per wall second; `step` uses explicit simulated seconds. The live wall driver caps a single wall interval at 0.25 s, so suspension or slow frames do not create a catch-up teleport. Explicit steps retain their full requested duration. Captures freeze advancement and commands; debug-shader HMR pauses the wall clock without clearing history, changing its generation, or emitting duplicate births. A successful full scene/fixture-generation reload invalidates the old authored trail and removes it; failed reload preserves the previous owner. Existing static-render benchmarks reject an installed trail; `remove` returns their original workload. This is not a trail-performance qualification.

Top-level `trailInstalled` is the authoritative installation flag, because Unity JSON can serialize a null nested class as a default-looking object. It becomes true only after a successful fixture commit and false on removal, committed scene reload, or cleanup. `viewport WIDTH HEIGHT` requests a windowed resolution, accepting integer dimensions 320–4096 by 240–4096 with at most 16,000,000 pixels. Resolution changes are asynchronous: requested dimensions are recorded separately from actual `width`/`height` and render-target sizes. The control preserves trail history and replay state and is rejected during captures/benchmarks. Only actual changed dimensions and command-lease counters prove a completed resize.

## Rendering and ownership

The consumer creates one reusable billboard mesh, one material, one command buffer, and two owned full-resolution render targets. It uploads cached vertex/color arrays only when history or the rendering origin/camera orientation changes. Transparent parcels sort back-to-front by current camera eye-depth with birth ID as the exact-depth tie breaker, using cold reusable arrays; the model's history ordering remains unchanged. No parcel GameObjects or per-frame materials are created. The clear-and-draw command batch is retained until the target/viewport lease changes; mesh and material inputs update in place. `commandBuilds` counts only those recordings. Native orbit/resize checks must still qualify the command buffer's consumption of updated inputs.

`preparationTicks` measures cold runtime construction. `simulationTicks` measures requested simulation advances, `uploadTicks` includes parcel reconstruction/sort/mesh publication, and `viewSetupTicks` includes targets/projection/uniform/command setup. `submissionTicks` covers only execution enqueue. `renderTicks` encloses the entire Render call, including upload and setup; `compositionTicks` covers the separate two-blit presentation. These are CPU wall envelopes at `clockFrequency`, not GPU execution or exclusive frame costs. Upload bytes estimate vertex/color payload and exclude driver allocations.

The transparent target uses explicit GPU projection into texture and positive eye-depth checks against both existing craft and body RFloat depth targets. It overlays the normal two-domain composition without changing either opaque owner's depth. Trail rendering is visible only in `view composed`; diagnostic craft/body views remain unchanged. Native-resolution IMGUI includes trail time/live/expiry/emission status. State includes oldest/newest body-fixed positions, wind, generation, upload counters, render count, and destruction-requested cleanup. The targets preserve `RenderTexture.active` on owned operations. Destroying the host releases the trail's command and target resources and requests mesh/material destruction.

## Build and checks

Copy the canonical `src/KspContinuum.Core/PersistentTrailOwner.cs` into the private Unity project's `Assets/PersistentTrailOwner.cs`, alongside the existing canonical Core copies. Copy the owned runtime/shader/workbench/builder sources through the established project staging route. `BuildAeroFieldWorkbench.Build` with `CONTINUUM_MULTISCALE_WORKBENCH_BUILD=1` retains the original trail shader and records runtime/Core/shader SHA256s. Existing pinned Unity2019 build/output settings and player invocation remain unchanged.

The portable check command is `dotnet run --project tests/KspContinuum.Trails.Tests`. Checks cover analytical wind/width/decay, future-only wind changes, split-step birth/expiry/capacity behavior, origin invariance, emission-off survival, expiry, and atomic invalid/work-bound rejection. Source authoring did not run builds or these checks; the supervising owner runs them.

The private matching-2019 Metal consumer has rendered visible parcels beside the captured craft and Kerbin. Exact live readbacks establish emission-off persistence, future wind without teleporting, analytical two-second transport, view-origin history preservation, advancement in 4x mode, lifetime expiry and debug-shader HMR history. A viewport change from 1920x1080 to 1280x720 preserved the clock/history and rebuilt the retained command batch once without another history upload. Rejected viewport inputs preserved the admitted request. Shutdown acknowledged the reply, requested owned resource destruction, restored host settings/hooks, and exited.

Native craft/body occlusion, general Metal/OpenGL UV orientation and GPU-fence completion remain unqualified. The body-hidden scene's parcel radius was only 0.000679 pixels, so its absence cannot prove GPU occlusion. Visible composition and readbacks establish neither weather realism nor live KSP exhaust integration, exact wall-time warp rate, visual fidelity or runtime performance.

## Resolvable synthetic depth and UV probe

In a static composed view without a bound replay, `trail probe on` temporarily replaces only trail draw parcels/depth inputs with a fixed witness. It keeps the same mesh, retained clear/draw commands, shader occlusion pass, radial alpha, and final overlay pass. Regular history stays frozen and unchanged. Scene/camera/history controls and automatic scene reload freeze; status, capture, viewport, quit, probe-off and removal remain available. `trail probe off` restores normal drawing and the previous paused/emission/time/generation values, recording `historyRestored` and resetting the wall clock. Probe state has an authoritative `probeInstalled` boolean independent of nested JSON defaults.

Two actual uploaded 64×64 RFloat textures use Point/Clamp/no mips, zero for no geometry. Craft depth is 10 m in texture-UV rectangle `[.125,.375) × [.25,.75)`; body depth is 20 m in `[.625,.875) × [.125,.625)`. The probe background samples these same textures: craft occupancy is dark red, body occupancy dark blue. It does not encode expected parcel outcomes or bypass the real occlusion comparisons.

| Parcel | Texture UV center | Eye depth | Expected center |
| --- | --- | --- | --- |
| Green | (.25,.375) | 8 m | Visible before craft |
| Red | (.25,.625) | 12 m | Hidden by craft |
| Cyan | (.75,.25) | 18 m | Visible before body |
| Yellow | (.75,.5) | 22 m | Hidden by body |
| Magenta | (.5,.875) | 15 m | Visible, upper empty region |
| White | (.5,.125) | 15 m | Visible, lower empty region |

UV is normalized from the bottom left; a top-left PNG center is `(u*width,(1-v)*height)`. Billboard radius is 4.5% of image height, rebuilt from actual FOV/aspect so centers remain resolvable after resize. Prefer at least 1280×720 to keep HUD text away from center neighborhoods. Metadata records actual camera-relative centers, measured eye depths, row-major view/GPU-projection matrices, texture IDs/format/regions, FOV/aspect, viewport, RGBA inputs and expected visibility. A full-viewport PNG should show four saturated visible centers and only dark domain backgrounds at hidden red/yellow centers. Asymmetric region heights plus magenta/white vertical ordering reject a flipped UV path. Small center neighborhoods avoid soft edges and UI text.

This synthetic witness qualifies only these explicit positive-depth/UV inputs on the actual admitted backend. Captured scene-depth correctness, transparent scene occluders and broader atmospheric effects remain separate gates. It is not a physics or performance observation.
