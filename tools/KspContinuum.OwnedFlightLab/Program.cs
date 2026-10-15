using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using KspContinuum;

internal static class Program
{
    static readonly JsonSerializerOptions Json=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
    sealed class Pin {public string role,path,sha256;public long bytes;}
    sealed class Receipt
    {
        public string schema="continuum-owned-flight-lab/v1",status="failed",failure,scope,assemblySha256,coreSha256;
        public string runId,vesselId,contextSha256;public int sourceProcessId,parts,bodies,resources,actuators,surfaces;
        public bool inputsUnchanged,predictionSourceUnchanged,nodeConsumed,nodeCheckpointExact,nodeRestoredAdmitted,nodeContinuationExact,
            checkpointExact,continuationExact,fieldHmrExecuted,failedHmrPreservedState;
        public bool sourceCoastAdmitted,sourceHadSpin;public string sourceCoastReason;
        public double electricWithdrawal,propellantWithdrawal,attitudeErrorBefore,attitudeErrorAfter,velocityErrorBefore,velocityErrorAfter;
        public long clockFrequency=Stopwatch.Frequency,preparationTicks,missionTicks,missionAllocatedBytes;
        public List<object> phases=new List<object>();public Pin[] inputs;public OwnedRuntimeSourceBinding source;
    }
    sealed class FieldSchedule : IOwnedRuntimeFieldSchedule
    {
        public bool Fail;public long Calls;
        public string Schema{get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema{get{return "world-bank-state-only/v1";}}
        public string ImplementationId{get{return Fail?"owned-flight-lab-failed-field/v1":"owned-flight-lab-forward-field/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment env,OwnedRuntimeControls controls)
        {Calls++;systems.Step(step,env,controls);if(Fail)throw new InvalidOperationException("Intentional provisional-step failure.");}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {Calls++;systems.Step(step,field,controls);if(Fail)throw new InvalidOperationException("Intentional provisional-step failure.");}
    }
    static void Need(bool value,string reason){if(!value)throw new InvalidDataException(reason);}
    static string Hash(byte[] bytes){return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();}
    static string HashFile(string path){using(var file=File.OpenRead(path))return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    static string State(object state){return Hash(JsonSerializer.SerializeToUtf8Bytes(state,Json));}
    static void NoLinks(string path)
    {
        for(string current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            Need(new FileInfo(current).LinkTarget==null&&new DirectoryInfo(current).LinkTarget==null
                &&(!(File.Exists(current)||Directory.Exists(current))||(File.GetAttributes(current)&FileAttributes.ReparsePoint)==0),"Symlink/reparse path rejected.");
    }
    static T Read<T>(string role,string path,List<Pin> pins)
    {
        Need(Path.IsPathFullyQualified(path),"Absolute inputs required.");path=Path.GetFullPath(path);NoLinks(path);
        var info=new FileInfo(path);Need(info.Exists&&info.Length>0&&info.Length<=64*1024*1024,"Input JSON outside64MiB bound.");
        byte[] bytes=File.ReadAllBytes(path);Need(bytes.Length==info.Length,"Input changed size while reading.");
        pins.Add(new Pin{role=role,path=path,sha256=Hash(bytes),bytes=bytes.Length});return JsonSerializer.Deserialize<T>(bytes,Json);
    }
    static void Write(string path,object data)
    {using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,data,data.GetType(),Json);file.Flush(true);}}
    static double Norm(Vec v){return Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);}
    static double[] V(Vec v){return new[]{v.X,v.Y,v.Z};}
    static OwnedRuntimeControls Quiet(){return new OwnedRuntimeControls();}
    static long Region(OwnedRuntimeWorld world){using(var read=world.AcquireRead()){Need(read.RegionCount==1,"Mission preparation requires one rigid region.");return read.GetRegion(0).Id;}}
    static object Observation(OwnedFlightSession session,string name)
    {
        using(var read=session.Runtime.AcquireRead())
        {
            double ec=0,propellant=0,min=double.MaxValue,max=0,totalMass=0;
            for(int r=0;r<read.RegionCount;r++)totalMass+=read.GetRegion(r).Properties.Mass;
            for(int r=0;r<read.ResourceCount;r++){var resource=read.Definition.GetResource(r);double q=read.GetResourceQuantity(r);if(resource.Kind=="ElectricCharge")ec+=q;propellant+=q*resource.MassPerUnit;}
            for(int p=0;p<read.PartCount;p++){double t=read.GetTemperature(p);min=Math.Min(min,t);max=Math.Max(max,t);}
            var state=read.GetRegion(0);Vec position=session.CenterPosition(0),velocity=state.Motion.Velocity;
            return new {phase=name,universalTime=session.UniversalTime,stamp=read.Stamp,regions=read.RegionCount,selectedRegionMass=state.Properties.Mass,totalMass,
                centerPosition=V(position),velocity=V(velocity),angularMomentum=V(state.Motion.AngularMomentum),altitude=session.Context.Altitude(position),
                density=session.EvaluateRegion(0).Density,electricCharge=ec,resourceMass=propellant,minTemperature=min,maxTemperature=max};
        }
    }
    static void Local(Receipt receipt,OwnedFlightSession session,OwnedRuntimeHost host,string phase,int count,OwnedRuntimeControls controls)
    {
        long start=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<count;i++)session.AdvanceLocal(host,.02,controls);
        receipt.phases.Add(new {phase=phase,steps=count,simulatedSeconds=count*.02,ticks=Stopwatch.GetTimestamp()-start,
            allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated,observation=Observation(session,phase)});
    }
    static void Mission(OwnedRuntimeImportResult imported,OwnedFlightWorld context,Receipt receipt,string output)
    {
        var host=new OwnedRuntimeHost(imported.World,imported.Systems);
        var session=OwnedFlightSession.SeedCaptured(imported.World,context,context.CapturedRootPosition);
        var quiet=Quiet();var plan=new OwnedRuntimeStepPlan(.02);long region=Region(imported.World);
        using(var read=imported.World.AcquireRead())receipt.sourceHadSpin=Norm(read.GetRegion(0).Motion.AngularMomentum)>0;
        receipt.phases.Add(Observation(session,"captured-placement"));
        receipt.sourceCoastAdmitted=session.TryCoast(imported.World.Stamp,session.UniversalTime+1,quiet,true,double.PositiveInfinity,0,out receipt.sourceCoastReason);
        if(receipt.sourceHadSpin)Need(!receipt.sourceCoastAdmitted,"Spinning captured state admitted passive coast.");

        Vec radial=context.SunDirection*(1/Norm(context.SunDirection));Vec normal=Vec.Cross(radial,new Vec(0,1,0));
        if(Norm(normal)<.1)normal=Vec.Cross(radial,new Vec(1,0,0));
        session.PlaceCircular(imported.World.Stamp,Math.Max(200000,context.AtmosphereDepth+100000),radial,normal);
        using(var read=imported.World.AcquireRead())
        {
            var motion=read.GetRegion(0).Motion;motion.AngularMomentum=new Vec();
            for(int i=0;i<read.Definition.ProviderStateCount;i++)Need(read.GetProviderState(i)==0,"Initial provider state requires a separate declared preparation.");
            imported.World.SetRegionMotions(read.Stamp,new[]{motion},read.Stamp.Time,session.FrameId);
        }
        receipt.phases.Add(new {phase="engineered-vacuum-circular-preparation",treatment="Acquired body mu/radius/Sun radial and complete real craft; explicit circular center velocity and zero chassis spin; no resource refill or captured-trajectory claim.",observation=Observation(session,"circular")});
        Local(receipt,session,host,"local-orbit",10,quiet);
        var forecast=OwnedFlightPrediction.Predict(session,host,imported,quiet,plan,region,30,OwnedFlightPredictionMode.VacuumCoast,true,maximumWallSeconds:30);
        Need(forecast.Status=="completed"&&forecast.SourceUnchanged,"Actual-input coast forecast failed: "+forecast.Failure);
        receipt.predictionSourceUnchanged=forecast.SourceUnchanged;Write(Path.Combine(output,"coast-prediction.json"),Prediction(forecast));
        Vec velocity;using(var read=imported.World.AcquireRead())velocity=read.GetRegion(0).Motion.Velocity;
        var node=new OwnedFlightManeuverPlan("single-prograde-node",session,host,region,session.UniversalTime+5,velocity*(1/Norm(velocity)));
        var nodePilot=new OwnedFlightPilotData{regionId=region};
        var nodeCheckpoint=OwnedFlightCheckpoint.Capture(host,imported,session,quiet,plan,nodePilot,"",node);
        Write(Path.Combine(output,"pending-node-checkpoint.json"),nodeCheckpoint);
        byte[] nodeBytes=OwnedRuntimeCheckpointBinary.EncodeFlight(nodeCheckpoint);
        using(var file=new FileStream(Path.Combine(output,"pending-node-checkpoint.ofcb"),FileMode.CreateNew,FileAccess.Write))file.Write(nodeBytes,0,nodeBytes.Length);
        var nodeRestored=OwnedFlightCheckpoint.Restore(host,imported,OwnedRuntimeCheckpointBinary.DecodeFlight(nodeBytes),
            imported.Source.nativeManifestSha256,imported.Source.partSpecsSha256,context.SourceSha256,"");
        var restoredNode=nodeRestored.Maneuver;
        receipt.nodeRestoredAdmitted=restoredNode!=null&&restoredNode.IsCurrent(nodeRestored.Session,nodeRestored.Host)
            &&restoredNode.Id==node.Id&&restoredNode.RegionId==node.RegionId&&restoredNode.EventUniversalTime==node.EventUniversalTime
            &&Equal(restoredNode.DeltaVelocity,node.DeltaVelocity)&&restoredNode.SourceStamp.Matches(node.SourceStamp)
            &&restoredNode.ContextSha256==node.ContextSha256&&restoredNode.ScheduleSha256==node.ScheduleSha256
            &&restoredNode.ScheduleImplementation==node.ScheduleImplementation&&restoredNode.CodeGeneration==node.CodeGeneration
            &&restoredNode.ConfigurationRevision==node.ConfigurationRevision;
        Need(receipt.nodeRestoredAdmitted,"Pending node lost event/source/code/configuration binding.");
        receipt.nodeCheckpointExact=State(nodeCheckpoint)==State(OwnedFlightCheckpoint.Capture(nodeRestored.Host,nodeRestored.Imported,
            nodeRestored.Session,nodeRestored.Controls,nodeRestored.Plan,nodeRestored.Pilot,"",restoredNode));
        Need(receipt.nodeCheckpointExact,"Pending node checkpoint did not roundtrip exactly.");
        long start=Stopwatch.GetTimestamp();node.ExecuteCoastManeuver(session,host,quiet,true);
        restoredNode.ExecuteCoastManeuver(nodeRestored.Session,nodeRestored.Host,nodeRestored.Controls,true);
        receipt.nodeContinuationExact=State(OwnedFlightCheckpoint.Capture(host,imported,session,quiet,plan,nodePilot,""))==
            State(OwnedFlightCheckpoint.Capture(nodeRestored.Host,nodeRestored.Imported,nodeRestored.Session,nodeRestored.Controls,nodeRestored.Plan,nodeRestored.Pilot,""));
        Need(receipt.nodeContinuationExact,"Restored pending node continuation differs.");
        receipt.nodeConsumed=!node.IsCurrent(session,host);Need(receipt.nodeConsumed,"Atomic node was not consumed.");
        receipt.phases.Add(new {phase="atomic-coast-node",id=node.Id,region=node.RegionId,deltaVelocity=V(node.DeltaVelocity),eventUniversalTime=node.EventUniversalTime,
            sourceStamp=node.SourceStamp,contextSha256=node.ContextSha256,scheduleSha256=node.ScheduleSha256,scheduleImplementation=node.ScheduleImplementation,
            codeGeneration=node.CodeGeneration,configurationRevision=node.ConfigurationRevision,ticks=Stopwatch.GetTimestamp()-start,observation=Observation(session,"node")});

        var authority=OwnedFlightControl.ObserveAuthority(imported.World,imported.Systems,region);
        Need(authority.Wheels>0&&authority.RcsJets>0,"Real acquired wheel/RCS capability is required.");
        Rotation target;
        using(var read=imported.World.AcquireRead())target=Rotation.AxisAngle((read.GetRegion(0).Motion.Orientation*imported.Systems.ControlOrientation).Rotate(new Vec(0,0,1)),.01)*read.GetRegion(0).Motion.Orientation;
        double ecBefore=imported.Systems.Counters.ElectricUnits,fuelBefore=imported.Systems.Counters.PropellantKilograms;
        for(int i=0;i<40;i++)
        {
            using(var read=imported.World.AcquireRead())
            {
                var state=read.GetRegion(0);var frame=state.Motion.Orientation*imported.Systems.ControlOrientation;
                var a=OwnedFlightControl.ObserveAuthority(imported.World,imported.Systems,region,frame);
                var guidance=OwnedFlightControl.AttitudeHold(state.Motion.Orientation,OwnedRuntimeDynamics.AngularVelocity(state.Properties,state.Motion.Orientation,state.Motion.AngularMomentum),target,frame,a.AngularAcceleration,.05,2);
                if(i==0)receipt.attitudeErrorBefore=guidance.AngleErrorRadians;if(i==39)receipt.attitudeErrorAfter=guidance.AngleErrorRadians;
                session.AdvanceLocal(host,.02,OwnedFlightControl.Controls(guidance.Input,rcs:false,wheels:true));
            }
        }
        receipt.phases.Add(Observation(session,"actual-wheel-guidance"));
        Vec referencePosition=session.CenterPosition(0),referenceVelocity;double referenceEpoch=session.UniversalTime;
        using(var read=imported.World.AcquireRead())referenceVelocity=read.GetRegion(0).Motion.Velocity;
        var reference=new CoastingEngine(referenceEpoch,context.Mu,new[]{new CoastingBody(0,referencePosition,referenceVelocity)},1);
        Vec deviation;using(var read=imported.World.AcquireRead())deviation=(read.GetRegion(0).Motion.Orientation*imported.Systems.ControlOrientation).Rotate(new Vec(.05,0,0));
        for(int i=0;i<50;i++)
        {
            using(var read=imported.World.AcquireRead())
            {
                var state=read.GetRegion(0);var frame=state.Motion.Orientation*imported.Systems.ControlOrientation;
                var a=OwnedFlightControl.ObserveAuthority(imported.World,imported.Systems,region,frame);
                Vec targetVelocity=reference.SampleAt(session.UniversalTime).Bodies[0].Velocity+deviation;
                var guidance=OwnedFlightControl.VelocityHold(state.Motion.Velocity,targetVelocity,frame,a.LinearAcceleration,1);
                if(i==0)receipt.velocityErrorBefore=guidance.VelocityErrorMetersPerSecond;if(i==49)receipt.velocityErrorAfter=guidance.VelocityErrorMetersPerSecond;
                session.AdvanceLocal(host,.02,OwnedFlightControl.Controls(guidance.Input,rcs:true,wheels:false));
            }
        }
        receipt.electricWithdrawal=imported.Systems.Counters.ElectricUnits-ecBefore;receipt.propellantWithdrawal=imported.Systems.Counters.PropellantKilograms-fuelBefore;
        Need(receipt.electricWithdrawal>0&&receipt.propellantWithdrawal>0,"Guidance did not reach actual resource-consuming actuators.");
        receipt.phases.Add(Observation(session,"actual-rcs-guidance"));

        Need(context.AtmosphereDepth>0,"Atmosphere passage requires captured atmosphere tables.");
        Vec position=radial*(context.Radius+context.AtmosphereDepth*.75),tangent=Vec.Cross(normal,radial);tangent=tangent*(1/Norm(tangent));
        session.Place(imported.World.Stamp,position,context.AtmosphericVelocity(position)+tangent*1200);
        Need(session.EvaluateRegion(0).Density>0,"Engineered passage did not enter acquired atmosphere.");
        receipt.phases.Add(new {phase="engineered-atmosphere-preparation",treatment="Sunward 75%-atmosphere-depth placement,1200m/s relative rotating flow; acquired atmosphere table and full craft retained.",observation=Observation(session,"entry")});
        var localPrediction=OwnedFlightPrediction.Predict(session,host,imported,quiet,plan,region,.2,OwnedFlightPredictionMode.LocalPhysical,maximumWallSeconds:30);
        Need(localPrediction.Status=="completed"&&localPrediction.SourceUnchanged,"Local atmospheric prediction failed: "+localPrediction.Failure);
        Write(Path.Combine(output,"atmosphere-prediction.json"),Prediction(localPrediction));Local(receipt,session,host,"atmosphere-passage",10,quiet);
        Vec centerBefore=session.CenterPosition(0);session.Rebase(imported.World.Stamp,session.Origin+new Vec(1234,-456,789));
        Need(Norm(session.CenterPosition(0)+centerBefore*-1)<1e-7,"Origin rebase moved physical center.");receipt.phases.Add(Observation(session,"origin-rebase"));

        var checkpoint=OwnedFlightCheckpoint.Capture(host,imported,session,quiet,plan,new OwnedFlightPilotData {regionId=region},"");
        Write(Path.Combine(output,"checkpoint.json"),checkpoint);byte[] encoded=OwnedRuntimeCheckpointBinary.EncodeFlight(checkpoint);
        using(var file=new FileStream(Path.Combine(output,"checkpoint.ofcb"),FileMode.CreateNew,FileAccess.Write))file.Write(encoded,0,encoded.Length);
        var decoded=OwnedRuntimeCheckpointBinary.DecodeFlight(encoded);
        var restored=OwnedFlightCheckpoint.Restore(host,imported,decoded,imported.Source.nativeManifestSha256,imported.Source.partSpecsSha256,context.SourceSha256,"");
        receipt.checkpointExact=State(checkpoint)==State(OwnedFlightCheckpoint.Capture(restored.Host,restored.Imported,restored.Session,restored.Controls,restored.Plan,restored.Pilot,""));
        Need(receipt.checkpointExact,"Combined checkpoint did not restore exact state/configuration.");
        Local(receipt,session,host,"checkpoint-original-continuation",5,quiet);Local(receipt,restored.Session,restored.Host,"checkpoint-restored-continuation",5,quiet);
        receipt.continuationExact=State(OwnedFlightCheckpoint.Capture(host,imported,session,quiet,plan,restored.Pilot,""))==
            State(OwnedFlightCheckpoint.Capture(restored.Host,restored.Imported,restored.Session,quiet,plan,restored.Pilot,""));Need(receipt.continuationExact,"Restored dynamics continuation differs.");

        var forward=new FieldSchedule();host.SwapSchedule(forward,receipt.assemblySha256,imported.World.Stamp);Local(receipt,session,host,"field-hmr",2,quiet);
        receipt.fieldHmrExecuted=forward.Calls==2;Need(receipt.fieldHmrExecuted,"Field HMR was not executed.");
        host.SwapSchedule(new FieldSchedule{Fail=true},receipt.assemblySha256,imported.World.Stamp);
        var failurePilot=new OwnedFlightPilotData{regionId=region};
        string stateBefore=State(OwnedFlightCheckpoint.Capture(host,imported,session,quiet,plan,failurePilot,""));bool failed=false;
        try{session.AdvanceLocal(host,.02,quiet);}catch(InvalidOperationException){failed=true;}
        receipt.failedHmrPreservedState=failed&&stateBefore==State(OwnedFlightCheckpoint.Capture(host,imported,session,quiet,plan,failurePilot,""));
        Need(receipt.failedHmrPreservedState,"Failed field HMR published provisional state.");
        host.SwapSchedule(new FieldSchedule(),receipt.assemblySha256,imported.World.Stamp);

        SplitJoin(receipt,imported,session,host,quiet);
        Write(Path.Combine(output,"final-checkpoint.json"),OwnedFlightCheckpoint.Capture(host,imported,session,quiet,plan,new OwnedFlightPilotData{regionId=region},""));
    }
    static bool Equal(Vec a,Vec b)
    {return BitConverter.DoubleToInt64Bits(a.X)==BitConverter.DoubleToInt64Bits(b.X)
        &&BitConverter.DoubleToInt64Bits(a.Y)==BitConverter.DoubleToInt64Bits(b.Y)&&BitConverter.DoubleToInt64Bits(a.Z)==BitConverter.DoubleToInt64Bits(b.Z);}
    static object Prediction(OwnedFlightPredictionResult r)
    {var points=new OwnedFlightPredictionPoint[r.Count];for(int i=0;i<points.Length;i++)points[i]=r.GetPoint(i);return new {r.Status,r.Failure,r.SourceStamp,r.ContextSha256,r.ScheduleSha256,r.CodeGeneration,r.ConfigurationRevision,r.RegionId,r.Mode,r.Steps,r.ElapsedTicks,r.ClockFrequency,r.SourceUnchanged,points};}
    static void SplitJoin(Receipt receipt,OwnedRuntimeImportResult imported,OwnedFlightSession session,OwnedRuntimeHost host,OwnedRuntimeControls quiet)
    {
        var snapshot=imported.World.CaptureSnapshot();OwnedRuntimeConnection chosen=new OwnedRuntimeConnection();bool found=false;
        foreach(var edge in snapshot.Connections)
        {
            if(imported.World.BodyOfPart(imported.World.PartSlot(edge.PartA))==imported.World.BodyOfPart(imported.World.PartSlot(edge.PartB)))continue;
            var trial=OwnedRuntimeWorld.RestoreSnapshot(snapshot);trial.Split(trial.Stamp,edge.Id);if(trial.RegionCount<2)continue;chosen=edge;found=true;break;
        }
        Need(found,"Captured authored graph has no separable physical region.");imported.World.Split(imported.World.Stamp,chosen.Id);
        Need(imported.World.RegionCount>1,"Actual topology split did not produce multiple regions.");Local(receipt,session,host,"split-field-regions",2,quiet);
        imported.World.SetConnectionLocked(imported.World.Stamp,chosen.Id,true);Need(imported.World.RegionCount==1,"Authored edge re-lock failed.");
        Local(receipt,session,host,"relocked-field-history",2,quiet);receipt.phases.Add(new {phase="authored-edge-unlock-relock",connection=chosen.Id,partA=chosen.PartA,partB=chosen.PartB,retainedHistory=imported.World.CaptureSnapshot().InputHistory.Length});
    }
    static int Main(string[] args)
    {
        var receipt=new Receipt{scope="Actual acquired craft/body/resources/actuators/aero; explicit engineered SI orbit and atmospheric scenarios, disposable predictions, guidance, node, checkpoint/HMR/topology. No stock trajectory/contact/render/FPS equivalence or performance comparison."};string output=null;bool owned=false;
        try
        {
            Need(args.Length>=10&&args.Length%2==0,"--native ABS --parts ABS --context ABS --aero ABS --output NEW_ABS_DIR [--resource-policy strict|acquired-surplus]");
            var options=new Dictionary<string,string>();for(int i=0;i<args.Length;i+=2)Need(new[]{"--native","--parts","--context","--aero","--output","--resource-policy"}.Contains(args[i])&&options.TryAdd(args[i],args[i+1]),"Unknown/duplicate option.");
            foreach(string key in new[]{"--native","--parts","--context","--aero","--output"})Need(options.ContainsKey(key)&&Path.IsPathFullyQualified(options[key]),"Required absolute input/output absent.");
            output=Path.GetFullPath(options["--output"]);NoLinks(output);Need(!File.Exists(output)&&!Directory.Exists(output),"New output required.");
            foreach(string key in new[]{"--native","--parts","--context","--aero"}){string parent=Path.GetDirectoryName(Path.GetFullPath(options[key]));Need(output!=parent&&!output.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.Ordinal),"Output must be outside input directories.");}
            Directory.CreateDirectory(output);owned=true;var pins=new List<Pin>();long start=Stopwatch.GetTimestamp();
            var native=Read<NativeAssemblySceneManifest>("native",options["--native"],pins);var parts=Read<OwnedRuntimeCapturedParts>("parts",options["--parts"],pins);
            var captured=Read<OwnedFlightCapture>("context",options["--context"],pins);var aero=Read<OwnedRuntimeCapturedAero>("aero",options["--aero"],pins);
            WholeCraftGeometry geometry=null;
            if(!string.IsNullOrEmpty(native.geometry))
            {
                MultiscaleSceneFormat.PortablePath(native.geometry);string parent=Path.GetDirectoryName(Path.GetFullPath(options["--native"]));
                string path=Path.GetFullPath(Path.Combine(parent,native.geometry));Need(path.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.Ordinal),"Geometry path escaped parent.");NoLinks(path);
                var info=new FileInfo(path);Need(info.Exists&&info.Length>0&&info.Length<=1024L*1024*1024,"Geometry bound exceeded.");
                string sha=HashFile(path);Need(sha==native.geometrySha256,"Geometry digest differs.");geometry=WholeCraftSceneFormat.ReadGeometry(path);Need(HashFile(path)==sha,"Geometry changed during decode.");pins.Add(new Pin{role="geometry",path=path,sha256=sha,bytes=info.Length});
            }
            string policy=options.TryGetValue("--resource-policy",out var value)?value:"strict";Need(policy=="strict"||policy=="acquired-surplus","Unknown resource policy.");
            var imported=OwnedRuntimeImport.Create(native,geometry,parts,pins[0].sha256,pins[1].sha256,allowAcquiredSurplus:policy=="acquired-surplus");
            OwnedRuntimeAcquiredAero.Attach(imported,OwnedRuntimeAcquiredAero.Build(imported,aero));Need(imported.Systems.SurfaceCount>0,"No acquired aero surfaces.");
            var context=OwnedFlightWorld.Admit(captured,pins[0].sha256,pins[1].sha256,pins[2].sha256);
            Need(context.RunId==native.runId&&context.VesselId==native.vesselId&&context.ProcessId==native.processId,"World source identity differs.");
            receipt.inputs=pins.ToArray();receipt.source=imported.Source;receipt.runId=context.RunId;receipt.vesselId=context.VesselId;receipt.sourceProcessId=context.ProcessId;receipt.contextSha256=context.SourceSha256;
            receipt.parts=imported.World.PartCount;receipt.bodies=imported.World.BodyCount;receipt.resources=imported.World.ResourceCount;receipt.actuators=imported.Systems.ActuatorCount;receipt.surfaces=imported.Systems.SurfaceCount;
            receipt.assemblySha256=HashFile(Assembly.GetExecutingAssembly().Location);receipt.coreSha256=HashFile(typeof(OwnedFlightWorld).Assembly.Location);receipt.preparationTicks=Stopwatch.GetTimestamp()-start;
            start=Stopwatch.GetTimestamp();long allocated=GC.GetAllocatedBytesForCurrentThread();Mission(imported,context,receipt,output);receipt.missionTicks=Stopwatch.GetTimestamp()-start;receipt.missionAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            receipt.inputsUnchanged=pins.All(p=>HashFile(p.path)==p.sha256);Need(receipt.inputsUnchanged,"Original input changed.");receipt.status="passed";
        }
        catch(Exception error){receipt.failure=error.ToString();Console.Error.WriteLine(receipt.failure);}
        if(owned)Write(Path.Combine(output,"receipt.json"),receipt);Console.WriteLine(receipt.status);return receipt.status=="passed"?0:2;
    }
}
