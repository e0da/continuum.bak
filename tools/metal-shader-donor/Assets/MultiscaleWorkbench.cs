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
        public string schema="continuum-multiscale-workbench/v1",status="starting",error,hostId,unity,backend,assemblySha256,scenePath,sceneSha256,vesselFixtureSha256,bodyGeometrySha256,bodyTextureSha256,bodyRecipe,preset="balanced",context="flight",focus,crop="rejected-unqualified-selective-coverage",geometryFallback="Only captured original meshes exist; no geometric LOD hierarchy",shadowFallback="Shadow requests unsatisfied; directional shadow maps disabled for first two-domain composition",lastPng,lastPngSha256,cleanupStatus;
        public string partBundleSha256,originalBundleSha256,bodyTextureCapture,materialMode="instanced",view="composed";
        public string debugShader="Continuum/MultiscaleDiagnostic",debugBundleSha256,debugLegend="Diagnostic only; no material fidelity or performance qualification",debugInputs="Actual captured geometry, camera-relative matrices, explicit white craft vertex COLOR, captured MainTex and ST; material IDs ordered by captured material index",fixtureScope="Captured material-family subset; not whole craft",fixtureFamily;
        public int fixtureInstances,fixtureMeshes,fixtureTextures;
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
        public MultiscaleSceneManifest Scene; public ResidentMaterialRuntime Craft; public Mesh Body; public Texture2D Texture; public Material Material,DebugMaterial; public string Hash;
        public void Dispose() { if(Craft!=null) Craft.Dispose(); if(Body!=null) UnityEngine.Object.Destroy(Body); if(Texture!=null) UnityEngine.Object.Destroy(Texture); if(Material!=null) UnityEngine.Object.Destroy(Material); if(DebugMaterial!=null) UnityEngine.Object.Destroy(DebugMaterial); }
    }
    sealed class Command { public string Text,Reply; public volatile bool Done,ReplyWritten; }
    sealed class Target : IDisposable
    {
        public Camera Camera; public RenderTexture Color,Depth; public CommandBuffer Copy;
        public void Dispose() { if(Camera!=null) { Camera.targetTexture=null; if(Copy!=null) Camera.RemoveCommandBuffer(CameraEvent.AfterEverything,Copy); } if(Copy!=null) Copy.Release(); if(Color!=null) { Color.Release(); UnityEngine.Object.Destroy(Color); } if(Depth!=null) { Depth.Release(); UnityEngine.Object.Destroy(Depth); } }
    }
    readonly State state=new State(); readonly ConcurrentQueue<Command> queue=new ConcurrentQueue<Command>();
    PresentationQualityOverride[] overrides=new PresentationQualityOverride[0]; Generation generation; Camera present,craftCamera,bodyCamera; Target craftTarget,bodyTarget; Material composite,depthCopy;
    EffectivePresentationQuality craftQuality,bodyQuality; bool policyDirty=true;
    string output,partBundle,originalBundle,seen; double distance; float yaw,pitch; double pollAt; bool capturing,settingsOwned,quitRequested; TcpListener listener; Thread socket; volatile bool stopping; int queued;
    readonly double[] cameraAnchor=new double[3]; readonly List<Camera> disabledCameras=new List<Camera>(); int priorAA,priorVsync,priorFrameRate;
    readonly double[] bodyGeometryAnchor=new double[3];
    readonly double[] focusAnchor=new double[3];
    Vector4 priorCameraPosition,priorCameraDirection;
    Material debugDepth; Shader debugShader; AssetBundle debugBundle; bool bodyGeometryDebug;
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
            if(state.unity!="2019.4.18f1"||!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)) throw new InvalidDataException("Pinned player and linear-depth targets required.");
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
            var scene=next.Scene; if(scene.engine!=Application.unityVersion||scene.colorSpace!=QualitySettings.activeColorSpace.ToString()) throw new InvalidDataException("Scene engine/color-space mismatch."); string root=Path.GetDirectoryName(path);
            double initialNorm=scene.initialCameraOffsetMeters.Sum(v=>v*v); if(initialNorm<.0625||double.IsInfinity(initialNorm)) throw new InvalidDataException("Initial camera offset bound.");
            if(generation!=null&&(generation.Scene.vesselId!=scene.vesselId||generation.Scene.bodyId!=scene.bodyId)) throw new InvalidDataException("Scene HMR retains logical object identities.");
            string vessel=Under(root,scene.vesselFixture),geometry=Under(root,scene.bodyGeometry),texture=Under(root,scene.bodyTexture.payload);
            if(Hash(vessel)!=scene.vesselFixtureSha256||Hash(geometry)!=scene.bodyGeometrySha256) throw new InvalidDataException("Scene asset hash mismatch.");
            next.Craft=new ResidentMaterialRuntime(vessel,partBundle,string.IsNullOrEmpty(originalBundle)?null:Absolute(originalBundle),true); next.Craft.SetMode(state.materialMode); next.Craft.IncludePresentationLightLayer(29); next.Craft.DisablePresentationShadows();
            if((next.Craft.LayerMask&(1<<29))!=0) throw new InvalidDataException("Craft layer overlaps owned body domain.");
            var snapshot=ResidentMaterialFormat.ReadSnapshot(next.Craft.StatePath); if(snapshot.instances.Any(i=>i.opacityEffective!=1)) throw new InvalidDataException("Opaque-only composition rejects translucent craft state.");
            ResidentGeometry body=ResidentMaterialFormat.ReadGeometry(geometry); if(body.meshes.Length!=1) throw new InvalidDataException("One captured scaled body mesh required."); var g=body.meshes[0]; int count=g.positions.Length/3; var p=new Vector3[count]; var n=new Vector3[count]; var uv=new Vector2[count];
            for(int i=0;i<count;i++) { p[i]=new Vector3(g.positions[i*3],g.positions[i*3+1],g.positions[i*3+2]); n[i]=new Vector3(g.normals[i*3],g.normals[i*3+1],g.normals[i*3+2]); uv[i]=new Vector2(g.uv[i*2],g.uv[i*2+1]); }
            next.Body=new Mesh {indexFormat=IndexFormat.UInt32,vertices=p,normals=n,uv=uv,triangles=g.indices}; next.Body.RecalculateBounds();
            var t=scene.bodyTexture; byte[] bytes=File.ReadAllBytes(texture); if(bytes.LongLength!=t.payloadLength||BytesHash(bytes)!=t.payloadSha256) throw new InvalidDataException("Body raw texture mismatch."); next.Texture=new Texture2D(t.width,t.height,(TextureFormat)t.format,t.mipCount>1,t.linear); next.Texture.LoadRawTextureData(bytes); next.Texture.Apply(false,true); next.Texture.filterMode=(FilterMode)t.filter; next.Texture.wrapMode=(TextureWrapMode)t.wrap; next.Texture.anisoLevel=t.aniso;
            var formatProperty=typeof(Texture).GetProperty("graphicsFormat"); if(next.Texture.mipmapCount!=t.mipCount||(int)next.Texture.format!=t.format||formatProperty==null||Convert.ToInt32(formatProperty.GetValue(next.Texture,null))!=t.graphicsFormat) throw new InvalidDataException("Body native texture format mismatch.");
            next.Material=NewMaterial("Continuum/MultiscaleBodyDiffuse"); next.Material.SetTexture("_MainTex",next.Texture); next.Material.color=new Color(scene.bodyColor[0],scene.bodyColor[1],scene.bodyColor[2],scene.bodyColor[3]); var st=scene.bodyTextureScaleOffset; next.Material.SetTextureScale("_MainTex",new Vector2(st[0],st[1])); next.Material.SetTextureOffset("_MainTex",new Vector2(st[2],st[3]));
            next.Craft.CommitDebugShader(next.Craft.PrepareDebugShader(debugShader)); next.DebugMaterial=PrepareBodyDebug(next,debugShader);
            if(state.focusInstance!=null) { float radius; next.Craft.PresentationInstanceCenter(state.focusInstance,out radius); }
            if(Hash(path)!=next.Hash||Hash(vessel)!=scene.vesselFixtureSha256||Hash(geometry)!=scene.bodyGeometrySha256) throw new InvalidDataException("Scene changed during preparation."); return next;
        }
        catch { next.Dispose(); throw; }
    }
    void Reload(string path,bool initial)
    {
        var next=Prepare(path); try { if(generation!=null) generation.Craft.ValidateActivationTransferTo(next.Craft); next.Craft.Activate(craftCamera,false); if(generation!=null) generation.Craft.TransferActivationTo(next.Craft); }
        catch { next.Dispose(); throw; }
        var previous=generation; generation=next; policyDirty=true; if(previous!=null) previous.Dispose(); state.scenePath=path; state.sceneSha256=seen=next.Hash; state.vesselFixtureSha256=next.Scene.vesselFixtureSha256; state.bodyGeometrySha256=next.Scene.bodyGeometrySha256; state.bodyTextureSha256=next.Scene.bodyTexture.payloadSha256; state.bodyRecipe=next.Scene.bodyMaterialRecipe; state.bodyTextureCapture=next.Scene.bodyTextureCapture; state.partBundleSha256=next.Craft.BundleSha256; state.originalBundleSha256=next.Craft.OriginalBundleSha256; state.sceneReloads++;
        if(initial) { state.focus=next.Scene.initialCameraTarget; var o=next.Scene.initialCameraOffsetMeters; distance=Math.Sqrt(o[0]*o[0]+o[1]*o[1]+o[2]*o[2]); yaw=(float)(Math.Atan2(-o[0],-o[2])*180/Math.PI); pitch=(float)(Math.Asin(o[1]/distance)*180/Math.PI); }
        SelectView(state.view);
        state.fixtureInstances=generation.Craft.InstanceCount; state.fixtureMeshes=generation.Craft.MeshCount; state.fixtureTextures=generation.Craft.Manifest.textures.Length; state.fixtureFamily=generation.Craft.Manifest.family;
    }
    void Update()
    {
        if(generation==null||state.status=="failed") return;
        Command cmd; while(queue.TryDequeue(out cmd)) { Interlocked.Decrement(ref queued); bool quitting=false; try { Execute(cmd.Text); cmd.Reply=JsonUtility.ToJson(state); quitting=cmd.Text=="quit"; } catch(Exception e) { cmd.Reply="{\"error\":\""+e.Message.Replace("\"","'")+"\"}"; } cmd.Done=true; if(quitting) StartCoroutine(QuitAfterReply(cmd)); }
        if(quitRequested) return;
        if(!capturing&&Time.realtimeSinceStartup>=pollAt) { pollAt=Time.realtimeSinceStartup+1; try { if(Hash(state.scenePath)!=seen) Reload(state.scenePath,false); } catch(Exception e) { state.error="Rejected reload: "+e.Message; Write(); } }
        if(!capturing&&Input.mouseScrollDelta.y!=0) distance=Math.Max(.25,Math.Min(1e10,distance*Math.Exp(-Input.mouseScrollDelta.y*.15)));
    }
    void LateUpdate()
    {
        if(generation==null||state.status=="failed") return;
        try
        {
            var s=generation.Scene; bool bodyFocus=state.focus==s.bodyId; double[] anchor=bodyFocus?s.bodyAnchorMeters:s.vesselAnchorMeters;
            if(state.focusInstance!=null) { float radius; Vector3 local=generation.Craft.PresentationInstanceCenter(state.focusInstance,out radius); for(int i=0;i<3;i++) focusAnchor[i]=s.vesselAnchorMeters[i]+local[i]; anchor=focusAnchor; } state.focusTargetMeters=anchor;
            if(bodyFocus) distance=Math.Max(distance,s.bodyRadiusMeters*1.01); Vector3 offset=Quaternion.Euler(pitch,yaw,0)*Vector3.back;
            for(int i=0;i<3;i++) cameraAnchor[i]=anchor[i]+offset[i]*distance;
            float x,y,z; MultiscaleSceneFormat.RelativePosition(s.vesselAnchorMeters,cameraAnchor,out x,out y,out z); generation.Craft.SetPresentationAnchor(new Vector3(x,y,z));
            Quaternion rotation=Quaternion.LookRotation(-offset,Vector3.up); present.transform.SetPositionAndRotation(Vector3.zero,rotation); present.fieldOfView=s.fov;
            craftCamera.transform.SetPositionAndRotation(Vector3.zero,rotation); bodyCamera.transform.SetPositionAndRotation(Vector3.zero,rotation); craftCamera.fieldOfView=bodyCamera.fieldOfView=s.fov; craftCamera.aspect=bodyCamera.aspect=present.aspect;
            if(policyDirty) { craftQuality=PresentationQualityPolicy.Resolve(state.preset,state.context,"craft","world",s.vesselId,overrides); bodyQuality=PresentationQualityPolicy.Resolve(state.preset,state.context,"body","world",s.bodyId,overrides); state.craftShadowPolicy=PresentationQualityPolicy.Resolve(state.preset,state.context,"craft","shadow",s.vesselId,overrides).provenance; state.bodyShadowPolicy=PresentationQualityPolicy.Resolve(state.preset,state.context,"body","shadow",s.bodyId,overrides).provenance; policyDirty=false; }
            var cq=craftQuality; var bq=bodyQuality; state.craftPolicy=cq.provenance; state.bodyPolicy=bq.provenance;
            state.craftWorldScale=cq.worldScale; state.bodyWorldScale=bq.worldScale; state.craftMipBias=cq.textureMipBias; state.bodyMipBias=bq.textureMipBias; state.craftGeometryErrorPixels=cq.geometryErrorPixels; state.bodyGeometryErrorPixels=bq.geometryErrorPixels;
            generation.Craft.SetTextureMipBias(cq.textureMipBias); generation.Texture.mipMapBias=s.bodyTexture.mipBias+bq.textureMipBias;
            Resize(ref craftTarget,craftCamera,cq.worldScale); Resize(ref bodyTarget,bodyCamera,bq.worldScale); craftCamera.cullingMask=generation.Craft.LayerMask; bodyCamera.cullingMask=1<<29;
            Vector3 center=generation.Craft.Bounds.center; float radius=Math.Max(.01f,generation.Craft.Bounds.extents.magnitude); double d=center.magnitude; craftCamera.nearClipPlane=(float)Math.Max(.001,(d-radius)*.5); craftCamera.farClipPlane=(float)Math.Max(craftCamera.nearClipPlane*2,d+radius*2);
            for(int i=0;i<3;i++) bodyGeometryAnchor[i]=s.bodyAnchorMeters[i]+s.bodyLocalToMeters[i*4+3];
            MultiscaleSceneFormat.RelativePosition(bodyGeometryAnchor,cameraAnchor,out x,out y,out z); Matrix4x4 bodyMatrix=Matrix(s.bodyLocalToMeters); bodyMatrix.m03=x; bodyMatrix.m13=y; bodyMatrix.m23=z;
            double bd=Math.Sqrt((double)x*x+(double)y*y+(double)z*z); bodyCamera.nearClipPlane=(float)Math.Max(1,(bd-s.bodyRadiusMeters)*.5); bodyCamera.farClipPlane=(float)Math.Max(bodyCamera.nearClipPlane*2,bd+s.bodyRadiusMeters*2);
            if(bodyCamera.nearClipPlane<=0||craftCamera.nearClipPlane<=0) throw new InvalidDataException("Invalid depth domain.");
            Shader.SetGlobalVector("_LocalCameraPos",new Vector4(0,0,0,priorCameraPosition.w)); Vector3 forward=rotation*Vector3.forward; Shader.SetGlobalVector("_LocalCameraDir",new Vector4(forward.x,forward.y,forward.z,priorCameraDirection.w));
            Graphics.DrawMesh(generation.Body,bodyMatrix,bodyGeometryDebug?generation.DebugMaterial:generation.Material,29,bodyCamera,0,null,ShadowCastingMode.On,false); bodyCamera.Render(); craftCamera.Render();
            state.craftDrawCalls=generation.Craft.DrawCalls; state.actualBodyMipCount=generation.Texture.mipmapCount; state.bodyMipPolicySatisfaction=state.actualBodyMipCount==1?"unsupported-single-level":"bias-applied-unqualified";
            composite.SetTexture("_BodyColor",bodyTarget.Color); composite.SetTexture("_BodyDepth",bodyTarget.Depth); composite.SetTexture("_CraftColor",craftTarget.Color); composite.SetTexture("_CraftDepth",craftTarget.Depth);
            state.frames++; state.bodyDraws++; state.craftFrames=generation.Craft.SubmittedFrames; state.width=Screen.width; state.height=Screen.height; state.bodyWidth=bodyTarget.Color.width; state.bodyHeight=bodyTarget.Color.height; state.craftWidth=craftTarget.Color.width; state.craftHeight=craftTarget.Color.height; state.distance=distance; state.logDistance=Math.Log10(distance); state.yaw=yaw; state.pitch=pitch; state.fov=s.fov; state.cameraAnchorMeters=cameraAnchor; state.vesselGeneration=generation.Craft.Generation; state.bodyNear=bodyCamera.nearClipPlane; state.bodyFar=bodyCamera.farClipPlane; state.craftNear=craftCamera.nearClipPlane; state.craftFar=craftCamera.farClipPlane;
            if(state.frames%30==0) Write();
        }
        catch(Exception e) { Fail(e); }
    }
    void BeforeCamera(Camera camera) { if(generation!=null&&camera==craftCamera) generation.Craft.Submit(camera); }
    void Resize(ref Target target,Camera camera,float scale)
    {
        int w=Math.Max(1,(int)Math.Ceiling(Screen.width*scale)),h=Math.Max(1,(int)Math.Ceiling(Screen.height*scale)); if(target!=null&&target.Color.width==w&&target.Color.height==h) return;
        if(target!=null) target.Dispose(); target=new Target {Camera=camera,Color=new RenderTexture(w,h,24,RenderTextureFormat.ARGB32) {antiAliasing=1,filterMode=FilterMode.Point},Depth=new RenderTexture(w,h,0,RenderTextureFormat.RFloat) {filterMode=FilterMode.Point}};
        if(!target.Color.Create()||!target.Depth.Create()) throw new InvalidDataException("World target allocation failed."); camera.targetTexture=target.Color;
        target.Copy=new CommandBuffer {name="Multiscale positive linear depth"}; target.Copy.Blit(BuiltinRenderTextureType.CameraTarget,target.Depth,depthCopy); camera.AddCommandBuffer(CameraEvent.AfterEverything,target.Copy);
    }
    void OnRenderImage(RenderTexture source,RenderTexture destination)
    {
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
        string[] words=text.Split(new[] {' '},StringSplitOptions.RemoveEmptyEntries); if(words.Length==0) return; if(capturing) throw new InvalidOperationException("Capture controls frozen."); if(quitRequested) throw new InvalidOperationException("Owned shutdown pending."); state.commands++;
        if(words[0]=="quit"&&words.Length==1) { quitRequested=true; state.status="stopping"; Write(); return; }
        if(words[0]=="status") { Write(); return; }
        if(words[0]=="captureframe") { StartCoroutine(Capture()); return; }
        if(words[0]=="materialmode"&&words.Length==2&&(words[1]=="reference"||words[1]=="instanced")) { generation.Craft.SetMode(words[1]); state.materialMode=words[1]; }
        else if(words[0]=="view"&&words.Length==2) SelectView(words[1]);
        else if(words[0]=="debugshaderreload") ReloadDebugShader(Absolute(text.Substring(words[0].Length).Trim()));
        else if(words[0]=="zoom"&&words.Length==2) distance=Math.Max(.25,Math.Min(1e10,Math.Pow(10,Number(words[1]))));
        else if(words[0]=="orbit"&&words.Length==3) { yaw=(float)Number(words[1]); pitch=Mathf.Clamp((float)Number(words[2]),-89,89); }
        else if(words[0]=="focusinstance"&&words.Length==2) { float radius; generation.Craft.PresentationInstanceCenter(words[1],out radius); state.focus=generation.Scene.vesselId; state.focusInstance=words[1]; distance=Math.Max(.25,radius*3); }
        else if(words[0]=="focus"&&words.Length==2&&(words[1]==generation.Scene.vesselId||words[1]==generation.Scene.bodyId)) { state.focus=words[1]; state.focusInstance=null; }
        else if(words[0]=="preset"&&words.Length==2) { PresentationQualityPolicy.Resolve(words[1],state.context,"craft","world",generation.Scene.vesselId,overrides); state.preset=words[1]; }
        else if(words[0]=="override"&&words.Length==5) { string id=words[1]; if(id!=generation.Scene.bodyId&&id!=generation.Scene.vesselId) throw new InvalidDataException("Known asset ID required."); var next=overrides.Where(o=>o.assetId!=id).Concat(new[] {new PresentationQualityOverride {scope="asset",assetId=id,pass="world",scaleSet=true,worldScale=(float)Number(words[2]),mipSet=true,textureMipBias=(float)Number(words[3]),geometrySet=true,geometryErrorPixels=(float)Number(words[4])}}).ToArray(); PresentationQualityPolicy.Resolve(state.preset,state.context,id==generation.Scene.bodyId?"body":"craft","world",id,next); overrides=next; }
        else if(words[0]=="fixturehotreload") Reload(Absolute(text.Substring(words[0].Length).Trim()),false);
        else if(words[0]=="snapshotreload") { string path=Absolute(text.Substring(words[0].Length).Trim()); var snap=ResidentMaterialFormat.ReadSnapshot(path); if(snap.instances.Any(i=>i.opacityEffective!=1)) throw new InvalidDataException("Translucent snapshot rejected."); generation.Craft.ReloadSnapshot(path); }
        else throw new InvalidDataException("status|quit|captureframe|materialmode reference/instanced|view composed/craft-color/body-color/craft-depth/body-depth|zoom log10metres|orbit yaw pitch|focus ID|preset low/balanced/high|override ID scale mip error|fixturehotreload PATH|snapshotreload PATH");
        policyDirty=true; Write();
    }
    static void ValidateDebugShader(Shader shader)
    {
        if(shader==null||!shader.isSupported||shader.name!="Continuum/MultiscaleDiagnostic") throw new InvalidDataException("Supported exact diagnostic shader required.");
        var probe=new Material(shader); try { foreach(string name in new[] {"_MainTex","_Mode","_MaterialColor"}) if(!probe.HasProperty(name)) throw new InvalidDataException("Diagnostic shader interface missing: "+name); } finally { Destroy(probe); }
    }
    static Material PrepareBodyDebug(Generation value,Shader shader)
    {
        var m=new Material(shader); m.SetTexture("_MainTex",value.Texture); m.SetTextureScale("_MainTex",value.Material.GetTextureScale("_MainTex")); m.SetTextureOffset("_MainTex",value.Material.GetTextureOffset("_MainTex")); m.SetColor("_MaterialColor",Color.magenta); return m;
    }
    void SelectView(string value)
    {
        if(value=="composed") { generation.Craft.SetGeometryDebug("off"); bodyGeometryDebug=false; state.view=value; return; }
        bool body=value.StartsWith("body-",StringComparison.Ordinal),craft=value.StartsWith("craft-",StringComparison.Ordinal); if(!body&&!craft) throw new InvalidDataException("View requires craft- or body- prefix.");
        string mode=value.Substring(body?5:6); bool geometry=mode!="color"&&mode!="depth"&&mode!="linear-depth"&&mode!="log-depth";
        int debug=geometry?ResidentMaterialRuntime.GeometryDebugMode(mode):0;
        if(body&&mode=="vertex-colors") throw new InvalidDataException("Body source COLOR was not captured; refusing fabricated vertex-color diagnostic.");
        generation.Craft.SetGeometryDebug(craft&&geometry?mode:"off"); bodyGeometryDebug=body&&geometry; if(bodyGeometryDebug) generation.DebugMaterial.SetFloat("_Mode",debug);
        state.view=value; state.debugLegend=geometry?"Unlit actual geometry; normals encoded [-1,1] to [0,1]; UV fractional RG; stable material-index hue":"Depth metres: raw clamps on display; linear near/far or log(1+depth)/log(1+far); black is unwritten";
    }
    void ReloadDebugShader(string path)
    {
        string digest=Hash(path); AssetBundle next=AssetBundle.LoadFromFile(path); if(next==null) throw new InvalidDataException("Diagnostic bundle load failed."); Material[] craft=null; Material body=null;
        try
        {
            Shader selected=null; foreach(var shader in next.LoadAllAssets<Shader>()) if(shader.name=="Continuum/MultiscaleDiagnostic") { if(selected!=null) throw new InvalidDataException("Duplicate diagnostic shader."); selected=shader; }
            ValidateDebugShader(selected); craft=generation.Craft.PrepareDebugShader(selected); body=PrepareBodyDebug(generation,selected); if(bodyGeometryDebug) body.SetFloat("_Mode",ResidentMaterialRuntime.GeometryDebugMode(state.view.Substring(5)));
            if(Hash(path)!=digest) throw new InvalidDataException("Diagnostic bundle changed during preparation.");
            var oldBundle=debugBundle; var oldBody=generation.DebugMaterial; generation.Craft.CommitDebugShader(craft); craft=null; generation.DebugMaterial=body; body=null; debugShader=selected; debugBundle=next; next=null; Destroy(oldBody); if(oldBundle!=null) oldBundle.Unload(true); state.debugBundleSha256=digest; state.debugShaderGeneration++; Write();
        }
        finally { if(craft!=null) foreach(var m in craft) Destroy(m); if(body!=null) Destroy(body); if(next!=null) next.Unload(true); }
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
        GUI.Box(new Rect(10,10,430,150),"Continuous multiscale scene — captured assets"); GUI.Label(new Rect(20,35,410,20),state.preset+" / "+state.distance.ToString("G5")+" metres / "+state.materialMode+" / "+state.view);
        float log=GUI.HorizontalSlider(new Rect(20,65,400,20),(float)Math.Log10(Math.Max(.25,distance)),-.6f,10); if(!capturing) distance=Math.Pow(10,log);
        if(GUI.Button(new Rect(20,90,100,22),"low")&&!capturing) { state.preset="low"; policyDirty=true; } if(GUI.Button(new Rect(130,90,100,22),"balanced")&&!capturing) { state.preset="balanced"; policyDirty=true; } if(GUI.Button(new Rect(240,90,100,22),"high")&&!capturing) { state.preset="high"; policyDirty=true; }
        GUI.Label(new Rect(20,120,410,30),state.fixtureScope+" / "+state.fixtureInstances+" rows / "+state.fixtureMeshes+" meshes");
        GUI.Label(new Rect(450,10,Screen.width-460,70),state.debugLegend);
        for(int i=0;i<debugViews.Length;i++) if(GUI.Button(new Rect(10+(i%2)*215,170+(i/2)*23,210,22),debugViews[i])&&!capturing&&!quitRequested) { SelectView(debugViews[i]); Write(); }
    }
    void SocketLoop()
    {
        while(!stopping) try { using(var client=listener.AcceptTcpClient()) using(var stream=client.GetStream()) { stream.ReadTimeout=2000; stream.WriteTimeout=2000; var bytes=new List<byte>(); int b; while((b=stream.ReadByte())>=0&&b!='\n') { if(bytes.Count>=4096) throw new InvalidDataException("Command bound."); bytes.Add((byte)b); } if(Interlocked.Increment(ref queued)>16) { Interlocked.Decrement(ref queued); continue; } var c=new Command {Text=Encoding.UTF8.GetString(bytes.ToArray()).Trim()}; queue.Enqueue(c); for(int i=0;i<500&&!c.Done&&!stopping;i++) Thread.Sleep(10); if(c.Done) { byte[] reply=Encoding.UTF8.GetBytes(c.Reply+"\n"); stream.Write(reply,0,reply.Length); stream.Flush(); c.ReplyWritten=true; } } } catch { if(stopping) return; }
    }
    void Write() { if(output!=null&&Directory.Exists(output)) File.WriteAllText(Path.Combine(output,"multiscale-status.json"),JsonUtility.ToJson(state,true)); }
    void Fail(Exception e) { state.status="failed"; state.error=e.ToString(); if(present!=null) present.enabled=false; Write(); Debug.LogException(e); }
    void OnDestroy()
    {
        if(debugDepth!=null) Destroy(debugDepth);
        stopping=true; if(listener!=null) listener.Stop(); Camera.onPreCull-=BeforeCamera; if(generation!=null) generation.Dispose(); if(debugBundle!=null) debugBundle.Unload(true); if(craftTarget!=null) craftTarget.Dispose(); if(bodyTarget!=null) bodyTarget.Dispose(); if(craftCamera!=null) Destroy(craftCamera.gameObject); if(bodyCamera!=null) Destroy(bodyCamera.gameObject); if(composite!=null) Destroy(composite); if(depthCopy!=null) Destroy(depthCopy);
        foreach(Camera c in disabledCameras) if(c!=null) c.enabled=true; if(settingsOwned) { QualitySettings.antiAliasing=priorAA; QualitySettings.vSyncCount=priorVsync; Application.targetFrameRate=priorFrameRate; Shader.SetGlobalVector("_LocalCameraPos",priorCameraPosition); Shader.SetGlobalVector("_LocalCameraDir",priorCameraDirection); } state.cleanupStatus="release-requested-owned-hooks-removed-settings-restored"; Write();
    }
}
