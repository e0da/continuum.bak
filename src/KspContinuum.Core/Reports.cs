using System;

namespace KspContinuum
{
    [Serializable] public sealed class BenchReport
    {
        public string schema = "ksp-continuum-bench/v1";
        public string status = "synthetic-engine-benchmark-only";
        public string utc = DateTime.UtcNow.ToString("o");
        public string unity;
        public string ksp;
        public string plugin;
        public string platform;
        public float stepSeconds = 0.02f;
        public int warmupSteps = 50, measuredSteps = 200, solverIterations = 6, solverVelocityIterations = 1;
        public Sample[] samples;
        public float splitInitialPoseError, splitLinearMomentumError, splitAngularMomentumError;
        public bool splitInitialPosePassed, splitPassed, collisionPassed;
        public float collisionFinalY;
        public QueuedForceIsolationSample[] queuedForceIsolation;
        public bool Passed()
        {
            if (!splitPassed || !collisionPassed || samples == null || samples.Length != 36 || !QueuedForceIsolationPassed()) return false;
            foreach (var sample in samples)
                if (sample.colliderRayHits != sample.boxes || double.IsNaN(sample.millisecondsPerStep) ||
                    double.IsInfinity(sample.millisecondsPerStep) || sample.millisecondsPerStep <= 0 ||
                    float.IsNaN(sample.maxSpacingError) || float.IsInfinity(sample.maxSpacingError) ||
                    (sample.compound && sample.maxSpacingError > 1e-4f)) return false;
            return true;
        }
        public bool QueuedForceIsolationPassed()
        {
            string[] expectedStrategies = { "baseline", "kinematic-toggle", "sleep-wake", "velocity-rewrite" };
            if (queuedForceIsolation == null || queuedForceIsolation.Length != expectedStrategies.Length) return false;
            var seen = new bool[expectedStrategies.Length];
            foreach (QueuedForceIsolationSample sample in queuedForceIsolation)
            {
                if (sample == null) return false;
                int strategy = Array.IndexOf(expectedStrategies, sample.strategy);
                if (strategy < 0 || seen[strategy]) return false;
                seen[strategy] = true;
                if (!(sample.expectedDeltaVelocity > 0) || float.IsNaN(sample.expectedDeltaVelocity) ||
                    float.IsInfinity(sample.expectedDeltaVelocity) || float.IsNaN(sample.observedDeltaVelocity) ||
                    float.IsInfinity(sample.observedDeltaVelocity) ||
                    (sample.queuedForceStatus != "retained" && sample.queuedForceStatus != "cleared" &&
                    sample.queuedForceStatus != "changed")) return false;
                if (sample.strategy == "baseline" && sample.queuedForceStatus != "retained") return false;
            }
            return true;
        }
    }
    [Serializable] public sealed class QueuedForceIsolationSample
    {
        public string strategy;
        public float expectedDeltaVelocity, observedDeltaVelocity;
        public string queuedForceStatus;
    }
    [Serializable] public sealed class Sample
    {
        public int boxes, bodies, joints, pair, colliderRayHits;
        public bool compound;
        public double millisecondsPerStep;
        public float maxSpacingError;
    }

    [Serializable] public sealed class VesselReport
    {
        public string schema = "ksp-continuum-vessel/v1";
        public string status = "read-only-candidate-inventory-not-merge-approval";
        public string utc = DateTime.UtcNow.ToString("o");
        public int parts, rigidbodies, joints, colliders, candidateParts;
        public bool packed;
        public PartReport[] inventory;
    }
    [Serializable] public sealed class PartReport
    {
        public string partType, exclusion;
        public int index, parentIndex;
        public float dryMass;
    }
    [Serializable] public sealed class MarkerReport
    {
        public string name, status, availabilityDetail;
        public bool recorderAvailableAtStart;
        public long[] nanoseconds;
        public int[] blocks;
        public bool[] available;
        public ProfileMarkerSummary summary;
    }
    [Serializable] public sealed class ResearchPanelReport
    {
        public string schema = "continuum-research-panel/v1", mode, status = "capturing";
        public string scope = "Enclosed IMGUI callbacks inside the Probe capture window; nested wall duration, not additive to whole-frame timing.";
        public bool startVisible, endVisible, startComponentPresent, endComponentPresent;
        public int captureStartFrame, captureEndFrame, modeChanges, errors, blockedModeChangeAttempts;
        public long captureStartClockTimestamp, captureEndClockTimestamp, clockFrequency;
        public long guiCalls, layoutCalls, repaintCalls, otherCalls, guiTicks, layoutTicks, repaintTicks, otherTicks, serviceUpdateCalls, serviceFixedCalls;
    }
    [Serializable] public sealed class ProbeReport
    {
        public ResearchPanelReport researchPanel;
        public string captureMode = "rendered-frame-count", completionReason, runId, captureStartUtc, captureEndUtc;
        public double requestedSimulatedSeconds, actualSimulatedSeconds, captureWallSeconds, wallTimeoutSeconds, settleSeconds;
        public double? requestedCatchupSeconds;
        public double effectivePhysicsFrameLimit, effectiveMaximumDeltaTime;
        public string cameraContract = "loaded-checkpoint-camera-no-injected-camera-controls";
        public bool topologyStable;
        public int topologyEvents;
        public long captureStartClockTimestamp, captureEndClockTimestamp, clockFrequency;
        public int screenWidth, screenHeight, qualityLevel, antiAliasing, shadowCascades;
        public double shadowDistance;
        public string graphicsDeviceType, graphicsDeviceVersion;
        public string schema = "ksp-continuum-markers/v2";
        public string status = "interrupted";
        public string measurementScope = "Observational previous-frame marker readings and callback cadence; not whole-frame CPU attribution.";
        public string fixedCallbackScope = "Owned MonoBehaviour FixedUpdate callbacks between coroutine boundaries; not aligned with Recorder previous-frame totals. Global physics steps must be derived from timestamped PlayerLoop fixed scopes.";
        public string playerLoopWindowScope = "Raw PlayerLoop instrumentation includes warmup and cleanup; use startedTicks/endedTicks and capture timestamps to select fully enclosed scopes, reporting omitted straddlers.";
        public string intervalScope = "Stopwatch interval between coroutine boundaries; includes waiting, scheduling and probe overhead.";
        public string markerScope = "Recorder previous-frame totals; marker scopes may overlap. Availability does not establish complete engine coverage.";
        public string percentileMethod = "Linear interpolation at (count-1)*quantile; marker distributions include only available frames with positive block counts.";
        public string contextScope = "Context sampled at prior coroutine boundary; frame identifiers expose gaps. State can change within a frame.";
        public string unity, ksp, plugin, platform, processor, graphicsDevice;
        public int processorCount, targetFrameRate, vSyncCount, warmupFrames = 1;
        public string recorderCleanupStatus;
        public string[] cleanupErrors;
        public string utc = DateTime.UtcNow.ToString("o");
        public MarkerReport[] markers;
        public int requestedFrames, completedFrames, contextMisalignedFrames;
        public ProfileFrame[] frames;
        public ProfileDistribution wallIntervals;
        public LoopTimingReport playerLoop;
        public FlightPerturbationReport perturbation;
        public WriterCensusReport writerCensus;
        public ForceObservationReport partForces;
        public PhysicsSubstitutionCanaryReport substitutionCanary;
        public CallbackAttributionReport callbackAttribution;
        public object flightIntegratorUpdateAttribution;
        public DryBuoyancyReport dryBuoyancy;
    }

    [Serializable] public sealed class FlightPerturbationReport
    {
        public string schema = "continuum-flight-perturbation/v1", runId, mode, status;
        public string targetFlightId, targetPartName, selection;
        public string clock = "Unity Time.fixedTime relative to capture admission";
        public string forceFrame = "Frozen root-local direction rotated by current root attitude, applied at selected body center of mass";
        public string units = "Unity rigidbody mass/force/impulse units; KSP mass is commonly tonnes, so do not interpret force values as SI newtons";
        public string flexWitness = "Physical parent-child attachment relative pose change from capture admission; cancels whole-craft rigid translation and rotation, not solver joint stress";
        public int processId, appliedCount, cachedBodies, sampledAttachments, observationSamples, peakAwakeBodies;
        public double requestedStartSeconds, requestedDurationSeconds, nominalDeltaVMetersPerSecond, targetMassUnityUnits;
        public double requestedForceUnityUnits, requestedImpulseUnityUnits, appliedImpulseUnityUnits, appliedDurationSeconds;
        public double fixedTimeOriginSeconds, firstAppliedFixedSeconds, lastAppliedFixedSeconds;
        public double observationCadenceSimulatedSeconds = .1;
        public double peakAngularRadiansPerSecond, peakAttachmentPositionDeltaMeters, peakAttachmentRotationDeltaDegrees;
        public Vec rootLocalForceDirection, initialRootLocalLeverMeters, appliedImpulseWorld, appliedAngularImpulseWorld;
    }

    [Serializable] public sealed class DryBuoyancyReport
    {
        public string schema = "ksp-continuum-dry-buoyancy/v1";
        public string strategy = "full-publication-batch";
        public string status = "not-started";
        public string limitation = "Opt-in stock-callback admission for the active loaded vessel in a high orbital dry regime. It republishes dry contact/force state but leaves stock diagnostic geometry and depth fields at their last dry values. Stock remains authoritative for initialization, water proximity and every ambiguous state.";
        public string installationStatus = "not-installed", cleanupStatus = "not-attempted", detail;
        public string[] foreignOwners = new string[0];
        public int ownedComponents;
        public long fixedSteps, domainAdmissions, bypassed, dryPublications, verifiedPublications, fallbacks, errors;
    }

    [Serializable] public sealed class CoastingAdapterReport
    {
        public string schema = "ksp-continuum-coasting-adapter/v2";
        public string status = "not-started";
        public string limitation = "Opt-in packed active-vessel canary. Continuum evaluates a fixed-reference-body two-body coast while KSP retains global time, vessel lifecycle, SOI transitions, rendering and release to stock physics.";
        public string reason, vesselId, referenceBody, installationStatus = "not-installed",
            cleanupStatus = "not-attempted";
        public double seedUniversalTime, forecastUniversalTime, finalGameUniversalTime,
            eventRadiusMeters, eventSearchHorizonSeconds, eventUniversalTime, eventTimeToleranceSeconds,
            eventRadiusErrorMeters,
            eventGuardSeconds, warpStopRequestUniversalTime,
            admittedEccentricity, admittedPeriapsisAltitude, admittedApoapsisRadius,
            maximumPositionErrorMeters, maximumVelocityErrorMetersPerSecond,
            maximumInjectedPositionErrorMeters, maximumInjectedVelocityErrorMetersPerSecond,
            maximumDriverPositionErrorMeters, maximumDriverVelocityErrorMetersPerSecond;
        public int admittedWarpRateIndex;
        public int eventEvaluations, warpRateIndexBeforeStopRequest, warpRateIndexAfterStopRequest;
        public long candidateDriverCalls, suppressedStockPropagations, stockFallbacks, errors;
        public long synchronizationStopwatchFrequency, synchronizationMeasuredCallbacks,
            stateCaptureTicks, forecastAdmissionTicks,
            engineSamples, callbackTicks, maximumCallbackTicks,
            evaluationTicks, maximumEvaluationTicks,
            publicationTicks, maximumPublicationTicks,
            validationTicks, maximumValidationTicks,
            driverRemainderTicks, maximumDriverRemainderTicks;
        public string admittedSphereOfInfluence, admittedPatchEndUniversalTime, admittedPatchEndTransition;
        public string eventKind, eventDirection;
        public bool forecastCompletedBeforePresentation, frontierUnchangedByPresentation,
            authorityAdmissionAttested, authorityExitAttested, releasedToStock;
        public bool eventConfigured, eventFound, engineFrontierAtEvent, warpStopRequested;
        public bool admittedHasNextPatch;
    }

    [Serializable] public sealed class PhysicsSubstitutionCanaryReport
    {
        public string schema = "ksp-continuum-physics-substitution-canary/v1";
        public string status = "not-started";
        public string limitation = "A bounded number of native PhysicsFixedUpdate callbacks already cached for one render frame are deliberately skipped and no replacement dynamics are computed. This is an ownership/restoration canary, not a physics result or gameplay mode.";
        public string reason, vesselId, topologyKey, installationStatus = "not-installed", restorationStatus = "not-attempted";
        public int candidateCallbacks, candidateFrame = -1;
        public long originGeneration;
        public Vec frameVelocity;
        public WriterCensusSnapshot before, after;
        public WriterCensusReport skippedInterval;
    }
}
