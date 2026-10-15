using System;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedFlightTimelineSegmentState
    {
        public double end;
        public bool closed,allowCoast,passiveFrozen;
        public OwnedFlightEventRow controls;
    }
    [Serializable] public sealed class OwnedFlightTimelineState
    {
        public string schema="continuum-owned-flight-timeline-state/v1",sourceId,contextSha256,scheduleSha256,frameId,fault,status;
        public long epoch,stateRevision,topologyRevision,definitionRevision,frameRevision,codeGeneration,configurationRevision,inputRevision;
        public long totalLocalCommits,totalCoastCommits,controlResolutions,supersededPendingControlSegments,totalEventsApplied;
        public double time,requestedRuntimeTime,maximumReferenceStepSeconds,warp,lastLiveInputRuntimeTime,deadlineUniversalTime,coastLeadSeconds;
        public bool paused,hasDeadline,allowCoast,passiveFrozen;
        public OwnedFlightEventRow policy;
        public OwnedFlightTimelineSegmentState[] segments;
        public OwnedFlightEventQueueData events;

        internal static OwnedFlightEventRow EncodeControls(OwnedRuntimeControls c)
        {return new OwnedFlightEventRow{id="timeline-policy",kind="Controls",sequence=1,impulse=new double[3],point=new double[3],controls=new[]{c.Throttle,c.Attitude.X,c.Attitude.Y,c.Attitude.Z,c.Translation.X,c.Translation.Y,c.Translation.Z},enginesEnabled=c.EnginesEnabled,rcsEnabled=c.RcsEnabled,wheelsEnabled=c.WheelsEnabled};}
        internal static OwnedRuntimeControls DecodeControls(OwnedFlightEventRow row)
        {
            if(row==null||row.kind!="Controls"||row.controls==null||row.controls.Length!=7)throw new ArgumentException("Timeline controls shape/type differs.");
            var c=new OwnedRuntimeControls {Throttle=row.controls[0],Attitude=new Vec(row.controls[1],row.controls[2],row.controls[3]),Translation=new Vec(row.controls[4],row.controls[5],row.controls[6]),EnginesEnabled=row.enginesEnabled,RcsEnabled=row.rcsEnabled,WheelsEnabled=row.wheelsEnabled};c.Validate();return c;
        }
    }
}
