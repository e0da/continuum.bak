using System;
using KspContinuum;

public static class FlightWorldTests
{
    static readonly string NativeSha=new string('a',64),PartsSha=new string('b',64),WorldSha=new string('c',64);
    static void Need(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static void Near(double actual,double expected,double tolerance=2e-10)
    {if(double.IsNaN(actual)||double.IsInfinity(actual)||Math.Abs(actual-expected)>tolerance*Math.Max(1,Math.Abs(expected)))throw new Exception("Owned flight mismatch "+actual+" versus "+expected);}
    static void Near(Vec actual,Vec expected,double tolerance=2e-10){Near(actual.X,expected.X,tolerance);Near(actual.Y,expected.Y,tolerance);Near(actual.Z,expected.Z,tolerance);}
    static void Reject(Action action)
    {try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Invalid owned flight operation accepted.");}
    static double Dot(Vec a,Vec b){return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
    static double Norm(Vec v){return Math.Sqrt(Dot(v,v));}
    static OwnedFlightCapture Capture()
    {
        return new OwnedFlightCapture {runId="analytic-flight",processId=1,vesselId="fixture",bodyId="analytic-central-body",
            nativeManifestSha256=NativeSha,partSpecsSha256=PartsSha,sourceEpochSeconds=12345,gravitationalParameter=4e6,radiusMeters=1000,
            atmosphereDepthMeters=100,angularVelocity=new[]{0d,0d,.01},sunDirection=new[]{1d,0d,0d},solarFluxWattsPerSquareMeter=1000,
            bodyRotation=new[]{0d,0d,0d,1d},rootPositionBodyRelative=new[]{1200d,0d,0d},rootRotation=new[]{0d,0d,0d,1d},
            centerOfMassVelocityInertial=new[]{0d,Math.Sqrt(4e6/1200),0d},altitudeMeters=new[]{0d,50d,100d},
            densityKilogramsPerCubicMeter=new[]{1d,.25,0d},temperatureKelvin=new[]{300d,250d,3d},omissions=new string[0]};
    }
    static OwnedFlightWorld Context(){return OwnedFlightWorld.Admit(Capture(),NativeSha,PartsSha,WorldSha);}
    static OwnedRuntimeWorld Runtime(Vec momentum=default(Vec),bool provider=false)
    {
        var member=new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity);
        var definition=new OwnedRuntimeDefinition("analytic-flight",0,new[]{new OwnedRuntimePartDefinition(1,"single",10,new Vec(),Rotation.Identity,100)},
            new[]{new OwnedRuntimeBodyDefinition(10,member)},new OwnedRuntimeResourceDefinition[0],provider?new long[]{9}:new long[0]);
        var topology=OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]);
        return new OwnedRuntimeWorld(definition,topology,new OwnedRuntimeMotion{RootPosition=new Vec(),Orientation=Rotation.Identity,Velocity=new Vec(),AngularMomentum=momentum},0,"cold-import");
    }
    static OwnedFlightSession Session(Vec momentum=default(Vec),bool provider=false)
    {return OwnedFlightSession.SeedCaptured(Runtime(momentum,provider),Context(),new Vec(1200,0,0));}
    static OwnedRuntimeControls Quiet(){return new OwnedRuntimeControls{EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};}
    static void MassTransportGuard()
    {
        var dry=new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity);
        var definition=new OwnedRuntimeDefinition("mass-transport-flight",0,new[]{
            new OwnedRuntimePartDefinition(1,"dry",10,new Vec(),Rotation.Identity,100),
            new OwnedRuntimePartDefinition(2,"offset-fuel",10,new Vec(0,1,0),Rotation.Identity,100)},
            new[]{new OwnedRuntimeBodyDefinition(10,dry)},new[]{new OwnedRuntimeResourceDefinition(3,2,"fuel",2,1,2)},new long[0]);
        var runtime=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),
            new OwnedRuntimeMotion {Orientation=Rotation.Identity});
        OwnedRuntimeMotion initial;using(var read=runtime.AcquireRead())
        {initial=read.GetRegion(0).Motion;initial.AngularMomentum=OwnedRuntimeDynamics.Momentum(read.GetRegion(0).Properties,Rotation.Identity,new Vec(0,0,-4000));}
        runtime.SetRegionMotions(runtime.Stamp,new[]{initial},runtime.Stamp.Time,"cold-import");
        var data=Capture();data.rootPositionBodyRelative=new[]{1005d,0d,0d};data.centerOfMassVelocityInertial=new[]{1000d,0d,0d};data.vesselBoundingRadiusMeters=1;
        var session=OwnedFlightSession.SeedCaptured(runtime,OwnedFlightWorld.Admit(data,NativeSha,PartsSha,WorldSha),new Vec(1005,0,0));
        var expected=runtime.Stamp;Vec originalVelocity;using(var read=runtime.AcquireRead())originalVelocity=read.GetRegion(0).Motion.Velocity;
        using(var step=runtime.BeginStep(expected,.02,new OwnedRuntimeInput()))
        {
            step.SetResourceQuantity(0,0);step.RefreshMassProperties();var state=step.GetRegion(0);
            Near(state.Motion.Velocity,new Vec(-1000,0,0));
            session.Sample(expected,0,state.Motion,state.Properties);
            var motion=state.Motion;OwnedRuntimeDynamics.Step(ref motion,state.Properties,new Vec(200000,0,0),new Vec(),.02);step.SetRegionMotion(0,motion);
            Near(motion.Velocity,originalVelocity);Near(session.Origin+motion.RootPosition,new Vec(1005,0,0));
            // Both endpoints clear the sphere, but x(t)=1005-1000*t+50000*t*t reaches 1000 at t=.01.
            Need(1005-1000*.01+50000*.01*.01<1001,"Independent curved-path fixture is not inside its guard.");
            Reject(()=>session.ValidateStep(step));
        }
        Need(runtime.Stamp.Matches(expected),"Mass-transport surface guard published its rejected transaction.");
    }
    public static void Run()
    {
        var bounded=OwnedFlightSession.SeedCaptured(Runtime(),Context(),new Vec(1200,0,0),150);
        var boundedStamp=bounded.Runtime.Stamp;
        Reject(()=>bounded.Place(boundedStamp,new Vec(1120,0,0),new Vec()));
        Need(bounded.Runtime.Stamp.Matches(boundedStamp),"Rejected domain placement published state.");
        double domainApo=1400,domainPeri=1120,domainSemimajor=(domainApo+domainPeri)*.5;
        bounded.Place(boundedStamp,new Vec(domainApo,0,0),new Vec(0,Math.Sqrt(bounded.Context.Mu*(2/domainApo-1/domainSemimajor)),0));
        string boundedReason;var coastStamp=bounded.Runtime.Stamp;
        Need(!bounded.TryCoast(coastStamp,bounded.UniversalTime+Math.PI*Math.Sqrt(domainSemimajor*domainSemimajor*domainSemimajor/bounded.Context.Mu),Quiet(),true,double.PositiveInfinity,0,out boundedReason),"Coast crossed the higher domain floor despite clearing atmosphere.");
        Need(bounded.Runtime.Stamp.Matches(coastStamp),"Rejected domain coast published state.");
        var boundedRestore=OwnedFlightSession.Restore(OwnedRuntimeWorld.RestoreSnapshot(bounded.Runtime.CaptureSnapshot()),bounded.Capture(),NativeSha,PartsSha);
        Need(boundedRestore.MinimumAltitudeMeters==150,"Session checkpoint discarded its domain floor.");
        var data=Capture();var context=OwnedFlightWorld.Admit(data,NativeSha,PartsSha,WorldSha);
        data.altitudeMeters[1]=99;data.angularVelocity[2]=123;data.rootPositionBodyRelative[0]=900;
        Near(context.CapturedRootPosition,new Vec(1200,0,0));Near(context.AngularVelocity,new Vec(0,0,.01));
        var sample=context.EvaluateSample(new Vec(1025,0,0),context.Epoch,0);
        Near(sample.Density,.625);Near(sample.Temperature,275);Near(sample.Gravity,new Vec(-4e6/(1025d*1025),0,0));
        Near(sample.WindVelocity,new Vec(0,10.25,0));Near(sample.FlowAngularVelocity,new Vec(0,0,.01));Near(sample.Epoch,0);
        Near(context.EvaluateSample(new Vec(1100,0,0),context.Epoch,0).Density,0);
        Near(context.BodyRotationAt(context.Epoch+Math.PI/(2*.01)).Rotate(new Vec(1,0,0)),new Vec(0,1,0));
        Reject(()=>context.EvaluateSample(new Vec(1000,0,0),context.Epoch,0));
        Reject(()=>OwnedFlightWorld.Admit(Capture(),PartsSha,PartsSha,WorldSha));
        data=Capture();data.altitudeMeters[1]=100;Reject(()=>OwnedFlightWorld.Admit(data,NativeSha,PartsSha,WorldSha));
        data=Capture();data.densityKilogramsPerCubicMeter[2]=.01;Reject(()=>OwnedFlightWorld.Admit(data,NativeSha,PartsSha,WorldSha));

        var session=Session();double radius=1200,speed=Math.Sqrt(context.Mu/radius),period=2*Math.PI*Math.Sqrt(radius*radius*radius/context.Mu);
        Near(session.CenterPosition(0),new Vec(radius,0,0));Near(session.Runtime.Stamp.Time,0);Near(session.UniversalTime,context.Epoch);
        string reason;var stamp=session.Runtime.Stamp;OwnedRuntimeMotion[] prepared;double runtimeTime;
        Need(session.PrepareCoast(stamp,context.Epoch+period*.25,Quiet(),true,double.PositiveInfinity,0,out prepared,out runtimeTime,out reason),reason);
        Need(session.Runtime.Stamp.Matches(stamp),"Preparing a coast changed authoritative state.");
        Near(prepared[0].RootPosition+session.Origin,new Vec(0,radius,0),2e-8);Near(prepared[0].Velocity,new Vec(-speed,0,0),2e-8);
        Need(session.TryCoast(stamp,context.Epoch+period*.25,Quiet(),true,double.PositiveInfinity,0,out reason),reason);
        Near(session.CenterPosition(0),new Vec(0,radius,0),2e-8);
        using(var read=session.Runtime.AcquireRead())
        {
            Vec r=session.CenterPosition(0),v=read.GetRegion(0).Motion.Velocity;
            Near(.5*Dot(v,v)-context.Mu/Norm(r),-context.Mu/(2*radius),2e-10);
            Near(Vec.Cross(r,v),new Vec(0,0,radius*speed),2e-10);
        }
        Near(session.UniversalTime,context.Epoch+period*.25);
        var center=session.CenterPosition(0);var gravity=session.EvaluateRegion(0).Gravity;
        session.Rebase(session.Runtime.Stamp,new Vec(-10000,2500,-3000));Near(session.CenterPosition(0),center);Near(session.EvaluateRegion(0).Gravity,gravity);
        var saved=session.Capture();var clone=OwnedFlightSession.Restore(OwnedRuntimeWorld.RestoreSnapshot(session.Runtime.CaptureSnapshot()),saved,NativeSha,PartsSha);
        Need(clone.Mode=="kepler","Coast mode was not retained.");Near(clone.CenterPosition(0),session.CenterPosition(0));Near(clone.UniversalTime,session.UniversalTime);

        session=Session();stamp=session.Runtime.Stamp;
        Need(!session.TryCoast(stamp,session.UniversalTime+1,Quiet(),false,double.PositiveInfinity,0,out reason),"Unqualified passive state was skipped.");
        var active=Quiet();active.Throttle=.01;Need(!session.TryCoast(stamp,session.UniversalTime+1,active,true,double.PositiveInfinity,0,out reason),"Powered coast was accepted.");
        Need(!session.TryCoast(stamp,session.UniversalTime+1,Quiet(),true,session.UniversalTime+1,0,out reason),"Pending event was overrun.");
        Need(session.Runtime.Stamp.Matches(stamp),"Rejected coast published state.");
        var spinning=Session(new Vec(0,0,1));Need(!spinning.TryCoast(spinning.Runtime.Stamp,spinning.UniversalTime+1,Quiet(),true,double.PositiveInfinity,0,out reason),"Packed coast silently froze rotating attitude.");
        var providerSession=Session(new Vec(),true);
        using(var step=providerSession.Runtime.BeginStep(providerSession.Runtime.Stamp,.02,new OwnedRuntimeInput())){step.SetProviderState(0,1);step.Commit();}
        Need(!providerSession.TryCoast(providerSession.Runtime.Stamp,providerSession.UniversalTime+1,Quiet(),true,double.PositiveInfinity,0,out reason),"Packed coast ignored provider state.");

        session=Session();double apo=2000,peri=1050,a=(apo+peri)*.5;
        session.Place(session.Runtime.Stamp,new Vec(apo,0,0),new Vec(0,Math.Sqrt(context.Mu*(2/apo-1/a)),0));stamp=session.Runtime.Stamp;
        double eccentricPeriod=2*Math.PI*Math.Sqrt(a*a*a/context.Mu);
        Need(!session.TryCoast(stamp,session.UniversalTime+eccentricPeriod,Quiet(),true,double.PositiveInfinity,0,out reason),"An intermediate atmospheric crossing was missed because endpoint is vacuum.");
        Need(session.Runtime.Stamp.Matches(stamp),"Entry rejection published a partial coast.");
        session.Place(session.Runtime.Stamp,new Vec(2000,0,0),new Vec(-2000,0,0));stamp=session.Runtime.Stamp;
        Need(!session.TryCoast(stamp,session.UniversalTime+1,Quiet(),true,double.PositiveInfinity,0,out reason),"Unsafe radial/hyperbolic event was treated as no crossing.");

        session=Session();var systems=new OwnedRuntimeSystems(session.Runtime,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0],new OwnedRuntimeThermalLink[0]);
        var host=new OwnedRuntimeHost(session.Runtime,systems);session.Place(session.Runtime.Stamp,new Vec(1001,0,0),new Vec(-200,0,0));stamp=session.Runtime.Stamp;
        Reject(()=>session.AdvanceLocal(host,.02,Quiet()));Need(session.Runtime.Stamp.Matches(stamp),"Local sphere rejection published before its guard.");
        session=Session();session.Runtime.Rebase(session.Runtime.Stamp,new Vec(1,0,0),session.FrameId);Reject(()=>session.EvaluateRegion(0));
        MassTransportGuard();
        Console.WriteLine("PASS captured world frame/flow, circular Kepler conservation, retained origin, packed eligibility and atomic surface/entry/event guards");
    }
}
