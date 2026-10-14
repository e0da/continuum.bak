using System;
using KspContinuum;

static class Program
{
    sealed class Fixture
    {
        public readonly OwnedFlightSession Session;
        public readonly OwnedRuntimeHost Host;
        public readonly OwnedFlightTimeline Timeline;
        public Fixture()
        {
            string native=new string('a',64),parts=new string('b',64);
            var capture=new OwnedFlightCapture {
                runId="timeline-analytic",processId=1,vesselId="single",bodyId="central",
                nativeManifestSha256=native,partSpecsSha256=parts,sourceEpochSeconds=0,
                gravitationalParameter=4e6,radiusMeters=1000,atmosphereDepthMeters=100,
                angularVelocity=new[]{0d,0d,.01},sunDirection=new[]{1d,0d,0d},solarFluxWattsPerSquareMeter=1000,
                bodyRotation=new[]{0d,0d,0d,1d},rootPositionBodyRelative=new[]{1200d,0d,0d},rootRotation=new[]{0d,0d,0d,1d},
                centerOfMassVelocityInertial=new[]{0d,Math.Sqrt(4e6/1200),0d},altitudeMeters=new[]{0d,50d,100d},
                densityKilogramsPerCubicMeter=new[]{1d,.25,0d},temperatureKelvin=new[]{300d,250d,3d},omissions=new string[0] };
            var member=new RigidAssemblyMember(0,10,new Vec(),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity);
            var definition=new OwnedRuntimeDefinition("timeline",0,
                new[]{new OwnedRuntimePartDefinition(1,"engine",10,new Vec(),Rotation.Identity,100)},
                new[]{new OwnedRuntimeBodyDefinition(10,member)},
                new[]{new OwnedRuntimeResourceDefinition(2,1,"fuel",1,1,1)},new long[0]);
            var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),
                new OwnedRuntimeMotion{Orientation=Rotation.Identity},0,"cold-import");
            Session=OwnedFlightSession.SeedCaptured(world,OwnedFlightWorld.Admit(capture,native,parts,new string('c',64)),new Vec(1200,0,0));
            var systems=new OwnedRuntimeSystems(world,new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,
                new Vec(),new Vec(0,0,1),10,0,1000)},new OwnedRuntimeSurfaceSpec[0],new OwnedRuntimeThermalLink[0]);
            Host=new OwnedRuntimeHost(world,systems);Timeline=new OwnedFlightTimeline(Host,Session,0);
        }
        public double Fuel {get {using(var read=Host.World.AcquireRead())return read.GetResourceQuantity(0);}}
        public OwnedRuntimeMotion Motion {get {using(var read=Host.World.AcquireRead())return read.GetRegion(0).Motion;}}
    }

    sealed class FailingSchedule : IOwnedRuntimeFieldSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "rollback-falsifier";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {systems.Step(step,environment,controls);throw new InvalidOperationException("intentional-after-computation");}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField environment,OwnedRuntimeControls controls)
        {systems.Step(step,environment,controls);throw new InvalidOperationException("intentional-after-computation");}
    }
    static OwnedRuntimeControls Controls(double throttle=0)
    {return new OwnedRuntimeControls{Throttle=throttle,EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};}
    static void Configure(Fixture f,double wall,OwnedRuntimeControls controls,bool paused=false,double warp=1,
        bool coast=false,bool frozen=false,double deadline=double.PositiveInfinity)
    {f.Timeline.Configure(f.Host.World.Stamp,f.Timeline.InputRevision,wall,controls,warp,paused,coast,frozen,deadline);}
    static void Need(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Near(double actual,double expected,double tolerance=1e-12)
    {if(double.IsNaN(actual)||double.IsInfinity(actual)||Math.Abs(actual-expected)>tolerance*Math.Max(1,Math.Abs(expected)))throw new Exception(actual+" != "+expected);}
    static void Bits(double actual,double expected)
    {Need(BitConverter.DoubleToInt64Bits(actual)==BitConverter.DoubleToInt64Bits(expected),"Committed partition result changed bits.");}
    static void Reject(Action action)
    {try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Invalid timeline operation was accepted.");}

    static void BudgetPauseAndWarp()
    {
        var f=new Fixture();Configure(f,0,Controls(.5),warp:4,deadline:.2);
        var stamp=f.Host.World.Stamp;var p=f.Timeline.Pump(.05,0);
        Need(p.BudgetExhausted&&p.LocalCommits==0&&f.Host.World.Stamp.Matches(stamp),"Zero commit budget executed work.");Near(p.DebtSeconds,.2);
        p=f.Timeline.Pump(.05,64,0);Need(p.BudgetExhausted&&p.LocalCommits==0,"Zero wall budget executed work.");
        Configure(f,.05,Controls(.5),true,4,deadline:.2);
        p=f.Timeline.Pump(100,64);Near(p.DebtSeconds,.2);Need(p.LocalCommits==0,"Pause consumed debt.");
        Configure(f,100,Controls(.5),false,4,deadline:.2);p=f.Timeline.Pump(100,64);
        Need(p.Fault==null&&p.DeadlineReached==false,"Debt drain faulted.");Near(p.CommittedRuntimeTime,.2);
        Near(f.Fuel,1-1/(1000*9.80665));Need(f.Motion.Velocity.Z>0,"Real engine integration did not execute.");
        Need(f.Host.Systems.Counters.Steps>=10,"Warp grew local dt instead of executing bounded quanta.");
    }

    static Fixture Partition(int frequency)
    {
        var f=new Fixture();Configure(f,0,Controls(.5),deadline:.2);
        for(int i=1;i<=frequency;i++)
        {var p=f.Timeline.Pump(.2*i/frequency,64);Need(p.Fault==null,"Partition run faulted.");}
        var final=f.Timeline.Pump(.2,64);Need(final.Fault==null&&final.DebtSeconds==0,"Semantic endpoint did not drain.");return f;
    }
    static void PresentationPartitionsAndBoundary()
    {
        var reference=Partition(30);
        foreach(int frequency in new[]{60,144})
        {
            var f=Partition(frequency);Bits(f.Fuel,reference.Fuel);Bits(f.Motion.RootPosition.X,reference.Motion.RootPosition.X);
            Bits(f.Motion.RootPosition.Y,reference.Motion.RootPosition.Y);Bits(f.Motion.RootPosition.Z,reference.Motion.RootPosition.Z);
            Bits(f.Motion.Velocity.X,reference.Motion.Velocity.X);Bits(f.Motion.Velocity.Y,reference.Motion.Velocity.Y);
            Bits(f.Motion.Velocity.Z,reference.Motion.Velocity.Z);Need(f.Host.World.Stamp.Epoch==reference.Host.World.Stamp.Epoch,"Local quantum sequence depends on presentation.");
        }
        var boundary=new Fixture();Configure(boundary,0,Controls(1));
        var first=boundary.Timeline.Pump(.005,64);Need(first.LocalCommits==0&&first.Status=="awaiting-local-quantum","Render tail became a fractional physical step.");
        Configure(boundary,.005,Controls());var second=boundary.Timeline.Pump(.015,64);
        Need(second.LocalCommits==1&&second.Status=="awaiting-local-quantum","Semantic sub-quantum control boundary was not honored.");
        Near(second.CommittedRuntimeTime,.005);Near(boundary.Fuel,1-.05/(1000*9.80665));Near(second.DebtSeconds,.01);
    }

    static void QueueAndInvalidCalls()
    {
        var f=new Fixture();Configure(f,0,Controls());
        for(int i=1;i<=OwnedFlightTimeline.MaximumInputSegments;i++)Configure(f,i*.001,Controls((i%2)*.5));
        var old=f.Timeline.ReadProgress();Need(old.PendingInputSegments==128,"Queue fixture did not reach its real bound.");
        Reject(()=>Configure(f,.129,Controls()));var retained=f.Timeline.ReadProgress();
        Bits(retained.RequestedRuntimeTime,old.RequestedRuntimeTime);Bits(retained.WallSeconds,old.WallSeconds);
        Need(retained.InputRevision==old.InputRevision&&retained.PendingInputSegments==old.PendingInputSegments,"Overflow discarded prior input demand.");
        var drained=f.Timeline.Pump(old.WallSeconds,512);Need(drained.Fault==null&&drained.DebtSeconds==0,"Retained queue cannot drain after rejected new demand.");
        var stamp=f.Host.World.Stamp;long revision=f.Timeline.InputRevision;double horizon=f.Timeline.RequestedRuntimeTime;
        Reject(()=>f.Timeline.Pump(double.NaN,1));Reject(()=>f.Timeline.Pump(old.WallSeconds-1,1));
        Reject(()=>f.Timeline.Pump(old.WallSeconds,-1));Reject(()=>f.Timeline.Pump(old.WallSeconds,1,double.NaN));
        Reject(()=>f.Timeline.Pump(old.WallSeconds,1,-1));Reject(()=>Configure(f,old.WallSeconds,Controls(),warp:double.PositiveInfinity));
        Reject(()=>f.Timeline.Configure(stamp,revision-1,old.WallSeconds,Controls(),1,false));
        Need(f.Host.World.Stamp.Matches(stamp)&&f.Timeline.InputRevision==revision,"Invalid caller input changed state.");Bits(f.Timeline.RequestedRuntimeTime,horizon);
    }

    static void FaultRollbackRecovery()
    {
        var f=new Fixture();f.Host.SwapSchedule(new FailingSchedule(),new string('d',64),f.Host.World.Stamp);
        var timeline=new OwnedFlightTimeline(f.Host,f.Session,0);timeline.Configure(f.Host.World.Stamp,0,0,Controls(.5),1,false,nextDeadlineUniversalTime:.1);
        var stamp=f.Host.World.Stamp;double fuel=f.Fuel;var failed=timeline.Pump(.1,64);
        Need(failed.Fault!=null&&failed.Paused&&failed.LocalCommits==0,"Transaction failure was not retained and paused.");
        Need(f.Host.World.Stamp.Matches(stamp),"Failed host transaction published a partial bank.");Bits(f.Fuel,fuel);Near(failed.DebtSeconds,.1);
        f.Host.SwapSchedule(new OwnedRuntimeBuiltinSchedule(),new string('e',64),stamp);
        timeline.CompleteEvent(stamp,100,"same-time schedule recovery",.1);
        var recovered=timeline.ReadProgress();Near(recovered.DebtSeconds,.1);Need(recovered.PendingInputSegments==1&&recovered.Fault==null,"Recovery erased historical demand.");
        timeline.Configure(f.Host.World.Stamp,timeline.InputRevision,100,Controls(.5),1,false,nextDeadlineUniversalTime:.1);
        var done=timeline.Pump(100,64);Need(done.Fault==null&&done.DebtSeconds==0,"Recovered host did not consume retained demand.");
        Near(f.Fuel,1-.5/(1000*9.80665));
    }

    static void EventDebtAndCoast()
    {
        var f=new Fixture();Configure(f,0,Controls(1),deadline:.03);Configure(f,.01,Controls(),deadline:.03);
        var barrier=f.Timeline.Pump(.1,64);Need(barrier.Fault==null&&barrier.DeadlineReached,"Actual event barrier was not reached.");
        Near(barrier.CommittedRuntimeTime,.03);Near(barrier.DebtSeconds,.07);Near(f.Fuel,1-.1/(1000*9.80665));
        Configure(f,.1,Controls(1),true,deadline:.03);var previous=f.Host.World.Stamp;var motion=f.Motion;
        motion.Velocity=motion.Velocity+new Vec(0,0,1);f.Host.World.SetRegionMotions(previous,new[]{motion},previous.Time,f.Session.FrameId);
        f.Timeline.CompleteEvent(previous,.1,"bound impulse",double.PositiveInfinity);
        Near(f.Timeline.DebtSeconds,.07);Configure(f,.1,Controls(1));var after=f.Timeline.Pump(.1,64);
        Need(after.Fault==null&&after.DebtSeconds==0,"Acknowledged event lost/deadlocked future debt.");
        Near(f.Fuel,1-.1/(1000*9.80665));

        var coast=new Fixture();Configure(coast,0,Controls(),coast:true,frozen:true);
        double quarter=2*Math.PI*Math.Sqrt(1200d*1200*1200/4e6)/4;
        var result=coast.Timeline.Pump(quarter,1);Need(result.Fault==null&&result.CoastCommits==1&&result.LocalCommits==0,"Existing exact passive coast was not used.");
        var center=coast.Session.CenterPosition(0);Near(center.X,0,2e-5);Near(center.Y,1200,2e-8);Near(coast.Fuel,1);
        var active=new Fixture();Configure(active,0,Controls(.5),coast:true,frozen:true,deadline:.02);
        var rejected=active.Timeline.Pump(.02,2);Need(rejected.Fault==null&&rejected.CoastCommits==0&&rejected.LocalCommits>0&&rejected.CoastRejection!=null,"Active control was fabricated into coast eligibility.");
    }

    public static int Main()
    {
        BudgetPauseAndWarp();PresentationPartitionsAndBoundary();QueueAndInvalidCalls();FaultRollbackRecovery();EventDebtAndCoast();
        Console.WriteLine("PASS owned timeline: independent presentation, explicit quanta/warp, retained budget/input debt, bounded overflow, atomic rollback recovery, event acknowledgement, authentic passive coast");return 0;
    }
}
