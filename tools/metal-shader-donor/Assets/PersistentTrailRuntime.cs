using System;
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
        public bool paused = true, emission;
        public int live, capacity = 1024, uploadCount;
        public long expired, capacityEvictions, emitted, generation, uploadBytes, renderedFrames;
        public long submissionTicks, commandBuilds;
        public string renderScope = "Reusable bounded billboard mesh; explicit dual scene-depth testing and premultiplied overlay; upload bytes estimate vertex/color payload, not driver allocation";
    }
    public readonly StatusData Status = new StatusData();
    readonly PersistentTrailOwner owner;
    readonly Mesh mesh;
    readonly Material material;
    readonly CommandBuffer commands;
    readonly Vector3[] vertices;
    readonly Color[] colors;
    long uploadedGeneration = -1;
    readonly double[] lastOrigin = new double[3];
    Quaternion lastRotation;
    RenderTexture target, composed;
    double lastWall;
    bool disposed;
    public PersistentTrailRuntime(double[] anchor)
    {
        owner = new PersistentTrailOwner(Status.capacity, new[] { anchor[0] + 3, anchor[1], anchor[2] }, new[] { 0.0, 0.0, -8.0 });
        try
        {
            Shader shader = Shader.Find("Hidden/Continuum/PersistentTrail");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Persistent trail shader unavailable.");
            material = new Material(shader); mesh = new Mesh { name = "Bounded persistent trail parcels" }; mesh.MarkDynamic();
            vertices = new Vector3[Status.capacity * 4]; colors = new Color[vertices.Length];
            var uv = new Vector2[vertices.Length]; var indices = new int[Status.capacity * 6];
            for (int i = 0; i < Status.capacity; i++) { int v = i * 4, t = i * 6; uv[v] = Vector2.zero; uv[v + 1] = Vector2.right; uv[v + 2] = Vector2.one; uv[v + 3] = Vector2.up; indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2; indices[t + 3] = v; indices[t + 4] = v + 2; indices[t + 5] = v + 3; }
            mesh.vertices = vertices; mesh.colors = colors; mesh.uv = uv; mesh.triangles = indices;
            commands = new CommandBuffer { name = "Owned persistent trail transparent depth consumer" };
            lastWall = Time.realtimeSinceStartup;
            Refresh();
        }
        catch { Dispose(); throw; }
    }
    public void Fixture()
    { owner.Reset(); Wind(1,0,0); owner.SetEmission(true); owner.Advance(3); owner.SetEmission(false); Status.paused = true; Refresh(); }
    public void Reset() { owner.Reset(); Status.paused = true; Refresh(); }
    public void Pause() { Status.paused = true; }
    public void HoldClock() { lastWall = Time.realtimeSinceStartup; }
    public void Play(double rate)
    { if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < .01 || rate > 100) throw new ArgumentException("Trail rate .01..100 required."); Status.playbackRate = rate; Status.paused = false; lastWall = Time.realtimeSinceStartup; }
    public void Step(double seconds) { owner.Advance(owner.Time + seconds); Refresh(); }
    public void Wind(double x, double y, double z) { owner.SetWind(x, y, z); Status.windX=x; Status.windY=y; Status.windZ=z; Refresh(); }
    public void Emission(bool enabled) { owner.SetEmission(enabled); Refresh(); }
    public void Tick(bool frozen)
    { double now = Time.realtimeSinceStartup, delta = Math.Max(0, Math.Min(.25, now - lastWall)); lastWall = now; if (!frozen && !Status.paused) Step(delta * Status.playbackRate); }
    void Refresh()
    { Status.simulationTime = owner.Time; Status.live = owner.Count; Status.expired = owner.Expired; Status.capacityEvictions = owner.CapacityEvictions; Status.emitted = owner.Emitted; Status.generation = owner.Generation; Status.emission = owner.Emission;
        if(owner.Count>0) { var a=owner.At(0); var b=owner.At(owner.Count-1); Status.oldestPositionMeters[0]=a.x; Status.oldestPositionMeters[1]=a.y; Status.oldestPositionMeters[2]=a.z; Status.newestPositionMeters[0]=b.x; Status.newestPositionMeters[1]=b.y; Status.newestPositionMeters[2]=b.z; Status.oldestAgeSeconds=a.age; Status.newestAgeSeconds=b.age; }
        else { Array.Clear(Status.oldestPositionMeters,0,3); Array.Clear(Status.newestPositionMeters,0,3); Status.oldestAgeSeconds=Status.newestAgeSeconds=0; }
    }
    public void Render(Camera camera, double[] origin, RenderTexture craftDepth, RenderTexture bodyDepth, int width, int height)
    {
        if (target == null || target.width != width || target.height != height)
        { ReleaseTarget(); target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point, name = "Trail premultiplied overlay" }; composed = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point, name = "Trail scene composition" }; if (!target.Create() || !composed.Create()) throw new InvalidOperationException("Trail target creation failed."); }
        Quaternion rotation = camera.transform.rotation;
        bool changed = uploadedGeneration != owner.Generation || rotation != lastRotation;
        for (int i = 0; i < 3; i++) changed |= lastOrigin[i] != origin[i];
        float far = camera.farClipPlane;
        if (changed)
        {
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;
            for (int i = 0; i < Status.capacity; i++)
            {
                int v = i * 4;
                if (i >= owner.Count) { for (int j = 0; j < 4; j++) { vertices[v + j] = Vector3.zero; colors[v + j] = Color.clear; } continue; }
                var p = owner.RelativeAt(i, origin); var center = new Vector3((float)p.x, (float)p.y, (float)p.z);
                Vector3 r = right * (float)p.width, u = up * (float)p.width;
                vertices[v] = center - r - u; vertices[v + 1] = center + r - u; vertices[v + 2] = center + r + u; vertices[v + 3] = center - r + u;
                Color c = new Color(.8f, .85f, 1, (float)p.opacity * .35f); for (int j = 0; j < 4; j++) colors[v + j] = c;
            }
            mesh.vertices = vertices; mesh.colors = colors; mesh.RecalculateBounds();
            Status.uploadCount++; Status.uploadBytes += vertices.Length * (12 + 16);
            uploadedGeneration = owner.Generation; lastRotation = rotation; Array.Copy(origin, lastOrigin, 3);
        }
        far = Math.Max(far, mesh.bounds.center.magnitude + mesh.bounds.extents.magnitude + 1);
        Matrix4x4 view = camera.worldToCameraMatrix, projection = GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, .001f, far), true);
        material.SetMatrix("_TrailVP", projection * view); material.SetMatrix("_TrailView", view); material.SetTexture("_CraftDepth", craftDepth); material.SetTexture("_BodyDepth", bodyDepth);
        RenderTexture old = RenderTexture.active;
        long before = System.Diagnostics.Stopwatch.GetTimestamp();
        try { commands.Clear(); commands.SetRenderTarget(target); commands.SetViewport(new Rect(0, 0, width, height)); commands.ClearRenderTarget(false, true, Color.clear); commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0); Status.commandBuilds++; Graphics.ExecuteCommandBuffer(commands); Status.renderedFrames++; }
        finally { RenderTexture.active = old; Status.submissionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - before; }
    }
    public void Compose(RenderTexture source, RenderTexture destination, Material worldComposite)
    { if(target==null||composed==null) { Graphics.Blit(source,destination,worldComposite); return; } RenderTexture old=RenderTexture.active; try { Graphics.Blit(source,composed,worldComposite); material.SetTexture("_TrailColor", target); Graphics.Blit(composed, destination, material, 1); } finally { RenderTexture.active=old; } }
    void ReleaseTarget() { if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); target = null; } if(composed!=null) { composed.Release(); UnityEngine.Object.Destroy(composed); composed=null; } }
    public void Dispose()
    { if (disposed) return; disposed = true; ReleaseTarget(); if (commands != null) commands.Release(); if (mesh != null) UnityEngine.Object.Destroy(mesh); if (material != null) UnityEngine.Object.Destroy(material); Status.cleanupStatus = "owned-resources-destruction-requested"; Status.status = "disposed"; }
}
