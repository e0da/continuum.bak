using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;

namespace KspContinuum
{
    internal sealed class LiveRigidVesselOwner : IDisposable, IPlayerLoopBracketObserver
    {
        const string PhysicsPhase = "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
        const string ScriptPhase = "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate";
        const string FrameOwner = "continuum.live-rigid-frame";
        static LiveRigidVesselOwner active;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly string output;
        readonly Func<bool> capturing;
        readonly bool controlled;
        readonly Dictionary<Rigidbody, Member> members = new Dictionary<Rigidbody, Member>();
        readonly List<Member> ordered = new List<Member>();
        readonly List<ModuleWheelBase> retractedWheels = new List<ModuleWheelBase>();
        readonly List<Command> commands = new List<Command>();
        readonly HashSet<int> accelerationReceivers = new HashSet<int>();
        readonly List<Step> steps = new List<Step>();
        readonly List<string> screenshots = new List<string>();
        readonly List<PendingView> pendingViews = new List<PendingView>(4);
        readonly float[] captureTimes = { 1.8f, 4.5f, 8.5f, 11.5f };
        int captureIndex;
        readonly List<RigidAssemblyMember> propertyScratch = new List<RigidAssemblyMember>();
        readonly Harmony framePatches = new Harmony(FrameOwner);
        readonly List<MethodBase> frameMethods = new List<MethodBase>();
        readonly MethodInfo[] stageMethods = new MethodInfo[3];
        readonly HashSet<object> stageStates = new HashSet<object>();
        FlightIntegrator stageIntegrator;
        Type stageCoreType;
        FieldInfo stageStateField;
        readonly LiveRigidVesselBridge bridge;
        static readonly FieldInfo StatsComplete = AccessTools.Field(typeof(VesselPrecalculate), "firstStatsRunComplete");
        Vessel vessel;
        Rigidbody body;
        GameObject root;
        GameObject retiredRoot;
        Part[] parts;
        int[] parents, aliases;
        Vector3 commonAcceleration;
        bool commonSet, disposed, entered, frameInstalled, pulseApplied, failed, admissionAttempted, screenshotRequested, screenshotCaptured, motionInitialized;
        int scriptFrame = -1;
        float scriptTime = -1, enteredTime;
        RigidAssemblyProperties properties;
        RetainedRigidAssemblyProperties retainedProperties;
        bool autoSyncTransforms;
        Step pending;
        LiveRigidFlightDriver driver;
        public readonly Receipt Report;
        public bool CaptureReady { get { return !controlled || Report.originalBodiesRestored && Report.bridgeReleased && Report.aggregateRetired &&
            Report.postRestorationSimulatedSeconds >= 1.9999 && driver != null && driver.Report.passed; } }
        public bool Invalid { get { return failed || Report.status == "invalid"; } }

        [Serializable] public sealed class Receipt
        {
            public string schema = "continuum-live-rigid-owner/v1", runId, status = "installing", detail, cleanupStatus = "not-installed";
            public string screenshotPath;
            public string scenario = "quiet-pulse";
            public string[] screenshots;
            public string imageEncodingPolicy = "inline", imageEncodingStatus = "not-started";
            public long imageReadbackTicks, imageEncodeTicks, imageFileWriteTicks, imageEncodingBeginTicks, imageEncodingEndTicks;
            public int imageReadbackCalls, imageEncodeCalls, imageFileWriteCalls, pendingViewCount, peakPendingViews;
            public string imageCostScope = "Image readback includes owned Texture2D creation, original end-of-frame ReadPixels and Apply. Controlled PNG encoding and fresh-file writes run only after capture is inactive, using those retained pixels. Readback remains inside the capture window; costs include timer overhead and do not establish playable FPS.";
            public LiveRigidFlightDriver.Receipt controlDriver;
            public string scope = "Fixed topology, no-contact vacuum, normal time. Original Part/Rigidbody identities retained as kinematic proxies; independent default-scene aggregate advances in unchanged native physics. Rigid projection, native mass weighting and zero aggregate damping are declared model choices. No stock trajectory, contact, arbitrary mod writer, long-flight, feel or performance qualification.";
            public int sourceParts, sourceBodies, retractedWheelParts, aggregateId, physicsSteps, postRestorationNativeSteps, routedCommands, omittedZeroCommands, accelerationCalls, duplicateAccelerationReceivers;
            public int velocityReads, angularReads, centerReads, pointReads, statsReads, frameVelocityChanges, frameTranslations;
            public int propertyRebuilds;
            public long costClockFrequency = Stopwatch.Frequency;
            public long auditTicks, driverAuditTicks, refreshTicks, combineTicks, publishTicks, publishPlanTicks, publishWriteTicks, publishReadbackTicks, publishSyncTicks, bridgeCheckTicks;
            public int auditCalls, driverAuditCalls, refreshCalls, combineCalls, publishCalls, publishedMembers, publishSyncCalls, bridgeCheckCalls;
            public string publicationMode = "explicit-transform-and-native", propertyReductionMode = "reference";
            public bool originalAutoSyncTransforms, autoSyncTransformsUnchanged;
            public string costScope = "Synchronous owner method wall costs over its lifetime. Driver audit is nested in audit; combine and property-triggered publication are nested in refresh; planning/write/readback are nested in publication. Periodic bridge checks exclude admission/cleanup. Nested costs are not additive and include timer overhead.";
            public double maxPropertyPositionShift, maxPropertyRotationShift;
            public double ownerStartUt, ownerEndUt, postRestorationSimulatedSeconds;
            public bool pulseQueued, originalBodiesRestored, aggregateRetired, bridgeReleased, passed;
            public bool stockContinuationVerified;
            public double maxProxyPositionError, maxProxyRotationError, removedKineticEnergy;
            public BodyState initial, restoredAggregate;
            public RestoredRow[] restoredRows;
            public Step[] steps;
            public LiveRigidVesselBridge.CallsiteRow[] bridgeSites;
            public BoundedStageCost[] stageCosts;
            public string stageStatus = "not-requested", stageCleanupStatus = "not-installed";
            public int stageFaults;
            public bool stageHooksRemoved;
            public string stageCostScope = "Controlled native ownership only. Three inclusive original method wall envelopes: exact active FI.FixedUpdate, root-only FI.Integrate and actual bound MechJeb AnalyzeParts. Integrate is nested in FI; MechJeb may overlap other declared scopes. Never add inclusive stages together. Entry filtering and finalizer dispatch/timer overhead are not independently removed. Zero calls means no observed execution, not zero cost. Original exceptions remain unchanged.";
        }
        [Serializable] public sealed class BoundedStageCost
        {
            public int slot, metadataToken, boundInstances, activeCalls;
            public string assembly, assemblySha256, assemblyMvid, declaringType, method, signature, ilSha256;
            public long calls, ticks, originalExceptions, clockFrequency = Stopwatch.Frequency;
            public bool installed, removed;
        }
        struct StageState
        {
            public LiveRigidVesselOwner owner;
            public long start;
            public int slot;
        }
        [Serializable] public sealed class RestoredRow
        {
            public int originalId, readbackId;
            public bool originalKinematic, readbackKinematic, originalDetectCollisions, readbackDetectCollisions;
            public float[] expectedVelocity, readbackVelocity, expectedAngularVelocity, readbackAngularVelocity;
        }
        [Serializable] public sealed class BodyState
        {
            public int id;
            public float mass;
            public float[] position, rotation, center, velocity, angularVelocity, inertia, inertiaRotation;
        }
        [Serializable] public sealed class Command
        {
            public int sourceId, mode;
            public bool torque, atPoint;
            public float[] value, point;
            public BodyState source;
        }
        [Serializable] public sealed class Step
        {
            public int index, frame, commonReceivers, dynamicProxies, commands;
            public float fixedTime, dt;
            public double universalTime;
            public string controlPhase, warpMode;
            public float warpRate;
            public int warpRateIndex;
            public float[] controls;
            public BodyState before, after;
            public double[] force, torque, impulse, angularImpulse;
            public float[] commonAcceleration;
            public Command[] nonzeroCommands;
            public double proxyPositionError, proxyRotationError;
        }
        sealed class Member
        {
            public Rigidbody Body;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Kinematic, DetectCollisions;
            public RigidAssemblyMember Properties;
            public int Depth;
            public int LocalId;
            public Vector3 InitialVelocity, InitialAngularVelocity;
            public Vector3 InitialPosition;
            public Quaternion InitialRotation;
            public float NativeMass;
            public Vector3 NativeCenter, NativeInertia;
            public Quaternion NativeInertiaRotation;
            public Vector3 PublishedPosition;
            public Quaternion PublishedRotation;
        }
        sealed class PendingView
        {
            public Texture2D image;
            public string path;
        }

        public LiveRigidVesselOwner(string output, string runId, Func<bool> capturing)
        {
            Need(active == null, "Another live rigid owner exists.");
            Need(Path.IsPathRooted(output) && !File.Exists(output) && Directory.Exists(Path.GetDirectoryName(output)), "Fresh absolute private receipt required.");
            this.output = output; this.capturing = capturing;
            Report = new Receipt { runId = runId };
            string scenario = Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_SCENARIO");
            Need(string.IsNullOrEmpty(scenario) || scenario == "control-warp", "Unknown live rigid scenario.");
            controlled = scenario == "control-warp";
            if (controlled)
            {
                Report.schema = "continuum-live-rigid-owner/v2"; Report.scenario = scenario;
                Report.imageEncodingPolicy = "retained-readback-encode-after-capture";
                Report.scope = "Twelve simulated seconds of original pilot attitude/RCS input, LOW 4x physics warp and normal return. Fixed topology, no-contact vacuum, original identities and default-scene native aggregate. Rigid projection, zero damping and material velocity preservation at mass/COM changes are declared model choices. Fuel-flow momentum, contact, arbitrary mods, human control feel and FPS remain unqualified.";
            }
            bridge = new LiveRigidVesselBridge();
            try
            {
                Need(bridge.Installed, "Rigid body bridge not installed.");
                InstallFramePatches(); Report.status = "waiting-for-native-boundary";
            }
            catch { Dispose(); throw; }
        }

        public static bool TryGet(Rigidbody receiver, out LiveRigidVesselOwner owner)
        {
            owner = active;
            return owner != null && receiver != null && owner.members.ContainsKey(receiver);
        }
        public Vector3 ReadVelocity(Rigidbody receiver) { Report.velocityReads++; return ReadPointVelocity(receiver, ReadWorldCenter(receiver)); }
        public Vector3 ReadAngularVelocity(Rigidbody receiver) { Report.angularReads++; return body.angularVelocity; }
        public Vector3 ReadWorldCenter(Rigidbody receiver)
        { Report.centerReads++; Member member = members[receiver]; return body.position + Normalized(body.rotation) * (member.Position + member.Rotation * receiver.centerOfMass); }
        public Vector3 ReadPointVelocity(Rigidbody receiver, Vector3 point) { Report.pointReads++; return body.GetPointVelocity(point); }
        public void WriteVelocity(Rigidbody receiver, Vector3 value)
        { Abort("Unsupported direct constituent velocity writer."); receiver.velocity = value; }
        public void WriteAngularVelocity(Rigidbody receiver, Vector3 value)
        { Abort("Unsupported direct constituent angular velocity writer."); receiver.angularVelocity = value; }

        public void RecordIntegrationAcceleration(Rigidbody receiver, Vector3 value, ForceMode mode)
        {
            RequireThread(); Need(mode == ForceMode.Acceleration, "FI common acceleration mode changed.");
            Need(Finite(value), "Nonfinite common acceleration.");
            if (commonSet) Need(Same(value, commonAcceleration), "FI common acceleration differs between receivers.");
            else { commonAcceleration = value; commonSet = true; }
            Report.accelerationCalls++;
            if (!accelerationReceivers.Add(receiver.GetInstanceID())) Report.duplicateAccelerationReceivers++;
        }
        public void Record(Rigidbody receiver, Vector3 value, Vector3 point, ForceMode mode, bool torque, bool atPoint)
        {
            RequireThread(); Need(Finite(value) && (!atPoint || Finite(point)), "Nonfinite routed command.");
            Need(mode == ForceMode.Force || mode == ForceMode.Impulse || mode == ForceMode.Acceleration || mode == ForceMode.VelocityChange, "Unknown force mode.");
            Report.routedCommands++;
            if (Same(value, Vector3.zero)) { Report.omittedZeroCommands++; return; }
            Need(commands.Count < 16384, "Per-step nonzero command bound exceeded.");
            commands.Add(new Command { sourceId = receiver.GetInstanceID(), mode = (int)mode, torque = torque, atPoint = atPoint,
                value = Array(value), point = Array(atPoint ? point : ReadWorldCenter(receiver)), source = State(receiver, true) });
        }

        public void Before(string phase, int frame, double time)
        {
            if (disposed || failed || !capturing()) return;
            try
            {
                RequireThread();
                if (phase == ScriptPhase)
                {
                    scriptFrame = frame; scriptTime = (float)time;
                    if (entered) Audit();
                }
                if (phase != PhysicsPhase || !entered) return;
                Need(scriptFrame == frame && scriptTime == (float)time, "Script/native physics ordering changed.");
                Audit(); RefreshProperties();
                Need(commonSet && accelerationReceivers.Count == members.Count && Report.duplicateAccelerationReceivers == 0, "Incomplete/duplicate native common acceleration coverage.");
                Vec force = new Vec(), torque = new Vec(), impulse = new Vec(), angularImpulse = new Vec();
                foreach (Command command in commands)
                {
                    Vec value = Vector(command.value);
                    if (command.mode == (int)ForceMode.Acceleration || command.mode == (int)ForceMode.VelocityChange)
                        value = command.torque ? InertiaMultiply(command.source, value) : value * command.source.mass;
                    Vec moment = command.torque ? value : Vec.Cross(Subtract(Vector(command.point), Vector(body.worldCenterOfMass)), value);
                    bool instantaneous = command.mode == (int)ForceMode.Impulse || command.mode == (int)ForceMode.VelocityChange;
                    if (instantaneous) { if (!command.torque) impulse += value; angularImpulse += moment; }
                    else { if (!command.torque) force += value; torque += moment; }
                }
                Need(steps.Count < 1024, "Native step receipt bound exceeded.");
                pending = new Step { index = steps.Count, frame = frame, fixedTime = (float)time, dt = Time.fixedDeltaTime,
                    universalTime = Planetarium.GetUniversalTime(),
                    controlPhase = driver == null ? "quiet-pulse" : driver.Phase,
                    warpMode = TimeWarp.WarpMode.ToString(), warpRate = TimeWarp.CurrentRate, warpRateIndex = TimeWarp.CurrentRateIndex,
                    controls = new[] { vessel.ctrlState.pitch, vessel.ctrlState.yaw, vessel.ctrlState.roll, vessel.ctrlState.X, vessel.ctrlState.Y, vessel.ctrlState.Z },
                    before = State(body, false), commonReceivers = accelerationReceivers.Count,
                    commonAcceleration = Array(commonSet ? commonAcceleration : Vector3.zero), commands = commands.Count,
                    nonzeroCommands = commands.ToArray(), force = Array(force), torque = Array(torque), impulse = Array(impulse), angularImpulse = Array(angularImpulse) };
                if (commonSet) body.AddForce(commonAcceleration, ForceMode.Acceleration);
                body.AddForce(Unity(force), ForceMode.Force); body.AddTorque(Unity(torque), ForceMode.Force);
                body.AddForce(Unity(impulse), ForceMode.Impulse); body.AddTorque(Unity(angularImpulse), ForceMode.Impulse);
                commands.Clear(); accelerationReceivers.Clear(); commonSet = false;
            }
            catch (Exception error) { Abort(error.ToString()); }
        }
        public void After(string phase, int frame, double time)
        {
            if (disposed || failed || !capturing()) return;
            float elapsed = Time.fixedTime - enteredTime;
            bool captureDue = controlled ? captureIndex < captureTimes.Length && elapsed >= captureTimes[captureIndex] : !screenshotRequested && elapsed > .3f;
            if (entered && captureDue)
            {
                screenshotRequested = true; int index = captureIndex++;
                try { ScalingQualification.CaptureAtEndOfFrame(() => CaptureView(index)); }
                catch (Exception error) { Abort("Render scheduling: " + error); }
            }
            if (phase != PhysicsPhase) return;
            try
            {
                if (!entered)
                {
                    if (!admissionAttempted) { admissionAttempted = true; Admit(); }
                    else
                    {
                        Need(ordered.All(member => member.Body != null && member.Body.isKinematic == member.Kinematic &&
                            member.Body.detectCollisions == member.DetectCollisions && Finite(member.Body.velocity) && Finite(member.Body.angularVelocity)),
                            "Original body ownership/motion changed after handback.");
                        if (controlled) Need(Report.bridgeReleased && driver.Report.controlsRestored && driver.Report.groupsRestored && driver.Report.warpRestored &&
                            vessel.ctrlState.pitch == 0 && vessel.ctrlState.yaw == 0 && vessel.ctrlState.roll == 0 && vessel.ctrlState.X == 0 && vessel.ctrlState.Y == 0 && vessel.ctrlState.Z == 0 &&
                            TimeWarp.CurrentRate == 1 && TimeWarp.CurrentRateIndex == 0, "Controlled stock continuation changed.");
                        Report.postRestorationNativeSteps++; Report.aggregateRetired = retiredRoot == null;
                        Report.postRestorationSimulatedSeconds += Time.fixedDeltaTime;
                        Report.stockContinuationVerified = Report.postRestorationNativeSteps >= 2;
                    }
                    return;
                }
                Need(pending != null && pending.frame == frame && pending.fixedTime == (float)time, "Missing matched native boundary.");
                pending.after = State(body, false); Publish(pending);
                steps.Add(pending); pending = null; Report.physicsSteps = steps.Count;
                if (steps.Count % 16 == 0) Need(CheckBridge(), "Live bridge graph changed during native ownership.");
                Report.status = "running";
                if (driver != null) driver.AfterPhysics();
                if (Time.fixedTime - enteredTime >= (controlled ? 12f : 1.5f)) { Restore(); Report.status = "restored-awaiting-stock-steps"; }
            }
            catch (Exception error) { Abort(error.ToString()); }
        }
        void CaptureView(int index)
        {
            if (disposed || failed || !entered || !capturing()) return;
            Texture2D image = null; RenderTexture previous = RenderTexture.active;
            try
            {
                long readbackStart = Stopwatch.GetTimestamp();
                try
                {
                    RenderTexture.active = null; image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0); image.Apply();
                }
                finally { Report.imageReadbackTicks += Stopwatch.GetTimestamp() - readbackStart; Report.imageReadbackCalls++; }
                string path = output + (controlled ? ".view-" + index : "") + ".png"; Need(!File.Exists(path), "Rigid flight screenshot already exists.");
                if (controlled)
                {
                    Need(pendingViews.Count < 4 && !pendingViews.Any(view => view.path == path), "Controlled image pending ownership bound/identity differs.");
                    pendingViews.Add(new PendingView { image = image, path = path }); image = null;
                    Report.pendingViewCount = pendingViews.Count; Report.peakPendingViews = Math.Max(Report.peakPendingViews, pendingViews.Count);
                    Report.imageEncodingStatus = "retained-readbacks";
                }
                else { File.WriteAllBytes(path, image.EncodeToPNG()); screenshots.Add(path); Report.screenshotPath = path; screenshotCaptured = true; }
            }
            catch (Exception error) { Abort("Render readback: " + error); }
            finally { RenderTexture.active = previous; if (image != null) UnityEngine.Object.Destroy(image); }
        }
        void FlushPendingViews()
        {
            if (!controlled || Report.imageEncodingStatus == "completed") return;
            bool captureInactive = false;
            try { captureInactive = !capturing(); }
            catch (Exception error) { ImageFailure("capture-state: " + error); }
            try
            {
                if (!captureInactive)
                {
                    ImageFailure("Controlled image encoding interrupted while capture remained active; retained views discarded.");
                    Report.imageEncodingStatus = "discarded-active-capture";
                    return;
                }
                if (pendingViews.Count > 0) Report.imageEncodingBeginTicks = Stopwatch.GetTimestamp();
                bool complete = pendingViews.Count == captureTimes.Length;
                foreach (PendingView view in pendingViews)
                {
                    try
                    {
                        Need(view.image != null && !File.Exists(view.path), "Retained image ownership/fresh path differs.");
                        byte[] bytes; long encodeStart = Stopwatch.GetTimestamp();
                        try { bytes = view.image.EncodeToPNG(); }
                        finally { Report.imageEncodeTicks += Stopwatch.GetTimestamp() - encodeStart; Report.imageEncodeCalls++; }
                        Need(bytes != null && bytes.Length > 0, "Retained PNG encoder returned no bytes.");
                        long writeStart = Stopwatch.GetTimestamp();
                        try { using (var stream = new FileStream(view.path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length); }
                        finally { Report.imageFileWriteTicks += Stopwatch.GetTimestamp() - writeStart; Report.imageFileWriteCalls++; }
                        screenshots.Add(view.path); Report.screenshotPath = view.path; screenshotCaptured = true;
                    }
                    catch (Exception error) { complete = false; ImageFailure("retained-image: " + error); }
                }
                Report.imageEncodingStatus = complete ? "completed" : "incomplete";
            }
            finally
            {
                foreach (PendingView view in pendingViews)
                {
                    try { if (view.image != null) UnityEngine.Object.Destroy(view.image); }
                    catch (Exception error) { ImageFailure("retained-image cleanup: " + error); Report.imageEncodingStatus = "cleanup-error"; }
                    view.image = null;
                }
                pendingViews.Clear(); Report.pendingViewCount = 0; Report.imageEncodingEndTicks = Stopwatch.GetTimestamp();
            }
        }
        void ImageFailure(string detail)
        {
            Report.status = "invalid"; failed = true;
            Report.detail = (Report.detail ?? "") + " Image: " + detail;
        }
        public void Fault(string phase, Exception error) { if (!disposed) Abort(phase + ": " + error); }

        void Admit()
        {
            RequireThread(); Need(active == null, "Live authority already assigned.");
            vessel = FlightGlobals.ActiveVessel; Eligible(); VerifyLoopOrder(PlayerLoop.GetCurrentPlayerLoop());
            Need(bridge.ExactInstalled() && vessel.precalc != null && vessel.precalc.GetType() == typeof(VesselPrecalculate) && StatsComplete != null && StatsComplete.FieldType == typeof(bool), "Reader/bridge graph admission changed.");
            parts = vessel.parts.ToArray(); parents = new int[parts.Length]; aliases = new int[parts.Length];
            if (controlled) BindStages();
            var found = new HashSet<Rigidbody>();
            for (int i = 0; i < parts.Length; i++)
            {
                Part part = parts[i]; Need(part != null && part.servoRb == null, "Servo ownership excluded.");
                parents[i] = part.parent == null ? 0 : part.parent.GetInstanceID(); aliases[i] = part.rb == null ? 0 : part.rb.GetInstanceID();
                foreach (PartModule module in part.Modules)
                {
                    ModuleWheelBase wheel = module as ModuleWheelBase;
                    if (wheel == null) continue;
                    RetractedWheel(wheel); retractedWheels.Add(wheel);
                }
                if (part.rb != null) found.Add(part.rb);
                foreach (Rigidbody candidate in part.transform.GetComponentsInChildren<Rigidbody>(true)) if (!candidate.isKinematic) found.Add(candidate);
            }
            Need(found.Count > 0 && found.Count <= 2048, "Dynamic native body census outside bounds.");
            Vector3 position = vessel.rootPart.rb.position; Quaternion rotation = Normalized(vessel.rootPart.rb.rotation), inverse = Quaternion.Inverse(rotation);
            var motions = new List<RigidAssemblyMotion>();
            foreach (Rigidbody candidate in found.OrderBy(candidate => candidate.GetInstanceID()))
            {
                Need(candidate != null && !candidate.isKinematic && candidate.gameObject.activeInHierarchy && candidate.constraints == RigidbodyConstraints.None, "Unqualified original native body.");
                var member = new Member { Body = candidate, Position = inverse * (candidate.position - position), Rotation = Normalized(inverse * candidate.rotation),
                    Kinematic = candidate.isKinematic, DetectCollisions = candidate.detectCollisions, Depth = Depth(candidate.transform), LocalId = members.Count,
                    InitialVelocity = candidate.velocity, InitialAngularVelocity = candidate.angularVelocity, InitialPosition = candidate.position, InitialRotation = candidate.rotation };
                member.Properties = Properties(member);
                motions.Add(new RigidAssemblyMotion(member.Properties, Vector(inverse * candidate.velocity), Vector(inverse * candidate.angularVelocity)));
                members.Add(candidate, member); ordered.Add(member);
            }
            ordered.Sort((a, b) => a.Depth == b.Depth ? a.Body.GetInstanceID().CompareTo(b.Body.GetInstanceID()) : a.Depth.CompareTo(b.Depth));
            autoSyncTransforms = Physics.autoSyncTransforms; Report.originalAutoSyncTransforms = autoSyncTransforms;
            if (controlled)
            {
                retainedProperties = new RetainedRigidAssemblyProperties(ordered.Select(member => member.Properties));
                Need(retainedProperties.Count == ordered.Count, "Retained property topology differs.");
                foreach (Member member in ordered) Need(retainedProperties.GetId(member.LocalId) == member.LocalId, "Retained property slot differs.");
                Report.propertyReductionMode = "retained-canonical-two-pass"; Report.publicationMode = "parent-first-transform-batch-one-global-sync";
            }
            RigidAssemblyProjection projection = RigidAssemblyProjection.Project(motions); properties = projection.Properties;
            root = new GameObject("Continuum live rigid authority"); root.transform.SetPositionAndRotation(position, rotation);
            body = root.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false; body.detectCollisions = false;
            body.drag = 0; body.angularDrag = 0; body.maxAngularVelocity = 100;
            PublishProperties();
            try
            {
                foreach (Member member in ordered) { member.Body.detectCollisions = false; member.Body.isKinematic = true; }
                body.isKinematic = false; body.velocity = rotation * Unity(projection.Velocity); body.angularVelocity = rotation * Unity(projection.AngularVelocity); body.WakeUp();
                entered = true; active = this; enteredTime = Time.fixedTime;
                Report.ownerStartUt = Planetarium.GetUniversalTime();
                Report.sourceParts = parts.Length; Report.sourceBodies = found.Count; Report.retractedWheelParts = retractedWheels.Count; Report.aggregateId = body.GetInstanceID();
                Report.removedKineticEnergy = projection.RemovedKineticEnergy; Report.initial = State(body, false); Publish(null);
                motionInitialized = true;
                if (controlled) { driver = new LiveRigidFlightDriver(vessel, enteredTime); Report.controlDriver = driver.Report; }
            }
            catch { Restore(); throw; }
        }
        void Eligible()
        {
            Need(vessel != null && FlightGlobals.ActiveVessel == vessel && vessel.loaded && !vessel.packed && !vessel.HoldPhysics &&
                !FlightDriver.Pause && WarpAdmitted() && vessel.situation == Vessel.Situations.ORBITING &&
                vessel.mainBody != null && vessel.altitude > vessel.mainBody.atmosphereDepth && vessel.ctrlState.mainThrottle < .01f, "Fixed vacuum lifecycle changed.");
        }
        bool WarpAdmitted()
        {
            bool normal = TimeWarp.CurrentRate == 1 && TimeWarp.CurrentRateIndex == 0 && Time.timeScale == 1 && Math.Abs(Time.fixedDeltaTime - .02f) < 1e-6f;
            if (normal) return true;
            return controlled && driver != null && TimeWarp.WarpMode == TimeWarp.Modes.LOW && TimeWarp.CurrentRate == 4 &&
                TimeWarp.CurrentRateIndex == 3 && Time.timeScale == 4 && Math.Abs(Time.fixedDeltaTime - .08f) < 1e-6f;
        }
        void Audit()
        {
            long start = Stopwatch.GetTimestamp();
            try { AuditCore(); }
            finally { Report.auditTicks += Stopwatch.GetTimestamp() - start; Report.auditCalls++; }
        }
        bool CheckBridge()
        {
            long start = Stopwatch.GetTimestamp();
            try { return bridge.ExactInstalled(); }
            finally { Report.bridgeCheckTicks += Stopwatch.GetTimestamp() - start; Report.bridgeCheckCalls++; }
        }
        void AuditCore()
        {
            Eligible(); Need(vessel.parts.Count == parts.Length, "Part count changed.");
            if (controlled) Need(Physics.autoSyncTransforms == autoSyncTransforms, "Global physics synchronization setting changed.");
            if (driver != null)
            {
                long start = Stopwatch.GetTimestamp();
                try { driver.Audit(); }
                finally { Report.driverAuditTicks += Stopwatch.GetTimestamp() - start; Report.driverAuditCalls++; }
            }
            foreach (ModuleWheelBase wheel in retractedWheels) RetractedWheel(wheel);
            for (int i = 0; i < parts.Length; i++) Need(vessel.parts[i] == parts[i] && parts[i] != null &&
                (parts[i].parent == null ? 0 : parts[i].parent.GetInstanceID()) == parents[i] &&
                (parts[i].rb == null ? 0 : parts[i].rb.GetInstanceID()) == aliases[i] && parts[i].servoRb == null, "Topology or alias changed.");
            Need(body != null && !body.isKinematic && !body.detectCollisions, "Aggregate ownership changed.");
            foreach (Member member in ordered) Need(member.Body != null && member.Body.isKinematic && !member.Body.detectCollisions, "Proxy ownership changed.");
            Vector3 aggregatePosition = body.position; Quaternion aggregateRotation = Normalized(body.rotation);
            foreach (Member member in ordered)
            {
                Vector3 position = aggregatePosition + aggregateRotation * member.Position; Quaternion rotation = Normalized(aggregateRotation * member.Rotation);
                double positionError = Math.Max((member.Body.transform.position - position).magnitude, (member.Body.position - position).magnitude);
                double rotationError = Math.Max(RotationError(member.Body.transform.rotation, rotation), RotationError(member.Body.rotation, rotation));
                if (!(positionError <= 1e-4 && rotationError <= .002)) Need(false, "Unrouted constituent pose writer: id=" + member.Body.GetInstanceID() +
                    " transformPositionError=" + positionError + " transformRotationError=" + rotationError +
                    " nativePositionError=" + (member.Body.position - position).magnitude);
            }
        }
        static void RetractedWheel(ModuleWheelBase wheel)
        {
            Need(wheel != null && wheel.GetType() == typeof(ModuleWheelBase), "Unqualified wheel module.");
            ModuleWheels.ModuleWheelDeployment deployment = wheel.part.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>();
            Need(deployment != null && deployment.GetType() == typeof(ModuleWheels.ModuleWheelDeployment) &&
                deployment.stateString == "Retracted" && deployment.position == deployment.retractedPosition &&
                !wheel.isGrounded && wheel.Wheel != null && !wheel.Wheel.enabled && !wheel.Wheel.IsGrounded &&
                wheel.Wheel.wheelCollider != null && !wheel.Wheel.wheelCollider.enabled,
                "Only inactive, fully retracted stock wheels admitted.");
        }
        void QueuePulse()
        {
            if (controlled) return;
            if (pulseApplied || Time.fixedTime - enteredTime < .2f) return;
            Part target = null; float largestArm = -1;
            foreach (Part candidate in parts)
            {
                if (candidate.rb == null || !members.ContainsKey(candidate.rb)) continue;
                float arm = (candidate.transform.position - body.worldCenterOfMass).sqrMagnitude;
                if (arm > largestArm) { largestArm = arm; target = candidate; }
            }
            Need(target != null && largestArm > 1, "Off-center source queue target missing.");
            Vector3 point = target.transform.position, lever = point - body.worldCenterOfMass;
            Vector3 direction = Vector3.Cross(lever, Normalized(body.rotation) * Vector3.forward);
            if (direction.sqrMagnitude < .001f) direction = Vector3.Cross(lever, Normalized(body.rotation) * Vector3.up);
            Vector3 force = direction.normalized * (body.mass * .25f / Time.fixedDeltaTime);
            target.AddForceAtPosition(force, point); pulseApplied = true; Report.pulseQueued = true;
        }
        void RefreshProperties()
        {
            long start = Stopwatch.GetTimestamp();
            try { RefreshPropertiesCore(); }
            finally { Report.refreshTicks += Stopwatch.GetTimestamp() - start; Report.refreshCalls++; }
        }
        void RefreshPropertiesCore()
        {
            bool changed = false; propertyScratch.Clear();
            foreach (Member member in ordered)
            {
                Rigidbody source = member.Body;
                float mass = source.mass; Vector3 center = source.centerOfMass, inertia = source.inertiaTensor; Quaternion inertiaRotation = source.inertiaTensorRotation;
                if (member.NativeMass != mass || !Same(member.NativeCenter, center) || !Same(member.NativeInertia, inertia) || !Same(member.NativeInertiaRotation, inertiaRotation))
                {
                    member.Properties = new RigidAssemblyMember(member.LocalId, mass, Vector(member.Position), Rotate(member.Rotation), Vector(center), Vector(inertia), Rotate(inertiaRotation));
                    member.NativeMass = mass; member.NativeCenter = center; member.NativeInertia = inertia; member.NativeInertiaRotation = inertiaRotation;
                    if (retainedProperties != null) retainedProperties.Update(member.LocalId, member.Properties);
                    changed = true;
                }
                if (retainedProperties == null) propertyScratch.Add(member.Properties);
            }
            if (!changed) return;
            Report.propertyRebuilds++;
            Vector3 oldCenter = body.worldCenterOfMass, velocity = body.velocity, omega = body.angularVelocity;
            Vector3 oldPosition = body.position; Quaternion oldRotation = body.rotation;
            long start = Stopwatch.GetTimestamp();
            try { properties = retainedProperties == null ? RigidAssemblyProperties.Combine(propertyScratch) : retainedProperties.Combine(); }
            finally { Report.combineTicks += Stopwatch.GetTimestamp() - start; Report.combineCalls++; }
            PublishProperties();
            body.velocity = velocity + Vector3.Cross(omega, body.worldCenterOfMass - oldCenter);
            Report.maxPropertyPositionShift = Math.Max(Report.maxPropertyPositionShift, (body.position - oldPosition).magnitude);
            Report.maxPropertyRotationShift = Math.Max(Report.maxPropertyRotationShift, RotationError(body.rotation, oldRotation));
            Publish(null);
        }
        void PublishProperties()
        {
            body.mass = (float)properties.Mass; body.centerOfMass = Unity(properties.CenterOfMass);
            body.inertiaTensor = Unity(properties.PrincipalInertia); body.inertiaTensorRotation = Unity(properties.PrincipalRotation);
            Need(float.IsNaN(body.mass) == false && float.IsInfinity(body.mass) == false && body.mass > 0, "Native mass admission failed.");
        }
        void Publish(Step step)
        {
            long start = Stopwatch.GetTimestamp();
            try { PublishCore(step); }
            finally { Report.publishTicks += Stopwatch.GetTimestamp() - start; Report.publishCalls++; }
        }
        void PublishCore(Step step)
        {
            Vector3 aggregatePosition = body.position; Quaternion aggregateRotation = Normalized(body.rotation);
            foreach (Member member in ordered)
            {
                long start = Stopwatch.GetTimestamp();
                Vector3 position = aggregatePosition + aggregateRotation * member.Position; Quaternion rotation = Normalized(aggregateRotation * member.Rotation);
                member.PublishedPosition = position; member.PublishedRotation = rotation;
                Report.publishPlanTicks += Stopwatch.GetTimestamp() - start;
                start = Stopwatch.GetTimestamp();
                member.Body.transform.SetPositionAndRotation(position, rotation);
                if (!controlled) { member.Body.position = position; member.Body.rotation = rotation; }
                Report.publishWriteTicks += Stopwatch.GetTimestamp() - start;
                if (!controlled) ValidatePublished(member, step);
            }
            if (controlled)
            {
                long start = Stopwatch.GetTimestamp();
                try { Need(Physics.autoSyncTransforms == autoSyncTransforms, "Global physics synchronization setting changed during publication."); Physics.SyncTransforms(); }
                finally { Report.publishSyncTicks += Stopwatch.GetTimestamp() - start; Report.publishSyncCalls++; }
                foreach (Member member in ordered) ValidatePublished(member, step);
            }
        }
        void ValidatePublished(Member member, Step step)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                Vector3 position = member.PublishedPosition; Quaternion rotation = member.PublishedRotation;
                double pe = Math.Max((member.Body.position - position).magnitude, (member.Body.transform.position - position).magnitude);
                double re = Math.Max(RotationError(member.Body.rotation, rotation), RotationError(member.Body.transform.rotation, rotation));
                Report.maxProxyPositionError = Math.Max(Report.maxProxyPositionError, pe); Report.maxProxyRotationError = Math.Max(Report.maxProxyRotationError, re);
                if (step != null) { step.proxyPositionError = Math.Max(step.proxyPositionError, pe); step.proxyRotationError = Math.Max(step.proxyRotationError, re); if (!member.Body.isKinematic) step.dynamicProxies++; }
                Need(pe <= 1e-4 && re <= .002, "Published native/Transform pose does not match its retained target.");
            }
            finally { Report.publishReadbackTicks += Stopwatch.GetTimestamp() - start; Report.publishedMembers++; }
        }
        RigidAssemblyMember Properties(Member member)
        {
            Rigidbody source = member.Body;
            member.NativeMass = source.mass; member.NativeCenter = source.centerOfMass;
            member.NativeInertia = source.inertiaTensor; member.NativeInertiaRotation = source.inertiaTensorRotation;
            return new RigidAssemblyMember(member.LocalId, member.NativeMass, Vector(member.Position), Rotate(member.Rotation),
                Vector(member.NativeCenter), Vector(member.NativeInertia), Rotate(member.NativeInertiaRotation));
        }

        void InstallFramePatches()
        {
            AddHook(AccessTools.DeclaredMethod(typeof(VesselPrecalculate), "CalculatePhysicsStats", Type.EmptyTypes), nameof(StatsPrefix));
            if (controlled) InstallStages();
            else AddHook(AccessTools.DeclaredMethod(typeof(FlightIntegrator), "Integrate", new[] { typeof(Part) }), nameof(PulsePrefix));
            foreach (string name in new[] { "ChangeWorldVelocity", "SetWorldVelocity", "OffsetVelocity" }) AddFrame(typeof(Vessel), name, new[] { typeof(Vector3d) });
            AddFrame(typeof(Vessel), "SetPosition", new[] { typeof(Vector3d), typeof(bool) });
            AddFrame(typeof(Vessel), "SetRotation", new[] { typeof(Quaternion), typeof(bool) });
            foreach (string name in new[] { "Pack", "Unpack", "ResumeVelocity" }) AddFrame(typeof(Part), name, Type.EmptyTypes);
            frameInstalled = true;
        }
        void AddFrame(Type type, string name, Type[] parameters)
        {
            AddHook(AccessTools.DeclaredMethod(type, name, parameters), nameof(FramePrefix));
        }
        static string StageHash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string StageAssemblyHash(Assembly assembly)
        { using (var stream = File.OpenRead(assembly.Location)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        void InstallStages()
        {
            Report.stageStatus = "installing";
            var rows = new List<BoundedStageCost>();
            Assembly stock = typeof(FlightIntegrator).Assembly;
            const string stockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
            Need(StageAssemblyHash(stock) == stockSha, "Stage stock assembly content differs.");
            StageMethod(rows, 0, typeof(FlightIntegrator), "FixedUpdate", 100677457, typeof(void), Type.EmptyTypes, stockSha, "10657063-2fc3-43a7-84fa-d39e75e877bf", null);
            StageMethod(rows, 1, typeof(FlightIntegrator), "Integrate", 100677461, typeof(void), new[] { typeof(Part) }, stockSha, "10657063-2fc3-43a7-84fa-d39e75e877bf", null);
            Assembly[] mods = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "MechJeb2").ToArray();
            Need(mods.Length <= 1, "Multiple stage MechJeb assemblies.");
            if (mods.Length == 1)
            {
                const string mechSha = "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929";
                Need(StageAssemblyHash(mods[0]) == mechSha, "Stage MechJeb assembly content differs.");
                stageCoreType = mods[0].GetType("MuMech.MechJebCore", true); Type stateType = mods[0].GetType("MuMech.VesselState", true);
                stageStateField = AccessTools.DeclaredField(stageCoreType, "VesselState");
                Need(stageStateField != null && !stageStateField.IsStatic && stageStateField.IsInitOnly && stageStateField.FieldType == stateType, "Stage MechJeb readonly state ownership differs.");
                StageMethod(rows, 2, stateType, "AnalyzeParts", 100665304, typeof(void), new[] { typeof(Vessel), stateType.GetNestedType("EngineInfo", BindingFlags.Public | BindingFlags.NonPublic), stateType.GetNestedType("IntakeInfo", BindingFlags.Public | BindingFlags.NonPublic) }, mechSha, "30fbe3c4-e77e-4abc-895b-a635d5924a08", "4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b");
            }
            Report.stageCosts = rows.ToArray(); Report.stageCleanupStatus = "installed";
            string[] prefixes = { nameof(StageFiPrefix), nameof(StagePulsePrefix), nameof(StageMechPrefix) };
            foreach (BoundedStageCost row in Report.stageCosts)
            {
                AddHook(stageMethods[row.slot], prefixes[row.slot], nameof(StageFinalizer));
                Need(ExactStageGraph(row.slot), "Stage installed owner graph differs."); row.installed = true;
            }
            Report.stageStatus = "installed";
        }
        void StageMethod(List<BoundedStageCost> rows, int slot, Type type, string name, int token, Type result, Type[] parameters, string sha, string mvid, string expectedIl)
        {
            Need(type != null && parameters.All(t => t != null), "Stage declared signature type absent.");
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
            Need(method != null && !method.IsStatic && method.DeclaringType == type && method.MetadataToken == token && method.ReturnType == result && method.Module.ModuleVersionId == new Guid(mvid) && method.GetMethodBody() != null, "Stage declared source signature differs.");
            string il = StageHash(method.GetMethodBody().GetILAsByteArray());
            Need(expectedIl == null || il == expectedIl, "Stage original IL differs.");
            stageMethods[slot] = method;
            rows.Add(new BoundedStageCost { slot = slot, metadataToken = token, assembly = method.Module.Assembly.GetName().Name,
                assemblySha256 = sha, assemblyMvid = mvid, declaringType = type.FullName, method = name, signature = method.ToString(), ilSha256 = il });
        }
        void BindStages()
        {
            stageIntegrator = FlightIntegrator.ActiveVesselFI;
            Need(stageIntegrator != null && stageIntegrator.GetType() == typeof(FlightIntegrator) && ReferenceEquals(AccessTools.Field(typeof(VesselModule), "vessel").GetValue(stageIntegrator), vessel), "Stage active FI owner differs.");
            Report.stageCosts[0].boundInstances = 1; Report.stageCosts[1].boundInstances = 1;
            if (stageCoreType == null) return;
            foreach (Part part in parts) foreach (PartModule module in part.Modules)
            {
                if (module.GetType() != stageCoreType) continue;
                object state = stageStateField.GetValue(module);
                Need(state != null && state.GetType() == stageStateField.FieldType && stageStates.Add(state), "Stage MechJeb state identity differs.");
            }
            Report.stageCosts[2].boundInstances = stageStates.Count;
        }
        bool ExactStageGraph(int slot)
        {
            MethodInfo method = stageMethods[slot];
            if (bridge.HasExactFrameHooks(method)) return true;
            Patches graph = Harmony.GetPatchInfo(method);
            string prefix = slot == 0 ? nameof(StageFiPrefix) : slot == 1 ? nameof(StagePulsePrefix) : nameof(StageMechPrefix);
            return graph != null && graph.Owners.Count() == 1 && graph.Owners.Contains(FrameOwner) && graph.Transpilers.Count == 0 && graph.Postfixes.Count == 0 && graph.Prefixes.Count == 1 && graph.Finalizers.Count == 1 &&
                graph.Prefixes[0].owner == FrameOwner && graph.Prefixes[0].PatchMethod == AccessTools.DeclaredMethod(typeof(LiveRigidVesselOwner), prefix) &&
                graph.Finalizers[0].owner == FrameOwner && graph.Finalizers[0].PatchMethod == AccessTools.DeclaredMethod(typeof(LiveRigidVesselOwner), nameof(StageFinalizer)) &&
                graph.Prefixes[0].priority == Priority.Normal && graph.Prefixes[0].before.Length == 0 && graph.Prefixes[0].after.Length == 0 &&
                graph.Finalizers[0].priority == Priority.Normal && graph.Finalizers[0].before.Length == 0 && graph.Finalizers[0].after.Length == 0;
        }
        static void StageEnter(LiveRigidVesselOwner owner, int slot, out StageState state)
        {
            state = default(StageState);
            if (owner == null || !owner.controlled || !owner.entered) return;
            if (Thread.CurrentThread.ManagedThreadId != owner.thread) { owner.Report.stageFaults++; return; }
            state.owner = owner; state.slot = slot; state.start = Stopwatch.GetTimestamp();
            owner.Report.stageCosts[slot].activeCalls++;
        }
        static void StageFiPrefix(FlightIntegrator __instance, out StageState __state)
        {
            LiveRigidVesselOwner owner = active;
            StageEnter(owner != null && ReferenceEquals(__instance, owner.stageIntegrator) ? owner : null, 0, out __state);
        }
        static void StagePulsePrefix(FlightIntegrator __instance, Part __0, out StageState __state)
        {
            PulsePrefix(__0);
            LiveRigidVesselOwner owner = active;
            StageEnter(owner != null && ReferenceEquals(__instance, owner.stageIntegrator) && ReferenceEquals(__0, owner.vessel.rootPart) ? owner : null, 1, out __state);
        }
        static void StageMechPrefix(object __instance, Vessel __0, out StageState __state)
        {
            LiveRigidVesselOwner owner = active;
            StageEnter(owner != null && ReferenceEquals(__0, owner.vessel) && owner.stageStates.Contains(__instance) ? owner : null, 2, out __state);
        }
        static Exception StageFinalizer(Exception __exception, StageState __state)
        {
            LiveRigidVesselOwner owner = __state.owner;
            if (owner == null) return __exception;
            long elapsed = Stopwatch.GetTimestamp() - __state.start;
            BoundedStageCost row = owner.Report.stageCosts[__state.slot];
            if (elapsed < 0 || row.activeCalls <= 0) owner.Report.stageFaults++;
            else { row.ticks += elapsed; row.calls++; row.activeCalls--; if (__exception != null) row.originalExceptions++; }
            return __exception;
        }
        void AddHook(MethodInfo method, string callback, string finalizerName = null)
        {
            Need(method != null && method.GetMethodBody() != null &&
                (method.Module.ModuleVersionId == new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf") ||
                 controlled && method == stageMethods[2] && method.Module.ModuleVersionId == new Guid("30fbe3c4-e77e-4abc-895b-a635d5924a08")), "Frame method pin missing.");
            Patches patches = Harmony.GetPatchInfo(method);
            MethodInfo prefix = AccessTools.DeclaredMethod(typeof(LiveRigidVesselOwner), callback);
            MethodInfo finalizer = finalizerName == null ? null : AccessTools.DeclaredMethod(typeof(LiveRigidVesselOwner), finalizerName);
            bool bridgeSelected = bridge.DeclareFrameHooks(method, FrameOwner, prefix, finalizer);
            Need(bridgeSelected || patches == null || !patches.Owners.Any(), "Existing frame method owner: " + method.Name);
            frameMethods.Add(method); framePatches.Patch(method, prefix: new HarmonyMethod(prefix), finalizer: finalizer == null ? null : new HarmonyMethod(finalizer));
        }
        static void PulsePrefix(Part __0)
        {
            LiveRigidVesselOwner owner = active;
            if (owner != null && __0 == owner.vessel.rootPart)
                try { owner.QueuePulse(); } catch (Exception error) { owner.Abort(error.ToString()); }
        }
        static bool StatsPrefix(VesselPrecalculate __instance)
        {
            LiveRigidVesselOwner owner = active;
            if (owner == null || __instance != owner.vessel.precalc) return true;
            try { owner.PublishStats(); StatsComplete.SetValue(__instance, true); return false; }
            catch (Exception error) { owner.Abort(error.ToString()); return true; }
        }
        void PublishStats()
        {
            RefreshProperties(); Report.statsReads++;
            vessel.totalMass = body.mass; vessel.CoMD = body.worldCenterOfMass; vessel.CoM = vessel.CoMD;
            vessel.localCoM = vessel.transform.InverseTransformPoint(vessel.CoM);
            vessel.rb_velocityD = body.velocity; vessel.rb_velocity = body.velocity;
            vessel.velocityD = vessel.rb_velocityD + Krakensbane.GetFrameVelocity();
            Quaternion referenceInverse = Quaternion.Inverse(vessel.ReferenceTransform.rotation);
            vessel.angularVelocity = referenceInverse * body.angularVelocity; vessel.angularVelocityD = vessel.angularVelocity;
            Quaternion axes = Normalized(referenceInverse * Normalized(body.rotation) * body.inertiaTensorRotation);
            Vector3 a = axes * Vector3.right, b = axes * Vector3.up, c = axes * Vector3.forward, moments = body.inertiaTensor;
            vessel.MOI = new Vector3(moments.x*a.x*a.x+moments.y*b.x*b.x+moments.z*c.x*c.x,
                moments.x*a.y*a.y+moments.y*b.y*b.y+moments.z*c.y*c.y, moments.x*a.z*a.z+moments.y*b.z*b.z+moments.z*c.z*c.z);
            Vector3 principalOmega = Quaternion.Inverse(axes) * vessel.angularVelocity;
            vessel.angularMomentum = axes * Vector3.Scale(moments, principalOmega);
        }
        static bool FramePrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            LiveRigidVesselOwner owner = active; if (owner == null) return true;
            Part part = __instance as Part;
            if (part != null)
            {
                if (part.vessel == owner.vessel) owner.Abort("Part lifecycle transition: " + __originalMethod.Name);
                return true;
            }
            if (!ReferenceEquals(__instance, owner.vessel)) return true;
            try
            {
                string name = __originalMethod.Name;
                if (name == "SetRotation" || (name == "SetPosition" && (bool)__args[1]))
                { owner.Abort("Unsupported pristine/rotation transaction."); return true; }
                Vector3d value = (Vector3d)__args[0]; Need(Finite((Vector3)value), "Nonfinite frame transaction.");
                if (name == "SetPosition")
                {
                    Vector3 offset = (Vector3)(value - (Vector3d)owner.vessel.transform.position);
                    owner.body.position += offset;
                    foreach (Command command in owner.commands)
                    {
                        command.point = Array(Unity(Vector(command.point)) + offset);
                        command.source.position = Array(Unity(Vector(command.source.position)) + offset);
                        command.source.center = Array(Unity(Vector(command.source.center)) + offset);
                    }
                    owner.Publish(null); owner.Report.frameTranslations++; return false;
                }
                if (name == "SetWorldVelocity") owner.body.velocity = value;
                else owner.body.velocity += (Vector3)value;
                if (name == "ChangeWorldVelocity") owner.vessel.krakensbaneAcc += value * (1.0 / TimeWarp.fixedDeltaTime);
                owner.Report.frameVelocityChanges++; return false;
            }
            catch (Exception error) { owner.Abort(error.ToString()); return true; }
        }
        void Abort(string detail)
        {
            if (Report.detail == null) Report.detail = detail;
            Report.status = "invalid"; failed = true;
            Restore();
        }
        void Restore()
        {
            if (ReferenceEquals(active, this)) active = null;
            if (ordered.Count == 0) { Report.originalBodiesRestored = body == null && root == null; return; }
            var faults = new List<string>();
            if (driver != null)
            {
                try { driver.Dispose(); }
                catch (Exception error) { faults.Add("pilot-restoration:" + error.Message); }
            }
            if (motionInitialized && body != null)
            {
                try { Report.restoredAggregate = State(body, false); Report.ownerEndUt = Planetarium.GetUniversalTime(); }
                catch (Exception error) { faults.Add("aggregate-state:" + error.Message); }
            }
            var restored = new List<RestoredRow>();
            foreach (Member member in ordered)
            {
                if (member.Body == null) { faults.Add("destroyed-member:" + member.LocalId); continue; }
                Vector3 velocity = member.InitialVelocity, omega = member.InitialAngularVelocity;
                if (!motionInitialized)
                {
                    try { member.Body.transform.SetPositionAndRotation(member.InitialPosition, member.InitialRotation); member.Body.position = member.InitialPosition; member.Body.rotation = member.InitialRotation; }
                    catch (Exception error) { faults.Add("member-initial-pose:" + member.LocalId + ":" + error.Message); }
                }
                if (motionInitialized && Report.restoredAggregate != null)
                {
                    try
                    {
                        BodyState state = Report.restoredAggregate;
                        Quaternion rotation = Normalized(new Quaternion(state.rotation[0], state.rotation[1], state.rotation[2], state.rotation[3]));
                        Vector3 position = Unity(Vector(state.position)) + rotation * member.Position;
                        Quaternion orientation = Normalized(rotation * member.Rotation);
                        Vector3 center = position + orientation * member.Body.centerOfMass;
                        omega = Unity(Vector(state.angularVelocity)); velocity = Unity(Vector(state.velocity)) + Vector3.Cross(omega, center - Unity(Vector(state.center)));
                        member.Body.transform.SetPositionAndRotation(position, orientation); member.Body.position = position; member.Body.rotation = orientation;
                    }
                    catch (Exception error) { faults.Add("member-pose:" + member.LocalId + ":" + error.Message); }
                }
                try { member.Body.isKinematic = member.Kinematic; }
                catch (Exception error) { faults.Add("member-mode:" + member.LocalId + ":" + error.Message); }
                try { if (!member.Kinematic) { member.Body.velocity = velocity; member.Body.angularVelocity = omega; } }
                catch (Exception error) { faults.Add("member-motion:" + member.LocalId + ":" + error.Message); }
                try { member.Body.detectCollisions = member.DetectCollisions; }
                catch (Exception error) { faults.Add("member-collision:" + member.LocalId + ":" + error.Message); }
                try
                {
                    restored.Add(new RestoredRow { originalId = member.Body.GetInstanceID(), readbackId = member.Body.GetInstanceID(),
                        originalKinematic = member.Kinematic, readbackKinematic = member.Body.isKinematic, originalDetectCollisions = member.DetectCollisions,
                        readbackDetectCollisions = member.Body.detectCollisions, expectedVelocity = Array(velocity), readbackVelocity = Array(member.Body.velocity),
                        expectedAngularVelocity = Array(omega), readbackAngularVelocity = Array(member.Body.angularVelocity) });
                }
                catch (Exception error) { faults.Add("member-readback:" + member.LocalId + ":" + error.Message); }
            }
            Report.restoredRows = restored.ToArray();
            Report.originalBodiesRestored = faults.Count == 0 && restored.Count == ordered.Count && restored.All(row =>
                row.originalKinematic == row.readbackKinematic && row.originalDetectCollisions == row.readbackDetectCollisions &&
                (Unity(Vector(row.expectedVelocity)) - Unity(Vector(row.readbackVelocity))).magnitude <= 1e-4 &&
                (Unity(Vector(row.expectedAngularVelocity)) - Unity(Vector(row.readbackAngularVelocity))).magnitude <= 1e-4);
            try { if (body != null) { body.isKinematic = true; body.detectCollisions = false; } }
            catch (Exception error) { faults.Add("aggregate-retirement:" + error.Message); }
            finally { if (root != null) { retiredRoot = root; UnityEngine.Object.Destroy(root); } body = null; root = null; }
            entered = false;
            if (controlled)
            {
                try { ReleaseHooks(); }
                catch (Exception error) { faults.Add("early-hook-release:" + error.Message); }
            }
            if (faults.Count > 0) { Report.status = "invalid"; failed = true; Report.detail = (Report.detail ?? "") + " Restore: " + string.Join(";", faults); }
        }
        void ReleaseHooks()
        {
            if (Report.bridgeReleased) return;
            if (controlled && Report.stageCosts != null)
                foreach (BoundedStageCost row in Report.stageCosts)
                    if (row.installed && (!ExactStageGraph(row.slot) || StageHash(stageMethods[row.slot].GetMethodBody().GetILAsByteArray()) != row.ilSha256)) Report.stageFaults++;
            if (bridge.Installed) Need(bridge.ExactInstalled(), "Bridge graph changed before cleanup.");
            if (frameMethods.Count > 0) framePatches.UnpatchAll(FrameOwner);
            frameInstalled = false; bridge.Dispose(); Report.bridgeReleased = bridge.CleanupStatus == "removed";
            foreach (MethodBase method in frameMethods) if (Harmony.GetPatchInfo(method)?.Owners.Contains(FrameOwner) == true) Report.bridgeReleased = false;
            if (controlled && Report.stageCosts != null)
            {
                bool removed = true;
                foreach (BoundedStageCost row in Report.stageCosts)
                {
                    MethodInfo method = stageMethods[row.slot];
                    row.removed = Harmony.GetPatchInfo(method)?.Owners.Any() != true && StageHash(method.GetMethodBody().GetILAsByteArray()) == row.ilSha256;
                    removed &= row.removed;
                }
                Report.stageHooksRemoved = removed;
                Report.stageCleanupStatus = removed ? "removed-owned-hooks-original-graphs" : "incomplete";
                if (!removed) Report.stageFaults++;
            }
            Need(Report.bridgeReleased, "Owned bridge cleanup incomplete.");
        }
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                if (body != null || entered || !Report.originalBodiesRestored) Restore();
                if (!Report.bridgeReleased && bridge.Installed && !bridge.ExactInstalled()) { Report.status = "invalid"; Report.detail = "Bridge graph changed before cleanup."; }
            }
            catch (Exception error) { Report.status = "invalid"; Report.detail = "Restore failed: " + error; }
            try
            {
                ReleaseHooks();
            }
            catch (Exception error) { Report.status = "invalid"; Report.detail = "Hook cleanup failed: " + error; }
            FlushPendingViews();
            Report.steps = steps.ToArray(); Report.bridgeSites = bridge.SourceCallsites;
            Report.autoSyncTransformsUnchanged = !controlled || Physics.autoSyncTransforms == autoSyncTransforms;
            Report.screenshots = screenshots.ToArray();
            Report.aggregateRetired = retiredRoot == null && Report.originalBodiesRestored;
            Report.cleanupStatus = Report.originalBodiesRestored && Report.aggregateRetired && Report.bridgeReleased && !frameInstalled ? "restored-original-ownership" : "incomplete";
            bool stagesComplete = !controlled || Report.stageCosts != null && Report.stageFaults == 0 && Report.stageHooksRemoved &&
                Report.stageCosts.All(row => row.installed && row.removed && row.activeCalls == 0 && row.originalExceptions == 0);
            if (controlled) Report.stageStatus = stagesComplete ? "observed" : "invalid";
            bool treatmentComplete = controlled ? driver != null && driver.Report.passed && steps.Sum(step => (double)step.dt) >= 11.99 &&
                Report.postRestorationSimulatedSeconds >= 1.9999 && screenshots.Count == captureTimes.Length && Report.imageEncodingStatus == "completed" && Report.pendingViewCount == 0 : Report.pulseQueued;
            Report.passed = Report.status != "invalid" && stagesComplete && Report.autoSyncTransformsUnchanged && steps.Count >= 10 && Report.postRestorationNativeSteps >= 2 && Report.stockContinuationVerified && treatmentComplete && screenshotCaptured && Report.cleanupStatus == "restored-original-ownership" &&
                Report.velocityReads > 0 && Report.accelerationCalls > 0 && Report.maxProxyPositionError <= 1e-4 && Report.maxProxyRotationError <= .002;
            if (Report.status != "invalid") Report.status = Report.passed ? "passed" : "incomplete";
            File.WriteAllText(output, ReportJson.Encode(Report));
            disposed = Report.bridgeReleased && (ordered.Count == 0 || Report.originalBodiesRestored);
        }
        BodyState State(Rigidbody source, bool proxy)
        {
            return new BodyState { id = source.GetInstanceID(), mass = source.mass, position = Array(source.position), rotation = Array(source.rotation),
                center = Array(proxy ? ReadWorldCenter(source) : source.worldCenterOfMass), velocity = Array(proxy ? ReadVelocity(source) : source.velocity),
                angularVelocity = Array(proxy ? ReadAngularVelocity(source) : source.angularVelocity), inertia = Array(source.inertiaTensor), inertiaRotation = Array(source.inertiaTensorRotation) };
        }
        static Vec InertiaMultiply(BodyState state, Vec value)
        {
            Rotation q = Rotate(state.rotation) * Rotate(state.inertiaRotation);
            return q.Rotate(Vec.Product(Vector(state.inertia), q.Inverse.Rotate(value)));
        }
        static void VerifyLoopOrder(PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.FixedUpdate))
            {
                int script = -1, physics = -1;
                for (int i = 0; i < loop.subSystemList.Length; i++) { Type type = loop.subSystemList[i].type; if (type == typeof(UnityEngine.PlayerLoop.FixedUpdate.ScriptRunBehaviourFixedUpdate)) script = i; if (type == typeof(UnityEngine.PlayerLoop.FixedUpdate.PhysicsFixedUpdate)) physics = i; }
                Need(script >= 0 && physics > script, "Actual native loop ordering not admitted."); return;
            }
            if (loop.subSystemList != null) foreach (PlayerLoopSystem child in loop.subSystemList) VerifyLoopOrder(child);
        }
        static int Depth(Transform transform) { int depth = 0; while (transform.parent != null) { depth++; transform = transform.parent; } return depth; }
        void RequireThread() { Need(thread == Thread.CurrentThread.ManagedThreadId, "Rigid owner requires main thread."); }
        static bool Finite(Vector3 value) { return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z); }
        static bool Same(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }
        static bool Same(Quaternion a, Quaternion b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }
        static double RotationError(Quaternion a, Quaternion b)
        {
            double dot = (double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z+(double)a.w*b.w;
            double aa = (double)a.x*a.x+(double)a.y*a.y+(double)a.z*a.z+(double)a.w*a.w;
            double bb = (double)b.x*b.x+(double)b.y*b.y+(double)b.z*b.z+(double)b.w*b.w;
            return 2*Math.Acos(Math.Min(1,Math.Abs(dot)/Math.Sqrt(aa*bb)))*180/Math.PI;
        }
        static Vec Vector(Vector3 value) { return new Vec(value.x, value.y, value.z); }
        static Vec Subtract(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        static Vec Vector(float[] value) { return new Vec(value[0], value[1], value[2]); }
        static Rotation Rotate(Quaternion value) { return new Rotation(value.x, value.y, value.z, value.w); }
        static Quaternion Normalized(Quaternion value) { return Unity(Rotate(value)); }
        static Rotation Rotate(float[] value) { return new Rotation(value[0], value[1], value[2], value[3]); }
        static Vector3 Unity(Vec value) { return new Vector3((float)value.X, (float)value.Y, (float)value.Z); }
        static Quaternion Unity(Rotation value) { return new Quaternion((float)value.X, (float)value.Y, (float)value.Z, (float)value.W); }
        static float[] Array(Vector3 value) { return new[] { value.x, value.y, value.z }; }
        static float[] Array(Quaternion value) { return new[] { value.x, value.y, value.z, value.w }; }
        static double[] Array(Vec value) { return new[] { value.X, value.Y, value.Z }; }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    }
}
