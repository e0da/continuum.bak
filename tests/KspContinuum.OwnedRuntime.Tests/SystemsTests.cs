using System;
using KspContinuum;

internal static class SystemsTests
{
    public static void Run()
    {
        var p = RigidAssemblyProperties.Combine(new[] { new RigidAssemblyMember(0, 2,
            new Vec(1,0,0), Rotation.Identity, new Vec(), new Vec(2,3,4), Rotation.Identity) });
        var motion = new OwnedRuntimeMotion { Orientation=Rotation.Identity };
        OwnedRuntimeDynamics.Step(ref motion,p,new Vec(4,0,0),new Vec(),.1);
        Near(motion.Velocity.X,.2,1e-14,"F/m dt");
        Near(motion.RootPosition.X,.01,1e-14,"constant force displacement");
        motion = new OwnedRuntimeMotion { Orientation=Rotation.Identity };
        OwnedRuntimeDynamics.Step(ref motion,p,new Vec(),new Vec(0,0,4),.1);
        Near(motion.AngularMomentum.Z,.4,1e-14,"world angular impulse");
        Near(OwnedRuntimeDynamics.AngularVelocity(p,motion.Orientation,motion.AngularMomentum).Z,.1,1e-14,"principal torque response");
        var free = new OwnedRuntimeMotion { Orientation=Rotation.Identity, AngularMomentum=new Vec(.3,.5,.7) };
        Vec initial = free.AngularMomentum;
        for(int i=0;i<1000;i++) OwnedRuntimeDynamics.Step(ref free,p,new Vec(),new Vec(),.001);
        Near(free.AngularMomentum.X,initial.X,0,"free world Lx");
        Near(free.AngularMomentum.Y,initial.Y,0,"free world Ly");
        Near(free.AngularMomentum.Z,initial.Z,0,"free world Lz");
        Near(Rotation.Dot(free.Orientation,free.Orientation),1,1e-14,"rotation normalization");
        var centered = RigidAssemblyProperties.Combine(new[] { new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity) });
        var stationary = new OwnedRuntimeMotion { RootPosition=new Vec(1e12,-1e12,5), Orientation=Rotation.Identity };
        OwnedRuntimeDynamics.Step(ref stationary,centered,new Vec(),new Vec(),.02);
        Near(stationary.RootPosition.X,1e12,0,"zero input double frame");
        var changed=RigidAssemblyProperties.Combine(new[] { new RigidAssemblyMember(0,1,new Vec(2,0,0),Rotation.Identity,new Vec(),new Vec(1,1.5,2),Rotation.Identity) });
        motion=new OwnedRuntimeMotion { Orientation=Rotation.Identity,AngularMomentum=new Vec(0,0,4) };
        OwnedRuntimeDynamics.ChangeProperties(ref motion,p,changed);
        Near(motion.Velocity.Y,1,1e-14,"comoving mass transport");
        Near(OwnedRuntimeDynamics.AngularVelocity(changed,motion.Orientation,motion.AngularMomentum).Z,1,1e-14,"mass refresh preserves omega");
        motion=new OwnedRuntimeMotion {Orientation=Rotation.Identity};
        OwnedRuntimeDynamics.StepCoupled(ref motion,centered,new Vec(),new Vec(),new Vec(),new Vec(0,0,-.2),.02);
        Vec total=motion.AngularMomentum+motion.Orientation.Rotate(new Vec(0,0,-.2));
        Near(total.Z,0,1e-14,"wheel plus chassis momentum conserved");
        ScheduleChecks();
        RetainedAxisChecks();
        Console.WriteLine("PASS owned runtime dynamics analytic force, torque, free momentum, mass transport and double frame checks");
    }

    static void ScheduleChecks()
    {
        var body=new OwnedRuntimeBodyDefinition(1,new RigidAssemblyMember(0,10,new Vec(),Rotation.Identity,new Vec(),new Vec(5,5,5),Rotation.Identity));
        var parts=new[]{new OwnedRuntimePartDefinition(1,"tank",1,new Vec(),Rotation.Identity,10,400),
            new OwnedRuntimePartDefinition(2,"actuator",1,new Vec(0,1,0),Rotation.Identity,20,200)};
        var resources=new[]{new OwnedRuntimeResourceDefinition(1,1,"Fuel",10,1,1),new OwnedRuntimeResourceDefinition(2,1,"ElectricCharge",10,0,10)};
        var definition=new OwnedRuntimeDefinition("analytic",0,parts,new[]{body},resources,new long[]{100});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),
            new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var rcs=new OwnedRuntimeActuatorSpec(1,OwnedRuntimeActuatorKind.Rcs,new Vec(),new Vec(1,0,0),980.665,
            new[]{"Fuel"},new[]{1.0},100);
        var systems=new OwnedRuntimeSystems(world,new[]{rcs},new OwnedRuntimeSurfaceSpec[0]);
        var environment=new OwnedRuntimeEnvironment("vacuum",0,0,0,300,new Vec(),new Vec());
        using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0,0,0,0,1)))
        {systems.Step(step,environment);step.Commit();}
        using(var read=world.AcquireRead())
        {
            Near(read.GetResourceQuantity(0),.98,1e-14,"region pool propellant");
            Near(read.GetRegion(0).Properties.Mass,10.98,1e-13,"resource mass before force");
            Near(read.GetRegion(0).Motion.Velocity.X,980.665*.02/10.98,1e-12,"RCS thrust uses final mass");
            Near(read.GetRegion(0).Motion.AngularMomentum.Z,-980.665*.02,1e-12,"off-center RCS torque");
        }
        double fuel;
        using(var read=world.AcquireRead())fuel=read.GetResourceQuantity(0);
        using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0)))
        {systems.Step(step,environment);step.Commit();}
        using(var read=world.AcquireRead())Near(read.GetResourceQuantity(0),fuel,0,"zero input no withdrawal");
        using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0,0,0,0,1)))
        {systems.Step(step,environment);}
        using(var read=world.AcquireRead())Near(read.GetResourceQuantity(0),fuel,0,"failed/uncommitted schedule rollback");
        var host=new OwnedRuntimeHost(world,systems);var before=world.Stamp;long configRevision=systems.ConfigurationRevision;
        var mutator=new ConfigurationMutationSchedule();host.SwapSchedule(mutator,new string('a',64),before);
        bool executionRejected=false;
        try{host.Advance(.02,new OwnedRuntimeInput(0),environment,new OwnedRuntimeControls());}
        catch(InvalidOperationException){executionRejected=true;}
        if(!executionRejected||!mutator.EnabledRejected||!mutator.FlowRejected||!systems.IsEnabled(0)
            ||!systems.IsResourceFlowEnabled(0)||systems.ConfigurationRevision!=configRevision||!world.Stamp.Matches(before))
            throw new Exception("Failed schedule changed cold system configuration or committed world.");
        systems.SetEnabled(0,false);systems.SetEnabled(0,true);
        using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0)))
        {
            systems.Step(step,environment);bool rejected=false;
            try{systems.Step(step,environment);}catch(InvalidOperationException){rejected=true;}
            if(!rejected)throw new Exception("Second coarse execution was admitted.");
        }

        var mixtureResources=new[]{new OwnedRuntimeResourceDefinition(1,1,"Fuel",1,1,.01),
            new OwnedRuntimeResourceDefinition(2,1,"Oxidizer",1,2,.5)};
        var mixtureDefinition=new OwnedRuntimeDefinition("mixture",0,parts,new[]{body},mixtureResources);
        var mixtureWorld=new OwnedRuntimeWorld(mixtureDefinition,OwnedRuntimeTopology.Compile(mixtureDefinition,new OwnedRuntimeConnection[0]),
            new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var engine=new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(1,0,0),980.665,
            new[]{"Fuel","Oxidizer"},new[]{.75,.25},100);
        var mixtureSystems=new OwnedRuntimeSystems(mixtureWorld,new[]{engine},new OwnedRuntimeSurfaceSpec[0]);
        using(var step=mixtureWorld.BeginStep(mixtureWorld.Stamp,.02,new OwnedRuntimeInput(1)))
        {mixtureSystems.Step(step,environment);step.Commit();}
        using(var read=mixtureWorld.AcquireRead())
        {
            Near(read.GetResourceQuantity(0),0,1e-14,"mixture limiting fuel");
            Near(read.GetResourceQuantity(1),.5-(.02*.25*(2.0/3)/2),1e-14,"mixture matched oxidizer");
            Near(mixtureSystems.Counters.PropellantKilograms,.02*(2.0/3),1e-14,"starved engine achieved flow");
        }

        world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var wheel=new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,0,1),10,
            electricResource:1,electricUnitsPerSecond:2,wheelMomentumCapacity:.2);
        systems=new OwnedRuntimeSystems(world,new[]{wheel},new OwnedRuntimeSurfaceSpec[0],wheelProviderSlots:new[]{0});
        for(int i=0;i<2;i++)using(var step=world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0,0,0,1)))
        {systems.Step(step,environment);step.Commit();}
        using(var read=world.AcquireRead())
        {
            Near(read.GetProviderState(0),-.2,1e-14,"wheel saturation");
            Near(read.GetResourceQuantity(1),9.96,1e-13,"saturated wheel no further EC draw");
            var state=read.GetRegion(0).Motion;
            Vec momentum=state.AngularMomentum+state.Orientation.Rotate(new Vec(0,0,read.GetProviderState(0)));
            Near(momentum.Z,0,1e-13,"whole schedule wheel momentum closure");
        }
        world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        systems=new OwnedRuntimeSystems(world,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0],new[]{new OwnedRuntimeThermalLink(0,1,10)});
        using(var step=world.BeginStep(world.Stamp,.1,new OwnedRuntimeInput(0)))
        {systems.Step(step,environment);step.Commit();}
        using(var read=world.AcquireRead())
        {
            Near(read.GetTemperature(0)*10+read.GetTemperature(1)*20,8000,1e-10,"thermal conduction energy");
            if(read.GetTemperature(0)>=400 || read.GetTemperature(1)<=200)throw new Exception("Thermal transfer not executed.");
        }
        if(systems.Counters.Steps!=1 || systems.Counters.ThermalLinks!=1 || systems.Counters.RegionIntegrations!=1)
            throw new Exception("Coarse schedule coverage counters differ.");

        var airWorld=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),
            new OwnedRuntimeMotion{Orientation=Rotation.Identity,Velocity=new Vec(10,0,0)});
        var surface=new OwnedRuntimeSurfaceSpec(0,new Vec(),new Vec(0,1,0),2,0,0,0,.5);
        var airSystems=new OwnedRuntimeSystems(airWorld,new OwnedRuntimeActuatorSpec[0],new[]{surface});
        using(var step=airWorld.BeginStep(airWorld.Stamp,.02,new OwnedRuntimeInput(0)))
        {airSystems.Step(step,new OwnedRuntimeEnvironment("air",0,0,1,300,new Vec(),new Vec()));step.Commit();}
        using(var read=airWorld.AcquireRead())
        {
            Near(read.GetRegion(0).Motion.Velocity.X,10-100*.02/11,1e-12,"q CdArea drag");
            Near(read.GetTemperature(0),401,1e-12,"declared aerodynamic heating fraction");
        }
    }
    sealed class ConfigurationMutationSchedule : IOwnedRuntimeSchedule
    {
        public bool EnabledRejected,FlowRejected;
        public string Schema{get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema{get{return "world-bank-state-only/v1";}}
        public string ImplementationId{get{return "configuration-mutation-falsifier";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {
            try{systems.SetEnabled(0,false);}catch(InvalidOperationException){EnabledRejected=true;}
            try{systems.SetResourceFlowEnabled(0,false);}catch(InvalidOperationException){FlowRejected=true;}
            throw new InvalidOperationException("Injected candidate failure after configuration mutation attempts.");
        }
    }
    static void RetainedAxisChecks()
    {
        Vec axis=new Vec(.6*(1-1e-13),.8*(1-1e-13),BitConverter.Int64BitsToDouble(long.MinValue));
        var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
        var method=typeof(OwnedRuntimeActuatorSpec).GetMethod("RestoreUnitAxis",flags,null,new[]{typeof(int),typeof(OwnedRuntimeActuatorKind),
            typeof(Vec),typeof(Vec),typeof(double),typeof(int),typeof(double),typeof(int),typeof(double),typeof(double),typeof(double)},null);
        if(method==null)throw new Exception("Retained actuator factory missing.");
        var actuator=(OwnedRuntimeActuatorSpec)method.Invoke(null,new object[]{0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),axis,1.0,-1,0.0,-1,0.0,0.0,100.0});
        var surfaceMethod=typeof(OwnedRuntimeSurfaceSpec).GetMethod("RestoreUnitNormal",flags);
        if(surfaceMethod==null)throw new Exception("Retained surface factory missing.");
        var surface=(OwnedRuntimeSurfaceSpec)surfaceMethod.Invoke(null,new object[]{0,new Vec(),axis,1.0,0.0,1.0,.5,0.0});
        Bits(actuator.LocalAxis,axis);Bits(surface.LocalNormal,axis);
    }
    static void Bits(Vec actual,Vec expected)
    {
        if(BitConverter.DoubleToInt64Bits(actual.X)!=BitConverter.DoubleToInt64Bits(expected.X)
            ||BitConverter.DoubleToInt64Bits(actual.Y)!=BitConverter.DoubleToInt64Bits(expected.Y)
            ||BitConverter.DoubleToInt64Bits(actual.Z)!=BitConverter.DoubleToInt64Bits(expected.Z))
            throw new Exception("Retained unit vector bits changed.");
    }
    static void Near(double actual,double expected,double tolerance,string name)
    { if(double.IsNaN(actual)||Math.Abs(actual-expected)>tolerance)throw new Exception(name+": "+actual+" != "+expected); }
}
