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
        public double LastLiveInputRuntimeTime;
        public int LocalCommits, CoastCommits, PendingInputSegments;
        public double WallSeconds, RequestedRuntimeTime, CommittedRuntimeTime, DebtSeconds, UniversalTime, Warp;
        public bool Paused, DeadlineReached, BudgetExhausted;
        public long WorkTicks, ClockFrequency;
    }

    // This owner schedules the existing physical transaction. Presentation only reads committed banks.
    public sealed class OwnedFlightTimeline
    {
        public const int MaximumInputSegments = 128;
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
        public readonly OwnedRuntimeHost Host;
        public readonly OwnedFlightSession Session;
        public readonly string SourceId;
        public readonly double MaximumLocalStepSeconds;
        public long InputRevision { get; private set; }
        public long TotalLocalCommits { get; private set; }
        public long TotalCoastCommits { get; private set; }
        public long ControlResolutions { get; private set; }
        public long SupersededPendingControlSegments { get; private set; }
        public double LastLiveInputRuntimeTime { get; private set; }
        public double RequestedRuntimeTime { get { return requested; } }
        public double CommittedRuntimeTime { get { return expected.Time; } }
        public double DebtSeconds { get { return Math.Max(0, requested - expected.Time); } }
        public bool Paused { get; private set; }
        public double Warp { get; private set; }
        public string Status { get; private set; }
        public string Fault { get; private set; }

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
            long begin = Stopwatch.GetTimestamp(); int local = 0, coast = 0; bool budget = false, reached = false;
            pumping = true;
            try
            {
                CheckSource(); Accrue(wallNowSeconds);
                if (Paused) { Status = "paused"; return Progress(local, coast, false, false, begin); }
                while (DebtSeconds > 0)
                {
                    if (local + coast >= maximumCommits || Elapsed(begin) >= wallBudgetSeconds) { budget = true; Status = "budget-exhausted"; break; }
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
            return Progress(local, coast, reached, budget, begin);
        }

        public OwnedFlightTimelineProgress Step(double deltaSeconds)
        {
            Enter(); CheckSource(); Positive(deltaSeconds);
            if (!Paused || deltaSeconds > MaximumLocalStepSeconds) throw new InvalidOperationException("Manual step requires pause and the same local-step bound.");
            long begin = Stopwatch.GetTimestamp(); bool wasCoast, waitingQuantum;
            pumping = true;
            try
            {
                if (DebtSeconds == 0) Grant(expected.Time + deltaSeconds, policy);
                if (!CommitNext(deltaSeconds, false, out wasCoast, out waitingQuantum)) { Status = "deadline"; return Progress(0, 0, true, false, begin); }
                Status = "paused"; return Progress(1, 0, false, false, begin);
            }
            catch (OwnedRuntimeReadPressureException) { Status = "blocked-readers"; return Progress(0, 0, false, false, begin); }
            catch (Exception error) { StopFault(error); return Progress(0, 0, false, false, begin); }
            finally { pumping = false; }
        }

        public OwnedFlightTimelineProgress CoastTo(double targetUniversalTime)
        {
            Enter(); CheckSource(); Finite(targetUniversalTime);
            if (!Paused || DebtSeconds != 0 || policy.Resolver != null || !policy.AllowCoast || !Host.SupportsPassiveKeplerCoast)
                throw new InvalidOperationException("Manual coast requires paused debt-free explicit passive admission and no feedback resolver.");
            long begin = Stopwatch.GetTimestamp(); string reason; OwnedRuntimeMotion[] motions; double time;
            if (!Session.PrepareCoast(expected, targetUniversalTime, policy.Controls, policy.PassiveFrozen, deadline, coastLead, out motions, out time, out reason) || Universal(time) > deadline)
            { LastCoastRejection = reason ?? "Prepared coast crossed the representable deadline."; Status = "coast-rejected"; return Progress(0, 0, false, false, begin); }
            Session.PublishCoast(expected, motions, time);
            expected = Host.World.Stamp; requested = expected.Time; TotalCoastCommits = checked(TotalCoastCommits + 1);
            Status = "paused"; return Progress(0, 1, false, false, begin);
        }

        // Explicit cold mutation acknowledgement cannot erase unfinished requested time.
        public void Synchronize(OwnedRuntimeStamp previousStamp, double wallNowSeconds, string reason)
        {
            Enter(); ValidateWall(wallNowSeconds);
            if (!Paused || DebtSeconds != 0 || !previousStamp.Matches(expected) || string.IsNullOrEmpty(reason))
                throw new InvalidOperationException("Cold synchronization requires paused debt-free source-bound ownership.");
            var current = Host.World.Stamp;
            if (current.Time < expected.Time) throw new ArgumentException("Synchronization cannot move simulation time backwards.");
            double admittedClock = Session.UniversalTime; Finite(admittedClock);
            if (admittedClock > deadline) throw new ArgumentException("Cold mutation crossed the retained event deadline.");
            expected = current; requested = requestAnchor = current.Time; wall = wallAnchor = wallNowSeconds; head = count = 0;
            BindCode(); Fault = null; Status = "paused"; InputRevision = checked(InputRevision + 1);
        }

        public void CompleteEvent(OwnedRuntimeStamp previousStamp, double wallNowSeconds, string reason, double nextDeadlineUniversalTime)
        {
            Enter(); ValidateWall(wallNowSeconds);
            if (!Paused || !previousStamp.Matches(expected) || string.IsNullOrEmpty(reason) ||
                double.IsNaN(nextDeadlineUniversalTime) || nextDeadlineUniversalTime <= Session.UniversalTime)
                throw new InvalidOperationException("Event acknowledgement requires a paused current source and future deadline.");
            var current = Host.World.Stamp;
            if (current.Time != expected.Time || Universal(current.Time) > deadline)
                throw new InvalidOperationException("Event mutation must keep the stopped physical time and not cross its barrier.");
            expected = current; wall = wallNowSeconds; deadline = nextDeadlineUniversalTime;
            BindCode(); Fault = null; Status = "paused"; InputRevision = checked(InputRevision + 1);
        }

        public string LastCoastRejection { get; private set; }
        public OwnedFlightTimelineProgress ReadProgress() { Enter(); return Progress(0, 0, Universal(expected.Time) >= deadline, false, Stopwatch.GetTimestamp()); }

        bool CommitNext(double manualLimit, bool allowCoast, out bool wasCoast, out bool waitingQuantum)
        {
            CheckSource(); wasCoast = false; waitingQuantum = false;
            if (count == 0 || Session.UniversalTime >= deadline) return false;
            Segment segment = segments[head]; double target = Math.Min(requested, segment.End);
            if (allowCoast && segment.Input.AllowCoast && segment.Input.Resolver == null && Host.SupportsPassiveKeplerCoast)
            {
                double targetUt = Session.Context.Epoch + (target - Session.RuntimeEpoch); string reason; OwnedRuntimeMotion[] motions; double time;
                if (Session.PrepareCoast(expected, targetUt, segment.Input.Controls, segment.Input.PassiveFrozen, deadline, coastLead, out motions, out time, out reason) && time <= target && Universal(time) <= deadline)
                {
                    Session.PublishCoast(expected, motions, time);
                    AcceptCommit(); TotalCoastCommits = checked(TotalCoastCommits + 1); wasCoast = true; return true;
                }
                LastCoastRejection = reason ?? "Prepared coast crossed the representable requested horizon/deadline.";
            }
            double delta = Math.Min(Math.Min(MaximumLocalStepSeconds, manualLimit), target - expected.Time);
            if (!double.IsPositiveInfinity(deadline)) delta = Math.Min(delta, deadline - Session.UniversalTime);
            bool semanticBoundary = segment.Closed || !double.IsPositiveInfinity(manualLimit) ||
                !double.IsPositiveInfinity(deadline) && deadline - Session.UniversalTime <= delta;
            if (!semanticBoundary && (delta < MaximumLocalStepSeconds || expected.Time + MaximumLocalStepSeconds > target))
            { waitingQuantum = true; return false; }
            double next = expected.Time + delta;
            for (int corrections = 0; corrections < 8 && (next > target || Universal(next) > deadline); corrections++) next = Previous(next);
            delta = next - expected.Time;
            if (!(delta > 0) || next > target || Universal(next) > deadline) return false;
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
        OwnedFlightTimelineProgress Progress(int local, int coast, bool reached, bool exhausted, long begin)
        {
            return new OwnedFlightTimelineProgress { Stamp = expected, SourceId = SourceId, ContextSha256 = Session.Context.SourceSha256,
                ScheduleSha256 = scheduleSha, InputRevision = InputRevision, CodeGeneration = codeGeneration, ConfigurationRevision = configurationRevision,
                Status = Status, Fault = Fault, CoastRejection = LastCoastRejection, TotalLocalCommits = TotalLocalCommits, TotalCoastCommits = TotalCoastCommits,
                ControlResolutions = ControlResolutions, LocalCommits = local, CoastCommits = coast, PendingInputSegments = count,
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
