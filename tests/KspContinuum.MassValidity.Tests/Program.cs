using System;
using KspContinuum;

class Program
{
    static void Need(bool condition,string message) { if(!condition)throw new Exception(message); }
    static void Close(double actual,double expected,string message) { Need(Math.Abs(actual-expected)<1e-12,message); }
    static OwnedRuntimeDefinition Definition(long revision=0,double dryMass=2)
    {
        return new OwnedRuntimeDefinition("mass-validity",revision,
            new[]{new OwnedRuntimePartDefinition(1,"fuel-offset",10,new Vec(1,0,0),Rotation.Identity,100)},
            new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,dryMass,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity))},
            new[]{new OwnedRuntimeResourceDefinition(1,1,"Fuel",2,1,2),new OwnedRuntimeResourceDefinition(2,1,"EC",10,0,10)});
    }
    static void Main()
    {
        var d=Definition();var world=new OwnedRuntimeWorld(d,OwnedRuntimeTopology.Compile(d,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion {Orientation=Rotation.Identity});
        RigidAssemblyProperties initial;
        using(var read=world.AcquireRead()) { initial=read.GetRegion(0).Properties;Close(initial.Mass,4,"Initial wet mass");Close(initial.CenterOfMass.X,.5,"Initial point COM");Close(initial.Inertia.YY,4,"Initial point tensor"); }
        using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0)))
        {
            step.RefreshMassProperties();Need(ReferenceEquals(initial,step.GetRegion(0).Properties),"Redundant refresh rebuilt unchanged properties.");
            step.SetResourceQuantity(1,9);step.RefreshMassProperties();Need(ReferenceEquals(initial,step.GetRegion(0).Properties),"Massless EC rebuilt physical properties.");
            step.SetRegionMotion(0,new OwnedRuntimeMotion {Orientation=Rotation.Identity,Velocity=new Vec(2,3,4),AngularMomentum=new Vec(0,0,5)});
            step.SetResourceQuantity(0,1);step.Commit();
        }
        using(var read=world.AcquireRead())
        {
            var r=read.GetRegion(0);Close(r.Properties.Mass,3,"Post-refresh public fuel edit was not refreshed at commit.");Close(r.Properties.CenterOfMass.X,1d/3,"Changed COM");Close(r.Properties.Inertia.YY,11d/3,"Changed off-center tensor");
            Close(r.Motion.Velocity.Y,3-1d/6,"Material-point velocity transfer");Close(r.Motion.AngularMomentum.Z,14d/3,"Angular velocity transfer");
        }
        var snapshot=world.CaptureSnapshot();var restored=OwnedRuntimeWorld.RestoreSnapshot(snapshot);
        using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0))) {step.SetResourceQuantity(0,0);step.RefreshMassProperties();}
        using(var read=world.AcquireRead())Close(read.GetRegion(0).Properties.Mass,3,"Rejected candidate changed committed mass.");
        foreach(var w in new[]{world,restored})using(var step=w.BeginStep(w.Stamp,.02,new OwnedRuntimeInput(0))) {step.SetResourceQuantity(0,.5);step.RefreshMassProperties();step.Commit();}
        using(var a=world.AcquireRead())using(var b=restored.AcquireRead())
        {Need(a.GetRegion(0).Properties.Mass==b.GetRegion(0).Properties.Mass&&a.GetRegion(0).Properties.Inertia.YY==b.GetRegion(0).Properties.Inertia.YY,"Restored/rejected-copy continuation diverged.");Close(a.GetRegion(0).Properties.Mass,2.5,"Continuation mass");}
        using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0)))
        {
            step.RefreshMassProperties();bool rejected=false;try {step.SetResourceQuantity(1,double.NaN);}catch(ArgumentException){rejected=true;}Need(rejected,"Nonfinite massless quantity accepted.");
            rejected=false;try {step.SetResourceQuantity(1,11);}catch(ArgumentException){rejected=true;}Need(rejected,"Out-of-domain massless quantity accepted.");
        }
        world.Reconfigure(world.Stamp,Definition(1,3));using(var read=world.AcquireRead())Close(read.GetRegion(0).Properties.Mass,3.5,"Definition change reused stale mass validity.");
        Console.WriteLine("PASS mass validity: unchanged and massless reuse, post-refresh fuel edit, independent offset COM/tensor/motion, rollback/restore continuation, resource domain and definition invalidation.");
    }
}
