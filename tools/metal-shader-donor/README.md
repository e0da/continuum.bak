# Matching-player shader bundle builds

This project builds macOS bundles with Unity **2019.4.18f1, revision 3310a4d4f880**. It can target the existing KSP OpenGL player without grafting serialized game assets. The editor checks its actual full version before building.

Put original shaders and includes in `Assets/ContinuumShaders`. Existing private Metal donor sources can remain in `Assets/MetalShaders`. Generated project caches, bundles, installer packages and proprietary shader inputs stay outside Git.

Run the matching editor with `-batchmode -nographics -quit -projectPath <absolute-project> -buildTarget StandaloneOSX -executeMethod BuildShaderBundle.Build -logFile <private-log>` and these environment variables:

| Variable | OpenGL experiment value |
|---|---|
| `CONTINUUM_SHADER_GRAPHICS_API` | `OpenGLCore` |
| `CONTINUUM_SHADER_SOURCE_ROOT` | `Assets/ContinuumShaders` |
| `CONTINUUM_SHADER_BUNDLE_NAME` | `continuum-opengl-aero` |
| `CONTINUUM_SHADER_REQUIRE_INSTANCING` | `1` for shaders with `#pragma multi_compile_instancing` |
| `CONTINUUM_SHADER_VARIANTS_FILE` | Optional absolute private TSV: exact imported shader name, exact `PassType`, and space-separated keywords in three tab-separated columns. Empty keyword column is allowed. Only declared tuples are retained; omission preserves existing cooking behavior. |
| `CONTINUUM_SHADER_BUNDLE_OUTPUT` | Absolute, existing, empty private directory |

The build retains an explicit `INSTANCING_ON` variant collection, disables automatic graphics API selection, performs a strict fresh LZ4 bundle build, and writes `build-receipt.json` with shader names, retained pass/keyword tuples and input/output SHA-256 hashes. Vertex/fragment shaders use `PassType.Normal`; surface shaders retain `ForwardBase` with `DIRECTIONAL` and, when `addshadow` is present, `ShadowCaster` with `SHADOWS_DEPTH`. Other required pass combinations must extend this contract before use. Metal remains the default for existing donor builds.

To verify compiler-error detection once, run the editor with `-executeMethod BuildShaderBundle.VerifyCompilerFailure` and `CONTINUUM_SHADER_PROBE_REPORT` pointing to a new absolute JSON path. This imports one intentionally invalid temporary shader, requires a compiler error from `ShaderUtil.GetShaderMessages`, and removes the asset afterward. Its intentional error in the editor log is expected. It does not produce a runtime bundle.

The explicit variant input is limited to 64 unique tuples and 64 KiB, rejects numeric pass aliases and unknown shader identities, and is hashed before and after cooking. Its path, digest and retained tuples appear in the build receipt. Choose tuples from the actual material consumer's keyword and pass observations; this option does not infer blend state, render queue or shader equivalence.

A successful bundle build proves compilation and provenance. The KSP consumer must separately verify the bundle hash, shader support, material binding, actual instanced execution and rendered behavior. A retained variant is not proof that the player physically executes it.

The official matching editor installer is [Unity 2019.4.18f1 for macOS](https://download.unity3d.com/download_unity/3310a4d4f880/MacEditorInstaller/Unity-2019.4.18f1.pkg). Existing activation must work in the batch editor; this tooling does not modify licensing or accept new terms. Ops/Actions use `/Applications/Unity/Hub/Editor/<version>/Unity.app` when an editor is installed there. An extracted private research editor may be invoked by its explicit path.

## Persistent aero workbench and hot reload

`BuildAeroFieldWorkbench.Build` builds a standalone development player with the original aero-field shaders supplied separately. Set `CONTINUUM_AERO_WORKBENCH_PLAYER` to a new absolute private `.app` path and `CONTINUUM_SHADER_GRAPHICS_API=OpenGLCore`, then invoke the matching editor with the build arguments above and this execute method. The player build retains the original `Continuum/FixtureOpaque` visible and depth passes and writes an adjacent `.build.json` receipt. It needs no KSP assemblies.

Run the built player with `-force-glcore -screen-width 960 -screen-height 540 -screen-fullscreen 0` and these variables:

| Variable | Contract |
|---|---|
| `CONTINUUM_AERO_WORKBENCH_OUTPUT` | New absolute private directory for status and images |
| `CONTINUUM_AERO_WORKBENCH_BUNDLE` | Absolute built bundle with the five `Continuum/AeroField*` shaders |
| `CONTINUUM_AERO_FIXTURE` | Optional absolute exported `manifest.json`; omitted selects labelled analytic geometry |
| `CONTINUUM_AERO_PUBLICATION` | Optional absolute cook-service `current.json`; it must already exist at startup |
| `CONTINUUM_AERO_DEBUG_PORT` | Optional loopback port; omitted or `0` selects an available port |
| `CONTINUUM_AERO_SMOKE_FRAMES` | Optional `3..300`: render, wait for splash completion, capture the screen and native fields, then exit |

`status.json` exposes process/host identity, chosen port, current generation, reload counts, camera pose, flow, intensity and effect clock. Send one newline-terminated text command per loopback TCP connection. Supported commands are `status`, `pause`, `resume`, `setflow x y z`, `setintensity value`, `cameraorbit yaw pitch distance`, `bundlehotreload ABSOLUTE_PATH`, `fixturehotreload ABSOLUTE_MANIFEST`, `captureframe`, and `quit`. Commands are bounded to 4096 bytes and queued onto the main thread; the queue is bounded to16. Rendering uses in-process textures/uniforms. JSON belongs to control and evidence, not per-frame geometry transfer.

For automatic shader HMR, start one persistent matching editor with `CONTINUUM_AERO_COOK_ROOT` set to a new private directory. Use `-batchmode -nographics -projectPath <absolute-project> -buildTarget StandaloneOSX -logFile <private-log>`, with no `-quit` and no execute method. The editor watcher coalesces changes under `Assets/ContinuumAeroField`, cooks a uniquely named immutable bundle, and atomically publishes `current.json` only after successful compilation and stable source checks. The running workbench polls that publication once per second. Changes to shader files and their includes need no player or KSP restart. Parameters change immediately through the socket; frozen geometry reloads independently.

Candidate shader/material sets are checked before replacing the current set. Compiler failures leave the last publication intact and write `last-error.json`; invalid runtime candidates leave the last working resources active. Camera, flow, intensity, pause and clock ownership stay in the host across reloads. A manual bundle with a colliding Unity bundle identity rejects without discarding the current set; use unique generation identities. C# host code changes still require rebuilding the workbench. Stateful executable-code reload is a separate capability.

Each cook stages the original shader/include sources under a fresh generated asset path without copying Unity metadata. Fresh asset GUIDs isolate failed compiler state from a later repaired generation. Source hashes must remain stable through copying, building and generated-source cleanup before publication. Keep this editor dedicated to the cook service: it owns the OpenGL target and bundle/instancing environment settings. `last-error.json` records the most recent failure and may remain after recovery; `current.json` is the accepted generation.

Cooker C# changes require editor-assembly recompilation too. The pinned batch editor was observed retaining an old `Library/ScriptAssemblies/Assembly-CSharp-Editor.dll` across source imports and restart. Stop the owned editor, preserve and invalidate that derived cache, and require a new compiled assembly before qualifying tool changes. Build receipts record the actual loaded tool assembly hash separately from source-input hashes. Before each forced shader reimport the cooker clears prior diagnostics, then keeps both import and strict bundle-build error checks; clearing messages after compilation would erase fresh failures.

`AeroFieldFixtureCapture` exports bounded, origin-relative opaque meshes and provenance from a disposable KSP flight into `geometry.bin` plus `manifest.json`. It captures geometry and effect parameters, not live gameplay or original materials. Keep both files private. The Rust laboratory retains the pair with run/PID/vessel and hash checks, including rejected field experiments.

The KSP treatment is selected by `--flight-aero-field true --flight-aero-field-bundle <absolute-bundle>` in the laboratory runner. It owns stock effect rendering exclusively; stock aero state, heating and physics remain active. Ordinary part batching can coexist. Stock aero batching, retained-part ownership, disabled effects, hidden craft and quality overrides reject this combination. Native camera execution, finite occupied fields, same-frame publication and restoration are required evidence; successful compilation alone cannot qualify appearance or speed. Stop the cook editor and workbench before timed flight comparisons.

## Isolated backend and pass measurements

For the resident material experiment, copy the exact `src/KspContinuum.Core/ResidentMaterialFormat.cs` into `Assets/ResidentMaterialFormat.cs` in a private generated project before building. The player receipt binds both that shared format and `ResidentMaterialRuntime.cs`. Set `CONTINUUM_RESIDENT_PART_BUNDLES=1` for `BuildAeroBackendExperiment.Build` to cook separate part bundles for both APIs. Those bundles retain non-instanced and instanced directional, captured SH and shadow variants; the field bundle has independent ownership.

Instancing bundle builds explicitly select the pinned editor's `Keep All` stripping enum before cooking and restore the previous project setting afterward. The receipt binds the effective enum value and name; listing a variant in a collection alone did not keep runtime-created part materials visible in the native consumer. The part surface shader requires target 3.5. `CONTINUUM_SHADER_GENERATED_SOURCE_OUTPUT=<new absolute directory>` optionally exports generated shader passes for inspecting the instance setup path; this is generated source evidence, not compiled GPU instructions or rendering proof.

Capture a private fixture with the owned helper's `CONTINUUM_RESIDENT_MATERIAL_FIXTURE` set to a new absolute directory and part batching enabled. The producer uses admitted prepared material bindings and successful texture-image publications, with no GPU readback. `continuum-resident-material-fixture/v2` contains a cold manifest, shared source-local meshes, exact raw texture mips and a packed topology-bound snapshot. Reconstructed game data stays outside Git. The producer uses the existing `ReportJson` encoder because this player does not reliably serialize nested arrays from external plugin assemblies through `JsonUtility`.

Run with `CONTINUUM_AERO_FIXTURE=<manifest>` and `CONTINUUM_RESIDENT_PART_BUNDLE=<matching bundle>`. The material fixture selects geometry mode. `materialmode reference|instanced` compares full-affine individual draws with grouped instancing. `snapshotreload ABS_PATH` admits only a newer generation with identical topology, leaving meshes/textures/materials resident. `fixturehotreload` constructs and admits a complete replacement before retiring the old owner; a rejected candidate retains the previous state, camera and settings. These commands reject during a benchmark. Status and frozen benchmark controls bind the material mode, topology, snapshot generation, state and bundle hashes. Hot state contains binary numeric rows; JSON is only a cold/control/evidence format. The native consumer establishes the research shader's reference/packet behavior, not stock shader, gameplay or whole-KSP equivalence.

`materialdiagnostic off|defaults|single` provides bounded draw-path controls: defaults omits property blocks/probes, and single submits one instance per call with captured inputs. Both isolate failures without changing the immutable fixture. Benchmark admission requires diagnostics off. Submission occurs once per frame at the view camera's `OnPreCull`; scene depth occupancy is only a coverage witness, and reference/packet image comparison remains a separate gate.

`BuildAeroBackendExperiment.Build` builds Metal and OpenGL bundles from stable shared sources, then one x86_64 player supporting both APIs. Set `CONTINUUM_AERO_BACKEND_BUILD_ROOT` to an empty absolute private directory and invoke the matching batch editor with `-executeMethod BuildAeroBackendExperiment.Build`. Its final `build.json`, bundle receipts and player receipt bind source and actual loaded/built assemblies. Run that same player with `-force-metal` or `-force-glcore`, selecting the corresponding direct bundle and a new output directory. Stop all owned editors/builds and the other player before measuring.

After splash completion, use `pause`, `setclock 0`, identical camera/flow/intensity commands, then `benchmark 2 10` for two seconds of warmup and ten seconds of capture. The benchmark temporarily disables VSync/frame capping and freezes the effect clock; it restores prior settings afterward. During capture it suppresses publication/fixture polls and evidence-file writes. Avoid socket requests until the capture ends: capture-time commands invalidate the result. `cancelbenchmark` or `quit` exports an interrupted result and restores the controls.

Each private benchmark JSON retains consecutive monotonic frame intervals and rendered-frame witnesses, frozen controls, resolution/quality/color space, geometry counts, fixture/bundle/actual assembly hashes and explicit completed/invalid/interrupted status. The first boundary only anchors timing; the deadline straddler is omitted. CPU/RSS evidence has a separate, slightly wider process window, and unsupported process counters remain explicit. These are whole-frame wall intervals, not GPU durations.

`rendermode full`, `fields` and `geometry` select the full effect, geometry plus shared fields without sheath/ribbons, or geometry plus main depth without shared fields/effects. These are deliberately different treatments. A captured craft uses static gray materials and separate MeshRenderer submissions; it does not reproduce KSP's admitted packet renderer, lighting, world or gameplay. Backend wins here motivate broader reconstruction, not a predicted game FPS.

The shared field contract maps raw render-texture UVs to GPU clip space using the pinned `UnityCG.ComputeGrabScreenPos` convention and its inverse. Metal and OpenGL use different texture origins; explicit GPU projection conversion is required. [Unity platform conventions](https://docs.unity3d.com/2019.4/Documentation/Manual/SL-PlatformDifferences.html) and [GPU projection matrices](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/GL.GetGPUProjectionMatrix.html) describe these differences. Inspect asymmetric native images and field alignment on both APIs; positive pixel counts alone cannot prove orientation.
