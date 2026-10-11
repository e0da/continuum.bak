using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KspContinuum;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class MultiscaleWorkbench : MonoBehaviour
{
    [Serializable] sealed class State
    {
        public VesselReplaySession.Observation replay;
        public string schema="continuum-multiscale-workbench/v1",status="starting",error,hostId,unity,backend,assemblySha256,scenePath,sceneSha256,vesselFixtureSha256,bodyGeometrySha256,bodyTextureSha256,bodyRecipe,preset="balanced",context="flight",focus,crop="rejected-unqualified-selective-coverage",geometryFallback="Only captured original meshes exist; no geometric LOD hierarchy",shadowFallback="Shadow requests unsatisfied; directional shadow maps disabled for first two-domain composition",lastPng,lastPngSha256,cleanupStatus;
        public string partBundleSha256,originalBundleSha256,bodyTextureCapture,materialMode="instanced",view="composed";
        public string debugShader="Continuum/MultiscaleDiagnostic",debugBundleSha256,debugLegend="Diagnostic only; no material fidelity or performance qualification",debugInputs="Actual captured geometry, camera-relative matrices, explicit white craft vertex COLOR, captured MainTex and ST; material IDs ordered by captured material index",fixtureScope="Captured material-family subset; not whole craft",fixtureFamily;
        public int fixtureInstances,fixtureMeshes,fixtureTextures;
        public string craftConsumer="mapped-subset",wholeCraftSha256,wholeGeometrySha256,wholeAppearance,wholeDependency,wholeCoverage;
        public int wholeCapturedRenderers,wholeRendererInventory,wholeOmittedRenderers,wholeDrawSlots,wholeMissingNormals,wholeMissingUV,wholeDefaultWhiteColors,wholeUnknownCull,wholeApproximateTransparency,wholeNoAlbedoTexture,wholeNonvisibleDraws;
        public int wholeForceRenderingOffObserved,wholePropertyBlockObserved,wholeFrozenSkinObserved;
        public long wholeRawTexturePayloadBudgetBytes,wholeRawTexturePayloadBytes;
        public string wholeTextureBudgetScope="1 GiB aggregate CPU raw/encoded texture payload admission; same bytes are GPU encoded estimate, excludes decoded/native/driver allocations";
        public string wholeRenderMode="reference",lastBenchmark,lastBenchmarkStatus,lastBenchmarkReason,debugBundlePath;
        public int wholeRecipes,wholePackets,wholeLastSubmissionCalls; public long wholePreparationTicks,wholeSubmissionTicks,clockFrequency;
        public int wholeStateUploadCount,wholeStateTextureId,wholeCommandBuildCount,wholeRetainedCommandCount;
        public long wholeNativeExecuteCount; public int wholeViewUniformUpdateCount;
        public long wholeStateUploadBytes; public string wholeSubmissionScope;
        public bool benchmarkActive;
        public string focusInstance; public double[] focusTargetMeters;
        public int debugShaderGeneration,craftDrawCalls; public float depthDisplayNear,depthDisplayFar; public int actualBodyMipCount; public string bodyMipPolicySatisfaction;
        public int processId,port,frames,bodyDraws,craftFrames,width,height,bodyWidth,bodyHeight,craftWidth,craftHeight,commands,sceneReloads;
        public long vesselGeneration; public double distance,logDistance; public double[] cameraAnchorMeters;
        public float yaw,pitch,fov,bodyNear,bodyFar,craftNear,craftFar;
        public float bodyWorldScale,craftWorldScale,bodyMipBias,craftMipBias,bodyGeometryErrorPixels,craftGeometryErrorPixels;
        public string bodyPolicy,craftPolicy,bodyShadowPolicy,craftShadowPolicy;
        public bool reversedZ,uiNativeResolution=true,opaqueOnly=true,splashFinishedAtCapture,quitReplyWritten;
    }
    sealed class Generation : IDisposable
    {
        public MultiscaleSceneManifest Scene; public ResidentMaterialRuntime Craft; public WholeCraftRuntime Whole; public Mesh Body; public Texture2D Texture; public Material Material,DebugMaterial; public string Hash;
        public Bounds CraftBounds { get { return Whole==null?Craft.Bounds:Whole.Bounds; } }
        public Vector3 Center(string id,out float radius) { return Whole==null?Craft.PresentationInstanceCenter(id,out radius):Whole.PresentationInstanceCenter(id,out radius); }
        public void Debug(string mode) { if(Whole!=null) Whole.SetGeometryDebug(mode); else Craft.SetGeometryDebug(mode); }
        public void Dispose() { if(Whole!=null) Whole.Dispose(); if(Craft!=null) Craft.Dispose(); if(Body!=null) UnityEngine.Object.Destroy(Body); if(Texture!=null) UnityEngine.Object.Destroy(Texture); if(Material!=null) UnityEngine.Object.Destroy(Material); if(DebugMaterial!=null) UnityEngine.Object.Destroy(DebugMaterial); }
    }
    sealed class Command { public string Text,Reply; public volatile bool Done,ReplyWritten; }
    sealed class Target : IDisposable
    {
        public Camera Camera; public RenderTexture Color,Depth; public CommandBuffer Copy; public bool CopyAttached;
        public void Dispose() { if(Camera!=null) { Camera.targetTexture=null; if(Copy!=null&&CopyAttached) Camera.RemoveCommandBuffer(CameraEvent.AfterEverything,Copy); } if(Copy!=null) Copy.Release(); if(Color!=null) { Color.Release(); UnityEngine.Object.Destroy(Color); } if(Depth!=null) { Depth.Release(); UnityEngine.Object.Destroy(Depth); } }
    }
    readonly State state=new State(); readonly ConcurrentQueue<Command> queue=new ConcurrentQueue<Command>();
    VesselReplaySession replay;
    PresentationQualityOverride[] overrides=new PresentationQualityOverride[0]; Generation generation; Camera present,craftCamera,bodyCamera; Target craftTarget,bodyTarget; Material composite,depthCopy;
    EffectivePresentationQuality craftQuality,bodyQuality; bool policyDirty=true;
    string output,partBundle,originalBundle,seen; double distance; float yaw,pitch; double pollAt; bool capturing,settingsOwned,quitRequested; TcpListener listener; Thread socket; volatile bool stopping; int queued;
    readonly double[] cameraAnchor=new double[3]; readonly List<Camera> disabledCameras=new List<Camera>(); int priorAA,priorVsync,priorFrameRate;
    readonly double[] bodyGeometryAnchor=new double[3];
    readonly double[] focusAnchor=new double[3];
    Vector4 priorCameraPosition,priorCameraDirection;
    Material debugDepth; Shader debugShader; AssetBundle debugBundle; bool bodyGeometryDebug;
    string donorMaterialMode="instanced";
    [Serializable] sealed class Benchmark
    {
        public string schema="continuum-multiscale-benchmark/v1",status,reason,hostId,sceneSha256,wholeCraftSha256,debugBundleSha256,assemblySha256,mode,view,frozenControlsJson;
        public string scope="Consecutive Update boundaries containing one witnessed previous presented frame; craft, body, composition, UI and waits included; no simulation or GPU timer claim";
        public int processId,width,height,antiAliasing,qualityLevel,vSyncCount,targetFrameRate,sampleCount,capacity=65536,omittedDeadlineStraddlers,commandsDuringCapture;
        public string colorSpace; public double warmupSeconds,requestedCaptureSeconds,measuredSeconds;
        public string processResourceStatus,processResourceError,processResourceScope="Whole-process CPU/RSS sampled outside retained frame intervals; may include final omitted deadline straddler; not GPU or exclusive submission cost";
        public long processResourceStartTicks,processResourceEndTicks,workingSetStartBytes,workingSetEndBytes;
        public double processCpuSeconds;
        public int processResourceStartFrame,processResourceEndFrame,stateUploadsStart,stateUploadsEnd,commandBuildsStart,commandBuildsEnd;
        public long stateUploadBytesStart,stateUploadBytesEnd; public string submissionScope;
        public long frequency,windowStartTicks,windowEndTicks,preparationTicks; public double[] frameMilliseconds,submissionMilliseconds; public long[] frameStartedTicks,frameEndedTicks; public int[] renderedFrames,submissionCalls;
    }
    Benchmark benchmark; long warmupEnd,deadline,previousTick,previousSubmission; int previousFrame,benchmarkPriorVsync,benchmarkPriorCap,lastPresentedFrame=-1,presentedInFrame; bool benchmarkCapturing;
    System.Diagnostics.Process benchmarkProcess; TimeSpan benchmarkCpuStart;
    static readonly string[] debugViews={"composed","craft-color","body-color","craft-flat","body-flat","craft-world-normals","body-world-normals","craft-view-normals","body-view-normals","craft-albedo","body-albedo","craft-vertex-colors","craft-uv","body-uv","craft-material-id","body-material-id","craft-linear-depth","body-linear-depth","craft-log-depth","body-log-depth"};
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Bootstrap()
    { if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_SCENE"))) new GameObject("Continuum Multiscale Workbench").AddComponent<MultiscaleWorkbench>(); }
    static string Absolute(string path) { if(string.IsNullOrEmpty(path)||!Path.IsPathRooted(path)) throw new InvalidDataException("Absolute private path required."); return Path.GetFullPath(path); }
    static string Under(string root,string relative) { string prefix=Path.GetFullPath(root)+Path.DirectorySeparatorChar,result=Path.GetFullPath(Path.Combine(root,relative)); if(!result.StartsWith(prefix,StringComparison.Ordinal)) throw new InvalidDataException("Asset escapes scene directory."); return result; }
    static string Hash(string path) { using(var s=File.OpenRead(path)) using(var h=SHA256.Create()) return Hex(h.ComputeHash(s)); }
    static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant(); }
    static string BytesHash(byte[] bytes) { using(var h=SHA256.Create()) return Hex(h.ComputeHash(bytes)); }
    static double Number(string text) { double n=double.Parse(text,CultureInfo.InvariantCulture); if(double.IsNaN(n)||double.IsInfinity(n)) throw new InvalidDataException("Finite control required."); return n; }
    static Matrix4x4 Matrix(double[] values) { var m=new Matrix4x4(); for(int r=0;r<4;r++) for(int c=0;c<4;c++) m[r,c]=(float)values[r*4+c]; return m; }
    void Start()
    {
        try
        {
            output=Absolute(Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_OUTPUT")); if(Directory.Exists(output)||File.Exists(output)) throw new InvalidDataException("New output directory required."); Directory.CreateDirectory(output);
            state.hostId=Guid.NewGuid().ToString("N"); state.processId=System.Diagnostics.Process.GetCurrentProcess().Id; state.unity=Application.unityVersion; state.backend=SystemInfo.graphicsDeviceType.ToString(); state.reversedZ=SystemInfo.usesReversedZBuffer; state.assemblySha256=Hash(typeof(MultiscaleWorkbench).Assembly.Location);
            bool modernProbe=Environment.GetEnvironmentVariable("CONTINUUM_MODERN_UNITY_PROBE")=="1"&&state.unity=="6000.3.26f1";
            if((state.unity!="2019.4.18f1"&&!modernProbe)||!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)) throw new InvalidDataException("Pinned player and linear-depth targets required.");
            priorAA=QualitySettings.antiAliasing; priorVsync=QualitySettings.vSyncCount; priorFrameRate=Application.targetFrameRate; QualitySettings.antiAliasing=0; QualitySettings.vSyncCount=0; Application.targetFrameRate=-1;
            priorCameraPosition=Shader.GetGlobalVector("_LocalCameraPos"); priorCameraDirection=Shader.GetGlobalVector("_LocalCameraDir");
            settingsOwned=true;
            foreach(Camera c in FindObjectsOfType<Camera>()) if(c.enabled) { disabledCameras.Add(c); c.enabled=false; }
            present=gameObject.AddComponent<Camera>(); present.cullingMask=0; present.clearFlags=CameraClearFlags.SolidColor; present.backgroundColor=Color.black; present.allowMSAA=false;
            craftCamera=NewCamera("Craft depth domain"); bodyCamera=NewCamera("Body depth domain");
            composite=NewMaterial("Hidden/Continuum/MultiscaleComposite"); depthCopy=NewMaterial("Hidden/Continuum/MultiscaleDepthCopy");
            debugDepth=NewMaterial("Hidden/Continuum/MultiscaleDebugDepth"); debugShader=Shader.Find("Continuum/MultiscaleDiagnostic"); ValidateDebugShader(debugShader);
            partBundle=Absolute(Environment.GetEnvironmentVariable("CONTINUUM_RESIDENT_PART_BUNDLE")); originalBundle=Environment.GetEnvironmentVariable("CONTINUUM_RESIDENT_ORIGINAL_BUNDLE");
            Reload(Absolute(Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_SCENE")),true);
            Camera.onPreCull+=BeforeCamera; listener=new TcpListener(IPAddress.Loopback,0); listener.Start(4); state.port=((IPEndPoint)listener.LocalEndpoint).Port; socket=new Thread(SocketLoop) {IsBackground=true,Name="Continuum multiscale control"}; socket.Start();
            state.status="resident"; Write();
        }
        catch(Exception e) { Fail(e); }
    }
    Camera NewCamera(string name) { var c=new GameObject(name).AddComponent<Camera>(); c.enabled=false; c.allowMSAA=false; c.renderingPath=RenderingPath.Forward; c.depthTextureMode=DepthTextureMode.Depth; c.backgroundColor=Color.clear; c.clearFlags=CameraClearFlags.SolidColor; return c; }
    static Material NewMaterial(string name) { Shader shader=Shader.Find(name); if(shader==null||!shader.isSupported) throw new InvalidDataException("Required original shader unsupported: "+name); return new Material(shader); }
    Generation Prepare(string path)
    {
        var next=new Generation(); try
        {
            if(new FileInfo(path).Length>1048576) throw new InvalidDataException("Scene manifest bound."); next.Hash=Hash(path); next.Scene=JsonUtility.FromJson<MultiscaleSceneManifest>(File.ReadAllText(path)); MultiscaleSceneFormat.Validate(next.Scene);
            var scene=next.Scene; bool modernProbe=Environment.GetEnvironmentVariable("CONTINUUM_MODERN_UNITY_PROBE")=="1"&&Application.unityVersion=="6000.3.26f1"&&scene.engine=="2019.4.18f1";
            if((scene.engine!=Application.unityVersion&&!modernProbe)||scene.colorSpace!=QualitySettings.activeColorSpace.ToString()) throw new InvalidDataException("Scene engine/color-space mismatch."); string root=Path.GetDirectoryName(path);
            double initialNorm=scene.initialCameraOffsetMeters.Sum(v=>v*v); if(initialNorm<.0625||double.IsInfinity(initialNorm)) throw new InvalidDataException("Initial camera offset bound.");
            if(generation!=null&&(generation.Scene.vesselId!=scene.vesselId||generation.Scene.bodyId!=scene.bodyId)) throw new InvalidDataException("Scene HMR retains logical object identities.");
            string vessel=Under(root,scene.vesselFixture),geometry=Under(root,scene.bodyGeometry),texture=Under(root,scene.bodyTexture.payload);
            if(Hash(vessel)!=scene.vesselFixtureSha256||Hash(geometry)!=scene.bodyGeometrySha256) throw new InvalidDataException("Scene asset hash mismatch.");
            next.Craft=new ResidentMaterialRuntime(vessel,partBundle,string.IsNullOrEmpty(originalBundle)?null:Absolute(originalBundle),true); next.Craft.SetMode(donorMaterialMode); next.Craft.IncludePresentationLightLayer(29); next.Craft.DisablePresentationShadows();
            if((next.Craft.LayerMask&(1<<29))!=0) throw new InvalidDataException("Craft layer overlaps owned body domain.");
            var snapshot=ResidentMaterialFormat.ReadSnapshot(next.Craft.StatePath); if(snapshot.instances.Any(i=>i.opacityEffective!=1)) throw new InvalidDataException("Opaque-only composition rejects translucent craft state.");
            ResidentGeometry body=ResidentMaterialFormat.ReadGeometry(geometry); if(body.meshes.Length!=1) throw new InvalidDataException("One captured scaled body mesh required."); var g=body.meshes[0]; int count=g.positions.Length/3; var p=new Vector3[count]; var n=new Vector3[count]; var uv=new Vector2[count];
            for(int i=0;i<count;i++) { p[i]=new Vector3(g.positions[i*3],g.positions[i*3+1],g.positions[i*3+2]); n[i]=new Vector3(g.normals[i*3],g.normals[i*3+1],g.normals[i*3+2]); uv[i]=new Vector2(g.uv[i*2],g.uv[i*2+1]); }
            next.Body=new Mesh {indexFormat=IndexFormat.UInt32,vertices=p,normals=n,uv=uv,triangles=g.indices}; next.Body.RecalculateBounds();
            var t=scene.bodyTexture; byte[] bytes=File.ReadAllBytes(texture); if(bytes.LongLength!=t.payloadLength||BytesHash(bytes)!=t.payloadSha256) throw new InvalidDataException("Body raw texture mismatch."); next.Texture=new Texture2D(t.width,t.height,(TextureFormat)t.format,t.mipCount>1,t.linear); next.Texture.LoadRawTextureData(bytes); next.Texture.Apply(false,true); next.Texture.filterMode=(FilterMode)t.filter; next.Texture.wrapMode=(TextureWrapMode)t.wrap; next.Texture.anisoLevel=t.aniso;
            var formatProperty=typeof(Texture).GetProperty("graphicsFormat"); if(next.Texture.mipmapCount!=t.mipCount||(int)next.Texture.format!=t.format||formatProperty==null||Convert.ToInt32(formatProperty.GetValue(next.Texture,null))!=t.graphicsFormat) throw new InvalidDataException("Body native texture format mismatch.");
            next.Material=NewMaterial("Continuum/MultiscaleBodyDiffuse"); next.Material.SetTexture("_MainTex",next.Texture); next.Material.color=new Color(scene.bodyColor[0],scene.bodyColor[1],scene.bodyColor[2],scene.bodyColor[3]); var st=scene.bodyTextureScaleOffset; next.Material.SetTextureScale("_MainTex",new Vector2(st[0],st[1])); next.Material.SetTextureOffset("_MainTex",new Vector2(st[2],st[3]));
            next.Craft.CommitDebugShader(next.Craft.PrepareDebugShader(debugShader)); next.DebugMaterial=PrepareBodyDebug(next,debugShader);
            if(!string.IsNullOrEmpty(scene.wholeCraft)) { string wholePath=Under(root,scene.wholeCraft); if(Hash(wholePath)!=scene.wholeCraftSha256) throw new InvalidDataException("Whole-craft manifest mismatch."); next.Whole=new WholeCraftRuntime(wholePath,debugShader); next.Whole.SetRenderMode(state.wholeRenderMode); if(next.Whole.ManifestSha256!=scene.wholeCraftSha256||next.Whole.Manifest.vesselId!=scene.vesselId) throw new InvalidDataException("Whole-craft identity mismatch."); if((next.Whole.LayerMask&(1<<29))!=0) throw new InvalidDataException("Whole craft/body layer overlap."); }
            if(state.focusInstance!=null) { float radius; next.Center(state.focusInstance,out radius); }
            if(Hash(path)!=next.Hash||Hash(vessel)!=scene.vesselFixtureSha256||Hash(geometry)!=scene.bodyGeometrySha256) throw new InvalidDataException("Scene changed during preparation."); return next;
        }
        catch { next.Dispose(); throw; }
    }
    void Reload(string path,bool initial)
    {
        if(replay!=null) throw new InvalidOperationException("Scene reload frozen while a recording is bound.");
        var next=Prepare(path); try { if(generation!=null) generation.Craft.ValidateActivationTransferTo(next.Craft); next.Craft.Activate(craftCamera,false); if(generation!=null) generation.Craft.TransferActivationTo(next.Craft); }
        catch { next.Dispose(); throw; }
        var previous=generation; generation=next; policyDirty=true; if(previous!=null) previous.Dispose(); state.scenePath=path; state.sceneSha256=seen=next.Hash; state.vesselFixtureSha256=next.Scene.vesselFixtureSha256; state.bodyGeometrySha256=next.Scene.bodyGeometrySha256; state.bodyTextureSha256=next.Scene.bodyTexture.payloadSha256; state.bodyRecipe=next.Scene.bodyMaterialRecipe; state.bodyTextureCapture=next.Scene.bodyTextureCapture; state.partBundleSha256=next.Craft.BundleSha256; state.originalBundleSha256=next.Craft.OriginalBundleSha256; state.sceneReloads++;
        if(initial) { state.focus=next.Scene.initialCameraTarget; var o=next.Scene.initialCameraOffsetMeters; distance=Math.Sqrt(o[0]*o[0]+o[1]*o[1]+o[2]*o[2]); yaw=(float)(Math.Atan2(-o[0],-o[2])*180/Math.PI); pitch=(float)(Math.Asin(o[1]/distance)*180/Math.PI); }
        SelectView(state.view);
        state.fixtureInstances=generation.Craft.InstanceCount; state.fixtureMeshes=generation.Craft.MeshCount; state.fixtureTextures=generation.Craft.Manifest.textures.Length; state.fixtureFamily=generation.Craft.Manifest.family;
        state.materialMode=generation.Whole==null?generation.Craft.Mode:generation.Whole.RenderMode+"-diagnostic";
        if(generation.Whole!=null) { var w=generation.Whole; state.craftConsumer="whole-craft-diagnostic"; state.wholeCraftSha256=w.ManifestSha256; state.wholeGeometrySha256=w.GeometrySha256; state.fixtureInstances=w.Manifest.draws.Length; state.fixtureMeshes=w.MeshCount; state.fixtureTextures=w.Manifest.textures.Length; state.fixtureFamily="multiple original families, approximate diagnostic"; state.fixtureScope="Whole captured triangle fixture; explicit omissions, not faithful whole craft"; state.wholeAppearance="two-sided opaque approximate MainTex/ST*tint*actual vertexRGB; no normal-map/specular/transparency parity"; state.wholeDependency="Mapped subset retained only for captured camera/light/global lease; no subset draws"; state.wholeRendererInventory=w.Manifest.rendererInventory; state.wholeCapturedRenderers=w.Manifest.capturedRenderers; state.wholeOmittedRenderers=w.Manifest.omittedRenderers; state.wholeDrawSlots=w.Manifest.drawSlots; state.wholeMissingNormals=w.MissingNormals; state.wholeMissingUV=w.MissingUV; state.wholeDefaultWhiteColors=w.DefaultWhiteColors; state.wholeUnknownCull=w.UnknownCull; state.wholeApproximateTransparency=w.ApproximateTransparency; state.wholeNoAlbedoTexture=w.NoAlbedoTexture; state.wholeNonvisibleDraws=w.NonvisibleDraws; state.wholeRawTexturePayloadBudgetBytes=WholeCraftRuntime.RawTexturePayloadBudgetBytes; state.wholeRawTexturePayloadBytes=w.RawTexturePayloadBytes; state.wholeForceRenderingOffObserved=w.ForceRenderingOffObserved; state.wholePropertyBlockObserved=w.PropertyBlockObserved; state.wholeFrozenSkinObserved=w.FrozenSkinObserved; state.debugInputs="Whole actual geometry/colors/poses; captured material properties and MainTex/ST; property blocks are observed but not reproduced"; state.wholeCoverage="Captured geometry draws only; omissions/nonvisible rows retained in immutable manifest"; } else { state.craftConsumer="mapped-subset"; state.fixtureScope="Captured material-family subset; not whole craft"; }
    }
    void Update()
    {
        if(generation==null||state.status=="failed") return;
        if(state.benchmarkActive) TickBenchmark();
        Command cmd; while(queue.TryDequeue(out cmd)) { Interlocked.Decrement(ref queued); bool quitting=false; try { Execute(cmd.Text); cmd.Reply=JsonUtility.ToJson(state); quitting=cmd.Text=="quit"; } catch(Exception e) { cmd.Reply="{\"error\":\""+e.Message.Replace("\"","'")+"\"}"; } cmd.Done=true; if(quitting) StartCoroutine(QuitAfterReply(cmd)); }
        if(quitRequested) return;
        if(replay!=null&&!capturing&&!state.benchmarkActive) try { replay.Tick(); state.replay=replay.Status; } catch(Exception error) { replay.Fault(error); state.replay=replay.Status; Write(); }
        if(replay==null&&!capturing&&!state.benchmarkActive&&Time.realtimeSinceStartup>=pollAt) { pollAt=Time.realtimeSinceStartup+1; try { if(Hash(state.scenePath)!=seen) Reload(state.scenePath,false); } catch(Exception e) { state.error="Rejected reload: "+e.Message; Write(); } }
        if(!capturing&&!state.benchmarkActive&&Input.mouseScrollDelta.y!=0) distance=Math.Max(.25,Math.Min(1e10,distance*Math.Exp(-Input.mouseScrollDelta.y*.15)));
    }
    void LateUpdate()
    {
        if(generation==null||state.status=="failed") return;
        try
        {
            var s=generation.Scene; bool bodyFocus=state.focus==s.bodyId; double[] anchor=bodyFocus?s.bodyAnchorMeters:s.vesselAnchorMeters;
            if(state.focusInstance!=null) { float instanceRadius; Vector3 local=generation.Center(state.focusInstance,out instanceRadius); for(int i=0;i<3;i++) focusAnchor[i]=s.vesselAnchorMeters[i]+local[i]; anchor=focusAnchor; } state.focusTargetMeters=anchor;
            if(bodyFocus) distance=Math.Max(distance,s.bodyRadiusMeters*1.01); Vector3 offset=Quaternion.Euler(pitch,yaw,0)*Vector3.back;
            for(int i=0;i<3;i++) cameraAnchor[i]=anchor[i]+offset[i]*distance;
            float x,y,z; MultiscaleSceneFormat.RelativePosition(s.vesselAnchorMeters,cameraAnchor,out x,out y,out z); if(generation.Whole==null) generation.Craft.SetPresentationAnchor(new Vector3(x,y,z)); else generation.Whole.SetPresentationAnchor(new Vector3(x,y,z));
            Quaternion rotation=Quaternion.LookRotation(-offset,Vector3.up); present.transform.SetPositionAndRotation(Vector3.zero,rotation); present.fieldOfView=s.fov;
            craftCamera.transform.SetPositionAndRotation(Vector3.zero,rotation); bodyCamera.transform.SetPositionAndRotation(Vector3.zero,rotation); craftCamera.fieldOfView=bodyCamera.fieldOfView=s.fov; craftCamera.aspect=bodyCamera.aspect=present.aspect;
            if(policyDirty) { craftQuality=PresentationQualityPolicy.Resolve(state.preset,state.context,"craft","world",s.vesselId,overrides); bodyQuality=PresentationQualityPolicy.Resolve(state.preset,state.context,"body","world",s.bodyId,overrides); state.craftShadowPolicy=PresentationQualityPolicy.Resolve(state.preset,state.context,"craft","shadow",s.vesselId,overrides).provenance; state.bodyShadowPolicy=PresentationQualityPolicy.Resolve(state.preset,state.context,"body","shadow",s.bodyId,overrides).provenance; policyDirty=false; }
            var cq=craftQuality; var bq=bodyQuality; state.craftPolicy=cq.provenance; state.bodyPolicy=bq.provenance;
            state.craftWorldScale=cq.worldScale; state.bodyWorldScale=bq.worldScale; state.craftMipBias=cq.textureMipBias; state.bodyMipBias=bq.textureMipBias; state.craftGeometryErrorPixels=cq.geometryErrorPixels; state.bodyGeometryErrorPixels=bq.geometryErrorPixels;
            if(generation.Whole==null) generation.Craft.SetTextureMipBias(cq.textureMipBias); else generation.Whole.SetTextureMipBias(cq.textureMipBias); generation.Texture.mipMapBias=s.bodyTexture.mipBias+bq.textureMipBias;
            Resize(ref craftTarget,craftCamera,cq.worldScale); Resize(ref bodyTarget,bodyCamera,bq.worldScale); craftCamera.cullingMask=generation.Whole==null?generation.Craft.LayerMask:generation.Whole.LayerMask; bodyCamera.cullingMask=1<<29;
            ConfigureCraftDepth();
            Vector3 center=generation.CraftBounds.center; float radius=Math.Max(.01f,generation.CraftBounds.extents.magnitude); double d=center.magnitude; craftCamera.nearClipPlane=(float)Math.Max(.001,(d-radius)*.5); craftCamera.farClipPlane=(float)Math.Max(craftCamera.nearClipPlane*2,d+radius*2);
            for(int i=0;i<3;i++) bodyGeometryAnchor[i]=s.bodyAnchorMeters[i]+s.bodyLocalToMeters[i*4+3];
            MultiscaleSceneFormat.RelativePosition(bodyGeometryAnchor,cameraAnchor,out x,out y,out z); Matrix4x4 bodyMatrix=Matrix(s.bodyLocalToMeters); bodyMatrix.m03=x; bodyMatrix.m13=y; bodyMatrix.m23=z;
            double bd=Math.Sqrt((double)x*x+(double)y*y+(double)z*z); bodyCamera.nearClipPlane=(float)Math.Max(1,(bd-s.bodyRadiusMeters)*.5); bodyCamera.farClipPlane=(float)Math.Max(bodyCamera.nearClipPlane*2,bd+s.bodyRadiusMeters*2);
            if(bodyCamera.nearClipPlane<=0||craftCamera.nearClipPlane<=0) throw new InvalidDataException("Invalid depth domain.");
            Shader.SetGlobalVector("_LocalCameraPos",new Vector4(0,0,0,priorCameraPosition.w)); Vector3 forward=rotation*Vector3.forward; Shader.SetGlobalVector("_LocalCameraDir",new Vector4(forward.x,forward.y,forward.z,priorCameraDirection.w));
            Graphics.DrawMesh(generation.Body,bodyMatrix,bodyGeometryDebug?generation.DebugMaterial:generation.Material,29,bodyCamera,0,null,ShadowCastingMode.On,false); bodyCamera.Render();
            if(generation.Whole!=null&&(generation.Whole.RenderMode=="retained-scalar"||generation.Whole.RenderMode=="retained-grouped")) generation.Whole.Submit(craftCamera); else craftCamera.Render();
            state.craftDrawCalls=generation.Whole==null?generation.Craft.DrawCalls:generation.Whole.DrawCalls; state.actualBodyMipCount=generation.Texture.mipmapCount; state.bodyMipPolicySatisfaction=state.actualBodyMipCount==1?"unsupported-single-level":"bias-applied-unqualified";
            if(generation.Whole!=null) { state.wholeNonvisibleDraws=generation.Whole.NonvisibleDraws; state.wholeRecipes=generation.Whole.RecipeCount; state.wholePackets=generation.Whole.PacketCount; state.wholeLastSubmissionCalls=generation.Whole.LastSubmissionCalls; state.wholePreparationTicks=generation.Whole.PreparationTicks; state.wholeSubmissionTicks=generation.Whole.SubmissionTicks; state.clockFrequency=System.Diagnostics.Stopwatch.Frequency; state.wholeStateUploadCount=generation.Whole.StateUploadCount; state.wholeStateTextureId=generation.Whole.StateTextureId; state.wholeStateUploadBytes=generation.Whole.StateUploadBytes; state.wholeCommandBuildCount=generation.Whole.CommandBuildCount; state.wholeNativeExecuteCount=generation.Whole.NativeExecuteCount; state.wholeViewUniformUpdateCount=generation.Whole.ViewUniformUpdateCount; state.wholeRetainedCommandCount=generation.Whole.RetainedCommandCount; state.wholeSubmissionScope=generation.Whole.RetainedScope; }
            composite.SetTexture("_BodyColor",bodyTarget.Color); composite.SetTexture("_BodyDepth",bodyTarget.Depth); composite.SetTexture("_CraftColor",craftTarget.Color); composite.SetTexture("_CraftDepth",craftTarget.Depth);
            state.frames++; state.bodyDraws++; state.craftFrames=generation.Whole==null?generation.Craft.SubmittedFrames:generation.Whole.SubmittedFrames; state.width=Screen.width; state.height=Screen.height; state.bodyWidth=bodyTarget.Color.width; state.bodyHeight=bodyTarget.Color.height; state.craftWidth=craftTarget.Color.width; state.craftHeight=craftTarget.Color.height; state.distance=distance; state.logDistance=Math.Log10(distance); state.yaw=yaw; state.pitch=pitch; state.fov=s.fov; state.cameraAnchorMeters=cameraAnchor; state.vesselGeneration=generation.Craft.Generation; state.bodyNear=bodyCamera.nearClipPlane; state.bodyFar=bodyCamera.farClipPlane; state.craftNear=craftCamera.nearClipPlane; state.craftFar=craftCamera.farClipPlane;
            if(!state.benchmarkActive&&state.frames%30==0) Write();
        }
        catch(Exception e) { Fail(e); }
    }
    void BeforeCamera(Camera camera) { if(generation!=null&&camera==craftCamera) { if(generation.Whole==null) generation.Craft.Submit(camera); else generation.Whole.Submit(camera); } }
    void ConfigureCraftDepth()
    {
        bool retained=generation.Whole!=null&&generation.Whole.RenderMode.StartsWith("retained-",StringComparison.Ordinal);
        craftCamera.depthTextureMode=retained?DepthTextureMode.None:DepthTextureMode.Depth;
        if(retained)
        {
            generation.Whole.ConfigureRetainedTargets(craftTarget.Color,craftTarget.Depth);
            if(craftTarget.CopyAttached) { craftCamera.RemoveCommandBuffer(CameraEvent.AfterEverything,craftTarget.Copy); craftTarget.CopyAttached=false; }
        }
        else if(!craftTarget.CopyAttached) { craftCamera.AddCommandBuffer(CameraEvent.AfterEverything,craftTarget.Copy); craftTarget.CopyAttached=true; }
    }
    void Resize(ref Target target,Camera camera,float scale)
    {
        int w=Math.Max(1,(int)Math.Ceiling(Screen.width*scale)),h=Math.Max(1,(int)Math.Ceiling(Screen.height*scale)); if(target!=null&&target.Color.width==w&&target.Color.height==h) return;
        if(target!=null) target.Dispose(); target=new Target {Camera=camera,Color=new RenderTexture(w,h,24,RenderTextureFormat.ARGB32) {antiAliasing=1,filterMode=FilterMode.Point},Depth=new RenderTexture(w,h,0,RenderTextureFormat.RFloat) {filterMode=FilterMode.Point}};
        if(!target.Color.Create()||!target.Depth.Create()) throw new InvalidDataException("World target allocation failed."); camera.targetTexture=target.Color;
        target.Copy=new CommandBuffer {name="Multiscale positive linear depth"}; target.Copy.Blit(BuiltinRenderTextureType.CameraTarget,target.Depth,depthCopy); camera.AddCommandBuffer(CameraEvent.AfterEverything,target.Copy); target.CopyAttached=true;
    }
    void OnRenderImage(RenderTexture source,RenderTexture destination)
    {
        if(lastPresentedFrame==Time.frameCount) presentedInFrame++; else { lastPresentedFrame=Time.frameCount; presentedInFrame=1; }
        if(composite==null||craftTarget==null||bodyTarget==null||state.status=="failed") { Graphics.Blit(source,destination); return; }
        if(state.view.EndsWith("linear-depth",StringComparison.Ordinal)||state.view.EndsWith("log-depth",StringComparison.Ordinal)) { bool body=state.view.StartsWith("body-",StringComparison.Ordinal); float near=body?bodyCamera.nearClipPlane:craftCamera.nearClipPlane,far=body?bodyCamera.farClipPlane:craftCamera.farClipPlane; state.depthDisplayNear=near; state.depthDisplayFar=far; debugDepth.SetVector("_Range",new Vector4(near,far,state.view.EndsWith("log-depth",StringComparison.Ordinal)?1:0,0)); Graphics.Blit(body?bodyTarget.Depth:craftTarget.Depth,destination,debugDepth); }
        else if(state.view=="craft-depth") Graphics.Blit(craftTarget.Depth,destination);
        else if(state.view=="body-depth") Graphics.Blit(bodyTarget.Depth,destination);
        else if(state.view.StartsWith("craft-",StringComparison.Ordinal)) Graphics.Blit(craftTarget.Color,destination);
        else if(state.view.StartsWith("body-",StringComparison.Ordinal)) Graphics.Blit(bodyTarget.Color,destination);
        else Graphics.Blit(source,destination,composite);
    }
    void Execute(string text)
    {
        string[] words=text.Split(new[] {' '},StringSplitOptions.RemoveEmptyEntries); if(words.Length==0) return; if(capturing) throw new InvalidOperationException("Capture controls frozen."); if(quitRequested) throw new InvalidOperationException("Owned shutdown pending."); if(state.benchmarkActive) { if(benchmarkCapturing) benchmark.commandsDuringCapture++; if(words[0]!="status"&&words[0]!="quit"&&words[0]!="cancelbenchmark") throw new InvalidOperationException("Benchmark freezes controls/captures/HMR."); } state.commands++;
        if(words[0]=="quit"&&words.Length==1) { if(state.benchmarkActive) EndBenchmark("interrupted","operator-quit"); quitRequested=true; state.status="stopping"; Write(); return; }
        if(words[0]=="status") { if(!state.benchmarkActive) Write(); return; }
        if(words[0]=="benchmark"&&words.Length==3) { BeginBenchmark(Number(words[1]),Number(words[2])); return; }
        if(words[0]=="cancelbenchmark"&&words.Length==1) { if(!state.benchmarkActive) throw new InvalidDataException("No active benchmark."); EndBenchmark("interrupted","operator-cancelled"); return; }
        if(words[0]=="captureframe") { if(replay!=null) replay.Pause(); StartCoroutine(Capture()); return; }
        if(words[0]=="replay.open"&&words.Length>=2) { var next=VesselReplaySession.Open(Absolute(text.Substring(words[0].Length).Trim()),state.sceneSha256,generation.Whole); replay=next; state.replay=next.Status; }
        else if(words[0]=="replay.resume"&&words.Length>=2) { var next=VesselReplaySession.Resume(Absolute(text.Substring(words[0].Length).Trim()),state.sceneSha256,generation.Whole); replay=next; state.replay=next.Status; }
        else if(words[0]=="replay.seek"&&words.Length==2) { RequireReplay(); replay.Seek(int.Parse(words[1],CultureInfo.InvariantCulture)); }
        else if(words[0]=="replay.play"&&(words.Length==1||words.Length==2)) { RequireReplay(); replay.Play(words.Length==2?Number(words[1]):1); }
        else if(words[0]=="replay.pause"&&words.Length==1) { RequireReplay(); replay.Pause(); }
        else if(words[0]=="replay.checkpoint"&&words.Length>=2) { RequireReplay(); replay.CheckpointTo(Absolute(text.Substring(words[0].Length).Trim())); }
        else if(words[0]=="materialmode"&&words.Length==2&&(words[1]=="reference"||words[1]=="instanced"||words[1]=="grouped"||words[1]=="retained-scalar"||words[1]=="retained-grouped")) { if(generation.Whole!=null) { generation.Whole.SetRenderMode(words[1]); state.wholeRenderMode=words[1]; state.materialMode=words[1]+"-diagnostic"; } else { generation.Craft.SetMode(words[1]); state.materialMode=donorMaterialMode=words[1]; } }
        else if(words[0]=="view"&&words.Length==2) SelectView(words[1]);
        else if(words[0]=="debugshaderreload") ReloadDebugShader(Absolute(text.Substring(words[0].Length).Trim()));
        else if(words[0]=="zoom"&&words.Length==2) distance=Math.Max(.25,Math.Min(1e10,Math.Pow(10,Number(words[1]))));
        else if(words[0]=="orbit"&&words.Length==3) { yaw=(float)Number(words[1]); pitch=Mathf.Clamp((float)Number(words[2]),-89,89); }
        else if(words[0]=="focusinstance"&&words.Length==2) { float radius; generation.Center(words[1],out radius); state.focus=generation.Scene.vesselId; state.focusInstance=words[1]; distance=Math.Max(.25,radius*3); }
        else if(words[0]=="focus"&&words.Length==2&&(words[1]==generation.Scene.vesselId||words[1]==generation.Scene.bodyId)) { state.focus=words[1]; state.focusInstance=null; }
        else if(words[0]=="preset"&&words.Length==2) { PresentationQualityPolicy.Resolve(words[1],state.context,"craft","world",generation.Scene.vesselId,overrides); state.preset=words[1]; }
        else if(words[0]=="override"&&words.Length==5) { string id=words[1]; if(id!=generation.Scene.bodyId&&id!=generation.Scene.vesselId) throw new InvalidDataException("Known asset ID required."); var next=overrides.Where(o=>o.assetId!=id).Concat(new[] {new PresentationQualityOverride {scope="asset",assetId=id,pass="world",scaleSet=true,worldScale=(float)Number(words[2]),mipSet=true,textureMipBias=(float)Number(words[3]),geometrySet=true,geometryErrorPixels=(float)Number(words[4])}}).ToArray(); PresentationQualityPolicy.Resolve(state.preset,state.context,id==generation.Scene.bodyId?"body":"craft","world",id,next); overrides=next; }
        else if(words[0]=="fixturehotreload") Reload(Absolute(text.Substring(words[0].Length).Trim()),false);
        else if(words[0]=="snapshotreload") { if(generation.Whole!=null) throw new InvalidDataException("Whole geometry/poses are immutable; reload scene generation instead of subset snapshot."); string path=Absolute(text.Substring(words[0].Length).Trim()); var snap=ResidentMaterialFormat.ReadSnapshot(path); if(snap.instances.Any(i=>i.opacityEffective!=1)) throw new InvalidDataException("Translucent snapshot rejected."); generation.Craft.ReloadSnapshot(path); }
        else throw new InvalidDataException("status|quit|captureframe|replay.open PATH|replay.seek INDEX|replay.play [RATE]|replay.pause|replay.checkpoint NEW_PATH|replay.resume PATH|materialmode reference/grouped/retained-scalar/retained-grouped (whole) or reference/instanced (subset)|benchmark WARMUP_SECONDS CAPTURE_SECONDS|cancelbenchmark|view composed or craft-/body- color/depth/flat/world-normals/view-normals/albedo/uv/material-id/linear-depth/log-depth (craft-vertex-colors only)|zoom log10metres|orbit yaw pitch|focus ID|focusinstance ID|preset low/balanced/high|override ID scale mip error|fixturehotreload PATH|snapshotreload PATH|debugshaderreload BUNDLE");
        policyDirty=true; Write();
    }
    void CheckBenchmarkSources()
    {
        if(Hash(state.scenePath)!=state.sceneSha256||Hash(Under(Path.GetDirectoryName(state.scenePath),generation.Scene.wholeCraft))!=state.wholeCraftSha256) throw new InvalidDataException("Bound benchmark manifest changed.");
        if(state.debugBundlePath!=null&&Hash(state.debugBundlePath)!=state.debugBundleSha256) throw new InvalidDataException("Bound diagnostic bundle changed.");
    }
    void RequireReplay() { if(replay==null) throw new InvalidOperationException("Open a bound replay first."); }
    void BeginBenchmark(double warmup,double seconds)
    {
        if(replay!=null) throw new InvalidOperationException("Static benchmark is not qualified for replay; use replay cost observations.");
        if(state.benchmarkActive||capturing||quitRequested||generation.Whole==null||!SplashScreen.isFinished||state.frames<1) throw new InvalidOperationException("Idle resident whole fixture after presentation required."); if(warmup<0||warmup>30||seconds<1||seconds>30) throw new InvalidDataException("Warmup0..30/capture1..30 seconds required."); CheckBenchmarkSources();
        benchmark=new Benchmark {hostId=state.hostId,processId=state.processId,sceneSha256=state.sceneSha256,wholeCraftSha256=state.wholeCraftSha256,debugBundleSha256=state.debugBundleSha256,assemblySha256=state.assemblySha256,mode=generation.Whole.RenderMode,view=state.view,width=Screen.width,height=Screen.height,antiAliasing=QualitySettings.antiAliasing,qualityLevel=QualitySettings.GetQualityLevel(),colorSpace=QualitySettings.activeColorSpace.ToString(),frequency=System.Diagnostics.Stopwatch.Frequency,warmupSeconds=warmup,requestedCaptureSeconds=seconds,preparationTicks=generation.Whole.PreparationTicks,frameMilliseconds=new double[65536],submissionMilliseconds=new double[65536],frameStartedTicks=new long[65536],frameEndedTicks=new long[65536],renderedFrames=new int[65536],submissionCalls=new int[65536]};
        benchmark.submissionScope=generation.Whole.RetainedScope; benchmarkPriorVsync=QualitySettings.vSyncCount; benchmarkPriorCap=Application.targetFrameRate; QualitySettings.vSyncCount=0; Application.targetFrameRate=-1; benchmark.vSyncCount=QualitySettings.vSyncCount; benchmark.targetFrameRate=Application.targetFrameRate; benchmark.frozenControlsJson=JsonUtility.ToJson(state); state.benchmarkActive=true; benchmarkCapturing=false; warmupEnd=System.Diagnostics.Stopwatch.GetTimestamp()+(long)(warmup*benchmark.frequency); Write();
    }
    void TickBenchmark()
    {
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        if(Screen.width!=benchmark.width||Screen.height!=benchmark.height||QualitySettings.vSyncCount!=0||Application.targetFrameRate!=-1||QualitySettings.antiAliasing!=benchmark.antiAliasing||QualitySettings.GetQualityLevel()!=benchmark.qualityLevel||QualitySettings.activeColorSpace.ToString()!=benchmark.colorSpace||!SplashScreen.isFinished||generation.Whole.RenderMode!=benchmark.mode||state.view!=benchmark.view) { EndBenchmark("invalid","effective-controls-changed"); return; }
        if(!benchmarkCapturing) { if(now<warmupEnd) return; BeginProcessResources(); benchmark.stateUploadsStart=generation.Whole.StateUploadCount; benchmark.stateUploadBytesStart=generation.Whole.StateUploadBytes; benchmark.commandBuildsStart=generation.Whole.CommandBuildCount; now=System.Diagnostics.Stopwatch.GetTimestamp(); benchmarkCapturing=true; benchmark.windowStartTicks=previousTick=now; previousFrame=Time.frameCount; previousSubmission=generation.Whole.SubmissionTicks; deadline=now+(long)(benchmark.requestedCaptureSeconds*benchmark.frequency); return; }
        if(now<=previousTick||Time.frameCount!=previousFrame+1||lastPresentedFrame!=previousFrame||presentedInFrame!=1) { EndBenchmark("invalid","missing-duplicate-or-nonconsecutive-presentation"); return; }
        if(now>deadline) { benchmark.omittedDeadlineStraddlers++; EndBenchmark(benchmark.sampleCount>0?"completed":"invalid",benchmark.sampleCount>0?"monotonic-window-complete":"no-complete-interval"); return; }
        int n=benchmark.sampleCount; if(n>=benchmark.capacity) { EndBenchmark("invalid","sample-capacity-exceeded"); return; } benchmark.frameStartedTicks[n]=previousTick; benchmark.frameEndedTicks[n]=now; benchmark.renderedFrames[n]=previousFrame; benchmark.frameMilliseconds[n]=(now-previousTick)*1000.0/benchmark.frequency; benchmark.submissionMilliseconds[n]=(generation.Whole.SubmissionTicks-previousSubmission)*1000.0/benchmark.frequency; benchmark.submissionCalls[n]=generation.Whole.LastSubmissionCalls; benchmark.sampleCount++; previousTick=now; previousFrame=Time.frameCount; previousSubmission=generation.Whole.SubmissionTicks;
    }
    void EndBenchmark(string status,string reason)
    {
        if(!state.benchmarkActive) return; state.benchmarkActive=false; EndProcessResources(); benchmark.stateUploadsEnd=generation.Whole.StateUploadCount; benchmark.stateUploadBytesEnd=generation.Whole.StateUploadBytes; benchmark.commandBuildsEnd=generation.Whole.CommandBuildCount; QualitySettings.vSyncCount=benchmarkPriorVsync; Application.targetFrameRate=benchmarkPriorCap; benchmark.status=status; benchmark.reason=reason; benchmark.windowEndTicks=benchmarkCapturing?previousTick:0; benchmark.measuredSeconds=benchmarkCapturing?(benchmark.windowEndTicks-benchmark.windowStartTicks)/(double)benchmark.frequency:0;
        if(benchmarkCapturing&&benchmark.mode.StartsWith("retained-",StringComparison.Ordinal)&&(benchmark.stateUploadsStart!=benchmark.stateUploadsEnd||benchmark.stateUploadBytesStart!=benchmark.stateUploadBytesEnd||benchmark.commandBuildsStart!=benchmark.commandBuildsEnd)) { benchmark.status="invalid"; benchmark.reason="retained-state-or-command-generation-changed"; }
        if(status=="completed"&&benchmark.commandsDuringCapture>0) { benchmark.status="invalid"; benchmark.reason="command-interference"; }
        try { CheckBenchmarkSources(); } catch(Exception e) { benchmark.status="invalid"; benchmark.reason="source-identity-drift: "+e.Message; }
        Array.Resize(ref benchmark.frameMilliseconds,benchmark.sampleCount); Array.Resize(ref benchmark.submissionMilliseconds,benchmark.sampleCount); Array.Resize(ref benchmark.frameStartedTicks,benchmark.sampleCount); Array.Resize(ref benchmark.frameEndedTicks,benchmark.sampleCount); Array.Resize(ref benchmark.renderedFrames,benchmark.sampleCount); Array.Resize(ref benchmark.submissionCalls,benchmark.sampleCount);
        try { string path=Path.Combine(output,"benchmark-"+DateTime.UtcNow.Ticks+".json"); File.WriteAllText(path+".tmp",JsonUtility.ToJson(benchmark,true)); File.Move(path+".tmp",path); state.lastBenchmark=path; state.lastBenchmarkStatus=benchmark.status; state.lastBenchmarkReason=benchmark.reason; }
        catch(Exception e) { state.lastBenchmark=null; state.lastBenchmarkStatus="invalid"; state.lastBenchmarkReason="benchmark-export-failed: "+e.Message; }
        finally { benchmark=null; benchmarkCapturing=false; Write(); }
    }
    void BeginProcessResources()
    {
        try
        {
            benchmarkProcess=System.Diagnostics.Process.GetCurrentProcess(); benchmarkProcess.Refresh();
            benchmarkCpuStart=benchmarkProcess.TotalProcessorTime; benchmark.workingSetStartBytes=benchmarkProcess.WorkingSet64;
            benchmark.processResourceStartFrame=Time.frameCount;
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
            benchmark.processResourceEndFrame=Time.frameCount;
            benchmark.processResourceStatus=benchmark.processCpuSeconds>=0?"available":"invalid-negative-cpu-delta";
        }
        catch(Exception error) { benchmark.processResourceStatus="unsupported"; benchmark.processResourceError=error.GetType().Name+": "+error.Message; }
        finally { benchmarkProcess.Dispose(); benchmarkProcess=null; }
    }
    static void ValidateDebugShader(Shader shader)
    {
        if(shader==null||!shader.isSupported||shader.name!="Continuum/MultiscaleDiagnostic") throw new InvalidDataException("Supported exact diagnostic shader required.");
        var probe=new Material(shader); try { foreach(string name in new[] {"_MainTex","_Mode","_MaterialColor","_Tint","_Cull","_ContinuumPacketContract"}) if(!probe.HasProperty(name)) throw new InvalidDataException("Diagnostic shader interface missing: "+name); if(probe.GetFloat("_ContinuumPacketContract")!=1) throw new InvalidDataException("Diagnostic shader declared packet contract differs."); } finally { Destroy(probe); }
    }
    static Material PrepareBodyDebug(Generation value,Shader shader)
    {
        var m=new Material(shader); m.SetTexture("_MainTex",value.Texture); m.SetTextureScale("_MainTex",value.Material.GetTextureScale("_MainTex")); m.SetTextureOffset("_MainTex",value.Material.GetTextureOffset("_MainTex")); m.SetColor("_MaterialColor",Color.magenta); return m;
    }
    void SelectView(string value)
    {
        if(value=="composed") { generation.Debug("off"); bodyGeometryDebug=false; state.view=value; state.debugLegend="Opaque craft/body composition; nearest positive depth wins; native-resolution UI"; return; }
        bool body=value.StartsWith("body-",StringComparison.Ordinal),craft=value.StartsWith("craft-",StringComparison.Ordinal); if(!body&&!craft) throw new InvalidDataException("View requires craft- or body- prefix.");
        string mode=value.Substring(body?5:6); bool geometry=mode!="color"&&mode!="depth"&&mode!="linear-depth"&&mode!="log-depth";
        int debug=geometry?ResidentMaterialRuntime.GeometryDebugMode(mode):0;
        if(body&&mode=="vertex-colors") throw new InvalidDataException("Body source COLOR was not captured; refusing fabricated vertex-color diagnostic.");
        generation.Debug(craft&&geometry?mode:"off"); bodyGeometryDebug=body&&geometry; if(bodyGeometryDebug) generation.DebugMaterial.SetFloat("_Mode",debug);
        state.view=value; state.debugLegend=geometry?"Unlit actual geometry; normals encoded [-1,1] to [0,1]; UV fractional RG; stable material-index hue":mode=="color"?"Captured color domain with normal material recipe; native-resolution UI":"Depth metres: raw clamps on display; linear near/far or log(1+depth)/log(1+far); black is unwritten";
    }
    void ReloadDebugShader(string path)
    {
        string digest=Hash(path); AssetBundle next=AssetBundle.LoadFromFile(path); if(next==null) throw new InvalidDataException("Diagnostic bundle load failed."); Material[] craft=null,whole=null; Material body=null; WholeCraftRuntime.DebugShaderCommit wholeCommit=null;
        try
        {
            Shader selected=null; foreach(var shader in next.LoadAllAssets<Shader>()) if(shader.name=="Continuum/MultiscaleDiagnostic") { if(selected!=null) throw new InvalidDataException("Duplicate diagnostic shader."); selected=shader; }
            ValidateDebugShader(selected); craft=generation.Craft.PrepareDebugShader(selected); if(generation.Whole!=null) whole=generation.Whole.PrepareDebugShader(selected); body=PrepareBodyDebug(generation,selected); if(bodyGeometryDebug) body.SetFloat("_Mode",ResidentMaterialRuntime.GeometryDebugMode(state.view.Substring(5)));
            if(Hash(path)!=digest) throw new InvalidDataException("Diagnostic bundle changed during preparation.");
            if(whole!=null) wholeCommit=generation.Whole.StageDebugShaderCommit(whole);
            var oldBundle=debugBundle; var oldBody=generation.DebugMaterial; if(wholeCommit!=null) { generation.Whole.CommitDebugShader(wholeCommit); whole=null; } generation.Craft.CommitDebugShader(craft); craft=null; generation.DebugMaterial=body; body=null; debugShader=selected; debugBundle=next; next=null; Destroy(oldBody); if(oldBundle!=null) oldBundle.Unload(true); state.debugBundleSha256=digest; state.debugBundlePath=path; state.debugShaderGeneration++; Write();
        }
        finally { if(wholeCommit!=null) wholeCommit.Dispose(); if(craft!=null) foreach(var m in craft) Destroy(m); if(whole!=null) foreach(var m in whole) Destroy(m); if(body!=null) Destroy(body); if(next!=null) next.Unload(true); }
    }
    IEnumerator QuitAfterReply(Command command)
    {
        float deadline=Time.realtimeSinceStartup+2;
        while(!command.ReplyWritten&&Time.realtimeSinceStartup<deadline) yield return null;
        state.quitReplyWritten=command.ReplyWritten; Write();
        yield return new WaitForEndOfFrame();
        Application.Quit();
    }
    IEnumerator Capture()
    {
        capturing=true; float deadline=Time.realtimeSinceStartup+30; while(!SplashScreen.isFinished&&Time.realtimeSinceStartup<deadline) yield return null; if(!SplashScreen.isFinished) { capturing=false; Fail(new InvalidOperationException("Splash did not finish before capture.")); yield break; } state.splashFinishedAtCapture=true; yield return new WaitForEndOfFrame(); RenderTexture old=RenderTexture.active; Texture2D image=null;
        try { RenderTexture.active=null; image=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false); image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0,false); image.Apply(false,false); byte[] png=image.EncodeToPNG(); string name="frame-"+state.frames+"-"+DateTime.UtcNow.Ticks+".png"; File.WriteAllBytes(Path.Combine(output,name),png); state.lastPng=name; state.lastPngSha256=BytesHash(png); Write(); }
        catch(Exception e) { Fail(e); }
        finally { RenderTexture.active=old; if(image!=null) Destroy(image); capturing=false; }
    }
    void OnGUI()
    {
        GUI.Box(new Rect(10,10,430,150),generation!=null&&generation.Whole!=null?"Whole craft snapshot / "+state.wholeRenderMode:"Captured material-family subset"); GUI.Label(new Rect(20,35,410,20),state.preset+" / "+state.distance.ToString("G5")+" m / "+state.view);
        float log=GUI.HorizontalSlider(new Rect(20,65,400,20),(float)Math.Log10(Math.Max(.25,distance)),-.6f,10); if(!capturing&&!state.benchmarkActive) distance=Math.Pow(10,log);
        if(GUI.Button(new Rect(20,90,100,22),"low")&&!capturing&&!state.benchmarkActive) { state.preset="low"; policyDirty=true; } if(GUI.Button(new Rect(130,90,100,22),"balanced")&&!capturing&&!state.benchmarkActive) { state.preset="balanced"; policyDirty=true; } if(GUI.Button(new Rect(240,90,100,22),"high")&&!capturing&&!state.benchmarkActive) { state.preset="high"; policyDirty=true; }
        GUI.Label(new Rect(20,120,410,30),generation!=null&&generation.Whole!=null?state.wholeCapturedRenderers+" captured / "+state.wholeOmittedRenderers+" omitted / "+state.fixtureInstances+" slots":state.fixtureInstances+" rows / "+state.fixtureMeshes+" meshes / "+state.materialMode);
        GUI.Label(new Rect(450,10,Screen.width-460,70),state.debugLegend);
        if(replay!=null) GUI.Label(new Rect(450,80,Screen.width-460,60),"Replay "+replay.Status.status+" / "+replay.Status.cursor+" of "+replay.Status.frames+" / UT "+replay.Status.universalTime.ToString("G10")+" / force "+replay.Status.forceSummary+" / moment "+replay.Status.momentSummary);
        for(int i=0;i<debugViews.Length;i++) if(GUI.Button(new Rect(10+(i%2)*215,170+(i/2)*23,210,22),debugViews[i])&&!capturing&&!state.benchmarkActive&&!quitRequested) { SelectView(debugViews[i]); Write(); }
    }
    void SocketLoop()
    {
        while(!stopping) try { using(var client=listener.AcceptTcpClient()) using(var stream=client.GetStream()) { stream.ReadTimeout=2000; stream.WriteTimeout=2000; var bytes=new List<byte>(); int b; while((b=stream.ReadByte())>=0&&b!='\n') { if(bytes.Count>=4096) throw new InvalidDataException("Command bound."); bytes.Add((byte)b); } if(Interlocked.Increment(ref queued)>16) { Interlocked.Decrement(ref queued); continue; } var c=new Command {Text=Encoding.UTF8.GetString(bytes.ToArray()).Trim()}; queue.Enqueue(c); for(int i=0;i<500&&!c.Done&&!stopping;i++) Thread.Sleep(10); if(c.Done) { byte[] reply=Encoding.UTF8.GetBytes(c.Reply+"\n"); stream.Write(reply,0,reply.Length); stream.Flush(); c.ReplyWritten=true; } } } catch { if(stopping) return; }
    }
    void Write() { if(output!=null&&Directory.Exists(output)) File.WriteAllText(Path.Combine(output,"multiscale-status.json"),JsonUtility.ToJson(state,true)); }
    void Fail(Exception e) { if(state.benchmarkActive) EndBenchmark("invalid","render-failed"); state.status="failed"; state.error=e.ToString(); if(present!=null) present.enabled=false; Write(); Debug.LogException(e); }
    void OnDestroy()
    {
        if(state.benchmarkActive) try { EndBenchmark("interrupted","owned-host-destroyed"); } catch(Exception e) { Debug.LogException(e); }
        if(debugDepth!=null) Destroy(debugDepth);
        stopping=true; if(listener!=null) listener.Stop(); Camera.onPreCull-=BeforeCamera; if(generation!=null) generation.Dispose(); if(debugBundle!=null) debugBundle.Unload(true); if(craftTarget!=null) craftTarget.Dispose(); if(bodyTarget!=null) bodyTarget.Dispose(); if(craftCamera!=null) Destroy(craftCamera.gameObject); if(bodyCamera!=null) Destroy(bodyCamera.gameObject); if(composite!=null) Destroy(composite); if(depthCopy!=null) Destroy(depthCopy);
        foreach(Camera c in disabledCameras) if(c!=null) c.enabled=true; if(settingsOwned) { QualitySettings.antiAliasing=priorAA; QualitySettings.vSyncCount=priorVsync; Application.targetFrameRate=priorFrameRate; Shader.SetGlobalVector("_LocalCameraPos",priorCameraPosition); Shader.SetGlobalVector("_LocalCameraDir",priorCameraDirection); } state.cleanupStatus="release-requested-owned-hooks-removed-settings-restored"; Write();
    }
}
