# Captured multiscale workbench

This opt-in consumer loads a `continuum-multiscale-scene/v1` produced from an actual vessel and its actual scaled celestial-body mesh. It does not substitute procedural planet/craft geometry. Body texture bytes, sampler metadata, source transform and the unchanged resident vessel subfixture are digest-bound. The body shader is an explicitly approximate diffuse recipe, not stock atmospheric appearance.

The resident v2 geometry wire omits vertex colors: its producer admits only absent or exactly white source colors. The consumer therefore uploads explicit RGBA white vertex colors once, preserving that semantic for shaders that multiply albedo by `COLOR`. It does not rely on a backend's value for a missing vertex attribute. Nonwhite source colors require a richer admitted representation rather than this normalization.

Copy the canonical `ResidentMaterialFormat.cs` and `MultiscaleSceneFormat.cs` from Core into the private project's `Assets`, along with this tool's updated workbench/runtime/editor files and `ContinuumMultiscale` shaders. Build with the existing `BuildAeroFieldWorkbench.Build` method, declaring `CONTINUUM_MULTISCALE_WORKBENCH_BUILD=1`, `CONTINUUM_SHADER_GRAPHICS_API=Metal,OpenGLCore`, and `CONTINUUM_AERO_WORKBENCH_PLAYER=<new absolute .app>`. The build serializes references to the three original presentation shaders and both diagnostic shaders, and binds the actual player assembly and both copied Core files.

Launch that player with `-force-metal` and:

- `CONTINUUM_MULTISCALE_SCENE=<absolute captured scene manifest>`
- `CONTINUUM_MULTISCALE_OUTPUT=<new absolute output directory>`
- `CONTINUUM_RESIDENT_PART_BUNDLE=<qualified matching part shader bundle>`
- Optional `CONTINUUM_RESIDENT_ORIGINAL_BUNDLE=<original shader oracle bundle>`

The aero workbench bootstrap declines this explicit scene mode. No field bundle or automatic smoke exit is needed. The independent consumer uses an ephemeral loopback port recorded in `multiscale-status.json`, with the established newline-delimited bounded workbench control pattern:

```
status
zoom 2
orbit 25 10
focus OBJECT_ID
preset high
override OBJECT_ID 0.5 1 2
captureframe
quit
materialmode reference|instanced
view composed|craft-color|body-color|craft-depth|body-depth
view craft-flat|craft-world-normals|craft-view-normals|craft-albedo|craft-vertex-colors|craft-uv|craft-material-id
view body-flat|body-world-normals|body-view-normals|body-albedo|body-uv|body-material-id
view craft-linear-depth|craft-log-depth|body-linear-depth|body-log-depth
focusinstance CAPTURED_INSTANCE_ID
debugshaderreload ABSOLUTE_NEW_DIAGNOSTIC_BUNDLE
fixturehotreload ABSOLUTE_SCENE_MANIFEST
snapshotreload ABSOLUTE_NEWER_SNAPSHOT
```

Zoom is log10 metres; asset override arguments are world scale, mip bias and requested geometry error in final display pixels. UI also provides continuous logarithmic distance and three presets. The scene manifest is watched once per second. Candidate preparation/activation precedes retirement; rejected scene/snapshot generations retain the previous resident owner. Hot reload preserves logical object identities, focus, camera distance and policy. Controls freeze during end-of-frame capture and wait for the splash screen to finish. Status binds scene/geometry/texture/shader inputs, actual backend/assembly, effective world/shadow policy provenance, camera anchors, clip ranges, dimensions, submission counters, PNG digest and cleanup scope.

The craft constructor removes its original float root translation **before** combining root orientation with local part matrices. Each frame subtracts authored camera and object anchors in doubles before publishing GPU float translations. The body mesh's authored affine offset is similarly combined before conversion. Craft and body cameras share orientation/FOV/aspect but have separate bounded near/far planes and separate world-scale targets. Original positive linear-depth copies are compared during composition. IMGUI follows at native display resolution.

First admission is opaque-only: translucent vessel states and body/craft layer collisions reject. Directional shadow maps are disabled; shadow policy requests and geometry-error requests are reported unsatisfied. No geometric hierarchy, skinning, detailed terrain, impostor, solar-system content, atmosphere/ocean or temporal history exists here. Selective supersampling/crop is explicitly rejected pending a qualified coverage compositor. Different target resolutions retain only one nearest opaque depth per pixel; cross-domain silhouette coverage is a remaining fidelity gate.

Source/portable checks do not qualify GPU execution. The root-owned native observation must show the actual craft and actual body at two resolving camera ranges, correct orientation/relative placement and occlusion, plus sharp native UI at a changed world scale. Inspect the recorded native images and depth conventions on each backend. This workbench is not a KSP FPS comparison or an arm-to-solar-system completion claim.

The fixture is an admitted material-family subset, not whole-craft coverage. Native status records its family and instance/mesh/texture counts. Geometry diagnostics deliberately use one unlit DrawMesh per actual instance, with the same immutable mesh and current camera-relative affine transform. These views isolate geometry and texture inputs without relying on the normal part shader or instancing variants. MainTex uses the captured texture and scale/offset; material-ID hue is stable by ordered captured material index. Craft COLOR is the admitted explicit-white semantic; body COLOR was not captured and its vertex-color view rejects rather than fabricates values. Normal views encode normalized world/view components from [-1,1] to [0,1]; zero normals encode neutral gray. UV displays fractional raw UV in RG. Readable depth uses the current clip near/far or log(1+metres)/log(1+far), with zero reserved for unwritten pixels. Raw depth remains available and clamps at display conversion.

The built-in `Continuum/MultiscaleDiagnostic` shader is retained by the player builder. A matching shader bundle can replace only cached debug materials with `debugshaderreload`; exact supported shader identity and MainTex/Mode/MaterialColor interface are required. Preparation and hashing finish before the frame-boundary swap; rejected bundles preserve the prior shader/materials. Geometry, captured textures, pose state, camera and socket remain resident. Status binds bundle SHA and reload generation. Returning to composed/color/depth views disables debug geometry without modifying normal materials or global shader inputs. Native appearance, HMR recovery and cleanup remain qualification gates; these diagnostics carry no fidelity or performance claim.

Single-level body captures report `unsupported-single-level` for mip policy: assigning a mip bias cannot select absent coarser levels. Multi-level bias application also remains visually unqualified until inspected.
