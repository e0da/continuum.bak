using System;
using System.Linq;
using KspContinuum;

static class Program
{
    static readonly string N=new string('a',64),P=new string('b',64),C=new string('c',64);
    sealed class Fixture
    {
        public OwnedRuntimeImportResult Import;
        public OwnedRuntimeHost Host;
        public OwnedFlightSession Session;
        public OwnedFlightTimeline Timeline;
        public Fixture(bool adaptive=true)
        {
            var member=new RigidAssemblyMember(0,10,new Vec(),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity);
            var definition=new OwnedRuntimeDefinition("causal",0,new[]{new OwnedRuntimePartDefinition(1,"engine",10,new Vec(),Rotation.Identity,100)},new[]{new OwnedRuntimeBodyDefinition(10,member)},new[]{new OwnedRuntimeResourceDefinition(2,1,"fuel",1,1,1)},new long[0]);
            var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity},0,"import");
            var capture=new OwnedFlightCapture {runId="causal",processId=1,vesselId="fixture",bodyId="central",nativeManifestSha256=N,partSpecsSha256=P,sourceEpochSeconds=0,
                gravitationalParameter=4e6,radiusMeters=1000,atmosphereDepthMeters=100,angularVelocity=new[]{0d,0d,.01},sunDirection=new[]{1d,0d,0d},solarFluxWattsPerSquareMeter=1000,
                bodyRotation=new[]{0d,0d,0d,1d},rootPositionBodyRelative=new[]{1200d,0d,0d},rootRotation=new[]{0d,0d,0d,1d},centerOfMassVelocityInertial=new[]{0d,Math.Sqrt(4e6/1200),0d},
                altitudeMeters=new[]{0d,50d,100d},densityKilogramsPerCubicMeter=new[]{1d,.25,0d},temperatureKelvin=new[]{300d,250d,3d},omissions=new string[0]};
            Session=OwnedFlightSession.SeedCaptured(world,OwnedFlightWorld.Admit(capture,N,P,C),new Vec(1200,0,0));
            var systems=new OwnedRuntimeSystems(world,new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(0,0,1),10,0,1000)},new OwnedRuntimeSurfaceSpec[0]);
            if(adaptive)systems.SetMechanicalPolicy(world.Stamp,new OwnedRuntimeMechanicalPolicy(true));
            Import=new OwnedRuntimeImportResult{World=world,Systems=systems,Source=new OwnedRuntimeSourceBinding{runId="causal",vesselId="fixture",processId=1,nativeManifestSha256=N,partSpecsSha256=P,scope="analytic fixture",subtractedNativeCenterVelocity=new double[3],sourceFrameVelocity=new double[3],bodies=new[]{new OwnedRuntimeBodyBinding{sourceBodyId=10,bodyId=10,bodySlot=0}},renderers=new OwnedRuntimeRendererBinding[0],omissions=new string[0]}};
            Host=new OwnedRuntimeHost(world,systems);Timeline=new OwnedFlightTimeline(Host,Session,0);
        }
        public byte[] Save(){return OwnedRuntimeCheckpointBinary.EncodeFlight(OwnedFlightCheckpoint.Capture(Host,Import,Session,Timeline.HeldControls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"",timeline:Timeline));}
        public double Fuel {get{using(var read=Host.World.AcquireRead())return read.GetResourceQuantity(0);}}
    }
    static void Need(bool value,string reason){if(!value)throw new Exception(reason);}
    static void Near(double value,double expected,double tolerance=1e-10){Need(Math.Abs(value-expected)<=tolerance,value+" differs from "+expected);}
    static void Reject(Action action){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Need(rejected,"Invalid execution state was admitted.");}
    static OwnedRuntimeControls Controls(double throttle=0){return new OwnedRuntimeControls{Throttle=throttle,EnginesEnabled=true};}
    static void Run(Fixture f,double wall,int budget=256)
    {var result=f.Timeline.Pump(wall,budget);Need(result.Fault==null,result.Fault??"Run faulted.");}
    static void EventsAndFuel()
    {
        var f=new Fixture();long region;using(var read=f.Host.World.AcquireRead())region=read.GetRegion(0).Id;
        f.Timeline.EnqueueEvent(new OwnedFlightEvent("burn",1,.0625,OwnedFlightEventKind.Controls,controls:Controls(1)));
        f.Timeline.EnqueueEvent(new OwnedFlightEvent("kick",2,.125,OwnedFlightEventKind.Impulse,region,new Vec(1,0,0)));
        f.Timeline.Configure(f.Host.World.Stamp,0,0,Controls(),1,false);
        Run(f,.0625);Near(f.Timeline.CommittedRuntimeTime,.0625,0);Need(f.Timeline.TotalEventsApplied==1&&f.Timeline.HeldControls.Throttle==1,"Timed control was not installed at its exact frontier.");
        Run(f,.125);Near(f.Timeline.CommittedRuntimeTime,.125,0);Need(f.Timeline.TotalEventsApplied==2&&f.Timeline.PendingEvents==0,"Impulse event did not close its exact frontier.");
        Near(f.Fuel,1-10*.0625/(1000*9.80665));Run(f,.125);Need(f.Timeline.TotalEventsApplied==2,"Event applied twice.");
    }
    static void CheckpointDemandAndFutureEvents()
    {
        var f=new Fixture();f.Timeline.EnqueueEvent(new OwnedFlightEvent("burn",1,.0625,OwnedFlightEventKind.Controls,controls:Controls(.5)));
        f.Timeline.Configure(f.Host.World.Stamp,0,0,Controls(),1,false);Run(f,.25,1);
        f.Timeline.Configure(f.Host.World.Stamp,f.Timeline.InputRevision,.25,Controls(),1,true);
        var bytes=f.Save();var packet=OwnedRuntimeCheckpointBinary.DecodeFlight(bytes);
        Need(packet.timeline!=null&&packet.timeline.events.pending.Length==1&&packet.timeline.requestedRuntimeTime>packet.timeline.time,"Checkpoint did not exercise debt and future work.");
        var restored=OwnedFlightCheckpoint.Restore(f.Host,f.Import,packet,N,P,C,"");
        var timeline=OwnedFlightTimeline.RestoreState(restored.Host,restored.Session,restored.TimelineState,1000);
        Near(timeline.DebtSeconds,f.Timeline.DebtSeconds,0);Near(timeline.ReadProgress().WallSeconds,1000,0);
        var copy=OwnedRuntimeCheckpointBinary.EncodeFlight(OwnedFlightCheckpoint.Capture(restored.Host,restored.Imported,restored.Session,timeline.HeldControls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"",timeline:timeline));
        Need(bytes.SequenceEqual(copy),"Reanchoring wall time changed complete execution checkpoint.");
        for(int i=0;i<128&&(f.Timeline.DebtSeconds>0||timeline.DebtSeconds>0);i++)
        {if(f.Timeline.DebtSeconds>0)Need(f.Timeline.Step(Math.Min(.02,f.Timeline.DebtSeconds)).Fault==null,"Original drain faulted.");if(timeline.DebtSeconds>0)Need(timeline.Step(Math.Min(.02,timeline.DebtSeconds)).Fault==null,"Restored drain faulted.");}
        Need(f.Timeline.DebtSeconds==0&&timeline.DebtSeconds==0&&timeline.TotalEventsApplied==1,"Restored demand was lost or blocked.");
        copy=OwnedRuntimeCheckpointBinary.EncodeFlight(OwnedFlightCheckpoint.Capture(restored.Host,restored.Imported,restored.Session,timeline.HeldControls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"",timeline:timeline));
        Need(f.Save().SequenceEqual(copy),"Future event/controller/input continuation diverged.");
        var bad=OwnedRuntimeCheckpointBinary.DecodeFlight(bytes).timeline;bad.requestedRuntimeTime+=.125;var stamp=restored.Host.World.Stamp;
        Reject(()=>OwnedFlightTimeline.RestoreState(restored.Host,restored.Session,bad,2000));Need(stamp.Matches(restored.Host.World.Stamp),"Invalid restore published state.");
    }
    static void TinySemanticBoundary()
    {
        var f=new Fixture();f.Timeline.EnqueueEvent(new OwnedFlightEvent("tiny",1,.00001,OwnedFlightEventKind.Controls,controls:Controls(1)));
        f.Timeline.Configure(f.Host.World.Stamp,0,0,Controls(),4,false);Run(f,.001,2);
        Near(f.Timeline.CommittedRuntimeTime,.00001,0);Need(f.Timeline.TotalEventsApplied==1&&f.Timeline.HeldControls.Throttle==1,"Adaptive minimum erased a smaller semantic boundary.");
        Reject(()=>f.Timeline.EnqueueEvent(new OwnedFlightEvent("past",2,0,OwnedFlightEventKind.Controls)));
    }
    static void WorkerHeldControls()
    {
        using(var worker=new OwnedFlightWorker(()=>{var f=new Fixture(false);return new OwnedFlightWorkerContext(f.Host,f.Session,f);},new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0}))
        {
            Need(worker.WaitUntilReady(3000),"Worker fixture failed.");
            var queued=worker.Invoke(c=>c.Timeline.EnqueueEvent(new OwnedFlightEvent("live",1,0,OwnedFlightEventKind.Controls,controls:Controls(.75))));Need(queued.Wait(3000)&&queued.Error==null,"Worker event was rejected.");
            var step=worker.Step(.015625);Need(step.Wait(3000)&&step.Error==null&&step.Result.TotalEventsApplied==1,"Worker did not apply controls.");
            var warp=worker.SetWarp(2);Need(warp.Wait(3000)&&warp.Error==null,"Warp failed.");var pause=worker.SetPause(true);Need(pause.Wait(3000)&&pause.Error==null,"Pause failed.");
            var held=worker.Invoke(c=>c.Timeline.HeldControls.Throttle);Need(held.Wait(3000)&&held.Error==null&&held.Result==.75,"Worker cached input overwrote timed controls.");
        }
    }
    static void ManualCoastQueue()
    {
        var f=new Fixture();f.Timeline.Configure(f.Host.World.Stamp,0,0,Controls(),1,true,true,true);
        var coast=f.Timeline.CoastTo(5);Need(coast.CoastCommits==1,"Manual coast did not publish.");
        f.Timeline.EnqueueEvent(new OwnedFlightEvent("after-coast",1,5.125,OwnedFlightEventKind.Controls,controls:Controls(.2)));
        Need(f.Timeline.CaptureState().events.pending.Length==1,"Manual coast left event identity stale.");
    }
    static void FutureInputBoundary()
    {
        var f=new Fixture(false);f.Timeline.Configure(f.Host.World.Stamp,0,0,Controls(1),1,false);
        Run(f,.05,0);f.Timeline.Configure(f.Host.World.Stamp,f.Timeline.InputRevision,.1,Controls(),1,false);
        Run(f,.2,0);f.Timeline.EnqueueEvent(new OwnedFlightEvent("half",1,.05,OwnedFlightEventKind.Controls,controls:Controls(.5)));
        Run(f,.2);Near(f.Fuel,1-.75/(1000*9.80665));
        Need(f.Timeline.HeldControls.Throttle==0&&f.Timeline.TotalEventsApplied==1,"Timed controls overwrote a future authored input boundary.");
    }
    static void Main(){EventsAndFuel();CheckpointDemandAndFutureEvents();TinySemanticBoundary();WorkerHeldControls();ManualCoastQueue();FutureInputBoundary();Console.WriteLine("PASS causal timeline: exact events/fuel, future work and controller checkpoint continuation, semantic tails and worker held controls");}
}
