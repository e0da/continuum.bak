using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class ScalingQualification : MonoBehaviour
    {
        static ScalingQualification instance;
        Probe probe;
        string directory, sweepState = "idle", sweepWindow;
        int sweepWindowIndex = -1;
        bool active, capturing, finished, strategySweep, quitAfterQualification;
        float eligibleSince;
        int expectedParts = -1;
        bool physicsWarp, atmosphericStress, setDragStressCandidate, warpRequested;
        bool normalAtmosphere, catchupApplied;
        double simulationSeconds, wallTimeout = 60, settleSeconds = 10;
        double? requestedCatchup;
        float originalPhysicsLimit, originalMaximumDelta, qualificationStarted;
        Guid? atmosphericVesselId;
        float warpRequestedAt;
        int stableWarpFrames;
        const float RequestedPhysicsWarp = 4;
        static readonly MethodInfo WarpModeSetter = typeof(TimeWarp).GetMethod("setMode",
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(TimeWarp.Modes) }, null);

        public void Awake() { instance = this; }

        public static bool TryStartDryBuoyancySweep(out string reason)
        {
            if (instance == null) { reason = "flight-sweep-component-unavailable"; return false; }
            return instance.StartExternalSweep(out reason);
        }

        public static string GetDryBuoyancySweepState() { return instance == null ? "unavailable" : instance.sweepState; }
        public static string GetDryBuoyancySweepDirectory() { return instance == null ? null : instance.directory; }
        public static string GetDryBuoyancySweepWindow() { return instance == null ? null : instance.sweepWindow; }
        public static int GetDryBuoyancySweepWindowIndex() { return instance == null ? -1 : instance.sweepWindowIndex; }

        bool StartExternalSweep(out string reason)
        {
            if (active || capturing || finished) { reason = "qualification-already-active"; return false; }
            Vessel current = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (current == null || current.parts == null) { reason = "active-vessel-unavailable"; return false; }
            try
            {
                PrepareDirectory(); strategySweep = true; quitAfterQualification = false;
                expectedParts = current.parts.Count; eligibleSince = 0; sweepWindow = null; sweepWindowIndex = -1;
                WriteScope(); active = true; sweepState = "waiting-for-orbit";
                reason = "accepted"; return true;
            }
            catch (Exception error) { sweepState = "error"; reason = error.GetType().Name + ": " + error.Message; return false; }
        }

        void PrepareDirectory()
        {
            directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                "scale-profile-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        void WriteScope()
        {
            File.WriteAllText(Path.Combine(directory, "scope.txt"),
                (strategySweep ? "Repeated in-process stock, full-publication batch and resident dry-domain orbital windows.\n" :
                atmosphericStress ? "Single loaded high-speed atmospheric stress window.\n" :
                "Single settled stock-vessel orbital window.\n") +
                "PlayerLoop scopes overlap and must not be summed. The active fixed parent owns strategy comparison.\n" +
                "Experimental strategies are opt-in and do not establish complete stock semantics.\n" +
                (atmosphericStress ? "Atmospheric stress admits a loaded Kerbin vessel and records topology changes instead of rejecting them; altitude is observed only at admission.\n" : "") +
                (physicsWarp ? "Requested 4x LOW-mode physics warp before capture; rate is reset before exit.\n" : ""));
        }

        public void Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "--continuum-experiment-host") >= 0) { sweepState = "idle"; return; }
            if (Array.IndexOf(arguments, "--continuum-scale-profile") < 0) return;
            if (Array.IndexOf(arguments, "--continuum-live-setdrag-provider") >= 0 &&
                Array.IndexOf(arguments, "--continuum-setdrag-quit-after-qualification") >= 0) return;
            try
            {
                PrepareDirectory(); quitAfterQualification = true;
                if (Array.IndexOf(arguments, "--continuum-playerloop") < 0)
                { Finish("missing-playerloop-flag", 2); return; }
                strategySweep = Array.IndexOf(arguments, "--continuum-dry-buoyancy-sweep") >= 0;
                physicsWarp = Array.IndexOf(arguments, "--continuum-physics-warp-pressure") >= 0;
                atmosphericStress = Array.IndexOf(arguments, "--continuum-atmospheric-stress") >= 0;
                normalAtmosphere = Array.IndexOf(arguments, "--continuum-normal-atmosphere") >= 0;
                simulationSeconds = OptionalNumber(arguments, "--continuum-flight-seconds=", 0, 0, 300);
                wallTimeout = OptionalNumber(arguments, "--continuum-flight-timeout=", 60, .1, 600);
                settleSeconds = OptionalNumber(arguments, "--continuum-flight-settle=", 10, 0, 60);
                double catchup = OptionalNumber(arguments, "--continuum-flight-catchup=", -1, .001, 1);
                if (catchup > 0) requestedCatchup = catchup;
                if (normalAtmosphere && (atmosphericStress || physicsWarp || strategySweep)) throw new ArgumentException("Normal atmosphere diagnostic excludes stress, warp and strategy sweep.");
                if (simulationSeconds > 0 && strategySweep) throw new ArgumentException("Simulation-duration capture excludes strategy sweep.");
                if (AtmosphericEntryPreparation.Requested && (!normalAtmosphere || physicsWarp || atmosphericStress || strategySweep))
                    throw new ArgumentException("Entry recipe requires normal atmosphere diagnostic.");
                qualificationStarted = Time.realtimeSinceStartup;
                AtmosphericEntryPreparation.Begin(directory);
                FlightDiagnosticControls.Apply();
                ApplyCatchup();
                setDragStressCandidate = Array.IndexOf(arguments, "--continuum-setdrag-stress-candidate") >= 0;
                if (setDragStressCandidate && !atmosphericStress)
                { Finish("setdrag-stress-requires-atmospheric-stress", 2); return; }
                if (atmosphericStress) physicsWarp = true;
                if (physicsWarp && strategySweep) { Finish("physics-warp-does-not-support-strategy-sweep", 2); return; }
                foreach (string argument in arguments) if (argument.StartsWith("--continuum-scale-parts=", StringComparison.Ordinal))
                {
                    int parsed;
                    if (!int.TryParse(argument.Substring("--continuum-scale-parts=".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out parsed) || parsed < 1) { Finish("invalid-part-count", 2); return; }
                    expectedParts = parsed;
                }
                WriteScope();
                active = true;
                sweepState = strategySweep ? "waiting-for-orbit" : "single-window";
            }
            catch (Exception error) { Fail(error); }
        }

        public void Update()
        {
            if (active && FlightAeroFieldRenderer.FailureReason != null)
            { Fail(new InvalidOperationException("Aero field runtime rejected: " + FlightAeroFieldRenderer.FailureReason)); return; }
            if (active && FlightCameraFreeze.FailureReason != null)
            {
                Fail(new InvalidOperationException("Camera freeze rejected: " + FlightCameraFreeze.FailureReason));
                return;
            }
            if (active && FlightAeroBatcher.FailureReason != null)
            {
                Fail(new InvalidOperationException("Aero batch runtime rejected: " + FlightAeroBatcher.FailureReason));
                return;
            }
            if (active && FlightPartBatcher.FailureReason != null)
            {
                Fail(new InvalidOperationException("Part batch runtime rejected: " + FlightPartBatcher.FailureReason));
                return;
            }
            if (!active || capturing) return;
            if (AtmosphericEntryPreparation.Requested && !AtmosphericEntryPreparation.Admitted)
            {
                if (!ScaleCheckpointLoadState.Ready) return;
                try
                {
                    if (!AtmosphericEntryPreparation.Tick(FlightGlobals.ready ? FlightGlobals.ActiveVessel : null, settleSeconds)) return;
                    qualificationStarted = Time.realtimeSinceStartup;
                }
                catch (Exception error) { Fail(error); return; }
            }
            if (simulationSeconds > 0 && Time.realtimeSinceStartup - qualificationStarted > wallTimeout)
            { File.WriteAllText(Path.Combine(directory,"status.txt"), "invalid\nreason=admission-wall-timeout\n"); Finish("invalid",2); return; }
            if (ScaleCheckpointLoadState.Requested && !ScaleCheckpointLoadState.Ready) return;
            Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            bool eligible = vessel != null && vessel.loaded && !vessel.packed && !FlightDriver.Pause &&
                (atmosphericStress || normalAtmosphere ? AtmosphericFlight(vessel) && (atmosphericStress || vessel.ctrlState != null && vessel.ctrlState.mainThrottle < .01) :
                vessel.situation == Vessel.Situations.ORBITING && vessel.ctrlState != null && vessel.ctrlState.mainThrottle < 0.01) &&
                (!ScaleCheckpointLoadState.Requested || vessel.id == ScaleCheckpointLoadState.VesselId) &&
                (expectedParts < 1 || vessel.parts.Count == expectedParts);
            if (!eligible) { eligibleSince = 0; stableWarpFrames = 0; return; }
            FlightDiagnosticControls.SeedCamera();
            if (physicsWarp && warpRequested)
            {
                bool entered = TimeWarp.fetch != null && TimeWarp.WarpMode == TimeWarp.Modes.LOW &&
                    TimeWarp.CurrentRateIndex == 3 && TimeWarp.CurrentRate == RequestedPhysicsWarp;
                if (!entered)
                {
                    stableWarpFrames = 0;
                    if (Time.realtimeSinceStartup - warpRequestedAt > 5) Finish("physics-warp-not-entered", 2);
                    return;
                }
                stableWarpFrames++;
                if (stableWarpFrames < 3) return;
                if (setDragStressCandidate && !AeroSetDragProviderQualification.StressReady)
                {
                    if (Time.realtimeSinceStartup - warpRequestedAt > 30) Finish("setdrag-stress-not-ready", 2);
                    return;
                }
                if (setDragStressCandidate && !AeroSetDragProviderQualification.BeginStressWindow())
                { Finish("setdrag-stress-admission-failed", 2); return; }
                StartCapture(false); return;
            }
            if (TimeWarp.CurrentRate != 1 || TimeWarp.CurrentRateIndex != 0) { eligibleSince = 0; return; }
            if (eligibleSince == 0) { eligibleSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - eligibleSince < (atmosphericStress ? .5 : settleSeconds)) return;
            if (physicsWarp)
            {
                if (atmosphericStress) atmosphericVesselId = vessel.id;
                if (!SetWarpMode(TimeWarp.Modes.LOW))
                { Finish("physics-warp-mode-rejected", 2); return; }
                TimeWarp.SetRate(3, true); warpRequested = true; warpRequestedAt = Time.realtimeSinceStartup;
                return;
            }
            StartCapture(strategySweep);
        }
        void StartCapture(bool sweep)
        {
            try
            {
                capturing = true;
                if (sweep) { sweepState = "running"; StartCoroutine(CaptureSweep()); }
                else { ApplyCatchup(); probe = NewProbe(); StartCoroutine(Capture()); }
            }
            catch (Exception error) { capturing = false; Fail(error); }
        }
        public void FixedUpdate() { Probe.ObserveFixedCallback(); }
        Probe NewProbe()
        {
            FlightDiagnosticControls.Record(directory);
            string runId = null;
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.StartsWith("--continuum-research-run=", StringComparison.Ordinal))
                    runId = argument.Substring("--continuum-research-run=".Length);
            FlightAeroBatcher.Report batch = FlightAeroBatcher.Start(FlightGlobals.ActiveVessel, directory, runId);
            if (batch != null && batch.status != "installed-experimental")
                throw new InvalidOperationException("Aero batch preparation rejected: " + batch.reason);
            FlightPartBatcher.Report parts = FlightPartBatcher.Start(FlightGlobals.ActiveVessel, directory, runId);
            if (parts != null && parts.status != "installed-experimental")
                throw new InvalidOperationException("Part batch preparation rejected: " + parts.reason);
            FlightAeroFieldRenderer.Report field = FlightAeroFieldRenderer.Start(FlightGlobals.ActiveVessel, directory, runId);
            if (field != null && field.status != "installed-experimental") throw new InvalidOperationException("Aero field preparation rejected: " + field.reason);
            FlightGpuTiming.StartOwned(gameObject, directory, runId);
            FlightCameraChain.Start(FlightGlobals.ActiveVessel, directory, runId);
            FlightWorldDraw.Start(FlightGlobals.ActiveVessel, directory, runId);
            if (FlightWorldDraw.FailureReason != null) throw new InvalidOperationException("World draw admission rejected: " + FlightWorldDraw.FailureReason);
            return new Probe(null,false,simulationSeconds,wallTimeout,simulationSeconds > 0 ? directory : null);
        }
        static double OptionalNumber(string[] arguments,string prefix,double fallback,double minimum,double maximum)
        {
            double result = fallback; bool found=false;
            foreach(string argument in arguments) if(argument.StartsWith(prefix,StringComparison.Ordinal))
            {
                if(found || !double.TryParse(argument.Substring(prefix.Length),NumberStyles.Float,CultureInfo.InvariantCulture,out result) ||
                    double.IsNaN(result) || double.IsInfinity(result) || result<minimum || result>maximum) throw new ArgumentException("Invalid or duplicate option: "+prefix);
                found=true;
            }
            return result;
        }
        void ApplyCatchup()
        {
            if(!requestedCatchup.HasValue) return;
            if(!catchupApplied) { originalPhysicsLimit=GameSettings.PHYSICS_FRAME_DT_LIMIT; originalMaximumDelta=Time.maximumDeltaTime; catchupApplied=true; }
            GameSettings.PHYSICS_FRAME_DT_LIMIT=(float)requestedCatchup.Value; Time.maximumDeltaTime=(float)requestedCatchup.Value;
        }

        IEnumerator Capture()
        {
            IEnumerator work = probe.Run(Complete);
            try
            {
                while (active)
                {
                    bool moved;
                    try { moved = work.MoveNext(); }
                    catch (Exception error) { Fail(error); yield break; }
                    if (!moved) break;
                    yield return work.Current;
                }
            }
            finally { var disposable = work as IDisposable; if (disposable != null) disposable.Dispose(); capturing = false; }
        }

        IEnumerator CaptureSweep()
        {
            string[] strategies = { DryBuoyancyRuntime.Stock, DryBuoyancyRuntime.FullPublication, DryBuoyancyRuntime.ResidentDomain,
                DryBuoyancyRuntime.Stock, DryBuoyancyRuntime.ResidentDomain, DryBuoyancyRuntime.FullPublication };
            string[] labels = { "stock-01", "full-publication-01", "resident-domain-01",
                "stock-02", "resident-domain-02", "full-publication-02" };
            bool valid = true; string reason = "verified-in-process-strategy-sweep";
            try
            {
                for (int i = 0; active && i < strategies.Length; i++)
                {
                    sweepWindowIndex = i; sweepWindow = labels[i];
                    ProbeReport captured = null;
                    probe = new Probe(strategies[i], true);
                    IEnumerator work = probe.Run(report => captured = report);
                    try
                    {
                        while (active)
                        {
                            bool moved;
                            try { moved = work.MoveNext(); }
                            catch (Exception error) { Fail(error); yield break; }
                            if (!moved) break;
                            yield return work.Current;
                        }
                    }
                    finally { var disposable = work as IDisposable; if (disposable != null) disposable.Dispose(); }
                    probe = null;
                    if (!active) yield break;
                    if (captured == null) { valid = false; reason = "missing-sweep-report"; break; }
                    try { File.WriteAllText(Path.Combine(directory, labels[i] + "-markers.json"), ReportJson.Encode(captured)); }
                    catch (Exception error) { Fail(error); yield break; }
                    string windowReason;
                    if (!Stable(captured, out windowReason)) { valid = false; reason = labels[i] + ":" + windowReason; break; }
                    yield return null;
                }
                bool sourceUnchanged = !ScaleCheckpointLoadState.Requested || ScaleCheckpointLoadState.SourceUnchanged();
                if (!sourceUnchanged) { valid = false; reason = "source-checkpoint-changed"; }
                try
                {
                    File.WriteAllText(Path.Combine(directory, "status.txt"), (valid ? "complete" : "invalid") + "\nreason=" + reason + "\n" +
                        "sequence=stock-01,full-publication-01,resident-domain-01,stock-02,resident-domain-02,full-publication-02\n" +
                        (ScaleCheckpointLoadState.Requested ? "save=" + ScaleCheckpointLoadState.Save + "\ncheckpoint=" +
                        ScaleCheckpointLoadState.Checkpoint + "\nsourceSha256=" + ScaleCheckpointLoadState.SourceSha256 +
                        "\nsourceUnchanged=" + sourceUnchanged + "\n" : ""));
                }
                catch (Exception error) { Fail(error); yield break; }
                sweepState = valid ? "complete" : "invalid";
                Finish(valid ? "complete" : "invalid", valid ? 0 : 2);
            }
            finally { capturing = false; }
        }

        void Complete(ProbeReport report)
        {
            try
            {
                FlightCameraChain.Finish("completed");
                FlightWorldDraw.EndWindow();
                FlightAeroFieldRenderer.ObservePixelsAfterWindow();
                FlightDiagnosticControls.RecordEnd(directory);
                report.requestedCatchupSeconds=requestedCatchup; report.settleSeconds=settleSeconds;
                File.WriteAllText(Path.Combine(directory, "markers.json"), ReportJson.Encode(report));
                string reason; bool valid = atmosphericStress ? AtmosphericStressComplete(report, out reason) :
                    Stable(report, physicsWarp ? RequestedPhysicsWarp : 1, out reason, normalAtmosphere);
                if (FlightCameraChain.FailureReason != null) { valid = false; reason = "camera-chain-invalid: " + FlightCameraChain.FailureReason; }
                if (FlightWorldDraw.FailureReason != null) { valid = false; reason = "world-draw-invalid: " + FlightWorldDraw.FailureReason; }
                if (setDragStressCandidate && !AeroSetDragProviderQualification.EndStressWindow())
                { valid = false; reason = "setdrag-stress-provider-invalid"; }
                bool sourceUnchanged = !ScaleCheckpointLoadState.Requested || ScaleCheckpointLoadState.SourceUnchanged();
                if (!sourceUnchanged)
                { valid = false; reason = "source-checkpoint-changed"; }
                File.WriteAllText(Path.Combine(directory, "status.txt"), (valid ? "complete" : "invalid") + "\nreason=" + reason + "\n" +
                    (ScaleCheckpointLoadState.Requested ? "save=" + ScaleCheckpointLoadState.Save + "\ncheckpoint=" +
                    ScaleCheckpointLoadState.Checkpoint + "\nsourceSha256=" + ScaleCheckpointLoadState.SourceSha256 +
                    "\nsourceUnchanged=" + sourceUnchanged + "\n" : ""));
                if (valid && Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-flight-screenshot") >= 0)
                {
                    active = false;
                    StartCoroutine(CaptureView(report.runId));
                }
                else Finish(valid ? "complete" : "invalid", valid ? 0 : 2);
            }
            catch (Exception error) { Fail(error); }
        }

        [Serializable]
        sealed class FlightViewReport
        {
            public string schema = "continuum-flight-view/v1", runId, status, reason;
            public int processId, width, height, captureFrame;
            public double universalTime;
        }

        IEnumerator CaptureView(string runId)
        {
            yield return new WaitForEndOfFrame();
            Texture2D image = null;
            var view = new FlightViewReport { runId = runId, processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                width = Screen.width, height = Screen.height, universalTime = Planetarium.GetUniversalTime(), captureFrame = Time.frameCount };
            int code = 0;
            try
            {
                image = new Texture2D(view.width, view.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, view.width, view.height), 0, 0, false);
                byte[] bytes = ImageConversion.EncodeToPNG(image);
                if (bytes == null || bytes.Length == 0) throw new InvalidOperationException("Empty flight image.");
                File.WriteAllBytes(Path.Combine(directory, "flight-view.png"), bytes);
                view.status = "captured-after-window";
            }
            catch (Exception error)
            {
                code = 2; view.status = "capture-failed"; view.reason = error.GetType().Name + ": " + error.Message;
                Debug.LogException(error);
                File.WriteAllText(Path.Combine(directory, "status.txt"), "invalid\nreason=post-window-image-failed\n");
            }
            finally
            {
                if (image != null) UnityEngine.Object.Destroy(image);
                try { File.WriteAllText(Path.Combine(directory, "flight-view.json"), JsonUtility.ToJson(view)); }
                catch (Exception error) { Debug.LogException(error); code = 2; }
                Finish(code == 0 ? "complete" : "invalid", code);
            }
        }

        static bool Stable(ProbeReport report, out string reason)
        { return Stable(report, 1, out reason); }

        static bool Stable(ProbeReport report, float expectedWarp, out string reason, bool atmosphere = false)
        {
            reason = atmosphere ? "verified-normal-atmospheric-context" : "verified-fixed-orbital-context";
            if (report == null || report.status != "complete" || !CompletedWindow(report) || report.frames == null || report.frames.Length == 0 ||
                report.requestedSimulatedSeconds > 0 && !report.topologyStable)
            { reason = "incomplete-capture"; return false; }
            ProfileFrame first = report.frames[0];
            foreach (ProfileFrame frame in report.frames)
                if (frame == null || frame.vesselId != first.vesselId || frame.parts != first.parts ||
                    frame.rigidbodies != first.rigidbodies || frame.joints != first.joints || frame.colliders != first.colliders ||
                    frame.loadedVessels != first.loadedVessels || frame.loaded != true || frame.packed != false || frame.paused != false ||
                    frame.warpRate != expectedWarp || !FinitePositive(frame.fixedDeltaSeconds) || !FinitePositive(frame.timeScale) ||
                    frame.fixedDeltaSeconds != first.fixedDeltaSeconds || frame.timeScale != first.timeScale ||
                    !frame.throttleCommand.HasValue || frame.throttleCommand.Value >= 0.01 ||
                    frame.maximumDeltaSeconds != first.maximumDeltaSeconds || frame.physicsFrameLimitSeconds != first.physicsFrameLimitSeconds ||
                    frame.warpMode != first.warpMode || frame.fixedCallbacksPerFrame < 0 ||
                    (atmosphere ? frame.body != "Kerbin" || !frame.altitudeMeters.HasValue || !frame.atmosphereDepthMeters.HasValue ||
                        frame.altitudeMeters.Value < 0 || frame.altitudeMeters.Value >= frame.atmosphereDepthMeters.Value : frame.situation != Vessel.Situations.ORBITING.ToString()))
                { reason = "context-or-topology-changed"; return false; }
            if (report.playerLoop == null || report.playerLoop.schema != "ksp-continuum-playerloop/v2" ||
                report.playerLoop.status != "observed" || report.playerLoop.integrityStatus != "verified-at-boundaries" ||
                report.playerLoop.cleanupStatus != "removed-owned-hooks")
            { reason = "playerloop-unqualified"; return false; }
            if (report.substitutionCanary != null &&
                (report.substitutionCanary.status != "observed-bounded-native-skip" ||
                 report.substitutionCanary.installationStatus != "candidate-installed" ||
                 report.substitutionCanary.restorationStatus != "native-node-restored" ||
                 report.substitutionCanary.candidateCallbacks < 1 || report.substitutionCanary.candidateCallbacks > 4))
            { reason = "physics-substitution-canary-invalid"; return false; }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-callback-attribution") >= 0 &&
                (report.callbackAttribution == null || report.callbackAttribution.status != "observed" ||
                 report.callbackAttribution.cleanupStatus != "removed-owned-patches" || report.callbackAttribution.patchedMethods < 1 ||
                 report.callbackAttribution.callbacks == null || report.callbackAttribution.callbacks.Length < 1))
            { reason = "callback-attribution-invalid"; return false; }
            if (report.dryBuoyancy != null && (report.dryBuoyancy.status != "complete" ||
                report.dryBuoyancy.cleanupStatus != "restored-owned-enables" || report.dryBuoyancy.ownedComponents < 1 ||
                report.dryBuoyancy.fixedSteps < 1 || report.dryBuoyancy.bypassed < 1 || report.dryBuoyancy.errors != 0))
            { reason = "dry-buoyancy-strategy-invalid"; return false; }
            return true;
        }
        static bool CompletedWindow(ProbeReport report)
        { return report.requestedSimulatedSeconds > 0 ? report.actualSimulatedSeconds >= report.requestedSimulatedSeconds && report.completedFrames <= report.requestedFrames : report.completedFrames == report.requestedFrames; }

        static bool AtmosphericFlight(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            return body != null && body.bodyName == "Kerbin" && body.atmosphere &&
                vessel.altitude >= 0 && vessel.altitude < body.atmosphereDepth;
        }

        bool AtmosphericStressComplete(ProbeReport report, out string reason)
        {
            reason = "verified-atmospheric-stress-capture";
            if (report == null || report.status != "complete" || !CompletedWindow(report) ||
                report.frames == null || report.frames.Length == 0)
            { reason = "incomplete-capture"; return false; }
            if (report.requestedSimulatedSeconds > 0 && !report.topologyStable)
            { reason = "atmospheric-stress-topology-changed"; return false; }
            string vesselId = atmosphericVesselId.HasValue ? atmosphericVesselId.Value.ToString("D") : null;
            foreach (ProfileFrame frame in report.frames)
                if (frame == null || frame.vesselId != vesselId || frame.body != "Kerbin" ||
                    frame.loaded != true || frame.packed != false || frame.paused != false ||
                    frame.warpRate != RequestedPhysicsWarp ||
                    !FinitePositive(frame.fixedDeltaSeconds) || !FinitePositive(frame.timeScale) ||
                    !frame.universalTime.HasValue || double.IsNaN(frame.universalTime.Value) ||
                    double.IsInfinity(frame.universalTime.Value))
                { reason = "atmospheric-stress-context-lost"; return false; }
            if (report.playerLoop == null || report.playerLoop.schema != "ksp-continuum-playerloop/v2" ||
                report.playerLoop.status != "observed" || report.playerLoop.integrityStatus != "verified-at-boundaries" ||
                report.playerLoop.cleanupStatus != "removed-owned-hooks")
            { reason = "playerloop-unqualified"; return false; }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-callback-attribution") >= 0 &&
                (report.callbackAttribution == null || report.callbackAttribution.status != "observed" ||
                 report.callbackAttribution.cleanupStatus != "removed-owned-patches" || report.callbackAttribution.patchedMethods < 1 ||
                 report.callbackAttribution.callbacks == null || report.callbackAttribution.callbacks.Length < 1))
            { reason = "callback-attribution-invalid"; return false; }
            return true;
        }

        static bool FinitePositive(double value) { return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0; }
        static bool SetWarpMode(TimeWarp.Modes mode)
        { return TimeWarp.fetch != null && WarpModeSetter != null && (bool)WarpModeSetter.Invoke(TimeWarp.fetch, new object[] { mode }); }

        void Fail(Exception error)
        {
            sweepState = "error";
            Debug.LogException(error);
            try { if (directory != null) File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString()); } catch { }
            Finish("error", 2);
        }
        void Finish(string status, int code)
        {
            try { FlightCameraChain.Stop(status); }
            catch (Exception error) { Debug.LogException(error); code = 2; }
            if (FlightGpuTiming.StopOwned(ok => Finish(status, ok ? code : 2)))
            {
                active = false;
                if (probe != null) { probe.Dispose(); probe = null; }
                return;
            }
            if (finished) return;
            if (quitAfterQualification) finished = true;
            active = false;
            try { if (probe != null) probe.Dispose(); } catch (Exception error) { Debug.LogException(error); code = 2; }
            probe = null;
            try { FlightAeroFieldRenderer.Finish(); }
            catch (Exception error) { Debug.LogException(error); code = 2; }
            try { FlightAeroBatcher.Finish(); }
            catch (Exception error) { Debug.LogException(error); code = 2; }
            try { FlightPartBatcher.Finish(); }
            catch (Exception error) { Debug.LogException(error); code = 2; }
            try { FlightWorldDraw.Stop(status); }
            catch (Exception error) { Debug.LogException(error); code = 2; }
            try { FlightDiagnosticControls.Restore(); }
            catch (Exception error) { Debug.LogException(error); code = 2; }
            try { AtmosphericEntryPreparation.ResetWarp(); }
            catch (Exception error) { Debug.LogException(error); code = 2; }
            bool resetFailed = false;
            if (physicsWarp)
                try
                {
                    if (TimeWarp.fetch != null)
                    {
                        TimeWarp.SetRate(0, true);
                        if (!SetWarpMode(TimeWarp.Modes.HIGH)) { code = 2; resetFailed = true; }
                        if (TimeWarp.CurrentRateIndex != 0 || TimeWarp.CurrentRate != 1 || TimeWarp.WarpMode != TimeWarp.Modes.HIGH)
                        { code = 2; resetFailed = true; }
                    }
                    else { code = 2; resetFailed = true; }
                }
                catch (Exception error) { Debug.LogException(error); code = 2; resetFailed = true; }
            if(catchupApplied) { GameSettings.PHYSICS_FRAME_DT_LIMIT=originalPhysicsLimit; Time.maximumDeltaTime=originalMaximumDelta; catchupApplied=false; }
            if (resetFailed && directory != null)
                try { File.WriteAllText(Path.Combine(directory, "status.txt"), "invalid\nreason=physics-warp-reset-failed\n"); }
                catch { code = 2; }
            if (directory != null && !File.Exists(Path.Combine(directory, "status.txt")))
                try { File.WriteAllText(Path.Combine(directory, "status.txt"), status + "\n"); } catch { code = 2; }
            if (quitAfterQualification) Application.Quit(code);
        }
        public void OnDestroy()
        {
            if (ReferenceEquals(instance, this)) instance = null;
            if (!finished && active) { sweepState = "interrupted"; Finish("interrupted", 2); }
        }
    }
}
