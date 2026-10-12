using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal static class WholeCraftSceneCapture
    {
        sealed class RuntimeBinding
        {
            internal readonly Vessel Vessel;
            internal readonly WholeCraftSceneManifest Manifest;
            internal readonly string Path, Sha256, Json;
            internal RuntimeBinding(Vessel vessel, WholeCraftSceneManifest manifest, string path, string sha256, string json)
            { Vessel=vessel; Manifest=manifest; Path=path; Sha256=sha256; Json=json; }
        }
        static RuntimeBinding runtimeBinding;
        static string Hash(string path) { return ScaleCheckpointLoadState.HashFile(path); }
        internal static WholeCraftSceneManifest TakeRuntimeManifest(Vessel vessel,string path,string sha256,string runId)
        {
            RuntimeBinding binding=runtimeBinding;
            if(binding==null) throw new InvalidDataException("Runtime cold manifest was not retained by this process.");
            if(!ReferenceEquals(binding.Vessel,vessel)) throw new InvalidDataException("Runtime cold manifest vessel reference differs.");
            if(!string.Equals(binding.Path,System.IO.Path.GetFullPath(path),StringComparison.Ordinal)) throw new InvalidDataException("Runtime cold manifest path differs: actual="+System.IO.Path.GetFullPath(path)+", expected="+binding.Path+".");
            if(!string.Equals(binding.Sha256,sha256,StringComparison.Ordinal)) throw new InvalidDataException("Runtime cold manifest encoded digest differs: actual="+sha256+", expected="+binding.Sha256+".");
            if(!string.Equals(binding.Manifest.runId,runId,StringComparison.Ordinal)) throw new InvalidDataException("Runtime retained manifest run differs: actual="+binding.Manifest.runId+", expected="+runId+".");
            if(!string.Equals(ReportJson.Encode(binding.Manifest),binding.Json,StringComparison.Ordinal)) throw new InvalidDataException("Runtime retained cold manifest changed after encoding.");
            runtimeBinding=null; return binding.Manifest;
        }
        static float[] Matrix(Matrix4x4 m) { var a=new float[16]; for(int r=0;r<4;r++) for(int c=0;c<4;c++) a[r*4+c]=m[r,c]; return a; }
        static double[] Identity() { var a=new double[16]; a[0]=a[5]=a[10]=a[15]=1; return a; }
        static double[] Multiply(double[] a,double[] b) { var c=new double[16]; for(int r=0;r<4;r++) for(int k=0;k<4;k++) for(int j=0;j<4;j++) c[r*4+j]+=a[r*4+k]*b[k*4+j]; return c; }
        static double[] Local(Transform t)
        {
            Quaternion q=t.localRotation; double x=q.x,y=q.y,z=q.z,w=q.w; Vector3 s=t.localScale,p=t.localPosition;
            return new[] {(1-2*y*y-2*z*z)*s.x,(2*x*y-2*z*w)*s.y,(2*x*z+2*y*w)*s.z,(double)p.x,
                (2*x*y+2*z*w)*s.x,(1-2*x*x-2*z*z)*s.y,(2*y*z-2*x*w)*s.z,(double)p.y,
                (2*x*z-2*y*w)*s.x,(2*y*z+2*x*w)*s.y,(1-2*x*x-2*y*y)*s.z,(double)p.z,0,0,0,1};
        }
        internal static double[] World(Transform value)
        {
            var chain=new List<Transform>(); while(value!=null) { if(chain.Count>=256) throw new InvalidDataException("Oversize world transform hierarchy."); chain.Add(value); value=value.parent; }
            double[] m=Identity(); for(int i=chain.Count-1;i>=0;i--) m=Multiply(m,Local(chain[i])); return m;
        }
        internal static double[] RootLocal(Transform value,Transform root,double[] rootWorld,out string source)
        {
            Transform original=value; var chain=new List<Transform>(); while(value!=root&&value!=null) { if(chain.Count>=256) throw new InvalidDataException("Oversize renderer hierarchy."); chain.Add(value); value=value.parent; }
            if(value==null) { source="detached-world-trs-double-relative"; return WholeCraftSceneFormat.RelativeAffine(rootWorld,World(original)); }
            source="ancestor-local-double"; double[] m=Identity(); for(int i=chain.Count-1;i>=0;i--) m=Multiply(m,Local(chain[i])); return m;
        }
        static void ValidatePose(double[] m)
        {
            foreach(double v in m) if(double.IsNaN(v)||double.IsInfinity(v)) throw new InvalidDataException("Nonfinite renderer pose.");
            double d=m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]);
            if(double.IsNaN(d)||double.IsInfinity(d)||Math.Abs(d)<=1e-30) throw new InvalidDataException("Singular renderer pose.");
        }
        internal static string Stable(Renderer renderer,Part part)
        {
            var indices=new List<int>(); Transform t=renderer.transform; while(t!=part.transform) { if(t==null||indices.Count>=256) throw new InvalidDataException("Renderer outside owning part."); indices.Add(t.GetSiblingIndex()); t=t.parent; } indices.Reverse(); return part.flightID+"/"+string.Join("/",indices.Select(i=>i.ToString()).ToArray())+"/"+renderer.GetType().Name;
        }
        static WholeCraftMeshData Geometry(Mesh mesh)
        {
            Vector3[] p=mesh.vertices,n=mesh.normals; Vector2[] uv=mesh.uv; Vector4[] t=mesh.tangents; Color[] c=mesh.colors;
            var data=new WholeCraftMeshData {positions=new float[p.Length*3],normals=new float[n.Length*3],uv=new float[uv.Length*2],tangents=new float[t.Length*4],colors=new float[c.Length*4],submeshes=new WholeCraftSubmesh[mesh.subMeshCount]};
            for(int i=0;i<p.Length;i++) { data.positions[i*3]=p[i].x; data.positions[i*3+1]=p[i].y; data.positions[i*3+2]=p[i].z; }
            for(int i=0;i<n.Length;i++) { data.normals[i*3]=n[i].x; data.normals[i*3+1]=n[i].y; data.normals[i*3+2]=n[i].z; }
            for(int i=0;i<uv.Length;i++) { data.uv[i*2]=uv[i].x; data.uv[i*2+1]=uv[i].y; }
            for(int i=0;i<t.Length;i++) { data.tangents[i*4]=t[i].x; data.tangents[i*4+1]=t[i].y; data.tangents[i*4+2]=t[i].z; data.tangents[i*4+3]=t[i].w; }
            for(int i=0;i<c.Length;i++) { data.colors[i*4]=c[i].r; data.colors[i*4+1]=c[i].g; data.colors[i*4+2]=c[i].b; data.colors[i*4+3]=c[i].a; }
            for(int i=0;i<data.submeshes.Length;i++) { if(mesh.GetTopology(i)!=MeshTopology.Triangles) throw new InvalidDataException("Nontriangle submesh."); data.submeshes[i]=new WholeCraftSubmesh {indices=mesh.GetIndices(i)}; }
            WholeCraftSceneFormat.ValidateGeometry(new WholeCraftGeometry {meshes=new[] {data}}); return data;
        }
        internal static string Capture(Vessel vessel,string output,string runId)
        {
            runtimeBinding=null;
            if(vessel==null||!vessel.loaded||vessel.packed||HighLogic.LoadedScene!=GameScenes.FLIGHT) throw new InvalidDataException("Whole-craft requires loaded physical flight vessel.");
            if(!Path.IsPathRooted(output)||Directory.Exists(output)||File.Exists(output)) throw new InvalidDataException("Whole-craft requires new absolute output.");
            string stage=output+".pending-"+Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage);
            var meshes=new List<WholeCraftMeshData>(); var meshIds=new Dictionary<Mesh,int>(); var materials=new List<WholeCraftMaterialAsset>(); var materialIds=new Dictionary<Material,int>(); var textures=new List<ResidentTextureAsset>(); var textureIds=new Dictionary<Texture2D,int>(); var modes=new List<string>(); var draws=new List<WholeCraftDraw>(); var omissions=new List<WholeCraftOmission>();
            double[] rootWorld=World(vessel.transform); ValidatePose(rootWorld);
            int textureAttempt=0; var parts=new HashSet<Part>(vessel.parts); var renderers=new Dictionary<int,Renderer>();
            foreach(Part part in vessel.parts) foreach(Renderer renderer in part.GetComponentsInChildren<Renderer>(true)) { if(renderer==null) continue; Part owner=renderer.GetComponentInParent<Part>(); if(owner!=null&&parts.Contains(owner)) renderers[renderer.GetInstanceID()]=renderer; }
            if(renderers.Count>50000) throw new InvalidDataException("Whole-craft renderer capacity exceeded.");
            foreach(var pair in renderers.OrderBy(p=>p.Key))
            {
                Renderer renderer=pair.Value; Part part=renderer.GetComponentInParent<Part>(); Mesh mesh=null; bool ownsMesh=false,frozenSkin=false;
                try
                {
                    var staticRenderer=renderer as MeshRenderer; var skin=renderer as SkinnedMeshRenderer;
                    if(staticRenderer!=null) { MeshFilter filter=renderer.GetComponent<MeshFilter>(); if(filter!=null) mesh=filter.sharedMesh; }
                    else if(skin!=null) { var bake=typeof(SkinnedMeshRenderer).GetMethod("BakeMesh",new[] {typeof(Mesh),typeof(bool)}); if(bake==null) throw new InvalidDataException("Frozen skin scale semantics unavailable; unsupported BakeMesh overload."); mesh=new Mesh(); ownsMesh=true; bake.Invoke(skin,new object[] {mesh,false}); frozenSkin=true; }
                    else throw new InvalidDataException("Nonmesh renderer type: "+renderer.GetType().Name);
                    if(mesh==null||mesh.vertexCount==0) throw new InvalidDataException("Mesh absent/empty.");
                    string poseSource; double[] pose=RootLocal(renderer.transform,vessel.transform,rootWorld,out poseSource); string stable=Stable(renderer,part); Material[] slots=renderer.sharedMaterials;
                    ValidatePose(pose);
                    if(slots.Length<mesh.subMeshCount||slots.Any(m=>m==null||m.shader==null)) throw new InvalidDataException("Unbound material/submesh slot.");
                    int meshIndex; if(!meshIds.TryGetValue(mesh,out meshIndex)) { var data=Geometry(mesh); meshIndex=meshes.Count; meshes.Add(data); if(!ownsMesh) meshIds.Add(mesh,meshIndex); }
                    var pending=new List<WholeCraftDraw>();
                    for(int slot=0;slot<slots.Length;slot++)
                    {
                        Material material=slots[slot]; int materialIndex;
                        if(!materialIds.TryGetValue(material,out materialIndex))
                        {
                            var properties=new List<ResidentMaterialProperty>(); var missing=new List<string>(); Shader shader=material.shader;
                            for(int p=0;p<shader.GetPropertyCount();p++)
                            {
                                string name=shader.GetPropertyName(p); var property=new ResidentMaterialProperty {name=name,kind=(int)shader.GetPropertyType(p),textureIndex=-1,value=new float[0],scaleOffset=new float[0]};
                                try
                                {
                                    if(property.kind==0) { Color v=material.GetColor(name); property.value=new[] {v.r,v.g,v.b,v.a}; }
                                    else if(property.kind==1) { Vector4 v=material.GetVector(name); property.value=new[] {v.x,v.y,v.z,v.w}; }
                                    else if(property.kind==2||property.kind==3) property.value=new[] {material.GetFloat(name)};
                                    else if(property.kind==4)
                                    {
                                        Texture original=material.GetTexture(name); Texture2D texture=original as Texture2D;
                                        if(original!=null&&texture==null) missing.Add(name+":unsupported-texture-type:"+original.GetType().Name);
                                        if(texture!=null)
                                        {
                                            int index; if(!textureIds.TryGetValue(texture,out index)) { string mode; string filename="texture-"+(textureAttempt++)+".bin"; var asset=MultiscaleSceneCapture.CaptureTexture(texture,stage,filename,out mode); index=textures.Count; textureIds.Add(texture,index); textures.Add(asset); modes.Add(mode); } property.textureIndex=index;
                                        }
                                        Vector2 scale=material.GetTextureScale(name),offset=material.GetTextureOffset(name); property.scaleOffset=new[] {scale.x,scale.y,offset.x,offset.y};
                                    }
                                    else { missing.Add(name+":unsupported-property-kind"); continue; }
                                    if(property.value.Any(v=>float.IsNaN(v)||float.IsInfinity(v))) { missing.Add(name+":nonfinite-value"); continue; }
                                    properties.Add(property);
                                }
                                catch(Exception error) { missing.Add(name+":"+error.GetType().Name+":"+error.Message); }
                            }
                            materialIndex=materials.Count; materialIds.Add(material,materialIndex); materials.Add(new WholeCraftMaterialAsset {id="material/"+material.GetInstanceID(),name=material.name,shader=shader.name,queue=material.renderQueue,keywords=material.shaderKeywords,properties=properties.ToArray(),omittedProperties=missing.ToArray()});
                        }
                        pending.Add(new WholeCraftDraw {id=stable+"/slot/"+slot,rendererName=renderer.name,rendererId=renderer.GetInstanceID(),partId=part.flightID,meshIndex=meshIndex,submeshIndex=Math.Min(slot,mesh.subMeshCount-1),materialIndex=materialIndex,matrix=pose,poseSource=poseSource,posePrecision="unity-float-local-trs-composed-double",layer=renderer.gameObject.layer,shadowCasting=(int)renderer.shadowCastingMode,receiveShadows=renderer.receiveShadows,enabled=renderer.enabled,active=renderer.gameObject.activeInHierarchy,forceRenderingOff=renderer.forceRenderingOff,propertyBlockPresent=renderer.HasPropertyBlock(),frozenSkin=frozenSkin});
                    }
                    draws.AddRange(pending);
                }
                catch(Exception error) { omissions.Add(new WholeCraftOmission {rendererId=renderer.GetInstanceID(),name=renderer.name,reason=error.GetType().Name+":"+error.Message}); }
                finally { if(ownsMesh&&mesh!=null) UnityEngine.Object.Destroy(mesh); }
            }
            var geometry=new WholeCraftGeometry {meshes=meshes.ToArray()}; string geometryPath=Path.Combine(stage,"geometry.bin"); WholeCraftSceneFormat.WriteGeometry(geometryPath,geometry);
            var root=new Matrix4x4(); for(int r=0;r<4;r++) for(int c=0;c<4;c++) root[r,c]=(float)rootWorld[r*4+c]; root.m03=root.m13=root.m23=0;
            var manifest=new WholeCraftSceneManifest {schema="continuum-whole-craft-scene/v1",runId=runId,processId=System.Diagnostics.Process.GetCurrentProcess().Id,vesselId=vessel.id.ToString("D"),engine=Application.unityVersion,colorSpace=QualitySettings.activeColorSpace.ToString(),geometry="geometry.bin",geometrySha256=Hash(geometryPath),rootToWorld=Matrix(root),rendererInventory=renderers.Count,capturedRenderers=draws.Select(d=>d.rendererId).Distinct().Count(),omittedRenderers=omissions.Count,drawSlots=draws.Count,materials=materials.ToArray(),textures=textures.ToArray(),textureCaptureModes=modes.ToArray(),draws=draws.ToArray(),omissions=omissions.ToArray()};
            WholeCraftSceneFormat.Validate(manifest,geometry); string json=ReportJson.Encode(manifest),manifestPath=Path.Combine(stage,"manifest.json");
            bool retainRuntime=VesselRuntimeCapture.Requested || TrailEmitterCapture.Requested;
            File.WriteAllText(manifestPath,json,new UTF8Encoding(false)); string manifestSha256=retainRuntime?Hash(manifestPath):null; Directory.Move(stage,output);
            if(retainRuntime)
            {
                string sceneDirectory=Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_SCENE");
                if(!string.IsNullOrEmpty(sceneDirectory)&&Path.IsPathRooted(sceneDirectory))
                    runtimeBinding=new RuntimeBinding(vessel,manifest,Path.GetFullPath(Path.Combine(sceneDirectory,"whole-craft","manifest.json")),manifestSha256,json);
            }
            Debug.Log("[Continuum whole craft] captured="+manifest.capturedRenderers+" omitted="+manifest.omittedRenderers+" slots="+manifest.drawSlots+" output="+output); return Path.Combine(output,"manifest.json");
        }
    }
}
