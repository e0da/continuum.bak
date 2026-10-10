using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal sealed class FlightPartBatcher
    {
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-part-batch/v1", runId, vesselId, status, reason, shaderName, shaderSource, bundleSha256;
            public string fidelity = "Original research shader, not stock shader equivalence. Albedo, normal, specular/emissive maps, stock temperature/highlight inputs retained under bounded opaque recipe; stock underwater fog, spec-map channel mapping and exact lighting require image qualification.";
            public string recipe = "Stock Kerbin ocean fixture above sea level; single-submesh positive-determinant MeshRenderer; stock KSP/Specular or KSP/Emissive/Bumped Specular (Mapped), no keywords, opaque opacity1, white burn, ambient multiplier1. Declared material bindings frozen; known stock MPB temperature/rim/emissive sampled each frame. Black base temperature/rim/emission RGB admitted with finite alpha retained in fingerprints (emission alpha unused). MPB opacity0 interpreted as absent only under initial opaque stock recipe: Unity2019 cannot distinguish absent from an explicit zero. Arbitrary unenumerable mod MPB inputs unsupported; not qualified for opacity transitions.";
            public string shadowContract = "Main flight cameras only; stock FX cameras unchanged. DrawMeshInstanced requests original shadowCasting/receiveShadows settings, but source suppression versus shadow scheduling requires native image qualification.";
            public string probeContract = "Off retained; BlendProbes interpolated at original probeAnchor or renderer bounds center and supplied as per-instance SH/occlusion. Proxy/custom probes excluded. Reflection probe and exact stock lighting behavior remain shader fidelity gates.";
            public int processId, eligibleRenderers, fallbackRenderers, packets, cameraInvocations, drawSubmissions, restoredOriginals, faults, releasedMaterials;
            public bool releasedBundle;
            public long publicationTicks;
            public long clockFrequency = Stopwatch.Frequency;
            public int admittedBlackEmissionBaseInputs;
            public bool retainedProvider;
            public int retiredRenderers, restoredRetiredRenderers, sharedPartPoseSnapshots, sharedSourcePublications, rigidLocalChecks, materialBindingChecks, propertyBlockCaptures;
            public long sharedPoseTicks;
            public double maxInitialRecoveredPositionErrorMeters;
            public int rigidRetainedSources, dynamicRetainedSources, dynamicPoseSnapshots;
            public int animationCullingLeases, restoredAnimationCullingLeases;
            public float minBaseEmissionAlpha, maxBaseEmissionAlpha;
            public double preparationMilliseconds;
            public string cleanupStatus = "not-started";
        }
        sealed class Source
        {
            public MeshRenderer Renderer; public MeshFilter Filter; public Mesh Mesh; public Material Material; public Part Owner;
            public string Key, Identity;
            public bool Retained;
        }
        sealed class Packet
        {
            public Mesh Mesh; public Material Material; public readonly List<Source> Sources = new List<Source>();
            public Matrix4x4[] Matrices; public Vector4[] Temperature, Rim, Emission; public float[] Falloff, Opacity;
            public MaterialPropertyBlock Block = new MaterialPropertyBlock(); public int Layer;
            public ShadowCastingMode Shadows; public bool ReceiveShadows;
            public LightProbeUsage Probes;
            public Vector3[] ProbePositions; public SphericalHarmonicsL2[] ProbeLighting; public Vector4[] ProbeOcclusion;
        }
        sealed class Binding
        {
            public Material Material; public Shader Shader; public int Queue; public readonly List<Value> Values = new List<Value>();
        }
        sealed class Value
        {
            public int Id; public ShaderPropertyType Type; public Vector4 Vector; public float Float;
            public Texture Texture; public Vector2 Scale, Offset;
        }
        static FlightPartBatcher current;
        const string PatchOwner="continuum.flight.part.opacity";
        public static string FailureReason { get { return current != null && current.faulted ? current.report.reason : null; } }
        readonly List<Packet> packets = new List<Packet>();
        readonly List<MeshRenderer> leased = new List<MeshRenderer>();
        readonly HashSet<Camera> cameras = new HashSet<Camera>();
        readonly HashSet<uint> parts = new HashSet<uint>();
        readonly HashSet<int> restored = new HashSet<int>();
        readonly Dictionary<Material,Binding> bindings = new Dictionary<Material,Binding>();
        readonly MaterialPropertyBlock observed = new MaterialPropertyBlock();
        Vessel vessel; Camera cameraLease; AssetBundle bundle; Shader shader; string directory;
        bool retained;
        Report report; bool standard, faulted, finished, callbacks, modified, died, scene;
        Harmony opacityHook;
        int frame = -1;
        static string Argument(string prefix)
        { foreach (string arg in Environment.GetCommandLineArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg.Substring(prefix.Length); return null; }
        static bool Flag(string flag) { return Array.IndexOf(Environment.GetCommandLineArgs(), flag) >= 0; }
        public static Report Start(Vessel vessel, string directory, string runId)
        {
            if (!Flag("--continuum-flight-part-batch")) return null;
            if (current != null) throw new InvalidOperationException("Part renderer already owned.");
            current = new FlightPartBatcher { vessel = vessel, directory = directory,
                report = new Report { runId = runId, vesselId = vessel == null ? null : vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id, status = "preparing" } };
            try { current.Prepare(); } catch (Exception error) { current.Fault("preparation: " + error); }
            return current.report;
        }
        public static Report Finish()
        { if (current == null) return null; FlightPartBatcher owner = current; current = null; owner.Cleanup(); return owner.report; }
        internal static void ExternalRenderFault(string reason) { if (current != null && current.retained) current.Fault("shared-FX: " + reason); }
        static string F(float x) { return x.ToString("R", CultureInfo.InvariantCulture); }
        static string V(Vector4 x) { return F(x.x)+","+F(x.y)+","+F(x.z)+","+F(x.w); }
        static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        static bool Finite(Vector4 x) { return Finite(x.x)&&Finite(x.y)&&Finite(x.z)&&Finite(x.w); }
        static bool BlackRgb(Color color) { return Finite(color) && color.r==0 && color.g==0 && color.b==0; }
        static string MaterialKey(Material material)
        {
            var key = new System.Text.StringBuilder(material.shader.name);
            for (int i = 0; i < material.shader.GetPropertyCount(); i++)
            {
                string name = material.shader.GetPropertyName(i);
                key.Append('|').Append(name).Append('=');
                switch (material.shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: key.Append(V(material.GetColor(name))); break;
                    case ShaderPropertyType.Vector: key.Append(V(material.GetVector(name))); break;
                    case ShaderPropertyType.Float: case ShaderPropertyType.Range: key.Append(F(material.GetFloat(name))); break;
                    case ShaderPropertyType.Texture:
                        Texture texture = material.GetTexture(name); Vector2 scale = material.GetTextureScale(name), offset = material.GetTextureOffset(name);
                        key.Append(texture == null ? 0 : texture.GetInstanceID()).Append('/').Append(F(scale.x)).Append(',').Append(F(scale.y)).Append('/').Append(F(offset.x)).Append(',').Append(F(offset.y)); break;
                    default: throw new InvalidOperationException("Unknown material property.");
                }
            }
            return key.ToString();
        }
        void CaptureBinding(Material material)
        {
            if (bindings.ContainsKey(material)) return;
            var binding = new Binding { Material=material, Shader=material.shader, Queue=material.renderQueue };
            for (int i=0;i<material.shader.GetPropertyCount();i++)
            {
                int id=material.shader.GetPropertyNameId(i); var value=new Value { Id=id, Type=material.shader.GetPropertyType(i) };
                switch(value.Type)
                {
                    case ShaderPropertyType.Color: value.Vector=material.GetColor(id); break;
                    case ShaderPropertyType.Vector: value.Vector=material.GetVector(id); break;
                    case ShaderPropertyType.Float: case ShaderPropertyType.Range: value.Float=material.GetFloat(id); break;
                    case ShaderPropertyType.Texture: value.Texture=material.GetTexture(id); value.Scale=material.GetTextureScale(id); value.Offset=material.GetTextureOffset(id); break;
                    default: throw new InvalidOperationException("Unsupported binding type.");
                }
                binding.Values.Add(value);
            }
            bindings.Add(material,binding);
        }
        static bool Exact(Vector4 a, Vector4 b) { return a.x==b.x && a.y==b.y && a.z==b.z && a.w==b.w; }
        void CheckBindings()
        {
            foreach(Binding binding in bindings.Values)
            {
                report.materialBindingChecks++;
                Material material=binding.Material;
                if(material==null || material.shader!=binding.Shader || material.renderQueue!=binding.Queue || material.shaderKeywords.Length != 0) throw new InvalidOperationException("Material/shader ownership changed.");
                foreach(Value value in binding.Values)
                {
                    bool match;
                    switch(value.Type)
                    {
                        case ShaderPropertyType.Color: match=Exact(material.GetColor(value.Id),value.Vector); break;
                        case ShaderPropertyType.Vector: match=Exact(material.GetVector(value.Id),value.Vector); break;
                        case ShaderPropertyType.Float: case ShaderPropertyType.Range: match=material.GetFloat(value.Id)==value.Float; break;
                        case ShaderPropertyType.Texture:
                            Vector2 scale=material.GetTextureScale(value.Id), offset=material.GetTextureOffset(value.Id);
                            match=material.GetTexture(value.Id)==value.Texture && scale.x==value.Scale.x && scale.y==value.Scale.y && offset.x==value.Offset.x && offset.y==value.Offset.y; break;
                        default: match=false; break;
                    }
                    if(!match) throw new InvalidOperationException("Frozen material value changed; material="+material.GetInstanceID()+" property="+value.Id);
                }
            }
        }
        void Prepare()
        {
            Stopwatch timer = Stopwatch.StartNew();
            retained = RetainedRenderSources.Requested; report.retainedProvider = retained;
            if (retained) RetainedRenderSources.Begin();
            if (retained) report.recipe += " Retained provider: FX-covered sources retired independently of pose compilation. Rigid child locals checked each published frame; animation/Animator ancestry retains once-per-frame live source transforms. Sources outside owned FX coverage keep original main-camera batching leases. Original components retained for property-block writers. Unknown idempotent renderer-disable writes are outside stable visibility; FX proxy bones remain live.";
            if (retained && !FlightAeroBatcher.IsActive) throw new InvalidOperationException("Retained main renderer requires an active owned FX provider.");
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.mainBody==null || vessel.mainBody.bodyName!="Kerbin" || !vessel.mainBody.ocean || vessel.altitude <= 0 || !SystemInfo.supportsInstancing)
                throw new InvalidOperationException("Physical stock Kerbin vessel above ocean sea level and native instancing required.");
            string path = Argument("--continuum-flight-part-shader-bundle=");
            if (path != null)
            {
                if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new InvalidOperationException("Absolute shader bundle required.");
                bundle = AssetBundle.LoadFromFile(path); if (bundle == null) throw new InvalidOperationException("Shader bundle failed to load.");
                foreach (Shader item in bundle.LoadAllAssets<Shader>()) if (item.name == "Continuum/FlightPartSpecularInstanced") shader = item;
                report.shaderSource = "custom-bundle"; report.bundleSha256 = ScaleCheckpointLoadState.HashFile(path);
            }
            else if (Flag("--continuum-flight-part-standard-fallback"))
            { shader = Shader.Find("Standard (Specular setup)"); standard = true; report.shaderSource = "shipped-standard-diagnostic"; }
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Supported explicit part shader unavailable.");
            report.shaderName = shader.name;
            if (standard) report.fidelity += " Standard fallback only accepts black temperature/highlight/emission; no per-instance emission variant assumed.";
            if (FlightCamera.fetch == null) throw new InvalidOperationException("Main flight cameras unavailable.");
            foreach (Camera camera in FlightCamera.fetch.cameras) if (camera != null) cameras.Add(camera);
            if (cameras.Count == 0) throw new InvalidOperationException("Main camera coverage empty.");
            var seen = new HashSet<Renderer>(); var groups = new Dictionary<string,Packet>();
            foreach (Part part in vessel.parts) if (part != null)
            {
                parts.Add(part.flightID);
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !seen.Add(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff) continue;
                    var sourceRenderer = renderer as MeshRenderer; var filter = renderer.GetComponent<MeshFilter>();
                    Material[] materials = renderer.sharedMaterials; Mesh mesh = filter == null ? null : filter.sharedMesh;
                    Material material = materials.Length == 1 ? materials[0] : null;
                    bool covered = false; foreach (Camera camera in cameras) if ((camera.cullingMask & (1 << renderer.gameObject.layer)) != 0) covered=true;
                    if (sourceRenderer == null || mesh == null || mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles || material == null || material.shader == null ||
                        (material.shader.name != "KSP/Specular" && material.shader.name != "KSP/Emissive/Bumped Specular (Mapped)") ||
                        material.shaderKeywords.Length != 0 || material.GetTag("RenderType",false,"") != "Opaque" || material.GetFloat("_Opacity") != 1 ||
                        !covered || material.GetColor("_BurnColor") != Color.white || renderer.lightmapIndex >= 0 || renderer.realtimeLightmapIndex >= 0 || renderer.transform.localToWorldMatrix.determinant <= 0 ||
                        (renderer.lightProbeUsage != LightProbeUsage.Off && renderer.lightProbeUsage != LightProbeUsage.BlendProbes) ||
                        !BlackRgb(material.GetColor("_TemperatureColor")) || !BlackRgb(material.GetColor("_RimColor")) ||
                        (material.HasProperty("_EmissiveColor") && !BlackRgb(material.GetColor("_EmissiveColor"))) ||
                        (material.HasProperty("_AmbientMultiplier") && material.GetFloat("_AmbientMultiplier") != 1))
                    { report.fallbackRenderers++; continue; }
                    Part owner = renderer.GetComponentInParent<Part>();
                    if (owner == null || owner.partInfo == null || !parts.Contains(owner.flightID) && !vessel.parts.Contains(owner)) { report.fallbackRenderers++; continue; }
                    bool sourceRetained = retained && RetainedRenderSources.Register(sourceRenderer, owner, mesh);
                    string materialKey = MaterialKey(material);
                    if(material.HasProperty("_EmissiveColor"))
                    {
                        float alpha=material.GetColor("_EmissiveColor").a;
                        if(report.admittedBlackEmissionBaseInputs==0) report.minBaseEmissionAlpha=report.maxBaseEmissionAlpha=alpha;
                        else { report.minBaseEmissionAlpha=Math.Min(report.minBaseEmissionAlpha,alpha); report.maxBaseEmissionAlpha=Math.Max(report.maxBaseEmissionAlpha,alpha); }
                        report.admittedBlackEmissionBaseInputs++;
                    }
                    CaptureBinding(material);
                    string key = mesh.GetInstanceID()+"/"+renderer.gameObject.layer+"/"+renderer.shadowCastingMode+"/"+renderer.receiveShadows+"/"+renderer.lightProbeUsage+"/"+materialKey;
                    Packet packet;
                    if (!groups.TryGetValue(key, out packet) || packet.Sources.Count == 1023)
                    {
                        packet = new Packet { Mesh = mesh, Material = Bind(material), Layer = renderer.gameObject.layer, Shadows=renderer.shadowCastingMode, ReceiveShadows=renderer.receiveShadows, Probes=renderer.lightProbeUsage }; packets.Add(packet); groups[key] = packet;
                    }
                    packet.Sources.Add(new Source { Renderer = sourceRenderer, Filter=filter, Mesh = mesh, Material = material, Owner = owner, Key = materialKey, Identity = renderer.name+"#"+renderer.GetInstanceID(), Retained = sourceRetained });
                    report.eligibleRenderers++;
                }
            }
            if (packets.Count == 0) throw new InvalidOperationException("No supported part geometry.");
            foreach (Packet packet in packets)
            {
                int n = packet.Sources.Count; packet.Matrices = new Matrix4x4[n]; packet.Temperature = new Vector4[n]; packet.Rim = new Vector4[n];
                packet.Emission = new Vector4[n]; packet.Falloff = new float[n]; packet.Opacity = new float[n];
                if (packet.Probes == LightProbeUsage.BlendProbes) {
                    packet.ProbePositions = new Vector3[n]; packet.ProbeLighting = new SphericalHarmonicsL2[n]; packet.ProbeOcclusion = new Vector4[n];
                }
            }
            UpdateInputs(); CaptureResidentMaterialFixture(); Camera.onPreCull += PreCull; Camera.onPostRender += PostRender; callbacks = true;
            var opacityMethod=AccessTools.Method(typeof(Part),"SetOpacity",new[] { typeof(float) });
            if(opacityMethod==null || Harmony.GetPatchInfo(opacityMethod)?.Owners.Count>0) throw new InvalidOperationException("Owned stock opacity boundary unavailable.");
            opacityHook=new Harmony(PatchOwner); opacityHook.Patch(opacityMethod,prefix:new HarmonyMethod(typeof(FlightPartBatcher),nameof(OpacityChanging)));
            GameEvents.onVesselWasModified.Add(Modified); modified = true; GameEvents.onPartDie.Add(Died); died = true;
            GameEvents.onGameSceneLoadRequested.Add(Scene); scene = true;
            if (retained) { RetainedRenderSources.RetireAll(); report.retiredRenderers = RetainedRenderSources.RetiredCount; }
            report.packets = packets.Count; report.preparationMilliseconds = timer.Elapsed.TotalMilliseconds;
            report.status = "installed-experimental"; report.cleanupStatus = "owned-hooks-active"; Write();
        }
        void CaptureResidentMaterialFixture()
        {
            string explicitDirectory = Environment.GetEnvironmentVariable("CONTINUUM_RESIDENT_MATERIAL_FIXTURE");
            if (!Flag("--continuum-resident-material-fixture") && string.IsNullOrEmpty(explicitDirectory)) return;
            var sources = new List<ResidentMaterialFixtureCapture.Source>();
            foreach (Packet packet in packets) for (int i = 0; i < packet.Sources.Count; i++)
            {
                Source source = packet.Sources[i];
                if (source.Material.shader.name != "KSP/Emissive/Bumped Specular (Mapped)") continue;
                if (packet.ProbeLighting == null || packet.ProbeOcclusion == null)
                    throw new InvalidDataException("Mapped fixture requires actual per-source SH and occlusion.");
                observed.Clear(); source.Renderer.GetPropertyBlock(observed);
                sources.Add(new ResidentMaterialFixtureCapture.Source { Mesh = source.Mesh, Material = packet.Material, Renderer = source.Renderer, Part = source.Owner,
                    Matrix = packet.Matrices[i], Temperature = packet.Temperature[i], Rim = packet.Rim[i], Emission = packet.Emission[i],
                    Lighting = packet.ProbeLighting[i], Occlusion = packet.ProbeOcclusion[i], Falloff = packet.Falloff[i],
                    RawOpacity = observed.GetFloat(PropertyIDs._Opacity), EffectiveOpacity = packet.Opacity[i] });
            }
            ResidentMaterialFixtureCapture.Capture(vessel, string.IsNullOrEmpty(explicitDirectory) ? Path.Combine(directory, "resident-material-fixture") : explicitDirectory, report.runId, sources);
        }
        Material Bind(Material original)
        {
            var result = new Material(shader) { enableInstancing = true, renderQueue = original.renderQueue };
            foreach (string name in new[] { "_MainTex", "_BumpMap", "_Emissive", "_SpecMap" })
                if (original.HasProperty(name) && result.HasProperty(name))
                { result.SetTexture(name, original.GetTexture(name)); result.SetTextureScale(name, original.GetTextureScale(name)); result.SetTextureOffset(name, original.GetTextureOffset(name)); }
            bool mapped = original.shader.name.EndsWith("(Mapped)",StringComparison.Ordinal);
            if (result.HasProperty("_Mapped")) result.SetFloat("_Mapped", mapped ? 1 : 0);
            result.SetColor("_Color", original.HasProperty("_Color") ? original.GetColor("_Color") : Color.white);
            float tint = mapped ? original.GetFloat("_SpecTint") : 0;
            result.SetColor("_SpecColor", mapped ? new Color(tint,tint,tint,1) : original.GetColor("_SpecColor"));
            if (result.HasProperty("_Shininess")) result.SetFloat("_Shininess", original.GetFloat("_Shininess"));
            if (standard)
            {
                result.SetFloat("_Glossiness", original.GetFloat("_Shininess"));
                if (mapped) { result.SetTexture("_SpecGlossMap", original.GetTexture("_SpecMap")); result.EnableKeyword("_SPECGLOSSMAP"); }
                if (original.HasProperty("_BumpMap")) result.EnableKeyword("_NORMALMAP");
                result.SetColor("_EmissionColor", Color.black);
            }
            return result;
        }
        void UpdateInputs()
        {
            if (frame == Time.frameCount) return; frame = Time.frameCount;
            long start = Stopwatch.GetTimestamp();
            CheckBindings();
            foreach (Packet packet in packets) for (int i=0;i<packet.Sources.Count;i++)
            {
                Source source = packet.Sources[i]; MeshRenderer renderer = source.Renderer;
                if (renderer == null || !RetainedRenderSources.Visible(renderer) ||
                    source.Filter == null || source.Filter.sharedMesh != source.Mesh || renderer.sharedMaterials.Length != 1 || renderer.sharedMaterial != source.Material ||
                    renderer.gameObject.layer!=packet.Layer || renderer.shadowCastingMode!=packet.Shadows || renderer.receiveShadows!=packet.ReceiveShadows)
                    throw new InvalidOperationException("Source visibility/mesh/material changed: "+source.Identity);
                observed.Clear(); renderer.GetPropertyBlock(observed);
                report.propertyBlockCaptures++;
                Color temperature = observed.GetColor(PhysicsGlobals.TemperaturePropertyID), rim = observed.GetColor(PropertyIDs._RimColor);
                Color emission = observed.GetColor("_EmissiveColor"); float opacity = observed.GetFloat(PropertyIDs._Opacity), falloff = observed.GetFloat(PropertyIDs._RimFalloff);
                if (!Finite(temperature) || !Finite(rim) || !Finite(emission) || !Finite(opacity) || !Finite(falloff) || (opacity != 0 && opacity != 1))
                    throw new InvalidOperationException("Unsupported stock block values: "+source.Identity);
                if (standard && (temperature.r != 0 || temperature.g != 0 || temperature.b != 0 || rim.r != 0 || rim.g != 0 || rim.b != 0 || emission.r != 0 || emission.g != 0 || emission.b != 0))
                    throw new InvalidOperationException("Standard diagnostic cannot preserve active heating/highlight/emission: "+source.Identity);
                Matrix4x4 matrix = source.Retained ? RetainedRenderSources.Pose(renderer) : renderer.transform.localToWorldMatrix;
                for (int element=0;element<16;element++) if (!Finite(matrix[element])) throw new InvalidOperationException("Nonfinite source pose.");
                if (matrix.determinant <= 0) throw new InvalidOperationException("Source determinant changed.");
                packet.Matrices[i] = matrix; packet.Temperature[i] = temperature; packet.Rim[i] = rim; packet.Emission[i] = emission;
                packet.Falloff[i] = falloff; packet.Opacity[i] = 1;
                if (renderer.lightProbeUsage != packet.Probes) throw new InvalidOperationException("Source probe usage changed.");
                if (packet.ProbePositions != null) packet.ProbePositions[i] = renderer.probeAnchor == null ? (source.Retained ? RetainedRenderSources.Bounds(renderer).center : renderer.bounds.center) : renderer.probeAnchor.position;
            }
            foreach (Packet packet in packets)
            {
                if (!standard) {
                    packet.Block.SetVectorArray("_TemperatureColor", packet.Temperature); packet.Block.SetVectorArray("_RimColor", packet.Rim);
                    packet.Block.SetVectorArray("_EmissiveColor", packet.Emission); packet.Block.SetFloatArray("_RimFalloff", packet.Falloff); packet.Block.SetFloatArray("_Opacity", packet.Opacity);
                }
                if (packet.ProbePositions != null) {
                    LightProbes.CalculateInterpolatedLightAndOcclusionProbes(packet.ProbePositions, packet.ProbeLighting, packet.ProbeOcclusion);
                    packet.Block.CopySHCoefficientArraysFrom(packet.ProbeLighting); packet.Block.CopyProbeOcclusionArrayFrom(packet.ProbeOcclusion);
                }
            }
            report.publicationTicks += Stopwatch.GetTimestamp()-start;
        }
        void PreCull(Camera camera)
        {
            if (cameraLease != null && cameraLease != camera) { Fault("Unclosed main camera lease."); return; }
            if (faulted || finished || !cameras.Contains(camera)) return;
            try
            {
                if (cameraLease != null || FlightGlobals.ActiveVessel != vessel || HighLogic.LoadedScene != GameScenes.FLIGHT || vessel.packed || vessel.mainBody==null || vessel.mainBody.bodyName!="Kerbin" || !vessel.mainBody.ocean || vessel.altitude <= 0)
                    throw new InvalidOperationException("Main camera/vessel context changed.");
                UpdateInputs(); cameraLease = camera;
                foreach (Packet packet in packets)
                {
                    if ((camera.cullingMask & (1 << packet.Layer)) == 0) continue;
                    foreach (Source source in packet.Sources) if (!source.Retained)
                    { if (source.Renderer.forceRenderingOff) throw new InvalidOperationException("Source visibility changed before lease."); leased.Add(source.Renderer); source.Renderer.forceRenderingOff=true; }
                    Graphics.DrawMeshInstanced(packet.Mesh, 0, packet.Material, packet.Matrices, packet.Matrices.Length, packet.Block,
                        packet.Shadows, packet.ReceiveShadows, packet.Layer, camera,
                        packet.Probes == LightProbeUsage.BlendProbes ? LightProbeUsage.CustomProvided : LightProbeUsage.Off);
                    report.drawSubmissions++;
                }
                report.cameraInvocations++;
            }
            catch (Exception error) { Fault("main-camera: "+error); }
        }
        void PostRender(Camera camera) { if (cameraLease == camera) Release(); }
        static void OpacityChanging(Part __instance,float opacity)
        {
            FlightPartBatcher owner=current;
            if(owner!=null && !owner.faulted && __instance!=null && owner.parts.Contains(__instance.flightID) && opacity!=1)
                owner.Fault("Stock opacity transition unsupported; part="+__instance.flightID+" requested="+F(opacity));
        }
        void Release() { foreach (MeshRenderer renderer in leased) if (renderer != null) { renderer.forceRenderingOff=false; restored.Add(renderer.GetInstanceID()); } report.restoredOriginals=restored.Count; leased.Clear(); cameraLease=null; }
        void Modified(Vessel changed) { if (changed==vessel) { if (vessel.parts.Count!=parts.Count) Fault("Topology changed."); else foreach (Part part in vessel.parts) if (part==null || !parts.Contains(part.flightID)) { Fault("Topology changed."); break; } } }
        void Died(Part part) { if (part!=null && parts.Contains(part.flightID)) Fault("Owned part died."); }
        void Scene(GameScenes next) { Fault("Scene change."); }
        void Fault(string reason) { if (faulted) return; faulted=true; report.faults++; report.reason=reason; report.status="invalid-fell-back-to-stock"; if (retained) RetainedRenderSources.RestoreAll(); Release(); Detach(); Write(); }
        void Detach()
        {
            if(opacityHook!=null) { opacityHook.UnpatchAll(PatchOwner); opacityHook=null; }
            if (callbacks) { Camera.onPreCull-=PreCull; Camera.onPostRender-=PostRender; callbacks=false; }
            if (modified) { GameEvents.onVesselWasModified.Remove(Modified); modified=false; }
            if (died) { GameEvents.onPartDie.Remove(Died); died=false; }
            if (scene) { GameEvents.onGameSceneLoadRequested.Remove(Scene); scene=false; }
        }
        void Cleanup()
        {
            if (finished) return; finished=true; if (retained) RetainedRenderSources.RestoreAll(); Release(); Detach(); foreach (Packet packet in packets) if (packet.Material!=null) { UnityEngine.Object.Destroy(packet.Material); report.releasedMaterials++; }
            if (bundle!=null) { bundle.Unload(true); report.releasedBundle=true; } report.cleanupStatus="restored-owned-renderers-removed-owned-hooks-destruction-requested";
            if (retained && (RetainedRenderSources.RestoredCount != RetainedRenderSources.RetiredCount || RetainedRenderSources.AnimationCullingLeases != RetainedRenderSources.RestoredAnimationCullingLeases))
            { faulted = true; report.faults++; report.reason = "Retired source or owned animation destroyed before restoration."; }
            if (!faulted) report.status="complete-experimental"; else report.status="invalid-fell-back-to-stock";
            Write(); if (retained) RetainedRenderSources.Clear();
        }
        void Write()
        {
            if (retained) { report.restoredRetiredRenderers = RetainedRenderSources.RestoredCount; report.sharedPartPoseSnapshots = RetainedRenderSources.PoseSnapshots;
                report.sharedSourcePublications = RetainedRenderSources.SourcePublications; report.rigidLocalChecks = RetainedRenderSources.LocalChecks; report.sharedPoseTicks = RetainedRenderSources.CaptureTicks; }
            if (retained) report.maxInitialRecoveredPositionErrorMeters = RetainedRenderSources.MaxInitialRecoveredPositionErrorMeters;
            if (retained) { report.rigidRetainedSources = RetainedRenderSources.RigidSources; report.dynamicRetainedSources = RetainedRenderSources.DynamicSources; report.dynamicPoseSnapshots = RetainedRenderSources.DynamicPoseSnapshots; }
            if (retained) { report.animationCullingLeases = RetainedRenderSources.AnimationCullingLeases; report.restoredAnimationCullingLeases = RetainedRenderSources.RestoredAnimationCullingLeases; }
            report.restoredOriginals = restored.Count + (retained ? RetainedRenderSources.RestoredCount : 0);
            File.WriteAllText(Path.Combine(directory,"part-batch.json"), ReportJson.Encode(report));
        }
    }
}
