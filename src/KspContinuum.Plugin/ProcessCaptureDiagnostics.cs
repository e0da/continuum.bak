using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KspContinuum
{
    internal sealed class ProcessCaptureDiagnostics : IDisposable
    {
        [Serializable] public sealed class Boundary
        {
            public long clockTimestamp, machAbsoluteBeforeTicks, machAbsoluteAfterTicks;
            public uint machTimebaseNumerator, machTimebaseDenominator;
            public string machClockStatus = "not-requested", detail;
        }
        [Serializable] public sealed class Snapshot
        {
            public string status, detail;
            public long cpuReadBeforeTicks, cpuReadAfterTicks, rssReadBeforeTicks, rssReadAfterTicks;
            public long processCpuTicks, workingSetBytes;
        }
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-process-capture/v1", status = "prepared", runId, detail;
            public string scope = "Owned process CPU cumulative delta across bracketed capture-boundary reads, all threads including observers. CPU/wall is average occupied cores, not main-thread utilization or GPU time. Working set is two endpoint samples, not capture peak. Boundaries include observer read tax and exclude startup/cleanup.";
            public int processId;
            public long clockFrequency = Stopwatch.Frequency, preparationTicks;
            public Boundary startBoundary, endBoundary;
            public Snapshot start, end;
            public double processCpuSeconds, cpuReadMidpointWallSeconds, averageOccupiedCores, snapshotReadMilliseconds;
        }
        [StructLayout(LayoutKind.Sequential)] struct Timebase { public uint numerator, denominator; }
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern ulong mach_absolute_time();
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern int mach_timebase_info(out Timebase info);
        readonly Process process;
        bool disposed;
        public readonly ReportData Report;

        public ProcessCaptureDiagnostics(string runId)
        {
            long before = Stopwatch.GetTimestamp();
            process = Process.GetCurrentProcess();
            Report = new ReportData { runId = runId, processId = process.Id };
            MarkBoundary(); Read();
            Report.preparationTicks = Stopwatch.GetTimestamp() - before;
        }
        public Boundary MarkBoundary()
        {
            var boundary = new Boundary();
            try
            {
                Timebase timebase;
                if (mach_timebase_info(out timebase) != 0 || timebase.numerator == 0 || timebase.denominator == 0)
                    throw new InvalidOperationException("mach timebase unavailable");
                boundary.machTimebaseNumerator = timebase.numerator;
                boundary.machTimebaseDenominator = timebase.denominator;
                boundary.machAbsoluteBeforeTicks = checked((long)mach_absolute_time());
                boundary.clockTimestamp = Stopwatch.GetTimestamp();
                boundary.machAbsoluteAfterTicks = checked((long)mach_absolute_time());
                boundary.machClockStatus = boundary.machAbsoluteAfterTicks >= boundary.machAbsoluteBeforeTicks ? "available" : "invalid";
            }
            catch (Exception error)
            {
                if (boundary.clockTimestamp == 0) boundary.clockTimestamp = Stopwatch.GetTimestamp();
                boundary.machClockStatus = "unavailable";
                boundary.detail = error.GetType().Name + ": " + error.Message;
            }
            return boundary;
        }
        Snapshot Read()
        {
            var snapshot = new Snapshot();
            try
            {
                process.Refresh();
                snapshot.cpuReadBeforeTicks = Stopwatch.GetTimestamp();
                snapshot.processCpuTicks = process.TotalProcessorTime.Ticks;
                snapshot.cpuReadAfterTicks = Stopwatch.GetTimestamp();
                snapshot.rssReadBeforeTicks = Stopwatch.GetTimestamp();
                snapshot.workingSetBytes = process.WorkingSet64;
                snapshot.rssReadAfterTicks = Stopwatch.GetTimestamp();
                snapshot.status = snapshot.processCpuTicks >= 0 && snapshot.workingSetBytes > 0 ? "available" : "unavailable";
            }
            catch (Exception error) { snapshot.status = "unavailable"; snapshot.detail = error.GetType().Name + ": " + error.Message; }
            return snapshot;
        }
        public void Begin(Boundary boundary) { Report.startBoundary = boundary; Report.start = Read(); }
        public void End(Boundary boundary, bool completed)
        {
            if (Report.end != null) return;
            Report.endBoundary = boundary; Report.end = Read();
            Snapshot start = Report.start, end = Report.end;
            if (start == null || start.status != "available" || end.status != "available" ||
                end.cpuReadBeforeTicks <= start.cpuReadAfterTicks || end.processCpuTicks < start.processCpuTicks)
            { Report.status = "unavailable"; Report.detail = "Incomplete, unsupported or nonmonotonic resource reads."; return; }
            Report.processCpuSeconds = (end.processCpuTicks - start.processCpuTicks) / (double)TimeSpan.TicksPerSecond;
            Report.cpuReadMidpointWallSeconds = ((end.cpuReadBeforeTicks - start.cpuReadBeforeTicks) +
                (end.cpuReadAfterTicks - start.cpuReadAfterTicks)) * .5 / Stopwatch.Frequency;
            Report.averageOccupiedCores = Report.processCpuSeconds / Report.cpuReadMidpointWallSeconds;
            Report.snapshotReadMilliseconds = ((start.rssReadAfterTicks - start.cpuReadBeforeTicks) +
                (end.rssReadAfterTicks - end.cpuReadBeforeTicks)) * 1000.0 / Stopwatch.Frequency;
            Report.status = completed ? "complete" : "interrupted";
        }
        public void Dispose() { if (disposed) return; disposed = true; process.Dispose(); }
    }
}
