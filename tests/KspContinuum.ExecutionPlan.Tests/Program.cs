using System;
using System.Reflection;
using KspContinuum;

static class Program
{
    sealed class Case {internal OwnedRuntimeWorld World;internal OwnedRuntimeSystems Systems;}
    static void Need(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Same(object a,object b,string path)
    {
        if(a==null||b==null){Need(a==b,path+" null mismatch");return;}
        var type=a.GetType();Need(type==b.GetType(),path+" type mismatch");
        if(type==typeof(double)){Need(BitConverter.DoubleToInt64Bits((double)a)==BitConverter.DoubleToInt64Bits((double)b),path+" double bits differ");return;}
        if(type.IsPrimitive||type.IsEnum||type==typeof(string)){Need(a.Equals(b),path+" value differs");return;}
        if(type.IsArray){var x=(Array)a;var y=(Array)b;Need(x.Length==y.Length,path+" length differs");for(int i=0;i<x.Length;i++)Same(x.GetValue(i),y.GetValue(i),path+"["+i+"]");return;}
        foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.Instance))Same(field.GetValue(a),field.GetValue(b),path+"."+field.Name);
    }
    static Case Fixture(bool compiled)
    {
        var rotation=Rotation.AxisAngle(new Vec(.3,.4,.5),.4);
        var parts=new[]{new OwnedRuntimePartDefinition(1,"first",10,new Vec(.2,.1,0),rotation,100,300),new OwnedRuntimePartDefinition(2,"second",20,new Vec(-.1,0,.2),Rotation.Identity,150,310)};
        var bodies=new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,8,new Vec(),rotation,new Vec(.1,.2,0),new Vec(3,4,5),rotation)),
            new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(1,6,new Vec(2,1,.2),Rotation.Identity,new Vec(0,.1,.2),new Vec(2,3,4),Rotation.Identity))};
        var resources=new[]{new OwnedRuntimeResourceDefinition(100,1,"A",1,2,.2),new OwnedRuntimeResourceDefinition(101,2,"A",1,2,.3),
            new OwnedRuntimeResourceDefinition(102,1,"B",1,1,.3),new OwnedRuntimeResourceDefinition(103,2,"B",1,1,.4),
            new OwnedRuntimeResourceDefinition(104,1,"ElectricCharge",100,0,20),new OwnedRuntimeResourceDefinition(105,2,"ElectricCharge",100,0,20)};
        var definition=new OwnedRuntimeDefinition("compiled-differential",0,parts,bodies,resources,new long[]{1000,1001});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new[]{new OwnedRuntimeConnection(42,1,2)}),
            new OwnedRuntimeMotion{RootPosition=new Vec(1e8,3,-2),Orientation=rotation,Velocity=new Vec(100,30,20),AngularMomentum=new Vec(.1,.2,.3)});
        var actuators=new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(.3,.2,.1),new Vec(.2,.3,1),1200,new[]{"A","B"},new[]{.5,.5},250,4,.1,2),
            new OwnedRuntimeActuatorSpec(1,OwnedRuntimeActuatorKind.Rcs,new Vec(-.2,.3,.1),new Vec(1,.2,.1),300,new[]{"A","B"},new[]{.5,.5},200,5,.05,1),
            new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(.2,1,.1),3,-1,0,4,.2,2,20),
            new OwnedRuntimeActuatorSpec(1,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(1,.1,.2),2,-1,0,5,.1,1,20)};
        var surfaces=new[]{new OwnedRuntimeSurfaceSpec(1,new Vec(.1,.2,.3),new Vec(.3,1,.2),.5,.2,.8,.5,.0001),
            new OwnedRuntimeSurfaceSpec(0,new Vec(-.2,.3,.1),new Vec(1,.2,.3),.4,0,.5,.6,.0002),
            new OwnedRuntimeSurfaceSpec(1,new Vec(.2,-.1,.3),new Vec(.2,.3,1),.3,.1,.4,.7,.0001)};
        var systems=new OwnedRuntimeSystems(world,actuators,surfaces,new[]{new OwnedRuntimeThermalLink(0,1,.2)},null,false,new[]{-1,-1,0,1});
        if(compiled)systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);
        return new Case{World=world,Systems=systems};
    }
    static void Advance(Case c,double density,OwnedRuntimeControls controls)
    {
        var env=new OwnedRuntimeEnvironment("differential-field",0,c.World.Stamp.Time,density,280,new Vec(2,3,1),new Vec(0,-9.81,0),.5,270);
        var input=new OwnedRuntimeInput(controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z);
        using(var step=c.World.BeginStep(c.World.Stamp,.01,input)){c.Systems.Step(step,env,controls);step.Commit();}
    }
    static void Compare(Case a,Case b)
    {
        Same(a.World.CaptureSnapshot(),b.World.CaptureSnapshot(),"complete-world");
        Same(a.Systems.Counters.DeliveredForceNewtonSeconds,b.Systems.Counters.DeliveredForceNewtonSeconds,"force-ledger");
        Same(a.Systems.Counters.DeliveredTorqueNewtonMeterSeconds,b.Systems.Counters.DeliveredTorqueNewtonMeterSeconds,"torque-ledger");
        Same(a.Systems.Counters.PropellantKilograms,b.Systems.Counters.PropellantKilograms,"fuel-ledger");
        Same(a.Systems.Counters.ElectricUnits,b.Systems.Counters.ElectricUnits,"electric-ledger");
    }
    static void Main()
    {
        MechanicalTransaction();
        BoundaryDifferential();
        var reference=Fixture(false);var candidate=Fixture(true);
        var powered=new OwnedRuntimeControls{Throttle=.6,Attitude=new Vec(.1,.2,-.4),Translation=new Vec(.2,-.3,.4),EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};
        for(int i=0;i<50;i++)
        {
            if(i==10){reference.Systems.SetEnabled(0,false);candidate.Systems.SetEnabled(0,false);}
            if(i==12){reference.Systems.SetEnabled(0,true);candidate.Systems.SetEnabled(0,true);}
            if(i==15){reference.Systems.SetResourceFlowEnabled(0,false);candidate.Systems.SetResourceFlowEnabled(0,false);}
            if(i==17){reference.Systems.SetResourceFlowEnabled(0,true);candidate.Systems.SetResourceFlowEnabled(0,true);}
            if(i==20){reference.World.Split(reference.World.Stamp,42);candidate.World.Split(candidate.World.Stamp,42);candidate.Systems.CompileExecutionPlan(candidate.World.Stamp);}
            if(i==30){reference.World.SetConnectionLocked(reference.World.Stamp,42,true);candidate.World.SetConnectionLocked(candidate.World.Stamp,42,true);candidate.Systems.CompileExecutionPlan(candidate.World.Stamp);}
            var controls=i%7==0?new OwnedRuntimeControls():powered;double density=i<5||i%4==0?0:.2;
            Advance(reference,density,controls);Advance(candidate,density,controls);Compare(reference,candidate);
        }
        Need(candidate.Systems.Counters.ExecutionPlanBuilds>=7,"Topology/config plan invalidations must execute.");
        Need(candidate.Systems.Counters.RegionMotionContexts<candidate.Systems.Counters.SurfaceRows,"Region context must be shared.");
        Need(candidate.Systems.Counters.VacuumSurfaceRows>0&&candidate.Systems.Counters.CompiledSurfaceRows>0,"Both active and vacuum domains must execute.");
        Need(candidate.Systems.Counters.InactiveActuatorRows>0,"Disabled routing witnessed.");
        var before=candidate.World.Stamp;bool rejected=false;var old=new OwnedRuntimeStamp(0,0,0,0,0,0,"old");try{candidate.Systems.CompileExecutionPlan(old);}catch(InvalidOperationException){rejected=true;}
        Need(rejected&&candidate.World.Stamp.Matches(before),"Stale cold plan cannot publish state.");
        Console.WriteLine("PASS compiled/reference bitwise complete-state geometry, shared resource pools, wheel history, vacuum/atmosphere and split/re-lock invalidation");
    }
    sealed class Field : IOwnedRuntimeEnvironmentField
    {
        public OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeStamp stamp,int region,OwnedRuntimeMotion motion,RigidAssemblyProperties properties)
        {return new OwnedRuntimeEnvironmentSample("stage-oracle",0,stamp.Time,0,300,new Vec(),new Vec());}
    }
    static void BoundaryDifferential()
    {
        var reference=Fixture(false);var compact=Fixture(true);
        var inputs=new[]{new OwnedRuntimeControls(),new OwnedRuntimeControls{Throttle=1,EnginesEnabled=true},
            new OwnedRuntimeControls{Attitude=new Vec(.3,-.7,.4),Translation=new Vec(.5,-.2,.4),EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true,Throttle=.7}};
        for(int condition=0;condition<4;condition++)
        {
            if(condition==1){reference.Systems.SetResourceFlowEnabled(0,false);compact.Systems.SetResourceFlowEnabled(0,false);}
            if(condition==2){reference.World.Split(reference.World.Stamp,42);compact.World.Split(compact.World.Stamp,42);}
            if(condition==3)
            {
                using(var step=reference.World.BeginStep(reference.World.Stamp,.001,new OwnedRuntimeInput())){step.SetProviderState(0,20);step.SetProviderState(1,-20);step.Commit();}
                using(var step=compact.World.BeginStep(compact.World.Stamp,.001,new OwnedRuntimeInput())){step.SetProviderState(0,20);step.SetProviderState(1,-20);step.Commit();}
            }
            foreach(var controls in inputs)foreach(double horizon in new[]{.0001,.1,10d,100d})
            {
                var beforeReference=reference.World.CaptureSnapshot();var beforeCompact=compact.World.CaptureSnapshot();
                var a=reference.Systems.FindActuatorBoundary(reference.World.Stamp,controls,horizon);var b=compact.Systems.FindActuatorBoundary(compact.World.Stamp,controls,horizon);
                Same(a.Supported,b.Supported,"boundary supported");Same(a.Reached,b.Reached,"boundary reached");Same(a.DeltaSeconds,b.DeltaSeconds,"boundary deadline bits");Same(a.Actuator,b.Actuator,"boundary ordered actuator");Same(a.Resource,b.Resource,"boundary resource");Same(a.Kind,b.Kind,"boundary kind");
                Same(beforeReference,reference.World.CaptureSnapshot(),"reference probe source immutable");Same(beforeCompact,compact.World.CaptureSnapshot(),"borrowed compact source immutable");
                Need(b.SourceStamp.Matches(compact.World.Stamp)&&b.ConfigurationRevision==compact.Systems.ConfigurationRevision,"Compact result remains source/config bound.");
            }
        }
        Need(compact.Systems.Counters.BoundaryProbes==reference.Systems.Counters.BoundaryProbes,"Exact original bisection/probe count retained.");
        Need(compact.Systems.Counters.BoundaryResourceCopies==compact.Systems.Counters.BoundaryProbes,"One compact resource copy per probe.");
        Need(compact.Systems.Counters.BoundaryResourceRowsCopied==compact.Systems.Counters.BoundaryResourceCopies*compact.World.ResourceCount,"Compact resource coverage exact.");
        Need(compact.Systems.Counters.PropellantKilograms==0&&compact.Systems.Counters.ElectricUnits==0,"Read-only probes cannot enter physical consumption ledgers.");
        bool rejected=false;var stamp=compact.World.Stamp;
        using(var step=compact.World.BeginStep(stamp,.01,new OwnedRuntimeInput()))
        {try{compact.Systems.FindActuatorBoundary(stamp,inputs[1],1);}catch(InvalidOperationException){rejected=true;}}
        Need(rejected&&compact.World.Stamp.Matches(stamp),"Compact probe cannot borrow during an active transaction.");
    }
    static void MechanicalTransaction()
    {
        var c=Fixture(true);var original=c.World.CaptureSnapshot();var controls=new OwnedRuntimeControls{Throttle=1,EnginesEnabled=true};
        using(var step=c.World.BeginStep(c.World.Stamp,.01,new OwnedRuntimeInput(1)))
        {
            c.Systems.BeginMechanical(step,controls);
            double mass=(original.ResourceQuantity[0]-step.GetResourceQuantity(0))*2+(original.ResourceQuantity[2]-step.GetResourceQuantity(2));
            Need(Math.Abs(mass-1200*.01/(250*9.80665))<1e-15,"Original propellant transaction executes once.");
            double fuel=step.GetResourceQuantity(0),ec=step.GetResourceQuantity(4);var state=step.GetRegion(0);
            var motions=new[]{state.Motion};var properties=new[]{state.Properties};var force=new Vec[1];var torque=new Vec[1];var sampled=new OwnedRuntimeEnvironmentSample[1];var heating=new double[2];
            c.Systems.SampleMechanical(step,new Field(),step.ExpectedStamp,motions,properties,force,torque,sampled,heating);
            c.Systems.SampleMechanical(step,new Field(),step.ExpectedStamp,motions,properties,force,torque,sampled,heating);
            object forged=step.ExpectedStamp;typeof(OwnedRuntimeStamp).GetField("Time").SetValue(forged,double.NaN);
            var forceBefore=force[0];double ledger=c.Systems.Counters.PropellantKilograms;bool invalidTimeRejected=false;
            try{c.Systems.SampleMechanical(step,new Field(),(OwnedRuntimeStamp)forged,motions,properties,force,torque,sampled,heating);}catch(ArgumentException){invalidTimeRejected=true;}
            Need(invalidTimeRejected,"Nonfinite stage time must reject before sampling/output writes.");Same(forceBefore,force[0],"invalid-stage output unchanged");Same(ledger,c.Systems.Counters.PropellantKilograms,"invalid-stage delivered ledger unchanged");
            Same(fuel,step.GetResourceQuantity(0),"stage fuel unchanged");Same(ec,step.GetResourceQuantity(4),"stage EC unchanged");
            c.Systems.AbortMechanical(step);
        }
        Same(original,c.World.CaptureSnapshot(),"aborted scalar/stages");Need(c.Systems.Counters.PropellantKilograms==0&&c.Systems.Counters.ElectricUnits==0,"Aborted delivered ledgers cannot count physical consumption.");
        bool rejected=false;
        using(var step=c.World.BeginStep(c.World.Stamp,.01,new OwnedRuntimeInput(1)))
        {c.Systems.BeginMechanical(step,controls);try{step.Commit();}catch(InvalidOperationException){rejected=true;}finally{c.Systems.AbortMechanical(step);}}
        Need(rejected,"Unfinished mechanical scalar transaction cannot publish.");Same(original,c.World.CaptureSnapshot(),"incomplete publication rejection");
    }
}
