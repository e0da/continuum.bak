using System;
using System.Diagnostics;
using System.Threading;

namespace KspContinuum
{
    public sealed class OwnedFlightManeuverPlan
    {
        public readonly string Id, ContextSha256, ScheduleSha256, ScheduleImplementation;
        public readonly OwnedRuntimeStamp SourceStamp;
        public readonly long RegionId, CodeGeneration, ConfigurationRevision;
        public readonly double EventUniversalTime;
        public readonly Vec DeltaVelocity;
        internal readonly OwnedRuntimeWorld SourceWorld;
        public OwnedFlightManeuverPlan(string id,OwnedFlightSession session,OwnedRuntimeHost host,long regionId,
            double eventUniversalTime,Vec deltaVelocity)
        {
            if(string.IsNullOrWhiteSpace(id)||id.Length>256)throw new ArgumentException("Maneuver identity is required.");
            if(session==null||host==null||!ReferenceEquals(session.Runtime,host.World))throw new ArgumentException("Maneuver owner differs.");
            AssemblyModel.Finite(eventUniversalTime);OwnedRuntimeDynamics.Require(deltaVelocity);
            if(eventUniversalTime<session.UniversalTime)throw new ArgumentException("Maneuver event has already passed.");
            session.FindRegion(regionId);Id=id;SourceWorld=session.Runtime;SourceStamp=session.Runtime.Stamp;
            ContextSha256=session.Context.SourceSha256;RegionId=regionId;EventUniversalTime=eventUniversalTime;DeltaVelocity=deltaVelocity;
            CodeGeneration=host.CodeGeneration;ConfigurationRevision=host.Systems.ConfigurationRevision;
            ScheduleSha256=host.ScheduleSourceSha256;ScheduleImplementation=host.ScheduleImplementation;
        }
        public bool IsCurrent(OwnedFlightSession session,OwnedRuntimeHost host)
        {
            return session!=null&&host!=null&&ReferenceEquals(session.Runtime,SourceWorld)&&ReferenceEquals(host.World,SourceWorld)
                &&SourceStamp.Matches(session.Runtime.Stamp)&&session.Context.SourceSha256==ContextSha256
                &&host.CodeGeneration==CodeGeneration&&host.Systems.ConfigurationRevision==ConfigurationRevision
                &&host.ScheduleSourceSha256==ScheduleSha256&&host.ScheduleImplementation==ScheduleImplementation
                &&session.UniversalTime<=EventUniversalTime;
        }
        public void ApplyNow(OwnedFlightSession session,OwnedRuntimeHost host)
        {
            RequireCurrent(session,host);
            if(Math.Abs(session.UniversalTime-EventUniversalTime)>1e-8)throw new InvalidOperationException("Impulse is not at its admitted event epoch.");
            var motions=CopyMotions(session.Runtime);int region=session.FindRegion(RegionId);
            motions[region].Velocity=motions[region].Velocity+DeltaVelocity;
            session.Runtime.SetRegionMotions(SourceStamp,motions,SourceStamp.Time,session.FrameId);
        }
        public void ExecuteCoastManeuver(OwnedFlightSession session,OwnedRuntimeHost host,OwnedRuntimeControls controls,bool passiveStateFrozen)
        {
            RequireCurrent(session,host);
            if(!host.SupportsPassiveKeplerCoast)throw new InvalidOperationException("Loaded executable schedule has no passive Kepler contract.");
            if(ConfigurationRevision!=host.Systems.ConfigurationRevision)throw new InvalidOperationException("Actuator configuration changed.");
            OwnedRuntimeMotion[] motions;double time;string reason;
            if(!session.PrepareCoast(SourceStamp,EventUniversalTime,controls,passiveStateFrozen,double.PositiveInfinity,0,out motions,out time,out reason))
                throw new InvalidOperationException("Coast-to-node rejected: "+reason);
            int region=session.FindRegion(RegionId);motions[region].Velocity=motions[region].Velocity+DeltaVelocity;
            RequireCurrent(session,host);
            session.PublishCoast(SourceStamp,motions,time);
        }
        void RequireCurrent(OwnedFlightSession session,OwnedRuntimeHost host)
        {if(!IsCurrent(session,host))throw new InvalidOperationException("Maneuver source, code, configuration or event epoch is stale.");}
        internal static OwnedRuntimeMotion[] CopyMotions(OwnedRuntimeWorld world)
        {using(var read=world.AcquireRead()){var result=new OwnedRuntimeMotion[read.RegionCount];for(int r=0;r<result.Length;r++)result[r]=read.GetRegion(r).Motion;return result;}}
    }

    public enum OwnedFlightPredictionMode { LocalPhysical, VacuumCoast }
    public struct OwnedFlightPredictionPoint
    {
        public double UniversalTime,AltitudeMeters;
        public Vec CenterPosition,RootPosition,Velocity;
        public OwnedRuntimeStamp Stamp;
    }
    public sealed class OwnedFlightPredictionResult
    {
        internal OwnedFlightPredictionPoint[] points;
        internal OwnedRuntimeWorld sourceWorld;
        public string Status,Failure,ContextSha256,ScheduleSha256,ScheduleImplementation;
        public OwnedRuntimeStamp SourceStamp;
        public long RegionId,CodeGeneration,ConfigurationRevision,ElapsedTicks;
        public readonly long ClockFrequency=Stopwatch.Frequency;
        public int Steps;
        public bool SourceUnchanged;
        public OwnedFlightPredictionMode Mode;
        public int Count{get{return points.Length;}}
        public OwnedFlightPredictionPoint GetPoint(int index){return points[index];}
        public bool IsCurrent(OwnedFlightSession session,OwnedRuntimeHost host)
        {
            return SourceUnchanged&&session!=null&&host!=null&&ReferenceEquals(sourceWorld,session.Runtime)
                &&ReferenceEquals(sourceWorld,host.World)&&SourceStamp.Matches(session.Runtime.Stamp)
                &&ContextSha256==session.Context.SourceSha256&&CodeGeneration==host.CodeGeneration
                &&ConfigurationRevision==host.Systems.ConfigurationRevision&&ScheduleSha256==host.ScheduleSourceSha256
                &&ScheduleImplementation==host.ScheduleImplementation;
        }
    }

    public static class OwnedFlightPrediction
    {
        public const int MaximumSteps=8192;
        public static OwnedFlightPredictionResult Predict(OwnedFlightSession session,OwnedRuntimeHost host,
            OwnedRuntimeImportResult imported,OwnedRuntimeControls controls,OwnedRuntimeStepPlan plan,long regionId,
            double horizonSeconds,OwnedFlightPredictionMode mode,bool passiveStateFrozen=false,
            OwnedFlightManeuverPlan maneuver=null,CancellationToken cancellation=default(CancellationToken),
            int maximumSteps=MaximumSteps,double maximumWallSeconds=5)
        {
            if(session==null||host==null||imported==null||plan==null||!ReferenceEquals(session.Runtime,host.World)
                ||!ReferenceEquals(imported.World,host.World)||!ReferenceEquals(imported.Systems,host.Systems))throw new ArgumentException("Prediction owner differs.");
            controls.Validate();AssemblyModel.Positive(horizonSeconds);AssemblyModel.Positive(maximumWallSeconds);
            if(horizonSeconds>7200||maximumWallSeconds>30||maximumSteps<1||maximumSteps>MaximumSteps||!Enum.IsDefined(typeof(OwnedFlightPredictionMode),mode))
                throw new ArgumentOutOfRangeException("Prediction bounds");
            session.FindRegion(regionId);
            if(mode==OwnedFlightPredictionMode.VacuumCoast&&!host.SupportsPassiveKeplerCoast)throw new InvalidOperationException("Loaded executable schedule has no passive Kepler contract.");
            if(maneuver!=null&&(!maneuver.IsCurrent(session,host)||maneuver.EventUniversalTime>session.UniversalTime+horizonSeconds))
                throw new InvalidOperationException("Prediction maneuver is stale or outside horizon.");
            var result=new OwnedFlightPredictionResult {Status="failed",sourceWorld=session.Runtime,SourceStamp=session.Runtime.Stamp,
                ContextSha256=session.Context.SourceSha256,ScheduleSha256=host.ScheduleSourceSha256,ScheduleImplementation=host.ScheduleImplementation,
                RegionId=regionId,CodeGeneration=host.CodeGeneration,ConfigurationRevision=host.Systems.ConfigurationRevision,Mode=mode};
            long start=Stopwatch.GetTimestamp();int count=0;
            var buffer=new OwnedFlightPredictionPoint[maximumSteps+2];
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var checkpoint=host.CaptureCheckpoint(imported,controls,session.EvaluateRegion(session.FindRegion(regionId)),plan);
                OwnedRuntimeRestoredState state;var forkHost=host.RestoreCheckpoint(checkpoint,imported.Source.nativeManifestSha256,imported.Source.partSpecsSha256,out state);
                var fork=new OwnedFlightSession(state.World,session.Context,session.Origin,session.RuntimeEpoch);
                double end=session.UniversalTime+horizonSeconds;AssemblyModel.Finite(end);
                bool applied=false;Record(fork,regionId,buffer,ref count);
                while(fork.UniversalTime<end)
                {
                    if(cancellation.IsCancellationRequested){result.Status="cancelled";break;}
                    if(result.Steps>=maximumSteps||(Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency>=maximumWallSeconds)
                    {result.Status="budget-exhausted";break;}
                    double interval=mode==OwnedFlightPredictionMode.VacuumCoast?
                        Math.Max(plan.Delta,horizonSeconds/Math.Min(maximumSteps,256)):plan.Delta;
                    double target=Math.Min(end,fork.UniversalTime+interval);
                    if(maneuver!=null&&!applied)target=Math.Min(target,maneuver.EventUniversalTime);
                    if(target>fork.UniversalTime)
                    {
                        if(mode==OwnedFlightPredictionMode.VacuumCoast)
                        {
                            string reason;
                            if(!fork.TryCoast(fork.Runtime.Stamp,target,controls,passiveStateFrozen,double.PositiveInfinity,0,out reason))
                                throw new InvalidOperationException("Prediction coast rejected: "+reason);
                        }
                        else fork.AdvanceLocal(forkHost,target-fork.UniversalTime,controls);
                        result.Steps++;
                    }
                    if(maneuver!=null&&!applied&&Math.Abs(fork.UniversalTime-maneuver.EventUniversalTime)<=1e-8)
                    {
                        var motions=OwnedFlightManeuverPlan.CopyMotions(fork.Runtime);int selected=fork.FindRegion(maneuver.RegionId);
                        motions[selected].Velocity=motions[selected].Velocity+maneuver.DeltaVelocity;
                        fork.Runtime.SetRegionMotions(fork.Runtime.Stamp,motions,fork.Runtime.Stamp.Time,fork.FrameId);applied=true;
                    }
                    Record(fork,regionId,buffer,ref count);
                }
                if(fork.UniversalTime>=end)result.Status="completed";
            }
            catch(OperationCanceledException){result.Status="cancelled";}
            catch(Exception error){result.Status="failed";result.Failure=error.Message;}
            finally
            {
                result.ElapsedTicks=Stopwatch.GetTimestamp()-start;result.SourceUnchanged=result.IsSourceCurrent(session,host);
                result.points=new OwnedFlightPredictionPoint[count];Array.Copy(buffer,result.points,count);
                if(!result.SourceUnchanged){result.Status="source-changed";result.Failure="Authoritative source changed during prediction.";}
            }
            return result;
        }

        static bool IsSourceCurrent(this OwnedFlightPredictionResult result,OwnedFlightSession session,OwnedRuntimeHost host)
        {return result.SourceStamp.Matches(session.Runtime.Stamp)&&result.ContextSha256==session.Context.SourceSha256
            &&result.CodeGeneration==host.CodeGeneration&&result.ConfigurationRevision==host.Systems.ConfigurationRevision
            &&result.ScheduleSha256==host.ScheduleSourceSha256&&result.ScheduleImplementation==host.ScheduleImplementation;}
        static void Record(OwnedFlightSession session,long regionId,OwnedFlightPredictionPoint[] points,ref int count)
        {
            int slot=session.FindRegion(regionId);using(var read=session.Runtime.AcquireRead())
            {
                points[count++]=new OwnedFlightPredictionPoint {UniversalTime=session.UniversalTime,CenterPosition=session.CenterPosition(slot),
                    RootPosition=session.BodyPosition(slot),Velocity=read.GetRegion(slot).Motion.Velocity,Stamp=read.Stamp,
                    AltitudeMeters=session.Context.Altitude(session.CenterPosition(slot))};
            }
        }
    }
}
