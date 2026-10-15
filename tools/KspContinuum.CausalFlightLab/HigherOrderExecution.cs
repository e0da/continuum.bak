using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using KspContinuum;

static partial class Program
{
    sealed class HigherRegion
    {
        public long id;
        public double mass;
        public double[] center=null!,velocity=null!,orientation=null!,momentum=null!,inertia=null!;
    }
    sealed class HigherState
    {
        public HigherRegion[] regions=null!;
        public double[] resources=null!,providers=null!,temperature=null!;
        public double thermalIntegratedTime,thermalPendingDuration;
    }
    sealed class HigherRow
    {
        public string scenario="",mode="",status="failed",failure="",checkpointSha256="";
        public int repetition;
        public long coldTicks,hotTicks,allocatedBytes,acceptedIntervals;
        public long hotTrials,hotRejected,hotHigherOrderAttempts,hotHigherOrderForceSamples,hotHigherOrderFallbacks,hotMidpointForceSamples;
        public double startTime,endTime,maximumInterval;
        public Dictionary<string,long> systemWork=null!,bankWork=null!;
        public OwnedRuntimeAdaptiveObservation observation;
        public OwnedRuntimeMechanicalState mechanical=null!;
        public HigherState final=null!;
        public bool checkpointExact;
    }
    static OwnedFlightWorkerContext HigherSeed(Inputs input,string scenario)
    {
        var c=Initialize(input);var context=c.Session.Context;
        Vec radial=context.SunDirection*(1/Magnitude(context.SunDirection)),normal=Magnitude(context.AngularVelocity)>0?context.AngularVelocity:new Vec(0,0,1);
        Vec tangent=Vec.Cross(normal,radial);Need(Magnitude(tangent)>0,"Higher-order fixture tangent is degenerate.");tangent=tangent*(1/Magnitude(tangent));
        if(scenario.StartsWith("atmosphere",StringComparison.Ordinal))
        {Need(context.AtmosphereDepth>35000,"Acquired atmosphere must cover the frozen35km case.");Vec center=radial*(context.Radius+35000);c.Session.Place(c.Host.World.Stamp,center,Vec.Cross(context.AngularVelocity,center)+tangent*300);}
        else c.Session.PlaceCircular(c.Host.World.Stamp,100000,radial,normal);
        c.Host.Systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);c.Host.Systems.SetDeadlineStrategy(c.Host.World.Stamp,OwnedRuntimeDeadlineStrategy.Retained);
        return c;
    }
    static OwnedRuntimeControls HigherControls(string scenario)
    {
        bool powered=scenario.EndsWith("powered",StringComparison.Ordinal);
        return new OwnedRuntimeControls{Throttle=powered?.35:0,Attitude=powered?new Vec(.05,-.025,.02):new Vec(),Translation=powered?new Vec(.05,0,.025):new Vec(),EnginesEnabled=powered,RcsEnabled=powered,WheelsEnabled=powered};
    }
    static OwnedFlightWorkerContext HigherRestore(Inputs input,OwnedFlightWorkerContext source,byte[] seed)
    {
        var imported=((State)source.State).Imported;
        var restored=OwnedFlightCheckpoint.Restore(source.Host,imported,OwnedRuntimeCheckpointBinary.DecodeFlight(seed),input.Pins[0].sha256,input.Pins[1].sha256,input.Pins[2].sha256,"");
        return new OwnedFlightWorkerContext(restored.Host,restored.Session,new State{Imported=restored.Imported});
    }
    static HigherState HigherRead(OwnedFlightWorkerContext c)
    {
        using var read=c.Host.World.AcquireRead();var result=new HigherState{regions=new HigherRegion[read.RegionCount],resources=new double[read.ResourceCount],providers=new double[read.Definition.ProviderStateCount],temperature=new double[read.PartCount],thermalIntegratedTime=read.ThermalIntegratedTime,thermalPendingDuration=read.ThermalPendingDuration};
        for(int r=0;r<read.RegionCount;r++)
        {
            var state=read.GetRegion(r);var p=state.Properties;var m=state.Motion;Vec center=c.Session.Origin+m.RootPosition+m.Orientation.Rotate(p.CenterOfMass);var q=m.Orientation;var i=p.Inertia;
            result.regions[r]=new HigherRegion{id=state.Id,mass=p.Mass,center=new[]{center.X,center.Y,center.Z},velocity=new[]{m.Velocity.X,m.Velocity.Y,m.Velocity.Z},orientation=new[]{q.X,q.Y,q.Z,q.W},momentum=new[]{m.AngularMomentum.X,m.AngularMomentum.Y,m.AngularMomentum.Z},inertia=new[]{i.XX,i.XY,i.XZ,i.YY,i.YZ,i.ZZ}};
        }
        for(int r=0;r<result.resources.Length;r++)result.resources[r]=read.GetResourceQuantity(r);
        for(int p=0;p<result.providers.Length;p++)result.providers[p]=read.GetProviderState(p);
        for(int p=0;p<result.temperature.Length;p++)result.temperature[p]=read.GetTemperature(p);
        return result;
    }
    static HigherRow HigherRun(Inputs input,OwnedFlightWorkerContext seedOwner,byte[] seed,string scenario,string mode,int repetition,string output)
    {
        var row=new HigherRow{scenario=scenario,mode=mode,repetition=repetition};long begin=Stopwatch.GetTimestamp();
        try
        {
            var c=HigherRestore(input,seedOwner,seed);var controls=HigherControls(scenario);bool reference=mode=="fine-rk23";
            var method=mode=="midpoint"?OwnedRuntimeMechanicalMethod.EmbeddedMidpoint:OwnedRuntimeMechanicalMethod.HigherOrderRK23;
            row.maximumInterval=reference?.0001:.1;
            c.Host.Systems.SetMechanicalPolicy(c.Host.World.Stamp,new OwnedRuntimeMechanicalPolicy(true,minimumInterval:1e-6,maximumInterval:row.maximumInterval,initialInterval:reference?.0001:.02,method:method));
            c.Host.World.SetWorkObservation(c.Host.World.Stamp,true);c.Host.Systems.SetWorkObservation(true);
            double start=c.Host.World.Stamp.Time;AdvanceTo(c,start+.02,controls,row.maximumInterval);row.coldTicks=Stopwatch.GetTimestamp()-begin;
            var beforeSystems=CountFields(c.Host.Systems.Counters);var beforeBank=CountFields(c.Host.World.Work);var beforeObservation=c.Host.AdaptiveObservation;long revision=c.Host.World.Stamp.StateRevision;
            row.startTime=c.Host.World.Stamp.Time;long allocated=GC.GetAllocatedBytesForCurrentThread();begin=Stopwatch.GetTimestamp();
            AdvanceTo(c,start+.25,controls,row.maximumInterval);row.hotTicks=Stopwatch.GetTimestamp()-begin;row.allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            row.endTime=c.Host.World.Stamp.Time;row.acceptedIntervals=c.Host.World.Stamp.StateRevision-revision;
            row.systemWork=Difference(CountFields(c.Host.Systems.Counters),beforeSystems);row.bankWork=Difference(CountFields(c.Host.World.Work),beforeBank);
            row.observation=c.Host.AdaptiveObservation;row.mechanical=c.Host.World.Mechanical;row.final=HigherRead(c);
            row.hotTrials=row.observation.Trials-beforeObservation.Trials;row.hotRejected=row.observation.Rejected-beforeObservation.Rejected;row.hotHigherOrderAttempts=row.observation.HigherOrderAttempts-beforeObservation.HigherOrderAttempts;row.hotHigherOrderForceSamples=row.observation.HigherOrderForceSamples-beforeObservation.HigherOrderForceSamples;row.hotHigherOrderFallbacks=row.observation.HigherOrderFallbacks-beforeObservation.HigherOrderFallbacks;row.hotMidpointForceSamples=row.observation.EmbeddedForceSamples-beforeObservation.EmbeddedForceSamples;
            byte[] packet=OwnedRuntimeCheckpointBinary.EncodeFlight(ExecutionCheckpoint(c,controls));row.checkpointSha256=Hash(packet);
            string path=Path.Combine(output,scenario+"-"+mode+"-"+repetition+".ofcb");using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write))file.Write(packet);
            var restored=HigherRestore(input,c,packet);byte[] readback=OwnedRuntimeCheckpointBinary.EncodeFlight(ExecutionCheckpoint(restored,controls));row.checkpointExact=packet.SequenceEqual(readback);Need(row.checkpointExact,"Higher-order complete checkpoint continuation differs.");row.status="passed";
        }
        catch(Exception error){row.failure=error.ToString();}
        using(var file=new FileStream(Path.Combine(output,scenario+"-"+mode+"-"+repetition+".json"),FileMode.CreateNew,FileAccess.Write))JsonSerializer.Serialize(file,row,Json);
        return row;
    }
    static object HigherDifferences(HigherState actual,HigherState reference)
    {
        Need(actual.regions.Length==reference.regions.Length&&actual.resources.Length==reference.resources.Length&&actual.providers.Length==reference.providers.Length&&actual.temperature.Length==reference.temperature.Length,"Higher-order reference topology differs.");
        double position=0,velocity=0,angle=0,momentum=0,mass=0,inertia=0;
        for(int r=0;r<actual.regions.Length;r++)
        {
            var a=actual.regions[r];var b=reference.regions[r];Need(a.id==b.id,"Higher-order region lineage differs.");position=Math.Max(position,Distance(a.center,b.center));velocity=Math.Max(velocity,Distance(a.velocity,b.velocity));momentum=Math.Max(momentum,Distance(a.momentum,b.momentum));mass=Math.Max(mass,Math.Abs(a.mass-b.mass));inertia=Math.Max(inertia,HigherArrayDifference(a.inertia,b.inertia));
            double dot=0;for(int i=0;i<4;i++)dot+=a.orientation[i]*b.orientation[i];double sign=dot<0?-1:1,square=0;for(int i=0;i<4;i++){double d=a.orientation[i]-sign*b.orientation[i];square+=d*d;}angle=Math.Max(angle,4*Math.Asin(Math.Min(1,.5*Math.Sqrt(square))));
        }
        return new{positionMeters=position,velocityMetersPerSecond=velocity,angleRadians=angle,momentum,massKilograms=mass,inertiaKilogramMetersSquared=inertia,resourceUnits=HigherArrayDifference(actual.resources,reference.resources),providerState=HigherArrayDifference(actual.providers,reference.providers),temperatureKelvin=HigherArrayDifference(actual.temperature,reference.temperature),thermalIntegratedTimeDifference=Math.Abs(actual.thermalIntegratedTime-reference.thermalIntegratedTime),thermalPendingDurationDifference=Math.Abs(actual.thermalPendingDuration-reference.thermalPendingDuration),withinLocalBudgetNumbers=position<=1e-4&&velocity<=1e-4&&angle<=1e-6,scope="Endpoint differences versus separately charged finer execution of the same source model. Local tolerance numbers are comparison observations, not accumulated/global-error acceptance certificates."};
    }
    static double HigherArrayDifference(double[] a,double[] b)
    {Need(a.Length==b.Length,"Higher-order state array differs.");double maximum=0;for(int i=0;i<a.Length;i++){Need(double.IsFinite(a[i])&&double.IsFinite(b[i]),"Nonfinite higher-order physical state.");maximum=Math.Max(maximum,Math.Abs(a[i]-b[i]));}return maximum;}
    static object HigherOrderExecution(Inputs input,string output,bool poweredOnly=false)
    {
        Need(Path.IsPathFullyQualified(output)&&Directory.Exists(output),"Higher-order comparison requires an existing absolute output directory.");NoLinks(output);
        var rows=new List<HigherRow>();var comparisons=new List<object>();var seeds=new List<object>();
        foreach(string scenario in poweredOnly?new[]{"vacuum-powered","atmosphere-powered"}:new[]{"vacuum-quiet","atmosphere-quiet","vacuum-powered","atmosphere-powered"})
        {
            long begin=Stopwatch.GetTimestamp();var c=HigherSeed(input,scenario);var controls=HigherControls(scenario);byte[] seed=OwnedRuntimeCheckpointBinary.EncodeFlight(ExecutionCheckpoint(c,controls));
            seeds.Add(new{scenario,preparationTicks=Stopwatch.GetTimestamp()-begin,sourceCheckpointSha256=Hash(seed),declaredPreparation="Captured full craft/resources/provider state placed circular100km or Sunward35km/300m/s; no source spin/resource reservoir reset."});
            var fine=HigherRun(input,c,seed,scenario,"fine-rk23",0,output);rows.Add(fine);
            for(int repetition=1;repetition<=2;repetition++)
            {
                var midpoint=HigherRun(input,c,seed,scenario,"midpoint",repetition,output);var higher=HigherRun(input,c,seed,scenario,"rk23",repetition,output);rows.Add(midpoint);rows.Add(higher);
                if(fine.status=="passed"&&midpoint.status=="passed"&&higher.status=="passed")comparisons.Add(new{scenario,repetition,completeHotCostRatio=(double)higher.hotTicks/midpoint.hotTicks,higherVersusFine=HigherDifferences(higher.final,fine.final),midpointVersusFine=HigherDifferences(midpoint.final,fine.final),fallbacks=higher.observation.HigherOrderFallbacks,fallbackReason=higher.observation.LastHigherOrderFallback});
            }
        }
        foreach(var pin in input.Pins)Need(HashFile(pin.path)==pin.sha256,"Acquired input changed during higher-order comparison.");
        return new{schema="continuum-acquired-higher-order-comparison/v1",status=rows.All(r=>r.status=="passed")?"passed":"failed",clockFrequency=Stopwatch.Frequency,seeds,rows,comparisons,scope="Acquired model, portable net8 execution with frozen1e-4 position/velocity,1e-6 angle/scaled-state budgets. Two timed observations per method/case; one separately charged finer same-model RK23 execution capped1e-4s, retaining explicit midpoint fallback. Preparation and first.02simsec cold cost separate; hot.23simsec charges all physical commits/guards/scalar/thermal/bank operations. Analytic operator tests are the independent oracle; fine execution is a convergence comparator, not independent source physics or stock FPS."};
    }
}
