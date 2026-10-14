using System;
using System.Diagnostics;
using System.Threading;

namespace KspContinuum
{
    public struct OwnedFlightReadDescriptor
    {
        public readonly OwnedRuntimeStamp Stamp;
        public readonly Vec Origin;
        public readonly double UniversalTime, RuntimeEpoch;
        public readonly string FrameId, Mode;
        public readonly OwnedFlightWorld Context;
        internal OwnedFlightReadDescriptor(OwnedRuntimeStamp stamp, Vec origin, double runtimeEpoch, string mode, OwnedFlightWorld context)
        {
            Stamp=stamp;Origin=origin;RuntimeEpoch=runtimeEpoch;FrameId=stamp.FrameId;Mode=mode;Context=context;
            UniversalTime=context.Epoch+(stamp.Time-runtimeEpoch);AssemblyModel.Finite(UniversalTime);
        }
        void Check(OwnedRuntimeReadLease read)
        {if(read==null||!read.Stamp.Matches(Stamp))throw new InvalidOperationException("Presentation descriptor and retained bank differ.");}
        public Vec BodyPosition(OwnedRuntimeReadLease read,int region)
        {Check(read);return Origin+read.GetRegion(region).Motion.RootPosition;}
        public Vec CenterPosition(OwnedRuntimeReadLease read,int region)
        {Check(read);var r=read.GetRegion(region);return Origin+r.Motion.RootPosition+r.Motion.Orientation.Rotate(r.Properties.CenterOfMass);}
        public OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeReadLease read,int region)
        {return Context.EvaluateSample(CenterPosition(read,region),UniversalTime,Stamp.Time);}
    }

    public sealed class OwnedFlightPresentationLease : IDisposable
    {
        readonly OwnedFlightWorker worker;
        int disposed;
        public readonly OwnedRuntimeReadLease Read;
        public readonly OwnedFlightReadDescriptor Descriptor;
        public readonly Vec Origin;
        public readonly double UniversalTime;
        public readonly string FrameId,Mode;
        public readonly OwnedFlightWorld Context;
        public readonly OwnedFlightTimelineProgress Progress;
        internal OwnedFlightPresentationLease(OwnedFlightWorker worker,OwnedRuntimeReadLease read,OwnedFlightReadDescriptor descriptor,OwnedFlightTimelineProgress progress)
        {this.worker=worker;Read=read;Descriptor=descriptor;Origin=descriptor.Origin;UniversalTime=descriptor.UniversalTime;FrameId=descriptor.FrameId;Mode=descriptor.Mode;Context=descriptor.Context;Progress=progress;}
        public void Dispose()
        {if(Interlocked.Exchange(ref disposed,1)==0){try{Read.Dispose();}finally{worker.ReleasePresentation();}}}
    }

    public enum OwnedFlightWorkerInvocation { ReadOnly, ColdMutation, SameTimeMutation, TimelineMutation, CheckpointReplacement }
    public sealed class OwnedFlightWorkerOptions
    {
        public bool AutomaticProgress=true;
        public double MaximumLocalStepSeconds=.02,WallBudgetSeconds=.004;
        public int MaximumCommitsPerPump=32,IdleWaitMilliseconds=4;
        public string SourceId="owned-flight-worker";
        public Func<double> WallClock;
    }
    [Serializable] public struct OwnedFlightWorkerStatus
    {
        public string Status,Fault,FrameId,ContextSha256;
        public int ThreadId,QueuedCommands;
        public long AcceptedCommands,CompletedCommands,CancelledCommands,RejectedCommands,Pumps,ReadPressurePumps,PublicationGaps;
        public long Epoch,StateRevision,TopologyRevision,DefinitionRevision,FrameRevision;
        public double UniversalTime;
        public bool Ready,Stopped;
        public OwnedFlightTimelineProgress Progress;
    }

    public sealed class OwnedFlightWorkerContext
    {
        internal OwnedFlightWorker Owner;
        internal bool CanReplace;
        public OwnedRuntimeHost Host {get;private set;}
        public OwnedFlightSession Session {get;private set;}
        public object State {get;private set;}
        public OwnedFlightTimeline Timeline {get;internal set;}
        public OwnedFlightWorkerContext(OwnedRuntimeHost host,OwnedFlightSession session,object state=null)
        {Assign(host,session,state);}
        void Assign(OwnedRuntimeHost host,OwnedFlightSession session,object state)
        {
            if(host==null||session==null||!ReferenceEquals(host.World,session.Runtime))throw new ArgumentException("Worker host/session differ.");
            Host=host;Session=session;State=state;
        }
        public void Replace(OwnedRuntimeHost host,OwnedFlightSession session,object state=null,OwnedFlightTimelineState execution=null)
        {
            if(Owner==null||!CanReplace)throw new InvalidOperationException("Owner replacement requires its worker cold invocation.");
            Owner.CheckThread();if(!Timeline.Paused||Timeline.DebtSeconds!=0)throw new InvalidOperationException("Replacement cannot discard unfinished demand.");
            if(host==null||session==null||!ReferenceEquals(host.World,session.Runtime))throw new ArgumentException("Replacement host/session differ.");
            host.BindWorkerThread();var restored=execution==null?null:OwnedFlightTimeline.RestoreState(host,session,execution,Owner.CurrentWall());
            Assign(host,session,state);Owner.Replaced(restored);
        }
        public OwnedFlightTimelineProgress Configure(OwnedRuntimeControls input,double warp,bool paused,
            bool allowPassiveCoast=false,bool passiveStateFrozen=false,double nextDeadlineUniversalTime=double.PositiveInfinity,double coastGuardLeadSeconds=0,double? wallSeconds=null)
        {
            if(Owner==null)throw new InvalidOperationException("Context has no execution owner.");Owner.CheckThread();
            return Owner.ConfigureNow(input,warp,paused,allowPassiveCoast,passiveStateFrozen,nextDeadlineUniversalTime,coastGuardLeadSeconds,wallSeconds);
        }
        public OwnedFlightTimelineProgress SetLiveControls(OwnedRuntimeControls input,double? wallSeconds=null)
        {if(Owner==null)throw new InvalidOperationException("Context has no execution owner.");Owner.CheckThread();return Owner.LiveControlsNow(input,wallSeconds);}
    }

    public sealed class OwnedFlightWorkerAcknowledgement<T>
    {
        readonly object gate=new object();
        int state;
        T result;
        Exception error;
        public readonly long Sequence;
        internal OwnedFlightWorkerAcknowledgement(long sequence){Sequence=sequence;}
        public bool IsCompleted {get{return Volatile.Read(ref state)>=2;}}
        public bool IsStarted {get{return Volatile.Read(ref state)==1||Volatile.Read(ref state)==2;}}
        public Exception Error {get{if(!IsCompleted)return null;return error;}}
        public T Result
        {get{if(!IsCompleted)throw new InvalidOperationException("Command acknowledgement is pending.");if(error!=null)throw new InvalidOperationException("Worker command failed.",error);return result;}}
        public bool TryCancel()
        {
            lock(gate)
            {if(state!=0)return false;error=new OperationCanceledException("Queued command cancelled before execution.");Volatile.Write(ref state,3);Monitor.PulseAll(gate);return true;}
        }
        public bool Wait(int timeoutMilliseconds)
        {
            if(timeoutMilliseconds< -1)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            long begin=Stopwatch.GetTimestamp();lock(gate)
            {
                while(state<2)
                {
                    int remaining=timeoutMilliseconds;
                    if(timeoutMilliseconds>=0)
                    {remaining=Math.Max(0,timeoutMilliseconds-(int)Math.Min(int.MaxValue,(Stopwatch.GetTimestamp()-begin)*1000d/Stopwatch.Frequency));if(remaining==0)return false;}
                    Monitor.Wait(gate,remaining);
                }
                return true;
            }
        }
        internal bool Start(){lock(gate){if(state!=0)return false;Volatile.Write(ref state,1);return true;}}
        internal void Complete(T value,Exception failure)
        {lock(gate){result=value;error=failure;Volatile.Write(ref state,2);Monitor.PulseAll(gate);}}
    }

    public sealed class OwnedFlightWorker : IDisposable
    {
        public const int MaximumQueuedCommands=128;
        abstract class Request {internal abstract bool Execute(OwnedFlightWorker worker);internal abstract void Cancel();}
        sealed class Request<T> : Request
        {
            internal readonly OwnedFlightWorkerAcknowledgement<T> Ack;
            readonly Func<OwnedFlightWorkerContext,T> action;
            readonly OwnedFlightWorkerInvocation kind;
            readonly bool internalCommand;
            internal Request(long sequence,Func<OwnedFlightWorkerContext,T> action,OwnedFlightWorkerInvocation kind,bool internalCommand)
            {Ack=new OwnedFlightWorkerAcknowledgement<T>(sequence);this.action=action;this.kind=kind;this.internalCommand=internalCommand;}
            internal override bool Execute(OwnedFlightWorker worker)
            {
                if(!Ack.Start())return false;
                try{Ack.Complete(worker.Execute(action,kind,internalCommand),null);}
                catch(Exception error){Ack.Complete(default(T),error);Interlocked.Increment(ref worker.rejected);}
                return true;
            }
            internal override void Cancel(){Ack.TryCancel();}
        }
        readonly object gate=new object();
        readonly Request[] queue=new Request[MaximumQueuedCommands];
        readonly Func<OwnedFlightWorkerContext> initialize;
        readonly Func<double> wallClock;
        readonly Thread thread;
        readonly bool automatic;
        readonly double maximumStep,wallBudget;
        readonly int maximumCommits,idleWait;
        readonly string sourceId;
        int head,count,threadId;
        bool ready,stopping,stopped,presentationHeld,published;
        long accepted,completed,cancelled,rejected,pumps,pressure,gaps;
        string fault,status="starting";
        OwnedFlightWorkerContext context;
        OwnedFlightReadDescriptor descriptor;
        OwnedFlightTimelineProgress progress;
        OwnedRuntimeWorld publishedWorld;
        OwnedFlightSession publishedSession;
        OwnedRuntimeControls controls;
        bool allowCoast,passiveFrozen;
        double deadline=double.PositiveInfinity,coastLead;

        public OwnedFlightWorker(Func<OwnedFlightWorkerContext> initialize,OwnedFlightWorkerOptions options=null)
        {
            if(initialize==null)throw new ArgumentNullException("initialize");options=options??new OwnedFlightWorkerOptions();
            AssemblyModel.Positive(options.MaximumLocalStepSeconds);AssemblyModel.Positive(options.WallBudgetSeconds);
            if(options.MaximumLocalStepSeconds>.1||options.MaximumCommitsPerPump<1||options.MaximumCommitsPerPump>65536||options.IdleWaitMilliseconds<1||options.IdleWaitMilliseconds>1000||string.IsNullOrEmpty(options.SourceId)||options.SourceId.Length>128)
                throw new ArgumentException("Worker scheduling options are outside bounds.");
            this.initialize=initialize;automatic=options.AutomaticProgress;maximumStep=options.MaximumLocalStepSeconds;wallBudget=options.WallBudgetSeconds;
            maximumCommits=options.MaximumCommitsPerPump;idleWait=options.IdleWaitMilliseconds;sourceId=options.SourceId;
            wallClock=options.WallClock??Clock;thread=new Thread(Run){IsBackground=true,Name="Continuum owned flight"};thread.Start();
        }
        static double Clock(){return Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;}
        internal void CheckThread(){if(Thread.CurrentThread.ManagedThreadId!=threadId)throw new InvalidOperationException("Only the flight worker owns mutable execution.");}
        public bool WaitUntilReady(int timeoutMilliseconds)
        {
            if(timeoutMilliseconds<0)throw new ArgumentOutOfRangeException("timeoutMilliseconds");long begin=Stopwatch.GetTimestamp();
            lock(gate)while(!ready&&!stopped)
            {int remaining=timeoutMilliseconds-(int)Math.Min(int.MaxValue,(Stopwatch.GetTimestamp()-begin)*1000d/Stopwatch.Frequency);if(remaining<=0)return false;Monitor.Wait(gate,remaining);}
            return ReadStatus().Ready;
        }
        public OwnedFlightWorkerStatus ReadStatus()
        {
            lock(gate)return new OwnedFlightWorkerStatus {Status=status,Fault=fault,ThreadId=threadId,QueuedCommands=count,AcceptedCommands=accepted,
                CompletedCommands=completed,CancelledCommands=cancelled,RejectedCommands=rejected,Pumps=pumps,ReadPressurePumps=pressure,PublicationGaps=gaps,Ready=ready,Stopped=stopped,Progress=progress,
                Epoch=descriptor.Stamp.Epoch,StateRevision=descriptor.Stamp.StateRevision,TopologyRevision=descriptor.Stamp.TopologyRevision,DefinitionRevision=descriptor.Stamp.DefinitionRevision,
                FrameRevision=descriptor.Stamp.FrameRevision,FrameId=descriptor.FrameId,ContextSha256=descriptor.Context==null?null:descriptor.Context.SourceSha256,UniversalTime=descriptor.UniversalTime};
        }
        OwnedFlightWorkerAcknowledgement<T> Enqueue<T>(Func<OwnedFlightWorkerContext,T> action,OwnedFlightWorkerInvocation kind,bool internalCommand)
        {
            if(action==null)throw new ArgumentNullException("action");if(!Enum.IsDefined(typeof(OwnedFlightWorkerInvocation),kind))throw new ArgumentException("Unknown invocation kind.");
            lock(gate)
            {
                if(stopping||stopped)throw new ObjectDisposedException("OwnedFlightWorker");if(count==queue.Length)throw new InvalidOperationException("Worker command queue is full; no command was admitted.");
                long sequence=checked(accepted+1);var request=new Request<T>(sequence,action,kind,internalCommand);
                queue[(head+count)%queue.Length]=request;count++;accepted=sequence;Monitor.PulseAll(gate);return request.Ack;
            }
        }
        public OwnedFlightWorkerAcknowledgement<T> Invoke<T>(Func<OwnedFlightWorkerContext,T> action,OwnedFlightWorkerInvocation kind=OwnedFlightWorkerInvocation.ReadOnly)
        {return Enqueue(action,kind,false);}
        public OwnedFlightWorkerAcknowledgement<bool> Invoke(Action<OwnedFlightWorkerContext> action,OwnedFlightWorkerInvocation kind=OwnedFlightWorkerInvocation.ReadOnly)
        {if(action==null)throw new ArgumentNullException("action");return Invoke(c=>{action(c);return true;},kind);}
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> Configure(OwnedRuntimeControls input,double warp,bool paused,
            bool allowPassiveCoast=false,bool passiveStateFrozen=false,double nextDeadlineUniversalTime=double.PositiveInfinity,double coastGuardLeadSeconds=0,double? wallSeconds=null)
        {
            input.Validate();return Enqueue(c=>c.Configure(input,warp,paused,allowPassiveCoast,passiveStateFrozen,nextDeadlineUniversalTime,coastGuardLeadSeconds,wallSeconds),OwnedFlightWorkerInvocation.ReadOnly,true);
        }
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> SetControls(OwnedRuntimeControls input)
        {input.Validate();return Enqueue(c=>Change(input,c.Timeline.Warp,c.Timeline.Paused),OwnedFlightWorkerInvocation.ReadOnly,true);}
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> SetLiveControls(OwnedRuntimeControls input)
        {input.Validate();return Enqueue(c=>c.SetLiveControls(input),OwnedFlightWorkerInvocation.ReadOnly,true);}
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> SetPause(bool paused)
        {return Enqueue(c=>Change(c.Timeline.HeldControls,c.Timeline.Warp,paused),OwnedFlightWorkerInvocation.ReadOnly,true);}
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> SetWarp(double warp)
        {return Enqueue(c=>Change(c.Timeline.HeldControls,warp,c.Timeline.Paused),OwnedFlightWorkerInvocation.ReadOnly,true);}
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> RequestDemand(double wallSeconds)
        {
            AssemblyModel.Finite(wallSeconds);if(automatic)throw new InvalidOperationException("Explicit demand requires deterministic caller-clock mode.");
            return Enqueue(c=>Pump(wallSeconds),OwnedFlightWorkerInvocation.ReadOnly,true);
        }
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> Step(double deltaSeconds)
        {return Enqueue(c=>c.Timeline.Step(deltaSeconds),OwnedFlightWorkerInvocation.ReadOnly,true);}
        public OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> Flush()
        {return Invoke(c=>c.Timeline.ReadProgress());}
        OwnedFlightTimelineProgress Change(OwnedRuntimeControls input,double warp,bool paused)
        {
            return ConfigureNow(input,warp,paused,allowCoast,passiveFrozen,deadline,coastLead,null);
        }
        internal OwnedFlightTimelineProgress ConfigureNow(OwnedRuntimeControls input,double warp,bool paused,bool coast,bool frozen,double nextDeadline,double lead,double? now)
        {
            CheckThread();context.Timeline.Configure(context.Host.World.Stamp,context.Timeline.InputRevision,Wall(now),input,warp,paused,coast,frozen,nextDeadline,lead);
            controls=input;allowCoast=coast;passiveFrozen=frozen;deadline=nextDeadline;coastLead=lead;return context.Timeline.ReadProgress();
        }
        internal OwnedFlightTimelineProgress LiveControlsNow(OwnedRuntimeControls input,double? now)
        {
            CheckThread();context.Timeline.SetLiveControls(context.Host.World.Stamp,context.Timeline.InputRevision,Wall(now),input);
            controls=input;return context.Timeline.ReadProgress();
        }
        double Wall(double? explicitWall)
        {return explicitWall.HasValue?explicitWall.Value:automatic?wallClock():context.Timeline.ReadProgress().WallSeconds;}
        internal double CurrentWall(){return Wall(null);}
        internal void Replaced(OwnedFlightTimeline restored=null)
        {
            CheckThread();double now=Wall(null);
            context.Timeline=restored??new OwnedFlightTimeline(context.Host,context.Session,now,maximumStep,sourceId);
            controls=context.Timeline.HeldControls;allowCoast=context.Timeline.AllowPassiveCoast;passiveFrozen=context.Timeline.PassiveStateFrozen;deadline=context.Timeline.NextDeadlineUniversalTime;coastLead=context.Timeline.CoastGuardLeadSeconds;
        }
        T Execute<T>(Func<OwnedFlightWorkerContext,T> action,OwnedFlightWorkerInvocation kind,bool internalCommand)
        {
            CheckThread();var before=context.Host.World.Stamp;long code=context.Host.CodeGeneration,configuration=context.Host.Systems.ConfigurationRevision;
            var oldHost=context.Host;var oldTimeline=context.Timeline;bool mutation=kind!=OwnedFlightWorkerInvocation.ReadOnly;
            if(!internalCommand&&mutation&&(!oldTimeline.Paused||((kind==OwnedFlightWorkerInvocation.ColdMutation||kind==OwnedFlightWorkerInvocation.CheckpointReplacement)&&oldTimeline.DebtSeconds!=0)))
                throw new InvalidOperationException("Cold invocation requires pause; physical replacement also requires zero debt.");
            if(!internalCommand&&(kind==OwnedFlightWorkerInvocation.ColdMutation||kind==OwnedFlightWorkerInvocation.SameTimeMutation)&&oldTimeline.PendingEvents>0)
                throw new InvalidOperationException("Resolve queued physical events before unrelated cold/code mutations.");
            context.CanReplace=!internalCommand&&(kind==OwnedFlightWorkerInvocation.ColdMutation||kind==OwnedFlightWorkerInvocation.CheckpointReplacement);
            try
            {
                T result=action(context);var after=context.Host.World.Stamp;
                bool changed=!ReferenceEquals(oldHost,context.Host)||!before.Matches(after)||code!=context.Host.CodeGeneration||configuration!=context.Host.Systems.ConfigurationRevision;
                if(!internalCommand&&(changed||kind==OwnedFlightWorkerInvocation.SameTimeMutation))
                {
                    if(kind==OwnedFlightWorkerInvocation.ReadOnly)throw new InvalidOperationException("Read-only invocation mutated execution state.");
                    if(kind==OwnedFlightWorkerInvocation.TimelineMutation)
                    {
                        if(!ReferenceEquals(oldHost,context.Host)||code!=context.Host.CodeGeneration||context.Timeline.ReadProgress().ConfigurationRevision!=context.Host.Systems.ConfigurationRevision||!context.Timeline.ReadProgress().Stamp.Matches(after))
                            throw new InvalidOperationException("Timeline invocation did not acknowledge its own unchanged-host physical transaction.");
                    }
                    else if(ReferenceEquals(oldTimeline,context.Timeline))
                    {
                        if(kind==OwnedFlightWorkerInvocation.SameTimeMutation)
                            context.Timeline.CompleteEvent(before,Wall(null),"worker same-time command",deadline);
                        else if(!context.Timeline.ReadProgress().Stamp.Matches(after)||context.Timeline.ReadProgress().ConfigurationRevision!=context.Host.Systems.ConfigurationRevision||context.Timeline.ReadProgress().CodeGeneration!=context.Host.CodeGeneration)
                            context.Timeline.Synchronize(context.Timeline.ReadProgress().Stamp,Wall(null),"worker cold command");
                    }
                    lock(gate)fault=null;
                }
                Publish();return result;
            }
            catch(Exception error)
            {
                if(!ReferenceEquals(oldHost,context.Host)||!before.Matches(context.Host.World.Stamp)||code!=context.Host.CodeGeneration||configuration!=context.Host.Systems.ConfigurationRevision)
                    Fail(new InvalidOperationException("Command failed after publication; no generic rollback is claimed.",error));
                throw;
            }
            finally{context.CanReplace=false;}
        }
        OwnedFlightTimelineProgress Pump(double now)
        {
            var result=context.Timeline.Pump(now,maximumCommits,wallBudget);pumps++;
            if(result.Status=="blocked-readers")pressure++;
            Publish();return result;
        }
        void Publish()
        {
            controls=context.Timeline.HeldControls;
            CheckThread();var nextProgress=context.Timeline.ReadProgress();OwnedFlightReadDescriptor next=descriptor;
            if(!published||!ReferenceEquals(publishedWorld,context.Host.World)||!ReferenceEquals(publishedSession,context.Session)||!descriptor.Stamp.Matches(context.Host.World.Stamp))
                next=context.Session.CaptureReadDescriptor(context.Host.World.Stamp);
            if(!next.Stamp.Matches(nextProgress.Stamp))throw new InvalidOperationException("Timeline and publication bank differ.");
            lock(gate){descriptor=next;progress=nextProgress;publishedWorld=context.Host.World;publishedSession=context.Session;published=true;status=nextProgress.Status;if(nextProgress.Fault!=null)fault=nextProgress.Fault;Monitor.PulseAll(gate);}
        }
        public OwnedFlightPresentationLease AcquirePresentation()
        {
            lock(gate)
            {
                if(!published)return null;if(presentationHeld)throw new InvalidOperationException("One external presentation lease may be retained at a time.");
                OwnedRuntimeReadLease read;if(!publishedWorld.TryAcquireRead(descriptor.Stamp,out read)){gaps++;return null;}
                presentationHeld=true;return new OwnedFlightPresentationLease(this,read,descriptor,progress);
            }
        }
        internal void ReleasePresentation(){lock(gate){presentationHeld=false;Monitor.PulseAll(gate);}}
        void Fail(Exception error){lock(gate){fault=error.ToString();status="faulted";Monitor.PulseAll(gate);}}
        void Run()
        {
            threadId=Thread.CurrentThread.ManagedThreadId;
            try
            {
                context=initialize();if(context==null||context.Owner!=null)throw new ArgumentException("Initialization did not produce a fresh worker context.");
                context.Host.BindWorkerThread();context.Owner=this;context.Timeline=new OwnedFlightTimeline(context.Host,context.Session,wallClock(),maximumStep,sourceId);
                Publish();lock(gate){ready=true;Monitor.PulseAll(gate);}
                while(true)
                {
                    Request request=null;
                    lock(gate)
                    {
                        if(stopping)break;
                        if(count>0){request=queue[head];queue[head]=null;head=(head+1)%queue.Length;count--;}
                    }
                    if(request!=null)
                    {if(request.Execute(this))Interlocked.Increment(ref completed);else Interlocked.Increment(ref cancelled);continue;}
                    bool failed;lock(gate)failed=fault!=null;
                    var current=context.Timeline.ReadProgress();
                    if(!failed&&current.Fault==null&&!current.Paused&&(automatic||current.DebtSeconds>0))
                    {
                        var result=Pump(automatic?wallClock():current.WallSeconds);
                        if(result.Fault!=null)Fail(new InvalidOperationException(result.Fault));
                        if(result.BudgetExhausted&&result.DebtSeconds>=maximumStep){Thread.Yield();continue;}
                    }
                    lock(gate)
                    {
                        if(stopping)break;if(count>0)continue;
                        var next=context.Timeline.ReadProgress();int wait=Timeout.Infinite;
                        if(next.Status=="blocked-readers")wait=idleWait;
                        else if(automatic&&!next.Paused&&fault==null&&!next.DeadlineReached&&next.Status!="deadline")
                        {
                            double remaining=Math.Max(0,context.Timeline.NextLocalIntervalSeconds-next.DebtSeconds)/next.Warp;
                            wait=Math.Max(1,Math.Min(idleWait,(int)Math.Min(int.MaxValue,Math.Ceiling(remaining*1000))));
                        }
                        Monitor.Wait(gate,wait);
                    }
                }
            }
            catch(Exception error){Fail(error);}
            finally
            {
                lock(gate)
                {
                    while(count>0){queue[head].Cancel();queue[head]=null;head=(head+1)%queue.Length;count--;cancelled++;}
                    stopped=true;status=fault==null?"stopped":"faulted-stopped";Monitor.PulseAll(gate);
                }
            }
        }
        public bool Stop(int timeoutMilliseconds)
        {
            if(timeoutMilliseconds<0)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            if(Thread.CurrentThread.ManagedThreadId==threadId)throw new InvalidOperationException("Worker cannot join itself.");
            lock(gate){stopping=true;Monitor.PulseAll(gate);}return thread.Join(timeoutMilliseconds);
        }
        public void Dispose(){if(!Stop(5000))throw new TimeoutException("Worker shutdown remains pending; no thread abort or incomplete transaction is claimed.");}
    }
}
