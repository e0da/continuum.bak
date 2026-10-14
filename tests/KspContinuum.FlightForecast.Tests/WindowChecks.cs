using System;
using KspContinuum;

static class WindowChecks
{
    static void Need(bool value,string message){if(!value)throw new Exception(message);}
    static void Near(double value,double expected,double tolerance=1e-10){Need(Math.Abs(value-expected)<=tolerance*Math.Max(1,Math.Abs(expected)),value+" != "+expected);}
    static OwnedRuntimeHost Fixture(out OwnedFlightSession session,double time=0,double epoch=100)
    {
        string a=new string('a',64),b=new string('b',64);
        var capture=new OwnedFlightCapture{runId="window",processId=1,vesselId="craft",bodyId="body",nativeManifestSha256=a,partSpecsSha256=b,sourceEpochSeconds=100,gravitationalParameter=4e6,radiusMeters=1000,atmosphereDepthMeters=100,vesselBoundingRadiusMeters=0,angularVelocity=new double[3],sunDirection=new[]{1d,0,0},bodyRotation=new[]{0d,0,0,1},rootRotation=new[]{0d,0,0,1},rootPositionBodyRelative=new[]{2000d,0,0},centerOfMassVelocityInertial=new[]{0d,20,0},altitudeMeters=new[]{0d,50,100},densityKilogramsPerCubicMeter=new[]{1d,.25,0},temperatureKelvin=new[]{300d,250,3},omissions=new string[0]};
        var definition=new OwnedRuntimeDefinition("window",0,new[]{new OwnedRuntimePartDefinition(1,"one",10,new Vec(),Rotation.Identity,100)},new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity))},new OwnedRuntimeResourceDefinition[0],new long[0]);
        capture.sourceEpochSeconds=epoch;
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity},time,"cold");
        session=OwnedFlightSession.SeedCaptured(world,OwnedFlightWorld.Admit(capture,a,b,new string('c',64)),new Vec(2000,0,0));
        return new OwnedRuntimeHost(world,new OwnedRuntimeSystems(world,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0],new OwnedRuntimeThermalLink[0]));
    }
    internal static void Run()
    {
        OwnedFlightSession largeEpoch;var largeHost=Fixture(out largeEpoch,epoch:1000000);var largeSource=largeHost.World.Stamp;
        largeEpoch.PlaceCircular(largeSource,1000,new Vec(1,0,0),new Vec(0,0,1));largeSource=largeHost.World.Stamp;
        OwnedRuntimeMotion[] largeMotions;double largeTime;string largeReason;OwnedFlightCoastWindow largeWindow;
        Need(largeEpoch.PrepareCoastWindow(largeSource,largeEpoch.UniversalTime+.3,new OwnedRuntimeControls(),true,double.PositiveInfinity,0,out largeMotions,out largeTime,out largeReason,out largeWindow,.3),largeReason);
        Need(largeTime==.3&&largeSource.Matches(largeHost.World.Stamp),"Large-epoch roundtrip lost the explicit runtime upper bound.");
        OwnedFlightSession session;var host=Fixture(out session);double apo=2000,peri=1050,major=(apo+peri)*.5,mu=session.Context.Mu;
        session.Place(host.World.Stamp,new Vec(apo,0,0),new Vec(0,Math.Sqrt(mu*(2/apo-1/major)),0));var source=host.World.Stamp;
        double period=2*Math.PI*Math.Sqrt(major*major*major/mu);OwnedRuntimeMotion[] motions;double end;string reason;OwnedFlightCoastWindow window;
        Need(!session.PrepareCoast(source,session.UniversalTime+period,new OwnedRuntimeControls(),true,double.PositiveInfinity,0,out motions,out end,out reason),"Strict coast silently changed semantics.");
        Need(session.PrepareCoastWindow(source,session.UniversalTime+period,new OwnedRuntimeControls(),true,double.PositiveInfinity,0,out motions,out end,out reason,out window),reason);
        Need(source.Matches(host.World.Stamp)&&window.Limited&&window.Kind=="atmosphere-entry"&&window.ReachedUniversalTime<window.LowerCrossingUniversalTime&&window.LowerCrossingUniversalTime<=window.UpperCrossingUniversalTime,"Partial source/guard/bracket differs.");
        double eccentric=(apo-peri)/(apo+peri),anomaly=2*Math.PI-Math.Acos((1-1100/major)/eccentric),mean=anomaly-eccentric*Math.Sin(anomaly),analytic=(mean-Math.PI)/Math.Sqrt(mu/(major*major*major));
        Need(window.LowerCrossingUniversalTime-100<=analytic+1e-7&&window.UpperCrossingUniversalTime-100>=analytic-1e-7,"Independent anomaly entry is outside the numerical bracket.");
        Need(window.UpperCrossingUniversalTime-window.LowerCrossingUniversalTime<=1e-6,"Entry bracket lost bounded resolution.");
        var timeline=new OwnedFlightTimeline(host,session,0);timeline.Configure(source,timeline.InputRevision,0,new OwnedRuntimeControls(),1,false,true,true);
        var progress=timeline.Pump(period,1);Need(progress.Fault==null&&progress.CoastCommits==1&&progress.DebtSeconds>0&&progress.CommittedRuntimeTime<period,"Partial coast discarded remainder.");
        progress=timeline.Pump(period,1);Need(progress.Fault==null&&progress.LocalCommits==1&&progress.CoastCommits==0&&progress.DebtSeconds>0,"Coast handoff chattered or lost local progress.");
        using(var read=host.World.AcquireRead())Need(read.ThermalPendingDuration==0,"Reference thermal invented pending ledger.");
        host=Fixture(out session);host.Systems.SetThermalPolicy(host.World.Stamp,new OwnedRuntimeThermalPolicy(true,maximumLagSeconds:.03));
        timeline=new OwnedFlightTimeline(host,session,0);timeline.Configure(host.World.Stamp,timeline.InputRevision,0,new OwnedRuntimeControls(),1,false);
        progress=timeline.Pump(.04,1);Near(progress.CommittedRuntimeTime,.02);
        using(var read=host.World.AcquireRead())
        {double interval=OwnedFlightWindows.ThermalLagInterval(read,.02);Near(interval,.01);Need((read.Stamp.Time+interval)-read.ThermalIntegratedTime>=.03,"Thermal cap failed original predicate.");}
        progress=timeline.Pump(.04,1);Need(progress.Fault==null&&timeline.LastLocalWindow.Kind=="thermal-lag"&&!timeline.LastLocalWindow.Advisory,"Thermal deadline not selected.");Near(progress.CommittedRuntimeTime,.03);
        using(var read=host.World.AcquireRead()){Need(read.ThermalPendingDuration==0,"Thermal lag did not flush at selected boundary.");Near(read.ThermalIntegratedTime,.03);}
        host=Fixture(out session,.3);host.Systems.SetThermalPolicy(host.World.Stamp,new OwnedRuntimeThermalPolicy(true,maximumLagSeconds:.1));
        using(var read=host.World.AcquireRead()){double dt=OwnedFlightWindows.ThermalLagInterval(read,.1);Need(dt<=.1&&(read.Stamp.Time+dt)-read.ThermalIntegratedTime>=.1,"Rounded thermal endpoint exceeded raw interval bound.");}
        foreach(double start in new[]{.2,1000000d})
        {
            host=Fixture(out session,start);host.Systems.SetThermalPolicy(host.World.Stamp,new OwnedRuntimeThermalPolicy(true,maximumLagSeconds:.03));
            session.AdvanceLocal(host,.02,new OwnedRuntimeControls());
            using(var read=host.World.AcquireRead())
            {double dt=OwnedFlightWindows.ThermalLagInterval(read,.02),target=read.Stamp.Time+dt;Need(target-read.ThermalIntegratedTime>=.03,"Nonzero thermal predicate missed.");double previous=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(target)-1);Need(previous-read.ThermalIntegratedTime<.03,"Nonzero thermal deadline isn't earliest endpoint.");}
        }
    }
}
