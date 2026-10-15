using System;
using System.Threading;
using KspContinuum;

public static class FlightControlTests
{
    public static void Run()
    {
        var attitude=OwnedFlightControl.AttitudeHold(Rotation.Identity,new Vec(),Rotation.AxisAngle(new Vec(0,0,1),Math.PI/2),
            Rotation.Identity,new Vec(.1,.2,.3),.5,1);
        Near(attitude.AngleErrorRadians,Math.PI/2,1e-14);Near(attitude.Input.Roll,1,0);
        Need(attitude.SaturatedAxes==1,"Attitude authority saturation absent.");
        var unavailable=OwnedFlightControl.AttitudeHold(Rotation.Identity,new Vec(),Rotation.AxisAngle(new Vec(0,0,1),Math.PI/2),
            Rotation.Identity,new Vec(1,1,0),.5,1);
        Need(unavailable.UnavailableAxes==1&&unavailable.Input.Roll==0,"Absent authority fabricated control.");
        var velocity=OwnedFlightControl.VelocityHold(new Vec(),new Vec(0,1,0),Rotation.AxisAngle(new Vec(0,0,1),Math.PI/2),new Vec(1,1,1),1);
        Near(velocity.Input.X,1,1e-14);Near(velocity.Input.Y,0,1e-14);
        var same=OwnedFlightControl.AttitudeHold(Rotation.Identity,new Vec(),new Rotation(0,0,0,-1),Rotation.Identity,new Vec(1,1,1),1,1);
        Near(same.AngleErrorRadians,0,0);
        SaturatedWheelUnwind();

        OwnedRuntimeImportResult imported;OwnedRuntimeHost host;var session=Fixture(out imported,out host);
        long region;using(var read=imported.World.AcquireRead())region=read.GetRegion(0).Id;
        var before=imported.World.Stamp;var controls=new OwnedRuntimeControls();var plan=new OwnedRuntimeStepPlan(.02);
        double quarterPeriod=Math.PI*50;
        var prediction=OwnedFlightPrediction.Predict(session,host,imported,controls,plan,region,quarterPeriod,
            OwnedFlightPredictionMode.VacuumCoast,true,maximumWallSeconds:30);
        Need(prediction.Status=="completed"&&prediction.SourceUnchanged&&prediction.IsCurrent(session,host),"Coast prediction incomplete/changed source: "+prediction.Failure);
        var last=prediction.GetPoint(prediction.Count-1);Near(last.CenterPosition.X,0,2e-6);Near(last.CenterPosition.Y,1000,2e-6);
        Need(imported.World.Stamp.Matches(before),"Disposable predictor published original bank.");
        var cancelled=OwnedFlightPrediction.Predict(session,host,imported,controls,plan,region,1,OwnedFlightPredictionMode.LocalPhysical,
            cancellation:new CancellationToken(true));
        Need(cancelled.Status=="cancelled"&&cancelled.Count==0&&cancelled.SourceUnchanged,"Cancelled predictor changed source.");
        var bounded=OwnedFlightPrediction.Predict(session,host,imported,controls,plan,region,.04,OwnedFlightPredictionMode.LocalPhysical,maximumSteps:1);
        Need(bounded.Status=="budget-exhausted"&&bounded.Steps==1&&bounded.SourceUnchanged,"Prediction step budget failed.");
        var node=new OwnedFlightManeuverPlan("quarter-orbit-prograde",session,host,region,quarterPeriod,new Vec(-1,0,0));
        node.ExecuteCoastManeuver(session,host,controls,true);
        using(var read=imported.World.AcquireRead())
        {
            Vec position=session.CenterPosition(0),v=read.GetRegion(0).Motion.Velocity;
            double r=Math.Sqrt(position.X*position.X+position.Y*position.Y+position.Z*position.Z);
            double energy=.5*(v.X*v.X+v.Y*v.Y+v.Z*v.Z)-session.Context.Mu/r;
            Near(energy,-39.5,2e-8);Near(-session.Context.Mu/(2*energy),100000.0/79,2e-6);
        }
        Need(!node.IsCurrent(session,host)&&!prediction.IsCurrent(session,host),"Applied event/prediction remained current.");
        bool stale=false;try{node.ApplyNow(session,host);}catch(InvalidOperationException){stale=true;}Need(stale,"Consumed maneuver reapplied.");
        var fresh=new OwnedFlightManeuverPlan("local-invalidates",session,host,region,session.UniversalTime+1,new Vec(1,0,0));
        session.AdvanceLocal(host,.02,controls);Need(!fresh.IsCurrent(session,host),"Local progression failed to stale maneuver.");
        Console.WriteLine("Owned flight bounded guidance, circular prediction, impulse conic, cancellation, budget and stale-event checks passed.");
    }

    static void SaturatedWheelUnwind()
    {
        var definition=new OwnedRuntimeDefinition("saturated-wheel",0,new[]{new OwnedRuntimePartDefinition(10,"wheel",20,new Vec(),Rotation.Identity,100)},
            new[]{new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity))},
            new[]{new OwnedRuntimeResourceDefinition(30,10,"ElectricCharge",10,0,10)},new long[]{40});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var wheel=new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,0,1),2,
            electricResource:0,electricUnitsPerSecond:1,wheelMomentumCapacity:.1);
        var systems=new OwnedRuntimeSystems(world,new[]{wheel},new OwnedRuntimeSurfaceSpec[0],wheelProviderSlots:new[]{0});
        long region;using(var read=world.AcquireRead())region=read.GetRegion(0).Id;
        foreach(double saturated in new[]{.1,-.1})
        {
            using(var step=world.BeginStep(world.Stamp,.001,new OwnedRuntimeInput(0)))
            {step.SetProviderState(0,saturated);step.SetRegionMotion(0,new OwnedRuntimeMotion{Orientation=Rotation.Identity});step.Commit();}
            var authority=OwnedFlightControl.ObserveAuthority(world,systems,region,Rotation.Identity);
            Near(saturated>0?authority.PositiveAngularAcceleration.Z:authority.NegativeAngularAcceleration.Z,.5,1e-14);
            Near(saturated>0?authority.NegativeAngularAcceleration.Z:authority.PositiveAngularAcceleration.Z,0,0);
            var unwind=OwnedFlightControl.AttitudeHold(Rotation.Identity,new Vec(),Rotation.AxisAngle(new Vec(0,0,1),saturated>0?.01:-.01),
                Rotation.Identity,authority.PositiveAngularAcceleration,authority.NegativeAngularAcceleration,1,1);
            var blocked=OwnedFlightControl.AttitudeHold(Rotation.Identity,new Vec(),Rotation.AxisAngle(new Vec(0,0,1),saturated>0?-.01:.01),
                Rotation.Identity,authority.PositiveAngularAcceleration,authority.NegativeAngularAcceleration,1,1);
            Need(unwind.Input.Roll*saturated>0&&blocked.Input.Roll==0&&blocked.UnavailableAxes==1,"Saturated wheel directional guidance differs.");
            double electricBefore;using(var read=world.AcquireRead())electricBefore=read.GetResourceQuantity(0);
            using(var step=world.BeginStep(world.Stamp,.02,unwind.Input))
            {systems.Step(step,new OwnedRuntimeEnvironment("wheel-test-vacuum",0,0,0,300,new Vec(),new Vec()),OwnedFlightControl.Controls(unwind.Input));step.Commit();}
            using(var read=world.AcquireRead())
            {
                Need(Math.Abs(read.GetProviderState(0))<.1,"Permitted unwind did not reduce actual wheel momentum.");
                Need(read.GetRegion(0).Motion.AngularMomentum.Z*saturated>0,"Permitted unwind did not reach physical chassis torque.");
                Need(read.GetResourceQuantity(0)<electricBefore,"Unwind did not reach electrical resource consumption.");
            }
        }
    }

    static OwnedFlightSession Fixture(out OwnedRuntimeImportResult imported,out OwnedRuntimeHost host)
    {
        string native=new string('a',64),parts=new string('b',64);
        var context=OwnedFlightWorld.Admit(new OwnedFlightCapture {runId="analytic-fixture",vesselId="analytic-vessel",bodyId="analytic-body",processId=1,
            nativeManifestSha256=native,partSpecsSha256=parts,sourceEpochSeconds=0,gravitationalParameter=100000,radiusMeters=100,
            atmosphereDepthMeters=0,solarFluxWattsPerSquareMeter=1,angularVelocity=new double[3],sunDirection=new[]{1.0,0,0},
            bodyRotation=new[]{0.0,0,0,1},altitudeMeters=new double[0],densityKilogramsPerCubicMeter=new double[0],temperatureKelvin=new double[0],
            rootPositionBodyRelative=new[]{1000.0,0,0},rootRotation=new[]{0.0,0,0,1},centerOfMassVelocityInertial=new[]{0.0,10,0},omissions=new string[0]},native,parts,new string('c',64));
        var definition=new OwnedRuntimeDefinition("analytic-flight",0,new[]{new OwnedRuntimePartDefinition(10,"part",20,new Vec(),Rotation.Identity,100)},
            new[]{new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity))},new OwnedRuntimeResourceDefinition[0]);
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var systems=new OwnedRuntimeSystems(world,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0]);
        imported=new OwnedRuntimeImportResult {World=world,Systems=systems,Source=new OwnedRuntimeSourceBinding {runId="analytic-fixture",vesselId="analytic-vessel",processId=1,
            nativeManifestSha256=native,partSpecsSha256=parts,scope="Analytic test only; not acquired craft evidence.",resourcePolicy="strict-capacity/v1",
            bodies=new[]{new OwnedRuntimeBodyBinding{sourceBodyId=20,bodyId=20,bodySlot=0}},renderers=new OwnedRuntimeRendererBinding[0],omissions=new string[0],
            subtractedNativeCenterVelocity=new double[3],sourceFrameVelocity=new double[3]}};
        host=new OwnedRuntimeHost(world,systems);return OwnedFlightSession.SeedCaptured(world,context,new Vec(1000,0,0));
    }
    static void Near(double value,double expected,double tolerance)
    {if(double.IsNaN(value)||double.IsInfinity(value)||Math.Abs(value-expected)>tolerance)throw new Exception("Flight control oracle differs: "+value+" vs "+expected);}
    static void Need(bool ok,string reason){if(!ok)throw new Exception(reason);}
}
