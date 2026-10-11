using System;
using System.IO;
using System.Text;

namespace KspContinuum
{
    [Serializable] public sealed class WholeCraftSceneManifest
    {
        public string schema, runId, vesselId, engine, colorSpace, geometry, geometrySha256;
        public int processId, rendererInventory, capturedRenderers, omittedRenderers, drawSlots;
        public float[] rootToWorld;
        public WholeCraftMaterialAsset[] materials; public ResidentTextureAsset[] textures;
        public WholeCraftDraw[] draws; public WholeCraftOmission[] omissions;
        public string[] textureCaptureModes;
        public string geometryScope="positions-normals-uv0-tangents-colors-triangle-submeshes; other UV channels and arbitrary MPB values not captured";
    }
    [Serializable] public sealed class WholeCraftMaterialAsset
    {
        public string id, name, shader; public int queue; public string[] keywords;
        public ResidentMaterialProperty[] properties;
        public string[] omittedProperties;
    }
    [Serializable] public sealed class WholeCraftDraw
    {
        public string id, rendererName; public int rendererId, meshIndex, submeshIndex, materialIndex, layer, shadowCasting;
        public uint partId; public bool receiveShadows, enabled, active, forceRenderingOff, propertyBlockPresent, frozenSkin;
        public double[] matrix;
    }
    [Serializable] public sealed class WholeCraftOmission { public int rendererId; public string name, reason; }
    [Serializable] public sealed class WholeCraftGeometry { public WholeCraftMeshData[] meshes; }
    [Serializable] public sealed class WholeCraftMeshData { public float[] positions, normals, uv, tangents, colors; public WholeCraftSubmesh[] submeshes; }
    [Serializable] public sealed class WholeCraftSubmesh { public int[] indices; }
    public static class WholeCraftSceneFormat
    {
        const int Magic=0x31474357, MaxRows=50000, MaxVertices=4000000, MaxIndices=12000000;
        static void Require(bool condition,string reason) { if(!condition) throw new InvalidDataException(reason); }
        static bool Finite(double v) { return !double.IsNaN(v)&&!double.IsInfinity(v); }
        static void Identity(string value) { Require(!string.IsNullOrEmpty(value)&&value.Length<=2048,"Whole-craft identity absent/oversize."); foreach(char c in value) Require(!char.IsControl(c),"Whole-craft identity contains controls."); }
        static void Digest(string value) { Require(value!=null&&value.Length==64,"Whole-craft digest differs."); foreach(char c in value) Require(c>='0'&&c<='9'||c>='a'&&c<='f',"Whole-craft digest encoding differs."); }
        static void Channel(float[] values,int count,bool optional) { Require(values!=null&&(values.Length==count||optional&&values.Length==0),"Whole-craft channel shape differs."); foreach(float v in values) Require(Finite(v),"Whole-craft channel nonfinite."); }
        public static void ValidateGeometry(WholeCraftGeometry data)
        {
            Require(data!=null&&data.meshes!=null&&data.meshes.Length>0&&data.meshes.Length<=MaxRows,"Whole-craft mesh capacity invalid."); long vertices=0,indices=0;
            foreach(var m in data.meshes)
            {
                Require(m!=null&&m.positions!=null&&m.positions.Length>0&&m.positions.Length%3==0,"Whole-craft positions differ."); int n=m.positions.Length/3; vertices+=n; Require(vertices<=MaxVertices,"Whole-craft vertex capacity exceeded.");
                Channel(m.positions,n*3,false); Channel(m.normals,n*3,true); Channel(m.uv,n*2,true); Channel(m.tangents,n*4,true); Channel(m.colors,n*4,true);
                Require(m.submeshes!=null&&m.submeshes.Length>0&&m.submeshes.Length<=4096,"Whole-craft submesh capacity invalid.");
                foreach(var s in m.submeshes) { Require(s!=null&&s.indices!=null&&s.indices.Length%3==0,"Whole-craft triangle shape differs."); indices+=s.indices.Length; Require(indices<=MaxIndices,"Whole-craft index capacity exceeded."); foreach(int i in s.indices) Require(i>=0&&i<n,"Whole-craft index out of range."); }
            }
        }
        public static void Validate(WholeCraftSceneManifest manifest,WholeCraftGeometry data)
        {
            ValidateGeometry(data); Require(manifest!=null&&manifest.schema=="continuum-whole-craft-scene/v1","Whole-craft schema differs."); Identity(manifest.runId); Identity(manifest.vesselId); Identity(manifest.engine);
            Require(manifest.processId>0&&(manifest.colorSpace=="Gamma"||manifest.colorSpace=="Linear"),"Whole-craft producer invalid."); MultiscaleSceneFormat.PortablePath(manifest.geometry); Digest(manifest.geometrySha256);
            Channel(manifest.rootToWorld,16,false); Require(manifest.rootToWorld[3]==0&&manifest.rootToWorld[7]==0&&manifest.rootToWorld[11]==0&&manifest.rootToWorld[12]==0&&manifest.rootToWorld[13]==0&&manifest.rootToWorld[14]==0&&manifest.rootToWorld[15]==1,"Whole-craft root must be translation-free affine.");
            var root=manifest.rootToWorld; double rootDet=(double)root[0]*(root[5]*root[10]-root[6]*root[9])-(double)root[1]*(root[4]*root[10]-root[6]*root[8])+(double)root[2]*(root[4]*root[9]-root[5]*root[8]); Require(Finite(rootDet)&&Math.Abs(rootDet)>1e-30,"Whole-craft root singular.");
            Require(manifest.draws!=null&&manifest.draws.Length>0&&manifest.draws.Length<=MaxRows&&manifest.drawSlots==manifest.draws.Length&&manifest.materials!=null&&manifest.materials.Length<=MaxRows&&manifest.textures!=null&&manifest.textures.Length<=4096&&manifest.omissions!=null&&manifest.omissions.Length<=MaxRows&&manifest.textureCaptureModes!=null&&manifest.textureCaptureModes.Length==manifest.textures.Length,"Whole-craft table capacity differs.");
            foreach(string mode in manifest.textureCaptureModes) Require(mode=="prepared-raw"||mode=="readable-raw"||mode=="rgba-readback-mip0","Whole-craft texture provenance differs.");
            var rendererIds=new System.Collections.Generic.HashSet<int>(); var ids=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach(var d in manifest.draws) { Require(d!=null,"Null whole-craft draw."); Identity(d.id); Require(ids.Add(d.id)&&d.rendererId!=0&&d.partId!=0&&d.layer>=0&&d.layer<=31,"Whole-craft draw identity invalid."); rendererIds.Add(d.rendererId); Require(d.meshIndex>=0&&d.meshIndex<data.meshes.Length&&d.submeshIndex>=0&&d.submeshIndex<data.meshes[d.meshIndex].submeshes.Length&&d.materialIndex>=0&&d.materialIndex<manifest.materials.Length,"Whole-craft draw reference invalid."); Require(d.matrix!=null&&d.matrix.Length==16,"Whole-craft pose shape invalid."); foreach(double v in d.matrix) Require(Finite(v),"Whole-craft pose nonfinite."); Require(d.matrix[12]==0&&d.matrix[13]==0&&d.matrix[14]==0&&d.matrix[15]==1,"Whole-craft pose not affine."); double[] m=d.matrix; double det=m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]); Require(Finite(det)&&Math.Abs(det)>1e-30,"Whole-craft pose singular."); }
            var omitted=new System.Collections.Generic.HashSet<int>(); foreach(var o in manifest.omissions) { Require(o!=null&&o.rendererId!=0&&omitted.Add(o.rendererId)&&!rendererIds.Contains(o.rendererId),"Whole-craft omission duplicates captured renderer."); Identity(o.name); Identity(o.reason); }
            Require(manifest.capturedRenderers==rendererIds.Count&&manifest.omittedRenderers==omitted.Count&&manifest.rendererInventory==checked(rendererIds.Count+omitted.Count),"Whole-craft renderer coverage differs.");
            foreach(var t in manifest.textures) { Require(t!=null,"Whole-craft texture absent."); Identity(t.id); MultiscaleSceneFormat.PortablePath(t.payload); Digest(t.payloadSha256); Require(t.width>0&&t.width<=16384&&t.height>0&&t.height<=16384&&t.payloadLength>0&&t.payloadLength<=1073741824&&t.mipCount>0&&t.mipCount<=15&&t.filter>=0&&t.filter<=2&&t.wrap>=0&&t.wrap<=3&&t.aniso>=0&&t.aniso<=16&&Finite(t.mipBias),"Whole-craft texture invalid."); }
            foreach(var material in manifest.materials) { Require(material!=null&&material.keywords!=null&&material.properties!=null,"Whole-craft material absent."); Identity(material.id); Identity(material.name); Identity(material.shader); Require(material.keywords.Length<=256&&material.properties.Length<=256,"Whole-craft material capacity invalid."); foreach(string k in material.keywords) Identity(k); var names=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal); foreach(var p in material.properties) { Require(p!=null,"Whole-craft property absent."); Identity(p.name); Require(names.Add(p.name)&&p.kind>=0&&p.kind<=4,"Whole-craft property identity invalid."); if(p.kind==4) { Require(p.textureIndex>=-1&&p.textureIndex<manifest.textures.Length,"Whole-craft texture reference invalid."); Channel(p.scaleOffset,4,false); Channel(p.value,0,false); } else { Channel(p.value,p.kind<=1?4:1,false); Channel(p.scaleOffset,0,false); Require(p.textureIndex==-1,"Whole-craft numeric texture reference differs."); } } }
        }
        public static void WriteGeometry(string path,WholeCraftGeometry data)
        {
            ValidateGeometry(data); using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write)) using(var writer=new BinaryWriter(stream,Encoding.UTF8)) { writer.Write(Magic); writer.Write(1); writer.Write(data.meshes.Length); foreach(var m in data.meshes) { foreach(var a in new[] {m.positions,m.normals,m.uv,m.tangents,m.colors}) { writer.Write(a.Length); foreach(float v in a) writer.Write(v); } writer.Write(m.submeshes.Length); foreach(var s in m.submeshes) { writer.Write(s.indices.Length); foreach(int i in s.indices) writer.Write(i); } } }
        }
        public static WholeCraftGeometry ReadGeometry(string path)
        {
            using(var stream=File.OpenRead(path)) using(var reader=new BinaryReader(stream,Encoding.UTF8)) { Require(reader.ReadInt32()==Magic&&reader.ReadInt32()==1,"Whole-craft geometry header differs."); int count=reader.ReadInt32(); Require(count>0&&count<=MaxRows,"Whole-craft mesh capacity invalid."); var data=new WholeCraftGeometry {meshes=new WholeCraftMeshData[count]}; long totalFloats=0,totalIndices=0;
                for(int j=0;j<count;j++) { var channels=new float[5][]; for(int c=0;c<5;c++) { int n=reader.ReadInt32(); totalFloats+=n; Require(n>=0&&n<=MaxVertices*4&&totalFloats<=MaxVertices*16&&n<=(stream.Length-stream.Position)/4,"Whole-craft channel capacity invalid."); var a=new float[n]; for(int i=0;i<n;i++) a[i]=reader.ReadSingle(); channels[c]=a; } int subs=reader.ReadInt32(); Require(subs>0&&subs<=4096,"Whole-craft submesh capacity invalid."); var s=new WholeCraftSubmesh[subs]; for(int q=0;q<subs;q++) { int n=reader.ReadInt32(); totalIndices+=n; Require(n>=0&&totalIndices<=MaxIndices&&n<=(stream.Length-stream.Position)/4,"Whole-craft index capacity invalid."); var a=new int[n]; for(int i=0;i<n;i++) a[i]=reader.ReadInt32(); s[q]=new WholeCraftSubmesh {indices=a}; } data.meshes[j]=new WholeCraftMeshData {positions=channels[0],normals=channels[1],uv=channels[2],tangents=channels[3],colors=channels[4],submeshes=s}; }
                Require(stream.Position==stream.Length,"Whole-craft trailing geometry bytes."); ValidateGeometry(data); return data;
            }
        }
    }
}
