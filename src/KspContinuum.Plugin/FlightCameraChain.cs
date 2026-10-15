using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal static class FlightCameraChain
    {
        const int MaxCameras = 64, MaxRenderers = 50000, Capacity = 8192;
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-camera-chain/v1", runId, vesselId, status, reason, cleanupStatus;
            public string scope = "Observed native camera callback brackets over diagnostic lifecycle, including warmup and capture straddlers. Inclusive submission/wait envelopes, not GPU durations or exclusive CPU. Child brackets can overlap parents; callback subscription order can hide nesting before our pre-cull callback.";
            public string ownershipScope = "Positive Component ancestry only; unclassified is not world. Layer/mask intersection indicates potential coverage, not actual draws.";
            public int processId, parts, callbacks, unmatchedOpenBrackets, registeredCameraCount, sceneRendererCount, unregisteredCameraId;
            public bool hooksRemoved;
            public long clockFrequency, startedTicks, endedTicks;
            public double inventoryMilliseconds;
            public CamRow[] cameras; public SceneRow[] scene;
        }
        [Serializable] internal sealed class CamRow
        {
            public int instanceId, flightCameraIndex = -1, cullingMask, targetId, pixelWidth, pixelHeight, targetWidth, targetHeight, targetDepth, targetAntiAliasing;
            public string name, role, roleProvenance, hierarchy, clearFlags, depthTextureMode, renderingPath, targetName, targetFormat;
            public string[] componentTypes, commandNames;
            public bool enabled, activeInHierarchy, orthographic, hasTarget;
            public float cameraDepth, nearClip, farClip, fieldOfView;
            public int potentialRendererCoverage, preCullCount, preRenderCount, postRenderCount, sampleCount;
            public int[] frames, parentCameraIds, depths, stages;
            public long[] startTicks, preRenderTicks, endTicks, childTicks;
        }
        [Serializable] internal sealed class SceneRow
        {
            public int instanceId, layer, partInstanceId, vesselInstanceId, pqsInstanceId;
            public string name, hierarchy, rendererType, ownership, partName, partFlightId, vesselId, pqsName;
            public bool enabled, activeInHierarchy;
        }
        static Report report; static Camera[] cameras; static RenderTexture[] targets; static Part[] parts;
        static Vessel vessel; static string output; static bool attached, exported;
        static int stackCount;
        static readonly int[] stackCameras = new int[MaxCameras], stackSamples = new int[MaxCameras];
        public static string FailureReason { get; private set; }
        static bool Requested { get { return Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-flight-camera-chain") >= 0; } }

        public static void Start(Vessel activeVessel, string directory, string runId)
        {
            if (!Requested) return;
            if (attached || (report != null && !exported)) throw new InvalidOperationException("Camera-chain lifecycle already owned.");
            FailureReason = null; exported = false; stackCount = 0; vessel = activeVessel;
            if (vessel == null || vessel.parts == null || !Path.IsPathRooted(directory) || string.IsNullOrEmpty(runId)) throw new InvalidOperationException("Physical vessel, run and absolute output required.");
            output = Path.Combine(Path.GetFullPath(directory), "camera-chain.json");
            if (File.Exists(output)) throw new InvalidOperationException("Camera-chain output already exists.");
            long begin = Stopwatch.GetTimestamp();
            report = new Report { runId = runId, vesselId = vessel.id.ToString(), processId = Process.GetCurrentProcess().Id, parts = vessel.parts.Count, clockFrequency = Stopwatch.Frequency, startedTicks = begin, status = "observing" };
            try
            {
                parts = vessel.parts.ToArray();
                cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
                if (cameras.Length == 0 || cameras.Length > MaxCameras) throw new InvalidOperationException("Camera inventory outside 1..64.");
                report.registeredCameraCount = cameras.Length; targets = new RenderTexture[cameras.Length]; report.cameras = new CamRow[cameras.Length];
                for (int i = 0; i < cameras.Length; i++) { targets[i] = cameras[i].targetTexture; report.cameras[i] = Inventory(cameras[i]); }
                Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
                if (all.Length > MaxRenderers) throw new InvalidOperationException("Scene renderer inventory exceeds 50000.");
                report.sceneRendererCount = all.Length; report.scene = new SceneRow[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    Renderer renderer = all[i]; Part part = renderer.GetComponentInParent<Part>(); Vessel owner = renderer.GetComponentInParent<Vessel>();
                    if (owner == null && part != null) owner = part.vessel;
                    PQS pqs = renderer.GetComponentInParent<PQS>();
                    var row = new SceneRow { instanceId = renderer.GetInstanceID(), name = renderer.name, hierarchy = Hierarchy(renderer.transform), rendererType = renderer.GetType().FullName,
                        layer = renderer.gameObject.layer, enabled = renderer.enabled, activeInHierarchy = renderer.gameObject.activeInHierarchy,
                        ownership = part != null ? "part-ancestry" : owner != null ? "vessel-ancestry" : pqs != null ? "pqs-ancestry" : "unclassified",
                        partInstanceId = part == null ? 0 : part.GetInstanceID(), partName = part == null ? null : part.name, partFlightId = part == null ? null : part.flightID.ToString(),
                        vesselInstanceId = owner == null ? 0 : owner.GetInstanceID(), vesselId = owner == null ? null : owner.id.ToString(), pqsInstanceId = pqs == null ? 0 : pqs.GetInstanceID(), pqsName = pqs == null ? null : pqs.name };
                    report.scene[i] = row;
                    if (row.enabled && row.activeInHierarchy) for (int c = 0; c < cameras.Length; c++) if ((report.cameras[c].cullingMask & (1 << row.layer)) != 0) report.cameras[c].potentialRendererCoverage++;
                }
                report.inventoryMilliseconds = (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency;
                Context(); attached = true; Camera.onPreCull += PreCull; Camera.onPreRender += PreRender; Camera.onPostRender += PostRender;
            }
            catch (Exception error) { FailureReason = error.GetType().Name + ": " + error.Message; Finish("invalid"); }
        }
        static CamRow Inventory(Camera camera)
        {
            RenderTexture target = camera.targetTexture;
            var row = new CamRow { instanceId = camera.GetInstanceID(), name = camera.name, hierarchy = Hierarchy(camera.transform), role = "unclassified-camera",
                enabled = camera.enabled, activeInHierarchy = camera.gameObject.activeInHierarchy, cullingMask = camera.cullingMask, cameraDepth = camera.depth,
                nearClip = camera.nearClipPlane, farClip = camera.farClipPlane, fieldOfView = camera.fieldOfView, orthographic = camera.orthographic, pixelWidth = camera.pixelWidth, pixelHeight = camera.pixelHeight,
                clearFlags = camera.clearFlags.ToString(), depthTextureMode = camera.depthTextureMode.ToString(), renderingPath = camera.actualRenderingPath.ToString(), hasTarget = target != null,
                targetId = target == null ? 0 : target.GetInstanceID(), targetName = target == null ? null : target.name, targetFormat = target == null ? null : target.format.ToString(),
                targetWidth = target == null ? 0 : target.width, targetHeight = target == null ? 0 : target.height, targetDepth = target == null ? 0 : target.depth, targetAntiAliasing = target == null ? 0 : target.antiAliasing,
                frames = new int[Capacity], parentCameraIds = new int[Capacity], depths = new int[Capacity], stages = new int[Capacity], startTicks = new long[Capacity], preRenderTicks = new long[Capacity], endTicks = new long[Capacity], childTicks = new long[Capacity] };
            if (FlightCamera.fetch != null && FlightCamera.fetch.cameras != null) for (int i = 0; i < FlightCamera.fetch.cameras.Length; i++) if (FlightCamera.fetch.cameras[i] == camera) { row.flightCameraIndex = i; row.role = "flight-camera-identity"; row.roleProvenance = "FlightCamera.fetch.cameras[" + i + "] object identity"; }
            const BindingFlags privateStatic = BindingFlags.NonPublic | BindingFlags.Static, privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo current = typeof(FlightAeroFieldRenderer).GetField("current", privateStatic);
            object fieldOwner = current == null ? null : current.GetValue(null);
            if (fieldOwner != null) foreach (string member in new[] { "main", "view", "flow", "depth", "compositor" })
            {
                FieldInfo field = typeof(FlightAeroFieldRenderer).GetField(member, privateInstance);
                if (field != null && ReferenceEquals(field.GetValue(fieldOwner), camera)) { row.role = "field-owner-" + member; row.roleProvenance = "FlightAeroFieldRenderer.current." + member + " object identity"; }
            }
            Component[] components = camera.GetComponents<Component>(); row.componentTypes = new string[components.Length];
            for (int i = 0; i < components.Length; i++) row.componentTypes[i] = components[i] == null ? "missing-component" : components[i].GetType().FullName;
            var names = new List<string>(); foreach (CameraEvent point in Enum.GetValues(typeof(CameraEvent))) foreach (CommandBuffer command in camera.GetCommandBuffers(point)) names.Add(point.ToString() + ":" + command.name);
            row.commandNames = names.ToArray(); return row;
        }
        static string Hierarchy(Transform node)
        {
            string result = node.name; int depth = 0;
            while (node.parent != null && depth++ < 64) { node = node.parent; result = node.name + "/" + result; }
            return result;
        }
        static void Context()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || vessel == null || FlightGlobals.ActiveVessel != vessel || !vessel.loaded || vessel.packed || vessel.parts.Count != parts.Length) throw new InvalidOperationException("Scene/vessel/topology context changed.");
            for (int i = 0; i < parts.Length; i++) if (parts[i] == null || vessel.parts[i] != parts[i]) throw new InvalidOperationException("Part topology/order changed.");
        }
        static int Locate(Camera camera)
        {
            Context();
            for (int i = 0; i < cameras.Length; i++) if (cameras[i] == camera)
            {
                CamRow row = report.cameras[i];
                if (camera == null || !camera.gameObject.activeInHierarchy || camera.cullingMask != row.cullingMask || camera.targetTexture != targets[i] || camera.pixelWidth != row.pixelWidth || camera.pixelHeight != row.pixelHeight)
                    throw new InvalidOperationException("Camera context changed.");
                return i;
            }
            report.unregisteredCameraId = camera == null ? 0 : camera.GetInstanceID();
            throw new InvalidOperationException("Unregistered native camera callback.");
        }
        static void PreCull(Camera camera)
        {
            if (!attached) return;
            try
            {
                int c = Locate(camera); CamRow row = report.cameras[c];
                if (stackCount >= MaxCameras || row.sampleCount >= Capacity) throw new InvalidOperationException("Camera stack/sample capacity exceeded.");
                for (int i = 0; i < stackCount; i++) if (stackCameras[i] == c) throw new InvalidOperationException("Reentrant same-camera render bracket.");
                int s = row.sampleCount++; row.preCullCount++; report.callbacks++;
                row.frames[s] = Time.frameCount; row.startTicks[s] = Stopwatch.GetTimestamp(); row.depths[s] = stackCount;
                row.parentCameraIds[s] = stackCount == 0 ? 0 : report.cameras[stackCameras[stackCount - 1]].instanceId;
                stackCameras[stackCount] = c; stackSamples[stackCount++] = s;
            }
            catch (Exception error) { Fault(error); }
        }
        static void PreRender(Camera camera)
        {
            if (!attached) return;
            try
            {
                int c = Locate(camera); if (stackCount == 0 || stackCameras[stackCount - 1] != c) throw new InvalidOperationException("Pre-render stack mismatch.");
                CamRow row = report.cameras[c]; int s = stackSamples[stackCount - 1];
                if (row.stages[s] != 0 || row.frames[s] != Time.frameCount) throw new InvalidOperationException("Duplicate/stale pre-render.");
                row.preRenderTicks[s] = Stopwatch.GetTimestamp(); row.stages[s] = 1; row.preRenderCount++; report.callbacks++;
            }
            catch (Exception error) { Fault(error); }
        }
        static void PostRender(Camera camera)
        {
            if (!attached) return;
            try
            {
                int c = Locate(camera); if (stackCount == 0 || stackCameras[stackCount - 1] != c) throw new InvalidOperationException("Post-render stack mismatch.");
                CamRow row = report.cameras[c]; int s = stackSamples[stackCount - 1];
                if (row.stages[s] != 1 || row.frames[s] != Time.frameCount) throw new InvalidOperationException("Missing/stale pre-render before post-render.");
                row.endTicks[s] = Stopwatch.GetTimestamp(); row.stages[s] = 2; row.postRenderCount++; report.callbacks++; stackCount--;
                if (stackCount > 0) report.cameras[stackCameras[stackCount - 1]].childTicks[stackSamples[stackCount - 1]] += row.endTicks[s] - row.startTicks[s];
            }
            catch (Exception error) { Fault(error); }
        }
        static void Fault(Exception error) { FailureReason = error.GetType().Name + ": " + error.Message; report.reason = FailureReason; report.status = "invalid"; Detach(); }
        static void Detach()
        {
            if (attached) { Camera.onPreCull -= PreCull; Camera.onPreRender -= PreRender; Camera.onPostRender -= PostRender; attached = false; }
            if (report != null) { report.hooksRemoved = true; report.cleanupStatus = "removed-owned-hooks"; }
        }
        public static void Finish(string status)
        {
            if (report == null || exported) return;
            if (attached) try { Context(); if (stackCount != 0) throw new InvalidOperationException("Open camera brackets at finish."); if (report.callbacks == 0) throw new InvalidOperationException("No native camera callbacks observed."); } catch (Exception error) { Fault(error); }
            Detach(); report.status = FailureReason == null ? status : "invalid"; report.reason = FailureReason ?? report.reason ?? status; report.unmatchedOpenBrackets = stackCount; report.endedTicks = Stopwatch.GetTimestamp();
            if (report.cameras != null) foreach (CamRow row in report.cameras)
            {
                if (row == null) continue;
                Array.Resize(ref row.frames, row.sampleCount); Array.Resize(ref row.parentCameraIds, row.sampleCount); Array.Resize(ref row.depths, row.sampleCount); Array.Resize(ref row.stages, row.sampleCount);
                Array.Resize(ref row.startTicks, row.sampleCount); Array.Resize(ref row.preRenderTicks, row.sampleCount); Array.Resize(ref row.endTicks, row.sampleCount); Array.Resize(ref row.childTicks, row.sampleCount);
            }
            try { Directory.CreateDirectory(Path.GetDirectoryName(output)); string temporary = output + ".tmp"; File.WriteAllText(temporary, ReportJson.Encode(report)); File.Move(temporary, output); exported = true; }
            catch (Exception error) { FailureReason = "camera-chain-export: " + error.Message; report.status = "invalid"; UnityEngine.Debug.LogError(FailureReason); }
        }
        public static void Stop(string reason)
        {
            if (report == null || exported) return;
            report.reason = reason; Finish("interrupted");
        }
    }
}
