using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using KspContinuum;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RigidAssemblyWorkbench : MonoBehaviour
{
    [Serializable] public sealed class BodyState
    {
        public int id,generation; public double mass;
        public double[] position,rotation,com,localCom,inertia,inertiaRotation,velocity,angularVelocity;
        public int[] members,colliders;
    }
    [Serializable] public sealed class MemberState
    { public int id,collider,body; public double mass; public double[] position,rotation,localPosition,localRotation,localCenterOfMass,principalInertia,principalRotation; }
    [Serializable] public sealed class State
    { public string operation; public int step,dynamicBodies; public double[] momentum,angularMomentum; public BodyState[] bodies; public MemberState[] members; }
    [Serializable] public sealed class Proof
    {
        public string schema="continuum-rigid-owner/v1",status="ready",qualification="Private native synthetic compound-body laboratory; live KSP graph takeover remains open.";
        public string unity,graphics,architecture,sourceRevision; public int processId,port;
        public int lastMutationRequestId;public string lastMutationCommand;
        public double timestep=.02,immediateMomentumBudget=.0001,impulseAngularBudget=.005,continuedAngularBudget=.02;
        public double impulseLinearError,impulseAngularError,transferLinearError,transferAngularError,splitLinearError,splitAngularError,continuedLinearError,continuedAngularError;
        public double updatedOwnerLinearError,updatedOwnerAngularError,updatedOwnerAngularVelocityError;
        public double[] impulse,impulsePoint;
        public double[] updatedImpulse,updatedImpulsePoint;
        public List<State> states=new List<State>(); public string error;
    }
    sealed class Request { public int Id,State;public string Text,Reply; public volatile bool Done; }
    readonly ConcurrentQueue<Request> requests=new ConcurrentQueue<Request>();
    readonly List<RigidAssemblyOwner> owners=new List<RigidAssemblyOwner>();
    readonly List<Material> materials=new List<Material>();
    readonly Proof proof=new Proof(); Scene physicsScene; PhysicsScene physics; Camera camera;
    TcpListener listener; Thread server; volatile bool stopping;bool sceneReleased; string output; int step,pending,phase,requestId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Bootstrap()
    {
        string path=Environment.GetEnvironmentVariable("CONTINUUM_RIGID_WORKBENCH_OUTPUT"); if(string.IsNullOrEmpty(path)) return;
        new GameObject("Continuum rigid assembly laboratory",typeof(RigidAssemblyWorkbench));
    }
    void Start()
    {
        try
        {
            output=Environment.GetEnvironmentVariable("CONTINUUM_RIGID_WORKBENCH_OUTPUT");
            if(!Path.IsPathRooted(output)||Directory.Exists(output)||File.Exists(output)) throw new ArgumentException("Fresh private absolute output directory required.");
            Directory.CreateDirectory(output);
            proof.unity=Application.unityVersion; proof.graphics=SystemInfo.graphicsDeviceType.ToString();
            proof.architecture="Unity2019 x86_64 player; host translation is external receipt evidence";
            proof.sourceRevision=Environment.GetEnvironmentVariable("CONTINUUM_SOURCE_REVISION"); proof.processId=System.Diagnostics.Process.GetCurrentProcess().Id;
            physicsScene=SceneManager.CreateScene("Rigid assembly isolated "+Guid.NewGuid(),new CreateSceneParameters(LocalPhysicsMode.Physics3D)); physics=physicsScene.GetPhysicsScene();
            BuildFixture(); ConfigureCamera();
            listener=new TcpListener(IPAddress.Loopback,0); listener.Start(4); proof.port=((IPEndPoint)listener.LocalEndpoint).Port;
            Save(); server=new Thread(Serve) {IsBackground=true};server.Start();
        }
        catch(Exception ex) { proof.status="failed";proof.error=ex.ToString();if(output!=null&&Directory.Exists(output))Save();Debug.LogException(ex); }
    }
    void BuildFixture()
    {
        var members=new List<RigidAssemblyOwner.Member>();
        Vector3[] positions={new Vector3(-2,0,0),new Vector3(.1f,.55f,0),new Vector3(2.1f,-.2f,.25f)};
        Vector3[] sizes={new Vector3(1.6f,.8f,.7f),new Vector3(1.0f,1.2f,.6f),new Vector3(1.4f,.7f,.9f)};
        double[] masses={4,7,3}; Color[] colors={new Color(.2f,.8f,1),new Color(1,.7f,.2f),new Color(.8f,.3f,.7f)};
        var rotation=UnityEngine.Quaternion.Euler(12,23,-14); Vector3 rootPosition=new Vector3(0,0,0);
        for(int i=0;i<3;i++)
        {
            var localRotation=UnityEngine.Quaternion.Euler(7*i,-11*i,20-13*i); Vector3 localCOM=new Vector3(.12f,-.06f,.04f);
            var anchor=new GameObject("Authored member "+i); SceneManager.MoveGameObjectToScene(anchor,physicsScene);
            anchor.transform.SetPositionAndRotation(rootPosition+rotation*positions[i],rotation*localRotation);
            var shape=GameObject.CreatePrimitive(PrimitiveType.Cube); shape.name="Member "+i+" retained collider"; shape.transform.SetParent(anchor.transform,false);
            shape.transform.localPosition=localCOM; shape.transform.localRotation=UnityEngine.Quaternion.Euler(0,0,9*i); shape.transform.localScale=sizes[i];
            var material=new Material(Shader.Find("Continuum/FixtureOpaque")); material.color=colors[i];materials.Add(material);shape.GetComponent<Renderer>().sharedMaterial=material;
            Vec inertia=new Box(masses[i],0,sizes[i].x,sizes[i].y,sizes[i].z).Inertia;
            var property=new RigidAssemblyMember(i,masses[i],V(positions[i]),Q(localRotation),V(localCOM),inertia,Q(shape.transform.localRotation));
            members.Add(new RigidAssemblyOwner.Member(property,anchor.transform,shape.GetComponent<Collider>()));
        }
        var owner=new RigidAssemblyOwner(physicsScene,rootPosition,rotation,members,0); owner.Activate(new Vector3(.15f,.02f,-.08f),Vector3.zero); owners.Add(owner);
        Physics.SyncTransforms(); Record("initial");
    }
    void ConfigureCamera()
    {
        var host=new GameObject("Rigid assembly scientific camera");SceneManager.MoveGameObjectToScene(host,physicsScene);camera=host.AddComponent<Camera>();
        camera.transform.position=new Vector3(1,4,-10);camera.transform.LookAt(new Vector3(0,.2f,0));camera.orthographic=true;camera.orthographicSize=3;
        camera.backgroundColor=new Color(.035f,.055f,.09f);camera.clearFlags=CameraClearFlags.SolidColor;camera.nearClipPlane=.1f;camera.farClipPlane=100;
        camera.allowHDR=false;camera.allowMSAA=false;camera.enabled=true;
        var grid=new GameObject("Reference grid");SceneManager.MoveGameObjectToScene(grid,physicsScene);
        var line=grid.AddComponent<LineRenderer>();line.useWorldSpace=true;line.widthMultiplier=.02f;line.loop=false;line.generateLightingData=true;
        var mat=new Material(Shader.Find("Continuum/FixtureOpaque"));mat.color=new Color(.6f,.7f,.8f);materials.Add(mat);line.sharedMaterial=mat;
        var points=new List<Vector3>();for(int x=-5;x<=5;x++){points.Add(new Vector3(x,-1.25f,-2));points.Add(new Vector3(x,-1.25f,2));points.Add(new Vector3(x,-1.25f,-2));}
        for(int z=-2;z<=2;z++){points.Add(new Vector3(-5,-1.25f,z));points.Add(new Vector3(5,-1.25f,z));points.Add(new Vector3(-5,-1.25f,z));}line.positionCount=points.Count;line.SetPositions(points.ToArray());
    }
    void Serve()
    {
        while(!stopping)
        {
            try { using(var client=listener.AcceptTcpClient())
            {
                client.ReceiveTimeout=2000;client.SendTimeout=2000;
                using(var stream=client.GetStream())
                {
                    var bytes=new List<byte>();int ch;while((ch=stream.ReadByte())!=-1&&ch!=10){if(bytes.Count>=4096)throw new IOException("Command limit");bytes.Add((byte)ch);}
                    if(stopping)break;
                    if(Interlocked.Increment(ref pending)>16){Interlocked.Decrement(ref pending);Write(stream,"{\"error\":\"queue full\"}");continue;}
                    var request=new Request {Id=Interlocked.Increment(ref requestId),Text=Encoding.UTF8.GetString(bytes.ToArray()).Trim()};requests.Enqueue(request);
                    var clock=System.Diagnostics.Stopwatch.StartNew();while(!request.Done&&!stopping&&clock.ElapsedMilliseconds<10000)Thread.Sleep(2);
                    if(request.Done)Write(stream,request.Reply);
                    else if(Interlocked.CompareExchange(ref request.State,3,0)==0)Write(stream,"{\"status\":\"cancelled before execution\",\"requestId\":"+request.Id+"}");
                    else Write(stream,"{\"status\":\"pending\",\"requestId\":"+request.Id+",\"instruction\":\"Inspect status; do not retry mutation.\"}");
                }
            }} catch(Exception ex) {if(!stopping)Debug.LogWarning(ex.Message);}
        }
    }
    static void Write(Stream stream,string text){byte[] bytes=Encoding.UTF8.GetBytes(text+"\n");stream.Write(bytes,0,bytes.Length);}
    void Update()
    {
        Request request;while(requests.TryDequeue(out request))
        {
            Interlocked.Decrement(ref pending);
            if(Interlocked.CompareExchange(ref request.State,1,0)!=0)continue;
            try {request.Reply=Execute(request.Text);}catch(Exception ex){proof.status="failed";proof.error=ex.ToString();Save();request.Reply=JsonUtility.ToJson(proof);Debug.LogException(ex);}
            if(request.Text.StartsWith("assembly ",StringComparison.Ordinal)) {proof.lastMutationRequestId=request.Id;proof.lastMutationCommand=request.Text;Save();}
            request.Done=true;
        }
    }
    string Execute(string command)
    {
        if(command=="status")return JsonUtility.ToJson(proof);
        if(command=="quit"){Invoke("Quit",.25f);return "{\"status\":\"quitting\"}";}
        if(command=="captureframe") {CaptureFrame();return "{\"status\":\"captured\"}";}
        if(proof.status=="failed")throw new InvalidOperationException("Failed scenario is immutable; use a fresh process/output.");
        if(command=="assembly impulse")
        {
            RequirePhase(0); State before=Last;Vector3 impulse=new Vector3(.5f,1.2f,-.3f),point=owners[0].Body.transform.TransformPoint(new Vector3(-2.3f,.4f,.1f));
            proof.impulse=A(impulse);proof.impulsePoint=A(point);owners[0].Body.AddForceAtPosition(impulse,point,ForceMode.Impulse);Step(1);State after=Record("off-center impulse");
            proof.impulseLinearError=Error(after.momentum,Add(before.momentum,A(impulse)));
            proof.impulseAngularError=Error(after.angularMomentum,Add(before.angularMomentum,A(Vector3.Cross(point,impulse))));
            Check(proof.impulseLinearError<=proof.immediateMomentumBudget&&proof.impulseAngularError<=proof.impulseAngularBudget,"Off-center impulse momentum");phase=1;
        }
        else if(command=="assembly transfermass")
        {
            RequirePhase(1);State before=Last;owners[0].TransferMass(0,2,.75);Physics.SyncTransforms();State after=Record("closed mass transfer");
            proof.transferLinearError=Error(after.momentum,before.momentum);proof.transferAngularError=Error(after.angularMomentum,before.angularMomentum);
            SameGeometry(before,after);Check(proof.transferLinearError<=proof.immediateMomentumBudget&&proof.transferAngularError<=proof.immediateMomentumBudget,"Mass transfer momentum");phase=2;
            Rigidbody updated=owners[0].Body;Vector3 impulse=new Vector3(-.4f,.3f,.2f),point=updated.transform.TransformPoint(new Vector3(2.4f,-.2f,.3f));
            proof.updatedImpulse=A(impulse);proof.updatedImpulsePoint=A(point);
            Vector3 expectedOmega=updated.angularVelocity+RigidAssemblyOwner.InverseWorldInertia(updated,Vector3.Cross(point-updated.worldCenterOfMass,impulse));
            updated.AddForceAtPosition(impulse,point,ForceMode.Impulse);Step(1);State advanced=Record("updated original owner impulse");
            proof.updatedOwnerLinearError=Error(advanced.momentum,Add(after.momentum,A(impulse)));
            proof.updatedOwnerAngularError=Error(advanced.angularMomentum,Add(after.angularMomentum,A(Vector3.Cross(point,impulse))));
            proof.updatedOwnerAngularVelocityError=Error(A(updated.angularVelocity),A(expectedOmega));
            Check(proof.updatedOwnerLinearError<=proof.immediateMomentumBudget&&proof.updatedOwnerAngularError<=proof.impulseAngularBudget&&proof.updatedOwnerAngularVelocityError<=proof.impulseAngularBudget,"Updated original owner solver inertia");
        }
        else if(command=="assembly split")
        {
            RequirePhase(2);State before=Last;var next=owners[0].Split(physicsScene,id=>id<2);owners.Clear();owners.AddRange(next);Physics.SyncTransforms();State after=Record("separated owners");
            proof.splitLinearError=Error(after.momentum,before.momentum);proof.splitAngularError=Error(after.angularMomentum,before.angularMomentum);
            SameGeometry(before,after);Check(after.dynamicBodies==2,"Exactly two native motion owners");Check(proof.splitLinearError<=proof.immediateMomentumBudget&&proof.splitAngularError<=proof.immediateMomentumBudget,"Split momentum");phase=3;
        }
        else if(command=="assembly step 10")
        {
            RequirePhase(3);State before=Last;Step(10);State after=Record("continued native simulation");
            proof.continuedLinearError=Error(after.momentum,before.momentum);proof.continuedAngularError=Error(after.angularMomentum,before.angularMomentum);
            Check(proof.continuedLinearError<=proof.immediateMomentumBudget&&proof.continuedAngularError<=proof.continuedAngularBudget,"Post-split momentum");
            Check(after.dynamicBodies==2,"Continued native owners");phase=4;proof.status="passed";
        }
        else throw new ArgumentException("Expected status, captureframe, quit, assembly impulse, assembly transfermass, assembly split or assembly step 10.");
        Save();return JsonUtility.ToJson(proof);
    }
    State Last {get{return proof.states[proof.states.Count-1];}}
    void RequirePhase(int expected){if(phase!=expected)throw new InvalidOperationException("Scenario phase mismatch");}
    void Step(int count){for(int i=0;i<count;i++){physics.Simulate((float)proof.timestep);step++;}}
    State Record(string operation)
    {
        var bodies=new List<BodyState>();var members=new List<MemberState>();Vector3 p=Vector3.zero,l=Vector3.zero;int dynamic=0;
        foreach(var root in physicsScene.GetRootGameObjects())foreach(var b in root.GetComponentsInChildren<Rigidbody>())if(!b.isKinematic)dynamic++;
        foreach(var owner in owners)
        {
            Rigidbody b=owner.Body;var ids=new List<int>();var colliders=new List<int>();p+=b.mass*b.velocity;l+=RigidAssemblyOwner.AngularMomentumAboutOrigin(b);
            Check(!b.isKinematic&&b.gameObject.activeInHierarchy,"Active native owner");
            Check(Math.Abs(b.mass-owner.Properties.Mass)<=1e-5*Math.Max(1,owner.Properties.Mass),"Published mass readback");
            Check((b.centerOfMass-RigidAssemblyOwner.Vector(owner.Properties.CenterOfMass)).magnitude<=1e-4,"Published COM readback");
            foreach(Vector3 axis in new[] {Vector3.right,Vector3.up,Vector3.forward})
            {
                Vector3 expected=b.rotation*RigidAssemblyOwner.Vector(owner.Properties.Inertia.Multiply(V(axis)));
                Vector3 actual=RigidAssemblyOwner.WorldInertia(b,b.rotation*axis);
                Check((expected-actual).magnitude<=1e-4*Math.Max(1,expected.magnitude),"Published full tensor readback");
            }
            foreach(var member in owner.Members)
            {
                Check(member.Collider.attachedRigidbody==b,"Unique collider owner");ids.Add(member.Properties.Id);colliders.Add(member.Collider.GetInstanceID());
                Check((member.Anchor.localPosition-RigidAssemblyOwner.Vector(member.Properties.LocalPosition)).magnitude<=1e-4,"Rigid retained local position");
                Check(1-Math.Abs(UnityEngine.Quaternion.Dot(member.Anchor.localRotation,RigidAssemblyOwner.Quaternion(member.Properties.LocalRotation)))<=1e-5,"Rigid retained local rotation");
                members.Add(new MemberState {id=member.Properties.Id,collider=member.Collider.GetInstanceID(),body=b.GetInstanceID(),mass=member.Properties.Mass,position=A(member.Anchor.position),rotation=A(member.Anchor.rotation),localPosition=A(member.Anchor.localPosition),localRotation=A(member.Anchor.localRotation),localCenterOfMass=A(RigidAssemblyOwner.Vector(member.Properties.LocalCenterOfMass)),principalInertia=A(RigidAssemblyOwner.Vector(member.Properties.PrincipalInertia)),principalRotation=A(RigidAssemblyOwner.Quaternion(member.Properties.PrincipalRotation))});
            }
            bodies.Add(new BodyState {id=b.GetInstanceID(),generation=owner.Generation,mass=b.mass,position=A(b.position),rotation=A(b.rotation),com=A(b.worldCenterOfMass),localCom=A(b.centerOfMass),inertia=A(b.inertiaTensor),inertiaRotation=A(b.inertiaTensorRotation),velocity=A(b.velocity),angularVelocity=A(b.angularVelocity),members=ids.ToArray(),colliders=colliders.ToArray()});
        }
        var state=new State {operation=operation,step=step,dynamicBodies=dynamic,momentum=A(p),angularMomentum=A(l),bodies=bodies.ToArray(),members=members.ToArray()};proof.states.Add(state);return state;
    }
    static void SameGeometry(State before,State after)
    {
        Check(before.members.Length==after.members.Length,"Retained member count");
        foreach(var a in before.members)
        {
            MemberState b=Array.Find(after.members,m=>m.id==a.id);Check(b!=null&&a.collider==b.collider,"Retained collider identity");
            Check(Error(a.position,b.position)<=1e-4,"Retained world position");double dot=0;for(int i=0;i<4;i++)dot+=a.rotation[i]*b.rotation[i];Check(1-Math.Abs(dot)<=1e-5,"Retained world rotation");
        }
    }
    void CaptureFrame()
    {
        var target=new RenderTexture(960,540,24,RenderTextureFormat.ARGB32);target.Create();var texture=new Texture2D(960,540,TextureFormat.RGB24,false);
        var prior=RenderTexture.active;var priorTarget=camera.targetTexture;
        try{camera.targetTexture=target;camera.Render();RenderTexture.active=target;texture.ReadPixels(new Rect(0,0,960,540),0,0);texture.Apply();File.WriteAllBytes(Path.Combine(output,"frame-"+phase+".png"),texture.EncodeToPNG());}
        finally{camera.targetTexture=priorTarget;RenderTexture.active=prior;target.Release();Destroy(target);Destroy(texture);}
    }
    void OnGUI()
    {
        GUI.color=Color.white;GUI.Label(new Rect(18,12,850,30),"CONTINUUM — RIGID ASSEMBLY OWNER   |   "+proof.status+"   |   "+proof.graphics);
        GUI.Label(new Rect(18,38,850,30),"Three authored members → one body → mass transfer → two bodies   ·   native steps: "+step);
        GUI.Label(new Rect(18,Screen.height-35,850,25),"Orthographic view · 1 m grid · no internal springs · isolated native physics scene");
    }
    void Save(){File.WriteAllText(Path.Combine(output,"status.json"),JsonUtility.ToJson(proof,true));}
    void Quit(){StartCoroutine(QuitCleanly());}
    System.Collections.IEnumerator QuitCleanly()
    {
        StopServer();if(physicsScene.IsValid()){yield return SceneManager.UnloadSceneAsync(physicsScene);sceneReleased=true;}Application.Quit();
    }
    void StopServer(){stopping=true;if(listener!=null)listener.Stop();if(server!=null)server.Join(250);}
    void OnDestroy(){StopServer();if(!sceneReleased&&physicsScene.IsValid())SceneManager.UnloadSceneAsync(physicsScene);foreach(var material in materials)Destroy(material);}
    static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    static double Error(double[] actual,double[] expected){double d=0,n=0;for(int i=0;i<3;i++){d+=(actual[i]-expected[i])*(actual[i]-expected[i]);n+=expected[i]*expected[i];}return Math.Sqrt(d)/Math.Max(1,Math.Sqrt(n));}
    static double[] Add(double[] a,double[] b){return new[] {a[0]+b[0],a[1]+b[1],a[2]+b[2]};}
    static double[] A(Vector3 v){return new[] {(double)v.x,(double)v.y,(double)v.z};}
    static double[] A(UnityEngine.Quaternion q){return new[] {(double)q.x,(double)q.y,(double)q.z,(double)q.w};}
    static Vec V(Vector3 v){return new Vec(v.x,v.y,v.z);}
    static Rotation Q(UnityEngine.Quaternion q){return new Rotation(q.x,q.y,q.z,q.w);}
}
