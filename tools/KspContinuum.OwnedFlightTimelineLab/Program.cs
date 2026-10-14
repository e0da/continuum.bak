using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static readonly double[] IrregularIntervals={.007,.073,.011,.041};
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    sealed class Pin { public string role="",path="",sha256=""; public long bytes; }
    sealed class Run
    {
        public string cadence="",stateSha256="",checkpointPath="",status="failed",failure="";
        public long pumpCalls,localCommits,coastCommits,workTicks,allocatedBytes,preparationTicks,preparationAllocatedBytes;
        public int terminalDebtCommits;
        public double requested,committed,debt,maximumDebt;
        public List<OwnedFlightTimelineProgress> boundaries=new();
    }
    static void Need(bool x,string reason){if(!x)throw new InvalidDataException(reason);}
    static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    static string HashFile(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    static void NoLinks(string path)
    {for(string? p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))Need((!File.Exists(p)&&!Directory.Exists(p))||(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"Symlink/reparse ancestry rejected.");}
    static T Read<T>(string role,string path,List<Pin> pins) where T : class
    {
        Need(Path.IsPathFullyQualified(path),"Absolute input required.");path=Path.GetFullPath(path);NoLinks(path);
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);long length=file.Length;Need(length>0&&length<=64*1024*1024,"JSON input exceeds64MiB.");
        var bytes=new byte[(int)length];file.ReadExactly(bytes);Need(file.ReadByte()==-1&&file.Length==length,"Input changed during read.");
        pins.Add(new Pin{role=role,path=path,sha256=Hash(bytes),bytes=length});return JsonSerializer.Deserialize<T>(bytes,Json)??throw new InvalidDataException("Null source JSON.");
    }
    static void Write(string path,object value){using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);JsonSerializer.Serialize(file,value,value.GetType(),Json);file.Flush(true);}
    static IEnumerable<double> Observations(string cadence,double begin,double end)
    {
        int index=1;double t=begin;
        while(t<end)
        {
            double next=cadence=="irregular"?t+IrregularIntervals[(index-1)%IrregularIntervals.Length]:begin+index/double.Parse(cadence,System.Globalization.CultureInfo.InvariantCulture);
            t=Math.Min(next,end);yield return t;index++;
        }
    }
    static Run Replay(string cadence,string output,NativeAssemblySceneManifest native,WholeCraftGeometry? geometry,OwnedRuntimeCapturedParts parts,OwnedRuntimeCapturedAero aero,OwnedFlightCapture captured,List<Pin> pins,bool surplus)
    {
        long preparationStarted=Stopwatch.GetTimestamp(),preparationAllocated=GC.GetAllocatedBytesForCurrentThread();
        var imported=OwnedRuntimeImport.Create(native,geometry,parts,pins[0].sha256,pins[1].sha256,allowAcquiredSurplus:surplus);
        OwnedRuntimeAcquiredAero.Attach(imported,OwnedRuntimeAcquiredAero.Build(imported,aero));
        var context=OwnedFlightWorld.Admit(captured,pins[0].sha256,pins[1].sha256,pins[2].sha256);
        var host=new OwnedRuntimeHost(imported.World,imported.Systems);var session=OwnedFlightSession.SeedCaptured(imported.World,context,context.CapturedRootPosition);
        var timeline=new OwnedFlightTimeline(host,session,0,.02,"actual-craft-timing-partition");
        var row=new Run{cadence=cadence,preparationTicks=Stopwatch.GetTimestamp()-preparationStarted,preparationAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-preparationAllocated};long started=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
        // These are frozen wall-policy boundaries, not render-frame-driven inputs.
        double[] ends={1,1.5,2,2.5,3};double[] warp={1,1,1,4,1};bool[] paused={false,true,false,false,false};
        var controls=new OwnedRuntimeControls{Attitude=new Vec(.03,-.02,.01),WheelsEnabled=true};
        double begin=0;
        for(int phase=0;phase<ends.Length;phase++)
        {
            timeline.Configure(host.World.Stamp,timeline.InputRevision,begin,controls,warp[phase],paused[phase]);
            foreach(double wall in Observations(cadence,begin,ends[phase]))
            {
                var progress=timeline.Pump(wall,8);row.pumpCalls++;Need(progress.Fault==null,progress.Fault??"Timeline fault.");
                row.maximumDebt=Math.Max(row.maximumDebt,progress.DebtSeconds);
            }
            row.boundaries.Add(timeline.ReadProgress());begin=ends[phase];
        }
        // End-of-recording is an explicit policy boundary. Finish its retained tail
        // while paused; never grant a new manual interval once debt reaches zero.
        timeline.Configure(host.World.Stamp,timeline.InputRevision,3,controls,1,true);
        for(int i=0;i<1024&&timeline.DebtSeconds>0;i++)
        {var progress=timeline.Step(Math.Min(.02,timeline.DebtSeconds));row.terminalDebtCommits++;Need(progress.Fault==null&&progress.LocalCommits==1,progress.Fault??"Terminal debt drain failed.");}
        var last=timeline.ReadProgress();row.localCommits=last.TotalLocalCommits;row.coastCommits=last.TotalCoastCommits;
        row.requested=last.RequestedRuntimeTime;row.committed=last.CommittedRuntimeTime;row.debt=last.DebtSeconds;
        Need(Math.Abs(row.requested-4)<=1e-12,"Piecewise wall demand is not four simulation seconds.");
        Need(row.localCommits==200&&row.coastCommits==0&&row.debt<=1e-12,"Expected200bounded local quanta; requested debt was lost or over-integrated.");
        var packet=OwnedFlightCheckpoint.Capture(host,imported,session,controls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"");
        byte[] checkpoint=OwnedRuntimeCheckpointBinary.EncodeFlight(packet);row.stateSha256=Hash(checkpoint);
        row.workTicks=Stopwatch.GetTimestamp()-started;row.allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        row.checkpointPath=Path.Combine(output,"state-"+cadence+".ofcb");using(var file=new FileStream(row.checkpointPath,FileMode.CreateNew,FileAccess.Write))file.Write(checkpoint);
        row.status="passed";return row;
    }
    static int Main(string[] args)
    {
        string? output=null;bool created=false;var runs=new List<Run>();var pins=new List<Pin>();string? failure=null;
        try
        {
            Need(args.Length>=10&&args.Length%2==0,"--native ABS --parts ABS --context ABS --aero ABS --output NEW_ABS_DIR [--resource-policy strict|acquired-surplus]");
            var options=new Dictionary<string,string>();for(int i=0;i<args.Length;i+=2)Need(new[]{"--native","--parts","--context","--aero","--output","--resource-policy"}.Contains(args[i])&&options.TryAdd(args[i],args[i+1]),"Unknown/duplicate option.");
            foreach(string key in new[]{"--native","--parts","--context","--aero","--output"})Need(options.ContainsKey(key)&&Path.IsPathFullyQualified(options[key]),"Required absolute path missing.");
            output=Path.GetFullPath(options["--output"]);NoLinks(output);Need(!Directory.Exists(output)&&!File.Exists(output),"New output required.");
            foreach(string role in new[]{"--native","--parts","--context","--aero"}){string parent=Path.GetDirectoryName(Path.GetFullPath(options[role]))!;Need(output!=parent&&!output.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.Ordinal),"Output overlaps source directory.");}
            Directory.CreateDirectory(output);created=true;
            var native=Read<NativeAssemblySceneManifest>("native",options["--native"],pins);var parts=Read<OwnedRuntimeCapturedParts>("parts",options["--parts"],pins);
            var captured=Read<OwnedFlightCapture>("context",options["--context"],pins);var aero=Read<OwnedRuntimeCapturedAero>("aero",options["--aero"],pins);WholeCraftGeometry? geometry=null;
            if(!string.IsNullOrEmpty(native.geometry))
            {
                MultiscaleSceneFormat.PortablePath(native.geometry);string parent=Path.GetDirectoryName(Path.GetFullPath(options["--native"]))!;string path=Path.GetFullPath(Path.Combine(parent,native.geometry));
                Need(path.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.Ordinal),"Geometry escapes parent.");NoLinks(path);long length=new FileInfo(path).Length;Need(length>0&&length<=1024L*1024*1024,"Geometry exceeds bound.");
                string sha=HashFile(path);Need(sha==native.geometrySha256,"Geometry hash differs.");geometry=WholeCraftSceneFormat.ReadGeometry(path);Need(HashFile(path)==sha,"Geometry changed during decode.");pins.Add(new Pin{role="geometry",path=path,sha256=sha,bytes=length});
            }
            string policy=options.GetValueOrDefault("--resource-policy","strict");Need(policy=="strict"||policy=="acquired-surplus","Unknown acquired-resource policy.");
            foreach(string cadence in new[]{"30","60","144","irregular"})runs.Add(Replay(cadence,output,native,geometry,parts,aero,captured,pins,policy=="acquired-surplus"));
            Need(runs.All(run=>run.stateSha256==runs[0].stateSha256),"Actual-input physical continuation depends on presentation callback partition.");
            Need(pins.All(pin=>HashFile(pin.path)==pin.sha256),"Original input changed.");
        }
        catch(Exception error){failure=error.ToString();}
        if(created)Write(Path.Combine(output!,"receipt.json"),new{schema="continuum-owned-flight-timeline-lab/v1",status=failure==null?"passed":"failed",failure,clockFrequency=Stopwatch.Frequency,
            assemblySha256=HashFile(Assembly.GetExecutingAssembly().Location),coreSha256=HashFile(typeof(OwnedFlightTimeline).Assembly.Location),inputs=pins,runs,
            scope="Actual acquired craft and body model under four synthetic presentation callback schedules, same frozen wall/control/recording-end boundaries, bounded .02 local quanta and explicit semantic tails, pause/debt/warp. Exact full binary physical checkpoints compared. CPU/allocations include scenario and checkpoint encoding; not measured renderer FPS, original KSP trajectory, or native visual qualification."});
        if(failure!=null){Console.Error.WriteLine(failure);return 2;}Console.WriteLine("Actual-source timing partitions preserve full physical continuation.");return 0;
    }
}
