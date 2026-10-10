using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class FlightAeroBatchService : MonoBehaviour
    {
        [Serializable] sealed class Transition
        {
            public string schema = "continuum-flight-aero-live/v1", sessionId, vesselId, state, reason, epochDirectory;
            public int processId, epoch, attempt, frame, parts;
            public double universalTime, wallSeconds, preparationMilliseconds;
        }
        string sessionId, root, journal, epochDirectory, state = "stock", reason;
        Vessel tracked;
        int trackedParts, epoch, attempt, releaseFrame = -1;
        double stableAfter, retryAfter;
        bool admitted, operatorEnabled = true, ownedBatch, hooks, stopping;
        const double DebounceSeconds = 1;
        const int MaximumAttempts = 3;

        public void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "--continuum-flight-aero-live") < 0) return;
            try
            {
                if (Array.IndexOf(args, "--continuum-scale-profile") >= 0 || FlightAeroBatcher.activeRequested)
                    throw new InvalidOperationException("Live aero service excludes laboratory capture ownership.");
                if (!File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json")))
                    throw new InvalidOperationException("Private laboratory required.");
                foreach (string arg in args) if (arg.StartsWith("--continuum-flight-live-output=", StringComparison.Ordinal))
                {
                    if (root != null) throw new ArgumentException("Duplicate live output directory.");
                    root = arg.Substring("--continuum-flight-live-output=".Length);
                }
                if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                    throw new ArgumentException("Absolute --continuum-flight-live-output directory required.");
                sessionId = Guid.NewGuid().ToString("N");
                root = Path.Combine(Path.GetFullPath(root), "live-" + sessionId);
                Directory.CreateDirectory(root); journal = Path.Combine(root, "transitions.jsonl");
                hooks = true;
                GameEvents.onVesselChange.Add(VesselChanged);
                GameEvents.onVesselGoOnRails.Add(VesselChanged);
                GameEvents.onVesselGoOffRails.Add(VesselChanged);
                GameEvents.onVesselWasModified.Add(Modified);
                GameEvents.onStageActivate.Add(Stage);
                GameEvents.onPartCouple.Add(Coupled);
                GameEvents.onPartUndock.Add(PartChanged);
                GameEvents.onPartDeCouple.Add(PartChanged);
                GameEvents.onPartDie.Add(PartChanged);
                GameEvents.onGameSceneLoadRequested.Add(Scene);
                admitted = true; Record("stock", "service-enabled", 0);
            }
            catch (Exception error) { UnityEngine.Debug.LogError("[ContinuumAeroLive] rejected: " + error.Message); Stop("admission-failed"); }
        }
        void VesselChanged(Vessel vessel) { Invalidate("vessel-or-rails-change"); }
        void Modified(Vessel vessel) { if (vessel == tracked) Invalidate("vessel-modified"); }
        void Stage(int stage) { Invalidate("staging"); }
        void Coupled(GameEvents.FromToAction<Part, Part> change) { Invalidate("docking"); }
        void PartChanged(Part part) { if (part != null && part.vessel == tracked) Invalidate("part-change"); }
        void Scene(GameScenes scene) { Stop("scene-change"); }

        void Invalidate(string cause)
        {
            if (!admitted || stopping) return;
            Release(cause); epoch++; attempt = 0; retryAfter = 0;
            stableAfter = Time.realtimeSinceStartup + DebounceSeconds;
            Record("stabilizing", cause, 0);
        }
        void Release(string cause)
        {
            if (!ownedBatch) return;
            ownedBatch = false;
            try { FlightAeroBatcher.Finish(); }
            finally { releaseFrame = Time.frameCount; }
        }
        public void Update()
        {
            if (!admitted || stopping) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.F8)) Toggle("keyboard-toggle");
                Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                int count = vessel == null || vessel.parts == null ? 0 : vessel.parts.Count;
                if (vessel != tracked || count != trackedParts)
                {
                    Invalidate("active-vessel-or-part-count"); tracked = vessel; trackedParts = count;
                }
                bool eligible = operatorEnabled && HighLogic.LoadedSceneIsFlight && vessel != null && vessel.loaded && !vessel.packed && count > 0;
                if (!eligible)
                {
                    if (ownedBatch) { Release("not-eligible"); Record("stock", "not-eligible", 0); }
                    stableAfter = Time.realtimeSinceStartup + DebounceSeconds; return;
                }
                if (ownedBatch)
                {
                    string fault = FlightAeroBatcher.FailureReason;
                    if (fault != null) { Release(fault); Backoff(fault, 0); }
                    return;
                }
                if (attempt >= MaximumAttempts || Time.realtimeSinceStartup < stableAfter || Time.realtimeSinceStartup < retryAfter || Time.frameCount <= releaseFrame + 1) return;
                if (FlightAeroBatcher.IsActive) { Record("paused", "other-batch-owner", 0); retryAfter = Time.realtimeSinceStartup + 2; return; }
                attempt++; epochDirectory = Path.Combine(root, "epoch-" + epoch + "-attempt-" + attempt);
                Directory.CreateDirectory(epochDirectory);
                var timer = Stopwatch.StartNew();
                FlightAeroBatcher.Report result = FlightAeroBatcher.StartExplicit(vessel, epochDirectory, sessionId + "-" + epoch + "-" + attempt);
                ownedBatch = true; timer.Stop();
                if (result == null || result.faults != 0 || FlightAeroBatcher.FailureReason != null)
                {
                    string failure = result == null ? "no-provider-report" : result.reason;
                    Release(failure); Backoff(failure, timer.Elapsed.TotalMilliseconds);
                }
                else Record("batched-experimental", "prepared", timer.Elapsed.TotalMilliseconds);
            }
            catch (Exception error)
            {
                try { Release("service-error"); } catch (Exception cleanup) { UnityEngine.Debug.LogException(cleanup); }
                operatorEnabled = false; Record("stock-disabled", error.GetType().Name + ": " + error.Message, 0);
                UnityEngine.Debug.LogException(error);
            }
        }
        void Backoff(string cause, double preparation)
        {
            retryAfter = Time.realtimeSinceStartup + (attempt <= 1 ? 2 : 8);
            stableAfter = retryAfter;
            Record(attempt >= MaximumAttempts ? "stock-retries-exhausted" : "stock-backoff", cause, preparation);
        }
        void Record(string next, string cause, double preparation)
        {
            state = next; reason = cause;
            if (journal == null) return;
            var row = new Transition { sessionId = sessionId, processId = Process.GetCurrentProcess().Id, vesselId = tracked == null ? null : tracked.id.ToString("D"),
                epoch = epoch, attempt = attempt, frame = Time.frameCount, parts = trackedParts, state = next, reason = cause,
                epochDirectory = epochDirectory, universalTime = Planetarium.GetUniversalTime(), wallSeconds = Time.realtimeSinceStartup,
                preparationMilliseconds = preparation };
            try { File.AppendAllText(journal, JsonUtility.ToJson(row) + Environment.NewLine); }
            catch (Exception error) { UnityEngine.Debug.LogError("[ContinuumAeroLive] journal failure: " + error.Message); }
        }
        public void OnGUI()
        {
            if (!admitted || stopping) return;
            bool previous = GUI.enabled;
            GUI.enabled = true;
            try
            {
                GUILayout.BeginArea(new Rect(Math.Max(0, Screen.width - 295), 85, 280, 112), "Continuum aero experiment", GUI.skin.window);
                GUILayout.Label(state);
                if (GUILayout.Button(operatorEnabled ? "Use stock effects" : "Enable experimental effects")) Toggle("operator-toggle");
                GUILayout.Label("F8: switch stock / experimental");
                GUILayout.EndArea();
            }
            finally { GUI.enabled = previous; }
        }
        void Toggle(string cause)
        {
            operatorEnabled = !operatorEnabled; Invalidate(operatorEnabled ? "operator-enabled" : "operator-disabled");
            Record(operatorEnabled ? "stabilizing" : "stock-disabled", cause, 0);
        }
        void Stop(string cause)
        {
            if (stopping) return; stopping = true;
            try { Release(cause); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
            if (hooks)
            {
                GameEvents.onVesselChange.Remove(VesselChanged); GameEvents.onVesselGoOnRails.Remove(VesselChanged); GameEvents.onVesselGoOffRails.Remove(VesselChanged);
                GameEvents.onVesselWasModified.Remove(Modified); GameEvents.onStageActivate.Remove(Stage); GameEvents.onPartCouple.Remove(Coupled);
                GameEvents.onPartUndock.Remove(PartChanged); GameEvents.onPartDeCouple.Remove(PartChanged); GameEvents.onPartDie.Remove(PartChanged);
                GameEvents.onGameSceneLoadRequested.Remove(Scene); hooks = false;
            }
            Record("stock-stopped", cause, 0); admitted = false;
        }
        public void OnDisable() { Stop("component-disabled"); }
        public void OnDestroy() { Stop("component-destroyed"); }
        public void OnApplicationQuit() { Stop("application-quit"); }
    }
}
