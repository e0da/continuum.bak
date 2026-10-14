using System;
using System.Diagnostics;
using System.Threading;

namespace KspContinuum
{
    [Serializable] public struct OwnedFlightTimelineProgress
    {
        public OwnedRuntimeStamp Stamp;
        public string SourceId, ContextSha256, ScheduleSha256, Status, Fault, CoastRejection;
        public long InputRevision, CodeGeneration, ConfigurationRevision, TotalLocalCommits, TotalCoastCommits, ControlResolutions;
        public long SupersededPendingControlSegments;
        public long TotalEventsApplied;
        public double LastLiveInputRuntimeTime;
        public int LocalCommits, CoastCommits, PendingInputSegments;
        public int PendingEvents,EventApplications;
        public double WallSeconds, RequestedRuntimeTime, CommittedRuntimeTime, DebtSeconds, UniversalTime, Warp;
        public bool Paused, DeadlineReached, BudgetExhausted;
        public long WorkTicks, ClockFrequency;
    }

    // This owner schedules the existing physical transaction. Presentation only reads committed banks.
    public sealed class OwnedFlightTimeline
    {
        public const int MaximumInputSegments = 128;
        const double AdvisoryMinimumSliceSeconds=1e-4;
        struct Policy
        {
            public OwnedRuntimeControls Controls;
            public bool AllowCoast, PassiveFrozen;
            public Func<OwnedRuntimeControls, OwnedRuntimeControls> Resolver;
        }
        struct Segment { public double End; public Policy Input; public bool Closed; }
        readonly Segment[] segments = new Segment[MaximumInputSegments];
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        int head, count;
        bool pumping;
        Policy policy;
        OwnedRuntimeStamp expected;
        long codeGeneration, configurationRevision;
        string scheduleSha;
        double wall, requested, requestAnchor, wallAnchor, deadline = double.PositiveInfinity, coastLead;
        Func<OwnedRuntimeControls, OwnedRuntimeControls> resolver;
        OwnedFlightEventQueue events;
        public readonly OwnedRuntimeHost Host;
        public readonly OwnedFlightSession Session;
        public readonly string SourceId;
        public readonly double MaximumLocalStepSeconds;
        public long InputRevision { get; private set; }
        public long TotalLocalCommits { get; private set; }
        public long TotalCoastCommits { get; private set; }
        public long ControlResolutions { get; private set; }
        public long SupersededPendingControlSegments { get; private set; }
        public long TotalEventsApplied { get; private set; }
        public double LastLiveInputRuntimeTime { get; private set; }
        public double RequestedRuntimeTime { get { return requested; } }
        public double CommittedRuntimeTime { get { return expected.Time; } }
        public double DebtSeconds { get { return Math.Max(0, requested - expected.Time); } }
        public bool Paused { get; private set; }
        public double Warp { get; private set; }
        public string Status { get; private set; }
        public string Fault { get; private set; }
        public int PendingEvents {get{return events.Count;}}
        public double NextEventRuntimeTime {get{return events.NextTime;}}
        public OwnedRuntimeControls HeldControls {get{return policy.Controls;}}
        public bool AllowPassiveCoast {get{return policy.AllowCoast;}}
        public bool PassiveStateFrozen {get{return policy.PassiveFrozen;}}
        public double NextDeadlineUniversalTime {get{return deadline;}}
        public double CoastGuardLeadSeconds {get{return coastLead;}}
        public double NextLocalIntervalSeconds {get{return Host.World.AdaptiveMechanicalEnabled?Math.Min(Host.World.SuggestedMechanicalInterval,Host.World.MaximumMechanicalInterval):MaximumLocalStepSeconds;}}

        public OwnedFlightTimeline(OwnedRuntimeHost host, OwnedFlightSession session, double wallNowSeconds,
            double maximumLocalStepSeconds = .02, string sourceId = "operator")
        {
            if (host == null || session == null || !ReferenceEquals(host.World, session.Runtime)) throw new ArgumentException("Timeline host/session differ.");
            Finite(wallNowSeconds); Positive(maximumLocalStepSeconds);
            if (maximumLocalStepSeconds > .1 || string.IsNullOrEmpty(sourceId) || sourceId.Length > 128) throw new ArgumentException("Timeline step/source is outside bounds.");
            Host = host; Session = session; SourceId = sourceId; MaximumLocalStepSeconds = maximumLocalStepSeconds;
            wall = wallAnchor = wallNowSeconds; expected = host.World.Stamp; requested = requestAnchor = expected.Time;
            double admittedClock = session.UniversalTime; Finite(admittedClock);
            BindCode(); Paused = true; Warp = 1; Status = "paused";
            events=new OwnedFlightEventQueue(host,sourceId);
        }

        public void EnqueueEvent(OwnedFlightEvent value)
        {Enter();CheckSource();events.Enqueue(value);}
        bool EventDue {get{return events.Count>0&&events.NextTime==expected.Time;}}
        void ApplyDueEvent()
        {
            if(TotalEventsApplied==long.MaxValue)throw new OverflowException("Event application counter exhausted.");
            var result=events.ApplyNext(expected);expected=result.After;
            if(result.Kind==OwnedFlightEventKind.Controls)ReplaceEventControls(result.Controls);
            BindCode();TotalEventsApplied++;Status=Paused?"paused":"owed";
        }
        void ReplaceEventControls(OwnedRuntimeControls controls)
        {
            if(count==0){policy.Controls=controls;return;}
            var segment=segments[head];segment.Input.Controls=controls;segments[head]=segment;
            if(!segment.Closed)policy.Controls=controls;
        }

        public Func<OwnedRuntimeControls, OwnedRuntimeControls> ControlResolver
        {
            get { return resolver; }
            set
            {
                Enter(); CheckSource();
                if (!Paused || DebtSeconds != 0) throw new InvalidOperationException("Resolver changes require a paused debt-free boundary.");
                resolver = value; policy.Resolver = value; InputRevision = checked(InputRevision + 1);
            }
        }

        public void Configure(OwnedRuntimeStamp sourceStamp, long expectedRevision, double wallNowSeconds,
            OwnedRuntimeControls controls, double warp, bool paused, bool allowPassiveCoast = false,
            bool passiveStateFrozen = false, double nextDeadlineUniversalTime = double.PositiveInfinity, double coastGuardLeadSeconds = 0)
        {
            Enter(); CheckSource(); controls.Validate(); Positive(warp); Nonnegative(coastGuardLeadSeconds);
            if (warp > 1000000 || !sourceStamp.Matches(expected) || expectedRevision != InputRevision)
                throw new ArgumentException("Timeline input source/revision/warp differs.");
            if (double.IsNaN(nextDeadlineUniversalTime) || nextDeadlineUniversalTime == double.NegativeInfinity || nextDeadlineUniversalTime < Session.UniversalTime)
                throw new ArgumentException("Deadline is invalid or already behind committed state.");
            ValidateWall(wallNowSeconds);
            long revision = checked(InputRevision + 1);
            Accrue(wallNowSeconds);
            var nextPolicy = new Policy { Controls = controls, AllowCoast = allowPassiveCoast, PassiveFrozen = passiveStateFrozen, Resolver = resolver };
            if (!Same(policy, nextPolicy) && count > 0)
            { int tail = (head + count - 1) % segments.Length; Segment last = segments[tail]; last.Closed = true; segments[tail] = last; }
            policy = nextPolicy;
            if (warp != Warp || paused != Paused) { wallAnchor = wallNowSeconds; requestAnchor = requested; }
            Warp = warp; Paused = paused; deadline = nextDeadlineUniversalTime; coastLead = coastGuardLeadSeconds; InputRevision = revision;
            Status = paused ? "paused" : DebtSeconds > 0 ? "owed" : "caught-up";
        }

        public void SetLiveControls(OwnedRuntimeStamp sourceStamp, long expectedRevision, double wallNowSeconds, OwnedRuntimeControls controls)
        {
            Enter(); CheckSource(); controls.Validate(); ValidateWall(wallNowSeconds);
            if (!sourceStamp.Matches(expected) || expectedRevision != InputRevision)
                throw new ArgumentException("Live input source/revision differs.");
            long revision = checked(InputRevision + 1);
            if (SupersededPendingControlSegments > long.MaxValue - count - 1)
                throw new OverflowException("Live input supersession counter exhausted.");
            Accrue(wallNowSeconds);
            int replaced = 0;
            for (int i = 0; i < count; i++)
            {
                int slot = (head + i) % segments.Length; Segment segment = segments[slot];
                if (!SameControls(segment.Input.Controls, controls)) replaced++;
                segment.Input.Controls = controls; segments[slot] = segment;
            }
            policy.Controls = controls;
            SupersededPendingControlSegments += replaced;
            LastLiveInputRuntimeTime = expected.Time; InputRevision = revision;
            Status = Paused ? "paused" : DebtSeconds > 0 ? "owed" : "caught-up";
        }

        public OwnedFlightTimelineProgress Pump(double wallNowSeconds, int maximumCommits, double wallBudgetSeconds = double.PositiveInfinity)
        {
            Enter(); ValidateBudget(maximumCommits, wallBudgetSeconds); ValidateWall(wallNowSeconds);
            long begin = Stopwatch.GetTimestamp(),eventsBefore=TotalEventsApplied; int local = 0, coast = 0; bool budget = false, reached = false;
            pumping = true;
            try
            {
                CheckSource(); Accrue(wallNowSeconds);
                if (Paused) { Status = "paused"; return Progress(local, coast, false, false, begin); }
                while (DebtSeconds > 0||EventDue)
                {
                    if (local + coast + TotalEventsApplied-eventsBefore >= maximumCommits || Elapsed(begin) >= wallBudgetSeconds) { budget = true; Status = "budget-exhausted"; break; }
                    if(EventDue){ApplyDueEvent();continue;}
                    bool wasCoast, waitingQuantum;
                    if (!CommitNext(double.PositiveInfinity, true, out wasCoast, out waitingQuantum))
                    { reached = !waitingQuantum; Status = waitingQuantum ? "awaiting-local-quantum" : "deadline"; break; }
                    if (wasCoast) coast++; else local++;
                }
                if (DebtSeconds == 0) Status = "caught-up";
            }
            catch (OwnedRuntimeReadPressureException) { Status = "blocked-readers"; }
            catch (Exception error) { StopFault(error); }
            finally { pumping = false; }
            return Progress(local, coast, reached, budget, begin,checked((int)(TotalEventsApplied-eventsBefore)));
        }

        public OwnedFlightTimelineProgress Step(double deltaSeconds)
        {
            Enter(); CheckSource(); Positive(deltaSeconds);
            if (!Paused || deltaSeconds > MaximumLocalStepSeconds) throw new InvalidOperationException("Manual step requires pause and the same local-step bound.");
            long begin = Stopwatch.GetTimestamp(),eventsBefore=TotalEventsApplied; bool wasCoast, waitingQuantum;
            pumping = true;
            try
            {
                while(EventDue)ApplyDueEvent();
                if (DebtSeconds == 0) Grant(expected.Time + deltaSeconds, policy);
                if (!CommitNext(deltaSeconds, false, out wasCoast, out waitingQuantum)) { Status = "deadline"; return Progress(0, 0, true, false, begin); }
                while(EventDue)ApplyDueEvent();
                Status = "paused"; return Progress(1, 0, false, false, begin,checked((int)(TotalEventsApplied-eventsBefore)));
            }
            catch (OwnedRuntimeReadPressureException) { Status = "blocked-readers"; return Progress(0, 0, false, false, begin); }
            catch (Exception error) { StopFault(error); return Progress(0, 0, false, false, begin); }
            finally { pumping = false; }
        }

        public OwnedFlightTimelineProgress CoastTo(double targetUniversalTime)
        {
            Enter(); CheckSource(); Finite(targetUniversalTime);
            if (!Paused || DebtSeconds != 0 || events.Count!=0 || policy.Resolver != null || !policy.AllowCoast || !Host.SupportsPassiveKeplerCoast)
                throw new InvalidOperationException("Manual coast requires paused debt-free explicit passive admission and no feedback resolver.");
            long begin = Stopwatch.GetTimestamp(); string reason; OwnedRuntimeMotion[] motions; double time;
            if (!Session.PrepareCoast(expected, targetUniversalTime, policy.Controls, policy.PassiveFrozen, deadline, coastLead, out motions, out time, out reason) || Universal(time) > deadline)
            { LastCoastRejection = reason ?? "Prepared coast crossed the representable deadline."; Status = "coast-rejected"; return Progress(0, 0, false, false, begin); }
            var before=expected;Session.PublishCoast(expected, motions, time);events.AcknowledgeCoastAdvance(before,Host.World.Stamp);
            expected = Host.World.Stamp; requested = expected.Time; TotalCoastCommits = checked(TotalCoastCommits + 1);
            Status = "paused"; return Progress(0, 1, false, false, begin);
        }

        // Explicit cold mutation acknowledgement cannot erase unfinished requested time.
        public void Synchronize(OwnedRuntimeStamp previousStamp, double wallNowSeconds, string reason)
        {
            Enter(); ValidateWall(wallNowSeconds);
            if (!Paused || DebtSeconds != 0 || events.Count!=0 || !previousStamp.Matches(expected) || string.IsNullOrEmpty(reason))
                throw new InvalidOperationException("Cold synchronization requires paused debt-free source-bound ownership.");
            var current = Host.World.Stamp;
            if (current.Time < expected.Time) throw new ArgumentException("Synchronization cannot move simulation time backwards.");
            double admittedClock = Session.UniversalTime; Finite(admittedClock);
            if (admittedClock > deadline) throw new ArgumentException("Cold mutation crossed the retained event deadline.");
            expected = current; requested = requestAnchor = current.Time; wall = wallAnchor = wallNowSeconds; head = count = 0;
            BindCode(); Fault = null; Status = "paused"; InputRevision = checked(InputRevision + 1);
            events.AcknowledgeBoundary(expected);
        }

        public void CompleteEvent(OwnedRuntimeStamp previousStamp, double wallNowSeconds, string reason, double nextDeadlineUniversalTime)
        {
            Enter(); ValidateWall(wallNowSeconds);
            if (!Paused || events.Count!=0 || !previousStamp.Matches(expected) || string.IsNullOrEmpty(reason) ||
                double.IsNaN(nextDeadlineUniversalTime) || nextDeadlineUniversalTime <= Session.UniversalTime)
                throw new InvalidOperationException("Event acknowledgement requires a paused current source and future deadline.");
            var current = Host.World.Stamp;
            if (current.Time != expected.Time || Universal(current.Time) > deadline)
                throw new InvalidOperationException("Event mutation must keep the stopped physical time and not cross its barrier.");
            expected = current; wall = wallNowSeconds; deadline = nextDeadlineUniversalTime;
            BindCode(); Fault = null; Status = "paused"; InputRevision = checked(InputRevision + 1);
            events.AcknowledgeBoundary(expected);
        }

        public string LastCoastRejection { get; private set; }
        public OwnedFlightCoastWindow LastCoastWindow {get;private set;}
        public OwnedFlightLocalWindow LastLocalWindow {get;private set;}
        public OwnedFlightTimelineState CaptureState()
        {
            Enter();
            if(!Host.World.Stamp.Matches(expected)||Host.CodeGeneration!=codeGeneration||Host.Systems.ConfigurationRevision!=configurationRevision||Host.ScheduleSourceSha256!=scheduleSha)
                throw new InvalidOperationException("Execution checkpoint source is not acknowledged.");
            if(resolver!=null)throw new InvalidOperationException("Execution checkpoint cannot serialize an arbitrary feedback callback.");
            var rows=new OwnedFlightTimelineSegmentState[count];
            for(int i=0;i<count;i++)
            {var s=segments[(head+i)%segments.Length];if(s.Input.Resolver!=null)throw new InvalidOperationException("Pending feedback policy has no execution codec.");rows[i]=new OwnedFlightTimelineSegmentState{end=s.End,closed=s.Closed,allowCoast=s.Input.AllowCoast,passiveFrozen=s.Input.PassiveFrozen,controls=OwnedFlightTimelineState.EncodeControls(s.Input.Controls)};}
            return new OwnedFlightTimelineState {sourceId=SourceId,contextSha256=Session.Context.SourceSha256,scheduleSha256=scheduleSha,frameId=expected.FrameId,
                epoch=expected.Epoch,stateRevision=expected.StateRevision,topologyRevision=expected.TopologyRevision,definitionRevision=expected.DefinitionRevision,frameRevision=expected.FrameRevision,
                codeGeneration=codeGeneration,configurationRevision=configurationRevision,inputRevision=InputRevision,totalLocalCommits=TotalLocalCommits,totalCoastCommits=TotalCoastCommits,
                controlResolutions=ControlResolutions,supersededPendingControlSegments=SupersededPendingControlSegments,totalEventsApplied=TotalEventsApplied,
                time=expected.Time,requestedRuntimeTime=requested,maximumReferenceStepSeconds=MaximumLocalStepSeconds,warp=Warp,lastLiveInputRuntimeTime=LastLiveInputRuntimeTime,
                hasDeadline=!double.IsPositiveInfinity(deadline),deadlineUniversalTime=double.IsPositiveInfinity(deadline)?0:deadline,coastLeadSeconds=coastLead,
                paused=Paused,allowCoast=policy.AllowCoast,passiveFrozen=policy.PassiveFrozen,policy=OwnedFlightTimelineState.EncodeControls(policy.Controls),segments=rows,events=events.Capture(),fault=Fault,status=Status};
        }
        public static OwnedFlightTimeline RestoreState(OwnedRuntimeHost host,OwnedFlightSession session,OwnedFlightTimelineState data,double wallNowSeconds)
        {
            if(data==null||data.schema!="continuum-owned-flight-timeline-state/v1"||data.segments==null||data.segments.Length>MaximumInputSegments||data.events==null||data.contextSha256!=session.Context.SourceSha256)
                throw new ArgumentException("Execution timeline schema/context differs.");
            var stamp=new OwnedRuntimeStamp(data.epoch,data.stateRevision,data.topologyRevision,data.definitionRevision,data.frameRevision,data.time,data.frameId);
            if(!stamp.Matches(host.World.Stamp)||data.codeGeneration!=host.CodeGeneration||data.configurationRevision!=host.Systems.ConfigurationRevision||data.scheduleSha256!=host.ScheduleSourceSha256)
                throw new InvalidOperationException("Execution timeline requires its exact physical/code/configuration frontier.");
            Finite(data.requestedRuntimeTime);Positive(data.warp);Nonnegative(data.coastLeadSeconds);Finite(data.lastLiveInputRuntimeTime);
            if(data.requestedRuntimeTime<data.time||data.warp>1000000||data.inputRevision<0||data.totalLocalCommits<0||data.totalCoastCommits<0||data.controlResolutions<0||data.supersededPendingControlSegments<0||data.totalEventsApplied<0||data.lastLiveInputRuntimeTime>data.time||data.fault!=null&&!data.paused)
                throw new ArgumentException("Execution timeline counters/demand/fault differ.");
            double nextDeadline=data.hasDeadline?data.deadlineUniversalTime:double.PositiveInfinity;
            if(data.hasDeadline){Finite(nextDeadline);if(nextDeadline<session.UniversalTime)throw new ArgumentException("Execution deadline is behind its frontier.");}
            var restored=new OwnedFlightTimeline(host,session,wallNowSeconds,data.maximumReferenceStepSeconds,data.sourceId);
            restored.policy=new Policy{Controls=OwnedFlightTimelineState.DecodeControls(data.policy),AllowCoast=data.allowCoast,PassiveFrozen=data.passiveFrozen};
            double previous=data.time;
            for(int i=0;i<data.segments.Length;i++)
            {
                var row=data.segments[i];if(row==null)throw new ArgumentException("Execution segment absent.");Finite(row.end);
                if(row.end<=previous||row.end>data.requestedRuntimeTime)throw new ArgumentException("Execution input intervals are not ordered inside demand.");
                restored.segments[i]=new Segment{End=row.end,Closed=row.closed,Input=new Policy{Controls=OwnedFlightTimelineState.DecodeControls(row.controls),AllowCoast=row.allowCoast,PassiveFrozen=row.passiveFrozen}};previous=row.end;
            }
            if(previous!=data.requestedRuntimeTime||(data.segments.Length==0)!=(data.requestedRuntimeTime==data.time))throw new ArgumentException("Execution intervals do not cover retained demand.");
            restored.events=OwnedFlightEventQueue.Restore(host,data.events,data.sourceId);
            if(restored.events.AppliedCount!=data.totalEventsApplied)throw new ArgumentException("Execution event counters differ.");
            restored.count=data.segments.Length;restored.requested=restored.requestAnchor=data.requestedRuntimeTime;
            restored.Paused=data.paused;restored.Warp=data.warp;restored.deadline=nextDeadline;restored.coastLead=data.coastLeadSeconds;
            restored.InputRevision=data.inputRevision;restored.TotalLocalCommits=data.totalLocalCommits;restored.TotalCoastCommits=data.totalCoastCommits;
            restored.ControlResolutions=data.controlResolutions;restored.SupersededPendingControlSegments=data.supersededPendingControlSegments;
            restored.LastLiveInputRuntimeTime=data.lastLiveInputRuntimeTime;restored.TotalEventsApplied=data.totalEventsApplied;
            restored.Fault=data.fault;restored.Status=data.fault!=null?"faulted":data.paused?"paused":"owed";return restored;
        }
        public OwnedFlightTimelineProgress ReadProgress() { Enter(); return Progress(0, 0, Universal(expected.Time) >= deadline, false, Stopwatch.GetTimestamp()); }

        bool CommitNext(double manualLimit, bool allowCoast, out bool wasCoast, out bool waitingQuantum)
        {
            CheckSource(); wasCoast = false; waitingQuantum = false;
            if (count == 0 || Session.UniversalTime >= deadline) return false;
            Segment segment = segments[head]; double target = Math.Min(Math.Min(requested, segment.End),events.NextTime);
            if(target<expected.Time)throw new InvalidOperationException("A physical event was overshot.");
            if (allowCoast && segment.Input.AllowCoast && segment.Input.Resolver == null && Host.SupportsPassiveKeplerCoast)
            {
                double targetUt = Session.Context.Epoch + (target - Session.RuntimeEpoch); string reason; OwnedRuntimeMotion[] motions; double time;OwnedFlightCoastWindow window;
                double eventBarrier=Math.Min(deadline,events.Count==0?double.PositiveInfinity:Universal(events.NextTime));
                if (Session.PrepareCoastWindow(expected, targetUt, segment.Input.Controls, segment.Input.PassiveFrozen, eventBarrier, coastLead, out motions, out time, out reason,out window,target) && time <= target && Universal(time) <= eventBarrier)
                {
                    LastCoastWindow=window;LastCoastRejection=null;
                    var before=expected;Session.PublishCoast(expected, motions, time);events.AcknowledgeCoastAdvance(before,Host.World.Stamp);
                    AcceptCommit(); TotalCoastCommits = checked(TotalCoastCommits + 1); wasCoast = true; return true;
                }
                LastCoastRejection = reason ?? "Prepared coast crossed the representable requested horizon/deadline.";
            }
            double quantum=NextLocalIntervalSeconds;
            if(segment.Input.Resolver!=null)quantum=Math.Min(quantum,MaximumLocalStepSeconds);
            double delta = Math.Min(Math.Min(quantum, manualLimit), target - expected.Time);
            if (!double.IsPositiveInfinity(deadline)) delta = Math.Min(delta, deadline - Session.UniversalTime);
            bool semanticBoundary = segment.Closed || target==events.NextTime || !double.IsPositiveInfinity(manualLimit) ||
                !double.IsPositiveInfinity(deadline) && deadline - Session.UniversalTime <= delta;
            if (!semanticBoundary && (delta < quantum || expected.Time + quantum > target))
            { waitingQuantum = true; return false; }
            if(!(delta>0))return false;
            var localWindow=new OwnedFlightLocalWindow{SourceStamp=expected,MaximumIntervalSeconds=delta,SelectedIntervalSeconds=delta,Kind="numerical-or-requested",ForecastRegion=-1,ContextSha256=Session.Context.SourceSha256,ConfigurationRevision=configurationRevision,CodeGeneration=codeGeneration};
            using(var read=Host.World.AcquireRead())
            {
                if(!read.Stamp.Matches(expected))throw new InvalidOperationException("Local forecast read is stale.");
                OwnedFlightForecastRow forecast;
                if(OwnedFlightForecast.TryFirst(Session,read,delta,out forecast)&&forecast.DeltaSeconds>=Math.Min(quantum,AdvisoryMinimumSliceSeconds)&&forecast.DeltaSeconds<delta)
                {delta=forecast.DeltaSeconds;localWindow.Kind=forecast.Kind.ToString();localWindow.Advisory=true;localWindow.ForecastRegion=forecast.RegionId;}
                double thermal=OwnedFlightWindows.ThermalLagInterval(read,delta);
                if(thermal<delta){delta=thermal;localWindow.Kind="thermal-lag";localWindow.Advisory=false;localWindow.ForecastRegion=-1;}
            }
            double admittedDelta=delta,next = expected.Time + delta;
            for (int corrections = 0; corrections < 8 && (next > target || Universal(next) > deadline); corrections++) next = Previous(next);
            delta = next - expected.Time;
            if(delta>admittedDelta&&expected.Time+admittedDelta==next)delta=admittedDelta;
            if (!(delta > 0) || next > target || Universal(next) > deadline) return false;
            localWindow.SelectedIntervalSeconds=delta;LastLocalWindow=localWindow;
            OwnedRuntimeControls controls = segment.Input.Controls;
            if (segment.Input.Resolver != null) { controls = segment.Input.Resolver(controls); ControlResolutions = checked(ControlResolutions + 1); controls.Validate(); }
            CheckSource();
            Session.AdvanceLocal(Host, delta, controls);
            AcceptCommit(); TotalLocalCommits = checked(TotalLocalCommits + 1); return true;
        }

        void AcceptCommit()
        {
            var stamp = Host.World.Stamp;
            if (!(stamp.Time > expected.Time) || stamp.Time > requested || Universal(stamp.Time) > deadline)
                throw new InvalidOperationException("Committed physical clock violated its timeline target/deadline.");
            expected = stamp;
            while (count > 0 && expected.Time >= segments[head].End)
            { segments[head] = default(Segment); head = (head + 1) % segments.Length; count--; }
        }
        void Accrue(double now)
        {
            ValidateWall(now);
            double next = requested;
            if (!Paused) { double addition = (now - wallAnchor) * Warp; Finite(addition); next = requestAnchor + addition; Finite(next); }
            if (next > requested) Grant(next, policy);
            wall = now;
        }
        void Grant(double target, Policy input)
        {
            Finite(target); if (!(target > requested)) throw new ArgumentException("Time request does not advance representable simulation time.");
            if (count > 0)
            {
                int tail = (head + count - 1) % segments.Length;
                if (!segments[tail].Closed && Same(segments[tail].Input, input)) { Segment last = segments[tail]; last.End = target; segments[tail] = last; requested = target; return; }
            }
            if (count == segments.Length) throw new InvalidOperationException("Bounded input history is full; requested time is retained and the new request is rejected.");
            segments[(head + count) % segments.Length] = new Segment { End = target, Input = input }; count++; requested = target;
        }
        void BindCode() { codeGeneration = Host.CodeGeneration; configurationRevision = Host.Systems.ConfigurationRevision; scheduleSha = Host.ScheduleSourceSha256; }
        void StopFault(Exception error)
        {
            Fault = error.ToString(); Status = "faulted"; Paused = true;
            wallAnchor = wall; requestAnchor = requested;
        }
        void CheckSource()
        {
            if (Fault != null) throw new InvalidOperationException("Timeline fault requires explicit cold recovery.");
            if (!Host.World.Stamp.Matches(expected) || Host.CodeGeneration != codeGeneration || Host.Systems.ConfigurationRevision != configurationRevision || Host.ScheduleSourceSha256 != scheduleSha)
                throw new InvalidOperationException("Physical state/code/configuration changed outside timeline ownership.");
            double clock = Session.UniversalTime; Finite(clock);
        }
        void Enter() { if (Thread.CurrentThread.ManagedThreadId != thread || pumping) throw new InvalidOperationException("Timeline requires its non-reentrant owner thread."); }
        void ValidateWall(double now) { Finite(now); if (now < wall) throw new ArgumentException("Timeline wall clock regressed."); }
        static void ValidateBudget(int commits, double seconds)
        { if (commits < 0 || commits > 65536 || double.IsNaN(seconds) || seconds < 0 || seconds == double.NegativeInfinity) throw new ArgumentException("Work budget is invalid."); }
        static double Elapsed(long begin) { return (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency; }
        double Universal(double runtimeTime) { return Session.Context.Epoch + (runtimeTime - Session.RuntimeEpoch); }
        static double Previous(double value) { return value == 0 ? -double.Epsilon : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + (value > 0 ? -1 : 1)); }
        static bool Same(Policy a, Policy b)
        {
            return a.AllowCoast == b.AllowCoast && a.PassiveFrozen == b.PassiveFrozen && ReferenceEquals(a.Resolver, b.Resolver) &&
                SameControls(a.Controls, b.Controls);
        }
        static bool SameControls(OwnedRuntimeControls a, OwnedRuntimeControls b)
        { return a.Throttle == b.Throttle && a.Translation.X == b.Translation.X && a.Translation.Y == b.Translation.Y && a.Translation.Z == b.Translation.Z &&
            a.Attitude.X == b.Attitude.X && a.Attitude.Y == b.Attitude.Y && a.Attitude.Z == b.Attitude.Z &&
            a.EnginesEnabled == b.EnginesEnabled && a.RcsEnabled == b.RcsEnabled && a.WheelsEnabled == b.WheelsEnabled; }
        OwnedFlightTimelineProgress Progress(int local, int coast, bool reached, bool exhausted, long begin,int eventApplications=0)
        {
            return new OwnedFlightTimelineProgress { Stamp = expected, SourceId = SourceId, ContextSha256 = Session.Context.SourceSha256,
                ScheduleSha256 = scheduleSha, InputRevision = InputRevision, CodeGeneration = codeGeneration, ConfigurationRevision = configurationRevision,
                Status = Status, Fault = Fault, CoastRejection = LastCoastRejection, TotalLocalCommits = TotalLocalCommits, TotalCoastCommits = TotalCoastCommits,
                ControlResolutions = ControlResolutions, LocalCommits = local, CoastCommits = coast, PendingInputSegments = count,
                TotalEventsApplied=TotalEventsApplied,PendingEvents=events.Count,EventApplications=eventApplications,
                SupersededPendingControlSegments = SupersededPendingControlSegments, LastLiveInputRuntimeTime = LastLiveInputRuntimeTime,
                WallSeconds = wall, RequestedRuntimeTime = requested, CommittedRuntimeTime = expected.Time, DebtSeconds = DebtSeconds,
                UniversalTime = Universal(expected.Time), Warp = Warp, Paused = Paused, DeadlineReached = reached, BudgetExhausted = exhausted,
                WorkTicks = Stopwatch.GetTimestamp() - begin, ClockFrequency = Stopwatch.Frequency };
        }
        static void Finite(double value) { AssemblyModel.Finite(value); }
        static void Positive(double value) { AssemblyModel.Positive(value); }
        static void Nonnegative(double value) { Finite(value); if (value < 0) throw new ArgumentOutOfRangeException("value"); }
    }
}
