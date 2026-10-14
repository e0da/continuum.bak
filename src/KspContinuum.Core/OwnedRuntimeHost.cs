using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Diagnostics;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedRuntimeHostCheckpoint
    {
        public string schema="continuum-owned-runtime-host-checkpoint/v1",implementation,sourceSha256;
        public long codeGeneration;
        public OwnedRuntimeCheckpointData state;
    }
    public interface IOwnedRuntimeSchedule
    {
        string Schema { get; }
        string StateSchema { get; }
        string ImplementationId { get; }
        void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls);
    }

    // Admission requires an original schedule whose implementation may run without Unity APIs.
    public interface IOwnedRuntimeWorkerSchedule : IOwnedRuntimeSchedule { }
    // Resource and wheel state follow one original ordered scalar fold with the supplied controls and interval.
    public interface IOwnedRuntimeScalarActuatorSchedule : IOwnedRuntimeSchedule { }

    public sealed class OwnedRuntimeBuiltinSchedule : IOwnedRuntimePassiveKeplerSchedule, IOwnedRuntimeWorkerSchedule, IOwnedRuntimeTrialSafeSchedule, IOwnedRuntimeScalarActuatorSchedule
    {
        public string Schema { get { return "continuum-owned-runtime-schedule/v1"; } }
        public string StateSchema { get { return "world-bank-state-only/v1"; } }
        public string ImplementationId { get { return "retained-bulk-systems/v1"; } }
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        { systems.Step(step,environment,controls); }
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {systems.Step(step,field,controls);}
    }

    public sealed class OwnedRuntimeHost
    {
        static readonly string BuiltinSourceSha256=HashBuiltin();
        static string HashBuiltin()
        {
            string path=typeof(OwnedRuntimeBuiltinSchedule).Assembly.Location;
            if(string.IsNullOrEmpty(path)||!File.Exists(path))throw new InvalidOperationException("Builtin schedule requires its actual executable assembly image.");
            using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
        }
        readonly object gate=new object();
        IOwnedRuntimeSchedule schedule;
        string implementation;
        bool advancing;
        int workerThread;
        OwnedRuntimeAdaptive adaptive;
        OwnedRuntimeEmbedded embedded;
        OwnedRuntimeHigherOrder higherOrder;
        OwnedRuntimeAdaptiveObservation adaptiveObservation;
        public OwnedRuntimeAdaptiveObservation AdaptiveObservation {get{lock(gate)return adaptiveObservation;}}
        public readonly OwnedRuntimeWorld World;
        public readonly OwnedRuntimeSystems Systems;
        public string ScheduleSourceSha256 { get; private set; }
        public string ScheduleImplementation { get { return implementation; } }
        public long CodeGeneration { get; private set; }
        public bool SupportsPassiveKeplerCoast {get {lock(gate)return schedule is IOwnedRuntimePassiveKeplerSchedule;}}
        public bool SupportsAdaptiveMechanical {get{lock(gate)return schedule is IOwnedRuntimeTrialSafeSchedule&&(World.MechanicalInternal.method==0||schedule is OwnedRuntimeBuiltinSchedule)&&(Systems.DeadlineStrategy!=OwnedRuntimeDeadlineStrategy.Retained||schedule is IOwnedRuntimeScalarActuatorSchedule);}}
        public bool SupportsEmbeddedMechanical {get{lock(gate)return schedule is OwnedRuntimeBuiltinSchedule;}}
        public int WorkerThreadId { get { return Volatile.Read(ref workerThread); } }
        public OwnedRuntimeHost(OwnedRuntimeWorld world,OwnedRuntimeSystems systems)
        {
            if(world==null||systems==null)throw new ArgumentNullException("world/systems");
            World=world;Systems=systems;schedule=new OwnedRuntimeBuiltinSchedule();implementation=schedule.ImplementationId;ScheduleSourceSha256=BuiltinSourceSha256;
        }
        public void BindWorkerThread()
        {
            lock(gate)
            {
                RequireWorkerThread();
                if(advancing || !(schedule is IOwnedRuntimeWorkerSchedule))throw new InvalidOperationException("Worker binding requires an idle worker-admitted schedule.");
                World.BindWriterThread();
                workerThread=Thread.CurrentThread.ManagedThreadId;
            }
        }
        void RequireWorkerThread()
        {if(workerThread!=0 && workerThread!=Thread.CurrentThread.ManagedThreadId)throw new InvalidOperationException("The simulation worker owns this runtime host.");}
        public OwnedRuntimeHostCheckpoint CaptureCheckpoint(OwnedRuntimeImportResult imported,OwnedRuntimeControls controls,OwnedRuntimeEnvironment environment,OwnedRuntimeStepPlan plan)
        {
            lock(gate)
            {
                RequireWorkerThread();
                if(advancing||!ReferenceEquals(imported.World,World)||!ReferenceEquals(imported.Systems,Systems))throw new InvalidOperationException("Checkpoint owner differs or is advancing.");
                return new OwnedRuntimeHostCheckpoint {implementation=implementation,sourceSha256=ScheduleSourceSha256,codeGeneration=CodeGeneration,state=OwnedRuntimeCheckpoint.Capture(imported,controls,environment,plan)};
            }
        }
        public OwnedRuntimeHost RestoreCheckpoint(OwnedRuntimeHostCheckpoint data,string nativeSha,string partSha,out OwnedRuntimeRestoredState state)
        {
            lock(gate)
            {
                RequireWorkerThread();
                if(advancing||data==null||data.schema!="continuum-owned-runtime-host-checkpoint/v1"||data.implementation!=implementation||data.sourceSha256!=ScheduleSourceSha256||data.codeGeneration!=CodeGeneration)
                    throw new InvalidOperationException("Checkpoint requires its exact loaded executable schedule generation.");
                if(data.state!=null&&data.state.mechanical!=null&&data.state.mechanical.adaptive&&!(schedule is IOwnedRuntimeTrialSafeSchedule))
                    throw new InvalidOperationException("Adaptive checkpoint requires its isolated-trial-safe schedule.");
                if(data.state!=null&&data.state.mechanical!=null&&data.state.mechanical.adaptive&&data.state.mechanical.method!=0&&!(schedule is OwnedRuntimeBuiltinSchedule))
                    throw new InvalidOperationException("Embedded checkpoint requires the builtin mechanical-stage schedule.");
                if(data.state!=null&&data.state.mechanical!=null&&data.state.mechanical.adaptive&&data.state.deadlineStrategy==1&&!(schedule is IOwnedRuntimeScalarActuatorSchedule))
                    throw new InvalidOperationException("Retained deadline checkpoint requires its scalar actuator schedule contract.");
                state=OwnedRuntimeCheckpoint.Restore(data.state,nativeSha,partSha);
                var restored=new OwnedRuntimeHost(state.World,state.Systems);
                restored.schedule=schedule;restored.implementation=implementation;restored.ScheduleSourceSha256=ScheduleSourceSha256;restored.CodeGeneration=CodeGeneration;
                if(workerThread!=0)restored.BindWorkerThread();
                return restored;
            }
        }
        public OwnedRuntimeStamp Advance(double delta,OwnedRuntimeInput input,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {return AdvanceCore(delta,input,environment,null,controls);}
        public OwnedRuntimeStamp Advance(double delta,OwnedRuntimeInput input,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {if(field==null)throw new ArgumentNullException("field");return AdvanceCore(delta,input,null,field,controls);}
        internal OwnedRuntimeHost CreateTrialHost(OwnedRuntimeWorld world,OwnedRuntimeSystems systems)
        {
            var h=new OwnedRuntimeHost(world,systems);h.schedule=schedule;h.implementation=implementation;h.ScheduleSourceSha256=ScheduleSourceSha256;h.CodeGeneration=CodeGeneration;
            if(workerThread!=0)h.BindWorkerThread();return h;
        }
        public OwnedRuntimeStamp AdvanceAdaptive(double deltaUpper,OwnedRuntimeInput input,OwnedFlightSession session,OwnedRuntimeControls controls)
        {
            if(session==null||!ReferenceEquals(session.Runtime,World))throw new ArgumentException("Adaptive session owner differs.");input.Validate();controls.Validate();AssemblyModel.Positive(deltaUpper);
            if(input.Throttle!=controls.Throttle||input.Pitch!=controls.Attitude.X||input.Yaw!=controls.Attitude.Y||input.Roll!=controls.Attitude.Z||input.X!=controls.Translation.X||input.Y!=controls.Translation.Y||input.Z!=controls.Translation.Z)
                throw new ArgumentException("Adaptive input and actuator deadline controls differ.");
            lock(gate)
            {
                RequireWorkerThread();if(advancing)throw new InvalidOperationException("Adaptive schedule cannot reenter.");
                if(!World.AdaptiveMechanicalEnabled)return AdvanceCore(deltaUpper,input,null,session,controls);
                long begin=Stopwatch.GetTimestamp();bool entered=false,admitted=false;
                try
                {
                    if(!(schedule is IOwnedRuntimeTrialSafeSchedule))throw new InvalidOperationException("Schedule has no repeated isolated-trial contract.");
                    if(World.MechanicalInternal.method!=0&&!(schedule is OwnedRuntimeBuiltinSchedule))throw new InvalidOperationException("Schedule has no builtin embedded-stage contract.");
                    if(Systems.DeadlineStrategy==OwnedRuntimeDeadlineStrategy.Retained&&!(schedule is IOwnedRuntimeScalarActuatorSchedule))throw new InvalidOperationException("Schedule has no retained scalar actuator deadline contract.");
                    var frontier=World.Stamp;double semanticEnd=frontier.Time+deltaUpper;AssemblyModel.Finite(semanticEnd);
                    if(!(semanticEnd>frontier.Time))throw new ArgumentException("Adaptive upper interval does not advance representable physical time.");
                    double horizon=Math.Min(deltaUpper,World.MaximumMechanicalInterval);
                    var boundary=Systems.FindActuatorBoundary(frontier,controls,horizon);
                    if(!boundary.Supported)throw new InvalidOperationException("Actuator boundary is unsupported: "+boundary.Reason);
                    if(!World.Stamp.Matches(frontier)||!boundary.SourceStamp.Matches(frontier)||boundary.ConfigurationRevision!=Systems.ConfigurationRevision||double.IsNaN(boundary.DeltaSeconds)||double.IsInfinity(boundary.DeltaSeconds)||boundary.DeltaSeconds<=0||boundary.DeltaSeconds>horizon)
                        throw new InvalidOperationException("Actuator deadline certificate differs from its admitted frontier/horizon.");
                    if(boundary.Reached)
                    {
                        double end=frontier.Time+boundary.DeltaSeconds;
                        if(!(end>frontier.Time))end=OwnedRuntimeAdaptive.NextTime(frontier.Time);
                        else if(end-frontier.Time<boundary.DeltaSeconds)end=OwnedRuntimeAdaptive.NextTime(end);
                        if(end>semanticEnd)end=semanticEnd;
                        double bounded=Math.Min(deltaUpper,end-frontier.Time);
                        if(bounded>boundary.DeltaSeconds)
                        {adaptiveObservation.ActuatorBoundaryClockCeilings++;adaptiveObservation.LastBoundaryClockCeilingSeconds=bounded-boundary.DeltaSeconds;}
                        deltaUpper=bounded;
                    }
                    var expected=World.Stamp;advancing=true;
                    if(World.MechanicalInternal.method==2)
                    {
                        if(higherOrder==null||!higherOrder.Matches(this))higherOrder=new OwnedRuntimeHigherOrder(this);
                        Systems.EnterExecution();entered=true;
                        return higherOrder.Advance(this,deltaUpper,input,session,controls,ref adaptiveObservation);
                    }
                    if(World.MechanicalInternal.method==1)
                    {
                        if(embedded==null||!embedded.Matches(this))embedded=new OwnedRuntimeEmbedded(this);
                        Systems.EnterExecution();entered=true;
                        return embedded.Advance(this,deltaUpper,input,session,controls,ref adaptiveObservation);
                    }
                    if(adaptive==null||!adaptive.Matches(this))adaptive=new OwnedRuntimeAdaptive(this);
                    Systems.EnterExecution();entered=true;World.EnterAdmission(expected);admitted=true;
                    return adaptive.Advance(this,deltaUpper,input,session,controls,ref adaptiveObservation);
                }
                finally{try{if(admitted)World.ExitAdmission();}finally{try{if(entered)Systems.ExitExecution();}finally{advancing=false;adaptiveObservation.WorkTicks+=Stopwatch.GetTimestamp()-begin;adaptiveObservation.ClockFrequency=Stopwatch.Frequency;}}}
            }
        }
        OwnedRuntimeStamp AdvanceCore(double delta,OwnedRuntimeInput input,OwnedRuntimeEnvironment environment,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {
            lock(gate)
            {
                RequireWorkerThread();
                if(advancing)throw new InvalidOperationException("Runtime schedule cannot reenter.");
                var fieldSchedule=schedule as IOwnedRuntimeFieldSchedule;
                if(field!=null&&fieldSchedule==null)throw new InvalidOperationException("Loaded schedule has no environment-field execution contract.");
                advancing=true;
                try
                {
                    var step=World.BeginHostedStep(World.Stamp,delta,input);
                    bool entered=false;
                    try
                    {
                        Systems.EnterExecution();entered=true;
                        if(field==null)schedule.Step(Systems,step,environment,controls);else fieldSchedule.Step(Systems,step,field,controls);
                        step.RefreshMassProperties();
                        var guard=field as IOwnedRuntimeStepGuard;if(guard!=null)guard.ValidateStep(step);
                        return World.CommitHostedStep(step);
                    }
                    finally {try {if(entered)Systems.ExitExecution();}finally {World.EndHostedStep(step);}}
                }
                finally {advancing=false;}
            }
        }
        public void SwapSchedule(IOwnedRuntimeSchedule replacement,string sha256,OwnedRuntimeStamp expected)
        {
            if(replacement==null||sha256==null||sha256.Length!=64)throw new ArgumentException("A source-bound schedule is required.");
            foreach(char value in sha256)if(!((value>='0'&&value<='9')||(value>='a'&&value<='f')))throw new ArgumentException("Schedule digest is invalid.");
            lock(gate)
            {
                RequireWorkerThread();
                if(advancing||!World.Stamp.Matches(expected))throw new InvalidOperationException("Schedule reload epoch is stale.");
                if(workerThread!=0 && !(replacement is IOwnedRuntimeWorkerSchedule))throw new InvalidOperationException("Schedule has no worker execution admission.");
                if(World.AdaptiveMechanicalEnabled&&!(replacement is IOwnedRuntimeTrialSafeSchedule))throw new InvalidOperationException("Adaptive mechanics requires an isolated-trial-safe schedule.");
                if(World.AdaptiveMechanicalEnabled&&World.MechanicalInternal.method!=0&&!(replacement is OwnedRuntimeBuiltinSchedule))throw new InvalidOperationException("Embedded mechanics requires the builtin mechanical-stage schedule.");
                if(World.AdaptiveMechanicalEnabled&&Systems.DeadlineStrategy==OwnedRuntimeDeadlineStrategy.Retained&&!(replacement is IOwnedRuntimeScalarActuatorSchedule))throw new InvalidOperationException("Retained deadlines require the scalar actuator schedule contract.");
                string nextImplementation=null;bool entered=false;World.EnterAdmission(expected);
                try
                {
                    Systems.EnterExecution();entered=true;
                    string schema=replacement.Schema,stateSchema=replacement.StateSchema;nextImplementation=replacement.ImplementationId;
                    if(schema!="continuum-owned-runtime-schedule/v1"||stateSchema!="world-bank-state-only/v1"
                        ||string.IsNullOrEmpty(nextImplementation)||nextImplementation.Length>256)
                        throw new ArgumentException("Schedule state/ABI is incompatible.");
                    if(!World.Stamp.Matches(expected))throw new InvalidOperationException("Schedule admission changed the world.");
                }
                finally {try {if(entered)Systems.ExitExecution();}finally {World.ExitAdmission();}}
                long generation=checked(CodeGeneration+1);schedule=replacement;implementation=nextImplementation;ScheduleSourceSha256=sha256;CodeGeneration=generation;
            }
        }
    }
}
