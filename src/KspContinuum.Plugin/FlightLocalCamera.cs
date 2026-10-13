using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal static class FlightLocalCamera
    {
        [Serializable] internal sealed class State
        {
            public bool enabled, activeSelf, activeInHierarchy, isPQS;
            public float nearClip, farClip, fieldOfView, depth;
            public int cullingMask, clearFlags, depthTextureMode, renderingPath, targetId;
        }
        [Serializable] internal sealed class CameraRow
        {
            public int instanceId, flightCameraIndex, preCullCount, preRenderCount, postRenderCount;
            public string name; public string[] componentTypes, commandNames;
            public State original, effective, restored;
            public float[] originalProjection, effectiveProjection, restoredProjection;
        }
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-local-camera/v1", mode, runId, vesselId, status, reason, cleanupStatus;
            public string scope = "Experimental stock D3D11 local-camera partition applied to a pinned OpenGL quiet-vacuum diagnostic. Depth precision and visual fidelity are unqualified. Callback counts are window-local; observerTicks includes lifecycle audits and is nested wall cost.";
            public string provenance = "Pinned FlightCamera.Start: Camera 00 far=750000, Camera 01 inactive, UnderwaterFog PQS role transferred to Camera 00.";
            public string controlledClockScope;
            public int processId, parts, faults, observedFrames, coverageChecks, windowStartFrame, windowEndFrame;
            public double maxProjectionError;
            public long windowStartClockTimestamp, windowEndClockTimestamp, clockFrequency, observerTicks;
            public bool hooksRemoved, restorationComplete, globalsUnchanged;
            public int[] originalLocalCameraPosBits, originalLocalCameraDirBits, endLocalCameraPosBits, endLocalCameraDirBits;
            public CameraRow camera00, camera01;
        }
        static Report report; static Camera near, far; static UnderwaterFog nearFog, farFog;
        static FlightCamera flight; static Vessel vessel; static CelestialBody body; static string output;
        static bool attached, sceneAttached, window, finished, changed;
        static bool controlledClock;
        sealed class SceneListener { public void Changed(GameScenes next) { Fault("Scene change before local-camera completion."); } }
        static readonly SceneListener sceneListener = new SceneListener();
        [StructLayout(LayoutKind.Explicit)] struct FloatBits { [FieldOffset(0)] public float value; [FieldOffset(0)] public int bits; }
        static int Raw(float value) { return new FloatBits { value=value }.bits; } static int lastFrame = -1;
        public static string FailureReason { get; private set; }
        static State Read(Camera camera, UnderwaterFog fog)
        {
            return new State { enabled = camera.enabled, activeSelf = camera.gameObject.activeSelf, activeInHierarchy = camera.gameObject.activeInHierarchy,
                isPQS = fog.isPQS, nearClip = camera.nearClipPlane, farClip = camera.farClipPlane, fieldOfView = camera.fieldOfView, depth = camera.depth,
                cullingMask = camera.cullingMask, clearFlags = (int)camera.clearFlags, depthTextureMode = (int)camera.depthTextureMode,
                renderingPath = (int)camera.renderingPath, targetId = camera.targetTexture == null ? 0 : camera.targetTexture.GetInstanceID() };
        }
        static bool Same(State a, State b)
        {
            return a.enabled == b.enabled && a.activeSelf == b.activeSelf && a.activeInHierarchy == b.activeInHierarchy && a.isPQS == b.isPQS &&
                a.nearClip == b.nearClip && a.farClip == b.farClip && a.fieldOfView == b.fieldOfView && a.depth == b.depth && a.cullingMask == b.cullingMask &&
                a.clearFlags == b.clearFlags && a.depthTextureMode == b.depthTextureMode && a.renderingPath == b.renderingPath && a.targetId == b.targetId;
        }
        static int[] Bits(string name)
        {
            Vector4 v = Shader.GetGlobalVector(name);
            return new[] { Raw(v.x), Raw(v.y), Raw(v.z), Raw(v.w) };
        }
        static bool GlobalSame(string name, int[] bits)
        { Vector4 value=Shader.GetGlobalVector(name); return Raw(value.x)==bits[0] && Raw(value.y)==bits[1] && Raw(value.z)==bits[2] && Raw(value.w)==bits[3]; }
        static bool Matches(Camera camera, UnderwaterFog fog, State state)
        {
            return camera.enabled==state.enabled && camera.gameObject.activeSelf==state.activeSelf && camera.gameObject.activeInHierarchy==state.activeInHierarchy && fog.isPQS==state.isPQS &&
                camera.nearClipPlane==state.nearClip && camera.farClipPlane==state.farClip && camera.fieldOfView==state.fieldOfView && camera.depth==state.depth && camera.cullingMask==state.cullingMask &&
                (int)camera.clearFlags==state.clearFlags && (int)camera.depthTextureMode==state.depthTextureMode && (int)camera.renderingPath==state.renderingPath && (camera.targetTexture==null?0:camera.targetTexture.GetInstanceID())==state.targetId;
        }
        static bool Equal(int[] a, int[] b) { for (int i=0;i<4;i++) if(a[i]!=b[i]) return false; return true; }
        static bool InactiveFog(int[] a, int w) { return a[0]==0x3f800000 && a[1]==0x3f800000 && a[2]==0x3f800000 && a[3]==w; }
        static string[] Commands(Camera camera)
        {
            var names = new List<string>();
            foreach (CameraEvent value in Enum.GetValues(typeof(CameraEvent))) foreach (CommandBuffer buffer in camera.GetCommandBuffers(value)) names.Add(value+":"+buffer.name);
            return names.ToArray();
        }
        static float[] Projection(Camera camera)
        {
            Matrix4x4 matrix=camera.projectionMatrix; var values=new float[16]; for(int i=0;i<16;i++) values[i]=matrix[i]; return values;
        }
        static void ProjectionGuard(Camera camera)
        {
            Matrix4x4 expected=Matrix4x4.Perspective(camera.fieldOfView,camera.aspect,camera.nearClipPlane,camera.farClipPlane), actual=camera.projectionMatrix;
            for(int i=0;i<16;i++) { double error=Math.Abs(expected[i]-actual[i]); Require(!double.IsNaN(error) && !double.IsInfinity(error) && error<=0.00001,"Unexpected/custom actual camera projection."); report.maxProjectionError=Math.Max(report.maxProjectionError,error); }
        }
        static CameraRow Inventory(Camera camera, UnderwaterFog fog, int index)
        {
            var names = new List<string>(); foreach(Component c in camera.GetComponents<Component>())
            { if(c==null) throw new InvalidOperationException("Missing camera component."); names.Add(c.GetType().FullName); }
            return new CameraRow { instanceId=camera.GetInstanceID(), flightCameraIndex=index, name=camera.name,
                componentTypes=names.ToArray(), commandNames=Commands(camera), original=Read(camera,fog), originalProjection=Projection(camera) };
        }
        static void Require(bool condition, string reason) { if(!condition) throw new InvalidOperationException(reason); }
        public static void Start(Vessel active, string directory, string runId)
        {
            string mode=null; string[] arguments=Environment.GetCommandLineArgs();
            foreach(string argument in arguments) if(argument.StartsWith("--continuum-flight-local-camera=",StringComparison.Ordinal))
            { Require(mode==null,"Duplicate local-camera selection."); mode=argument.Substring("--continuum-flight-local-camera=".Length); }
            if(mode==null) return;
            Require(mode=="stock" || mode=="merged","Invalid local-camera mode.");
            Require(!attached && (report==null || finished),"Local-camera lifecycle already owned.");
            FailureReason=null; finished=false; changed=false; window=false; lastFrame=-1; near=null; far=null; nearFog=null; farFog=null; flight=null; vessel=active; body=active==null?null:active.mainBody;
            Require(Path.IsPathRooted(directory) && !string.IsNullOrEmpty(runId),"Bound output and run required.");
            output=Path.Combine(directory,"local-camera.json"); Require(!File.Exists(output),"Local-camera output already exists.");
            report=new Report { mode=mode,runId=runId,processId=Process.GetCurrentProcess().Id,vesselId=active==null?null:active.id.ToString(),
                parts=active==null?0:active.parts.Count,status="preparing",clockFrequency=Stopwatch.Frequency };
            string rigidOwner=Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_OWNER");
            controlledClock=mode=="stock" && Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_SCENARIO")=="control-warp" && !string.IsNullOrEmpty(rigidOwner) && Path.IsPathRooted(rigidOwner);
            report.controlledClockScope=controlledClock?"Explicit control-warp stock-camera observer: normal rate1/index0/timeScale1/native dt0.02 or LOW rate4/index3/timeScale4/native dt0.08; camera, vacuum and rendering guards unchanged.":"Original normal-time-only local-camera context.";
            try
            {
                StartupPinnedGameIdentity.Require(typeof(FlightCamera).Assembly);
                Require(Array.IndexOf(arguments,"--continuum-flight-diagnostic")>=0,"Local camera requires diagnostic controls.");
                bool unsupported=false;
                for(int i=0;i<arguments.Length;i++)
                {
                    string argument=arguments[i];
                    if(argument=="--continuum-normal-atmosphere" || argument=="--continuum-atmospheric-stress" || argument=="--continuum-physics-warp-pressure" || argument.StartsWith("--continuum-flight-entry-target",StringComparison.Ordinal) || argument=="--continuum-flight-part-retained" || argument=="--continuum-flight-camera-chain" || argument.StartsWith("--continuum-flight-gpu-timing-library=",StringComparison.Ordinal)) unsupported=true;
                    if(argument=="--continuum-flight-perturbation" && (i+1>=arguments.Length || arguments[i+1]!="quiet")) unsupported=true;
                    if(argument.StartsWith("--continuum-flight-perturbation=",StringComparison.Ordinal) && argument!="--continuum-flight-perturbation=quiet") unsupported=true;
                }
                if(unsupported && mode=="stock")
                {
                    report.status="not-applicable"; report.reason="declared-unsupported-stock-workload";
                    report.hooksRemoved=true; report.restorationComplete=true; report.cleanupStatus="not-applicable-no-owned-hooks-or-mutations";
                    finished=true; Export(); return;
                }
                Require(!unsupported,"Local camera excludes declared atmosphere/entry/warp/pulse treatment.");
                Context(); flight=FlightCamera.fetch;
                Require(flight!=null && flight.cameras!=null && flight.cameras.Length==2,"Expected exactly two owned local cameras.");
                near=flight.cameras[0]; far=flight.cameras[1];
                Require(near!=null && far!=null && near!=far && near.name=="Camera 00" && far.name=="Camera 01" && flight.mainCamera==near,"Local camera identities differ.");
                nearFog=near.GetComponent<UnderwaterFog>(); farFog=far.GetComponent<UnderwaterFog>();
                Require(nearFog!=null && farFog!=null && !nearFog.isPQS && farFog.isPQS,"Expected original OpenGL fog roles.");
                report.camera00=Inventory(near,nearFog,0); report.camera01=Inventory(far,farFog,1);
                Require(report.camera01.commandNames.Length==0 && far.transform.childCount==0,"Far-camera command/child ownership unsupported.");
                foreach(string name in report.camera01.componentTypes) Require(name=="UnityEngine.Transform" || name=="UnityEngine.Camera" || name=="UnderwaterFog","Unexpected far-camera component: "+name);
                foreach(State s in new[] {report.camera00.original,report.camera01.original}) Require(s.enabled && s.activeSelf && s.activeInHierarchy && s.targetId==0,"Original local camera must render to screen.");
                Require(near.nearClipPlane==0.21f && near.farClipPlane==400f && far.nearClipPlane==397f && far.farClipPlane==750000f,"Original clip partition differs.");
                SharedView();
                report.originalLocalCameraPosBits=Bits("_LocalCameraPos"); report.originalLocalCameraDirBits=Bits("_LocalCameraDir");
                Require(InactiveFog(report.originalLocalCameraPosBits,0x7f800000) && InactiveFog(report.originalLocalCameraDirBits,0),"Quiet-vacuum stock fog globals differ from captured inactive profile.");
                Camera.onPreCull+=PreCull; Camera.onPreRender+=PreRender; Camera.onPostRender+=PostRender;
                attached=true; GameEvents.onGameSceneLoadRequested.Add(sceneListener.Changed); sceneAttached=true;
                if(mode=="merged") { changed=true; near.farClipPlane=750000f; nearFog.isPQS=true; farFog.isPQS=false; far.gameObject.SetActive(false); }
                report.camera00.effective=Read(near,nearFog); report.camera01.effective=Read(far,farFog); report.camera00.effectiveProjection=Projection(near); report.camera01.effectiveProjection=Projection(far); report.status="observing-experimental";
                Audit();
            }
            catch(Exception error) { Fault(error.Message); throw; }
        }
        static void Context()
        {
            bool clock=TimeWarp.CurrentRate==1;
            if(controlledClock)
            {
                string rigidOwner=Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_OWNER");
                Require(report.mode=="stock" && Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_SCENARIO")=="control-warp" && !string.IsNullOrEmpty(rigidOwner) && Path.IsPathRooted(rigidOwner),"Controlled local-camera declaration changed.");
                bool normal=TimeWarp.CurrentRate==1 && TimeWarp.CurrentRateIndex==0 && Time.timeScale==1 && Time.fixedDeltaTime==.02f;
                bool low=TimeWarp.WarpMode==TimeWarp.Modes.LOW && TimeWarp.CurrentRate==4 && TimeWarp.CurrentRateIndex==3 && Time.timeScale==4 && Time.fixedDeltaTime==.08f;
                clock=normal||low;
            }
            Require(HighLogic.LoadedScene==GameScenes.FLIGHT && !MapView.MapIsEnabled && vessel!=null && vessel==FlightGlobals.ActiveVessel && vessel.loaded && !vessel.packed &&
                vessel.parts.Count==report.parts && vessel.mainBody==body && body!=null && vessel.altitude>body.atmosphereDepth && vessel.atmDensity==0 &&
                clock && SystemInfo.graphicsDeviceType==GraphicsDeviceType.OpenGLCore,"Quiet physical vacuum/OpenGL/declared-clock context drift.");
        }
        static void SharedView()
        {
            ProjectionGuard(near); ProjectionGuard(far);
            Require(!near.orthographic && !far.orthographic && near.cullingMask==far.cullingMask && near.fieldOfView==60f && near.fieldOfView==far.fieldOfView && near.aspect==far.aspect && near.rect==far.rect &&
                (near.transform.position-far.transform.position).sqrMagnitude<=1e-8f && Quaternion.Angle(near.transform.rotation,far.transform.rotation)<=0.001f,"Local ray projection/pose/mask differs.");
        }
        static void Audit()
        {
            long start=Stopwatch.GetTimestamp();
            try
            {
                Context(); Require(FlightCamera.fetch==flight && flight.cameras.Length==2 && flight.cameras[0]==near && flight.cameras[1]==far && flight.mainCamera==near && near!=null && far!=null && nearFog!=null && farFog!=null,"Local camera owner drift.");
                SharedView(); Require(Matches(near,nearFog,report.camera00.effective) && Matches(far,farFog,report.camera01.effective),"Local camera state drift.");
                Require(far.commandBufferCount==0,"Far-camera command buffers appeared.");
                Require(GlobalSame("_LocalCameraPos",report.originalLocalCameraPosBits) && GlobalSame("_LocalCameraDir",report.originalLocalCameraDirBits),"Frozen inactive fog globals changed.");
                if(window) { report.coverageChecks++; if(lastFrame!=Time.frameCount) { lastFrame=Time.frameCount; report.observedFrames++; } }
            }
            finally { report.observerTicks+=Stopwatch.GetTimestamp()-start; }
        }
        static CameraRow Row(Camera c) { return c==near?report.camera00:c==far?report.camera01:null; }
        static void PreCull(Camera c)
        {
            if(!attached || Row(c)==null) return;
            try { if(c==near) Audit(); else Require(report.mode=="stock","Retired far camera rendered."); if(window) Row(c).preCullCount++; }
            catch(Exception error) { Fault(error.Message); }
        }
        static void PreRender(Camera c) { if(attached && window && Row(c)!=null) Row(c).preRenderCount++; }
        static void PostRender(Camera c) { if(attached && window && Row(c)!=null) Row(c).postRenderCount++; }
        public static void BeginWindow(long clock)
        {
            if(report==null || finished) return; Require(attached,"Local-camera observer absent at window start."); Audit();
            report.windowStartClockTimestamp=clock; report.windowStartFrame=Time.frameCount; window=true;
        }
        public static void EndWindow(long clock)
        {
            if(report==null || report.windowStartClockTimestamp==0 || report.windowEndClockTimestamp!=0) return;
            window=false; report.windowEndClockTimestamp=clock; report.windowEndFrame=Time.frameCount;
            if(attached) try { Audit(); } catch(Exception error) { Fault(error.Message); }
        }
        static void Fault(string reason)
        {
            if(report==null) return; report.faults++; report.reason=reason; report.status="invalid"; FailureReason=reason;
            Restore(); Export();
        }
        static void Restore()
        {
            if(attached) { Camera.onPreCull-=PreCull; Camera.onPreRender-=PreRender; Camera.onPostRender-=PostRender; attached=false; }
            if(sceneAttached) { GameEvents.onGameSceneLoadRequested.Remove(sceneListener.Changed); sceneAttached=false; }
            report.hooksRemoved=true;
            try
            {
                Require(near!=null && far!=null && nearFog!=null && farFog!=null && report.camera00!=null && report.camera01!=null,"Camera owner missing during restoration.");
                if(changed) { near.farClipPlane=report.camera00.original.farClip; nearFog.isPQS=report.camera00.original.isPQS; farFog.isPQS=report.camera01.original.isPQS; far.gameObject.SetActive(report.camera01.original.activeSelf); changed=false; }
                report.camera00.restored=Read(near,nearFog); report.camera01.restored=Read(far,farFog); report.camera00.restoredProjection=Projection(near); report.camera01.restoredProjection=Projection(far);
                report.restorationComplete=Same(report.camera00.original,report.camera00.restored) && Same(report.camera01.original,report.camera01.restored);
                if(!report.restorationComplete) throw new InvalidOperationException("Original local-camera state not restored.");
                report.cleanupStatus="removed-owned-hooks-restored-original-camera-state";
            }
            catch(Exception error) { report.faults++; report.cleanupStatus="restoration-error"; report.status="invalid"; FailureReason=error.Message; report.reason=error.Message; }
        }
        static void Export() { if(report!=null && output!=null) File.WriteAllText(output,ReportJson.Encode(report)); }
        public static void Finish(string status)
        {
            if(report==null || finished) return; finished=true; window=false;
            report.endLocalCameraPosBits=Bits("_LocalCameraPos"); report.endLocalCameraDirBits=Bits("_LocalCameraDir");
            report.globalsUnchanged=report.originalLocalCameraPosBits!=null && report.originalLocalCameraDirBits!=null && Equal(report.originalLocalCameraPosBits,report.endLocalCameraPosBits) && Equal(report.originalLocalCameraDirBits,report.endLocalCameraDirBits);
            Restore();
            bool observed=report.camera00!=null && report.camera01!=null && report.camera00.preCullCount>0 && report.camera00.postRenderCount>0 &&
                (report.mode=="stock" ? report.camera01.preCullCount>0 && report.camera01.postRenderCount>0 : report.camera01.preCullCount==0 && report.camera01.preRenderCount==0 && report.camera01.postRenderCount==0);
            if(status=="complete" && report.faults==0 && report.restorationComplete && report.globalsUnchanged && observed && report.windowEndClockTimestamp>report.windowStartClockTimestamp) report.status="completed-experimental";
            else { report.status="invalid"; if(FailureReason==null) FailureReason=report.reason="Incomplete local-camera evidence."; }
            Export();
        }
    }
}
