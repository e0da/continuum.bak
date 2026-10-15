using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KspContinuum
{
    public sealed class MacThreadWorkTiming : IDisposable
    {
        const int Capacity = 4096, BasicFlavor = 3;
        const uint BasicCount = 10;
        static readonly string[] Names = {
            "UnityEngine.PlayerLoop.Update",
            "UnityEngine.PlayerLoop.Update+ScriptRunDelayedDynamicFrameRate",
            "UnityEngine.PlayerLoop.FixedUpdate",
            "UnityEngine.PlayerLoop.PostLateUpdate"
        };
        [StructLayout(LayoutKind.Sequential)] struct BasicInfo
        {
            public int userSeconds, userMicroseconds, systemSeconds, systemMicroseconds;
            public int cpuUsage, policy, runState, flags, suspendCount, sleepTime;
        }
        [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr pthread_self();
        [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
        static extern uint pthread_mach_thread_np(IntPtr thread);
        [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
        static extern int thread_info(uint thread, int flavor, out BasicInfo information, ref uint count);

        [Serializable] public struct Row
        {
            public string scope;
            public int frame;
            public long sequence, beginTicks, endTicks;
            public long userBeginMicroseconds, userEndMicroseconds, systemBeginMicroseconds, systemEndMicroseconds;
            public long cpuMicroseconds, readTaxTicks;
        }
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-mac-thread-work/v1", status, detail;
            public bool requested;
            public int managedThreadId, nativeInfoCount = 10, capacity = Capacity, retainedRows, incompleteScopes;
            public uint nativeThreadPort;
            public long clockFrequency, readCalls, readTaxTicks, completedRows, droppedRows;
            public string scope = "Main owner-thread cumulative user+system CPU time versus elapsed wall brackets. Four named PlayerLoop scopes overlap and are not additive. CPU reads and nested observer work remain included; readTaxTicks measures native-reader wall cost, not an exact CPU-tax subtraction. Recent bounded rows exclude overwritten history. Wall-minus-CPU is not independently GPU time or a coroutine identity.";
            public string nativeAbi = "macOS SDK thread_info THREAD_BASIC_INFO=3, ten int32 fields; pthread_mach_thread_np(pthread_self()) borrowed thread port, never deallocated.";
            public Row[] rows;
        }
        readonly Row[] ring = new Row[Capacity], pending = new Row[Names.Length];
        readonly bool[] entered = new bool[Names.Length];
        readonly int[] stack = new int[Names.Length];
        readonly int ownerThread;
        readonly ReportData report;
        uint port;
        long lastUser, lastSystem;
        int next, count, depth;
        bool disposed;
        public static bool Requested { get { return Environment.GetEnvironmentVariable("CONTINUUM_LOOP_THREAD_CPU") == "1"; } }

        public MacThreadWorkTiming()
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            report = new ReportData { requested = Requested, status = Requested ? "unavailable" : "not-requested",
                managedThreadId = ownerThread, clockFrequency = Stopwatch.Frequency };
            if (!report.requested) return;
            try
            {
                if (Marshal.SizeOf(typeof(BasicInfo)) != 40) { Fail("THREAD_BASIC_INFO managed ABI size differs.", false); return; }
                port = pthread_mach_thread_np(pthread_self()); report.nativeThreadPort = port;
                if (port == 0) { Fail("Borrowed pthread Mach thread port is unavailable.", false); return; }
                long user, system, tax;
                if (!Read(out user, out system, out tax)) return;
                report.status = "installed";
            }
            catch (Exception error) { Fail(error.GetType().Name + ": " + error.Message, false); }
        }
        static int Slot(string scope)
        { for (int i = 0; i < Names.Length; i++) if (scope == Names[i]) return i; return -1; }
        bool Owner()
        {
            if (Thread.CurrentThread.ManagedThreadId == ownerThread) return true;
            Fail("CPU attribution callback changed managed owner thread.", true); return false;
        }
        void Fail(string detail, bool invalid)
        { report.status = invalid ? "invalid" : "unavailable"; if (report.detail == null) report.detail = detail; }
        bool Read(out long user, out long system, out long tax)
        {
            user = system = tax = 0;
            long begin = Stopwatch.GetTimestamp();
            try
            {
                BasicInfo data; uint size = BasicCount;
                int result = thread_info(port, BasicFlavor, out data, ref size);
                if (result != 0 || size != BasicCount)
                { Fail("thread_info return/count differs: " + result + "/" + size, report.status == "installed"); return false; }
                if (data.userSeconds < 0 || data.systemSeconds < 0 || data.userMicroseconds < 0 || data.userMicroseconds >= 1000000 ||
                    data.systemMicroseconds < 0 || data.systemMicroseconds >= 1000000)
                { Fail("Native CPU time-value fields are outside their ABI domain.", report.status == "installed"); return false; }
                user = checked((long)data.userSeconds * 1000000 + data.userMicroseconds);
                system = checked((long)data.systemSeconds * 1000000 + data.systemMicroseconds);
                if (user < lastUser || system < lastSystem)
                { Fail("Cumulative owner-thread CPU time reversed.", report.status == "installed"); return false; }
                lastUser = user; lastSystem = system; return true;
            }
            catch (Exception error) { Fail(error.GetType().Name + ": " + error.Message, report.status == "installed"); return false; }
            finally { tax = Stopwatch.GetTimestamp() - begin; report.readCalls++; report.readTaxTicks += tax; }
        }
        public void Before(string scope, int frame)
        {
            int slot = Slot(scope);
            if (slot < 0 || disposed || report.status != "installed") return;
            if (!Owner()) return;
            if (frame < 0 || entered[slot] || depth == stack.Length)
            { Fail("CPU bracket has invalid frame, recursion, or nesting.", true); return; }
            long begin = Stopwatch.GetTimestamp(), user, system, tax;
            if (!Read(out user, out system, out tax)) return;
            pending[slot] = new Row { scope = Names[slot], frame = frame, beginTicks = begin,
                userBeginMicroseconds = user, systemBeginMicroseconds = system, readTaxTicks = tax };
            entered[slot] = true; stack[depth++] = slot;
        }
        public void After(string scope, int frame)
        {
            int slot = Slot(scope);
            if (slot < 0 || disposed || report.status != "installed") return;
            if (!Owner()) return;
            if (!entered[slot] || depth == 0 || stack[depth - 1] != slot || frame != pending[slot].frame)
            { Fail("CPU bracket ended outside its owner/frame/nesting.", true); return; }
            long user, system, tax;
            if (!Read(out user, out system, out tax)) return;
            Row row = pending[slot]; row.endTicks = Stopwatch.GetTimestamp();
            row.userEndMicroseconds = user; row.systemEndMicroseconds = system;
            row.cpuMicroseconds = checked(user - row.userBeginMicroseconds + system - row.systemBeginMicroseconds);
            row.readTaxTicks += tax;
            if (row.endTicks < row.beginTicks) { Fail("CPU bracket wall clock reversed.", true); return; }
            entered[slot] = false; depth--;
            row.sequence = report.completedRows++; ring[next] = row; next = (next + 1) % Capacity;
            if (count < Capacity) count++; else report.droppedRows++;
        }
        public ReportData Snapshot()
        {
            if (!Owner()) return Copy();
            return Copy();
        }
        ReportData Copy()
        {
            var rows = new Row[count];
            for (int i = 0; i < count; i++) rows[i] = ring[(next - count + Capacity + i) % Capacity];
            return new ReportData { status = report.status, detail = report.detail, requested = report.requested,
                managedThreadId = ownerThread, nativeThreadPort = port, clockFrequency = report.clockFrequency,
                readCalls = report.readCalls, readTaxTicks = report.readTaxTicks, completedRows = report.completedRows,
                droppedRows = report.droppedRows, retainedRows = count, incompleteScopes = depth, rows = rows };
        }
        public void Dispose()
        {
            if (disposed) return;
            if (!Owner()) return;
            if (report.status == "installed")
            {
                report.status = "completed";
                if (depth != 0) report.detail = "Unfinished brackets omitted at removal; no ending CPU or wall sample fabricated.";
            }
            disposed = true;
        }
    }
}
