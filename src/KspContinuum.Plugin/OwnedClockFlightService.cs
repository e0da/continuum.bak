using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    public interface IOwnedClockFlightFactory
    {
        OwnedClockFlightBinding Acquire(Vessel vessel, string freshDirectory, string runId);
    }

    public sealed class OwnedClockFlightBinding
    {
        public NativeAssemblySceneManifest Native;
        public OwnedRuntimeCapturedParts Parts;
        public string NativeSha256, PartsSha256, ContextSha256, ModelScope;
        public OwnedClockProviderBinding[] Providers;
        public Func<OwnedFlightWorkerContext> InitializeWorker;
        public Func<OwnedFlightWorkerContext, OwnedFlightCheckpointData> CaptureCheckpoint;
        public Func<OwnedFlightWorkerContext, OwnedFlightCheckpointData, OwnedFlightRestoredState> RestoreCheckpoint;
        public Action<OwnedFlightPresentationLease> PublishProviders;
        public double MinimumAltitudeMeters;
    }

    [Serializable] public sealed class OwnedClockProviderBinding
    {
        public uint partId;
        public int moduleIndex;
        public string sourceType, kind, modelRecipe;
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class OwnedClockFlightService : MonoBehaviour
    {
        const int MaximumEvents = 256;
        static OwnedClockFlightService active;
        static IOwnedClockFlightFactory factory;
        readonly EventRow[] events = new EventRow[MaximumEvents];
        OwnedClockKspBridge bridge;
        OwnedFlightWorker worker;
        OwnedClockFlightBinding binding;
        Vessel vessel;
        string directory, runId, sessionId;
        int threadId, eventWrite, eventCount, epoch;
        long eventOverwrites;
        bool requested, authorityEnabled, stopping, handingBack, failed, establishedOwner;
        OwnedRuntimeControls controls;
        OwnedFlightWorkerAcknowledgement<OwnedFlightTimelineProgress> inputAck;
        int checkpointSequence;
        CheckpointReceipt latestCheckpoint;

        [Serializable] public struct EventRow
        { public long ticks, ownershipEpoch, numericalEpoch; public int frame; public double universalTime; public string state, reason; }
        [Serializable] public sealed class ReportData
        {
            public int operatorMouseDowns, operatorMouseUps, operatorKeyDowns, operatorInputFrame;
            public string operatorInputKind, operatorInputKey, operatorInputModifiers;
            public float operatorMouseX, operatorMouseY;
            public bool operatorGuiEnabled, operatorAvailable;
            public string schema = "continuum-owned-clock-flight/v1", runId, sessionId, vesselId, status = "off", reason;
            public string helperSha256, sourceSha256, sourceMvid, modelScope, cleanupStatus = "not-installed", checkpointPath, checkpointSha256;
            public string interruptedCheckpointPath, interruptedCheckpointSha256, restoreStatus, restoreFailurePath, restoreFailureSha256, restoreRecoveryError;
            public string acquisitionStage, acquisitionError, acquisitionCleanupError, acquisitionFailurePath, acquisitionFailureSha256;
            public string shellRoundtripStatus, shellRoundtripError;
            public string handbackCheckpointStatus, handbackCheckpointError, handbackCheckpointAttemptPrefix, handbackReceiptError;
            public int processId, ownershipEpoch, acquisitionEpoch, eventCount;
            public long clockFrequency = Stopwatch.Frequency, publications, inputUpdates, eventOverwrites;
            public long shellRoundtripChecks, shellRoundtripRows;
            public bool requested, enabled, owned, workerJoined, fixedRestored, nativeRestored, partialPublication, acquisitionRetryable;
            public double publishedUniversalTime, outstandingDemandSeconds;
            public EventRow[] events;
            public OwnedClockKspBridge.ReportData bridge;
            public OwnedFlightWorkerStatus worker;
            public string scope = "One loaded active fixed-topology airborne craft; acquired-model motion, resources and thermal state on an independent worker. Entire stock fixed subtree suspended. No contact, arbitrary mod/provider, stock response, or background-vessel module equivalence.";
        }
        public readonly ReportData Report = new ReportData();
        [Serializable] public sealed class ShellRow { public uint partId; public int moduleIndex; public string sourceType, node; }
        [Serializable] public sealed class ShellData { public string schema = "continuum-owned-clock-shell/v1", sessionId, vesselId; public double universalTime; public ShellRow[] modules; }
        [Serializable] public sealed class CheckpointReceipt
        {
            public string schema = "continuum-owned-clock-checkpoint/v1", sessionId, vesselId, runId, helperSha256, modelScope;
            public string binaryPath, binarySha256, shellPath, shellSha256, nativeSha256, partsSha256, contextSha256;
            public string shellEncoding = "xml-serializer/v1";
            public string receiptPath, operation;
            public int processId, ownershipEpoch;
            public double universalTime, debtSeconds;
            public long epoch, stateRevision, topologyRevision, definitionRevision, frameRevision;
        }
        [Serializable] sealed class RestoreFailureReceipt
        {
            public string schema = "continuum-owned-clock-restore-failure/v1", sessionId, runId, error;
            public string targetCheckpointPath, interruptedCheckpointPath, candidateBinaryPath, candidateBinarySha256, candidateShellPath, candidateShellSha256, candidateCheckpointError;
            public int processId, ownershipEpoch;
            public OwnedFlightWorkerStatus worker;
            public OwnedClockKspBridge.ReportData bridge;
        }
        [Serializable] sealed class AcquisitionFailureReceipt
        {
            public string schema = "continuum-owned-clock-acquisition-failure/v1", sessionId, runId, helperSha256, stage, error, cleanupError;
            public int processId, ownershipEpoch;
            public bool installationAttempted, retryable, workerJoined, nativeRestored, fixedRestored, hooksRemoved;
            public OwnedFlightWorkerStatus worker;
            public OwnedClockKspBridge.ReportData bridge;
        }
        public static bool Requested { get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_OWNED_CLOCK_FLIGHT")); } }
        public static void RegisterFactory(IOwnedClockFlightFactory value)
        { if (value == null) throw new ArgumentNullException("value"); if (active != null && active.IsOwned) throw new InvalidOperationException("Cannot replace an active clock factory."); factory = value; }
        public bool IsOwned { get { return bridge != null && bridge.OwnsFixedLoop && worker != null; } }

        public void Awake()
        {
            if (!Requested) return;
            if (active != null) { UnityEngine.Object.Destroy(this); return; }
            requested = true; threadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                Need(!PlayableRigidFlightService.Requested && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_OWNER")), "Native rigid authority and owned clock authority are exclusive.");
                Need(File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json")), "Private instance marker required.");
                foreach (string argument in Environment.GetCommandLineArgs()) if (argument.StartsWith("--continuum-research-run=", StringComparison.Ordinal))
                { Need(runId == null, "Duplicate run identity."); runId = argument.Substring("--continuum-research-run=".Length); }
                Need(!string.IsNullOrEmpty(runId) && runId.Length <= 128, "Bound research run required.");
                string home = Environment.GetEnvironmentVariable("CONTINUUM_OWNED_CLOCK_FLIGHT"); ValidateDirectory(home);
                sessionId = Guid.NewGuid().ToString("D"); directory = Path.Combine(Path.GetFullPath(home), "owned-clock-" + sessionId);
                Need(!Directory.Exists(directory) && !File.Exists(directory), "Fresh session output required."); Directory.CreateDirectory(directory);
                Report.requested = true; Report.runId = runId; Report.sessionId = sessionId; Report.helperSha256 = HashFile(typeof(OwnedClockFlightService).Assembly.Location);
                using (var process = Process.GetCurrentProcess()) Report.processId = process.Id;
                active = this; UnityEngine.Object.DontDestroyOnLoad(gameObject); Report.status = "stock-disabled"; Record("stock-disabled", "Explicit enable required.");
            }
            catch (Exception error) { failed = true; Report.status = "invalid"; Report.reason = error.ToString(); UnityEngine.Debug.LogError("[ContinuumOwnedClock] " + Report.reason); }
        }

        public static string SetEnabled(bool value)
        {
            if (active == null || !active.requested) return "owned-clock-not-requested";
            try { active.MainThread(); if (value) active.Acquire(); else active.HandbackBeforeStockTransition("operator-disable"); return null; }
            catch (Exception error)
            {
                if (!value || active.failed || !active.Report.acquisitionRetryable || active.bridge != null || active.worker != null) active.Fault(error);
                return error.Message;
            }
        }
        public static string CaptureLightingFacts()
        {
            Need(active!=null&&active.requested&&!active.stopping,"Owned clock diagnostic session unavailable.");active.MainThread();
            Need(!string.IsNullOrEmpty(active.directory)&&Directory.Exists(active.directory),"Bound private diagnostic directory unavailable.");
            var facts=OwnedClockLightingFacts.Capture(FlightGlobals.ActiveVessel,active.runId,active.sessionId,active.epoch,active.IsOwned);
            string path=Path.Combine(active.directory,"lighting-facts-"+Guid.NewGuid().ToString("N")+".json");WriteNew(path,ReportJson.Encode(facts));return path;
        }
        public static string ReframeSunward()
        {
            Need(active != null && active.requested && !active.stopping, "Owned clock camera session unavailable."); active.MainThread();
            Need(!string.IsNullOrEmpty(active.directory) && Directory.Exists(active.directory), "Bound private camera directory unavailable.");
            var configuration = OwnedClockProbeStage.ReframeSunward(FlightGlobals.ActiveVessel);
            string path = Path.Combine(active.directory, "camera-reframe-" + Guid.NewGuid().ToString("N") + ".json");
            WriteNew(path, ReportJson.Encode(configuration)); return path;
        }
        public static ReportData Snapshot()
        { if (active == null) return new ReportData(); active.RefreshReport(); active.CopyEvents(); return active.Report; }
        public struct OperatorState
        {
            public bool Available, Owned, Paused, CanEnable, HasCheckpoint;
            public string Status, Error;
            public double UniversalTime, Warp, DebtSeconds;
        }
        public static OperatorState GetOperatorState()
        {
            var service = active;
            if (service == null) return new OperatorState { Status = "initializing", Warp = 1 };
            service.MainThread();
            var state = service.worker == null ? service.Report.worker : service.worker.ReadStatus();
            bool owned = service.IsOwned;
            return new OperatorState
            {
                Available = service.requested && !service.stopping,
                Owned = owned,
                CanEnable = !owned && !service.failed && !service.handingBack && !service.stopping && service.bridge == null && service.worker == null,
                HasCheckpoint = owned && service.latestCheckpoint != null && service.latestCheckpoint.ownershipEpoch == service.epoch,
                Paused = owned && state.Progress.Paused,
                Status = owned ? (state.Progress.Paused ? "paused" : state.Status) : service.Report.status,
                Error = state.Fault ?? (service.failed || service.Report.status == "stock-acquisition-refused" ? service.Report.reason : null),
                UniversalTime = owned ? state.UniversalTime : Planetarium.GetUniversalTime(),
                Warp = owned ? state.Progress.Warp : 1,
                DebtSeconds = owned ? state.Progress.DebtSeconds : 0
            };
        }
        public static void ObserveOperatorEvent(Event input, bool available)
        {
            if (active == null || input == null || (input.type != EventType.MouseDown && input.type != EventType.MouseUp && input.type != EventType.KeyDown)) return;
            active.MainThread(); var report = active.Report;
            if (input.type == EventType.MouseDown) report.operatorMouseDowns++;
            if (input.type == EventType.MouseUp) report.operatorMouseUps++;
            if (input.type == EventType.KeyDown) report.operatorKeyDowns++;
            report.operatorInputFrame = Time.frameCount; report.operatorInputKind = input.type.ToString();
            report.operatorInputKey = input.keyCode.ToString(); report.operatorInputModifiers = input.modifiers.ToString();
            report.operatorMouseX = input.mousePosition.x; report.operatorMouseY = input.mousePosition.y;
            report.operatorGuiEnabled = GUI.enabled; report.operatorAvailable = available;
        }
        public static string SetWarp(double rate)
        {
            if (active == null || !active.IsOwned) return "owned-clock-not-owned";
            try { active.MainThread(); Need(Finite(rate) && rate > 0 && rate <= 1000000, "Owned warp outside bounds."); Wait(active.worker.SetWarp(rate));active.bridge.SetDisplayWarp(rate); active.Record("owned", "warp:" + rate.ToString("R", CultureInfo.InvariantCulture)); return null; }
            catch (Exception error) { active.Fault(error); return error.Message; }
        }
        public static string SetPaused(bool value)
        {
            if (active == null || !active.IsOwned) return "owned-clock-not-owned";
            try { active.MainThread(); Wait(active.worker.SetPause(value)); active.Record(value ? "paused" : "owned", "operator-pause"); return null; }
            catch (Exception error) { active.Fault(error); return error.Message; }
        }
        public static string SaveCheckpoint()
        {
            if (active == null || !active.IsOwned) return "owned-clock-not-owned";
            try
            {
                active.MainThread(); Wait(active.worker.SetPause(true));
                var numeric = Wait(active.worker.Invoke(active.binding.CaptureCheckpoint));
                using (var view = active.worker.AcquirePresentation())
                { Need(view != null, "Paused checkpoint publication unavailable."); active.bridge.Publish(view); active.SavePacket(numeric, active.bridge.CaptureShell(active.sessionId), view); }
                return null;
            }
            catch (Exception error) { active.Report.reason = error.ToString(); return error.Message; }
        }
        public static string RestoreCheckpoint()
        {
            if (active == null || !active.IsOwned) return "owned-clock-not-owned";
            return active.RestoreSavedCheckpoint();
        }
        string RestoreSavedCheckpoint()
        {
            OwnedFlightCheckpointData interruptedNumeric = null; ShellData interruptedShell = null;
            CheckpointReceipt target = latestCheckpoint, interrupted = null; bool replacementStarted = false;
            long initialPartialPublications = bridge == null ? 0 : bridge.Report.partialPublications;
            try
            {
                MainThread(); Need(!failed && !handingBack && !stopping, "Service cannot restore during failed cleanup.");
                Need(target != null && target.sessionId == sessionId && target.ownershipEpoch == epoch && target.vesselId == vessel.id.ToString("D") && target.helperSha256 == Report.helperSha256 && target.nativeSha256 == binding.NativeSha256 && target.partsSha256 == binding.PartsSha256 && target.contextSha256 == binding.ContextSha256, "Checkpoint belongs to a different physical/code/source lease.");
                Need(target.shellEncoding == "xml-serializer/v1", "Saved shell codec is not the qualified XML format.");
                Need(HashFile(target.binaryPath) == target.binarySha256 && HashFile(target.shellPath) == target.shellSha256, "Saved owned checkpoint bytes changed.");
                var info = new FileInfo(target.binaryPath); Need(info.Length > 0 && info.Length <= 64 * 1024 * 1024, "Checkpoint size outside bound.");
                var shellInfo = new FileInfo(target.shellPath); Need(shellInfo.Length > 0 && shellInfo.Length <= 64 * 1024 * 1024, "Shell checkpoint size outside bound.");
                var numeric = OwnedRuntimeCheckpointBinary.DecodeFlight(File.ReadAllBytes(target.binaryPath));
                var shell = OwnedClockShellCodec.Read<ShellData>(target.shellPath); bridge.ValidateShell(shell, sessionId);
                Need(numeric.timeline != null && numeric.timeline.paused && numeric.timeline.fault == null && shell.universalTime == target.universalTime && numeric.timeline.epoch == target.epoch && numeric.timeline.stateRevision == target.stateRevision && numeric.timeline.requestedRuntimeTime - numeric.timeline.time == target.debtSeconds, "Saved checkpoint frontier/demand differs from its receipt.");

                Wait(worker.SetPause(true)); inputAck = null;
                interruptedNumeric = Wait(worker.Invoke(binding.CaptureCheckpoint));
                using (var view = worker.AcquirePresentation())
                {
                    Need(view != null, "Interrupted coherent publication unavailable."); bridge.Publish(view);
                    interruptedShell = bridge.CaptureShell(sessionId);
                    interrupted = SavePacket(interruptedNumeric, interruptedShell, view, false, "interrupted-rollback");
                }
                Report.interruptedCheckpointPath = interrupted.receiptPath; Report.interruptedCheckpointSha256 = HashFile(interrupted.receiptPath);
                Report.restoreStatus = "interrupted-state-retained"; Record("paused", "restore-interrupted-state-retained");
                replacementStarted = true;
                JoinCurrentWorker();
                StartRestoredWorker(numeric);
                PublishRestoredShell(shell);
                Report.restoreStatus = "restored-saved-state"; Report.status = "paused";
                Record("paused", "owned-checkpoint-restored-with-retained-demand"); return null;
            }
            catch (Exception error)
            {
                Report.reason = error.ToString();
                if (!replacementStarted)
                {
                    Report.restoreStatus = "refused-current-owner-retained";
                    if (bridge != null && bridge.Report.partialPublications != initialPartialPublications) Fault(error);
                    return error.Message;
                }
                try { RetainRestoreFailure(error, target, interrupted); }
                catch (Exception evidenceError) { Report.reason += "\nFailure receipt could not be written: " + evidenceError; }
                try
                {
                    JoinCurrentWorker();
                    Need(interruptedNumeric != null && interruptedShell != null && interrupted != null, "Interrupted rollback packet unavailable.");
                    Need(HashFile(interrupted.binaryPath) == interrupted.binarySha256 && HashFile(interrupted.shellPath) == interrupted.shellSha256, "Interrupted rollback packet changed.");
                    StartRestoredWorker(interruptedNumeric); PublishRestoredShell(interruptedShell);
                    Report.restoreStatus = "failed-target-recovered-interrupted-state"; Report.status = "paused";
                    Record("paused", "failed-restore-recovered-interrupted-state");
                }
                catch (Exception recoveryError)
                {
                    Report.restoreStatus = "restore-and-recovery-failed"; Report.restoreRecoveryError = recoveryError.ToString();
                    Fault(new InvalidOperationException("Saved restore and interrupted-state recovery failed; stock transition requires safe restoration.", new AggregateException(error, recoveryError)));
                }
                return error.Message;
            }
        }
        void JoinCurrentWorker()
        {
            if (worker == null) return;
            Need(worker.Stop(10000), "Replacement worker still executing; fixed/native lease remains held.");
            Need(worker.ReadStatus().Stopped, "Replacement worker did not report joined shutdown."); Report.workerJoined = true;
            worker.Dispose();
        }
        void StartRestoredWorker(OwnedFlightCheckpointData numeric)
        {
            worker = new OwnedFlightWorker(binding.InitializeWorker, new OwnedFlightWorkerOptions { AutomaticProgress = true, SourceId = "ksp-owned-clock-" + epoch });
            Report.workerJoined = false;
            Need(worker.WaitUntilReady(10000), "Fresh restore worker did not become ready.");
            var ready = worker.ReadStatus();
            Need(ready.Fault == null && ready.Progress.Paused && ready.Progress.DebtSeconds == 0, "Fresh worker is not a debt-free paused replacement context.");
            var restoreNumeric = binding.RestoreCheckpoint;
            Wait(worker.Invoke(c =>
            {
                var restored = restoreNumeric(c, numeric);
                Need(restored != null && restored.TimelineState != null && restored.TimelineState.paused, "Restored execution must include its paused demand queue.");
                c.Replace(restored.Host, restored.Session, restored.Imported, restored.TimelineState);
            }, OwnedFlightWorkerInvocation.CheckpointReplacement));
            var state = worker.ReadStatus();
            Need(state.Fault == null && state.Progress.Paused && state.Progress.Stamp.Time == numeric.timeline.time && state.Progress.DebtSeconds == numeric.timeline.requestedRuntimeTime - numeric.timeline.time, "Fresh restore changed the saved frontier or demand.");
        }
        void PublishRestoredShell(ShellData shell)
        {
            bridge.RestoreShell(shell, sessionId); bridge.AllowCheckpointPublication();
            using (var view = worker.AcquirePresentation())
            { Need(view != null && view.Progress.Paused && view.UniversalTime == shell.universalTime, "Restored coherent shell/numeric publication unavailable."); bridge.Publish(view); }
            controls = Wait(worker.Invoke(c => c.Timeline.HeldControls)); inputAck = null;
        }
        void RetainRestoreFailure(Exception error, CheckpointReceipt target, CheckpointReceipt interrupted)
        {
            string candidatePath = null, candidateHash = null, candidateShellPath = null, candidateShellHash = null, candidateError = null;
            try
            {
                if (worker != null && worker.ReadStatus().Ready && !worker.ReadStatus().Stopped)
                {
                    Wait(worker.SetPause(true)); var candidate = Wait(worker.Invoke(binding.CaptureCheckpoint));
                    string prefix = Path.Combine(directory, "failed-restore-candidate-" + epoch + "-" + (++checkpointSequence));
                    candidatePath = prefix + ".ofcb"; byte[] bytes = OwnedRuntimeCheckpointBinary.EncodeFlight(candidate);
                    using (var file = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Write(bytes, 0, bytes.Length);
                    candidateHash = HashFile(candidatePath);
                    // Failed native/shell publication can differ from the candidate bank. Retain both without claiming a coherent checkpoint pair.
                    candidateShellPath = prefix + ".shell.xml"; OwnedClockShellCodec.WriteNew(candidateShellPath, bridge.CaptureShell(sessionId)); candidateShellHash = HashFile(candidateShellPath);
                }
            }
            catch (Exception candidateFailure) { candidateError = candidateFailure.ToString(); }
            string path = Path.Combine(directory, "restore-failure-" + epoch + "-" + (++checkpointSequence) + ".json");
            var receipt = new RestoreFailureReceipt { sessionId = sessionId, runId = runId, processId = Report.processId, ownershipEpoch = epoch,
                error = error.ToString(), targetCheckpointPath = target == null ? null : target.receiptPath, interruptedCheckpointPath = interrupted == null ? null : interrupted.receiptPath,
                candidateBinaryPath = candidatePath, candidateBinarySha256 = candidateHash, candidateShellPath = candidateShellPath, candidateShellSha256 = candidateShellHash,
                candidateCheckpointError = candidateError, worker = worker == null ? default(OwnedFlightWorkerStatus) : worker.ReadStatus(), bridge = bridge == null ? null : bridge.Report };
            WriteNew(path, ReportJson.Encode(receipt)); Report.restoreFailurePath = path; Report.restoreFailureSha256 = HashFile(path);
        }
        CheckpointReceipt SavePacket(OwnedFlightCheckpointData numeric, ShellData shell, OwnedFlightPresentationLease view, bool becomeLatest = true, string operation = "checkpoint")
        {
            Need(numeric != null && numeric.timeline != null && numeric.timeline.time == view.Read.Stamp.Time && shell.universalTime == view.UniversalTime, "Numeric/shell checkpoint epochs differ.");
            int sequence = ++checkpointSequence;
            string prefix = Path.Combine(directory, "checkpoint-" + epoch + "-" + sequence);
            string binary = prefix + ".ofcb", shellPath = prefix + ".shell.xml", receiptPath = prefix + ".json";
            byte[] bytes = OwnedRuntimeCheckpointBinary.EncodeFlight(numeric);
            using (var file = new FileStream(binary, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Write(bytes, 0, bytes.Length);
            OwnedClockShellCodec.WriteNew(shellPath, shell);
            ValidatePersistedShell(shell, shellPath);
            var stamp = view.Read.Stamp;
            var receipt = new CheckpointReceipt { sessionId = sessionId, vesselId = vessel.id.ToString("D"), runId = runId, processId = Report.processId, ownershipEpoch = epoch,
                receiptPath = receiptPath, operation = operation,
                helperSha256 = Report.helperSha256, modelScope = binding.ModelScope, nativeSha256 = binding.NativeSha256, partsSha256 = binding.PartsSha256, contextSha256 = binding.ContextSha256,
                binaryPath = binary, binarySha256 = HashFile(binary), shellPath = shellPath, shellSha256 = HashFile(shellPath), universalTime = view.UniversalTime, debtSeconds = view.Progress.DebtSeconds,
                epoch = stamp.Epoch, stateRevision = stamp.StateRevision, topologyRevision = stamp.TopologyRevision, definitionRevision = stamp.DefinitionRevision, frameRevision = stamp.FrameRevision };
            WriteNew(receiptPath, ReportJson.Encode(receipt));
            if (becomeLatest)
            { string receiptHash = HashFile(receiptPath); latestCheckpoint = receipt; Report.checkpointPath = receiptPath; Report.checkpointSha256 = receiptHash; }
            return receipt;
        }
        void ValidatePersistedShell(ShellData original, string path)
        {
            Report.shellRoundtripChecks++;
            try
            {
                var decoded = OwnedClockShellCodec.Read<ShellData>(path);
                bridge.ValidateShell(decoded, sessionId);
                Need(decoded.schema == original.schema && decoded.sessionId == original.sessionId && decoded.vesselId == original.vesselId &&
                    BitConverter.DoubleToInt64Bits(decoded.universalTime) == BitConverter.DoubleToInt64Bits(original.universalTime) &&
                    original.modules != null && decoded.modules.Length == original.modules.Length, "Persisted shell roundtrip changed identity, time bits or module coverage.");
                for (int i = 0; i < original.modules.Length; i++)
                {
                    var expected = original.modules[i]; var actual = decoded.modules[i];
                    Need(expected != null && actual != null && actual.partId == expected.partId && actual.moduleIndex == expected.moduleIndex &&
                        string.Equals(actual.sourceType, expected.sourceType, StringComparison.Ordinal) && string.Equals(actual.node, expected.node, StringComparison.Ordinal),
                        "Persisted shell roundtrip changed module identity/order or ConfigNode content at row " + i.ToString(CultureInfo.InvariantCulture) + ".");
                }
                Report.shellRoundtripRows += decoded.modules.Length; Report.shellRoundtripStatus = "persisted-identity-content-and-time-bits-exact"; Report.shellRoundtripError = null;
            }
            catch (Exception error)
            { Report.shellRoundtripStatus = "failed-before-checkpoint-assignment"; Report.shellRoundtripError = error.ToString(); throw; }
        }

        void Acquire()
        {
            MainThread(); Need(!failed && !stopping && !handingBack, "Service is not available for acquisition.");
            if (IsOwned) return;
            Report.acquisitionRetryable = false;
            Need(bridge == null && worker == null && !establishedOwner, "Prior acquisition still has an unresolved owner.");
            string acquisition = Path.Combine(directory, "epoch-" + (++epoch).ToString(CultureInfo.InvariantCulture));
            bool installationAttempted = false;
            Report.acquisitionEpoch = epoch; Report.acquisitionStage = "create-output"; Report.acquisitionError = null; Report.acquisitionCleanupError = null;
            Report.acquisitionFailurePath = null; Report.acquisitionFailureSha256 = null;
            Report.worker = default(OwnedFlightWorkerStatus); Report.bridge = null; Report.workerJoined = false; Report.fixedRestored = false; Report.nativeRestored = false;
            Report.partialPublication = false; Report.outstandingDemandSeconds = 0;
            try
            {
                Need(!Directory.Exists(acquisition) && !File.Exists(acquisition), "Fresh acquisition epoch output required."); Directory.CreateDirectory(acquisition);
                Report.acquisitionStage = "eligibility"; Need(factory != null, "Owned clock acquisition factory is unavailable.");
                StartupWorkScheduler.RetireAfterStartupForClock();
                var selected = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                string reason = OwnedClockKspBridge.EligibilityReason(selected); Need(reason == null, reason);
                Report.acquisitionStage = "capture-and-model"; vessel = selected; binding = factory.Acquire(vessel, acquisition, runId);
                Need(binding != null && binding.InitializeWorker != null && binding.CaptureCheckpoint != null && binding.RestoreCheckpoint != null, "Complete numeric acquisition/checkpoint contract required.");
                Report.acquisitionStage = "bridge-construction"; bridge = new OwnedClockKspBridge(this, vessel, binding); Report.bridge = bridge.Report;
                Report.acquisitionStage = "worker-initialization";
                worker = new OwnedFlightWorker(binding.InitializeWorker, new OwnedFlightWorkerOptions { AutomaticProgress = true, SourceId = "ksp-owned-clock-" + epoch });
                Need(worker.WaitUntilReady(10000), "Owned worker did not become ready.");
                var status = worker.ReadStatus(); Need(status.Fault == null, "Owned worker initialization failed: " + status.Fault);
                Report.acquisitionStage = "bridge-preparation";
                using (var view = worker.AcquirePresentation()) { Need(view != null, "Initial coherent worker view unavailable."); bridge.Prepare(view); }
                Report.acquisitionStage = "bridge-installation"; installationAttempted = true; bridge.Install();
                Report.acquisitionStage = "initial-publication";
                using (var view = worker.AcquirePresentation()) { Need(view != null, "Initial worker publication unavailable."); bridge.Publish(view); }
                Report.acquisitionStage = "start-demand";
                controls = bridge.ReadControls(); Wait(worker.Configure(controls, 1, false, false, false, binding.Native.universalTime + 300)); authorityEnabled = true; establishedOwner = true;
                Report.status = "owned"; Report.modelScope = binding.ModelScope; Report.ownershipEpoch = epoch; Report.vesselId = vessel.id.ToString("D");
                Report.sourceSha256 = binding.Parts.sourceAssemblySha256; Report.sourceMvid = binding.Parts.sourceAssemblyMvid; Report.acquisitionStage = "owned"; Record("owned", "acquired");
            }
            catch (Exception error)
            {
                Report.acquisitionError = error.ToString(); var attemptedBridge = bridge;
                bool safe = false;
                try
                {
                    if (!installationAttempted)
                    {
                        // Prepare only reads source/native state. Calling Dispose on an incomplete Prepare would write uninitialized original clock values.
                        if (worker != null) { JoinCurrentWorker(); Report.worker = worker.ReadStatus(); }
                        Need(bridge == null || !bridge.OwnsFixedLoop, "Unexpected pre-install fixed ownership.");
                        worker = null; bridge = null; binding = null; vessel = null; inputAck = null; authorityEnabled = false;
                        Report.cleanupStatus = "pre-authority-no-native-or-fixed-mutation"; safe = true;
                    }
                    else
                    {
                        HandbackBeforeStockTransition("acquisition-failed:" + Report.acquisitionStage);
                        safe = worker == null && bridge == null && attemptedBridge != null && attemptedBridge.Report.nativeRestored && attemptedBridge.Report.fixedRestored && attemptedBridge.Report.hooksRemoved && Report.workerJoined && attemptedBridge.Report.publications == 0 && !attemptedBridge.Report.partialPublication;
                        Need(safe, "Acquisition reached native/shell publication or cleanup is not fully verified; it remains a latched failure.");
                    }
                }
                catch (Exception cleanup) { Report.acquisitionCleanupError = cleanup.ToString(); }
                Report.acquisitionRetryable = safe && Report.acquisitionCleanupError == null && worker == null && bridge == null;
                if (!Report.acquisitionRetryable) failed = true;
                Report.status = Report.acquisitionRetryable ? "stock-acquisition-refused" : "invalid-acquisition"; Report.reason = Report.acquisitionError;
                Record(Report.status, Report.acquisitionStage + ":" + error.Message);
                try
                {
                    string path = Path.Combine(acquisition, "acquisition-failure.json");
                    var receipt = new AcquisitionFailureReceipt { sessionId = sessionId, runId = runId, processId = Report.processId, ownershipEpoch = epoch, helperSha256 = Report.helperSha256,
                        stage = Report.acquisitionStage, error = Report.acquisitionError, cleanupError = Report.acquisitionCleanupError, installationAttempted = installationAttempted,
                        retryable = Report.acquisitionRetryable, workerJoined = Report.workerJoined, nativeRestored = attemptedBridge != null && attemptedBridge.Report.nativeRestored,
                        fixedRestored = attemptedBridge != null && attemptedBridge.Report.fixedRestored, hooksRemoved = attemptedBridge != null && attemptedBridge.Report.hooksRemoved,
                        worker = Report.worker, bridge = attemptedBridge == null ? null : attemptedBridge.Report };
                    WriteNew(path, ReportJson.Encode(receipt)); Report.acquisitionFailurePath = path; Report.acquisitionFailureSha256 = HashFile(path);
                }
                catch (Exception evidence) { Report.reason += "\nAcquisition failure receipt could not be written: " + evidence; }
                throw;
            }
        }

        internal void BeforeScriptUpdate()
        {
            if (!IsOwned || handingBack || stopping) return;
            try
            {
                MainThread(); string reason = bridge.CurrentEligibilityReason();
                if (reason != null) { HandbackBeforeStockTransition(reason); return; }
                var status = worker.ReadStatus(); Need(status.Fault == null, "Owned worker fault: " + status.Fault);
                if (status.Progress.DeadlineReached) { HandbackBeforeStockTransition("source-domain-deadline"); return; }
                using (var view = worker.AcquirePresentation())
                {
                    if (view == null) { Report.reason = "publication-gap-retained-prior-epoch"; return; }
                    bridge.Publish(view); Report.publications++; Report.publishedUniversalTime = view.UniversalTime;
                    if (Report.reason == "publication-gap-retained-prior-epoch") Report.reason = null;
                }
                if (inputAck != null && inputAck.IsCompleted) { var observed = inputAck.Result; inputAck = null; }
                bridge.CollectInput(); if (!IsOwned) return; var next = bridge.ReadControls();
                if (!SameControls(controls, next))
                { if (inputAck != null && !inputAck.IsCompleted) return; controls = next; inputAck = worker.SetLiveControls(next); Report.inputUpdates++; }
            }
            catch (Exception error) { Fault(error); }
        }
        internal void StockWarp(float rate)
        { if (IsOwned) { Need(Finite(rate) && rate > 0 && rate <= 1000000, "Stock warp demand outside owned bounds."); Wait(worker.SetWarp(rate)); } }
        public void HandbackBeforeStockTransition(string reason)
        {
            MainThread(); if (handingBack) throw new InvalidOperationException("Reentrant stock handback.");
            if (bridge == null && worker == null) { authorityEnabled = false; return; }
            handingBack = true;
            try
            {
                Report.handbackCheckpointStatus = "not-attempted"; Report.handbackCheckpointError = null; Report.handbackCheckpointAttemptPrefix = null; Report.handbackReceiptError = null;
                if (worker != null)
                {
                    OwnedFlightCheckpointData numeric = null;
                    if (establishedOwner && worker.ReadStatus().Ready && worker.ReadStatus().Fault == null)
                    {
                        Wait(worker.SetPause(true));
                        try { numeric = Wait(worker.Invoke(binding.CaptureCheckpoint)); }
                        catch (Exception checkpointError)
                        { Report.handbackCheckpointStatus = "capture-failed-cleanup-continues"; Report.handbackCheckpointError = checkpointError.ToString(); }
                    }
                    Need(worker.Stop(10000), "Worker still executing; original stock transition is blocked."); Report.workerJoined = worker.ReadStatus().Stopped;
                    using (var view = worker.AcquirePresentation())
                    {
                        if (establishedOwner && bridge != null && bridge.Prepared) { Need(view != null, "Joined final bank unavailable."); bridge.RestoreNative(view); }
                        else if (bridge != null) bridge.RestoreAdmission();
                        if (numeric != null && bridge != null && view != null)
                        {
                            Report.handbackCheckpointAttemptPrefix = Path.Combine(directory, "checkpoint-" + epoch + "-" + (checkpointSequence + 1).ToString(CultureInfo.InvariantCulture));
                            try { SavePacket(numeric, bridge.CaptureShell(sessionId), view); Report.handbackCheckpointStatus = "saved-and-persisted-shell-qualified"; }
                            catch (Exception checkpointError)
                            { Report.handbackCheckpointStatus = "save-failed-cleanup-continues"; Report.handbackCheckpointError = checkpointError.ToString(); }
                        }
                        else if (Report.handbackCheckpointError == null) Report.handbackCheckpointStatus = "not-available";
                    }
                }
                else if (bridge != null) bridge.RestoreAdmission();
                if (bridge != null) { bridge.Dispose(); Report.nativeRestored = bridge.Report.nativeRestored; Report.fixedRestored = bridge.Report.fixedRestored; }
                authorityEnabled = false; Report.status = failed ? "invalid-handed-back" : "stock-handed-back"; Report.reason = reason;
                Record(Report.status, reason); RefreshReport(); CopyEvents();
                try { WriteNew(Path.Combine(directory, "epoch-" + epoch + "-handback.json"), ReportJson.Encode(Report)); }
                catch (Exception receiptError) { Report.handbackReceiptError = receiptError.ToString(); }
                worker = null; bridge = null; binding = null; vessel = null; establishedOwner = false;
            }
            finally { handingBack = false; }
        }
        void Fault(Exception error)
        {
            failed = true; Report.reason = error.ToString(); Report.status = "invalid"; UnityEngine.Debug.LogError("[ContinuumOwnedClock] " + Report.reason);
            try { HandbackBeforeStockTransition("fault:" + error.Message); } catch (Exception cleanup) { Report.cleanupStatus = "blocked-recovery"; Report.reason += "\n" + cleanup; }
        }
        public void Update() { if (requested && !stopping) RefreshReport(); }
        void RefreshReport()
        {
            Report.enabled = authorityEnabled; Report.owned = IsOwned; Report.eventCount = eventCount; Report.eventOverwrites = eventOverwrites;
            if (worker != null)
            {
                Report.worker = worker.ReadStatus(); Report.outstandingDemandSeconds = Report.worker.Progress.DebtSeconds;
                if (Report.owned && !failed && !handingBack) Report.status = Report.worker.Progress.Paused ? "paused" : Report.worker.Status;
            }
            if (bridge != null) { Report.bridge = bridge.Report; Report.partialPublication = bridge.Report.partialPublication; }
        }
        void CopyEvents()
        { var rows = new EventRow[eventCount]; int begin = (eventWrite - eventCount + events.Length) % events.Length; for (int i = 0; i < rows.Length; i++) rows[i] = events[(begin + i) % events.Length]; Report.events = rows; }
        void Record(string state, string reason)
        {
            events[eventWrite] = new EventRow { ticks = Stopwatch.GetTimestamp(), frame = Time.frameCount, universalTime = Planetarium.GetUniversalTime(), ownershipEpoch = epoch,
                numericalEpoch = worker == null ? Report.worker.Epoch : worker.ReadStatus().Epoch, state = state, reason = reason };
            eventWrite = (eventWrite + 1) % events.Length; if (eventCount < events.Length) eventCount++; else eventOverwrites++;
        }
        void MainThread() { Need(Thread.CurrentThread.ManagedThreadId == threadId, "Owned KSP service requires its main thread."); }
        internal static T Wait<T>(OwnedFlightWorkerAcknowledgement<T> ack)
        {
            if (!ack.Wait(10000)) { bool cancelled = ack.TryCancel(); throw new TimeoutException(cancelled ? "Worker command cancelled before execution." : "Worker command began but remains pending; no rollback is claimed."); }
            return ack.Result;
        }
        static bool SameControls(OwnedRuntimeControls a, OwnedRuntimeControls b)
        { return a.Throttle == b.Throttle && a.Attitude.X == b.Attitude.X && a.Attitude.Y == b.Attitude.Y && a.Attitude.Z == b.Attitude.Z && a.Translation.X == b.Translation.X && a.Translation.Y == b.Translation.Y && a.Translation.Z == b.Translation.Z && a.EnginesEnabled == b.EnginesEnabled && a.RcsEnabled == b.RcsEnabled && a.WheelsEnabled == b.WheelsEnabled; }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        internal static string HashFile(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream)); }
        internal static string Hex(byte[] hash) { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }
        static void ValidateDirectory(string path)
        { Need(!string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && Directory.Exists(path), "Existing absolute receipt directory required."); for (var d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent) Need((d.Attributes & FileAttributes.ReparsePoint) == 0, "Receipt ancestry is symlinked."); }
        internal static void WriteNew(string path, string content) { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) using (var writer = new StreamWriter(stream)) writer.Write(content); }
        public void OnDestroy() { Shutdown("scene-destroy"); }
        public void OnApplicationQuit() { Shutdown("application-quit"); }
        void Shutdown(string reason)
        {
            if (!requested || stopping) return;
            try { HandbackBeforeStockTransition(reason); stopping = true; Report.cleanupStatus = "restored-stock"; }
            catch (Exception error) { failed = true; Report.reason = error.ToString(); Report.cleanupStatus = "blocked-recovery"; }
            if (active == this && !IsOwned) active = null;
        }
    }
}
