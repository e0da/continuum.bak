using System;
using System.Collections.Generic;
using KspContinuum;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class PersistentTrailRuntime : IDisposable
{
    [Serializable] public sealed class StatusData
    {
        public string schema = "continuum-persistent-trail/v1", model = "Artistic authored emitter history; uniform wind, analytical diffusion/decay; no weather or contrail microphysics", status = "resident", cleanupStatus;
        public double simulationTime, playbackRate = 1;
        public double windX,windY,windZ,lifetimeSeconds=30,initialWidthMeters=.5,diffusionSquareMetersPerSecond=.1,decayPerSecond=.05,emissionCadenceSeconds=.1;
        public double[] oldestPositionMeters=new double[3],newestPositionMeters=new double[3];
        public double oldestAgeSeconds,newestAgeSeconds;
        public bool paused = true, emission, visible=true;
        public bool compositionMainTextureProperty,compositionTrailTextureProperty;
        public bool probeInstalled;
        public string recordingPath,recordingSha256,recordingRunId,recordingMode,recordingPresentation;
        public string representation="particles",segmentScope="Measured centerline strip; 0.5 pixel centerline simplification only; tapered width/alpha are artistic approximations, not density conservation or plume physics";
        public int segmentInputSpans,segmentDraws,segmentRetainedPoints,segmentNearClippedSpans,segmentGapSpans,segmentViewportWidth,segmentViewportHeight;
        public long segmentBuilds;
        public int segmentCapacity=MeasuredTrailSegments.MaxSegments,segmentWindChanges;
        public double segmentMaximumSourceSpanSeconds=MeasuredTrailSegments.MaximumSourceSpanSeconds;
        public double segmentErrorPixels=.5,segmentRemovedErrorPixels,segmentNearPlane;
        public double segmentDepthAbsoluteToleranceMeters=MeasuredTrailSegments.DepthAbsoluteToleranceMeters,segmentDepthRelativeTolerance=MeasuredTrailSegments.DepthRelativeTolerance,segmentRemovedDepthErrorMeters,segmentRemovedDepthErrorRatio;
        public double[] segmentEndpointsScene=new double[12],segmentEndpointTimes=new double[4],segmentCameraOrigin=new double[3];
        public double[] segmentGeometry=new double[0];
        public string segmentGeometryLayout="13 doubles per retained segment: ax ay az bx by bz birthA birthB widthA widthB opacityA opacityB emissionRun";
        public float[] segmentView=new float[16],segmentGpuProjection=new float[16];
        public int recordingProcessId,recordingCursor,recordingFrames;
        public double recordingUniversalTime,recordingDuration;
        public double[] recordedRootScene=new double[3];
        public ProbeData probe;
        public int live, capacity = 1024, uploadCount;
        public long expired, capacityEvictions, emitted, generation, uploadBytes, renderedFrames;
        public long submissionTicks, commandBuilds,preparationTicks,simulationTicks,uploadTicks,viewSetupTicks,renderTicks,compositionTicks,clockFrequency=System.Diagnostics.Stopwatch.Frequency;
        public string renderScope = "Reusable bounded billboard mesh; explicit dual scene-depth testing and premultiplied overlay; upload bytes estimate vertex/color payload, not driver allocation";
    }
    [Serializable] public sealed class ProbeData
    {
        public string depthSource="synthetic-texture2d",sceneSha256,wholeGeometrySha256,bodyGeometrySha256;
        public SceneTriangle[] triangles;
        public float[] craftProducerProjection,bodyProducerProjection,producerNearFar;
        public int[] producerDimensions;
        public int sceneGeneration,debugShaderGeneration;
        public string craftRenderMode;
        public float[] craftProjectedAabbPixels;
        public float bodyCraftExclusionMarginPixels;
        public string scope="Synthetic positive-eye-depth/texture-UV witness using actual trail occlusion and composition passes; not live-scene occlusion qualification",uvConvention="Normalized bottom-left texture UV; PNG top-left rows invert Y";
        public float[] craftRegion={.125f,.25f,.375f,.75f,10},bodyRegion={.625f,.125f,.875f,.625f,20};
        public float[] centerUvDepth={.25f,.375f,8,.25f,.625f,12,.75f,.25f,18,.75f,.5f,22,.5f,.875f,15,.5f,.125f,15};
        public float[] rgba={0,1,0,1,1,0,0,1,0,1,1,1,1,1,0,1,1,0,1,1,1,1,1,1};
        public bool[] expectedVisible={true,false,true,false,true,true};
        public int textureSize=64,craftTextureId,bodyTextureId,width,height;
        public string textureFormat="RFloat",sampler="Point/Clamp/no-mips";
        public float radiusScreenHeight=.045f,actualFov,actualAspect;
        public float[] actualCentersCameraRelative=new float[18],actualEyeDepths=new float[6],actualGpuProjection=new float[16],actualView=new float[16];
        public double historyTimeOnInstall; public long historyGenerationOnInstall;
        public bool pausedOnInstall,emissionOnInstall,historyRestored;
    }
    [Serializable] public sealed class SceneTriangle
    {
        public string domain,source;
        public int triangle;
        public float[] worldVertices,projectedUvDepth;
        public float centerU,centerV,inradiusPixels;
    }
    public static SceneTriangle SelectTriangle(SceneTriangle best,string domain,string source,Vector3 a,Vector3 b,Vector3 c,int index,Camera camera,int width,int height,SceneTriangle exclude)
    {
        Vector3 pa=camera.WorldToViewportPoint(a),pb=camera.WorldToViewportPoint(b),pc=camera.WorldToViewportPoint(c);
        if(!Inside(pa,camera)||!Inside(pb,camera)||!Inside(pc,camera)) return best;
        Vector2 x=new Vector2(pa.x*width,pa.y*height),y=new Vector2(pb.x*width,pb.y*height),z=new Vector2(pc.x*width,pc.y*height);
        float la=(y-z).magnitude,lb=(x-z).magnitude,lc=(x-y).magnitude,sum=la+lb+lc;
        if(sum<=0) return best;
        Vector2 center=(x*la+y*lb+z*lc)/sum;
        float radius=Mathf.Abs((y.x-x.x)*(z.y-x.y)-(y.y-x.y)*(z.x-x.x))/sum;
        if(radius<16||center.x<20||center.x>width-20||center.y<20||center.y>height-20) return best;
        if(exclude!=null&&(center-new Vector2(exclude.centerU*width,exclude.centerV*height)).magnitude<50) return best;
        if(best!=null&&best.inradiusPixels>=radius) return best;
        return new SceneTriangle {domain=domain,source=source,triangle=index,worldVertices=new[]{a.x,a.y,a.z,b.x,b.y,b.z,c.x,c.y,c.z},projectedUvDepth=new[]{pa.x,pa.y,pa.z,pb.x,pb.y,pb.z,pc.x,pc.y,pc.z},centerU=center.x/width,centerV=center.y/height,inradiusPixels=radius};
    }
    static bool Inside(Vector3 p,Camera c) { return !float.IsNaN(p.x)&&!float.IsNaN(p.y)&&!float.IsNaN(p.z)&&p.z>c.nearClipPlane&&p.z<c.farClipPlane&&p.x>=0&&p.x<=1&&p.y>=0&&p.y<=1; }
    public readonly StatusData Status = new StatusData();
    readonly PersistentTrailOwner owner;
    readonly TrailEmitterRecording recording;
    readonly MeasuredTrailSegments segments;
    readonly Mesh mesh;
    readonly Mesh segmentMesh;
    readonly Vector3[] segmentVertices;
    readonly Color[] segmentColors;
    readonly Material material;
    readonly CommandBuffer commands;
    readonly Vector3[] vertices;
    readonly Color[] colors;
    readonly PersistentTrailParcel[] parcels;
    readonly int[] order;
    readonly double[] depths;
    readonly long[] ids;
    readonly DepthOrder depthOrder;
    readonly int[] segmentOrder=new int[MeasuredTrailSegments.MaxSegments];
    readonly double[] segmentDepths=new double[MeasuredTrailSegments.MaxSegments];
    readonly long[] segmentIds=new long[MeasuredTrailSegments.MaxSegments];
    readonly DepthOrder segmentDepthOrder;
    bool commandsRecorded;
    sealed class DepthOrder : IComparer<int>
    {
        readonly double[] depth; readonly long[] id;
        public DepthOrder(double[] depth,long[] id) { this.depth=depth; this.id=id; }
        public int Compare(int a,int b) { int d=depth[b].CompareTo(depth[a]); return d!=0?d:id[a].CompareTo(id[b]); }
    }
    long uploadedGeneration = -1;
    readonly double[] lastOrigin = new double[3];
    Quaternion lastRotation;
    float lastProbeFov,lastProbeAspect;
    RenderTexture target, composed;
    Texture2D probeCraft,probeBody;
    double lastWall;
    bool disposed;
    Matrix4x4 sceneProbeView;
    float sceneProbeFov,sceneProbeAspect;
    public void SceneProbe(SceneTriangle craft,SceneTriangle body,Camera camera,float bodyNear,int width,int height,string sceneHash,string wholeHash,string bodyHash)
    {
        if(Status.probeInstalled) throw new InvalidOperationException("Turn prior probe off first.");
        if(craft==null||body==null) throw new InvalidOperationException("Actual triangles need >=16px inscribed radius, unclipped and separated; focus/zoom then retry.");
        float near=Mathf.Min(camera.nearClipPlane,bodyNear);
        if(near<=.004f) throw new InvalidOperationException("Both scene near planes need room for an unclipped front witness; zoom out before admission.");
        float front=Mathf.Min(near,1)*.5f;
        var p=new ProbeData {depthSource="actual-scene-rendertextures",scope="Actual captured triangle projection plus existing scene positive-depth producers; opaque static witness only",triangles=new[]{craft,body},sceneSha256=sceneHash,wholeGeometrySha256=wholeHash,bodyGeometrySha256=bodyHash,width=width,height=height,radiusScreenHeight=4f/height,textureSize=0,craftRegion=new float[0],bodyRegion=new float[0],centerUvDepth=new float[12],actualCentersCameraRelative=new float[12],actualEyeDepths=new float[4],rgba=new float[]{0,1,0,1,1,0,0,1,0,1,1,1,1,1,0,1},expectedVisible=new[]{true,false,true,false},historyTimeOnInstall=owner.Time,historyGenerationOnInstall=owner.Generation,pausedOnInstall=Status.paused,emissionOnInstall=owner.Emission};
        for(int d=0;d<2;d++) { var t=p.triangles[d]; float behind=Mathf.Max(t.projectedUvDepth[2],Mathf.Max(t.projectedUvDepth[5],t.projectedUvDepth[8]))*1.05f; for(int k=0;k<2;k++) { int i=(d*2+k)*3; p.centerUvDepth[i]=t.centerU+(k==0?-8f:8f)/width; p.centerUvDepth[i+1]=t.centerV; p.centerUvDepth[i+2]=k==0?front:behind; } }
        sceneProbeView=camera.worldToCameraMatrix; sceneProbeFov=camera.fieldOfView; sceneProbeAspect=camera.aspect;
        Status.probe=p; Status.probeInstalled=true; uploadedGeneration=-1; HoldClock();
    }
    public PersistentTrailRuntime(double[] anchor,TrailEmitterRecording measured=null,string recordingPath=null,string recordingHash=null)
    {
        long preparationStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        if(measured!=null) TrailEmitterFormat.Validate(measured);
        recording=measured;
        segmentDepthOrder=new DepthOrder(segmentDepths,segmentIds);
        if(measured!=null)segments=new MeasuredTrailSegments(measured);
        owner = new PersistentTrailOwner(Status.capacity, new[] { anchor[0] + 3, anchor[1], anchor[2] }, new[] { 0.0, 0.0, -8.0 });
        try
        {
            Shader shader = Shader.Find("Hidden/Continuum/PersistentTrail");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Persistent trail shader unavailable.");
            material = new Material(shader);
            Status.compositionMainTextureProperty=material.HasProperty("_MainTex"); Status.compositionTrailTextureProperty=material.HasProperty("_TrailColor");
            if(!Status.compositionMainTextureProperty||!Status.compositionTrailTextureProperty) throw new InvalidOperationException("Trail composition texture binding contract absent.");
            mesh = new Mesh { name = "Bounded persistent trail parcels" }; mesh.MarkDynamic();
            vertices = new Vector3[Status.capacity * 4]; colors = new Color[vertices.Length];
            parcels=new PersistentTrailParcel[Status.capacity]; order=new int[Status.capacity]; depths=new double[Status.capacity]; ids=new long[Status.capacity]; depthOrder=new DepthOrder(depths,ids);
            var uv = new Vector2[vertices.Length]; var indices = new int[Status.capacity * 6];
            for (int i = 0; i < Status.capacity; i++) { int v = i * 4, t = i * 6; uv[v] = Vector2.zero; uv[v + 1] = Vector2.right; uv[v + 2] = Vector2.one; uv[v + 3] = Vector2.up; indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2; indices[t + 3] = v; indices[t + 4] = v + 2; indices[t + 5] = v + 3; }
            mesh.vertices = vertices; mesh.colors = colors; mesh.uv = uv; mesh.triangles = indices;
            if(recording!=null)
            {
                segmentMesh=new Mesh{name="Bounded measured centerline strips"};segmentMesh.MarkDynamic();segmentVertices=new Vector3[MeasuredTrailSegments.MaxSegments*4];segmentColors=new Color[segmentVertices.Length];
                var su=new Vector2[segmentVertices.Length];var si=new int[MeasuredTrailSegments.MaxSegments*6];
                for(int i=0;i<MeasuredTrailSegments.MaxSegments;i++){int v=i*4,t=i*6;su[v]=Vector2.zero;su[v+1]=Vector2.right;su[v+2]=Vector2.one;su[v+3]=Vector2.up;si[t]=v;si[t+1]=v+1;si[t+2]=v+2;si[t+3]=v;si[t+4]=v+2;si[t+5]=v+3;}
                segmentMesh.vertices=segmentVertices;segmentMesh.colors=segmentColors;segmentMesh.uv=su;segmentMesh.triangles=si;
            }
            commands = new CommandBuffer { name = "Owned persistent trail transparent depth consumer" };
            lastWall = Time.realtimeSinceStartup;
            Refresh();
            if(recording!=null) { Status.model="Artistic particles driven by captured KSP part motion; no observed plume/weather; endpoint interpolation"; Status.recordingPath=recordingPath; Status.recordingSha256=recordingHash; Status.recordingRunId=recording.runId; Status.recordingProcessId=recording.processId; Status.recordingMode=recording.mode; Status.recordingFrames=recording.frames.Length; Status.recordingDuration=recording.frames[recording.frames.Length-1].universalTime-recording.frames[0].universalTime; Status.recordingPresentation="Measured body-fixed worldline; artistic endpoint-interpolated births; immutable captured craft geometry follows root translation only, no recorded deformation/rotation applied";owner.SetEmission(recording.frames[0].emission); UpdateRecordedRoot(); Refresh(); }
        }
        catch { Dispose(); throw; }
        finally { Status.preparationTicks=System.Diagnostics.Stopwatch.GetTimestamp()-preparationStarted; }
    }
    public void Fixture()
    { owner.Reset(); Wind(1,0,0); owner.SetEmission(true); owner.Advance(3); owner.SetEmission(false); Status.paused = true; Refresh(); }
    public void Reset() { owner.Reset(); Status.paused = true; if(recording!=null) { segments.ResetWind();owner.SetWind(0,0,0);owner.SetEmission(recording.frames[0].emission); Status.windX=Status.windY=Status.windZ=0; Status.recordingCursor=0; UpdateRecordedRoot(); } Refresh(); }
    public void Pause() { Status.paused = true; }
    public void SetVisible(bool visible) { Status.visible=visible; }
    public void SetRepresentation(string value)
    {
        if(value!="particles"&&value!="segments")throw new ArgumentException("Trail representation particles|segments required.");
        if(Status.probeInstalled)throw new InvalidOperationException("Turn probe off before changing trail representation.");
        if(value=="segments"&&segments==null)throw new InvalidOperationException("Segments require an admitted measured recording.");
        if(value==Status.representation)return;
        Status.representation=value;Status.renderScope=value=="segments"?"Retained measured centerline strips; cached screen simplification and upload on time/view change, transparent midpoint order; dual actual positive-depth tests; not density conservation":"Reusable bounded billboard mesh; explicit dual scene-depth testing and premultiplied overlay; upload bytes estimate vertex/color payload, not driver allocation";uploadedGeneration=-1;commandsRecorded=false;commands.Clear();HoldClock();
    }
    public void HoldClock() { lastWall = Time.realtimeSinceStartup; }
    public void Play(double rate)
    { if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < .01 || rate > 100) throw new ArgumentException("Trail rate .01..100 required."); Status.playbackRate = rate; Status.paused = false; lastWall = Time.realtimeSinceStartup; }
    public void Step(double seconds) { long started=System.Diagnostics.Stopwatch.GetTimestamp(); try { if(recording==null) owner.Advance(owner.Time + seconds); else AdvanceRecording(owner.Time+seconds); Refresh(); } finally { Status.simulationTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started; } }
    public void SeekRecording(double seconds)
    { if(recording==null||double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0||seconds>Status.recordingDuration) throw new ArgumentException("Recording seek outside measured duration."); Reset(); AdvanceRecording(seconds); Refresh(); }
    void AdvanceRecording(double target)
    {
        if(double.IsNaN(target)||double.IsInfinity(target)||target<owner.Time||target>Status.recordingDuration||target-owner.Time>3600) throw new ArgumentException("Recording advance outside measured duration.");
        while(Status.recordingCursor+1<recording.frames.Length&&owner.Time<target)
        {
            int i=Status.recordingCursor; var a=recording.frames[i]; var b=recording.frames[i+1]; double start=a.universalTime-recording.frames[0].universalTime,end=b.universalTime-recording.frames[0].universalTime;
            if(end<=owner.Time) {Status.recordingCursor++;continue;}
            double next=Math.Min(target,end); var p=RecordedPosition(a.emitterBodyFixed,b.emitterBodyFixed,(owner.Time-start)/(end-start)); var q=RecordedPosition(a.emitterBodyFixed,b.emitterBodyFixed,(next-start)/(end-start));
            owner.AdvanceMeasured(next,p,q,a.emission); if(next==end)Status.recordingCursor++;
        }
        if(owner.Time==Status.recordingDuration)Status.paused=true;
        UpdateRecordedRoot();
    }
    double[] RecordedPosition(double[] a,double[] b,double fraction)
    { return TrailEmitterFormat.Position(recording,new[]{a[0]+(b[0]-a[0])*fraction,a[1]+(b[1]-a[1])*fraction,a[2]+(b[2]-a[2])*fraction}); }
    void UpdateRecordedRoot()
    {
        int i=Status.recordingCursor;var a=recording.frames[i];var b=recording.frames[Math.Min(i+1,recording.frames.Length-1)];double span=b.universalTime-a.universalTime;
        Status.recordedRootScene=RecordedPosition(a.rootBodyFixed,b.rootBodyFixed,span==0?0:(owner.Time+recording.frames[0].universalTime-a.universalTime)/span);Status.recordingUniversalTime=recording.frames[0].universalTime+owner.Time;
    }
    public void Wind(double x, double y, double z) { if(segments!=null)segments.CheckWind(owner.Time,x,y,z);owner.SetWind(x, y, z);if(segments!=null)segments.SetWind(owner.Time,x,y,z); Status.windX=x; Status.windY=y; Status.windZ=z; Refresh(); }
    public void Emission(bool enabled) { if(recording!=null)throw new InvalidOperationException("Recording emission is owned by measured mode/readback.");owner.SetEmission(enabled); Refresh(); }
    public void Probe(bool enabled)
    {
        if(enabled&&Status.representation!="particles")throw new InvalidOperationException("Depth probes retain the qualified particle representation; switch to particles first.");
        if(enabled==Status.probeInstalled) return;
        if(enabled)
        {
            if(probeCraft==null||probeBody==null)
            {
                Texture2D craft=null,body=null;
                try { var recipe=new ProbeData(); craft=DepthMap(recipe.craftRegion); body=DepthMap(recipe.bodyRegion); probeCraft=craft; probeBody=body; }
                catch { if(craft!=null) UnityEngine.Object.Destroy(craft); if(body!=null) UnityEngine.Object.Destroy(body); throw; }
            }
            Status.probe=new ProbeData {craftTextureId=probeCraft.GetInstanceID(),bodyTextureId=probeBody.GetInstanceID(),historyTimeOnInstall=owner.Time,historyGenerationOnInstall=owner.Generation,pausedOnInstall=Status.paused,emissionOnInstall=owner.Emission};
        }
        else Status.probe.historyRestored=owner.Time==Status.probe.historyTimeOnInstall&&owner.Generation==Status.probe.historyGenerationOnInstall&&Status.paused==Status.probe.pausedOnInstall&&owner.Emission==Status.probe.emissionOnInstall;
        Status.probeInstalled=enabled; uploadedGeneration=-1; HoldClock();
    }
    static Texture2D DepthMap(float[] region)
    {
        var values=new float[64*64];
        for(int y=0;y<64;y++) for(int x=0;x<64;x++) { float u=(x+.5f)/64,v=(y+.5f)/64; if(u>=region[0]&&u<region[2]&&v>=region[1]&&v<region[3]) values[y*64+x]=region[4]; }
        var bytes=new byte[values.Length*4]; Buffer.BlockCopy(values,0,bytes,0,bytes.Length);
        var texture=new Texture2D(64,64,TextureFormat.RFloat,false,true) {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,name="Synthetic trail positive depth"};
        try { texture.LoadRawTextureData(bytes); texture.Apply(false,true); return texture; } catch { UnityEngine.Object.Destroy(texture); throw; }
    }
    public void Tick(bool frozen)
    { double now = Time.realtimeSinceStartup, delta = Math.Max(0, Math.Min(.25, now - lastWall)); lastWall = now; if (!frozen && !Status.paused&&!Status.probeInstalled) Step(recording==null?delta*Status.playbackRate:Math.Min(delta*Status.playbackRate,Status.recordingDuration-owner.Time)); }
    void Refresh()
    { Status.simulationTime = owner.Time; Status.live = owner.Count; Status.expired = owner.Expired; Status.capacityEvictions = owner.CapacityEvictions; Status.emitted = owner.Emitted; Status.generation = owner.Generation; Status.emission = owner.Emission;
        if(owner.Count>0) { var a=owner.At(0); var b=owner.At(owner.Count-1); Status.oldestPositionMeters[0]=a.x; Status.oldestPositionMeters[1]=a.y; Status.oldestPositionMeters[2]=a.z; Status.newestPositionMeters[0]=b.x; Status.newestPositionMeters[1]=b.y; Status.newestPositionMeters[2]=b.z; Status.oldestAgeSeconds=a.age; Status.newestAgeSeconds=b.age; }
        else { Array.Clear(Status.oldestPositionMeters,0,3); Array.Clear(Status.newestPositionMeters,0,3); Status.oldestAgeSeconds=Status.newestAgeSeconds=0; }
    }
    public void Render(Camera camera, double[] origin, RenderTexture craftDepth, RenderTexture bodyDepth, int width, int height)
    {
        long renderStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        RenderTexture old=RenderTexture.active;
        try
        {
        long setupStarted=renderStarted;
        if (target == null || target.width != width || target.height != height)
        { ReleaseTarget(); target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point, name = "Trail premultiplied overlay" }; composed = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point, name = "Trail scene composition" }; if (!target.Create() || !composed.Create()) throw new InvalidOperationException("Trail target creation failed."); }
        Quaternion rotation = camera.transform.rotation;
        Mesh activeMesh=Status.representation=="segments"?segmentMesh:mesh;
        bool changed = uploadedGeneration != owner.Generation || rotation.x != lastRotation.x || rotation.y != lastRotation.y || rotation.z != lastRotation.z || rotation.w != lastRotation.w;
        if(Status.probeInstalled||Status.representation=="segments") changed|=lastProbeFov!=camera.fieldOfView||lastProbeAspect!=camera.aspect;
        if(Status.representation=="segments")changed|=Status.segmentNearPlane!=camera.nearClipPlane||Status.segmentViewportWidth!=width||Status.segmentViewportHeight!=height;
        for (int i = 0; i < 3; i++) changed |= lastOrigin[i] != origin[i];
        Matrix4x4 view=camera.worldToCameraMatrix;
        bool actualProbe=Status.probeInstalled&&Status.probe.depthSource=="actual-scene-rendertextures";
        if(actualProbe&&(width!=Status.probe.width||height!=Status.probe.height||camera.fieldOfView!=sceneProbeFov||camera.aspect!=sceneProbeAspect||view!=sceneProbeView)) throw new InvalidOperationException("Actual scene probe view changed; turn probe off before resizing/reframing.");
        if(Status.probeInstalled) { Status.probe.width=width; Status.probe.height=height; Status.probe.actualFov=camera.fieldOfView; Status.probe.actualAspect=camera.aspect; }
        Status.viewSetupTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-setupStarted;
        float far = camera.farClipPlane;
        if (changed)
        {
            long uploadStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;
            if(Status.representation=="segments") UploadSegments(camera,origin,right,up);
            else
            {
            int drawCount=Status.probeInstalled?Status.probe.centerUvDepth.Length/3:owner.Count;
            for(int i=0;i<drawCount;i++)
            {
                if(Status.probeInstalled) { var recipe=Status.probe; float d=recipe.centerUvDepth[i*3+2],half=d*Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f); Vector3 c=rotation*new Vector3((recipe.centerUvDepth[i*3]*2-1)*half*camera.aspect,(recipe.centerUvDepth[i*3+1]*2-1)*half,d); parcels[i]=new PersistentTrailParcel {id=i+1,x=c.x,y=c.y,z=c.z,width=half*recipe.radiusScreenHeight*2,opacity=1}; }
                else parcels[i]=owner.RelativeAt(i,origin);
                order[i]=i; ids[i]=parcels[i].id; depths[i]=-view.MultiplyPoint3x4(new Vector3((float)parcels[i].x,(float)parcels[i].y,(float)parcels[i].z)).z;
                if(Status.probeInstalled) { Status.probe.actualCentersCameraRelative[i*3]=(float)parcels[i].x; Status.probe.actualCentersCameraRelative[i*3+1]=(float)parcels[i].y; Status.probe.actualCentersCameraRelative[i*3+2]=(float)parcels[i].z; Status.probe.actualEyeDepths[i]=(float)depths[i]; }
            }
            Array.Sort(order,0,drawCount,depthOrder);
            for (int i = 0; i < vertices.Length/4; i++)
            {
                int v = i * 4;
                if (i >= drawCount) { for (int j = 0; j < 4; j++) { vertices[v + j] = Vector3.zero; colors[v + j] = Color.clear; } continue; }
                var p = parcels[order[i]]; var center = new Vector3((float)p.x, (float)p.y, (float)p.z);
                Vector3 r = right * (float)p.width, u = up * (float)p.width;
                vertices[v] = center - r - u; vertices[v + 1] = center + r - u; vertices[v + 2] = center + r + u; vertices[v + 3] = center - r + u;
                Color c = Status.probeInstalled?new Color(Status.probe.rgba[order[i]*4],Status.probe.rgba[order[i]*4+1],Status.probe.rgba[order[i]*4+2],1):new Color(.8f, .85f, 1, (float)p.opacity * .35f); for (int j = 0; j < 4; j++) colors[v + j] = c;
            }
            }
            activeMesh.vertices = Status.representation=="segments"?segmentVertices:vertices; activeMesh.colors = Status.representation=="segments"?segmentColors:colors; activeMesh.RecalculateBounds();
            Status.uploadCount++; Status.uploadBytes += (Status.representation=="segments"?segmentVertices.Length:vertices.Length) * (12 + 16);
            uploadedGeneration = owner.Generation; lastRotation = rotation; Array.Copy(origin, lastOrigin, 3);
            lastProbeFov=camera.fieldOfView; lastProbeAspect=camera.aspect;
            }
            finally { Status.uploadTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-uploadStarted; }
        }
        setupStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        far = Math.Max(far, activeMesh.bounds.center.magnitude + activeMesh.bounds.extents.magnitude + 1);
        Matrix4x4 projection = GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, .001f, far), true);
        if(Status.probeInstalled) for(int r=0;r<4;r++) for(int c=0;c<4;c++) { Status.probe.actualGpuProjection[r*4+c]=projection[r,c]; Status.probe.actualView[r*4+c]=view[r,c]; }
        if(actualProbe) { Status.probe.craftTextureId=craftDepth.GetInstanceID(); Status.probe.bodyTextureId=bodyDepth.GetInstanceID(); }
        material.SetMatrix("_TrailVP", projection * view); material.SetMatrix("_TrailView", view); material.SetTexture("_CraftDepth", Status.probeInstalled&&!actualProbe?(Texture)probeCraft:craftDepth); material.SetTexture("_BodyDepth", Status.probeInstalled&&!actualProbe?(Texture)probeBody:bodyDepth);
        material.SetFloat("_TrailStrip",Status.representation=="segments"?1:0);
        if(Status.representation=="segments") for(int r=0;r<4;r++)for(int c=0;c<4;c++){Status.segmentView[r*4+c]=view[r,c];Status.segmentGpuProjection[r*4+c]=projection[r,c];}
        if(!commandsRecorded) { commands.Clear(); commands.SetRenderTarget(target); commands.SetViewport(new Rect(0,0,width,height)); commands.ClearRenderTarget(false,true,Color.clear); commands.DrawMesh(activeMesh,Matrix4x4.identity,material,0,0); commandsRecorded=true; Status.commandBuilds++; }
        Status.viewSetupTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-setupStarted;
        long before = System.Diagnostics.Stopwatch.GetTimestamp();
        try { Graphics.ExecuteCommandBuffer(commands); Status.renderedFrames++; }
        finally { Status.submissionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - before; }
        }
        finally { RenderTexture.active=old; Status.renderTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-renderStarted; }
    }
    void UploadSegments(Camera camera,double[] origin,Vector3 right,Vector3 up)
    {
        Vector3 forward=camera.transform.forward;
        segments.Build(owner.Time,Status.lifetimeSeconds,Status.initialWidthMeters,Status.diffusionSquareMetersPerSecond,Status.decayPerSecond,origin,new double[]{right.x,right.y,right.z},new double[]{up.x,up.y,up.z},new double[]{forward.x,forward.y,forward.z},target.height/(2*Math.Tan(camera.fieldOfView*Math.PI/360)),camera.nearClipPlane,Status.segmentErrorPixels);
        Status.segmentInputSpans=segments.InputSpans;Status.segmentDraws=segments.Count;Status.segmentRetainedPoints=segments.RetainedPoints;Status.segmentNearClippedSpans=segments.NearClippedSpans;Status.segmentGapSpans=segments.GapSpans;Status.segmentRemovedErrorPixels=segments.MaximumRemovedErrorPixels;Status.segmentNearPlane=camera.nearClipPlane;Status.segmentViewportWidth=target.width;Status.segmentViewportHeight=target.height;Status.segmentBuilds++;Array.Copy(origin,Status.segmentCameraOrigin,3);
        Status.segmentWindChanges=segments.WindChanges;
        Status.segmentRemovedDepthErrorMeters=segments.MaximumRemovedDepthErrorMeters;Status.segmentRemovedDepthErrorRatio=segments.MaximumRemovedDepthErrorRatio;
        Array.Clear(Status.segmentEndpointsScene,0,12);Array.Clear(Status.segmentEndpointTimes,0,4);
        Status.segmentGeometry=new double[segments.Count*13];
        for(int i=0;i<segments.Count;i++)
        {
            var s=segments.Segments[i];int o=i*13;var g=Status.segmentGeometry;
            g[o]=s.a.x;g[o+1]=s.a.y;g[o+2]=s.a.z;g[o+3]=s.b.x;g[o+4]=s.b.y;g[o+5]=s.b.z;g[o+6]=s.a.time;g[o+7]=s.b.time;g[o+8]=s.a.width;g[o+9]=s.b.width;g[o+10]=s.a.opacity;g[o+11]=s.b.opacity;g[o+12]=s.run;
            segmentOrder[i]=i;segmentIds[i]=i;segmentDepths[i]=((s.a.x+s.b.x)*.5-origin[0])*forward.x+((s.a.y+s.b.y)*.5-origin[1])*forward.y+((s.a.z+s.b.z)*.5-origin[2])*forward.z;
        }
        Array.Sort(segmentOrder,0,segments.Count,segmentDepthOrder);
        if(segments.Count>0)
        {
            var a=segments.Segments[0];var b=segments.Segments[segments.Count-1];var g=Status.segmentEndpointsScene;
            g[0]=a.a.x;g[1]=a.a.y;g[2]=a.a.z;g[3]=a.b.x;g[4]=a.b.y;g[5]=a.b.z;g[6]=b.a.x;g[7]=b.a.y;g[8]=b.a.z;g[9]=b.b.x;g[10]=b.b.y;g[11]=b.b.z;
            Status.segmentEndpointTimes[0]=a.a.time;Status.segmentEndpointTimes[1]=a.b.time;Status.segmentEndpointTimes[2]=b.a.time;Status.segmentEndpointTimes[3]=b.b.time;
        }
        for(int i=0;i<MeasuredTrailSegments.MaxSegments;i++)
        {
            int v=i*4;if(i>=segments.Count){for(int j=0;j<4;j++){segmentVertices[v+j]=Vector3.zero;segmentColors[v+j]=Color.clear;}continue;}
            var s=segments.Segments[segmentOrder[i]];var a=new Vector3((float)(s.a.x-origin[0]),(float)(s.a.y-origin[1]),(float)(s.a.z-origin[2]));var b=new Vector3((float)(s.b.x-origin[0]),(float)(s.b.y-origin[1]),(float)(s.b.z-origin[2]));
            Vector3 side=Vector3.Cross(forward,b-a).normalized;if(side.sqrMagnitude==0)side=right;
            segmentVertices[v]=a-side*(float)s.a.width;segmentVertices[v+1]=b-side*(float)s.b.width;segmentVertices[v+2]=b+side*(float)s.b.width;segmentVertices[v+3]=a+side*(float)s.a.width;
            segmentColors[v]=segmentColors[v+3]=new Color(.8f,.85f,1,(float)s.a.opacity*.35f);segmentColors[v+1]=segmentColors[v+2]=new Color(.8f,.85f,1,(float)s.b.opacity*.35f);
        }
    }
    public void Compose(RenderTexture source, RenderTexture destination, Material worldComposite)
    { long started=System.Diagnostics.Stopwatch.GetTimestamp(); RenderTexture old=RenderTexture.active; try { if(!Status.visible||target==null||composed==null) { Graphics.Blit(source,destination,worldComposite); return; } if(Status.probeInstalled&&Status.probe.depthSource=="synthetic-texture2d") Graphics.Blit(source,composed,material,2); else Graphics.Blit(source,composed,worldComposite); material.SetTexture("_MainTex",composed); material.SetTexture("_TrailColor", target); Graphics.Blit(composed, destination, material, 1); } finally { RenderTexture.active=old; Status.compositionTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started; } }
    void ReleaseTarget() { commandsRecorded=false; if(commands!=null) commands.Clear(); if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); target = null; } if(composed!=null) { composed.Release(); UnityEngine.Object.Destroy(composed); composed=null; } }
    public void Dispose()
    { if (disposed) return; disposed = true; Status.probeInstalled=false; if(probeCraft!=null) UnityEngine.Object.Destroy(probeCraft); if(probeBody!=null) UnityEngine.Object.Destroy(probeBody); ReleaseTarget(); if (commands != null) commands.Release(); if (mesh != null) UnityEngine.Object.Destroy(mesh); if(segmentMesh!=null)UnityEngine.Object.Destroy(segmentMesh); if (material != null) UnityEngine.Object.Destroy(material); Status.cleanupStatus = "owned-resources-destruction-requested"; Status.status = "disposed"; }
}
