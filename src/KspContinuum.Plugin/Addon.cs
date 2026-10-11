using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    public abstract class Panel : MonoBehaviour
    {
        string status = "Ready. Runs only when requested.";
        bool running;
        bool automatedBench;
        Bench bench;
        Probe probe;
        FlightTimeline timeline;
        ShadowCapture shadow;
        LifecycleTraceCapture lifecycleTrace;
        PhysicsBoundaryQualification physicsBoundary;
        StructuralExperimentCapture structuralExperiment;
        AeroCaptureSession aeroCapture;
        string pendingStructuralMode;
        Coroutine lifecycleContinuation;
        bool lifecycleExportAttempted;
        bool physicsBoundaryExportAttempted;
        public bool ShadowRunning { get { return shadow != null && shadow.IsRunning; } }
        public string ShadowStatus { get { return shadow == null ? "Shadow not started." : shadow.Status; } }
        public string ShadowReportPath { get; private set; }
        public string LifecycleTraceReportPath { get; private set; }
        public string PhysicsBoundaryReportPath { get; private set; }
        public string StructuralExperimentReportPath { get; private set; }
        public string StructuralVesselCensusReportPath { get; private set; }
        public string AeroCaptureReportPath { get; private set; }
        string replayFile = "replay.csv";
        protected abstract bool IsMenu { get; }
        static bool Supported { get { return Versioning.version_major == 1 && Versioning.version_minor == 12 && Versioning.Revision == 5; } }
        public void Start()
        {
            ResearchPanelPresentation.Attach(this);
            if (!IsMenu && Supported && Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-rigid-cluster-shadow") >= 0) BeginRigidClusterShadowCapture();
            else if (!IsMenu && Supported && Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-shadow") >= 0) BeginShadowCapture();
            if (!IsMenu && Supported && Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-lifecycle-trace") >= 0) BeginLifecycleTrace();
            if (!IsMenu && Supported && Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-qualify-physics-boundary") >= 0) BeginPhysicsBoundaryQualification();
            if (!IsMenu || Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-bench") < 0) return;
            automatedBench = true;
            if (!Supported) { Application.Quit(2); return; }
            RunBench();
        }
        public void Update()
        {
            ResearchPanelPresentation.ObserveService(this, false);
            ResearchPanelPresentation.PollToggle(this);
            if (lifecycleTrace != null)
            {
                lifecycleTrace.ObserveUpdate();
                FinishLifecycleTrace();
            }
            if (physicsBoundary != null)
            {
                physicsBoundary.Audit();
                FinishPhysicsBoundaryQualification();
            }
            if (structuralExperiment != null) structuralExperiment.Tick();
            if (aeroCapture != null && aeroCapture.ReachedBound) aeroCapture.Dispose();
            FinishAeroCapture();
            if (shadow != null) shadow.Tick(true);
            if (timeline == null) return;
            if (Input.GetKeyDown(KeyCode.Escape) && timeline.IsReplaying) timeline.Stop();
            timeline.Tick();
        }
        public void FixedUpdate()
        {
            ResearchPanelPresentation.ObserveService(this, true);
            if (shadow != null) shadow.FixedBoundary();
            if (lifecycleTrace != null) lifecycleTrace.ObserveFixedUpdate();
        }
        public void LateUpdate() { if (shadow != null) shadow.Tick(false); }
        public void BeginShadowCapture()
        {
            if (IsMenu || !Supported) throw new InvalidOperationException("Flight shadow requires KSP 1.12.5 flight.");
            if (ShadowRunning) throw new InvalidOperationException("Shadow capture is already active.");
            ShadowReportPath = null;
            shadow = new ShadowCapture(report => ShadowReportPath = Write("shadow", report));
        }
        public void BeginRigidClusterShadowCapture()
        {
            if (IsMenu || !Supported) throw new InvalidOperationException("Flight shadow requires KSP 1.12.5 flight.");
            if (ShadowRunning) throw new InvalidOperationException("Shadow capture is already active.");
            ShadowReportPath = null;
            shadow = new ShadowCapture(new RigidClusterBackend(), "translational-rigid-cluster/v1",
                "All captured vessel bodies share one mass-weighted linear velocity and retain their captured relative position for the predicted step; rotation is not simulated.",
                false, report => ShadowReportPath = Write("rigid-cluster-shadow", report));
        }
        public void StopShadowCapture() { if (shadow != null) shadow.Dispose(); }
        public void BeginLifecycleTrace()
        {
            if (IsMenu || !Supported) throw new InvalidOperationException("Lifecycle trace requires KSP 1.12.5 flight.");
            if (lifecycleTrace != null && lifecycleTrace.IsRunning) throw new InvalidOperationException("Lifecycle trace is already active.");
            FinishLifecycleTrace();
            LifecycleTraceReportPath = null;
            lifecycleExportAttempted = false;
            lifecycleTrace = new LifecycleTraceCapture();
            try
            {
                lifecycleTrace.Start();
                if (lifecycleTrace.IsRunning) lifecycleContinuation = StartCoroutine(TraceAfterFixedUpdate(lifecycleTrace));
                FinishLifecycleTrace();
            }
            catch
            {
                lifecycleTrace.Dispose();
                FinishLifecycleTrace();
                throw;
            }
        }
        IEnumerator TraceAfterFixedUpdate(LifecycleTraceCapture capture)
        {
            while (capture.IsRunning)
            {
                yield return new WaitForFixedUpdate();
                if (capture.IsRunning) capture.ObserveAfterFixedUpdate();
            }
        }
        public void StopLifecycleTrace()
        {
            if (lifecycleTrace != null) lifecycleTrace.Dispose();
            FinishLifecycleTrace();
        }
        void FinishLifecycleTrace()
        {
            if (lifecycleTrace == null || lifecycleTrace.IsRunning) return;
            if (lifecycleContinuation != null)
            {
                StopCoroutine(lifecycleContinuation);
                lifecycleContinuation = null;
            }
            if (lifecycleExportAttempted) return;
            lifecycleExportAttempted = true;
            try { LifecycleTraceReportPath = Write("lifecycle", lifecycleTrace.Report); }
            catch (Exception error)
            {
                status = "Lifecycle trace export failed: " + error.GetType().Name;
                Debug.LogException(error);
            }
        }
        public void BeginPhysicsBoundaryQualification()
        {
            if (IsMenu || !Supported) throw new InvalidOperationException("Physics-boundary qualification requires KSP 1.12.5 flight.");
            if (physicsBoundary != null && physicsBoundary.IsRunning) throw new InvalidOperationException("Physics-boundary qualification is already active.");
            FinishPhysicsBoundaryQualification(); PhysicsBoundaryReportPath = null; physicsBoundaryExportAttempted = false;
            physicsBoundary = new PhysicsBoundaryQualification(); physicsBoundary.Start(); FinishPhysicsBoundaryQualification();
        }
        public void StopPhysicsBoundaryQualification()
        {
            if (physicsBoundary != null) physicsBoundary.Dispose(); FinishPhysicsBoundaryQualification();
        }
        void FinishPhysicsBoundaryQualification()
        {
            if (physicsBoundary == null || physicsBoundary.IsRunning || physicsBoundaryExportAttempted) return;
            physicsBoundaryExportAttempted = true; physicsBoundary.Dispose();
            try { PhysicsBoundaryReportPath = Write("physics-boundary", physicsBoundary.Report); }
            catch (Exception error) { status = "Physics-boundary export failed: " + error.GetType().Name; Debug.LogException(error); }
            if (pendingStructuralMode != null)
            {
                string mode = pendingStructuralMode; pendingStructuralMode = null;
                if (physicsBoundary.Report.status == "qualified") BeginQualifiedStructuralExperiment(mode);
                else status = "Structural experiment stopped: physics boundary did not qualify.";
            }
        }
        public void BeginStructuralExperiment(string mode)
        {
            if (IsMenu || !Supported) throw new InvalidOperationException("Structural experiment requires KSP 1.12.5 flight.");
            if (mode != "sham" && mode != "impulse") throw new ArgumentException("Structural mode must be sham or impulse.");
            if (structuralExperiment != null && structuralExperiment.IsRunning)
                throw new InvalidOperationException("A structural experiment is already active.");
            StopStructuralExperiment(); StructuralExperimentReportPath = null;
            pendingStructuralMode = mode; BeginPhysicsBoundaryQualification();
            status = "Qualifying physics boundary before structural " + mode + ".";
        }
        void BeginQualifiedStructuralExperiment(string mode)
        {
            structuralExperiment = new StructuralExperimentCapture(mode, physicsBoundary.Report, report =>
            {
                try
                {
                    StructuralExperimentReportPath = Write("structural-experiment", report);
                    status = "Structural " + mode + " capture saved in PluginData.";
                }
                catch (Exception error)
                {
                    status = "Structural capture export failed: " + error.GetType().Name;
                    Debug.LogException(error);
                }
            });
            structuralExperiment.Start(); status = structuralExperiment.Status;
        }
        public void StopStructuralExperiment()
        {
            pendingStructuralMode = null;
            if (structuralExperiment != null) structuralExperiment.Dispose();
        }
        public void BeginAeroCapture()
        {
            if (IsMenu || !Supported) throw new InvalidOperationException("Aerodynamic capture requires KSP 1.12.5 flight.");
            if (aeroCapture != null) throw new InvalidOperationException("Aerodynamic capture is already active.");
            AeroCaptureReportPath = null;
            aeroCapture = new AeroCaptureSession();
            aeroCapture.Start();
            FinishAeroCapture();
            status = aeroCapture == null ? status : "Read-only stock aerodynamic capture is running.";
        }
        public void StopAeroCapture()
        {
            if (aeroCapture == null) return;
            aeroCapture.Dispose(); FinishAeroCapture();
        }
        void FinishAeroCapture()
        {
            if (aeroCapture == null || aeroCapture.Report == null) return;
            try
            {
                AeroCaptureReportPath = Write("aero-capture", aeroCapture.Report);
                status = "Aerodynamic capture saved in PluginData.";
            }
            catch (Exception error) { status = "Aerodynamic capture export failed: " + error.GetType().Name; Debug.LogException(error); }
            finally { aeroCapture = null; }
        }
        void RunBench()
        {
            bench = new Bench();
            StartCoroutine(Guard(bench.Run(report =>
            {
                Write("bench", report);
                if (automatedBench) Application.Quit(report.Passed() ? 0 : 1);
            })));
        }
        string Write(string kind, object report)
        {
            string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
            Directory.CreateDirectory(directory);
            string filename = kind + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json";
            string path = Path.Combine(directory, filename);
            string temporary = path + ".tmp";
            try
            {
                string encoded = ReportJson.Encode(report);
                if ((kind == "shadow" || kind == "rigid-cluster-shadow" || kind == "lifecycle" || kind == "physics-boundary" || kind == "structural-experiment" || kind == "aero-capture")
                    && System.Text.Encoding.UTF8.GetByteCount(encoded) > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Observation receipt exceeds its 4 MiB export bound.");
                File.WriteAllText(temporary, encoded);
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            status = "Report saved in GameData/KspContinuum/PluginData.";
            return path;
        }
        IEnumerator Guard(IEnumerator work)
        {
            running = true; status = "Running; results are written when complete.";
            try
            {
                while (true)
                {
                    bool next;
                    try { next = work.MoveNext(); }
                    catch (Exception ex)
                    {
                        status = "Experiment failed: " + ex.GetType().Name; Debug.LogException(ex);
                        if (automatedBench) Application.Quit(1);
                        yield break;
                    }
                    if (!next) break;
                    yield return work.Current;
                }
            }
            finally
            {
                var disposable = work as IDisposable;
                if (disposable != null) disposable.Dispose();
                running = false;
            }
        }
        internal void RenderResearchView()
        {
            GUILayout.BeginArea(new Rect(20, 80, 430, IsMenu ? 195 : 700), "KSP Continuum — research prototype (Ctrl+`)", GUI.skin.window);
            GUILayout.Label(Supported ? status : "Unsupported KSP version; requires 1.12.5.");
            bool old = GUI.enabled; GUI.enabled = old && Supported && !running;
            if (IsMenu)
            {
                GUILayout.Label("Synthetic boxes in isolated physics scenes. No vessel changes.");
                if (GUILayout.Button("Run jointed / compound benchmark"))
                {
                    RunBench();
                }
            }
            else
            {
                GUILayout.Label(lifecycleTrace == null ? "Lifecycle trace not started." : "Lifecycle trace: " + lifecycleTrace.Report.status);
                if (GUILayout.Button("Start read-only lifecycle trace"))
                {
                    try { BeginLifecycleTrace(); }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Stop lifecycle trace")) StopLifecycleTrace();
                GUILayout.Label(physicsBoundary == null ? "Physics boundary not qualified." : "Physics boundary: " + physicsBoundary.Report.status);
                if (GUILayout.Button("Qualify native physics boundary"))
                {
                    try { BeginPhysicsBoundaryQualification(); }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Stop physics-boundary qualification")) StopPhysicsBoundaryQualification();
                GUILayout.Label(structuralExperiment == null ? "Structural experiment not started." : structuralExperiment.Status);
                if (GUILayout.Button("Run structural sham on active vessel"))
                {
                    try { BeginStructuralExperiment("sham"); }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Run structural impulse on active vessel"))
                {
                    try { BeginStructuralExperiment("impulse"); }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Stop structural experiment")) StopStructuralExperiment();
                if (GUILayout.Button("Capture structural vessel census (read only)"))
                {
                    try
                    {
                        StructuralVesselCensusReportPath = Write("structural-vessel-census",
                            StructuralVesselCensusCapture.Capture(FlightGlobals.ActiveVessel));
                    }
                    catch (Exception ex) { status = "Structural census failed: " + ex.GetType().Name; Debug.LogException(ex); }
                }
                GUILayout.Label(ShadowStatus);
                if (GUILayout.Button("Start read-only stock aero capture"))
                {
                    try { BeginAeroCapture(); }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Stop stock aero capture")) StopAeroCapture();
                if (GUILayout.Button("Start read-only worker shadow capture"))
                {
                    try { BeginShadowCapture(); }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Start translational rigid-cluster shadow"))
                {
                    try { BeginRigidClusterShadowCapture(); }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Stop shadow capture")) StopShadowCapture();
                if (timeline == null) timeline = new FlightTimeline();
                GUILayout.Label(timeline.Status);
                if (GUILayout.Button("Record flight inputs"))
                {
                    try { timeline.BeginRecording(FlightGlobals.ActiveVessel); }
                    catch (Exception ex) { status = ex.Message; }
                }
                GUI.enabled = old;
                if (GUILayout.Button("Stop recording / abort replay")) timeline.Stop();
                GUI.enabled = old && Supported && !running;
                replayFile = GUILayout.TextField(replayFile);
                GUILayout.Label("Replay starts from CURRENT state, with SAS off. Stops at discrete events.");
                if (GUILayout.Button("Replay PluginData file"))
                {
                    try
                    {
                        if (Path.GetFileName(replayFile) != replayFile || !replayFile.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("Use a CSV filename directly in PluginData.");
                        timeline.LoadAndReplay(FlightGlobals.ActiveVessel, Path.Combine(KSPUtil.ApplicationRootPath,
                            "GameData", "KspContinuum", "PluginData", replayFile));
                    }
                    catch (Exception ex) { status = ex.Message; }
                }
                if (GUILayout.Button("Inspect active vessel (read only)"))
                {
                    try { Write("vessel", Inspector.Capture(FlightGlobals.ActiveVessel)); }
                    catch (Exception ex) { status = "Inspection failed: " + ex.GetType().Name; Debug.LogException(ex); }
                }
                if (GUILayout.Button("Capture 300 frames of available timing markers"))
                {
                    probe = new Probe(); StartCoroutine(Guard(probe.Run(report => Write("markers", report))));
                }
            }
            GUI.enabled = old; GUILayout.EndArea();
        }
        public void OnDisable() { StopStructuralExperiment(); StopLifecycleTrace(); StopPhysicsBoundaryQualification(); StopAeroCapture(); }
        public void OnDestroy()
        {
            ResearchPanelPresentation.Detach(this);
            StopStructuralExperiment();
            StopLifecycleTrace();
            StopPhysicsBoundaryQualification();
            StopShadowCapture();
            StopAeroCapture();
            StopAllCoroutines();
            if (bench != null) bench.Dispose();
            if (probe != null) probe.Dispose();
            if (timeline != null) timeline.Dispose();
        }
    }
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public sealed class MenuPanel : Panel { protected override bool IsMenu { get { return true; } } }
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class FlightPanel : Panel { protected override bool IsMenu { get { return false; } } }
}
