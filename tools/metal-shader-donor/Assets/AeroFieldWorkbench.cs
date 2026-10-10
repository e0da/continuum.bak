using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class AeroFieldWorkbench : MonoBehaviour
{
    [Serializable] public sealed class Fixture
    {
        public string schema, runId, vesselId, profile, geometrySha256, geometry;
        public int rows, vertices, indices;
        public float[] cameraPosition, cameraRotation, flowDirection, effectColor, effectParameters, textureParameters, falloffs;
        public float fov, near, far;
    }
    [Serializable] public sealed class Publication { public string schema, generation, bundle, bundleSha256; }
    [Serializable] sealed class State
    {
        public string schema = "continuum-aero-workbench/v1", hostId, renderer, unity, renderPath, geometrySource, fixtureSha256, bundleSha256, generation, reloadError, lastPng;
        public string contract = "Frozen gray opaque fixture or labelled analytic geometry; isolated experimental fields, no stock appearance equivalence.";
        public int processId, port, frames, fieldRenders, flowRenders, composites, ribbonSubmissions, shaderReloads, fixtureReloads, commandErrors;
        public int viewOccupiedPixels, flowOccupiedPixels, sceneOccupiedPixels;
        public bool fieldPixelProof;
        public bool splashFinishedAtCapture;
        public string screenshotTarget = "explicit-screen-backbuffer";
        public float cameraYaw, cameraPitch, cameraDistance, intensity, effectClock;
        public float[] flowDirection, cameraPosition, cameraRotation;
        public bool paused; public string[] shaders;
    }
    sealed class Command { public string text, response; public volatile bool done; }
    readonly ConcurrentQueue<Command> commands = new ConcurrentQueue<Command>();
    readonly List<Mesh> meshes = new List<Mesh>();
    readonly Matrix4x4[] seeds = new Matrix4x4[256];
    Camera view, field, flow; GameObject geometryRoot;
    Bounds geometryBounds; Material opaque, coverage, flowMaterial, sheath, ribbon, depthCopy;
    AssetBundle bundle; Mesh quad; Texture2D noise;
    RenderTexture fieldMap, flowMap, sceneMap;
    CommandBuffer ribbonCommands, sceneCommands;
    TcpListener listener; Thread worker; volatile bool stopping;
    int queued, smokeFrames; float clock, nextPoll;
    string output, publicationPath, publicationSeen, fixturePath, fixtureSeen;
    Vector3 direction = Vector3.down;
    Color effectColor = new Color(1,.3f,.05f,1);
    Vector4 effect = new Vector4(1,2,1,1), texture = new Vector4(1,2,.3f,0), falloffs = Vector4.one;
    readonly State state = new State();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_OUTPUT")))
            new GameObject("Continuum Aero Field Workbench").AddComponent<AeroFieldWorkbench>();
    }
    public static string HashFile(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    static string Absolute(string path) { if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidDataException("Absolute private path required."); return Path.GetFullPath(path); }
    static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    static void Check(Vector3 v) { if (!Finite(v.x)||!Finite(v.y)||!Finite(v.z)) throw new InvalidDataException("Nonfinite vector."); }
    static float Number(string text) { float v = float.Parse(text, CultureInfo.InvariantCulture); if (!Finite(v)) throw new InvalidDataException("Nonfinite argument."); return v; }
    static Vector3 Vector(float[] a) { if (a == null || a.Length != 3) throw new InvalidDataException("Vector layout."); var v = new Vector3(a[0],a[1],a[2]); Check(v); return v; }
    static Vector4 Vector4Of(float[] a) { if (a == null || a.Length != 4) throw new InvalidDataException("Parameter layout."); foreach(float v in a) if(!Finite(v)) throw new InvalidDataException("Nonfinite parameter."); return new Vector4(a[0],a[1],a[2],a[3]); }
    void Start()
    {
        try
        {
            output = Absolute(Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_OUTPUT"));
            if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("New private output directory required.");
            Directory.CreateDirectory(output);
            state.hostId = Guid.NewGuid().ToString("N"); state.processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            state.renderer = SystemInfo.graphicsDeviceType.ToString(); state.unity = Application.unityVersion;
            if (Application.unityVersion != "2019.4.18f1" || !SystemInfo.supportsInstancing || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
                throw new InvalidOperationException("Pinned editor, instancing and float field formats required.");
            foreach(Camera old in FindObjectsOfType<Camera>()) old.enabled = false;
            view = gameObject.AddComponent<Camera>(); view.renderingPath = RenderingPath.Forward; view.depthTextureMode = DepthTextureMode.Depth;
            view.backgroundColor = new Color(.035f,.045f,.07f); view.clearFlags = CameraClearFlags.SolidColor; view.cullingMask = 1 << 28;
            field = new GameObject("View field camera").AddComponent<Camera>(); field.enabled = false;
            flow = new GameObject("Flow field camera").AddComponent<Camera>(); flow.enabled = false;
            Shader fixtureShader=Shader.Find("Continuum/FixtureOpaque");
            if(fixtureShader==null||!fixtureShader.isSupported) throw new InvalidOperationException("Depth-capable original fixture shader unavailable.");
            opaque = new Material(fixtureShader); opaque.color = new Color(.25f,.28f,.32f);
            quad = new Mesh { name = "original ribbon quad" }; quad.vertices = new[] { new Vector3(-.5f,-.5f,0),new Vector3(.5f,-.5f,0),new Vector3(.5f,.5f,0),new Vector3(-.5f,.5f,0) };
            quad.uv = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.up }; quad.triangles = new[] { 0,1,2,0,2,3 }; quad.RecalculateBounds();
            noise = new Texture2D(32,32,TextureFormat.RGBA32,false,true); noise.wrapMode = TextureWrapMode.Repeat;
            var colors = new Color[1024]; var random = new System.Random(190418); for(int i=0;i<colors.Length;i++) colors[i]=Color.white*Mathf.Lerp(.35f,1f,(float)random.NextDouble()); noise.SetPixels(colors); noise.Apply();
            for(int i=0;i<seeds.Length;i++) seeds[i]=Matrix4x4.identity;
            fixturePath = Environment.GetEnvironmentVariable("CONTINUUM_AERO_FIXTURE");
            if (!string.IsNullOrEmpty(fixturePath)) LoadFixture(Absolute(fixturePath), true); else Analytic();
            publicationPath = Environment.GetEnvironmentVariable("CONTINUUM_AERO_PUBLICATION");
            if (!string.IsNullOrEmpty(publicationPath)) { publicationPath=Absolute(publicationPath); ReloadPublication(); }
            else ReloadBundle(Absolute(Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_BUNDLE") ?? Environment.GetEnvironmentVariable("CONTINUUM_AERO_SHADER_BUNDLE")), null, "direct");
            smokeFrames = 0; string smoke=Environment.GetEnvironmentVariable("CONTINUUM_AERO_SMOKE_FRAMES"); if(!string.IsNullOrEmpty(smoke)) { smokeFrames=int.Parse(smoke); if(smokeFrames<3||smokeFrames>300) throw new InvalidDataException("Smoke frames3..300."); }
            int port = 0; string portText=Environment.GetEnvironmentVariable("CONTINUUM_AERO_DEBUG_PORT"); if(!string.IsNullOrEmpty(portText)) port=int.Parse(portText);
            if(port<0||port>65535) throw new InvalidDataException("Port range.");
            listener = new TcpListener(IPAddress.Loopback,port); listener.Start(4); state.port=((IPEndPoint)listener.LocalEndpoint).Port;
            worker = new Thread(SocketLoop) { IsBackground=true, Name="Continuum bounded debug socket" }; worker.Start();
            WriteStatus();
        }
        catch(Exception error) { state.reloadError=error.ToString(); if(!string.IsNullOrEmpty(output)&&Directory.Exists(output)) WriteStatus(); Debug.LogException(error); Application.Quit(2); enabled=false; }
    }
    void SocketLoop()
    {
        while(!stopping)
        {
            try
            {
                using(TcpClient client=listener.AcceptTcpClient())
                using(NetworkStream stream=client.GetStream())
                {
                    stream.ReadTimeout=2000; stream.WriteTimeout=2000;
                    var bytes=new List<byte>(); int b;
                    while((b=stream.ReadByte())>=0&&b!='\n') { if(bytes.Count>=4096) throw new InvalidDataException("Command too large."); bytes.Add((byte)b); }
                    if(bytes.Count==0) continue;
                    if(Interlocked.Increment(ref queued)>16) { Interlocked.Decrement(ref queued); throw new InvalidDataException("Command queue full."); }
                    var command=new Command { text=Encoding.UTF8.GetString(bytes.ToArray()).Trim() }; commands.Enqueue(command);
                    for(int wait=0;wait<500&&!command.done&&!stopping;wait++) Thread.Sleep(10);
                    if(command.done) { byte[] reply=Encoding.UTF8.GetBytes(command.response+"\n"); stream.Write(reply,0,reply.Length); }
                }
            }
            catch(Exception) { if(stopping) return; }
        }
    }
    void Update()
    {
        if(view==null||coverage==null) return;
        if(!state.paused) clock+=Time.unscaledDeltaTime;
        Command command; for(int i=0;i<8&&commands.TryDequeue(out command);i++)
        {
            Interlocked.Decrement(ref queued);
            try { Execute(command.text); command.response=JsonUtility.ToJson(state); }
            catch(Exception error) { state.commandErrors++; command.response="{\"error\":\""+Escape(error.Message)+"\",\"hostId\":\""+state.hostId+"\"}"; }
            command.done=true;
        }
        if(Time.realtimeSinceStartup>=nextPoll)
        {
            nextPoll=Time.realtimeSinceStartup+1;
            try { if(!string.IsNullOrEmpty(publicationPath)&&HashFile(publicationPath)!=publicationSeen) ReloadPublication(); } catch(Exception e) { state.reloadError=e.Message; }
            try { if(!string.IsNullOrEmpty(fixturePath)&&HashFile(fixturePath)!=fixtureSeen) LoadFixture(fixturePath,false); } catch(Exception e) { state.reloadError=e.Message; }
            WriteStatus();
        }
    }
    static string Escape(string v) { return v.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","\\r"); }
    void Execute(string text)
    {
        string[] words=text.Split(new[] {' '},StringSplitOptions.RemoveEmptyEntries); if(words.Length==0) throw new InvalidDataException("Empty command.");
        switch(words[0].ToLowerInvariant())
        {
            case "status": break;
            case "pause": state.paused=true; break;
            case "resume": state.paused=false; break;
            case "setflow": if(words.Length!=4) throw new InvalidDataException("setflow x y z"); Vector3 nextDirection=new Vector3(Number(words[1]),Number(words[2]),Number(words[3])); if(nextDirection.sqrMagnitude<.000001f) throw new InvalidDataException("Zero flow."); direction=nextDirection.normalized; break;
            case "setintensity": if(words.Length!=2) throw new InvalidDataException("setintensity value"); effect.x=Mathf.Clamp(Number(words[1]),0,10); break;
            case "cameraorbit": if(words.Length!=4) throw new InvalidDataException("cameraorbit yaw pitch distance"); Quaternion orbit=Quaternion.Euler(Mathf.Clamp(Number(words[2]),-89,89),Number(words[1]),0); view.transform.position=geometryBounds.center+orbit*Vector3.back*Mathf.Clamp(Number(words[3]),.1f,10000); view.transform.LookAt(geometryBounds.center); break;
            case "bundlehotreload": ReloadBundle(Absolute(text.Substring(words[0].Length).Trim()),null,"manual-"+DateTime.UtcNow.Ticks); break;
            case "fixturehotreload": LoadFixture(Absolute(text.Substring(words[0].Length).Trim()),false); break;
            case "captureframe": StartCoroutine(Capture(false)); break;
            case "quit": Application.Quit(0); break;
            default: throw new InvalidDataException("Unknown command.");
        }
        WriteStatus();
    }
    void ReloadPublication()
    {
        string hash=HashFile(publicationPath); if(new FileInfo(publicationPath).Length>16384) throw new InvalidDataException("Publication too large.");
        Publication publication=JsonUtility.FromJson<Publication>(File.ReadAllText(publicationPath));
        if(publication==null||publication.schema!="continuum-aero-shader-publication/v1"||string.IsNullOrEmpty(publication.generation)||string.IsNullOrEmpty(publication.bundleSha256)) throw new InvalidDataException("Incomplete publication.");
        ReloadBundle(Absolute(publication.bundle),publication.bundleSha256,publication.generation,hash);
        publicationSeen=hash;
    }
    void ReloadBundle(string path,string expected,string generation,string publicationHash=null)
    {
        string before=HashFile(path); if(expected!=null&&before!=expected) throw new InvalidDataException("Bundle hash differs.");
        if(before==state.bundleSha256) { if(publicationHash!=null&&HashFile(publicationPath)!=publicationHash) throw new InvalidDataException("Publication changed during reload."); state.generation=generation; return; }
        // A same-name loaded container can reject a candidate; leave the last good owner intact.
        AssetBundle next=AssetBundle.LoadFromFile(path); if(next==null) throw new InvalidDataException("Bundle cannot load (possibly loaded identity collision); publish a unique bundle generation.");
        var materials=new List<Material>();
        try
        {
            Shader[] all=next.LoadAllAssets<Shader>();
            foreach(string suffix in new[] {"Coverage","Flow","Sheath","Ribbon","SceneDepthCopy"})
            {
                Shader found=null; foreach(Shader shader in all) if(shader.name=="Continuum/AeroField"+suffix) found=shader;
                if(found==null||!found.isSupported) throw new InvalidDataException("Required supported shader absent: "+suffix);
                materials.Add(new Material(found));
            }
            materials[3].enableInstancing=true;
            if(HashFile(path)!=before) throw new InvalidDataException("Bundle changed during reload.");
            if(publicationHash!=null&&HashFile(publicationPath)!=publicationHash) throw new InvalidDataException("Publication changed during reload.");
            foreach(Material m in materials) m.SetTexture("_ContinuumNoiseTex",noise);
            Material[] old={coverage,flowMaterial,sheath,ribbon,depthCopy}; AssetBundle previous=bundle;
            coverage=materials[0]; flowMaterial=materials[1]; sheath=materials[2]; ribbon=materials[3]; depthCopy=materials[4]; bundle=next;
            ribbonCommands?.Clear(); sceneCommands?.Clear(); foreach(Material material in old) if(material!=null) Destroy(material); if(previous!=null) previous.Unload(true);
            state.bundleSha256=before; state.generation=generation; state.shaders=Array.ConvertAll(materials.ToArray(),m=>m.shader.name); state.shaderReloads++; state.reloadError=null;
        }
        catch { foreach(Material material in materials) Destroy(material); next.Unload(true); throw; }
    }
    void LoadFixture(string path,bool initial)
    {
        string manifestHash=HashFile(path); if(new FileInfo(path).Length>1048576) throw new InvalidDataException("Fixture manifest bound.");
        Fixture metadata=JsonUtility.FromJson<Fixture>(File.ReadAllText(path));
        if(metadata==null||metadata.schema!="continuum-aero-field-fixture/v1"||metadata.geometry!="geometry.bin") throw new InvalidDataException("Fixture schema/path.");
        string chunk=Path.Combine(Path.GetDirectoryName(path),metadata.geometry); if(new FileInfo(chunk).Length>300000000||HashFile(chunk)!=metadata.geometrySha256) throw new InvalidDataException("Fixture payload hash/bound.");
        GameObject next=new GameObject("Frozen fixture"); next.SetActive(false); var loaded=new List<Mesh>(); int totalV=0,totalI=0;
        try
        {
            using(var reader=new BinaryReader(File.OpenRead(chunk)))
            {
                if(reader.ReadInt32()!=0x31464641||reader.ReadInt32()!=1) throw new InvalidDataException("Chunk identity.");
                int count=reader.ReadInt32(); if(count<1||count>4096||count!=metadata.rows) throw new InvalidDataException("Row count.");
                for(int row=0;row<count;row++)
                {
                    int nv=reader.ReadInt32(),ni=reader.ReadInt32(); if(nv<1||ni<3||ni%3!=0) throw new InvalidDataException("Mesh dimensions."); totalV=checked(totalV+nv); totalI=checked(totalI+ni); if(totalV>4000000||totalI>12000000) throw new InvalidDataException("Mesh capacity.");
                    var vertices=new Vector3[nv]; var normals=new Vector3[nv]; var uv=new Vector2[nv]; var indices=new int[ni];
                    for(int i=0;i<nv;i++) { vertices[i]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()); normals[i]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()); uv[i]=new Vector2(reader.ReadSingle(),reader.ReadSingle()); Check(vertices[i]); Check(normals[i]); if(!Finite(uv[i].x)||!Finite(uv[i].y)) throw new InvalidDataException("UV nonfinite."); }
                    for(int i=0;i<ni;i++) { indices[i]=reader.ReadInt32(); if(indices[i]<0||indices[i]>=nv) throw new InvalidDataException("Index range."); }
                    var mesh=new Mesh { indexFormat=IndexFormat.UInt32 }; loaded.Add(mesh); mesh.vertices=vertices; mesh.normals=normals; mesh.uv=uv; mesh.triangles=indices; mesh.RecalculateBounds(); AddMesh(next,mesh);
                }
                if(reader.BaseStream.Position!=reader.BaseStream.Length||metadata.vertices!=totalV||metadata.indices!=totalI) throw new InvalidDataException("Chunk trailing bytes/counts.");
            }
            if(HashFile(path)!=manifestHash||HashFile(chunk)!=metadata.geometrySha256) throw new InvalidDataException("Fixture changed during read.");
            if(initial)
            {
                Vector3 position=Vector(metadata.cameraPosition); Vector4 rotation=Vector4Of(metadata.cameraRotation); Quaternion q=new Quaternion(rotation.x,rotation.y,rotation.z,rotation.w); if(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w<.9f) throw new InvalidDataException("Camera rotation.");
                if(!Finite(metadata.fov)||metadata.fov<1||metadata.fov>179||!Finite(metadata.near)||!Finite(metadata.far)||metadata.near<=0||metadata.far<=metadata.near) throw new InvalidDataException("Camera range.");
                direction=Vector(metadata.flowDirection); if(direction.sqrMagnitude<.000001f) throw new InvalidDataException("Flow direction."); direction.Normalize(); effect=Vector4Of(metadata.effectParameters); texture=Vector4Of(metadata.textureParameters); Vector4 color=Vector4Of(metadata.effectColor); effectColor=new Color(color.x,color.y,color.z,color.w); Vector3 falls=Vector(metadata.falloffs); falloffs=new Vector4(falls.x,falls.y,falls.z,0);
                view.transform.SetPositionAndRotation(position,q); view.fieldOfView=metadata.fov; view.nearClipPlane=metadata.near; view.farClipPlane=metadata.far;
            }
            if(geometryRoot!=null) { geometryRoot.SetActive(false); Destroy(geometryRoot); } foreach(Mesh mesh in meshes) Destroy(mesh); meshes.Clear(); meshes.AddRange(loaded); geometryRoot=next; next.SetActive(true); ComputeBounds();
            fixturePath=path; fixtureSeen=manifestHash; state.fixtureSha256=manifestHash; state.geometrySource="private-frozen-geometry"; state.fixtureReloads++;
        }
        catch { Destroy(next); foreach(Mesh mesh in loaded) Destroy(mesh); throw; }
    }
    void AddMesh(GameObject root,Mesh mesh) { GameObject node=new GameObject("Opaque snapshot",typeof(MeshFilter),typeof(MeshRenderer)); node.layer=28; node.transform.SetParent(root.transform,false); node.GetComponent<MeshFilter>().sharedMesh=mesh; node.GetComponent<MeshRenderer>().sharedMaterial=opaque; }
    void ComputeBounds() { bool first=true; foreach(Renderer renderer in geometryRoot.GetComponentsInChildren<Renderer>()) { if(first) { geometryBounds=renderer.bounds; first=false; } else geometryBounds.Encapsulate(renderer.bounds); } }
    void Analytic()
    {
        geometryRoot=new GameObject("LABELLED analytic concave shielded geometry");
        foreach(Vector3 center in new[] {new Vector3(-1,0,0),new Vector3(1,0,0),new Vector3(0,-1,0),new Vector3(0,0,2)})
        { GameObject cube=GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(geometryRoot.transform,false); cube.transform.localPosition=center; cube.transform.localScale=new Vector3(1,3,.5f); cube.layer=28; cube.GetComponent<Renderer>().sharedMaterial=opaque; Destroy(cube.GetComponent<Collider>()); }
        ComputeBounds(); view.transform.position=new Vector3(0,1,-12); view.transform.LookAt(geometryBounds.center); view.fieldOfView=45; view.nearClipPlane=.1f; view.farClipPlane=100; direction=Vector3.forward; state.geometrySource="analytic-concave-shielded-not-KSP";
    }
    RenderTexture Target(string name,RenderTextureFormat format,int depth) { var rt=new RenderTexture(Screen.width,Screen.height,depth,format,RenderTextureReadWrite.Linear) { name=name, filterMode=FilterMode.Point }; rt.Create(); return rt; }
    void EnsureTargets()
    {
        if(Screen.width<1||Screen.height<1||Screen.width>4096||Screen.height>4096) throw new InvalidOperationException("Workbench field dimensions outside1..4096.");
        if(fieldMap!=null&&fieldMap.width==Screen.width&&fieldMap.height==Screen.height) return;
        foreach(RenderTexture rt in new[] {fieldMap,flowMap,sceneMap}) if(rt!=null) { rt.Release(); Destroy(rt); }
        fieldMap=Target("View normal/depth",RenderTextureFormat.ARGBFloat,24); flowMap=Target("Flow depth",RenderTextureFormat.RFloat,24); sceneMap=Target("Scene eye depth",RenderTextureFormat.RFloat,0);
    }
    void OnPreRender()
    {
        if(coverage==null) return;
        EnsureTargets(); field.CopyFrom(view); field.enabled=false; field.depthTextureMode=DepthTextureMode.None; field.cullingMask=1<<28; field.targetTexture=fieldMap; field.clearFlags=CameraClearFlags.SolidColor; field.backgroundColor=Color.clear; field.transform.SetPositionAndRotation(view.transform.position,view.transform.rotation);
        field.RenderWithShader(coverage.shader,"RenderType"); state.fieldRenders++;
        float radius=Mathf.Max(1,geometryBounds.extents.magnitude); flow.CopyFrom(view); flow.enabled=false; flow.depthTextureMode=DepthTextureMode.None; flow.orthographic=true; flow.orthographicSize=radius*1.1f; flow.nearClipPlane=.01f; flow.farClipPlane=radius*4+.1f; flow.aspect=1; flow.cullingMask=1<<28; flow.targetTexture=flowMap; flow.clearFlags=CameraClearFlags.SolidColor; flow.backgroundColor=Color.clear;
        Vector3 up=Mathf.Abs(Vector3.Dot(direction,Vector3.up))>.95f?Vector3.right:Vector3.up; flow.transform.SetPositionAndRotation(geometryBounds.center-direction*radius*2,Quaternion.LookRotation(direction,up));
        Shader.SetGlobalFloat("_ContinuumFlowFar",flow.farClipPlane); flow.RenderWithShader(flowMaterial.shader,"RenderType"); state.flowRenders++;
        Globals();
        if(sceneCommands==null) { sceneCommands=new CommandBuffer { name="Current main-camera positive eye depth" }; view.AddCommandBuffer(CameraEvent.AfterForwardOpaque,sceneCommands); }
        sceneCommands.Clear(); sceneCommands.Blit(BuiltinRenderTextureType.CurrentActive,sceneMap,depthCopy); sceneCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        if(ribbonCommands==null) { ribbonCommands=new CommandBuffer { name="256 bounded original ribbons" }; view.AddCommandBuffer(CameraEvent.AfterEverything,ribbonCommands); }
        ribbonCommands.Clear(); ribbonCommands.DrawMeshInstanced(quad,0,ribbon,0,seeds,256); state.ribbonSubmissions++;
    }
    void Globals()
    {
        Shader.SetGlobalTexture("_ContinuumViewField",fieldMap); Shader.SetGlobalTexture("_ContinuumFlowField",flowMap); Shader.SetGlobalTexture("_ContinuumSceneDepth",sceneMap); Shader.SetGlobalTexture("_ContinuumNoiseTex",noise);
        Shader.SetGlobalVector("_ContinuumViewField_TexelSize",new Vector4(1f/fieldMap.width,1f/fieldMap.height,fieldMap.width,fieldMap.height));
        Shader.SetGlobalMatrix("_ContinuumViewInverseProjection",GL.GetGPUProjectionMatrix(view.projectionMatrix,true).inverse); Shader.SetGlobalMatrix("_ContinuumViewCameraToWorld",view.cameraToWorldMatrix);
        Shader.SetGlobalMatrix("_ContinuumFlowViewProjection",GL.GetGPUProjectionMatrix(flow.projectionMatrix,true)*flow.worldToCameraMatrix); Shader.SetGlobalMatrix("_ContinuumFlowWorldToView",flow.worldToCameraMatrix); Shader.SetGlobalFloat("_ContinuumFlowFar",flow.farClipPlane);
        Shader.SetGlobalVector("_ContinuumEffectDirection",direction); Shader.SetGlobalColor("_ContinuumEffectColor",effectColor); Shader.SetGlobalVector("_ContinuumEffectParameters",effect); texture.w=clock; Shader.SetGlobalVector("_ContinuumTextureParameters",texture); Shader.SetGlobalVector("_ContinuumFalloffs",falloffs);
        Shader.SetGlobalVector("_ContinuumSeedRect",new Vector4(0,0,1,1));
    }
    void OnRenderImage(RenderTexture source,RenderTexture destination)
    {
        if(depthCopy==null) { Graphics.Blit(source,destination); return; }
        Globals(); Graphics.Blit(source,destination); Graphics.Blit(source,destination,sheath); state.composites++; state.frames++; state.renderPath=view.actualRenderingPath.ToString();
        if(smokeFrames>0&&state.frames==smokeFrames) StartCoroutine(Capture(true));
    }
    IEnumerator Capture(bool quit)
    {
        // Startup fields can render underneath Unity's splash presentation.
        while(!SplashScreen.isFinished) yield return null;
        yield return new WaitForEndOfFrame();
        state.splashFinishedAtCapture=SplashScreen.isFinished;
        string path=Path.Combine(output,"frame-"+state.frames+"-"+DateTime.UtcNow.Ticks+".png"); var image=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
        RenderTexture previous=RenderTexture.active;
        try { RenderTexture.active=null; image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0); image.Apply(); RenderTexture.active=previous; File.WriteAllBytes(path,image.EncodeToPNG()); state.lastPng=path;
            state.viewOccupiedPixels=PixelProof(fieldMap,true); state.flowOccupiedPixels=PixelProof(flowMap,false); state.sceneOccupiedPixels=PixelProof(sceneMap,false);
            state.fieldPixelProof=state.viewOccupiedPixels>0&&state.flowOccupiedPixels>0&&state.sceneOccupiedPixels>0; WriteStatus(); }
        catch(Exception error) { state.fieldPixelProof=false; state.reloadError="capture: "+error.Message; WriteStatus(); if(quit) Application.Quit(2); }
        finally { RenderTexture.active=previous; Destroy(image); }
        if(quit) Application.Quit(state.splashFinishedAtCapture&&state.fieldPixelProof&&state.renderPath=="Forward"&&state.fieldRenders>0&&state.flowRenders>0&&state.composites>0&&state.ribbonSubmissions>0?0:2);
    }
    static int PixelProof(RenderTexture rt,bool normalDepth)
    {
        RenderTexture previous=RenderTexture.active; var pixels=new Texture2D(rt.width,rt.height,TextureFormat.RGBAFloat,false,true);
        try { RenderTexture.active=rt; pixels.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); pixels.Apply(); int count=0;
            foreach(Color color in pixels.GetPixels()) { float depth=normalDepth?color.a:color.r; if(!Finite(depth)||depth<0) throw new InvalidDataException("Invalid native field depth.");
                if(depth>0) { if(normalDepth&&(!Finite(color.r)||!Finite(color.g)||!Finite(color.b)||color.r<0||color.r>1||color.g<0||color.g>1||color.b<0||color.b>1)) throw new InvalidDataException("Invalid native normal encoding."); count++; } } return count; }
        finally { RenderTexture.active=previous; Destroy(pixels); }
    }
    void WriteStatus()
    {
        if(view!=null)
        {
            Vector3 offset=view.transform.position-geometryBounds.center;
            state.cameraDistance=offset.magnitude;
            state.cameraYaw=Mathf.Atan2(-offset.x,-offset.z)*Mathf.Rad2Deg;
            state.cameraPitch=Mathf.Asin(Mathf.Clamp(offset.y/Mathf.Max(offset.magnitude,.00001f),-1,1))*Mathf.Rad2Deg;
            Vector3 position=view.transform.position; Quaternion rotation=view.transform.rotation;
            state.cameraPosition=new[] {position.x,position.y,position.z}; state.cameraRotation=new[] {rotation.x,rotation.y,rotation.z,rotation.w};
        }
        state.flowDirection=new[] {direction.x,direction.y,direction.z}; state.intensity=effect.x; state.effectClock=clock;
        string target=Path.Combine(output,"status.json"),temp=target+".tmp"; File.WriteAllText(temp,JsonUtility.ToJson(state,true)); if(File.Exists(target)) File.Replace(temp,target,null); else File.Move(temp,target);
    }
    void OnDestroy()
    {
        stopping=true; listener?.Stop(); Command command; while(commands.TryDequeue(out command)) { command.response="{\"error\":\"host-stopping\"}"; command.done=true; }
        if(view!=null&&ribbonCommands!=null) view.RemoveCommandBuffer(CameraEvent.AfterEverything,ribbonCommands); ribbonCommands?.Release();
        if(view!=null&&sceneCommands!=null) view.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque,sceneCommands); sceneCommands?.Release();
        foreach(Material m in new[] {opaque,coverage,flowMaterial,sheath,ribbon,depthCopy}) if(m!=null) Destroy(m);
        foreach(RenderTexture rt in new[] {fieldMap,flowMap,sceneMap}) if(rt!=null) { rt.Release(); Destroy(rt); }
        foreach(Mesh mesh in meshes) Destroy(mesh); if(quad!=null) Destroy(quad); if(noise!=null) Destroy(noise); if(bundle!=null) bundle.Unload(true); if(field!=null) Destroy(field.gameObject); if(flow!=null) Destroy(flow.gameObject); if(geometryRoot!=null) Destroy(geometryRoot);
    }
}
