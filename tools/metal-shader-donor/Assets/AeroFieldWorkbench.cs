using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KspContinuum;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class AeroFieldWorkbench : MonoBehaviour
{
    [Serializable] public sealed class Fixture
    {
        public string schema, runId, vesselId, profile, geometrySha256, geometry;
        public int rows, vertices, indices;
        public float[] cameraPosition, cameraRotation, flowDirection, effectColor, effectParameters, textureParameters, falloffs;
        public float fov, near, far;
    }
    [Serializable] public sealed class Publication { public string schema, generation, bundle, bundleSha256; }
    [Serializable] sealed class State
    {
        public string schema = "continuum-aero-workbench/v1", hostId, renderer, unity, renderPath, geometrySource, fixtureSha256, bundleSha256, generation, reloadError, lastPng;
        public string contract = "Frozen gray opaque fixture or labelled analytic geometry; isolated experimental fields, no stock appearance equivalence.";
        public int processId, port, frames, fieldRenders, flowRenders, composites, ribbonSubmissions, shaderReloads, fixtureReloads, commandErrors;
        public int viewOccupiedPixels, flowOccupiedPixels, sceneOccupiedPixels;
        public bool fieldPixelProof;
        public bool splashFinishedAtCapture;
        public string screenshotTarget = "explicit-screen-backbuffer";
        public float cameraYaw, cameraPitch, cameraDistance, intensity, effectClock;
        public float[] flowDirection, cameraPosition, cameraRotation;
        public bool paused; public string[] shaders;
        public bool benchmarkActive; public string lastBenchmark,lastBenchmarkStatus,lastBenchmarkReason, pipelineMode = "full";
        public string materialMode, materialDiagnostic, materialTopologyId, materialStateSha256, materialBundleSha256, materialOriginalBundleSha256;
        public long materialGeneration;
        public int materialInstances, materialMeshes, materialPackets, materialDrawCalls, materialSubmittedFrames, materialSnapshotPublications, materialOccupiedPixels;
        public bool materialPixelProof;
        public string projection="perspective"; public float fov,verticalSpanMeters,near,far; public double pixelsPerMeterAtFocus; public int viewportWidth,viewportHeight;
        public ScientificOverlay.StatusData scientificOverlay;
        public string orthographicShaderContract="continuum-aero-field-orthographic/v1",orthographicShaderAdmission;
        public bool orthographicShadersSupported;
        public string[] orthographicShaderNames; public bool[] orthographicShaderPropertyPresent; public float[] orthographicShaderContractValues;
        public float[] viewForward;
    }
    [Serializable] sealed class Benchmark
    {
        public string schema="continuum-aero-workbench-benchmark/v1",status,reason,hostId,renderer,graphicsVersion,unity,renderPath,pipelineMode,geometrySource,fixtureSha256,bundleSha256,generation,assemblySha256,frozenControlsJson;
        public string scope="Consecutive workbench Update boundaries containing one fully witnessed prior rendered frame; inclusive main-thread wall intervals, not GPU duration or KSP gameplay.";
        public int processId,width,height,vSyncCount,targetFrameRate,antiAliasing,qualityLevel,sampleCount,omittedDeadlineStraddlers,commandsDuringCapture,capacity=65536;
        public int geometryRendererCount,geometryVertexCount,geometryTriangleCount;
        public string geometrySubmissionScope="Static gray fixture, unbatched MeshRenderer submission workload; not the admitted KSP packet renderer.";
        public string colorSpace,processResourceStatus,processResourceError; public bool paused,graphicsUVStartsAtTop;
        public float cameraProjectionM11,gpuProjectionToTextureM11,cameraFov,cameraNear,cameraFar;
        public float[] effectColor,effectParameters,textureParameters,falloffs;
        public string processResourceScope="Whole process CPU and RSS sampled outside retained frame intervals; resource window may include final omitted deadline straddler, not GPU duration.";
        public long processResourceStartTicks,processResourceEndTicks,workingSetStartBytes,workingSetEndBytes;
        public double processCpuSeconds;
        public double warmupSeconds,requestedCaptureSeconds,measuredSeconds;
        public long frequency,windowStartTicks,windowEndTicks;
        public double[] frameMilliseconds;
        public long[] frameStartedTicks,frameEndedTicks;
        public int[] renderedFrames;
    }
    sealed class Command { public string text, response; public volatile bool done; }
    readonly ConcurrentQueue<Command> commands = new ConcurrentQueue<Command>();
    readonly List<Mesh> meshes = new List<Mesh>();
    readonly Matrix4x4[] seeds = new Matrix4x4[256];
    Camera view, field, flow; GameObject geometryRoot;
    readonly ScientificOverlay scientificOverlay=new ScientificOverlay();
    Bounds geometryBounds; Material opaque, coverage, flowMaterial, sheath, ribbon, depthCopy;
    AssetBundle bundle; Mesh quad; Texture2D noise;
    RenderTexture fieldMap, flowMap, sceneMap;
    CommandBuffer ribbonCommands, sceneCommands;
    TcpListener listener; Thread worker; volatile bool stopping;
    int queued, smokeFrames, capturesPending; float clock, nextPoll;
    string activeBundlePath;
    ResidentMaterialRuntime resident;
    Benchmark benchmark; long benchmarkWarmupEnd,benchmarkDeadline,benchmarkPreviousTicks; int benchmarkPreviousFrame,lastRenderedFrame=-1,renderedInFrame;
    bool benchmarkCapturing,priorPaused; int priorVSync,priorFrameRate;
    RenderingPath benchmarkRenderPath,observedRenderPath; ColorSpace benchmarkColorSpace; bool renderPathRecorded;
    System.Diagnostics.Process benchmarkProcess; TimeSpan benchmarkCpuStart;
    string output, publicationPath, publicationSeen, fixturePath, fixtureSeen;
    Vector3 direction = Vector3.down;
    Color effectColor = new Color(1,.3f,.05f,1);
    Vector4 effect = new Vector4(1,2,1,1), texture = new Vector4(1,2,.3f,0), falloffs = Vector4.one;
    readonly State state = new State();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_SCENE"))) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_OUTPUT")))
            new GameObject("Continuum Aero Field Workbench").AddComponent<AeroFieldWorkbench>();
    }
    public static string HashFile(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    static string Absolute(string path) { if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidDataException("Absolute private path required."); return Path.GetFullPath(path); }
    static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    static void Check(Vector3 v) { if (!Finite(v.x)||!Finite(v.y)||!Finite(v.z)) throw new InvalidDataException("Nonfinite vector."); }
    static float Number(string text) { float v = float.Parse(text, CultureInfo.InvariantCulture); if (!Finite(v)) throw new InvalidDataException("Nonfinite argument."); return v; }
    static Vector3 Vector(float[] a) { if (a == null || a.Length != 3) throw new InvalidDataException("Vector layout."); var v = new Vector3(a[0],a[1],a[2]); Check(v); return v; }
    static Vector4 Vector4Of(float[] a) { if (a == null || a.Length != 4) throw new InvalidDataException("Parameter layout."); foreach(float v in a) if(!Finite(v)) throw new InvalidDataException("Nonfinite parameter."); return new Vector4(a[0],a[1],a[2],a[3]); }
    void Start()
    {
        try
        {
            output = Absolute(Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_OUTPUT"));
            if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("New private output directory required.");
            Directory.CreateDirectory(output);
            state.hostId = Guid.NewGuid().ToString("N"); state.processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            state.renderer = SystemInfo.graphicsDeviceType.ToString(); state.unity = Application.unityVersion;
            if (Application.unityVersion != "2019.4.18f1" || !SystemInfo.supportsInstancing || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
                throw new InvalidOperationException("Pinned editor, instancing and float field formats required.");
            foreach(Camera old in FindObjectsOfType<Camera>()) old.enabled = false;
            view = gameObject.AddComponent<Camera>(); view.renderingPath = RenderingPath.Forward; view.depthTextureMode = DepthTextureMode.Depth;
            view.backgroundColor = new Color(.035f,.045f,.07f); view.clearFlags = CameraClearFlags.SolidColor; view.cullingMask = 1 << 28;
            field = new GameObject("View field camera").AddComponent<Camera>(); field.enabled = false;
            flow = new GameObject("Flow field camera").AddComponent<Camera>(); flow.enabled = false;
            Shader fixtureShader=Shader.Find("Continuum/FixtureOpaque");
            if(fixtureShader==null||!fixtureShader.isSupported) throw new InvalidOperationException("Depth-capable original fixture shader unavailable.");
            opaque = new Material(fixtureShader); opaque.color = new Color(.25f,.28f,.32f);
            quad = new Mesh { name = "original ribbon quad" }; quad.vertices = new[] { new Vector3(-.5f,-.5f,0),new Vector3(.5f,-.5f,0),new Vector3(.5f,.5f,0),new Vector3(-.5f,.5f,0) };
            quad.uv = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.up }; quad.triangles = new[] { 0,1,2,0,2,3 }; quad.RecalculateBounds();
            noise = new Texture2D(32,32,TextureFormat.RGBA32,false,true); noise.wrapMode = TextureWrapMode.Repeat;
            var colors = new Color[1024]; var random = new System.Random(190418); for(int i=0;i<colors.Length;i++) colors[i]=Color.white*Mathf.Lerp(.35f,1f,(float)random.NextDouble()); noise.SetPixels(colors); noise.Apply();
            for(int i=0;i<seeds.Length;i++) seeds[i]=Matrix4x4.identity;
            fixturePath = Environment.GetEnvironmentVariable("CONTINUUM_AERO_FIXTURE");
            if (!string.IsNullOrEmpty(fixturePath)) LoadFixture(Absolute(fixturePath), true); else Analytic();
            publicationPath = Environment.GetEnvironmentVariable("CONTINUUM_AERO_PUBLICATION");
            if (!string.IsNullOrEmpty(publicationPath)) { publicationPath=Absolute(publicationPath); ReloadPublication(); }
            else ReloadBundle(Absolute(Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_BUNDLE") ?? Environment.GetEnvironmentVariable("CONTINUUM_AERO_SHADER_BUNDLE")), null, "direct");
            smokeFrames = 0; string smoke=Environment.GetEnvironmentVariable("CONTINUUM_AERO_SMOKE_FRAMES"); if(!string.IsNullOrEmpty(smoke)) { smokeFrames=int.Parse(smoke); if(smokeFrames<3||smokeFrames>300) throw new InvalidDataException("Smoke frames3..300."); }
            int port = 0; string portText=Environment.GetEnvironmentVariable("CONTINUUM_AERO_DEBUG_PORT"); if(!string.IsNullOrEmpty(portText)) port=int.Parse(portText);
            if(port<0||port>65535) throw new InvalidDataException("Port range.");
            listener = new TcpListener(IPAddress.Loopback,port); listener.Start(4); state.port=((IPEndPoint)listener.LocalEndpoint).Port;
            worker = new Thread(SocketLoop) { IsBackground=true, Name="Continuum bounded debug socket" }; worker.Start();
            WriteStatus();
        }
        catch(Exception error) { state.reloadError=error.ToString(); if(!string.IsNullOrEmpty(output)&&Directory.Exists(output)) WriteStatus(); Debug.LogException(error); Application.Quit(2); enabled=false; }
    }
    void SocketLoop()
    {
        while(!stopping)
        {
            try
            {
                using(TcpClient client=listener.AcceptTcpClient())
                using(NetworkStream stream=client.GetStream())
                {
                    stream.ReadTimeout=2000; stream.WriteTimeout=2000;
                    var bytes=new List<byte>(); int b;
                    while((b=stream.ReadByte())>=0&&b!='\n') { if(bytes.Count>=4096) throw new InvalidDataException("Command too large."); bytes.Add((byte)b); }
                    if(bytes.Count==0) continue;
                    if(Interlocked.Increment(ref queued)>16) { Interlocked.Decrement(ref queued); throw new InvalidDataException("Command queue full."); }
                    var command=new Command { text=Encoding.UTF8.GetString(bytes.ToArray()).Trim() }; commands.Enqueue(command);
                    for(int wait=0;wait<500&&!command.done&&!stopping;wait++) Thread.Sleep(10);
                    if(command.done) { byte[] reply=Encoding.UTF8.GetBytes(command.response+"\n"); stream.Write(reply,0,reply.Length); }
                }
            }
            catch(Exception) { if(stopping) return; }
        }
    }
    void Update()
    {
        if(view==null||coverage==null) return;
        if(state.benchmarkActive) TickBenchmark();
        if(!state.paused) clock+=Time.unscaledDeltaTime;
        Command command; for(int i=0;i<8&&commands.TryDequeue(out command);i++)
        {
            Interlocked.Decrement(ref queued);
            if(state.benchmarkActive&&benchmarkCapturing) benchmark.commandsDuringCapture++;
            try { Execute(command.text); command.response=state.benchmarkActive?"{\"benchmarkActive\":true,\"hostId\":\""+state.hostId+"\"}":JsonUtility.ToJson(state); }
            catch(Exception error) { state.commandErrors++; command.response="{\"error\":\""+Escape(error.Message)+"\",\"hostId\":\""+state.hostId+"\"}"; }
            command.done=true;
        }
        if(!state.benchmarkActive&&Time.realtimeSinceStartup>=nextPoll)
        {
            nextPoll=Time.realtimeSinceStartup+1;
            try { if(!string.IsNullOrEmpty(publicationPath)&&HashFile(publicationPath)!=publicationSeen) ReloadPublication(); } catch(Exception e) { state.reloadError=e.Message; }
            try { if(!string.IsNullOrEmpty(fixturePath)&&HashFile(fixturePath)!=fixtureSeen) LoadFixture(fixturePath,false); } catch(Exception e) { state.reloadError=e.Message; }
            WriteStatus();
        }
    }
    void OnPreCull()
    {
        if(resident!=null) try { resident.Submit(view); } catch(Exception error) { state.reloadError="material-submit: "+error; if(state.benchmarkActive) EndBenchmark("invalid","material-submit-failed"); WriteStatus(); Application.Quit(2); enabled=false; }
    }
    static string Escape(string v) { return v.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","\\r"); }
    void Execute(string text)
    {
        string[] words=text.Split(new[] {' '},StringSplitOptions.RemoveEmptyEntries); if(words.Length==0) throw new InvalidDataException("Empty command.");
        string operation=words[0].ToLowerInvariant();
        if(state.benchmarkActive&&operation!="status"&&operation!="quit"&&operation!="cancelbenchmark") throw new InvalidOperationException("Benchmark freezes controls, captures, fixtures and shaders.");
        switch(operation)
        {
            case "status": break;
            case "projection": Projection(words); break;
            case "cad": scientificOverlay.Handle(words); state.scientificOverlay=scientificOverlay.Status; break;
            case "pause": state.paused=true; break;
            case "resume": state.paused=false; break;
            case "setflow": if(words.Length!=4) throw new InvalidDataException("setflow x y z"); Vector3 nextDirection=new Vector3(Number(words[1]),Number(words[2]),Number(words[3])); if(nextDirection.sqrMagnitude<.000001f) throw new InvalidDataException("Zero flow."); direction=nextDirection.normalized; break;
            case "setintensity": if(words.Length!=2) throw new InvalidDataException("setintensity value"); effect.x=Mathf.Clamp(Number(words[1]),0,10); break;
            case "setclock": if(words.Length!=2) throw new InvalidDataException("setclock seconds"); float nextClock=Number(words[1]); if(nextClock<0||nextClock>1000000) throw new InvalidDataException("Clock0..1000000."); clock=nextClock; break;
            case "rendermode": if(words.Length!=2||!(words[1]=="full"||words[1]=="geometry"||words[1]=="fields")||(resident!=null&&words[1]!="geometry")) throw new InvalidDataException("rendermode full|geometry|fields; resident material admits geometry only."); state.pipelineMode=words[1]; ribbonCommands?.Clear(); sceneCommands?.Clear(); break;
            case "materialmode": if(resident==null||words.Length!=2) throw new InvalidDataException("Loaded material fixture and original|reference|instanced required."); resident.SetMode(words[1]); state.materialPixelProof=false; state.materialOccupiedPixels=0; break;
            case "materialdiagnostic": if(resident==null||words.Length!=2) throw new InvalidDataException("Loaded material fixture and off|defaults|single required."); resident.SetDiagnostic(words[1]); state.materialPixelProof=false; state.materialOccupiedPixels=0; break;
            case "snapshotreload": if(resident==null) throw new InvalidDataException("Loaded material fixture required."); resident.ReloadSnapshot(Absolute(text.Substring(words[0].Length).Trim())); geometryBounds=resident.Bounds; state.materialPixelProof=false; state.materialOccupiedPixels=0; break;
            case "benchmark": if(words.Length!=3) throw new InvalidDataException("benchmark warmupSeconds captureSeconds"); BeginBenchmark(Number(words[1]),Number(words[2])); break;
            case "cancelbenchmark": if(!state.benchmarkActive) throw new InvalidOperationException("No active benchmark."); EndBenchmark("interrupted","operator-cancelled"); break;
            case "cameraorbit": if(words.Length!=4) throw new InvalidDataException("cameraorbit yaw pitch distance"); Quaternion orbit=Quaternion.Euler(Mathf.Clamp(Number(words[2]),-89,89),Number(words[1]),0); view.transform.position=geometryBounds.center+orbit*Vector3.back*Mathf.Clamp(Number(words[3]),.1f,10000); view.transform.LookAt(geometryBounds.center); break;
            case "bundlehotreload": ReloadBundle(Absolute(text.Substring(words[0].Length).Trim()),null,"manual-"+DateTime.UtcNow.Ticks); break;
            case "fixturehotreload": LoadFixture(Absolute(text.Substring(words[0].Length).Trim()),false); break;
            case "captureframe": StartCoroutine(Capture(false)); break;
            case "quit": if(state.benchmarkActive) EndBenchmark("interrupted","operator-quit"); Application.Quit(0); break;
            default: throw new InvalidDataException("Unknown command.");
        }
        if(!state.benchmarkActive) WriteStatus();
    }
    void Projection(string[] words)
    {
        if(words.Length>=2&&words[1]=="perspective"&&(words.Length==2||words.Length==3))
        {
            float fov=words.Length==3?Number(words[2]):view.fieldOfView;
            if(fov<1||fov>179)throw new InvalidDataException("Perspective FOV1..179 degrees required.");
            view.fieldOfView=fov; view.orthographic=false;
        }
        else if(words.Length==3&&words[1]=="ortho")
        {
            float span=Number(words[2]); if(span<.001f||span>1e12f)throw new InvalidDataException("Orthographic full vertical span .001..1e12 metres required.");
            if(!state.orthographicShadersSupported)throw new InvalidDataException("Orthographic mode requires recooked Sheath, Ribbon and SceneDepthCopy with _ContinuumOrthographicContract exactly1; current bundle is perspective-only.");
            view.orthographicSize=span*.5f; view.orthographic=true;
        }
        else throw new InvalidDataException("projection perspective [FOV_DEGREES] | projection ortho VERTICAL_SPAN_METRES");
        state.fieldPixelProof=state.materialPixelProof=false; state.viewOccupiedPixels=state.flowOccupiedPixels=state.sceneOccupiedPixels=state.materialOccupiedPixels=0;
        ribbonCommands?.Clear(); sceneCommands?.Clear();
    }
    ScientificView CurrentScientificView()
    {
        Vector3 origin=view.transform.position,right=view.transform.right,up=view.transform.up,forward=view.transform.forward;
        return new ScientificView(new ScientificVector3(origin.x,origin.y,origin.z),new ScientificVector3(right.x,right.y,right.z),new ScientificVector3(up.x,up.y,up.z),new ScientificVector3(forward.x,forward.y,forward.z),Screen.width,Screen.height,view.orthographic?ScientificProjectionKind.Orthographic:ScientificProjectionKind.Perspective,view.fieldOfView,view.orthographicSize*2,view.nearClipPlane,view.farClipPlane);
    }
    void OnGUI()
    {
        if(view==null)return;
        Vector3 focus=geometryBounds.center;
        var scientificView=CurrentScientificView();
        scientificOverlay.SetFocus(new ScientificVector3(focus.x,focus.y,focus.z)); scientificOverlay.Draw(scientificView); state.scientificOverlay=scientificOverlay.Status;
        double focusDepth=Vector3.Dot(focus-view.transform.position,view.transform.forward);
        GUI.Label(new Rect(12,12,Screen.width-24,24),(view.orthographic?"ortho":"perspective")+(view.orthographic?" / vertical "+(view.orthographicSize*2).ToString("G6")+" m":" / FOV "+view.fieldOfView.ToString("G5")+" degrees")+" / "+(focusDepth>0?scientificView.PixelsPerMeter(focusDepth).ToString("G5")+" px/m at focus":"focus behind camera"));
    }
    void BeginBenchmark(double warmup,double duration)
    {
        if(state.benchmarkActive||capturesPending!=0||smokeFrames>0||!SplashScreen.isFinished) throw new InvalidOperationException("Idle persistent host after splash required.");
        if(resident!=null&&resident.Diagnostic!="off") throw new InvalidOperationException("Material diagnostics cannot be benchmarked.");
        if(warmup<0||warmup>30||duration<1||duration>30) throw new InvalidDataException("Warmup0..30 and capture1..30 seconds required.");
        benchmark=new Benchmark {hostId=state.hostId,processId=state.processId,renderer=state.renderer,graphicsVersion=SystemInfo.graphicsDeviceVersion,unity=Application.unityVersion,
            renderPath=view.actualRenderingPath.ToString(),pipelineMode=state.pipelineMode,geometrySource=state.geometrySource,fixtureSha256=state.fixtureSha256,bundleSha256=state.bundleSha256,generation=state.generation,
            assemblySha256=HashFile(typeof(AeroFieldWorkbench).Assembly.Location),frozenControlsJson=JsonUtility.ToJson(state),warmupSeconds=warmup,requestedCaptureSeconds=duration,
            cameraFov=view.fieldOfView,cameraNear=view.nearClipPlane,cameraFar=view.farClipPlane,
            effectColor=new[] {effectColor.r,effectColor.g,effectColor.b,effectColor.a},effectParameters=new[] {effect.x,effect.y,effect.z,effect.w},textureParameters=new[] {texture.x,texture.y,texture.z,clock},falloffs=new[] {falloffs.x,falloffs.y,falloffs.z,falloffs.w},
            graphicsUVStartsAtTop=SystemInfo.graphicsUVStartsAtTop,cameraProjectionM11=view.projectionMatrix.m11,gpuProjectionToTextureM11=GL.GetGPUProjectionMatrix(view.projectionMatrix,true).m11,
            width=Screen.width,height=Screen.height,antiAliasing=QualitySettings.antiAliasing,qualityLevel=QualitySettings.GetQualityLevel(),colorSpace=QualitySettings.activeColorSpace.ToString(),paused=true,
            frequency=System.Diagnostics.Stopwatch.Frequency,frameMilliseconds=new double[65536],frameStartedTicks=new long[65536],frameEndedTicks=new long[65536],renderedFrames=new int[65536]};
        if(resident!=null)
        {
            benchmark.geometryRendererCount=resident.InstanceCount; benchmark.geometryVertexCount=resident.VertexCount; benchmark.geometryTriangleCount=resident.TriangleCount;
            benchmark.geometrySubmissionScope="Resident real-material fixture; shared immutable meshes/textures with reference full-affine draws or instance packets, captured visual state/light/SH; not stock shader equivalence.";
        }
        else foreach(MeshFilter filter in geometryRoot.GetComponentsInChildren<MeshFilter>())
        {
            Mesh mesh=filter.sharedMesh; if(mesh==null) throw new InvalidDataException("Missing fixture mesh.");
            benchmark.geometryRendererCount++; benchmark.geometryVertexCount=checked(benchmark.geometryVertexCount+mesh.vertexCount);
            for(int sub=0;sub<mesh.subMeshCount;sub++) benchmark.geometryTriangleCount=checked(benchmark.geometryTriangleCount+(int)(mesh.GetIndexCount(sub)/3));
        }
        if(HashFile(activeBundlePath)!=state.bundleSha256||(!string.IsNullOrEmpty(fixturePath)&&HashFile(fixturePath)!=state.fixtureSha256)) throw new InvalidOperationException("Loaded source identity drifted before benchmark.");
        if(resident!=null) resident.ValidateLoadedFiles();
        priorVSync=QualitySettings.vSyncCount; priorFrameRate=Application.targetFrameRate; priorPaused=state.paused;
        try
        {
            QualitySettings.vSyncCount=0; Application.targetFrameRate=-1; state.paused=true;
            benchmark.vSyncCount=QualitySettings.vSyncCount; benchmark.targetFrameRate=Application.targetFrameRate;
            WriteStatus(); benchmark.frozenControlsJson=JsonUtility.ToJson(state);
            benchmarkRenderPath=view.actualRenderingPath; benchmarkColorSpace=QualitySettings.activeColorSpace;
            benchmarkWarmupEnd=System.Diagnostics.Stopwatch.GetTimestamp()+(long)(warmup*benchmark.frequency); benchmarkCapturing=false; benchmarkPreviousTicks=0; state.benchmarkActive=true;
        }
        catch { QualitySettings.vSyncCount=priorVSync; Application.targetFrameRate=priorFrameRate; state.paused=priorPaused; benchmark=null; throw; }
    }
    void TickBenchmark()
    {
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        if(Screen.width!=benchmark.width||Screen.height!=benchmark.height||QualitySettings.vSyncCount!=0||Application.targetFrameRate!=-1||QualitySettings.antiAliasing!=benchmark.antiAliasing||QualitySettings.GetQualityLevel()!=benchmark.qualityLevel||QualitySettings.activeColorSpace!=benchmarkColorSpace||!SplashScreen.isFinished||view.actualRenderingPath!=benchmarkRenderPath)
        { EndBenchmark("invalid","effective-controls-or-presentation-changed"); return; }
        if(!benchmarkCapturing)
        {
            if(now<benchmarkWarmupEnd) return;
            BeginProcessResources();
            now=System.Diagnostics.Stopwatch.GetTimestamp();
            benchmarkCapturing=true; benchmark.windowStartTicks=now; benchmarkPreviousTicks=now; benchmarkPreviousFrame=Time.frameCount;
            benchmarkDeadline=now+(long)(benchmark.requestedCaptureSeconds*benchmark.frequency); return;
        }
        if(now<=benchmarkPreviousTicks||Time.frameCount!=benchmarkPreviousFrame+1||lastRenderedFrame!=benchmarkPreviousFrame||renderedInFrame!=1)
        { EndBenchmark("invalid","missing-duplicate-or-nonconsecutive-render-witness"); return; }
        if(now>benchmarkDeadline) { benchmark.omittedDeadlineStraddlers++; EndBenchmark(benchmark.sampleCount>0?"completed":"invalid",benchmark.sampleCount>0?"monotonic-window-complete":"no-complete-frame-intervals"); return; }
        int index=benchmark.sampleCount; if(index>=benchmark.capacity) { EndBenchmark("invalid","sample-capacity-exceeded"); return; }
        benchmark.frameStartedTicks[index]=benchmarkPreviousTicks; benchmark.frameEndedTicks[index]=now; benchmark.renderedFrames[index]=benchmarkPreviousFrame;
        benchmark.frameMilliseconds[index]=(now-benchmarkPreviousTicks)*1000.0/benchmark.frequency; benchmark.sampleCount++;
        benchmarkPreviousTicks=now; benchmarkPreviousFrame=Time.frameCount;
    }
    void EndBenchmark(string status,string reason)
    {
        if(!state.benchmarkActive) return;
        state.benchmarkActive=false; EndProcessResources(); benchmark.status=status; benchmark.reason=reason; benchmark.windowEndTicks=benchmarkPreviousTicks;
        if(status=="completed"&&benchmark.commandsDuringCapture>0) { benchmark.status="invalid"; benchmark.reason="diagnostic-command-interference"; }
        benchmark.measuredSeconds=benchmarkCapturing?(benchmark.windowEndTicks-benchmark.windowStartTicks)/(double)benchmark.frequency:0;
        QualitySettings.vSyncCount=priorVSync; Application.targetFrameRate=priorFrameRate; state.paused=priorPaused;
        try
        {
            if(HashFile(activeBundlePath)!=benchmark.bundleSha256||(!string.IsNullOrEmpty(fixturePath)&&HashFile(fixturePath)!=benchmark.fixtureSha256)) { benchmark.status="invalid"; benchmark.reason="source-identity-drifted"; }
            if(resident!=null) resident.ValidateLoadedFiles();
        }
        catch(Exception error) { benchmark.status="invalid"; benchmark.reason="source-check-failed: "+error.Message; }
        Array.Resize(ref benchmark.frameMilliseconds,benchmark.sampleCount); Array.Resize(ref benchmark.frameStartedTicks,benchmark.sampleCount); Array.Resize(ref benchmark.frameEndedTicks,benchmark.sampleCount); Array.Resize(ref benchmark.renderedFrames,benchmark.sampleCount);
        try
        {
            string path=Path.Combine(output,"benchmark-"+DateTime.UtcNow.Ticks+".json"),temp=path+".tmp";
            File.WriteAllText(temp,JsonUtility.ToJson(benchmark,true)); File.Move(temp,path); state.lastBenchmark=path; state.lastBenchmarkStatus=benchmark.status; state.lastBenchmarkReason=benchmark.reason;
        }
        catch(Exception error) { state.lastBenchmark=null; state.lastBenchmarkStatus="invalid"; state.lastBenchmarkReason="benchmark-export-failed"; state.reloadError="benchmark-export-failed: "+error.Message; }
        finally { benchmark=null; benchmarkCapturing=false; }
        try { WriteStatus(); } catch(Exception error) { Debug.LogError("Workbench benchmark status export failed: "+error.Message); }
    }
    void BeginProcessResources()
    {
        try
        {
            benchmarkProcess=System.Diagnostics.Process.GetCurrentProcess(); benchmarkProcess.Refresh();
            benchmarkCpuStart=benchmarkProcess.TotalProcessorTime; benchmark.workingSetStartBytes=benchmarkProcess.WorkingSet64;
            benchmark.processResourceStartTicks=System.Diagnostics.Stopwatch.GetTimestamp(); benchmark.processResourceStatus="started";
        }
        catch(Exception error) { benchmark.processResourceStatus="unsupported"; benchmark.processResourceError=error.GetType().Name+": "+error.Message; benchmarkProcess?.Dispose(); benchmarkProcess=null; }
    }
    void EndProcessResources()
    {
        if(benchmarkProcess==null) { if(string.IsNullOrEmpty(benchmark.processResourceStatus)) benchmark.processResourceStatus="not-started"; return; }
        try
        {
            benchmarkProcess.Refresh(); benchmark.processResourceEndTicks=System.Diagnostics.Stopwatch.GetTimestamp();
            benchmark.processCpuSeconds=(benchmarkProcess.TotalProcessorTime-benchmarkCpuStart).TotalSeconds; benchmark.workingSetEndBytes=benchmarkProcess.WorkingSet64;
            benchmark.processResourceStatus=benchmark.processCpuSeconds>=0?"available":"invalid-negative-cpu-delta";
        }
        catch(Exception error) { benchmark.processResourceStatus="unsupported"; benchmark.processResourceError=error.GetType().Name+": "+error.Message; }
        finally { benchmarkProcess.Dispose(); benchmarkProcess=null; }
    }
    void ReloadPublication()
    {
        string hash=HashFile(publicationPath); if(new FileInfo(publicationPath).Length>16384) throw new InvalidDataException("Publication too large.");
        Publication publication=JsonUtility.FromJson<Publication>(File.ReadAllText(publicationPath));
        if(publication==null||publication.schema!="continuum-aero-shader-publication/v1"||string.IsNullOrEmpty(publication.generation)||string.IsNullOrEmpty(publication.bundleSha256)) throw new InvalidDataException("Incomplete publication.");
        ReloadBundle(Absolute(publication.bundle),publication.bundleSha256,publication.generation,hash);
        publicationSeen=hash;
    }
    void ReloadBundle(string path,string expected,string generation,string publicationHash=null)
    {
        string before=HashFile(path); if(expected!=null&&before!=expected) throw new InvalidDataException("Bundle hash differs.");
        if(before==state.bundleSha256) { if(publicationHash!=null&&HashFile(publicationPath)!=publicationHash) throw new InvalidDataException("Publication changed during reload."); state.generation=generation; return; }
        // A same-name loaded container can reject a candidate; leave the last good owner intact.
        AssetBundle next=AssetBundle.LoadFromFile(path); if(next==null) throw new InvalidDataException("Bundle cannot load (possibly loaded identity collision); publish a unique bundle generation.");
        var materials=new List<Material>();
        try
        {
            Shader[] all=next.LoadAllAssets<Shader>();
            foreach(string suffix in new[] {"Coverage","Flow","Sheath","Ribbon","SceneDepthCopy"})
            {
                Shader found=null; foreach(Shader shader in all) if(shader.name=="Continuum/AeroField"+suffix) found=shader;
                if(found==null||!found.isSupported) throw new InvalidDataException("Required supported shader absent: "+suffix);
                materials.Add(new Material(found));
            }
            var orthoNames=new string[3]; var orthoPresent=new bool[3]; var orthoValues=new float[3]; bool orthoSupported=true;
            for(int i=0;i<3;i++)
            {
                Material material=materials[i+2]; orthoNames[i]=material.shader.name;
                orthoPresent[i]=material.HasProperty("_ContinuumOrthographicContract");
                orthoValues[i]=orthoPresent[i]?material.GetFloat("_ContinuumOrthographicContract"):0;
                orthoSupported&=orthoPresent[i]&&orthoValues[i]==1;
            }
            if(view!=null&&view.orthographic&&!orthoSupported)throw new InvalidDataException("Orthographic bundle reload requires all three exact v1 contracts; previous supported owner retained.");
            materials[3].enableInstancing=true;
            if(HashFile(path)!=before) throw new InvalidDataException("Bundle changed during reload.");
            if(publicationHash!=null&&HashFile(publicationPath)!=publicationHash) throw new InvalidDataException("Publication changed during reload.");
            foreach(Material m in materials) m.SetTexture("_ContinuumNoiseTex",noise);
            Material[] old={coverage,flowMaterial,sheath,ribbon,depthCopy}; AssetBundle previous=bundle;
            coverage=materials[0]; flowMaterial=materials[1]; sheath=materials[2]; ribbon=materials[3]; depthCopy=materials[4]; bundle=next;
            ribbonCommands?.Clear(); sceneCommands?.Clear(); foreach(Material material in old) if(material!=null) Destroy(material); if(previous!=null) previous.Unload(true);
            state.bundleSha256=before; state.generation=generation; state.shaders=Array.ConvertAll(materials.ToArray(),m=>m.shader.name); state.shaderReloads++; state.reloadError=null;
            state.orthographicShadersSupported=orthoSupported; state.orthographicShaderNames=orthoNames; state.orthographicShaderPropertyPresent=orthoPresent; state.orthographicShaderContractValues=orthoValues; state.orthographicShaderAdmission=orthoSupported?"supported-exact-v1":"legacy-perspective-only";
            activeBundlePath=path;
        }
        catch { foreach(Material material in materials) Destroy(material); next.Unload(true); throw; }
    }
    void LoadFixture(string path,bool initial)
    {
        if(new FileInfo(path).Length>1048576) throw new InvalidDataException("Fixture manifest bound.");
        if(JsonUtility.FromJson<Fixture>(File.ReadAllText(path)).schema=="continuum-resident-material-fixture/v2")
        {
            string bundlePath=Environment.GetEnvironmentVariable("CONTINUUM_RESIDENT_PART_BUNDLE");
            if(string.IsNullOrEmpty(bundlePath)) throw new InvalidDataException("CONTINUUM_RESIDENT_PART_BUNDLE required.");
            string originalBundlePath=Environment.GetEnvironmentVariable("CONTINUUM_RESIDENT_ORIGINAL_BUNDLE");
            var nextResident=new ResidentMaterialRuntime(path,Absolute(bundlePath),string.IsNullOrEmpty(originalBundlePath)?null:Absolute(originalBundlePath));
            try
            {
                if(resident!=null) { resident.ValidateActivationTransferTo(nextResident); nextResident.SetMode(resident.Mode); nextResident.SetDiagnostic(resident.Diagnostic); }
                // Candidate activation can roll back to the still-live owner's camera and settings.
                nextResident.Activate(view,initial);
                if(resident!=null) { resident.TransferActivationTo(nextResident); resident.Dispose(); }
                if(geometryRoot!=null) { geometryRoot.SetActive(false); Destroy(geometryRoot); geometryRoot=null; }
                foreach(Mesh mesh in meshes) Destroy(mesh); meshes.Clear();
                resident=nextResident; geometryBounds=resident.Bounds;
                state.pipelineMode="geometry"; sceneCommands?.Clear(); ribbonCommands?.Clear();
                fixturePath=path; fixtureSeen=resident.ManifestSha256; state.fixtureSha256=fixtureSeen; state.geometrySource="private-resident-material-v2"; state.fixtureReloads++; state.materialPixelProof=false; state.materialOccupiedPixels=0;
                return;
            }
            catch { nextResident.Dispose(); throw; }
        }
        string manifestHash=HashFile(path); if(new FileInfo(path).Length>1048576) throw new InvalidDataException("Fixture manifest bound.");
        Fixture metadata=JsonUtility.FromJson<Fixture>(File.ReadAllText(path));
        if(metadata==null||metadata.schema!="continuum-aero-field-fixture/v1"||metadata.geometry!="geometry.bin") throw new InvalidDataException("Fixture schema/path.");
        string chunk=Path.Combine(Path.GetDirectoryName(path),metadata.geometry); if(new FileInfo(chunk).Length>300000000||HashFile(chunk)!=metadata.geometrySha256) throw new InvalidDataException("Fixture payload hash/bound.");
        GameObject next=new GameObject("Frozen fixture"); next.SetActive(false); var loaded=new List<Mesh>(); int totalV=0,totalI=0;
        try
        {
            using(var reader=new BinaryReader(File.OpenRead(chunk)))
            {
                if(reader.ReadInt32()!=0x31464641||reader.ReadInt32()!=1) throw new InvalidDataException("Chunk identity.");
                int count=reader.ReadInt32(); if(count<1||count>4096||count!=metadata.rows) throw new InvalidDataException("Row count.");
                for(int row=0;row<count;row++)
                {
                    int nv=reader.ReadInt32(),ni=reader.ReadInt32(); if(nv<1||ni<3||ni%3!=0) throw new InvalidDataException("Mesh dimensions."); totalV=checked(totalV+nv); totalI=checked(totalI+ni); if(totalV>4000000||totalI>12000000) throw new InvalidDataException("Mesh capacity.");
                    var vertices=new Vector3[nv]; var normals=new Vector3[nv]; var uv=new Vector2[nv]; var indices=new int[ni];
                    for(int i=0;i<nv;i++) { vertices[i]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()); normals[i]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()); uv[i]=new Vector2(reader.ReadSingle(),reader.ReadSingle()); Check(vertices[i]); Check(normals[i]); if(!Finite(uv[i].x)||!Finite(uv[i].y)) throw new InvalidDataException("UV nonfinite."); }
                    for(int i=0;i<ni;i++) { indices[i]=reader.ReadInt32(); if(indices[i]<0||indices[i]>=nv) throw new InvalidDataException("Index range."); }
                    var mesh=new Mesh { indexFormat=IndexFormat.UInt32 }; loaded.Add(mesh); mesh.vertices=vertices; mesh.normals=normals; mesh.uv=uv; mesh.triangles=indices; mesh.RecalculateBounds(); AddMesh(next,mesh);
                }
                if(reader.BaseStream.Position!=reader.BaseStream.Length||metadata.vertices!=totalV||metadata.indices!=totalI) throw new InvalidDataException("Chunk trailing bytes/counts.");
            }
            if(HashFile(path)!=manifestHash||HashFile(chunk)!=metadata.geometrySha256) throw new InvalidDataException("Fixture changed during read.");
            if(initial)
            {
                Vector3 position=Vector(metadata.cameraPosition); Vector4 rotation=Vector4Of(metadata.cameraRotation); Quaternion q=new Quaternion(rotation.x,rotation.y,rotation.z,rotation.w); if(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w<.9f) throw new InvalidDataException("Camera rotation.");
                if(!Finite(metadata.fov)||metadata.fov<1||metadata.fov>179||!Finite(metadata.near)||!Finite(metadata.far)||metadata.near<=0||metadata.far<=metadata.near) throw new InvalidDataException("Camera range.");
                direction=Vector(metadata.flowDirection); if(direction.sqrMagnitude<.000001f) throw new InvalidDataException("Flow direction."); direction.Normalize(); effect=Vector4Of(metadata.effectParameters); texture=Vector4Of(metadata.textureParameters); Vector4 color=Vector4Of(metadata.effectColor); effectColor=new Color(color.x,color.y,color.z,color.w); Vector3 falls=Vector(metadata.falloffs); falloffs=new Vector4(falls.x,falls.y,falls.z,0);
                view.transform.SetPositionAndRotation(position,q); view.fieldOfView=metadata.fov; view.nearClipPlane=metadata.near; view.farClipPlane=metadata.far;
            }
            if(resident!=null) { resident.Dispose(); resident=null; }
            if(geometryRoot!=null) { geometryRoot.SetActive(false); Destroy(geometryRoot); } foreach(Mesh mesh in meshes) Destroy(mesh); meshes.Clear(); meshes.AddRange(loaded); geometryRoot=next; next.SetActive(true); ComputeBounds();
            fixturePath=path; fixtureSeen=manifestHash; state.fixtureSha256=manifestHash; state.geometrySource="private-frozen-geometry"; state.fixtureReloads++;
        }
        catch { Destroy(next); foreach(Mesh mesh in loaded) Destroy(mesh); throw; }
    }
    void AddMesh(GameObject root,Mesh mesh) { GameObject node=new GameObject("Opaque snapshot",typeof(MeshFilter),typeof(MeshRenderer)); node.layer=28; node.transform.SetParent(root.transform,false); node.GetComponent<MeshFilter>().sharedMesh=mesh; node.GetComponent<MeshRenderer>().sharedMaterial=opaque; }
    void ComputeBounds() { bool first=true; foreach(Renderer renderer in geometryRoot.GetComponentsInChildren<Renderer>()) { if(first) { geometryBounds=renderer.bounds; first=false; } else geometryBounds.Encapsulate(renderer.bounds); } }
    void Analytic()
    {
        geometryRoot=new GameObject("LABELLED analytic concave shielded geometry");
        foreach(Vector3 center in new[] {new Vector3(-1,0,0),new Vector3(1,0,0),new Vector3(0,-1,0),new Vector3(0,0,2)})
        { GameObject cube=GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(geometryRoot.transform,false); cube.transform.localPosition=center; cube.transform.localScale=new Vector3(1,3,.5f); cube.layer=28; cube.GetComponent<Renderer>().sharedMaterial=opaque; Destroy(cube.GetComponent<Collider>()); }
        ComputeBounds(); view.transform.position=new Vector3(0,1,-12); view.transform.LookAt(geometryBounds.center); view.fieldOfView=45; view.nearClipPlane=.1f; view.farClipPlane=100; direction=Vector3.forward; state.geometrySource="analytic-concave-shielded-not-KSP";
    }
    RenderTexture Target(string name,RenderTextureFormat format,int depth) { var rt=new RenderTexture(Screen.width,Screen.height,depth,format,RenderTextureReadWrite.Linear) { name=name, filterMode=FilterMode.Point }; rt.Create(); return rt; }
    void EnsureTargets()
    {
        if(Screen.width<1||Screen.height<1||Screen.width>4096||Screen.height>4096) throw new InvalidOperationException("Workbench field dimensions outside1..4096.");
        if(fieldMap!=null&&fieldMap.width==Screen.width&&fieldMap.height==Screen.height) return;
        foreach(RenderTexture rt in new[] {fieldMap,flowMap,sceneMap}) if(rt!=null) { rt.Release(); Destroy(rt); }
        fieldMap=Target("View normal/depth",RenderTextureFormat.ARGBFloat,24); flowMap=Target("Flow depth",RenderTextureFormat.RFloat,24); sceneMap=Target("Scene eye depth",RenderTextureFormat.RFloat,0);
    }
    void OnPreRender()
    {
        if(coverage==null) return;
        if(resident!=null)
        {
            ribbonCommands?.Clear();
            if(sceneMap==null||sceneMap.width!=Screen.width||sceneMap.height!=Screen.height) { if(sceneMap!=null) { sceneMap.Release(); Destroy(sceneMap); } sceneMap=Target("Resident positive scene depth",RenderTextureFormat.RFloat,0); }
            if(sceneCommands==null) { sceneCommands=new CommandBuffer {name="Resident main-camera positive eye depth"}; view.AddCommandBuffer(CameraEvent.AfterForwardOpaque,sceneCommands); }
            sceneCommands.Clear(); sceneCommands.Blit(BuiltinRenderTextureType.CurrentActive,sceneMap,depthCopy); sceneCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget); return;
        }
        if(state.pipelineMode=="geometry") { ribbonCommands?.Clear(); sceneCommands?.Clear(); return; }
        EnsureTargets(); field.CopyFrom(view); field.enabled=false; field.depthTextureMode=DepthTextureMode.None; field.cullingMask=1<<28; field.targetTexture=fieldMap; field.clearFlags=CameraClearFlags.SolidColor; field.backgroundColor=Color.clear; field.transform.SetPositionAndRotation(view.transform.position,view.transform.rotation);
        field.RenderWithShader(coverage.shader,"RenderType"); state.fieldRenders++;
        float radius=Mathf.Max(1,geometryBounds.extents.magnitude); flow.CopyFrom(view); flow.enabled=false; flow.depthTextureMode=DepthTextureMode.None; flow.orthographic=true; flow.orthographicSize=radius*1.1f; flow.nearClipPlane=.01f; flow.farClipPlane=radius*4+.1f; flow.aspect=1; flow.cullingMask=1<<28; flow.targetTexture=flowMap; flow.clearFlags=CameraClearFlags.SolidColor; flow.backgroundColor=Color.clear;
        Vector3 up=Mathf.Abs(Vector3.Dot(direction,Vector3.up))>.95f?Vector3.right:Vector3.up; flow.transform.SetPositionAndRotation(geometryBounds.center-direction*radius*2,Quaternion.LookRotation(direction,up));
        Shader.SetGlobalFloat("_ContinuumFlowFar",flow.farClipPlane); flow.RenderWithShader(flowMaterial.shader,"RenderType"); state.flowRenders++;
        Globals();
        if(sceneCommands==null) { sceneCommands=new CommandBuffer { name="Current main-camera positive eye depth" }; view.AddCommandBuffer(CameraEvent.AfterForwardOpaque,sceneCommands); }
        sceneCommands.Clear(); sceneCommands.Blit(BuiltinRenderTextureType.CurrentActive,sceneMap,depthCopy); sceneCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        if(ribbonCommands==null) { ribbonCommands=new CommandBuffer { name="256 bounded original ribbons" }; view.AddCommandBuffer(CameraEvent.AfterEverything,ribbonCommands); }
        ribbonCommands.Clear(); if(state.pipelineMode=="full") { ribbonCommands.DrawMeshInstanced(quad,0,ribbon,0,seeds,256); state.ribbonSubmissions++; }
    }
    void Globals()
    {
        Shader.SetGlobalTexture("_ContinuumViewField",fieldMap); Shader.SetGlobalTexture("_ContinuumFlowField",flowMap); Shader.SetGlobalTexture("_ContinuumSceneDepth",sceneMap); Shader.SetGlobalTexture("_ContinuumNoiseTex",noise);
        Shader.SetGlobalVector("_ContinuumViewField_TexelSize",new Vector4(1f/fieldMap.width,1f/fieldMap.height,fieldMap.width,fieldMap.height));
        Shader.SetGlobalMatrix("_ContinuumViewInverseProjection",GL.GetGPUProjectionMatrix(view.projectionMatrix,true).inverse); Shader.SetGlobalMatrix("_ContinuumViewCameraToWorld",view.cameraToWorldMatrix);
        Shader.SetGlobalFloat("_ContinuumViewOrthographic",view.orthographic?1:0);
        Shader.SetGlobalVector("_ContinuumViewForward",view.transform.forward);
        Shader.SetGlobalMatrix("_ContinuumFlowViewProjection",GL.GetGPUProjectionMatrix(flow.projectionMatrix,true)*flow.worldToCameraMatrix); Shader.SetGlobalMatrix("_ContinuumFlowWorldToView",flow.worldToCameraMatrix); Shader.SetGlobalFloat("_ContinuumFlowFar",flow.farClipPlane);
        Shader.SetGlobalVector("_ContinuumEffectDirection",direction); Shader.SetGlobalColor("_ContinuumEffectColor",effectColor); Shader.SetGlobalVector("_ContinuumEffectParameters",effect); texture.w=clock; Shader.SetGlobalVector("_ContinuumTextureParameters",texture); Shader.SetGlobalVector("_ContinuumFalloffs",falloffs);
        Shader.SetGlobalVector("_ContinuumSeedRect",new Vector4(0,0,1,1));
    }
    void OnRenderImage(RenderTexture source,RenderTexture destination)
    {
        if(depthCopy==null) { Graphics.Blit(source,destination); return; }
        if(resident==null&&state.pipelineMode!="geometry") Globals(); Graphics.Blit(source,destination); if(resident==null&&state.pipelineMode=="full") { Graphics.Blit(source,destination,sheath); state.composites++; } state.frames++;
        RenderingPath path=view.actualRenderingPath; if(!renderPathRecorded||observedRenderPath!=path) { observedRenderPath=path; renderPathRecorded=true; state.renderPath=path.ToString(); }
        if(lastRenderedFrame==Time.frameCount) renderedInFrame++; else { lastRenderedFrame=Time.frameCount; renderedInFrame=1; }
        if(smokeFrames>0&&state.frames==smokeFrames) StartCoroutine(Capture(true));
    }
    IEnumerator Capture(bool quit)
    {
        if(state.benchmarkActive) yield break;
        capturesPending++;
        // Startup fields can render underneath Unity's splash presentation.
        while(!SplashScreen.isFinished) yield return null;
        yield return new WaitForEndOfFrame();
        state.splashFinishedAtCapture=SplashScreen.isFinished;
        string path=Path.Combine(output,"frame-"+state.frames+"-"+DateTime.UtcNow.Ticks+".png"); var image=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
        RenderTexture previous=RenderTexture.active;
        try { RenderTexture.active=null; image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0); image.Apply(); RenderTexture.active=previous; File.WriteAllBytes(path,image.EncodeToPNG()); state.lastPng=path;
            if(resident!=null)
            {
                int count=PixelProof(sceneMap,false);
                state.materialOccupiedPixels=count; state.materialPixelProof=count>0&&resident.SubmittedFrames>0&&resident.DrawCalls>0;
            }
            else { state.viewOccupiedPixels=PixelProof(fieldMap,true); state.flowOccupiedPixels=PixelProof(flowMap,false); state.sceneOccupiedPixels=PixelProof(sceneMap,false);
                state.fieldPixelProof=state.viewOccupiedPixels>0&&state.flowOccupiedPixels>0&&state.sceneOccupiedPixels>0; } WriteStatus(); }
        catch(Exception error) { state.fieldPixelProof=false; state.materialPixelProof=false; state.materialOccupiedPixels=0; state.reloadError="capture: "+error.Message; WriteStatus(); if(quit) Application.Quit(2); }
        finally { RenderTexture.active=previous; Destroy(image); capturesPending--; }
        if(quit) Application.Quit(state.splashFinishedAtCapture&&state.renderPath=="Forward"&&(resident!=null?state.materialPixelProof:state.fieldPixelProof&&state.fieldRenders>0&&state.flowRenders>0&&state.composites>0&&state.ribbonSubmissions>0)?0:2);
    }
    static int PixelProof(RenderTexture rt,bool normalDepth)
    {
        RenderTexture previous=RenderTexture.active; var pixels=new Texture2D(rt.width,rt.height,TextureFormat.RGBAFloat,false,true);
        try { RenderTexture.active=rt; pixels.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); pixels.Apply(); int count=0;
            foreach(Color color in pixels.GetPixels()) { float depth=normalDepth?color.a:color.r; if(!Finite(depth)||depth<0) throw new InvalidDataException("Invalid native field depth.");
                if(depth>0) { if(normalDepth&&(!Finite(color.r)||!Finite(color.g)||!Finite(color.b)||color.r<0||color.r>1||color.g<0||color.g>1||color.b<0||color.b>1)) throw new InvalidDataException("Invalid native normal encoding: "+color.r.ToString("R",CultureInfo.InvariantCulture)+","+color.g.ToString("R",CultureInfo.InvariantCulture)+","+color.b.ToString("R",CultureInfo.InvariantCulture)+" at depth "+depth.ToString("R",CultureInfo.InvariantCulture)); count++; } } return count; }
        finally { RenderTexture.active=previous; Destroy(pixels); }
    }
    void WriteStatus()
    {
        if(view!=null)
        {
            Vector3 offset=view.transform.position-geometryBounds.center;
            state.cameraDistance=offset.magnitude;
            state.cameraYaw=Mathf.Atan2(-offset.x,-offset.z)*Mathf.Rad2Deg;
            state.cameraPitch=Mathf.Asin(Mathf.Clamp(offset.y/Mathf.Max(offset.magnitude,.00001f),-1,1))*Mathf.Rad2Deg;
            Vector3 position=view.transform.position; Quaternion rotation=view.transform.rotation;
            state.cameraPosition=new[] {position.x,position.y,position.z}; state.cameraRotation=new[] {rotation.x,rotation.y,rotation.z,rotation.w};
            state.projection=view.orthographic?"ortho":"perspective"; state.fov=view.fieldOfView; state.verticalSpanMeters=view.orthographic?view.orthographicSize*2:0; state.near=view.nearClipPlane; state.far=view.farClipPlane; state.viewportWidth=Screen.width; state.viewportHeight=Screen.height;
            double focusDepth=Vector3.Dot(geometryBounds.center-position,view.transform.forward);
            state.pixelsPerMeterAtFocus=focusDepth>0?CurrentScientificView().PixelsPerMeter(focusDepth):0; state.scientificOverlay=scientificOverlay.Status;
            Vector3 forward=view.transform.forward; state.viewForward=new[]{forward.x,forward.y,forward.z};
        }
        state.flowDirection=new[] {direction.x,direction.y,direction.z}; state.intensity=effect.x; state.effectClock=clock;
        if(resident!=null) { state.materialMode=resident.Mode; state.materialDiagnostic=resident.Diagnostic; state.materialTopologyId=resident.TopologyId; state.materialStateSha256=resident.StateSha256; state.materialBundleSha256=resident.BundleSha256; state.materialOriginalBundleSha256=resident.OriginalBundleSha256; state.materialGeneration=resident.Generation; state.materialInstances=resident.InstanceCount; state.materialMeshes=resident.MeshCount; state.materialPackets=resident.PacketCount; state.materialDrawCalls=resident.DrawCalls; state.materialSubmittedFrames=resident.SubmittedFrames; state.materialSnapshotPublications=resident.SnapshotPublications; state.contract="Resident real-material fixture; optional shipped shader reference, native comparisons qualify only their captured dependencies."; }
        else { state.materialMode=null; state.materialDiagnostic=null; state.materialTopologyId=null; state.materialStateSha256=null; state.materialBundleSha256=null; state.materialGeneration=0; state.materialInstances=0; state.materialMeshes=0; state.materialPackets=0; state.materialDrawCalls=0; state.materialSubmittedFrames=0; state.materialSnapshotPublications=0; state.materialOccupiedPixels=0; state.materialPixelProof=false; state.contract="Frozen gray opaque fixture or labelled analytic geometry; isolated experimental fields, no stock appearance equivalence."; }
        string target=Path.Combine(output,"status.json"),temp=target+".tmp"; File.WriteAllText(temp,JsonUtility.ToJson(state,true)); if(File.Exists(target)) File.Replace(temp,target,null); else File.Move(temp,target);
    }
    void OnDestroy()
    {
        scientificOverlay.Dispose();
        if(state.benchmarkActive) EndBenchmark("interrupted","host-destroyed");
        if(resident!=null) { resident.Dispose(); resident=null; }
        stopping=true; listener?.Stop(); Command command; while(commands.TryDequeue(out command)) { command.response="{\"error\":\"host-stopping\"}"; command.done=true; }
        if(view!=null&&ribbonCommands!=null) view.RemoveCommandBuffer(CameraEvent.AfterEverything,ribbonCommands); ribbonCommands?.Release();
        if(view!=null&&sceneCommands!=null) view.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque,sceneCommands); sceneCommands?.Release();
        foreach(Material m in new[] {opaque,coverage,flowMaterial,sheath,ribbon,depthCopy}) if(m!=null) Destroy(m);
        foreach(RenderTexture rt in new[] {fieldMap,flowMap,sceneMap}) if(rt!=null) { rt.Release(); Destroy(rt); }
        foreach(Mesh mesh in meshes) Destroy(mesh); if(quad!=null) Destroy(quad); if(noise!=null) Destroy(noise); if(bundle!=null) bundle.Unload(true); if(field!=null) Destroy(field.gameObject); if(flow!=null) Destroy(flow.gameObject); if(geometryRoot!=null) Destroy(geometryRoot);
    }
    void OnApplicationQuit() { if(state.benchmarkActive) EndBenchmark("interrupted","application-quit"); }
}
