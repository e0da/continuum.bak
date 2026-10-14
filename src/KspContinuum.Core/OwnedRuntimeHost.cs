using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

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

    public sealed class OwnedRuntimeBuiltinSchedule : IOwnedRuntimePassiveKeplerSchedule, IOwnedRuntimeWorkerSchedule
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
        public readonly OwnedRuntimeWorld World;
        public readonly OwnedRuntimeSystems Systems;
        public string ScheduleSourceSha256 { get; private set; }
        public string ScheduleImplementation { get { return implementation; } }
        public long CodeGeneration { get; private set; }
        public bool SupportsPassiveKeplerCoast {get {lock(gate)return schedule is IOwnedRuntimePassiveKeplerSchedule;}}
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
