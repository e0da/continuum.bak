using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace KspContinuum
{
    internal static class MultiscaleSceneCapture
    {
        static string Hash(string path) { return ScaleCheckpointLoadState.HashFile(path); }
        static double[] D(Vector3d v) { return new[] { v.x, v.y, v.z }; }
        static float[] V(Color c) { return new[] { c.r, c.g, c.b, c.a }; }
        internal static string Capture(Vessel vessel, string output, string runId, IList<ResidentMaterialFixtureCapture.Source> sources)
        {
            if (!Path.IsPathRooted(output) || Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("New absolute multiscale capture directory required.");
            CelestialBody body = vessel.mainBody;
            if (body == null || body.scaledBody == null) throw new InvalidDataException("Actual main-body scaled source unavailable.");
            MeshFilter filter = body.scaledBody.GetComponent<MeshFilter>(); MeshRenderer renderer = body.scaledBody.GetComponent<MeshRenderer>();
            if (filter == null || filter.sharedMesh == null || renderer == null || renderer.sharedMaterials.Length != 1) throw new InvalidDataException("One actual scaled-body mesh/material required.");
            Mesh mesh = filter.sharedMesh; Material material = renderer.sharedMaterial;
            if (material == null || material.shader == null || !material.HasProperty("_MainTex") || !(material.GetTexture("_MainTex") is Texture2D)) throw new InvalidDataException("Actual body MainTex unavailable.");
            Texture2D texture = (Texture2D)material.GetTexture("_MainTex");
            string staging = output + ".pending-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
            try
            {
                string fixture = ResidentMaterialFixtureCapture.Capture(vessel, Path.Combine(staging, "vessel"), runId, sources);
                string wholeCraft = WholeCraftSceneCapture.Capture(vessel, Path.Combine(staging, "whole-craft"), runId);
                Vector3[] vertices = mesh.vertices, normals = mesh.normals; Vector2[] uv = mesh.uv; Vector4[] tangents = mesh.tangents;
                if (vertices.Length == 0 || normals.Length != vertices.Length || uv.Length != vertices.Length || mesh.subMeshCount != 1) throw new InvalidDataException("Scaled mesh source layout unsupported.");
                bool derivedTangents = tangents.Length != vertices.Length;
                var geometry = new ResidentMeshData { positions = new float[vertices.Length * 3], normals = new float[vertices.Length * 3], uv = new float[vertices.Length * 2], tangents = new float[vertices.Length * 4], indices = mesh.triangles };
                for (int i = 0; i < vertices.Length; i++)
                {
                    geometry.positions[i*3] = vertices[i].x; geometry.positions[i*3+1] = vertices[i].y; geometry.positions[i*3+2] = vertices[i].z;
                    geometry.normals[i*3] = normals[i].x; geometry.normals[i*3+1] = normals[i].y; geometry.normals[i*3+2] = normals[i].z;
                    geometry.uv[i*2] = uv[i].x; geometry.uv[i*2+1] = uv[i].y;
                    Vector4 t;
                    if (derivedTangents) { Vector3 basis = Vector3.Cross(normals[i], Mathf.Abs(normals[i].y) < .9f ? Vector3.up : Vector3.right).normalized; t = new Vector4(basis.x, basis.y, basis.z, 1); }
                    else t = tangents[i];
                    geometry.tangents[i*4] = t.x; geometry.tangents[i*4+1] = t.y; geometry.tangents[i*4+2] = t.z; geometry.tangents[i*4+3] = t.w;
                }
                string geometryPath = Path.Combine(staging, "body-geometry.bin"); ResidentMaterialFormat.WriteGeometry(geometryPath, new ResidentGeometry { meshes = new[] { geometry } });
                string captureMode; ResidentTextureAsset bodyTexture = CaptureTexture(texture, staging, out captureMode);
                Matrix4x4 scaled = filter.transform.localToWorldMatrix; Vector3 centre = body.scaledBody.transform.position;
                double scale = ScaledSpace.ScaleFactor; var transform = new double[16];
                for (int r = 0; r < 3; r++) { for (int c = 0; c < 3; c++) transform[r*4+c] = (double)scaled[r,c] * scale; transform[r*4+3] = ((double)scaled[r,3] - centre[r]) * scale; } transform[15] = 1;
                Vector3d centreOfMass = vessel.GetWorldPos3D();
                Vector3d rootOffset = (Vector3d)vessel.transform.position - centreOfMass;
                Vector3d anchor = (centreOfMass - body.position) + rootOffset;
                Camera camera = FlightCamera.fetch == null ? null : FlightCamera.fetch.mainCamera;
                if (camera == null) throw new InvalidDataException("Actual flight camera unavailable.");
                Vector3 cameraOffset = camera.transform.position - vessel.transform.position;
                Vector2 tiling = material.GetTextureScale("_MainTex"), offset = material.GetTextureOffset("_MainTex");
                var scene = new MultiscaleSceneManifest { schema = "continuum-multiscale-scene/v2", runId = runId, processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    vesselId = vessel.id.ToString("D"), engine = Application.unityVersion, colorSpace = QualitySettings.activeColorSpace.ToString(), sourceFrame = "body-centred-world-axes",
                    bodyId = "body/" + body.bodyName, bodyName = body.bodyName, vesselFixture = "vessel/manifest.json", vesselFixtureSha256 = Hash(fixture),
                    wholeCraft = "whole-craft/manifest.json", wholeCraftSha256 = Hash(wholeCraft),
                    bodyGeometry = "body-geometry.bin", bodyGeometrySha256 = Hash(geometryPath), vesselAnchorMeters = D(anchor), bodyAnchorMeters = new double[3], bodyLocalToMeters = transform,
                    bodyRadiusMeters = body.Radius, initialCameraTarget = vessel.id.ToString("D"), initialCameraOffsetMeters = new[] { (double)cameraOffset.x, cameraOffset.y, cameraOffset.z }, fov = camera.fieldOfView,
                    bodyMaterialRecipe = "continuum-body-diffuse-v1", originalBodyShader = material.shader.name, bodyTextureProperty = "_MainTex", bodyTextureCapture = captureMode,
                    bodyTexture = bodyTexture, bodyColor = V(material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white), bodyTextureScaleOffset = new[] { tiling.x,tiling.y,offset.x,offset.y } };
                scene.scientificAnchors=CaptureAnchors(vessel,centreOfMass,scene);
                scene.scientificAnchorsCaptured=true;
                MultiscaleSceneFormat.Validate(scene);
                File.WriteAllText(Path.Combine(staging, "capture-evidence.json"), JsonUtility.ToJson(new Evidence { runId=runId, body=body.bodyName, mesh=mesh.name, shader=material.shader.name,
                    vertices=vertices.Length, triangles=geometry.indices.Length/3, derivedTangents=derivedTangents, textureCapture=captureMode, sourceTexture=texture.name,
                    sourceTextureFormat=(int)texture.format, sourceTextureMips=texture.mipmapCount, scaledSpaceFactor=scale }, true));
                File.WriteAllText(Path.Combine(staging, "manifest.json"), ReportJson.Encode(scene));
                Directory.Move(staging, output); Debug.Log("[Continuum multiscale] ready actualBody=" + body.bodyName + " texture=" + captureMode + " output=" + output);
                return Path.Combine(output, "manifest.json");
            }
            catch { Debug.LogError("[Continuum multiscale] failed staging retained=" + staging); throw; }
        }
        [Serializable] sealed class Evidence { public string schema="continuum-multiscale-capture/v1",runId,body,mesh,shader,textureCapture,sourceTexture; public int vertices,triangles,sourceTextureFormat,sourceTextureMips; public bool derivedTangents; public double scaledSpaceFactor; public string qualification="Actual source geometry and texture captured; body diffuse recipe omits original atmosphere/ocean/material parity. No simulation state mutation."; }
        static ScientificAnchorSet CaptureAnchors(Vessel vessel,Vector3d centreOfMass,MultiscaleSceneManifest scene)
        {
            double[] origin=WholeCraftSceneCapture.World(vessel.transform);var rows=new List<ScientificAnchorData>();
            var vesselOrigin=TransformAnchor("vessel-origin","vessel-origin","Captured vessel transform origin",0,vessel.transform,origin,"Vessel.transform; composed ancestor local TRS in double");rows.Add(vesselOrigin);
            string preferred=vesselOrigin.id;
            if(vessel.rootPart!=null&&vessel.parts.Contains(vessel.rootPart)) {var root=TransformAnchor("root-part/"+vessel.rootPart.flightID,"root-part","Root part origin",vessel.rootPart.flightID,vessel.rootPart.transform,origin,"Vessel.rootPart.transform; composed ancestor local TRS in double");rows.Add(root);preferred=root.id;}
            Part controlPart=vessel.GetReferenceTransformPart();Transform control=controlPart==null?null:vessel.ReferenceTransform;
            if(controlPart!=null&&control!=null&&vessel.parts.Contains(controlPart)) {var row=TransformAnchor("control/"+controlPart.flightID,"control","Control from here",controlPart.flightID,control,origin,"Vessel.GetReferenceTransformPart + actual Vessel.ReferenceTransform; composed ancestor local TRS in double");rows.Add(row);preferred=row.id;}
            Vector3d offset=centreOfMass-(Vector3d)vessel.transform.position;
            rows.Add(new ScientificAnchorData {id="center-of-mass",kind="center-of-mass",label="Captured vessel center of mass",source="Vessel.GetWorldPos3D/CoMD captured by scene producer",offsetMeters=D(offset),axesAvailable=false});
            string selected=Environment.GetEnvironmentVariable("CONTINUUM_SCIENTIFIC_ATTACHMENT_NODES");
            if(!string.IsNullOrEmpty(selected))
            {
                string[] selections=selected.Split(',');if(selections.Length>128)throw new InvalidDataException("Selected attachment anchor capacity exceeded.");
                foreach(string selection in selections)
                {
                    int colon=selection.IndexOf(':');uint flightId;if(colon<=0||!uint.TryParse(selection.Substring(0,colon),out flightId)||flightId==0||colon==selection.Length-1)throw new InvalidDataException("Attachment selection requires flightId:actual-node-id.");
                    string nodeId=selection.Substring(colon+1);Part part=null;foreach(Part p in vessel.parts)if(p.flightID==flightId) {if(part!=null)throw new InvalidDataException("Ambiguous selected part identity.");part=p;}
                    if(part==null)throw new InvalidDataException("Selected attachment part is absent.");
                    AttachNode node=null;foreach(AttachNode n in part.attachNodes)if(n!=null&&n.id==nodeId) {if(node!=null)throw new InvalidDataException("Ambiguous selected attachment identity.");node=n;}
                    if(part.srfAttachNode!=null&&part.srfAttachNode.id==nodeId) {if(node!=null&&!ReferenceEquals(node,part.srfAttachNode))throw new InvalidDataException("Ambiguous selected surface attachment identity.");node=part.srfAttachNode;}
                    if(node==null||!ReferenceEquals(node.owner,part))throw new InvalidDataException("Selected attachment owner is absent or differs.");
                    double[] m=WholeCraftSceneCapture.World(part.transform);Vector3 local=node.position;
                    var row=new ScientificAnchorData {id="attachment/"+flightId+"/"+nodeId,kind="attachment-node",label="Attachment "+nodeId,source="Actual AttachNode.position in owner-local coordinates; stock owner.TransformPoint convention, double composed TRS",partFlightId=flightId,nodeId=nodeId,offsetMeters=new[] {m[0]*local.x+m[1]*local.y+m[2]*local.z+m[3]-origin[3],m[4]*local.x+m[5]*local.y+m[6]*local.z+m[7]-origin[7],m[8]*local.x+m[9]*local.y+m[10]*local.z+m[11]-origin[11]}};
                    Vector3 normal=node.orientation,secondary=node.secondaryAxis;
                    double[] f=Direction(m,normal),u=Direction(m,secondary);double fl=Length(f),ul=Length(u);
                    if(fl>0&&ul>0) {for(int i=0;i<3;i++)f[i]/=fl;double dot=Dot(u,f);for(int i=0;i<3;i++)u[i]-=dot*f[i];ul=Length(u);if(ul>1e-10) {for(int i=0;i<3;i++)u[i]/=ul;row.forward=f;row.up=u;row.right=Cross(u,f);row.axesAvailable=true;}}
                    rows.Add(row);
                }
            }
            return new ScientificAnchorSet {runId=scene.runId,processId=scene.processId,vesselId=scene.vesselId,bodyId=scene.bodyId,epochSeconds=Planetarium.GetUniversalTime(),preferredId=preferred,anchors=rows.ToArray()};
        }
        static ScientificAnchorData TransformAnchor(string id,string kind,string label,uint partId,Transform value,double[] origin,string source)
        {
            var m=WholeCraftSceneCapture.World(value);var row=new ScientificAnchorData {id=id,kind=kind,label=label,source=source,partFlightId=partId,offsetMeters=new[] {m[3]-origin[3],m[7]-origin[7],m[11]-origin[11]}};
            double[] r={m[0],m[4],m[8]},u={m[1],m[5],m[9]},f={m[2],m[6],m[10]};double rl=Length(r),ul=Length(u),fl=Length(f);
            if(rl>0&&ul>0&&fl>0) {for(int i=0;i<3;i++) {r[i]/=rl;u[i]/=ul;f[i]/=fl;}if(Math.Abs(Dot(r,u))<1e-6&&Math.Abs(Dot(r,f))<1e-6&&Math.Abs(Dot(u,f))<1e-6&&Dot(Cross(r,u),f)>1-1e-6) {row.axesAvailable=true;row.right=r;row.up=u;row.forward=f;}}
            return row;
        }
        static double[] Direction(double[] m,Vector3 p) {return new[] {m[0]*p.x+m[1]*p.y+m[2]*p.z,m[4]*p.x+m[5]*p.y+m[6]*p.z,m[8]*p.x+m[9]*p.y+m[10]*p.z};}
        static double Dot(double[] a,double[] b) {return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];}
        static double Length(double[] a) {return Math.Sqrt(Dot(a,a));}
        static double[] Cross(double[] a,double[] b) {return new[] {a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};}
        static ResidentTextureAsset CaptureTexture(Texture2D source, string directory, out string captureMode)
        { return CaptureTexture(source,directory,"body-texture.bin",out captureMode); }
        internal static ResidentTextureAsset CaptureTexture(Texture2D source, string directory, string filename, out string captureMode)
        {
            MultiscaleSceneFormat.PortablePath(filename);
            string path=Path.Combine(directory,filename), payload; ResourceImageEntry entry = null;
            try { entry = StartupResourceImage.PublishedPayload(source, out payload); } catch (InvalidDataException) { payload = null; }
            Texture2D encoded = source; bool linear = GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat) == false;
            try
            {
                if (entry != null) { File.Copy(payload,path); captureMode="prepared-raw"; }
                else if (source.isReadable) { File.WriteAllBytes(path,source.GetRawTextureData()); captureMode="readable-raw"; }
                else
                {
                    RenderTexture previous = RenderTexture.active;
                    encoded = new Texture2D(source.width,source.height,TextureFormat.RGBA32,false,true);
                    RenderTexture target = RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                    try { Graphics.Blit(source,target); RenderTexture.active=target; encoded.ReadPixels(new Rect(0,0,source.width,source.height),0,0,false); encoded.Apply(false,false); File.WriteAllBytes(path,encoded.GetRawTextureData()); }
                    finally { RenderTexture.active=previous; RenderTexture.ReleaseTemporary(target); }
                    linear=true; captureMode="rgba-readback-mip0";
                }
                string digest = Hash(path);
                return new ResidentTextureAsset { id=digest, payload=filename,payloadSha256=digest,payloadLength=new FileInfo(path).Length,
                    width=encoded.width,height=encoded.height,format=(int)encoded.format,graphicsFormat=Convert.ToInt32(encoded.graphicsFormat),mipCount=encoded.mipmapCount,
                    filter=(int)source.filterMode,wrap=(int)source.wrapMode,aniso=source.anisoLevel,mipBias=source.mipMapBias,linear=entry == null ? linear : entry.linear };
            }
            finally { if (encoded != source) UnityEngine.Object.Destroy(encoded); }
        }
    }
}
