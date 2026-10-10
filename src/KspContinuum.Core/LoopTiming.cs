using System;
namespace KspContinuum
{
    [Serializable] public sealed class LoopTimingReport
    {
        public string schema = "ksp-continuum-playerloop/v2", status, integrityStatus, cleanupStatus, detail;
        public string measurementScope = "Elapsed wall time around unchanged PlayerLoop subtrees. FixedUpdate parent and child scopes overlap and must not be summed.";
        public long clockFrequency, timerReadFloorTicks;
        public LoopTimingScope[] scopes;
    }
    [Serializable] public sealed class LoopTimingScope
    {
        public string name, status, timeDomain, overlap;
        public int droppedSamples, sequenceErrors;
        public LoopTimingSample[] samples;
        public ProfileDistribution milliseconds;
    }
    [Serializable] public struct LoopTimingSample
    {
        public int frame;
        public double fixedTimeSeconds, fixedDeltaSeconds;
        public long elapsedTicks, startedTicks, endedTicks;
    }
    public sealed class LoopTimingBuffer
    {
        readonly string name;
        readonly long frequency;
        readonly LoopTimingSample[] samples;
        int count, dropped, errors;
        bool pending, finished;
        long start;
        LoopTimingSample current;
        public LoopTimingBuffer(string name, int capacity, long frequency)
        {
            if (string.IsNullOrEmpty(name) || capacity < 1 || capacity > 4096 || frequency <= 0)
                throw new ArgumentException("Invalid timing buffer configuration.");
            this.name = name; this.frequency = frequency; samples = new LoopTimingSample[capacity];
        }
        public void Begin(long ticks, int frame, double time, double delta)
        {
            if (finished) return;
            if (pending) errors++;
            pending = true; start = ticks;
            current = new LoopTimingSample { frame = frame, fixedTimeSeconds = time, fixedDeltaSeconds = delta, startedTicks = ticks };
            if (double.IsNaN(time) || double.IsInfinity(time) || double.IsNaN(delta) || double.IsInfinity(delta) || delta <= 0)
            { errors++; pending = false; }
        }
        public void End(long ticks, int frame)
        {
            if (finished) return;
            if (!pending) { errors++; return; }
            pending = false;
            if (ticks < start || frame != current.frame) { errors++; return; }
            current.elapsedTicks = ticks - start;
            current.endedTicks = ticks;
            if (count < samples.Length) samples[count++] = current; else dropped++;
        }
        public void Fault() { if (!finished) errors++; }
        public LoopTimingScope Finish(bool valid)
        {
            if (pending) { errors++; pending = false; }
            finished = true;
            var retained = new LoopTimingSample[count]; Array.Copy(samples, retained, count);
            var durations = new double[count];
            for (int i = 0; i < count; i++) durations[i] = retained[i].elapsedTicks * (1000.0 / frequency);
            return new LoopTimingScope { name = name, status = !valid || errors > 0 ? "invalid" : count == 0 ? "no-samples" : "observed",
                droppedSamples = dropped, sequenceErrors = errors, samples = retained,
                milliseconds = valid && errors == 0 ? ProfilingSummary.Distribution(durations) : null };
        }
    }
}
