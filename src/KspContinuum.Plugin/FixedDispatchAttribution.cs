using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class FixedDispatchAttribution : IDisposable, IPlayerLoopBracketObserver
    {
        internal const string Owner = "continuum.fixed-dispatch-attribution";
        const string Sha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        static readonly Guid Mvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static readonly string Phase = typeof(UnityEngine.PlayerLoop.FixedUpdate.ScriptRunBehaviourFixedUpdate).FullName;
        const int MaxWindows = 1024, MaxDepth = 16, MaxShifts = 2048, MaxSubscribers = 128;
        static FixedDispatchAttribution active;
        static MethodInfo eventInvoke;
        static readonly MethodInfo End = Method(nameof(Finalizer)), EventWrapper = Method(nameof(InvokeSubscriber));
        readonly Harmony harmony = new Harmony(Owner);
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly Func<bool> capturing;
        readonly Vessel selected;
        readonly FloatingOrigin origin;
        readonly EventData<Vector3d, Vector3d> originEvent;
        readonly MethodInfo[] targets = new MethodInfo[4];
        readonly MethodInfo[] prefixes = { Method(nameof(PartPrefix)), Method(nameof(VesselPrefix)), Method(nameof(ShiftPrefix)), Method(nameof(EventPrefix)) };
        readonly Dictionary<Part, int> parts = new Dictionary<Part, int>();
        readonly Dictionary<Part, Vessel> partVessels = new Dictionary<Part, Vessel>();
        readonly Dictionary<Vessel, int> vessels = new Dictionary<Vessel, int>();
        readonly Dictionary<Delegate, int> subscribers = new Dictionary<Delegate, int>();
        readonly List<Part> partRefs = new List<Part>();
        readonly List<Vessel> vesselRefs = new List<Vessel>();
        readonly List<Delegate> subscriberRefs = new List<Delegate>();
        readonly StackEntry[] stack = new StackEntry[MaxDepth];
        readonly Window[] windows = new Window[MaxWindows];
        readonly ShiftSample[] shifts = new ShiftSample[MaxShifts];
        int window = -1, windowCount, depth, shiftCount, eventDepth, foreignEventDepth;
        bool installed, disposed, faulted;
        public ReportData Report { get; private set; }
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-fixed-dispatch-attribution/v1", runId, vesselId, status = "installing", cleanupStatus = "not-installed", detail;
            public string phase = Phase, scope = "Original Part/Vessel FixedUpdate envelopes and origin setter/event/subscriber body envelopes, only inside admitted fixed script windows. Self excludes only this observer's nested brackets; it still includes existing ten-owner callbacks. Never add these envelopes to existing inclusive/self totals. Sentinel transform/body changes witness selected movement only, not all world objects. Stock event reverse clone iteration, null removal and caught subscriber exceptions remain original. Duplicate/equal registrations and multicast delegates are explicitly unsupported attribution; no registry rewrites occur. Subscriber errors are observed before stock catches them.";
            public string overheadStatus = "unqualified-intrusive-observer", coverageStatus = "pending", observerTicksScope = "Before/After boundary preparation only; per-call prefix/finalizer/transpiler tax is not independently calibrated.";
            public int processId, mainThreadId, errors, originalExceptions, ignoredForeignCalls, completedWindows, startedWindows, droppedWindows, droppedShifts;
            public bool hooksRemoved;
            public long clockFrequency = Stopwatch.Frequency, preparationTicks, observerTicks, cleanupTicks;
            public Target[] targets; public Instance[] instances; public Subscriber[] subscribers;
            public Window[] windows; public Shift[] shifts;
            public string[] preinstallPatchGraph, installedPatchGraph, precleanupPatchGraph, restoredPatchGraph;
        }
        [Serializable] public sealed class Target { public int slot, metadataToken; public string declaringType, method, assemblySha256, assemblyMvid, ilSha256; }
        [Serializable] public sealed class Instance { public int slot, instanceId; public uint partFlightId; public string kind, vesselId; public bool loaded, packed; public long calls; }
        [Serializable] public sealed class Subscriber { public int slot, targetInstanceId, managedTargetIdentity; public string type, method, assemblySha256, assemblyMvid, ilSha256; public int metadataToken; public long calls, ticks, exceptions; }
        [Serializable] public sealed class Metric { public int slot; public long calls, inclusiveTicks, selfTicks, activeVesselCalls, otherVesselCalls; }
        [Serializable] public sealed class Window { public int sequence, frame; public double fixedTimeSeconds; public long startedTicks, endedTicks; public bool captureActiveAtEnd; public Metric[] metrics; public SubscriberMetric[] subscribers; }
        [Serializable] public sealed class SubscriberMetric { public int slot; public long calls, ticks, exceptions; }
        [Serializable] public sealed class Shift
        {
            public int sequence, window, frame, originInstanceId, bodyInstanceId, vesselInstanceId;
            public string vesselId; public long startedTicks, endedTicks, eventCalls, eventTicks;
            public bool completed, finite, selectedTransformChanged, selectedBodyChanged;
            public double[] requestedOffset, nonFrameOffset, vesselBefore, vesselAfter, bodyBefore, bodyAfter;
        }
        struct StackEntry { public int slot, instance, shift; public long start, child; }
        struct ShiftSample { public Shift row; public Vector3d request, nonFrame, vesselBefore, bodyBefore; public CelestialBody body; }
        public struct CallState { internal FixedDispatchAttribution owner; internal int depth; internal bool entered, foreignEvent; internal int eventForeignBefore; }
        static MethodInfo Method(string name) { return AccessTools.DeclaredMethod(typeof(FixedDispatchAttribution), name); }
        static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
        static bool Same(MethodBase a, MethodBase b) { return FlightIntegratorUpdateAttribution.SameMethod(a, b); }
        static bool Finite(Vector3d v) { return !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z) || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z)); }
        static void Values(double[] destination, Vector3d v) { destination[0] = v.x; destination[1] = v.y; destination[2] = v.z; }
        static bool Changed(Vector3d a, Vector3d b) { return a.x != b.x || a.y != b.y || a.z != b.z; }
        FixedDispatchAttribution(string runId, Func<bool> capturing)
        {
            long start = Stopwatch.GetTimestamp(); this.capturing = capturing; selected = FlightGlobals.ActiveVessel; origin = FloatingOrigin.fetch; originEvent = GameEvents.onFloatingOriginShift;
            Report = new ReportData { runId = runId, vesselId = selected.id.ToString("D"), processId = Process.GetCurrentProcess().Id, mainThreadId = thread };
            try
            {
                Require(active == null && origin != null && origin.GetType() == typeof(FloatingOrigin), "Origin singleton absent or overridden.");
                StartupPinnedGameIdentity.Require(typeof(Vessel).Assembly);
                Require(Hash(File.ReadAllBytes(typeof(Vessel).Assembly.Location)) == Sha, "Original game assembly differs.");
                targets[0] = AccessTools.DeclaredMethod(typeof(Part), "FixedUpdate"); targets[1] = AccessTools.DeclaredMethod(typeof(Vessel), "FixedUpdate");
                targets[2] = AccessTools.DeclaredMethod(typeof(FloatingOrigin), "setOffset", new[] { typeof(Vector3d), typeof(Vector3d) });
                targets[3] = AccessTools.DeclaredMethod(typeof(EventData<Vector3d, Vector3d>), "Fire", new[] { typeof(Vector3d), typeof(Vector3d) });
                eventInvoke = AccessTools.DeclaredMethod(typeof(EventData<Vector3d, Vector3d>.OnEvent), "Invoke");
                Require(eventInvoke != null && originEvent != null, "Origin event signature absent.");
                Report.targets = targets.Select((m, i) =>
                {
                    Require(m != null && !m.IsStatic && m.ReturnType == typeof(void) && m.Module.ModuleVersionId == Mvid && m.GetMethodBody() != null &&
                        (i >= 2 || m.GetParameters().Length == 0), "Original dispatch signature/module differs.");
                    Empty(m); return new Target { slot = i, metadataToken = m.MetadataToken, declaringType = m.DeclaringType.FullName, method = m.Name,
                        assemblySha256 = Sha, assemblyMvid = Mvid.ToString("D"), ilSha256 = Hash(m.GetMethodBody().GetILAsByteArray()) };
                }).ToArray();
                var identities = new List<Instance>();
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    Require(v != null && !vessels.ContainsKey(v), "Vessel census absent/duplicate."); int slot = identities.Count;
                    vessels.Add(v, slot); vesselRefs.Add(v); identities.Add(new Instance { slot = slot, instanceId = v.GetInstanceID(), kind = "vessel", vesselId = v.id.ToString("D"), loaded = v.loaded, packed = v.packed });
                    foreach (Part p in v.parts)
                    {
                        Require(p != null && ReferenceEquals(p.vessel, v) && !parts.ContainsKey(p) && p.flightID != 0 && identities.Count < 8192, "Part census unsupported."); slot = identities.Count;
                        parts.Add(p, slot); partVessels.Add(p, v); partRefs.Add(p); identities.Add(new Instance { slot = slot, instanceId = p.GetInstanceID(), partFlightId = p.flightID, kind = "part", vesselId = v.id.ToString("D"), loaded = v.loaded, packed = v.packed });
                    }
                }
                Report.instances = identities.ToArray(); BindSubscribers();
                for (int i = 0; i < MaxWindows; i++) { windows[i] = new Window { metrics = Enumerable.Range(0, 4).Select(s => new Metric { slot = s }).ToArray(), subscribers = Enumerable.Range(0, Report.subscribers.Length).Select(s => new SubscriberMetric { slot = s }).ToArray() }; }
                for (int i = 0; i < MaxShifts; i++) shifts[i].row = new Shift { requestedOffset = new double[3], nonFrameOffset = new double[3], vesselBefore = new double[3], vesselAfter = new double[3], bodyBefore = new double[3], bodyAfter = new double[3] };
                Report.preinstallPatchGraph = Graph(); active = this;
                for (int i = 0; i < 4; i++) harmony.Patch(targets[i], prefix: new HarmonyMethod(prefixes[i]), finalizer: new HarmonyMethod(End),
                    transpiler: i == 3 ? new HarmonyMethod(Method(nameof(EventTranspile))) : null);
                Owned(); Report.installedPatchGraph = Graph(); installed = true; Report.status = "installed";
            }
            catch (Exception e) { Fail("installation", e); Report.status = "unavailable"; Remove(); }
            finally { Report.preparationTicks = Stopwatch.GetTimestamp() - start; }
        }
        public static FixedDispatchAttribution Create(string[] args, string runId, Func<bool> capturing)
        {
            string request = Environment.GetEnvironmentVariable("CONTINUUM_FIXED_DISPATCH_ATTRIBUTION"); if (string.IsNullOrEmpty(request)) return null;
            Require(request == "1" && !string.IsNullOrEmpty(runId) && capturing != null && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed,
                "Fixed dispatch observer requires bound physical flight."); return new FixedDispatchAttribution(runId, capturing);
        }
        void BindSubscribers()
        {
            var list = AccessTools.Field(originEvent.GetType(), "events").GetValue(originEvent) as IList;
            Require(list != null && list.Count <= MaxSubscribers, "Origin event census unavailable/bounded."); var rows = new List<Subscriber>();
            foreach (object item in list)
            {
                var callback = AccessTools.Field(item.GetType(), "evt").GetValue(item) as Delegate;
                Require(callback != null && callback.GetInvocationList().Length == 1 && !subscribers.ContainsKey(callback), "Origin callback census unsupported/multicast/duplicate.");
                MethodInfo m = callback.Method; int slot = rows.Count; subscribers.Add(callback, slot); subscriberRefs.Add(callback);
                var unity = callback.Target as UnityEngine.Object;
                rows.Add(new Subscriber { slot = slot, managedTargetIdentity = callback.Target == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(callback.Target), targetInstanceId = unity == null ? 0 : unity.GetInstanceID(), type = callback.Target == null ? m.DeclaringType.FullName : callback.Target.GetType().FullName,
                    method = m.Name, metadataToken = m.MetadataToken, assemblySha256 = Hash(File.ReadAllBytes(m.Module.Assembly.Location)), assemblyMvid = m.Module.ModuleVersionId.ToString("D"), ilSha256 = m.GetMethodBody() == null ? null : Hash(m.GetMethodBody().GetILAsByteArray()) });
            }
            Report.subscribers = rows.ToArray();
        }
        static void Empty(MethodInfo m) { Patches p = Harmony.GetPatchInfo(m); Require(p == null || p.Owners.Count == 0, "Dispatch seam already patched: " + m); }
        string[] Graph() { return targets.Where(m => m != null).SelectMany(m => FlightIntegratorUpdateAttribution.DescribeGraph(m, Harmony.GetPatchInfo(m))).ToArray(); }
        void Owned()
        {
            for (int i = 0; i < 4; i++)
            {
                Patches p = Harmony.GetPatchInfo(targets[i]); Require(p != null && p.Prefixes.Count == 1 && p.Finalizers.Count == 1 && p.Postfixes.Count == 0 && p.Transpilers.Count == (i == 3 ? 1 : 0), "Dispatch patch shape changed.");
                Require(Exact(p.Prefixes[0], prefixes[i]) && Exact(p.Finalizers[0], End) && (i != 3 || Exact(p.Transpilers[0], Method(nameof(EventTranspile)))), "Dispatch patch owner changed.");
            }
        }
        static bool Exact(Patch p, MethodInfo m) { return p.owner == Owner && Same(p.PatchMethod, m) && p.priority == Priority.Normal && p.before.Length == 0 && p.after.Length == 0; }
        static IEnumerable<CodeInstruction> EventTranspile(IEnumerable<CodeInstruction> instructions)
        {
            var rows = instructions.ToList(); int count = 0;
            foreach (CodeInstruction row in rows) if ((row.opcode == OpCodes.Callvirt || row.opcode == OpCodes.Call) && row.operand is MethodInfo && Same((MethodInfo)row.operand, eventInvoke))
            { row.opcode = OpCodes.Call; row.operand = EventWrapper; count++; }
            Require(count == 1, "Original event Invoke call shape differs."); return rows;
        }
        bool Sampling { get { return installed && !faulted && window >= 0; } }
        void Enter(int slot, int instance, ref CallState state)
        {
            if (!Sampling) return;
            try { Require(Thread.CurrentThread.ManagedThreadId == thread && depth < MaxDepth, "Dispatch stack/thread invalid."); stack[depth] = new StackEntry { slot = slot, instance = instance, shift = -1, start = Stopwatch.GetTimestamp() }; state.owner = this; state.depth = depth++; state.entered = true; }
            catch (Exception e) { Fail("entry", e); }
        }
        static void PartPrefix(Part __instance, ref CallState __state) { var o = active; if (o == null || !o.Sampling) return; int i;
            if (!o.parts.TryGetValue(__instance, out i)) { o.Report.ignoredForeignCalls++; o.Fail("part", new InvalidOperationException("Unadmitted Part callback.")); return; }
            if (__instance.vessel == null || !ReferenceEquals(__instance.vessel, o.partVessels[__instance]) || __instance.flightID != o.Report.instances[i].partFlightId) { o.Fail("part", new InvalidOperationException("Part owner changed.")); return; } o.Enter(0, i, ref __state); }
        static void VesselPrefix(Vessel __instance, ref CallState __state) { var o = active; if (o == null || !o.Sampling) return; int i;
            if (!o.vessels.TryGetValue(__instance, out i)) { o.Report.ignoredForeignCalls++; o.Fail("vessel", new InvalidOperationException("Unadmitted Vessel callback.")); return; } o.Enter(1, i, ref __state); }
        static void ShiftPrefix(FloatingOrigin __instance, Vector3d __0, Vector3d __1, ref CallState __state)
        {
            var o = active; if (o == null || !o.Sampling) return;
            try
            {
                Require(ReferenceEquals(__instance, o.origin) && ReferenceEquals(FloatingOrigin.fetch, o.origin) && o.selected != null && o.selected.mainBody != null, "Origin owner changed.");
                Require(o.shiftCount < MaxShifts, "Shift tape bound exceeded."); o.Enter(2, -1, ref __state); if (!__state.entered) return;
                int i = o.shiftCount++; var s = o.shifts[i]; var r = s.row;
                r.sequence = i; r.window = o.window; r.frame = o.windows[o.window].frame; r.originInstanceId = __instance.GetInstanceID(); r.vesselInstanceId = o.selected.GetInstanceID(); r.vesselId = o.Report.vesselId;
                s.body = o.selected.mainBody; r.bodyInstanceId = s.body.GetInstanceID(); r.startedTicks = o.stack[__state.depth].start;
                s.request = __0; s.nonFrame = __1; s.vesselBefore = o.selected.transform.position; s.bodyBefore = s.body.position;
                o.shifts[i] = s; o.stack[__state.depth].shift = i;
            }
            catch (Exception e) { o.Report.droppedShifts++; o.Fail("shift", e); }
        }
        static void EventPrefix(EventData<Vector3d, Vector3d> __instance, ref CallState __state)
        { var o = active; if (o == null || !o.Sampling) return; if (!ReferenceEquals(__instance, o.originEvent)) { if (o.eventDepth > 0) { o.foreignEventDepth++; __state.owner = o; __state.foreignEvent = true; } return; } o.Enter(3, -1, ref __state); if (__state.entered) { __state.eventForeignBefore = o.foreignEventDepth; o.foreignEventDepth = 0; o.eventDepth++; } }
        static void InvokeSubscriber(EventData<Vector3d, Vector3d>.OnEvent callback, Vector3d a, Vector3d b)
        {
            var o = active; if (o == null || !o.Sampling || o.eventDepth == 0 || o.foreignEventDepth > 0) { callback(a, b); return; }
            int index; if (!o.subscribers.TryGetValue(callback, out index)) { o.Fail("subscriber", new InvalidOperationException("New unadmitted origin subscriber.")); callback(a, b); return; }
            long start = Stopwatch.GetTimestamp(); var row = o.Report.subscribers[index]; var metric = o.windows[o.window].subscribers[index]; row.calls++; metric.calls++;
            try { callback(a, b); } catch { row.exceptions++; metric.exceptions++; throw; } finally { long elapsed = Stopwatch.GetTimestamp() - start; row.ticks += elapsed; metric.ticks += elapsed; }
        }
        static Exception Finalizer(Exception __exception, CallState __state)
        {
            if (__state.foreignEvent) { __state.owner.foreignEventDepth--; return __exception; } if (!__state.entered) return __exception; var o = __state.owner; long end = Stopwatch.GetTimestamp();
            try
            {
                Require(o.window >= 0 && o.depth == __state.depth + 1 && Thread.CurrentThread.ManagedThreadId == o.thread, "Dispatch exit stack/window differs.");
                var s = o.stack[__state.depth]; long elapsed = end - s.start; Require(elapsed >= s.child && s.child >= 0, "Dispatch nested clock invalid.");
                var m = o.windows[o.window].metrics[s.slot]; m.calls++; m.inclusiveTicks += elapsed; m.selfTicks += elapsed - s.child;
                if (s.instance >= 0) { var id = o.Report.instances[s.instance]; id.calls++; if (id.vesselId == o.Report.vesselId) m.activeVesselCalls++; else m.otherVesselCalls++; }
                if (__exception != null) o.Report.originalExceptions++;
                if (s.slot == 3) { o.eventDepth--; o.foreignEventDepth = __state.eventForeignBefore; for (int i = __state.depth - 1; i >= 0; i--) if (o.stack[i].shift >= 0) { var r = o.shifts[o.stack[i].shift].row; r.eventCalls++; r.eventTicks += elapsed; break; } }
                if (s.shift >= 0)
                {
                    var sample = o.shifts[s.shift]; var r = sample.row; Vector3d vp = o.selected.transform.position, bp = sample.body.position;
                    r.endedTicks = end; r.completed = __exception == null; r.finite = Finite(sample.request) && Finite(sample.nonFrame) && Finite(sample.vesselBefore) && Finite(sample.bodyBefore) && Finite(vp) && Finite(bp);
                    r.selectedTransformChanged = Changed(sample.vesselBefore, vp); r.selectedBodyChanged = Changed(sample.bodyBefore, bp);
                    if (r.finite) { Values(r.requestedOffset, sample.request); Values(r.nonFrameOffset, sample.nonFrame); Values(r.vesselBefore, sample.vesselBefore); Values(r.vesselAfter, vp); Values(r.bodyBefore, sample.bodyBefore); Values(r.bodyAfter, bp); }
                    if (!r.finite) o.Fail("shift values", new InvalidOperationException("Nonfinite original relocation values."));
                }
                o.depth--; if (o.depth > 0) o.stack[o.depth - 1].child += elapsed;
            }
            catch (Exception e) { o.Fail("exit", e); }
            return __exception;
        }
        public void Before(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Phase || !installed || faulted || !capturing()) return; long start = Stopwatch.GetTimestamp();
            try { Require(window == -1 && depth == 0 && eventDepth == 0 && foreignEventDepth == 0 && Thread.CurrentThread.ManagedThreadId == thread && ReferenceEquals(selected, FlightGlobals.ActiveVessel), "Dispatch phase context differs.");
                Require(windowCount < MaxWindows, "Dispatch phase bound exceeded."); window = windowCount++; var w = windows[window]; w.sequence = window; w.frame = frame; w.fixedTimeSeconds = fixedTimeSeconds; w.startedTicks = Stopwatch.GetTimestamp(); Report.startedWindows++; }
            catch (Exception e) { Report.droppedWindows++; Fail("Before", e); } finally { Report.observerTicks += Stopwatch.GetTimestamp() - start; }
        }
        public void After(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Phase || window < 0) return; long end = Stopwatch.GetTimestamp();
            try { var w = windows[window]; Require(depth == 0 && eventDepth == 0 && foreignEventDepth == 0 && w.frame == frame && w.fixedTimeSeconds == fixedTimeSeconds, "Dispatch phase exit differs."); w.endedTicks = end; w.captureActiveAtEnd = capturing(); Report.completedWindows++; }
            catch (Exception e) { Fail("After", e); } finally { window = -1; depth = eventDepth = foreignEventDepth = 0; Report.observerTicks += Stopwatch.GetTimestamp() - end; }
        }
        public void Fault(string scope, Exception error) { if (scope == Phase) Fail("PlayerLoop", error); }
        void Fail(string context, Exception e) { faulted = true; Report.errors++; if (Report.detail == null) Report.detail = context + ": " + e; }
        void Remove()
        {
            installed = false; try { if (ReferenceEquals(active, this)) harmony.UnpatchAll(Owner);
                foreach (MethodInfo m in targets.Where(m => m != null)) { var p = Harmony.GetPatchInfo(m); Require(p == null || !p.Owners.Contains(Owner), "Dispatch owned hook remains."); }
                Report.hooksRemoved = true; Report.cleanupStatus = "removed-owned-hooks"; }
            catch (Exception e) { Fail("cleanup", e); Report.cleanupStatus = "cleanup-error"; } finally { if (ReferenceEquals(active, this)) active = null; }
        }
        public bool IsValidFinal(out string reason)
        {
            reason = null;
            if (faulted || Report.errors != 0 || Report.originalExceptions != 0 || !Report.hooksRemoved || Report.cleanupStatus != "removed-owned-hooks" ||
                Report.completedWindows == 0 || Report.startedWindows != Report.completedWindows || Report.subscribers == null || Report.subscribers.Any(r => r.exceptions != 0))
            { reason = "Dispatch observer failure, original exception or incomplete ownership/window closure."; return false; }
            return true;
        }
        public void Dispose()
        {
            if (disposed) return; long start = Stopwatch.GetTimestamp();
            try
            {
                Require(window == -1 && depth == 0 && eventDepth == 0 && foreignEventDepth == 0, "Dispatch released during call/window.");
                if (installed) { Owned(); Report.precleanupPatchGraph = Graph(); Require(Report.installedPatchGraph.SequenceEqual(Report.precleanupPatchGraph), "Dispatch graph drift.");
                    Require(Hash(File.ReadAllBytes(typeof(Vessel).Assembly.Location)) == Sha, "Original assembly changed.");
                    for (int i = 0; i < 4; i++) Require(Hash(targets[i].GetMethodBody().GetILAsByteArray()) == Report.targets[i].ilSha256, "Original dispatch body changed.");
                    foreach (Vessel v in vesselRefs) { var r = Report.instances[vessels[v]]; Require(v != null && v.id.ToString("D") == r.vesselId && v.loaded == r.loaded && v.packed == r.packed, "Vessel census owner/state changed."); }
                    foreach (Part p in partRefs) { var r = Report.instances[parts[p]]; Require(p != null && p.flightID == r.partFlightId && p.vessel != null && p.vessel.id.ToString("D") == r.vesselId, "Part census owner changed."); }
                    var list = AccessTools.Field(originEvent.GetType(), "events").GetValue(originEvent) as IList;
                    Require(list != null && list.Count == subscriberRefs.Count && ReferenceEquals(GameEvents.onFloatingOriginShift, originEvent) && ReferenceEquals(FloatingOrigin.fetch, origin), "Origin event/singleton or subscriber census changed.");
                    for (int i = 0; i < list.Count; i++) {
                        Require(ReferenceEquals(AccessTools.Field(list[i].GetType(), "evt").GetValue(list[i]), subscriberRefs[i]), "Origin subscriber identity/order changed.");
                        var m = subscriberRefs[i].Method; var r = Report.subscribers[i];
                        Require(Hash(File.ReadAllBytes(m.Module.Assembly.Location)) == r.assemblySha256 && (m.GetMethodBody() == null ? null : Hash(m.GetMethodBody().GetILAsByteArray())) == r.ilSha256, "Origin subscriber assembly/body changed.");
                    }
                }
            }
            catch (Exception e) { Fail("cleanup admission", e); }
            Remove();
            try { Report.restoredPatchGraph = Graph(); Require(Report.preinstallPatchGraph != null && Report.preinstallPatchGraph.SequenceEqual(Report.restoredPatchGraph), "Original graphs not restored."); }
            catch (Exception e) { Fail("restoration", e); }
            Report.windows = windows.Take(windowCount).ToArray(); Report.shifts = shifts.Take(shiftCount).Select(s => s.row).ToArray();
            Report.coverageStatus = windowCount == 0 ? "no-window-execution" : shiftCount == 0 ? "dispatch-observed-no-setter-execution" : "dispatch-and-setter-observed";
            string reason; Report.status = IsValidFinal(out reason) ? "observed" : "invalid"; if (Report.detail == null) Report.detail = reason;
            Report.cleanupTicks = Stopwatch.GetTimestamp() - start; disposed = true;
        }
    }
}
