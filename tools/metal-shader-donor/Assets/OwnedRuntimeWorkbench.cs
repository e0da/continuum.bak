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
    [Serializable] sealed class Region
    {public long id;public double mass;public double[] position,rotation,velocity,momentum;}
    [Serializable] sealed class Receipt
    {
        public string schema="continuum-owned-runtime-workbench/v1",status="starting",unity,graphics,sourceRevision,nativeSha,partSpecsSha,implementation,lastCommand,error;
        public string scope="Owned double rigid regions, retained part/resource/provider banks and scheduled SI control/aero/thermal dynamics; captured collision geometry for visible presentation. Original KSP module/terrain/contact/UI behavior is not claimed.";
        public int processId,port,parts,bodies,resources,regions,actuators,surfaces,keyboardFrames;
        public long epoch,stateRevision,topologyRevision,codeGeneration,steps,presentationRootWrites,lastRequest;
        public double time,delta=.02,density,ambient=300,maximumBodyPoseError,lastAdvanceMilliseconds,totalAdvanceMilliseconds;
        public string aeroSha,resourcePolicy,scheduleSha;
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
    int reloadCount;
    bool keyboardControlActive;

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
            imported=OwnedRuntimeImport.Create(native,geometry,specs,Hash(manifestPath),Hash(specsPath),allowAcquiredSurplus:true);
            string aeroPath=Path.Combine(Path.GetDirectoryName(manifestPath),"owned-aero.json");
            if(File.Exists(aeroPath))OwnedRuntimeAcquiredAero.Attach(imported,OwnedRuntimeAcquiredAero.Build(imported,JsonUtility.FromJson<OwnedRuntimeCapturedAero>(File.ReadAllText(aeroPath))));
            receipt.aeroSha=File.Exists(aeroPath)?Hash(aeroPath):null;receipt.resourcePolicy=imported.Source.resourcePolicy;
            host=new OwnedRuntimeHost(imported.World,imported.Systems);
            controls=new OwnedRuntimeControls {EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};
            environment=new OwnedRuntimeEnvironment("captured-craft/vacuum-research",0,host.World.Stamp.Time,0,300,new Vec(),new Vec());
            scene=SceneManager.CreateScene("Owned runtime "+Guid.NewGuid().ToString("N"),new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            visible=NativeAssemblySceneImport.Load(manifestPath,scene);visible.Owner.Body.isKinematic=true;visible.Owner.Body.detectCollisions=false;
            anchors=new Transform[visible.Owner.Members.Count];anchorSlots=new int[anchors.Length];
            var slots=new Dictionary<int,int>();foreach(var binding in imported.BodyBindings)slots.Add(binding.sourceBodyId,binding.bodySlot);
            for(int i=0;i<anchors.Length;i++){anchors[i]=visible.Owner.Members[i].Anchor;anchorSlots[i]=slots[visible.Receipt.sourceBodyIds[i]];foreach(var collider in visible.Owner.Members[i].Colliders)collider.enabled=false;}
            camera=new GameObject("Owned runtime inspection camera").AddComponent<Camera>();SceneManager.MoveGameObjectToScene(camera.gameObject,scene);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.055f,.085f);camera.fieldOfView=50;camera.nearClipPlane=.05f;camera.farClipPlane=10000;camera.allowMSAA=false;
            QualitySettings.vSyncCount=0;Application.targetFrameRate=60;Time.timeScale=1;
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start(4);receipt.port=((IPEndPoint)listener.LocalEndpoint).Port;
            receipt.processId=System.Diagnostics.Process.GetCurrentProcess().Id;receipt.unity=Application.unityVersion;receipt.graphics=SystemInfo.graphicsDeviceType.ToString();
            receipt.sourceRevision=Environment.GetEnvironmentVariable("CONTINUUM_SOURCE_REVISION");receipt.nativeSha=Hash(manifestPath);receipt.partSpecsSha=Hash(specsPath);receipt.status="ready";
            Present(true);UpdateReceipt();SaveReceipt();server=new Thread(Serve){IsBackground=true};server.Start();
        }
        catch(Exception error){receipt.status="failed";receipt.error=error.ToString();if(output!=null&&Directory.Exists(output))SaveReceipt();Debug.LogException(error);}
    }
    void Update()
    {
        Request request;int handled=0;
        while(handled++<16&&requests.TryDequeue(out request))
        {
            if(Interlocked.CompareExchange(ref request.State,1,0)!=0)continue;
            receipt.lastRequest=request.Id;receipt.lastCommand=request.Text;
            try {request.Reply=Execute(request.Text);UpdateReceipt();SaveReceipt();}
            catch(Exception error){events.Add("rejected: "+request.Text+" :: "+error.Message);request.Reply="{\"status\":\"rejected\",\"request\":"+request.Id+",\"message\":\"Command failed; inspect status for committed progress.\"}";receipt.error=error.ToString();UpdateReceipt();SaveReceipt();}
            finally {Interlocked.Exchange(ref request.State,2);}
        }
        if(host==null)return;
        if(Input.GetKeyDown(KeyCode.Space))receipt.paused=!receipt.paused;
        if(Input.GetKeyDown(KeyCode.O)){receipt.ortho=!receipt.ortho;camera.orthographic=receipt.ortho;}
        if(!receipt.paused)
        {
            if(Input.GetKey(KeyCode.W)||Input.GetKey(KeyCode.S)||Input.GetKey(KeyCode.A)||Input.GetKey(KeyCode.D)||Input.GetKey(KeyCode.Q)||Input.GetKey(KeyCode.E)
                ||Input.GetKey(KeyCode.I)||Input.GetKey(KeyCode.J)||Input.GetKey(KeyCode.K)||Input.GetKey(KeyCode.L)||Input.GetKey(KeyCode.H)||Input.GetKey(KeyCode.N))keyboardControlActive=true;
            if(keyboardControlActive)
            {
                controls.Attitude=new Vec(Axis(KeyCode.W,KeyCode.S),Axis(KeyCode.A,KeyCode.D),Axis(KeyCode.Q,KeyCode.E));
                controls.Translation=new Vec(Axis(KeyCode.L,KeyCode.J),Axis(KeyCode.I,KeyCode.K),Axis(KeyCode.H,KeyCode.N));receipt.keyboardFrames++;
            }
            accumulator+=Math.Min(Time.unscaledDeltaTime,.1);
            int count=0;
            while(accumulator>=plan.FixedDelta&&count++<8)
            {
                try {Advance(1);}catch(Exception error){receipt.paused=true;receipt.error=error.ToString();SaveReceipt();break;}
                accumulator-=plan.FixedDelta;
            }
        }
        Present(false);FollowCamera();
    }
    static double Axis(KeyCode positive,KeyCode negative){return (Input.GetKey(positive)?1:0)-(Input.GetKey(negative)?1:0);}
    void Advance(int count)
    {
        Need(count>0&&count<=10000,"Step request outside its bound.");
        for(int i=0;i<count;i++)
        {
            var input=new OwnedRuntimeInput(controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z);
            long started=System.Diagnostics.Stopwatch.GetTimestamp();host.Advance(plan.Delta,input,environment,controls);
            receipt.lastAdvanceMilliseconds=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
            receipt.totalAdvanceMilliseconds+=receipt.lastAdvanceMilliseconds;receipt.steps++;
        }
        Present(false);
    }
    void Present(bool force)
    {
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
        using(var view=host.World.AcquireRead())
        {
            Vec center=view.GetRegion(0).Motion.RootPosition;Vector3 target=V(center);
            Vector3 lightView=new Vector3(.3f,.7f,-.5f).normalized;
            camera.transform.SetPositionAndRotation(target+lightView*cameraDistance,Quaternion.LookRotation(-lightView,Vector3.up));
            camera.orthographicSize=cameraDistance*.35f;
        }
    }
    void UpdateReceipt()
    {
        if(host==null)return;
        using(var view=host.World.AcquireRead())
        {
            receipt.parts=view.PartCount;receipt.bodies=view.BodyCount;receipt.resources=view.ResourceCount;receipt.regions=view.RegionCount;
            receipt.actuators=host.Systems.ActuatorCount;receipt.surfaces=host.Systems.SurfaceCount;
            receipt.time=view.Stamp.Time;receipt.epoch=view.Stamp.Epoch;receipt.stateRevision=view.Stamp.StateRevision;receipt.topologyRevision=view.Stamp.TopologyRevision;
            receipt.codeGeneration=host.CodeGeneration;receipt.implementation=host.ScheduleImplementation;receipt.scheduleSha=host.ScheduleSourceSha256;receipt.delta=plan.Delta;receipt.density=environment.Density;receipt.ambient=environment.Temperature;
            receipt.states=new Region[view.RegionCount];
            for(int r=0;r<view.RegionCount;r++){var state=view.GetRegion(r);receipt.states[r]=new Region {id=state.Id,mass=state.Properties.Mass,position=A(state.Motion.RootPosition),rotation=A(state.Motion.Orientation),velocity=A(state.Motion.Velocity),momentum=A(state.Motion.AngularMomentum)};}
        }
        receipt.events=events.ToArray();
    }
    void SaveReceipt(){File.WriteAllText(Path.Combine(output,"status.json"),JsonUtility.ToJson(receipt,true));}
    string Execute(string text)
    {
        string[] args=text.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);Need(args.Length>0,"Empty command.");
        if(args[0]=="status"){UpdateReceipt();return JsonUtility.ToJson(receipt);}
        if(args[0]=="quit"){stopping=true;Invoke("Quit",.15f);return "{\"status\":\"quitting\"}";}
        Need(host!=null&&receipt.status=="ready","Runtime is not ready.");
        if(args[0]=="step"&&args.Length==2)Advance(int.Parse(args[1],CultureInfo.InvariantCulture));
        else if(args[0]=="pause"&&args.Length==2)receipt.paused=args[1]!="off";
        else if(args[0]=="input"&&args.Length==8)
        {var next=new OwnedRuntimeControls {Throttle=Number(args[1]),Attitude=new Vec(Number(args[2]),Number(args[3]),Number(args[4])),Translation=new Vec(Number(args[5]),Number(args[6]),Number(args[7])),EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true};next.Validate();controls=next;keyboardControlActive=false;}
        else if(args[0]=="atmosphere"&&args.Length==3)
        {environment=new OwnedRuntimeEnvironment("explicit-atmosphere-research",environment.Revision+1,host.World.Stamp.Time,Number(args[1]),Number(args[2]),new Vec(),new Vec(),5);}
        else if(args[0]=="vacuum"&&args.Length==1)environment=new OwnedRuntimeEnvironment("explicit-vacuum-research",environment.Revision+1,host.World.Stamp.Time,0,300,new Vec(),new Vec());
        else if(args[0]=="wind"&&args.Length==4)environment=new OwnedRuntimeEnvironment("explicit-flow-research",environment.Revision+1,host.World.Stamp.Time,environment.Density,environment.Temperature,new Vec(Number(args[1]),Number(args[2]),Number(args[3])),environment.Gravity,environment.ConvectiveCoefficient,environment.RadiationTemperature);
        else if(args[0]=="warp"&&args.Length==2)plan=new OwnedRuntimeStepPlan(.02,Number(args[1]));
        else if(args[0]=="split"&&args.Length==2){host.World.Split(host.World.Stamp,long.Parse(args[1],CultureInfo.InvariantCulture));Present(true);}
        else if(args[0]=="join"&&args.Length==4){host.World.Dock(host.World.Stamp,long.Parse(args[1],CultureInfo.InvariantCulture),long.Parse(args[2],CultureInfo.InvariantCulture),long.Parse(args[3],CultureInfo.InvariantCulture));Present(true);}
        else if(args[0]=="save"&&args.Length==2)
        {Need(!File.Exists(args[1])&&Path.IsPathRooted(args[1]),"Fresh absolute checkpoint required.");byte[] bytes=OwnedRuntimeCheckpointBinary.Encode(host.CaptureCheckpoint(imported,controls,environment,plan));using(var file=new FileStream(args[1],FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(bytes,0,bytes.Length);}
        else if(args[0]=="restore"&&args.Length==2)
        {
            Need(new FileInfo(args[1]).Length<=64L*1024*1024+256,"Checkpoint size bound exceeded.");var data=OwnedRuntimeCheckpointBinary.Decode(File.ReadAllBytes(args[1]));
            OwnedRuntimeRestoredState restored;var nextHost=host.RestoreCheckpoint(data,receipt.nativeSha,receipt.partSpecsSha,out restored);
            imported.World=restored.World;imported.Systems=restored.Systems;imported.Source=restored.Source;
            host=nextHost;controls=restored.Controls;environment=restored.Environment;plan=restored.Plan;Present(true);
        }
        else if(args[0]=="reload"&&args.Length==3)
        {
            Need(reloadCount<8&&Path.IsPathRooted(args[1])&&File.Exists(args[1]),"Owned code reload outside its bound.");
            Need(new FileInfo(args[1]).Length<=16*1024*1024,"Reload image exceeds16MiB.");byte[] bytes=File.ReadAllBytes(args[1]);Need(bytes.Length<=16*1024*1024,"Reload image exceeds16MiB.");
            var assembly=Assembly.Load(bytes);var type=assembly.GetType(args[2],true);
            Need(typeof(IOwnedRuntimeSchedule).IsAssignableFrom(type),"Reload type does not implement the current ABI.");
            var next=(IOwnedRuntimeSchedule)Activator.CreateInstance(type);host.SwapSchedule(next,Hash(bytes),host.World.Stamp);reloadCount++;
        }
        else if(args[0]=="camera"&&args.Length==2){double distance=Number(args[1]);Need(distance>=2&&distance<=5000,"Camera range outside2..5000m.");cameraDistance=(float)distance;}
        else if(args[0]=="ortho"&&args.Length==2){receipt.ortho=args[1]=="on";camera.orthographic=receipt.ortho;}
        else if(args[0]=="capture"&&args.Length==2)
        {Need(Path.IsPathRooted(args[1])&&!File.Exists(args[1]),"Fresh absolute screenshot required.");FollowCamera();ScreenCapture.CaptureScreenshot(args[1]);}
        else if(args[0]=="poses"&&args.Length==1)
        {
            double maximum=0;using(var view=host.World.AcquireRead())for(int i=0;i<anchors.Length;i++)
            {int body=anchorSlots[i],region=view.Topology.RegionOfBody(body);var state=view.GetRegion(region);var member=view.GetBody(body);Vec expected=state.Motion.RootPosition+state.Motion.Orientation.Rotate(member.LocalPosition);Vector3 actual=anchors[i].position;maximum=Math.Max(maximum,Math.Sqrt(Math.Pow(actual.x-expected.X,2)+Math.Pow(actual.y-expected.Y,2)+Math.Pow(actual.z-expected.Z,2)));}
            receipt.maximumBodyPoseError=maximum;
        }
        else throw new ArgumentException("Unknown owned runtime command.");
        events.Add(text);if(events.Count>256)events.RemoveAt(0);UpdateReceipt();return JsonUtility.ToJson(receipt);
    }
    void OnGUI()
    {
        GUI.Box(new Rect(10,10,425,145),"Continuum owned runtime");
        GUI.Label(new Rect(22,35,400,28),host==null?receipt.status:host.World.PartCount+" parts / "+host.World.BodyCount+" bodies / "+host.World.RegionCount+" rigid regions");
        GUI.Label(new Rect(22,60,400,25),"Space: pause   W/S A/D Q/E: attitude   I/J/K/L H/N: RCS");
        GUI.Label(new Rect(22,85,400,25),"O: orthographic   debug socket: "+receipt.port+"   "+(receipt.paused?"paused":"running"));
        if(host!=null)GUI.Label(new Rect(22,110,400,25),"t="+host.World.Stamp.Time.ToString("F2")+"  step="+plan.Delta.ToString("F3")+"  "+host.ScheduleImplementation);
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
        if(visible!=null)visible.ReleaseResources();
        if(scene.IsValid()&&scene.isLoaded){var operation=SceneManager.UnloadSceneAsync(scene);if(operation!=null)yield return operation;}
        if(server!=null)server.Join(1000);
        receipt.emptySceneReleased=!scene.IsValid()||!scene.isLoaded;UpdateReceipt();SaveReceipt();Application.Quit(receipt.emptySceneReleased?0:1);
    }
    void OnDestroy(){stopping=true;if(listener!=null)listener.Stop();}
}
