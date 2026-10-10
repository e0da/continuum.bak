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
| `CONTINUUM_SHADER_BUNDLE_OUTPUT` | Absolute, existing, empty private directory |

The build retains an explicit `INSTANCING_ON` variant collection, disables automatic graphics API selection, performs a strict fresh LZ4 bundle build, and writes `build-receipt.json` with shader names, retained pass/keyword tuples and input/output SHA-256 hashes. Vertex/fragment shaders use `PassType.Normal`; surface shaders retain `ForwardBase` with `DIRECTIONAL` and, when `addshadow` is present, `ShadowCaster` with `SHADOWS_DEPTH`. Other required pass combinations must extend this contract before use. Metal remains the default for existing donor builds.

To verify compiler-error detection once, run the editor with `-executeMethod BuildShaderBundle.VerifyCompilerFailure` and `CONTINUUM_SHADER_PROBE_REPORT` pointing to a new absolute JSON path. This imports one intentionally invalid temporary shader, requires a compiler error from `ShaderUtil.GetShaderMessages`, and removes the asset afterward. Its intentional error in the editor log is expected. It does not produce a runtime bundle.

A successful bundle build proves compilation and provenance. The KSP consumer must separately verify the bundle hash, shader support, material binding, actual instanced execution and rendered behavior. A retained variant is not proof that the player physically executes it.

The official matching editor installer is [Unity 2019.4.18f1 for macOS](https://download.unity3d.com/download_unity/3310a4d4f880/MacEditorInstaller/Unity-2019.4.18f1.pkg). Existing activation must work in the batch editor; this tooling does not modify licensing or accept new terms. Ops/Actions use `/Applications/Unity/Hub/Editor/<version>/Unity.app` when an editor is installed there. An extracted private research editor may be invoked by its explicit path.
