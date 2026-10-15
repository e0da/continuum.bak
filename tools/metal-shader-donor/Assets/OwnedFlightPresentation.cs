using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KspContinuum;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class OwnedFlightPresentation : IDisposable
{
    [Serializable] public sealed class StatusData
    {
        public string schema="continuum-owned-flight-presentation/v1",status="preparing",sceneSha256,wholeCraftSha256,bodyGeometrySha256,bodyTextureSha256,contextSha256;
        public string appearance="Captured whole triangle geometry/MainTex/ST/tint/vertexRGB; two-sided opaque approximation, not faithful stock shaders",bodyAppearance="Captured scaled mesh/MainTex, Lambert approximation",view="composed",cleanupStatus;
        public string bodyMipPolicy,scope="Owned region poses bound to immutable actual part-local render geometry; separate positive-eye-depth craft/body domains; no stock alpha/shadow/specular/MPB parity";
        public int sourceProcessId,drawSlots,rendererInventory,capturedRenderers,omittedRenderers,meshes,textures,materials,recipes,packets,visibleDraws,propertyBlocksObserved,approximateTransparency,unknownCull,missingNormals,missingUV,defaultWhiteColors,bodyMipCount;
        public string[] shaderFamilies;
        public int width,height,worldWidth,worldHeight,frames,stateTextureId,stateUploads,dynamicUploads,commandBuilds,lastSubmissionCalls;
        public long matrixUpdates,matrixTicks,renderTicks,clockFrequency=System.Diagnostics.Stopwatch.Frequency,dynamicUploadBytes;
        public double universalTime,physicalUniversalTime,worldTime,displayDelaySeconds;public long worldEpoch,displayRevision;public bool interpolated;
        public double[] renderOriginBodyMeters=new double[3],cameraOriginBodyMeters=new double[3];
        public float craftNear,craftFar,bodyNear,bodyFar,worldScale=1;public bool orthographic;public float verticalSpanMeters,fov;
    }
    sealed class Targets : IDisposable
    {
        public Camera Camera;public RenderTexture Color,Depth;public CommandBuffer Copy;public bool CopyAttached;
        public void Dispose(){if(Camera!=null){if(Camera.targetTexture==Color)Camera.targetTexture=null;if(Copy!=null&&CopyAttached)Camera.RemoveCommandBuffer(CameraEvent.AfterEverything,Copy);}if(Copy!=null)Copy.Release();if(Color!=null){Color.Release();UnityEngine.Object.Destroy(Color);}if(Depth!=null){Depth.Release();UnityEngine.Object.Destroy(Depth);}}
    }
    public readonly StatusData Status=new StatusData();
    public readonly OwnedFlightVisualScene Scene;
    readonly OwnedRuntimeImportResult imported;
    readonly OwnedFlightPresentationBindings bindings;
    readonly WholeCraftRuntime craft;
    readonly Mesh body;
    readonly Texture2D bodyTexture;
    readonly Material bodyMaterial,bodyDebug,depthCopy,composite,debugDepth;
    readonly Camera craftCamera,bodyCamera;
    readonly Light sunlight;
    readonly double[] doubles;
    readonly Matrix4x4[] matrices;
    readonly int[] rowBodies;
    readonly Rotation[] regionDelta;
    readonly Vec[] actualRoots,displayRoots;
    long matrixDisplayRevision=-1;
    readonly Matrix4x4 sourceBodyMatrix;
    readonly Rotation sourceBodyRotation;
    readonly AmbientMode priorAmbientMode;
    readonly Color priorAmbient;
    bool ambientOwned,disposed,matrixSet;
    OwnedFlightSession flight;
    Targets craftTargets,bodyTargets;
    OwnedRuntimeStamp matrixStamp;
    Vec matrixOrigin,lastWorldOrigin;
    static void Need(bool ok,string reason){if(!ok)throw new InvalidDataException(reason);}
    static bool Finite(double n){return !double.IsNaN(n)&&!double.IsInfinity(n);}
    static Vector3 F(Vec v){Need(Finite(v.X)&&Finite(v.Y)&&Finite(v.Z)&&Math.Abs(v.X)<=float.MaxValue&&Math.Abs(v.Y)<=float.MaxValue&&Math.Abs(v.Z)<=float.MaxValue,"Owned visual position exceeds native float range.");return new Vector3((float)v.X,(float)v.Y,(float)v.Z);}
    static Vec Sub(Vec a,Vec b){return new Vec(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
    static bool Same(Vec a,Vec b){return a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return Hex(h.ComputeHash(s));}
    static string Hex(byte[] value){return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();}
    static string Under(string root,string relative){MultiscaleSceneFormat.PortablePath(relative);string prefix=Path.GetFullPath(root)+Path.DirectorySeparatorChar,path=Path.GetFullPath(Path.Combine(root,relative));Need(path.StartsWith(prefix,StringComparison.Ordinal),"Owned visual path escaped its scene directory.");return path;}
    static byte[] Bytes(string path,long expected,long maximum)
    {using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){Need(expected>=0&&file.Length==expected&&expected<=maximum&&expected<=int.MaxValue,"Owned visual payload size differs.");var value=new byte[(int)expected];int i=0;while(i<value.Length){int n=file.Read(value,i,value.Length-i);Need(n>0,"Owned visual payload truncated.");i+=n;}Need(file.ReadByte()==-1,"Owned visual payload grew during read.");return value;}}
    static Matrix4x4 Matrix(double[] a){var m=new Matrix4x4();for(int r=0;r<4;r++)for(int c=0;c<4;c++){Need(Finite(a[r*4+c])&&Math.Abs(a[r*4+c])<=float.MaxValue,"Owned visual matrix exceeds native range.");m[r,c]=(float)a[r*4+c];}return m;}
    static Color ColorValue(float[] a){return new Color(a[0],a[1],a[2],a[3]);}
    static Material Material(string name){var shader=Shader.Find(name);Need(shader!=null&&shader.isSupported,"Required owned-flight shader is absent/unsupported: "+name);return new Material(shader);}
    static Camera Camera(string name){var value=new GameObject(name).AddComponent<Camera>();value.enabled=false;value.renderingPath=RenderingPath.Forward;value.clearFlags=CameraClearFlags.SolidColor;value.backgroundColor=Color.clear;value.allowMSAA=false;value.allowHDR=false;return value;}
    public OwnedFlightPresentation(OwnedRuntimeImportResult imported,OwnedFlightSession flight,string visualScenePath,string expectedSceneSha256,Shader diagnosticShader)
    {
        this.imported=imported;
        try
        {
            Need(imported!=null&&flight!=null&&ReferenceEquals(imported.World,flight.Runtime)&&Path.IsPathRooted(visualScenePath),"Owned visual owner/path differs.");
            Need(expectedSceneSha256!=null&&Hash(visualScenePath)==expectedSceneSha256,"Owned visual manifest digest differs.");long length=new FileInfo(visualScenePath).Length;Need(length<=1024*1024,"Owned visual manifest exceeds bound.");Scene=JsonUtility.FromJson<OwnedFlightVisualScene>(System.Text.Encoding.UTF8.GetString(Bytes(visualScenePath,length,1024*1024)));OwnedFlightPresentationBindings.Validate(Scene,imported);
            bool modern=Environment.GetEnvironmentVariable("CONTINUUM_MODERN_UNITY_PROBE")=="1"&&Application.unityVersion=="6000.3.26f1"&&Scene.engine=="2019.4.18f1";
            Need((Scene.engine==Application.unityVersion||modern)&&Scene.colorSpace==QualitySettings.activeColorSpace.ToString(),"Owned visual engine/colour space differs.");
            Need(flight.Context.BodyId==Scene.bodyId&&flight.Context.RunId==Scene.runId&&flight.Context.VesselId==Scene.vesselId&&flight.Context.ProcessId==Scene.processId,"Owned visual body/context source differs.");
            Need(Math.Abs(flight.Context.Radius-Scene.bodyRadiusMeters)<=1e-9*Math.Max(1,Scene.bodyRadiusMeters),"Owned visual body radius/context differs.");
            Vec sourceRoot=Sub(flight.Context.CapturedRootPosition,new Vec(Scene.rootPositionBodyRelative[0],Scene.rootPositionBodyRelative[1],Scene.rootPositionBodyRelative[2]));Need(sourceRoot.X*sourceRoot.X+sourceRoot.Y*sourceRoot.Y+sourceRoot.Z*sourceRoot.Z<=1e-8,"Owned visual root position/context differs beyond native acquisition precision.");
            Need(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)&&SystemInfo.supportedRenderTargetCount>=2,"Owned craft/body positive-depth targets unavailable.");
            string root=Path.GetDirectoryName(Path.GetFullPath(visualScenePath)),wholePath=Under(root,Scene.wholeCraft);Need(Hash(wholePath)==Scene.wholeCraftSha256,"Owned whole source hash differs.");
            craft=new WholeCraftRuntime(wholePath,diagnosticShader);Need(craft.ManifestSha256==Scene.wholeCraftSha256&&(craft.LayerMask&(1<<29))==0,"Owned craft overlaps reserved body layer or changed source.");
            var wholeGeometry=WholeCraftSceneFormat.ReadGeometry(Under(Path.GetDirectoryName(wholePath),craft.Manifest.geometry));bindings=new OwnedFlightPresentationBindings(imported,Scene,craft.Manifest,wholeGeometry);
            craft.SetRenderMode("retained-grouped");craft.SetGeometryDebug("off");doubles=new double[bindings.DrawCount*16];matrices=new Matrix4x4[bindings.DrawCount];
            rowBodies=new int[bindings.DrawCount];var definition=imported.World.Definition;for(int i=0;i<rowBodies.Length;i++)rowBodies[i]=definition.BodySlot(definition.GetPart(definition.PartSlot(craft.Manifest.draws[i].partId)).BodyId);
            regionDelta=new Rotation[definition.BodyCount];actualRoots=new Vec[definition.BodyCount];displayRoots=new Vec[definition.BodyCount];
            string geometryPath=Under(root,Scene.bodyGeometry);Need(Hash(geometryPath)==Scene.bodyGeometrySha256,"Owned body geometry hash differs.");var geometry=ResidentMaterialFormat.ReadGeometry(geometryPath);Need(geometry.meshes.Length==1,"Owned body geometry requires one captured mesh.");var g=geometry.meshes[0];int count=g.positions.Length/3;var p=new Vector3[count];var n=new Vector3[count];var uv=new Vector2[count];
            Need(g.normals.Length==g.positions.Length&&g.uv.Length==count*2,"Body normals/UV missing; faithful input cannot be fabricated.");for(int i=0;i<count;i++){p[i]=new Vector3(g.positions[i*3],g.positions[i*3+1],g.positions[i*3+2]);n[i]=new Vector3(g.normals[i*3],g.normals[i*3+1],g.normals[i*3+2]);uv[i]=new Vector2(g.uv[i*2],g.uv[i*2+1]);}
            body=new Mesh{indexFormat=IndexFormat.UInt32,vertices=p,normals=n,uv=uv,triangles=g.indices};body.RecalculateBounds();
            var t=Scene.bodyTexture;byte[] payload=Bytes(Under(root,t.payload),t.payloadLength,1073741824);using(var h=SHA256.Create())Need(Hex(h.ComputeHash(payload))==t.payloadSha256,"Owned body texture hash differs.");
            bodyTexture=new Texture2D(t.width,t.height,(TextureFormat)t.format,t.mipCount>1,t.linear);bodyTexture.LoadRawTextureData(payload);bodyTexture.Apply(false,true);bodyTexture.filterMode=(FilterMode)t.filter;bodyTexture.wrapMode=(TextureWrapMode)t.wrap;bodyTexture.anisoLevel=t.aniso;bodyTexture.mipMapBias=t.mipBias;
            var graphics=typeof(Texture).GetProperty("graphicsFormat");Need(bodyTexture.mipmapCount==t.mipCount&&(int)bodyTexture.format==t.format&&graphics!=null&&Convert.ToInt32(graphics.GetValue(bodyTexture,null))==t.graphicsFormat,"Owned body native texture descriptor differs.");
            bodyMaterial=Material("Continuum/MultiscaleBodyDiffuse");bodyMaterial.SetTexture("_MainTex",bodyTexture);bodyMaterial.color=ColorValue(Scene.bodyColor);bodyMaterial.SetTextureScale("_MainTex",new Vector2(Scene.bodyTextureScaleOffset[0],Scene.bodyTextureScaleOffset[1]));bodyMaterial.SetTextureOffset("_MainTex",new Vector2(Scene.bodyTextureScaleOffset[2],Scene.bodyTextureScaleOffset[3]));
            Need(diagnosticShader!=null&&diagnosticShader.isSupported,"Owned diagnostic shader unavailable.");bodyDebug=new Material(diagnosticShader);bodyDebug.SetTexture("_MainTex",bodyTexture);bodyDebug.SetVector("_MainTex_ST",new Vector4(Scene.bodyTextureScaleOffset[0],Scene.bodyTextureScaleOffset[1],Scene.bodyTextureScaleOffset[2],Scene.bodyTextureScaleOffset[3]));bodyDebug.SetColor("_Tint",ColorValue(Scene.bodyColor));bodyDebug.SetColor("_MaterialColor",Color.cyan);bodyDebug.SetFloat("_ContinuumRetained",0);
            depthCopy=Material("Hidden/Continuum/MultiscaleDepthCopy");composite=Material("Hidden/Continuum/MultiscaleComposite");debugDepth=Material("Hidden/Continuum/MultiscaleDebugDepth");craftCamera=Camera("Owned flight actual craft MRT");bodyCamera=Camera("Owned flight captured planet");
            sourceBodyMatrix=Matrix(Scene.bodyLocalToMeters);sourceBodyRotation=new Rotation(Scene.bodyRotation[0],Scene.bodyRotation[1],Scene.bodyRotation[2],Scene.bodyRotation[3]);Need(Math.Abs(Rotation.Dot(sourceBodyRotation,flight.Context.CapturedBodyRotation))>=1-1e-10,"Owned body physical orientation/context differs.");
            sunlight=new GameObject("Owned captured Sun").AddComponent<Light>();sunlight.type=LightType.Directional;sunlight.color=ColorValue(Scene.sunColor);sunlight.intensity=1;sunlight.cullingMask=1<<29;sunlight.shadows=LightShadows.None;sunlight.transform.rotation=Quaternion.LookRotation(-F(new Vec(Scene.directionToSun[0],Scene.directionToSun[1],Scene.directionToSun[2])));
            priorAmbientMode=RenderSettings.ambientMode;priorAmbient=RenderSettings.ambientLight;RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=ColorValue(Scene.ambientColor);ambientOwned=true;
            BindSession(flight);Need(Hash(visualScenePath)==expectedSceneSha256&&Hash(wholePath)==Scene.wholeCraftSha256&&Hash(geometryPath)==Scene.bodyGeometrySha256,"Owned visual input changed during preparation.");
            Status.sceneSha256=expectedSceneSha256;Status.wholeCraftSha256=Scene.wholeCraftSha256;Status.bodyGeometrySha256=Scene.bodyGeometrySha256;Status.bodyTextureSha256=t.payloadSha256;Status.sourceProcessId=Scene.processId;
            Status.drawSlots=craft.Manifest.drawSlots;Status.rendererInventory=craft.Manifest.rendererInventory;Status.capturedRenderers=craft.Manifest.capturedRenderers;Status.omittedRenderers=craft.Manifest.omittedRenderers;Status.meshes=craft.MeshCount;Status.textures=craft.Manifest.textures.Length;Status.materials=craft.Manifest.materials.Length;Status.recipes=craft.RecipeCount;Status.packets=craft.PacketCount;Status.visibleDraws=craft.Manifest.draws.Length-craft.NonvisibleDraws;Status.propertyBlocksObserved=craft.PropertyBlockObserved;Status.approximateTransparency=craft.ApproximateTransparency;Status.unknownCull=craft.UnknownCull;Status.missingNormals=craft.MissingNormals;Status.missingUV=craft.MissingUV;Status.defaultWhiteColors=craft.DefaultWhiteColors;Status.shaderFamilies=craft.Manifest.materials.Select(m=>m.shader).Distinct().OrderBy(s=>s,StringComparer.Ordinal).ToArray();Status.bodyMipCount=bodyTexture.mipmapCount;Status.bodyMipPolicy=Status.bodyMipCount==1?"single-level; texture detail improvement unsupported":"captured mip chain; visual quality unqualified";Status.status="resident";
        }
        catch{Dispose();throw;}
    }
    public void BindSession(OwnedFlightSession next)
    {Need(!disposed&&next!=null&&next.Context.NativeManifestSha256==imported.Source.nativeManifestSha256&&next.Context.PartSpecsSha256==imported.Source.partSpecsSha256&&next.Context.BodyId==Scene.bodyId,"Owned visual session has foreign source/context.");flight=next;matrixSet=false;Status.contextSha256=next.Context.SourceSha256;}
    public void SetWorldScale(float scale){Need(Finite(scale)&&scale>=.25f&&scale<=1,"Owned render scale must be .25..1.");Status.worldScale=scale;}
    public void SetDebugView(string value)
    {
        string[] views={"composed","craft-color","body-color","craft-flat","body-flat","craft-albedo","body-albedo","craft-world-normals","body-world-normals","craft-view-normals","body-view-normals","craft-uv","body-uv","craft-material-id","body-material-id","craft-vertex-colors","craft-linear-depth","body-linear-depth","craft-log-depth","body-log-depth"};Need(Array.IndexOf(views,value)>=0,"Unknown owned visual debug view.");
        if(value.StartsWith("craft-")&&!value.EndsWith("depth")&&value!="craft-color")craft.SetGeometryDebug(value.Substring(6));else craft.SetGeometryDebug("off");
        if(value.StartsWith("body-")&&!value.EndsWith("depth")&&value!="body-color")bodyDebug.SetFloat("_Mode",ResidentMaterialRuntime.GeometryDebugMode(value.Substring(5)));Status.view=value;
    }
    Targets NewTargets(Camera camera,int width,int height,bool copy)
    {
        var target=new Targets{Camera=camera,Color=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32){antiAliasing=1,filterMode=FilterMode.Point},Depth=new RenderTexture(width,height,0,RenderTextureFormat.RFloat){antiAliasing=1,filterMode=FilterMode.Point}};
        try{Need(target.Color.Create()&&target.Depth.Create(),"Owned visual target allocation failed.");if(copy){target.Copy=new CommandBuffer{name="Owned planet positive depth"};target.Copy.Blit(BuiltinRenderTextureType.CameraTarget,target.Depth,depthCopy);}return target;}catch{target.Dispose();throw;}
    }
    void Resize(int width,int height)
    {
        if(craftTargets!=null&&craftTargets.Color.width==width&&craftTargets.Color.height==height)return;Targets nextCraft=null,nextBody=null;
        try{nextCraft=NewTargets(craftCamera,width,height,false);nextBody=NewTargets(bodyCamera,width,height,true);craft.ConfigureRetainedTargets(nextCraft.Color,nextCraft.Depth);}
        catch{if(nextCraft!=null)nextCraft.Dispose();if(nextBody!=null)nextBody.Dispose();throw;}
        if(craftTargets!=null)craftTargets.Dispose();if(bodyTargets!=null)bodyTargets.Dispose();craftTargets=nextCraft;bodyTargets=nextBody;craftCamera.targetTexture=nextCraft.Color;bodyCamera.targetTexture=nextBody.Color;bodyCamera.AddCommandBuffer(CameraEvent.AfterEverything,nextBody.Copy);nextBody.CopyAttached=true;
    }
    static void Configure(Camera source,Camera target,RenderTexture color,int mask,bool depth)
    {target.CopyFrom(source);target.enabled=false;target.transform.SetPositionAndRotation(Vector3.zero,source.transform.rotation);target.targetTexture=color;target.renderingPath=RenderingPath.Forward;target.cullingMask=mask;target.clearFlags=CameraClearFlags.SolidColor;target.backgroundColor=Color.clear;target.allowHDR=false;target.allowMSAA=false;target.depthTextureMode=depth?DepthTextureMode.Depth:DepthTextureMode.None;target.rect=new Rect(0,0,1,1);target.ResetProjectionMatrix();}
    public void Render(Camera view,Vec cameraBodyOriginMeters,RenderTexture destination,OwnedFlightPresentationLease frame,OwnedFlightDisplayFrame display)
    {
        Need(!disposed&&view!=null&&frame!=null&&display!=null&&display.PhysicalStamp.Matches(frame.Read.Stamp)&&display.ContextSha256==frame.Context.SourceSha256&&frame.Context.SourceSha256==Status.contextSha256,"Owned visual render owner/source unavailable.");long start=System.Diagnostics.Stopwatch.GetTimestamp();var previous=RenderTexture.active;
        try
        {
            int width=destination==null?view.pixelWidth:destination.width,height=destination==null?view.pixelHeight:destination.height;Need(width>0&&height>0&&width<=8192&&height<=8192&&(long)width*height<=16777216,"Owned visual viewport exceeds bounds.");int w=Math.Max(1,(int)Math.Ceiling(width*Status.worldScale)),h=Math.Max(1,(int)Math.Ceiling(height*Status.worldScale));Resize(w,h);
            Configure(view,craftCamera,craftTargets.Color,craft.LayerMask,false);Configure(view,bodyCamera,bodyTargets.Color,1<<29,true);
            var read=frame.Read;
            {
                Need(display.RegionCount==read.RegionCount,"Display region shape differs.");Vec origin=display.GetRegion(0).RootPositionBodyMeters;
                if(!matrixSet||!matrixStamp.Matches(read.Stamp)||!Same(lastWorldOrigin,frame.Origin)||matrixDisplayRevision!=display.Revision)
                {
                    long t=System.Diagnostics.Stopwatch.GetTimestamp();bindings.Fill(read,frame.Origin,origin,doubles);
                    for(int r=0;r<read.RegionCount;r++){var actual=read.GetRegion(r);var visual=display.GetRegion(r);Need(actual.Id==visual.Id,"Display region identity differs.");regionDelta[r]=visual.Orientation*actual.Motion.Orientation.Inverse;actualRoots[r]=Sub(frame.Origin+actual.Motion.RootPosition,origin);displayRoots[r]=Sub(visual.RootPositionBodyMeters,origin);}
                    for(int i=0;i<matrices.Length;i++)
                    {
                        int r=read.Topology.RegionOfBody(rowBodies[i]);for(int c=0;c<4;c++){var p=new Vec(doubles[i*16+c],doubles[i*16+4+c],doubles[i*16+8+c]);p=c==3?displayRoots[r]+regionDelta[r].Rotate(Sub(p,actualRoots[r])):regionDelta[r].Rotate(p);Vector3 value=F(p);matrices[i][0,c]=value.x;matrices[i][1,c]=value.y;matrices[i][2,c]=value.z;matrices[i][3,c]=(float)doubles[i*16+12+c];}
                    }
                    craft.ApplyPresentationMatrices(matrices);matrixOrigin=origin;lastWorldOrigin=frame.Origin;matrixStamp=read.Stamp;matrixDisplayRevision=display.Revision;matrixSet=true;Status.matrixUpdates++;Status.matrixTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-t;
                }
                craft.SetPresentationAnchor(F(Sub(matrixOrigin,cameraBodyOriginMeters)));Status.worldEpoch=read.Stamp.Epoch;Status.worldTime=read.Stamp.Time;
            }
            var bounds=craft.Bounds;float radius=Math.Max(.01f,bounds.extents.magnitude),distance=bounds.center.magnitude;craftCamera.nearClipPlane=Math.Max(.001f,(distance-radius)*.5f);craftCamera.farClipPlane=Math.Max(craftCamera.nearClipPlane*2,distance+radius*2);
            double utc=display.UniversalTime;Status.physicalUniversalTime=frame.UniversalTime;Status.displayDelaySeconds=display.DelaySeconds;Status.displayRevision=display.Revision;Status.interpolated=display.Interpolated;Rotation rotation=frame.Context.BodyRotationAt(utc)*sourceBodyRotation.Inverse;var bodyMatrix=Matrix4x4.Rotate(new Quaternion((float)rotation.X,(float)rotation.Y,(float)rotation.Z,(float)rotation.W))*sourceBodyMatrix;
            Vec offset=rotation.Rotate(new Vec(Scene.bodyLocalToMeters[3],Scene.bodyLocalToMeters[7],Scene.bodyLocalToMeters[11]));Vector3 position=F(Sub(offset,cameraBodyOriginMeters));bodyMatrix.m03=position.x;bodyMatrix.m13=position.y;bodyMatrix.m23=position.z;
            double bodyDistance=Math.Sqrt(cameraBodyOriginMeters.X*cameraBodyOriginMeters.X+cameraBodyOriginMeters.Y*cameraBodyOriginMeters.Y+cameraBodyOriginMeters.Z*cameraBodyOriginMeters.Z);bodyCamera.nearClipPlane=(float)Math.Max(1,(bodyDistance-Scene.bodyRadiusMeters)*.5);bodyCamera.farClipPlane=(float)Math.Max(bodyCamera.nearClipPlane*2,bodyDistance+Scene.bodyRadiusMeters*2);
            bool bodyDiagnostic=Status.view.StartsWith("body-")&&!Status.view.EndsWith("depth")&&Status.view!="body-color";Graphics.DrawMesh(body,bodyMatrix,bodyDiagnostic?bodyDebug:bodyMaterial,29,bodyCamera,0,null,ShadowCastingMode.Off,false);bodyCamera.Render();craft.Submit(craftCamera);
            composite.SetTexture("_BodyColor",bodyTargets.Color);composite.SetTexture("_BodyDepth",bodyTargets.Depth);composite.SetTexture("_CraftColor",craftTargets.Color);composite.SetTexture("_CraftDepth",craftTargets.Depth);
            if(Status.view.EndsWith("depth")){bool planet=Status.view.StartsWith("body-");debugDepth.SetVector("_Range",new Vector4(planet?bodyCamera.nearClipPlane:craftCamera.nearClipPlane,planet?bodyCamera.farClipPlane:craftCamera.farClipPlane,Status.view.EndsWith("log-depth")?1:0,0));Graphics.Blit(planet?bodyTargets.Depth:craftTargets.Depth,destination,debugDepth);}
            else if(Status.view.StartsWith("craft-"))Graphics.Blit(craftTargets.Color,destination);else if(Status.view.StartsWith("body-"))Graphics.Blit(bodyTargets.Color,destination);else Graphics.Blit(craftTargets.Color,destination,composite);
            Status.frames++;Status.width=width;Status.height=height;Status.worldWidth=w;Status.worldHeight=h;Status.universalTime=utc;Status.orthographic=view.orthographic;Status.verticalSpanMeters=view.orthographicSize*2;Status.fov=view.fieldOfView;Status.craftNear=craftCamera.nearClipPlane;Status.craftFar=craftCamera.farClipPlane;Status.bodyNear=bodyCamera.nearClipPlane;Status.bodyFar=bodyCamera.farClipPlane;Status.stateTextureId=craft.StateTextureId;Status.stateUploads=craft.StateUploadCount;Status.dynamicUploads=craft.DynamicUploadCount;Status.dynamicUploadBytes=craft.DynamicUploadBytes;Status.commandBuilds=craft.CommandBuildCount;Status.lastSubmissionCalls=craft.LastSubmissionCalls;
            Status.cameraOriginBodyMeters[0]=cameraBodyOriginMeters.X;Status.cameraOriginBodyMeters[1]=cameraBodyOriginMeters.Y;Status.cameraOriginBodyMeters[2]=cameraBodyOriginMeters.Z;Status.renderOriginBodyMeters[0]=matrixOrigin.X;Status.renderOriginBodyMeters[1]=matrixOrigin.Y;Status.renderOriginBodyMeters[2]=matrixOrigin.Z;Status.renderTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-start;
        }
        finally{RenderTexture.active=previous;}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;if(craft!=null)craft.Dispose();if(craftTargets!=null)craftTargets.Dispose();if(bodyTargets!=null)bodyTargets.Dispose();
        foreach(var o in new UnityEngine.Object[]{body,bodyTexture,bodyMaterial,bodyDebug,depthCopy,composite,debugDepth})if(o!=null)UnityEngine.Object.Destroy(o);
        if(craftCamera!=null)UnityEngine.Object.Destroy(craftCamera.gameObject);if(bodyCamera!=null)UnityEngine.Object.Destroy(bodyCamera.gameObject);if(sunlight!=null)UnityEngine.Object.Destroy(sunlight.gameObject);
        if(ambientOwned){RenderSettings.ambientMode=priorAmbientMode;RenderSettings.ambientLight=priorAmbient;}Status.status="disposed";Status.cleanupStatus="released-owned-assets-targets-commands-cameras-light-and-restored-ambient";
    }
}
