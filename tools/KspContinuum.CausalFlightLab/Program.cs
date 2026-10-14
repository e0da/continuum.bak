using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using KspContinuum;

static class Program
{
    static readonly JsonSerializerOptions Json=new(){IncludeFields=true,WriteIndented=true};
    sealed class Pin {public string role="",path="",sha256="";public long bytes;}
    sealed class Inputs
    {
        public NativeAssemblySceneManifest Native=null!;public OwnedRuntimeCapturedParts Parts=null!;
        public OwnedRuntimeCapturedAero Aero=null!;public OwnedFlightCapture Context=null!;
        public WholeCraftGeometry? Geometry;public readonly List<Pin> Pins=new();public bool Surplus;
    }
    sealed class State {public OwnedRuntimeImportResult Imported=null!;public readonly OwnedRuntimeControls Controls=new(){Attitude=new Vec(.03,-.02,.01),WheelsEnabled=true};}
    sealed class Run
    {
        public string name="",status="failed",stateSha256="",pendingCheckpointSha256="";
        public long viewReads,workTicks,workerAllocatedBytes,mechanicalCommits,thermalAdvances,thermalDeferredSteps;
        public double time,pendingAtCheckpoint,integratedThermalTime,pendingThermalTime;
        public bool checkpointRestored,invalidCheckpointRejected,codeReplacementPreservedState,failedTransactionRolledBack,recoveredDebt;
        public int splitRegions,joinedRegions;
        public bool adaptive,pendingEventsRestored,invalidExecutionRejected;public long eventsApplied,visualSamples;public OwnedRuntimeMechanicalState? mechanical;
        public double[]? finalCenter,finalVelocity,finalOrientation;public double maximumDisplayDelay;
        public OwnedFlightWorkerStatus worker;
    }
    sealed class SameSchedule : IOwnedRuntimeFieldSchedule, IOwnedRuntimeWorkerSchedule, IOwnedRuntimeTrialSafeSchedule
    {
        public string Schema=>"continuum-owned-runtime-schedule/v1";
        public string StateSchema=>"world-bank-state-only/v1";
        public string ImplementationId=>"causal-lab-source-bound-same-systems/v1";
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)=>systems.Step(step,environment,controls);
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)=>systems.Step(step,field,controls);
    }
    sealed class FailAfterSystems : IOwnedRuntimeFieldSchedule, IOwnedRuntimeWorkerSchedule, IOwnedRuntimeTrialSafeSchedule
    {
        public string Schema=>"continuum-owned-runtime-schedule/v1";
        public string StateSchema=>"world-bank-state-only/v1";
        public string ImplementationId=>"causal-lab-deliberate-post-systems-rejection/v1";
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {systems.Step(step,environment,controls);throw new InvalidOperationException("Frozen deliberate rejection after private-bank computation.");}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {systems.Step(step,field,controls);throw new InvalidOperationException("Frozen deliberate rejection after private-bank computation.");}
    }
    static void Need(bool valid,string reason){if(!valid)throw new InvalidDataException(reason);}
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string HashFile(string path){using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();}
    static void NoLinks(string path)
    {for(string? p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))Need((!File.Exists(p)&&!Directory.Exists(p))||(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"Reparse ancestry rejected.");}
    static T Read<T>(string role,string path,Inputs input) where T:class
    {
        Need(Path.IsPathFullyQualified(path),"Absolute source path required.");NoLinks(path);
        using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);long length=f.Length;
        Need(length>0&&length<=64*1024*1024,"JSON source exceeds64MiB.");var bytes=new byte[(int)length];f.ReadExactly(bytes);
        Need(f.ReadByte()==-1&&f.Length==length,"Source changed during read.");input.Pins.Add(new Pin{role=role,path=Path.GetFullPath(path),sha256=Hash(bytes),bytes=length});
        return JsonSerializer.Deserialize<T>(bytes,Json)??throw new InvalidDataException("Null input.");
    }
    static T Await<T>(OwnedFlightWorkerAcknowledgement<T> ack)
    {Need(ack.Wait(60000),"Started worker command did not acknowledge within60s; no retry was issued.");if(ack.Error!=null)throw new InvalidOperationException("Worker rejected command.",ack.Error);return ack.Result;}
    static OwnedFlightWorkerContext Initialize(Inputs input)
    {
        var imported=OwnedRuntimeImport.Create(input.Native,input.Geometry,input.Parts,input.Pins[0].sha256,input.Pins[1].sha256,allowAcquiredSurplus:input.Surplus);
        OwnedRuntimeAcquiredAero.Attach(imported,OwnedRuntimeAcquiredAero.Build(imported,input.Aero));
        imported.Systems.SetThermalPolicy(imported.World.Stamp,new OwnedRuntimeThermalPolicy(true));
        var host=new OwnedRuntimeHost(imported.World,imported.Systems);
        var context=OwnedFlightWorld.Admit(input.Context,input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256);
        var session=OwnedFlightSession.SeedCaptured(imported.World,context,context.CapturedRootPosition);
        return new OwnedFlightWorkerContext(host,session,new State{Imported=imported});
    }
    static byte[] Checkpoint(OwnedFlightWorkerContext c)
    {return Checkpoint(c,false);}
    static byte[] Checkpoint(OwnedFlightWorkerContext c,bool execution)
    {var s=(State)c.State;return OwnedRuntimeCheckpointBinary.EncodeFlight(OwnedFlightCheckpoint.Capture(c.Host,s.Imported,c.Session,s.Controls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"",timeline:execution?c.Timeline:null));}
    static string PhysicalHash(byte[] checkpoint)
    {var p=OwnedRuntimeCheckpointBinary.DecodeFlight(checkpoint);return Hash(JsonSerializer.SerializeToUtf8Bytes(new{state=p.host.state,flight=p.flight,pilot=p.pilot,maneuver=p.maneuver,visual=p.visualSha256},Json));}
    static void Observe(OwnedFlightWorker worker,Run result)
    {
        using var view=worker.AcquirePresentation();Need(view!=null,"No committed view after acknowledged command.");
        Need(view!.Read.Stamp.Matches(view.Progress.Stamp),"View bank and progress differ.");
        var sample=view.Descriptor.Sample(view.Read,0);Need(double.IsFinite(sample.Density),"Nonfinite pure presentation environment.");result.viewReads++;
    }
    static Run Replay(string name,Inputs input,string output,bool observed)
    {
        var result=new Run{name=name};long begin=Stopwatch.GetTimestamp();
        using var worker=new OwnedFlightWorker(()=>Initialize(input),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0,MaximumCommitsPerPump=65536,WallBudgetSeconds=60});
        Need(worker.WaitUntilReady(60000),worker.ReadStatus().Fault??"Worker did not initialize.");
        long allocationStart=Await(worker.Invoke(c=>GC.GetAllocatedBytesForCurrentThread()));
        OwnedRuntimeControls controls=Await(worker.Invoke(c=>((State)c.State).Controls));
        Await(worker.Configure(controls,1,false,wallSeconds:0));
        foreach(double demand in new[]{.02,.04,.06})
        {var p=Await(worker.RequestDemand(demand));Need(p.Fault==null,p.Fault??"Demand failed.");if(observed)Observe(worker,result);}
        Await(worker.Configure(controls,1,true,wallSeconds:.06));
        // Close and consume any representable sub-quantum tail at this explicit checkpoint boundary.
        for(int i=0;i<8&&worker.ReadStatus().Progress.DebtSeconds>0;i++)Await(worker.Step(Math.Min(.02,worker.ReadStatus().Progress.DebtSeconds)));
        byte[] saved=Await(worker.Invoke(Checkpoint));result.pendingCheckpointSha256=Hash(saved);
        var packet=OwnedRuntimeCheckpointBinary.DecodeFlight(saved);result.pendingAtCheckpoint=packet.host.state.thermal.pendingDuration;
        Need(result.pendingAtCheckpoint>0,"Checkpoint must exercise deferred thermal energy, not only poses.");
        string path=Path.Combine(output,name+"-pending.checkpoint");using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write))file.Write(saved);
        Await(worker.Invoke(c=>
        {
            var s=(State)c.State;var decoded=OwnedRuntimeCheckpointBinary.DecodeFlight(saved);
            var restored=OwnedFlightCheckpoint.Restore(c.Host,s.Imported,decoded,input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256,"");
            c.Replace(restored.Host,restored.Session,new State{Imported=restored.Imported});return true;
        },OwnedFlightWorkerInvocation.ColdMutation));
        Need(Hash(Await(worker.Invoke(Checkpoint)))==Hash(saved),"Exact pending-energy checkpoint continuation changed.");result.checkpointRestored=true;
        byte[] damaged=(byte[])saved.Clone();damaged[damaged.Length/2]^=1;
        string before=Hash(Await(worker.Invoke(Checkpoint)));
        var bad=worker.Invoke(c=>{OwnedRuntimeCheckpointBinary.DecodeFlight(damaged);return true;},OwnedFlightWorkerInvocation.ReadOnly);
        Need(bad.Wait(60000)&&bad.Error!=null,"Corrupt checkpoint was not rejected.");
        Need(Hash(Await(worker.Invoke(Checkpoint)))==before,"Rejected checkpoint changed authoritative state.");result.invalidCheckpointRejected=true;
        string physicalBefore=PhysicalHash(Await(worker.Invoke(Checkpoint)));
        Await(worker.Invoke(c=>{c.Host.SwapSchedule(new SameSchedule(),HashFile(Assembly.GetExecutingAssembly().Location),c.Host.World.Stamp);return true;},OwnedFlightWorkerInvocation.SameTimeMutation));
        Need(PhysicalHash(Await(worker.Invoke(Checkpoint)))==physicalBefore,"Schedule replacement changed physical banks/pending thermal state.");result.codeReplacementPreservedState=true;
        Await(worker.Invoke(c=>{c.Host.SwapSchedule(new FailAfterSystems(),HashFile(Assembly.GetExecutingAssembly().Location),c.Host.World.Stamp);return true;},OwnedFlightWorkerInvocation.SameTimeMutation));
        Await(worker.Configure(controls,1,false,wallSeconds:.06));
        var rejected=Await(worker.RequestDemand(.08));Need(rejected.Fault!=null,"Deliberately failed physical transaction did not fault.");
        Need(PhysicalHash(Await(worker.Invoke(Checkpoint)))==physicalBefore,"Failed physical transaction published a changed bank/thermal ledger.");result.failedTransactionRolledBack=true;
        double owed=rejected.DebtSeconds;Need(owed>0,"Failed transaction silently discarded requested time.");
        Await(worker.Invoke(c=>{c.Host.SwapSchedule(new SameSchedule(),HashFile(Assembly.GetExecutingAssembly().Location),c.Host.World.Stamp);return true;},OwnedFlightWorkerInvocation.SameTimeMutation));
        Need(worker.ReadStatus().Progress.DebtSeconds==owed&&worker.ReadStatus().Fault==null,"Same-time recovery discarded debt or retained worker fault.");result.recoveredDebt=true;
        for(int i=0;i<16&&worker.ReadStatus().Progress.DebtSeconds>0;i++){var p=Await(worker.Step(Math.Min(.02,worker.ReadStatus().Progress.DebtSeconds)));Need(p.Fault==null&&p.LocalCommits==1,p.Fault??"Recovered debt did not commit.");}
        Need(worker.ReadStatus().Progress.DebtSeconds==0,"Recovered debt drain exceeded its bound.");
        Await(worker.Configure(controls,4,false,wallSeconds:.08));
        foreach(double demand in new[]{.1,.12,.14,.16})
        {var p=Await(worker.RequestDemand(demand));Need(p.Fault==null,p.Fault??"Warp demand failed.");if(observed)Observe(worker,result);}
        Await(worker.Configure(controls,1,true,wallSeconds:.16));
        for(int i=0;i<8&&worker.ReadStatus().Progress.DebtSeconds>0;i++)Await(worker.Step(Math.Min(.02,worker.ReadStatus().Progress.DebtSeconds)));
        var cut=Await(worker.Invoke(c=>
        {
            c.Host.Systems.FlushThermal(c.Host.World.Stamp);var snapshot=c.Host.World.CaptureSnapshot();
            var link=snapshot.Connections.First(x=>x.Locked&&snapshot.Definition.GetPart(snapshot.Definition.PartSlot(x.PartA)).BodyId!=snapshot.Definition.GetPart(snapshot.Definition.PartSlot(x.PartB)).BodyId);
            c.Host.World.Split(c.Host.World.Stamp,link.Id);return Tuple.Create(link,snapshot.Connections.Max(x=>x.Id));
        },OwnedFlightWorkerInvocation.ColdMutation));
        result.splitRegions=Await(worker.Invoke(c=>c.Host.World.RegionCount));Need(result.splitRegions>1,"Actual captured connection did not create separate regions.");if(observed)Observe(worker,result);
        Await(worker.Invoke(c=>{c.Host.World.Dock(c.Host.World.Stamp,checked(cut.Item2+1),cut.Item1.PartA,cut.Item1.PartB);return true;},OwnedFlightWorkerInvocation.ColdMutation));
        result.joinedRegions=Await(worker.Invoke(c=>c.Host.World.RegionCount));Need(result.joinedRegions==1,"Explicit acquired-part join did not restore a single assembly.");
        var final=Await(worker.Invoke(c=>{var bytes=Checkpoint(c);var snap=c.Host.World.CaptureSnapshot();return Tuple.Create(bytes,snap.Thermal); }));
        result.stateSha256=Hash(final.Item1);result.thermalAdvances=final.Item2.advances;result.thermalDeferredSteps=final.Item2.deferredSteps;
        result.integratedThermalTime=final.Item2.integratedTime;result.pendingThermalTime=final.Item2.pendingDuration;
        result.time=worker.ReadStatus().Progress.CommittedRuntimeTime;result.mechanicalCommits=worker.ReadStatus().Progress.TotalLocalCommits;
        Need(result.thermalAdvances>0&&result.thermalDeferredSteps>0,"Different thermal/mechanical clocks were not exercised.");
        result.workerAllocatedBytes=Await(worker.Invoke(c=>GC.GetAllocatedBytesForCurrentThread()))-allocationStart;
        result.worker=worker.ReadStatus();Need(result.worker.Fault==null&&result.worker.Progress.Fault==null,"Worker faulted.");
        result.workTicks=Stopwatch.GetTimestamp()-begin;result.status="passed";return result;
    }
    static object Autonomous(Inputs input)
    {
        using var worker=new OwnedFlightWorker(()=>Initialize(input));Need(worker.WaitUntilReady(60000),"Autonomous init failed.");
        var controls=Await(worker.Invoke(c=>((State)c.State).Controls));Await(worker.Configure(controls,1,false));
        // No presentation, demand or input callback occurs during this bounded wall interval.
        Thread.Sleep(150);Await(worker.SetPause(true));var status=worker.ReadStatus();
        Need(status.Progress.TotalLocalCommits>0&&status.Fault==null,"Simulation required a presentation callback to advance.");
        return new{status,scope="Actual worker advances while presentation has no callbacks; wall duration is a functionality witness, not a throughput/FPS benchmark."};
    }
    static double[] Vector(Vec p)=>new[]{p.X,p.Y,p.Z};
    static double[] Quaternion(Rotation q)=>new[]{q.X,q.Y,q.Z,q.W};
    static double Difference(double[] a,double[] b)=>Math.Sqrt(a.Zip(b,(x,y)=>(x-y)*(x-y)).Sum());
    static void Drain(OwnedFlightWorker worker)
    {for(int i=0;i<256&&worker.ReadStatus().Progress.DebtSeconds>0;i++){var p=Await(worker.Step(Math.Min(.02,worker.ReadStatus().Progress.DebtSeconds)));Need(p.Fault==null&&p.LocalCommits==1,p.Fault??"Debt drain did not commit.");}Need(worker.ReadStatus().Progress.DebtSeconds==0,"Bounded semantic endpoint drain remained owed.");}
    static Run AdaptiveReplay(string name,Inputs input,string output,bool adaptive,bool observed)
    {
        var row=new Run{name=name,adaptive=adaptive};long begin=Stopwatch.GetTimestamp();
        using var worker=new OwnedFlightWorker(()=>Initialize(input),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0,MaximumCommitsPerPump=65536,WallBudgetSeconds=60});
        Need(worker.WaitUntilReady(60000),worker.ReadStatus().Fault??"Adaptive source initialization failed.");
        long allocationStart=Await(worker.Invoke(c=>GC.GetAllocatedBytesForCurrentThread()));
        Await(worker.Invoke(c=>{c.Host.Systems.SetMechanicalPolicy(c.Host.World.Stamp,new OwnedRuntimeMechanicalPolicy(adaptive));return true;},OwnedFlightWorkerInvocation.ColdMutation));
        var controls=Await(worker.Invoke(c=>((State)c.State).Controls));
        Await(worker.Invoke(c=>
        {
            long id;using(var read=c.Host.World.AcquireRead())id=read.GetRegion(0).Id;
            c.Timeline.EnqueueEvent(new OwnedFlightEvent("actual-region-impulse",1,.075,OwnedFlightEventKind.Impulse,id,new Vec(0,10,0)));
            var held=controls;held.Attitude=new Vec(-.02,.01,0);
            c.Timeline.EnqueueEvent(new OwnedFlightEvent("held-controls-after-impulse",2,.075,OwnedFlightEventKind.Controls,controls:held));return true;
        }));
        var smoothing=new OwnedFlightInterpolation(input.Native.bodies.Length);double lastVisual=double.NegativeInfinity;long lastResets=-1;
        Action observe=delegate
        {
            using var frame=worker.AcquirePresentation();Need(frame!=null,"Acknowledged source lacks a coherent view.");double wall=frame!.Progress.WallSeconds;
            smoothing.Accept(frame,wall);var display=smoothing.Sample(wall);Need(display.UniversalTime<=frame.UniversalTime,"Visual interpolation extrapolated.");
            Need(display.UniversalTime>=lastVisual||display.Resets>lastResets,"Visual clock regressed without source reset.");lastVisual=display.UniversalTime;lastResets=display.Resets;
            row.maximumDisplayDelay=Math.Max(row.maximumDisplayDelay,display.DelaySeconds);row.visualSamples++;row.viewReads++;
        };
        Await(worker.Configure(controls,1,false,wallSeconds:0));if(observed)observe();var first=Await(worker.RequestDemand(.05));Need(first.Fault==null,first.Fault??"Initial adaptive demand faulted.");if(observed)observe();
        Await(worker.Configure(controls,1,true,wallSeconds:.05));Drain(worker);if(observed)observe();
        byte[] saved=Await(worker.Invoke(c=>Checkpoint(c,true)));row.pendingCheckpointSha256=Hash(saved);var packet=OwnedRuntimeCheckpointBinary.DecodeFlight(saved);
        Need(packet.timeline!=null&&packet.timeline.events.pending.Length==2&&packet.timeline.events.history.Length==0,"Future events were missing from execution checkpoint.");
        row.pendingAtCheckpoint=packet.host.state.thermal.pendingDuration;Need(row.pendingAtCheckpoint>0,"Adaptive checkpoint lacked deferred thermal forcing.");
        using(var file=new FileStream(Path.Combine(output,name+"-events-pending.checkpoint"),FileMode.CreateNew,FileAccess.Write))file.Write(saved);
        Await(worker.Invoke(c=>
        {
            var state=(State)c.State;var restored=OwnedFlightCheckpoint.Restore(c.Host,state.Imported,OwnedRuntimeCheckpointBinary.DecodeFlight(saved),input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256,"");
            c.Replace(restored.Host,restored.Session,new State{Imported=restored.Imported},restored.TimelineState);return true;
        },OwnedFlightWorkerInvocation.CheckpointReplacement));
        Need(Hash(Await(worker.Invoke(c=>Checkpoint(c,true))))==Hash(saved),"Full adaptive/event/thermal checkpoint did not restore exactly.");row.pendingEventsRestored=row.checkpointRestored=true;
        var rejectedExecution=worker.Invoke(c=>
        {
            var state=(State)c.State;var invalid=OwnedRuntimeCheckpointBinary.DecodeFlight(saved);invalid.timeline.events.pending[0].regionId=long.MaxValue;
            var candidate=OwnedFlightCheckpoint.Restore(c.Host,state.Imported,invalid,input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256,"");
            c.Replace(candidate.Host,candidate.Session,new State{Imported=candidate.Imported},candidate.TimelineState);return true;
        },OwnedFlightWorkerInvocation.CheckpointReplacement);
        Need(rejectedExecution.Wait(60000)&&rejectedExecution.Error!=null,"Dangling event receiver execution state was accepted.");
        Need(Hash(Await(worker.Invoke(c=>Checkpoint(c,true))))==Hash(saved)&&worker.ReadStatus().Fault==null,"Rejected execution restore changed authoritative state or latched an unrelated publication fault.");row.invalidExecutionRejected=true;
        Await(worker.Configure(controls,1,false,wallSeconds:.05));foreach(double demand in new[]{.08,.1,.12,.15}){var next=Await(worker.RequestDemand(demand));Need(next.Fault==null,next.Fault??"Post-event adaptive demand faulted.");if(observed)observe();}
        Await(worker.SetPause(true));Drain(worker);if(observed)observe();
        var final=Await(worker.Invoke(c=>
        {
            using var read=c.Host.World.AcquireRead();var r=read.GetRegion(0);Vec center=c.Session.Origin+r.Motion.RootPosition+r.Motion.Orientation.Rotate(r.Properties.CenterOfMass);
            return Tuple.Create(Checkpoint(c,true),c.Host.World.CaptureSnapshot(),Vector(center),Vector(r.Motion.Velocity),Quaternion(r.Motion.Orientation));
        }));
        var mechanical=final.Item2.Mechanical??throw new InvalidDataException("Missing committed mechanical controller.");row.stateSha256=Hash(final.Item1);row.mechanical=mechanical;row.thermalAdvances=final.Item2.Thermal.advances;row.thermalDeferredSteps=final.Item2.Thermal.deferredSteps;
        row.integratedThermalTime=final.Item2.Thermal.integratedTime;row.pendingThermalTime=final.Item2.Thermal.pendingDuration;row.finalCenter=final.Item3;row.finalVelocity=final.Item4;row.finalOrientation=final.Item5;
        row.worker=worker.ReadStatus();row.time=row.worker.Progress.CommittedRuntimeTime;row.eventsApplied=row.worker.Progress.TotalEventsApplied;row.mechanicalCommits=row.worker.Progress.TotalLocalCommits;
        Need(row.eventsApplied==2&&row.worker.Progress.PendingEvents==0,"Physical events did not apply exactly once.");
        var history=OwnedRuntimeCheckpointBinary.DecodeFlight(final.Item1).timeline.events.history;Need(history.Length==2&&history[0].id=="actual-region-impulse"&&history[1].id=="held-controls-after-impulse"&&history.All(x=>x.time==.075),"Event order/frontier changed.");
        Need(row.worker.Fault==null&&row.worker.Progress.Fault==null,"Adaptive consumer faulted.");if(adaptive)Need(mechanical.accepted>0&&mechanical.trialExecutions>=3*mechanical.accepted,"Isolated full/two-half mechanical trials were not accounted.");if(observed)Need(row.maximumDisplayDelay>0,"Observed acquired source never exercised delayed display poses.");
        row.workerAllocatedBytes=Await(worker.Invoke(c=>GC.GetAllocatedBytesForCurrentThread()))-allocationStart;row.workTicks=Stopwatch.GetTimestamp()-begin;row.status="passed";return row;
    }
    static object Legacy(string path,Inputs input)
    {
        Need(Path.IsPathFullyQualified(path),"Absolute legacy checkpoint required.");NoLinks(path);byte[] bytes;
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {long length=file.Length;Need(length>0&&length<=64L*1024*1024+256,"Legacy checkpoint exceeds bound.");bytes=new byte[(int)length];file.ReadExactly(bytes);Need(file.ReadByte()==-1&&file.Length==length,"Legacy checkpoint changed during read.");}
        string sha=Hash(bytes);input.Pins.Add(new Pin{role="legacy-checkpoint",path=Path.GetFullPath(path),sha256=sha,bytes=bytes.Length});
        var packet=OwnedRuntimeCheckpointBinary.DecodeFlight(bytes);Need(packet.host.state.schema=="continuum-owned-runtime-checkpoint/v1"&&packet.host.state.thermal==null,"Selected compatibility fixture is not an original prethermal checkpoint.");
        var restored=OwnedRuntimeCheckpoint.Restore(packet.host.state,input.Pins[0].sha256,input.Pins[1].sha256);
        var session=OwnedFlightSession.Restore(restored.World,packet.flight,input.Pins[0].sha256,input.Pins[1].sha256);
        Need(session.Context.SourceSha256==input.Pins[2].sha256,"Legacy flight context differs from acquired source.");
        using var read=restored.World.AcquireRead();Need(read.ThermalPendingDuration==0&&read.ThermalIntegratedTime==read.Stamp.Time,"Legacy restore invented pending energy or changed thermal frontier.");
        var captured=restored.World.CaptureSnapshot();Need(captured.Thermal.heatJoules.All(x=>x==0)&&captured.Thermal.convectionExposure.All(x=>x==0)&&captured.Thermal.radiationExposure.All(x=>x==0),"Legacy restore invented forcing history.");
        return new{status="passed",path=Path.GetFullPath(path),sha256=sha,originalProducerScheduleSha256=packet.host.sourceSha256,originalProducerCodeGeneration=packet.host.codeGeneration,
            parts=read.PartCount,bodies=read.BodyCount,resources=read.ResourceCount,stamp=read.Stamp,thermalIntegratedTime=read.ThermalIntegratedTime,thermalPendingDuration=read.ThermalPendingDuration,
            scope="Original retained prethermal binary decoded and full physical/resource/provider/input state restored against acquired source hashes; empty thermal ledger initialized at physical time. Original executable schedule is recorded but not loaded/rebound or claimed continued."};
    }
    static int Main(string[] args)
    {
        string? output=null,failure=null;bool created=false;var input=new Inputs();var runs=new List<Run>();object? autonomous=null,legacy=null,adaptiveComparison=null;
        try
        {
            Need(args.Length>=10&&args.Length%2==0,"--native ABS --parts ABS --context ABS --aero ABS --output NEW_ABS [--resource-policy strict|acquired-surplus] [--legacy-checkpoint ABS]");
            var options=new Dictionary<string,string>();for(int i=0;i<args.Length;i+=2)Need(new[]{"--native","--parts","--context","--aero","--output","--resource-policy","--legacy-checkpoint"}.Contains(args[i])&&options.TryAdd(args[i],args[i+1]),"Unknown/duplicate option.");
            foreach(string key in new[]{"--native","--parts","--context","--aero","--output"})Need(options.ContainsKey(key)&&Path.IsPathFullyQualified(options[key]),"Absolute required argument missing.");
            output=Path.GetFullPath(options["--output"]);NoLinks(output);Need(!File.Exists(output)&&!Directory.Exists(output),"Fresh output required.");
            foreach(string key in new[]{"--native","--parts","--context","--aero"}){string parent=Path.GetDirectoryName(Path.GetFullPath(options[key]))!;Need(output!=parent&&!output.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.Ordinal),"Output overlaps inputs.");}
            input.Native=Read<NativeAssemblySceneManifest>("native",options["--native"],input);input.Parts=Read<OwnedRuntimeCapturedParts>("parts",options["--parts"],input);
            input.Context=Read<OwnedFlightCapture>("context",options["--context"],input);input.Aero=Read<OwnedRuntimeCapturedAero>("aero",options["--aero"],input);
            string policy=options.GetValueOrDefault("--resource-policy","strict");Need(policy=="strict"||policy=="acquired-surplus","Unknown resource policy.");input.Surplus=policy=="acquired-surplus";
            if(!string.IsNullOrEmpty(input.Native.geometry))
            {MultiscaleSceneFormat.PortablePath(input.Native.geometry);string parent=Path.GetDirectoryName(options["--native"])!;string path=Path.GetFullPath(Path.Combine(parent,input.Native.geometry));NoLinks(path);Need(path.StartsWith(Path.GetFullPath(parent)+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&new FileInfo(path).Length<=1073741824,"Geometry path/size rejected.");string hash=HashFile(path);Need(hash==input.Native.geometrySha256,"Geometry hash differs.");input.Geometry=WholeCraftSceneFormat.ReadGeometry(path);input.Pins.Add(new Pin{role="geometry",path=path,sha256=hash,bytes=new FileInfo(path).Length});}
            Directory.CreateDirectory(output);created=true;
            if(options.TryGetValue("--legacy-checkpoint",out string? legacyPath))legacy=Legacy(legacyPath,input);
            runs.Add(Replay("observed",input,output,true));runs.Add(Replay("suspended",input,output,false));
            Need(runs[0].stateSha256==runs[1].stateSha256&&runs[0].pendingCheckpointSha256==runs[1].pendingCheckpointSha256,"Physical continuation depended on presentation lease cadence.");
            var adaptiveObserved=AdaptiveReplay("adaptive-observed",input,output,true,true);runs.Add(adaptiveObserved);
            var adaptiveSuspended=AdaptiveReplay("adaptive-suspended",input,output,true,false);runs.Add(adaptiveSuspended);
            Need(adaptiveObserved.stateSha256==adaptiveSuspended.stateSha256&&adaptiveObserved.pendingCheckpointSha256==adaptiveSuspended.pendingCheckpointSha256,"Adaptive/event continuation depended on presentation interpolation.");
            var reference=AdaptiveReplay("fixed-reference-events",input,output,false,false);runs.Add(reference);
            adaptiveComparison=new{reference=reference.name,candidate=adaptiveSuspended.name,referenceCompleteTicks=reference.workTicks,candidateCompleteTicks=adaptiveSuspended.workTicks,
                positionDifferenceMeters=Difference(reference.finalCenter!,adaptiveSuspended.finalCenter!),velocityDifferenceMetersPerSecond=Difference(reference.finalVelocity!,adaptiveSuspended.finalVelocity!),
                orientationDifferenceRadians=2*Math.Acos(Math.Min(1,Math.Abs(reference.finalOrientation!.Zip(adaptiveSuspended.finalOrientation!,(x,y)=>x*y).Sum()))),
                candidateMechanical=adaptiveSuspended.mechanical,scope="One acquired-input complete headless transaction per numerical strategy, same semantic controls/events/checkpoint/restore and no presentation reads. Includes preparation, isolated trials, codecs and acknowledgements. Local estimator budgets are not a global trajectory or FPS guarantee; differences are reported, not silently tolerance-qualified."};
            autonomous=Autonomous(input);Need(input.Pins.All(p=>HashFile(p.path)==p.sha256),"Acquired inputs changed.");
        }
        catch(Exception error){failure=error.ToString();}
        if(created){using var file=new FileStream(Path.Combine(output!,"receipt.json"),FileMode.CreateNew,FileAccess.Write);JsonSerializer.Serialize(file,new{schema="continuum-causal-flight-lab/v1",status=failure==null?"passed":"failed",failure,inputs=input.Pins,runs,autonomous,legacy,adaptiveComparison,clockFrequency=Stopwatch.Frequency,assemblySha256=HashFile(Assembly.GetExecutingAssembly().Location),coreSha256=HashFile(typeof(OwnedFlightWorker).Assembly.Location),scope="Actual acquired craft. Separate worker execution/presentation, bounded physical quanta and causal thermal ledger, pending-energy binary checkpoint continuation, rejected corruption and source-bound schedule replacement. Costs include initialization/acks/checkpoint/scenario; allocations report worker thread only. Not native-render FPS, original KSP behavior or universal multirate parity."},Json);}
        if(failure!=null){Console.Error.WriteLine(failure);return 2;}Console.WriteLine("Actual acquired worker continuation and pending thermal state passed.");return 0;
    }
}
