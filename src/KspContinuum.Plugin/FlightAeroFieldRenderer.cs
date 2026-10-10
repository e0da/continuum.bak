using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal sealed class FlightAeroFieldRenderer
    {
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-aero-field/v1", runId, vesselId, status, reason, cleanupStatus, bundlePath, bundleSha256;
            public string engine, renderer;
            public string[] producerPatchAdmission;
            public string fidelity = "Original experimental coverage/flow sheath and ribbon effect; stock state/heating/physics retained. No stock visual equivalence claim.";
            public string counterScope = "Start through Finish including warmup, post-window screenshot and cleanup. Callbacks are not GPU timing.";
            public int processId, width, height, faults, coveragePre, coveragePost, flowPre, flowPost, compositorPre, compositorPost, sceneDepthCallbacks, producerSnapshots, suppressedDepth, lastProducerFrame, lastCompositeFrame;
            public int lastCoverageFrame = -1, lastFlowFrame = -1, lastSceneDepthFrame = -1;
            public int stockDepthPre, stockDepthPost, viewPositiveSamples, flowPositiveSamples, scenePositiveSamples, nonFiniteSamples, invalidNormalSamples, invalidFlowSamples, invalidSceneDepthSamples, geometryRestoredOriginals, geometryDestroyedProxies;
            public bool pixelProofComplete;
            public int proofTemporaryCreated, proofTemporaryReleased;
            public string fixturePath, flowRestoration = "Pinned FXCamera.Start creates Transform+Camera without a replacement shader; restore original-none.";
            public int eligibleRenderers, geometryPackets, fallbackRenderers, ribbonInstances = 256, createdTextures, releasedTextures, createdMaterials, destroyedMaterials;
            public bool stockRestored, hooksRemoved, geometryRestored;
            public float intensity, length, edgeFade, obliqueness, textureSpeed, textureScale, wobble, flowFar, viewNear, viewFar;
            public float[] effectDirection = new float[3], effectColor = new float[4];
        }
        const string Owner = "continuum.flight-aero-field";
        static FlightAeroFieldRenderer current;
        public static bool Requested { get { return Environment.GetCommandLineArgs().Contains("--continuum-flight-aero-field"); } }
        public static string FailureReason { get { return current != null && current.faulted ? current.report.reason : null; } }
        readonly Matrix4x4[] instances = Enumerable.Repeat(Matrix4x4.identity, 256).ToArray();
        readonly MaterialPropertyBlock ribbonBlock = new MaterialPropertyBlock();
        FXCamera producer; FXDepthCamera depthProducer;
        AerodynamicsFX aeroProducer;
        Camera view, flow, depth, main, compositor;
        RenderTexture viewField, flowField, sceneDepth, originalViewTarget, originalFlowTarget;
        CameraClearFlags originalViewClear, originalFlowClear;
        Color originalViewBackground, originalFlowBackground;
        DepthTextureMode originalMainDepth;
        DepthTextureMode originalFlowDepth;
        Shader coverageShader, flowShader, originalViewShader;
        bool originalDepthEnabled, cameraOwned, preAttached, postAttached, faulted, finished, geometryOwned;
        AssetBundle bundle; Material sheath, ribbon, depthCopy; Mesh quad;
        CommandBuffer composeCommands, depthCommands;
        Harmony harmony; MethodInfo lateMethod, preMethod, aeroLateMethod;
        Vessel vessel; Report report; string directory;
        long bundleLength; DateTime bundleModified;
        Renderer[] clipSources;
        float originalFlowFar;

        public static Report Start(Vessel vessel, string directory, string runId)
        {
            if (!Requested) return null;
            if (current != null) throw new InvalidOperationException("Field renderer already owned.");
            var owner = new FlightAeroFieldRenderer { vessel = vessel, directory = directory,
                report = new Report { runId = runId, vesselId = vessel == null ? null : vessel.id.ToString("D"), processId = System.Diagnostics.Process.GetCurrentProcess().Id, status = "preparing" } };
            current = owner;
            owner.report.engine = Application.unityVersion; owner.report.renderer = SystemInfo.graphicsDeviceType + ":" + SystemInfo.graphicsDeviceVersion;
            try { owner.Prepare(); }
            catch (Exception error) { owner.Fault("prepare:" + error.GetType().Name + ":" + error.Message); }
            return owner.report;
        }
        static string OneArgument(string prefix)
        {
            string[] args = Environment.GetCommandLineArgs().Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (args.Length != 1 || args[0].Length == prefix.Length) throw new InvalidOperationException("Exactly one field bundle path required.");
            return args[0].Substring(prefix.Length);
        }
        static void NoSymlinks(string path)
        {
            for (string p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Field bundle symlink not admitted.");
        }
        Shader ShaderNamed(string name)
        {
            Shader shader = bundle.LoadAllAssets<Shader>().SingleOrDefault(s => s.name == name);
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Supported shader required: " + name);
            return shader;
        }
        RenderTexture Target(string name, int width, int height, int bits, RenderTextureFormat format)
        {
            if (!SystemInfo.SupportsRenderTextureFormat(format)) throw new InvalidOperationException("Field target format unsupported: " + format);
            var target = new RenderTexture(width, height, bits, format, RenderTextureReadWrite.Linear) { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, antiAliasing = 1 };
            if (!target.Create()) { UnityEngine.Object.Destroy(target); throw new InvalidOperationException("Field target allocation failed."); }
            report.createdTextures++; return target;
        }
        Material MaterialFor(Shader shader) { report.createdMaterials++; return new Material(shader); }
        string AdmitProducer(MethodInfo method, string name, bool allowFrozenPose)
        {
            if (method == null) throw new MissingMethodException("Producer absent: " + name);
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return name + ":unpatched";
            string detail = name + ":" + string.Join(",", patches.Prefixes.Select(p => "prefix=" + p.owner + "/" + p.PatchMethod.DeclaringType.FullName + "." + p.PatchMethod.Name)
                .Concat(patches.Postfixes.Select(p => "postfix=" + p.owner + "/" + p.PatchMethod.Name))
                .Concat(patches.Transpilers.Select(p => "transpiler=" + p.owner + "/" + p.PatchMethod.Name))
                .Concat(patches.Finalizers.Select(p => "finalizer=" + p.owner + "/" + p.PatchMethod.Name)).ToArray());
            MethodInfo freeze = AccessTools.DeclaredMethod(typeof(FlightCameraFreeze), "EffectPrefix", Type.EmptyTypes);
            bool compatible = allowFrozenPose && patches.Prefixes.Count == 1 && patches.Postfixes.Count == 0 && patches.Transpilers.Count == 0 && patches.Finalizers.Count == 0 &&
                patches.Prefixes[0].owner == "continuum.flight.camera.freeze" && patches.Prefixes[0].PatchMethod == freeze && freeze != null && freeze.ReturnType == typeof(void);
            if (!compatible) throw new InvalidOperationException("Unrecognized producer modifier: " + detail);
            return detail + ":admitted-known-frozen-pose-prefix-original-retained";
        }
        void Prepare()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (new[] { "--continuum-flight-aero-batch", "--continuum-flight-no-aero-fx", "--continuum-flight-part-retained" }.Any(args.Contains) || args.Any(a => a.StartsWith("--continuum-flight-aero-quality=", StringComparison.Ordinal)))
                throw new InvalidOperationException("Field renderer owns the effect treatment exclusively.");
            StartupPinnedGameIdentity.Require(typeof(FXCamera).Assembly);
            if (vessel == null || HighLogic.LoadedScene != GameScenes.FLIGHT || !SystemInfo.supportsInstancing) throw new InvalidOperationException("Flight instancing context required.");
            producer = UnityEngine.Object.FindObjectOfType<FXCamera>(); depthProducer = UnityEngine.Object.FindObjectOfType<FXDepthCamera>();
            if (producer == null || depthProducer == null) throw new InvalidOperationException("Stock FX producers unavailable.");
            aeroProducer = UnityEngine.Object.FindObjectsOfType<AerodynamicsFX>().SingleOrDefault(p => p.fxCamera == producer);
            if (aeroProducer == null) throw new InvalidOperationException("Single authoritative stock state producer required.");
            view = producer.GetComponent<Camera>(); flow = AccessTools.DeclaredField(typeof(FXCamera), "velocityCam").GetValue(producer) as Camera; depth = depthProducer.depthCamera;
            if (view == null || flow == null || depth == null || !view.enabled || !flow.enabled || producer.ReplacementShaders == null || producer.ReplacementShaders.Length == 0)
                throw new InvalidOperationException("Active stock view/flow/depth cameras required.");
            if (flow.GetComponents<Component>().Length != 2 || Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(FXCamera), "Start"))?.Owners.Count > 0)
                throw new InvalidOperationException("Unknown flow replacement owner is unsupported.");
            if (FlightGlobals.Vessels.Any(other => other != vessel && other != null && other.loaded && !other.packed))
                throw new InvalidOperationException("Field prototype requires one loaded unpacked vessel.");
            main = FlightCamera.fetch.cameras.FirstOrDefault(camera => camera != null && camera.enabled && SameView(camera, view));
            if (main == null) throw new InvalidOperationException("Matching main/FX projection unavailable.");
            if (view.rect != new Rect(0, 0, 1, 1)) throw new InvalidOperationException("Full-screen field viewport required.");
            clipSources = vessel.parts.SelectMany(part => part.GetComponentsInChildren<Renderer>()).Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy && (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) && (view.cullingMask & (1 << renderer.gameObject.layer)) != 0 && renderer.sharedMaterials.Length > 0 && renderer.sharedMaterials.All(material => material != null && material.GetTag("RenderType", false, "") == "Opaque" && material.renderQueue <= 2500)).Distinct().ToArray();
            if (clipSources.Length == 0) throw new InvalidOperationException("No visible vessel geometry to admit.");
            report.fixturePath = AeroFieldFixtureCapture.Capture(vessel, producer, Path.Combine(directory, "aero-field-fixture"), report.runId);
            string path = OneArgument("--continuum-flight-aero-field-bundle=");
            if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("Canonical absolute field bundle required.");
            NoSymlinks(path); var info = new FileInfo(path); bundleLength = info.Length; bundleModified = info.LastWriteTimeUtc;
            report.bundlePath = path;
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) report.bundleSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            bundle = AssetBundle.LoadFromFile(path); if (bundle == null) throw new InvalidOperationException("Field bundle load failed.");
            coverageShader = ShaderNamed("Continuum/AeroFieldCoverage"); flowShader = ShaderNamed("Continuum/AeroFieldFlow");
            sheath = MaterialFor(ShaderNamed("Continuum/AeroFieldSheath")); ribbon = MaterialFor(ShaderNamed("Continuum/AeroFieldRibbon")); ribbon.enableInstancing = true;
            depthCopy = MaterialFor(ShaderNamed("Continuum/AeroFieldSceneDepthCopy"));
            report.width = Screen.width; report.height = Screen.height;
            viewField = Target("ContinuumViewField", report.width, report.height, 24, RenderTextureFormat.ARGBFloat);
            flowField = Target("ContinuumFlowField", producer.occlusionMapResolution, producer.occlusionMapResolution, 24, RenderTextureFormat.RFloat);
            sceneDepth = Target("ContinuumSceneDepth", report.width, report.height, 0, RenderTextureFormat.RFloat);
            quad = new Mesh { name = "ContinuumFieldQuad" };
            quad.vertices = new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) };
            quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }; quad.triangles = new[] { 0,1,2,0,2,3 }; quad.RecalculateBounds();
            compositor = new GameObject("ContinuumAeroFieldCompositor").AddComponent<Camera>(); compositor.CopyFrom(view); compositor.enabled = false;
            compositor.cullingMask = 0; compositor.clearFlags = CameraClearFlags.Nothing; compositor.targetTexture = null; compositor.depth = view.depth + .5f; compositor.depthTextureMode = DepthTextureMode.None;
            composeCommands = new CommandBuffer { name = "Continuum field sheath and ribbons" }; compositor.AddCommandBuffer(CameraEvent.AfterEverything, composeCommands);
            depthCommands = new CommandBuffer { name = "Continuum linear scene depth" }; depthCommands.Blit(BuiltinRenderTextureType.None, sceneDepth, depthCopy);
            depthCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            lateMethod = AccessTools.DeclaredMethod(typeof(FXCamera), "LateUpdate"); preMethod = AccessTools.DeclaredMethod(typeof(FXCamera), "OnPreRender");
            aeroLateMethod = AccessTools.DeclaredMethod(typeof(AerodynamicsFX), "LateUpdate");
            report.producerPatchAdmission = new[] { AdmitProducer(lateMethod, "FXCamera.LateUpdate", true), AdmitProducer(preMethod, "FXCamera.OnPreRender", false), AdmitProducer(aeroLateMethod, "AerodynamicsFX.LateUpdate", false) };
            FlightAeroBatcher.Report geometry = FlightAeroBatcher.StartExplicit(vessel, directory, report.runId); geometryOwned = true;
            if (geometry.status != "installed-experimental") throw new InvalidOperationException("Geometry preparation failed: " + geometry.reason);
            FlightAeroBatcher.ConfigureForField(depth); report.eligibleRenderers = geometry.eligibleRenderers; report.geometryPackets = geometry.batches; report.fallbackRenderers = geometry.fallbackRenderers;
            originalViewTarget = view.targetTexture; originalFlowTarget = flow.targetTexture; originalViewClear = view.clearFlags; originalFlowClear = flow.clearFlags;
            originalViewBackground = view.backgroundColor; originalFlowBackground = flow.backgroundColor; originalDepthEnabled = depth.enabled; originalMainDepth = main.depthTextureMode;
            originalFlowDepth = flow.depthTextureMode;
            originalViewShader = producer.debugShader ? producer.depthDebugShader : producer.ReplacementShaders[Mathf.Clamp(producer.shaderLOD, 0, producer.ReplacementShaders.Length - 1)];
            originalFlowFar = Shader.GetGlobalFloat("_ContinuumFlowFar");
            cameraOwned = true;
            main.depthTextureMode |= DepthTextureMode.Depth; main.AddCommandBuffer(CameraEvent.AfterForwardOpaque, depthCommands);
            harmony = new Harmony(Owner); harmony.Patch(lateMethod, postfix: new HarmonyMethod(typeof(FlightAeroFieldRenderer), nameof(ProducerLate))); harmony.Patch(preMethod, postfix: new HarmonyMethod(typeof(FlightAeroFieldRenderer), nameof(ProducerPre)));
            harmony.Patch(aeroLateMethod, postfix: new HarmonyMethod(typeof(FlightAeroFieldRenderer), nameof(StateProducerLate)));
            Camera.onPreCull += PreCull; preAttached = true; Camera.onPostRender += PostRender; postAttached = true;
            Publish(); report.lastProducerFrame = -1; compositor.enabled = true; report.status = "installed-experimental"; report.cleanupStatus = "owned-field-hooks-active"; Write();
        }
        static bool SameView(Camera a, Camera b)
        {
            Matrix4x4 ap = a.projectionMatrix, bp = b.projectionMatrix;
            for (int row = 0; row < 4; row++)
                if (row != 2)
                    for (int column = 0; column < 4; column++)
                        if (!Finite(ap[row,column]) || !Finite(bp[row,column]) || Mathf.Abs(ap[row,column]-bp[row,column]) > .00001f) return false;
            return !a.orthographic && !b.orthographic && (a.transform.position - b.transform.position).sqrMagnitude < .000001f &&
                Quaternion.Angle(a.transform.rotation, b.transform.rotation) < .001f && Mathf.Abs(a.fieldOfView-b.fieldOfView) < .001f && Mathf.Abs(a.aspect-b.aspect) < .00001f && a.rect == b.rect;
        }
        static void ProducerLate(FXCamera __instance) { if (current != null && current.producer == __instance) current.UpdateOwned(); }
        static void ProducerPre(FXCamera __instance) { if (current != null && current.producer == __instance) current.UpdateOwned(); }
        static void StateProducerLate(AerodynamicsFX __instance) { if (current != null && current.aeroProducer == __instance) current.UpdateOwned(); }
        void UpdateOwned()
        {
            if (faulted || finished) return;
            try { EnforceOwned(); }
            catch (Exception error) { Fault("publication:" + error.GetType().Name + ":" + error.Message); }
        }
        void Publish()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.ActiveVessel != vessel || !SameView(main, view) || Screen.width != report.width || Screen.height != report.height || !view.enabled || !flow.enabled)
                throw new InvalidOperationException("Field view context changed.");
            var info = new FileInfo(report.bundlePath); if (info.Length != bundleLength || info.LastWriteTimeUtc != bundleModified) throw new InvalidOperationException("Field bundle changed.");
            Vector2 minimum = Vector2.one, maximum = Vector2.zero;
            Bounds aggregate = default(Bounds); bool first = true;
            foreach (Renderer renderer in clipSources)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) throw new InvalidOperationException("Admitted vessel geometry visibility changed.");
                if (first) { aggregate = renderer.bounds; first = false; } else aggregate.Encapsulate(renderer.bounds);
            }
            for (int i = 0; i < 8; i++)
            {
                Vector3 point = aggregate.center + Vector3.Scale(aggregate.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 uv = view.WorldToViewportPoint(point), mainUv = main.WorldToViewportPoint(point);
                if (uv.z <= view.nearClipPlane || uv.z >= view.farClipPlane || mainUv.z <= main.nearClipPlane || mainUv.z >= main.farClipPlane || uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1)
                    throw new InvalidOperationException("Vessel exceeds admitted common view clip.");
                minimum = Vector2.Min(minimum, uv); maximum = Vector2.Max(maximum, uv);
            }
            report.producerSnapshots++; report.lastProducerFrame = Time.frameCount; report.intensity = producer.intensity; report.length = producer.length; report.edgeFade = producer.edgeFade;
            report.obliqueness = producer.obliqueness; report.effectDirection[0] = producer.effectDirection.x; report.effectDirection[1] = producer.effectDirection.y; report.effectDirection[2] = producer.effectDirection.z;
            report.effectColor[0] = producer.color.r; report.effectColor[1] = producer.color.g; report.effectColor[2] = producer.color.b; report.effectColor[3] = producer.color.a; report.textureSpeed = producer.textureSpeed;
            report.textureScale = producer.textureScale; report.wobble = producer.wobble; report.viewNear = view.nearClipPlane; report.viewFar = view.farClipPlane; report.flowFar = flow.farClipPlane;
            EnforceOwned();
            compositor.transform.SetPositionAndRotation(view.transform.position, view.transform.rotation); compositor.projectionMatrix = view.projectionMatrix; compositor.depth = view.depth + .5f;
            composeCommands.Clear();
            composeCommands.SetGlobalTexture("_ContinuumViewField", viewField); composeCommands.SetGlobalTexture("_ContinuumFlowField", flowField); composeCommands.SetGlobalTexture("_ContinuumSceneDepth", sceneDepth);
            composeCommands.SetGlobalVector("_ContinuumViewField_TexelSize", new Vector4(1f / viewField.width, 1f / viewField.height, viewField.width, viewField.height));
            composeCommands.SetGlobalMatrix("_ContinuumViewInverseProjection", GL.GetGPUProjectionMatrix(view.projectionMatrix, true).inverse);
            composeCommands.SetGlobalMatrix("_ContinuumViewCameraToWorld", view.cameraToWorldMatrix);
            composeCommands.SetGlobalMatrix("_ContinuumFlowViewProjection", GL.GetGPUProjectionMatrix(flow.projectionMatrix, true) * flow.worldToCameraMatrix);
            composeCommands.SetGlobalMatrix("_ContinuumFlowWorldToView", flow.worldToCameraMatrix); composeCommands.SetGlobalFloat("_ContinuumFlowFar", flow.farClipPlane);
            composeCommands.SetGlobalVector("_ContinuumDepthParams", new Vector4(view.nearClipPlane, view.farClipPlane, 0, 0));
            composeCommands.SetGlobalColor("_ContinuumEffectColor", producer.color); composeCommands.SetGlobalVector("_ContinuumEffectDirection", producer.effectDirection);
            composeCommands.SetGlobalVector("_ContinuumEffectParameters", new Vector4(producer.intensity, producer.length, producer.edgeFade, producer.obliqueness));
            composeCommands.SetGlobalVector("_ContinuumTextureParameters", new Vector4(producer.textureSpeed, producer.textureScale, producer.wobble, Time.time));
            composeCommands.SetGlobalVector("_ContinuumFalloffs", new Vector4(producer.falloff1, producer.falloff2, producer.falloff3, 0));
            composeCommands.SetGlobalTexture("_ContinuumNoiseTex", producer.texture == null ? Texture2D.whiteTexture : producer.texture);
            ribbon.SetTexture("_ContinuumNoiseTex", producer.texture == null ? Texture2D.whiteTexture : producer.texture);
            composeCommands.SetGlobalVector("_ContinuumSeedRect", new Vector4(minimum.x, minimum.y, maximum.x-minimum.x, maximum.y-minimum.y));
            composeCommands.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CameraTarget, sheath, 0);
            composeCommands.SetViewProjectionMatrices(view.worldToCameraMatrix, view.projectionMatrix);
            composeCommands.DrawMeshInstanced(quad, 0, ribbon, 0, instances, instances.Length, ribbonBlock);
        }
        void EnforceOwned()
        {
            if (view == null || flow == null || depth == null) throw new InvalidOperationException("Owned FX camera destroyed.");
            view.targetTexture = viewField; view.clearFlags = CameraClearFlags.Color; view.backgroundColor = Color.clear; view.SetReplacementShader(coverageShader, "RenderType");
            flow.targetTexture = flowField; flow.clearFlags = CameraClearFlags.Color; flow.backgroundColor = Color.clear; flow.SetReplacementShader(flowShader, "RenderType"); flow.depthTextureMode = DepthTextureMode.None;
            Shader.SetGlobalFloat("_ContinuumFlowFar", flow.farClipPlane);
            depth.enabled = false; report.suppressedDepth++;
        }
        void PreCull(Camera camera)
        {
            if (faulted || finished) return;
            if (FlightAeroBatcher.FailureReason != null) { Fault("geometry-fault:" + FlightAeroBatcher.FailureReason); return; }
            if (camera == flow && report.lastProducerFrame != Time.frameCount)
            {
                try { Publish(); }
                catch (Exception error) { Fault("frame-publication:" + error.GetType().Name + ":" + error.Message); return; }
            }
            if (camera == view) report.coveragePre++;
            if (camera == flow) report.flowPre++;
            if (camera == depth) { report.stockDepthPre++; Fault("stock-depth-camera-rendered-while-owned"); return; }
            if (camera == main) report.sceneDepthCallbacks++;
            if (camera == compositor)
            {
                if (report.lastProducerFrame != Time.frameCount || report.lastCoverageFrame != Time.frameCount || report.lastFlowFrame != Time.frameCount || report.lastSceneDepthFrame != Time.frameCount)
                { Fault("stale-producer-or-field-publication"); return; }
                report.compositorPre++; report.lastCompositeFrame = Time.frameCount;
            }
        }
        void PostRender(Camera camera)
        {
            if (faulted || finished) return;
            if (camera == view) { report.coveragePost++; report.lastCoverageFrame = Time.frameCount; }
            if (camera == flow) { report.flowPost++; report.lastFlowFrame = Time.frameCount; }
            if (camera == main) report.lastSceneDepthFrame = Time.frameCount;
            if (camera == depth) report.stockDepthPost++;
            if (camera == compositor) report.compositorPost++;
        }
        void Fault(string reason)
        {
            if (faulted) return; faulted = true; report.faults++; report.reason = reason; report.status = "invalid-fell-back-to-stock";
            Restore(); Write();
        }
        void Restore()
        {
            if (preAttached) { Camera.onPreCull -= PreCull; preAttached = false; } if (postAttached) { Camera.onPostRender -= PostRender; postAttached = false; }
            if (harmony != null) { harmony.UnpatchAll(Owner); harmony = null; }
            if (compositor != null) compositor.enabled = false;
            if (cameraOwned)
            {
                if (view != null) { view.targetTexture = originalViewTarget; view.clearFlags = originalViewClear; view.backgroundColor = originalViewBackground; view.ResetReplacementShader(); if (originalViewShader != null) view.SetReplacementShader(originalViewShader, "RenderType"); }
                if (flow != null) { flow.targetTexture = originalFlowTarget; flow.clearFlags = originalFlowClear; flow.backgroundColor = originalFlowBackground; flow.depthTextureMode = originalFlowDepth; flow.ResetReplacementShader(); }
                if (depth != null) depth.enabled = originalDepthEnabled;
                if (main != null) { main.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, depthCommands); main.depthTextureMode = originalMainDepth; }
                cameraOwned = false; report.stockRestored = true;
                Shader.SetGlobalFloat("_ContinuumFlowFar", originalFlowFar);
            }
            if (geometryOwned) { FlightAeroBatcher.Report geometry = FlightAeroBatcher.Finish(); geometryOwned = false; report.geometryRestored = true; report.geometryRestoredOriginals = geometry.restoredOriginals; report.geometryDestroyedProxies = geometry.destroyedProxies; }
            report.hooksRemoved = true;
        }
        public static void ObservePixelsAfterWindow()
        {
            if (!Requested) return;
            if (current == null || current.faulted || current.finished) throw new InvalidOperationException("No valid field owner for post-window pixel proof.");
            try
            {
                current.ReadPixels(current.viewField, "coverage"); current.ReadPixels(current.flowField, "flow"); current.ReadPixels(current.sceneDepth, "scene");
                Report r = current.report;
                if (r.viewPositiveSamples == 0 || r.flowPositiveSamples == 0 || r.scenePositiveSamples == 0 || r.nonFiniteSamples != 0 || r.invalidNormalSamples != 0 || r.invalidFlowSamples != 0 || r.invalidSceneDepthSamples != 0)
                    throw new InvalidOperationException("Field pixel proof failed.");
                r.pixelProofComplete = true; current.Write();
            }
            catch (Exception error) { current.Fault("pixel-proof:" + error.GetType().Name + ":" + error.Message); throw; }
        }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        void ReadPixels(RenderTexture source, string role)
        {
            RenderTexture previous = RenderTexture.active, small = null; Texture2D pixels = null;
            try
            {
                small = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear); report.proofTemporaryCreated++;
                pixels = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true); report.proofTemporaryCreated++;
                Graphics.Blit(source, small); RenderTexture.active = small;
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0, false);
                foreach (Color pixel in pixels.GetPixels())
                {
                    if (!Finite(pixel.r) || !Finite(pixel.g) || !Finite(pixel.b) || !Finite(pixel.a)) { report.nonFiniteSamples++; continue; }
                    if (role == "coverage")
                    {
                        if (pixel.a <= 0) continue;
                        report.viewPositiveSamples++;
                        float norm = new Vector3(pixel.r * 2 - 1, pixel.g * 2 - 1, pixel.b * 2 - 1).magnitude;
                        if (Mathf.Abs(norm - 1) > .05f || pixel.a < view.nearClipPlane || pixel.a > view.farClipPlane) report.invalidNormalSamples++;
                    }
                    else if (role == "flow")
                    {
                        if (pixel.r > 0) report.flowPositiveSamples++;
                        if (pixel.r < 0 || pixel.r > 1.00001f) report.invalidFlowSamples++;
                    }
                    else
                    {
                        if (pixel.r > 0) report.scenePositiveSamples++;
                        if (pixel.r < 0 || (pixel.r > 0 && pixel.r < main.nearClipPlane * .99999f) || pixel.r > main.farClipPlane * 1.00001f) report.invalidSceneDepthSamples++;
                    }
                }
            }
            finally
            {
                RenderTexture.active = previous;
                if (pixels != null) { Destroy(pixels); report.proofTemporaryReleased++; }
                if (small != null) { RenderTexture.ReleaseTemporary(small); report.proofTemporaryReleased++; }
            }
        }
        static void Destroy(UnityEngine.Object value) { if (value != null) UnityEngine.Object.Destroy(value); }
        public static Report Finish()
        {
            if (current == null) return null; var owner = current;
            try
            {
                owner.Restore(); owner.finished = true;
                if (owner.compositor != null) owner.compositor.RemoveAllCommandBuffers();
                if (owner.composeCommands != null) owner.composeCommands.Dispose(); if (owner.depthCommands != null) owner.depthCommands.Dispose();
                foreach (RenderTexture target in new[] { owner.viewField, owner.flowField, owner.sceneDepth }) if (target != null) { target.Release(); Destroy(target); owner.report.releasedTextures++; }
                foreach (Material material in new[] { owner.sheath, owner.ribbon, owner.depthCopy }) if (material != null) { Destroy(material); owner.report.destroyedMaterials++; }
                Destroy(owner.quad); if (owner.compositor != null) Destroy(owner.compositor.gameObject); if (owner.bundle != null) owner.bundle.Unload(false);
                owner.report.cleanupStatus = "stock-restored-owned-hooks-removed-resources-release-requested";
                if (!owner.faulted) owner.report.status = owner.report.pixelProofComplete && owner.report.coveragePost > 0 && owner.report.flowPost > 0 && owner.report.compositorPost > 0 ? "complete-experimental" : "invalid-no-field-render";
                owner.Write(); return owner.report;
            }
            finally { current = null; }
        }
        void Write() { File.WriteAllText(Path.Combine(directory, "aero-field.json"), ReportJson.Encode(report)); }
    }
}
