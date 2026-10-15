using System;
using KspContinuum;

internal static class ThermalTests
{
    static readonly string NativeSha=new string('a',64),PartsSha=new string('b',64);
    static void Need(bool value,string message){if(!value)throw new Exception(message);}
    static void Near(double a,double b,double tolerance,string message){Need(Math.Abs(a-b)<=tolerance,message+": "+a+" / "+b);}
    static void Reject(Action action){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Need(rejected,"Unsupported thermal state must reject atomically.");}
    static OwnedRuntimeImportResult Fixture(bool pair=false,bool surface=true)
    {
        var parts=pair?new[]{new OwnedRuntimePartDefinition(1,"hot",1,new Vec(),Rotation.Identity,100,400),new OwnedRuntimePartDefinition(2,"cold",2,new Vec(),Rotation.Identity,200,200)}:
            new[]{new OwnedRuntimePartDefinition(1,"heated",1,new Vec(),Rotation.Identity,100,300)};
        var bodies=new OwnedRuntimeBodyDefinition[parts.Length];for(int i=0;i<bodies.Length;i++)bodies[i]=new OwnedRuntimeBodyDefinition(i+1,new RigidAssemblyMember(i,1,new Vec(i,0,0),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity));
        var definition=new OwnedRuntimeDefinition("thermal-analytic",0,parts,bodies,new OwnedRuntimeResourceDefinition[0],new long[]{100});
        var connections=pair?new[]{new OwnedRuntimeConnection(1,1,2,true)}:new OwnedRuntimeConnection[0];
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,connections),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var systems=new OwnedRuntimeSystems(world,new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,0,1),1,-1,0,-1,0,100,100)},
            surface?new[]{new OwnedRuntimeSurfaceSpec(0,new Vec(),new Vec(0,0,1),0,0,1,0)}:new OwnedRuntimeSurfaceSpec[0],
            pair?new[]{new OwnedRuntimeThermalLink(0,1,10)}:null,null,false,new[]{0});
        var binding=new OwnedRuntimeBodyBinding[bodies.Length];for(int i=0;i<binding.Length;i++)binding[i]=new OwnedRuntimeBodyBinding{sourceBodyId=i+1,bodyId=i+1,bodySlot=i};
        return new OwnedRuntimeImportResult{World=world,Systems=systems,Source=new OwnedRuntimeSourceBinding{runId="thermal-analytic",vesselId="fixture",processId=1,nativeManifestSha256=NativeSha,partSpecsSha256=PartsSha,scope="Analytic thermal oracle",subtractedNativeCenterVelocity=new double[3],sourceFrameVelocity=new double[3],bodies=binding,renderers=new OwnedRuntimeRendererBinding[0],omissions=new string[0]}};
    }
    static OwnedRuntimeEnvironment Environment(OwnedRuntimeWorld world,double ambient,double convection)
    {return new OwnedRuntimeEnvironment("analytic-forcing",0,world.Stamp.Time,0,ambient,new Vec(),new Vec(),convection,3);}
    static void Advance(OwnedRuntimeImportResult runtime,bool heat,double ambient=300,double convection=0,double dt=.02)
    {
        var controls=new OwnedRuntimeControls{WheelsEnabled=true,Attitude=new Vec(0,0,heat?1:0)};
        using(var step=runtime.World.BeginStep(runtime.World.Stamp,dt,new OwnedRuntimeInput(0,0,0,heat?1:0)))
        {runtime.Systems.Step(step,Environment(runtime.World,ambient,convection),controls);step.Commit();}
    }
    public static void Run()
    {
        Reference();PulseOrdering();Conservation();Radiation();Continuation();RollbackAndBoundaries();CoastBoundary();
        Console.WriteLine("PASS thermal reference, piecewise heat/ambient ordering, analytic cooling/conduction, pending checkpoint continuation and rollback guards");
    }
    static OwnedRuntimeThermalPolicy Policy()
    {return new OwnedRuntimeThermalPolicy(true,1,100,1e-6,1e-8,12);}
    static void Reference()
    {
        var runtime=Fixture();Advance(runtime,true,250,20);
        var snapshot=runtime.World.CaptureSnapshot();Near(snapshot.Temperature[0],(100*300.02+.02*20*250)/(100+.02*20),1e-12,"Default legacy thermal arithmetic");
        Need(!snapshot.Thermal.independent&&snapshot.Thermal.pendingDuration==0&&snapshot.Thermal.segmentCount==0,"Reference leaves no pending heat.");
    }
    static double Pulse(bool early)
    {
        var runtime=Fixture();runtime.Systems.SetThermalPolicy(runtime.World.Stamp,Policy());double exact=300;
        for(int i=0;i<4;i++)
        {
            bool hot=early?i==0:i==3;double ambient=i<2?250:400;
            Advance(runtime,hot,ambient,20);
            double equilibrium=ambient+(hot?100:0)/20.0;exact=equilibrium+(exact-equilibrium)*Math.Exp(-20*.02/100);
        }
        var pending=runtime.World.CaptureSnapshot();Need(pending.Thermal.segmentCount==4,"Ordered forcing history retained.");Near(pending.Thermal.heatJoules[0],2,1e-14,"Exact actuator interval energy");
        double time=runtime.World.Stamp.Time;runtime.Systems.FlushThermal(runtime.World.Stamp);
        Near(runtime.World.Stamp.Time,time,0,"Flush cannot invent physical time");double actual=runtime.World.CaptureSnapshot().Temperature[0];
        Near(actual,exact,4e-6,"Independent exact piecewise ODE oracle");return actual;
    }
    static void PulseOrdering()
    {double early=Pulse(true),late=Pulse(false);Need(late>early&&late-early>1e-5,"Averaging must not erase early/late pulse order.");}
    static void Conservation()
    {
        var runtime=Fixture(true,false);runtime.Systems.SetThermalPolicy(runtime.World.Stamp,Policy());for(int i=0;i<4;i++)Advance(runtime,false);
        runtime.Systems.FlushThermal(runtime.World.Stamp);var s=runtime.World.CaptureSnapshot();
        Near(100*s.Temperature[0]+200*s.Temperature[1],80000,1e-8,"Conduction conserves total energy");
        double difference=200*Math.Exp(-10*(1.0/100+1.0/200)*.08);
        Near(s.Temperature[0]-s.Temperature[1],difference,8e-6,"Independent two-capacity conduction ODE");
    }
    static void Radiation()
    {
        var runtime=Fixture(false,false);
        runtime.Systems=new OwnedRuntimeSystems(runtime.World,new OwnedRuntimeActuatorSpec[0],new[]{new OwnedRuntimeSurfaceSpec(0,new Vec(),new Vec(0,0,1),0,0,1,.5)});
        using(var step=runtime.World.BeginStep(runtime.World.Stamp,.001,new OwnedRuntimeInput(0))){step.SetTemperature(0,400);step.Commit();}
        runtime.Systems.SetThermalPolicy(runtime.World.Stamp,Policy());for(int i=0;i<4;i++)Advance(runtime,false);
        runtime.Systems.FlushThermal(runtime.World.Stamp);double exact=400,h=.08/8000;
        Func<double,double> derivative=t=>-5.670374419e-8*.5*(t*t*t*t-81)/100;
        for(int i=0;i<8000;i++){double a=derivative(exact),b=derivative(exact+h*a/2),c=derivative(exact+h*b/2),d=derivative(exact+h*c);exact+=h*(a+2*b+2*c+d)/6;}
        Near(runtime.World.CaptureSnapshot().Temperature[0],exact,5e-6,"Independent radiative RK4 oracle");
    }
    static void Continuation()
    {
        var runtime=Fixture();runtime.Systems.SetThermalPolicy(runtime.World.Stamp,Policy());Advance(runtime,true,250,20);Advance(runtime,false,400,20);
        var controls=new OwnedRuntimeControls{WheelsEnabled=true};var environment=Environment(runtime.World,300,20);
        var host=new OwnedRuntimeHost(runtime.World,runtime.Systems);var checkpoint=host.CaptureCheckpoint(runtime,controls,environment,new OwnedRuntimeStepPlan(.02));
        var restored=OwnedRuntimeCheckpoint.Restore(OwnedRuntimeCheckpointBinary.Decode(OwnedRuntimeCheckpointBinary.Encode(checkpoint)).state,NativeSha,PartsSha);
        var second=new OwnedRuntimeImportResult{World=restored.World,Systems=restored.Systems,Source=restored.Source};
        for(int i=0;i<3;i++){Advance(runtime,i==2,350,20);Advance(second,i==2,350,20);}
        runtime.Systems.FlushThermal(runtime.World.Stamp);second.Systems.FlushThermal(second.World.Stamp);
        var a=runtime.World.CaptureSnapshot();var b=second.World.CaptureSnapshot();
        Need(BitConverter.DoubleToInt64Bits(a.Temperature[0])==BitConverter.DoubleToInt64Bits(b.Temperature[0]),"Pending heat checkpoint must continue bitwise.");
        Need(a.Thermal.causalRevision==b.Thermal.causalRevision&&a.Thermal.sourceJoules==b.Thermal.sourceJoules,"Thermal causal/energy ledger restored.");
        var malformed=second.World.CaptureSnapshot();malformed.Thermal.segmentHeat[0]=double.NaN;Reject(()=>OwnedRuntimeWorld.RestoreSnapshot(malformed));
    }
    static void RollbackAndBoundaries()
    {
        var runtime=Fixture(true);runtime.Systems.SetThermalPolicy(runtime.World.Stamp,Policy());Advance(runtime,true);
        var stamp=runtime.World.Stamp;var before=runtime.World.CaptureSnapshot();
        Reject(()=>runtime.World.Split(stamp,1));Reject(()=>runtime.World.SetRegionMotions(stamp,before.RegionMotion,stamp.Time+1,stamp.FrameId));
        using(var step=runtime.World.BeginStep(stamp,.02,new OwnedRuntimeInput(0)))
        {Reject(()=>step.SetTemperature(0,400));}
        using(var step=runtime.World.BeginStep(stamp,.02,new OwnedRuntimeInput(0)))
        {runtime.Systems.Step(step,Environment(runtime.World,300,0),new OwnedRuntimeControls());}
        Need(runtime.World.Stamp.Matches(stamp)&&runtime.World.CaptureSnapshot().Thermal.pendingDuration==before.Thermal.pendingDuration,"Canceled mechanics step retains pending heat exactly.");
        runtime.World.Rebase(stamp,new Vec(1e8,0,0),stamp.FrameId);
        Need(runtime.World.CaptureSnapshot().Thermal.heatJoules[0]==before.Thermal.heatJoules[0],"Rebase preserves heat ledger.");
        runtime.Systems.FlushThermal(runtime.World.Stamp);runtime.World.Split(runtime.World.Stamp,1);
        var deadline=Fixture(false,false);deadline.Systems.SetThermalPolicy(deadline.World.Stamp,new OwnedRuntimeThermalPolicy(true,.03,100));
        Advance(deadline,false);Need(deadline.World.CaptureSnapshot().Thermal.pendingDuration>0,"Independent schedule really defers.");
        Advance(deadline,false);Need(deadline.World.CaptureSnapshot().Thermal.pendingDuration==0,"Physical deadline forces thermal publication.");
    }
    static void CoastBoundary()
    {
        var runtime=Fixture(false,false);
        var source=new OwnedFlightCapture{runId="thermal-coast",processId=1,vesselId="fixture",bodyId="analytic-body",nativeManifestSha256=NativeSha,partSpecsSha256=PartsSha,
            sourceEpochSeconds=12345,gravitationalParameter=4e6,radiusMeters=1000,atmosphereDepthMeters=0,angularVelocity=new double[3],sunDirection=new[]{1d,0d,0d},
            bodyRotation=new[]{0d,0d,0d,1d},rootPositionBodyRelative=new[]{1200d,0d,0d},rootRotation=new[]{0d,0d,0d,1d},centerOfMassVelocityInertial=new[]{0d,Math.Sqrt(4e6/1200),0d},
            altitudeMeters=new double[0],densityKilogramsPerCubicMeter=new double[0],temperatureKelvin=new double[0],omissions=new string[0]};
        var session=OwnedFlightSession.SeedCaptured(runtime.World,OwnedFlightWorld.Admit(source,NativeSha,PartsSha,new string('c',64)),new Vec(1200,0,0));
        runtime.Systems.SetThermalPolicy(runtime.World.Stamp,Policy());Advance(runtime,false);
        var stamp=runtime.World.Stamp;string reason;OwnedRuntimeMotion[] motions;double time;
        Need(!session.PrepareCoast(stamp,session.UniversalTime+1,new OwnedRuntimeControls(),true,double.PositiveInfinity,0,out motions,out time,out reason),"Pending thermal history must fall back before coast publication.");
        Need(runtime.World.Stamp.Matches(stamp)&&runtime.World.CaptureSnapshot().Thermal.pendingDuration>0,"Rejected coast must preserve pending history and frontier.");
        runtime.Systems.FlushThermal(stamp);
        Need(session.PrepareCoast(runtime.World.Stamp,session.UniversalTime+1,new OwnedRuntimeControls(),true,double.PositiveInfinity,0,out motions,out time,out reason),reason);
    }
}
