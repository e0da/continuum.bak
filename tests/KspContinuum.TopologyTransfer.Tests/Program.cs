using System;
using KspContinuum;

static class Program
{
    const double Budget=1e-10;
    static double Dot(Vec a,Vec b){return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
    static void Need(bool value,string message){if(!value)throw new Exception(message);}
    static void Near(double a,double b,string label){Need(Math.Abs(a-b)<=Budget*Math.Max(1,Math.Max(Math.Abs(a),Math.Abs(b))),label+": "+a+" vs "+b);}
    static void Near(Vec a,Vec b,string label){Near(a.X,b.X,label+" X");Near(a.Y,b.Y,label+" Y");Near(a.Z,b.Z,label+" Z");}
    static readonly Rotation Frame=Rotation.AxisAngle(new Vec(1,2,-1),.63);
    static readonly Vec Root=new Vec(2,-4,8),Velocity=new Vec(.5,-.25,.75),Omega=new Vec(.125,-.25,.375);
    static OwnedRuntimeWorld Make(Vec translation,Vec boost)
    {
        var parts=new[]{new OwnedRuntimePartDefinition(1,"a",10,new Vec(),Rotation.Identity,100),new OwnedRuntimePartDefinition(2,"b",20,new Vec(),Rotation.Identity,100),new OwnedRuntimePartDefinition(3,"c",30,new Vec(),Rotation.Identity,100)};
        var members=new[]{
            new RigidAssemblyMember(0,2,new Vec(-2,.5,1),Rotation.AxisAngle(new Vec(1,0,1),.3),new Vec(.25,-.125,.5),new Vec(1,2,3),Rotation.AxisAngle(new Vec(0,1,1),.2)),
            new RigidAssemblyMember(1,3,new Vec(1,2,-.5),Rotation.AxisAngle(new Vec(0,1,0),-.4),new Vec(-.5,.25,.125),new Vec(2,4,5),Rotation.AxisAngle(new Vec(1,1,0),.5)),
            new RigidAssemblyMember(2,5,new Vec(3,-1,2),Rotation.AxisAngle(new Vec(1,0,0),.25),new Vec(.125,.5,-.25),new Vec(3,6,7),Rotation.AxisAngle(new Vec(0,0,1),-.3))};
        var bodies=new[]{new OwnedRuntimeBodyDefinition(10,members[0]),new OwnedRuntimeBodyDefinition(20,members[1]),new OwnedRuntimeBodyDefinition(30,members[2])};
        var definition=new OwnedRuntimeDefinition("topology-transfer",0,parts,bodies,new OwnedRuntimeResourceDefinition[0]);
        var properties=RigidAssemblyProperties.Combine(members);
        var motion=new OwnedRuntimeMotion{RootPosition=Root+translation,Orientation=Frame,Velocity=Velocity+boost,AngularMomentum=Frame.Rotate(properties.Inertia.Multiply(Frame.Inverse.Rotate(Omega)))};
        return new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new[]{new OwnedRuntimeConnection(11,1,2),new OwnedRuntimeConnection(12,2,3)}),motion);
    }
    struct Totals{internal Vec P,L;internal double E;}
    static Totals Measure(OwnedRuntimeWorld world,Vec translation,Vec boost)
    {
        var result=new Totals();using(var read=world.AcquireRead())
        for(int r=0;r<read.RegionCount;r++)
        {
            var state=read.GetRegion(r);var motion=state.Motion;
            Vec center=(motion.RootPosition+(Root+translation)*-1)+motion.Orientation.Rotate(state.Properties.CenterOfMass);
            Vec v=motion.Velocity+boost*-1,p=v*state.Properties.Mass;
            Vec omega=OwnedRuntimeDynamics.AngularVelocity(state.Properties,motion.Orientation,motion.AngularMomentum);
            result.P=result.P+p;result.L=result.L+motion.AngularMomentum+Vec.Cross(center,p);
            result.E+=.5*state.Properties.Mass*Dot(v,v)+.5*Dot(omega,motion.AngularMomentum);
        }
        return result;
    }
    static void Compare(OwnedRuntimeWorld a,OwnedRuntimeWorld b,Vec translation,Vec boost)
    {
        using(var x=a.AcquireRead())using(var y=b.AcquireRead())
        {
            Need(x.RegionCount==y.RegionCount,"Region count differs.");
            for(int r=0;r<x.RegionCount;r++)
            {
                var p=x.GetRegion(r);var q=y.GetRegion(r);Need(p.Id==q.Id,"Stable region identity differs.");
                Near(p.Motion.RootPosition,q.Motion.RootPosition+translation*-1,"Translated root");Near(p.Motion.Velocity,q.Motion.Velocity+boost*-1,"Boosted COM velocity");Near(p.Motion.AngularMomentum,q.Motion.AngularMomentum,"World angular momentum");
                Near(p.Properties.CenterOfMass,q.Properties.CenterOfMass,"Local COM");Near(p.Properties.Mass,q.Properties.Mass,"Mass");
                Near(Math.Abs(Rotation.Dot(p.Motion.Orientation,q.Motion.Orientation)),1,"Frame rotation");
            }
        }
        Near(a.LastTopologyRemovedEnergy,b.LastTopologyRemovedEnergy,"Galilean removed energy");
    }
    static void Run(Vec translation,Vec boost)
    {
        var plain=Make(new Vec(),new Vec());var shifted=Make(translation,boost);var initial=Measure(shifted,translation,boost);
        var old=shifted.CaptureSnapshot();
        plain.Split(plain.Stamp,11);shifted.Split(shifted.Stamp,11);Compare(plain,shifted,translation,boost);
        var split=Measure(shifted,translation,boost);Near(initial.P,split.P,"Split P");Near(initial.L,split.L,"Split L");Near(initial.E,split.E,"Split energy");Near(shifted.LastTopologyRemovedEnergy,0,"Rigid split removes no energy");
        Need(old.RegionMotion.Length==1&&old.RegionMotion[0].RootPosition.X==Root.X+translation.X&&old.Connections.Length==2,"Source snapshot mutated.");
        plain=ReframeAndSlip(plain);shifted=ReframeAndSlip(shifted);Compare(plain,shifted,translation,boost);
        var beforeJoin=Measure(shifted,translation,boost);
        plain.Dock(plain.Stamp,99,1,2);shifted.Dock(shifted.Stamp,99,1,2);Compare(plain,shifted,translation,boost);
        var joined=Measure(shifted,translation,boost);Near(beforeJoin.P,joined.P,"Rejoin P");Near(beforeJoin.L,joined.L,"Rejoin L");Near(beforeJoin.E-joined.E,shifted.LastTopologyRemovedEnergy,"Removed relative energy");Need(shifted.LastTopologyRemovedEnergy>0,"Slip join must remove energy.");
        var stamp=shifted.Stamp;bool rejected=false;try{shifted.Split(stamp,999);}catch(ArgumentException){rejected=true;}Need(rejected&&shifted.Stamp.Matches(stamp),"Rejected topology changed source.");
    }
    static OwnedRuntimeWorld ReframeAndSlip(OwnedRuntimeWorld world)
    {
        var snapshot=world.CaptureSnapshot();Vec shift=new Vec(8,-4,2);
        var motion=snapshot.RegionMotion[1];Vec localShift=motion.Orientation.Inverse.Rotate(shift);
        motion.RootPosition=motion.RootPosition+shift;motion.Velocity=motion.Velocity+new Vec(.25,.125,-.5);snapshot.RegionMotion[1]=motion;
        for(int b=0;b<snapshot.BodyMembers.Length;b++)if(world.Topology.RegionOfBody(b)==1)
        {var member=snapshot.BodyMembers[b];snapshot.BodyMembers[b]=new RigidAssemblyMember(member.Id,member.Mass,member.LocalPosition+localShift*-1,member.LocalRotation,member.LocalCenterOfMass,member.PrincipalInertia,member.PrincipalRotation);}
        return OwnedRuntimeWorld.RestoreSnapshot(snapshot);
    }
    static void Main(){Run(new Vec(1000000,-2000000,3000000),new Vec());Run(new Vec(1000000,-2000000,3000000),new Vec(8192,-4096,16384));Console.WriteLine("PASS local topology transfer: translated/boosted split/rejoin, P/L/energy, nontrivial frames and source immutability.");}
}
