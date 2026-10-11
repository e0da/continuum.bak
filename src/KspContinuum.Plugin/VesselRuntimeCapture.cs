using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KspContinuum
{
    public sealed class VesselRuntimeCapture : IDisposable
    {
        [Serializable] public sealed class ResultRow
        {
            public long sequence;
            public int unityFrame, liftingSurfaces, shieldedParts, physicslessParts, missingRigidbodies;
            public double universalTime, captureMilliseconds, validationMilliseconds, analysisMilliseconds;
            public double[] force, moment;
        }
        [Serializable] public sealed class CaptureReport
        {
            public string schema = "continuum-vessel-runtime-capture/v1";
            public string status = "admitting", reason, runId, vesselId, manifestSha256, recordingSha256, recordingPath, reportPath;
            public string observationScope = "Main-thread Probe coroutine observation after yield null, before its wall-time endpoint; poses and stored force fields are current at that boundary, not synchronized force publications.";
            public string forceScope = "Aerodynamic diagnostic only: stored part drag, projected stored body lift when !hasLiftModule and enabled active ModuleLiftingSurface lift/internal drag; shielded parts excluded. No field freshness proof, arbitrary mod-provider equivalence, gravity/thrust/other force coverage, or physics writeback.";
            public string momentScope = "Resultant about the vessel transform origin using each part Rigidbody world center of mass; physicsless parts share their parent body center. Missing bodies use the part CoMOffset. CoP/CoL application moments and intrinsic torque are not represented.";
            public string units = "Vessel-root local metres, newtons and newton-metres; stored KSP force vectors in kN multiplied by 1000. Root transform must be rigid.";
            public string poseScope = "Current rigid renderer transform per captured draw; frozen skinned geometry does not include changing skin/bone deformation. Visibility observes source renderer flags, including forceRenderingOff, at the capture boundary.";
            public string topologyScope = "One session topology epoch; changed part order, parent, module order, owned renderer membership, admitted draw identity or destroyed reference aborts without reindexing.";
            public string costScope = "captureTicks includes topology validation, pose/load capture, frame validation and shared analysis; validationTicks and analysisTicks are nested portions. Admission and persistence are outside the Probe window; encodedBytes includes the binary header.";
            public int frames, draws, parts, renderers, frozenSkinnedDraws, physicslessParts, processId, topologyEvents;
            public long clockFrequency = Stopwatch.Frequency, captureTicks, validationTicks, analysisTicks, frameBytes, capturedBytes, encodedBytes;
            public double admissionMilliseconds, persistenceMilliseconds;
            public ResultRow[] results = new ResultRow[0];
        }
        sealed class RendererBinding
        {
            public Renderer Renderer;
            public Part Part;
            public string Stable;
            public int[] Draws;
        }
        readonly CaptureReport report = new CaptureReport();
        readonly List<VesselRuntimeFrame> frames = new List<VesselRuntimeFrame>();
        readonly List<ResultRow> results = new List<ResultRow>();
        readonly string output;
        readonly int threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        readonly Vessel vessel;
        readonly WholeCraftSceneManifest manifest;
        readonly VesselRuntimeRecording recording;
        readonly Part[] parts, parents;
        readonly Rigidbody[] bodies, localBodies;
        readonly PartModule[][] modules;
        readonly ModuleLiftingSurface[][] surfaces;
        readonly Renderer[][] partRenderers;
        readonly RendererBinding[] bindings;
        long headerBytes;
        bool modifiedHook, diedHook, disposed, finished;
        string topologyFailure;
        public static bool Requested { get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_VESSEL_RUNTIME_RECORDING")); } }
        public CaptureReport Report { get { return report; } }
        public string FailureReason { get { return report.status == "invalid" || report.status == "persistence-failed" ? report.reason : null; } }

        public static VesselRuntimeCapture Create(Vessel vessel, string runId)
        {
            string path = Environment.GetEnvironmentVariable("CONTINUUM_VESSEL_RUNTIME_RECORDING");
            return string.IsNullOrEmpty(path) ? null : new VesselRuntimeCapture(vessel, runId, path);
        }
        static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
        static Renderer[] OwnedRenderers(Part part)
        {
            return part.GetComponentsInChildren<Renderer>(true).Where(r => r != null && ReferenceEquals(r.GetComponentInParent<Part>(), part)).ToArray();
        }
        VesselRuntimeCapture(Vessel vessel, string runId, string path)
        {
            long start = Stopwatch.GetTimestamp();
            Require(Path.IsPathRooted(path) && !string.IsNullOrEmpty(Path.GetFileName(path)), "Runtime recording requires an absolute new file path.");
            output = Path.GetFullPath(path);
            Require(!File.Exists(output) && !Directory.Exists(output) && !File.Exists(output + ".partial") && !Directory.Exists(output + ".partial") && !File.Exists(output + ".json") && !Directory.Exists(output + ".json"), "Runtime recording output already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            report.runId = runId; report.recordingPath = output; report.reportPath = output + ".json";
            report.processId = Process.GetCurrentProcess().Id;
            try
            {
                Require(vessel != null && vessel.loaded && !vessel.packed && HighLogic.LoadedSceneIsFlight, "Runtime capture requires a loaded physical flight vessel.");
                Require(!string.IsNullOrEmpty(runId), "Runtime capture requires an exact run identity.");
                this.vessel = vessel;
                string sceneDirectory = Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_SCENE");
                Require(!string.IsNullOrEmpty(sceneDirectory) && Path.IsPathRooted(sceneDirectory), "Runtime capture requires the cold multiscale scene output.");
                string scenePath = Path.Combine(sceneDirectory, "manifest.json"), manifestPath = Path.Combine(sceneDirectory, "whole-craft", "manifest.json");
                Require(File.Exists(scenePath) && File.Exists(manifestPath) && new FileInfo(scenePath).Length <= 16 * 1024 * 1024 && new FileInfo(manifestPath).Length <= 16 * 1024 * 1024, "Runtime cold manifest missing or oversize.");
                var scene = JsonUtility.FromJson<MultiscaleSceneManifest>(File.ReadAllText(scenePath));
                report.manifestSha256 = ScaleCheckpointLoadState.HashFile(manifestPath);
                manifest = JsonUtility.FromJson<WholeCraftSceneManifest>(File.ReadAllText(manifestPath));
                report.vesselId = vessel.id.ToString("D");
                Require(scene != null && scene.wholeCraft == "whole-craft/manifest.json" && scene.wholeCraftSha256 == report.manifestSha256 && scene.vesselId == report.vesselId && scene.runId == runId, "Runtime scene binding differs.");
                Require(manifest != null && manifest.schema == "continuum-whole-craft-scene/v1" && manifest.vesselId == report.vesselId && manifest.runId == runId && manifest.engine == Application.unityVersion && manifest.draws != null && manifest.draws.Length > 0 && manifest.draws.Length <= VesselRuntimeFormat.MaxDraws, "Runtime whole-craft binding differs.");
                Require(ScaleCheckpointLoadState.HashFile(manifestPath) == report.manifestSha256, "Runtime cold manifest changed during admission.");
                parts = vessel.parts.ToArray();
                Require(parts.Length > 0 && parts.Length <= VesselRuntimeFormat.MaxParts, "Runtime part capacity invalid.");
                parents = new Part[parts.Length]; bodies = new Rigidbody[parts.Length]; localBodies = new Rigidbody[parts.Length];
                modules = new PartModule[parts.Length][]; surfaces = new ModuleLiftingSurface[parts.Length][]; partRenderers = new Renderer[parts.Length][];
                var ids = new HashSet<uint>(); var sourceByStable = new Dictionary<string, RendererBinding>(StringComparer.Ordinal);
                for (int i = 0; i < parts.Length; i++)
                {
                    Part part = parts[i]; Require(part != null && ids.Add(part.flightID) && part.flightID != 0, "Runtime part identity duplicated or absent.");
                    parents[i] = part.parent; bodies[i] = part.Rigidbody; localBodies[i] = part.rb; if (part.rb == null) report.physicslessParts++;
                    modules[i] = part.Modules.Cast<PartModule>().ToArray();
                    surfaces[i] = modules[i].OfType<ModuleLiftingSurface>().ToArray(); partRenderers[i] = OwnedRenderers(part);
                    foreach (Renderer renderer in partRenderers[i])
                    {
                        string stable = WholeCraftSceneCapture.Stable(renderer, part);
                        Require(!sourceByStable.ContainsKey(stable), "Runtime renderer stable identity duplicated.");
                        sourceByStable.Add(stable, new RendererBinding { Renderer = renderer, Part = part, Stable = stable });
                    }
                }
                var admitted = new Dictionary<RendererBinding, List<int>>();
                for (int i = 0; i < manifest.draws.Length; i++)
                {
                    WholeCraftDraw draw = manifest.draws[i]; Require(draw != null && ids.Contains(draw.partId) && draw.id != null, "Runtime draw part binding differs.");
                    int slot = draw.id.LastIndexOf("/slot/", StringComparison.Ordinal); Require(slot > 0, "Runtime draw slot identity invalid.");
                    RendererBinding binding; Require(sourceByStable.TryGetValue(draw.id.Substring(0, slot), out binding) && binding.Part.flightID == draw.partId && binding.Renderer.GetInstanceID() == draw.rendererId, "Runtime live draw identity differs from cold manifest.");
                    List<int> rows; if (!admitted.TryGetValue(binding, out rows)) { rows = new List<int>(); admitted.Add(binding, rows); } rows.Add(i);
                    if (draw.frozenSkin) report.frozenSkinnedDraws++;
                }
                foreach (var pair in admitted) pair.Key.Draws = pair.Value.ToArray();
                bindings = admitted.Keys.ToArray();
                recording = new VesselRuntimeRecording { sessionId = runId + "/" + Guid.NewGuid().ToString("N"), vesselId = report.vesselId, manifestSha256 = report.manifestSha256,
                    topology = 1, drawIds = manifest.draws.Select(d => d.id).ToArray(), partIds = parts.Select(p => p.flightID).ToArray() };
                report.parts = parts.Length; report.draws = recording.drawIds.Length; report.renderers = bindings.Length;
                report.frameBytes = 8 + 8 + 16 * 8 + 3 * 8 + report.draws * 129L + report.parts * 48L;
                headerBytes = 8 + EncodedString(recording.sessionId) + EncodedString(recording.vesselId) + EncodedString(recording.manifestSha256) + 8 + 4 + 4 + report.parts * 4L + 4;
                foreach (string id in recording.drawIds) headerBytes += EncodedString(id);
                GameEvents.onVesselWasModified.Add(Modified); modifiedHook = true;
                GameEvents.onPartDie.Add(Died); diedHook = true;
                report.status = "admitted";
            }
            catch (Exception error)
            {
                report.status = "invalid"; report.reason = "admission: " + error.GetType().Name + ": " + error.Message;
                report.admissionMilliseconds = Milliseconds(Stopwatch.GetTimestamp() - start);
                Dispose(); WriteReport(); throw;
            }
            finally { report.admissionMilliseconds = Milliseconds(Stopwatch.GetTimestamp() - start); }
        }
        static long EncodedString(string value) { return 4L + Encoding.UTF8.GetByteCount(value); }
        static double Milliseconds(long ticks) { return ticks * 1000d / Stopwatch.Frequency; }
        void Modified(Vessel changed) { if (ReferenceEquals(changed, vessel)) { report.topologyEvents++; topologyFailure = "vessel-modified-event"; } }
        void Died(Part part) { if (parts != null && Array.IndexOf(parts, part) >= 0) { report.topologyEvents++; topologyFailure = "admitted-part-died"; } }
        void RequireThread() { Require(System.Threading.Thread.CurrentThread.ManagedThreadId == threadId, "Runtime capture must remain on its admitting Unity thread."); }
        void ValidateTopology()
        {
            RequireThread(); Require(topologyFailure == null, topologyFailure);
            Require(!disposed && ReferenceEquals(vessel, FlightGlobals.ActiveVessel) && vessel != null && vessel.loaded && !vessel.packed && !vessel.HoldPhysics && !FlightDriver.Pause && HighLogic.LoadedSceneIsFlight && TimeWarp.CurrentRate == 1 && Time.timeScale == 1, "Runtime vessel or time domain changed.");
            Require(vessel.parts.Count == parts.Length && vessel.id.ToString("D") == recording.vesselId, "Runtime part membership changed.");
            for (int i = 0; i < parts.Length; i++)
            {
                Part part = parts[i]; Require(part != null && ReferenceEquals(vessel.parts[i], part) && part.flightID == recording.partIds[i] && ReferenceEquals(part.parent, parents[i]) && ReferenceEquals(part.vessel, vessel), "Runtime ordered part topology changed.");
                Require(ReferenceEquals(part.Rigidbody, bodies[i]) && ReferenceEquals(part.rb, localBodies[i]) && (ReferenceEquals(bodies[i], null) || bodies[i] != null), "Runtime rigidbody ownership changed or was destroyed.");
                Require(part.Modules.Count == modules[i].Length, "Runtime module membership changed.");
                for (int j = 0; j < modules[i].Length; j++) Require(modules[i][j] != null && ReferenceEquals(part.Modules[j], modules[i][j]), "Runtime ordered module topology changed.");
                Renderer[] current = OwnedRenderers(part); Require(current.Length == partRenderers[i].Length, "Runtime renderer membership changed.");
                for (int j = 0; j < current.Length; j++) Require(partRenderers[i][j] != null && ReferenceEquals(current[j], partRenderers[i][j]), "Runtime ordered renderer topology changed.");
            }
            foreach (var binding in bindings) Require(binding.Renderer != null && WholeCraftSceneCapture.Stable(binding.Renderer, binding.Part) == binding.Stable, "Runtime admitted draw hierarchy changed.");
        }
        static double[] RootInverse(double[] root)
        {
            double[] local = WholeCraftSceneFormat.RelativeAffine(root, new double[] { 1, 0, 0, root[3], 0, 1, 0, root[7], 0, 0, 1, root[11], 0, 0, 0, 1 });
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++)
            {
                double dot = 0; for (int k = 0; k < 3; k++) dot += root[k * 4 + r] * root[k * 4 + c];
                Require(Math.Abs(dot - (r == c ? 1 : 0)) <= 1e-5, "Runtime root is not rigid; force units would differ.");
            }
            return local;
        }
        static void AddForce(Vector3 force, ref double x, ref double y, ref double z) { x += force.x * 1000d; y += force.y * 1000d; z += force.z * 1000d; }
        public void CaptureFrame()
        {
            Require(!finished, "Runtime capture already finished.");
            long start = Stopwatch.GetTimestamp(), validation = 0, analysis = 0;
            try
            {
                ValidateTopology(); validation = Stopwatch.GetTimestamp() - start;
                Require(frames.Count < VesselRuntimeFormat.MaxFrames && headerBytes + report.frameBytes * (frames.Count + 1) <= VesselRuntimeFormat.MaxBytes, "Runtime recording frame or byte capacity exceeded.");
                double[] world = WholeCraftSceneCapture.World(vessel.transform), inverse = RootInverse(world), root = (double[])world.Clone(); root[3] = root[7] = root[11] = 0;
                var frame = new VesselRuntimeFrame { sequence = frames.Count, universalTime = Planetarium.GetUniversalTime(), rootToWorld = root, altitude = vessel.altitude,
                    density = vessel.atmDensity, speed = vessel.srfSpeed, poses = new double[report.draws * 16], visible = new bool[report.draws], loads = new double[report.parts * 6] };
                var row = new ResultRow { sequence = frame.sequence, unityFrame = Time.frameCount, universalTime = frame.universalTime };
                foreach (var binding in bindings)
                {
                    string source; double[] pose = WholeCraftSceneCapture.RootLocal(binding.Renderer.transform, vessel.transform, world, out source);
                    bool visible = binding.Renderer.enabled && binding.Renderer.gameObject.activeInHierarchy && !binding.Renderer.forceRenderingOff;
                    foreach (int draw in binding.Draws) { Array.Copy(pose, 0, frame.poses, draw * 16, 16); frame.visible[draw] = visible; }
                }
                for (int i = 0; i < parts.Length; i++)
                {
                    Part part = parts[i]; Rigidbody body = bodies[i]; Vector3 center = body != null ? body.worldCenterOfMass : part.partTransform.TransformPoint(part.CoMOffset);
                    if (body == null) row.missingRigidbodies++;
                    if (localBodies[i] == null) row.physicslessParts++;
                    double x = center.x - world[3], y = center.y - world[7], z = center.z - world[11], fx = 0, fy = 0, fz = 0;
                    if (part.ShieldedFromAirstream) row.shieldedParts++;
                    else
                    {
                        AddForce(-part.dragVectorDir * part.dragScalar, ref fx, ref fy, ref fz);
                        if (!part.hasLiftModule && part.DragCubes != null) AddForce(Vector3.ProjectOnPlane(part.transform.rotation * (part.bodyLiftScalar * part.DragCubes.LiftForce), -part.dragVectorDir), ref fx, ref fy, ref fz);
                        foreach (ModuleLiftingSurface surface in surfaces[i]) if (surface.isEnabled && surface.enabled && surface.gameObject.activeInHierarchy)
                        {
                            row.liftingSurfaces++; AddForce(surface.liftForce, ref fx, ref fy, ref fz);
                            if (surface.useInternalDragModel) AddForce(surface.dragForce, ref fx, ref fy, ref fz);
                        }
                    }
                    int at = i * 6;
                    for (int r = 0; r < 3; r++) { frame.loads[at + r] = inverse[r * 4] * x + inverse[r * 4 + 1] * y + inverse[r * 4 + 2] * z; frame.loads[at + 3 + r] = inverse[r * 4] * fx + inverse[r * 4 + 1] * fy + inverse[r * 4 + 2] * fz; }
                }
                VesselRuntimeFormat.ValidateFrame(frame, report.draws, report.parts);
                Require(frames.Count == 0 || frame.universalTime >= frames[frames.Count - 1].universalTime, "Runtime simulation time moved backward.");
                long before = Stopwatch.GetTimestamp(); VesselLoadResult resultant = VesselRuntimeFormat.Analyze(frame); analysis = Stopwatch.GetTimestamp() - before;
                row.force = resultant.force; row.moment = resultant.moment; row.validationMilliseconds = Milliseconds(validation); row.analysisMilliseconds = Milliseconds(analysis);
                row.captureMilliseconds = Milliseconds(Stopwatch.GetTimestamp() - start);
                frames.Add(frame); results.Add(row); report.frames = frames.Count; report.capturedBytes = report.frameBytes * frames.Count; report.status = "capturing";
            }
            catch (Exception error) { report.status = "invalid"; report.reason = "capture: " + error.GetType().Name + ": " + error.Message; throw; }
            finally { report.captureTicks += Stopwatch.GetTimestamp() - start; report.validationTicks += validation; report.analysisTicks += analysis; }
        }
        public void Finish(bool complete, string reason)
        {
            if (finished) return; finished = true;
            RequireThread(); Dispose();
            bool valid = report.status != "invalid" && topologyFailure == null;
            report.status = complete && valid && frames.Count > 0 ? "complete" : valid ? "interrupted" : "invalid";
            if (report.reason == null) report.reason = topologyFailure ?? reason ?? (frames.Count == 0 ? "no-runtime-frames" : "capture-interrupted");
            if (frames.Count == 0 && complete) { report.status = "invalid"; report.reason = "no-runtime-frames"; }
            report.results = results.ToArray();
            long start = Stopwatch.GetTimestamp();
            string staged = output + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                if (frames.Count > 0)
                {
                    recording.frames = frames.ToArray(); VesselRuntimeFormat.Bind(recording, report.manifestSha256, manifest);
                    VesselRuntimeFormat.Write(staged, recording); report.encodedBytes = new FileInfo(staged).Length;
                    report.recordingSha256 = ScaleCheckpointLoadState.HashFile(staged);
                    string target = report.status == "complete" ? output : output + ".partial";
                    File.Move(staged, target); report.recordingPath = target;
                }
                else report.recordingPath = null;
            }
            catch (Exception error)
            {
                report.status = "persistence-failed"; report.reason = error.GetType().Name + ": " + error.Message;
                report.recordingPath = File.Exists(staged) ? staged : null;
            }
            finally { report.persistenceMilliseconds = Milliseconds(Stopwatch.GetTimestamp() - start); WriteReport(); }
        }
        void WriteReport()
        {
            string staged = report.reportPath + ".pending-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(staged, JsonUtility.ToJson(report, true)); File.Move(staged, report.reportPath);
        }
        public void Dispose()
        {
            if (disposed) return; RequireThread(); disposed = true;
            if (modifiedHook) { GameEvents.onVesselWasModified.Remove(Modified); modifiedHook = false; }
            if (diedHook) { GameEvents.onPartDie.Remove(Died); diedHook = false; }
        }
    }
}
