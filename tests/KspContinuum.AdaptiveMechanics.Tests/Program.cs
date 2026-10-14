using System;
using System.IO;
using KspContinuum;

static class Program
{
    static readonly string Native=new string('a',64),Parts=new string('b',64);
    sealed class Fixture
    {
        public OwnedRuntimeWorld World;public OwnedRuntimeSystems Systems;public OwnedRuntimeHost Host;public OwnedFlightSession Session;public OwnedRuntimeImportResult Imported;
        public Fixture(bool orbit=false,bool pair=false,bool wheel=false,double fuel=1,double isp=1e100,double initialTime=0)
        {
            var first=new RigidAssemblyMember(0,10,pair?new Vec(-1,0,0):new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity);
            var second=new RigidAssemblyMember(1,3,new Vec(1,0,0),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity);
            var parts=pair?new[]{new OwnedRuntimePartDefinition(1,"first",10,new Vec(0,1,0),Rotation.Identity,100),new OwnedRuntimePartDefinition(2,"second",20,new Vec(0,-1,0),Rotation.Identity,100)}:
                new[]{new OwnedRuntimePartDefinition(1,"first",10,new Vec(),Rotation.Identity,100)};
            var bodies=pair?new[]{new OwnedRuntimeBodyDefinition(10,first),new OwnedRuntimeBodyDefinition(20,second)}:new[]{new OwnedRuntimeBodyDefinition(10,first)};
            var resources=orbit?new OwnedRuntimeResourceDefinition[0]:pair?new[]{new OwnedRuntimeResourceDefinition(2,1,"fuel",1,1,fuel),new OwnedRuntimeResourceDefinition(3,2,"fuel",1,1,.5)}:new[]{new OwnedRuntimeResourceDefinition(2,1,"fuel",1,1,fuel)};
            var d=new OwnedRuntimeDefinition("adaptive-analytic",0,parts,bodies,resources,wheel?new long[]{9}:new long[0]);
            var links=pair?new[]{new OwnedRuntimeConnection(1,1,2,true)}:new OwnedRuntimeConnection[0];
            World=new OwnedRuntimeWorld(d,OwnedRuntimeTopology.Compile(d,links),new OwnedRuntimeMotion{Orientation=Rotation.Identity},initialTime,"cold-import");
            double mu=orbit?4e6:1e-20;
            var data=new OwnedFlightCapture {runId="adaptive-analytic",processId=1,vesselId="fixture",bodyId="central",nativeManifestSha256=Native,partSpecsSha256=Parts,
                sourceEpochSeconds=0,gravitationalParameter=mu,radiusMeters=1000,atmosphereDepthMeters=0,angularVelocity=new[]{0d,0d,0d},sunDirection=new[]{1d,0d,0d},solarFluxWattsPerSquareMeter=0,
                bodyRotation=new[]{0d,0d,0d,1d},rootPositionBodyRelative=new[]{1200d,0d,0d},rootRotation=new[]{0d,0d,0d,1d},centerOfMassVelocityInertial=new[]{0d,orbit?Math.Sqrt(mu/1200):0,0d},
                altitudeMeters=new double[0],densityKilogramsPerCubicMeter=new double[0],temperatureKelvin=new double[0],omissions=new string[0]};
            Session=OwnedFlightSession.SeedCaptured(World,OwnedFlightWorld.Admit(data,Native,Parts,new string('c',64)),new Vec(1200,0,0));
            var actuators=orbit?new OwnedRuntimeActuatorSpec[0]:wheel?new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,1,0),1,wheelMomentumCapacity:100)}:
                pair?new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(0,0,1),1,0,isp),new OwnedRuntimeActuatorSpec(1,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(0,0,1),2,1,isp)}:
                new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(0,0,1),11,0,isp)};
            Systems=new OwnedRuntimeSystems(World,actuators,new OwnedRuntimeSurfaceSpec[0],wheelProviderSlots:wheel?new[]{0}:null);Host=new OwnedRuntimeHost(World,Systems);
            var bindings=new OwnedRuntimeBodyBinding[bodies.Length];for(int i=0;i<bindings.Length;i++)bindings[i]=new OwnedRuntimeBodyBinding {sourceBodyId=(int)bodies[i].Id,bodyId=bodies[i].Id,bodySlot=i};
            Imported=new OwnedRuntimeImportResult{World=World,Systems=Systems,Source=new OwnedRuntimeSourceBinding {runId="adaptive-analytic",processId=1,vesselId="fixture",nativeManifestSha256=Native,partSpecsSha256=Parts,
                resourcePolicy="strict-capacity/v1",scope="Independent analytic source",bodies=bindings,renderers=new OwnedRuntimeRendererBinding[0],omissions=new string[0],subtractedNativeCenterVelocity=new double[3],sourceFrameVelocity=new double[3]}};
        }
        public void Adaptive(OwnedRuntimeMechanicalPolicy policy=null){Systems.SetMechanicalPolicy(World.Stamp,policy??new OwnedRuntimeMechanicalPolicy(true));}
        public OwnedRuntimeMotion Motion {get{using(var read=World.AcquireRead())return read.GetRegion(0).Motion;}}
        public double Fuel {get{using(var read=World.AcquireRead())return read.GetResourceQuantity(0);}}
    }
    sealed class OrdinarySchedule : IOwnedRuntimeFieldSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}public string StateSchema {get{return "world-bank-state-only/v1";}}public string ImplementationId {get{return "original-reference-only";}}
        public void Step(OwnedRuntimeSystems s,OwnedRuntimeStep step,OwnedRuntimeEnvironment e,OwnedRuntimeControls c){s.Step(step,e,c);}
        public void Step(OwnedRuntimeSystems s,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField e,OwnedRuntimeControls c){s.Step(step,e,c);}
    }
    sealed class UnspecifiedScalarSchedule : IOwnedRuntimeTrialSafeSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}public string StateSchema {get{return "world-bank-state-only/v1";}}public string ImplementationId {get{return "trial-safe-unqualified-scalar";}}
        public void Step(OwnedRuntimeSystems s,OwnedRuntimeStep step,OwnedRuntimeEnvironment e,OwnedRuntimeControls c){throw new Exception("Unqualified scalar schedule executed.");}
        public void Step(OwnedRuntimeSystems s,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField e,OwnedRuntimeControls c){throw new Exception("Unqualified scalar schedule executed.");}
    }
    static OwnedRuntimeControls Controls(double throttle=0,Vec attitude=default(Vec))
    {return new OwnedRuntimeControls{Throttle=throttle,Attitude=attitude,EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};}
    static void Need(bool x,string reason){if(!x)throw new Exception(reason);}
    static void Near(double a,double b,double tolerance=1e-12){Need(!double.IsNaN(a)&&!double.IsInfinity(a)&&Math.Abs(a-b)<=tolerance*Math.Max(1,Math.Abs(b)),a+" != "+b);}
    static void Near(Vec a,Vec b,double tolerance=1e-12){Near(a.X,b.X,tolerance);Near(a.Y,b.Y,tolerance);Near(a.Z,b.Z,tolerance);}
    static double Norm(Vec v){return Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);}
    static double Successor(double value){return BitConverter.Int64BitsToDouble(checked(BitConverter.DoubleToInt64Bits(value)+1));}
    static void Reject(Action a){try{a();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Unsupported adaptive operation accepted.");}
    static void ConstantForceWheelAndBoundary()
    {
        var mismatch=new Fixture();mismatch.Adaptive();var untouched=mismatch.World.Stamp;
        Reject(()=>mismatch.Host.AdvanceAdaptive(.1,new OwnedRuntimeInput(),mismatch.Session,Controls(1)));
        Need(mismatch.World.Stamp.Matches(untouched)&&mismatch.Fuel==1,"Mismatching deadline controls changed the physical source.");
        var admission=new Fixture();admission.Adaptive();admission.Systems.SetDeadlineStrategy(admission.World.Stamp,OwnedRuntimeDeadlineStrategy.Retained);untouched=admission.World.Stamp;
        Reject(()=>admission.Host.SwapSchedule(new UnspecifiedScalarSchedule(),new string('d',64),untouched));
        Need(admission.World.Stamp.Matches(untouched)&&admission.Host.CodeGeneration==0,"Unqualified scalar HMR changed its owner.");
        var loaded=new Fixture();loaded.Host.SwapSchedule(new UnspecifiedScalarSchedule(),new string('d',64),loaded.World.Stamp);loaded.Systems.SetDeadlineStrategy(loaded.World.Stamp,OwnedRuntimeDeadlineStrategy.Retained);loaded.Adaptive();untouched=loaded.World.Stamp;
        Need(!loaded.Host.SupportsAdaptiveMechanical,"Unqualified scalar schedule acquired retained deadline support.");Reject(()=>loaded.Session.AdvanceLocal(loaded.Host,.02,Controls()));Need(loaded.World.Stamp.Matches(untouched),"Unqualified scalar deadline search published state.");
        var f=new Fixture();f.Adaptive(new OwnedRuntimeMechanicalPolicy(true,initialInterval:.1));var before=f.World.Stamp;f.Session.AdvanceLocal(f.Host,.1,Controls(1));
        Near(f.Motion.Velocity.Z,.1);Near(f.Motion.RootPosition.Z,.005);Need(f.World.Stamp.StateRevision==before.StateRevision+1,"Trial commits leaked into live revisions.");
        var snapshot=f.World.CaptureSnapshot();Need(snapshot.InputHistory.Length==1&&snapshot.Mechanical.trialExecutions==3&&snapshot.Mechanical.accepted==1,"Accepted interval history is not one atomic publication.");
        var w=new Fixture(wheel:true);w.Adaptive(new OwnedRuntimeMechanicalPolicy(true,initialInterval:.1));w.Session.AdvanceLocal(w.Host,.1,Controls(attitude:new Vec(0,1,0)));
        using(var read=w.World.AcquireRead()){Near(read.GetProviderState(0),-.1);Near(read.GetRegion(0).Motion.AngularMomentum,new Vec(0,.1,0));Near(read.GetRegion(0).Motion.Orientation.Rotate(new Vec(1,0,0)),new Vec(Math.Cos(.005),0,-Math.Sin(.005)));}
        var deplete=new Fixture(fuel:.00001,isp:1);deplete.Adaptive(new OwnedRuntimeMechanicalPolicy(true,initialInterval:.1));var stamp=deplete.World.Stamp;double initial=deplete.Fuel;
        var boundary=deplete.Systems.FindActuatorBoundary(stamp,Controls(1),.1);Need(boundary.Supported&&boundary.Reached&&boundary.Kind=="resource-empty","Original resource limiter was not reached.");Near(boundary.DeltaSeconds,initial*9.80665/11);
        Need(deplete.World.Stamp.Matches(stamp)&&deplete.Fuel==initial,"Boundary forecast published scalar state.");
        deplete.Session.AdvanceLocal(deplete.Host,.1,Controls(1));Need(deplete.World.Stamp.Time<=boundary.DeltaSeconds,"Adaptive interval crossed its original limiter.");
        var shortTail=new Fixture();shortTail.Adaptive();shortTail.Session.AdvanceLocal(shortTail.Host,.00005,Controls());Near(shortTail.World.Stamp.Time,.00005);
    }
    static void MultiBodyResourceResetAndContinuation()
    {
        var f=new Fixture(pair:true,isp:1000);f.Adaptive();var controls=Controls(.5);
        for(int step=0;step<24;step++)
        {
            f.Session.AdvanceLocal(f.Host,.02,controls);var snapshot=f.World.CaptureSnapshot();var independent=RigidAssemblyProperties.Combine(snapshot.BodyMembers);
            using(var read=f.World.AcquireRead())
            {var actual=read.GetRegion(0).Properties;Near(actual.Mass,independent.Mass);Near(actual.CenterOfMass,independent.CenterOfMass);Near(actual.Inertia.XX,independent.Inertia.XX);Near(actual.Inertia.XY,independent.Inertia.XY);Near(actual.Inertia.XZ,independent.Inertia.XZ);Near(actual.Inertia.YY,independent.Inertia.YY);Near(actual.Inertia.YZ,independent.Inertia.YZ);Near(actual.Inertia.ZZ,independent.Inertia.ZZ);Near(read.GetResourceQuantity(0),1-.5*f.World.Stamp.Time/(1000*9.80665));Near(read.GetResourceQuantity(1),.5-f.World.Stamp.Time/(1000*9.80665));}
        }
        var checkpoint=f.Host.CaptureCheckpoint(f.Imported,controls,f.Session.EvaluateRegion(0),new OwnedRuntimeStepPlan(.02));
        byte[] binary=OwnedRuntimeCheckpointBinary.Encode(checkpoint);Need(BitConverter.ToInt32(binary,4)==OwnedRuntimeCheckpointBinary.Version,"Adaptive codec is not current.");var decoded=OwnedRuntimeCheckpointBinary.Decode(binary);Need(decoded.state.mechanical!=null,"Mechanical continuation was omitted.");
        OwnedRuntimeRestoredState restored;var host=f.Host.RestoreCheckpoint(decoded,Native,Parts,out restored);var session=OwnedFlightSession.Restore(restored.World,f.Session.Capture(),Native,Parts);
        double next=f.World.SuggestedMechanicalInterval;Near(restored.World.SuggestedMechanicalInterval,next);
        f.Session.AdvanceLocal(f.Host,.02,controls);session.AdvanceLocal(host,.02,controls);
        using(var a=f.World.AcquireRead())using(var b=restored.World.AcquireRead())
        {Need(a.Stamp.Matches(b.Stamp),"Checkpoint selected a different accepted interval.");Near(a.GetRegion(0).Motion.RootPosition,b.GetRegion(0).Motion.RootPosition);Near(a.GetRegion(0).Motion.Velocity,b.GetRegion(0).Motion.Velocity);Near(a.GetRegion(0).Properties.CenterOfMass,b.GetRegion(0).Properties.CenterOfMass);Near(a.GetResourceQuantity(0),b.GetResourceQuantity(0));}
    }
    static void NonzeroClockRoundoffBoundaries()
    {
        const double start=31.052604680881637;double end=Successor(start),one=end-start;
        var single=new Fixture(initialTime:start);single.Adaptive();var before=single.World.Stamp;
        Need(!(start+one*.5>start&&start+one*.5<end),"Regression clock unexpectedly admits an interior half.");
        single.Session.AdvanceLocal(single.Host,one,Controls(1));
        Need(single.World.Stamp.Time==end&&single.World.Stamp.StateRevision==before.StateRevision+1,"Adjacent clock endpoint was not published once.");
        Near(single.Motion.Velocity.Z/one,1);Near(single.Motion.RootPosition.Z/(.5*one*one),1);
        Need(single.World.Mechanical.roundoffCoalescedIntervals==1&&single.World.Mechanical.trialExecutions==1&&single.Host.AdaptiveObservation.RoundoffCoalescedIntervals==1,"Unsplittable interval pretended to execute refined halves.");
        var exact=single.Host.CaptureCheckpoint(single.Imported,Controls(1),single.Session.EvaluateRegion(0),new OwnedRuntimeStepPlan(.02));
        Need(OwnedRuntimeCheckpointBinary.Decode(OwnedRuntimeCheckpointBinary.Encode(exact)).state.mechanical.roundoffCoalescedIntervals==1,"Roundoff continuation was omitted from its binary checkpoint.");

        var halves=new Fixture(initialTime:start);halves.Adaptive();double two=Successor(end)-start;
        halves.Session.AdvanceLocal(halves.Host,two,Controls(1));
        Need(halves.World.Stamp.Time==Successor(end)&&halves.World.Mechanical.trialExecutions==3&&halves.World.Mechanical.roundoffCoalescedIntervals==0,"Two-ULP interval failed to use two genuinely advancing halves.");
        Near(halves.Motion.Velocity.Z/two,1);

        const double initialFuel=1e-20;var limiter=new Fixture(fuel:initialFuel,isp:1,initialTime:start);limiter.Adaptive();
        var boundary=limiter.Systems.FindActuatorBoundary(limiter.World.Stamp,Controls(1),.02);
        Need(boundary.Reached&&start+boundary.DeltaSeconds==start,"Regression resource limiter is not below the clock ULP.");
        limiter.Session.AdvanceLocal(limiter.Host,.02,Controls(1));
        Need(limiter.World.Stamp.Time==end&&limiter.Fuel==0&&limiter.Host.AdaptiveObservation.ActuatorBoundaryClockCeilings==1,"Original limiter was not coalesced at the earliest clock endpoint.");
        Near(limiter.Motion.Velocity.Z/(initialFuel*9.80665/10),1);
        Need(limiter.World.Mechanical.roundoffCoalescedIntervals==1&&limiter.World.SuggestedMechanicalInterval==.02,"Clock coalescence changed the held input or taught the controller a false tiny interval.");

        var excessive=new Fixture(initialTime:start);var motion=excessive.Motion;motion.Velocity=new Vec(0,0,1e15);
        excessive.World.SetRegionMotions(excessive.World.Stamp,new[]{motion},start,excessive.World.Stamp.FrameId);excessive.Adaptive();before=excessive.World.Stamp;
        bool rejected=false;try{excessive.Session.AdvanceLocal(excessive.Host,one,Controls());}catch(OwnedRuntimeMechanicalAccuracyException){rejected=true;}
        Need(rejected&&excessive.World.Stamp.Matches(before)&&excessive.World.Mechanical.roundoffCoalescedIntervals==0&&excessive.Host.AdaptiveObservation.Rejected==1,"Adjacent-clock change bypassed the existing quality budgets or published failed state.");
        Near(excessive.Motion.Velocity,motion.Velocity);Near(excessive.Motion.RootPosition,motion.RootPosition);
    }
    static double Orbit(double tolerance)
    {
        var f=new Fixture(orbit:true);f.Adaptive(new OwnedRuntimeMechanicalPolicy(true,positionTolerance:tolerance,velocityTolerance:tolerance));
        while(f.World.Stamp.Time<2)f.Session.AdvanceLocal(f.Host,Math.Min(.1,2-f.World.Stamp.Time),Controls());
        double speed=Math.Sqrt(4e6/1200),angle=2*speed/1200;Vec exact=new Vec(1200*Math.Cos(angle),1200*Math.Sin(angle),0);
        return Norm(f.Session.CenterPosition(0)+exact*-1);
    }
    static void RejectionAndOriginalReference()
    {
        var f=new Fixture(orbit:true);f.Adaptive(new OwnedRuntimeMechanicalPolicy(true,positionTolerance:1e-30,velocityTolerance:1e-30,angleTolerance:1e-30,scaledStateTolerance:1e-30));
        var stamp=f.World.Stamp;var motion=f.Motion;Reject(()=>f.Session.AdvanceLocal(f.Host,.02,Controls()));Need(f.World.Stamp.Matches(stamp),"Rejected trials published live state.");Near(f.Motion.RootPosition,motion.RootPosition);Need(f.World.CaptureSnapshot().InputHistory.Length==0&&f.World.Mechanical.accepted==0,"Rejected trials changed history/controller bank.");
        Need(f.Host.AdaptiveObservation.Rejected>0,"Failed accuracy search was not observed.");
        var original=new Fixture();original.Host.SwapSchedule(new OrdinarySchedule(),new string('d',64),original.World.Stamp);
        Need(!original.Host.SupportsAdaptiveMechanical,"Unmarked schedule acquired trial ownership.");original.Host.AdvanceAdaptive(.02,new OwnedRuntimeInput(),original.Session,Controls());
        original.Adaptive();stamp=original.World.Stamp;Reject(()=>original.Session.AdvanceLocal(original.Host,.02,Controls()));Need(original.World.Stamp.Matches(stamp),"Unsupported schedule changed physical state.");
        double loose=Orbit(1e-4),tight=Orbit(1e-5);Need(tight<loose&&tight<.02,"Tighter frozen estimator did not converge toward independent circular orbit: "+loose+" / "+tight);
    }
    public static int Main(string[] args)
    {
        ConstantForceWheelAndBoundary();MultiBodyResourceResetAndContinuation();RejectionAndOriginalReference();NonzeroClockRoundoffBoundaries();
        foreach(string path in args)
        {byte[] bytes=File.ReadAllBytes(path);int version=BitConverter.ToInt32(bytes,4);Need(version>=1&&version<=4,"Legacy witness must be retained v1-v4.");var c=bytes[1]==70?OwnedRuntimeCheckpointBinary.DecodeFlight(bytes).host:OwnedRuntimeCheckpointBinary.Decode(bytes);Need(c.state.deadlineStrategy==0,"Legacy decode invented retained deadline scheduling.");Need(version>=3?c.state.mechanical!=null&&(version!=3||c.state.mechanical.method==0&&c.state.executionStrategy==0):c.state.mechanical==null,"Legacy decode invented a numerical method or execution strategy.");Console.WriteLine("PASS retained legacy binary version "+version+" "+path);}
        Console.WriteLine("PASS adaptive coupled mechanics: independent force/wheel, original limiter, atomic rejection, retained multi-body mass reset, checkpoint continuation, orbital convergence, nonzero-clock ULP boundaries and reference admission");return 0;
    }
}
