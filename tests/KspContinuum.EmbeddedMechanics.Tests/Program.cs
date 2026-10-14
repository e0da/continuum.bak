using System;
using KspContinuum;

static class Program
{
    sealed class Fixture
    {
        public readonly OwnedRuntimeWorld World;
        public readonly OwnedRuntimeSystems Systems;
        public readonly OwnedRuntimeHost Host;
        public readonly OwnedFlightSession Session;
        public Fixture(bool orbit=false,bool wheel=false,double initialTime=0,double force=11,double fuel=1,double isp=1e100,bool opposing=false)
        {
            var part=new OwnedRuntimePartDefinition(1,"analytic",10,new Vec(),Rotation.Identity,100);
            var body=new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,10,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity));
            var resource=new OwnedRuntimeResourceDefinition(2,1,"fuel",20,1,fuel);
            var resources=opposing?new[]{resource,new OwnedRuntimeResourceDefinition(3,1,"second-fuel",20,1,fuel)}:new[]{resource};
            var definition=new OwnedRuntimeDefinition("embedded-analytic",0,new[]{part},new[]{body},resources,wheel?new long[]{9}:new long[0]);
            World=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity},initialTime,"cold");
            var capture=new OwnedFlightCapture{runId="embedded-analytic",processId=1,vesselId="fixture",bodyId="central",nativeManifestSha256=new string('a',64),partSpecsSha256=new string('b',64),
                sourceEpochSeconds=0,gravitationalParameter=orbit?4e6:1e-20,radiusMeters=1000,atmosphereDepthMeters=0,angularVelocity=new double[3],sunDirection=new[]{1d,0,0},solarFluxWattsPerSquareMeter=0,
                bodyRotation=new[]{0d,0,0,1},rootPositionBodyRelative=new[]{1200d,0,0},rootRotation=new[]{0d,0,0,1},centerOfMassVelocityInertial=new[]{0d,orbit?Math.Sqrt(4e6/1200):0,0},
                altitudeMeters=new double[0],densityKilogramsPerCubicMeter=new double[0],temperatureKelvin=new double[0],omissions=new string[0]};
            Session=OwnedFlightSession.SeedCaptured(World,OwnedFlightWorld.Admit(capture,new string('a',64),new string('b',64),new string('c',64)),new Vec(1200,0,0));
            var actuator=wheel?new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,1,0),1,wheelMomentumCapacity:100):
                new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(0,0,1),force,0,isp);
            var actuators=opposing?new[]{actuator,new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(0,0,-1),force,1,isp)}:new[]{actuator};
            Systems=new OwnedRuntimeSystems(World,actuators,new OwnedRuntimeSurfaceSpec[0],wheelProviderSlots:wheel?new[]{0}:null);Host=new OwnedRuntimeHost(World,Systems);
        }
        public void Embedded(double tolerance=1e-4,double maximum=.1)
        {Systems.SetMechanicalPolicy(World.Stamp,new OwnedRuntimeMechanicalPolicy(true,maximumInterval:maximum,positionTolerance:tolerance,velocityTolerance:tolerance,initialInterval:maximum,method:OwnedRuntimeMechanicalMethod.EmbeddedMidpoint));}
        public OwnedRuntimeMotion Motion{get{using(var read=World.AcquireRead())return read.GetRegion(0).Motion;}}
        public RigidAssemblyProperties Properties{get{using(var read=World.AcquireRead())return read.GetRegion(0).Properties;}}
        public double Fuel{get{using(var read=World.AcquireRead())return read.GetResourceQuantity(0);}}
    }
    sealed class MarkedSchedule : IOwnedRuntimeTrialSafeSchedule
    {
        public string Schema{get{return "continuum-owned-runtime-schedule/v1";}}public string StateSchema{get{return "world-bank-state-only/v1";}}public string ImplementationId{get{return "marker-is-not-stage-admission";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls){systems.Step(step,environment,controls);}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls){systems.Step(step,field,controls);}
    }
    static OwnedRuntimeControls Controls(double throttle=0,Vec attitude=default(Vec))
    {return new OwnedRuntimeControls{Throttle=throttle,Attitude=attitude,EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};}
    static void Need(bool value,string reason){if(!value)throw new Exception(reason);}
    static void Near(double actual,double expected,double tolerance=1e-11)
    {Need(!double.IsNaN(actual)&&!double.IsInfinity(actual)&&Math.Abs(actual-expected)<=tolerance*Math.Max(1,Math.Abs(expected)),actual+" != "+expected);}
    static void Near(Vec a,Vec b,double tolerance=1e-11){Near(a.X,b.X,tolerance);Near(a.Y,b.Y,tolerance);Near(a.Z,b.Z,tolerance);}
    static double Norm(Vec v){return Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);}
    static void ConstantAndChangingMass()
    {
        var fixedMass=new Fixture();fixedMass.Embedded();var stamp=fixedMass.World.Stamp;fixedMass.Session.AdvanceLocal(fixedMass.Host,.1,Controls(1));
        Near(fixedMass.Motion.Velocity.Z,.1);Near(fixedMass.Motion.RootPosition.Z,.005);
        Need(fixedMass.World.Stamp.Time==.1&&fixedMass.World.Stamp.StateRevision==stamp.StateRevision+1&&fixedMass.Host.AdaptiveObservation.EmbeddedForceSamples==2&&fixedMass.World.Mechanical.rejected==0,"Constant acceleration manufactured retries or multiple publications.");
        var burning=new Fixture(force:10,fuel:10,isp:1);burning.Embedded();double initial=burning.Properties.Mass,h=.1,rate=10/9.80665;
        burning.Session.AdvanceLocal(burning.Host,h,Controls(1));double terminal=initial-rate*h,log=Math.Log(initial/terminal);
        Near(burning.Fuel,10-rate*h);Near(burning.Properties.Mass,terminal);
        Near(burning.Motion.Velocity.Z,10/rate*log);Near(burning.Motion.RootPosition.Z,10/(rate*rate)*(rate*h-terminal*log),1e-10);
        Need(burning.World.Stamp.Time==h&&burning.World.CaptureSnapshot().InputHistory.Length==1,"Mass quadrature replayed the resource transaction.");
        var tail=new Fixture(initialTime:31.052604680881637);tail.Embedded();double start=tail.World.Stamp.Time,end=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(start)+1);
        tail.Session.AdvanceLocal(tail.Host,end-start,Controls(1));Need(tail.World.Stamp.Time==end&&tail.World.Mechanical.roundoffCoalescedIntervals==1&&tail.Host.AdaptiveObservation.EmbeddedForceSamples==1,"Adjacent clock tail fabricated two samples or lost time.");
        var hidden=new Fixture(initialTime:start,force:1e20,fuel:10,isp:1,opposing:true);hidden.Embedded();stamp=hidden.World.Stamp;bool rejected=false;
        try{hidden.Session.AdvanceLocal(hidden.Host,end-start,Controls(1));}catch(OwnedRuntimeMechanicalAccuracyException){rejected=true;}
        Need(rejected&&hidden.World.Stamp.Matches(stamp)&&hidden.Fuel==10,"Zero net wrench concealed a large coalesced resource change.");
    }
    static double Energy(OwnedRuntimeMotion motion,RigidAssemblyProperties properties)
    {Vec omega=OwnedRuntimeDynamics.AngularVelocity(properties,motion.Orientation,motion.AngularMomentum);return .5*(omega.X*motion.AngularMomentum.X+omega.Y*motion.AngularMomentum.Y+omega.Z*motion.AngularMomentum.Z);}
    static void SpinAndWheel()
    {
        var wheel=new Fixture(wheel:true);var motion=wheel.Motion;motion.AngularMomentum=new Vec(.2,.3,-.4);
        wheel.World.SetRegionMotions(wheel.World.Stamp,new[]{motion},0,wheel.World.Stamp.FrameId);wheel.Embedded();double h=.01;
        wheel.Session.AdvanceLocal(wheel.Host,h,Controls(attitude:new Vec(0,1,0)));
        using(var read=wheel.World.AcquireRead())
        {var actual=read.GetRegion(0).Motion;double rotor=read.GetProviderState(0);Near(rotor,-wheel.World.Stamp.Time);Near(actual.AngularMomentum+actual.Orientation.Rotate(new Vec(0,rotor,0)),motion.AngularMomentum);}
        var free=new Fixture();motion=free.Motion;motion.AngularMomentum=new Vec(.1,.2,.3);
        free.World.SetRegionMotions(free.World.Stamp,new[]{motion},0,free.World.Stamp.FrameId);free.Embedded(maximum:.01);double energy=Energy(motion,free.Properties);
        double maximumEstimate=0;
        while(free.World.Stamp.Time<2){free.Session.AdvanceLocal(free.Host,Math.Min(.01,2-free.World.Stamp.Time),Controls());maximumEstimate=Math.Max(maximumEstimate,free.Host.AdaptiveObservation.LastError);}
        Near(free.Motion.AngularMomentum,motion.AngularMomentum);Near(Energy(free.Motion,free.Properties),energy,1e-8);
        Need(maximumEstimate>0,"Asymmetric free spin was invisible to the embedded rotation estimate.");
    }
    static double Orbit(double tolerance)
    {
        var orbit=new Fixture(orbit:true);orbit.Embedded(tolerance);
        while(orbit.World.Stamp.Time<2)orbit.Session.AdvanceLocal(orbit.Host,Math.Min(.1,2-orbit.World.Stamp.Time),Controls());
        double angle=Math.Sqrt(4e6/1200)/1200*2;Vec exact=new Vec(1200*Math.Cos(angle),1200*Math.Sin(angle),0);
        return Norm(orbit.Session.CenterPosition(0)+exact*-1);
    }
    static void RejectionAndAdmission()
    {
        var f=new Fixture(orbit:true,isp:1000);f.Systems.SetMechanicalPolicy(f.World.Stamp,new OwnedRuntimeMechanicalPolicy(true,positionTolerance:1e-30,velocityTolerance:1e-30,angleTolerance:1e-30,scaledStateTolerance:1e-30,method:OwnedRuntimeMechanicalMethod.EmbeddedMidpoint));
        var stamp=f.World.Stamp;var motion=f.Motion;double fuel=f.Fuel;bool rejected=false;
        try{f.Session.AdvanceLocal(f.Host,.02,Controls(1));}catch(OwnedRuntimeMechanicalAccuracyException){rejected=true;}
        Need(rejected&&f.World.Stamp.Matches(stamp)&&f.Fuel==fuel&&f.World.CaptureSnapshot().InputHistory.Length==0,"Rejected embedded attempt published state.");
        Near(f.Motion.RootPosition,motion.RootPosition);Need(f.Systems.Counters.PropellantKilograms==0&&f.Systems.Counters.DeliveredForceNewtonSeconds==0,"Rejected scalar intent appeared as delivered physical work.");
        long code=f.Host.CodeGeneration;rejected=false;try{f.Host.SwapSchedule(new MarkedSchedule(),new string('d',64),stamp);}catch(InvalidOperationException){rejected=true;}
        Need(rejected&&f.Host.CodeGeneration==code&&f.World.Stamp.Matches(stamp),"TrialSafe marker silently acquired the builtin derivative seam.");
        double loose=Orbit(1e-4),tight=Orbit(1e-5);Need(tight<loose&&tight<.02,"Embedded central gravity did not converge toward independent circular motion: "+loose+" / "+tight);
    }
    public static int Main()
    {ConstantAndChangingMass();SpinAndWheel();RejectionAndAdmission();Console.WriteLine("PASS embedded mechanics: independent force/depletion integrals, wheel total-H, asymmetric spin energy, orbital convergence, exact clock tails, atomic rejection and builtin stage admission");return 0;}
}
