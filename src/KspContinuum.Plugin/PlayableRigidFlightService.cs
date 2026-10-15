using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class PlayableRigidFlightService : MonoBehaviour, IPlayerLoopBracketObserver
    {
        internal const string LifecycleOwner = "continuum.playable-rigid-lifecycle";
        const string HookOwner = LifecycleOwner;
        const string StockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const string StockMvid = "10657063-2fc3-43a7-84fa-d39e75e877bf";
        const int MaximumEvents = 512;
        static PlayableRigidFlightService active;
        static readonly MethodInfo Prefix = AccessTools.DeclaredMethod(typeof(PlayableRigidFlightService), "BeforeStockTransition");
        readonly Harmony harmony = new Harmony(HookOwner);
        readonly List<LifecycleSource> sources = new List<LifecycleSource>();
        readonly List<MethodBase> installed = new List<MethodBase>();
        readonly PlayableRigidLifecycle policy = new PlayableRigidLifecycle(1);
        LiveRigidVesselOwner owner, retiringOwner;
        PlayerLoopTiming loop;
        Vessel tracked;
        StreamWriter journal;
        string directory, sessionId, runId, helperSha, reason = "not-requested";
        int pid, mainThread, parts, revision, attempt, eventCount, lastRenderedFrame = -1;
        int auditSteps = 1;
        long renderFrames, physicsSteps, rateFrames, rateTicks;
        readonly FrameInterval[] frameIntervals = new FrameInterval[4096];
        int frameWrite, frameCount;
        long frameOverwrites, priorFrameTicks, priorEpoch;
        double priorFrameFixedTime;
        bool priorOwned;
        bool priorDiagnosticNormalUnpowered;
        double rateFixed, fps, simulationRate, nextStatus;
        bool requested, stopping, handlingBoundary, subscribed, readyWritten, lifecycleInstalled, callbacksRemoved;
        LoopTimingReport finalLoop;
        MacThreadWorkTiming.ReportData finalThreadWork;
        public static bool Requested { get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_PLAYABLE_RIGID")); } }

        [Serializable] public struct FrameInterval
        {
            public int frame;
            public long endedTicks, previousEndedTicks, intervalTicks, epoch, previousEpoch;
            public double fixedTime, previousFixedTime;
            public bool owned, previouslyOwned;
            public bool diagnosticNormalUnpowered, previouslyDiagnosticNormalUnpowered;
            public int width, height, antialiasing;
            public bool focused, runInBackground;
            public int captureFramerate;
            public float maximumDeltaTime, timeScale;
        }
        [Serializable] public sealed class LifecycleSource
        {
            public string declaringType, method, ilSha256;
            public int metadataToken;
            public bool installed, removed;
        }
        [Serializable] public sealed class Receipt
        {
            public string schema = "continuum-playable-rigid/v1", runId, sessionId, state, reason, vesselId;
            public string assemblySha256, assemblyMvid, helperSha256, cleanupStatus;
            public bool playerLoopRetained;
            public FrameInterval[] frameIntervals;
            public long frameIntervalOverwrites;
            public string frameIntervalScope = "Recent completed FinishFrameRendering timestamp intervals, including every intervening main-thread phase and wait. Partition only intervals whose previous/current owned flag and epoch agree. These are complete presentation cadence, not GPU execution. PlayerLoop bounded samples retain their own distinct windows.";
            public int processId, events, attempts;
            public int auditSteps;
            public long epoch, physicsSteps, renderFrames, clockFrequency = Stopwatch.Frequency;
            public bool enabled, owned, hooksRemoved, playerLoopRemoved, ownerReleased, callbacksRemoved;
            public double framesPerSecond, simulationRate;
            public string scope = "Explicit operator-enabled rigid airborne flight with original stock/MechJeb providers, stock input and cameras. Fixed topology while owned; transitions restore stock before original execution. No contact, staging/docking replacement, arbitrary-mod or performance qualification.";
            public string ratesScope = "Completed FinishFrameRendering brackets per wall second and fixedTime advance per wall second are distinct observed rates; nested PlayerLoop scopes are not additive CPU/GPU costs.";
            public LifecycleSource[] sources;
            public LoopTimingReport playerLoop;
            public MacThreadWorkTiming.ReportData mainThreadWork;
        }
        [Serializable] sealed class Ready
        {
            public string schema = "continuum-playable-rigid-ready/v1", runId, sessionId, vesselId;
            public int processId;
            public long epoch;
            public bool stockReady = true;
        }
        [Serializable] sealed class EventRow
        {
            public string schema = "continuum-playable-rigid-event/v1", runId, sessionId, state, reason, vesselId;
            public int processId, frame, attempt;
            public long epoch, ticks;
            public double universalTime;
        }
        public void Awake()
        {
            if (!Requested) return;
            requested = true; mainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                if (active != null) { requested = false; UnityEngine.Object.Destroy(this); return; }
                string root = Environment.GetEnvironmentVariable("CONTINUUM_PLAYABLE_RIGID");
                ValidateDirectory(root);
                string audit = Environment.GetEnvironmentVariable("CONTINUUM_PLAYABLE_RIGID_AUDIT_STEPS");
                if (!string.IsNullOrEmpty(audit) && (!int.TryParse(audit, out auditSteps) || (auditSteps != 1 && auditSteps != 16))) throw new ArgumentException("Rigid audit steps must be 1 or 16.");
                string[] args = Environment.GetCommandLineArgs();
                if (Array.IndexOf(args, "--continuum-scale-profile") >= 0 || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_OWNER")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_SCENARIO")))
                    throw new InvalidOperationException("Playable rigid excludes laboratory owner/capture/driver.");
                if (!File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json"))) throw new InvalidOperationException("Private instance marker required.");
                foreach (string arg in args) if (arg.StartsWith("--continuum-research-run=", StringComparison.Ordinal)) { if (runId != null) throw new ArgumentException("Duplicate run identity."); runId = arg.Substring("--continuum-research-run=".Length); }
                if (string.IsNullOrEmpty(runId) || runId.Length > 128) throw new ArgumentException("Bound research run identity required.");
                using (var process = Process.GetCurrentProcess()) pid = process.Id;
                sessionId = Guid.NewGuid().ToString("D"); directory = Path.Combine(Path.GetFullPath(root), "rigid-" + sessionId);
                if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Fresh session directory required.");
                Directory.CreateDirectory(directory);
                journal = new StreamWriter(new FileStream(Path.Combine(directory, "transitions.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                helperSha = AssemblyHash(typeof(PlayableRigidFlightService).Assembly);
                active = this; UnityEngine.Object.DontDestroyOnLoad(gameObject); InstallLifecycle(); Subscribe();
                loop = new PlayerLoopTiming(this); loop.Start();
                if (loop.Report.integrityStatus != "verified-at-boundaries") throw new InvalidOperationException("PlayerLoop observer admission failed: " + loop.Report.detail);
                rateTicks = Stopwatch.GetTimestamp(); rateFixed = Time.fixedTime; nextStatus = Time.realtimeSinceStartup + 5;
                Record("service-ready-stock-disabled");
            }
            catch (Exception error) { reason = error.GetType().Name + ": " + error.Message; UnityEngine.Debug.LogError("[ContinuumRigidPlay] " + reason); Stop("admission-failed: " + reason); }
        }
        static void ValidateDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) throw new ArgumentException("Absolute existing private receipt directory required.");
            for (var d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent)
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Receipt directory ancestry must not be symlinked.");
        }
        static string Hash(byte[] value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        static string AssemblyHash(Assembly assembly) { using (var stream = File.OpenRead(assembly.Location)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        void Admit(Type type, string name, params Type[] arguments)
        {
            MethodInfo method = type == null ? null : AccessTools.DeclaredMethod(type, name, arguments);
            if (method == null || method.Module.ModuleVersionId.ToString("D") != StockMvid || method.GetMethodBody() == null) throw new InvalidOperationException("Pinned lifecycle method missing: " + name);
            var info = Harmony.GetPatchInfo(method);
            if (info != null && info.Owners.Count != 0) throw new InvalidOperationException("Foreign lifecycle hook: " + type.FullName + "." + name);
            sources.Add(new LifecycleSource { declaringType = type.FullName, method = name, metadataToken = method.MetadataToken, ilSha256 = Hash(method.GetMethodBody().GetILAsByteArray()) });
            installed.Add(method);
        }
        void InstallLifecycle()
        {
            var stock = typeof(Vessel).Assembly;
            if (AssemblyHash(stock) != StockSha || stock.ManifestModule.ModuleVersionId.ToString("D") != StockMvid) throw new InvalidOperationException("Stock lifecycle assembly differs.");
            Admit(typeof(GamePersistence), "SaveGame", typeof(string), typeof(string), typeof(SaveMode), typeof(GameScenes));
            Admit(typeof(GamePersistence), "SaveGame", typeof(Game), typeof(string), typeof(string), typeof(SaveMode));
            Admit(typeof(QuickSaveLoad), "doSave", typeof(string), typeof(string)); Admit(typeof(QuickSaveLoad), "quickLoad", typeof(string), typeof(string));
            Type stage = stock.GetType("KSP.UI.Screens.StageManager", true);
            Admit(stage, "ActivateNextStage"); Admit(stage, "ActivateStage", typeof(int));
            Admit(typeof(Vessel), "GoOnRails"); Admit(typeof(Vessel), "GoOffRails"); Admit(typeof(Vessel), "Die");
            Admit(typeof(FlightGlobals), "SetActiveVessel", typeof(Vessel), typeof(bool)); Admit(typeof(FlightGlobals), "ForceSetActiveVessel", typeof(Vessel));
            Admit(typeof(FlightDriver), "StartAndFocusVessel", typeof(Game), typeof(int));
            Admit(typeof(Part), "Couple", typeof(Part)); Admit(typeof(Part), "decouple", typeof(float)); Admit(typeof(Part), "Undock", typeof(DockedVesselInfo)); Admit(typeof(Part), "Die");
            Admit(typeof(HighLogic), "LoadScene", typeof(GameScenes)); Admit(typeof(FlightDriver), "SetPause", typeof(bool), typeof(bool));
            lifecycleInstalled = true;
            for (int i = 0; i < installed.Count; i++) { harmony.Patch(installed[i], prefix: new HarmonyMethod(Prefix)); sources[i].installed = true; if (!HasExactLifecyclePrefix(installed[i])) throw new InvalidOperationException("Lifecycle prefix publication differs."); }
        }
        internal static bool HasExactLifecyclePrefix(MethodBase method)
        {
            var service = active; if (service == null || !service.lifecycleInstalled || !service.installed.Contains(method)) return false;
            var info = Harmony.GetPatchInfo(method); if (info == null) return false;
            int owned = 0;
            foreach (var patch in info.Prefixes) if (patch.owner == HookOwner) { if (patch.PatchMethod != Prefix || patch.priority != Priority.Normal || patch.before.Length != 0 || patch.after.Length != 0) return false; owned++; }
            foreach (var patch in info.Postfixes) if (patch.owner == HookOwner) return false;
            foreach (var patch in info.Transpilers) if (patch.owner == HookOwner) return false;
            foreach (var patch in info.Finalizers) if (patch.owner == HookOwner) return false;
            return owned == 1;
        }
        internal static bool IsLifecycleMethod(MethodBase method) { return active != null && active.lifecycleInstalled && active.installed.Contains(method); }
        internal static MethodInfo LifecyclePrefixMethod { get { return Prefix; } }
        internal static string LifecycleOwnerId { get { return HookOwner; } }
        static void BeforeStockTransition(MethodBase __originalMethod)
        {
            var service = active; if (service == null || service.stopping || service.handlingBoundary) return;
            service.MainThread(); service.handlingBoundary = true;
            try
            {
                service.Release("stock-before:" + __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name);
                service.policy.Boundary(service.reason, Time.realtimeSinceStartup);
                if (__originalMethod.Name != "SaveGame" && __originalMethod.Name != "doSave") service.revision = checked(service.revision + 1);
                service.Record(service.reason);
            }
            catch (Exception error) { service.policy.Fault("stock-boundary-handback-failed: " + error.Message); service.Record(service.policy.Reason); throw; }
            finally { service.handlingBoundary = false; }
        }
        void MainThread() { if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Playable flight requires the Unity main thread."); }
        void Subscribe()
        {
            subscribed = true; GameEvents.onVesselWasModified.Add(Modified); GameEvents.onVesselChange.Add(Changed); GameEvents.onGameSceneLoadRequested.Add(SceneChange);
            if (!HasOwnEvent(GameEvents.onVesselWasModified) || !HasOwnEvent(GameEvents.onVesselChange) || !HasOwnEvent(GameEvents.onGameSceneLoadRequested)) throw new InvalidOperationException("Lifecycle subscriber admission differs.");
        }
        bool HasOwnEvent(object source)
        {
            var field = AccessTools.Field(source.GetType(), "events"); var rows = field == null ? null : field.GetValue(source) as IList;
            if (rows == null || rows.Count > 4096) throw new InvalidOperationException("Pinned lifecycle event registry unavailable.");
            foreach (object row in rows) { var callback = AccessTools.Field(row.GetType(), "evt").GetValue(row) as Delegate; if (callback != null && ReferenceEquals(callback.Target, this)) return true; }
            return false;
        }
        void Modified(Vessel vessel) { if (!stopping && vessel == tracked) { Release("vessel-modified"); revision = checked(revision + 1); policy.Boundary("vessel-modified", Time.realtimeSinceStartup); Record("vessel-modified"); } }
        void Changed(Vessel vessel) { if (stopping) return; Release("active-vessel-event"); policy.Boundary("active-vessel-event", Time.realtimeSinceStartup); }
        void SceneChange(GameScenes scene) { if (stopping) return; Release("scene-request:" + scene); revision = checked(revision + 1); policy.Boundary(reason, Time.realtimeSinceStartup); Record(reason); }
        public void Update()
        {
            if (!requested || stopping || active != this) return;
            try
            {
                MainThread(); FinalizeRetirement();
                Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                int count = vessel == null || vessel.parts == null ? 0 : vessel.parts.Count;
                if (!ReferenceEquals(vessel, tracked) || count != parts)
                { Release("active-vessel-or-part-count-changed"); tracked = vessel; parts = count; revision = checked(revision + 1); }
                policy.ObserveTarget(vessel == null ? null : vessel.id.ToString("D"), revision, Time.realtimeSinceStartup);
                bool ready = HighLogic.LoadedSceneIsFlight && vessel != null && vessel.loaded && !vessel.packed && count > 0;
                if (ready && !readyWritten) { WriteNew("ready.json", JsonUtility.ToJson(new Ready { runId = runId, sessionId = sessionId, processId = pid, vesselId = vessel.id.ToString("D"), epoch = policy.Epoch }, true)); readyWritten = true; }
                policy.SetPaused(FlightDriver.Pause, Time.realtimeSinceStartup);
                if (owner != null && owner.Invalid) { string failure = owner.Report.detail; Release("owner-invalid"); policy.Fault(failure ?? "owner-invalid"); Record(policy.Reason); }
                if (owner != null && owner.ReadyForReacquisition) { Release(owner.HandbackReason ?? "owner-restored"); Record(reason); }
                string unsupported = ready ? LiveRigidVesselOwner.EligibilityReason(vessel) : "loaded-unpacked-active-flight-required";
                bool eligible = journal != null && eventCount < MaximumEvents && loop != null && loop.Report.integrityStatus == "verified-at-boundaries" && unsupported == null;
                if (owner == null && policy.Enabled && unsupported != null) reason = Bounded(unsupported);
                PlayableRigidAction action = policy.Evaluate(eligible, Time.realtimeSinceStartup);
                if (action == PlayableRigidAction.Handback) { Release("not-eligible-or-paused"); Record(reason); }
                if (action == PlayableRigidAction.Acquire && owner == null && retiringOwner == null)
                {
                    attempt++; Record("owner-attempt-started"); string path = Path.Combine(directory, "epoch-" + policy.Epoch + "-attempt-" + attempt + ".json");
                    try { owner = new LiveRigidVesselOwner(path, runId, () => !stopping && policy.Enabled && !policy.Paused, true, auditSteps); owner.BindPlayableIdentity(sessionId, policy.Epoch, attempt); policy.Acquired(); Record("owner-admitted-awaiting-native"); }
                    catch (Exception error) { if (owner != null) Release("acquisition-failed"); policy.Fault(error.GetType().Name + ": " + error.Message); Record("owner-constructor-or-bind-failed: " + policy.Reason); }
                }
                long now = Stopwatch.GetTimestamp(); if (now - rateTicks >= Stopwatch.Frequency) { double seconds = (now - rateTicks) / (double)Stopwatch.Frequency; fps = (renderFrames - rateFrames) / seconds; simulationRate = Math.Max(0, Time.fixedTime - rateFixed) / seconds; rateTicks = now; rateFrames = renderFrames; rateFixed = Time.fixedTime; }
                if (Time.realtimeSinceStartup >= nextStatus) { WriteStatus(); nextStatus = Time.realtimeSinceStartup + 5; }
            }
            catch (Exception error) { policy.Fault(error.GetType().Name + ": " + error.Message); try { Release("service-fault"); } catch (Exception cleanup) { UnityEngine.Debug.LogException(cleanup); } Record(policy.Reason); }
        }
        void Release(string why)
        {
            MainThread(); reason = why;
            if (owner != null)
            {
                owner.HandbackBeforeStockTransition(why);
                if (owner.IsOwned) throw new InvalidOperationException("Stock transition requires completed logical rigid handback.");
                if (retiringOwner != null) throw new InvalidOperationException("Prior owner retirement remains pending.");
                retiringOwner = owner; owner = null;
            }
            if (policy.Owned) policy.Released(why, Time.realtimeSinceStartup);
        }
        void FinalizeRetirement()
        {
            if (retiringOwner == null || !retiringOwner.ReadyForReacquisition) return;
            retiringOwner.Dispose(); retiringOwner = null; Record("owner-retirement-complete");
        }
        public static string SetEnabled(bool value)
        {
            var s = active; if (s == null || s.stopping) return "service-unavailable";
            s.MainThread(); try { if (!value) s.Release("operator-disabled"); s.policy.SetEnabled(value, Time.realtimeSinceStartup); s.Record(s.policy.Reason); return null; } catch (Exception error) { s.policy.Fault(error.Message); s.Record(s.policy.Reason); return error.Message; }
        }
        public static string Retry()
        {
            var s = active; if (s == null || s.stopping) return "service-unavailable";
            s.MainThread(); try { if (s.eventCount >= MaximumEvents || s.journal == null) return "receipt-capacity-or-integrity-requires-service-restart"; if (s.loop == null || s.loop.Report.integrityStatus != "verified-at-boundaries") return "player-loop-integrity-requires-service-restart"; if (s.owner != null || s.retiringOwner != null) return "handback-required-before-retry"; s.policy.Retry(Time.realtimeSinceStartup); s.Record(s.policy.Reason); return null; } catch (Exception error) { return error.Message; }
        }
        public static LiveRigidStatus Snapshot()
        {
            var s = active; if (s == null) return new LiveRigidStatus { state = "unavailable", reason = "service-not-running" };
            s.MainThread(); var v = s.tracked;
            var row = new LiveRigidStatus { state = s.owner != null && !s.owner.IsOwned ? "admitted-awaiting-native" : s.policy.State, reason = Bounded(s.reason), sessionId = s.sessionId, receiptDirectory = s.directory, epoch = s.policy.Epoch, enabled = s.policy.Enabled, owned = s.owner != null && s.owner.IsOwned, parts = s.parts, physicsSteps = s.physicsSteps, renderFrames = s.renderFrames, observationTicks = Stopwatch.GetTimestamp(), clockFrequency = Stopwatch.Frequency, fixedTime = Time.fixedTime, framesPerSecond = s.fps, simulationRate = s.simulationRate, warpRate = TimeWarp.CurrentRate, warpMode = TimeWarp.WarpMode.ToString(), fixedDeltaTime = Time.fixedDeltaTime, vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate, width = Screen.width, height = Screen.height, antialiasing = QualitySettings.antiAliasing, focused = Application.isFocused, runInBackground = Application.runInBackground, captureFramerate = Time.captureFramerate, maximumDeltaTime = Time.maximumDeltaTime, timeScale = Time.timeScale };
            if (v != null)
            {
                row.vesselId = v.id.ToString("D"); row.universalTime = Planetarium.GetUniversalTime(); row.altitude = v.altitude; row.mass = v.GetTotalMass(); row.atmosphericDensity = v.atmDensity; row.dynamicPressureKPa = v.dynamicPressurekPa; row.surfaceSpeed = v.srfSpeed;
                if (v.ctrlState != null) { row.pitch = v.ctrlState.pitch; row.yaw = v.ctrlState.yaw; row.roll = v.ctrlState.roll; row.x = v.ctrlState.X; row.y = v.ctrlState.Y; row.z = v.ctrlState.Z; row.throttle = v.ctrlState.mainThrottle; }
                if (v.parts != null) foreach (Part part in v.parts) if (part != null) foreach (PartResource resource in part.Resources) { if (resource.resourceName == "ElectricCharge") row.electricCharge += resource.amount; if (resource.resourceName == "MonoPropellant") row.monoPropellant += resource.amount; }
            }
            if (s.owner != null) { row.bodies = s.owner.Report.sourceBodies; row.maximumProxyPositionError = s.owner.Report.maxProxyPositionError; row.maximumProxyRotationError = s.owner.Report.maxProxyRotationError; }
            return row;
        }
        public static string SetPhysicsWarp(bool four)
        {
            var service = active; if (service == null || service.stopping) return "service-unavailable";
            service.MainThread();
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null || FlightDriver.Pause) return "active-unpaused-flight-required";
            try
            {
                TimeWarp.SetRate(0, true, false);
                if (four)
                {
                    var method = AccessTools.DeclaredMethod(typeof(TimeWarp), "setMode", new[] { typeof(TimeWarp.Modes) });
                    if (method == null || method.Module.ModuleVersionId.ToString("D") != StockMvid || method.ReturnType != typeof(bool) || Harmony.GetPatchInfo(method)?.Owners.Count > 0)
                        return "original-physics-warp-mode-unavailable";
                    if (!(bool)method.Invoke(TimeWarp.fetch, new object[] { TimeWarp.Modes.LOW })) return "original-physics-warp-mode-rejected";
                    TimeWarp.SetRate(3, true, false);
                }
                float expected = four ? 4 : 1;
                if (TimeWarp.CurrentRate != expected || four && TimeWarp.WarpMode != TimeWarp.Modes.LOW) return "original-warp-readback-differs";
                service.Record(four ? "operator-original-low4" : "operator-original-normal"); return null;
            }
            catch (Exception error) { return Bounded(error.GetType().Name + ": " + error.Message); }
        }
        public static string Quicksave(bool load)
        {
            var service = active; if (service == null || service.stopping) return "service-unavailable";
            service.MainThread();
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null) return "active-flight-required";
            try
            {
                if (load)
                {
                    string path = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "quicksave.sfs");
                    if (!File.Exists(path)) return "original-quicksave-unavailable";
                }
                MethodInfo method = AccessTools.DeclaredMethod(typeof(QuickSaveLoad), load ? "quickLoad" : "quickSave", load ? new[] { typeof(string), typeof(string) } : new[] { typeof(bool) });
                if (method == null || method.Module.ModuleVersionId.ToString("D") != StockMvid || QuickSaveLoad.fetch == null) return "original-save-controller-unavailable";
                method.Invoke(QuickSaveLoad.fetch, load ? new object[] { "quicksave", HighLogic.SaveFolder } : new object[] { false });
                service.Record(load ? "operator-original-quickload-requested" : "operator-original-quicksave-requested"); return null;
            }
            catch (Exception error) { return Bounded(error.GetType().Name + ": " + error.Message); }
        }
        public static string CaptureFrames(out string path)
        {
            path = null;
            var service = active;
            if (service == null || service.stopping) return "service-unavailable";
            try
            {
                service.MainThread(); ValidateDirectory(service.directory);
                string name = "frames-" + Guid.NewGuid().ToString("N") + ".json";
                string payload = ReportJson.Encode(service.CaptureReceipt("captured-operator-window"));
                service.WriteNew(name, payload);
                path = Path.Combine(service.directory, name);
                return null;
            }
            catch (Exception error) { return Bounded(error.GetType().Name + ": " + error.Message); }
        }
        static string Bounded(string text) { return text == null ? null : text.Length <= 1024 ? text : text.Substring(0, 1024); }
        public void Before(string scope, int frame, double time) { if (!stopping && owner != null) owner.Before(scope, frame, time); }
        public void After(string scope, int frame, double time)
        {
            if (!stopping && owner != null) owner.After(scope, frame, time);
            if (scope == "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate") physicsSteps++;
            if (scope == "UnityEngine.PlayerLoop.PostLateUpdate+FinishFrameRendering" && frame != lastRenderedFrame)
            {
                renderFrames++; lastRenderedFrame = frame; long now = Stopwatch.GetTimestamp(); bool owned = owner != null && owner.IsOwned;
                Vessel observed = FlightGlobals.ActiveVessel;
                bool diagnostic = HighLogic.LoadedSceneIsFlight && observed != null && ReferenceEquals(observed, tracked) && observed.loaded && !observed.packed && !observed.HoldPhysics && !FlightDriver.Pause && observed.ctrlState != null && observed.ctrlState.mainThrottle == 0 && TimeWarp.CurrentRate == 1 && TimeWarp.CurrentRateIndex == 0 && Time.timeScale == 1 && Time.fixedDeltaTime == .02f && QualitySettings.vSyncCount == 0 && Application.targetFrameRate == -1;
                if (priorFrameTicks != 0)
                {
                    frameIntervals[frameWrite] = new FrameInterval { frame = frame, endedTicks = now, previousEndedTicks = priorFrameTicks, intervalTicks = now - priorFrameTicks, epoch = policy.Epoch, previousEpoch = priorEpoch, fixedTime = Time.fixedTime, previousFixedTime = priorFrameFixedTime, owned = owned, previouslyOwned = priorOwned, diagnosticNormalUnpowered = diagnostic, previouslyDiagnosticNormalUnpowered = priorDiagnosticNormalUnpowered, width = Screen.width, height = Screen.height, antialiasing = QualitySettings.antiAliasing, focused = Application.isFocused, runInBackground = Application.runInBackground, captureFramerate = Time.captureFramerate, maximumDeltaTime = Time.maximumDeltaTime, timeScale = Time.timeScale };
                    frameWrite = (frameWrite + 1) % frameIntervals.Length; if (frameCount < frameIntervals.Length) frameCount++; else frameOverwrites++;
                }
                priorFrameTicks = now; priorEpoch = policy.Epoch; priorOwned = owned; priorFrameFixedTime = Time.fixedTime; priorDiagnosticNormalUnpowered = diagnostic;
            }
        }
        public void Fault(string scope, Exception error) { if (owner != null) owner.Fault(scope, error); policy.Fault("player-loop:" + scope + ": " + error.Message); }
        public void OnGUI()
        {
            if (!requested || stopping || active != this || !HighLogic.LoadedSceneIsFlight) return;
            GUI.Box(new Rect(10, 190, 385, 190), "Continuum rigid flight — experimental");
            GUI.Label(new Rect(22, 215, 365, 23), (owner != null && !owner.IsOwned ? "admitted-awaiting-native" : policy.State) + " / " + (owner != null && owner.IsOwned ? "rigid" : "stock"));
            GUI.Label(new Rect(22, 239, 365, 23), fps.ToString("F1") + " render frames/s; " + simulationRate.ToString("F2") + " simulated s/wall s");
            GUI.Label(new Rect(22, 263, 365, 23), Bounded(reason));
            if (GUI.Button(new Rect(22, 296, 135, 30), policy.Enabled ? "Disable" : "Enable")) SetEnabled(!policy.Enabled);
            if (GUI.Button(new Rect(166, 296, 100, 30), "Retry")) Retry();
            if (GUI.Button(new Rect(22, 334, 75, 30), "Normal")) SetPhysicsWarp(false);
            if (GUI.Button(new Rect(103, 334, 75, 30), "Physics 4×")) SetPhysicsWarp(true);
            if (GUI.Button(new Rect(184, 334, 85, 30), "Quicksave")) Quicksave(false);
            if (GUI.Button(new Rect(275, 334, 85, 30), "Quickload")) Quicksave(true);
        }
        FrameInterval[] CaptureIntervals()
        {
            var result = new FrameInterval[frameCount]; int first = (frameWrite - frameCount + frameIntervals.Length) % frameIntervals.Length;
            for (int i = 0; i < result.Length; i++) result[i] = frameIntervals[(first + i) % frameIntervals.Length]; return result;
        }
        Receipt CaptureReceipt(string cleanup = "running")
        {
            return new Receipt { runId = runId, sessionId = sessionId, processId = pid, auditSteps = auditSteps, state = owner != null && !owner.IsOwned ? "admitted-awaiting-native" : policy.State, reason = Bounded(reason), vesselId = tracked == null ? null : tracked.id.ToString("D"), assemblySha256 = StockSha, assemblyMvid = StockMvid, helperSha256 = helperSha, events = eventCount, attempts = attempt, epoch = policy.Epoch, enabled = policy.Enabled, owned = owner != null && owner.IsOwned, physicsSteps = physicsSteps, renderFrames = renderFrames, framesPerSecond = fps, simulationRate = simulationRate, cleanupStatus = cleanup, ownerReleased = owner == null && retiringOwner == null, playerLoopRemoved = loop == null, hooksRemoved = !lifecycleInstalled, callbacksRemoved = callbacksRemoved, sources = sources.ToArray(), frameIntervalOverwrites = frameOverwrites, frameIntervals = cleanup == "running" ? null : CaptureIntervals(), playerLoopRetained = cleanup != "running" && finalLoop != null, mainThreadWork = cleanup == "running" ? null : (loop == null ? finalThreadWork : loop.MainThreadWork), playerLoop = cleanup == "running" ? null : finalLoop };
        }
        void Record(string why)
        {
            reason = Bounded(why); if (journal == null) return;
            if (eventCount >= MaximumEvents) { policy.Fault("event-journal-capacity; explicit service restart required"); return; }
            try
            {
                eventCount++; journal.WriteLine(JsonUtility.ToJson(new EventRow { runId = runId, sessionId = sessionId, processId = pid, state = policy.State, reason = reason, vesselId = tracked == null ? null : tracked.id.ToString("D"), frame = Time.frameCount, epoch = policy.Epoch, attempt = attempt, ticks = Stopwatch.GetTimestamp(), universalTime = Planetarium.GetUniversalTime() })); journal.Flush(); WriteStatus();
            }
            catch (Exception error) { policy.Fault("receipt-write-failed: " + error.Message); reason = Bounded(policy.Reason); UnityEngine.Debug.LogException(error); if (journal != null) { try { journal.Dispose(); } catch { } journal = null; } }

        }
        void WriteNew(string name, string value) { using (var writer = new StreamWriter(new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read))) writer.Write(value); }
        void WriteStatus()
        {
            if (directory == null) return;
            string temp = Path.Combine(directory, "status-" + Guid.NewGuid().ToString("N") + ".tmp"); File.WriteAllText(temp, ReportJson.Encode(CaptureReceipt()));
            string target = Path.Combine(directory, "status.json"); if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
        }
        void Stop(string why)
        {
            if (stopping) return; stopping = true; reason = Bounded(why); var failures = new List<string>();
            try { Release(why); FinalizeRetirement(); if (retiringOwner != null) { retiringOwner.FinishInterruptedRetirement(); failures.Add("owner-retirement-requested-not-observed-before-shutdown"); } } catch (Exception error) { failures.Add("owner:" + error.Message); }
            if (loop != null) { try { loop.Dispose(); finalLoop = loop.Report; finalThreadWork = loop.MainThreadWork; if (loop.Report.cleanupStatus != "removed-owned-hooks") failures.Add("loop:" + loop.Report.cleanupStatus); else loop = null; } catch (Exception error) { failures.Add("loop:" + error.Message); } }
            if (subscribed)
            {
                try { GameEvents.onVesselWasModified.Remove(Modified); } catch (Exception error) { failures.Add("modified-callback:" + error.Message); }
                try { GameEvents.onVesselChange.Remove(Changed); } catch (Exception error) { failures.Add("change-callback:" + error.Message); }
                try { GameEvents.onGameSceneLoadRequested.Remove(SceneChange); } catch (Exception error) { failures.Add("scene-callback:" + error.Message); }
                try { callbacksRemoved = !HasOwnEvent(GameEvents.onVesselWasModified) && !HasOwnEvent(GameEvents.onVesselChange) && !HasOwnEvent(GameEvents.onGameSceneLoadRequested); if (!callbacksRemoved) failures.Add("owned-callback-removal-incomplete"); } catch (Exception error) { failures.Add("callback-readback:" + error.Message); }
                subscribed = !callbacksRemoved;
            }
            else callbacksRemoved = true;
            bool removed = true;
            for (int i = 0; i < installed.Count; i++)
            {
                try { harmony.Unpatch(installed[i], HarmonyPatchType.All, HookOwner); var info = Harmony.GetPatchInfo(installed[i]); sources[i].removed = info == null || !info.Owners.Contains(HookOwner); if (!sources[i].removed) removed = false; if (Hash(installed[i].GetMethodBody().GetILAsByteArray()) != sources[i].ilSha256) failures.Add("stock-source-changed"); }
                catch (Exception error) { removed = false; failures.Add("hook:" + error.Message); }
            }
            lifecycleInstalled = !removed; policy.Stop(Time.realtimeSinceStartup);
            if (directory != null) try { WriteNew("final.json", ReportJson.Encode(CaptureReceipt(failures.Count == 0 ? "released-owned-service-hooks-loop-and-motion" : "failed:" + string.Join(";", failures.ToArray())))); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
            if (journal != null) { journal.Dispose(); journal = null; }
            if (ReferenceEquals(active, this)) active = null;
        }
        public void OnDestroy() { if (requested) Stop("flight-addon-destroyed"); }
        public void OnApplicationQuit() { if (requested) Stop("application-quit"); }
    }
}
