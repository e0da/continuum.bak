using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class UpdateCallbackAttribution : IDisposable, IPlayerLoopBracketObserver
    {
        const string Owner = "continuum.update-callback-attribution";
        const int MaxTargets = 512, MaxWindows = 1024, MaxNestedFrames = 128;
        static readonly string Phase = typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate).FullName;
        static readonly MethodInfo PrefixMethod = AccessTools.DeclaredMethod(typeof(UpdateCallbackAttribution), "Prefix");
        static readonly MethodInfo FinalizerMethod = AccessTools.DeclaredMethod(typeof(UpdateCallbackAttribution), "Finalizer");
        static UpdateCallbackAttribution active;

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-update-callback-attribution/v1", runId, status = "not-started", cleanupStatus = "not-installed", detail;
            public string scope = "Declared managed instance void/no-argument MonoBehaviour.Update bodies in the frozen loaded Unity-referencing assemblies, excluding the entire Continuum helper. All instances of each declared body are included; inherited bodies are patched once and overrides/base calls remain distinct. Only main-thread body entries within ScriptRunBehaviourUpdate windows whose Before observed captureActive are recorded. Other callbacks, coroutine work, native dispatch, uninstrumented/inlined descendants and calls outside these windows have no row attribution.";
            public string timingScope = "Inclusive elapsed wall body brackets contain observed descendants and hook tax. Self subtracts only directly observed child envelopes, including recursive calls; it retains uninstrumented descendants and observer overhead. Inclusive rows overlap and must not be added. Sum of self rows is bounded by the window wall interval and is not exclusive CPU or whole-frame time. No original calls, state, return/exception behavior or force folds are replaced.";
            public string coverageScope = "All discovered targets are retained. Zero calls means no execution coverage, not zero cost. Scan/pin/patch failures, foreign hooks, new eligible assemblies/targets, original exceptions, stack/window/time drift and dropped windows invalidate qualification. The target and patch graph are audited at installation and cleanup, not on every invocation; transient foreign mutations between those boundaries are not proven absent.";
            public string overheadStatus = "unqualified-intrusive-observer";
            public string storageScope = "Flat primitive arrays and stack/boundary storage are allocated once after discovery. storagePayloadBytes sums field/element payloads, excluding array/object headers and struct padding. Sparse report objects are allocated only during cleanup/export outside captured windows.";
            public string clock = "System.Diagnostics.Stopwatch monotonic ticks", phase = Phase;
            public string excludedAssembly, excludedAssemblyMvid;
            public bool requested = true;
            public int processId, mainThreadId, targetCount, patchedMethods, errors, scanErrors, ownershipErrors, contextErrors, originalExceptions, droppedWindows;
            public int startedWindows, completedWindows, observedTargets;
            public int maximumTargets = MaxTargets, maximumWindows = MaxWindows, maximumNestedFrames = MaxNestedFrames;
            public long clockFrequency = Stopwatch.Frequency, timerReadFloorTicks, capturedCalls;
            public long admissionStartedTicks, admissionEndedTicks, storageStartedTicks, storageEndedTicks, storagePayloadBytes;
            public long cleanupStartedTicks, cleanupEndedTicks, exportStartedTicks, exportEndedTicks;
            public Target[] targets = new Target[0];
            public Window[] windows = new Window[0];
            public string[] eligibleAssemblyIdentities = new string[0];
        }
        [Serializable] public sealed class Target
        {
            public int slot, metadataToken;
            public string assembly, assemblyFullName, assemblySha256, assemblyMvid, declaringType, method, ilSha256;
        }
        [Serializable] public sealed class Window
        {
            public int sequence, frame;
            public long startedTicks, endedTicks;
            public Entry[] entries;
        }
        [Serializable] public sealed class Entry
        {
            public int slot, calls;
            public long inclusiveTicks, selfTicks;
        }
        struct BodyIdentity : IEquatable<BodyIdentity>
        {
            readonly Guid mvid;
            readonly int token;
            readonly Type declaringType;
            internal BodyIdentity(MethodBase method)
            { mvid = method.Module.ModuleVersionId; token = method.MetadataToken; declaringType = method.DeclaringType; }
            public bool Equals(BodyIdentity other)
            { return mvid == other.mvid && token == other.token && ReferenceEquals(declaringType, other.declaringType); }
            public override bool Equals(object other) { return other is BodyIdentity && Equals((BodyIdentity)other); }
            public override int GetHashCode() { return mvid.GetHashCode() ^ token ^ declaringType.GetHashCode(); }
        }
        sealed class Discovered
        {
            internal MethodInfo Method;
            internal Target Target;
        }
        struct Boundary
        {
            internal int Frame;
            internal long Start, End;
        }
        struct StackFrame
        {
            internal long Id, Started, ChildTicks;
            internal int Slot, Window;
        }
        public struct CallState
        {
            internal UpdateCallbackAttribution Owner;
            internal long Id;
            internal int Depth, Window;
            internal bool Entered;
        }
        readonly Harmony harmony = new Harmony(Owner);
        readonly Func<bool> capturing;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly Dictionary<BodyIdentity, int> slots = new Dictionary<BodyIdentity, int>();
        MethodInfo[] methods;
        string[] lastDiscoveredAssemblies;
        Boundary[] boundaries;
        StackFrame[] stack;
        int[] calls;
        long[] inclusive, self;
        int count, depth, window = -1, phaseFrame;
        long callId;
        double phaseTime;
        bool phaseOpen, installed, disposed, faulted;
        public ReportData Report { get; private set; }

        UpdateCallbackAttribution(string runId, Func<bool> capturing)
        {
            this.capturing = capturing;
            Report = new ReportData { runId = runId, processId = Process.GetCurrentProcess().Id, mainThreadId = thread,
                excludedAssembly = typeof(UpdateCallbackAttribution).Assembly.FullName,
                excludedAssemblyMvid = typeof(UpdateCallbackAttribution).Module.ModuleVersionId.ToString("D") };
        }

        public static UpdateCallbackAttribution Create(string[] arguments, string runId, Func<bool> capturing)
        {
            string requested = Environment.GetEnvironmentVariable("CONTINUUM_UPDATE_CALLBACK_ATTRIBUTION");
            if (string.IsNullOrEmpty(requested)) return null;
            var result = new UpdateCallbackAttribution(runId, capturing);
            result.Start(arguments, requested);
            return result;
        }
        void Start(string[] arguments, string requested)
        {
            Report.admissionStartedTicks = Stopwatch.GetTimestamp();
            try
            {
                Require(requested == "1" && arguments != null && !string.IsNullOrEmpty(Report.runId) && capturing != null,
                    "Update attribution requires env=1, run identity and capture predicate.");
                Require(active == null && !disposed, "Update attribution supports one owner.");
                Require(!arguments.Any(arg => arg == "--continuum-callback-attribution" || arg == "--continuum-flight-integrator-update-attribution") &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_INTEGRATION_ATTRIBUTION")),
                    "Update attribution excludes broad Fixed/narrow FI/aero callsite attribution.");
                List<Discovered> found = Discover();
                Require(found.Count > 0, "No declared managed MonoBehaviour.Update bodies discovered.");
                Report.eligibleAssemblyIdentities = lastDiscoveredAssemblies;
                Report.targetCount = found.Count;
                methods = new MethodInfo[found.Count]; Report.targets = new Target[found.Count];
                for (int i = 0; i < found.Count; i++)
                {
                    found[i].Target.slot = i; Report.targets[i] = found[i].Target; methods[i] = found[i].Method;
                    slots.Add(new BodyIdentity(methods[i]), i);
                }
                for (int i = 0; i < methods.Length; i++)
                {
                    Patches graph = Harmony.GetPatchInfo(methods[i]);
                    if (graph != null && graph.Owners.Any())
                    {
                        Report.ownershipErrors++;
                        throw new InvalidOperationException("Foreign Update patch at admission: " + Identity(Report.targets[i]));
                    }
                }
                Report.storageStartedTicks = Stopwatch.GetTimestamp();
                int cells = checked(MaxWindows * found.Count);
                calls = new int[cells]; inclusive = new long[cells]; self = new long[cells];
                boundaries = new Boundary[MaxWindows]; stack = new StackFrame[MaxNestedFrames];
                Report.storagePayloadBytes = (long)cells * (sizeof(int) + 2 * sizeof(long)) +
                    (long)MaxWindows * (sizeof(int) + 2 * sizeof(long)) + (long)MaxNestedFrames * (2 * sizeof(int) + 3 * sizeof(long));
                Report.storageEndedTicks = Stopwatch.GetTimestamp();
                long floor = long.MaxValue;
                for (int i = 0; i < 128; i++) { long start = Stopwatch.GetTimestamp(); floor = Math.Min(floor, Stopwatch.GetTimestamp() - start); }
                Report.timerReadFloorTicks = floor;
                active = this;
                foreach (MethodInfo method in methods)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(PrefixMethod) { priority = Priority.First },
                        finalizer: new HarmonyMethod(FinalizerMethod) { priority = Priority.Last });
                    Report.patchedMethods++;
                }
                Audit(false);
                installed = true; Report.status = "installed"; Report.cleanupStatus = "installed";
            }
            catch (Exception error)
            {
                Fail("installation", error); Report.status = "unavailable"; Remove();
            }
            finally { Report.admissionEndedTicks = Stopwatch.GetTimestamp(); }
        }

        List<Discovered> Discover()
        {
            try
            {
                var result = new List<Discovered>();
                var assemblies = new List<string>();
                Assembly own = typeof(UpdateCallbackAttribution).Assembly;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(item => item.FullName, StringComparer.Ordinal))
                {
                    if (ReferenceEquals(assembly, own) || !ReferencesUnity(assembly)) continue;
                    Require(!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location), "Eligible assembly has no immutable file identity: " + assembly.FullName);
                    assemblies.Add(assembly.FullName + "|" + assembly.ManifestModule.ModuleVersionId.ToString("D"));
                    Type[] types = assembly.GetTypes();
                    string sha = null;
                    foreach (Type type in types)
                    {
                        if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (method.Name != "Update" || method.ReturnType != typeof(void) || method.GetParameters().Length != 0 || method.IsAbstract) continue;
                            Require(!method.IsGenericMethod && !method.ContainsGenericParameters && !type.ContainsGenericParameters,
                                "Open generic Update body cannot be qualified: " + type.FullName);
                            MethodBody body = method.GetMethodBody();
                            Require(body != null, "Declared Update has no managed body: " + type.FullName);
                            if (sha == null) sha = AssemblyHash(assembly);
                            result.Add(new Discovered { Method = method, Target = new Target { assembly = assembly.GetName().Name,
                                assemblyFullName = assembly.FullName, assemblySha256 = sha, assemblyMvid = method.Module.ModuleVersionId.ToString("D"),
                                declaringType = type.FullName, method = method.Name, metadataToken = method.MetadataToken, ilSha256 = Hash(body.GetILAsByteArray()) } });
                            Require(result.Count <= MaxTargets, "Declared Update target bound exceeded.");
                        }
                    }
                }
                result.Sort((left, right) => string.CompareOrdinal(Identity(left.Target), Identity(right.Target)));
                lastDiscoveredAssemblies = assemblies.ToArray();
                return result;
            }
            catch { Report.scanErrors++; throw; }
        }
        static bool ReferencesUnity(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return name == "Assembly-CSharp" || name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                assembly.GetReferencedAssemblies().Any(item => item.Name.StartsWith("UnityEngine", StringComparison.Ordinal));
        }
        static string Identity(Target target)
        { return target.assemblyFullName + "|" + target.assemblyMvid + "|" + target.declaringType + "|" + target.metadataToken.ToString("D10", System.Globalization.CultureInfo.InvariantCulture); }
        static string AssemblyHash(Assembly assembly)
        { using (var input = File.OpenRead(assembly.Location)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(input)); }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static bool SameMethod(MethodBase left, MethodBase right)
        { return left != null && right != null && new BodyIdentity(left).Equals(new BodyIdentity(right)); }
        static bool Owned(Patch patch, MethodInfo callback, int priority)
        { return patch.owner == Owner && patch.priority == priority && SameMethod(patch.PatchMethod, callback); }

        void Audit(bool rediscover)
        {
            try
            {
                if (rediscover)
                {
                    List<Discovered> found = Discover();
                    Require(lastDiscoveredAssemblies.SequenceEqual(Report.eligibleAssemblyIdentities), "Frozen eligible Unity-referencing assembly set changed.");
                    Require(found.Count == Report.targets.Length, "Frozen Update target set changed.");
                    for (int i = 0; i < found.Count; i++)
                    {
                        Target actual = found[i].Target, expected = Report.targets[i];
                        Require(Identity(actual) == Identity(expected) && actual.assemblySha256 == expected.assemblySha256 &&
                            actual.ilSha256 == expected.ilSha256 && SameMethod(found[i].Method, methods[i]), "Frozen Update bytes/body identity changed: " + Identity(expected));
                    }
                }
                foreach (MethodInfo method in methods)
                {
                    Patches graph = Harmony.GetPatchInfo(method);
                    Require(graph != null && graph.Owners.Count() == 1 && graph.Owners.Contains(Owner) && graph.Prefixes.Count == 1 &&
                        graph.Finalizers.Count == 1 && graph.Postfixes.Count == 0 && graph.Transpilers.Count == 0 &&
                        Owned(graph.Prefixes[0], PrefixMethod, Priority.First) && Owned(graph.Finalizers[0], FinalizerMethod, Priority.Last),
                        "Update body lost its exact owned patch graph: " + method.DeclaringType.FullName);
                }
            }
            catch { Report.ownershipErrors++; throw; }
        }

        public void Before(string scope, int frame, double time)
        {
            if (!installed || disposed || faulted || scope != Phase) return;
            try
            {
                Require(Thread.CurrentThread.ManagedThreadId == thread && !phaseOpen && depth == 0 && window == -1 &&
                    !double.IsNaN(time) && !double.IsInfinity(time), "Update Before thread/stack/window/time drift.");
                phaseOpen = true; phaseFrame = frame; phaseTime = time;
                if (!capturing()) return;
                if (count == MaxWindows) { Report.droppedWindows++; throw new InvalidOperationException("Update window storage bound exceeded."); }
                if (count > 0) Require(frame > boundaries[count - 1].Frame, "Update window frame order drift.");
                window = count++; Report.startedWindows++;
                boundaries[window] = new Boundary { Frame = frame, Start = Stopwatch.GetTimestamp() };
            }
            catch (Exception error) { Report.contextErrors++; Fail("Before", error); }
        }
        public void After(string scope, int frame, double time)
        {
            if (!installed || disposed || scope != Phase || (!phaseOpen && faulted)) return;
            try
            {
                long end = Stopwatch.GetTimestamp();
                Require(Thread.CurrentThread.ManagedThreadId == thread && phaseOpen && phaseFrame == frame && phaseTime == time && depth == 0,
                    "Update After thread/stack/window/time drift.");
                if (window >= 0)
                {
                    Require(end >= boundaries[window].Start, "Update window clock moved backwards.");
                    boundaries[window].End = end; Report.completedWindows++;
                }
            }
            catch (Exception error) { Report.contextErrors++; Fail("After", error); }
            finally { phaseOpen = false; window = -1; }
        }
        public void Fault(string scope, Exception error)
        { if (scope == Phase) { Report.contextErrors++; Fail("PlayerLoop", error); } }

        public static void Prefix(MethodBase __originalMethod, ref CallState __state)
        {
            UpdateCallbackAttribution owner = active;
            if (owner == null || !owner.installed || owner.disposed || owner.faulted || owner.window < 0) return;
            try
            {
                long started = Stopwatch.GetTimestamp();
                Require(Thread.CurrentThread.ManagedThreadId == owner.thread && owner.phaseOpen, "Update callback escaped main-thread window.");
                int slot;
                Require(owner.slots.TryGetValue(new BodyIdentity(__originalMethod), out slot), "Unknown declared Update body reached.");
                Require(owner.depth < MaxNestedFrames, "Update nested callback stack bound exceeded.");
                int depth = owner.depth;
                long id = checked(++owner.callId);
                owner.stack[depth] = new StackFrame { Id = id, Slot = slot, Window = owner.window, Started = started };
                __state = new CallState { Owner = owner, Id = id, Depth = depth, Window = owner.window, Entered = true };
                owner.depth++;
            }
            catch (Exception error) { owner.Report.contextErrors++; owner.Fail("Prefix", error); }
        }
        public static Exception Finalizer(Exception __exception, ref CallState __state)
        {
            if (!__state.Entered) return __exception;
            UpdateCallbackAttribution owner = __state.Owner;
            __state.Entered = false;
            try
            {
                long end = Stopwatch.GetTimestamp();
                Require(owner != null && ReferenceEquals(active, owner) && !owner.disposed && owner.installed &&
                    Thread.CurrentThread.ManagedThreadId == owner.thread && owner.phaseOpen && owner.window == __state.Window &&
                    owner.depth == __state.Depth + 1, "Update finalizer stack/window/owner drift.");
                StackFrame current = owner.stack[__state.Depth];
                Require(current.Id == __state.Id && current.Window == __state.Window && end >= current.Started,
                    "Update finalizer identity/clock drift.");
                long elapsed = end - current.Started;
                Require(current.ChildTicks >= 0 && current.ChildTicks <= elapsed, "Update child wall envelopes exceed parent.");
                int cell = checked(current.Window * owner.Report.targetCount + current.Slot);
                owner.calls[cell] = checked(owner.calls[cell] + 1);
                owner.inclusive[cell] = checked(owner.inclusive[cell] + elapsed);
                owner.self[cell] = checked(owner.self[cell] + elapsed - current.ChildTicks);
                owner.Report.capturedCalls = checked(owner.Report.capturedCalls + 1);
                if (__exception != null) owner.Report.originalExceptions++;
                owner.depth--;
                if (owner.depth > 0) owner.stack[owner.depth - 1].ChildTicks = checked(owner.stack[owner.depth - 1].ChildTicks + elapsed);
            }
            catch (Exception error)
            {
                if (owner != null) { owner.Report.contextErrors++; owner.Fail("Finalizer", error); }
            }
            return __exception;
        }
        void Fail(string context, Exception error)
        {
            Report.errors++; faulted = true;
            if (Report.detail == null) Report.detail = context + ": " + (error == null ? "unknown observer fault" : error.ToString());
            if (Report.status != "unavailable") Report.status = "invalid";
        }
        void Remove()
        {
            installed = false;
            try
            {
                if (ReferenceEquals(active, this)) harmony.UnpatchAll(Owner);
                if (methods != null) foreach (MethodInfo method in methods)
                {
                    Patches graph = Harmony.GetPatchInfo(method);
                    Require(graph == null || !graph.Owners.Contains(Owner), "Owned Update patches remain after removal.");
                }
                Report.cleanupStatus = "removed-owned-patches";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Fail("cleanup", error); }
            finally { if (ReferenceEquals(active, this)) active = null; }
        }
        public void Dispose()
        {
            if (disposed) return;
            Report.cleanupStartedTicks = Stopwatch.GetTimestamp();
            installed = false;
            if (phaseOpen || window >= 0 || depth != 0)
            { Report.contextErrors++; Report.droppedWindows += window >= 0 ? 1 : 0; Fail("cleanup", new InvalidOperationException("Unclosed Update window/stack.")); }
            if (Report.patchedMethods == Report.targetCount && Report.patchedMethods > 0 && ReferenceEquals(active, this))
                try { Audit(true); } catch (Exception error) { Fail("final admission", error); }
            Remove(); disposed = true;
            Report.exportStartedTicks = Stopwatch.GetTimestamp();
            var exported = new List<Window>(Report.completedWindows);
            var covered = new bool[Report.targetCount];
            if (boundaries != null) for (int index = 0; index < count; index++)
            {
                Boundary boundary = boundaries[index];
                if (boundary.End == 0) continue;
                int offset = index * Report.targetCount, entries = 0;
                for (int slot = 0; slot < Report.targetCount; slot++) if (calls[offset + slot] > 0) entries++;
                var rows = new Entry[entries]; int next = 0;
                for (int slot = 0; slot < Report.targetCount; slot++) if (calls[offset + slot] > 0)
                {
                    covered[slot] = true;
                    rows[next++] = new Entry { slot = slot, calls = calls[offset + slot], inclusiveTicks = inclusive[offset + slot], selfTicks = self[offset + slot] };
                }
                exported.Add(new Window { sequence = index, frame = boundary.Frame, startedTicks = boundary.Start, endedTicks = boundary.End, entries = rows });
            }
            Report.observedTargets = covered.Count(value => value);
            Report.windows = exported.ToArray();
            Report.exportEndedTicks = Stopwatch.GetTimestamp(); Report.cleanupEndedTicks = Report.exportEndedTicks;
            if (Report.status != "unavailable") Report.status = Report.errors == 0 && Report.originalExceptions == 0 &&
                Report.droppedWindows == 0 && Report.patchedMethods == Report.targetCount && Report.targetCount > 0 &&
                Report.completedWindows == Report.startedWindows && Report.completedWindows > 0 && Report.capturedCalls > 0 &&
                Report.cleanupStatus == "removed-owned-patches" ? "observed" : "invalid";
        }
        public bool IsValidFinal(out string reason)
        {
            reason = null;
            if (!disposed || Report.status != "observed" || Report.cleanupStatus != "removed-owned-patches" ||
                Report.errors != 0 || Report.scanErrors != 0 || Report.ownershipErrors != 0 || Report.contextErrors != 0 ||
                Report.originalExceptions != 0 || Report.droppedWindows != 0 || Report.targetCount <= 0 || Report.targetCount > MaxTargets ||
                Report.patchedMethods != Report.targetCount || Report.targets.Length != Report.targetCount || Report.clockFrequency <= 0 ||
                Report.startedWindows != Report.completedWindows || Report.windows.Length != Report.completedWindows ||
                Report.windows.Length == 0 || Report.windows.Length > MaxWindows || Report.capturedCalls <= 0)
            { reason = "Update observer did not close with complete admitted coverage and owned cleanup: " + Report.status + ". " + Report.detail; return false; }
            for (int index = 0; index < Report.windows.Length; index++)
            {
                Window row = Report.windows[index]; long selfTotal = 0;
                if (row.sequence != index || row.startedTicks <= 0 || row.endedTicks < row.startedTicks ||
                    (index > 0 && (row.frame <= Report.windows[index - 1].frame || row.startedTicks < Report.windows[index - 1].endedTicks)))
                { reason = "Update window sequence/frame/clock drift."; return false; }
                int previous = -1;
                foreach (Entry entry in row.entries)
                {
                    if (entry.slot <= previous || entry.slot >= Report.targetCount || entry.calls <= 0 || entry.selfTicks < 0 || entry.inclusiveTicks < entry.selfTicks)
                    { reason = "Update sparse callback entries are malformed."; return false; }
                    previous = entry.slot; selfTotal = checked(selfTotal + entry.selfTicks);
                }
                if (selfTotal > row.endedTicks - row.startedTicks)
                { reason = "Update callback self sum exceeds its wall window."; return false; }
            }
            return true;
        }
    }
}
