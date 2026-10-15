using System;

namespace KspContinuum
{
    public enum OwnedFlightEventKind { Impulse, Controls, ActuatorEnabled, ResourceFlowEnabled }

    public sealed class OwnedFlightEvent
    {
        public readonly long Sequence,RegionId;
        public readonly double Time;
        public readonly string Id;
        public readonly OwnedFlightEventKind Kind;
        public readonly Vec Impulse,Point;
        public readonly int Slot;
        public readonly bool Enabled,AtPoint;
        public readonly OwnedRuntimeControls Controls;
        public OwnedFlightEvent(string id,long sequence,double time,OwnedFlightEventKind kind,long regionId=0,
            Vec impulse=default(Vec),Vec point=default(Vec),bool atPoint=false,int slot=0,bool enabled=false,OwnedRuntimeControls controls=default(OwnedRuntimeControls))
        {
            if(string.IsNullOrWhiteSpace(id)||id.Length>128||sequence<1||slot<0||!Enum.IsDefined(typeof(OwnedFlightEventKind),kind))throw new ArgumentException("Physical event identity/type differs.");
            AssemblyModel.Finite(time);OwnedRuntimeDynamics.Require(impulse);OwnedRuntimeDynamics.Require(point);controls.Validate();
            if(kind==OwnedFlightEventKind.Impulse&&(regionId<=0||Dot(impulse,impulse)==0))throw new ArgumentException("Impulse requires an owned region and nonzero momentum.");
            Id=id;Sequence=sequence;Time=time;Kind=kind;RegionId=regionId;Impulse=impulse;Point=point;AtPoint=atPoint;Slot=slot;Enabled=enabled;Controls=controls;
        }
        static double Dot(Vec v,Vec w){return v.X*w.X+v.Y*w.Y+v.Z*w.Z;}
    }

    [Serializable] public sealed class OwnedFlightEventRow
    {
        public string id,kind;
        public long sequence,regionId;
        public double time;
        public double[] impulse,point,controls;
        public int slot;
        public bool enabled,atPoint,enginesEnabled,rcsEnabled,wheelsEnabled;
    }
    [Serializable] public sealed class OwnedFlightEventHistory
    {
        public string id,kind;
        public long sequence,beforeEpoch,beforeRevision,afterEpoch,afterRevision;
        public double time;
    }
    [Serializable] public sealed class OwnedFlightEventQueueData
    {
        public string schema="continuum-owned-flight-events/v1",sourceId,scheduleSha256,frameId;
        public long epoch,topologyRevision,definitionRevision,frameRevision,codeGeneration,configurationRevision,lastSequence;
        public int capacity;
        public OwnedFlightEventRow[] pending;
        public OwnedFlightEventHistory[] history;
        public OwnedFlightEventRow controls;
    }
    public struct OwnedFlightEventApplication
    {
        public readonly string Id;
        public readonly OwnedFlightEventKind Kind;
        public readonly OwnedRuntimeStamp Before,After;
        public readonly OwnedRuntimeControls Controls;
        internal OwnedFlightEventApplication(OwnedFlightEvent e,OwnedRuntimeStamp before,OwnedRuntimeStamp after,OwnedRuntimeControls controls)
        {Id=e.Id;Kind=e.Kind;Before=before;After=after;Controls=controls;}
    }

    public sealed class OwnedFlightEventQueue
    {
        public const int MaximumCapacity=4096;
        readonly OwnedRuntimeHost host;
        readonly OwnedFlightEvent[] pending;
        readonly OwnedFlightEventHistory[] history;
        readonly string sourceId;
        string scheduleSha;
        long epoch,topology,definition,frame,code,configuration,lastSequence;
        string frameId;
        int count,historyCount;
        OwnedRuntimeControls controls;
        public int Count {get{return count;}}
        public int AppliedCount {get{return historyCount;}}
        public OwnedRuntimeControls Controls {get{return controls;}}
        public double NextTime {get{return count==0?double.PositiveInfinity:pending[0].Time;}}
        public OwnedRuntimeActuatorBoundary FindActuatorBoundary(OwnedRuntimeStamp expected,double maximumHorizonSeconds)
        {
            RequireOwner(expected);var result=host.Systems.FindActuatorBoundary(expected,controls,maximumHorizonSeconds);
            if(!result.SourceStamp.Matches(expected)||result.ConfigurationRevision!=configuration)throw new InvalidOperationException("Actuator boundary source changed during preparation.");
            return result;
        }
        public OwnedFlightEventQueue(OwnedRuntimeHost host,string sourceId,int capacity=256,OwnedRuntimeControls controls=default(OwnedRuntimeControls))
        {
            if(host==null||string.IsNullOrWhiteSpace(sourceId)||sourceId.Length>256||capacity<1||capacity>MaximumCapacity)throw new ArgumentException("Event queue owner/bounds differ.");
            controls.Validate();this.host=host;this.sourceId=sourceId;this.controls=controls;pending=new OwnedFlightEvent[capacity];history=new OwnedFlightEventHistory[capacity];
            scheduleSha=host.ScheduleSourceSha256;code=host.CodeGeneration;configuration=host.Systems.ConfigurationRevision;BindStamp(host.World.Stamp);
        }
        void BindStamp(OwnedRuntimeStamp s)
        {epoch=s.Epoch;topology=s.TopologyRevision;definition=s.DefinitionRevision;frame=s.FrameRevision;frameId=s.FrameId;}
        public void AcknowledgeBoundary(OwnedRuntimeStamp expected)
        {
            host.World.RequireWriterThread();
            if(count!=0||!host.World.Stamp.Matches(expected)||!host.Systems.IsBoundTo(host.World))throw new InvalidOperationException("Boundary acknowledgment requires an empty queue and exact current owner.");
            scheduleSha=host.ScheduleSourceSha256;code=host.CodeGeneration;configuration=host.Systems.ConfigurationRevision;BindStamp(expected);
        }
        void RequireOwner(OwnedRuntimeStamp expected)
        {
            if(!host.World.Stamp.Matches(expected))throw new InvalidOperationException("Physical event current frontier is stale.");
            RequireSource(expected);
        }
        void RequireSource(OwnedRuntimeStamp expected)
        {
            if(expected.Epoch!=epoch||expected.TopologyRevision!=topology||expected.DefinitionRevision!=definition||expected.FrameRevision!=frame||expected.FrameId!=frameId
                ||host.ScheduleSourceSha256!=scheduleSha||host.CodeGeneration!=code||host.Systems.ConfigurationRevision!=configuration||!host.Systems.IsBoundTo(host.World))
                throw new InvalidOperationException("Physical event source, topology, code or configuration is stale.");
        }
        public void AcknowledgeCoastAdvance(OwnedRuntimeStamp before,OwnedRuntimeStamp after)
        {
            host.World.RequireWriterThread();RequireSource(before);
            if(!host.World.Stamp.Matches(after)||after.Time<=before.Time||after.Time>NextTime||after.Epoch!=checked(before.Epoch+1)||after.StateRevision!=checked(before.StateRevision+1)
                ||after.TopologyRevision!=before.TopologyRevision||after.DefinitionRevision!=before.DefinitionRevision||after.FrameRevision!=before.FrameRevision||after.FrameId!=before.FrameId)
                throw new InvalidOperationException("Coast acknowledgment differs from one admitted advance or crosses a pending event.");
            BindStamp(after);
        }
        public void Enqueue(OwnedFlightEvent value)
        {
            if(value==null)throw new ArgumentNullException("value");host.World.RequireWriterThread();RequireOwner(host.World.Stamp);
            if(value.Time<host.World.Stamp.Time||value.Sequence<=lastSequence||count+historyCount>=pending.Length)throw new InvalidOperationException("Event is passed, repeated or beyond retained capacity.");
            for(int i=0;i<count;i++)if(pending[i].Id==value.Id)throw new ArgumentException("Event ID already queued.");
            for(int i=0;i<historyCount;i++)if(history[i].id==value.Id)throw new ArgumentException("Event ID already applied.");
            ValidateTarget(value);
            int at=count;while(at>0&&(pending[at-1].Time>value.Time||(pending[at-1].Time==value.Time&&pending[at-1].Sequence>value.Sequence))){pending[at]=pending[at-1];at--;}
            pending[at]=value;count++;lastSequence=value.Sequence;
        }
        void ValidateTarget(OwnedFlightEvent e)
        {
            if(e.Kind==OwnedFlightEventKind.ActuatorEnabled&&e.Slot>=host.Systems.ActuatorCount)throw new ArgumentException("Actuator slot absent.");
            if(e.Kind==OwnedFlightEventKind.ResourceFlowEnabled&&e.Slot>=host.World.ResourceCount)throw new ArgumentException("Resource slot absent.");
            if(e.Kind==OwnedFlightEventKind.Impulse)using(var read=host.World.AcquireRead()){bool found=false;for(int r=0;r<read.RegionCount;r++)found|=read.GetRegion(r).Id==e.RegionId;if(!found)throw new ArgumentException("Impulse region absent.");}
        }
        public OwnedFlightEventApplication ApplyNext(OwnedRuntimeStamp expected)
        {
            host.World.RequireWriterThread();RequireOwner(expected);
            if(count==0||pending[0].Time!=expected.Time)throw new InvalidOperationException("Event application requires its exact physical frontier; deadlines cannot be skipped.");
            var e=pending[0];ValidateTarget(e);
            var ledger=new OwnedFlightEventHistory{id=e.Id,kind=e.Kind.ToString(),sequence=e.Sequence,time=e.Time,beforeEpoch=expected.Epoch,beforeRevision=expected.StateRevision};
            OwnedRuntimeStamp after=expected;
            if(e.Kind==OwnedFlightEventKind.Impulse)
            {
                using(var read=host.World.AcquireRead())
                {
                    if(!read.Stamp.Matches(expected))throw new InvalidOperationException("Impulse read frontier changed.");
                    var motions=new OwnedRuntimeMotion[read.RegionCount];
                    for(int r=0;r<motions.Length;r++)
                    {
                        var state=read.GetRegion(r);var motion=state.Motion;
                        if(state.Id==e.RegionId)
                        {
                            motion.Velocity=motion.Velocity+e.Impulse*(1/state.Properties.Mass);
                            if(e.AtPoint){Vec center=motion.RootPosition+motion.Orientation.Rotate(state.Properties.CenterOfMass);motion.AngularMomentum=motion.AngularMomentum+Vec.Cross(e.Point+center*-1,e.Impulse);}
                        }
                        motions[r]=motion;
                    }
                    after=host.World.SetRegionMotions(expected,motions,expected.Time,expected.FrameId);
                }
            }
            else if(e.Kind==OwnedFlightEventKind.ActuatorEnabled)host.Systems.SetEnabled(e.Slot,e.Enabled);
            else if(e.Kind==OwnedFlightEventKind.ResourceFlowEnabled)host.Systems.SetResourceFlowEnabled(e.Slot,e.Enabled);
            else controls=e.Controls;
            configuration=host.Systems.ConfigurationRevision;BindStamp(after);ledger.afterEpoch=after.Epoch;ledger.afterRevision=after.StateRevision;history[historyCount++]=ledger;
            for(int i=1;i<count;i++)pending[i-1]=pending[i];pending[--count]=null;
            return new OwnedFlightEventApplication(e,expected,after,controls);
        }
        public OwnedFlightEventQueueData Capture()
        {
            RequireOwner(host.World.Stamp);var rows=new OwnedFlightEventRow[count];for(int i=0;i<count;i++)rows[i]=Row(pending[i]);
            var past=new OwnedFlightEventHistory[historyCount];for(int i=0;i<past.Length;i++){var h=history[i];past[i]=new OwnedFlightEventHistory{id=h.id,kind=h.kind,sequence=h.sequence,time=h.time,beforeEpoch=h.beforeEpoch,beforeRevision=h.beforeRevision,afterEpoch=h.afterEpoch,afterRevision=h.afterRevision};}
            return new OwnedFlightEventQueueData{sourceId=sourceId,scheduleSha256=scheduleSha,epoch=epoch,topologyRevision=topology,definitionRevision=definition,frameRevision=frame,frameId=frameId,
                codeGeneration=code,configurationRevision=configuration,lastSequence=lastSequence,capacity=pending.Length,pending=rows,history=past,controls=Row(new OwnedFlightEvent("controls",1,host.World.Stamp.Time,OwnedFlightEventKind.Controls,controls:controls))};
        }
        public static OwnedFlightEventQueue Restore(OwnedRuntimeHost host,OwnedFlightEventQueueData data,string expectedSourceId)
        {
            if(data==null||data.schema!="continuum-owned-flight-events/v1"||data.sourceId!=expectedSourceId||data.pending==null||data.history==null||data.pending.Length+data.history.Length>data.capacity||data.lastSequence<0)throw new ArgumentException("Event checkpoint identity/bounds differ.");
            var controlEvent=FromRow(data.controls);if(controlEvent.Kind!=OwnedFlightEventKind.Controls||controlEvent.Id!="controls"||controlEvent.Sequence!=1||controlEvent.Time!=host.World.Stamp.Time)throw new ArgumentException("Event checkpoint control state differs.");
            var queue=new OwnedFlightEventQueue(host,expectedSourceId,data.capacity,controlEvent.Controls);
            if(queue.scheduleSha!=data.scheduleSha256||queue.code!=data.codeGeneration||queue.configuration!=data.configurationRevision||queue.epoch!=data.epoch||queue.topology!=data.topologyRevision||queue.definition!=data.definitionRevision||queue.frame!=data.frameRevision||queue.frameId!=data.frameId)throw new InvalidOperationException("Event checkpoint source differs.");
            for(int i=0;i<data.history.Length;i++)
            {
                var h=data.history[i];OwnedFlightEventKind kind;
                if(h==null||string.IsNullOrWhiteSpace(h.id)||h.id.Length>128||!Enum.TryParse(h.kind,out kind)||!Enum.IsDefined(typeof(OwnedFlightEventKind),kind)||h.kind!=kind.ToString()||h.sequence<1||h.sequence>data.lastSequence||h.time>host.World.Stamp.Time||h.beforeEpoch<0||h.beforeRevision<0||h.afterEpoch<h.beforeEpoch||h.afterRevision<h.beforeRevision||h.afterEpoch>data.epoch||h.afterRevision>host.World.Stamp.StateRevision)throw new ArgumentException("Event history row differs.");
                AssemblyModel.Finite(h.time);for(int j=0;j<i;j++)if(queue.history[j].id==h.id||queue.history[j].sequence==h.sequence||(queue.history[j].time>h.time)||(queue.history[j].time==h.time&&queue.history[j].sequence>h.sequence))throw new ArgumentException("Event history order/identity differs.");
                if(kind==OwnedFlightEventKind.Impulse?(h.afterEpoch!=h.beforeEpoch+1||h.afterRevision!=h.beforeRevision+1):(h.afterEpoch!=h.beforeEpoch||h.afterRevision!=h.beforeRevision))throw new ArgumentException("Event history publication differs from its typed operation.");
                if(i>0&&(queue.history[i-1].afterEpoch>h.beforeEpoch||queue.history[i-1].afterRevision>h.beforeRevision))throw new ArgumentException("Event history causal frontier regresses.");
                queue.history[queue.historyCount++]=new OwnedFlightEventHistory{id=h.id,kind=h.kind,sequence=h.sequence,time=h.time,beforeEpoch=h.beforeEpoch,beforeRevision=h.beforeRevision,afterEpoch=h.afterEpoch,afterRevision=h.afterRevision};
            }
            long previous=0;
            for(int i=0;i<data.pending.Length;i++)
            {
                var e=FromRow(data.pending[i]);if(e.Sequence>data.lastSequence)throw new ArgumentException("Event sequence exceeds checkpoint frontier.");
                for(int j=0;j<i;j++)if(queue.pending[j].Id==e.Id||queue.pending[j].Sequence==e.Sequence)throw new ArgumentException("Repeated pending event.");
                for(int j=0;j<queue.historyCount;j++)if(queue.history[j].id==e.Id||queue.history[j].sequence==e.Sequence)throw new ArgumentException("Pending event already applied.");
                if(e.Time<host.World.Stamp.Time||(i>0&&(queue.pending[i-1].Time>e.Time||(queue.pending[i-1].Time==e.Time&&previous>=e.Sequence))))throw new ArgumentException("Pending event order/time differs.");
                queue.ValidateTarget(e);queue.pending[queue.count++]=e;previous=e.Sequence;
            }
            queue.lastSequence=data.lastSequence;return queue;
        }
        static double[] V(Vec v){return new[]{v.X,v.Y,v.Z};}
        static Vec V(double[] v){if(v==null||v.Length!=3)throw new ArgumentException("Event vector shape differs.");return new Vec(v[0],v[1],v[2]);}
        static OwnedFlightEventRow Row(OwnedFlightEvent e)
        {return new OwnedFlightEventRow{id=e.Id,kind=e.Kind.ToString(),sequence=e.Sequence,regionId=e.RegionId,time=e.Time,impulse=V(e.Impulse),point=V(e.Point),slot=e.Slot,enabled=e.Enabled,atPoint=e.AtPoint,
            controls=new[]{e.Controls.Throttle,e.Controls.Attitude.X,e.Controls.Attitude.Y,e.Controls.Attitude.Z,e.Controls.Translation.X,e.Controls.Translation.Y,e.Controls.Translation.Z},enginesEnabled=e.Controls.EnginesEnabled,rcsEnabled=e.Controls.RcsEnabled,wheelsEnabled=e.Controls.WheelsEnabled};}
        static OwnedFlightEvent FromRow(OwnedFlightEventRow row)
        {
            OwnedFlightEventKind kind;if(row==null||!Enum.TryParse(row.kind,out kind)||row.kind!=kind.ToString()||row.controls==null||row.controls.Length!=7)throw new ArgumentException("Event row shape/type differs.");
            var controls=new OwnedRuntimeControls{Throttle=row.controls[0],Attitude=new Vec(row.controls[1],row.controls[2],row.controls[3]),Translation=new Vec(row.controls[4],row.controls[5],row.controls[6]),EnginesEnabled=row.enginesEnabled,RcsEnabled=row.rcsEnabled,WheelsEnabled=row.wheelsEnabled};
            return new OwnedFlightEvent(row.id,row.sequence,row.time,kind,row.regionId,V(row.impulse),V(row.point),row.atPoint,row.slot,row.enabled,controls);
        }
    }
}
