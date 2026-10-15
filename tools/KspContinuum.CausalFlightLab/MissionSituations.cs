using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using KspContinuum;

static partial class Program
{
    sealed class MissionPhase
    {
        public string name="",status="failed";
        public double startRuntimeTime,endRuntimeTime,startUniversalTime,endUniversalTime,requestedRuntimeTime,debtSeconds,warp,wallBudgetSeconds;
        public long completeTicks,workerAllocatedBytes,localCommits,eventsApplied,pumps,budgetExhaustions,netLocalCommitChange,netEventChange;
        public Dictionary<string,long> systemsWork=new(),bankWork=new();
        public int maximumCommitsPerPump;
        public bool workCountersRebound;
    }
    sealed class MissionCheck
    {public string name="",scope="";public bool passed;public double[] before=Array.Empty<double>(),after=Array.Empty<double>();public double maximumResidual;}
    sealed class MissionReceipt
    {
        public string schema="continuum-mission-situation-receipt/v1",status="failed",failure="",method="",deadlineStrategy="",finalCheckpoint="",finalSha256="",pendingCheckpoint="",pendingSha256="";
        public MissionSituationManifest situation=null!;public List<Pin> inputs=null!;
        public string toolSha256="",coreSha256="",scheduleSha256="",frameId="",contextSha256="";
        public long clockFrequency=Stopwatch.Frequency,completeTicks,codeGeneration;
        public int parts,bodies,engines,rcs,wheels,surfaces;
        public int[] authoredEnabledActuatorSlots=Array.Empty<int>();
        public List<MissionPhase> phases=new();public List<MissionCheck> checks=new();
        public OwnedFlightEventHistory[] events=Array.Empty<OwnedFlightEventHistory>();
        public double[] eventUniversalTimes=Array.Empty<double>();
        public OwnedRuntimeMechanicalState? mechanical;public OwnedRuntimeThermalState? thermal;public OwnedRuntimeSystemCounters? systems;
        public OwnedFlightWorkerStatus worker;
        public double sourceEpochSeconds,startUniversalTime,endUniversalTime,finalRuntimeTime,finalDebtSeconds,minimumAltitudeMeters,maximumDensity,maximumTemperatureKelvin,propellantKilograms,electricUnits;
        public string scope="Captured source craft and central-body data with explicitly authored mission placement/control cues. Phase complete clocks include worker dispatch, original schedule work, bounds and all attempted numerical intervals. Bank/system phase clocks overlap and are not additive. Checkpoints/topology checks are explicit cold observation/transaction phases. No render pacing, KSP FPS, native contacts or universal accuracy claim.";
    }
    sealed class MissionBudget
    {public int Commits;public double Seconds;public double Wall;}
    sealed class MissionPhaseStart
    {public OwnedRuntimeStamp Stamp;public double UniversalTime;public long Allocated,Local,Events;public Dictionary<string,long> Systems=new(),Bank=new();}
    readonly struct MissionMomentumRegion
    {
        public readonly double Mass;
        public readonly Vec Root,LocalCenter,Velocity,ChassisMomentum;
        public MissionMomentumRegion(double mass,Vec root,Vec rotatedLocalCenter,Vec velocity,Vec chassisMomentum)
        {Mass=mass;Root=root;LocalCenter=rotatedLocalCenter;Velocity=velocity;ChassisMomentum=chassisMomentum;}
    }
    struct MissionCompensatedSum
    {
        double sum,correction;
        public void Add(double value){double next=sum+value;correction+=Math.Abs(sum)>=Math.Abs(value)?(sum-next)+value:(value-next)+sum;sum=next;}
        public double Value=>sum+correction;
    }
    static byte[] MissionCheckpoint(OwnedFlightWorkerContext c)
    {var state=(State)c.State;return OwnedRuntimeCheckpointBinary.EncodeFlight(OwnedFlightCheckpoint.Capture(c.Host,state.Imported,c.Session,c.Timeline.HeldControls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"",timeline:c.Timeline));}
    static MissionPhase MissionMeasure(OwnedFlightWorker worker,MissionReceipt receipt,MissionBudget budget,string name,Action<MissionPhase> action)
    {
        var before=Await(worker.Invoke(c=>new MissionPhaseStart{Stamp=c.Host.World.Stamp,UniversalTime=c.Session.UniversalTime,Allocated=GC.GetAllocatedBytesForCurrentThread(),Local=c.Timeline.TotalLocalCommits,Events=c.Timeline.TotalEventsApplied,Systems=CountFields(c.Host.Systems.Counters),Bank=CountFields(c.Host.World.Work)}));
        var row=new MissionPhase{name=name,startRuntimeTime=before.Stamp.Time,startUniversalTime=before.UniversalTime,maximumCommitsPerPump=budget.Commits,wallBudgetSeconds=budget.Seconds};receipt.phases.Add(row);long begin=Stopwatch.GetTimestamp();
        try{action(row);row.status="passed";return row;}
        finally
        {
            row.completeTicks=Stopwatch.GetTimestamp()-begin;
            var after=Await(worker.Invoke(c=>new MissionPhaseStart{Stamp=c.Host.World.Stamp,UniversalTime=c.Session.UniversalTime,Allocated=GC.GetAllocatedBytesForCurrentThread(),Local=c.Timeline.TotalLocalCommits,Events=c.Timeline.TotalEventsApplied,Systems=CountFields(c.Host.Systems.Counters),Bank=CountFields(c.Host.World.Work)}));
            var p=worker.ReadStatus().Progress;row.endRuntimeTime=after.Stamp.Time;row.endUniversalTime=after.UniversalTime;row.requestedRuntimeTime=p.RequestedRuntimeTime;row.debtSeconds=p.DebtSeconds;row.warp=p.Warp;
            row.workerAllocatedBytes=after.Allocated-before.Allocated;row.netLocalCommitChange=after.Local-before.Local;row.netEventChange=after.Events-before.Events;row.systemsWork=Difference(after.Systems,before.Systems);row.bankWork=Difference(after.Bank,before.Bank);
        }
    }
    static void MissionPump(OwnedFlightWorker worker,MissionBudget budget,MissionPhase row)
    {
        var p=Await(worker.RequestDemand(budget.Wall));row.pumps++;row.localCommits+=p.LocalCommits;row.eventsApplied+=p.EventApplications;if(p.BudgetExhausted)row.budgetExhaustions++;
        Need(p.Fault==null,p.Fault??"Mission physical demand faulted.");
    }
    static void MissionConsume(OwnedFlightWorker worker,MissionBudget budget,MissionPhase row,double target)
    {
        Need(worker.ReadStatus().Progress.CommittedRuntimeTime<=target,"Mission horizon was already passed.");
        for(int i=0;i<16384&&worker.ReadStatus().Progress.CommittedRuntimeTime<target;i++)
        {
            double before=worker.ReadStatus().Progress.CommittedRuntimeTime;MissionPump(worker,budget,row);var p=worker.ReadStatus().Progress;
            Need(p.CommittedRuntimeTime<=target,"Mission physical demand crossed its authored endpoint.");
            if(p.Status=="awaiting-local-quantum")
            {
                Await(worker.Configure(Await(worker.Invoke(c=>c.Timeline.HeldControls)),p.Warp,true,wallSeconds:budget.Wall));
                for(int tails=0;tails<4096&&worker.ReadStatus().Progress.CommittedRuntimeTime<target;tails++)
                {var q=Await(worker.Step(Math.Min(.02,target-worker.ReadStatus().Progress.CommittedRuntimeTime)));row.localCommits+=q.LocalCommits;row.eventsApplied+=q.EventApplications;Need(q.Fault==null&&q.LocalCommits==1,q.Fault??"Semantic endpoint tail did not commit.");Need(q.CommittedRuntimeTime<=target,"Semantic tail crossed its endpoint.");}
                break;
            }
            Need(p.CommittedRuntimeTime>before||p.EventApplications>0||p.BudgetExhausted,"Mission demand made no progress without a declared work boundary.");
        }
        Need(worker.ReadStatus().Progress.CommittedRuntimeTime==target&&worker.ReadStatus().Progress.DebtSeconds==0,"Mission bounded drain did not reach its exact physical endpoint.");
        var controls=Await(worker.Invoke(c=>c.Timeline.HeldControls));Await(worker.Configure(controls,worker.ReadStatus().Progress.Warp,true,wallSeconds:budget.Wall));
    }
    static void MissionDemandTo(OwnedFlightWorker worker,MissionBudget budget,MissionPhase row,double target,double warp)
    {
        var p=worker.ReadStatus().Progress;Need(p.Paused&&p.DebtSeconds==0&&target>p.CommittedRuntimeTime,"Mission segment requires a paused exact prior endpoint.");
        var controls=Await(worker.Invoke(c=>c.Timeline.HeldControls));Await(worker.Configure(controls,warp,false,wallSeconds:budget.Wall));
        budget.Wall+=(target-p.CommittedRuntimeTime)/warp;MissionConsume(worker,budget,row,target);
    }
    static double[] MissionMomentum(OwnedFlightWorkerContext c)
    {
        using var read=c.Host.World.AcquireRead();var rows=new MissionMomentumRegion[read.RegionCount];
        for(int r=0;r<rows.Length;r++){var a=read.GetRegion(r);rows[r]=new MissionMomentumRegion(a.Properties.Mass,a.Motion.RootPosition,a.Motion.Orientation.Rotate(a.Properties.CenterOfMass),a.Motion.Velocity,a.Motion.AngularMomentum);}
        return MissionMomentum(rows);
    }
    static double[] MissionMomentum(MissionMomentumRegion[] rows)
    {
        Need(rows.Length>0,"Momentum oracle requires an actual region.");var reference=rows[0];
        if(rows.Length==1)return new[]{reference.Mass,reference.Mass*reference.Velocity.X,reference.Mass*reference.Velocity.Y,reference.Mass*reference.Velocity.Z,reference.ChassisMomentum.X,reference.ChassisMomentum.Y,reference.ChassisMomentum.Z};
        var mass=new MissionCompensatedSum();var cx=new MissionCompensatedSum();var cy=new MissionCompensatedSum();var cz=new MissionCompensatedSum();
        foreach(var row in rows)
        {
            Vec relative=(row.Root+reference.Root*-1)+row.LocalCenter+reference.LocalCenter*-1;
            mass.Add(row.Mass);cx.Add(relative.X*row.Mass);cy.Add(relative.Y*row.Mass);cz.Add(relative.Z*row.Mass);
        }
        double total=mass.Value;Vec center=new(cx.Value/total,cy.Value/total,cz.Value/total);
        var px=new MissionCompensatedSum();var py=new MissionCompensatedSum();var pz=new MissionCompensatedSum();var lx=new MissionCompensatedSum();var ly=new MissionCompensatedSum();var lz=new MissionCompensatedSum();
        foreach(var row in rows)
        {
            Vec relative=(row.Root+reference.Root*-1)+row.LocalCenter+reference.LocalCenter*-1;
            Vec momentum=(row.Velocity+reference.Velocity*-1)*row.Mass;
            Vec orbit=Vec.Cross(relative+center*-1,momentum);
            px.Add(momentum.X);py.Add(momentum.Y);pz.Add(momentum.Z);
            lx.Add(row.ChassisMomentum.X);lx.Add(orbit.X);ly.Add(row.ChassisMomentum.Y);ly.Add(orbit.Y);lz.Add(row.ChassisMomentum.Z);lz.Add(orbit.Z);
        }
        return new[]{total,total*reference.Velocity.X+px.Value,total*reference.Velocity.Y+py.Value,total*reference.Velocity.Z+pz.Value,lx.Value,ly.Value,lz.Value};
    }
    static MissionCheck MissionMomentumRegression()
    {
        Vec root=new(1e9,-2e9,3e9),velocity=new(1e8,-2e8,3e8),h=new(2,-0d,5);
        var single=new MissionMomentumRegion(123456.7,root,new Vec(.7,-.9,1.1),velocity,h);double[] one=MissionMomentum(new[]{single});
        Need(BitConverter.DoubleToInt64Bits(one[4])==BitConverter.DoubleToInt64Bits(h.X)&&BitConverter.DoubleToInt64Bits(one[5])==BitConverter.DoubleToInt64Bits(h.Y)&&BitConverter.DoubleToInt64Bits(one[6])==BitConverter.DoubleToInt64Bits(h.Z),"Single-region oracle must return exact original chassis angular momentum, including signed zero.");
        var a=new MissionMomentumRegion(2,root,new Vec(.25,.5,-.75),velocity,new Vec(2,3,5));
        var b=new MissionMomentumRegion(3,root+new Vec(4,2,-1),new Vec(.5,.25,.75),velocity+new Vec(.125,-.25,.5),new Vec(7,11,13));
        double[] actual=MissionMomentum(new[]{a,b});double[] expected={5,5e8+.375,-1e9-.75,1.5e9+1.5,10.2,11.525,16.4625};double residual=0;
        for(int i=0;i<expected.Length;i++)residual=Math.Max(residual,Math.Abs(actual[i]-expected[i]));Need(residual<=1e-12,"Independent two-region reduced-mass angular momentum oracle differs.");
        Vec shift=new(-1e9,2e9,-3e9),boost=velocity*-1;
        double[] moved=MissionMomentum(new[]{new MissionMomentumRegion(a.Mass,a.Root+shift,a.LocalCenter,a.Velocity+boost,a.ChassisMomentum),new MissionMomentumRegion(b.Mass,b.Root+shift,b.LocalCenter,b.Velocity+boost,b.ChassisMomentum)});
        for(int i=4;i<7;i++)Need(BitConverter.DoubleToInt64Bits(moved[i])==BitConverter.DoubleToInt64Bits(actual[i]),"Chassis angular momentum oracle depends on common translation/bulk velocity.");
        return new MissionCheck{name="compensated-relative-momentum-oracle-regressions",passed=true,maximumResidual=residual,before=expected,after=actual,scope="Independent two-region analytic reduced-mass cross product, exact single-region chassis H including signed zero, and common translation/velocity invariance. No simulation or production topology calculator is called."};
    }
    static void MissionConserved(MissionReceipt receipt,string name,double[] before,double[] after)
    {
        var check=new MissionCheck{name=name,before=before,after=after,scope="Independent compensated sum in first-region COM/velocity reference of mass, world linear momentum and chassis angular momentum about shared COM across one same-time topology transaction; common orbital bulk velocity cancels analytically. Relative rounding bound remains 1e-10, no rotor-total/trajectory/stock parity."};receipt.checks.Add(check);
        for(int i=0;i<before.Length;i++){double error=Math.Abs(before[i]-after[i])/Math.Max(1,Math.Max(Math.Abs(before[i]),Math.Abs(after[i])));check.maximumResidual=Math.Max(check.maximumResidual,error);}
        check.passed=check.maximumResidual<=1e-10;Need(check.passed,"Mission topology changed independently summed mass/momentum.");
    }
    static void MissionWriteBytes(string path,byte[] bytes)
    {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(bytes);}
    static OwnedFlightWorkerContext MissionInitialize(Inputs input,MissionSituationManifest manifest,string method,string deadlineStrategy)
    {
        var c=Initialize(input);var body=c.Session.Context;Vec radial=body.SunDirection*(1/Magnitude(body.SunDirection));
        Vec normal=Magnitude(body.AngularVelocity)>0?body.AngularVelocity:new Vec(0,0,1);Vec tangent=Vec.Cross(normal,radial);
        if(Magnitude(tangent)<=1e-12)tangent=Vec.Cross(new Vec(0,1,0),radial);if(Magnitude(tangent)<=1e-12)tangent=Vec.Cross(new Vec(1,0,0),radial);tangent*=1/Magnitude(tangent);
        if(manifest.id=="orbital")c.Session.PlaceCircular(c.Host.World.Stamp,Math.Max(manifest.altitudeMeters,body.AtmosphereDepth+10000),radial,Vec.Cross(radial,tangent));
        else
        {
            Need(body.AtmosphereDepth>0,"Reentry situation requires an actual captured atmosphere.");double altitude=Math.Min(manifest.altitudeMeters,body.AtmosphereDepth*.4);
            Vec center=radial*(body.Radius+altitude);c.Session.Place(c.Host.World.Stamp,center,Vec.Cross(body.AngularVelocity,center)+tangent*manifest.airSpeedMetersPerSecond+radial*-manifest.descentMetersPerSecond);
            body.Atmosphere(altitude,out double density,out _);Need(density>0,"Authored reentry point has no positive captured atmosphere density.");
        }
        for(int i=0;i<c.Host.Systems.ActuatorCount;i++)c.Host.Systems.SetEnabled(i,true);
        c.Host.Systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);c.Host.Systems.CompileExecutionPlan(c.Host.World.Stamp);
        c.Host.Systems.SetMechanicalPolicy(c.Host.World.Stamp,new OwnedRuntimeMechanicalPolicy(method!="fixed",minimumInterval:manifest.minimumMechanicalIntervalSeconds,method:method=="embedded"?OwnedRuntimeMechanicalMethod.EmbeddedMidpoint:OwnedRuntimeMechanicalMethod.StepDoubling));
        c.Host.Systems.SetDeadlineStrategy(c.Host.World.Stamp,deadlineStrategy=="retained"?OwnedRuntimeDeadlineStrategy.Retained:OwnedRuntimeDeadlineStrategy.ExactOracle);
        c.Host.Systems.SetWorkObservation(true);c.Host.World.SetWorkObservation(c.Host.World.Stamp,true);return c;
    }
    static void MissionQueue(OwnedFlightWorker worker,MissionSituationManifest manifest)
    {Await(worker.Invoke(c=>{foreach(var cue in manifest.controls)c.Timeline.EnqueueEvent(new OwnedFlightEvent(cue.id,cue.sequence,cue.runtimeSeconds,OwnedFlightEventKind.Controls,controls:cue.Controls()));return true;},OwnedFlightWorkerInvocation.TimelineMutation));}
    static void MissionCheckpointContinuation(OwnedFlightWorker worker,MissionReceipt receipt,MissionBudget budget,string output,Inputs input)
    {
        MissionMeasure(worker,receipt,budget,"pending-execution-checkpoint",row=>
        {
            var quiet=new OwnedRuntimeControls();double previousWall=budget.Wall;budget.Wall=.125;
            Await(worker.Invoke(c=>{c.Configure(quiet,4,false,wallSeconds:previousWall);c.Timeline.Pump(budget.Wall,0,double.PositiveInfinity);c.Configure(c.Timeline.HeldControls,4,true,wallSeconds:budget.Wall);return true;},OwnedFlightWorkerInvocation.TimelineMutation));
            byte[] saved=Await(worker.Invoke(MissionCheckpoint));var packet=OwnedRuntimeCheckpointBinary.DecodeFlight(saved);
            Need(packet.timeline!=null&&packet.timeline.requestedRuntimeTime>packet.host.state.time&&packet.timeline.events.pending.Length>0&&packet.host.state.thermal.pendingDuration>0,"Checkpoint must retain actual simulation debt, future controls and pending heat.");
            receipt.pendingCheckpoint=Path.Combine(output,"orbital-pending.ofcb");receipt.pendingSha256=Hash(saved);MissionWriteBytes(receipt.pendingCheckpoint,saved);
        });
        byte[] pending=File.ReadAllBytes(receipt.pendingCheckpoint),baseline=Array.Empty<byte>();double endpoint=OwnedRuntimeCheckpointBinary.DecodeFlight(pending).timeline.requestedRuntimeTime;
        MissionMeasure(worker,receipt,budget,"checkpoint-reference-continuation",row=>{var controls=Await(worker.Invoke(c=>c.Timeline.HeldControls));Await(worker.Configure(controls,4,false,wallSeconds:budget.Wall));MissionConsume(worker,budget,row,endpoint);baseline=Await(worker.Invoke(MissionCheckpoint));});
        MissionMeasure(worker,receipt,budget,"restore-and-exact-continuation",row=>
        {
            row.workCountersRebound=true;
            Await(worker.Invoke(c=>{var state=(State)c.State;var restored=OwnedFlightCheckpoint.Restore(c.Host,state.Imported,OwnedRuntimeCheckpointBinary.DecodeFlight(pending),input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256,"");c.Replace(restored.Host,restored.Session,new State{Imported=restored.Imported},restored.TimelineState);return true;},OwnedFlightWorkerInvocation.CheckpointReplacement));
            Need(Await(worker.Invoke(MissionCheckpoint)).SequenceEqual(pending),"Restored queued demand/thermal/controller checkpoint differs.");
            var controls=Await(worker.Invoke(c=>c.Timeline.HeldControls));Await(worker.Configure(controls,4,false,wallSeconds:budget.Wall));MissionConsume(worker,budget,row,endpoint);
            bool equal=Await(worker.Invoke(MissionCheckpoint)).SequenceEqual(baseline);receipt.checks.Add(new MissionCheck{name="complete-execution-checkpoint-continuation",passed=equal,scope="All binary fields including bank/history/resources/providers/thermal/mechanical/flight/pilot/controls/event queue and demand policy match after the same future continuation."});Need(equal,"Restored complete mission continuation differs.");
        });
    }
    static void MissionImpulse(OwnedFlightWorker worker,MissionReceipt receipt,MissionBudget budget)
    {
        MissionMeasure(worker,receipt,budget,"exact-maneuver-impulse-order",row=>
        {
            var before=Await(worker.Invoke(c=>{using var read=c.Host.World.AcquireRead();var region=read.GetRegion(0);return Tuple.Create(region.Id,region.Properties.Mass,region.Motion.Velocity,region.Motion.AngularMomentum,c.Host.World.Stamp.Time);}));
            Vec impulse=new Vec(0,before.Item2*.25,0);
            Await(worker.Invoke(c=>{c.Timeline.EnqueueEvent(new OwnedFlightEvent("center-impulse",6,before.Item5,OwnedFlightEventKind.Impulse,before.Item1,impulse));c.Timeline.EnqueueEvent(new OwnedFlightEvent("post-impulse-controls",7,before.Item5,OwnedFlightEventKind.Controls,controls:new OwnedRuntimeControls()));c.Configure(c.Timeline.HeldControls,4,false,wallSeconds:budget.Wall);var p=c.Timeline.Pump(budget.Wall,2,double.PositiveInfinity);row.eventsApplied+=p.EventApplications;Need(p.EventApplications==2&&p.LocalCommits==0&&p.CoastCommits==0,"Same-frontier impulse/control sequence performed a physical fold or missed an event.");c.Configure(c.Timeline.HeldControls,4,true,wallSeconds:budget.Wall);return true;},OwnedFlightWorkerInvocation.TimelineMutation));
            var after=Await(worker.Invoke(c=>{using var read=c.Host.World.AcquireRead();return read.GetRegion(0).Motion;}));Vec expected=before.Item3+impulse*(1/before.Item2);double residual=Magnitude(after.Velocity+expected*-1);
            bool passed=residual==0&&after.AngularMomentum.X==before.Item4.X&&after.AngularMomentum.Y==before.Item4.Y&&after.AngularMomentum.Z==before.Item4.Z&&worker.ReadStatus().Progress.CommittedRuntimeTime==before.Item5;
            receipt.checks.Add(new MissionCheck{name="center-impulse-analytic",passed=passed,maximumResidual=residual,before=Vector(before.Item3),after=Vector(after.Velocity),scope="No simulation interval. Independent P+=impulse, unchanged center angular momentum and physical time; exact same-time impulse precedes controls."});Need(passed,"Center impulse changed an unrelated motion quantity.");
        });
    }
    static void MissionTopology(OwnedFlightWorker worker,MissionReceipt receipt,MissionBudget budget)
    {
        MissionMeasure(worker,receipt,budget,"flush-split-rejoin",row=>
        {
            Await(worker.Invoke(c=>{c.Host.Systems.FlushThermal(c.Host.World.Stamp);return true;},OwnedFlightWorkerInvocation.ColdMutation));
            var original=Await(worker.Invoke(c=>c.Host.World.CaptureSnapshot()));var candidates=original.Connections.Where(a=>a.Locked&&original.Definition.GetPart(original.Definition.PartSlot(a.PartA)).BodyId!=original.Definition.GetPart(original.Definition.PartSlot(a.PartB)).BodyId).ToArray();
            Need(candidates.Length>0,"No actual acquired inter-body connection is available for split.");var link=candidates[0];long nextConnection=checked(original.Connections.Max(a=>a.Id)+1);double[] before=Await(worker.Invoke(MissionMomentum));
            Await(worker.Invoke(c=>{c.Host.World.Split(c.Host.World.Stamp,link.Id);return true;},OwnedFlightWorkerInvocation.ColdMutation));Need(Await(worker.Invoke(c=>c.Host.World.RegionCount))>1,"Acquired connection did not produce multiple physical regions.");
            MissionConserved(receipt,"split-mass-momentum",before,Await(worker.Invoke(MissionMomentum)));double[] split=Await(worker.Invoke(MissionMomentum));
            Await(worker.Invoke(c=>{c.Host.World.Dock(c.Host.World.Stamp,nextConnection,link.PartA,link.PartB);return true;},OwnedFlightWorkerInvocation.ColdMutation));Need(Await(worker.Invoke(c=>c.Host.World.RegionCount))==1,"Rejoined captured assembly has multiple regions.");MissionConserved(receipt,"join-mass-momentum",split,Await(worker.Invoke(MissionMomentum)));
            var joined=Await(worker.Invoke(c=>c.Host.World.CaptureSnapshot()));bool identity=original.Definition.PartCount==joined.Definition.PartCount&&original.Definition.BodyCount==joined.Definition.BodyCount&&Enumerable.Range(0,original.Definition.PartCount).All(i=>original.Definition.GetPart(i).Id==joined.Definition.GetPart(i).Id)&&Enumerable.Range(0,original.Definition.BodyCount).All(i=>original.Definition.GetBody(i).Id==joined.Definition.GetBody(i).Id)&&original.ResourceQuantity.SequenceEqual(joined.ResourceQuantity)&&original.ProviderState.SequenceEqual(joined.ProviderState)&&original.Stamp.Time==joined.Stamp.Time;
            receipt.checks.Add(new MissionCheck{name="topology-authored-identities-and-scalar-state",passed=identity,scope="Same authored part/body IDs, quantities, provider state and time across cold split/join; no collision/docking-port or decoupler behavior claim."});Need(identity,"Topology transaction changed unrelated acquired identity/scalar state.");
        });
    }
    static MissionReceipt MissionRun(Inputs input,string output,MissionSituationManifest manifest,string method,string deadlineStrategy,int commits,double seconds)
    {
        var receipt=new MissionReceipt{situation=manifest,inputs=input.Pins,method=method,deadlineStrategy=deadlineStrategy,toolSha256=HashFile(Assembly.GetExecutingAssembly().Location),coreSha256=HashFile(typeof(OwnedFlightWorker).Assembly.Location)};long begin=Stopwatch.GetTimestamp();var budget=new MissionBudget{Commits=commits,Seconds=seconds};OwnedFlightWorker? worker=null;
        try
        {
            receipt.checks.Add(MissionMomentumRegression());
            worker=new OwnedFlightWorker(()=>MissionInitialize(input,manifest,method,deadlineStrategy),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0,MaximumCommitsPerPump=commits,WallBudgetSeconds=seconds});Need(worker.WaitUntilReady(60000),worker.ReadStatus().Fault??"Mission worker initialization did not complete.");
            Await(worker.Invoke(c=>{using var read=c.Host.World.AcquireRead();receipt.parts=read.PartCount;receipt.bodies=read.BodyCount;receipt.surfaces=c.Host.Systems.SurfaceCount;receipt.frameId=read.Stamp.FrameId;receipt.sourceEpochSeconds=c.Session.Context.Epoch;receipt.startUniversalTime=c.Session.UniversalTime;receipt.contextSha256=c.Session.Context.SourceSha256;receipt.scheduleSha256=c.Host.ScheduleSourceSha256;receipt.codeGeneration=c.Host.CodeGeneration;receipt.authoredEnabledActuatorSlots=Enumerable.Range(0,c.Host.Systems.ActuatorCount).ToArray();for(int i=0;i<c.Host.Systems.ActuatorCount;i++){switch(c.Host.Systems.GetActuator(i).Kind){case OwnedRuntimeActuatorKind.Engine:receipt.engines++;break;case OwnedRuntimeActuatorKind.Rcs:receipt.rcs++;break;case OwnedRuntimeActuatorKind.ReactionWheel:receipt.wheels++;break;}}return true;}));
            Need(receipt.wheels>0&&receipt.surfaces>0,"Situation requires actual acquired wheel and aero rows.");if(manifest.id=="orbital")Need(receipt.engines>0&&receipt.rcs>0,"Orbital script requires actual acquired engines and RCS.");
            MissionQueue(worker,manifest);
            if(manifest.id=="orbital")
            {
                MissionMeasure(worker,receipt,budget,"quiet-local-pending-heat",row=>{var p=Await(worker.Step(.015625));row.localCommits+=p.LocalCommits;Need(p.Fault==null&&p.LocalCommits==1,p.Fault??"Initial quiet interval did not commit.");});
                MissionCheckpointContinuation(worker,receipt,budget,output,input);
                MissionMeasure(worker,receipt,budget,"4x-burn-cutoff-reversal",row=>MissionDemandTo(worker,budget,row,.75,4));MissionImpulse(worker,receipt,budget);MissionTopology(worker,receipt,budget);
                MissionMeasure(worker,receipt,budget,"4x-post-topology",row=>MissionDemandTo(worker,budget,row,.875,4));MissionMeasure(worker,receipt,budget,"32x-demand-no-presentation",row=>MissionDemandTo(worker,budget,row,manifest.durationSeconds,32));
            }
            else MissionMeasure(worker,receipt,budget,"reentry-rotating-flow-and-reversals",row=>MissionDemandTo(worker,budget,row,manifest.durationSeconds,4));
            var final=Await(worker.Invoke(c=>{byte[] bytes=MissionCheckpoint(c);var packet=OwnedRuntimeCheckpointBinary.DecodeFlight(bytes);receipt.mechanical=packet.host.state.mechanical;receipt.thermal=packet.host.state.thermal;receipt.events=packet.timeline.events.history;receipt.eventUniversalTimes=receipt.events.Select(e=>c.Session.Context.Epoch+(e.time-c.Session.RuntimeEpoch)).ToArray();receipt.endUniversalTime=c.Session.UniversalTime;receipt.systems=c.Host.Systems.Counters.Snapshot();receipt.minimumAltitudeMeters=double.PositiveInfinity;using var read=c.Host.World.AcquireRead();for(int r=0;r<read.RegionCount;r++){var env=c.Session.EvaluateRegion(r);receipt.minimumAltitudeMeters=Math.Min(receipt.minimumAltitudeMeters,c.Session.Context.Altitude(c.Session.CenterPosition(r)));receipt.maximumDensity=Math.Max(receipt.maximumDensity,env.Density);}for(int p=0;p<read.PartCount;p++)receipt.maximumTemperatureKelvin=Math.Max(receipt.maximumTemperatureKelvin,read.GetTemperature(p));return bytes;}));
            var expected=manifest.controls.Select(c=>Tuple.Create(c.id,c.sequence,c.runtimeSeconds)).Concat(manifest.id=="orbital"?new[]{Tuple.Create("center-impulse",6L,.75),Tuple.Create("post-impulse-controls",7L,.75)}:Array.Empty<Tuple<string,long,double>>()).ToArray();
            bool events=receipt.events.Length==expected.Length&&receipt.events.Zip(expected,(a,b)=>a.id==b.Item1&&a.sequence==b.Item2&&a.time==b.Item3).All(a=>a);receipt.checks.Add(new MissionCheck{name="exact-authored-event-order",passed=events,scope="Every authored relative-runtime event appears once, at its exact time, in sequence order; UTC values bind the captured central-body epoch."});Need(events,"Mission event order/time/coverage differs.");
            Need(receipt.thermal!=null&&receipt.thermal.advances>0&&receipt.thermal.deferredSteps>0,"Independent thermal clock did not both defer and advance.");Need(receipt.systems!=null,"Mission system counters absent.");
            Need(receipt.mechanical!=null&&(!receipt.mechanical.adaptive||receipt.mechanical.accepted>0&&receipt.mechanical.lastError<=1),"Final accepted mechanical interval did not satisfy the unchanged local error contract.");
            if(manifest.id=="orbital")Need(receipt.systems!.PropellantKilograms>0&&receipt.systems.ElectricUnits>0,"Actual burn/control script did not consume captured resources.");else Need(receipt.maximumDensity>0&&receipt.systems!.SurfaceRows>0&&receipt.minimumAltitudeMeters>0,"Reentry lacked positive atmosphere/surface work or left airborne scope.");
            receipt.propellantKilograms=receipt.systems!.PropellantKilograms;receipt.electricUnits=receipt.systems.ElectricUnits;receipt.finalCheckpoint=Path.Combine(output,manifest.id+"-final.ofcb");receipt.finalSha256=Hash(final);MissionWriteBytes(receipt.finalCheckpoint,final);receipt.worker=worker.ReadStatus();receipt.finalRuntimeTime=receipt.worker.Progress.CommittedRuntimeTime;receipt.finalDebtSeconds=receipt.worker.Progress.DebtSeconds;
            Need(receipt.finalRuntimeTime==manifest.durationSeconds&&receipt.finalDebtSeconds==0&&receipt.worker.Fault==null&&receipt.worker.Progress.Fault==null,"Mission final physical frontier/debt/fault differs.");Need(input.Pins.All(pin=>HashFile(pin.path)==pin.sha256),"Mission source changed.");receipt.status="passed";
        }
        catch(Exception error){receipt.failure=error.ToString();if(worker!=null)receipt.worker=worker.ReadStatus();}
        finally
        {
            try
            {
                if(worker!=null){var prior=worker.ReadStatus();worker.Dispose();receipt.worker=worker.ReadStatus();Need(receipt.worker.Stopped,"Mission worker did not join.");if(receipt.status=="passed")Need(receipt.worker.Progress.Stamp.Matches(prior.Progress.Stamp)&&receipt.worker.Progress.CommittedRuntimeTime==manifest.durationSeconds&&receipt.worker.Progress.DebtSeconds==0&&receipt.worker.Fault==null&&receipt.worker.Progress.Fault==null,"Joined mission worker changed its exact final stamp/clock/debt/fault frontier.");}
            }
            catch(Exception error){receipt.status="failed";receipt.failure+="\nShutdown: "+error;}
            receipt.completeTicks=Stopwatch.GetTimestamp()-begin;using var file=new FileStream(Path.Combine(output,manifest.id+"-mission.json"),FileMode.CreateNew,FileAccess.Write);JsonSerializer.Serialize(file,receipt,Json);
        }
        Need(receipt.status=="passed",receipt.failure);return receipt;
    }
    static object MissionSituations(Inputs input,string output,string selector="all",string method="embedded",int maximumCommitsPerPump=32,double wallBudgetSeconds=.004,string deadlineStrategy="retained")
    {
        Need(method=="fixed"||method=="trials"||method=="embedded","Mission mechanical method must be fixed, trials or embedded.");Need(deadlineStrategy=="oracle"||deadlineStrategy=="retained","Mission deadline strategy must be oracle or retained.");Need(maximumCommitsPerPump>=1&&maximumCommitsPerPump<=65536&&double.IsFinite(wallBudgetSeconds)&&wallBudgetSeconds>0,"Mission work bounds invalid.");
        var manifests=MissionSituationCatalog.Create(selector);using(var file=new FileStream(Path.Combine(output,"mission-situations.json"),FileMode.CreateNew,FileAccess.Write))JsonSerializer.Serialize(file,manifests,Json);
        var receipts=new List<MissionReceipt>();foreach(var manifest in manifests)receipts.Add(MissionRun(input,output,manifest,method,deadlineStrategy,maximumCommitsPerPump,wallBudgetSeconds));
        return new{schema="continuum-mission-situations/v1",status="passed",selector,method,deadlineStrategy,maximumCommitsPerPump,wallBudgetSeconds,receipts,scope="Short acquired-craft situations exercise real worker/timeline/physical transactions and exact event boundaries. Warp multiplies requested progress, never the mechanical interval. No presentation callbacks occur. Receipt phase clocks include bounded work attempts/dispatch and preserve debt; cold continuation checks are separate from mission advance costs. This is a functional/complete-work artifact, not a whole mission or FPS benchmark."};
    }
}
