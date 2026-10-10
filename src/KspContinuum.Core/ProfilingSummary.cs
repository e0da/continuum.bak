using System;
using System.Collections.Generic;

namespace KspContinuum
{
    [Serializable] public sealed class ProfileDistribution
    {
        public int count;
        public double minimum, maximum, mean, p50, p95, p99;
    }
    [Serializable] public sealed class ProfileMarkerSummary
    {
        public int availableFrames, unavailableFrames, observedFrames, zeroBlockFrames;
        public long totalBlocks;
        public ProfileDistribution observedMilliseconds;
    }
    [Serializable] public sealed class ProfileFrame
    {
        public int contextFrame, markerFrame, observedFrame, parts;
        public int rigidbodies, joints, colliders, loadedVessels;
        public int renderedFrame, screenWidth, screenHeight;
        public int physicslessParts = -1, moduleCount = -1, fixedCallbacksPerFrame;
        public int awakeRigidbodies = -1;
        public double? motionSampleSimulatedSeconds, angularRmsRadiansPerSecond, angularMaxRadiansPerSecond;
        public double? attachmentPositionDeltaMaxMeters, attachmentRotationDeltaMaxDegrees;
        public string warpMode;
        public double maximumDeltaSeconds, physicsFrameLimitSeconds;
        public double? altitudeMeters, atmosphereDepthMeters, atmosphericDensity;
        public long managedBytes;
        public int gcGeneration0, gcGeneration1, gcGeneration2;
        public double boundaryWallSeconds, fixedDeltaSeconds, timeScale;
        public double? universalTime, warpRate, throttleCommand;
        public double? bodyRadiusMeters, bodyMuMetersCubedPerSecondSquared;
        public double? orbitalRadiusMeters, orbitalSpeedMetersPerSecond, radialSpeedMetersPerSecond;
        public bool? packed, loaded, paused;
        public string scene, body, situation;
        public bool contextAligned;
        public double wallMilliseconds;
        public string vesselStatus, vesselId;
    }
    public static class ProfilingSummary
    {
        public static ProfileDistribution Distribution(IEnumerable<double> values)
        {
            if (values == null) throw new ArgumentNullException("values");
            var sorted = new List<double>();
            double mean = 0;
            foreach (double value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new ArgumentException("Invalid profiling observation.");
                sorted.Add(value);
                mean += (value - mean) / sorted.Count;
            }
            if (sorted.Count == 0) return null;
            sorted.Sort();
            return new ProfileDistribution { count = sorted.Count, minimum = sorted[0], maximum = sorted[sorted.Count - 1],
                mean = mean, p50 = Percentile(sorted, 0.5), p95 = Percentile(sorted, 0.95), p99 = Percentile(sorted, 0.99) };
        }

        public static void Finish(ProbeReport report, int count)
        {
            if (report == null || report.frames == null || report.markers == null || count < 0 || count > report.frames.Length || count > report.requestedFrames)
                throw new ArgumentException("Invalid profile completion boundary.");
            var intervals = new List<double>();
            int misaligned = 0;
            for (int i = 0; i < count; i++)
            {
                if (report.frames[i] == null) throw new ArgumentException("Missing completed frame.");
                intervals.Add(report.frames[i].wallMilliseconds);
                if (!report.frames[i].contextAligned) misaligned++;
            }
            report.wallIntervals = Distribution(intervals);
            foreach (MarkerReport marker in report.markers)
            {
                if (marker.available == null || marker.nanoseconds == null || marker.blocks == null ||
                    marker.available.Length < count || marker.nanoseconds.Length < count || marker.blocks.Length < count)
                    throw new ArgumentException("Incomplete marker capture arrays.");
                Array.Resize(ref marker.available, count);
                Array.Resize(ref marker.nanoseconds, count);
                Array.Resize(ref marker.blocks, count);
                marker.summary = Marker(marker);
                marker.status = marker.summary.observedFrames > 0 ? "observed" :
                    marker.summary.availableFrames > 0 || marker.recorderAvailableAtStart ? "available-no-samples" : "unavailable";
            }
            Array.Resize(ref report.frames, count);
            report.completedFrames = count;
            report.contextMisalignedFrames = misaligned;
        }

        static double Percentile(List<double> sorted, double fraction)
        {
            double index = (sorted.Count - 1) * fraction;
            int lower = (int)index;
            int upper = Math.Min(lower + 1, sorted.Count - 1);
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
        }
        public static ProfileMarkerSummary Marker(MarkerReport marker)
        {
            if (marker == null || marker.available == null || marker.nanoseconds == null || marker.blocks == null ||
                marker.available.Length != marker.nanoseconds.Length || marker.available.Length != marker.blocks.Length)
                throw new ArgumentException("Marker arrays must have matching lengths.");
            var result = new ProfileMarkerSummary();
            var observed = new List<double>();
            for (int i = 0; i < marker.available.Length; i++)
            {
                if (!marker.available[i]) { result.unavailableFrames++; continue; }
                long time = marker.nanoseconds[i]; int blocks = marker.blocks[i];
                if (time < 0 || blocks < 0 || (blocks == 0 && time != 0)) throw new ArgumentException("Invalid available marker reading.");
                result.availableFrames++;
                if (blocks == 0) { result.zeroBlockFrames++; continue; }
                result.observedFrames++;
                result.totalBlocks += blocks;
                observed.Add(time / 1000000.0);
            }
            result.observedMilliseconds = Distribution(observed);
            return result;
        }
    }
}
