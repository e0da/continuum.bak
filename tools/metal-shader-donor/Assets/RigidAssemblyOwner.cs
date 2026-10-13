using System;
using System.Collections.Generic;
using KspContinuum;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RigidAssemblyOwner
{
    public sealed class Member
    {
        public RigidAssemblyMember Properties;
        public readonly Transform Anchor;
        public readonly Collider[] Colliders;
        public Collider Collider { get { return Colliders.Length==0?null:Colliders[0]; } }
        public Member(RigidAssemblyMember properties, Transform anchor, Collider collider)
            :this(properties,anchor,new[] {collider}) {}
        public Member(RigidAssemblyMember properties, Transform anchor, IEnumerable<Collider> colliders)
        { Properties=properties; Anchor=anchor; Colliders=new List<Collider>(colliders).ToArray(); }
    }
    readonly List<Member> members;
    public IList<Member> Members { get { return members.AsReadOnly(); } }
    public Rigidbody Body { get; private set; }
    public RigidAssemblyProperties Properties { get; private set; }
    public int Generation { get; private set; }
    public bool Retired { get; private set; }

    public RigidAssemblyOwner(Scene scene, Vector3 position, Quaternion rotation, IEnumerable<Member> source, int generation)
        :this(scene,position,rotation,source,generation,true) {}

    RigidAssemblyOwner(Scene scene, Vector3 position, Quaternion rotation, IEnumerable<Member> source, int generation,bool attach)
    {
        members=new List<Member>(source);
        var properties=new List<RigidAssemblyMember>(); foreach(var member in members) properties.Add(member.Properties);
        Properties=RigidAssemblyProperties.Combine(properties);
        RequireNativeProperties(Properties);
        var shapes=new HashSet<int>();var anchors=new HashSet<int>();
        foreach(var member in members)
        {
            if(member.Anchor==null||!anchors.Add(member.Anchor.GetInstanceID()))throw new ArgumentException("Distinct retained member anchors required.");
            foreach(var collider in member.Colliders)
                if(collider==null||!collider.transform.IsChildOf(member.Anchor)||!shapes.Add(collider.GetInstanceID()))throw new ArgumentException("Distinct retained shapes below member anchor required.");
        }
        var root=new GameObject("Rigid region generation "+generation); SceneManager.MoveGameObjectToScene(root,scene);
        try
        {
            root.transform.SetPositionAndRotation(position,rotation);
            Body=root.AddComponent<Rigidbody>(); Body.isKinematic=true; Body.useGravity=false;
            Body.drag=0; Body.angularDrag=0; Body.maxAngularVelocity=100;
            Generation=generation;
            PublishProperties();
            if(attach) {Attach();PublishProperties();}
        }
        catch {UnityEngine.Object.Destroy(root);throw;}
    }

    public void Activate(Vector3 velocity,Vector3 angularVelocity)
    { RequireLive(); Body.isKinematic=false; Body.velocity=velocity; Body.angularVelocity=angularVelocity; Body.WakeUp(); }

    void PublishProperties()
    {
        Body.mass=(float)Properties.Mass; Body.centerOfMass=Vector(Properties.CenterOfMass);
        Body.inertiaTensor=Vector(Properties.PrincipalInertia); Body.inertiaTensorRotation=Quaternion(Properties.PrincipalRotation);
    }
    void Attach(){foreach(var member in members)member.Anchor.SetParent(Body.transform,true);}
    static void RequireNativeProperties(RigidAssemblyProperties properties)
    {
        foreach(double value in new[] {properties.Mass,properties.PrincipalInertia.X,properties.PrincipalInertia.Y,properties.PrincipalInertia.Z})
            if(float.IsInfinity((float)value)||(float)value<=0)throw new ArgumentException("Properties outside positive native binary32 domain.");
        foreach(double value in new[] {properties.CenterOfMass.X,properties.CenterOfMass.Y,properties.CenterOfMass.Z})
            if(float.IsInfinity((float)value)||float.IsNaN((float)value))throw new ArgumentException("COM outside native binary32 domain.");
    }

    // This closed transfer preserves P and angular momentum; it does not model fuel-flow work.
    public void TransferMass(int fromId,int toId,double amount)
    {
        RequireLive(); AssemblyModel.Positive(amount);
        Member from=members.Find(m=>m.Properties.Id==fromId),to=members.Find(m=>m.Properties.Id==toId);
        if(from==null||to==null||from==to||amount>=from.Properties.Mass) throw new ArgumentException("Distinct retained members and positive remaining mass required.");
        Vector3 momentum=Body.mass*Body.velocity,angular=AngularMomentumAboutOrigin(Body);
        var updated=new List<RigidAssemblyMember>();
        foreach(var member in members)
        {
            double mass=member.Properties.Mass+(member==from?-amount:member==to?amount:0);
            updated.Add(member.Properties.WithMass(mass,member.Properties.PrincipalInertia*(mass/member.Properties.Mass)));
        }
        var prepared=RigidAssemblyProperties.Combine(updated);
        RequireNativeProperties(prepared);var prior=Properties;Vector3 velocity=Body.velocity,omega=Body.angularVelocity;
        try
        {
            Properties=prepared;PublishProperties(); Body.velocity=momentum/Body.mass;
            Body.angularVelocity=InverseWorldInertia(Body,angular-Vector3.Cross(Body.worldCenterOfMass,momentum));
            Body.WakeUp();for(int i=0;i<members.Count;i++)members[i].Properties=updated[i];
        }
        catch {Properties=prior;PublishProperties();Body.velocity=velocity;Body.angularVelocity=omega;throw;}
    }

    // Caller supplies the already-resolved graph cut; alternate connection paths belong to the graph owner.
    public RigidAssemblyOwner[] Split(Scene scene,Predicate<int> firstRegion)
    {
        RequireLive(); var a=new List<Member>(); var b=new List<Member>();
        foreach(var member in members) (firstRegion(member.Properties.Id)?a:b).Add(member);
        if(a.Count==0||b.Count==0) throw new ArgumentException("Two nonempty regions required.");
        var ap=new List<RigidAssemblyMember>();var bp=new List<RigidAssemblyMember>();
        foreach(var m in a) ap.Add(m.Properties); foreach(var m in b) bp.Add(m.Properties);
        var pa=RigidAssemblyProperties.Combine(ap); var pb=RigidAssemblyProperties.Combine(bp);
        Vector3 va=Body.GetPointVelocity(Body.transform.TransformPoint(Vector(pa.CenterOfMass)));
        Vector3 vb=Body.GetPointVelocity(Body.transform.TransformPoint(Vector(pb.CenterOfMass)));
        Vector3 omega=Body.angularVelocity,position=Body.position,velocity=Body.velocity; UnityEngine.Quaternion rotation=Body.rotation;
        RigidAssemblyOwner ownerA=null,ownerB=null;
        try
        {
            ownerA=new RigidAssemblyOwner(scene,position,rotation,a,Generation+1,false);
            ownerB=new RigidAssemblyOwner(scene,position,rotation,b,Generation+1,false);
            Body.isKinematic=true;
            ownerA.Attach();ownerB.Attach();ownerA.PublishProperties();ownerB.PublishProperties();
            ownerA.Activate(va,omega);ownerB.Activate(vb,omega);
        }
        catch
        {
            if(ownerA!=null)ownerA.Body.isKinematic=true;if(ownerB!=null)ownerB.Body.isKinematic=true;
            Attach();PublishProperties();Activate(velocity,omega);
            if(ownerA!=null)UnityEngine.Object.Destroy(ownerA.Body.gameObject);if(ownerB!=null)UnityEngine.Object.Destroy(ownerB.Body.gameObject);
            throw;
        }
        Retired=true; members.Clear(); UnityEngine.Object.Destroy(Body.gameObject);
        return new[] {ownerA,ownerB};
    }

    void RequireLive() { if(Retired||Body==null) throw new InvalidOperationException("Rigid region retired."); }
    public static Vector3 AngularMomentumAboutOrigin(Rigidbody body)
    { return WorldInertia(body,body.angularVelocity)+Vector3.Cross(body.worldCenterOfMass,body.mass*body.velocity); }
    public static Vector3 WorldInertia(Rigidbody body,Vector3 value)
    {
        UnityEngine.Quaternion q=body.rotation*body.inertiaTensorRotation;
        return q*Vector3.Scale(body.inertiaTensor,UnityEngine.Quaternion.Inverse(q)*value);
    }
    public static Vector3 InverseWorldInertia(Rigidbody body,Vector3 value)
    {
        UnityEngine.Quaternion q=body.rotation*body.inertiaTensorRotation; Vector3 local=UnityEngine.Quaternion.Inverse(q)*value,i=body.inertiaTensor;
        return q*new Vector3(local.x/i.x,local.y/i.y,local.z/i.z);
    }
    public static Vector3 Vector(Vec value) { return new Vector3((float)value.X,(float)value.Y,(float)value.Z); }
    public static UnityEngine.Quaternion Quaternion(Rotation value) { return new UnityEngine.Quaternion((float)value.X,(float)value.Y,(float)value.Z,(float)value.W); }
}
