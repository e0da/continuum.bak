using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KspContinuum;

internal static class Program
{
    const int MaximumJsonBytes=64*1024*1024;
    static readonly JsonSerializerOptions Json=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
    sealed class InputPin {public string role,path,sha256;public long bytes;}
    sealed class AdvanceRow
    {
        public string phase;public long startTicks,endTicks,allocatedBytes,stateRevision,epoch;
        public double delta,time,completeMilliseconds;public int regions;
        public double throttle;public double[] attitude,translation;
    }
    sealed class StateRow
    {
        public string phase,stateSha256;public double time;public long epoch,stateRevision,topologyRevision;
        public int regions;public double mass,electricCharge,propellantKilograms,minTemperature,maxTemperature;
        public double[] rootPositions,velocities,angularMomenta;
    }
    sealed class Receipt
    {
        public string schema="continuum-owned-runtime-lab/v1",status="failed",scope,failure;
        public string assemblySha256,coreAssemblySha256,sourceRunId,sourceVesselId,aeroProvenance,aeroRecipe;
        public int sourceProcessId,parts,bodies,resources,actuators,surfaces,sourceOmissions;
        public long clockFrequency=Stopwatch.Frequency,preparationTicks,preparationAllocatedBytes,missionTicks,missionAllocatedBytes;
        public bool checkpointExact,continuationExact,rejectedSchedulePreservedState,failedAdvancePreservedState,inputFilesUnchanged;
        public string checkpointSha256,checkpointFile;
        public long splitConnectionId,joinConnectionId,scheduleGeneration,replacementCalls;
        public long checkpointCaptureTicks,checkpointRestoreTicks,checkpointCaptureAllocatedBytes,checkpointRestoreAllocatedBytes;
        public InputPin[] inputs;public OwnedRuntimeSourceBinding source;
        public List<AdvanceRow> advances=new List<AdvanceRow>();public List<StateRow> states=new List<StateRow>();
        public OwnedRuntimeSystemCounters counters;
    }
    sealed class Replacement : IOwnedRuntimeSchedule
    {
        public bool bad,fail;public long Calls;
        public string Schema{get{return bad?"unsupported":"continuum-owned-runtime-schedule/v1";}}
        public string StateSchema{get{return "world-bank-state-only/v1";}}
        public string ImplementationId{get{return "owned-lab-source-bound-forward-schedule/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {Calls++;systems.Step(step,environment,controls);if(fail)throw new InvalidOperationException("Owned lab injected prepublication schedule fault.");}
    }
    static void Need(bool ok,string reason){if(!ok)throw new InvalidDataException(reason);}
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();}
    static string HashFile(string path){using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return Convert.ToHexString(sha.ComputeHash(file)).ToLowerInvariant();}
    static string Serialize(object value){return JsonSerializer.Serialize(value,value.GetType(),Json);}
    static void NoLinks(string path)
    {
        var full=Path.GetFullPath(path);string current=Path.GetPathRoot(full);
        foreach(string component in full.Substring(current.Length).Split(Path.DirectorySeparatorChar))
        {if(component.Length==0)continue;current=Path.Combine(current,component);if(File.Exists(current)||Directory.Exists(current))Need((File.GetAttributes(current)&FileAttributes.ReparsePoint)==0,"Symbolic/reparse input or output path rejected.");}
    }
    static byte[] Read(string role,string path,List<InputPin> pins)
    {
        Need(Path.IsPathRooted(path),"Inputs must be absolute paths.");path=Path.GetFullPath(path);NoLinks(path);
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            Need(stream.Length>0&&stream.Length<=MaximumJsonBytes,"JSON input size bound exceeded.");var bytes=new byte[(int)stream.Length];int offset=0;
            while(offset<bytes.Length){int read=stream.Read(bytes,offset,bytes.Length-offset);Need(read>0,"JSON input truncated.");offset+=read;}
            Need(stream.ReadByte()==-1,"JSON input grew during read.");pins.Add(new InputPin{role=role,path=path,sha256=Hash(bytes),bytes=bytes.Length});return bytes;
        }
    }
    static T ReadJson<T>(string role,string path,List<InputPin> pins){return JsonSerializer.Deserialize<T>(Read(role,path,pins),Json);}
    static WholeCraftGeometry ReadGeometry(string parent,NativeAssemblySceneManifest native,List<InputPin> pins)
    {
        if(string.IsNullOrEmpty(native.geometry)){Need(string.IsNullOrEmpty(native.geometrySha256),"Missing geometry has a digest.");return null;}
        MultiscaleSceneFormat.PortablePath(native.geometry);string path=Path.GetFullPath(Path.Combine(parent,native.geometry));
        Need(path.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.Ordinal),"Geometry escaped scene directory.");NoLinks(path);
        var file=new FileInfo(path);Need(file.Exists&&file.Length>0&&file.Length<=1024L*1024*1024,"Geometry file bound exceeded.");
        string hash=HashFile(path);Need(hash==native.geometrySha256,"Geometry source digest differs.");
        var geometry=WholeCraftSceneFormat.ReadGeometry(path);Need(HashFile(path)==hash,"Geometry changed during decode.");pins.Add(new InputPin{role="collider-geometry",path=path,sha256=hash,bytes=file.Length});return geometry;
    }
    static void WriteNew(string path,object value)
    {NoLinks(path);using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){byte[] bytes=Encoding.UTF8.GetBytes(Serialize(value));stream.Write(bytes,0,bytes.Length);stream.Flush(true);}}
    static Dictionary<string,string> Arguments(string[] args)
    {
        Need(args.Length>=6&&args.Length%2==0,"Usage: --native ABS --parts ABS --output NEW_ABS_DIR [--aero ABS | --declared-aero ABS] [--resource-policy strict|acquired-surplus]");
        var result=new Dictionary<string,string>(StringComparer.Ordinal);for(int i=0;i<args.Length;i+=2){Need(new[]{"--native","--parts","--output","--aero","--declared-aero","--resource-policy"}.Contains(args[i])&&!result.ContainsKey(args[i]),"Unknown/duplicate option.");result.Add(args[i],args[i+1]);}
        Need(result.ContainsKey("--native")&&result.ContainsKey("--parts")&&result.ContainsKey("--output")&&!(result.ContainsKey("--aero")&&result.ContainsKey("--declared-aero")),"Required input/output or aero selection differs.");return result;
    }
    static OwnedRuntimeControls Controls(Vec attitude,Vec translation)
    {return new OwnedRuntimeControls{Attitude=attitude,Translation=translation,WheelsEnabled=true,RcsEnabled=true,EnginesEnabled=true};}
    static OwnedRuntimeInput Input(OwnedRuntimeControls c){return new OwnedRuntimeInput(c.Throttle,c.Attitude.X,c.Attitude.Y,c.Attitude.Z,c.Translation.X,c.Translation.Y,c.Translation.Z);}
    static double[] Vector(Vec value){return new[]{value.X,value.Y,value.Z};}
    static OwnedRuntimeCheckpointData Checkpoint(OwnedRuntimeImportResult runtime,OwnedRuntimeControls controls,OwnedRuntimeEnvironment env,OwnedRuntimeStepPlan plan)
    {return OwnedRuntimeCheckpoint.Capture(runtime,controls,env,plan);}
    static string StateHash(OwnedRuntimeImportResult runtime,OwnedRuntimeControls c,OwnedRuntimeEnvironment e,OwnedRuntimeStepPlan p)
    {return Hash(Encoding.UTF8.GetBytes(Serialize(Checkpoint(runtime,c,e,p))));}
    static void Advance(OwnedRuntimeHost host,Receipt report,string phase,int steps,OwnedRuntimeControls controls,OwnedRuntimeEnvironment environment,OwnedRuntimeStepPlan plan)
    {
        var input=Input(controls);
        for(int i=0;i<steps;i++)
        {
            long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();var stamp=host.Advance(plan.Delta,input,environment,controls);long end=Stopwatch.GetTimestamp(),bytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            report.advances.Add(new AdvanceRow{phase=phase,startTicks=start,endTicks=end,allocatedBytes=bytes,completeMilliseconds=(end-start)*1000.0/Stopwatch.Frequency,delta=plan.Delta,time=stamp.Time,epoch=stamp.Epoch,stateRevision=stamp.StateRevision,regions=host.World.RegionCount,throttle=controls.Throttle,attitude=Vector(controls.Attitude),translation=Vector(controls.Translation)});
        }
    }
    static void Observe(OwnedRuntimeImportResult runtime,Receipt report,string phase,OwnedRuntimeControls controls,OwnedRuntimeEnvironment environment,OwnedRuntimeStepPlan plan)
    {
        using(var read=runtime.World.AcquireRead())
        {
            var row=new StateRow{phase=phase,stateSha256=StateHash(runtime,controls,environment,plan),time=read.Stamp.Time,epoch=read.Stamp.Epoch,stateRevision=read.Stamp.StateRevision,topologyRevision=read.Stamp.TopologyRevision,regions=read.RegionCount,rootPositions=new double[read.RegionCount*3],velocities=new double[read.RegionCount*3],angularMomenta=new double[read.RegionCount*3],minTemperature=double.PositiveInfinity};
            for(int i=0;i<read.RegionCount;i++){var r=read.GetRegion(i);row.mass+=r.Properties.Mass;Array.Copy(Vector(r.Motion.RootPosition),0,row.rootPositions,i*3,3);Array.Copy(Vector(r.Motion.Velocity),0,row.velocities,i*3,3);Array.Copy(Vector(r.Motion.AngularMomentum),0,row.angularMomenta,i*3,3);}
            for(int i=0;i<read.ResourceCount;i++){var r=read.Definition.GetResource(i);double q=read.GetResourceQuantity(i);if(r.Kind=="ElectricCharge")row.electricCharge+=q;else row.propellantKilograms+=q*r.MassPerUnit;}
            for(int i=0;i<read.PartCount;i++){row.minTemperature=Math.Min(row.minTemperature,read.GetTemperature(i));row.maxTemperature=Math.Max(row.maxTemperature,read.GetTemperature(i));}report.states.Add(row);
        }
    }
    static long SplitConnection(OwnedRuntimeWorld world)
    {
        var snapshot=world.CaptureSnapshot();
        foreach(var connection in snapshot.Connections)
        {
            var rows=(OwnedRuntimeConnection[])snapshot.Connections.Clone();for(int i=0;i<rows.Length;i++)if(rows[i].Id==connection.Id)rows[i]=new OwnedRuntimeConnection(connection.Id,connection.PartA,connection.PartB,false);
            if(OwnedRuntimeTopology.Compile(snapshot.Definition,rows).RegionCount>world.RegionCount)return connection.Id;
        }
        throw new InvalidDataException("Captured graph has no independently separable body region.");
    }
    static void Mission(OwnedRuntimeImportResult runtime,Receipt report,string output)
    {
        var host=new OwnedRuntimeHost(runtime.World,runtime.Systems);var normal=new OwnedRuntimeStepPlan(.02);var warp=new OwnedRuntimeStepPlan(.02,4);
        var quiet=Controls(new Vec(),new Vec());quiet.WheelsEnabled=quiet.RcsEnabled=quiet.EnginesEnabled=false;
        var wheel=Controls(new Vec(.35,.15,-.2),new Vec());wheel.RcsEnabled=wheel.EnginesEnabled=false;
        var translation=Controls(new Vec(),new Vec(.5,.2,-.3));translation.WheelsEnabled=translation.EnginesEnabled=false;
        var vacuum=new OwnedRuntimeEnvironment("declared-lab-vacuum",0,0,0,300,new Vec(),new Vec());
        var atmosphere=new OwnedRuntimeEnvironment("declared-lab-uniform-atmosphere",0,0,.01,300,new Vec(40,0,0),new Vec(),.1,280);
        Observe(runtime,report,"initial",quiet,vacuum,normal);
        Advance(host,report,"quiet",5,quiet,vacuum,normal);Advance(host,report,"actual-spec-wheel",25,wheel,vacuum,normal);
        Advance(host,report,"actual-spec-rcs",25,translation,vacuum,normal);Observe(runtime,report,"powered",translation,vacuum,normal);
        Advance(host,report,"declared-atmosphere-4x",10,quiet,atmosphere,warp);Observe(runtime,report,"atmosphere",quiet,atmosphere,warp);
        long split=SplitConnection(runtime.World);var old=runtime.World.Topology.CopyConnections().First(c=>c.Id==split);report.splitConnectionId=split;
        runtime.World.Split(runtime.World.Stamp,split);Advance(host,report,"separated-coast",5,quiet,vacuum,normal);Observe(runtime,report,"split",quiet,vacuum,normal);
        report.joinConnectionId=checked(runtime.World.Topology.CopyConnections().Max(c=>c.Id)+1);runtime.World.Dock(runtime.World.Stamp,report.joinConnectionId,old.PartA,old.PartB);Observe(runtime,report,"joined",quiet,vacuum,normal);
        long a=GC.GetAllocatedBytesForCurrentThread(),t=Stopwatch.GetTimestamp();var checkpoint=Checkpoint(runtime,translation,atmosphere,normal);report.checkpointCaptureTicks=Stopwatch.GetTimestamp()-t;report.checkpointCaptureAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-a;
        string encoded=Serialize(checkpoint);report.checkpointSha256=Hash(Encoding.UTF8.GetBytes(encoded));report.checkpointFile=Path.Combine(output,"checkpoint.json");WriteNew(report.checkpointFile,checkpoint);
        var decoded=JsonSerializer.Deserialize<OwnedRuntimeCheckpointData>(File.ReadAllBytes(report.checkpointFile),Json);
        a=GC.GetAllocatedBytesForCurrentThread();t=Stopwatch.GetTimestamp();var restored=OwnedRuntimeCheckpoint.Restore(decoded,runtime.Source.nativeManifestSha256,runtime.Source.partSpecsSha256);report.checkpointRestoreTicks=Stopwatch.GetTimestamp()-t;report.checkpointRestoreAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-a;
        var clone=new OwnedRuntimeImportResult{World=restored.World,Systems=restored.Systems,Source=restored.Source};var cloneHost=new OwnedRuntimeHost(clone.World,clone.Systems);
        report.checkpointExact=StateHash(clone,restored.Controls,restored.Environment,restored.Plan)==report.checkpointSha256;Need(report.checkpointExact,"Serialized complete checkpoint did not exactly reconstruct.");
        for(int i=0;i<8;i++){Advance(host,report,"continuation-original",1,translation,atmosphere,normal);Advance(cloneHost,report,"continuation-restored",1,restored.Controls,restored.Environment,restored.Plan);Need(StateHash(runtime,translation,atmosphere,normal)==StateHash(clone,restored.Controls,restored.Environment,restored.Plan),"Restored complete continuation differs.");}
        report.continuationExact=true;Observe(runtime,report,"continued",translation,atmosphere,normal);
        string before=StateHash(runtime,quiet,vacuum,normal);long generation=host.CodeGeneration;bool rejected=false;
        try{host.SwapSchedule(new Replacement{bad=true},report.assemblySha256,runtime.World.Stamp);}catch(ArgumentException){rejected=true;}
        report.rejectedSchedulePreservedState=rejected&&host.CodeGeneration==generation&&StateHash(runtime,quiet,vacuum,normal)==before;Need(report.rejectedSchedulePreservedState,"Rejected schedule lost current owner/state.");
        var replacement=new Replacement();host.SwapSchedule(replacement,report.assemblySha256,runtime.World.Stamp);Advance(host,report,"replacement-code",5,quiet,vacuum,normal);
        Need(replacement.Calls==5,"Replacement executable was not the advance owner.");before=StateHash(runtime,quiet,vacuum,normal);host.SwapSchedule(new Replacement{fail=true},report.assemblySha256,runtime.World.Stamp);rejected=false;
        try{host.Advance(normal.Delta,Input(quiet),vacuum,quiet);}catch(InvalidOperationException){rejected=true;}
        report.failedAdvancePreservedState=rejected&&StateHash(runtime,quiet,vacuum,normal)==before;Need(report.failedAdvancePreservedState,"Failed replacement exposed partial state.");
        host.SwapSchedule(replacement,report.assemblySha256,runtime.World.Stamp);Advance(host,report,"replacement-recovery",1,quiet,vacuum,normal);report.replacementCalls=replacement.Calls;report.scheduleGeneration=host.CodeGeneration;
        Observe(runtime,report,"final",quiet,vacuum,normal);report.counters=runtime.Systems.Counters;
    }
    static int Main(string[] args)
    {
        Receipt report=new Receipt{scope="Actual acquired native body/resources/actuators; owned static SI rigid/controller/resource model; explicit aero/environment model; headless continuation and execution ownership only, no stock gameplay, contact, rendering, FPS or performance comparison claim."};string output=null;bool outputOwned=false;
        try
        {
            var options=Arguments(args);output=Path.GetFullPath(options["--output"]);Need(Path.IsPathRooted(options["--output"]),"Absolute output directory required.");NoLinks(output);Need(!Directory.Exists(output)&&!File.Exists(output),"Output must be new.");
            foreach(var key in new[]{"--native","--parts","--aero","--declared-aero"})if(options.ContainsKey(key)){string parent=Path.GetDirectoryName(Path.GetFullPath(options[key]));Need(!output.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&output!=parent,"Output must be outside input directories.");}
            string resourcePolicy=options.ContainsKey("--resource-policy")?options["--resource-policy"]:"strict";Need(resourcePolicy=="strict"||resourcePolicy=="acquired-surplus","Unsupported resource policy.");Directory.CreateDirectory(output);outputOwned=true;
            var pins=new List<InputPin>();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
            var native=ReadJson<NativeAssemblySceneManifest>("native",options["--native"],pins);var parts=ReadJson<OwnedRuntimeCapturedParts>("parts",options["--parts"],pins);
            var geometry=ReadGeometry(Path.GetDirectoryName(Path.GetFullPath(options["--native"])),native,pins);
            report.inputs=pins.ToArray();report.sourceRunId=native.runId;report.sourceVesselId=native.vesselId;report.sourceProcessId=native.processId;
            report.assemblySha256=HashFile(Assembly.GetExecutingAssembly().Location);report.coreAssemblySha256=HashFile(typeof(OwnedRuntimeWorld).Assembly.Location);
            var imported=OwnedRuntimeImport.Create(native,geometry,parts,pins[0].sha256,pins[1].sha256,allowAcquiredSurplus:resourcePolicy=="acquired-surplus");
            if(options.ContainsKey("--aero"))
            {var aero=ReadJson<OwnedRuntimeCapturedAero>("acquired-aero",options["--aero"],pins);OwnedRuntimeAcquiredAero.Attach(imported,OwnedRuntimeAcquiredAero.Build(imported,aero));report.aeroProvenance="actual-acquired-six-face-areas/declared-isotropic-Cd1-heating.0001";report.aeroRecipe=aero.recipe;}
            else if(options.ContainsKey("--declared-aero"))
            {var rows=ReadJson<OwnedRuntimeCheckpointSurface[]>("declared-aero",options["--declared-aero"],pins);Need(rows!=null&&rows.Length>0&&rows.Length<=200000,"Declared aero rows absent/bounded.");var surfaces=new List<OwnedRuntimeSurfaceSpec>();foreach(var s in rows){Need(s!=null&&s.point!=null&&s.point.Length==3&&s.normal!=null&&s.normal.Length==3,"Declared aero vector shape differs.");surfaces.Add(new OwnedRuntimeSurfaceSpec(imported.World.PartSlot(s.partId),new Vec(s.point[0],s.point[1],s.point[2]),new Vec(s.normal[0],s.normal[1],s.normal[2]),s.dragArea,s.liftArea,s.thermalArea,s.emissivity,s.heatingFraction));}OwnedRuntimeAcquiredAero.Attach(imported,surfaces.ToArray());report.aeroProvenance="explicit-authored-surface-config/no-acquired-aero-claim";}
            else throw new InvalidDataException("This complete mission requires --aero or explicit --declared-aero.");
            Need(imported.Systems.SurfaceCount>0,"Atmospheric mission has no enabled acquired/declared surfaces.");Need(Enumerable.Range(0,imported.Systems.ActuatorCount).Any(i=>imported.Systems.GetActuator(i).Kind==OwnedRuntimeActuatorKind.ReactionWheel&&imported.Systems.IsEnabled(i)),"No acquired enabled wheel capability.");Need(Enumerable.Range(0,imported.Systems.ActuatorCount).Any(i=>imported.Systems.GetActuator(i).Kind==OwnedRuntimeActuatorKind.Rcs&&imported.Systems.IsEnabled(i)),"No acquired enabled RCS capability.");
            report.preparationTicks=Stopwatch.GetTimestamp()-start;report.preparationAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-bytes;
            report.inputs=pins.ToArray();report.source=imported.Source;report.sourceRunId=native.runId;report.sourceVesselId=native.vesselId;report.sourceProcessId=native.processId;
            report.parts=imported.World.PartCount;report.bodies=imported.World.BodyCount;report.resources=imported.World.ResourceCount;report.actuators=imported.Systems.ActuatorCount;report.surfaces=imported.Systems.SurfaceCount;report.sourceOmissions=imported.Source.omissions.Length;
            bytes=GC.GetAllocatedBytesForCurrentThread();start=Stopwatch.GetTimestamp();Mission(imported,report,output);report.missionTicks=Stopwatch.GetTimestamp()-start;report.missionAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-bytes;
            report.inputFilesUnchanged=pins.All(p=>HashFile(p.path)==p.sha256);Need(report.inputFilesUnchanged,"An original input changed.");
            Need(report.counters.DeliveredForceNewtonSeconds>0&&report.counters.DeliveredTorqueNewtonMeterSeconds>0&&report.counters.PropellantKilograms>0&&report.counters.ElectricUnits>0,"Powered mission did not deliver force, torque and real resource consumption.");report.status="completed";
            WriteNew(Path.Combine(output,"receipt.json"),report);Console.WriteLine("completed: actual owned mission; receipt "+Path.Combine(output,"receipt.json"));return 0;
        }
        catch(Exception ex)
        {report.failure=ex.ToString();if(outputOwned&&!File.Exists(Path.Combine(output,"receipt.json")))WriteNew(Path.Combine(output,"receipt.json"),report);Console.Error.WriteLine(ex.Message);return 2;}
    }
}
