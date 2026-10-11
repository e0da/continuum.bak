using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using KspContinuum;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class WholeCraftRuntime : IDisposable
{
    public readonly WholeCraftSceneManifest Manifest;
    public readonly string ManifestSha256,GeometrySha256;
    readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
    Mesh[] meshes; Texture2D[] textures; Material[] materials; Matrix4x4[] bases,matrices;
    bool[] visibility; Color[] retainedPixels;
    public long ReplayFrameCount { get; private set; }
    public long ReplayApplyTicks { get; private set; }
    public long DynamicUploadBytes { get; private set; }
    public int DynamicUploadCount { get; private set; }
    public bool ReplayGpuFaulted { get; private set; }
    sealed class Recipe { public int Texture=-1; public Vector4 ST=new Vector4(1,1,0,0); public Color Tint=Color.white; }
    sealed class Packet { public int Mesh,Submesh,Layer,Recipe; public int[] Sources; public Matrix4x4[] Matrices; public MaterialPropertyBlock Block; }
    Recipe[] recipes; int[] materialRecipes; Packet[] packets; MaterialPropertyBlock[] referenceColors;
    Texture2D retainedState; CommandBuffer retainedCommands; RenderTexture retainedColor,retainedDepth; int retainedRows,retainedCalls; Matrix4x4 retainedView,retainedProjection; bool viewSet;
    static readonly int[] viewRows={Shader.PropertyToID("_ContinuumRetainedView0"),Shader.PropertyToID("_ContinuumRetainedView1"),Shader.PropertyToID("_ContinuumRetainedView2"),Shader.PropertyToID("_ContinuumRetainedView3")};
    static readonly int[] vpRows={Shader.PropertyToID("_ContinuumRetainedVP0"),Shader.PropertyToID("_ContinuumRetainedVP1"),Shader.PropertyToID("_ContinuumRetainedVP2"),Shader.PropertyToID("_ContinuumRetainedVP3")};
    public int NativeExecuteCount { get; private set; }
    public int ViewUniformUpdateCount { get; private set; }
    public int StateUploadCount { get; private set; }
    public long StateUploadBytes { get; private set; }
    public int StateTextureId { get { return retainedState==null?0:retainedState.GetInstanceID(); } }
    public int CommandBuildCount { get; private set; }
    public int RetainedCommandCount { get { return retainedCalls; } }
    public string RetainedScope { get { return Retained(RenderMode)?"Cold GPU affine/normal/hue state and explicit MRT commands; no craft Camera.Render, per-row frustum culling or shadow consumer; submission ticks cover owner uniforms/ExecuteCommandBuffer enqueue only":"Legacy managed DrawMesh/DrawMeshInstanced enqueue envelope; automatic native preparation/depth costs remain within complete-frame timing"; } }
    public string RenderMode { get; private set; }="reference";
    public int RecipeCount { get { return recipes.Length; } }
    public int PacketCount { get { return packets.Length; } }
    public long PreparationTicks { get; private set; }
    public long SubmissionTicks { get; private set; }
    public int LastSubmissionCalls { get; private set; }
    Bounds baseBounds;
    public Bounds Bounds { get; private set; }
    public int LayerMask { get; private set; }
    public int DrawCalls { get; private set; }
    public int SubmittedFrames { get; private set; }
    public int MeshCount { get { return meshes.Length; } }
    public int ForceRenderingOffObserved,PropertyBlockObserved,FrozenSkinObserved;
    public const long RawTexturePayloadBudgetBytes=1073741824;
    public long RawTexturePayloadBytes { get; private set; }
    public int MissingNormals,MissingUV,DefaultWhiteColors,UnknownCull,ApproximateTransparency,NoAlbedoTexture,NonvisibleDraws;
    public string DebugMode { get; private set; }="off";
    bool anchorSet,biasSet; Vector3 lastAnchor; float lastBias;
    int lastFrame=-1; bool disposed;
    static string Hash(byte[] bytes) { using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
    static string Under(string root,string path) { string prefix=Path.GetFullPath(root)+Path.DirectorySeparatorChar,result=Path.GetFullPath(Path.Combine(root,path)); if(Path.IsPathRooted(path)||!result.StartsWith(prefix,StringComparison.Ordinal)) throw new InvalidDataException("Whole-craft payload escapes root."); return result; }
    static bool Finite(float v) { return !float.IsNaN(v)&&!float.IsInfinity(v); }
    static void Check(Matrix4x4 m) { for(int r=0;r<4;r++) for(int c=0;c<4;c++) if(!Finite(m[r,c])) throw new InvalidDataException("Whole-craft float matrix nonrepresentable."); }
    static void Check(Vector3 v) { if(!Finite(v.x)||!Finite(v.y)||!Finite(v.z)) throw new InvalidDataException("Whole-craft float position nonrepresentable."); }
    static Matrix4x4 Matrix(float[] a) { var m=new Matrix4x4(); for(int r=0;r<4;r++) for(int c=0;c<4;c++) m[r,c]=a[r*4+c]; Check(m); return m; }
    static Matrix4x4 Matrix(double[] a) { var m=new Matrix4x4(); for(int r=0;r<4;r++) for(int c=0;c<4;c++) { double v=a[r*4+c]; if(double.IsNaN(v)||double.IsInfinity(v)||v>float.MaxValue||v < -float.MaxValue) throw new InvalidDataException("Whole-craft double pose not representable in float."); float value=(float)v; if(v!=0&&value==0) throw new InvalidDataException("Whole-craft double pose underflows float."); m[r,c]=value; } Check(m); return m; }
    static byte[] ReadBounded(string path,long expected,long budget)
    {
        if(expected<=0||expected>budget||expected>int.MaxValue) throw new InvalidDataException("Whole-craft payload allocation bound.");
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) { if(stream.Length!=expected) throw new InvalidDataException("Whole-craft payload length differs before allocation."); var bytes=new byte[(int)expected]; int offset=0; while(offset<bytes.Length) { int n=stream.Read(bytes,offset,bytes.Length-offset); if(n==0) throw new InvalidDataException("Whole-craft payload truncated."); offset+=n; } if(stream.ReadByte()!=-1||stream.Length!=expected) throw new InvalidDataException("Whole-craft payload grew during read."); return bytes; }
    }
    public WholeCraftRuntime(string path,Shader shader)
    {
        long preparationStart=System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if(new FileInfo(path).Length>16*1024*1024) throw new InvalidDataException("Whole-craft manifest bound."); byte[] json=ReadBounded(path,new FileInfo(path).Length,16*1024*1024); ManifestSha256=Hash(json); Manifest=JsonUtility.FromJson<WholeCraftSceneManifest>(System.Text.Encoding.UTF8.GetString(json)); string root=Path.GetDirectoryName(path),geometryPath=Under(root,Manifest.geometry); GeometrySha256=ResidentMaterialRuntime.Hash(geometryPath); if(GeometrySha256!=Manifest.geometrySha256) throw new InvalidDataException("Whole geometry digest mismatch."); var g=WholeCraftSceneFormat.ReadGeometry(geometryPath); WholeCraftSceneFormat.Validate(Manifest,g); if(ResidentMaterialRuntime.Hash(geometryPath)!=GeometrySha256) throw new InvalidDataException("Whole geometry changed during read.");
            if(Manifest.engine!=Application.unityVersion||Manifest.colorSpace!=QualitySettings.activeColorSpace.ToString()) throw new InvalidDataException("Whole-craft engine/color space mismatch.");
            meshes=new Mesh[g.meshes.Length];
            for(int k=0;k<meshes.Length;k++)
            {
                var a=g.meshes[k]; int n=a.positions.Length/3; var p=new Vector3[n]; var normal=new Vector3[n]; var uv=new Vector2[n]; var colors=new Color[n]; var tangent=new Vector4[n];
                if(a.normals.Length==0) MissingNormals++; if(a.uv.Length==0) MissingUV++; if(a.colors.Length==0) DefaultWhiteColors++;
                for(int i=0;i<n;i++) { p[i]=new Vector3(a.positions[i*3],a.positions[i*3+1],a.positions[i*3+2]); if(a.normals.Length!=0) normal[i]=new Vector3(a.normals[i*3],a.normals[i*3+1],a.normals[i*3+2]); if(a.uv.Length!=0) uv[i]=new Vector2(a.uv[i*2],a.uv[i*2+1]); colors[i]=a.colors.Length==0?Color.white:new Color(a.colors[i*4],a.colors[i*4+1],a.colors[i*4+2],a.colors[i*4+3]); if(a.tangents.Length!=0) tangent[i]=new Vector4(a.tangents[i*4],a.tangents[i*4+1],a.tangents[i*4+2],a.tangents[i*4+3]); }
                var mesh=new Mesh {indexFormat=IndexFormat.UInt32,vertices=p,normals=normal,uv=uv,colors=colors,tangents=tangent,subMeshCount=a.submeshes.Length}; owned.Add(mesh); for(int s=0;s<a.submeshes.Length;s++) mesh.SetTriangles(a.submeshes[s].indices,s); mesh.RecalculateBounds(); meshes[k]=mesh;
            }
            foreach(var asset in Manifest.textures) { RawTexturePayloadBytes=checked(RawTexturePayloadBytes+asset.payloadLength); if(RawTexturePayloadBytes>RawTexturePayloadBudgetBytes) throw new InvalidDataException("Whole-craft aggregate raw texture budget exceeded."); }
            textures=new Texture2D[Manifest.textures.Length];
            for(int i=0;i<textures.Length;i++) { var a=Manifest.textures[i]; byte[] bytes=ReadBounded(Under(root,a.payload),a.payloadLength,RawTexturePayloadBudgetBytes); if(bytes.LongLength!=a.payloadLength||Hash(bytes)!=a.payloadSha256) throw new InvalidDataException("Whole texture digest mismatch."); var t=new Texture2D(a.width,a.height,(TextureFormat)a.format,a.mipCount>1,a.linear); owned.Add(t); t.LoadRawTextureData(bytes); t.Apply(false,true); t.filterMode=(FilterMode)a.filter; t.wrapMode=(TextureWrapMode)a.wrap; t.anisoLevel=a.aniso; t.mipMapBias=a.mipBias; var prop=typeof(Texture).GetProperty("graphicsFormat"); if(t.mipmapCount!=a.mipCount||t.format!=(TextureFormat)a.format||prop==null||Convert.ToInt32(prop.GetValue(t,null))!=a.graphicsFormat) throw new InvalidDataException("Whole texture native format mismatch."); textures[i]=t; }
            bases=new Matrix4x4[Manifest.draws.Length]; matrices=new Matrix4x4[bases.Length]; Matrix4x4 rootMatrix=Matrix(Manifest.rootToWorld); rootMatrix.m03=rootMatrix.m13=rootMatrix.m23=0; bool first=true;
            for(int i=0;i<bases.Length;i++) { var a=Manifest.draws[i]; matrices[i]=bases[i]=rootMatrix*Matrix(a.matrix); Check(bases[i]); if(!a.enabled||!a.active||a.forceRenderingOff) { NonvisibleDraws++; continue; } LayerMask|=1<<a.layer; Bounds b=meshes[a.meshIndex].bounds; for(int c=0;c<8;c++) { Vector3 v=bases[i].MultiplyPoint3x4(b.center+Vector3.Scale(b.extents,new Vector3((c&1)==0?-1:1,(c&2)==0?-1:1,(c&4)==0?-1:1))); Check(v); if(first) { baseBounds=new Bounds(v,Vector3.zero); first=false; } else baseBounds.Encapsulate(v); } }
            foreach(var row in Manifest.draws) { if(row.forceRenderingOff) ForceRenderingOffObserved++; if(row.propertyBlockPresent) PropertyBlockObserved++; if(row.frozenSkin) FrozenSkinObserved++; }
            if(first) throw new InvalidDataException("Whole fixture has no visible captured rows."); Check(baseBounds.center); Check(baseBounds.extents); Bounds=baseBounds; BuildRecipes(); BuildPackets(); materials=PrepareDebugShader(shader); owned.AddRange(materials); PreparationTicks=System.Diagnostics.Stopwatch.GetTimestamp()-preparationStart;
        }
        catch { Dispose(); throw; }
    }
    static string Bits(float v) { return BitConverter.ToInt32(BitConverter.GetBytes(v),0).ToString("x8"); }
    void BuildRecipes()
    {
        var index=new Dictionary<string,int>(StringComparer.Ordinal); var list=new List<Recipe>(); materialRecipes=new int[Manifest.materials.Length];
        for(int i=0;i<Manifest.materials.Length;i++)
        {
            var a=Manifest.materials[i]; var recipe=new Recipe(); bool cull=false;
            foreach(var p in a.properties) { if(p.name=="_MainTex"&&p.kind==4) { recipe.Texture=p.textureIndex; recipe.ST=new Vector4(p.scaleOffset[0],p.scaleOffset[1],p.scaleOffset[2],p.scaleOffset[3]); } if((p.name=="_Color"||p.name=="_BurnColor")&&(p.kind==0||p.kind==1)) recipe.Tint=new Color(p.value[0],p.value[1],p.value[2],p.value[3]); if((p.name=="_Cull"||p.name=="_CullMode")&&(p.kind==2||p.kind==3)&&p.value[0]>=0&&p.value[0]<=2&&p.value[0]==(int)p.value[0]) cull=true; }
            if(recipe.Texture<0) NoAlbedoTexture++; if(!cull) UnknownCull++; if(a.queue>=2450) ApproximateTransparency++;
            string key="opaque-two-sided-v1/"+recipe.Texture+"/"+Bits(recipe.ST.x)+Bits(recipe.ST.y)+Bits(recipe.ST.z)+Bits(recipe.ST.w)+"/"+Bits(recipe.Tint.r)+Bits(recipe.Tint.g)+Bits(recipe.Tint.b)+Bits(recipe.Tint.a);
            int id; if(!index.TryGetValue(key,out id)) { id=list.Count; list.Add(recipe); index.Add(key,id); } materialRecipes[i]=id;
        }
        recipes=list.ToArray();
    }
    void BuildPackets()
    {
        visibility=new bool[Manifest.draws.Length]; for(int i=0;i<visibility.Length;i++) visibility[i]=Manifest.draws[i].enabled&&Manifest.draws[i].active&&!Manifest.draws[i].forceRenderingOff;
        referenceColors=new MaterialPropertyBlock[Manifest.draws.Length]; var groups=new Dictionary<string,List<int>>(StringComparer.Ordinal); var keys=new List<string>();
        for(int i=0;i<Manifest.draws.Length;i++) { var a=Manifest.draws[i]; var color=Color.HSVToRGB((a.materialIndex*.61803398875f)%1,.7f,.95f); var block=new MaterialPropertyBlock(); block.SetColor("_MaterialColor",color); referenceColors[i]=block; if(!a.enabled||!a.active||a.forceRenderingOff) continue; string key=a.meshIndex+"/"+a.submeshIndex+"/"+a.layer+"/"+materialRecipes[a.materialIndex]; List<int> sources; if(!groups.TryGetValue(key,out sources)) { sources=new List<int>(); groups.Add(key,sources); keys.Add(key); } sources.Add(i); }
        var list=new List<Packet>(); foreach(var key in keys) { var sources=groups[key]; int start=0; foreach(int count in PacketSlices(sources.Count)) { var first=Manifest.draws[sources[start]]; var packet=new Packet {Mesh=first.meshIndex,Submesh=first.submeshIndex,Layer=first.layer,Recipe=materialRecipes[first.materialIndex],Sources=new int[count],Matrices=new Matrix4x4[count],Block=new MaterialPropertyBlock()}; var colors=new Vector4[count]; for(int j=0;j<count;j++) { int source=sources[start+j]; packet.Sources[j]=source; packet.Matrices[j]=matrices[source]; colors[j]=Color.HSVToRGB((Manifest.draws[source].materialIndex*.61803398875f)%1,.7f,.95f); } packet.Block.SetVectorArray("_MaterialColor",colors); list.Add(packet); start+=count; } } packets=list.ToArray();
    }
    public static int[] PacketSlices(int sources) { if(sources<0||sources>50000) throw new InvalidDataException("Packet source capacity differs."); var result=new int[(sources+1022)/1023]; for(int i=0;i<result.Length;i++) result[i]=Math.Min(1023,sources-i*1023); return result; }
    Packet[] ReplayPackets(Matrix4x4[] poses,bool[] visible)
    {
        var groups=new Dictionary<string,List<int>>(StringComparer.Ordinal); var keys=new List<string>();
        for(int i=0;i<visible.Length;i++) if(visible[i]) { var d=Manifest.draws[i]; string key=d.meshIndex+"/"+d.submeshIndex+"/"+d.layer+"/"+materialRecipes[d.materialIndex]; List<int> rows; if(!groups.TryGetValue(key,out rows)) { rows=new List<int>(); groups.Add(key,rows); keys.Add(key); } rows.Add(i); }
        var result=new List<Packet>();
        foreach(string key in keys) { var rows=groups[key]; int start=0; foreach(int count in PacketSlices(rows.Count)) { var d=Manifest.draws[rows[start]]; var p=new Packet {Mesh=d.meshIndex,Submesh=d.submeshIndex,Layer=d.layer,Recipe=materialRecipes[d.materialIndex],Sources=new int[count],Matrices=new Matrix4x4[count],Block=new MaterialPropertyBlock()}; var hues=new Vector4[count]; for(int k=0;k<count;k++) { int i=rows[start+k]; p.Sources[k]=i; p.Matrices[k]=poses[i]; hues[k]=Color.HSVToRGB((Manifest.draws[i].materialIndex*.61803398875f)%1,.7f,.95f); } p.Block.SetVectorArray("_MaterialColor",hues); result.Add(p); start+=count; } }
        return result.ToArray();
    }
    Color[] ReplayPixels(Matrix4x4[] poses,Packet[] groups)
    {
        var pixels=new Color[Manifest.draws.Length*8]; int row=0;
        foreach(var p in groups) foreach(int i in p.Sources) { var m=poses[i]; if(!Finite(m.determinant)||m.determinant==0) throw new InvalidDataException("Replay inverse singular."); var n=m.inverse.transpose; Check(n); for(int r=0;r<4;r++) { var v=m.GetRow(r); pixels[row*8+r]=new Color(v.x,v.y,v.z,v.w); } for(int r=0;r<3;r++) { var v=n.GetRow(r); pixels[row*8+4+r]=new Color(v.x,v.y,v.z,0); } pixels[row*8+7]=Color.HSVToRGB((Manifest.draws[i].materialIndex*.61803398875f)%1,.7f,.95f); row++; }
        return pixels;
    }
    public void ApplyReplayFrame(VesselRuntimeFrame frame)
    {
        long start=System.Diagnostics.Stopwatch.GetTimestamp();
        VesselRuntimeFormat.ValidateFrame(frame,Manifest.draws.Length,frame==null||frame.loads==null?0:frame.loads.Length/6);
        if(disposed||frame==null||frame.poses==null||frame.poses.Length!=bases.Length*16||frame.visible==null||frame.visible.Length!=bases.Length||frame.rootToWorld==null||frame.rootToWorld.Length!=16) throw new InvalidDataException("Replay frame shape differs.");
        var root=Matrix(frame.rootToWorld); if(root.m03!=0||root.m13!=0||root.m23!=0) throw new InvalidDataException("Replay root translation must be zero.");
        var nextBases=new Matrix4x4[bases.Length]; var nextMatrices=new Matrix4x4[bases.Length]; var visible=(bool[])frame.visible.Clone(); bool changed=false,first=true; int mask=0,hidden=0; var bounds=new Bounds(Vector3.zero,Vector3.zero);
        for(int i=0;i<bases.Length;i++)
        {
            var pose=new Matrix4x4(); for(int r=0;r<4;r++) for(int c=0;c<4;c++) { double value=frame.poses[i*16+r*4+c]; if(double.IsNaN(value)||double.IsInfinity(value)||value>float.MaxValue||value < -float.MaxValue||(value!=0&&(float)value==0)) throw new InvalidDataException("Replay pose is not representable."); pose[r,c]=(float)value; }
            var m=root*pose; Check(m); if(m.m30!=0||m.m31!=0||m.m32!=0||m.m33!=1||!Finite(m.determinant)||m.determinant==0) throw new InvalidDataException("Replay pose is not invertible affine."); nextBases[i]=m; m.m03+=lastAnchor.x; m.m13+=lastAnchor.y; m.m23+=lastAnchor.z; Check(m); nextMatrices[i]=m;
            changed|=visible[i]!=visibility[i]; if(!visible[i]) { hidden++; continue; } var d=Manifest.draws[i]; mask|=1<<d.layer; var b=meshes[d.meshIndex].bounds;
            for(int c=0;c<8;c++) { Vector3 p=nextBases[i].MultiplyPoint3x4(b.center+Vector3.Scale(b.extents,new Vector3((c&1)==0?-1:1,(c&2)==0?-1:1,(c&4)==0?-1:1))); Check(p); if(first) { bounds=new Bounds(p,Vector3.zero); first=false; } else bounds.Encapsulate(p); }
        }
        Check(bounds.center); Check(bounds.extents); var publishedBounds=bounds; publishedBounds.center+=lastAnchor; Check(publishedBounds.center);
        var groups=changed?ReplayPackets(nextMatrices,visible):packets; var pixels=ReplayPixels(nextBases,groups); CommandBuffer nextCommands=null; int calls=0;
        try
        {
            if(changed&&Retained(RenderMode)&&retainedColor!=null) nextCommands=PrepareCommands(materials,RenderMode,out calls,groups);
            if(retainedState!=null)
            {
                try { retainedState.SetPixels(pixels); retainedState.Apply(false,false); }
                catch { try { retainedState.SetPixels(retainedPixels); retainedState.Apply(false,false); } catch(Exception rollback) { ReplayGpuFaulted=true; throw new InvalidOperationException("Replay GPU rollback failed; renderer must stop.",rollback); } throw; }
            }
            if(changed) { ReplaceCommands(nextCommands,calls); nextCommands=null; }
            bases=nextBases; matrices=nextMatrices; visibility=visible; packets=groups; foreach(var p in packets) for(int k=0;k<p.Sources.Length;k++) p.Matrices[k]=matrices[p.Sources[k]];
            baseBounds=bounds; Bounds=publishedBounds; LayerMask=mask; NonvisibleDraws=hidden;
            if(retainedState!=null) { retainedPixels=pixels; DynamicUploadCount++; DynamicUploadBytes+=(long)pixels.Length*16; }
            ReplayFrameCount++; ReplayApplyTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-start;
        }
        finally { if(nextCommands!=null) nextCommands.Release(); }
    }
    static bool Retained(string mode) { return mode=="retained-scalar"||mode=="retained-grouped"; }
    public void SetRenderMode(string mode)
    {
        if(mode!="reference"&&mode!="grouped"&&!Retained(mode)) throw new InvalidDataException("Whole render mode reference|grouped|retained-scalar|retained-grouped required.");
        if(mode!="reference"&&!SystemInfo.supportsInstancing) throw new InvalidDataException("Native instancing unavailable."); if(mode==RenderMode) return;
        CommandBuffer next=null; int calls=0; if(Retained(mode)) { ValidateRetainedMaterials(materials); if(SystemInfo.supportedRenderTargetCount<2) throw new InvalidDataException("Native two-target rendering unavailable."); EnsureRetainedState(); if(retainedColor!=null) next=PrepareCommands(materials,mode,out calls); }
        try { ReplaceCommands(next,calls); } catch { if(next!=null) next.Release(); throw; } RenderMode=mode;
    }
    void ValidateRetainedMaterials(Material[] table)
    {
        for(int i=Manifest.materials.Length;i<table.Length;i++)
        {
            var m=table[i]; foreach(string property in new[] {"_ContinuumRetainedContract","_ContinuumRetained","_ContinuumState","_ContinuumStateShape","_ContinuumStateBase","_ContinuumRetainedAnchor"}) if(m==null||!m.HasProperty(property)) throw new InvalidDataException("Retained diagnostic shader interface absent: "+property);
            if(m.GetFloat("_ContinuumRetainedContract")!=2||!m.HasProperty("_ContinuumExplicitViewContract")||m.GetFloat("_ContinuumExplicitViewContract")!=1) throw new InvalidDataException("Retained explicit-view shader contract differs.");
            for(int r=0;r<4;r++) if(!m.HasProperty("_ContinuumRetainedVP"+r)||!m.HasProperty("_ContinuumRetainedView"+r)) throw new InvalidDataException("Retained shader view rows absent.");
        }
    }
    public void ConfigureRetainedTargets(RenderTexture color,RenderTexture positiveDepth)
    {
        if(color==null||positiveDepth==null||!color.IsCreated()||!positiveDepth.IsCreated()||color.width!=positiveDepth.width||color.height!=positiveDepth.height||color.depth<16||color.antiAliasing!=1||positiveDepth.antiAliasing!=1||positiveDepth.format!=RenderTextureFormat.RFloat||SystemInfo.supportedRenderTargetCount<2) throw new InvalidDataException("Retained MRT requires matching single-sample color/depth targets and native two-target support.");
        if(retainedColor==color&&retainedDepth==positiveDepth) return;
        var oldColor=retainedColor; var oldDepth=retainedDepth; retainedColor=color; retainedDepth=positiveDepth;
        try { if(Retained(RenderMode)) { int calls; var next=PrepareCommands(materials,RenderMode,out calls); try { ReplaceCommands(next,calls); } catch { next.Release(); throw; } } }
        catch { retainedColor=oldColor; retainedDepth=oldDepth; throw; }
    }
    void EnsureRetainedState()
    {
        if(retainedState!=null) return;
        int rows=Manifest.draws.Length;
        if(rows<=0||rows>SystemInfo.maxTextureSize||SystemInfo.maxTextureSize<8||!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat)) throw new InvalidDataException("Bounded vertex state texture unavailable.");
        var pixels=new Color[rows*8]; int row=0;
        foreach(var p in packets) foreach(int source in p.Sources)
        {
            var m=bases[source]; Check(m); if(!Finite(m.determinant)||m.determinant==0) throw new InvalidDataException("Retained affine inverse singular."); var n=m.inverse.transpose; Check(n);
            for(int r=0;r<4;r++) { var v=m.GetRow(r); pixels[row*8+r]=new Color(v.x,v.y,v.z,v.w); }
            for(int r=0;r<3;r++) { var v=n.GetRow(r); pixels[row*8+4+r]=new Color(v.x,v.y,v.z,0); }
            pixels[row*8+7]=Color.HSVToRGB((Manifest.draws[source].materialIndex*.61803398875f)%1,.7f,.95f); row++;
        }
        Texture2D texture=null;
        try { texture=new Texture2D(8,rows,TextureFormat.RGBAFloat,false,true) {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,anisoLevel=0}; texture.SetPixels(pixels); texture.Apply(false,false); retainedPixels=pixels; retainedState=texture; retainedRows=rows; owned.Add(texture); StateUploadCount++; StateUploadBytes+=(long)rows*8*16; }
        catch { if(texture!=null) UnityEngine.Object.Destroy(texture); throw; }
    }
    CommandBuffer PrepareCommands(Material[] table,string mode,out int calls,Packet[] candidates=null)
    {
        calls=0; var commands=new CommandBuffer {name="Retained whole diagnostic "+mode};
        try
        {
            if(retainedColor==null||retainedDepth==null) throw new InvalidDataException("Retained targets must be configured before command preparation.");
            commands.SetRenderTarget(new[] {new RenderTargetIdentifier(retainedColor),new RenderTargetIdentifier(retainedDepth)},new RenderTargetIdentifier(retainedColor));
            commands.SetViewport(new Rect(0,0,retainedColor.width,retainedColor.height));
            commands.ClearRenderTarget(true,true,Color.clear);
            var identity=new Matrix4x4[1023]; for(int i=0;i<identity.Length;i++) identity[i]=Matrix4x4.identity; int row=0;
            foreach(var p in candidates??packets)
            {
                var material=table[Manifest.materials.Length+p.Recipe]; if(!material.HasProperty("_ContinuumRetainedContract")||material.GetFloat("_ContinuumRetainedContract")!=2) throw new InvalidDataException("Retained explicit-view shader contract absent.");
                int stride=mode=="retained-grouped"?p.Sources.Length:1;
                for(int i=0;i<p.Sources.Length;i+=stride)
                {
                    var block=new MaterialPropertyBlock(); block.SetFloat("_ContinuumRetained",1); block.SetTexture("_ContinuumState",retainedState); block.SetVector("_ContinuumStateShape",new Vector4(8,retainedRows,0,0)); block.SetFloat("_ContinuumStateBase",row+i);
                    commands.DrawMeshInstanced(meshes[p.Mesh],p.Submesh,material,0,identity,stride,block); calls++;
                }
                row+=p.Sources.Length;
            }
            CommandBuildCount++; return commands;
        }
        catch { commands.Release(); throw; }
    }
    void ReplaceCommands(CommandBuffer next,int calls)
    {
        if(retainedCommands!=null) retainedCommands.Release(); retainedCommands=next; retainedCalls=calls;
    }
    public Material[] PrepareDebugShader(Shader shader)
    {
        if(Retained(RenderMode)) { var probe=new Material(shader); try { if(!probe.HasProperty("_ContinuumRetainedContract")||probe.GetFloat("_ContinuumRetainedContract")!=2||!probe.HasProperty("_ContinuumExplicitViewContract")||probe.GetFloat("_ContinuumExplicitViewContract")!=1) throw new InvalidDataException("Staged shader lacks retained explicit-view contract."); } finally { UnityEngine.Object.Destroy(probe); } }
        var next=new Material[Manifest.materials.Length+recipes.Length];
        try { for(int i=0;i<next.Length;i++) { bool grouped=i>=Manifest.materials.Length; var recipe=recipes[grouped?i-Manifest.materials.Length:materialRecipes[i]]; var m=new Material(shader) {enableInstancing=grouped}; next[i]=m; m.SetFloat("_Mode",DebugMode=="off"?7:ResidentMaterialRuntime.GeometryDebugMode(DebugMode)); m.SetFloat("_Cull",0); m.SetColor("_Tint",recipe.Tint); m.SetVector("_ContinuumRetainedAnchor",lastAnchor); if(recipe.Texture>=0) m.SetTexture("_MainTex",textures[recipe.Texture]); m.SetTextureScale("_MainTex",new Vector2(recipe.ST.x,recipe.ST.y)); m.SetTextureOffset("_MainTex",new Vector2(recipe.ST.z,recipe.ST.w)); } if(viewSet) BindView(next); return next; }
        catch { foreach(var m in next) if(m!=null) UnityEngine.Object.Destroy(m); throw; }
    }
    public sealed class DebugShaderCommit : IDisposable
    {
        internal WholeCraftRuntime Owner; internal Material[] Table; internal CommandBuffer Commands; internal int Calls; internal string Mode; internal bool Used,Disposed;
        public void Dispose() { if(Disposed) return; Disposed=true; if(Commands!=null) Commands.Release(); Commands=null; }
    }
    // The caller owns staged materials until commit; disposing a rejected lease releases commands only.
    public DebugShaderCommit StageDebugShaderCommit(Material[] next)
    {
        if(disposed||next==null||next.Length!=Manifest.materials.Length+recipes.Length) throw new InvalidDataException("Staged whole debug material table differs.");
        var stage=new DebugShaderCommit {Owner=this,Table=next,Mode=RenderMode};
        try { if(Retained(RenderMode)) { ValidateRetainedMaterials(next); stage.Commands=PrepareCommands(next,RenderMode,out stage.Calls); } return stage; }
        catch { stage.Dispose(); throw; }
    }
    public void CommitDebugShader(DebugShaderCommit stage)
    {
        if(disposed||stage==null||stage.Owner!=this||stage.Used||stage.Disposed||stage.Mode!=RenderMode) throw new InvalidDataException("Whole shader commit lease is stale or foreign.");
        ReplaceCommands(stage.Commands,stage.Calls); stage.Commands=null; stage.Used=true;
        if(materials!=null) foreach(var m in materials) { owned.Remove(m); UnityEngine.Object.Destroy(m); } materials=stage.Table; owned.AddRange(materials);
    }
    public void CommitDebugShader(Material[] next)
    {
        using(var stage=StageDebugShaderCommit(next)) CommitDebugShader(stage);
    }
    public void SetGeometryDebug(string value) { int mode=value=="off"?7:ResidentMaterialRuntime.GeometryDebugMode(value); foreach(var m in materials) m.SetFloat("_Mode",mode); DebugMode=value; }
    public void SetTextureMipBias(float bias) { if(!Finite(bias)) throw new InvalidDataException("Finite whole mip bias required."); if(biasSet&&bias==lastBias) return; for(int i=0;i<textures.Length;i++) textures[i].mipMapBias=Manifest.textures[i].mipBias+bias; lastBias=bias; biasSet=true; }
    public void SetPresentationAnchor(Vector3 anchor)
    {
        Check(anchor); if(anchorSet&&anchor.x==lastAnchor.x&&anchor.y==lastAnchor.y&&anchor.z==lastAnchor.z) return; var bounds=baseBounds; bounds.center+=anchor; Check(bounds.center); Check(bounds.extents);
        for(int i=0;i<matrices.Length;i++) { var candidate=bases[i]; candidate.m03+=anchor.x; candidate.m13+=anchor.y; candidate.m23+=anchor.z; Check(candidate); }
        for(int i=0;i<matrices.Length;i++) { var m=bases[i]; m.m03+=anchor.x; m.m13+=anchor.y; m.m23+=anchor.z; matrices[i]=m; } foreach(var packet in packets) for(int i=0;i<packet.Sources.Length;i++) packet.Matrices[i]=matrices[packet.Sources[i]]; foreach(var m in materials) m.SetVector("_ContinuumRetainedAnchor",anchor); Bounds=bounds; lastAnchor=anchor; anchorSet=true;
    }
    public Vector3 PresentationInstanceCenter(string id,out float radius) { for(int i=0;i<Manifest.draws.Length;i++) if(Manifest.draws[i].id==id) { var b=meshes[Manifest.draws[i].meshIndex].bounds; var m=bases[i]; radius=(m.GetColumn(0).magnitude*b.extents.x+m.GetColumn(1).magnitude*b.extents.y+m.GetColumn(2).magnitude*b.extents.z); return m.MultiplyPoint3x4(b.center); } throw new InvalidDataException("Unknown whole-craft row identity."); }
    static bool Same(Matrix4x4 a,Matrix4x4 b) { for(int r=0;r<4;r++) for(int c=0;c<4;c++) if(a[r,c]!=b[r,c]) return false; return true; }
    void BindView(Material[] table)
    {
        var vp=retainedProjection*retainedView; Check(vp);
        for(int i=Manifest.materials.Length;i<table.Length;i++) for(int r=0;r<4;r++) { table[i].SetVector(vpRows[r],vp.GetRow(r)); table[i].SetVector(viewRows[r],retainedView.GetRow(r)); }
    }
    void UpdateView(Camera camera)
    {
        var view=camera.worldToCameraMatrix; var projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true); Check(view); Check(projection);
        if(viewSet&&Same(view,retainedView)&&Same(projection,retainedProjection)) return;
        retainedView=view; retainedProjection=projection; BindView(materials); viewSet=true; ViewUniformUpdateCount++;
    }
    public void Submit(Camera camera)
    {
        if(ReplayGpuFaulted) throw new InvalidOperationException("Replay GPU recovery failed; no further draw permitted.");
        if(disposed||lastFrame==Time.frameCount) return; lastFrame=Time.frameCount; long start=System.Diagnostics.Stopwatch.GetTimestamp(); LastSubmissionCalls=0;
        if(Retained(RenderMode))
        {
            if(camera==null||retainedCommands==null||camera.targetTexture!=retainedColor||(camera.cullingMask&LayerMask)!=LayerMask||camera.renderingPath!=RenderingPath.Forward) throw new InvalidDataException("Retained owner requires configured MRT and explicit Forward camera covering every admitted layer.");
            UpdateView(camera); var previousTarget=RenderTexture.active;
            try { Graphics.ExecuteCommandBuffer(retainedCommands); NativeExecuteCount++; } finally { RenderTexture.active=previousTarget; }
            LastSubmissionCalls=retainedCalls;
        }
        else if(RenderMode=="grouped") foreach(var packet in packets) { Graphics.DrawMeshInstanced(meshes[packet.Mesh],packet.Submesh,materials[Manifest.materials.Length+packet.Recipe],packet.Matrices,packet.Matrices.Length,packet.Block,ShadowCastingMode.On,false,packet.Layer,camera,LightProbeUsage.Off,null); LastSubmissionCalls++; }
        else for(int i=0;i<Manifest.draws.Length;i++) { var a=Manifest.draws[i]; if(!visibility[i]) continue; Graphics.DrawMesh(meshes[a.meshIndex],matrices[i],materials[a.materialIndex],a.layer,camera,a.submeshIndex,referenceColors[i],ShadowCastingMode.On,false,null,LightProbeUsage.Off,null); LastSubmissionCalls++; }
        DrawCalls+=LastSubmissionCalls; SubmissionTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-start; SubmittedFrames++;
    }
    public void Dispose() { if(disposed) return; disposed=true; ReplaceCommands(null,0); foreach(var o in owned) if(o!=null) UnityEngine.Object.Destroy(o); }
}
