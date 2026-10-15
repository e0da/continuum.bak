using System;
using System.Diagnostics;
using System.Threading;
using KspContinuum;

static class Program
{
    static readonly string Native=new string('a',64),Parts=new string('b',64);
    static OwnedRuntimeControls Controls(double throttle=0)
    {return new OwnedRuntimeControls {Throttle=throttle,EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};}
    static OwnedFlightWorkerContext Create(bool failing=false)
    {
        var data=new OwnedFlightCapture {runId="worker-analytic",processId=1,vesselId="single",bodyId="central",nativeManifestSha256=Native,partSpecsSha256=Parts,
            sourceEpochSeconds=0,gravitationalParameter=4e6,radiusMeters=1000,atmosphereDepthMeters=100,
            angularVelocity=new[]{0d,0d,.01},sunDirection=new[]{1d,0d,0d},solarFluxWattsPerSquareMeter=1000,
            bodyRotation=new[]{0d,0d,0d,1d},rootPositionBodyRelative=new[]{1200d,0d,0d},rootRotation=new[]{0d,0d,0d,1d},
            centerOfMassVelocityInertial=new[]{0d,Math.Sqrt(4e6/1200),0d},altitudeMeters=new[]{0d,50d,100d},
            densityKilogramsPerCubicMeter=new[]{1d,.25,0d},temperatureKelvin=new[]{300d,250d,3d},omissions=new string[0]};
        var member=new RigidAssemblyMember(0,10,new Vec(),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity);
        var definition=new OwnedRuntimeDefinition("worker",0,new[]{new OwnedRuntimePartDefinition(1,"engine",10,new Vec(),Rotation.Identity,100)},
            new[]{new OwnedRuntimeBodyDefinition(10,member)},new[]{new OwnedRuntimeResourceDefinition(2,1,"fuel",1,1,1)},new long[0]);
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity},0,"cold-import");
        var session=OwnedFlightSession.SeedCaptured(world,OwnedFlightWorld.Admit(data,Native,Parts,new string('c',64)),new Vec(1200,0,0));
        var systems=new OwnedRuntimeSystems(world,new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(0,0,1),10,0,1000)},new OwnedRuntimeSurfaceSpec[0],new OwnedRuntimeThermalLink[0]);
        var host=new OwnedRuntimeHost(world,systems);if(failing)host.SwapSchedule(new FailingSchedule(),new string('d',64),world.Stamp);
        return new OwnedFlightWorkerContext(host,session);
    }
    sealed class FailingSchedule : IOwnedRuntimeFieldSchedule,IOwnedRuntimeWorkerSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "worker-rollback-falsifier";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {systems.Step(step,environment,controls);throw new InvalidOperationException("after real systems computation");}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {systems.Step(step,field,controls);throw new InvalidOperationException("after real systems computation");}
    }
    static void Need(bool value,string message){if(!value)throw new Exception(message);}
    static void Near(double a,double b,double tolerance=1e-12)
    {Need(!double.IsNaN(a)&&!double.IsInfinity(a)&&Math.Abs(a-b)<=tolerance*Math.Max(1,Math.Abs(b)),a+" != "+b);}
    static T Done<T>(OwnedFlightWorkerAcknowledgement<T> ack)
    {Need(ack.Wait(3000),"Worker acknowledgement timed out.");return ack.Result;}
    static void Reject(Action action)
    {try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Invalid worker operation accepted.");}
    static OwnedFlightPresentationLease View(OwnedFlightWorker worker)
    {
        var begin=Stopwatch.StartNew();while(begin.ElapsedMilliseconds<3000)
        {var view=worker.AcquirePresentation();if(view!=null)return view;Thread.Yield();}throw new Exception("No coherent publication available.");
    }

    static void IndependentProgressAndBankLease()
    {
        int constructionThread=0;OwnedRuntimeHost bound=null;
        using(var worker=new OwnedFlightWorker(()=>{constructionThread=Thread.CurrentThread.ManagedThreadId;var c=Create();bound=c.Host;return c;}))
        {
            Need(worker.WaitUntilReady(3000),"Worker did not initialize.");Need(constructionThread!=Thread.CurrentThread.ManagedThreadId,"Core initialization ran on presentation thread.");
            Done(worker.Configure(Controls(.5),1,false,nextDeadlineUniversalTime:.12));
            var limit=Stopwatch.StartNew();while(worker.ReadStatus().Progress.CommittedRuntimeTime<.08&&limit.ElapsedMilliseconds<3000)Thread.Sleep(1);
            Need(worker.ReadStatus().Progress.CommittedRuntimeTime>=.08,"Simulation depended on renderer pumping.");
            using(var view=View(worker))
            {
                Need(view.Progress.Stamp.Matches(view.Read.Stamp),"Frame progress and physical bank differ.");
                Near(view.UniversalTime,view.Read.Stamp.Time);var stamp=view.Read.Stamp;double fuel=view.Read.GetResourceQuantity(0);
                Reject(()=>worker.AcquirePresentation());Thread.Sleep(30);
                Need(view.Read.Stamp.Matches(stamp),"Leased bank changed under its renderer.");Near(view.Read.GetResourceQuantity(0),fuel);
                Near(view.Descriptor.Sample(view.Read,0).Epoch,stamp.Time);
            }
            Done(worker.SetPause(true));
            var before=bound.World.Stamp;
            Reject(()=>bound.Advance(.02,new OwnedRuntimeInput(),new OwnedRuntimeEnvironment("foreign",0,before.Time,0,3,new Vec(),new Vec()),Controls()));
            Need(bound.World.Stamp.Matches(before),"Foreign writer changed physical state.");
        }
    }

    static void ActualRetainedReaderPressure()
    {
        using(var worker=new OwnedFlightWorker(()=>Create(),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0,MaximumCommitsPerPump=1,MaximumLocalStepSeconds=.015625}))
        {
            Need(worker.WaitUntilReady(3000),"Pressure fixture did not initialize.");
            using(var first=Done(worker.Invoke(c=>c.Host.World.AcquireRead())))
            {
                Done(worker.Configure(Controls(),1,false));Done(worker.RequestDemand(.015625));
                using(var second=Done(worker.Invoke(c=>c.Host.World.AcquireRead())))
                {
                    Done(worker.RequestDemand(.03125));var blocked=Done(worker.RequestDemand(.046875));
                    Need(blocked.Status=="blocked-readers"&&blocked.Fault==null&&!blocked.Paused,"Expected actual read pressure; status="+blocked.Status+", fault="+blocked.Fault);Near(blocked.DebtSeconds,.015625);
                    first.Dispose();var timer=Stopwatch.StartNew();while(worker.ReadStatus().Progress.CommittedRuntimeTime<.046875&&timer.ElapsedMilliseconds<3000)Thread.Sleep(1);
                    Near(worker.ReadStatus().Progress.CommittedRuntimeTime,.046875);Need(worker.ReadStatus().ReadPressurePumps>0&&worker.ReadStatus().Fault==null,"Released reader did not unblock retained demand.");
                }
            }
        }
    }

    static void OrderedCancellationAndPressure()
    {
        using(var worker=new OwnedFlightWorker(()=>Create(),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0}))
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
        {
            Need(worker.WaitUntilReady(3000),"Deterministic worker did not initialize.");int calls=0;
            var blocker=worker.Invoke(c=>{entered.Set();Need(release.Wait(3000),"Queue fixture release timed out.");return 1;});
            Need(entered.Wait(3000)&&!blocker.TryCancel(),"Started command was incorrectly cancelled.");
            var rows=new OwnedFlightWorkerAcknowledgement<int>[OwnedFlightWorker.MaximumQueuedCommands];
            for(int i=0;i<rows.Length;i++)rows[i]=worker.Invoke(c=>++calls);
            long accepted=worker.ReadStatus().AcceptedCommands;Reject(()=>worker.Invoke(c=>++calls));
            Need(worker.ReadStatus().AcceptedCommands==accepted&&worker.ReadStatus().QueuedCommands==128,"Overflow changed admitted queue.");
            Need(rows[17].TryCancel()&&rows[17].Error is OperationCanceledException,"Queued cancellation was not acknowledged.");
            release.Set();Done(blocker);Done(rows[rows.Length-1]);Done(worker.Flush());
            Need(calls==127&&worker.ReadStatus().CancelledCommands==1,"Cancelled callback ran or ordering/drain changed.");
            Done(worker.Configure(Controls(.5),1,false,nextDeadlineUniversalTime:.1,wallSeconds:0));Done(worker.RequestDemand(.1));
            Done(worker.SetPause(true));
            using(var view=View(worker)){Near(view.Read.Stamp.Time,.1);Near(view.Read.GetResourceQuantity(0),1-.5/(1000*9.80665));}
            Need(worker.Stop(3000),"Worker did not join.");Reject(()=>worker.SetPause(false));
        }
    }

    static void FailedTransactionAndColdFrame()
    {
        using(var worker=new OwnedFlightWorker(()=>Create(true),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0}))
        {
            Need(worker.WaitUntilReady(3000),"Failing fixture did not initialize.");
            var original=Done(worker.Invoke(c=>c.Host.World.Stamp));Done(worker.Configure(Controls(.5),1,false,nextDeadlineUniversalTime:.1));
            var failed=Done(worker.RequestDemand(.1));Need(failed.Fault!=null&&failed.Paused,"Failed physical transaction did not freeze accrual.");Near(failed.DebtSeconds,.1);
            using(var view=View(worker)){Need(view.Read.Stamp.Matches(original),"Failed physical work published a partial bank.");Near(view.Read.GetResourceQuantity(0),1);}
            Done(worker.Invoke(c=>c.Host.SwapSchedule(new OwnedRuntimeBuiltinSchedule(),new string('e',64),c.Host.World.Stamp),OwnedFlightWorkerInvocation.SameTimeMutation));
            Need(worker.ReadStatus().Fault==null,"Explicit same-time repair did not clear worker fault.");Near(Done(worker.Flush()).DebtSeconds,.1);
            Done(worker.SetPause(false));Done(worker.RequestDemand(.1));Done(worker.SetPause(true));
            using(var view=View(worker)){Near(view.Read.Stamp.Time,.1);Near(view.Read.GetResourceQuantity(0),1-.5/(1000*9.80665));}
            Vec before=Done(worker.Invoke(c=>c.Session.CenterPosition(0)));
            Done(worker.Invoke(c=>
            {
                var start=c.Host.World.Stamp;c.Session.Rebase(start,new Vec(-11000,2500,-3000));
                c.Timeline.Synchronize(start,c.Timeline.ReadProgress().WallSeconds,"inner acknowledged cold boundary");
                c.Session.Rebase(c.Host.World.Stamp,new Vec(-10000,2500,-3000));return true;
            },OwnedFlightWorkerInvocation.ColdMutation));
            using(var view=View(worker)){Near(view.Origin.X,-10000);Vec after=view.Descriptor.CenterPosition(view.Read,0);Near(after.X,before.X);Near(after.Y,before.Y);Near(after.Z,before.Z);}
            Need(worker.ReadStatus().Fault==null&&worker.ReadStatus().Progress.DebtSeconds==0,"Final cold acknowledgement ignored an inner acknowledged boundary or lost demand.");
        }
    }

    static void ReadSamplingDoesNotOverwritePhysicalGuard()
    {
        using(var worker=new OwnedFlightWorker(()=>Create(),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0}))
        {
            Need(worker.WaitUntilReady(3000),"Sampling fixture did not initialize.");
            Done(worker.Invoke(c=>
            {
                var stamp=c.Host.World.Stamp;
                using(var step=c.Host.World.BeginStep(stamp,.02,new OwnedRuntimeInput()))
                {
                    step.SetResourceQuantity(0,.5);step.RefreshMassProperties();var state=step.GetRegion(0);
                    var sample=c.Session.Sample(stamp,0,state.Motion,state.Properties);
                    c.Session.EvaluateRegion(0);
                    var motion=state.Motion;OwnedRuntimeDynamics.Step(ref motion,state.Properties,sample.Gravity*state.Properties.Mass,new Vec(),.02);step.SetRegionMotion(0,motion);
                    c.Session.ValidateStep(step);
                }
                Need(c.Host.World.Stamp.Matches(stamp),"Uncommitted sample regression mutated the bank.");return true;
            }));
        }
    }
    static double PendingControls(bool live)
    {
        using(var worker=new OwnedFlightWorker(()=>Create(),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0}))
        {
            Need(worker.WaitUntilReady(3000),"Live policy fixture did not initialize.");
            Done(worker.Invoke(c=>
            {
                c.Configure(Controls(.5),1,false,wallSeconds:0);
                c.Timeline.Pump(.1,0);
                return c.Configure(Controls(.5),1,true,wallSeconds:.1);
            }));
            Near(worker.ReadStatus().Progress.DebtSeconds,.1);
            var changed=live?Done(worker.SetLiveControls(Controls())):Done(worker.SetControls(Controls()));
            Near(changed.DebtSeconds,.1);
            var stamp=changed.Stamp;long revision=changed.InputRevision;
            var stale=worker.Invoke(c=>c.Timeline.SetLiveControls(c.Host.World.Stamp,revision-1,.1,Controls(1)));
            Need(stale.Wait(3000)&&stale.Error!=null,"Stale live controls were admitted.");
            var invalid=Controls();invalid.Throttle=double.NaN;Reject(()=>worker.SetLiveControls(invalid));
            Need(Done(worker.Invoke(c=>c.Host.World.Stamp)).Matches(stamp),"Invalid live policy changed the physical bank.");
            for(int i=0;i<16&&worker.ReadStatus().Progress.DebtSeconds>0;i++)Done(worker.Step(Math.Min(.02,worker.ReadStatus().Progress.DebtSeconds)));
            using(var view=View(worker)){Near(view.Read.Stamp.Time,.1);Near(view.Progress.DebtSeconds,0);return view.Read.GetResourceQuantity(0);}
        }
    }
    public static int Main()
    {
        IndependentProgressAndBankLease();OrderedCancellationAndPressure();ActualRetainedReaderPressure();FailedTransactionAndColdFrame();ReadSamplingDoesNotOverwritePhysicalGuard();
        Near(PendingControls(true),1);Near(PendingControls(false),1-.5/(1000*9.80665));
        Console.WriteLine("PASS independent owned worker: no-render progress, exclusive writer, coherent retained view, ordered bounded cancellation, true transaction rollback/recovery, rebase frame and read-only guard sampling");return 0;
    }
}
