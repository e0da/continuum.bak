using System;
using System.IO;
using KspContinuum;

static class Program
{
    static double[] Identity() { return new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1}; }
    static NativeAssemblySceneManifest Packet()
    {
        return new NativeAssemblySceneManifest {
            schema="continuum-native-assembly-scene/v1",runId="analytical",vesselId="vessel",engine="fixture",geometry="",geometrySha256="",
            coordinateFrame="native-world-axes-anchor-relative",nativePoseSource="Unity-Rigidbody-float-physics",hierarchyPoseSource="Unity-float-local-TRS-composed-double",coverageScope="analytical primitive fixture",
            processId=1,unityFrame=2,unityFrameEnd=2,rootTransformId=3,rootTransformIdEnd=3,fixedTime=.04,fixedTimeEnd=.04,universalTime=100,universalTimeEnd=100,
            startedTicks=10,copiedTicks=20,clockFrequency=1000,epochUnchanged=true,identityUnchanged=true,completeColliderCoverage=true,nativeAnchor=new float[]{1000,2000,3000},hierarchyRootWorld=Identity(),frameVelocity=new double[3],
            bodies=new[]{new NativeAssemblyBody {ownerPartId=1,ownershipSource="part-ancestor",bodyId=10,transformId=11,mass=3,position=new double[]{2,0,0},rotation=new float[]{0,0,0,1},localCenterOfMass=new float[]{0,1,0},worldCenterOfMass=new double[]{2,1,0},principalInertia=new float[]{13,10,5},principalRotation=new float[]{0,0,0,1},velocity=new float[]{2,3,4},angularVelocity=new float[]{0,.5f,0},maxAngularVelocity=7,sleepThreshold=.005f,detectCollisions=true,active=true}},
            parts=new[]{new NativeAssemblyPartAlias {partId=1,partInstanceId=21,name="root",ownBodyId=10,effectiveBodyId=10},new NativeAssemblyPartAlias {partId=2,parentPartId=1,partInstanceId=22,name="physicsless-child",effectiveBodyId=10}},
            colliders=new[]{new NativeAssemblyCollider {colliderId=30,transformId=31,bodyId=10,partId=2,name="actual box",kind="box",poseSource="analytical",meshIndex=-1,localToBody=Identity(),center=new float[]{0,1,0},size=new float[]{2,4,6},enabled=true,active=true,supported=true,contactOffset=.01f}},
            omissions=new NativeAssemblyOmission[0]
        };
    }
    static int checks;
    static void Check(bool pass,string message) { checks++;if(!pass) throw new Exception(message); }
    static void Reject(Action<NativeAssemblySceneManifest> change,string name)
    {
        var m=Packet();change(m);bool rejected=false;try {NativeAssemblySceneFormat.Validate(m);} catch(InvalidDataException) {rejected=true;}Check(rejected,name);
    }
    static void Main()
    {
        var m=Packet();NativeAssemblySceneFormat.Validate(m);Check(m.bodies.Length==1 && m.parts.Length==2,"Aliases must not duplicate mass-bearing bodies.");
        var b=m.bodies[0];
        var aggregate=RigidAssemblyProperties.Combine(new[]{new RigidAssemblyMember(b.bodyId,b.mass,new Vec(b.position[0],b.position[1],b.position[2]),Rotation.Identity,new Vec(0,1,0),new Vec(13,10,5),Rotation.Identity)});
        Check(aggregate.Mass==3 && aggregate.CenterOfMass.X==2 && aggregate.CenterOfMass.Y==1,"Native COM offset maps into the assembly frame.");
        // Independent box inertia m*(other two side lengths squared)/12, about its captured local COM.
        Check(aggregate.Inertia.XX==13 && aggregate.Inertia.YY==10 && aggregate.Inertia.ZZ==5,"Analytical 3kg box tensor.");
        var a=Identity();a[0]=2;a[5]=3;a[10]=4;a[3]=5;Check(NativeAssemblySceneFormat.SupportedAffine(a),"Positive nonuniform orthogonal scale is representable.");
        a[1]=.1;Check(!NativeAssemblySceneFormat.SupportedAffine(a),"Shear must be omitted.");
        a=Identity();a[0]=-1;Check(!NativeAssemblySceneFormat.SupportedAffine(a),"Reflection must be omitted.");
        Reject(x=>x.bodies=new[]{x.bodies[0],x.bodies[0]},"Duplicate native body ID.");
        Reject(x=>x.parts[1].effectiveBodyId=999,"Dangling effective alias.");
        Reject(x=>x.parts[1].servoBodyId=999,"Dangling servo alias.");
        Reject(x=>x.bodies[0].ownerPartId=999,"Dangling native owner.");
        Reject(x=>x.parts[1].parentPartId=999,"Dangling parent.");
        Reject(x=>{x.parts[0].parentPartId=2;x.parts[1].parentPartId=1;},"Parent cycle/no root.");
        Reject(x=>x.bodies[0].principalInertia[1]=float.NaN,"Nonfinite inertia.");
        Reject(x=>x.bodies[0].principalInertia[1]=0,"Zero finite inertia unsupported.");
        Reject(x=>x.bodies[0].rotation=new float[4],"Default quaternion invalid.");
        Reject(x=>x.fixedTimeEnd+=.02,"Epoch drift.");
        Reject(x=>x.universalTimeEnd+=.01,"UT drift.");
        Reject(x=>x.identityUnchanged=false,"Identity drift.");
        Reject(x=>x.colliders[0].bodyId=999,"Dangling supported collider owner.");
        Reject(x=>x.colliders[0].localToBody[0]=-1,"Reflected supported collider.");
        Reject(x=>x.colliders[0].localToBody[1]=.1,"Sheared supported collider.");
        Reject(x=>x.colliders[0].meshIndex=0,"Primitive geometry dependency forbidden.");
        Reject(x=>x.geometry="unexpected.bin","Primitive-only payload binds no geometry file.");
        m=Packet();m.colliders[0].supported=false;m.colliders[0].localToBody[0]=-1;m.colliders[0].omissionReason="unsupported-reflection";m.omissions=new[]{new NativeAssemblyOmission {colliderId=30,reason="unsupported-reflection"}};m.completeColliderCoverage=false;NativeAssemblySceneFormat.Validate(m);Check(m.bodies[0].mass==3,"Omitted shape retains native body mass.");
        Reject(x=>{x.colliders[0].supported=false;x.colliders[0].omissionReason="unsupported";x.completeColliderCoverage=false;},"Missing omission ledger.");
        m=Packet();m.colliders=new NativeAssemblyCollider[0];NativeAssemblySceneFormat.Validate(m);Check(m.bodies.Length==1,"Body without colliders remains represented.");
        Console.WriteLine("Native assembly portable checks passed: "+checks);
    }
}
