using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;

namespace KspContinuum
{
    public sealed class Probe : IDisposable
    {
        sealed class Slot
        {
            public Recorder Recorder;
            public bool EnabledBefore;
            public MarkerReport Report;
        }
        static Probe owner;
        static readonly string[] Names = { "Physics.Simulate", "Physics.Processing", "BehaviourFixedUpdate", "BehaviourUpdate", "GC.Collect" };
        readonly List<Slot> slots = new List<Slot>();
        readonly Stopwatch clock = new Stopwatch();
        ProbeReport report;
        PlayerLoopTiming playerLoop;
        ActiveVesselWriterCensus writerCensus;
        ActiveVesselPhysicsSubstitutionCanary substitutionCanary;
        PartForceObservation partForces;
        FixedCallbackAttribution callbackAttribution;
        FlightIntegratorUpdateAttribution integratorUpdateAttribution;
        ConvectionCapture convectionCapture;
        ConvectionReplacement convectionReplacement;
        AeroIntegrationAttribution aeroIntegrationAttribution;
        AeroStepContext aeroStepContext;
        ProcessCaptureDiagnostics processCapture;
        ProcessCaptureDiagnostics.Boundary processStartBoundary, processEndBoundary;
        DryBuoyancyRuntime dryBuoyancy;
        VesselRuntimeCapture vesselRuntime;
        Action<ProbeReport> completion;
        bool started, finished;
        int completed;
        Vessel lastVessel;
        string lastVesselId;
        int structuralRigidbodies = -1, structuralJoints = -1, structuralColliders = -1;
        const int FrameCount = 300;
        readonly string dryBuoyancyStrategy;
        readonly bool forcePlayerLoop;
        readonly double simulationSeconds, wallTimeoutSeconds;
        readonly string signalDirectory;
        double captureStartWall, captureStartUt;
        int fixedCallbacks, capturedFixedCallbacks, physicslessParts = -1, moduleCount = -1;
        bool topologyHooks, captureActive;
        FlightPerturbation perturbation;
        Exception perturbationError;
        public VesselRuntimeCapture.CaptureReport VesselRuntimeReport { get { return vesselRuntime == null ? null : vesselRuntime.Report; } }

        [Serializable] sealed class CaptureSignal
        {
            public string schema = "continuum-flight-capture-signal/v1", runId, status, utc, reason;
            public double universalTime;
            public double requestedSimulatedSeconds, actualSimulatedSeconds, captureWallSeconds;
            public long clockTimestamp, clockFrequency;
            public int processId;
            public string machClockStatus = "not-requested";
            public long machAbsoluteBeforeTicks, machAbsoluteAfterTicks;
            public uint machTimebaseNumerator, machTimebaseDenominator;
        }

        public Probe(string dryBuoyancyStrategy = null, bool forcePlayerLoop = false, double simulationSeconds = 0,
            double wallTimeoutSeconds = 60, string signalDirectory = null)
        { this.dryBuoyancyStrategy = dryBuoyancyStrategy; this.forcePlayerLoop = forcePlayerLoop;
            this.simulationSeconds = simulationSeconds; this.wallTimeoutSeconds = wallTimeoutSeconds; this.signalDirectory = signalDirectory; }

        public static void ObserveFixedCallback()
        {
            if (owner == null) return;
            owner.fixedCallbacks++;
            if (!owner.captureActive || owner.perturbation == null || owner.perturbationError != null) return;
            try { owner.perturbation.FixedStep(); }
            catch (Exception error) { owner.perturbationError = error; }
        }

        public IEnumerator Run(Action<ProbeReport> complete)
        {
            if (complete == null) throw new ArgumentNullException("complete");
            if (started || finished) throw new InvalidOperationException("Probe instances support one capture.");
            if (owner != null) throw new InvalidOperationException("Another Continuum timing capture is already running.");
            started = true; owner = this; completion = complete;
            try
            {
                int requestedFrames = simulationSeconds > 0 ? (VesselRuntimeCapture.Requested ? VesselRuntimeFormat.MaxFrames : 20000) : FrameCount;
                report = new ProbeReport {
                    requestedFrames = requestedFrames, frames = new ProfileFrame[requestedFrames], markers = new MarkerReport[Names.Length],
                    unity = Application.unityVersion, ksp = Versioning.GetVersionString(),
                    plugin = typeof(Probe).Assembly.GetName().Version.ToString(), platform = Application.platform.ToString(),
                    processor = SystemInfo.processorType, processorCount = SystemInfo.processorCount,
                    graphicsDevice = SystemInfo.graphicsDeviceName, targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount
                };
                report.captureMode = simulationSeconds > 0 ? "simulation-duration" : "rendered-frame-count";
                report.requestedSimulatedSeconds = simulationSeconds; report.wallTimeoutSeconds = wallTimeoutSeconds;
                report.effectivePhysicsFrameLimit = GameSettings.PHYSICS_FRAME_DT_LIMIT; report.effectiveMaximumDeltaTime = Time.maximumDeltaTime;
                report.screenWidth=Screen.width; report.screenHeight=Screen.height; report.qualityLevel=QualitySettings.GetQualityLevel();
                report.antiAliasing=QualitySettings.antiAliasing; report.shadowDistance=QualitySettings.shadowDistance; report.shadowCascades=QualitySettings.shadowCascades;
                report.graphicsDeviceType=SystemInfo.graphicsDeviceType.ToString(); report.graphicsDeviceVersion=SystemInfo.graphicsDeviceVersion;
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-flight-diagnostic") >= 0)
                    report.cameraContract="diagnostic-controls.json records requested and effective seeded FREE camera; engine camera adjustments remain possible";
                foreach (string argument in Environment.GetCommandLineArgs()) if (argument.StartsWith("--continuum-research-run=", StringComparison.Ordinal))
                    report.runId = argument.Substring("--continuum-research-run=".Length);
                for (int i = 0; i < Names.Length; i++)
                {
                    var row = new MarkerReport { name = Names[i], status = "unavailable", availabilityDetail = "Recorder unavailable in this player or marker has not been created.",
                        nanoseconds = new long[report.requestedFrames], blocks = new int[report.requestedFrames], available = new bool[report.requestedFrames] };
                    report.markers[i] = row;
                }
                if (VesselRuntimeCapture.Requested && Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-callback-attribution") >= 0)
                    throw new InvalidOperationException("Vessel runtime recording excludes broad callback attribution.");
                vesselRuntime = VesselRuntimeCapture.Create(FlightGlobals.ActiveVessel, report.runId);
                foreach (MarkerReport row in report.markers) Acquire(row);
                string[] arguments = Environment.GetCommandLineArgs();
                if (Environment.GetEnvironmentVariable("CONTINUUM_PROCESS_CAPTURE") == "1")
                {
                    processCapture = new ProcessCaptureDiagnostics(report.runId);
                    report.processCapture = processCapture.Report;
                }
                convectionReplacement = ConvectionReplacement.Create(arguments, report.runId, () => captureActive);
                if (convectionReplacement != null) report.convectionReplacement = convectionReplacement.Report;
                aeroStepContext = AeroStepContext.Create(arguments, report.runId, () => captureActive);
                if (aeroStepContext != null)
                {
                    report.aeroStepContext = aeroStepContext.Report;
                    if (aeroStepContext.Report.status != "installed") throw new InvalidOperationException("AeroStepContext: " + aeroStepContext.Report.detail);
                }
                aeroIntegrationAttribution = AeroIntegrationAttribution.Create(arguments, report.runId, () => captureActive);
                if (aeroIntegrationAttribution != null) report.aeroIntegrationAttribution = aeroIntegrationAttribution.Report;
                convectionCapture = ConvectionCapture.Create(arguments, report.runId, () => captureActive);
                if (convectionCapture != null) report.convectionCapture = convectionCapture.Report;
                string canaryReason;
                bool canaryRequested = ActiveVesselPhysicsSubstitutionCanary.RequestedAndQualified(arguments, out canaryReason);
                if (canaryRequested && canaryReason != null)
                {
                    report.substitutionCanary = new PhysicsSubstitutionCanaryReport { status = "invalid", reason = canaryReason };
                    throw new InvalidOperationException(canaryReason);
                }
                if (Array.IndexOf(arguments, "--continuum-writer-census") >= 0)
                {
                    writerCensus = new ActiveVesselWriterCensus(); report.writerCensus = writerCensus.Census.Report;
                }
                if (canaryRequested)
                {
                    substitutionCanary = new ActiveVesselPhysicsSubstitutionCanary(writerCensus);
                    report.substitutionCanary = substitutionCanary.Report;
                }
                if (Array.IndexOf(arguments, "--continuum-callback-attribution") >= 0)
                {
                    callbackAttribution = new FixedCallbackAttribution(convectionReplacement); callbackAttribution.Start();
                    report.callbackAttribution = callbackAttribution.Report;
                }
                if (Array.IndexOf(arguments, "--continuum-flight-integrator-update-attribution") >= 0)
                {
                    if (callbackAttribution != null) throw new InvalidOperationException("Narrow integrator attribution excludes broad callback attribution.");
                    integratorUpdateAttribution = new FlightIntegratorUpdateAttribution(); integratorUpdateAttribution.Start();
                    report.flightIntegratorUpdateAttribution = integratorUpdateAttribution.Report;
                }
                bool explicitStock = dryBuoyancyStrategy == DryBuoyancyRuntime.Stock;
                string requestedDryStrategy = explicitStock ? null : dryBuoyancyStrategy;
                if (!explicitStock && requestedDryStrategy == null && Array.IndexOf(arguments, "--continuum-dry-buoyancy") >= 0)
                    requestedDryStrategy = DryBuoyancyRuntime.FullPublication;
                if (requestedDryStrategy != null)
                {
                    dryBuoyancy = new DryBuoyancyRuntime(requestedDryStrategy); dryBuoyancy.Start(); report.dryBuoyancy = dryBuoyancy.Report;
                    if (dryBuoyancy.Report.status != "installed") throw new InvalidOperationException("Dry buoyancy admission did not install: " + dryBuoyancy.Report.status);
                }
                if (forcePlayerLoop || Array.IndexOf(arguments, "--continuum-playerloop") >= 0 || writerCensus != null || callbackAttribution != null || integratorUpdateAttribution != null || dryBuoyancy != null)
                {
                    IPlayerLoopBracketObserver observer = writerCensus == null ? null : writerCensus.Census;
                    if (substitutionCanary != null) observer = new CompositePlayerLoopObserver(observer, substitutionCanary);
                    if (callbackAttribution != null) observer = new CompositePlayerLoopObserver(observer, callbackAttribution);
                    if (integratorUpdateAttribution != null) observer = new CompositePlayerLoopObserver(observer, integratorUpdateAttribution);
                    if (dryBuoyancy != null) observer = new CompositePlayerLoopObserver(observer, dryBuoyancy);
                    playerLoop = new PlayerLoopTiming(observer); playerLoop.Start(); report.playerLoop = playerLoop.Report;
                    if (substitutionCanary != null) substitutionCanary.Start();
                }
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-part-forces") >= 0)
                {
                    partForces = new PartForceObservation();
                    partForces.Start();
                    report.partForces = partForces.Report;
                }
                clock.Start();
                if(simulationSeconds > 0) { GameEvents.onVesselWasModified.Add(VesselModified); GameEvents.onPartDie.Add(PartDied); topologyHooks=true; }
                // Recorder counters describe the previous frame; discard the partly enabled initial frame.
                yield return null;
                if (finished) yield break;
                if (aeroStepContext != null && !aeroStepContext.ReadyForCapture)
                {
                    aeroStepContext.Report.dragWarmupWaitStartedTicks = Stopwatch.GetTimestamp();
                    while (!aeroStepContext.ReadyForCapture)
                    {
                        if (aeroStepContext.QualificationFailed || clock.Elapsed.TotalSeconds >= wallTimeoutSeconds)
                            throw new InvalidOperationException("Aero drag qualification failed or timed out before capture.");
                        yield return null;
                        if (finished) yield break;
                    }
                    aeroStepContext.Report.dragWarmupWaitEndedTicks = Stopwatch.GetTimestamp();
                }
                perturbation = FlightPerturbation.Create(arguments, FlightGlobals.ActiveVessel, report.runId);
                if (perturbation != null)
                {
                    report.perturbation = perturbation.Report;
                    if (simulationSeconds <= 0 || perturbation.Report.requestedStartSeconds + perturbation.Report.requestedDurationSeconds >= simulationSeconds)
                        throw new ArgumentException("Perturbation requires a simulation-duration capture extending beyond the pulse.");
                }
                report.captureStartUtc = DateTime.UtcNow.ToString("o"); captureStartUt = Planetarium.GetUniversalTime();
                ProfileFrame previous = Context();
                report.clockFrequency=Stopwatch.Frequency;
                processStartBoundary = processCapture == null ? null : processCapture.MarkBoundary();
                report.captureStartClockTimestamp = processStartBoundary == null ? Stopwatch.GetTimestamp() : processStartBoundary.clockTimestamp;
                if (processCapture != null) processCapture.Begin(processStartBoundary);
                report.researchPanel = ResearchPanelPresentation.BeginCapture(report.captureStartClockTimestamp);
                FlightLocalCamera.BeginWindow(report.captureStartClockTimestamp);
                FlightDiagnosticControls.BeginWindow(report.captureStartClockTimestamp);
                FlightPartBatcher.CaptureStart(report.captureStartClockTimestamp);
                captureStartWall=clock.Elapsed.TotalSeconds; previous.boundaryWallSeconds=captureStartWall; capturedFixedCallbacks=fixedCallbacks;
                Signal("capture-start", null);
                captureActive=true;
                for (int frame = 0; frame < report.requestedFrames; frame++)
                {
                    yield return null;
                    if (finished) yield break;
                    if (perturbationError != null)
                    {
                        report.status = "invalid"; report.completionReason = "perturbation-error";
                        throw new InvalidOperationException("Flight perturbation failed.", perturbationError);
                    }
                    if (playerLoop != null)
                    {
                        playerLoop.Audit();
                        if (dryBuoyancy != null && playerLoop.Report.integrityStatus == "invalidated")
                        {
                            dryBuoyancy.Dispose();
                            dryBuoyancy = null;
                        }
                    }
                    if (partForces != null) partForces.Tick();
                    if (vesselRuntime != null)
                        try { vesselRuntime.CaptureFrame(); }
                        catch { report.status = "invalid"; report.completionReason = "vessel-runtime-capture-invalid"; throw; }
                    double now = clock.Elapsed.TotalSeconds;
                    previous.observedFrame = Time.frameCount;
                    previous.markerFrame = Time.frameCount - 1;
                    previous.contextAligned = previous.contextFrame == previous.markerFrame;
                    previous.wallMilliseconds = (now - previous.boundaryWallSeconds) * 1000;
                    previous.fixedCallbacksPerFrame = fixedCallbacks - capturedFixedCallbacks; capturedFixedCallbacks = fixedCallbacks;
                    foreach (Slot slot in slots) Read(slot, frame);
                    report.frames[frame] = previous;
                    completed++;
                    report.actualSimulatedSeconds = Planetarium.GetUniversalTime() - captureStartUt;
                    report.captureWallSeconds = now - captureStartWall;
                    if (simulationSeconds > 0 && report.actualSimulatedSeconds >= simulationSeconds) { report.status = "complete"; report.completionReason = "simulation-duration-reached"; break; }
                    if (simulationSeconds > 0 && report.captureWallSeconds >= wallTimeoutSeconds) { report.status = "invalid"; report.completionReason = "capture-wall-timeout"; break; }
                    if (completed < report.requestedFrames) previous = Context();
                }
                captureActive=false;
                processEndBoundary = processCapture == null ? null : processCapture.MarkBoundary();
                report.captureEndClockTimestamp = processEndBoundary == null ? Stopwatch.GetTimestamp() : processEndBoundary.clockTimestamp;
                if (processCapture != null) processCapture.End(processEndBoundary, simulationSeconds <= 0 || report.status == "complete");
                ResearchPanelPresentation.EndCapture(report.researchPanel, report.captureEndClockTimestamp);
                if (report.researchPanel.status != "complete") { report.status = "invalid"; report.completionReason = "research-panel-visibility-or-callback-invalid"; }
                FlightLocalCamera.EndWindow(report.captureEndClockTimestamp);
                FlightDiagnosticControls.EndWindow(report.captureEndClockTimestamp);
                FlightPartBatcher.CaptureEnd(report.captureEndClockTimestamp);
                if(simulationSeconds > 0 && completed > 0)
                {
                    ProfileFrame last = Context(true), first=report.frames[0];
                    report.topologyStable=report.topologyEvents==0 && last.vesselId==first.vesselId && last.parts==first.parts && last.rigidbodies==first.rigidbodies &&
                        last.joints==first.joints && last.colliders==first.colliders && last.moduleCount==first.moduleCount &&
                        last.physicslessParts==first.physicslessParts && last.loadedVessels==first.loadedVessels;
                    if(!report.topologyStable) { report.status="invalid"; report.completionReason="final-topology-changed"; }
                }
                if (simulationSeconds <= 0) { report.status = "complete"; report.completionReason = "frame-count-reached"; }
                else if (report.status == "interrupted") { report.status = "invalid"; report.completionReason = "frame-cap-before-duration"; }
                Finish(false);
            }
            finally { Dispose(); }
        }

        void Acquire(MarkerReport row)
        {
            try
            {
                Recorder recorder = Recorder.Get(row.name);
                if (recorder == null || !recorder.isValid) return;
                var slot = new Slot { Recorder = recorder, EnabledBefore = recorder.enabled, Report = row };
                slots.Add(slot);
                if (!slot.EnabledBefore) recorder.enabled = true;
                row.recorderAvailableAtStart = recorder.enabled;
                row.availabilityDetail = recorder.enabled ? "Recorder valid and enabled; per-frame availability is recorded separately." : "Recorder could not be enabled.";
            }
            catch (Exception error) { row.availabilityDetail = "Recorder setup failed: " + error.GetType().Name; }
        }

        static void Read(Slot slot, int index)
        {
            try
            {
                if (!slot.Recorder.isValid || !slot.Recorder.enabled)
                {
                    slot.Report.availabilityDetail = "Recorder became invalid or disabled during capture.";
                    return;
                }
                long nanoseconds = slot.Recorder.elapsedNanoseconds;
                int blocks = slot.Recorder.sampleBlockCount;
                slot.Report.nanoseconds[index] = nanoseconds;
                slot.Report.blocks[index] = blocks;
                slot.Report.available[index] = nanoseconds >= 0 && blocks >= 0 && (blocks != 0 || nanoseconds == 0);
                if (!slot.Report.available[index]) slot.Report.availabilityDetail = "Native counters contained an invalid or inconsistent reading; its availability is false.";
            }
            catch (Exception error) { slot.Report.availabilityDetail = "Recorder read failed: " + error.GetType().Name; }
        }

        ProfileFrame Context(bool boundary = false)
        {
            var frame = new ProfileFrame {
                contextFrame = Time.frameCount, renderedFrame = Time.renderedFrameCount, boundaryWallSeconds = clock.Elapsed.TotalSeconds,
                fixedDeltaSeconds = Time.fixedDeltaTime, timeScale = Time.timeScale, scene = HighLogic.LoadedScene.ToString(),
                maximumDeltaSeconds = Time.maximumDeltaTime, physicsFrameLimitSeconds = GameSettings.PHYSICS_FRAME_DT_LIMIT,
                warpMode = TimeWarp.fetch == null ? "unavailable" : TimeWarp.WarpMode.ToString(),
                screenWidth = Screen.width, screenHeight = Screen.height, parts = -1,
                rigidbodies = -1, joints = -1, colliders = -1, loadedVessels = -1,
                managedBytes = GC.GetTotalMemory(false), gcGeneration0 = GC.CollectionCount(0), gcGeneration1 = GC.CollectionCount(1), gcGeneration2 = GC.CollectionCount(2),
                vesselStatus = "unavailable-no-active-vessel"
            };
            if (HighLogic.CurrentGame != null) frame.universalTime = Planetarium.GetUniversalTime();
            if (TimeWarp.fetch != null) frame.warpRate = TimeWarp.CurrentRate;
            if (HighLogic.LoadedSceneIsFlight) frame.paused = FlightDriver.Pause;
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (vessel == null) return frame;
            if (lastVessel != vessel) { lastVessel = vessel; lastVesselId = vessel.id.ToString("D"); structuralRigidbodies = -1; }
            frame.vesselId = lastVesselId;
            frame.vesselStatus = vessel.loaded ? "loaded" : "unloaded";
            frame.loaded = vessel.loaded; frame.packed = vessel.packed;
            frame.throttleCommand = vessel.ctrlState == null ? (double?)null : vessel.ctrlState.mainThrottle;
            frame.parts = vessel.parts == null ? -1 : vessel.parts.Count;
            if (FlightGlobals.VesselsLoaded != null) frame.loadedVessels = FlightGlobals.VesselsLoaded.Count;
            // Census the whole vessel only at capture boundaries. Per-frame hierarchy walks would
            // perturb the same part-count scaling this probe is intended to measure.
            if (structuralRigidbodies < 0 || boundary || completed == 0 || completed == report.requestedFrames - 1)
            {
                StructuralCensusResult census = StructuralCensusCapture.Capture(vessel);
                structuralRigidbodies = census.rigidbodies;
                structuralJoints = census.joints;
                structuralColliders = census.colliders;
                physicslessParts = 0; moduleCount = 0;
                foreach (Part part in vessel.parts) if (part != null) { if (part.physicalSignificance == Part.PhysicalSignificance.NONE) physicslessParts++; if (part.Modules != null) moduleCount += part.Modules.Count; }
            }
            frame.rigidbodies = structuralRigidbodies; frame.joints = structuralJoints; frame.colliders = structuralColliders;
            frame.physicslessParts = physicslessParts; frame.moduleCount = moduleCount;
            frame.body = vessel.mainBody == null ? null : vessel.mainBody.bodyName;
            frame.altitudeMeters=vessel.altitude; frame.atmosphericDensity=vessel.atmDensity;
            if(vessel.mainBody!=null) frame.atmosphereDepthMeters=vessel.mainBody.atmosphereDepth;
            frame.situation = vessel.situation.ToString();
            if (vessel.mainBody != null)
            {
                frame.bodyRadiusMeters = vessel.mainBody.Radius;
                frame.bodyMuMetersCubedPerSecondSquared = vessel.mainBody.gravParameter;
            }
            if (vessel.orbit != null)
            {
                Vector3d position = vessel.orbit.pos;
                Vector3d velocity = vessel.orbit.vel;
                double radius = position.magnitude;
                if (radius > 0 && !double.IsNaN(radius) && !double.IsInfinity(radius))
                {
                    frame.orbitalRadiusMeters = radius;
                    frame.orbitalSpeedMetersPerSecond = velocity.magnitude;
                    frame.radialSpeedMetersPerSecond = Vector3d.Dot(position, velocity) / radius;
                }
            }
            if (perturbation != null) perturbation.Observe(frame);
            return frame;
        }

        void Finish(bool suppressExportFailure)
        {
            if (finished) return;
            finished = true;
            var errors = new List<string>();
            if (aeroStepContext != null)
            {
                try
                {
                    aeroStepContext.Dispose();
                    string reason;
                    if (!aeroStepContext.IsValidFinal(out reason)) errors.Add("AeroStepContext: " + reason);
                }
                catch (Exception error) { errors.Add("AeroStepContext: " + error.GetType().Name); }
                aeroStepContext = null;
            }
            if (aeroIntegrationAttribution != null)
            {
                try
                {
                    aeroIntegrationAttribution.Dispose();
                    string reason;
                    if (!aeroIntegrationAttribution.IsValidFinal(out reason)) errors.Add("AeroIntegrationAttribution: " + reason);
                }
                catch (Exception error) { errors.Add("AeroIntegrationAttribution: " + error.GetType().Name); }
                aeroIntegrationAttribution = null;
            }
            if (callbackAttribution != null)
            {
                try { callbackAttribution.Dispose(); }
                catch (Exception error) { errors.Add("CallbackAttribution: " + error.GetType().Name); }
                if (callbackAttribution.Report.cleanupStatus == "cleanup-error") errors.Add("CallbackAttribution: cleanup-error");
                if (convectionReplacement != null && callbackAttribution.Report.status != "observed")
                    errors.Add("CallbackAttribution: candidate coexistence " + callbackAttribution.Report.status);
                callbackAttribution = null;
            }
            if (convectionReplacement != null)
            {
                try
                {
                    convectionReplacement.Dispose();
                    string reason;
                    if (!convectionReplacement.IsValidFinal(out reason)) errors.Add("ConvectionReplacement: " + reason);
                }
                catch (Exception error) { errors.Add("ConvectionReplacement: " + error.GetType().Name); }
                convectionReplacement = null;
            }
            if (convectionCapture != null)
            {
                try { convectionCapture.Dispose(); }
                catch (Exception error) { errors.Add("ConvectionCapture: " + error.GetType().Name); }
                if (convectionCapture.Report.status != "complete") errors.Add("ConvectionCapture: " + convectionCapture.Report.status);
                convectionCapture = null;
            }
            if (substitutionCanary != null)
            {
                try { substitutionCanary.Dispose(); }
                catch (Exception error) { errors.Add("SubstitutionCanary: " + error.GetType().Name); }
                substitutionCanary = null;
            }
            if (playerLoop != null)
            {
                try { playerLoop.Dispose(); }
                catch (Exception error) { errors.Add("PlayerLoop: " + error.GetType().Name); }
                if (playerLoop.Report.cleanupStatus == "cleanup-error") errors.Add("PlayerLoop: cleanup-error");
                playerLoop = null;
            }
            if (writerCensus != null)
            {
                try { writerCensus.Dispose(); }
                catch (Exception error) { errors.Add("WriterCensus: " + error.GetType().Name); }
                writerCensus = null;
            }
            if (integratorUpdateAttribution != null)
            {
                try { integratorUpdateAttribution.Dispose(); }
                catch (Exception error) { errors.Add("IntegratorUpdateAttribution: " + error.GetType().Name); }
                if (integratorUpdateAttribution.Report.cleanupStatus == "cleanup-error") errors.Add("IntegratorUpdateAttribution: cleanup-error");
                integratorUpdateAttribution = null;
            }
            if (dryBuoyancy != null)
            {
                try { dryBuoyancy.Dispose(); }
                catch (Exception error) { errors.Add("DryBuoyancy: " + error.GetType().Name); }
                if (dryBuoyancy.Report.cleanupStatus == "cleanup-error") errors.Add("DryBuoyancy: cleanup-error");
                if (dryBuoyancy.Report.status != "complete") errors.Add("DryBuoyancy: " + dryBuoyancy.Report.status);
                dryBuoyancy = null;
            }
            if (partForces != null)
            {
                try { partForces.Dispose(); }
                catch (Exception error) { errors.Add("PartForces: " + error.GetType().Name); }
                if (partForces.Report.cleanupStatus == "cleanup-error") errors.Add("PartForces: cleanup-error");
                partForces = null;
            }
            foreach (Slot slot in slots)
            {
                try { if (!slot.EnabledBefore && slot.Recorder.enabled) slot.Recorder.enabled = false; }
                catch (Exception error) { errors.Add(slot.Report.name + ": " + error.GetType().Name); }
            }
            slots.Clear();
            captureActive=false;
            if (perturbation != null)
            {
                if (perturbationError != null) perturbation.Report.status = "invalid-perturbation-error";
                if (!perturbation.Finish())
                { report.status = "invalid"; report.completionReason = "incomplete-or-failed-perturbation"; }
                if (signalDirectory != null)
                    try { File.WriteAllText(Path.Combine(signalDirectory, "perturbation.json"), ReportJson.Encode(perturbation.Report)); }
                    catch (Exception error) { errors.Add("PerturbationExport: " + error.GetType().Name); }
                perturbation = null;
            }
            if(topologyHooks) { GameEvents.onVesselWasModified.Remove(VesselModified); GameEvents.onPartDie.Remove(PartDied); topologyHooks=false; }
            if (ReferenceEquals(owner, this)) owner = null;
            clock.Stop();
            Action<ProbeReport> callback = completion;
            completion = null;
            if (report == null || callback == null)
            {
                if (processCapture != null) { processCapture.Dispose(); processCapture = null; }
                return;
            }
            try
            {
                report.cleanupErrors = errors.ToArray();
                report.recorderCleanupStatus = errors.Count == 0 ? "restored-owned-enables" : "restoration-errors";
                if (errors.Count != 0) report.status = "cleanup-error";
                if(simulationSeconds > 0 && report.playerLoop!=null && report.playerLoop.scopes!=null)
                    foreach(LoopTimingScope scope in report.playerLoop.scopes) if(scope.droppedSamples>0 || scope.sequenceErrors>0)
                    { report.status="invalid"; report.completionReason="truncated-or-invalid-playerloop"; }
                ProfilingSummary.Finish(report, completed);
                report.captureEndUtc = DateTime.UtcNow.ToString("o");
                if (report.completionReason == null) report.completionReason = "capture-interrupted";
                if(report.captureStartClockTimestamp>0)
                {
                    if(report.captureEndClockTimestamp==0)
                    {
                        processEndBoundary = processCapture == null ? null : processCapture.MarkBoundary();
                        report.captureEndClockTimestamp = processEndBoundary == null ? Stopwatch.GetTimestamp() : processEndBoundary.clockTimestamp;
                        if (processCapture != null) processCapture.End(processEndBoundary, false);
                    }
                    ResearchPanelPresentation.EndCapture(report.researchPanel, report.captureEndClockTimestamp);
                    if (report.researchPanel != null && report.researchPanel.status != "complete") { report.status = "invalid"; report.completionReason = "research-panel-visibility-or-callback-invalid"; }
                    FlightLocalCamera.EndWindow(report.captureEndClockTimestamp);
                    FlightDiagnosticControls.EndWindow(report.captureEndClockTimestamp);
                    FlightPartBatcher.CaptureEnd(report.captureEndClockTimestamp);
                }
                if (vesselRuntime != null)
                {
                    vesselRuntime.Finish(report.status == "complete", report.completionReason);
                    if (vesselRuntime.Report.status != "complete")
                    { report.status = "invalid"; report.completionReason = "vessel-runtime-" + vesselRuntime.Report.status; }
                }
                if (report.captureStartClockTimestamp > 0) Signal("capture-end", report.completionReason);
                callback(report);
            }
            catch (Exception error)
            {
                if (!suppressExportFailure) throw;
                // Teardown must continue even if the destination became unwritable.
                UnityEngine.Debug.LogError("[Continuum] Partial profiling report export failed: " + error.GetType().Name);
                UnityEngine.Debug.LogException(error);
            }
            finally { if (processCapture != null) { processCapture.Dispose(); processCapture = null; } }
        }

        public void Dispose() { Finish(true); }
        void VesselModified(Vessel vessel) { if(captureActive && ReferenceEquals(vessel,lastVessel)) report.topologyEvents++; }
        void PartDied(Part part) { if(captureActive && part!=null && ReferenceEquals(part.vessel,lastVessel)) report.topologyEvents++; }
        void Signal(string name, string reason)
        {
            if (signalDirectory == null) return;
            var signal = new CaptureSignal { runId = report.runId, status = name == "capture-start" ? "capturing" : report.status,
                utc = DateTime.UtcNow.ToString("o"), reason = reason, universalTime = name == "capture-start" ? captureStartUt : captureStartUt + report.actualSimulatedSeconds,
                requestedSimulatedSeconds=report.requestedSimulatedSeconds, actualSimulatedSeconds=report.actualSimulatedSeconds,
                captureWallSeconds=report.captureWallSeconds,
                clockTimestamp = name=="capture-start" ? report.captureStartClockTimestamp : report.captureEndClockTimestamp,
                clockFrequency = Stopwatch.Frequency, processId = Process.GetCurrentProcess().Id };
            ProcessCaptureDiagnostics.Boundary boundary = name == "capture-start" ? processStartBoundary : processEndBoundary;
            if (boundary != null)
            {
                signal.machClockStatus = boundary.machClockStatus;
                signal.machAbsoluteBeforeTicks = boundary.machAbsoluteBeforeTicks;
                signal.machAbsoluteAfterTicks = boundary.machAbsoluteAfterTicks;
                signal.machTimebaseNumerator = boundary.machTimebaseNumerator;
                signal.machTimebaseDenominator = boundary.machTimebaseDenominator;
            }
            string path = Path.Combine(signalDirectory, name + ".json"), temporary = path + ".pending";
            File.WriteAllText(temporary, JsonUtility.ToJson(signal)); File.Move(temporary, path);
        }
    }
}
