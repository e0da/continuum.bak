using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    public static class NativeAssemblySceneCapture
    {
        static void Need(bool ok,string reason) { if(!ok) throw new InvalidDataException(reason); }
        static float[] V(Vector3 v) { return new[]{v.x,v.y,v.z}; }
        static double[] D(Vector3d v) { return new[]{v.x,v.y,v.z}; }
        static float[] Q(Quaternion q) { return new[]{q.x,q.y,q.z,q.w}; }
        static double[] Relative(Vector3 v,Vector3 anchor) { return new[]{(double)v.x-anchor.x,(double)v.y-anchor.y,(double)v.z-anchor.z}; }
        static int Id(UnityEngine.Object o) { return o==null?0:o.GetInstanceID(); }
        static double[] Identity() { var m=new double[16]; m[0]=m[5]=m[10]=m[15]=1; return m; }
        static double[] RigidWorld(Rigidbody b)
        {
            var m=Matrix4x4.TRS(b.position,b.rotation,Vector3.one); var a=new double[16];
            for(int r=0;r<4;r++) for(int c=0;c<4;c++) a[r*4+c]=m[r,c]; return a;
        }
        static NativeAssemblyBody Body(Rigidbody b,Vector3 anchor,Part owner,string ownership)
        {
            Need(b!=null,"Native body disappeared during capture.");
            return new NativeAssemblyBody {ownerPartId=owner.flightID,ownershipSource=ownership,bodyId=Id(b),transformId=Id(b.transform),mass=b.mass,position=Relative(b.position,anchor),rotation=Q(b.rotation),localCenterOfMass=V(b.centerOfMass),worldCenterOfMass=Relative(b.worldCenterOfMass,anchor),principalInertia=V(b.inertiaTensor),principalRotation=Q(b.inertiaTensorRotation),velocity=V(b.velocity),angularVelocity=V(b.angularVelocity),isKinematic=b.isKinematic,useGravity=b.useGravity,detectCollisions=b.detectCollisions,sleeping=b.IsSleeping(),active=b.gameObject.activeInHierarchy,constraints=(int)b.constraints,interpolation=(int)b.interpolation,collisionDetection=(int)b.collisionDetectionMode,drag=b.drag,angularDrag=b.angularDrag,maxAngularVelocity=b.maxAngularVelocity,sleepThreshold=b.sleepThreshold};
        }
        static NativeAssemblyPartAlias Alias(Part p)
        {
            Need(p!=null,"Native assembly part disappeared.");
            return new NativeAssemblyPartAlias {partId=p.flightID,parentPartId=p.parent==null?0:p.parent.flightID,partInstanceId=Id(p),name=p.name,ownBodyId=Id(p.rb),effectiveBodyId=Id(p.Rigidbody),servoBodyId=Id(p.servoRb)};
        }
        static Part Owner(Transform t)
        {
            int n=0; while(t!=null) { Need(++n<=256,"Collider ancestor depth exceeded."); Part p=t.GetComponent<Part>(); if(p!=null) return p; t=t.parent; } return null;
        }
        static NativeAssemblyCollider Shape(Collider c,Part p,HashSet<int> bodyIds,Dictionary<Mesh,int> meshIds,List<WholeCraftMeshData> meshes,bool copyMesh,string ancestryOmission)
        {
            Need(c!=null,"Native assembly collider disappeared."); Rigidbody body=c.attachedRigidbody;
            var s=new NativeAssemblyCollider {colliderId=Id(c),transformId=Id(c.transform),bodyId=Id(body),partId=p.flightID,name=c.name,kind=c.GetType().Name,meshIndex=-1,layer=c.gameObject.layer,enabled=c.enabled,active=c.gameObject.activeInHierarchy,trigger=c.isTrigger,contactOffset=c.contactOffset,center=new float[3],size=new float[3],localToBody=Identity(),poseSource="hierarchy-local-TRS-double-relative-to-native-body-pose",omissionReason=ancestryOmission};
            PhysicMaterial material=c.sharedMaterial;
            s.materialPresent=material!=null;
            if(material!=null) { s.dynamicFriction=material.dynamicFriction;s.staticFriction=material.staticFriction;s.bounciness=material.bounciness;s.frictionCombine=(int)material.frictionCombine;s.bounceCombine=(int)material.bounceCombine; }
            if(body==null) s.omissionReason="no-attached-native-body";
            else if(!bodyIds.Contains(Id(body))) s.omissionReason="attached-body-outside-part-alias-ledger";
            else
            {
                string pose; var ownerWorld=WholeCraftSceneCapture.World(body.transform);
                var local=WholeCraftSceneCapture.RootLocal(c.transform,body.transform,ownerWorld,out pose);
                var world=WholeCraftSceneFormat.MultiplyAffine(ownerWorld,local);
                s.poseSource=pose+"-relative-to-native-body-pose";
                s.localToBody=WholeCraftSceneFormat.RelativeAffine(RigidWorld(body),world);
                if(!NativeAssemblySceneFormat.SupportedAffine(s.localToBody)) s.omissionReason="unsupported-shear-reflection-or-degenerate-scale";
            }
            BoxCollider box=c as BoxCollider; SphereCollider sphere=c as SphereCollider; CapsuleCollider capsule=c as CapsuleCollider; MeshCollider mesh=c as MeshCollider;
            if(box!=null) {s.kind="box";s.center=V(box.center);s.size=V(box.size);}
            else if(sphere!=null) {s.kind="sphere";s.center=V(sphere.center);s.radius=sphere.radius;}
            else if(capsule!=null) {s.kind="capsule";s.center=V(capsule.center);s.radius=capsule.radius;s.height=capsule.height;s.direction=capsule.direction;}
            else if(mesh!=null)
            {
                s.kind="convex-mesh";s.convex=mesh.convex;s.cookingOptions=(int)mesh.cookingOptions;
                Mesh source=mesh.sharedMesh;
                if(string.IsNullOrEmpty(s.omissionReason))
                {
                    if(!mesh.convex) s.omissionReason="unsupported-nonconvex-mesh";
                    else if(source==null) s.omissionReason="missing-collider-mesh";
                    else if(!source.isReadable) s.omissionReason="unreadable-collider-mesh";
                    else
                    {
                        int index=-1;
                        if(source.vertexCount<=0 || source.vertexCount>4000000 || source.subMeshCount<=0 || source.subMeshCount>4096) s.omissionReason="collider-mesh-capacity-exceeded";
                        else
                        {
                            bool triangles=true;long indices=0;for(int n=0;n<source.subMeshCount;n++) { if(source.GetTopology(n)!=MeshTopology.Triangles) triangles=false; indices+=source.GetIndexCount(n); }
                            if(!triangles) s.omissionReason="unsupported-collider-mesh-topology";
                            else if(indices>12000000) s.omissionReason="collider-mesh-index-capacity-exceeded";
                            else if(!meshIds.TryGetValue(source,out index))
                            {
                                    Need(copyMesh,"Collider mesh identity changed during capture.");
                                    long totalVertices=source.vertexCount,totalIndices=indices;foreach(var prior in meshes) {totalVertices+=prior.positions.Length/3;foreach(var sub in prior.submeshes) totalIndices+=sub.indices.Length;}
                                    Need(totalVertices<=4000000 && totalIndices<=12000000,"Native assembly aggregate mesh capacity exceeded.");
                                    Vector3[] vertices=source.vertices; var positions=new float[checked(vertices.Length*3)]; for(int n=0;n<vertices.Length;n++) {positions[n*3]=vertices[n].x;positions[n*3+1]=vertices[n].y;positions[n*3+2]=vertices[n].z;}
                                    var subs=new WholeCraftSubmesh[source.subMeshCount]; for(int n=0;n<subs.Length;n++) subs[n]=new WholeCraftSubmesh {indices=source.GetIndices(n)};
                                    index=meshes.Count;meshes.Add(new WholeCraftMeshData {positions=positions,normals=new float[0],uv=new float[0],tangents=new float[0],colors=new float[0],submeshes=subs});meshIds.Add(source,index);
                            }
                        }
                        if(string.IsNullOrEmpty(s.omissionReason)) s.meshIndex=index;
                    }
                }
            }
            else if(string.IsNullOrEmpty(s.omissionReason)) s.omissionReason=c.GetType().FullName=="UnityEngine.WheelCollider"?"unsupported-wheel-collider":"unsupported-collider-type";
            s.supported=string.IsNullOrEmpty(s.omissionReason); return s;
        }
        static bool Equal(object a,object b) { return string.Equals(ReportJson.Encode(a),ReportJson.Encode(b),StringComparison.Ordinal); }
        static void SafeParent(string path)
        {
            for(var d=new DirectoryInfo(path);d!=null;d=d.Parent) if(d.Exists) Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Native assembly output parent is a link.");
        }
        public static string Capture(Vessel vessel,string output,string runId)
        {
            NativeAssemblySceneManifest native;OwnedRuntimeCapturedParts parts;OwnedFlightCapture context;
            return CaptureCore(vessel,output,runId,Environment.GetEnvironmentVariable("CONTINUUM_OWNED_RUNTIME_CAPTURE")=="1",Environment.GetEnvironmentVariable("CONTINUUM_OWNED_FLIGHT_CAPTURE")=="1",true,out native,out parts,out context);
        }
        public static string CaptureOwned(Vessel vessel,string output,string runId,out NativeAssemblySceneManifest native,out OwnedRuntimeCapturedParts parts,out OwnedFlightCapture context)
        {return CaptureCore(vessel,output,runId,true,true,false,out native,out parts,out context);}
        static string CaptureCore(Vessel vessel,string output,string runId,bool captureParts,bool captureFlight,bool includeVisuals,out NativeAssemblySceneManifest capturedNative,out OwnedRuntimeCapturedParts capturedParts,out OwnedFlightCapture capturedContext)
        {
            capturedNative=null;capturedParts=null;capturedContext=null;
            Need(SynchronizationContext.Current!=null && SynchronizationContext.Current.GetType().Name=="UnitySynchronizationContext","Native assembly capture requires Unity main-thread context.");
            Need(vessel!=null && !vessel.packed && vessel.rootPart!=null && vessel.transform!=null && vessel.parts!=null,"Native assembly requires an unpacked live vessel.");
            Need(!string.IsNullOrEmpty(output) && Path.IsPathRooted(output),"Native assembly output must be absolute/new.");
            output=Path.GetFullPath(output); Need(!Directory.Exists(output)&&!File.Exists(output),"Native assembly output already exists."); SafeParent(Path.GetDirectoryName(output));
            long start=Stopwatch.GetTimestamp(); int frame=Time.frameCount; double fixedTime=Time.fixedTime,ut=Planetarium.GetUniversalTime();
            Transform root=vessel.transform; Part rootPart=vessel.rootPart; Vector3 anchor=root.position; Vector3d frameVelocity=Krakensbane.GetFrameVelocity(); var rootWorld=WholeCraftSceneCapture.World(root);
            Part[] parts=vessel.parts.ToArray(); Need(parts.Length>0 && parts.Length<=NativeAssemblySceneFormat.MaxRows,"Native assembly part capacity exceeded.");
            var nativeBodies=new List<Rigidbody>();var bodyIds=new HashSet<int>();var aliases=new List<NativeAssemblyPartAlias>();var partSet=new HashSet<Part>();var bodyOwners=new Dictionary<Rigidbody,Part>();var bodyOwnership=new Dictionary<Rigidbody,string>();
            foreach(Part p in parts) Need(p!=null && ReferenceEquals(p.vessel,vessel) && partSet.Add(p),"Native assembly foreign/repeated part.");
            foreach(Part p in parts)
            {
                aliases.Add(Alias(p));
                foreach(Rigidbody body in new[]{p.rb,p.Rigidbody,p.servoRb}) if(body!=null && bodyIds.Add(Id(body))) {Part ancestor=Owner(body.transform);nativeBodies.Add(body);bodyOwners.Add(body,ancestor!=null&&partSet.Contains(ancestor)?ancestor:p);bodyOwnership.Add(body,ancestor!=null&&partSet.Contains(ancestor)?"part-ancestor":"explicit-part-alias");}
                foreach(Rigidbody body in p.GetComponentsInChildren<Rigidbody>(true)) if(body!=null && bodyIds.Add(Id(body))) {Part ancestor=Owner(body.transform);Need(ancestor!=null&&partSet.Contains(ancestor),"Native descendant body has a foreign part owner.");nativeBodies.Add(body);bodyOwners.Add(body,ancestor);bodyOwnership.Add(body,"part-ancestor");}
            }
            Need(nativeBodies.Count>0 && nativeBodies.Count<=NativeAssemblySceneFormat.MaxRows,"Native assembly body capacity exceeded.");
            var shapes=new List<Collider>();var owners=new List<Part>();var actualOwners=new List<Part>();var ancestryOmissions=new List<string>();var seen=new HashSet<int>();
            Action<Collider,Part> survey=(c,fallback)=>
            {
                if(c==null || !seen.Add(Id(c))) return; Part owner=Owner(c.transform);actualOwners.Add(owner);
                ancestryOmissions.Add(owner==null?"detached-collider-part-ancestry":!partSet.Contains(owner)?"foreign-collider-part-ancestry":null);
                shapes.Add(c);owners.Add(owner!=null&&partSet.Contains(owner)?owner:fallback);
                Need(shapes.Count<=NativeAssemblySceneFormat.MaxRows,"Native assembly collider capacity exceeded.");
            };
            foreach(Part p in parts) foreach(Collider c in p.GetComponentsInChildren<Collider>(true)) survey(c,p);
            foreach(Rigidbody body in nativeBodies) foreach(Collider c in body.GetComponentsInChildren<Collider>(true)) survey(c,bodyOwners[body]);
            var bodies=nativeBodies.Select(b=>Body(b,anchor,bodyOwners[b],bodyOwnership[b])).ToArray();var meshes=new List<WholeCraftMeshData>();var meshIds=new Dictionary<Mesh,int>();var colliders=new NativeAssemblyCollider[shapes.Count];var omissions=new List<NativeAssemblyOmission>();
            var meshReferences=new Mesh[shapes.Count]; var materialReferences=new PhysicMaterial[shapes.Count];
            for(int n=0;n<shapes.Count;n++) { var mesh=shapes[n] as MeshCollider;meshReferences[n]=mesh==null?null:mesh.sharedMesh;materialReferences[n]=shapes[n].sharedMaterial;colliders[n]=Shape(shapes[n],owners[n],bodyIds,meshIds,meshes,true,ancestryOmissions[n]); if(!colliders[n].supported) omissions.Add(new NativeAssemblyOmission {colliderId=Id(shapes[n]),reason=colliders[n].omissionReason}); }
            var endBodies=nativeBodies.Select(b=>Body(b,anchor,bodyOwners[b],bodyOwnership[b])).ToArray();var endAliases=new NativeAssemblyPartAlias[parts.Length];var endShapes=new NativeAssemblyCollider[shapes.Count];
            bool identity=ReferenceEquals(vessel.rootPart,rootPart)&&ReferenceEquals(vessel.transform,root)&&vessel.parts.Count==parts.Length;
            for(int n=0;n<parts.Length;n++) { identity &= ReferenceEquals(vessel.parts[n],parts[n])&&ReferenceEquals(parts[n].vessel,vessel);endAliases[n]=Alias(parts[n]); }
            for(int n=0;n<shapes.Count;n++) { Need(shapes[n]!=null,"Collider destroyed during capture."); var mesh=shapes[n] as MeshCollider; identity &= ReferenceEquals(Owner(shapes[n].transform),actualOwners[n])&&ReferenceEquals(shapes[n].sharedMaterial,materialReferences[n])&&ReferenceEquals(mesh==null?null:mesh.sharedMesh,meshReferences[n]);endShapes[n]=Shape(shapes[n],owners[n],bodyIds,meshIds,meshes,false,ancestryOmissions[n]); }
            var endColliderIds=new HashSet<int>();foreach(Part p in parts) foreach(Collider c in p.GetComponentsInChildren<Collider>(true)) if(c!=null) {endColliderIds.Add(Id(c));Need(endColliderIds.Count<=NativeAssemblySceneFormat.MaxRows,"Native assembly end inventory capacity exceeded.");}
            foreach(Rigidbody body in nativeBodies) foreach(Collider c in body.GetComponentsInChildren<Collider>(true)) if(c!=null) {endColliderIds.Add(Id(c));Need(endColliderIds.Count<=NativeAssemblySceneFormat.MaxRows,"Native assembly end inventory capacity exceeded.");}
            identity &= seen.SetEquals(endColliderIds);
            var endBodyIds=new HashSet<int>();foreach(Part p in parts) {foreach(Rigidbody body in new[]{p.rb,p.Rigidbody,p.servoRb}) if(body!=null) endBodyIds.Add(Id(body));foreach(Rigidbody body in p.GetComponentsInChildren<Rigidbody>(true)) if(body!=null) endBodyIds.Add(Id(body));Need(endBodyIds.Count<=NativeAssemblySceneFormat.MaxRows,"Native assembly end body capacity exceeded.");}
            identity &= bodyIds.SetEquals(endBodyIds);foreach(Rigidbody body in nativeBodies) if(bodyOwnership[body]=="part-ancestor") identity &= ReferenceEquals(Owner(body.transform),bodyOwners[body]);
            int frameEnd=Time.frameCount;double fixedEnd=Time.fixedTime,utEnd=Planetarium.GetUniversalTime();var rootEnd=WholeCraftSceneCapture.World(root);Vector3d frameVelocityEnd=Krakensbane.GetFrameVelocity();int rootEndId=Id(root);
            long copied=Stopwatch.GetTimestamp();
            identity &= Equal(bodies,endBodies)&&Equal(aliases.ToArray(),endAliases)&&Equal(colliders,endShapes)&&Equal(rootWorld,rootEnd)&&Equal(D(frameVelocity),D(frameVelocityEnd));
            int processId;using(var process=Process.GetCurrentProcess()) processId=process.Id;
            var manifest=new NativeAssemblySceneManifest {schema="continuum-native-assembly-scene/v1",runId=runId,vesselId=vessel.id.ToString("D"),engine=Application.unityVersion,processId=processId,unityFrame=frame,unityFrameEnd=frameEnd,rootTransformId=Id(root),rootTransformIdEnd=rootEndId,fixedTime=fixedTime,fixedTimeEnd=fixedEnd,universalTime=ut,universalTimeEnd=utEnd,startedTicks=start,copiedTicks=copied,clockFrequency=Stopwatch.Frequency,epochUnchanged=frame==frameEnd&&fixedTime==fixedEnd&&ut==utEnd,identityUnchanged=identity,nativeAnchor=V(anchor),hierarchyRootWorld=rootWorld,frameVelocity=D(frameVelocity),coordinateFrame="native-world-axes-anchor-relative",nativePoseSource="Unity-Rigidbody-float-physics",hierarchyPoseSource="Unity-float-local-TRS-composed-double",coverageScope="part-owned collider/body descendants including inactive and own/effective/servo body aliases; convex source geometry, not cooked hull/contact/pending-force history",completeColliderCoverage=omissions.Count==0,bodies=bodies,parts=aliases.ToArray(),colliders=colliders,omissions=omissions.ToArray(),geometry="",geometrySha256=""};
            WholeCraftGeometry geometry=meshes.Count==0?null:new WholeCraftGeometry {meshes=meshes.ToArray()};
            string parent=Path.GetDirectoryName(output);Directory.CreateDirectory(parent);string stage=output+".stage-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(stage);
            try
            {
                if(geometry!=null) { string path=Path.Combine(stage,"geometry.bin");WholeCraftSceneFormat.WriteGeometry(path,geometry);manifest.geometry="geometry.bin";manifest.geometrySha256=ScaleCheckpointLoadState.HashFile(path);geometry=WholeCraftSceneFormat.ReadGeometry(path); }
                NativeAssemblySceneFormat.Validate(manifest,geometry);
                string json=ReportJson.Encode(manifest);string manifestPath=Path.Combine(stage,"manifest.json"); using(var stream=new FileStream(manifestPath,FileMode.CreateNew,FileAccess.Write)) using(var writer=new StreamWriter(stream,new UTF8Encoding(false))) writer.Write(json);
                OwnedRuntimeCapturedParts partsResult=null;OwnedFlightCapture contextResult=null;
                if(captureParts)OwnedRuntimeKspCapture.Capture(vessel,manifest,manifestPath,Path.Combine(stage,"owned-parts.json"),out partsResult);
                if(captureFlight)OwnedFlightKspCapture.Capture(vessel,manifest,manifestPath,Path.Combine(stage,"owned-parts.json"),stage,out contextResult,includeVisuals);
                Need(File.ReadAllText(manifestPath)==json,"Native assembly manifest readback differs.");SafeParent(parent);Need(!Directory.Exists(output)&&!File.Exists(output),"Native assembly output appeared during capture.");Directory.Move(stage,output);capturedNative=manifest;capturedParts=partsResult;capturedContext=contextResult;return Path.Combine(output,"manifest.json");
            }
            catch { if(Directory.Exists(stage)) Directory.Delete(stage,true);throw; }
        }
    }
}
