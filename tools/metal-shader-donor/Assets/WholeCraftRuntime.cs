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
            if(first) throw new InvalidDataException("Whole fixture has no visible captured rows."); Check(baseBounds.center); Check(baseBounds.extents); Bounds=baseBounds; materials=PrepareDebugShader(shader); owned.AddRange(materials);
        }
        catch { Dispose(); throw; }
    }
    public Material[] PrepareDebugShader(Shader shader)
    {
        var next=new Material[Manifest.materials.Length];
        try { for(int i=0;i<next.Length;i++) { var a=Manifest.materials[i]; var m=new Material(shader); next[i]=m; m.SetFloat("_Mode",DebugMode=="off"?7:ResidentMaterialRuntime.GeometryDebugMode(DebugMode)); m.SetColor("_MaterialColor",Color.HSVToRGB((i*.61803398875f)%1,.7f,.95f)); m.SetColor("_Tint",Color.white); m.SetFloat("_Cull",0); bool albedo=false,cull=false; foreach(var p in a.properties) { if(p.name=="_MainTex"&&p.kind==4) { if(p.textureIndex>=0) { m.SetTexture("_MainTex",textures[p.textureIndex]); albedo=true; } m.SetTextureScale("_MainTex",new Vector2(p.scaleOffset[0],p.scaleOffset[1])); m.SetTextureOffset("_MainTex",new Vector2(p.scaleOffset[2],p.scaleOffset[3])); } if((p.name=="_Color"||p.name=="_BurnColor")&&(p.kind==0||p.kind==1)) m.SetColor("_Tint",new Color(p.value[0],p.value[1],p.value[2],p.value[3])); if((p.name=="_Cull"||p.name=="_CullMode")&&(p.kind==2||p.kind==3)&&p.value[0]>=0&&p.value[0]<=2&&p.value[0]==(int)p.value[0]) { cull=true; } } if(materials==null) { if(!albedo) NoAlbedoTexture++; if(!cull) UnknownCull++; if(a.queue>=2450) ApproximateTransparency++; } } return next; }
        catch { foreach(var m in next) if(m!=null) UnityEngine.Object.Destroy(m); throw; }
    }
    public void CommitDebugShader(Material[] next) { if(materials!=null) foreach(var m in materials) { owned.Remove(m); UnityEngine.Object.Destroy(m); } materials=next; owned.AddRange(next); }
    public void SetGeometryDebug(string value) { int mode=value=="off"?7:ResidentMaterialRuntime.GeometryDebugMode(value); foreach(var m in materials) m.SetFloat("_Mode",mode); DebugMode=value; }
    public void SetTextureMipBias(float bias) { for(int i=0;i<textures.Length;i++) textures[i].mipMapBias=Manifest.textures[i].mipBias+bias; }
    public void SetPresentationAnchor(Vector3 anchor)
    {
        Check(anchor); var bounds=baseBounds; bounds.center+=anchor; Check(bounds.center); Check(bounds.extents);
        for(int i=0;i<matrices.Length;i++) { var candidate=bases[i]; candidate.m03+=anchor.x; candidate.m13+=anchor.y; candidate.m23+=anchor.z; Check(candidate); }
        for(int i=0;i<matrices.Length;i++) { var m=bases[i]; m.m03+=anchor.x; m.m13+=anchor.y; m.m23+=anchor.z; matrices[i]=m; } Bounds=bounds;
    }
    public Vector3 PresentationInstanceCenter(string id,out float radius) { for(int i=0;i<Manifest.draws.Length;i++) if(Manifest.draws[i].id==id) { var b=meshes[Manifest.draws[i].meshIndex].bounds; var m=bases[i]; radius=(m.GetColumn(0).magnitude*b.extents.x+m.GetColumn(1).magnitude*b.extents.y+m.GetColumn(2).magnitude*b.extents.z); return m.MultiplyPoint3x4(b.center); } throw new InvalidDataException("Unknown whole-craft row identity."); }
    public void Submit(Camera camera) { if(disposed||lastFrame==Time.frameCount) return; lastFrame=Time.frameCount; for(int i=0;i<Manifest.draws.Length;i++) { var a=Manifest.draws[i]; if(!a.enabled||!a.active||a.forceRenderingOff) continue; Graphics.DrawMesh(meshes[a.meshIndex],matrices[i],materials[a.materialIndex],a.layer,camera,a.submeshIndex,null,ShadowCastingMode.On,false,null,LightProbeUsage.Off,null); DrawCalls++; } SubmittedFrames++; }
    public void Dispose() { if(disposed) return; disposed=true; foreach(var o in owned) if(o!=null) UnityEngine.Object.Destroy(o); }
}
