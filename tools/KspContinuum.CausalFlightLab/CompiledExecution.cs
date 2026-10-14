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
    sealed class ExecutionRow
    {
        public string scenario="",mode="",deadlineStrategy="",physicalSha256="",status="failed";
        public int repetition,parts,bodies,actuators,surfaces;
        public long preparationTicks,coldTicks,hotTicks,hotAllocatedBytes,acceptedIntervals;
        public double startTime,endTime,energyPerKilogram,angularMomentumMagnitude;
        public double[] center=null!,velocity=null!;
        public Dictionary<string,long> systemWork=null!,bankWork=null!;
        public OwnedRuntimeMechanicalState mechanical=null!;
        public OwnedRuntimeAdaptiveObservation adaptive;
        public OwnedRuntimeActuatorBoundary lastBoundary;
        public bool restoredExactly;
    }
    static Dictionary<string,long> CountFields(object value)
    {
        var result=new Dictionary<string,long>();
        foreach(var field in value.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance))
            if(field.FieldType==typeof(long)&&field.Name!="ClockFrequency")result.Add(field.Name,(long)field.GetValue(value)!);
        return result;
    }
    static Dictionary<string,long> Difference(Dictionary<string,long> after,Dictionary<string,long> before)
    {return after.ToDictionary(pair=>pair.Key,pair=>pair.Value-before.GetValueOrDefault(pair.Key));}
    static void AdvanceTo(OwnedFlightWorkerContext c,double target,OwnedRuntimeControls controls,double limit)
    {
        int count=0;
        while(c.Host.World.Stamp.Time<target)
        {
            Need(++count<=100000,"Execution horizon exhausted100000 physical intervals.");
            var previous=c.Host.World.Stamp;var next=c.Session.AdvanceLocal(c.Host,Math.Min(limit,target-previous.Time),controls);
            Need(next.Time>previous.Time&&next.Time<=target,"Execution did not preserve its physical horizon.");
        }
        Need(c.Host.World.Stamp.Time==target,"Execution missed exact horizon.");
    }
    static OwnedFlightCheckpointData ExecutionCheckpoint(OwnedFlightWorkerContext c,OwnedRuntimeControls controls)
    {
        var state=(State)c.State;
        return OwnedFlightCheckpoint.Capture(c.Host,state.Imported,c.Session,controls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"");
    }
    static ExecutionRow ExecuteCase(Inputs input,string scenario,string mode,int repetition,string output)
    {
        var row=new ExecutionRow{scenario=scenario,mode=mode,repetition=repetition};long prepared=Stopwatch.GetTimestamp();
        var c=Initialize(input);var body=c.Session.Context;Vec radial=body.SunDirection*(1/Magnitude(body.SunDirection));
        Vec normal=Magnitude(body.AngularVelocity)>0?body.AngularVelocity:new Vec(0,0,1);
        Vec tangent=Vec.Cross(normal,radial);tangent=tangent*(1/Magnitude(tangent));
        bool atmosphere=scenario.StartsWith("atmosphere",StringComparison.Ordinal),powered=scenario.EndsWith("powered",StringComparison.Ordinal);
        if(atmosphere){Vec center=radial*(body.Radius+35000);c.Session.Place(c.Host.World.Stamp,center,Vec.Cross(body.AngularVelocity,center)+tangent*300);}
        else c.Session.PlaceCircular(c.Host.World.Stamp,100000,radial,normal);
        var controls=new OwnedRuntimeControls{Throttle=powered?.35:0,Attitude=powered?new Vec(.05,-.025,.02):new Vec(),Translation=powered?new Vec(.05,0,.025):new Vec(),EnginesEnabled=powered,RcsEnabled=powered,WheelsEnabled=powered};
        AdvanceTo(c,.2,controls,.02);
        bool compiled=mode!="reference",embedded=mode.StartsWith("embedded",StringComparison.Ordinal),adaptive=mode=="trials"||embedded;
        c.Host.Systems.SetExecutionStrategy(compiled?OwnedRuntimeExecutionStrategy.Compiled:OwnedRuntimeExecutionStrategy.Reference);
        if(compiled)c.Host.Systems.CompileExecutionPlan(c.Host.World.Stamp);
        c.Host.Systems.SetDeadlineStrategy(c.Host.World.Stamp,mode=="embedded-retained"?OwnedRuntimeDeadlineStrategy.Retained:OwnedRuntimeDeadlineStrategy.ExactOracle);
        row.deadlineStrategy=c.Host.Systems.DeadlineStrategy.ToString();
        c.Host.Systems.SetMechanicalPolicy(c.Host.World.Stamp,new OwnedRuntimeMechanicalPolicy(adaptive,method:embedded?OwnedRuntimeMechanicalMethod.EmbeddedMidpoint:OwnedRuntimeMechanicalMethod.StepDoubling));
        c.Host.World.SetWorkObservation(c.Host.World.Stamp,true);c.Host.Systems.SetWorkObservation(true);
        row.preparationTicks=Stopwatch.GetTimestamp()-prepared;
        long begin=Stopwatch.GetTimestamp();AdvanceTo(c,.3,controls,adaptive?.1:.02);row.coldTicks=Stopwatch.GetTimestamp()-begin;
        var beforeSystems=CountFields(c.Host.Systems.Counters);var beforeBank=CountFields(c.Host.World.Work);long revision=c.Host.World.Stamp.StateRevision;
        long allocated=GC.GetAllocatedBytesForCurrentThread();begin=Stopwatch.GetTimestamp();row.startTime=c.Host.World.Stamp.Time;
        AdvanceTo(c,1.3,controls,adaptive?.1:.02);row.hotTicks=Stopwatch.GetTimestamp()-begin;row.hotAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        row.endTime=c.Host.World.Stamp.Time;row.acceptedIntervals=c.Host.World.Stamp.StateRevision-revision;
        row.systemWork=Difference(CountFields(c.Host.Systems.Counters),beforeSystems);row.bankWork=Difference(CountFields(c.Host.World.Work),beforeBank);
        row.mechanical=c.Host.World.Mechanical;row.adaptive=c.Host.AdaptiveObservation;row.lastBoundary=c.Host.Systems.LastActuatorBoundary;
        using(var read=c.Host.World.AcquireRead())
        {
            row.parts=read.PartCount;row.bodies=read.BodyCount;row.actuators=c.Host.Systems.ActuatorCount;row.surfaces=c.Host.Systems.SurfaceCount;
            var region=read.GetRegion(0);Vec center=c.Session.CenterPosition(0);row.center=new[]{center.X,center.Y,center.Z};row.velocity=new[]{region.Motion.Velocity.X,region.Motion.Velocity.Y,region.Motion.Velocity.Z};
            row.energyPerKilogram=.5*ScalarProduct(region.Motion.Velocity,region.Motion.Velocity)-body.Mu/Magnitude(center);
            row.angularMomentumMagnitude=Magnitude(Vec.Cross(center,region.Motion.Velocity));
        }
        var checkpoint=ExecutionCheckpoint(c,controls);byte[] encoded=OwnedRuntimeCheckpointBinary.EncodeFlight(checkpoint);
        string path=Path.Combine(output,scenario+"-"+mode+"-"+repetition+".ofcb");using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write))file.Write(encoded);
        var restored=OwnedFlightCheckpoint.Restore(c.Host,((State)c.State).Imported,OwnedRuntimeCheckpointBinary.DecodeFlight(encoded),input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256,"");
        var restoredPacket=OwnedFlightCheckpoint.Capture(restored.Host,restored.Imported,restored.Session,restored.Controls,restored.Plan,restored.Pilot,"");
        row.restoredExactly=encoded.SequenceEqual(OwnedRuntimeCheckpointBinary.EncodeFlight(restoredPacket));Need(row.restoredExactly,"Execution strategy/method checkpoint did not restore exactly.");
        checkpoint.host.state.executionStrategy=0;checkpoint.host.state.deadlineStrategy=0;checkpoint.host.state.systemsRevision=0;
        row.physicalSha256=Hash(OwnedRuntimeCheckpointBinary.EncodeFlight(checkpoint));row.status="passed";return row;
    }
    static object CompiledExecution(Inputs input,string output,string executionModes)
    {
        bool adaptiveOnly=executionModes=="adaptive",deadlineOnly=executionModes=="deadlines",compiledOnly=executionModes=="compiled";
        var rows=new List<ExecutionRow>();var comparisons=new List<object>();
        foreach(string scenario in deadlineOnly?new[]{"vacuum-powered","atmosphere-powered"}:new[]{"vacuum-quiet","vacuum-powered","atmosphere-quiet","atmosphere-powered"})
        for(int repetition=1;repetition<=2;repetition++)
        {
            foreach(string mode in deadlineOnly?new[]{"embedded-oracle","embedded-retained"}:adaptiveOnly?new[]{"trials","embedded"}:compiledOnly?new[]{"reference","compiled"}:new[]{"reference","compiled","trials","embedded"})
            {
                var row=ExecuteCase(input,scenario,mode,repetition,output);rows.Add(row);
                using var file=new FileStream(Path.Combine(output,scenario+"-"+mode+"-"+repetition+".json"),FileMode.CreateNew,FileAccess.Write);JsonSerializer.Serialize(file,row,Json);
            }
            var group=rows.Where(row=>row.scenario==scenario&&row.repetition==repetition).ToArray();
            if(deadlineOnly)
            {
                bool exact=group[0].physicalSha256==group[1].physicalSha256;
                comparisons.Add(new{scenario,repetition,retainedVersusOracleRatio=(double)group[1].hotTicks/group[0].hotTicks,completePhysicalContinuationExact=exact,positionDifferenceMeters=Distance(group[1].center,group[0].center),velocityDifferenceMetersPerSecond=Distance(group[1].velocity,group[0].velocity),scope="Same compiled embedded numerical operator, deliberate deadline scheduling treatment. Complete normalized equality reported with raw packets retained; only strategy/configuration metadata excluded. No renderer or unchanged matrix rerun."});
            }
            else if(adaptiveOnly)comparisons.Add(new{scenario,repetition,embeddedVersusTrialsRatio=(double)group[1].hotTicks/group[0].hotTicks,embeddedVersusTrialsPositionMeters=Distance(group[1].center,group[0].center),embeddedVersusTrialsVelocityMetersPerSecond=Distance(group[1].velocity,group[0].velocity),scope="Changed compact-boundary code qualification; unchanged reference/compiled cases are retained from previous exact-source evidence, not rerun."});
            else
            {
                Need(group[0].physicalSha256==group[1].physicalSha256,"Compiled fold changed complete physical/resource/provider/thermal continuation.");
                if(compiledOnly){comparisons.Add(new{scenario,repetition,compiledBitwiseEquivalent=true,compiledHotRatio=(double)group[1].hotTicks/group[0].hotTicks,scope="Targeted reference/compiled fold comparison; unchanged numerical strategies are omitted. Full normalized continuation excludes only declared execution/configuration metadata."});continue;}
                comparisons.Add(new{scenario,repetition,compiledBitwiseEquivalent=true,compiledHotRatio=(double)group[1].hotTicks/group[0].hotTicks,embeddedVersusTrialsRatio=(double)group[3].hotTicks/group[2].hotTicks,
                    embeddedVersusReferencePositionMeters=Distance(group[3].center,group[0].center),embeddedVersusReferenceVelocityMetersPerSecond=Distance(group[3].velocity,group[0].velocity),scope="Numerical method is a declared treatment; differences are reported. Exact fold gate excludes ONLY executionStrategy and systemsRevision treatment metadata."});
            }
        }
        return new{status="passed",rows,comparisons,scope="Direct captured-state full physical intervals on this net8 host, no renderer pacing. Every strategy gets identical reference warmup through.2, first numerical interval window through.3 recorded separately, then one simulated second of retained work. Preparation includes import/placement/warmup/plan; checkpoint observations excluded from hot timing and separately validated. Two sequential resolving observations per unchanged case; no confidence or original KSP FPS claim."};
    }
    static double Distance(double[] a,double[] b)
    {double x=a[0]-b[0],y=a[1]-b[1],z=a[2]-b[2];return Math.Sqrt(x*x+y*y+z*z);}
    static double ScalarProduct(Vec a,Vec b){return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
    static double Magnitude(Vec a){return Math.Sqrt(ScalarProduct(a,a));}
}
