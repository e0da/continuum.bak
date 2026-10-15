using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    public sealed class FlightGpuTiming : MonoBehaviour
    {
        static FlightGpuTiming active;
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BeginRun(byte[] run, int length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Enqueue(int kind, int camera, int frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Callback();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Snapshot(byte[] bytes, int capacity);
        Enqueue enqueue; Snapshot snapshot; IntPtr callback, library;
        string directory; Report report;
        bool initQueued, stopping, finished, attached;
        readonly Dictionary<Camera, Pair> cameras = new Dictionary<Camera, Pair>();
        readonly List<string> snapshots = new List<string>();
        int lastSnapshotFrame, snapshotDrops;
        readonly byte[] snapshotBuffer = new byte[1024 * 1024];
        sealed class Pair { public Camera camera; public CommandBuffer begin, end; public CameraRow row; }
        [Serializable] public sealed class CameraRow
        {
            public string role, name, renderingPath;
            public int instanceId, preCull, postRender, queuedPairs, declinedPath, firstFrame = -1, lastFrame = -1;
        }
        [Serializable] public sealed class Report
        {
            public string schema = "continuum-flight-gpu-timing/v1", runId, status, reason, libraryPath, librarySha256, nativeJson;
            public string scope = "Diagnostic GPU timestamps BeforeForwardOpaque to AfterEverything for named cameras. Per-camera intervals may overlap/nest and must not be summed. Tags name the managed enqueue frame, not native completion time. Rows include warmup and post-window work; consumer selects frames witnessed inside flight capture. Native query readback is asynchronous; not full-frame GPU time, driver CPU cost, or GPU utilization. Query/callback overhead perturbs the workload.";
            public int processId, enqueueFailures;
            public bool cleanupObserved;
            public CameraRow[] cameras;
            public int snapshotDrops;
            public string[] nativeSnapshots;
        }
        [Serializable] sealed class NativeState { public string status = null; public int pending = -1; }

        public static void StartOwned(GameObject owner, string output, string run)
        {
            string prefix = "--continuum-flight-gpu-timing-library=";
            string[] paths = Environment.GetCommandLineArgs().Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (paths.Length == 0) return;
            if (paths.Length != 1 || active != null) throw new InvalidOperationException("One GPU diagnostic owner/path required.");
            var observer = owner.AddComponent<FlightGpuTiming>(); active = observer;
            try { observer.Install(paths[0].Substring(prefix.Length), output, run); }
            catch (Exception error) { observer.Fault("install: " + error.Message); observer.Detach(); observer.Export(); }
        }
        void Install(string path, string output, string run)
        {
            directory = output;
            report = new Report { runId = run, processId = Process.GetCurrentProcess().Id, status = "installed-diagnostic", libraryPath = path };
            if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path || !File.Exists(path) || Path.GetExtension(path) != ".dylib" ||
                string.IsNullOrEmpty(run) || !File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json")))
                throw new InvalidOperationException("Absolute admitted private diagnostic library and run required.");
            string current = path;
            while (!string.IsNullOrEmpty(current)) { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Diagnostic library path is symbolic."); current = Path.GetDirectoryName(current); }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLCore) throw new InvalidOperationException("GPU timestamp diagnostic requires OpenGLCore.");
            report.librarySha256 = ScaleCheckpointLoadState.HashFile(path);
            library = dlopen(path, 2);
            if (library == IntPtr.Zero) throw new InvalidOperationException("Diagnostic dlopen failed.");
            BeginRun beginRun = Symbol<BeginRun>("crt_begin_run"); enqueue = Symbol<Enqueue>("crt_enqueue");
            snapshot = Symbol<Snapshot>("crt_json"); callback = Symbol<Callback>("crt_get_render_event_func")();
            byte[] identity = Encoding.UTF8.GetBytes(run);
            if (callback == IntPtr.Zero || beginRun(identity, identity.Length) != 0) throw new InvalidOperationException("Native diagnostic run admission rejected.");
            if (FlightCamera.fetch == null) throw new InvalidOperationException("Flight camera unavailable.");
            if (FlightCamera.fetch.cameras == null || FlightCamera.fetch.cameras.Length == 0) throw new InvalidOperationException("Flight camera array unavailable.");
            for (int i = 0; i < FlightCamera.fetch.cameras.Length; i++) Add("flight-" + i, FlightCamera.fetch.cameras[i]);
            FXCamera fx = UnityEngine.Object.FindObjectOfType<FXCamera>();
            FXDepthCamera depth = UnityEngine.Object.FindObjectOfType<FXDepthCamera>();
            if (fx != null)
            {
                Add("fx", fx.GetComponent<Camera>());
                FieldInfo velocity = typeof(FXCamera).GetField("velocityCam", BindingFlags.Instance | BindingFlags.NonPublic);
                Add("velocity-occlusion", velocity == null ? null : velocity.GetValue(fx) as Camera);
            }
            if (depth != null) Add("fx-depth", depth.depthCamera);
            Camera.onPreCull += PreCull; Camera.onPostRender += PostRender; attached = true;
        }
        T Symbol<T>(string name) where T : class
        {
            IntPtr pointer = dlsym(library, name);
            if (pointer == IntPtr.Zero) throw new MissingMethodException(name);
            return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
        }
        void Add(string role, Camera camera)
        {
            if (camera == null || cameras.ContainsKey(camera)) return;
            var pair = new Pair { camera = camera, begin = new CommandBuffer { name = "Continuum GPU begin " + role }, end = new CommandBuffer { name = "Continuum GPU end " + role },
                row = new CameraRow { role = role, name = camera.name, instanceId = camera.GetInstanceID(), renderingPath = camera.actualRenderingPath.ToString() } };
            cameras.Add(camera, pair);
            camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, pair.begin); camera.AddCommandBuffer(CameraEvent.AfterEverything, pair.end);
        }
        int Token(int kind, int camera)
        {
            int token = enqueue(kind, camera, Time.frameCount);
            if (token <= 0) { report.enqueueFailures++; throw new InvalidOperationException("Native diagnostic event queue rejected."); }
            return token;
        }
        void PreCull(Camera camera)
        {
            Pair pair;
            if (stopping || finished || !cameras.TryGetValue(camera, out pair)) return;
            try
            {
                pair.row.preCull++; pair.row.lastFrame = Time.frameCount;
                if (pair.row.firstFrame < 0) pair.row.firstFrame = Time.frameCount;
                pair.begin.Clear(); pair.end.Clear(); pair.row.renderingPath = camera.actualRenderingPath.ToString();
                if (camera.actualRenderingPath != RenderingPath.Forward) { pair.row.declinedPath++; return; }
                pair.begin.IssuePluginEvent(callback, Token(0, pair.row.instanceId)); initQueued = true;
                pair.begin.IssuePluginEvent(callback, Token(1, pair.row.instanceId));
                pair.end.IssuePluginEvent(callback, Token(2, pair.row.instanceId)); pair.end.IssuePluginEvent(callback, Token(3, pair.row.instanceId));
                pair.row.queuedPairs++;
            }
            catch (Exception error) { Fault("camera: " + error.Message); Detach(); }
        }
        void PostRender(Camera camera) { Pair pair; if (cameras.TryGetValue(camera, out pair)) pair.row.postRender++; }
        string NativeJson()
        {
            int needed = snapshot(snapshotBuffer, snapshotBuffer.Length);
            if (needed <= 0 || needed > snapshotBuffer.Length || snapshotBuffer[needed - 1] != 0) throw new InvalidDataException("Native diagnostic snapshot exceeds bound or lacks terminator.");
            return Encoding.UTF8.GetString(snapshotBuffer, 0, needed - 1);
        }
        public void LateUpdate()
        {
            if (snapshot == null || finished || stopping || Time.frameCount - lastSnapshotFrame < 8) return;
            lastSnapshotFrame = Time.frameCount;
            try
            {
                if (snapshots.Count >= 256) snapshotDrops++;
                else snapshots.Add(NativeJson());
            }
            catch (Exception error) { Fault("periodic-snapshot: " + error.Message); }
        }
        public static bool StopOwned(Action<bool> complete)
        {
            if (active == null || active.finished) return false;
            if (active.stopping) return true;
            active.stopping = true; active.Detach(); active.StartCoroutine(active.Stop(complete)); return true;
        }
        IEnumerator Stop(Action<bool> complete)
        {
            bool ok = false;
            try
            {
                if (snapshot != null && callback != IntPtr.Zero && initQueued)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        GL.IssuePluginEvent(callback, Token(3, 0)); yield return new WaitForEndOfFrame();
                        report.nativeJson = NativeJson();
                        if (JsonUtility.FromJson<NativeState>(report.nativeJson).pending == 0) break;
                    }
                    GL.IssuePluginEvent(callback, Token(4, 0));
                    for (int i = 0; i < 4; i++)
                    {
                        yield return new WaitForEndOfFrame(); report.nativeJson = NativeJson();
                        if (JsonUtility.FromJson<NativeState>(report.nativeJson).status == "cleaned") { ok = true; break; }
                    }
                }
            }
            finally
            {
                finished = true; report.cleanupObserved = ok;
                if (report.reason == null) { report.status = ok ? "complete-diagnostic" : "incomplete-diagnostic"; report.reason = ok ? "render-context-cleanup-observed" : "cleanup-or-context-not-observed"; }
                try { Export(); } finally { complete(ok && report.status == "complete-diagnostic"); }
            }
        }
        void Fault(string reason) { if (report == null) report = new Report(); report.status = "failed-diagnostic"; report.reason = reason; }
        void Detach()
        {
            if (attached) { Camera.onPreCull -= PreCull; Camera.onPostRender -= PostRender; attached = false; }
            foreach (Pair pair in cameras.Values)
            {
                if (pair.camera != null) { pair.camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, pair.begin); pair.camera.RemoveCommandBuffer(CameraEvent.AfterEverything, pair.end); }
                pair.begin.Clear(); pair.end.Clear();
            }
        }
        void Export()
        {
            if (report == null || directory == null) return;
            if (snapshot != null) try { report.nativeJson = NativeJson(); } catch (Exception error) { Fault("snapshot: " + error.Message); }
            report.cameras = cameras.Values.Select(pair => pair.row).OrderBy(row => row.role).ToArray();
            report.nativeSnapshots = snapshots.ToArray(); report.snapshotDrops = snapshotDrops;
            File.WriteAllText(Path.Combine(directory, "gpu-timing.json"), ReportJson.Encode(report));
        }
        public void OnApplicationQuit()
        {
            if (finished) return;
            Detach(); Fault("interrupted-before-render-context-cleanup"); Export();
        }
        public void OnDestroy()
        {
            Detach(); foreach (Pair pair in cameras.Values) { pair.begin.Release(); pair.end.Release(); }
            if (active == this) active = null;
        }
    }
}
