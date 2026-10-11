using System;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static int assertions;
    static void Check(bool value) { assertions++; if (!value) throw new Exception("Profiling assertion " + assertions); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static void Near(double actual, double expected) { Check(Math.Abs(actual - expected) < 1e-10); }
    static void Main()
    {
        var scaleSelection = ScaleQualificationSelection.Parse(new[] { "ksp", "--continuum-scale-save", "scenarios",
            "--continuum-scale-checkpoint", "Space Station 1" });
        Check(scaleSelection.Save == "scenarios" && scaleSelection.Checkpoint == "Space Station 1");
        Check(ScaleQualificationSelection.Requested(new[] { "--continuum-scale-save", "scenarios" }));
        Check(!ScaleQualificationSelection.Requested(new[] { "--continuum-scale-profile" }));
        Check(Reject(() => ScaleQualificationSelection.Parse(new[] { "--continuum-scale-save", "../saves", "--continuum-scale-checkpoint", "flight" })));
        Check(Reject(() => ScaleQualificationSelection.Parse(new[] { "--continuum-scale-save", "scenarios", "--continuum-scale-checkpoint", "../persistent" })));
        Check(Reject(() => ScaleQualificationSelection.Parse(new[] { "--continuum-scale-save", "scenarios", "--continuum-scale-save", "other", "--continuum-scale-checkpoint", "flight" })));
        Check(Reject(() => ScaleQualificationSelection.Parse(new[] { "--continuum-scale-save", "scenarios" })));
        var distribution = ProfilingSummary.Distribution(new double[] { 10, 1, 4, 2, 3 });
        Check(distribution != null && distribution.count == 5);
        Near(distribution.minimum, 1); Near(distribution.maximum, 10); Near(distribution.mean, 4);
        Near(distribution.p50, 3); Near(distribution.p95, 8.8); Near(distribution.p99, 9.76);
        Check(ProfilingSummary.Distribution(new double[0]) == null);
        var single = ProfilingSummary.Distribution(new double[] { 0 });
        Near(single.p99, 0); Check(single.count == 1);
        Check(Reject(() => ProfilingSummary.Distribution(new double[] { double.NaN })));
        Check(Reject(() => ProfilingSummary.Distribution(new double[] { double.PositiveInfinity })));
        Check(Reject(() => ProfilingSummary.Distribution(new double[] { -1 })));

        var census = new StructuralCensusAccumulator();
        // Model overlapping Part descendants, explicit Part.rb ownership, and the
        // Vessel root view. Each native component must contribute exactly once.
        census.AddBody(-11); census.AddBody(-12); census.AddBody(-11); census.AddBody(-13);
        census.AddJoint(-21); census.AddJoint(-21); census.AddJoint(-22);
        census.AddCollider(-31); census.AddCollider(-32); census.AddCollider(-31);
        StructuralCensusResult censusResult = census.Snapshot();
        Check(censusResult.rigidbodies == 3 && censusResult.joints == 2 && censusResult.colliders == 2);
        Check(Reject(() => census.AddBody(0)));

        var marker = new MarkerReport { name = "Example", nanoseconds = new long[] { 1000000, 0, 0, 3000000 },
            blocks = new int[] { 2, 0, 0, 1 }, available = new bool[] { true, true, false, true } };
        var summary = ProfilingSummary.Marker(marker);
        Check(summary.availableFrames == 3 && summary.unavailableFrames == 1 && summary.observedFrames == 2 && summary.zeroBlockFrames == 1);
        Check(summary.totalBlocks == 3); Near(summary.observedMilliseconds.mean, 2); Near(summary.observedMilliseconds.p50, 2);
        var absent = ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { 0 }, blocks = new int[] { 0 }, available = new bool[] { false } });
        Check(absent.observedMilliseconds == null && absent.unavailableFrames == 1 && absent.zeroBlockFrames == 0);
        var inactive = ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { 0 }, blocks = new int[] { 0 }, available = new bool[] { true } });
        Check(inactive.observedMilliseconds == null && inactive.unavailableFrames == 0 && inactive.zeroBlockFrames == 1);
        Check(Reject(() => ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { -1 }, blocks = new int[] { 1 }, available = new bool[] { true } })));
        Check(Reject(() => ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { 1 }, blocks = new int[] { 0 }, available = new bool[] { true } })));
        Check(Reject(() => ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[0], blocks = new int[] { 1 }, available = new bool[] { true } })));

        var attribution = new CallbackAttributionAccumulator(1000);
        int slow = attribution.Register(new CallbackAttributionRow { category = "unity-fixed-callback", assembly = "Game", declaringType = "Slow", method = "FixedUpdate" });
        int fast = attribution.Register(new CallbackAttributionRow { category = "stock-provider-seam", assembly = "Game", declaringType = "Fast", method = "Work" });
        attribution.Register(new CallbackAttributionRow { assembly = "Game", declaringType = "Unobserved", method = "Work" });
        attribution.Record(fast, 2, true); attribution.Record(slow, 7, true); attribution.Record(slow, 3, false);
        CallbackAttributionRow[] attributed = attribution.Finish(20);
        Check(attributed.Length == 2 && attributed[0].declaringType == "Slow" && attributed[0].calls == 2);
        Near(attributed[0].inclusiveMilliseconds, 10); Near(attributed[0].meanMilliseconds, 5);
        Near(attributed[0].maximumMilliseconds, 7); Near(attributed[0].fractionOfActiveWindow, .5);
        Check(attributed[0].outermostCalls == 1 && attributed[0].outermostInclusiveTicks == 7);
        Near(attributed[0].outermostInclusiveMilliseconds, 7); Near(attributed[0].outermostFractionOfActiveWindow, .35);
        Check(attributed[1].calls == 1 && attributed[1].inclusiveTicks == 2);
        Check(Reject(() => attribution.Record(slow, 1, true)));

        var coverage = new CallbackAttributionAccumulator(1000);
        coverage.Register(new CallbackAttributionRow { assembly = "Game", declaringType = "Unobserved", method = "Work" });
        CallbackAttributionRow[] unobserved = coverage.Finish(0, true);
        Check(unobserved.Length == 1 && unobserved[0].calls == 0);
        Near(unobserved[0].meanMilliseconds, 0); Near(unobserved[0].outermostMeanMilliseconds, 0);
        Near(unobserved[0].fractionOfActiveWindow, 0);

        var baselinePerformance = Observation("scalar", new[] { 2.0, 4.0, 3.0 }, new long[] { 100, 120, 110 });
        var candidatePerformance = Observation("simd", new[] { 1.0, 2.0, 1.5 }, new long[] { 50, 60, 55 });
        PerformanceObservations.Validate(baselinePerformance);
        PerformanceComparison performance = PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05);
        Near(performance.speedup, 2); Near(performance.candidateToBaselineAllocationRatio.GetValueOrDefault(), 0.5);
        Check(performance.withinMaximumRegression && performance.baselineStrategy == "scalar" && performance.candidateStrategy == "simd");
        Check(performance.environmentSha256 == baselinePerformance.environmentSha256 &&
            performance.measurementProtocol == "test-clock-v1" && performance.sampleProtocol == "test-samples-v1" &&
            performance.samples == 3 && performance.maximumRegressionFraction == 0.05 &&
            performance.allocationKind == "managed-allocated-bytes" && performance.allocationScope == "current-thread");
        candidatePerformance.workload.items = 65;
        Check(Reject(() => PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05)));
        candidatePerformance.workload.items = 64;
        candidatePerformance.compute.milliseconds = new[] { double.NaN, 1.0, 1.0 };
        Check(Reject(() => PerformanceObservations.Validate(candidatePerformance)));
        candidatePerformance = Observation("simd", new[] { 1.0, 2.0, 1.5 }, new long[] { 50, 60, 55 });
        candidatePerformance.environmentSha256 = new string('b', 64);
        Check(Reject(() => PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05)));
        candidatePerformance = Observation("simd", new[] { 1.0, 2.0, 1.5 }, new long[] { 50, 60, 55 });
        candidatePerformance.workload.configurationSha256 = new string('c', 64);
        Check(Reject(() => PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05)));
        candidatePerformance = Observation("simd", new[] { 1.0, 2.0 }, new long[] { 50, 60 });
        Check(Reject(() => PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05)));
        candidatePerformance = Observation("simd", new[] { 1.0, 2.0, 1.5 }, new long[] { 50, 60, 55 });
        candidatePerformance.sampleProtocol = "different-protocol";
        Check(Reject(() => PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05)));
        candidatePerformance = Observation("simd", new[] { 1.0, 2.0, 1.5 }, new long[] { 50, 60, 55 });
        candidatePerformance.total.allocations = new PerformanceAllocationSamples();
        performance = PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05);
        Check(!performance.candidateToBaselineAllocationRatio.HasValue && performance.allocationKind == "unavailable" && performance.allocationScope == "unavailable");
        candidatePerformance.workload.fixtureSha256 = new string('z', 64);
        Check(Reject(() => PerformanceObservations.Validate(candidatePerformance)));
        baselinePerformance = Observation("scalar", new[] { double.MaxValue, double.MaxValue }, new long[] { 1, 1 });
        candidatePerformance = Observation("simd", new[] { double.MaxValue, double.MaxValue }, new long[] { 1, 1 });
        performance = PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05);
        Check(performance.baselineMedianMilliseconds == double.MaxValue && performance.speedup == 1);
        candidatePerformance = Observation("simd", new[] { double.Epsilon, double.Epsilon }, new long[] { 1, 1 });
        Check(Reject(() => PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05)));
        baselinePerformance = Observation("scalar", new[] { double.Epsilon, double.Epsilon }, new long[] { 1, 1 });
        candidatePerformance = Observation("simd", new[] { double.MaxValue, double.MaxValue }, new long[] { 1, 1 });
        Check(Reject(() => PerformanceObservations.Compare(baselinePerformance, candidatePerformance, 0.05)));
        marker.summary = summary;
        var report = new ProbeReport { markers = new[] { marker }, frames = new[] { new ProfileFrame {
            contextFrame = 10, markerFrame = 10, observedFrame = 11, contextAligned = true, wallMilliseconds = 16.7,
            vesselStatus = "unavailable-no-active-vessel", parts = -1, vesselId = null } }, wallIntervals = distribution,
            callbackAttribution = new CallbackAttributionReport { status = "observed", cleanupStatus = "removed-owned-patches",
                callbacks = attributed } };
        using (JsonDocument json = JsonDocument.Parse(ReportJson.Encode(report)))
        {
            Check(json.RootElement.GetProperty("schema").GetString() == "ksp-continuum-markers/v2");
            Check(json.RootElement.GetProperty("frames")[0].GetProperty("parts").GetInt32() == -1);
            Check(json.RootElement.GetProperty("frames")[0].GetProperty("vesselId").ValueKind == JsonValueKind.Null);
            Check(!json.RootElement.GetProperty("markers")[0].GetProperty("available")[2].GetBoolean());
            Near(json.RootElement.GetProperty("markers")[0].GetProperty("summary").GetProperty("observedMilliseconds").GetProperty("mean").GetDouble(), 2);
            Check(json.RootElement.GetProperty("callbackAttribution").GetProperty("schema").GetString() ==
                "ksp-continuum-fixed-callback-attribution/v1");
            Check(json.RootElement.GetProperty("callbackAttribution").GetProperty("callbacks")[0].GetProperty("declaringType").GetString() == "Slow");
        }
        var interrupted = new ProbeReport { requestedFrames = 3, frames = new[] {
            new ProfileFrame { wallMilliseconds = 10, contextAligned = true },
            new ProfileFrame { wallMilliseconds = 30, contextAligned = false },
            new ProfileFrame { wallMilliseconds = 9000, contextAligned = true } },
            markers = new[] { new MarkerReport { name = "Partial", available = new[] { true, false, false },
                nanoseconds = new long[] { 2000000, 0, 0 }, blocks = new[] { 1, 0, 0 } } } };
        ProfilingSummary.Finish(interrupted, 2);
        Check(interrupted.frames.Length == 2 && interrupted.markers[0].nanoseconds.Length == 2 && interrupted.completedFrames == 2);
        Check(interrupted.wallIntervals.count == 2 && interrupted.contextMisalignedFrames == 1);
        Near(interrupted.wallIntervals.mean, 20);
        Check(interrupted.markers[0].status == "observed" && interrupted.markers[0].summary.unavailableFrames == 1);
        Check(Reject(() => ProfilingSummary.Finish(interrupted, 3)));
        var empty = new ProbeReport { requestedFrames = 2, frames = new ProfileFrame[2], markers = new[] {
            new MarkerReport { name = "Missing", available = new bool[2], nanoseconds = new long[2], blocks = new int[2] } } };
        ProfilingSummary.Finish(empty, 0);
        Check(empty.frames.Length == 0 && empty.wallIntervals == null && empty.markers[0].status == "unavailable");
        var stockProfile = ComparableProfile(10, 8, 6, 4);
        var candidateProfile = ComparableProfile(8, 6, 3, 3);
        var expected = new ProfileComparisonExpectation { substitutionId = "cluster-a", substitutionStatus = "verified",
            stockRigidbodies = 128, stockJoints = 127, candidateRigidbodies = 128, candidateJoints = 127 };
        var comparison = ProfileComparisonSummary.Compare(stockProfile, candidateProfile, expected);
        Near(comparison.wallIntervals.meanSpeedupPercent, 20);
        Near(comparison.fixedUpdate.meanSpeedupPercent, 25);
        Near(comparison.physicsFixedUpdate.meanSpeedupPercent, 50);
        Near(comparison.behaviourFixedUpdate.meanSpeedupPercent, 25);
        Check(comparison.stockFrames == 1 && comparison.candidateFrames == 1);
        candidateProfile.frames[0].joints = 8;
        Check(Reject(() => ProfileComparisonSummary.Compare(stockProfile, candidateProfile, expected)));
        candidateProfile.frames[0].rigidbodies = 1;
        candidateProfile.frames[0].joints = 0;
        var structuralExpected = new ProfileComparisonExpectation { substitutionId = "cluster-a", substitutionStatus = "verified",
            stockRigidbodies = 128, stockJoints = 127, candidateRigidbodies = 1, candidateJoints = 0 };
        var structural = ProfileComparisonSummary.Compare(stockProfile, candidateProfile, structuralExpected);
        Check(structural.status == "comparable" && structural.substitutionId == "cluster-a");
        candidateProfile.playerLoop.scopes[0].droppedSamples = 1;
        Check(Reject(() => ProfileComparisonSummary.Compare(stockProfile, candidateProfile, structuralExpected)));
        Check(Reject(() => ProfileComparisonSummary.Compare(stockProfile, candidateProfile, null)));
        var forceContext = new ForceObservationContext("11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", "FLIGHT", "frame-1", 10, -42, 1, 1, 1, 0, 100, 1, .02, new Vec());
        var forcePart = new ForcePartObservation(1, 0, -8, -9, new Vec(1, 2, 3), new Vec(), new Vec(4, 5, 6),
            new[] { new ForceAtPositionObservation(new Vec(7, 8, 9), new Vec(5, 5, 6), new Vec(1, 0, 0)) });
        var forceReport = new ForceObservationReport { batches = new[] { new ForceObservationBatch(forceContext, new[] { forcePart }) } };
        using (JsonDocument json = JsonDocument.Parse(ReportJson.Encode(forceReport)))
        {
            var batch = json.RootElement.GetProperty("batches")[0];
            Check(batch.GetProperty("parts")[0].GetProperty("force").GetProperty("Y").GetDouble() == 2);
            Check(batch.GetProperty("parts")[0].GetProperty("forces")[0].GetProperty("worldLeverArm").GetProperty("X").GetDouble() == 1);
            Check(json.RootElement.GetProperty("gravity").GetString() == "unavailable-not-observed");
        }
        Console.WriteLine("Profiling: " + assertions + " assertions passed.");
    }

    static ProbeReport ComparableProfile(double wall, double fixedMs, double physicsMs, double behaviourMs)
    {
        return new ProbeReport {
            status = "complete", requestedFrames = 1, completedFrames = 1, contextMisalignedFrames = 0,
            unity = "2019.4", ksp = "1.12.5", platform = "OSXPlayer", processor = "CPU", processorCount = 8,
            graphicsDevice = "GPU", targetFrameRate = -1, vSyncCount = 0,
            frames = new[] { new ProfileFrame { contextAligned = true, scene = "FLIGHT", body = "Kerbin", situation = "ORBITING",
                vesselId = "vessel", parts = 128, rigidbodies = 128, joints = 127, colliders = 128, loadedVessels = 1,
                screenWidth = 1920, screenHeight = 1080, fixedDeltaSeconds = .02, timeScale = 1, warpRate = 1,
                throttleCommand = 0, packed = false, loaded = true, paused = false, wallMilliseconds = wall } },
            wallIntervals = Dist(wall), markers = new MarkerReport[0],
            playerLoop = new LoopTimingReport { status = "observed", integrityStatus = "verified-at-boundaries", cleanupStatus = "removed-owned-hooks",
                scopes = new[] { Scope("UnityEngine.PlayerLoop.FixedUpdate", fixedMs),
                    Scope("UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate", physicsMs),
                    Scope("UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate", behaviourMs) } }
        };
    }
    static LoopTimingScope Scope(string name, double value) { return new LoopTimingScope { name = name, status = "observed", milliseconds = Dist(value), samples = new LoopTimingSample[1] }; }
    static ProfileDistribution Dist(double value) { return new ProfileDistribution { count = 1, minimum = value, maximum = value, mean = value, p50 = value, p95 = value, p99 = value }; }
    static PerformanceObservation Observation(string strategy, double[] totals, long[] allocations)
    {
        var workload = new PerformanceWorkloadIdentity { system = "test-system", workload = "free-body",
            fixtureSha256 = new string('a', 64), configurationSha256 = new string('b', 64),
            items = 64, steps = 1, stepSeconds = 0.02 };
        PerformancePhaseSamples Phase(double value)
        {
            var milliseconds = new double[totals.Length]; Array.Fill(milliseconds, value);
            return new PerformancePhaseSamples { milliseconds = milliseconds, allocations = new PerformanceAllocationSamples {
                available = true, kind = "managed-allocated-bytes", scope = "current-thread", bytes = new long[totals.Length] } };
        }
        return new PerformanceObservation { workload = workload, strategy = strategy, environmentSha256 = new string('c', 64),
            measurementProtocol = "test-clock-v1", sampleProtocol = "test-samples-v1",
            capture = Phase(0.1), pack = Phase(0.2), compute = Phase(0.5), synchronize = Phase(0), publish = Phase(0.2),
            total = new PerformancePhaseSamples { milliseconds = totals, allocations = new PerformanceAllocationSamples {
                available = true, kind = "managed-allocated-bytes", scope = "current-thread", bytes = allocations } } };
    }
}
