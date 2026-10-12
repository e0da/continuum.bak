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
        public PersistentTrailRuntime.StatusData trail;
        public bool trailInstalled;
        public int viewportWidthRequested,viewportHeightRequested;
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
        public string projection="perspective"; public float verticalSpanMeters; public double pixelsPerMeterAtFocus;
        public ScientificOverlay.StatusData scientificOverlay;
        public ScientificCameraControls.StatusData cameraControls;
        public float[] cameraRotation;
        public float bodyWorldScale,craftWorldScale,bodyMipBias,craftMipBias,bodyGeometryErrorPixels,craftGeometryErrorPixels;
        public string bodyPolicy,craftPolicy,bodyShadowPolicy,craftShadowPolicy;
        public bool reversedZ,uiNativeResolution=true,opaqueOnly=true,splashFinishedAtCapture,quitReplyWritten;
    }
    sealed class Generation : IDisposable
    {
        public MultiscaleSceneManifest Scene; public ResidentMaterialRuntime Craft; public WholeCraftRuntime Whole; public Mesh Body; public Texture2D Texture; public Material Material,DebugMaterial; public string Hash;
        public ScientificAnchorCatalog Anchors; public ScientificCameraControls.Anchor[] CameraAnchors;
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
    PersistentTrailRuntime trail;
    readonly ScientificOverlay scientificOverlay=new ScientificOverlay();
    readonly ScientificCameraControls cameraControls=new ScientificCameraControls();
    float? perspectiveFov;
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
        var next=Prepare(path); try { PrepareCameraAnchors(next);cameraControls.ValidateReplacement(next.CameraAnchors);if(generation!=null) generation.Craft.ValidateActivationTransferTo(next.Craft); next.Craft.Activate(craftCamera,false); if(generation!=null) generation.Craft.TransferActivationTo(next.Craft); }
        catch { next.Dispose(); throw; }
        var previous=generation; generation=next; policyDirty=true; if(previous!=null) previous.Dispose(); state.scenePath=path; state.sceneSha256=seen=next.Hash; state.vesselFixtureSha256=next.Scene.vesselFixtureSha256; state.bodyGeometrySha256=next.Scene.bodyGeometrySha256; state.bodyTextureSha256=next.Scene.bodyTexture.payloadSha256; state.bodyRecipe=next.Scene.bodyMaterialRecipe; state.bodyTextureCapture=next.Scene.bodyTextureCapture; state.partBundleSha256=next.Craft.BundleSha256; state.originalBundleSha256=next.Craft.OriginalBundleSha256; state.sceneReloads++;
        if(trail!=null) { trail.Dispose(); trail=null; state.trail=null; } state.trailInstalled=false;
        string preferred=next.Scene.initialCameraTarget==next.Scene.bodyId?next.Scene.bodyId:next.Anchors==null?next.Scene.vesselId:next.Anchors.preferredId;
        cameraControls.SetAnchors(next.CameraAnchors,preferred);
        if(initial) { state.focus=next.Scene.initialCameraTarget; var o=next.Scene.initialCameraOffsetMeters; distance=Math.Sqrt(o[0]*o[0]+o[1]*o[1]+o[2]*o[2]); yaw=(float)(Math.Atan2(-o[0],-o[2])*180/Math.PI); pitch=(float)(Math.Asin(o[1]/distance)*180/Math.PI);cameraControls.SetWorldUpOrbit(yaw,pitch); }
        SyncCameraSelection();
        SelectView(state.view);
        state.fixtureInstances=generation.Craft.InstanceCount; state.fixtureMeshes=generation.Craft.MeshCount; state.fixtureTextures=generation.Craft.Manifest.textures.Length; state.fixtureFamily=generation.Craft.Manifest.family;
        state.materialMode=generation.Whole==null?generation.Craft.Mode:generation.Whole.RenderMode+"-diagnostic";
        if(generation.Whole!=null) { var w=generation.Whole; state.craftConsumer="whole-craft-diagnostic"; state.wholeCraftSha256=w.ManifestSha256; state.wholeGeometrySha256=w.GeometrySha256; state.fixtureInstances=w.Manifest.draws.Length; state.fixtureMeshes=w.MeshCount; state.fixtureTextures=w.Manifest.textures.Length; state.fixtureFamily="multiple original families, approximate diagnostic"; state.fixtureScope="Whole captured triangle fixture; explicit omissions, not faithful whole craft"; state.wholeAppearance="two-sided opaque approximate MainTex/ST*tint*actual vertexRGB; no normal-map/specular/transparency parity"; state.wholeDependency="Mapped subset retained only for captured camera/light/global lease; no subset draws"; state.wholeRendererInventory=w.Manifest.rendererInventory; state.wholeCapturedRenderers=w.Manifest.capturedRenderers; state.wholeOmittedRenderers=w.Manifest.omittedRenderers; state.wholeDrawSlots=w.Manifest.drawSlots; state.wholeMissingNormals=w.MissingNormals; state.wholeMissingUV=w.MissingUV; state.wholeDefaultWhiteColors=w.DefaultWhiteColors; state.wholeUnknownCull=w.UnknownCull; state.wholeApproximateTransparency=w.ApproximateTransparency; state.wholeNoAlbedoTexture=w.NoAlbedoTexture; state.wholeNonvisibleDraws=w.NonvisibleDraws; state.wholeRawTexturePayloadBudgetBytes=WholeCraftRuntime.RawTexturePayloadBudgetBytes; state.wholeRawTexturePayloadBytes=w.RawTexturePayloadBytes; state.wholeForceRenderingOffObserved=w.ForceRenderingOffObserved; state.wholePropertyBlockObserved=w.PropertyBlockObserved; state.wholeFrozenSkinObserved=w.FrozenSkinObserved; state.debugInputs="Whole actual geometry/colors/poses; captured material properties and MainTex/ST; property blocks are observed but not reproduced"; state.wholeCoverage="Captured geometry draws only; omissions/nonvisible rows retained in immutable manifest"; } else { state.craftConsumer="mapped-subset"; state.fixtureScope="Captured material-family subset; not whole craft"; }
    }
    static ScientificVector3 Point(double[] p) {return new ScientificVector3(p[0],p[1],p[2]);}
    static Vector3 FloatVector(ScientificVector3 p) {return new Vector3((float)p.x,(float)p.y,(float)p.z);}
    void PrepareCameraAnchors(Generation next)
    {
        var s=next.Scene;var rows=new List<ScientificCameraControls.Anchor>();
        if(s.scientificAnchorsCaptured)
        {
            next.Anchors=ScientificAnchors.Admit(s.scientificAnchors,s.runId,s.processId,s.vesselId,s.bodyId,s.sourceFrame);
            for(int i=0;i<next.Anchors.Count;i++) {var a=next.Anchors.At(i);Quaternion q=Quaternion.identity;if(a.axesAvailable&&!ScientificCameraControls.TryBasis(FloatVector(a.right),FloatVector(a.up),FloatVector(a.forward),out q))throw new InvalidDataException("Captured scientific anchor axes cannot form a right-handed camera frame: "+a.id);rows.Add(new ScientificCameraControls.Anchor(a.kind,a.id,a.label,ScientificAnchors.Resolve(a,Point(s.vesselAnchorMeters)),q,a.axesAvailable));}
        }
        else
        {
            Quaternion basis;bool axes=ScientificCameraControls.TryBasis(next.Whole==null?next.Craft.Manifest.rootToWorld:next.Whole.Manifest.rootToWorld,out basis);
            rows.Add(new ScientificCameraControls.Anchor("vessel-origin",s.vesselId,"Captured vessel origin / capture-epoch vessel axes",Point(s.vesselAnchorMeters),basis,axes));
        }
        rows.Add(new ScientificCameraControls.Anchor("body",s.bodyId,"Captured body origin / world axes",Point(s.bodyAnchorMeters),Quaternion.identity));
        if(cameraControls.Initialized)
        {
            AddRetainedInstanceAnchor(next,rows,cameraControls.Focus);if(cameraControls.GridReference!=cameraControls.Focus)AddRetainedInstanceAnchor(next,rows,cameraControls.GridReference);
            if(cameraControls.AxisReference!=null&&cameraControls.AxisReference!=cameraControls.Focus&&cameraControls.AxisReference!=cameraControls.GridReference)AddRetainedInstanceAnchor(next,rows,cameraControls.AxisReference);
        }
        next.CameraAnchors=rows.ToArray();
    }
    void AddRetainedInstanceAnchor(Generation next,List<ScientificCameraControls.Anchor> rows,ScientificCameraControls.Anchor previous)
    {
        if(previous.kind!="instance")return;float radius;Vector3 local=next.Center(previous.id,out radius);double[] origin=next.Scene.vesselAnchorMeters;Quaternion basis;bool axes=ScientificCameraControls.TryBasis(next.Whole==null?next.Craft.Manifest.rootToWorld:next.Whole.Manifest.rootToWorld,out basis);
        rows.Add(new ScientificCameraControls.Anchor("instance",previous.id,"Captured draw instance "+previous.id+" / vessel axes",new ScientificVector3(origin[0]+local.x,origin[1]+local.y,origin[2]+local.z),basis,axes));
    }
    void UpdateCameraAnchors(double[] vesselOrigin)
    {
        var s=generation.Scene;
        if(generation.Anchors!=null)for(int i=0;i<generation.Anchors.Count;i++) {var a=generation.Anchors.At(i);Quaternion q=Quaternion.identity;if(a.axesAvailable&&!ScientificCameraControls.TryBasis(FloatVector(a.right),FloatVector(a.up),FloatVector(a.forward),out q))throw new InvalidDataException("Captured anchor frame changed.");cameraControls.UpdateAnchor(a.kind,a.id,ScientificAnchors.Resolve(a,Point(vesselOrigin)),q,a.axesAvailable);}
        else {var a=generation.CameraAnchors[0];cameraControls.UpdateAnchor(a.kind,a.id,Point(vesselOrigin),a.basis,a.axesAvailable);}
        if(cameraControls.Focus.kind=="instance")UpdateInstanceAnchor(cameraControls.Focus,vesselOrigin);
        if(cameraControls.GridReference.kind=="instance"&&cameraControls.GridReference!=cameraControls.Focus)UpdateInstanceAnchor(cameraControls.GridReference,vesselOrigin);
        if(cameraControls.AxisReference!=null&&cameraControls.AxisReference.kind=="instance"&&cameraControls.AxisReference!=cameraControls.Focus&&cameraControls.AxisReference!=cameraControls.GridReference)UpdateInstanceAnchor(cameraControls.AxisReference,vesselOrigin);
    }
    void UpdateInstanceAnchor(ScientificCameraControls.Anchor a,double[] origin)
    {float radius;Vector3 local=generation.Center(a.id,out radius);cameraControls.UpdateAnchor(a.kind,a.id,new ScientificVector3(origin[0]+local.x,origin[1]+local.y,origin[2]+local.z),a.basis,a.axesAvailable);}
    void SyncCameraSelection()
    {state.focus=cameraControls.Focus.kind=="body"?generation.Scene.bodyId:generation.Scene.vesselId;state.focusInstance=cameraControls.Focus.kind=="instance"?cameraControls.Focus.id:null;state.cameraControls=cameraControls.Status;state.cameraRotation=cameraControls.Status.rotation;}
    void SelectInstance(string id)
    {
        float radius;Vector3 local=generation.Center(id,out radius);var s=generation.Scene;Quaternion q;bool axes=ScientificCameraControls.TryBasis(generation.Whole==null?generation.Craft.Manifest.rootToWorld:generation.Whole.Manifest.rootToWorld,out q);
        cameraControls.Upsert(new ScientificCameraControls.Anchor("instance",id,"Captured draw instance "+id+" / vessel axes",new ScientificVector3(s.vesselAnchorMeters[0]+local.x,s.vesselAnchorMeters[1]+local.y,s.vesselAnchorMeters[2]+local.z),q,axes));
        cameraControls.Handle(new[] {"camera","focus","instance",id});distance=Math.Max(.25,radius*3);SyncCameraSelection();
    }
    void Update()
    {
        if(generation==null||state.status=="failed") return;
        if(state.benchmarkActive) TickBenchmark();
        Command cmd; while(queue.TryDequeue(out cmd)) { Interlocked.Decrement(ref queued); bool quitting=false; try { Execute(cmd.Text); cmd.Reply=JsonUtility.ToJson(state); quitting=cmd.Text=="quit"; } catch(Exception e) { cmd.Reply="{\"error\":\""+e.Message.Replace("\"","'")+"\"}"; } finally { if(trail!=null) trail.HoldClock(); } cmd.Done=true; if(quitting) StartCoroutine(QuitAfterReply(cmd)); }
        if(quitRequested) return;
        if(trail!=null) try { trail.Tick(capturing||state.benchmarkActive); } catch(Exception error) { trail.Pause(); state.error="Trail paused: "+error.Message; Write(); }
        if(replay!=null&&!capturing&&!state.benchmarkActive&&(trail==null||!trail.Status.probeInstalled)) try { replay.Tick(); state.replay=replay.Status; } catch(Exception error) { replay.Fault(error); state.replay=replay.Status; Write(); }
        if(replay==null&&!capturing&&!state.benchmarkActive&&(trail==null||!trail.Status.probeInstalled)&&Time.realtimeSinceStartup>=pollAt) { pollAt=Time.realtimeSinceStartup+1; try { if(Hash(state.scenePath)!=seen) Reload(state.scenePath,false); } catch(Exception e) { state.error="Rejected reload: "+e.Message; Write(); } finally { if(trail!=null) trail.HoldClock(); } }
        if(!capturing&&!state.benchmarkActive&&(trail==null||!trail.Status.probeInstalled)&&Input.mouseScrollDelta.y!=0) distance=Math.Max(.25,Math.Min(1e10,distance*Math.Exp(-Input.mouseScrollDelta.y*.15)));
    }
    void LateUpdate()
    {
        if(generation==null||state.status=="failed") return;
        try
        {
            var s=generation.Scene; double[] vesselAnchor=trail!=null&&!string.IsNullOrEmpty(trail.Status.recordingPath)?trail.Status.recordedRootScene:s.vesselAnchorMeters;
            UpdateCameraAnchors(vesselAnchor);SyncCameraSelection();var selected=cameraControls.Focus.position;focusAnchor[0]=selected.x;focusAnchor[1]=selected.y;focusAnchor[2]=selected.z;double[] anchor=focusAnchor;state.focusTargetMeters=anchor;
            bool bodyFocus=cameraControls.Focus.kind=="body";if(bodyFocus) distance=Math.Max(distance,s.bodyRadiusMeters*1.01);Quaternion rotation=cameraControls.Rotation;Vector3 offset=rotation*Vector3.back;
            for(int i=0;i<3;i++) cameraAnchor[i]=anchor[i]+offset[i]*distance;
            float x,y,z; MultiscaleSceneFormat.RelativePosition(vesselAnchor,cameraAnchor,out x,out y,out z); if(generation.Whole==null) generation.Craft.SetPresentationAnchor(new Vector3(x,y,z)); else generation.Whole.SetPresentationAnchor(new Vector3(x,y,z));
            present.transform.SetPositionAndRotation(Vector3.zero,rotation);
            craftCamera.transform.SetPositionAndRotation(Vector3.zero,rotation); bodyCamera.transform.SetPositionAndRotation(Vector3.zero,rotation);
            float fov=perspectiveFov??s.fov; bool ortho=state.projection=="ortho";
            present.orthographic=craftCamera.orthographic=bodyCamera.orthographic=ortho;
            present.fieldOfView=craftCamera.fieldOfView=bodyCamera.fieldOfView=fov;
            if(ortho)present.orthographicSize=craftCamera.orthographicSize=bodyCamera.orthographicSize=state.verticalSpanMeters*.5f;
            craftCamera.aspect=bodyCamera.aspect=present.aspect;
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
            present.nearClipPlane=Math.Min(craftCamera.nearClipPlane,bodyCamera.nearClipPlane); present.farClipPlane=Math.Max(craftCamera.farClipPlane,bodyCamera.farClipPlane);
            Shader.SetGlobalVector("_LocalCameraPos",new Vector4(0,0,0,priorCameraPosition.w)); Vector3 forward=rotation*Vector3.forward; Shader.SetGlobalVector("_LocalCameraDir",new Vector4(forward.x,forward.y,forward.z,priorCameraDirection.w));
            Graphics.DrawMesh(generation.Body,bodyMatrix,bodyGeometryDebug?generation.DebugMaterial:generation.Material,29,bodyCamera,0,null,ShadowCastingMode.On,false); bodyCamera.Render();
            if(generation.Whole!=null&&(generation.Whole.RenderMode=="retained-scalar"||generation.Whole.RenderMode=="retained-grouped")) generation.Whole.Submit(craftCamera); else craftCamera.Render();
            if(trail!=null&&state.view=="composed") trail.Render(craftCamera,cameraAnchor,craftTarget.Depth,bodyTarget.Depth,Screen.width,Screen.height);
            state.craftDrawCalls=generation.Whole==null?generation.Craft.DrawCalls:generation.Whole.DrawCalls; state.actualBodyMipCount=generation.Texture.mipmapCount; state.bodyMipPolicySatisfaction=state.actualBodyMipCount==1?"unsupported-single-level":"bias-applied-unqualified";
            if(generation.Whole!=null) { state.wholeNonvisibleDraws=generation.Whole.NonvisibleDraws; state.wholeRecipes=generation.Whole.RecipeCount; state.wholePackets=generation.Whole.PacketCount; state.wholeLastSubmissionCalls=generation.Whole.LastSubmissionCalls; state.wholePreparationTicks=generation.Whole.PreparationTicks; state.wholeSubmissionTicks=generation.Whole.SubmissionTicks; state.clockFrequency=System.Diagnostics.Stopwatch.Frequency; state.wholeStateUploadCount=generation.Whole.StateUploadCount; state.wholeStateTextureId=generation.Whole.StateTextureId; state.wholeStateUploadBytes=generation.Whole.StateUploadBytes; state.wholeCommandBuildCount=generation.Whole.CommandBuildCount; state.wholeNativeExecuteCount=generation.Whole.NativeExecuteCount; state.wholeViewUniformUpdateCount=generation.Whole.ViewUniformUpdateCount; state.wholeRetainedCommandCount=generation.Whole.RetainedCommandCount; state.wholeSubmissionScope=generation.Whole.RetainedScope; }
            composite.SetTexture("_BodyColor",bodyTarget.Color); composite.SetTexture("_BodyDepth",bodyTarget.Depth); composite.SetTexture("_CraftColor",craftTarget.Color); composite.SetTexture("_CraftDepth",craftTarget.Depth);
            state.frames++; state.bodyDraws++; state.craftFrames=generation.Whole==null?generation.Craft.SubmittedFrames:generation.Whole.SubmittedFrames; state.width=Screen.width; state.height=Screen.height; state.bodyWidth=bodyTarget.Color.width; state.bodyHeight=bodyTarget.Color.height; state.craftWidth=craftTarget.Color.width; state.craftHeight=craftTarget.Color.height; state.distance=distance; state.logDistance=Math.Log10(distance); state.yaw=yaw; state.pitch=pitch; state.fov=fov; state.cameraAnchorMeters=cameraAnchor; state.vesselGeneration=generation.Craft.Generation; state.bodyNear=bodyCamera.nearClipPlane; state.bodyFar=bodyCamera.farClipPlane; state.craftNear=craftCamera.nearClipPlane; state.craftFar=craftCamera.farClipPlane;
            state.fov=fov;
            var scientificView=CurrentScientificView();
            double focusDepth=(anchor[0]-cameraAnchor[0])*present.transform.forward.x+(anchor[1]-cameraAnchor[1])*present.transform.forward.y+(anchor[2]-cameraAnchor[2])*present.transform.forward.z;
            state.pixelsPerMeterAtFocus=scientificView.PixelsPerMeter(focusDepth);
            state.scientificOverlay=scientificOverlay.Status;
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
        else if(trail!=null) trail.Compose(source,destination,composite);
        else Graphics.Blit(source,destination,composite);
    }
    void Execute(string text)
    {
        string[] words=text.Split(new[] {' '},StringSplitOptions.RemoveEmptyEntries); if(words.Length==0) return; if(capturing) throw new InvalidOperationException("Capture controls frozen."); if(quitRequested) throw new InvalidOperationException("Owned shutdown pending."); if(state.benchmarkActive) { if(benchmarkCapturing) benchmark.commandsDuringCapture++; if(words[0]!="status"&&words[0]!="quit"&&words[0]!="cancelbenchmark") throw new InvalidOperationException("Benchmark freezes controls/captures/HMR."); } state.commands++;
        if(trail!=null&&trail.Status.probeInstalled&&words[0]!="status"&&words[0]!="quit"&&words[0]!="captureframe"&&!(words[0]=="viewport"&&trail.Status.probe.depthSource=="synthetic-texture2d")&&!(words[0]=="trail"&&((words.Length==3&&words[1]=="probe"&&words[2]=="off")||(words.Length==2&&words[1]=="remove")))) throw new InvalidOperationException("Depth/UV probe freezes scene, camera and history controls; turn probe off first.");
        if(words[0]=="quit"&&words.Length==1) { if(state.benchmarkActive) EndBenchmark("interrupted","operator-quit"); quitRequested=true; state.status="stopping"; Write(); return; }
        if(words[0]=="status") { if(!state.benchmarkActive) Write(); return; }
        if(words[0]=="benchmark"&&words.Length==3) { BeginBenchmark(Number(words[1]),Number(words[2])); return; }
        if(words[0]=="cancelbenchmark"&&words.Length==1) { if(!state.benchmarkActive) throw new InvalidDataException("No active benchmark."); EndBenchmark("interrupted","operator-cancelled"); return; }
        if(words[0]=="captureframe") { if(replay!=null) replay.Pause(); StartCoroutine(Capture()); return; }
        if(words[0]=="trail") { TrailCommand(words); return; }
        if(words[0]=="projection") { Projection(words); Write(); return; }
        if(words[0]=="camera") {if(words.Length>=3&&words[1]=="focus"&&words[2]=="instance"&&words.Length==4)SelectInstance(words[3]);else cameraControls.Handle(words);SyncCameraSelection();Write();return;}
        if(words[0]=="cad") { scientificOverlay.Handle(words); state.scientificOverlay=scientificOverlay.Status; Write(); return; }
        if(words[0]=="viewport"&&words.Length==3)
        {
            int width,height;
            if(!int.TryParse(words[1],NumberStyles.None,CultureInfo.InvariantCulture,out width)||!int.TryParse(words[2],NumberStyles.None,CultureInfo.InvariantCulture,out height)||width<320||width>4096||height<240||height>4096||(long)width*height>16000000) throw new InvalidDataException("Viewport integer320..4096 by240..4096, at most16000000 pixels required.");
            Screen.SetResolution(width,height,false); state.viewportWidthRequested=width; state.viewportHeightRequested=height; return;
        }
        if((words[0]=="replay.open"||words[0]=="replay.resume")&&trail!=null&&!string.IsNullOrEmpty(trail.Status.recordingPath))throw new InvalidOperationException("Remove measured emitter worldline before vessel pose replay.");
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
        else if(words[0]=="orbit"&&words.Length==3) { yaw=(float)Number(words[1]); pitch=Mathf.Clamp((float)Number(words[2]),-89,89);cameraControls.SetWorldUpOrbit(yaw,pitch); }
        else if(words[0]=="focusinstance"&&words.Length==2)SelectInstance(words[1]);
        else if(words[0]=="focus"&&words.Length==2&&(words[1]==generation.Scene.vesselId||words[1]==generation.Scene.bodyId)) {cameraControls.Handle(new[] {"camera","focus",words[1]==generation.Scene.bodyId?"body":"vessel-origin"});SyncCameraSelection();}
        else if(words[0]=="preset"&&words.Length==2) { PresentationQualityPolicy.Resolve(words[1],state.context,"craft","world",generation.Scene.vesselId,overrides); state.preset=words[1]; }
        else if(words[0]=="override"&&words.Length==5) { string id=words[1]; if(id!=generation.Scene.bodyId&&id!=generation.Scene.vesselId) throw new InvalidDataException("Known asset ID required."); var next=overrides.Where(o=>o.assetId!=id).Concat(new[] {new PresentationQualityOverride {scope="asset",assetId=id,pass="world",scaleSet=true,worldScale=(float)Number(words[2]),mipSet=true,textureMipBias=(float)Number(words[3]),geometrySet=true,geometryErrorPixels=(float)Number(words[4])}}).ToArray(); PresentationQualityPolicy.Resolve(state.preset,state.context,id==generation.Scene.bodyId?"body":"craft","world",id,next); overrides=next; }
        else if(words[0]=="fixturehotreload") Reload(Absolute(text.Substring(words[0].Length).Trim()),false);
        else if(words[0]=="snapshotreload") { if(generation.Whole!=null) throw new InvalidDataException("Whole geometry/poses are immutable; reload scene generation instead of subset snapshot."); string path=Absolute(text.Substring(words[0].Length).Trim()); var snap=ResidentMaterialFormat.ReadSnapshot(path); if(snap.instances.Any(i=>i.opacityEffective!=1)) throw new InvalidDataException("Translucent snapshot rejected."); generation.Craft.ReloadSnapshot(path); }
        else throw new InvalidDataException("status|quit|captureframe|trail fixture/remove/reset/pause/play [RATE]/step SECONDS/wind X Y Z/emission on/off|replay.open PATH|replay.seek INDEX|replay.play [RATE]|replay.pause|replay.checkpoint NEW_PATH|replay.resume PATH|materialmode reference/grouped/retained-scalar/retained-grouped (whole) or reference/instanced (subset)|benchmark WARMUP_SECONDS CAPTURE_SECONDS|cancelbenchmark|view composed or craft-/body- color/depth/flat/world-normals/view-normals/albedo/uv/material-id/linear-depth/log-depth (craft-vertex-colors only)|zoom log10metres|orbit yaw pitch|focus ID|focusinstance ID|preset low/balanced/high|override ID scale mip error|fixturehotreload PATH|snapshotreload PATH|debugshaderreload BUNDLE");
        policyDirty=true; Write();
    }
    void CheckBenchmarkSources()
    {
        if(Hash(state.scenePath)!=state.sceneSha256||Hash(Under(Path.GetDirectoryName(state.scenePath),generation.Scene.wholeCraft))!=state.wholeCraftSha256) throw new InvalidDataException("Bound benchmark manifest changed.");
        if(state.debugBundlePath!=null&&Hash(state.debugBundlePath)!=state.debugBundleSha256) throw new InvalidDataException("Bound diagnostic bundle changed.");
    }
    void RequireReplay() { if(replay==null) throw new InvalidOperationException("Open a bound replay first."); }
    void Projection(string[] words)
    {
        if(words.Length>=2&&words[1]=="perspective"&&(words.Length==2||words.Length==3))
        {
            double fov=words.Length==3?Number(words[2]):perspectiveFov??generation.Scene.fov;
            if(fov<1||fov>179)throw new InvalidDataException("Perspective FOV1..179 degrees required.");
            if(words.Length==3)perspectiveFov=(float)fov; state.projection="perspective"; state.fov=(float)fov; state.verticalSpanMeters=0;
        }
        else if(words.Length==3&&words[1]=="ortho")
        {
            double span=Number(words[2]); if(span<.001||span>1e12)throw new InvalidDataException("Orthographic full vertical span .001..1e12 metres required.");
            state.projection="ortho"; state.verticalSpanMeters=(float)span;
        }
        else throw new InvalidDataException("projection perspective [FOV_DEGREES] | projection ortho VERTICAL_SPAN_METRES");
    }
    ScientificView CurrentScientificView()
    {
        Vector3 right=present.transform.right,up=present.transform.up,forward=present.transform.forward;
        double far=Math.Max(present.farClipPlane,Math.Max(craftCamera.farClipPlane,bodyCamera.farClipPlane));
        return new ScientificView(new ScientificVector3(cameraAnchor[0],cameraAnchor[1],cameraAnchor[2]),new ScientificVector3(right.x,right.y,right.z),new ScientificVector3(up.x,up.y,up.z),new ScientificVector3(forward.x,forward.y,forward.z),Screen.width,Screen.height,present.orthographic?ScientificProjectionKind.Orthographic:ScientificProjectionKind.Perspective,present.fieldOfView,present.orthographicSize*2,.001,far);
    }
    void TrailCommand(string[] words)
    {
        if(words.Length==3&&words[1]=="recording")
        {
            if(replay!=null)throw new InvalidOperationException("Remove bound vessel replay before emitter worldline presentation.");
            string path=Path.GetFullPath(words[2]); if(!Path.IsPathRooted(words[2]))throw new InvalidDataException("Absolute emitter recording path required.");
            string digest=Hash(path);var recording=TrailEmitterFormat.Read(path);if(Hash(path)!=digest)throw new InvalidDataException("Emitter recording changed during read.");
            if(generation.Whole==null||recording.sceneSha256!=state.sceneSha256||recording.wholeCraftSha256!=generation.Whole.ManifestSha256||recording.vesselId!=generation.Scene.vesselId||recording.bodyName!=generation.Scene.bodyName||!generation.Whole.Manifest.draws.Any(d=>d.partId==recording.partId))throw new InvalidDataException("Emitter recording differs from resident cold scene/part.");
            PersistentTrailRuntime next=null;try{next=new PersistentTrailRuntime(generation.Scene.vesselAnchorMeters,recording,path,digest);}catch{if(next!=null)next.Dispose();throw;}
            var old=trail;trail=next;state.trail=next.Status;state.trailInstalled=true;if(old!=null)old.Dispose();return;
        }
        if(words.Length==2&&words[1]=="remove") { if(trail!=null) trail.Dispose(); trail=null; state.trail=null; state.trailInstalled=false; return; }
        if(words.Length==2&&words[1]=="fixture")
        {
            PersistentTrailRuntime next=null;
            try { next=new PersistentTrailRuntime(generation.Scene.vesselAnchorMeters); next.Fixture(); }
            catch { if(next!=null) next.Dispose(); throw; }
            var old=trail; trail=next; state.trail=next.Status; state.trailInstalled=true; if(old!=null) old.Dispose(); return;
        }
        if(trail==null) throw new InvalidOperationException("Create trail fixture first.");
        if(words.Length==3&&words[1]=="probe"&&words[2]=="scene") InstallSceneTrailProbe();
        else if(words.Length==3&&words[1]=="probe"&&(words[2]=="on"||words[2]=="off")) { if(words[2]=="on"&&(replay!=null||state.view!="composed")) throw new InvalidOperationException("Synthetic depth/UV probe requires composed static scene without replay."); trail.Probe(words[2]=="on"); }
        else if(words.Length==2&&words[1]=="reset") trail.Reset();
        else if(words.Length==2&&words[1]=="pause") trail.Pause();
        else if((words.Length==2||words.Length==3)&&words[1]=="play") trail.Play(words.Length==3?Number(words[2]):1);
        else if(words.Length==3&&words[1]=="step") trail.Step(Number(words[2]));
        else if(words.Length==3&&words[1]=="seek") trail.SeekRecording(Number(words[2]));
        else if(words.Length==5&&words[1]=="wind") trail.Wind(Number(words[2]),Number(words[3]),Number(words[4]));
        else if(words.Length==3&&words[1]=="emission"&&(words[2]=="on"||words[2]=="off")) trail.Emission(words[2]=="on");
        else if(words.Length==3&&words[1]=="visibility"&&(words[2]=="on"||words[2]=="off")) trail.SetVisible(words[2]=="on");
        else if(words.Length==3&&words[1]=="representation") trail.SetRepresentation(words[2]);
        else throw new InvalidDataException("trail fixture|remove|probe on/scene/off|reset|pause|play [RATE]|step SECONDS|wind X Y Z|emission on/off");
    }
    void InstallSceneTrailProbe()
    {
        if(replay!=null||!string.IsNullOrEmpty(trail.Status.recordingPath)||state.view!="composed"||generation.Whole==null||craftTarget==null||bodyTarget==null||state.frames==0) throw new InvalidOperationException("Actual probe requires resident static whole craft, composed view and completed scene render.");
        if(trail.Status.probeInstalled) throw new InvalidOperationException("Turn prior probe off first.");
        var s=generation.Scene; string manifestPath=Under(Path.GetDirectoryName(state.scenePath),s.wholeCraft),root=Path.GetDirectoryName(manifestPath);
        var manifest=generation.Whole.Manifest; string geometryPath=Under(root,manifest.geometry);
        if(Hash(geometryPath)!=generation.Whole.GeometrySha256) throw new InvalidDataException("Scene probe geometry changed.");
        var geometry=WholeCraftSceneFormat.ReadGeometry(geometryPath); WholeCraftSceneFormat.Validate(manifest,geometry);
        if(Hash(geometryPath)!=generation.Whole.GeometrySha256) throw new InvalidDataException("Scene probe geometry changed during read.");
        Matrix4x4 rootMatrix=new Matrix4x4(); for(int r=0;r<4;r++) for(int c=0;c<4;c++) rootMatrix[r,c]=manifest.rootToWorld[r*4+c]; rootMatrix.m03=rootMatrix.m13=rootMatrix.m23=0;
        float x,y,z; MultiscaleSceneFormat.RelativePosition(s.vesselAnchorMeters,cameraAnchor,out x,out y,out z); Vector3 relative=new Vector3(x,y,z);
        PersistentTrailRuntime.SceneTriangle craft=null,body=null; int triangles=0;
        float minCraftX=float.PositiveInfinity,minCraftY=float.PositiveInfinity,maxCraftX=float.NegativeInfinity,maxCraftY=float.NegativeInfinity;
        for(int d=0;d<manifest.draws.Length;d++)
        {
            var draw=manifest.draws[d]; if(!draw.enabled||!draw.active||draw.forceRenderingOff||(craftCamera.cullingMask&(1<<draw.layer))==0) continue;
            Matrix4x4 m=rootMatrix*Matrix(draw.matrix); m.m03+=relative.x; m.m13+=relative.y; m.m23+=relative.z;
            var mesh=geometry.meshes[draw.meshIndex]; var indices=mesh.submeshes[draw.submeshIndex].indices; var p=mesh.positions;
            for(int i=0;i<indices.Length;i+=3)
            {
                if(++triangles>2000000) throw new InvalidDataException("Scene probe triangle projection bound exceeded.");
                int a=indices[i]*3,b=indices[i+1]*3,c=indices[i+2]*3;
                Vector3 va=m.MultiplyPoint3x4(new Vector3(p[a],p[a+1],p[a+2])),vb=m.MultiplyPoint3x4(new Vector3(p[b],p[b+1],p[b+2])),vc=m.MultiplyPoint3x4(new Vector3(p[c],p[c+1],p[c+2]));
                ExpandCraftProbeBounds(craftCamera.WorldToViewportPoint(va),ref minCraftX,ref minCraftY,ref maxCraftX,ref maxCraftY);
                ExpandCraftProbeBounds(craftCamera.WorldToViewportPoint(vb),ref minCraftX,ref minCraftY,ref maxCraftX,ref maxCraftY);
                ExpandCraftProbeBounds(craftCamera.WorldToViewportPoint(vc),ref minCraftX,ref minCraftY,ref maxCraftX,ref maxCraftY);
                craft=PersistentTrailRuntime.SelectTriangle(craft,"craft",draw.id,va,vb,vc,i/3,craftCamera,Screen.width,Screen.height,null);
            }
        }
        for(int i=0;i<3;i++) bodyGeometryAnchor[i]=s.bodyAnchorMeters[i]+s.bodyLocalToMeters[i*4+3];
        MultiscaleSceneFormat.RelativePosition(bodyGeometryAnchor,cameraAnchor,out x,out y,out z); Matrix4x4 bm=Matrix(s.bodyLocalToMeters); bm.m03=x; bm.m13=y; bm.m23=z;
        var vertices=generation.Body.vertices; var bodyIndices=generation.Body.triangles;
        for(int i=0;i<bodyIndices.Length;i+=3)
        {
            if(++triangles>2000000) throw new InvalidDataException("Scene probe triangle projection bound exceeded.");
            Vector3 a=bm.MultiplyPoint3x4(vertices[bodyIndices[i]]),b=bm.MultiplyPoint3x4(vertices[bodyIndices[i+1]]),c=bm.MultiplyPoint3x4(vertices[bodyIndices[i+2]]);
            if(Vector3.Dot(Vector3.Cross(b-a,c-a),a)*Mathf.Sign(bm.determinant)>=0) continue;
            var candidate=PersistentTrailRuntime.SelectTriangle(null,"body",s.bodyId,a,b,c,i/3,bodyCamera,Screen.width,Screen.height,craft);
            if(candidate==null) continue;
            float px=candidate.centerU*Screen.width,py=candidate.centerV*Screen.height;
            if(px>=minCraftX-20&&px<=maxCraftX+20&&py>=minCraftY-20&&py<=maxCraftY+20) continue;
            if(body==null||candidate.inradiusPixels>body.inradiusPixels) body=candidate;
        }
        trail.SceneProbe(craft,body,craftCamera,bodyCamera.nearClipPlane,Screen.width,Screen.height,state.sceneSha256,generation.Whole.GeometrySha256,state.bodyGeometrySha256);
        var proof=trail.Status.probe; proof.craftProducerProjection=new float[16]; proof.bodyProducerProjection=new float[16];
        Matrix4x4 cp=GL.GetGPUProjectionMatrix(craftCamera.projectionMatrix,true),bp=GL.GetGPUProjectionMatrix(bodyCamera.projectionMatrix,true);
        for(int r=0;r<4;r++) for(int c=0;c<4;c++) { proof.craftProducerProjection[r*4+c]=cp[r,c]; proof.bodyProducerProjection[r*4+c]=bp[r,c]; }
        proof.producerNearFar=new[]{craftCamera.nearClipPlane,craftCamera.farClipPlane,bodyCamera.nearClipPlane,bodyCamera.farClipPlane}; proof.producerDimensions=new[]{craftTarget.Depth.width,craftTarget.Depth.height,bodyTarget.Depth.width,bodyTarget.Depth.height};
        proof.sceneGeneration=state.sceneReloads; proof.debugShaderGeneration=state.debugShaderGeneration; proof.craftRenderMode=generation.Whole.RenderMode;
        proof.craftProjectedAabbPixels=new[]{minCraftX,minCraftY,maxCraftX,maxCraftY}; proof.bodyCraftExclusionMarginPixels=20;
    }
    void ExpandCraftProbeBounds(Vector3 projected,ref float minX,ref float minY,ref float maxX,ref float maxY)
    {
        // Near-plane crossing requires clipping to bound it; this diagnostic declines it conservatively.
        if(projected.z<=craftCamera.nearClipPlane||float.IsNaN(projected.x)||float.IsNaN(projected.y)) { minX=minY=float.NegativeInfinity; maxX=maxY=float.PositiveInfinity; return; }
        minX=Mathf.Min(minX,projected.x*Screen.width); minY=Mathf.Min(minY,projected.y*Screen.height);
        maxX=Mathf.Max(maxX,projected.x*Screen.width); maxY=Mathf.Max(maxY,projected.y*Screen.height);
    }
    void BeginBenchmark(double warmup,double seconds)
    {
        if(trail!=null) throw new InvalidOperationException("Remove artistic trail before existing static-render benchmark; trail costs are outside its frozen workload.");
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
        if (trail != null && trail.Status.probeInstalled) return;
        if(present!=null&&generation!=null&&state.status!="failed")
        {
            var focus=state.focusTargetMeters;
            if(focus!=null)scientificOverlay.SetFocus(new ScientificVector3(focus[0],focus[1],focus[2]));
            var grid=cameraControls.GridReference;Quaternion gridBasis=cameraControls.GridBasis;scientificOverlay.SetReference(grid.position,VectorPoint(gridBasis*Vector3.right),VectorPoint(gridBasis*Vector3.up),VectorPoint(gridBasis*Vector3.forward),cameraControls.GridLabel);
            scientificOverlay.Draw(CurrentScientificView()); state.scientificOverlay=scientificOverlay.Status;
        }
        bool controlsAllowed=!capturing&&!state.benchmarkActive&&!quitRequested&&(trail==null||!trail.Status.probeInstalled);
        if(cameraControls.Initialized)cameraControls.Draw(new Rect(450,230,340,354),controlsAllowed,true,state.projection=="ortho",state.fov,state.verticalSpanMeters>0?state.verticalSpanMeters:(float)Math.Max(.001,distance*.8),CameraUI);
        GUI.Box(new Rect(10,10,430,150),generation!=null&&generation.Whole!=null?"Whole craft snapshot / "+state.wholeRenderMode:"Captured material-family subset"); GUI.Label(new Rect(20,35,410,20),state.preset+" / "+state.distance.ToString("G5")+" m / "+state.view);
        float currentLog=(float)Math.Log10(Math.Max(.25,distance));float log=GUI.HorizontalSlider(new Rect(20,65,400,20),currentLog,-.6f,10);if(controlsAllowed&&log!=currentLog)distance=Math.Pow(10,log);
        if(GUI.Button(new Rect(20,90,100,22),"low")&&controlsAllowed) { state.preset="low"; policyDirty=true; } if(GUI.Button(new Rect(130,90,100,22),"balanced")&&controlsAllowed) { state.preset="balanced"; policyDirty=true; } if(GUI.Button(new Rect(240,90,100,22),"high")&&controlsAllowed) { state.preset="high"; policyDirty=true; }
        GUI.Label(new Rect(20,120,410,30),generation!=null&&generation.Whole!=null?state.wholeCapturedRenderers+" captured / "+state.wholeOmittedRenderers+" omitted / "+state.fixtureInstances+" slots":state.fixtureInstances+" rows / "+state.fixtureMeshes+" meshes / "+state.materialMode);
        GUI.Label(new Rect(450,10,Screen.width-460,70),state.debugLegend);
        if(replay!=null) GUI.Label(new Rect(450,80,Screen.width-460,60),"Replay "+replay.Status.status+" / "+replay.Status.cursor+" of "+replay.Status.frames+" / UT "+replay.Status.universalTime.ToString("G10")+" / force "+replay.Status.forceSummary+" / moment "+replay.Status.momentSummary);
        if(trail!=null) GUI.Label(new Rect(450,145,Screen.width-460,50),"Artistic trail / t="+trail.Status.simulationTime.ToString("F2")+" / live "+trail.Status.live+" / expired "+trail.Status.expired+" / "+(trail.Status.paused?"paused":"playing")+" / emission "+trail.Status.emission);
        GUI.Label(new Rect(450,200,Screen.width-460,22),state.projection+(state.projection=="ortho"?" / vertical "+state.verticalSpanMeters.ToString("G6")+" m":" / FOV "+state.fov.ToString("G5")+" degrees")+" / "+state.pixelsPerMeterAtFocus.ToString("G5")+" px/m at focus");
        for(int i=0;i<debugViews.Length;i++) if(GUI.Button(new Rect(10+(i%2)*215,170+(i/2)*23,210,22),debugViews[i])&&controlsAllowed) { SelectView(debugViews[i]); Write(); }
    }
    static ScientificVector3 VectorPoint(Vector3 v) {return new ScientificVector3(v.x,v.y,v.z);}
    void CameraUI(string[] words) {try {Execute(string.Join(" ",words));}catch(Exception e) {cameraControls.Status.lastError=e.Message;}}
    void SocketLoop()
    {
        while(!stopping) try { using(var client=listener.AcceptTcpClient()) using(var stream=client.GetStream()) { stream.ReadTimeout=2000; stream.WriteTimeout=2000; var bytes=new List<byte>(); int b; while((b=stream.ReadByte())>=0&&b!='\n') { if(bytes.Count>=4096) throw new InvalidDataException("Command bound."); bytes.Add((byte)b); } if(Interlocked.Increment(ref queued)>16) { Interlocked.Decrement(ref queued); continue; } var c=new Command {Text=Encoding.UTF8.GetString(bytes.ToArray()).Trim()}; queue.Enqueue(c); for(int i=0;i<500&&!c.Done&&!stopping;i++) Thread.Sleep(10); if(c.Done) { byte[] reply=Encoding.UTF8.GetBytes(c.Reply+"\n"); stream.Write(reply,0,reply.Length); stream.Flush(); c.ReplyWritten=true; } } } catch { if(stopping) return; }
    }
    void Write() { if(output!=null&&Directory.Exists(output)) File.WriteAllText(Path.Combine(output,"multiscale-status.json"),JsonUtility.ToJson(state,true)); }
    void Fail(Exception e) { if(state.benchmarkActive) EndBenchmark("invalid","render-failed"); state.status="failed"; state.error=e.ToString(); if(present!=null) present.enabled=false; Write(); Debug.LogException(e); }
    void OnDestroy()
    {
        state.trailInstalled=false;
        scientificOverlay.Dispose();
        if(trail!=null) trail.Dispose();
        if(state.benchmarkActive) try { EndBenchmark("interrupted","owned-host-destroyed"); } catch(Exception e) { Debug.LogException(e); }
        if(debugDepth!=null) Destroy(debugDepth);
        stopping=true; if(listener!=null) listener.Stop(); Camera.onPreCull-=BeforeCamera; if(generation!=null) generation.Dispose(); if(debugBundle!=null) debugBundle.Unload(true); if(craftTarget!=null) craftTarget.Dispose(); if(bodyTarget!=null) bodyTarget.Dispose(); if(craftCamera!=null) Destroy(craftCamera.gameObject); if(bodyCamera!=null) Destroy(bodyCamera.gameObject); if(composite!=null) Destroy(composite); if(depthCopy!=null) Destroy(depthCopy);
        foreach(Camera c in disabledCameras) if(c!=null) c.enabled=true; if(settingsOwned) { QualitySettings.antiAliasing=priorAA; QualitySettings.vSyncCount=priorVsync; Application.targetFrameRate=priorFrameRate; Shader.SetGlobalVector("_LocalCameraPos",priorCameraPosition); Shader.SetGlobalVector("_LocalCameraDir",priorCameraDirection); } state.cleanupStatus="release-requested-owned-hooks-removed-settings-restored"; Write();
    }
}
