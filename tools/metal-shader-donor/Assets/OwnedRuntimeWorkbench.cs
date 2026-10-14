using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KspContinuum;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class OwnedRuntimeWorkbench : MonoBehaviour
{
    [Serializable] sealed class StampData
    {public long Epoch,StateRevision,TopologyRevision,DefinitionRevision,FrameRevision;public double Time;public string FrameId;}
    [Serializable] sealed class TimelineData
    {
        public StampData Stamp=new StampData();public string SourceId,ContextSha256,ScheduleSha256,Status,Fault,CoastRejection;
        public long InputRevision,CodeGeneration,ConfigurationRevision,TotalLocalCommits,TotalCoastCommits,ControlResolutions,WorkTicks,ClockFrequency;
        public int LocalCommits,CoastCommits,PendingInputSegments;public double WallSeconds,RequestedRuntimeTime,CommittedRuntimeTime,DebtSeconds,UniversalTime,Warp;
        public bool Paused,DeadlineReached,BudgetExhausted;
    }
    [Serializable] sealed class WorkerData
    {
        public string Status,Fault;public int ThreadId,QueuedCommands;public long AcceptedCommands,CompletedCommands,CancelledCommands,RejectedCommands,Pumps,ReadPressurePumps,PublicationGaps;
        public bool Ready,Stopped;public TimelineData Progress=new TimelineData();
    }
    [Serializable] sealed class Region
    {public long id;public double mass;public double[] position,rotation,velocity,momentum;}
    [Serializable] sealed class Receipt
    {
        public string schema="continuum-owned-runtime-workbench/v1",status="starting",unity,graphics,sourceRevision,nativeSha,partSpecsSha,implementation,lastCommand,error;
        public string scope="Owned double rigid regions, retained part/resource/provider banks and scheduled SI control/aero/thermal dynamics; captured collision geometry for visible presentation. Original KSP module/terrain/contact/UI behavior is not claimed.";
        public int processId,port,parts,bodies,resources,regions,actuators,surfaces,keyboardFrames,targetFrameRate,width,height;
        public long renderFrames;
        public long epoch,stateRevision,topologyRevision,codeGeneration,steps,presentationRootWrites,lastRequest;
        public double time,delta=.02,density,ambient=300,maximumBodyPoseError,lastAdvanceMilliseconds,totalAdvanceMilliseconds;
        public string aeroSha,resourcePolicy,scheduleSha;
        public string worldSha,flightMode,pilotMode,predictionStatus,maneuverId;
        public bool timelineInstalled;
        public TimelineData timeline=new TimelineData();
        public bool workerInstalled,presentationSuspended;
        public string workerStatus,workerFault;
        public string renderError,lastRenderError,renderPauseError;
        public int renderFailures,renderRecoveryAttempts,diagnosticRenderFailures;
        public bool renderPauseAcknowledged;
        public string inputPolicy="Interactive controls replace unapplied values at the next committed frontier while preserving time debt; recorded lab Configure uses historical wall-policy FIFO.";
        public WorkerData worker=new WorkerData();
        public double thermalIntegratedTime,thermalPendingDuration,thermalMaximumLagSeconds,thermalMaximumDeferredKelvin,thermalAbsoluteKelvinError,thermalRelativeError;
        public long thermalAdvances,thermalDeferredSteps,thermalCausalRevision,thermalIntegratedCausalRevision;public bool thermalIndependent;
        public double timelinePumpMilliseconds,totalTimelinePumpMilliseconds,legacyDebtSeconds;
        public string clockScope="Stopwatch wall demand drives retained simulation debt; render reads committed state. Warp changes requested duration, not the local integration quantum. Legacy unframed owner uses a separately labelled retained wall accumulator.";
        public double universalTime,altitude,predictionMilliseconds;
        public int predictionPoints;
        public OwnedFlightPresentation.StatusData visual;
        public bool paused=true,ortho,emptySceneReleased;
        public Region[] states;
        public string[] events;
    }
    sealed class Request {public long Id;public string Text,Reply;public int State;}
    readonly ConcurrentQueue<Request> requests=new ConcurrentQueue<Request>();
    readonly List<string> events=new List<string>();
    readonly Dictionary<long,Transform> regionRoots=new Dictionary<long,Transform>();
    readonly Receipt receipt=new Receipt();
    NativeAssemblySceneImport visible;
    OwnedRuntimeImportResult imported;
    OwnedRuntimeHost host;
    OwnedRuntimeControls controls;
    OwnedRuntimeEnvironment environment;
    OwnedFlightSession flight;
    OwnedFlightTimeline timeline;
    OwnedFlightWorker worker;
    OwnedFlightWorkerContext executingContext;
    OwnedFlightPresentationLease presentationFrame;
    bool presentationSuspended;
    OwnedRuntimeControls configuredControls;
    bool timelineConfigured,configuredPaused;
    double configuredWarp,configuredDeadline,lastWall;
    Func<OwnedRuntimeControls,OwnedRuntimeControls> pilotResolver;
    OwnedFlightPresentation presentation;
    OwnedFlightOverlay overlay;
    bool scientificGrid=true;
    string gridParent="root";
    OwnedFlightPilotData pilot=new OwnedFlightPilotData();
    OwnedFlightPredictionResult prediction;
    OwnedFlightManeuverPlan maneuver;
    Vec cameraBodyOrigin;
    RenderTexture heldFlightImage;
    string visualSha="";
    string cameraFrame="sun";
    string cameraFocus="root";
    OwnedRuntimeStepPlan plan=new OwnedRuntimeStepPlan(.02);
    Transform[] anchors;
    int[] anchorSlots;
    Scene scene;
    new Camera camera;
    TcpListener listener;Thread server;
    volatile bool stopping;
    string output;
    long requestId,lastPresentationTopology=-1;
    double accumulator;
    float cameraDistance=50;
    int reloadCount,lastObservedRenderFrame=-1;
    bool keyboardControlActive;
    bool keyboardSent,workerRetired;
    bool failNextRender;
    Vec lastKeyboardAttitude,lastKeyboardTranslation;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Bootstrap()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_OWNED_RUNTIME_WORKBENCH_OUTPUT")))return;
        new GameObject("Continuum owned runtime",typeof(OwnedRuntimeWorkbench));
    }
    static string Hash(string path){using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
    static string Hash(byte[] bytes){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static Vector3 V(Vec value){return new Vector3((float)value.X,(float)value.Y,(float)value.Z);}
    static Quaternion Q(Rotation value){return new Quaternion((float)value.X,(float)value.Y,(float)value.Z,(float)value.W);}
    static double[] A(Vec value){return new[]{value.X,value.Y,value.Z};}
    static double[] A(Rotation value){return new[]{value.X,value.Y,value.Z,value.W};}
    static double Number(string text){return double.Parse(text,CultureInfo.InvariantCulture);}
    static void Need(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    void Start()
    {
        try
        {
            output=Environment.GetEnvironmentVariable("CONTINUUM_OWNED_RUNTIME_WORKBENCH_OUTPUT");
            Need(Path.IsPathRooted(output)&&!Directory.Exists(output)&&!File.Exists(output),"New private absolute output required.");Directory.CreateDirectory(output);
            string manifestPath=Environment.GetEnvironmentVariable("CONTINUUM_OWNED_RUNTIME_IMPORT");
            Need(!string.IsNullOrEmpty(manifestPath)&&Path.IsPathRooted(manifestPath)&&File.Exists(manifestPath),"Captured real craft is required.");
            string specsPath=Path.Combine(Path.GetDirectoryName(manifestPath),"owned-parts.json");
            var native=JsonUtility.FromJson<NativeAssemblySceneManifest>(File.ReadAllText(manifestPath));
            var specs=JsonUtility.FromJson<OwnedRuntimeCapturedParts>(File.ReadAllText(specsPath));
            WholeCraftGeometry geometry=null;
            if(!string.IsNullOrEmpty(native.geometry))
            {string file=Path.Combine(Path.GetDirectoryName(manifestPath),native.geometry);Need(Hash(file)==native.geometrySha256,"Collider geometry digest differs.");geometry=WholeCraftSceneFormat.ReadGeometry(file);}
            string aeroPath=Path.Combine(Path.GetDirectoryName(manifestPath),"owned-aero.json");
            var aero=File.Exists(aeroPath)?JsonUtility.FromJson<OwnedRuntimeCapturedAero>(File.ReadAllText(aeroPath)):null;
            string nativeHash=Hash(manifestPath),specHash=Hash(specsPath);
            receipt.aeroSha=File.Exists(aeroPath)?Hash(aeroPath):null;
            string worldPath=Environment.GetEnvironmentVariable("CONTINUUM_OWNED_FLIGHT_WORLD");
            OwnedFlightCapture capturedWorld=null;string worldHash=null;
            if(!string.IsNullOrEmpty(worldPath))
            {
                Need(Path.IsPathRooted(worldPath)&&File.Exists(worldPath)&&new FileInfo(worldPath).Length<=1024*1024,"Owned flight context missing/excessive.");
                capturedWorld=JsonUtility.FromJson<OwnedFlightCapture>(File.ReadAllText(worldPath));worldHash=Hash(worldPath);
                receipt.scope="Owned captured central-body flight, control/resource/aero/thermal banks, guarded passive coast, disposable predictions and actual captured craft/planet visual assets. Terrain/contact, articulation, original modules and stock shader fidelity are unqualified.";
            }
            Func<OwnedFlightWorkerContext> initialize=delegate
            {
                imported=OwnedRuntimeImport.Create(native,geometry,specs,nativeHash,specHash,allowAcquiredSurplus:true);
                if(aero!=null)OwnedRuntimeAcquiredAero.Attach(imported,OwnedRuntimeAcquiredAero.Build(imported,aero));
                imported.Systems.SetThermalPolicy(imported.World.Stamp,new OwnedRuntimeThermalPolicy(true));
                host=new OwnedRuntimeHost(imported.World,imported.Systems);controls=new OwnedRuntimeControls {EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};
                environment=new OwnedRuntimeEnvironment("captured-craft/vacuum-research",0,host.World.Stamp.Time,0,300,new Vec(),new Vec());
                var context=OwnedFlightWorld.Admit(capturedWorld,nativeHash,specHash,worldHash);
                flight=OwnedFlightSession.SeedCaptured(host.World,context,context.CapturedRootPosition);
                return new OwnedFlightWorkerContext(host,flight,imported);
            };
            if(capturedWorld!=null)
            {
                worker=new OwnedFlightWorker(initialize);receipt.workerInstalled=receipt.timelineInstalled=true;receipt.worldSha=worldHash;
                OnWorker(delegate(OwnedFlightWorkerContext context){timeline=context.Timeline;pilotResolver=ResolvePilot;timelineConfigured=false;ConfigureTimeline(WallNow());});
            }
            else
            {
                imported=OwnedRuntimeImport.Create(native,geometry,specs,nativeHash,specHash,allowAcquiredSurplus:true);
                if(aero!=null)OwnedRuntimeAcquiredAero.Attach(imported,OwnedRuntimeAcquiredAero.Build(imported,aero));
                host=new OwnedRuntimeHost(imported.World,imported.Systems);controls=new OwnedRuntimeControls {EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};
                environment=new OwnedRuntimeEnvironment("captured-craft/vacuum-research",0,host.World.Stamp.Time,0,300,new Vec(),new Vec());
            }
            receipt.resourcePolicy=imported.Source.resourcePolicy;
            scene=SceneManager.CreateScene("Owned runtime "+Guid.NewGuid().ToString("N"),new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            visible=NativeAssemblySceneImport.Load(manifestPath,scene);visible.Owner.Body.isKinematic=true;visible.Owner.Body.detectCollisions=false;
            anchors=new Transform[visible.Owner.Members.Count];anchorSlots=new int[anchors.Length];
            var slots=new Dictionary<int,int>();foreach(var binding in imported.BodyBindings)slots.Add(binding.sourceBodyId,binding.bodySlot);
            for(int i=0;i<anchors.Length;i++){anchors[i]=visible.Owner.Members[i].Anchor;anchorSlots[i]=slots[visible.Receipt.sourceBodyIds[i]];foreach(var collider in visible.Owner.Members[i].Colliders)collider.enabled=false;}
            camera=new GameObject("Owned runtime inspection camera").AddComponent<Camera>();SceneManager.MoveGameObjectToScene(camera.gameObject,scene);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.055f,.085f);camera.fieldOfView=50;camera.nearClipPlane=.05f;camera.farClipPlane=10000;camera.allowMSAA=false;
            if(flight!=null)
            {
                string path=Environment.GetEnvironmentVariable("CONTINUUM_OWNED_FLIGHT_VISUALS");Need(!string.IsNullOrEmpty(path),"Same-epoch visual assets required for world flight.");visualSha=Hash(path);
                presentation=new OwnedFlightPresentation(imported,flight,path,visualSha,Shader.Find("Continuum/MultiscaleDiagnostic"));
                overlay=new OwnedFlightOverlay();
                foreach(var shape in anchors)foreach(var renderer in shape.GetComponentsInChildren<Renderer>(true))renderer.enabled=false;
                camera.cullingMask=0;camera.gameObject.AddComponent<OwnedFlightImageEffect>().Owner=this;
            }
            QualitySettings.vSyncCount=0;Application.targetFrameRate=60;Time.timeScale=1;
            lastWall=WallNow();if(worker==null)ResetTimeline();else RefreshFrame();
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start(4);receipt.port=((IPEndPoint)listener.LocalEndpoint).Port;
            receipt.processId=System.Diagnostics.Process.GetCurrentProcess().Id;receipt.unity=Application.unityVersion;receipt.graphics=SystemInfo.graphicsDeviceType.ToString();
            receipt.sourceRevision=Environment.GetEnvironmentVariable("CONTINUUM_SOURCE_REVISION");receipt.nativeSha=Hash(manifestPath);receipt.partSpecsSha=Hash(specsPath);receipt.status="ready";
            Present(true);FollowCamera();UpdateReceipt();SaveReceipt();server=new Thread(Serve){IsBackground=true};server.Start();
        }
        catch(Exception error){receipt.status="failed";receipt.error=error.ToString();if(output!=null&&Directory.Exists(output))SaveReceipt();Debug.LogException(error);}
    }
    void Update()
    {
        if(stopping)return;
        ReleaseFrame();
        Request request;int handled=0;
        while(handled++<16&&requests.TryDequeue(out request))
        {
            if(Interlocked.CompareExchange(ref request.State,1,0)!=0)continue;
            receipt.lastRequest=request.Id;receipt.lastCommand=request.Text;
            try {request.Reply=Execute(request.Text);UpdateReceipt();SaveReceipt();}
            catch(Exception error){events.Add("rejected: "+request.Text+" :: "+error.Message);request.Reply="{\"status\":\"rejected\",\"request\":"+request.Id+",\"message\":\"Command failed; inspect status for committed progress.\"}";receipt.error=error.ToString();UpdateReceipt();SaveReceipt();}
            finally {Interlocked.Exchange(ref request.State,2);}
        }
        if(stopping||host==null)return;
        if(worker!=null){RefreshFrame();var state=worker.ReadStatus();receipt.workerFault=state.Fault;if(presentationFrame!=null)receipt.paused=presentationFrame.Progress.Paused||receipt.workerFault!=null;}
        if(Input.GetKeyDown(KeyCode.Space)&&receipt.error==null){try{Execute("pause "+(receipt.paused?"off":"on"));}catch(Exception error){receipt.error=error.Message;}}
        if(Input.GetKeyDown(KeyCode.O)){receipt.ortho=!receipt.ortho;camera.orthographic=receipt.ortho;}
        if(!receipt.paused&&receipt.workerFault==null)
        {
            if(Input.GetKey(KeyCode.W)||Input.GetKey(KeyCode.S)||Input.GetKey(KeyCode.A)||Input.GetKey(KeyCode.D)||Input.GetKey(KeyCode.Q)||Input.GetKey(KeyCode.E)
                ||Input.GetKey(KeyCode.I)||Input.GetKey(KeyCode.J)||Input.GetKey(KeyCode.K)||Input.GetKey(KeyCode.L)||Input.GetKey(KeyCode.H)||Input.GetKey(KeyCode.N))keyboardControlActive=true;
            if(keyboardControlActive)
            {
                var attitude=new Vec(Axis(KeyCode.W,KeyCode.S),Axis(KeyCode.A,KeyCode.D),Axis(KeyCode.Q,KeyCode.E));
                var translation=new Vec(Axis(KeyCode.L,KeyCode.J),Axis(KeyCode.I,KeyCode.K),Axis(KeyCode.H,KeyCode.N));
                if(worker!=null&&(!keyboardSent||!SameVector(lastKeyboardAttitude,attitude)||!SameVector(lastKeyboardTranslation,translation)))
                {OnWorker(delegate(OwnedFlightWorkerContext context){controls.Attitude=attitude;controls.Translation=translation;context.SetLiveControls(controls);configuredControls=controls;});lastKeyboardAttitude=attitude;lastKeyboardTranslation=translation;keyboardSent=true;}
                else if(worker==null){controls.Attitude=attitude;controls.Translation=translation;}receipt.keyboardFrames++;
            }
        }
        double wall=WallNow();
        if(worker!=null)
        {
            RefreshFrame();
            CopyWorker(receipt.worker,worker.ReadStatus());receipt.workerStatus=receipt.worker.Status;receipt.workerFault=receipt.worker.Fault;
            if(presentationFrame!=null){UpdateFrameReadbacks(presentationFrame);receipt.paused=receipt.timeline.Paused||receipt.workerFault!=null;receipt.steps=receipt.timeline.TotalLocalCommits+receipt.timeline.TotalCoastCommits;}
        }
        else
        {
            double elapsed=wall-lastWall;Need(elapsed>=0,"Wall clock regressed.");lastWall=wall;
            if(!receipt.paused)accumulator+=elapsed;
            int count=0;while(!receipt.paused&&accumulator>=plan.FixedDelta&&count++<8)
            {try{AdvanceLegacyOne();accumulator-=plan.FixedDelta;}catch(Exception error){receipt.paused=true;receipt.error=error.ToString();SaveReceipt();break;}}
            receipt.legacyDebtSeconds=accumulator*plan.WarpRate;
        }
        if(!presentationSuspended){Present(false);FollowCamera();}
    }
    void OnWorker(Action<OwnedFlightWorkerContext> action,OwnedFlightWorkerInvocation kind=OwnedFlightWorkerInvocation.ReadOnly)
    {
        var acknowledged=worker.Invoke<int>(delegate(OwnedFlightWorkerContext context){host=context.Host;flight=context.Session;timeline=context.Timeline;executingContext=context;try{action(context);return 0;}finally{executingContext=null;}},kind);
        Need(acknowledged.Wait(30000),"Worker request remains pending; do not repeat the physical mutation.");if(acknowledged.Error!=null)throw acknowledged.Error;
    }
    void ReleaseFrame(){if(presentationFrame!=null){presentationFrame.Dispose();presentationFrame=null;}}
    void RefreshFrame(){ReleaseFrame();if(worker!=null)presentationFrame=worker.AcquirePresentation();}
    void UpdateFrameReadbacks(OwnedFlightPresentationLease frame)
    {
        var read=frame.Read;CopyProgress(receipt.timeline,frame.Progress);CopyProgress(receipt.worker.Progress,frame.Progress);
        receipt.time=read.Stamp.Time;receipt.epoch=read.Stamp.Epoch;receipt.stateRevision=read.Stamp.StateRevision;receipt.topologyRevision=read.Stamp.TopologyRevision;
        receipt.universalTime=frame.UniversalTime;receipt.altitude=frame.Context.Altitude(frame.Descriptor.CenterPosition(read,0));receipt.flightMode=frame.Mode;
        receipt.thermalIntegratedTime=read.ThermalIntegratedTime;receipt.thermalPendingDuration=read.ThermalPendingDuration;
    }
    static double Axis(KeyCode positive,KeyCode negative){return (Input.GetKey(positive)?1:0)-(Input.GetKey(negative)?1:0);}
    static double LengthSquared(Vec value){return value.X*value.X+value.Y*value.Y+value.Z*value.Z;}
    static bool SameVector(Vec a,Vec b){return a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;}
    static void CopyProgress(TimelineData d,OwnedFlightTimelineProgress s)
    {
        d.Stamp.Epoch=s.Stamp.Epoch;d.Stamp.StateRevision=s.Stamp.StateRevision;d.Stamp.TopologyRevision=s.Stamp.TopologyRevision;d.Stamp.DefinitionRevision=s.Stamp.DefinitionRevision;d.Stamp.FrameRevision=s.Stamp.FrameRevision;d.Stamp.Time=s.Stamp.Time;d.Stamp.FrameId=s.Stamp.FrameId;
        d.SourceId=s.SourceId;d.ContextSha256=s.ContextSha256;d.ScheduleSha256=s.ScheduleSha256;d.Status=s.Status;d.Fault=s.Fault;d.CoastRejection=s.CoastRejection;
        d.InputRevision=s.InputRevision;d.CodeGeneration=s.CodeGeneration;d.ConfigurationRevision=s.ConfigurationRevision;d.TotalLocalCommits=s.TotalLocalCommits;d.TotalCoastCommits=s.TotalCoastCommits;d.ControlResolutions=s.ControlResolutions;
        d.LocalCommits=s.LocalCommits;d.CoastCommits=s.CoastCommits;d.PendingInputSegments=s.PendingInputSegments;d.WallSeconds=s.WallSeconds;d.RequestedRuntimeTime=s.RequestedRuntimeTime;d.CommittedRuntimeTime=s.CommittedRuntimeTime;d.DebtSeconds=s.DebtSeconds;d.UniversalTime=s.UniversalTime;d.Warp=s.Warp;
        d.Paused=s.Paused;d.DeadlineReached=s.DeadlineReached;d.BudgetExhausted=s.BudgetExhausted;d.WorkTicks=s.WorkTicks;d.ClockFrequency=s.ClockFrequency;
    }
    static void CopyWorker(WorkerData d,OwnedFlightWorkerStatus s)
    {
        d.Status=s.Status;d.Fault=s.Fault;d.ThreadId=s.ThreadId;d.QueuedCommands=s.QueuedCommands;d.AcceptedCommands=s.AcceptedCommands;d.CompletedCommands=s.CompletedCommands;d.CancelledCommands=s.CancelledCommands;d.RejectedCommands=s.RejectedCommands;
        d.Pumps=s.Pumps;d.ReadPressurePumps=s.ReadPressurePumps;d.PublicationGaps=s.PublicationGaps;d.Ready=s.Ready;d.Stopped=s.Stopped;CopyProgress(d.Progress,s.Progress);
    }
    static double WallNow(){return System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;}
    static bool SameControls(OwnedRuntimeControls a,OwnedRuntimeControls b)
    {return a.Throttle==b.Throttle&&a.Attitude.X==b.Attitude.X&&a.Attitude.Y==b.Attitude.Y&&a.Attitude.Z==b.Attitude.Z&&a.Translation.X==b.Translation.X&&a.Translation.Y==b.Translation.Y&&a.Translation.Z==b.Translation.Z&&a.EnginesEnabled==b.EnginesEnabled&&a.RcsEnabled==b.RcsEnabled&&a.WheelsEnabled==b.WheelsEnabled;}
    void ResetTimeline()
    {
        timeline=null;timelineConfigured=false;receipt.timelineInstalled=flight!=null;
        if(flight==null)return;
        timeline=new OwnedFlightTimeline(host,flight,WallNow(),plan.FixedDelta,"owned-runtime-workbench");
        if(pilotResolver==null)pilotResolver=ResolvePilot;
        timeline.ControlResolver=pilot.mode=="manual"?null:pilotResolver;ConfigureTimeline(WallNow());
    }
    void ConfigureTimeline(double wall,bool coast=false)
    {
        if(timeline==null)return;
        double deadline=maneuver==null?double.PositiveInfinity:maneuver.EventUniversalTime;
        if(!timelineConfigured||!SameControls(controls,configuredControls)||receipt.paused!=configuredPaused||plan.WarpRate!=configuredWarp||deadline!=configuredDeadline||coast)
        {
            if(executingContext!=null)executingContext.Configure(controls,plan.WarpRate,receipt.paused,coast,coast,deadline,0,wall);
            else timeline.Configure(host.World.Stamp,timeline.InputRevision,wall,controls,plan.WarpRate,receipt.paused,coast,coast,deadline);
            configuredControls=controls;configuredPaused=receipt.paused;configuredWarp=plan.WarpRate;configuredDeadline=deadline;timelineConfigured=true;
        }
    }
    void SetPause(bool value)
    {
        Need(value||maneuver==null,"Pending nodes support paused immediate coast execution only; execute-node or clear-node before running.");
        Need(timeline==null||timeline.Fault==null,"Timeline fault is latched; use explicit code reload recovery before resuming.");
        receipt.paused=value;ConfigureTimeline(WallNow());
    }
    void RequireColdTimeline()
    {if(timeline!=null)Need(receipt.paused&&timeline.Paused&&timeline.DebtSeconds==0,"This cold mutation requires pause and zero retained debt; use step to consume debt explicitly.");}
    void AcceptProgress(OwnedFlightTimelineProgress progress)
    {
        CopyProgress(receipt.timeline,progress);receipt.steps+=progress.LocalCommits+progress.CoastCommits;
        receipt.lastAdvanceMilliseconds=progress.WorkTicks*1000.0/progress.ClockFrequency;receipt.totalAdvanceMilliseconds+=receipt.lastAdvanceMilliseconds;
        if(progress.Fault!=null){receipt.paused=true;receipt.error=progress.Fault;}
    }
    void AdvanceLegacyOne()
    {
        var input=new OwnedRuntimeInput(controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z);
        long started=System.Diagnostics.Stopwatch.GetTimestamp();host.Advance(plan.Delta,input,environment,controls);
        receipt.lastAdvanceMilliseconds=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
        receipt.totalAdvanceMilliseconds+=receipt.lastAdvanceMilliseconds;receipt.steps++;
    }
    void Advance(int count)
    {
        Need(count>0&&count<=10000,"Step request outside its bound.");
        if(timeline!=null){ConfigureTimeline(WallNow());for(int i=0;i<count;i++){var progress=timeline.Step(plan.FixedDelta);AcceptProgress(progress);Need(progress.Fault==null&&progress.LocalCommits==1,progress.Fault??"Step reached its deadline without a commit.");}}
        else for(int i=0;i<count;i++)AdvanceLegacyOne();
    }
    void Present(bool force)
    {
        if(presentation!=null)return;
        using(var view=host.World.AcquireRead())
        {
            if(force||lastPresentationTopology!=view.Stamp.TopologyRevision)
            {
                var retained=new HashSet<long>();for(int r=0;r<view.RegionCount;r++)
                {
                    var state=view.GetRegion(r);retained.Add(state.Id);Transform root;
                    if(!regionRoots.TryGetValue(state.Id,out root)){root=new GameObject("Owned rigid region "+state.Id).transform;SceneManager.MoveGameObjectToScene(root.gameObject,scene);regionRoots.Add(state.Id,root);}
                    root.SetPositionAndRotation(V(state.Motion.RootPosition),Q(state.Motion.Orientation));receipt.presentationRootWrites++;
                }
                for(int i=0;i<anchors.Length;i++)
                {
                    int body=anchorSlots[i],region=view.Topology.RegionOfBody(body);var state=view.GetRegion(region);var member=view.GetBody(body);
                    anchors[i].SetParent(regionRoots[state.Id],false);anchors[i].localPosition=V(member.LocalPosition);anchors[i].localRotation=Q(member.LocalRotation);anchors[i].localScale=Vector3.one;
                }
                var removed=new List<long>();foreach(var pair in regionRoots)if(!retained.Contains(pair.Key)){Destroy(pair.Value.gameObject);removed.Add(pair.Key);}foreach(long id in removed)regionRoots.Remove(id);
                lastPresentationTopology=view.Stamp.TopologyRevision;
            }
            for(int r=0;r<view.RegionCount;r++){var state=view.GetRegion(r);regionRoots[state.Id].SetPositionAndRotation(V(state.Motion.RootPosition),Q(state.Motion.Orientation));receipt.presentationRootWrites++;}
        }
    }
    void FollowCamera()
    {
        if(worker!=null)
        {
            if(presentationFrame==null)return;var view=presentationFrame.Read;var state=view.GetRegion(0);
            Vec root=presentationFrame.Origin+state.Motion.RootPosition,center=root+state.Motion.Orientation.Rotate(state.Properties.CenterOfMass);
            Vec target=cameraFocus=="body"?new Vec():cameraFocus=="com"?center:root,direction=presentationFrame.Context.SunDirection;
            if(cameraFrame=="orbit")direction=Unit(center);
            else if(cameraFrame=="ship")direction=state.Motion.Orientation.Rotate(new Vec(0,0,-1));
            else if(cameraFrame=="plane")direction=Unit(Vec.Cross(center,state.Motion.Velocity));
            else if(cameraFrame=="quarter")direction=Unit(direction+Unit(Vec.Cross(direction,new Vec(0,1,0)))*.6);
            cameraBodyOrigin=target+direction*cameraDistance;
            Vector3 forward=V(direction*-1);Vector3 up=Math.Abs(Vector3.Dot(forward,Vector3.up))>.95f?Vector3.forward:Vector3.up;
            camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.LookRotation(forward,up));camera.orthographicSize=cameraDistance*.35f;return;
        }
        using(var view=host.World.AcquireRead())
        {
            Vec center=view.GetRegion(0).Motion.RootPosition;Vector3 target=V(center);
            Vector3 lightView=new Vector3(.3f,.7f,-.5f).normalized;
            camera.transform.SetPositionAndRotation(target+lightView*cameraDistance,Quaternion.LookRotation(-lightView,Vector3.up));
            camera.orthographicSize=cameraDistance*.35f;
        }
    }
    static Vec Unit(Vec value){double length=Math.Sqrt(value.X*value.X+value.Y*value.Y+value.Z*value.Z);Need(length>0,"Camera direction is degenerate.");return value*(1/length);}
    public void RenderFlight(RenderTexture source,RenderTexture destination)
    {
        if(stopping||presentation==null){Graphics.Blit(source,destination);return;}
        if(presentationSuspended){Graphics.Blit(heldFlightImage==null?source:heldFlightImage,destination);return;}
        try
        {
            if(failNextRender){failNextRender=false;receipt.diagnosticRenderFailures++;throw new InvalidOperationException("Owned diagnostic fail-next-render before GPU submission.");}
            int width=destination==null?camera.pixelWidth:destination.width,height=destination==null?camera.pixelHeight:destination.height;
            if(heldFlightImage==null||heldFlightImage.width!=width||heldFlightImage.height!=height)
            {if(heldFlightImage!=null){heldFlightImage.Release();Destroy(heldFlightImage);}heldFlightImage=new RenderTexture(width,height,0,RenderTextureFormat.ARGB32);heldFlightImage.Create();}
            if(!presentationSuspended&&presentationFrame!=null){presentation.Render(camera,cameraBodyOrigin,heldFlightImage,presentationFrame);if(lastObservedRenderFrame!=Time.frameCount){lastObservedRenderFrame=Time.frameCount;receipt.renderFrames++;}}
            Graphics.Blit(heldFlightImage,destination);
        }
        catch(Exception error)
        {
            receipt.renderError=receipt.lastRenderError=error.ToString();receipt.renderFailures++;presentationSuspended=receipt.presentationSuspended=true;
            ReleaseFrame();receipt.renderPauseAcknowledged=false;receipt.renderPauseError=null;
            if(worker!=null)
            {
                try
                {
                    var pause=worker.SetPause(true);Need(pause.Wait(5000),"Render failure pause remains pending; no repeat request was issued.");
                    if(pause.Error!=null)throw pause.Error;var progress=pause.Result;CopyProgress(receipt.timeline,progress);
                    receipt.paused=progress.Paused;receipt.renderPauseAcknowledged=progress.Paused;if(progress.Paused)timelineConfigured=false;
                    CopyWorker(receipt.worker,worker.ReadStatus());
                }
                catch(Exception pauseError){receipt.renderPauseError=pauseError.ToString();var observed=worker.ReadStatus();CopyWorker(receipt.worker,observed);receipt.paused=observed.Progress.Paused;}
            }
            else{receipt.paused=true;receipt.renderPauseAcknowledged=true;}
            SaveReceipt();Graphics.Blit(source,destination);
        }
    }
    OwnedRuntimeControls ResolvePilot(OwnedRuntimeControls source)
    {
        if(pilot.mode=="manual")return source;
        int slot=flight.FindRegion(pilot.regionId);using(var read=host.World.AcquireRead())
        {
            var state=read.GetRegion(slot);Rotation frame=state.Motion.Orientation*host.Systems.ControlOrientation;
            var authority=OwnedFlightControl.ObserveAuthority(host.World,host.Systems,state.Id,frame);
            OwnedFlightGuidance guidance=pilot.mode=="attitude"?OwnedFlightControl.AttitudeHold(state.Motion.Orientation,
                OwnedRuntimeDynamics.AngularVelocity(state.Properties,state.Motion.Orientation,state.Motion.AngularMomentum),new Rotation(pilot.targetOrientation[0],pilot.targetOrientation[1],pilot.targetOrientation[2],pilot.targetOrientation[3]),frame,authority.PositiveAngularAcceleration,authority.NegativeAngularAcceleration,pilot.maximumRate,pilot.responseSeconds):
                OwnedFlightControl.VelocityHold(state.Motion.Velocity,new Vec(pilot.targetVelocity[0],pilot.targetVelocity[1],pilot.targetVelocity[2]),frame,authority.LinearAcceleration,pilot.responseSeconds);
            return OwnedFlightControl.Controls(guidance.Input,false,true,true);
        }
    }
    void UpdateReceipt()
    {
        if(host==null)return;
        receipt.targetFrameRate=Application.targetFrameRate;receipt.width=Screen.width;receipt.height=Screen.height;receipt.presentationSuspended=presentationSuspended;
        if(worker!=null){ReleaseFrame();OnWorker(delegate(OwnedFlightWorkerContext context){CollectPhysicalReceipt();});RefreshFrame();}
        else if(!workerRetired)CollectPhysicalReceipt();
        receipt.events=events.ToArray();receipt.visual=presentation==null?null:presentation.Status;
    }
    void CollectPhysicalReceipt()
    {
        if(worker!=null)CopyWorker(receipt.worker,worker.ReadStatus());
        if(timeline!=null){var progress=timeline.ReadProgress();CopyProgress(receipt.timeline,progress);CopyProgress(receipt.worker.Progress,progress);}
        using(var view=host.World.AcquireRead())
        {
            receipt.parts=view.PartCount;receipt.bodies=view.BodyCount;receipt.resources=view.ResourceCount;receipt.regions=view.RegionCount;
            receipt.actuators=host.Systems.ActuatorCount;receipt.surfaces=host.Systems.SurfaceCount;
            receipt.time=view.Stamp.Time;receipt.epoch=view.Stamp.Epoch;receipt.stateRevision=view.Stamp.StateRevision;receipt.topologyRevision=view.Stamp.TopologyRevision;receipt.thermalIntegratedTime=view.ThermalIntegratedTime;receipt.thermalPendingDuration=view.ThermalPendingDuration;
            receipt.codeGeneration=host.CodeGeneration;receipt.implementation=host.ScheduleImplementation;receipt.scheduleSha=host.ScheduleSourceSha256;receipt.delta=timeline==null?plan.Delta:timeline.MaximumLocalStepSeconds;receipt.density=environment.Density;receipt.ambient=environment.Temperature;
            receipt.states=new Region[view.RegionCount];
            for(int r=0;r<view.RegionCount;r++){var state=view.GetRegion(r);receipt.states[r]=new Region {id=state.Id,mass=state.Properties.Mass,position=A(state.Motion.RootPosition),rotation=A(state.Motion.Orientation),velocity=A(state.Motion.Velocity),momentum=A(state.Motion.AngularMomentum)};}
        }
        if(flight!=null){using(var read=host.World.AcquireRead()){var state=read.GetRegion(0);Vec center=flight.Origin+state.Motion.RootPosition+state.Motion.Orientation.Rotate(state.Properties.CenterOfMass);double utc=flight.Context.Epoch+(read.Stamp.Time-flight.RuntimeEpoch);var sampled=flight.Context.EvaluateSample(center,utc,read.Stamp.Time);receipt.density=sampled.Density;receipt.ambient=sampled.Temperature;receipt.universalTime=utc;receipt.altitude=flight.Context.Altitude(center);}receipt.flightMode=flight.Mode;receipt.pilotMode=pilot.mode;
            if(prediction!=null){receipt.predictionStatus=prediction.Status;receipt.predictionPoints=prediction.Count;receipt.predictionMilliseconds=prediction.ElapsedTicks*1000.0/prediction.ClockFrequency;}receipt.maneuverId=maneuver==null?null:maneuver.Id;}
        var thermal=host.World.CaptureSnapshot().Thermal;receipt.thermalIndependent=thermal.independent;receipt.thermalAdvances=thermal.advances;receipt.thermalDeferredSteps=thermal.deferredSteps;receipt.thermalCausalRevision=thermal.causalRevision;receipt.thermalIntegratedCausalRevision=thermal.integratedCausalRevision;
        receipt.thermalMaximumLagSeconds=thermal.maximumLagSeconds;receipt.thermalMaximumDeferredKelvin=thermal.maximumDeferredKelvin;receipt.thermalAbsoluteKelvinError=thermal.absoluteKelvinError;receipt.thermalRelativeError=thermal.relativeError;
    }
    void SaveReceipt(){File.WriteAllText(Path.Combine(output,"status.json"),JsonUtility.ToJson(receipt,true));}
    string Execute(string text)
    {
        string[] args=text.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);Need(args.Length>0,"Empty command.");ReleaseFrame();
        if(args[0]=="status"){UpdateReceipt();return JsonUtility.ToJson(receipt);}
        if(args[0]=="quit"){stopping=true;Invoke("Quit",.15f);return "{\"status\":\"quitting\"}";}
        if(args[0]=="render-rate"&&args.Length==2){int rate=int.Parse(args[1],CultureInfo.InvariantCulture);Need(rate==-1||rate==30||rate==60||rate==144,"Render rate accepts -1,30,60,144.");Application.targetFrameRate=rate;}
        else if(args[0]=="presentation"&&args.Length==2){Need(args[1]=="on"||args[1]=="off","Presentation expects on or off.");presentationSuspended=args[1]=="off";if(!presentationSuspended&&receipt.renderError!=null){receipt.renderRecoveryAttempts++;receipt.renderError=null;}}
        else if(args[0]=="render-fail-next"&&args.Length==1){Need(presentation!=null&&!presentationSuspended,"Active owned presentation required for one diagnostic failure.");failNextRender=true;}
        else if(args[0]=="debug"&&args.Length==2){Need(presentation!=null,"Owned visual source required.");presentation.SetDebugView(args[1]);}
        else if(args[0]=="scale"&&args.Length==2){Need(presentation!=null,"Owned visual source required.");presentation.SetWorldScale((float)Number(args[1]));}
        else if(args[0]=="view"&&args.Length==2){Need(args[1]=="sun"||args[1]=="quarter"||args[1]=="ship"||args[1]=="orbit"||args[1]=="plane","Unknown camera frame.");cameraFrame=args[1];}
        else if(args[0]=="focus"&&args.Length==2){Need(args[1]=="root"||args[1]=="com"||args[1]=="body","Unknown camera focus.");cameraFocus=args[1];}
        else if(args[0]=="grid"&&args.Length==2){Need(args[1]=="off"||args[1]=="root"||args[1]=="com","Grid parent required.");scientificGrid=args[1]!="off";if(scientificGrid)gridParent=args[1];}
        else if(args[0]=="camera"&&args.Length==2){double distance=Number(args[1]);Need(!double.IsNaN(distance)&&distance>=2&&distance<=100000000,"Camera range outside2..100000000m.");cameraDistance=(float)distance;}
        else if(args[0]=="ortho"&&args.Length==2){Need(args[1]=="on"||args[1]=="off","Ortho expects on or off.");receipt.ortho=args[1]=="on";camera.orthographic=receipt.ortho;}
        else if(args[0]=="capture"&&args.Length==2){Need(Path.IsPathRooted(args[1])&&!File.Exists(args[1]),"Fresh absolute screenshot required.");RefreshFrame();FollowCamera();ScreenCapture.CaptureScreenshot(args[1]);}
        else if(args[0]=="poses"&&args.Length==1)
        {
            RefreshFrame();var view=presentationFrame==null?host.World.AcquireRead():presentationFrame.Read;
            try{double maximum=0;for(int i=0;i<anchors.Length;i++){int body=anchorSlots[i],region=view.Topology.RegionOfBody(body);var state=view.GetRegion(region);var member=view.GetBody(body);Vec expected=state.Motion.RootPosition+state.Motion.Orientation.Rotate(member.LocalPosition);Vector3 actual=anchors[i].position;maximum=Math.Max(maximum,Math.Sqrt(Math.Pow(actual.x-expected.X,2)+Math.Pow(actual.y-expected.Y,2)+Math.Pow(actual.z-expected.Z,2)));}receipt.maximumBodyPoseError=maximum;}finally{if(presentationFrame==null)view.Dispose();}
        }
        else if(worker!=null)
        {
            var kind=args[0]=="reload"?OwnedFlightWorkerInvocation.SameTimeMutation:
                args[0]=="step"||args[0]=="timeline"||args[0]=="coast"?OwnedFlightWorkerInvocation.TimelineMutation:
                args[0]=="orbit"||args[0]=="atmo-flight"||args[0]=="rebase"||args[0]=="pilot"||args[0]=="node"||args[0]=="split"||args[0]=="join"||args[0]=="restore"||args[0]=="execute-node"||args[0]=="thermal"?OwnedFlightWorkerInvocation.ColdMutation:OwnedFlightWorkerInvocation.ReadOnly;
            OnWorker(delegate(OwnedFlightWorkerContext context){ExecutePhysical(text,args);if(args[0]=="restore")context.Replace(host,flight,imported);},kind);
            if(kind!=OwnedFlightWorkerInvocation.ReadOnly)OnWorker(delegate(OwnedFlightWorkerContext context){timelineConfigured=false;receipt.error=null;ConfigureTimeline(WallNow());});
            if(args[0]=="restore")presentation.BindSession(flight);
        }
        else ExecutePhysical(text,args);
        events.Add(text);if(events.Count>256)events.RemoveAt(0);RefreshFrame();Present(args[0]=="restore"||args[0]=="split"||args[0]=="join");FollowCamera();UpdateReceipt();return JsonUtility.ToJson(receipt);
    }
    void ExecutePhysical(string text,string[] args)
    {
        Need(host!=null&&receipt.status=="ready","Runtime is not ready.");
        if(flight!=null&&(args[0]=="atmosphere"||args[0]=="vacuum"||args[0]=="wind"))throw new InvalidOperationException("World flight obtains the field from captured body/altitude; use atmo-flight/orbit placement.");
        if(timeline!=null&&(args[0]=="orbit"||args[0]=="atmo-flight"||args[0]=="rebase"||args[0]=="pilot"||args[0]=="node"||args[0]=="execute-node"||args[0]=="coast"||args[0]=="split"||args[0]=="join"||args[0]=="save"||args[0]=="restore"))RequireColdTimeline();
        double orbitAltitude=0;Vec orbitRadial=new Vec(),orbitPlane=new Vec();
        if(args[0]=="orbit")
        {
            Need(args.Length==2&&flight!=null,"Orbit expects a world flight and one positive altitude.");orbitAltitude=Number(args[1]);
            double radius=flight.Context.Radius+orbitAltitude;Need(!double.IsNaN(orbitAltitude)&&!double.IsInfinity(orbitAltitude)&&orbitAltitude>0&&!double.IsInfinity(radius),"Orbital altitude/radius is invalid.");
            orbitRadial=flight.Context.SunDirection;orbitPlane=OrbitNormal();Vec radial=Unit(orbitRadial);double tangentSquare=LengthSquared(Vec.Cross(orbitPlane,radial));Need(!double.IsInfinity(tangentSquare)&&tangentSquare>1e-24,"Circular placement plane is degenerate.");
            using(var read=host.World.AcquireRead())
            {
                Need(read.RegionCount==1,"Orbital placement requires one region.");for(int p=0;p<read.Definition.ProviderStateCount;p++)Need(read.GetProviderState(p)==0,"Orbital zero-spin fixture requires empty provider state.");
                double extent=flight.Context.VesselBoundingRadius;for(int b=0;b<read.BodyCount;b++)extent=Math.Max(extent,Math.Sqrt(LengthSquared(read.GetBody(b).LocalPosition))+flight.Context.VesselBoundingRadius);
                extent+=Math.Sqrt(LengthSquared(read.GetRegion(0).Properties.CenterOfMass));double centerRadius=Math.Sqrt(LengthSquared(radial*radius));
                Need(!double.IsInfinity(centerRadius)&&centerRadius>flight.Context.Radius+extent,"Orbital placement intersects the spherical surface guard.");
            }
        }
        if(timeline!=null&&(args[0]=="coast"||args[0]=="node"||args[0]=="execute-node"||args[0]=="split"||args[0]=="join"||args[0]=="orbit"||args[0]=="atmo-flight"))
        {
            RequireColdTimeline();var prior=host.World.Stamp;using(var read=host.World.AcquireRead())if(read.ThermalPendingDuration>0)host.Systems.FlushThermal(prior);
            if(!prior.Matches(host.World.Stamp))timeline.Synchronize(prior,WallNow(),"flush thermal before physical event");
        }
        var beforeMutation=host.World.Stamp;
        if(args[0]=="step"&&args.Length==2)Advance(int.Parse(args[1],CultureInfo.InvariantCulture));
        else if(args[0]=="timeline"&&args.Length==2&&args[1]=="drain")
        {
            Need(timeline!=null&&receipt.paused,"Debt drain requires paused world flight.");
            for(int i=0;i<8&&timeline.DebtSeconds>0;i++){var progress=timeline.Step(Math.Min(timeline.MaximumLocalStepSeconds,timeline.DebtSeconds));AcceptProgress(progress);Need(progress.Fault==null&&progress.LocalCommits==1,progress.Fault??"Debt is blocked at an event deadline.");}
        }
        else if(args[0]=="pause"&&args.Length==2){Need(args[1]=="on"||args[1]=="off","Pause expects on or off.");SetPause(args[1]=="on");}
        else if(args[0]=="input"&&args.Length==8)
        {if(timeline!=null&&pilot.mode!="manual")RequireColdTimeline();var next=new OwnedRuntimeControls {Throttle=Number(args[1]),Attitude=new Vec(Number(args[2]),Number(args[3]),Number(args[4])),Translation=new Vec(Number(args[5]),Number(args[6]),Number(args[7])),EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};next.Validate();controls=next;if(executingContext!=null){executingContext.SetLiveControls(next);configuredControls=next;}pilot.mode="manual";keyboardControlActive=false;keyboardSent=false;}
        else if(args[0]=="orbit"&&args.Length==2)
        {
            flight.PlaceCircular(host.World.Stamp,orbitAltitude,orbitRadial,orbitPlane);
            var motions=OwnedFlightManeuverPlanCopy();motions[0].AngularMomentum=new Vec();host.World.SetRegionMotions(host.World.Stamp,motions,host.World.Stamp.Time,flight.FrameId);
            controls=new OwnedRuntimeControls();pilot.mode="manual";events.Add("Engineering treatment: Sunward circular placement, captured craft/mu/radius; chassis spin zeroed, provider state required zero.");
        }
        else if(args[0]=="atmo-flight"&&args.Length==4)
        {
            Need(flight!=null,"World flight required.");Vec radial=flight.Context.SunDirection,tangent=Unit(Vec.Cross(OrbitNormal(),radial));
            flight.Place(host.World.Stamp,radial*(flight.Context.Radius+Number(args[1])),radial*Number(args[2])+tangent*Number(args[3]));
            controls=new OwnedRuntimeControls();pilot.mode="manual";events.Add("Engineering treatment: declared altitude/radial/tangential atmospheric state; retained craft/resources/spin.");
        }
        else if(args[0]=="coast"&&args.Length==3&&args[2]=="freeze-passive")
        {Need(flight!=null&&pilot.mode=="manual"&&host.SupportsPassiveKeplerCoast,"Manual world flight with admitted passive Kepler schedule required.");OwnedFlightTimelineProgress progress;ConfigureTimeline(WallNow(),true);try{progress=timeline.CoastTo(flight.UniversalTime+Number(args[1]));}finally{timelineConfigured=false;ConfigureTimeline(WallNow());}AcceptProgress(progress);Need(progress.CoastCommits==1,progress.CoastRejection??progress.Fault??"Coast rejected.");}
        else if(args[0]=="forecast"&&args.Length==3)
        {
            Need(flight!=null&&pilot.mode=="manual"&&(args[2]=="local"||args[2]=="coast"),"Manual world prediction mode required.");long id;using(var read=host.World.AcquireRead())id=read.GetRegion(0).Id;
            var physicalPlan=timeline==null?plan:new OwnedRuntimeStepPlan(timeline.MaximumLocalStepSeconds,1);
            prediction=OwnedFlightPrediction.Predict(flight,host,imported,controls,physicalPlan,id,Number(args[1]),args[2]=="coast"?OwnedFlightPredictionMode.VacuumCoast:OwnedFlightPredictionMode.LocalPhysical,args[2]=="coast",maneuver);
            Need(prediction.Status=="completed"&&prediction.SourceUnchanged,prediction.Failure??prediction.Status);
        }
        else if(args[0]=="node"&&args.Length==5)
        {Need(flight!=null,"World flight required.");long id;using(var read=host.World.AcquireRead())id=read.GetRegion(0).Id;maneuver=new OwnedFlightManeuverPlan("live-node/"+receipt.lastRequest,flight,host,id,flight.UniversalTime+Number(args[1]),new Vec(Number(args[2]),Number(args[3]),Number(args[4])));}
        else if(args[0]=="execute-node"&&args.Length==2&&args[1]=="freeze-passive")
        {Need(flight!=null&&maneuver!=null&&pilot.mode=="manual","Manual world maneuver required.");maneuver.ExecuteCoastManeuver(flight,host,controls,true);maneuver=null;}
        else if(args[0]=="clear-node"&&args.Length==1){Need(timeline==null||receipt.paused,"Clear node requires pause.");maneuver=null;if(timeline!=null){timeline.CompleteEvent(beforeMutation,WallNow(),"clear-node",double.PositiveInfinity);timelineConfigured=false;}}
        else if(args[0]=="rebase"&&args.Length==4){Need(flight!=null,"World flight required.");flight.Rebase(host.World.Stamp,new Vec(Number(args[1]),Number(args[2]),Number(args[3])));}
        else if(args[0]=="pilot"&&args.Length==2)
        {
            Need(flight!=null&&(args[1]=="manual"||args[1]=="attitude"||args[1]=="velocity"),"Unknown world pilot mode.");using(var read=host.World.AcquireRead())
            {var state=read.GetRegion(0);pilot=new OwnedFlightPilotData {mode=args[1],regionId=state.Id,targetOrientation=A(state.Motion.Orientation),targetVelocity=A(state.Motion.Velocity)};}
        }
        else if(args[0]=="atmosphere"&&args.Length==3)
        {environment=new OwnedRuntimeEnvironment("explicit-atmosphere-research",environment.Revision+1,host.World.Stamp.Time,Number(args[1]),Number(args[2]),new Vec(),new Vec(),5);}
        else if(args[0]=="vacuum"&&args.Length==1)environment=new OwnedRuntimeEnvironment("explicit-vacuum-research",environment.Revision+1,host.World.Stamp.Time,0,300,new Vec(),new Vec());
        else if(args[0]=="wind"&&args.Length==4)environment=new OwnedRuntimeEnvironment("explicit-flow-research",environment.Revision+1,host.World.Stamp.Time,environment.Density,environment.Temperature,new Vec(Number(args[1]),Number(args[2]),Number(args[3])),environment.Gravity,environment.ConvectiveCoefficient,environment.RadiationTemperature);
        else if(args[0]=="warp"&&args.Length==2)plan=new OwnedRuntimeStepPlan(.02,Number(args[1]));
        else if(args[0]=="thermal"&&args.Length==2)
        {RequireColdTimeline();Need(args[1]=="flush"||args[1]=="independent"||args[1]=="reference","Thermal expects flush, independent or reference.");host.Systems.FlushThermal(host.World.Stamp);if(args[1]!="flush")host.Systems.SetThermalPolicy(host.World.Stamp,args[1]=="independent"?new OwnedRuntimeThermalPolicy(true):OwnedRuntimeThermalPolicy.Reference);}
        else if(args[0]=="split"&&args.Length==2){host.World.Split(host.World.Stamp,long.Parse(args[1],CultureInfo.InvariantCulture));}
        else if(args[0]=="join"&&args.Length==4){host.World.Dock(host.World.Stamp,long.Parse(args[1],CultureInfo.InvariantCulture),long.Parse(args[2],CultureInfo.InvariantCulture),long.Parse(args[3],CultureInfo.InvariantCulture));}
        else if(args[0]=="save"&&args.Length==2)
        {Need(!File.Exists(args[1])&&Path.IsPathRooted(args[1]),"Fresh absolute checkpoint required.");byte[] bytes=flight==null?OwnedRuntimeCheckpointBinary.Encode(host.CaptureCheckpoint(imported,controls,environment,plan)):OwnedRuntimeCheckpointBinary.EncodeFlight(OwnedFlightCheckpoint.Capture(host,imported,flight,controls,plan,pilot,visualSha,maneuver));using(var file=new FileStream(args[1],FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(bytes,0,bytes.Length);}
        else if(args[0]=="restore"&&args.Length==2)
        {
            Need(new FileInfo(args[1]).Length<=64L*1024*1024+256,"Checkpoint size bound exceeded.");byte[] bytes=File.ReadAllBytes(args[1]);
            if(flight!=null)
            {
                var data=OwnedRuntimeCheckpointBinary.DecodeFlight(bytes);var next=OwnedFlightCheckpoint.Restore(host,imported,data,receipt.nativeSha,receipt.partSpecsSha,receipt.worldSha,visualSha);
                host=next.Host;imported=next.Imported;flight=next.Session;controls=next.Controls;plan=next.Plan;pilot=next.Pilot;prediction=null;maneuver=next.Maneuver;
            }
            else{var data=OwnedRuntimeCheckpointBinary.Decode(bytes);OwnedRuntimeRestoredState restored;var nextHost=host.RestoreCheckpoint(data,receipt.nativeSha,receipt.partSpecsSha,out restored);
                imported.World=restored.World;imported.Systems=restored.Systems;imported.Source=restored.Source;host=nextHost;controls=restored.Controls;environment=restored.Environment;plan=restored.Plan;}
            if(worker==null)ResetTimeline();
        }
        else if(args[0]=="reload"&&args.Length==3)
        {
            Need(timeline==null||receipt.paused&&timeline.Paused,"Code reload requires pause.");Need(timeline==null||configuredDeadline>flight.UniversalTime,"Resolve the reached event barrier before code reload.");
            Need(reloadCount<8&&Path.IsPathRooted(args[1])&&File.Exists(args[1]),"Owned code reload outside its bound.");
            Need(new FileInfo(args[1]).Length<=16*1024*1024,"Reload image exceeds16MiB.");byte[] bytes=File.ReadAllBytes(args[1]);Need(bytes.Length<=16*1024*1024,"Reload image exceeds16MiB.");
            var assembly=Assembly.Load(bytes);var type=assembly.GetType(args[2],true);
            Need(typeof(IOwnedRuntimeSchedule).IsAssignableFrom(type),"Reload type does not implement the current ABI.");
            var next=(IOwnedRuntimeSchedule)Activator.CreateInstance(type);host.SwapSchedule(next,Hash(bytes),host.World.Stamp);reloadCount++;
        }
        else throw new ArgumentException("Unknown owned runtime command.");
        if(timeline!=null)
        {
            if(worker!=null&&(args[0]=="restore"||args[0]=="reload"||!beforeMutation.Matches(host.World.Stamp)&&args[0]!="step"&&args[0]!="coast"&&args[0]!="timeline"))return;
            if(args[0]=="reload"){timeline.CompleteEvent(beforeMutation,WallNow(),"reload",configuredDeadline);receipt.error=null;}
            else if(args[0]!="restore"&&args[0]!="step"&&args[0]!="coast"&&args[0]!="timeline"&&!beforeMutation.Matches(host.World.Stamp))timeline.Synchronize(beforeMutation,WallNow(),args[0]);
            var desiredResolver=pilot.mode=="manual"?null:pilotResolver;if(timeline.ControlResolver!=desiredResolver)timeline.ControlResolver=desiredResolver;
            if(args[0]=="coast")timelineConfigured=false;
            if(timeline.Fault==null)ConfigureTimeline(WallNow());CopyProgress(receipt.timeline,timeline.ReadProgress());
        }
    }
    Vec OrbitNormal(){Vec spin=flight.Context.AngularVelocity;return Math.Sqrt(spin.X*spin.X+spin.Y*spin.Y+spin.Z*spin.Z)>0?Unit(spin):Unit(Vec.Cross(flight.Context.SunDirection,new Vec(0,1,0)));}
    OwnedRuntimeMotion[] OwnedFlightManeuverPlanCopy(){using(var read=host.World.AcquireRead()){var rows=new OwnedRuntimeMotion[read.RegionCount];for(int i=0;i<rows.Length;i++)rows[i]=read.GetRegion(i).Motion;return rows;}}
    void OnGUI()
    {
        if(stopping)return;
        if(!presentationSuspended&&overlay!=null&&presentationFrame!=null)overlay.Draw(camera,cameraBodyOrigin,presentationFrame,prediction,scientificGrid,gridParent);
        GUI.Box(new Rect(10,10,425,200),worker==null?"Continuum owned runtime":"Continuum independent flight worker");
        GUI.Label(new Rect(22,35,400,28),host==null?receipt.status:receipt.parts+" parts / "+receipt.bodies+" bodies / "+receipt.regions+" rigid regions");
        GUI.Label(new Rect(22,60,400,25),"Space: pause   W/S A/D Q/E: attitude   I/J/K/L H/N: RCS");
        GUI.Label(new Rect(22,85,400,25),"O: orthographic   debug socket: "+receipt.port+"   "+(receipt.paused?"paused":"running"));
        if(host!=null)GUI.Label(new Rect(22,110,400,25),"t="+(presentationFrame==null?receipt.time:presentationFrame.Read.Stamp.Time).ToString("F2")+"  "+receipt.implementation);
        if(worker!=null)GUI.Label(new Rect(22,160,400,25),"owed="+receipt.timeline.DebtSeconds.ToString("F3")+"s  warp="+receipt.timeline.Warp.ToString("F1")+"  "+receipt.timeline.Status);
        if(maneuver!=null)GUI.Label(new Rect(22,185,400,25),"Node: paused immediate coast only; execute or clear.");
        if(presentationFrame!=null){var state=presentationFrame.Read.GetRegion(0);Vec center=presentationFrame.Origin+state.Motion.RootPosition+state.Motion.Orientation.Rotate(state.Properties.CenterOfMass);GUI.Label(new Rect(22,135,400,25),"altitude="+(presentationFrame.Context.Altitude(center)/1000).ToString("F2")+"km  "+presentationFrame.Mode+"  "+receipt.pilotMode);}
    }
    void Serve()
    {
        try
        {
            while(!stopping)
            {
                using(var client=listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout=5000;client.SendTimeout=5000;
                    using(var stream=client.GetStream())using(var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true))using(var writer=new StreamWriter(stream,new UTF8Encoding(false),4096,true){AutoFlush=true})
                    {
                        var bounded=new StringBuilder();int character;
                        while((character=reader.Read())!=-1&&character!='\n'){if(bounded.Length>=8192)throw new InvalidDataException("Debug command exceeds bound.");bounded.Append((char)character);}
                        if(character==-1&&bounded.Length==0)continue;string line=bounded.ToString().TrimEnd('\r');
                        var request=new Request {Id=Interlocked.Increment(ref requestId),Text=line};requests.Enqueue(request);
                        int waited=0;while(Volatile.Read(ref request.State)!=2&&waited++<2000)Thread.Sleep(5);
                        int state=Volatile.Read(ref request.State);
                        if(state==2)writer.WriteLine(request.Reply);
                        else if(Interlocked.CompareExchange(ref request.State,3,0)==0)writer.WriteLine("{\"status\":\"cancelled-before-execution\",\"request\":"+request.Id+"}");
                        else if(Volatile.Read(ref request.State)==2)writer.WriteLine(request.Reply);
                        else writer.WriteLine("{\"status\":\"started\",\"request\":"+request.Id+",\"message\":\"Execution continues; inspect status before another command.\"}");
                    }
                }
            }
        }
        catch(Exception error){if(!stopping)Debug.LogException(error);}
    }
    void Quit()
    {
        StartCoroutine(Retire());
    }
    IEnumerator Retire()
    {
        stopping=true;if(listener!=null)listener.Stop();
        ReleaseFrame();if(worker!=null)
        {
            try
            {
                var pause=worker.SetPause(true);Need(pause.Wait(30000),"Shutdown pause remains pending.");if(pause.Error!=null)throw pause.Error;
                receipt.paused=pause.Result.Paused;Need(receipt.paused,"Shutdown worker did not acknowledge pause.");UpdateReceipt();ReleaseFrame();
            }
            catch(Exception error){receipt.error="Worker shutdown pause: "+error;}
            worker.Dispose();CopyWorker(receipt.worker,worker.ReadStatus());receipt.workerStatus=receipt.worker.Status;
            using(var final=worker.AcquirePresentation())if(final!=null)CollectFinalFrame(final);
            workerRetired=true;worker=null;timeline=null;
        }
        if(heldFlightImage!=null){heldFlightImage.Release();Destroy(heldFlightImage);heldFlightImage=null;}
        if(presentation!=null)presentation.Dispose();
        if(overlay!=null)overlay.Dispose();
        if(visible!=null)visible.ReleaseResources();
        if(scene.IsValid()&&scene.isLoaded){var operation=SceneManager.UnloadSceneAsync(scene);if(operation!=null)yield return operation;}
        if(server!=null)server.Join(1000);
        receipt.emptySceneReleased=!scene.IsValid()||!scene.isLoaded;UpdateReceipt();SaveReceipt();Application.Quit(receipt.emptySceneReleased?0:1);
    }
    void CollectFinalFrame(OwnedFlightPresentationLease final)
    {
        var read=final.Read;CopyProgress(receipt.timeline,final.Progress);receipt.time=read.Stamp.Time;receipt.epoch=read.Stamp.Epoch;receipt.stateRevision=read.Stamp.StateRevision;receipt.topologyRevision=read.Stamp.TopologyRevision;
        receipt.parts=read.PartCount;receipt.bodies=read.BodyCount;receipt.resources=read.ResourceCount;receipt.regions=read.RegionCount;receipt.thermalIntegratedTime=read.ThermalIntegratedTime;receipt.thermalPendingDuration=read.ThermalPendingDuration;
        receipt.states=new Region[read.RegionCount];for(int r=0;r<read.RegionCount;r++){var state=read.GetRegion(r);receipt.states[r]=new Region{id=state.Id,mass=state.Properties.Mass,position=A(state.Motion.RootPosition),rotation=A(state.Motion.Orientation),velocity=A(state.Motion.Velocity),momentum=A(state.Motion.AngularMomentum)};}
        var sample=final.Descriptor.Sample(read,0);receipt.density=sample.Density;receipt.ambient=sample.Temperature;receipt.universalTime=final.UniversalTime;receipt.altitude=final.Context.Altitude(final.Descriptor.CenterPosition(read,0));receipt.flightMode=final.Mode;
    }
    void OnDestroy(){stopping=true;if(listener!=null)listener.Stop();ReleaseFrame();if(worker!=null)worker.Dispose();if(heldFlightImage!=null){heldFlightImage.Release();Destroy(heldFlightImage);}if(presentation!=null)presentation.Dispose();if(overlay!=null)overlay.Dispose();}
}
public sealed class OwnedFlightImageEffect : MonoBehaviour
{
    public OwnedRuntimeWorkbench Owner;
    void OnRenderImage(RenderTexture source,RenderTexture destination){if(Owner!=null)Owner.RenderFlight(source,destination);else Graphics.Blit(source,destination);}
}
