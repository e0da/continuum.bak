using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using KspContinuum;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class ResidentMaterialRuntime : IDisposable
{
    sealed class ShaderLease { public AssetBundle Bundle; public Shader Shader; public int References; }
    static readonly Dictionary<string,ShaderLease> shaderLeases=new Dictionary<string,ShaderLease>();
    sealed class Packet
    {
        public int Mesh, Material, Layer, Shadow; public bool Receive;
        public int[] Sources; public Matrix4x4[] Matrices; public MaterialPropertyBlock Block;
    }
    public readonly ResidentMaterialManifest Manifest;
    public readonly string ManifestPath, ManifestSha256, BundlePath, BundleSha256, OriginalBundlePath, OriginalBundleSha256;
    public string StatePath { get; private set; }
    public string StateSha256 { get; private set; }
    public string Mode { get; private set; } = "reference";
    public string Diagnostic { get; private set; } = "off";
    public string GeometryDebug { get; private set; } = "off";
    Material[] debugMaterials;
    public long Generation { get { return snapshot.generation; } }
    public string TopologyId { get { return Manifest.topologyId; } }
    public int InstanceCount { get { return Manifest.instances.Length; } }
    public int MeshCount { get { return meshes.Length; } }
    public int VertexCount { get; private set; }
    public int TriangleCount { get; private set; }
    public int PacketCount { get { return packets.Length; } }
    public int DrawCalls { get; private set; }
    public int SubmittedFrames { get; private set; }
    public int SnapshotPublications { get; private set; }
    public int LayerMask { get; private set; }
    public Bounds Bounds { get; private set; }
    public Matrix4x4 Root { get; private set; }
    readonly bool cameraRelativeRoot;
    readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    Mesh[] meshes; Texture2D[] textures; Material[] referenceMaterials, packetMaterials, originalMaterials;
    ResidentSnapshot snapshot; Matrix4x4[] matrices; MaterialPropertyBlock[] referenceBlocks, singleBlocks; Matrix4x4[][] singleMatrices; Packet[] packets;
    Matrix4x4[] presentationBaseMatrices; Bounds presentationBaseBounds; Vector3 presentationAnchor; bool presentationAnchorRequested;
    ShaderLease shaderLease, originalShaderLease; GameObject lightObject; Camera activeCamera; int priorCameraMask; bool disposed, activated; int lastFrame = -1;
    ResidentMaterialProperty[] priorShaderGlobals;
    AmbientMode priorAmbient; Color priorSky, priorEquator, priorGround; float priorAmbientIntensity, priorShadowDistance; int priorShadowCascades;
    Vector3 priorPosition; Quaternion priorRotation; float priorFov, priorNear, priorFar;
    public static string Hash(string path) { using(var s=File.OpenRead(path)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    static string HashBytes(byte[] bytes) { using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static string PathUnder(string directory, string relative)
    {
        if(string.IsNullOrEmpty(relative)||Path.IsPathRooted(relative)) throw new InvalidDataException("Relative payload path required.");
        string root=Path.GetFullPath(directory)+Path.DirectorySeparatorChar, path=Path.GetFullPath(Path.Combine(directory,relative));
        if(!path.StartsWith(root,StringComparison.Ordinal)) throw new InvalidDataException("Payload outside fixture."); return path;
    }
    static Vector4 V(float[] v) { return new Vector4(v[0],v[1],v[2],v[3]); }
    static Color C(float[] v) { return new Color(v[0],v[1],v[2],v[3]); }
    static Matrix4x4 M(float[] v) { var m=new Matrix4x4(); for(int r=0;r<4;r++) for(int c=0;c<4;c++) m[r,c]=v[r*4+c]; return m; }
    static Quaternion Q(float[] v) { return new Quaternion(v[0],v[1],v[2],v[3]); }
    static float[] GlobalValues(ResidentMaterialProperty property)
    {
        if(property.valueBits==null||property.valueBits.Length==0) return property.value;
        var values=new float[property.valueBits.Length]; for(int i=0;i<values.Length;i++) values[i]=BitConverter.ToSingle(BitConverter.GetBytes(property.valueBits[i]),0); return values;
    }
    static ShaderLease AcquireShader(string path,string digest,string name)
    {
        ShaderLease lease;
        if(!shaderLeases.TryGetValue(digest,out lease))
        {
            var container=AssetBundle.LoadFromFile(path); if(container==null) throw new InvalidDataException("Shader bundle unavailable.");
            try
            {
                Shader selected=null; foreach(Shader value in container.LoadAllAssets<Shader>()) if(value.name==name) { if(selected!=null) throw new InvalidDataException("Duplicate shader identity."); selected=value; }
                if(selected==null||!selected.isSupported) throw new InvalidDataException("Required shader unsupported: "+name);
                lease=new ShaderLease {Bundle=container,Shader=selected}; shaderLeases.Add(digest,lease);
            }
            catch { container.Unload(true); throw; }
        }
        if(lease.Shader.name!=name) throw new InvalidDataException("Shared shader lease identity differs.");
        lease.References++; return lease;
    }
    void ApplyProperties(Material material,ResidentMaterialAsset asset)
    {
        foreach(var p in asset.properties)
        {
            if(!material.HasProperty(p.name)) throw new InvalidDataException("Captured shader property unavailable: "+p.name);
            if(p.kind==0) material.SetColor(p.name,C(p.value)); else if(p.kind==1) material.SetVector(p.name,V(p.value)); else if(p.kind==2||p.kind==3) material.SetFloat(p.name,p.value[0]);
            else if(p.kind==4) { material.SetTexture(p.name,p.textureIndex<0?null:textures[p.textureIndex]); material.SetTextureScale(p.name,new Vector2(p.scaleOffset[0],p.scaleOffset[1])); material.SetTextureOffset(p.name,new Vector2(p.scaleOffset[2],p.scaleOffset[3])); }
        }
    }
    public ResidentMaterialRuntime(string path,string partBundlePath,string originalBundlePath=null,bool cameraRelativeRoot=false)
    {
        this.cameraRelativeRoot=cameraRelativeRoot;
        if(!Path.IsPathRooted(path)||!Path.IsPathRooted(partBundlePath)) throw new InvalidDataException("Absolute fixture and part bundle required.");
        ManifestPath=Path.GetFullPath(path); BundlePath=Path.GetFullPath(partBundlePath); ManifestSha256=Hash(ManifestPath); BundleSha256=Hash(BundlePath);
        if(!string.IsNullOrEmpty(originalBundlePath)) { if(!Path.IsPathRooted(originalBundlePath)) throw new InvalidDataException("Absolute original shader bundle required."); OriginalBundlePath=Path.GetFullPath(originalBundlePath); OriginalBundleSha256=Hash(OriginalBundlePath); }
        try
        {
            if(new FileInfo(ManifestPath).Length>1048576) throw new InvalidDataException("Fixture manifest bound.");
            Manifest=JsonUtility.FromJson<ResidentMaterialManifest>(File.ReadAllText(ManifestPath));
            string directory=Path.GetDirectoryName(ManifestPath), geometryPath=PathUnder(directory,Manifest.geometry);
            if(Hash(geometryPath)!=Manifest.geometrySha256) throw new InvalidDataException("Geometry digest mismatch.");
            ResidentGeometry geometry=ResidentMaterialFormat.ReadGeometry(geometryPath); ResidentMaterialFormat.ValidateManifest(Manifest,geometry);
            bool modernProbe=Environment.GetEnvironmentVariable("CONTINUUM_MODERN_UNITY_PROBE")=="1"&&Application.unityVersion=="6000.3.26f1"&&Manifest.engine=="2019.4.18f1";
            if((Manifest.engine!=Application.unityVersion&&!modernProbe) || Manifest.colorSpace!=QualitySettings.activeColorSpace.ToString()) throw new InvalidDataException("Engine/color-space mismatch.");
            Root=M(Manifest.rootToWorld);
            Vector3 x=Root.GetColumn(0),y=Root.GetColumn(1),z=Root.GetColumn(2);
            if(Mathf.Abs(x.sqrMagnitude-1)>.0001f||Mathf.Abs(y.sqrMagnitude-1)>.0001f||Mathf.Abs(z.sqrMagnitude-1)>.0001f||Mathf.Abs(Vector3.Dot(x,y))>.0001f||Mathf.Abs(Vector3.Dot(x,z))>.0001f||Mathf.Abs(Vector3.Dot(y,z))>.0001f)
                throw new InvalidDataException("Rigid world orientation required for camera/light basis.");
            shaderLease=AcquireShader(BundlePath,BundleSha256,Manifest.materials[0].shader); Shader shader=shaderLease.Shader;
            if(shader==null||!shader.isSupported||!SystemInfo.supportsInstancing) throw new InvalidDataException("Supported resident shader/instancing required.");
            meshes=new Mesh[geometry.meshes.Length];
            for(int j=0;j<meshes.Length;j++)
            {
                var g=geometry.meshes[j]; int n=g.positions.Length/3; var vertices=new Vector3[n]; var normals=new Vector3[n]; var uv=new Vector2[n]; var tangents=new Vector4[n]; var colors=new Color32[n];
                for(int i=0;i<n;i++) { vertices[i]=new Vector3(g.positions[i*3],g.positions[i*3+1],g.positions[i*3+2]); normals[i]=new Vector3(g.normals[i*3],g.normals[i*3+1],g.normals[i*3+2]); uv[i]=new Vector2(g.uv[i*2],g.uv[i*2+1]); tangents[i]=new Vector4(g.tangents[i*4],g.tangents[i*4+1],g.tangents[i*4+2],g.tangents[i*4+3]); colors[i]=new Color32(255,255,255,255); }
                var mesh=new Mesh {name="Resident immutable mesh",indexFormat=IndexFormat.UInt32}; owned.Add(mesh); mesh.vertices=vertices; mesh.normals=normals; mesh.uv=uv; mesh.tangents=tangents; mesh.colors32=colors; mesh.triangles=g.indices; mesh.RecalculateBounds(); meshes[j]=mesh; VertexCount+=n; TriangleCount+=g.indices.Length/3;
            }
            textures=new Texture2D[Manifest.textures.Length];
            for(int i=0;i<textures.Length;i++)
            {
                var t=Manifest.textures[i]; string file=PathUnder(directory,t.payload); byte[] bytes=File.ReadAllBytes(file);
                if(bytes.LongLength!=t.payloadLength||HashBytes(bytes)!=t.payloadSha256) throw new InvalidDataException("Texture payload mismatch.");
                var texture=new Texture2D(t.width,t.height,(TextureFormat)t.format,t.mipCount>1,t.linear); owned.Add(texture);
                texture.LoadRawTextureData(bytes); texture.Apply(false,true); texture.filterMode=(FilterMode)t.filter; texture.wrapMode=(TextureWrapMode)t.wrap; texture.anisoLevel=t.aniso; texture.mipMapBias=t.mipBias;
                var property=typeof(Texture).GetProperty("graphicsFormat");
                if(texture.width!=t.width||texture.height!=t.height||(int)texture.format!=t.format||texture.mipmapCount!=t.mipCount||property==null||Convert.ToInt32(property.GetValue(texture,null))!=t.graphicsFormat) throw new InvalidDataException("Native texture format/mips mismatch.");
                textures[i]=texture;
            }
            referenceMaterials=new Material[Manifest.materials.Length]; packetMaterials=new Material[referenceMaterials.Length];
            for(int i=0;i<referenceMaterials.Length;i++)
            {
                var material=new Material(shader) {enableInstancing=false,renderQueue=Manifest.materials[i].queue}; owned.Add(material);
                ApplyProperties(material,Manifest.materials[i]);
                referenceMaterials[i]=material; var packet=new Material(material) {enableInstancing=true}; owned.Add(packet); packetMaterials[i]=packet;
            }
            if(OriginalBundlePath!=null)
            {
                if(Manifest.originalMaterials==null) throw new InvalidDataException("Actual original material bindings required.");
                originalShaderLease=AcquireShader(OriginalBundlePath,OriginalBundleSha256,Manifest.family); originalMaterials=new Material[Manifest.originalMaterials.Length];
                for(int i=0;i<originalMaterials.Length;i++) { var material=new Material(originalShaderLease.Shader) {enableInstancing=false,renderQueue=Manifest.originalMaterials[i].queue}; owned.Add(material); ApplyProperties(material,Manifest.originalMaterials[i]); originalMaterials[i]=material; }
            }
            var groups=new Dictionary<string,List<int>>(); var list=new List<Packet>();
            for(int i=0;i<Manifest.instances.Length;i++)
            {
                var a=Manifest.instances[i]; string key=a.meshIndex+"/"+a.materialIndex+"/"+a.layer+"/"+a.shadowCasting+"/"+a.receiveShadows;
                LayerMask|=1<<a.layer;
                List<int> indices; if(!groups.TryGetValue(key,out indices)) { indices=new List<int>(); groups.Add(key,indices); } indices.Add(i);
            }
            foreach(var indices in groups.Values) for(int first=0;first<indices.Count;first+=1023)
            {
                int count=Math.Min(1023,indices.Count-first); int[] source=indices.GetRange(first,count).ToArray(); var a=Manifest.instances[source[0]];
                list.Add(new Packet {Mesh=a.meshIndex,Material=a.materialIndex,Layer=a.layer,Shadow=a.shadowCasting,Receive=a.receiveShadows,Sources=source});
            }
            packets=list.ToArray(); StatePath=PathUnder(directory,Manifest.state);
            if(Hash(StatePath)!=Manifest.stateSha256) throw new InvalidDataException("Initial snapshot digest mismatch.");
            Publish(ResidentMaterialFormat.ReadSnapshot(StatePath)); StateSha256=Manifest.stateSha256;
            var l=Manifest.lighting; Vector3 forward=new Vector3(l.direction[0],l.direction[1],l.direction[2]);
            if(forward.sqrMagnitude<.000001f) throw new InvalidDataException("Directional light vector absent.");
            lightObject=new GameObject("Resident captured directional light"); lightObject.SetActive(false); var light=lightObject.AddComponent<Light>(); light.type=LightType.Directional;
            light.transform.rotation=Quaternion.LookRotation(forward,Mathf.Abs(Vector3.Dot(forward.normalized,Vector3.up))>.99f?Vector3.right:Vector3.up); light.color=C(l.color); light.intensity=l.intensity; light.shadows=(LightShadows)l.shadows; light.shadowStrength=l.shadowStrength; light.cullingMask=LayerMask;
            if(Hash(ManifestPath)!=ManifestSha256||Hash(BundlePath)!=BundleSha256||Hash(geometryPath)!=Manifest.geometrySha256||(OriginalBundlePath!=null&&Hash(OriginalBundlePath)!=OriginalBundleSha256)) throw new InvalidDataException("Fixture changed during construction.");
        }
        catch { Dispose(); throw; }
    }
    public void Activate(Camera camera,bool applyCamera)
    {
        if(disposed||activated) throw new InvalidOperationException("Resident generation activation state.");
        priorAmbient=RenderSettings.ambientMode; priorSky=RenderSettings.ambientSkyColor; priorEquator=RenderSettings.ambientEquatorColor; priorGround=RenderSettings.ambientGroundColor; priorAmbientIntensity=RenderSettings.ambientIntensity; priorShadowDistance=QualitySettings.shadowDistance; priorShadowCascades=QualitySettings.shadowCascades;
        activeCamera=camera; priorCameraMask=camera.cullingMask; priorPosition=camera.transform.position; priorRotation=camera.transform.rotation; priorFov=camera.fieldOfView; priorNear=camera.nearClipPlane; priorFar=camera.farClipPlane;
        activated=true; camera.cullingMask=LayerMask; var l=Manifest.lighting;
        if(Manifest.shaderGlobals!=null)
        {
            priorShaderGlobals=new ResidentMaterialProperty[Manifest.shaderGlobals.Length];
            for(int i=0;i<Manifest.shaderGlobals.Length;i++)
            {
                var p=Manifest.shaderGlobals[i];
                priorShaderGlobals[i]=new ResidentMaterialProperty {name=p.name,kind=p.kind,value=p.kind==2?new[] {Shader.GetGlobalFloat(p.name)}:new[] {Shader.GetGlobalVector(p.name).x,Shader.GetGlobalVector(p.name).y,Shader.GetGlobalVector(p.name).z,Shader.GetGlobalVector(p.name).w}};
                float[] values=GlobalValues(p); if(p.kind==2) Shader.SetGlobalFloat(p.name,values[0]); else Shader.SetGlobalVector(p.name,V(values));
            }
        }
        RenderSettings.ambientMode=(AmbientMode)l.ambientMode; RenderSettings.ambientSkyColor=C(l.ambientSky); RenderSettings.ambientEquatorColor=C(l.ambientEquator); RenderSettings.ambientGroundColor=C(l.ambientGround); RenderSettings.ambientIntensity=l.ambientIntensity; QualitySettings.shadowDistance=l.shadowDistance; QualitySettings.shadowCascades=l.shadowCascades;
        lightObject.SetActive(true);
        if(applyCamera) { camera.transform.SetPositionAndRotation(Root.MultiplyPoint3x4(new Vector3(Manifest.cameraPosition[0],Manifest.cameraPosition[1],Manifest.cameraPosition[2])),Quaternion.LookRotation(Root.GetColumn(2),Root.GetColumn(1))*Q(Manifest.cameraRotation)); camera.fieldOfView=Manifest.fov; camera.nearClipPlane=Manifest.near; camera.farClipPlane=Manifest.far; }
    }
    public void ValidateActivationTransferTo(ResidentMaterialRuntime next)
    {
        if(next==null||disposed||next.disposed||!activated) throw new InvalidOperationException("Live activation owners required.");
        var current=Manifest.shaderGlobals; var candidate=next.Manifest.shaderGlobals;
        if((current==null)!=(candidate==null)||(current!=null&&current.Length!=candidate.Length)) throw new InvalidOperationException("Shader global ownership differs between fixture generations.");
        if(current!=null) for(int i=0;i<current.Length;i++) if(current[i].name!=candidate[i].name||current[i].kind!=candidate[i].kind) throw new InvalidOperationException("Ordered shader global names/types differ between fixture generations.");
    }
    public void TransferActivationTo(ResidentMaterialRuntime next)
    {
        ValidateActivationTransferTo(next);
        if(!activated||!next.activated||activeCamera!=next.activeCamera) throw new InvalidOperationException("Activation transfer requires the same admitted camera.");
        if((priorShaderGlobals==null)!=(next.priorShaderGlobals==null)) throw new InvalidOperationException("Shader global ownership differs between fixture generations.");
        next.priorShaderGlobals=priorShaderGlobals;
        next.priorCameraMask=priorCameraMask; next.priorPosition=priorPosition; next.priorRotation=priorRotation; next.priorFov=priorFov; next.priorNear=priorNear; next.priorFar=priorFar;
        next.priorAmbient=priorAmbient; next.priorSky=priorSky; next.priorEquator=priorEquator; next.priorGround=priorGround; next.priorAmbientIntensity=priorAmbientIntensity; next.priorShadowDistance=priorShadowDistance; next.priorShadowCascades=priorShadowCascades;
        activated=false;
    }
    public void SetMode(string value) { if(value!="original"&&value!="reference"&&value!="instanced") throw new InvalidDataException("Material mode original|reference|instanced."); if(value=="original"&&originalMaterials==null) throw new InvalidDataException("Loaded original shader and bindings required."); Mode=value; }
    public void SetDiagnostic(string value) { if(value!="off"&&value!="defaults"&&value!="single") throw new InvalidDataException("Material diagnostic off|defaults|single."); Diagnostic=value; }
    public static int GeometryDebugMode(string value)
    {
        switch(value) { case "flat":return 0; case "world-normals":return 1; case "view-normals":return 2; case "albedo":return 3; case "vertex-colors":return 4; case "uv":return 5; case "material-id":return 6; default:throw new InvalidDataException("Unknown geometry debug mode."); }
    }
    public void SetGeometryDebug(string value)
    {
        if(value=="off") { GeometryDebug=value; return; }
        int mode=GeometryDebugMode(value);
        if(debugMaterials==null)
        {
            Shader shader=Shader.Find("Continuum/MultiscaleDiagnostic"); if(shader==null||!shader.isSupported) throw new InvalidDataException("Native geometry diagnostic shader unavailable.");
            CommitDebugShader(PrepareDebugShader(shader));
        }
        foreach(var m in debugMaterials) m.SetFloat("_Mode",mode); GeometryDebug=value;
    }
    public Material[] PrepareDebugShader(Shader shader)
    {
        var result=new Material[referenceMaterials.Length];
        try { for(int i=0;i<result.Length;i++) { var m=new Material(shader); result[i]=m; var original=referenceMaterials[i]; if(original.HasProperty("_MainTex")) { m.SetTexture("_MainTex",original.GetTexture("_MainTex")); m.SetTextureScale("_MainTex",original.GetTextureScale("_MainTex")); m.SetTextureOffset("_MainTex",original.GetTextureOffset("_MainTex")); } m.SetColor("_MaterialColor",Color.HSVToRGB((i*.61803398875f)%1,.7f,.95f)); m.SetFloat("_Mode",GeometryDebug=="off"?0:GeometryDebugMode(GeometryDebug)); } return result; }
        catch { foreach(var m in result) if(m!=null) UnityEngine.Object.Destroy(m); throw; }
    }
    public void CommitDebugShader(Material[] next)
    {
        if(debugMaterials!=null) foreach(var m in debugMaterials) { owned.Remove(m); UnityEngine.Object.Destroy(m); }
        debugMaterials=next; owned.AddRange(next);
    }
    public void SetTextureMipBias(float bias) { if(float.IsNaN(bias)||float.IsInfinity(bias)||bias < -2||bias>4) throw new InvalidDataException("Texture mip policy bound."); for(int i=0;i<textures.Length;i++) textures[i].mipMapBias=Manifest.textures[i].mipBias+bias; }
    public void IncludePresentationLightLayer(int layer) { if(layer<0||layer>31) throw new InvalidDataException("Presentation light layer bound."); lightObject.GetComponent<Light>().cullingMask|=1<<layer; }
    public void DisablePresentationShadows() { lightObject.GetComponent<Light>().shadows=LightShadows.None; }
    public void ValidateLoadedFiles() { if(Hash(ManifestPath)!=ManifestSha256||Hash(StatePath)!=StateSha256||Hash(BundlePath)!=BundleSha256||(OriginalBundlePath!=null&&Hash(OriginalBundlePath)!=OriginalBundleSha256)) throw new InvalidDataException("Resident loaded provenance files changed."); }
    public void ReloadSnapshot(string path)
    {
        if(!Path.IsPathRooted(path)) throw new InvalidDataException("Absolute snapshot required."); string before=Hash(path); var next=ResidentMaterialFormat.ReadSnapshot(path); ResidentMaterialFormat.ValidateSnapshot(Manifest,next);
        if(next.generation<=Generation) throw new InvalidDataException("Strictly newer snapshot generation required."); if(Hash(path)!=before) throw new InvalidDataException("Snapshot changed during read.");
        Publish(next); StatePath=Path.GetFullPath(path); StateSha256=before;
    }
    void Publish(ResidentSnapshot next)
    {
        ResidentMaterialFormat.ValidateSnapshot(Manifest,next); var nextMatrices=new Matrix4x4[next.instances.Length]; var blocks=new MaterialPropertyBlock[next.instances.Length]; var oneBlocks=new MaterialPropertyBlock[next.instances.Length]; var oneMatrices=new Matrix4x4[next.instances.Length][];
        Matrix4x4 placementRoot=Root; if(cameraRelativeRoot) { placementRoot.m03=placementRoot.m13=placementRoot.m23=0; }
        for(int i=0;i<next.instances.Length;i++) { nextMatrices[i]=placementRoot*M(next.instances[i].matrix); blocks[i]=Block(next,new[] {i},false); oneBlocks[i]=Block(next,new[] {i},true); oneMatrices[i]=new[] {nextMatrices[i]}; }
        var packetMatrices=new Matrix4x4[packets.Length][]; var packetBlocks=new MaterialPropertyBlock[packets.Length];
        for(int i=0;i<packets.Length;i++) { var p=packets[i]; packetMatrices[i]=new Matrix4x4[p.Sources.Length]; for(int j=0;j<p.Sources.Length;j++) packetMatrices[i][j]=nextMatrices[p.Sources[j]]; packetBlocks[i]=Block(next,p.Sources,true); }
        Bounds bounds=new Bounds(); bool first=true;
        for(int i=0;i<next.instances.Length;i++) { Bounds b=meshes[Manifest.instances[i].meshIndex].bounds; for(int corner=0;corner<8;corner++) { Vector3 local=b.center+Vector3.Scale(b.extents,new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1)); Vector3 world=nextMatrices[i].MultiplyPoint3x4(local); if(first) { bounds=new Bounds(world,Vector3.zero); first=false; } else bounds.Encapsulate(world); } }
        matrices=nextMatrices; presentationBaseMatrices=(Matrix4x4[])nextMatrices.Clone(); presentationBaseBounds=bounds; referenceBlocks=blocks; singleBlocks=oneBlocks; singleMatrices=oneMatrices; for(int i=0;i<packets.Length;i++) { packets[i].Matrices=packetMatrices[i]; packets[i].Block=packetBlocks[i]; } snapshot=next; Bounds=bounds; SnapshotPublications++; if(presentationAnchorRequested) SetPresentationAnchor(presentationAnchor);
    }
    public void SetPresentationAnchor(Vector3 anchor)
    {
        if(float.IsNaN(anchor.x)||float.IsInfinity(anchor.x)||float.IsNaN(anchor.y)||float.IsInfinity(anchor.y)||float.IsNaN(anchor.z)||float.IsInfinity(anchor.z)) throw new InvalidDataException("Finite camera-relative presentation anchor required.");
        presentationAnchor=anchor; presentationAnchorRequested=true; Vector3 offset=anchor-(cameraRelativeRoot?Vector3.zero:new Vector3(Root.m03,Root.m13,Root.m23));
        for(int i=0;i<matrices.Length;i++) { var m=presentationBaseMatrices[i]; m.m03+=offset.x; m.m13+=offset.y; m.m23+=offset.z; matrices[i]=m; singleMatrices[i][0]=m; }
        foreach(var p in packets) for(int i=0;i<p.Sources.Length;i++) p.Matrices[i]=matrices[p.Sources[i]];
        Bounds=presentationBaseBounds; var b=Bounds; b.center+=offset; Bounds=b;
    }
    public Vector3 PresentationInstanceCenter(string id,out float radius)
    {
        for(int i=0;i<Manifest.instances.Length;i++) if(Manifest.instances[i].id==id) { var b=meshes[Manifest.instances[i].meshIndex].bounds; var m=presentationBaseMatrices[i]; Vector3 e=b.extents; var worldExtents=new Vector3(Math.Abs(m.m00)*e.x+Math.Abs(m.m01)*e.y+Math.Abs(m.m02)*e.z,Math.Abs(m.m10)*e.x+Math.Abs(m.m11)*e.y+Math.Abs(m.m12)*e.z,Math.Abs(m.m20)*e.x+Math.Abs(m.m21)*e.y+Math.Abs(m.m22)*e.z); radius=worldExtents.magnitude; return m.MultiplyPoint3x4(b.center); }
        throw new InvalidDataException("Unknown captured instance identity.");
    }
    static MaterialPropertyBlock Block(ResidentSnapshot state,int[] ids,bool arrays)
    {
        int n=ids.Length; var block=new MaterialPropertyBlock(); var temperature=new Vector4[n]; var rim=new Vector4[n]; var emission=new Vector4[n]; var falloff=new float[n]; var opacity=new float[n]; var sh=new SphericalHarmonicsL2[n]; var occlusion=new Vector4[n];
        for(int i=0;i<n;i++) { var s=state.instances[ids[i]]; temperature[i]=V(s.temperature); rim[i]=V(s.rim); emission[i]=V(s.emission); falloff[i]=s.falloff; opacity[i]=s.opacityEffective; occlusion[i]=V(s.occlusion); for(int channel=0;channel<3;channel++) for(int c=0;c<9;c++) sh[i][channel,c]=s.sh[channel*9+c]; }
        if(arrays) { block.SetVectorArray("_TemperatureColor",temperature); block.SetVectorArray("_RimColor",rim); block.SetVectorArray("_EmissiveColor",emission); block.SetFloatArray("_RimFalloff",falloff); block.SetFloatArray("_Opacity",opacity); }
        else { block.SetVector("_TemperatureColor",temperature[0]); block.SetVector("_RimColor",rim[0]); block.SetVector("_EmissiveColor",emission[0]); block.SetFloat("_RimFalloff",falloff[0]); block.SetFloat("_Opacity",opacity[0]); }
        block.CopySHCoefficientArraysFrom(sh); block.CopyProbeOcclusionArrayFrom(occlusion);
        if(!arrays)
        {
            foreach(string name in new[] {"unity_SHAr","unity_SHAg","unity_SHAb","unity_SHBr","unity_SHBg","unity_SHBb","unity_SHC"})
            {
                Vector4[] packed=block.GetVectorArray(name); if(packed==null||packed.Length!=1) throw new InvalidDataException("Native SH coefficient packing unavailable: "+name); block.SetVector(name,packed[0]);
            }
            block.SetVector("unity_ProbesOcclusion",occlusion[0]);
        }
        return block;
    }
    public void Submit(Camera camera)
    {
        if(disposed||!activated||lastFrame==Time.frameCount) return; lastFrame=Time.frameCount;
        if(GeometryDebug!="off") for(int i=0;i<Manifest.instances.Length;i++) { var a=Manifest.instances[i]; Graphics.DrawMesh(meshes[a.meshIndex],matrices[i],debugMaterials[a.materialIndex],a.layer,camera,0,null,ShadowCastingMode.On,false,null,LightProbeUsage.Off,null); DrawCalls++; }
        else if(Mode=="reference"||Mode=="original") for(int i=0;i<Manifest.instances.Length;i++) { var a=Manifest.instances[i]; Graphics.DrawMesh(meshes[a.meshIndex],matrices[i],(Mode=="original"?originalMaterials:referenceMaterials)[a.materialIndex],a.layer,camera,0,referenceBlocks[i],(ShadowCastingMode)a.shadowCasting,a.receiveShadows,null,LightProbeUsage.CustomProvided,null); DrawCalls++; }
        else if(Diagnostic=="single") for(int i=0;i<Manifest.instances.Length;i++) { var a=Manifest.instances[i]; Graphics.DrawMeshInstanced(meshes[a.meshIndex],0,packetMaterials[a.materialIndex],singleMatrices[i],1,singleBlocks[i],(ShadowCastingMode)a.shadowCasting,a.receiveShadows,a.layer,camera,LightProbeUsage.CustomProvided,null); DrawCalls++; }
        else foreach(var p in packets) { Graphics.DrawMeshInstanced(meshes[p.Mesh],0,packetMaterials[p.Material],p.Matrices,p.Matrices.Length,Diagnostic=="defaults"?null:p.Block,(ShadowCastingMode)p.Shadow,p.Receive,p.Layer,camera,Diagnostic=="defaults"?LightProbeUsage.Off:LightProbeUsage.CustomProvided,null); DrawCalls++; }
        SubmittedFrames++;
    }
    public void Dispose()
    {
        if(disposed) return; disposed=true; if(lightObject!=null) { lightObject.SetActive(false); UnityEngine.Object.Destroy(lightObject); }
        if(activated) { if(priorShaderGlobals!=null) foreach(var p in priorShaderGlobals) { if(p.kind==2) Shader.SetGlobalFloat(p.name,p.value[0]); else Shader.SetGlobalVector(p.name,V(p.value)); } if(activeCamera!=null) { activeCamera.cullingMask=priorCameraMask; activeCamera.transform.SetPositionAndRotation(priorPosition,priorRotation); activeCamera.fieldOfView=priorFov; activeCamera.nearClipPlane=priorNear; activeCamera.farClipPlane=priorFar; } RenderSettings.ambientMode=priorAmbient; RenderSettings.ambientSkyColor=priorSky; RenderSettings.ambientEquatorColor=priorEquator; RenderSettings.ambientGroundColor=priorGround; RenderSettings.ambientIntensity=priorAmbientIntensity; QualitySettings.shadowDistance=priorShadowDistance; QualitySettings.shadowCascades=priorShadowCascades; }
        foreach(var value in owned) if(value!=null) UnityEngine.Object.Destroy(value);
        if(shaderLease!=null&&--shaderLease.References==0) { shaderLeases.Remove(BundleSha256); shaderLease.Bundle.Unload(true); }
        if(originalShaderLease!=null&&--originalShaderLease.References==0) { shaderLeases.Remove(OriginalBundleSha256); originalShaderLease.Bundle.Unload(true); }
    }
}
