using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using KspContinuum;

static partial class Program
{
    sealed class ForecastCase
    {
        public string name="",status="failed",failure="",scope="",checkpointSha256="",continuationSha256="";
        public long completeTicks,clockFrequency=Stopwatch.Frequency,localCommits,coastCommits;
        public double requestedRuntimeTime,reachedRuntimeTime,independentCrossingDelta,guardRadius,semimajorAxis,eccentricity,initialAnomaly;
        public double maximumThermalLag,maximumDensity;
        public bool sourceUnchanged,checkpointRestored,continuationExact;
        public object? coastWindow,lastLocalWindow,prediction;
        public List<object> forecasts=new(),slices=new();
        public OwnedFlightWorkerStatus worker;
    }
    sealed class ForecastPredictionObservation
    {
        public string Status="",Failure="";
        public long ElapsedTicks,ClockFrequency;
        public int Steps;
        public bool SourceUnchanged,sourceBinaryExact;
        public List<object> points=new();
    }
    static Vec ForecastUnit(Vec value)=>value*(1/Math.Sqrt(value.X*value.X+value.Y*value.Y+value.Z*value.Z));
    static double ForecastNorm(Vec value)=>Math.Sqrt(value.X*value.X+value.Y*value.Y+value.Z*value.Z);
    static double ForecastExtent(OwnedFlightWorkerContext c)
    {
        using var read=c.Host.World.AcquireRead();Need(read.RegionCount==1,"Forecast situations require the acquired single rigid region.");
        double extent=c.Session.Context.VesselBoundingRadius;
        for(int b=0;b<read.BodyCount;b++)extent=Math.Max(extent,ForecastNorm(read.GetBody(b).LocalPosition)+c.Session.Context.VesselBoundingRadius);
        return extent+ForecastNorm(read.GetRegion(0).Properties.CenterOfMass);
    }
    static OwnedFlightWorker ForecastWorker(Inputs input)
    {
        var worker=new OwnedFlightWorker(()=>Initialize(input),new OwnedFlightWorkerOptions{AutomaticProgress=false,WallClock=()=>0,MaximumCommitsPerPump=32,WallBudgetSeconds=.004});
        Need(worker.WaitUntilReady(60000),worker.ReadStatus().Fault??"Forecast worker did not initialize.");
        Await(worker.Invoke(c=>
        {
            for(int i=0;i<c.Host.Systems.ActuatorCount;i++)c.Host.Systems.SetEnabled(i,false);
            c.Host.Systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);
            c.Host.Systems.SetDeadlineStrategy(c.Host.World.Stamp,OwnedRuntimeDeadlineStrategy.Retained);
            c.Host.Systems.SetMechanicalPolicy(c.Host.World.Stamp,new OwnedRuntimeMechanicalPolicy(true,method:OwnedRuntimeMechanicalMethod.EmbeddedMidpoint));
            using(var read=c.Host.World.AcquireRead())for(int p=0;p<read.Definition.ProviderStateCount;p++)Need(read.GetProviderState(p)==0,"Passive forecast fixture requires acquired empty provider reservoirs.");
            var motions=c.Host.World.CaptureSnapshot().RegionMotion;for(int r=0;r<motions.Length;r++)motions[r].AngularMomentum=new Vec();
            c.Host.World.SetRegionMotions(c.Host.World.Stamp,motions,c.Host.World.Stamp.Time,c.Session.FrameId);
            return true;
        },OwnedFlightWorkerInvocation.ColdMutation));return worker;
    }
    static byte[] ForecastCheckpoint(OwnedFlightWorkerContext c)
    {
        var state=(State)c.State;
        return OwnedRuntimeCheckpointBinary.EncodeFlight(OwnedFlightCheckpoint.Capture(c.Host,state.Imported,c.Session,c.Timeline.HeldControls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"",timeline:c.Timeline));
    }
    static void ForecastRestore(OwnedFlightWorker worker,Inputs input,byte[] bytes)
    {
        Await(worker.Invoke(c=>
        {
            var prior=(State)c.State;var restored=OwnedFlightCheckpoint.Restore(c.Host,prior.Imported,OwnedRuntimeCheckpointBinary.DecodeFlight(bytes),input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256,"");
            c.Replace(restored.Host,restored.Session,new State{Imported=restored.Imported},restored.TimelineState);return true;
        },OwnedFlightWorkerInvocation.CheckpointReplacement));
    }
    static OwnedFlightTimelineProgress ForecastCompleteDemand(OwnedFlightWorker worker)
    {
        return Await(worker.Invoke(c=>
        {
            double wall=c.Timeline.ReadProgress().WallSeconds;
            c.Configure(new OwnedRuntimeControls(),1,false,wallSeconds:wall);
            var progress=c.Timeline.Pump(wall+.1,2048,double.PositiveInfinity);
            Need(progress.Fault==null,"Forecast continuation pump faulted.");
            c.Configure(new OwnedRuntimeControls(),1,true,wallSeconds:wall+.1);
            for(int i=0;i<2048&&c.Timeline.DebtSeconds>0;i++)
            {progress=c.Timeline.Step(Math.Min(.02,c.Timeline.DebtSeconds));Need(progress.Fault==null&&progress.LocalCommits==1,"Forecast continuation debt stalled.");}
            progress=c.Timeline.ReadProgress();Need(progress.DebtSeconds==0&&progress.Fault==null,"Forecast continuation exceeded its retained work budget.");return progress;
        },OwnedFlightWorkerInvocation.TimelineMutation));
    }
    static object ForecastCapture(OwnedFlightWorkerContext c,double horizon)
    {
        var before=ForecastCheckpoint(c);var forecast=OwnedFlightForecast.Capture(c.Session,c.Host.World.Stamp,horizon);forecast.RequireCurrent(c.Session);
        Need(before.SequenceEqual(ForecastCheckpoint(c)),"Advisory forecast changed physical/execution source.");
        var rows=new List<object>();double previous=0;
        for(int i=0;i<forecast.Count;i++)
        {
            var row=forecast.GetRow(i);Need(row.DeltaSeconds>0&&row.DeltaSeconds>=previous&&row.DeltaSeconds<=horizon,"Forecast positive/order/horizon contract differs.");previous=row.DeltaSeconds;
            using var read=c.Host.World.AcquireRead();int region=c.Session.FindRegion(row.RegionId);var state=read.GetRegion(region);
            Vec p=c.Session.Origin+state.Motion.RootPosition+state.Motion.Orientation.Rotate(state.Properties.CenterOfMass),v=state.Motion.Velocity;
            double aa=v.X*v.X+v.Y*v.Y+v.Z*v.Z,bb=2*(p.X*v.X+p.Y*v.Y+p.Z*v.Z),cc=p.X*p.X+p.Y*p.Y+p.Z*p.Z-row.RadiusMeters*row.RadiusMeters;
            double disc=bb*bb-4*aa*cc;Need(aa>0&&disc>=0,"Returned crossing has no independent constant-velocity root.");
            double root1=(-bb-Math.Sqrt(disc))/(2*aa),root2=(-bb+Math.Sqrt(disc))/(2*aa),expected=root1>0?root1:root2;
            Need(expected>0&&Math.Abs(expected-row.DeltaSeconds)<=1e-5*Math.Max(1,expected),"Forecast row disagrees with independent quadratic root.");
            rows.Add(new{row.RegionId,kind=row.Kind.ToString(),row.RadiusMeters,row.DeltaSeconds,direction=row.Direction.ToString(),row.InitiallyOnBoundary,position=V(p),velocity=V(v),independentDelta=expected});
        }
        return new{source=forecast.SourceStamp,forecast.ContextSha256,forecast.FrameId,forecast.Model,forecast.UniversalTime,forecast.HorizonSeconds,forecast.SurfaceGuardAlreadyReached,forecast.FirstPositiveDelta,rows,sourceSha256=Hash(before),sourceUnchanged=true};
    }
    static double[] V(Vec v)=>new[]{v.X,v.Y,v.Z};
    static object ForecastSlice(OwnedFlightWorkerContext c,ForecastCase result)
    {
        using var read=c.Host.World.AcquireRead();var region=read.GetRegion(0);var thermal=c.Host.World.CaptureSnapshot().Thermal??throw new InvalidDataException("Missing thermal state.");
        Vec center=c.Session.Origin+region.Motion.RootPosition+region.Motion.Orientation.Rotate(region.Properties.CenterOfMass);
        var sample=c.Session.Context.EvaluateSample(center,c.Session.UniversalTime,read.Stamp.Time);
        result.maximumDensity=Math.Max(result.maximumDensity,sample.Density);result.maximumThermalLag=Math.Max(result.maximumThermalLag,read.ThermalPendingDuration);
        Need(read.ThermalPendingDuration<=thermal.maximumLagSeconds+1e-12,"Thermal lag escaped the admitted slice bound.");
        return new{stamp=read.Stamp,universalTime=c.Session.UniversalTime,center=V(center),velocity=V(region.Motion.Velocity),sample.Density,sample.Temperature,integratedThermalTime=read.ThermalIntegratedTime,pendingThermalDuration=read.ThermalPendingDuration,thermal.advances,thermal.deferredSteps,thermal.segmentCount,thermal.causalRevision,thermal.integratedCausalRevision};
    }
    static ForecastCase ForecastCoast(Inputs input)
    {
        var result=new ForecastCase{name="elliptic-atmosphere-entry",scope="Acquired craft/body with authored descending ellipse; independent anomaly crossing at finite-size atmosphere guard. One target beyond guard partially coasts then continues local. No contact or radial/hyperbolic admission."};long begin=Stopwatch.GetTimestamp();OwnedFlightWorker? worker=null;
        try
        {
            worker=ForecastWorker(input);
            double targetDelta=Await(worker.Invoke(c=>
            {
                var body=c.Session.Context;Need(body.AtmosphereDepth>0,"Captured atmosphere required.");double guard=body.Radius+body.AtmosphereDepth+ForecastExtent(c);
                double peri=guard-Math.Max(1000,ForecastExtent(c)*2),apo=guard+Math.Max(2000,ForecastExtent(c)*4),a=(peri+apo)*.5,e=(apo-peri)/(apo+peri);
                double cosine=(1-guard/a)/e;Need(cosine>-1&&cosine<1,"Authored ellipse does not cross guard.");double crossingE=2*Math.PI-Math.Acos(cosine),initialE=Math.PI;
                double n=Math.Sqrt(body.Mu/(a*a*a)),crossing=((crossingE-e*Math.Sin(crossingE))-(initialE-e*Math.Sin(initialE)))/n;
                double radialSpeed=Math.Abs(a*e*Math.Sin(crossingE)*n/(1-e*Math.Cos(crossingE)));
                Vec radial=ForecastUnit(body.SunDirection),tangent=Vec.Cross(new Vec(0,0,1),radial);if(ForecastNorm(tangent)<1e-12)tangent=Vec.Cross(new Vec(0,1,0),radial);tangent=ForecastUnit(tangent);
                c.Session.Place(c.Host.World.Stamp,radial*apo,tangent*Math.Sqrt(body.Mu*(2/apo-1/a)));
                result.guardRadius=guard;result.semimajorAxis=a;result.eccentricity=e;result.initialAnomaly=initialE;result.independentCrossingDelta=crossing;
                return crossing+Math.Max(2,(ForecastExtent(c)+200)/radialSpeed);
            },OwnedFlightWorkerInvocation.ColdMutation));
            result.forecasts.Add(Await(worker.Invoke(c=>
            {
                byte[] before=ForecastCheckpoint(c);OwnedRuntimeMotion[] unused;double time;string reason;
                bool accepted=c.Session.PrepareCoast(c.Host.World.Stamp,c.Session.UniversalTime+targetDelta,new OwnedRuntimeControls(),true,double.PositiveInfinity,0,out unused,out time,out reason);
                Need(!accepted&&before.SequenceEqual(ForecastCheckpoint(c)),"Strict crossing coast must reject without source publication.");return new{kind="strict-coast-rejection",reason,sourceUnchanged=true,requestedDelta=targetDelta,checkpointSha256=Hash(before)};
            })));result.sourceUnchanged=true;
            var firstAndWindow=Await(worker.Invoke(c=>
            {
                c.Configure(new OwnedRuntimeControls(),1,false,true,true,wallSeconds:0);
                var first=c.Timeline.Pump(targetDelta,1,double.PositiveInfinity);var window=c.Timeline.LastCoastWindow;
                c.Configure(new OwnedRuntimeControls(),1,true,true,true,wallSeconds:targetDelta);
                return (first,window);
            },OwnedFlightWorkerInvocation.TimelineMutation));
            var first=firstAndWindow.first;var window=firstAndWindow.window;result.requestedRuntimeTime=first.RequestedRuntimeTime;result.coastWindow=window;
            Need(first.Fault==null&&first.CoastCommits==1,first.Fault??"First bounded commit was not a partial coast.");
            Need(window.Limited&&window.ReachedUniversalTime>input.Context.sourceEpochSeconds&&window.ReachedUniversalTime<window.RequestedUniversalTime,"Crossing request did not retain a positive partial coast window.");
            double oracle=input.Context.sourceEpochSeconds+result.independentCrossingDelta;
            Need(window.LowerCrossingUniversalTime<=oracle+1e-4&&window.UpperCrossingUniversalTime>=oracle-1e-4,"Numeric crossing bracket disagrees with independent elliptic anomaly.");
            Need(window.ReachedUniversalTime<=window.LowerCrossingUniversalTime-.02+1e-7,"Partial coast did not preserve outside lead.");
            result.localCommits+=first.LocalCommits;result.coastCommits+=first.CoastCommits;
            Await(worker.Configure(new OwnedRuntimeControls(),1,false,true,true,wallSeconds:targetDelta));
            for(int i=0;i<16384&&worker.ReadStatus().Progress.DebtSeconds>0;i++)
            {
                var before=worker.ReadStatus().Progress;var p=Await(worker.RequestDemand(targetDelta));Need(p.Fault==null,p.Fault??"Entry continuation faulted.");
                result.localCommits+=p.LocalCommits;result.coastCommits+=p.CoastCommits;
                result.slices.Add(Await(worker.Invoke(c=>ForecastSlice(c,result))));
                if(p.Status=="awaiting-local-quantum")break;
                Need(p.CommittedRuntimeTime>before.CommittedRuntimeTime||p.EventApplications>0||p.BudgetExhausted,"Entry continuation stalled without a work boundary.");
            }
            Await(worker.SetPause(true));
            for(int i=0;i<64&&worker.ReadStatus().Progress.DebtSeconds>0;i++){var p=Await(worker.Step(Math.Min(.02,worker.ReadStatus().Progress.DebtSeconds)));Need(p.Fault==null&&p.LocalCommits==1,"Entry semantic tail failed.");result.localCommits+=p.LocalCommits;}
            var finalProgress=worker.ReadStatus().Progress;result.localCommits=finalProgress.TotalLocalCommits;result.coastCommits=finalProgress.TotalCoastCommits;
            result.lastLocalWindow=Await(worker.Invoke(c=>c.Timeline.LastLocalWindow));result.slices.Add(Await(worker.Invoke(c=>ForecastSlice(c,result))));
            result.reachedRuntimeTime=finalProgress.CommittedRuntimeTime;Need(finalProgress.DebtSeconds==0&&result.reachedRuntimeTime==result.requestedRuntimeTime&&result.localCommits>0&&result.coastCommits>0&&result.maximumDensity>0,"Entry request did not complete coast plus actual atmosphere local work.");result.status="passed";
        }
        catch(Exception error){result.failure=error.ToString();}
        finally{if(worker!=null){worker.Dispose();result.worker=worker.ReadStatus();}result.completeTicks=Stopwatch.GetTimestamp()-begin;}return result;
    }
    static ForecastCase ForecastAtmosphere(Inputs input,string output)
    {
        var result=new ForecastCase{name="atmosphere-forecast-and-thermal-continuation",scope="Captured table constant-velocity advisory roots, disposable local prediction/source immutability, bounded thermal slices and exact pending-ledger binary continuation; not future force parity or global trajectory guarantee."};long begin=Stopwatch.GetTimestamp();OwnedFlightWorker? worker=null;
        try
        {
            worker=ForecastWorker(input);
            Await(worker.Invoke(c=>
            {
                var capture=c.Session.Context.Capture();Need(capture.altitudeMeters.Length>=3&&c.Session.Context.AtmosphereDepth>0,"Captured atmosphere table required.");
                int knot=capture.altitudeMeters.Length/2;double altitude=capture.altitudeMeters[knot]+1;Need(altitude>ForecastExtent(c)+100&&altitude<c.Session.Context.AtmosphereDepth,"Table knot is not an admitted no-contact airborne fixture.");
                Vec radial=ForecastUnit(c.Session.Context.SunDirection),center=radial*(c.Session.Context.Radius+altitude);
                c.Session.Place(c.Host.World.Stamp,center,Vec.Cross(c.Session.Context.AngularVelocity,center)+radial*-20);
                c.Host.Systems.SetThermalPolicy(c.Host.World.Stamp,new OwnedRuntimeThermalPolicy(true,.06,.25));return true;
            },OwnedFlightWorkerInvocation.ColdMutation));
            result.forecasts.Add(Await(worker.Invoke(c=>{var forecast=OwnedFlightForecast.Capture(c.Session,c.Host.World.Stamp,.1);Need(forecast.Count>0,"Authored table-knot crossing absent.");return ForecastCapture(c,.1);})));result.sourceUnchanged=true;
            var predictionObservation=Await(worker.Invoke(c=>
            {
                var state=(State)c.State;var bytes=ForecastCheckpoint(c);long region;using(var read=c.Host.World.AcquireRead())region=read.GetRegion(0).Id;
                var prediction=OwnedFlightPrediction.Predict(c.Session,c.Host,state.Imported,new OwnedRuntimeControls(),new OwnedRuntimeStepPlan(.02),region,.1,OwnedFlightPredictionMode.LocalPhysical,maximumSteps:2048,maximumWallSeconds:30);
                bool sourceExact=bytes.SequenceEqual(ForecastCheckpoint(c));
                var points=new List<object>();for(int i=0;i<prediction.Count;i++){var p=prediction.GetPoint(i);points.Add(new{p.UniversalTime,center=V(p.CenterPosition),velocity=V(p.Velocity)});}
                return new ForecastPredictionObservation{Status=prediction.Status,Failure=prediction.Failure??"",ElapsedTicks=prediction.ElapsedTicks,ClockFrequency=prediction.ClockFrequency,Steps=prediction.Steps,SourceUnchanged=prediction.SourceUnchanged,sourceBinaryExact=sourceExact,points=points};
            }));
            result.prediction=predictionObservation;
            Need(predictionObservation.Status=="completed"&&predictionObservation.SourceUnchanged&&predictionObservation.sourceBinaryExact,
                "Disposable local forecast rejected: status="+predictionObservation.Status+", sourceUnchanged="+predictionObservation.SourceUnchanged+", sourceBinaryExact="+predictionObservation.sourceBinaryExact+", failure="+predictionObservation.Failure);
            Await(worker.Step(.02));result.slices.Add(Await(worker.Invoke(c=>ForecastSlice(c,result))));
            byte[] pending=Await(worker.Invoke(ForecastCheckpoint));Need(worker.ReadStatus().Progress.Paused,"Pending ledger checkpoint must be paused.");
            Need(Await(worker.Invoke(c=>{using var r=c.Host.World.AcquireRead();return r.ThermalPendingDuration;}))>0,"Pending thermal ledger witness absent.");result.checkpointSha256=Hash(pending);
            MissionWriteBytes(Path.Combine(output,"forecast-pending.ofcb"),pending);
            var completed=ForecastCompleteDemand(worker);result.localCommits=completed.TotalLocalCommits;result.requestedRuntimeTime=completed.RequestedRuntimeTime;result.reachedRuntimeTime=completed.CommittedRuntimeTime;result.slices.Add(Await(worker.Invoke(c=>ForecastSlice(c,result))));
            byte[] final=Await(worker.Invoke(ForecastCheckpoint));MissionWriteBytes(Path.Combine(output,"forecast-continuation.ofcb"),final);ForecastRestore(worker,input,pending);Need(pending.SequenceEqual(Await(worker.Invoke(ForecastCheckpoint))),"Pending checkpoint did not restore exactly.");result.checkpointRestored=true;
            ForecastCompleteDemand(worker);
            Need(final.SequenceEqual(Await(worker.Invoke(ForecastCheckpoint))),"Thermal checkpoint continuation changed physical/execution bits.");result.continuationExact=true;result.continuationSha256=Hash(final);result.lastLocalWindow=Await(worker.Invoke(c=>c.Timeline.LastLocalWindow));result.status="passed";
        }
        catch(Exception error){result.failure=error.ToString();}
        finally{if(worker!=null){worker.Dispose();result.worker=worker.ReadStatus();}result.completeTicks=Stopwatch.GetTimestamp()-begin;}return result;
    }
    static object ForecastSituations(Inputs input,string output,string selector="all")
    {
        Need(selector=="all"||selector=="entry"||selector=="atmosphere","Forecast selector must be all, entry or atmosphere.");var cases=new List<ForecastCase>();
        if(selector!="atmosphere")cases.Add(ForecastCoast(input));if(selector!="entry")cases.Add(ForecastAtmosphere(input,output));
        foreach(var pin in input.Pins)Need(HashFile(pin.path)==pin.sha256,"Forecast source input changed.");
        var receipt=new{schema="continuum-owned-forecast-situations/v1",status=cases.All(c=>c.status=="passed")?"passed":"failed",selector,inputs=input.Pins,toolSha256=HashFile(typeof(Program).Assembly.Location),coreSha256=HashFile(typeof(OwnedFlightSession).Assembly.Location),cases,scope="One functional acquired-input scenario per selected situation. Complete clocks include worker dispatch/preparation/forecast/continuation and cleanup; no speedup or FPS inference. Original no-contact and elliptic-only coast admission retained."};
        using(var file=new FileStream(Path.Combine(output,"forecast-situations.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))JsonSerializer.Serialize(file,receipt,Json);
        Need(cases.All(c=>c.status=="passed"),string.Join("\n",cases.Where(c=>c.status!="passed").Select(c=>c.failure)));return receipt;
    }
}
