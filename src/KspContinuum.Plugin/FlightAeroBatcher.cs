using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal sealed class FlightAeroBatcher
    {
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-aero-batch/v1", runId, vesselId, status, reason;
            public string fidelity = "Experimental opaque FX geometry packet. Stock shaders/cameras/globals retained; per-source material/property-block inputs and object-space shader behavior require image qualification. No visual equivalence claim.";
            public string channelContract = "Vertex values retained through initial root-space position/normal/tangent conversion; UV dimensions and channel presence retained, colors read as float values. One rigid source-transform bone per segment.";
            public string counterScope = "Camera callbacks since Start through Finish, including capture warmup and cleanup; native skinning and render leases run inside the measured frame workload.";
            public string scaleContract = "Exact authored localScale and parent identity checked for unique source and proxy-frame ancestors through their actual scene roots, including detached physical-part hierarchies; derived lossyScale is not a pose invariant. Nonuniform hierarchy scales with animated rotations may introduce shear; native skinned normals require image qualification.";
            public int processId, eligibleRenderers, fallbackRenderers, vertices, triangles, batches, faults, cameraInvocations;
            public int propertyBlockSources, destroyedProxies, restoredOriginals;
            public int scaleOwners, nonuniformScaleOwners;
            public int linkedMeshDeformationRoots;
            public int retainedSourceCameraVisits, sharedBoundsReads;
            public double preparationMilliseconds, maxInitialPositionErrorMeters, maxInitialNormalError;
            public string cleanupStatus = "not-started";
            public Fallback[] fallbacks;
            public CameraCount[] cameras;
        }
        [Serializable] internal sealed class Fallback { public string reason; public int count; }
        [Serializable] internal sealed class CameraCount { public string role, name; public int instanceId, invocations; }
        sealed class Source
        {
            public MeshRenderer Renderer; public Mesh Mesh; public Transform Bone; public Vector3 Scale;
            public string Key, Identity; public int[] UvDimensions; public bool Color, Tangent;
        }
        sealed class Packet
        {
            public GameObject Object; public Mesh Mesh; public SkinnedMeshRenderer Renderer;
            public readonly List<Source> Sources = new List<Source>();
        }
        sealed class ScaleOwner
        {
            public Transform Transform, Parent; public Vector3 LocalScale; public string Identity;
        }
        static FlightAeroBatcher current;
        public static bool IsActive { get { return current != null; } }
        public static bool activeRequested { get { return Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-flight-aero-batch") >= 0; } }
        public static string FailureReason { get { return current != null && current.faulted ? current.report.reason : null; } }
        internal static bool Covers(MeshRenderer renderer)
        {
            if (current == null || current.faulted || current.finished || current.cameras.Count != 3) return false;
            foreach (Camera camera in current.cameras.Keys) if (camera == null || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) return false;
            foreach (Source source in current.sources) if (source.Renderer == renderer) return true;
            return false;
        }
        readonly List<Source> sources = new List<Source>();
        readonly List<Packet> packets = new List<Packet>();
        readonly Dictionary<string,int> fallback = new Dictionary<string,int>();
        readonly Dictionary<Camera,CameraCount> cameras = new Dictionary<Camera,CameraCount>();
        readonly HashSet<uint> parts = new HashSet<uint>();
        readonly List<MeshRenderer> suppressed = new List<MeshRenderer>();
        readonly Dictionary<Transform,ScaleOwner> scaleOwners = new Dictionary<Transform,ScaleOwner>();
        readonly HashSet<Transform> deformationRoots = new HashSet<Transform>();
        Vessel vessel; string directory; Report report; Camera leasedCamera;
        bool cameraPreAttached, cameraPostAttached, modifiedAttached, diedAttached, sceneAttached, faulted, finished;
        int boundsFrame = -1;

        public static Report Start(Vessel vessel, string directory, string runId)
        {
            if (!activeRequested) return null;
            return StartExplicit(vessel, directory, runId);
        }
        public static Report StartExplicit(Vessel vessel, string directory, string runId)
        {
            if (current != null) throw new InvalidOperationException("Aero batch owner already exists.");
            current = new FlightAeroBatcher { vessel = vessel, directory = directory,
                report = new Report { runId = runId, processId = Process.GetCurrentProcess().Id, status = "preparing",
                    vesselId = vessel == null ? null : vessel.id.ToString("D") } };
            try { current.Prepare(); }
            catch (Exception error) { current.Fault("preparation: " + error.GetType().Name + ": " + error.Message); }
            return current.report;
        }
        public static Report Finish()
        {
            if (current == null) return null;
            FlightAeroBatcher owner = current; current = null; owner.Cleanup(); return owner.report;
        }
        void Decline(string reason) { int count; fallback.TryGetValue(reason, out count); fallback[reason] = count + 1; report.fallbackRenderers++; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static bool Finite(Vector3 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z); }
        static bool Finite(Vector4 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w); }
        static string ChannelKey(Mesh mesh, int[] dimensions)
        {
            string key = "";
            for (int i = 0; i < 8; i++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + i);
                dimensions[i] = mesh.HasVertexAttribute(attribute) ? mesh.GetVertexAttributeDimension(attribute) : 0;
                if (dimensions[i] == 1) throw new InvalidOperationException("one-dimensional-uv");
                key += "/" + dimensions[i];
            }
            return key;
        }
        void RegisterCamera(string role, Camera camera)
        {
            if (camera != null && !cameras.ContainsKey(camera)) cameras.Add(camera, new CameraCount { role = role, name = camera.name, instanceId = camera.GetInstanceID() });
        }
        static string Identity(Transform transform) { return transform.name + "#" + transform.GetInstanceID(); }
        void CacheScaleOwners(Transform transform)
        {
            Transform owner = transform;
            while (owner != null)
            {
                if (scaleOwners.ContainsKey(owner)) break;
                Vector3 scale = owner.localScale;
                if (!Finite(scale) || scale.x == 0 || scale.y == 0 || scale.z == 0)
                    throw new InvalidOperationException("Invalid authored scale: " + Identity(owner));
                scaleOwners.Add(owner, new ScaleOwner { Transform = owner, Parent = owner.parent, LocalScale = scale, Identity = Identity(owner) });
                if (Mathf.Abs(scale.x) != Mathf.Abs(scale.y) || Mathf.Abs(scale.x) != Mathf.Abs(scale.z)) report.nonuniformScaleOwners++;
                owner = owner.parent;
            }
        }
        void Prepare()
        {
            Stopwatch timer = Stopwatch.StartNew();
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.parts == null) throw new InvalidOperationException("Loaded physical vessel required.");
            CacheScaleOwners(vessel.transform);
            foreach (Part part in vessel.parts) if (part != null)
            {
                parts.Add(part.flightID);
                foreach (CompoundParts.CModuleLinkedMesh module in part.GetComponents<CompoundParts.CModuleLinkedMesh>())
                    if (module != null && module.line != null) deformationRoots.Add(module.line);
            }
            report.linkedMeshDeformationRoots = deformationRoots.Count;
            var seen = new HashSet<Renderer>();
            foreach (Part part in vessel.parts) if (part != null)
            {
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !seen.Add(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    bool deforming = false;
                    for (Transform ancestor = renderer.transform; ancestor != null; ancestor = ancestor.parent)
                        if (deformationRoots.Contains(ancestor)) { deforming = true; break; }
                    if (deforming) { Decline("stock-linked-mesh-deformation"); continue; }
                    var meshRenderer = renderer as MeshRenderer;
                    if (meshRenderer == null) { Decline("non-mesh-renderer"); continue; }
                    if (renderer.forceRenderingOff) { Decline("already-force-rendering-off"); continue; }
                    var filter = renderer.GetComponent<MeshFilter>(); Mesh mesh = filter == null ? null : filter.sharedMesh;
                    if (mesh == null || !mesh.isReadable) { Decline("missing-or-unreadable-mesh"); continue; }
                    Material[] materials = renderer.sharedMaterials;
                    if (materials.Length != 1 || materials[0] == null || mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles)
                    { Decline("not-single-material-triangle-submesh"); continue; }
                    if (materials[0].GetTag("RenderType", false, "") != "Opaque") { Decline("non-opaque-render-type"); continue; }
                    if (mesh.vertexCount > 60000 || mesh.vertexCount < 1 || mesh.normals.Length != mesh.vertexCount || mesh.blendShapeCount != 0)
                    { Decline("unsupported-vertex-layout"); continue; }
                    var source = new Source { Renderer = meshRenderer, Mesh = mesh, Bone = renderer.transform, Scale = renderer.transform.lossyScale,
                        Identity = Identity(renderer.transform),
                        UvDimensions = new int[8], Color = mesh.HasVertexAttribute(VertexAttribute.Color), Tangent = mesh.HasVertexAttribute(VertexAttribute.Tangent) };
                    Part actualOwner = renderer.GetComponentInParent<Part>();
                    if (actualOwner != null) source.Identity += " part=" + actualOwner.flightID;
                    if (!Finite(source.Scale) || Mathf.Abs(source.Scale.x * source.Scale.y * source.Scale.z) < 1e-8f)
                    { Decline("singular-transform"); continue; }
                    if ((vessel.transform.worldToLocalMatrix * source.Bone.localToWorldMatrix).determinant < 0)
                    { Decline("mirrored-transform"); continue; }
                    try { source.Key = ChannelKey(mesh, source.UvDimensions); }
                    catch (InvalidOperationException) { Decline("one-dimensional-uv"); continue; }
                    source.Key += "/" + source.Color + "/" + source.Tangent + "/" + renderer.gameObject.layer + "/" +
                        renderer.shadowCastingMode + "/" + renderer.receiveShadows + "/" + renderer.motionVectorGenerationMode + "/" +
                        renderer.lightProbeUsage + "/" + renderer.reflectionProbeUsage + "/" + materials[0].renderQueue;
                    CacheScaleOwners(source.Bone); sources.Add(source); if (renderer.HasPropertyBlock()) report.propertyBlockSources++;
                }
            }
            FXCamera fx = UnityEngine.Object.FindObjectOfType<FXCamera>(); FXDepthCamera depth = UnityEngine.Object.FindObjectOfType<FXDepthCamera>();
            if (fx != null)
            {
                RegisterCamera("fx", fx.GetComponent<Camera>());
                FieldInfo field = typeof(FXCamera).GetField("velocityCam", BindingFlags.Instance | BindingFlags.NonPublic);
                RegisterCamera("velocity-occlusion", field == null ? null : field.GetValue(fx) as Camera);
            }
            if (depth != null) RegisterCamera("fx-depth", depth.depthCamera);
            if (cameras.Count != 3 || sources.Count == 0) throw new InvalidOperationException("Three stock aero cameras and eligible geometry required.");
            var groups = new Dictionary<string,List<Source>>();
            foreach (Source source in sources) { List<Source> group; if (!groups.TryGetValue(source.Key, out group)) { group = new List<Source>(); groups.Add(source.Key, group); } group.Add(source); }
            foreach (List<Source> group in groups.Values)
            {
                var chunk = new List<Source>(); int vertices = 0;
                foreach (Source source in group)
                {
                    if (chunk.Count == 128 || vertices + source.Mesh.vertexCount > 60000) { Build(chunk); chunk.Clear(); vertices = 0; }
                    chunk.Add(source); vertices += source.Mesh.vertexCount;
                }
                if (chunk.Count > 0) Build(chunk);
            }
            report.eligibleRenderers = sources.Count; report.batches = packets.Count; report.scaleOwners = scaleOwners.Count; report.preparationMilliseconds = timer.Elapsed.TotalMilliseconds;
            Camera.onPreCull += PreCull; cameraPreAttached = true;
            Camera.onPostRender += PostRender; cameraPostAttached = true;
            GameEvents.onVesselWasModified.Add(Modified); modifiedAttached = true;
            GameEvents.onPartDie.Add(Died); diedAttached = true;
            GameEvents.onGameSceneLoadRequested.Add(Scene); sceneAttached = true;
            report.status = "installed-experimental"; report.cleanupStatus = "owned-hooks-active";
            Write();
        }
        void Build(List<Source> chunk)
        {
            var packet = new Packet { Object = new GameObject("Continuum opaque FX packet") }; packets.Add(packet);
            packet.Object.transform.SetParent(vessel.transform, false); packet.Object.layer = chunk[0].Renderer.gameObject.layer;
            packet.Renderer = packet.Object.AddComponent<SkinnedMeshRenderer>();
            packet.Renderer.forceRenderingOff = true; packet.Renderer.enabled = true;
            var positions = new List<Vector3>(); var normals = new List<Vector3>(); var tangents = new List<Vector4>(); var colors = new List<Color>();
            var indices = new List<int>(); var weights = new List<BoneWeight>(); var bones = new List<Transform>(); var binds = new List<Matrix4x4>();
            var uv = new List<Vector4>[8]; for (int i = 0; i < 8; i++) uv[i] = new List<Vector4>();
            Bounds bounds = new Bounds(); bool boundsSet = false;
            foreach (Source source in chunk)
            {
                int bone = bones.Count, offset = positions.Count; bones.Add(source.Bone);
                binds.Add(source.Bone.worldToLocalMatrix * packet.Object.transform.localToWorldMatrix);
                Matrix4x4 transform = packet.Object.transform.worldToLocalMatrix * source.Bone.localToWorldMatrix;
                Matrix4x4 normalTransform = transform.inverse.transpose;
                Vector3[] vertices = source.Mesh.vertices, sourceNormals = source.Mesh.normals;
                Vector4[] sourceTangents = source.Tangent ? source.Mesh.tangents : null; Color[] sourceColors = source.Color ? source.Mesh.colors : null;
                if (source.Tangent && sourceTangents.Length != vertices.Length || source.Color && sourceColors.Length != vertices.Length)
                    throw new InvalidOperationException("Incomplete tangent/color channel.");
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 position = transform.MultiplyPoint3x4(vertices[i]), normal = normalTransform.MultiplyVector(sourceNormals[i]);
                    if (!Finite(position) || !Finite(normal) || normal.sqrMagnitude < 1e-12f) throw new InvalidOperationException("Nonfinite or degenerate geometry.");
                    normal.Normalize(); positions.Add(position); normals.Add(normal);
                    Vector3 expected = source.Bone.localToWorldMatrix.MultiplyPoint3x4(vertices[i]);
                    double error = (packet.Object.transform.localToWorldMatrix.MultiplyPoint3x4(position) - expected).magnitude;
                    report.maxInitialPositionErrorMeters = Math.Max(report.maxInitialPositionErrorMeters, error);
                    Vector3 expectedNormal = source.Bone.worldToLocalMatrix.transpose.MultiplyVector(sourceNormals[i]).normalized;
                    Vector3 actualNormal = packet.Object.transform.worldToLocalMatrix.transpose.MultiplyVector(normal).normalized;
                    report.maxInitialNormalError = Math.Max(report.maxInitialNormalError, (actualNormal - expectedNormal).magnitude);
                    if (!boundsSet) { bounds = new Bounds(position, Vector3.zero); boundsSet = true; } else bounds.Encapsulate(position);
                    weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
                    if (source.Tangent)
                    {
                        Vector4 original = sourceTangents[i]; Vector3 tangent = transform.MultiplyVector(new Vector3(original.x, original.y, original.z)).normalized;
                        Vector4 value = new Vector4(tangent.x, tangent.y, tangent.z, original.w * (transform.determinant < 0 ? -1 : 1));
                        if (!Finite(value)) throw new InvalidOperationException("Nonfinite tangent."); tangents.Add(value);
                    }
                    if (source.Color) { Color color = sourceColors[i]; if (!Finite(new Vector4(color.r,color.g,color.b,color.a))) throw new InvalidOperationException("Nonfinite color."); colors.Add(color); }
                }
                for (int channel = 0; channel < 8; channel++) if (source.UvDimensions[channel] != 0)
                {
                    var values = new List<Vector4>(); source.Mesh.GetUVs(channel, values);
                    if (values.Count != vertices.Length) throw new InvalidOperationException("Incomplete UV channel.");
                    foreach (Vector4 value in values) { if (!Finite(value)) throw new InvalidOperationException("Nonfinite UV."); uv[channel].Add(value); }
                }
                int[] triangles = source.Mesh.GetTriangles(0);
                if (triangles.Length % 3 != 0) throw new InvalidOperationException("Incomplete triangle.");
                foreach (int index in triangles) { if (index < 0 || index >= vertices.Length) throw new InvalidOperationException("Triangle index out of bounds."); indices.Add(index + offset); }
                packet.Sources.Add(source);
            }
            if (report.maxInitialPositionErrorMeters > .001 || report.maxInitialNormalError > .0001)
                throw new InvalidOperationException("Initial proxy geometry proof failed.");
            var mesh = new Mesh { name = "Continuum opaque FX packet" }; packet.Mesh = mesh;
            mesh.SetVertices(positions); mesh.SetNormals(normals); if (tangents.Count > 0) mesh.SetTangents(tangents); if (colors.Count > 0) mesh.SetColors(colors);
            for (int channel = 0; channel < 8; channel++)
            {
                int dimension = chunk[0].UvDimensions[channel];
                if (dimension == 2) { var values = new List<Vector2>(); foreach (Vector4 value in uv[channel]) values.Add(new Vector2(value.x,value.y)); mesh.SetUVs(channel,values); }
                else if (dimension == 3) { var values = new List<Vector3>(); foreach (Vector4 value in uv[channel]) values.Add(new Vector3(value.x,value.y,value.z)); mesh.SetUVs(channel,values); }
                else if (dimension == 4) mesh.SetUVs(channel,uv[channel]);
            }
            mesh.SetTriangles(indices, 0, false); mesh.boneWeights = weights.ToArray(); mesh.bindposes = binds.ToArray(); mesh.bounds = bounds;
            packet.Renderer.sharedMesh = mesh; packet.Renderer.bones = bones.ToArray(); packet.Renderer.rootBone = vessel.transform;
            packet.Renderer.sharedMaterial = chunk[0].Renderer.sharedMaterial; packet.Renderer.localBounds = bounds;
            packet.Renderer.updateWhenOffscreen = true; packet.Renderer.quality = SkinQuality.Bone1;
            packet.Renderer.shadowCastingMode = chunk[0].Renderer.shadowCastingMode; packet.Renderer.receiveShadows = chunk[0].Renderer.receiveShadows;
            packet.Renderer.motionVectorGenerationMode = chunk[0].Renderer.motionVectorGenerationMode;
            packet.Renderer.lightProbeUsage = chunk[0].Renderer.lightProbeUsage; packet.Renderer.reflectionProbeUsage = chunk[0].Renderer.reflectionProbeUsage;
            report.vertices += positions.Count; report.triangles += indices.Count / 3;
        }
        void UpdateBounds()
        {
            if (boundsFrame == Time.frameCount) return; boundsFrame = Time.frameCount;
            foreach (ScaleOwner owner in scaleOwners.Values)
            {
                if (owner.Transform == null) throw new InvalidOperationException("Scale owner destroyed: " + owner.Identity);
                if (owner.Transform.parent != owner.Parent) throw new InvalidOperationException("Hierarchy changed: " + owner.Identity);
                Vector3 actual = owner.Transform.localScale, expected = owner.LocalScale;
                if (actual.x != expected.x || actual.y != expected.y || actual.z != expected.z)
                    throw new InvalidOperationException("Authored localScale changed: " + owner.Identity + " expected=" + expected.ToString("R") + " actual=" + actual.ToString("R"));
            }
            foreach (Packet packet in packets)
            {
                bool set = false; Bounds bounds = new Bounds();
                foreach (Source source in packet.Sources)
                {
                    if (source.Renderer == null) throw new InvalidOperationException("Source renderer destroyed: " + source.Identity);
                    if (!RetainedRenderSources.Visible(source.Renderer)) throw new InvalidOperationException("Source renderer disabled: " + source.Identity);
                    if (source.Renderer.forceRenderingOff) throw new InvalidOperationException("Source renderer externally forced off: " + source.Identity);
                    if (!source.Renderer.gameObject.activeInHierarchy) throw new InvalidOperationException("Source hierarchy inactive: " + source.Identity);
                    bool retained = RetainedRenderSources.IsRetired(source.Renderer);
                    Bounds world = retained ? RetainedRenderSources.Bounds(source.Renderer) : source.Renderer.bounds;
                    if (retained) report.sharedBoundsReads++;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 point = world.center + Vector3.Scale(world.extents, new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1));
                        point = packet.Object.transform.InverseTransformPoint(point);
                        if (!set) { bounds = new Bounds(point, Vector3.zero); set = true; } else bounds.Encapsulate(point);
                    }
                }
                packet.Renderer.localBounds = bounds;
            }
        }
        void PreCull(Camera camera)
        {
            if (leasedCamera != null && leasedCamera != camera) { Fault("unclosed-camera-lease"); return; }
            if (faulted || finished || !cameras.ContainsKey(camera)) return;
            try
            {
                if (leasedCamera != null) throw new InvalidOperationException("Nested aero camera lease.");
                if (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.ActiveVessel != vessel) throw new InvalidOperationException("Scene or active vessel changed.");
                UpdateBounds(); leasedCamera = camera;
                foreach (Packet packet in packets)
                {
                    if ((camera.cullingMask & (1 << packet.Object.layer)) == 0) continue;
                    foreach (Source source in packet.Sources)
                    {
                        if (RetainedRenderSources.IsRetired(source.Renderer))
                        {
                            if (!RetainedRenderSources.Visible(source.Renderer)) throw new InvalidOperationException("Retained FX source visibility changed: " + source.Identity);
                            report.retainedSourceCameraVisits++; continue;
                        }
                        if (!source.Renderer.enabled || source.Renderer.forceRenderingOff || !source.Renderer.gameObject.activeInHierarchy)
                            throw new InvalidOperationException("Source renderer visibility changed before lease: " + source.Identity + " enabled=" + source.Renderer.enabled + " forceRenderingOff=" + source.Renderer.forceRenderingOff + " active=" + source.Renderer.gameObject.activeInHierarchy);
                        suppressed.Add(source.Renderer); source.Renderer.forceRenderingOff = true;
                    }
                    packet.Renderer.forceRenderingOff = false;
                }
                report.cameraInvocations++; cameras[camera].invocations++;
            }
            catch (Exception error) { Fault("camera-lease: " + error.GetType().Name + ": " + error.Message); }
        }
        void PostRender(Camera camera) { if (leasedCamera == camera) Release(); }
        void Release()
        {
            foreach (MeshRenderer renderer in suppressed) if (renderer != null) { renderer.forceRenderingOff = false; report.restoredOriginals++; }
            suppressed.Clear(); foreach (Packet packet in packets) if (packet.Renderer != null) packet.Renderer.forceRenderingOff = true; leasedCamera = null;
        }
        void Modified(Vessel changed)
        {
            if (changed != vessel || faulted) return;
            if (vessel.parts.Count != parts.Count) { Fault("topology-changed"); return; }
            foreach (Part part in vessel.parts) if (part == null || !parts.Contains(part.flightID)) { Fault("topology-changed"); return; }
        }
        void Died(Part part) { if (part != null && parts.Contains(part.flightID)) Fault("owned-part-died"); }
        void Scene(GameScenes scene) { Fault("scene-change"); }
        void Fault(string reason)
        {
            if (faulted) return; faulted = true; report.faults++; report.reason = reason; report.status = "invalid-fell-back-to-stock";
            FlightPartBatcher.ExternalRenderFault(reason);
            Release(); foreach (Packet packet in packets) if (packet.Renderer != null) packet.Renderer.enabled = false;
            Detach(); Write();
        }
        void Detach()
        {
            if (cameraPreAttached) { Camera.onPreCull -= PreCull; cameraPreAttached = false; }
            if (cameraPostAttached) { Camera.onPostRender -= PostRender; cameraPostAttached = false; }
            if (modifiedAttached) { GameEvents.onVesselWasModified.Remove(Modified); modifiedAttached = false; }
            if (diedAttached) { GameEvents.onPartDie.Remove(Died); diedAttached = false; }
            if (sceneAttached) { GameEvents.onGameSceneLoadRequested.Remove(Scene); sceneAttached = false; }
        }
        void Cleanup()
        {
            if (finished) return; finished = true; Release(); Detach();
            foreach (Packet packet in packets)
            {
                if (packet.Object != null) { UnityEngine.Object.Destroy(packet.Object); report.destroyedProxies++; }
                if (packet.Mesh != null) UnityEngine.Object.Destroy(packet.Mesh);
            }
            report.cleanupStatus = "removed-owned-hooks-restored-owned-renderers-destruction-requested";
            if (!faulted) report.status = "complete-experimental"; Write();
        }
        void Write()
        {
            var rows = new List<Fallback>(); foreach (var item in fallback) rows.Add(new Fallback { reason = item.Key, count = item.Value });
            rows.Sort((a,b) => string.CompareOrdinal(a.reason,b.reason)); report.fallbacks = rows.ToArray();
            var cameraRows = new List<CameraCount>(cameras.Values); cameraRows.Sort((a,b) => string.CompareOrdinal(a.role,b.role)); report.cameras = cameraRows.ToArray();
            File.WriteAllText(Path.Combine(directory, "aero-batch.json"), ReportJson.Encode(report));
        }
    }
}
