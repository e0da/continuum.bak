using System;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedFlightPilotData
    {
        public string mode="manual";
        public long regionId;
        public double responseSeconds=2,maximumRate=.1;
        public double[] targetOrientation=new[]{0d,0,0,1},targetVelocity=new double[3];
    }
    [Serializable] public sealed class OwnedFlightCheckpointData
    {
        public string schema="continuum-owned-flight-checkpoint/v1",visualSha256;
        public OwnedRuntimeHostCheckpoint host;
        public OwnedFlightSessionData flight;
        public OwnedFlightPilotData pilot;
        public OwnedFlightManeuverData maneuver;
        public OwnedFlightTimelineState timeline;
    }
    [Serializable] public sealed class OwnedFlightManeuverData
    {public string id;public long regionId;public double universalTime;public double[] deltaVelocity;}
    public sealed class OwnedFlightRestoredState
    {
        public OwnedRuntimeHost Host;
        public OwnedRuntimeImportResult Imported;
        public OwnedFlightSession Session;
        public OwnedRuntimeControls Controls;
        public OwnedRuntimeStepPlan Plan;
        public OwnedFlightPilotData Pilot;
        public OwnedFlightManeuverPlan Maneuver;
        public OwnedFlightTimelineState TimelineState;
    }
    public static class OwnedFlightCheckpoint
    {
        public static OwnedFlightPilotData CopyPilot(OwnedFlightPilotData value)
        {
            if(value==null||(value.mode!="manual"&&value.mode!="attitude"&&value.mode!="velocity"))throw new ArgumentException("Flight pilot mode differs.");
            AssemblyModel.Positive(value.responseSeconds);AssemblyModel.Positive(value.maximumRate);
            OwnedRuntimeCheckpoint.Q(value.targetOrientation);OwnedRuntimeCheckpoint.V(value.targetVelocity);
            return new OwnedFlightPilotData {mode=value.mode,regionId=value.regionId,responseSeconds=value.responseSeconds,maximumRate=value.maximumRate,
                targetOrientation=(double[])value.targetOrientation.Clone(),targetVelocity=(double[])value.targetVelocity.Clone()};
        }
        public static OwnedFlightCheckpointData Capture(OwnedRuntimeHost host,OwnedRuntimeImportResult imported,OwnedFlightSession session,
            OwnedRuntimeControls controls,OwnedRuntimeStepPlan plan,OwnedFlightPilotData pilot,string visualSha256,OwnedFlightManeuverPlan maneuver=null,OwnedFlightTimeline timeline=null)
        {
            if(host==null||session==null||!ReferenceEquals(host.World,session.Runtime))throw new ArgumentException("Flight checkpoint owner differs.");
            if(!string.IsNullOrEmpty(visualSha256))OwnedRuntimeImport.Digest(visualSha256);
            var copied=CopyPilot(pilot);if(copied.mode!="manual")session.FindRegion(copied.regionId);
            var stamp=host.World.Stamp;
            if(timeline!=null)controls=timeline.HeldControls;
            var result=new OwnedFlightCheckpointData {visualSha256=visualSha256??"",pilot=copied,flight=session.Capture(),
                host=host.CaptureCheckpoint(imported,controls,session.EvaluateRegion(0),plan)};
            if(timeline!=null)
            {if(!ReferenceEquals(timeline.Host,host)||!ReferenceEquals(timeline.Session,session))throw new ArgumentException("Execution checkpoint owner differs.");result.timeline=timeline.CaptureState();result.schema="continuum-owned-flight-checkpoint/v2";}
            if(maneuver!=null)
            {
                if(!maneuver.IsCurrent(session,host))throw new InvalidOperationException("Pending maneuver is stale; clear/replan before checkpointing.");
                result.maneuver=new OwnedFlightManeuverData {id=maneuver.Id,regionId=maneuver.RegionId,universalTime=maneuver.EventUniversalTime,deltaVelocity=OwnedRuntimeCheckpoint.V(maneuver.DeltaVelocity)};
            }
            if(!host.World.Stamp.Matches(stamp))throw new InvalidOperationException("Flight checkpoint source changed during capture.");
            return result;
        }
        public static OwnedFlightRestoredState Restore(OwnedRuntimeHost current,OwnedRuntimeImportResult imported,OwnedFlightCheckpointData data,
            string nativeSha,string partSha,string expectedContextSha,string expectedVisualSha)
        {
            if(current==null||imported==null||!ReferenceEquals(current.World,imported.World)||data==null||(data.schema!="continuum-owned-flight-checkpoint/v1"&&data.schema!="continuum-owned-flight-checkpoint/v2")
                ||data.flight==null||data.flight.contextSha256!=expectedContextSha||data.visualSha256!=(expectedVisualSha??""))
                throw new InvalidOperationException("Flight checkpoint source/context/visual binding differs.");
            if((data.schema=="continuum-owned-flight-checkpoint/v2")!=(data.timeline!=null))throw new InvalidOperationException("Flight execution checkpoint presence differs.");
            var copied=CopyPilot(data.pilot);
            OwnedRuntimeRestoredState restored;var nextHost=current.RestoreCheckpoint(data.host,nativeSha,partSha,out restored);
            var session=OwnedFlightSession.Restore(restored.World,data.flight,nativeSha,partSha);
            if(copied.mode!="manual")session.FindRegion(copied.regionId);
            var nextImported=new OwnedRuntimeImportResult {World=restored.World,Systems=restored.Systems,Source=restored.Source,
                CapturedParts=imported.CapturedParts,NativeScene=imported.NativeScene,ColliderGeometry=imported.ColliderGeometry};
            OwnedFlightManeuverPlan node=data.maneuver==null?null:new OwnedFlightManeuverPlan(data.maneuver.id,session,nextHost,data.maneuver.regionId,data.maneuver.universalTime,OwnedRuntimeCheckpoint.V(data.maneuver.deltaVelocity));
            return new OwnedFlightRestoredState {Host=nextHost,Imported=nextImported,Session=session,Controls=restored.Controls,Plan=restored.Plan,Pilot=copied,Maneuver=node,TimelineState=data.timeline};
        }
    }
}
