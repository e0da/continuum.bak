using System;
using KspContinuum;

static class Program
{
    sealed class Fixture {internal OwnedRuntimeWorld World;internal OwnedRuntimeSystems Systems;}
    static void Need(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Bits(double a,double b,string message){Need(BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b),message);}
    static Fixture Make(double first=.25,bool wheels=false,bool mixture=false,double secondSpecies=1)
    {
        var parts=new[]{new OwnedRuntimePartDefinition(1,"first",10,new Vec(),Rotation.Identity,100),new OwnedRuntimePartDefinition(2,"second",20,new Vec(),Rotation.Identity,100)};
        var bodies=new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity)),new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(1,1,new Vec(1,0,0),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity))};
        var resources=new[]{new OwnedRuntimeResourceDefinition(100,1,"Fuel",200,1,first),new OwnedRuntimeResourceDefinition(101,2,"Fuel",200,1,100),new OwnedRuntimeResourceDefinition(102,1,"Other",10,1,secondSpecies),new OwnedRuntimeResourceDefinition(103,1,"ElectricCharge",10,0,1)};
        var definition=new OwnedRuntimeDefinition("deadline-oracle",0,parts,bodies,resources,new long[]{1,2});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new[]{new OwnedRuntimeConnection(42,1,2)}),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var actuator=wheels?new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(1,0,0),1,-1,0,3,1,0,10),new OwnedRuntimeActuatorSpec(1,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(1,0,0),2,-1,0,3,2,0,20)}:
            new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(1,0,0),9.80665,mixture?new[]{"Fuel","Other"}:new[]{"Fuel"},mixture?new[]{.5,.5}:new[]{1d},1,3,.1)};
        var systems=new OwnedRuntimeSystems(world,actuator,new OwnedRuntimeSurfaceSpec[0],null,null,false,wheels?new[]{0,1}:new[]{-1});systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);systems.SetDeadlineStrategy(world.Stamp,OwnedRuntimeDeadlineStrategy.Retained);
        return new Fixture{World=world,Systems=systems};
    }
    static OwnedRuntimeControls Engine(){return new OwnedRuntimeControls{Throttle=1,EnginesEnabled=true};}
    static OwnedRuntimeControls Wheel(double sign=1){return new OwnedRuntimeControls{Attitude=new Vec(sign,0,0),WheelsEnabled=true};}
    static void Compare(Fixture f,OwnedRuntimeControls controls,double horizon,bool expectReached)
    {
        var stamp=f.World.Stamp;var before=f.World.CaptureSnapshot();
        var fast=f.Systems.FindActuatorBoundary(stamp,controls,horizon);var oracle=f.Systems.FindActuatorBoundaryExactOracle(stamp,controls,horizon);
        Need(fast.Reached==oracle.Reached&&fast.Reached==expectReached,"Reached status differs.");Bits(fast.DeltaSeconds,oracle.DeltaSeconds,"Ordinary frontier must match bounded reference bits.");Need(fast.Actuator==oracle.Actuator&&fast.Resource==oracle.Resource&&fast.Kind==oracle.Kind,"Ordered boundary identity differs.");
        Need(stamp.Matches(f.World.Stamp),"Deadline cannot publish physical state.");var after=f.World.CaptureSnapshot();
        for(int i=0;i<before.ResourceQuantity.Length;i++)Bits(before.ResourceQuantity[i],after.ResourceQuantity[i],"Resource mutated by deadline.");for(int i=0;i<before.ProviderState.Length;i++)Bits(before.ProviderState[i],after.ProviderState[i],"Provider mutated by deadline.");
        Need(fast.SourceStamp.Matches(stamp)&&fast.ConfigurationRevision==f.Systems.ConfigurationRevision,"Full current frontier must bind result.");
    }
    static void Main()
    {
        var tank=Make();Compare(tank,Engine(),1,true);var fast=tank.Systems.FindActuatorBoundary(tank.World.Stamp,Engine(),1);
        Need(fast.Resource==0&&fast.DeltaSeconds<=.25&&fast.Certification=="full-rate-adjacent-original-predicate","First tank, not pool-empty, is the event.");Need(fast.ProbeCount<16,"Normal seed must avoid64 probes.");
        long builds=tank.Systems.Counters.DeadlinePlanBuilds;
        using(var step=tank.World.BeginStep(tank.World.Stamp,.01,new OwnedRuntimeInput(1))){tank.Systems.Step(step,new OwnedRuntimeEnvironment("vacuum",0,tank.World.Stamp.Time,0,300,new Vec(),new Vec()),Engine());step.Commit();}
        Compare(tank,Engine(),1,true);Need(tank.Systems.Counters.DeadlinePlanBuilds==builds&&tank.Systems.Counters.DeadlinePlanHits>0,"Committed inventory refresh must retain rate plan.");
        var wheel=Make(wheels:true);Compare(wheel,Wheel(),1,true);var w=wheel.Systems.FindActuatorBoundary(wheel.World.Stamp,Wheel(),1);Need(w.Resource==3&&Math.Abs(w.DeltaSeconds-1d/3)<1e-15,"Shared EC cumulative authored demand.");
        using(var step=wheel.World.BeginStep(wheel.World.Stamp,.001,new OwnedRuntimeInput())){step.SetProviderState(0,-10);step.SetProviderState(1,-20);step.Commit();}
        Compare(wheel,Wheel(),1,false);Compare(wheel,Wheel(-1),1,true);
        var rounding=Make(wheels:true);rounding.Systems.SetEnabled(1,false);
        double inward=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(10d)-1);
        using(var step=rounding.World.BeginStep(rounding.World.Stamp,.001,new OwnedRuntimeInput())){step.SetProviderState(0,-inward);step.Commit();}
        Compare(rounding,Wheel(),1e-12,true);
        var rounded=rounding.Systems.FindActuatorBoundary(rounding.World.Stamp,Wheel(),1e-12);
        Need(rounded.Certification=="full-rate-adjacent-original-predicate"&&rounded.ProbeCount<24,"Large-base wheel rounding cell must certify locally.");
        Need(rounded.DeltaSeconds<(10-inward)*.75&&rounded.PredictionAvailable&&rounded.PredictedKind=="wheel-capacity","Capacity rounding threshold precedes continuous headroom root.");
        Compare(Make(mixture:true,secondSpecies:0),Engine(),1,false);Compare(Make(first:0),Engine(),1,false);
        var tiny=Make(first:1e-100);var exact=tiny.Systems.FindActuatorBoundaryExactOracle(tiny.World.Stamp,Engine(),1);var certified=tiny.Systems.FindActuatorBoundary(tiny.World.Stamp,Engine(),1);
        Need(certified.Reached&&certified.Certification=="full-rate-adjacent-original-predicate"&&certified.DeltaSeconds<exact.DeltaSeconds,"Tiny root distinguishes adjacent certificate from bounded64 upper.");
        var subnormal=Make(first:double.Epsilon);var fallback=subnormal.Systems.FindActuatorBoundary(subnormal.World.Stamp,Engine(),1);Need(fallback.Certification=="fallback-bounded-64-bisection-upper"&&fallback.FallbackReason!=null&&subnormal.Systems.Counters.DeadlineAdmissionFallbacks==1,"Uncertified numeric regime must retain counted explicit oracle fallback.");
        var flow=Make();Compare(flow,Engine(),1,true);long initial=flow.Systems.Counters.DeadlinePlanBuilds;flow.Systems.SetResourceFlowEnabled(0,false);Compare(flow,Engine(),1,false);Need(flow.Systems.Counters.DeadlinePlanBuilds>initial,"Flow revision invalidates rates.");
        flow.World.Split(flow.World.Stamp,42);Compare(flow,Engine(),1,false);flow.World.SetConnectionLocked(flow.World.Stamp,42,true);Compare(flow,Engine(),1,false);
        var snapshot=flow.World.CaptureSnapshot();using(var step=flow.World.BeginStep(flow.World.Stamp,.01,new OwnedRuntimeInput(1))){flow.Systems.Step(step,new OwnedRuntimeEnvironment("vacuum",0,flow.World.Stamp.Time,0,300,new Vec(),new Vec()),Engine());}
        for(int i=0;i<snapshot.ResourceQuantity.Length;i++)Bits(snapshot.ResourceQuantity[i],flow.World.CaptureSnapshot().ResourceQuantity[i],"Canceled interval preserves deadline inventory.");
        Compare(flow,new OwnedRuntimeControls(),1,false);bool stale=false;try{flow.Systems.FindActuatorBoundary(snapshot.Stamp,Engine(),1);}catch(InvalidOperationException){stale=true;}
        Need(!stale,"Canceled transaction leaves original stamp usable.");
        flow.World.SetRegionMotions(flow.World.Stamp,flow.World.CaptureSnapshot().RegionMotion,flow.World.Stamp.Time,flow.World.Stamp.FrameId);try{flow.Systems.FindActuatorBoundary(snapshot.Stamp,Engine(),1);}catch(InvalidOperationException){stale=true;}Need(stale,"Foreign epoch must reject.");
        Console.WriteLine("PASS retained deadlines: first-row/shared-EC/mixture/saturation, adjacent frontier, tiny64 difference, fallback, live inventory/cache invalidation and rollback");
    }
}
