# Resident assets, stock material behavior and measured integration

The independent matching-2019 runtime renders a captured KSP mapped material family with resident meshes/textures and a separate binary visual snapshot. Its original mode now uses the shipped serialized shader and exact original material bindings. Reference and instanced modes use the reconstructed shader. JSON carries cold manifests and debug controls; geometry, textures and changing visual state use binary payloads.

The mapped-only recipe uses `Continuum/FlightPartMappedInstanced`: StandardSpecular/GGX lighting, AG normal packing, RGB specularity, smoothness, burn/vertex albedo modulation and alpha-weighted thermal, rim and texture emission. It retains stock ambient/underwater inputs. Other families retain their previous research shader. This is bounded reconstruction; underwater, captured reflection textures, broader shadow/lighting conditions and arbitrary mod inputs remain unqualified.

## Data and original-shader ownership

`continuum-resident-material-fixture/v2` keeps geometry and texture payloads immutable. Prepared `materials` and optional `originalMaterials` share material indices and deduplicated texture descriptors. Equivalent original material clones may share a prepared packet; their complete declared recipes must match, including shader, queue, typed properties and texture transforms. Distinct recipes reject. Current geometry admits absent or uniformly white vertex colors; richer color data requires a new wire.

Optional `shaderGlobals` preserves the eleven selected stock uniforms. Numeric legacy values remain supported. `valueBits` carries exact IEEE-754 bits without JSON float conversion, with one encoding per row. NaNs reject; infinities are limited to declared underwater distance scalars and the unused fourth component of `_LocalCameraPos`. The actual captured infinity was `_LocalCameraPos.w`; the recovered shader reads XYZ. Materials cannot use this raw-global encoding. `pending-manifest.json` is diagnostic evidence; only final `manifest.json` signals successful validation and payload publication.

`ShaderGraft transplant` creates a private loadable donor bundle from a shipped shader. It traverses a bounded shader-only dependency graph across source-local serialized files and Unity builtin resources, remaps references, and verifies all other shader bytes plus untouched donor objects. Unsafe/missing paths, external roots and nonshader dependencies reject. Its receipt binds every source file, object mapping and output hash. Game shaders, source programs, class databases and derived bundles stay private.

The workbench accepts `CONTINUUM_RESIDENT_ORIGINAL_BUNDLE` and `materialmode original|reference|instanced`. Original mode requires the actual original table and shader. Snapshot reload preserves topology/generation checks. Fixture reload preserves its camera and mode; shader/global leases restore prior state on disposal. This is an in-process matching-2019 x86 player under Rosetta, not a native ARM64 game reconstruction.

## Flight controls and cost

The laboratory runner adds `--flight-part-mapped-only true` and `--flight-part-reference true`, requiring part batching. Reference mode captures the same cold inputs but leaves original rendering intact: no hot publication, camera callbacks, suppression or replacement draws. Reference cannot enable retained ownership. The replacement uses the same shader bundle and helper and selects only the mapped family.

Capture-boundary counters include publication, property-block reads, matrix/bounds work, probe calculation, main-camera callbacks and restoration. Main-camera cost includes publication and submission/suppression. The publication subsets overlap it; do not add them. Restoration is separate. Normalize window costs by completed Probe frame intervals, and per-publication work by `windowUpdateCalls`. Cumulative camera/draw counts also contain warmup and screenshots. These counters do not measure GPU execution or hypothetical RPC transport.

## Bounded observed outcome

Five native states—cold, thermal, rim, emissive and zero alpha—passed the frozen original/instanced image gate. Supported-family silhouettes matched exactly across 1,746 pixels. Mean channel differences were 0.020–0.060 levels out of 255; p99 was at most 2. Full-frame errors remain in the private receipt. The fixed development watermark is excluded only from the supported-family mask. Thermal, rim and emission changed actual pixels; zero-alpha inputs reproduced the cold image.

One same-helper stock/candidate pair used the quiet 256-part vacuum checkpoint at 1080p, normal time, through ten simulated seconds. It replaced 156 mapped renderers with five packets per main camera and restored all admitted originals without faults.

| Measurement | Stock reference | Instanced candidate |
| --- | ---: | ---: |
| Mean FPS | 72.45 | 76.75 |
| Mean frame | 13.803 ms | 13.028 ms |
| p95 / p99 | 20.301 / 22.863 ms | 19.085 / 22.710 ms |
| Simulation rate | 1.0021x | 1.0032x |
| Controller capture/draw/restoration | 0 | 0.615 ms/frame |

The observed mean-frame reduction is 5.61%; one pair has no confidence interval and does not qualify playable performance. Candidate publication costs 0.536 ms/frame, including property reads 0.069, matrix/bounds 0.085 and probes 0.008. The remaining 0.374 ms contains material audits, packing and other publication work; it is not an independently attributed audit cost. Three failed cold exports remain excluded: clone identity, JSON nonfinite values, and an initially incorrect nonfinite-field admission. All original launch/flight and shader-source inputs were restored or verified unchanged; owned processes exited.

The larger rendering boundary remains the next target. Candidate PostLateUpdate was 5.160 ms/frame, including FinishFrameRendering 4.419; native physics was 1.008 ms/frame. These are nested wall phases. The laboratory's visible research panel also remained enabled in both arms, so isolate its cost before assigning the remainder exclusively to game rendering. Move toward a resident vessel/scene consumer and explicit visual-state ownership, using this stock oracle and binary contract. Do not extrapolate this small material family to whole-game Metal, ARM64, atmosphere, warp or gameplay.
