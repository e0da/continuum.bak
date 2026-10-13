using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using KspContinuum;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class NativeAssemblySceneImport
{
    [Serializable] public sealed class ColliderBinding {public int sourceColliderId,memberId,colliderId;}
    [Serializable] public sealed class Evidence
    {
        public string manifestSha256,geometrySha256,runId,vesselId;
        public string frame="native-world-axes-anchor-relative; laboratory Galilean rest frame subtracts source aggregate COM velocity";
        public string model="One rigid region; no original joints, pending forces, contacts or solver history. Native contact simulation disabled; admitted collision geometry retained.";
        public int processId,sourceBodies,importedBodies,importedColliders,omittedColliders;
        public int[] sourceBodyIds;
        public ColliderBinding[] colliderBindings;
        public bool completeColliderCoverage;
        public double[] sourceLinearMomentum,sourceAngularMomentumAboutCenter,sourceVelocity,sourceAngularVelocity;
        public double sourceKineticEnergy,rigidKineticEnergy,removedKineticEnergy;
        public double maxReconstructedNativeComErrorMeters;
    }
    public RigidAssemblyOwner Owner {get;private set;}
    public Evidence Receipt {get;private set;}
    public Bounds Bounds {get;private set;}
    readonly List<UnityEngine.Object> resources=new List<UnityEngine.Object>();
    readonly Dictionary<int,Mesh> meshes=new Dictionary<int,Mesh>();
    public void ReleaseResources(){foreach(var resource in resources)UnityEngine.Object.Destroy(resource);resources.Clear();}
    static string Hash(string path){using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
    static Vec V(float[] a){return new Vec(a[0],a[1],a[2]);}
    static Vec V(double[] a){return new Vec(a[0],a[1],a[2]);}
    static Rotation Q(float[] a){return new Rotation(a[0],a[1],a[2],a[3]);}
    static double[] A(Vec v){return new[]{v.X,v.Y,v.Z};}

    public static NativeAssemblySceneImport Load(string path,Scene scene)
    {
        if(!Path.IsPathRooted(path)||!File.Exists(path)||new FileInfo(path).Length>32*1024*1024)throw new InvalidDataException("Bounded private absolute physics manifest required.");
        var manifest=JsonUtility.FromJson<NativeAssemblySceneManifest>(File.ReadAllText(path));
        WholeCraftGeometry geometry=null;
        if(manifest==null)throw new InvalidDataException("Physics manifest absent.");
        if(!string.IsNullOrEmpty(manifest.geometry))
        {
            MultiscaleSceneFormat.PortablePath(manifest.geometry);
            string file=Path.Combine(Path.GetDirectoryName(path),manifest.geometry);
            if(Hash(file)!=manifest.geometrySha256)throw new InvalidDataException("Captured collider geometry digest differs.");
            geometry=WholeCraftSceneFormat.ReadGeometry(file);
        }
        NativeAssemblySceneFormat.Validate(manifest,geometry);
        var result=new NativeAssemblySceneImport();
        var bodies=new List<NativeAssemblyBody>();foreach(var b in manifest.bodies)if(b.active&&!b.isKinematic)bodies.Add(b);
        bodies.Sort((a,b)=>a.bodyId.CompareTo(b.bodyId));
        if(bodies.Count<2)throw new InvalidDataException("Real transaction lab requires at least two dynamic members.");
        var motions=new List<RigidAssemblyMotion>();var members=new List<RigidAssemblyOwner.Member>();var nativeIds=new List<int>();
        double comError=0;
        for(int i=0;i<bodies.Count;i++)
        {
            var b=bodies[i];if(b.constraints!=0)throw new InvalidDataException("Constrained body is outside free rigid import model.");
            nativeIds.Add(b.bodyId);
            var member=new RigidAssemblyMember(i,b.mass,V(b.position),Q(b.rotation),V(b.localCenterOfMass),V(b.principalInertia),Q(b.principalRotation));
            motions.Add(new RigidAssemblyMotion(member,V(b.velocity),V(b.angularVelocity)));
            Vec center=member.LocalPosition+member.LocalRotation.Rotate(member.LocalCenterOfMass),native=V(b.worldCenterOfMass);
            comError=Math.Max(comError,Math.Sqrt((center.X-native.X)*(center.X-native.X)+(center.Y-native.Y)*(center.Y-native.Y)+(center.Z-native.Z)*(center.Z-native.Z)));
        }
        var projection=RigidAssemblyProjection.Project(motions);
        var createdAnchors=new List<GameObject>();
        try
        {
            int count=0;bool hasBounds=false;var allBounds=new Bounds();var bindings=new List<ColliderBinding>();
            for(int i=0;i<bodies.Count;i++)
            {
                var property=motions[i].Member;var anchor=new GameObject("Captured body "+bodies[i].bodyId);SceneManager.MoveGameObjectToScene(anchor,scene);
                createdAnchors.Add(anchor);
                anchor.transform.SetPositionAndRotation(RigidAssemblyOwner.Vector(property.LocalPosition),RigidAssemblyOwner.Quaternion(property.LocalRotation));
                var shapes=new List<Collider>();
                foreach(var row in manifest.colliders)
                {
                    if(!row.supported||row.bodyId!=bodies[i].bodyId)continue;
                    Collider collider=result.Shape(row,anchor.transform,geometry,i);
                    shapes.Add(collider);count++;
                    bindings.Add(new ColliderBinding {sourceColliderId=row.colliderId,memberId=i,colliderId=collider.GetInstanceID()});
                    if(row.active)foreach(var renderer in collider.GetComponentsInChildren<Renderer>()){if(hasBounds)allBounds.Encapsulate(renderer.bounds);else{allBounds=renderer.bounds;hasBounds=true;}}
                }
                members.Add(new RigidAssemblyOwner.Member(property,anchor.transform,shapes));
            }
            result.Owner=new RigidAssemblyOwner(scene,Vector3.zero,UnityEngine.Quaternion.identity,members,0);
            result.Owner.Body.detectCollisions=false;
            result.Owner.Activate(Vector3.zero,RigidAssemblyOwner.Vector(projection.AngularVelocity));
            result.Bounds=hasBounds?allBounds:new Bounds(RigidAssemblyOwner.Vector(projection.Properties.CenterOfMass),Vector3.one);
            result.Receipt=new Evidence {manifestSha256=Hash(path),geometrySha256=manifest.geometrySha256,runId=manifest.runId,vesselId=manifest.vesselId,processId=manifest.processId,
                sourceBodies=manifest.bodies.Length,importedBodies=bodies.Count,sourceBodyIds=nativeIds.ToArray(),importedColliders=count,omittedColliders=manifest.colliders.Length-count,
                colliderBindings=bindings.ToArray(),maxReconstructedNativeComErrorMeters=comError,
                completeColliderCoverage=manifest.completeColliderCoverage&&count==manifest.colliders.Length,
                sourceLinearMomentum=A(projection.LinearMomentum),sourceAngularMomentumAboutCenter=A(projection.AngularMomentumAboutCenter),sourceVelocity=A(projection.Velocity),sourceAngularVelocity=A(projection.AngularVelocity),
                sourceKineticEnergy=projection.SourceKineticEnergy,rigidKineticEnergy=projection.RigidKineticEnergy,removedKineticEnergy=projection.RemovedKineticEnergy};
            return result;
        }
        catch
        {
            if(result.Owner!=null&&result.Owner.Body!=null){result.Owner.Body.isKinematic=true;UnityEngine.Object.Destroy(result.Owner.Body.gameObject);}
            foreach(var anchor in createdAnchors)if(anchor!=null)UnityEngine.Object.Destroy(anchor);
            result.ReleaseResources();throw;
        }
    }
    Collider Shape(NativeAssemblyCollider row,Transform anchor,WholeCraftGeometry geometry,int colorIndex)
    {
        var game=new GameObject("Captured collider "+row.colliderId+" "+row.kind);game.layer=row.layer;game.transform.SetParent(anchor,false);
        double[] a=row.localToBody;var x=new Vector3((float)a[0],(float)a[4],(float)a[8]);var y=new Vector3((float)a[1],(float)a[5],(float)a[9]);var z=new Vector3((float)a[2],(float)a[6],(float)a[10]);
        game.transform.localPosition=new Vector3((float)a[3],(float)a[7],(float)a[11]);game.transform.localRotation=UnityEngine.Quaternion.LookRotation(z.normalized,y.normalized);game.transform.localScale=new Vector3(x.magnitude,y.magnitude,z.magnitude);
        Collider collider;Mesh mesh=null;
        if(row.kind=="convex-mesh")
        {
            if(!meshes.TryGetValue(row.meshIndex,out mesh))
            {
                var source=geometry.meshes[row.meshIndex];mesh=new Mesh {indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};resources.Add(mesh);meshes.Add(row.meshIndex,mesh);
                var positions=new Vector3[source.positions.Length/3];for(int i=0;i<positions.Length;i++)positions[i]=new Vector3(source.positions[3*i],source.positions[3*i+1],source.positions[3*i+2]);
                mesh.vertices=positions;var triangles=new List<int>();foreach(var sub in source.submeshes)triangles.AddRange(sub.indices);mesh.triangles=triangles.ToArray();mesh.RecalculateNormals();mesh.RecalculateBounds();
            }
            var shape=game.AddComponent<MeshCollider>();shape.cookingOptions=(MeshColliderCookingOptions)row.cookingOptions;shape.sharedMesh=mesh;shape.convex=true;collider=shape;
            if(shape.sharedMesh==null)throw new InvalidDataException("Native mesh cooking left no shape.");
            game.AddComponent<MeshFilter>().sharedMesh=mesh;
        }
        else if(row.kind=="box"){var shape=game.AddComponent<BoxCollider>();shape.center=RigidAssemblyOwner.Vector(V(row.center));shape.size=RigidAssemblyOwner.Vector(V(row.size));collider=shape;}
        else if(row.kind=="sphere"){var shape=game.AddComponent<SphereCollider>();shape.center=RigidAssemblyOwner.Vector(V(row.center));shape.radius=row.radius;collider=shape;}
        else if(row.kind=="capsule"){var shape=game.AddComponent<CapsuleCollider>();shape.center=RigidAssemblyOwner.Vector(V(row.center));shape.radius=row.radius;shape.height=row.height;shape.direction=row.direction;collider=shape;}
        else throw new InvalidDataException("Unadmitted collider type.");
        collider.enabled=row.enabled;collider.isTrigger=row.trigger;collider.contactOffset=row.contactOffset;
        if(row.materialPresent){var material=new PhysicMaterial {dynamicFriction=row.dynamicFriction,staticFriction=row.staticFriction,bounciness=row.bounciness,frictionCombine=(PhysicMaterialCombine)row.frictionCombine,bounceCombine=(PhysicMaterialCombine)row.bounceCombine};resources.Add(material);collider.sharedMaterial=material;}
        var visible=game;
        if(mesh==null)
        {
            visible=GameObject.CreatePrimitive(row.kind=="box"?PrimitiveType.Cube:row.kind=="sphere"?PrimitiveType.Sphere:PrimitiveType.Capsule);UnityEngine.Object.Destroy(visible.GetComponent<Collider>());visible.transform.SetParent(game.transform,false);
            visible.transform.localPosition=RigidAssemblyOwner.Vector(V(row.center));
            visible.transform.localScale=row.kind=="box"?RigidAssemblyOwner.Vector(V(row.size)):row.kind=="sphere"?Vector3.one*(2*row.radius):new Vector3(2*row.radius,row.height*.5f,2*row.radius);
            if(row.kind=="capsule")visible.transform.localRotation=row.direction==0?UnityEngine.Quaternion.Euler(0,0,90):row.direction==2?UnityEngine.Quaternion.Euler(90,0,0):UnityEngine.Quaternion.identity;
        }
        var draw=visible.GetComponent<Renderer>()??visible.AddComponent<MeshRenderer>();var color=new Material(Shader.Find("Continuum/FixtureOpaque"));resources.Add(color);
        color.color=Color.HSVToRGB((colorIndex*.6180339f)%1,.48f,.9f);draw.sharedMaterial=color;game.SetActive(row.active);
        return collider;
    }
}
