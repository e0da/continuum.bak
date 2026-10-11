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
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;
using UpdateLoop = UnityEngine.PlayerLoop.Update;

namespace KspContinuum
{
    internal sealed class FixedCallbackAttribution : IDisposable, IPlayerLoopBracketObserver
    {
        const string Owner = "continuum.fixed-callback-attribution";
        const int MaximumTargets = 4096;
        static readonly Guid StockMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static readonly string[] StockProviderMethods = { "ThermoPrecalculate", "Integrate", "UpdateThermodynamics",
            "UpdateOcclusion", "UpdateOcclusionConvection", "UpdateOcclusionSolar", "UpdateOcclusionBody",
            "UpdateAerodynamics", "UpdateConvection" };
        static FixedCallbackAttribution activeOwner;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Dictionary<MethodBase, int> slots = new Dictionary<MethodBase, int>();
        readonly CallbackAttributionAccumulator accumulator = new CallbackAttributionAccumulator(Stopwatch.Frequency);
        int[] depths;
        long windowStart, activeWindowTicks;
        int activeThreadId;
        bool activeWindow, installed, disposed;
        FeedInputAttribution feedInput;
        MechJebDriveAttribution mechJebDrive;
        public struct CallState { public long Started; public int Slot; public bool Outermost; }
        public CallbackAttributionReport Report { get; private set; }

        public FixedCallbackAttribution()
        {
            Report = new CallbackAttributionReport { clockFrequency = Stopwatch.Frequency };
            Report.timerReadFloorTicks = MeasureFloor();
        }

        public void Start()
        {
            if (installed || disposed || activeOwner != null) throw new InvalidOperationException("Fixed callback attribution supports one owner.");
            activeOwner = this;
            try
            {
                feedInput = new FeedInputAttribution(() => installed && activeWindow && Thread.CurrentThread.ManagedThreadId == activeThreadId);
                Report.feedInput = feedInput.Report;
                feedInput.Start();
                mechJebDrive = new MechJebDriveAttribution(() => installed && activeWindow && Thread.CurrentThread.ManagedThreadId == activeThreadId);
                Report.mechJebDrive = mechJebDrive.Report;
                mechJebDrive.Start();
                MethodInfo prefix = AccessTools.DeclaredMethod(typeof(FixedCallbackAttribution), "Prefix");
                MethodInfo finalizer = AccessTools.DeclaredMethod(typeof(FixedCallbackAttribution), "Finalizer");
                foreach (MethodInfo target in Discover())
                {
                    int slot = accumulator.Register(Row(target)); slots.Add(target, slot);
                    harmony.Patch(target, new HarmonyMethod(prefix) { priority = Priority.First },
                        null, null,
                        new HarmonyMethod(finalizer) { priority = Priority.Last });
                    Report.patchedMethods++;
                }
                if (Report.patchedMethods == 0) throw new InvalidOperationException("No patchable managed FixedUpdate callbacks were found.");
                depths = new int[Report.patchedMethods];
                installed = true; Report.status = "installed"; Report.cleanupStatus = "installed";
            }
            catch (Exception error)
            {
                Report.status = "unavailable"; Report.detail = error.GetType().Name + ": " + error.Message;
                Remove();
            }
        }

        IEnumerable<MethodInfo> Discover()
        {
            var result = new List<MethodInfo>();
            HashSet<MethodInfo> boundedProviders = PinnedProviders();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                if (assembly.IsDynamic || !ReferencesUnity(assembly)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error)
                {
                    Report.scanErrors++;
                    types = error.Types.Where(type => type != null).ToArray();
                }
                catch { Report.scanErrors++; continue; }
                foreach (Type type in types.OrderBy(item => item.FullName, StringComparer.Ordinal))
                {
                    if (type == null) continue;
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        bool fixedCallback = typeof(MonoBehaviour).IsAssignableFrom(type) && method.Name == "FixedUpdate" &&
                            method.ReturnType == typeof(void) && method.GetParameters().Length == 0;
                        bool stockProvider = (type.FullName == "FlightIntegrator" && Array.IndexOf(StockProviderMethods, method.Name) >= 0) ||
                            (type.FullName == "VesselPrecalculate" && method.Name == "CalculatePhysicsStats") ||
                            boundedProviders.Contains(method);
                        if ((!fixedCallback && !stockProvider) || method.IsAbstract || method.IsGenericMethodDefinition ||
                            method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                        result.Add(method);
                        if (result.Count > MaximumTargets) throw new InvalidOperationException("FixedUpdate target bound exceeded.");
                    }
                }
            }
            foreach (MethodInfo provider in boundedProviders)
                if (!result.Contains(provider)) throw new InvalidOperationException("Pinned stock attribution seam was not discovered: " + provider.DeclaringType.FullName + "." + provider.Name);
            result.Sort((left, right) => string.CompareOrdinal(Identity(left), Identity(right)));
            Report.discoveredMethods = result.Count;
            return result;
        }

        static HashSet<MethodInfo> PinnedProviders()
        {
            var result = new HashSet<MethodInfo>();
            AddPinnedProvider(result, typeof(Vessel), "FeedInputFeed", 100677715, typeof(void), Type.EmptyTypes);
            AddPinnedProvider(result, typeof(VesselAutopilot.VesselSAS), "GetTotalVesselTorque", 100704548, typeof(Vector3), new[] { typeof(Vessel) });
            AddPinnedProvider(result, typeof(Part), "propagateControlUpdate", 100670966, typeof(void), new[] { typeof(FlightCtrlState) });
            AddPinnedProvider(result, typeof(FlightIntegrator), "UpdateMassStats", 100677455, typeof(void), Type.EmptyTypes);
            AddPinnedProvider(result, typeof(FlightIntegrator), "DragCubeSetupAndPartAeroStats", 100677504, typeof(void), new[] { typeof(Vessel) });
            return result;
        }

        static void AddPinnedProvider(HashSet<MethodInfo> result, Type type, string name, int token, Type returnType, Type[] parameters)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
            if (type.Assembly.GetName().Name != "Assembly-CSharp" || type.Module.ModuleVersionId != StockMvid ||
                method == null || method.MetadataToken != token || method.ReturnType != returnType ||
                method.IsStatic || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
                throw new InvalidOperationException("Pinned stock attribution seam differs: " + type.FullName + "." + name);
            result.Add(method);
        }

        static bool ReferencesUnity(Assembly assembly)
        {
            string own = assembly.GetName().Name;
            if (own == "Assembly-CSharp" || own.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
            try { return assembly.GetReferencedAssemblies().Any(reference => reference.Name.StartsWith("UnityEngine", StringComparison.Ordinal)); }
            catch { return false; }
        }

        static string Identity(MethodInfo method) => method.Module.Assembly.GetName().Name + "|" + method.DeclaringType.FullName + "|" + method.MetadataToken;
        static CallbackAttributionRow Row(MethodInfo method)
        {
            AssemblyName name = method.Module.Assembly.GetName();
            bool callback = typeof(MonoBehaviour).IsAssignableFrom(method.DeclaringType) && method.Name == "FixedUpdate" && method.GetParameters().Length == 0;
            return new CallbackAttributionRow { category = callback ? "unity-fixed-callback" : "stock-provider-seam",
                assembly = name.Name, assemblyVersion = name.Version == null ? null : name.Version.ToString(),
                assemblyMvid = method.Module.ModuleVersionId.ToString("D"), declaringType = method.DeclaringType.FullName,
                method = method.Name, metadataToken = method.MetadataToken };
        }

        public void Before(string scope, int frame, double time)
        {
            if (!installed || scope != typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) return;
            if (activeWindow) { Report.callbackErrors++; return; }
            activeWindow = true; activeThreadId = Thread.CurrentThread.ManagedThreadId; windowStart = Stopwatch.GetTimestamp();
        }
        public void After(string scope, int frame, double time)
        {
            if (!installed || scope != typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) return;
            if (!activeWindow) { Report.callbackErrors++; return; }
            long end = Stopwatch.GetTimestamp(); activeWindow = false;
            if (end < windowStart) Report.callbackErrors++; else activeWindowTicks += end - windowStart;
        }
        public void Fault(string scope, Exception error) { if (scope == typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) Report.callbackErrors++; }

        public static void Prefix(MethodBase __originalMethod, ref CallState __state)
        {
            FixedCallbackAttribution owner = activeOwner;
            if (owner == null || !owner.installed || !owner.activeWindow ||
                Thread.CurrentThread.ManagedThreadId != owner.activeThreadId) return;
            int slot;
            if (!owner.slots.TryGetValue(__originalMethod, out slot)) { owner.Report.callbackErrors++; return; }
            __state.Slot = slot; __state.Outermost = owner.depths[slot]++ == 0; __state.Started = Stopwatch.GetTimestamp();
        }
        public static Exception Finalizer(Exception __exception, ref CallState __state)
        {
            Complete(ref __state); return __exception;
        }
        static void Complete(ref CallState state)
        {
            if (state.Started == 0) return;
            long end = Stopwatch.GetTimestamp(), start = state.Started; state.Started = 0;
            FixedCallbackAttribution owner = activeOwner;
            if (owner == null || end < start) { if (owner != null) owner.Report.callbackErrors++; return; }
            if (state.Slot < 0 || state.Slot >= owner.depths.Length || owner.depths[state.Slot] < 1)
            { owner.Report.callbackErrors++; return; }
            owner.depths[state.Slot]--;
            try { owner.accumulator.Record(state.Slot, end - start, state.Outermost); } catch { owner.Report.callbackErrors++; }
        }

        static long MeasureFloor()
        {
            long floor = long.MaxValue;
            for (int i = 0; i < 128; i++) { long start = Stopwatch.GetTimestamp(); long elapsed = Stopwatch.GetTimestamp() - start; if (elapsed < floor) floor = elapsed; }
            return floor;
        }

        void Remove()
        {
            if (feedInput != null) feedInput.Dispose();
            if (mechJebDrive != null) mechJebDrive.Finish(activeWindowTicks);
            try
            {
                harmony.UnpatchAll(Owner);
                bool remains = slots.Keys.Any(method => { Patches patches = Harmony.GetPatchInfo(method); return patches != null &&
                    patches.Owners.Contains(Owner); });
                Report.cleanupStatus = remains ? "cleanup-error" : "removed-owned-patches";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Report.detail = error.GetType().Name + ": " + error.Message; }
            if (ReferenceEquals(activeOwner, this)) activeOwner = null;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (activeWindow) { activeWindow = false; Report.callbackErrors++; }
            Remove();
            Report.activeWindowTicks = activeWindowTicks;
            Report.callbacks = accumulator.Finish(activeWindowTicks);
            if (Report.status != "unavailable") Report.status = Report.cleanupStatus == "removed-owned-patches" &&
                Report.callbackErrors == 0 && Report.scanErrors == 0 && Report.patchedMethods == Report.discoveredMethods &&
                Report.callbacks.Length > 0 && Report.feedInput != null && Report.feedInput.status == "observed" &&
                Report.mechJebDrive != null && (Report.mechJebDrive.status == "observed" || Report.mechJebDrive.status == "not-present") ? "observed" : "invalid";
        }
    }

    internal sealed class FlightIntegratorUpdateAttribution : IDisposable, IPlayerLoopBracketObserver
    {
        const string Owner = "continuum.flight-integrator-update-attribution";
        const string StockSha256 = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int Slots = 6, MaximumWindows = 4096;
        static readonly Guid StockMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static readonly string[] Names = { "Update", "ThermoPrecalculate", "UpdateOcclusion", "UpdateOcclusionConvection", "UpdateOcclusionSolar", "UpdateOcclusionBody" };
        static readonly int[] Tokens = { 100677458, 100677459, 100677505, 100677506, 100677507, 100677508 };
        static FlightIntegratorUpdateAttribution activeOwner;
        internal enum Phase { None = -1, Update = 0, Fixed = 1 }
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-flight-integrator-attribution/v1", status = "not-started", cleanupStatus = "not-installed", detail, assemblySha256;
            public string scope = "Observer-lifetime inclusive wall time for six pinned stock FlightIntegrator declared methods. Render Update and physics Fixed windows have separate denominators; nested method rows overlap and are not exclusive CPU. Other methods, overrides that do not call these declared bodies and calls outside those subtrees are not attributed. All instances of the pinned declared bodies are included.";
            public string overheadScope = "Intrusive observer tax remains unqualified. Admission, storage preparation, report validation and serialization are outside method samples; timer floor does not measure Harmony or whole-frame observer tax. No force, thermal, input or rendering state is written.";
            public string windowScope = "Timestamped windows include warmup/cleanup. Select fully enclosed startedTicks/endedTicks against Probe capture timestamps before deriving capture means. Each slot follows seamOrder; zero calls means no execution coverage, not zero cost.";
            public string witnessStatus = "not-run";
            public string[] seamOrder;
            public long clockFrequency = Stopwatch.Frequency, timerReadFloorTicks, storageStartedTicks, storageEndedTicks, admissionStartedTicks, admissionEndedTicks;
            public int patchedMethods, callbackErrors, contextErrors, originalExceptions, droppedWindows, completedUpdateWindows, completedFixedWindows;
            public CallbackAttributionReport Update, Fixed;
            public Window[] windows = new Window[0];
        }
        [Serializable] public sealed class Window
        {
            public string phase;
            public int sequence, frame;
            public double timeSeconds;
            public long startedTicks, endedTicks, elapsedTicks;
            public int[] calls, outermostCalls;
            public long[] inclusiveTicks, outermostInclusiveTicks;
        }
        struct Boundary
        {
            internal Phase Phase;
            internal int Frame;
            internal double Time;
            internal long Start, End;
        }
        public struct CallState
        {
            internal FlightIntegratorUpdateAttribution Owner;
            internal long Started;
            internal int Slot, Window;
            internal Phase Phase;
            internal bool Entered, Outermost;
        }
        readonly long storageStartedTicks = Stopwatch.GetTimestamp();
        readonly Harmony harmony = new Harmony(Owner);
        readonly CallbackAttributionAccumulator[] accumulators = {
            new CallbackAttributionAccumulator(Stopwatch.Frequency), new CallbackAttributionAccumulator(Stopwatch.Frequency) };
        readonly List<MethodInfo> patched = new List<MethodInfo>();
        readonly Dictionary<int, int> slots = new Dictionary<int, int>();
        readonly int[] depths = new int[Slots], calls = new int[MaximumWindows * Slots], outerCalls = new int[MaximumWindows * Slots];
        readonly long[] ticks = new long[MaximumWindows * Slots], outerTicks = new long[MaximumWindows * Slots], phaseTicks = new long[2];
        readonly Boundary[] boundaries = new Boundary[MaximumWindows];
        Module stockModule;
        Phase activePhase = Phase.None;
        int threadId, activeIndex = -1, windowCount;
        int activeFrame;
        long windowStart;
        bool installed, disposed;
        public ReportData Report { get; private set; } = new ReportData();

        public FlightIntegratorUpdateAttribution()
        { Report.storageStartedTicks = storageStartedTicks; Report.storageEndedTicks = Stopwatch.GetTimestamp(); }

        public static bool Requested(string[] arguments)
        { return Array.IndexOf(arguments, "--continuum-flight-integrator-update-attribution") >= 0; }
        static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static CallbackAttributionReport PhaseReport(string phase)
        {
            return new CallbackAttributionReport { schema = "continuum-flight-integrator-" + phase + "/v1", clockFrequency = Stopwatch.Frequency,
                measurementScope = "Inclusive wall method samples inside ScriptRunBehaviour" + (phase == "update" ? "Update" : "FixedUpdate") + ". activeWindowTicks is this phase's observer-lifetime denominator, never a different phase's denominator. Outermost removes recursion of the same method only; rows overlap.",
                coverageScope = "Six pinned FlightIntegrator declared bodies. All six rows are retained. A row category ending /no-coverage and calls=0 has no valid cost estimate; numeric zero fields are placeholders." };
        }
        public void Start()
        {
            Require(!installed && !disposed && activeOwner == null, "FlightIntegrator attribution supports one owner.");
            activeOwner = this; threadId = Thread.CurrentThread.ManagedThreadId;
            Report.admissionStartedTicks = Stopwatch.GetTimestamp();
            Report.Update = PhaseReport("update"); Report.Fixed = PhaseReport("fixed");
            long floor = long.MaxValue;
            for (int i = 0; i < 128; i++) { long start = Stopwatch.GetTimestamp(); floor = Math.Min(floor, Stopwatch.GetTimestamp() - start); }
            Report.timerReadFloorTicks = Report.Update.timerReadFloorTicks = Report.Fixed.timerReadFloorTicks = floor;
            try
            {
                Type type = typeof(FlightIntegrator); stockModule = type.Module;
                Require(type.Assembly.GetName().Name == "Assembly-CSharp" && stockModule.ModuleVersionId == StockMvid, "Pinned FlightIntegrator module differs.");
                Report.assemblySha256 = Hash(File.ReadAllBytes(type.Assembly.Location));
                Require(Report.assemblySha256 == StockSha256, "Pinned stock assembly bytes differ.");
                var identities = new string[Slots];
                var targets = new MethodInfo[Slots];
                for (int i = 0; i < Slots; i++)
                {
                    MethodInfo method = stockModule.ResolveMethod(Tokens[i]) as MethodInfo;
                    Type[] parameters = i == 2 ? new[] { typeof(bool) } : Type.EmptyTypes;
                    Require(method != null && method.DeclaringType == type && method.Module.ModuleVersionId == StockMvid && method.MetadataToken == Tokens[i] &&
                        method.Name == Names[i] && method.ReturnType == typeof(void) && !method.IsStatic && !method.IsAbstract &&
                        !method.ContainsGenericParameters && method.GetMethodBody() != null && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters),
                        "Pinned declared FlightIntegrator seam differs: " + Names[i]);
                    Require(Harmony.GetPatchInfo(method) == null, "FlightIntegrator seam has a foreign patch: " + Names[i]);
                    targets[i] = method; slots.Add(Tokens[i], i);
                    string il = Hash(method.GetMethodBody().GetILAsByteArray());
                    identities[i] = "Assembly-CSharp|FlightIntegrator|" + Tokens[i] + "|" + il;
                    for (int phase = 0; phase < 2; phase++)
                    {
                        int registered = accumulators[phase].Register(new CallbackAttributionRow { assembly = "Assembly-CSharp", assemblyVersion = type.Assembly.GetName().Version.ToString(),
                            assemblyMvid = StockMvid.ToString("D"), declaringType = "FlightIntegrator", method = Names[i], metadataToken = Tokens[i], ilSha256 = il,
                            category = "flight-integrator/" + (phase == 0 ? "update" : "fixed") });
                        Require(registered == i, "FlightIntegrator timing slot order differs.");
                    }
                }
                Report.seamOrder = identities;
                foreach (MethodInfo method in targets)
                {
                    patched.Add(method);
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(FlightIntegratorUpdateAttribution), nameof(Prefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(FlightIntegratorUpdateAttribution), nameof(Finalizer)) { priority = Priority.Last });
                    Report.patchedMethods++;
                }
                Report.Update.discoveredMethods = Report.Fixed.discoveredMethods = Slots;
                Report.Update.patchedMethods = Report.Fixed.patchedMethods = Report.patchedMethods;
                installed = true; Report.status = Report.Update.status = Report.Fixed.status = "installed";
                Report.cleanupStatus = Report.Update.cleanupStatus = Report.Fixed.cleanupStatus = "installed";
            }
            catch (Exception error)
            { Report.status = Report.Update.status = Report.Fixed.status = "unavailable"; Report.detail = error.GetType().Name + ": " + error.Message; Remove(); }
            finally { Report.admissionEndedTicks = Stopwatch.GetTimestamp(); }
        }
        static Phase ScopePhase(string scope)
        {
            if (scope == typeof(UpdateLoop.ScriptRunBehaviourUpdate).FullName) return Phase.Update;
            if (scope == typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) return Phase.Fixed;
            return Phase.None;
        }
        void Error(bool context = false)
        { Interlocked.Increment(ref Report.callbackErrors); if (context) Interlocked.Increment(ref Report.contextErrors); }
        public void Before(string scope, int frame, double time)
        {
            Phase phase = ScopePhase(scope); if (!installed || phase == Phase.None) return;
            if (activePhase != Phase.None || Thread.CurrentThread.ManagedThreadId != threadId || double.IsNaN(time) || double.IsInfinity(time)) { Error(true); return; }
            activePhase = phase; activeFrame = frame; windowStart = Stopwatch.GetTimestamp();
            if (windowCount == MaximumWindows) { Report.droppedWindows++; activeIndex = -2; return; }
            activeIndex = windowCount++;
            boundaries[activeIndex] = new Boundary { Phase = phase, Frame = frame, Time = time, Start = windowStart };
        }
        public void After(string scope, int frame, double time)
        {
            Phase phase = ScopePhase(scope); if (!installed || phase == Phase.None) return;
            if (phase != activePhase || frame != activeFrame || Thread.CurrentThread.ManagedThreadId != threadId || activeIndex >= 0 && boundaries[activeIndex].Time != time) { Error(true); return; }
            long end = Stopwatch.GetTimestamp();
            if (end < windowStart) Error(); else phaseTicks[(int)phase] += end - windowStart;
            if (activeIndex >= 0) { Boundary boundary = boundaries[activeIndex]; boundary.End = end; boundaries[activeIndex] = boundary; }
            if (phase == Phase.Update) Report.completedUpdateWindows++; else Report.completedFixedWindows++;
            for (int i = 0; i < Slots; i++) if (depths[i] != 0) { Error(true); depths[i] = 0; }
            activePhase = Phase.None; activeIndex = -1;
        }
        public void Fault(string scope, Exception error) { if (ScopePhase(scope) != Phase.None) Error(); }
        public static void Prefix(MethodBase __originalMethod, ref CallState __state)
        {
            FlightIntegratorUpdateAttribution owner = activeOwner;
            if (owner == null || !owner.installed || owner.activeIndex < 0) return;
            if (Thread.CurrentThread.ManagedThreadId != owner.threadId) { owner.Error(true); return; }
            int slot;
            if (__originalMethod.DeclaringType != typeof(FlightIntegrator) || __originalMethod.Module.ModuleVersionId != StockMvid || !owner.slots.TryGetValue(__originalMethod.MetadataToken, out slot)) { owner.Error(); return; }
            __state.Owner = owner; __state.Slot = slot; __state.Window = owner.activeIndex; __state.Phase = owner.activePhase;
            __state.Outermost = owner.depths[slot]++ == 0; __state.Entered = true; __state.Started = Stopwatch.GetTimestamp();
        }
        public static Exception Finalizer(Exception __exception, ref CallState __state)
        {
            if (!__state.Entered) return __exception;
            long end = Stopwatch.GetTimestamp(); __state.Entered = false;
            FlightIntegratorUpdateAttribution owner = __state.Owner;
            if (__exception != null) Interlocked.Increment(ref owner.Report.originalExceptions);
            if (Thread.CurrentThread.ManagedThreadId != owner.threadId || __state.Window != owner.activeIndex || __state.Phase != owner.activePhase ||
                __state.Slot < 0 || __state.Slot >= Slots || owner.depths[__state.Slot] < 1 || end < __state.Started) { owner.Error(true); return __exception; }
            owner.depths[__state.Slot]--;
            long elapsed = end - __state.Started; int at = __state.Window * Slots + __state.Slot;
            try
            {
                owner.accumulators[(int)__state.Phase].Record(__state.Slot, elapsed, __state.Outermost);
                owner.calls[at]++; owner.ticks[at] += elapsed;
                if (__state.Outermost) { owner.outerCalls[at]++; owner.outerTicks[at] += elapsed; }
            }
            catch { owner.Error(); }
            return __exception;
        }
        void Remove()
        {
            if (installed)
                foreach (MethodInfo method in patched)
                {
                    try
                    {
                        Patches graph = Harmony.GetPatchInfo(method);
                        if (graph == null || graph.Owners.Any(owner => owner != Owner) || graph.Prefixes.Count != 1 || graph.Finalizers.Count != 1 || graph.Postfixes.Count != 0 || graph.Transpilers.Count != 0)
                        { Error(); Report.detail = "FlightIntegrator patch graph changed: " + method.Name; }
                    }
                    catch (Exception error) { Error(); Report.detail = "Patch graph validation: " + error.GetType().Name; }
                }
            installed = false;
            try
            {
                if (patched.Count > 0) harmony.UnpatchAll(Owner);
                bool remains = patched.Any(method => { Patches patches = Harmony.GetPatchInfo(method); return patches != null && patches.Owners.Contains(Owner); });
                Report.cleanupStatus = remains ? "cleanup-error" : "removed-owned-patches";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Report.detail = error.GetType().Name + ": " + error.Message; }
            if (ReferenceEquals(activeOwner, this)) activeOwner = null;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (activePhase != Phase.None || depths.Any(depth => depth != 0)) Error(true);
            activePhase = Phase.None; activeIndex = -1; Remove();
            var windows = new Window[windowCount];
            for (int i = 0; i < windowCount; i++)
            {
                Boundary b = boundaries[i];
                var row = new Window { sequence = i, phase = b.Phase == Phase.Update ? "update" : "fixed", frame = b.Frame, timeSeconds = b.Time,
                    startedTicks = b.Start, endedTicks = b.End, elapsedTicks = b.End - b.Start,
                    calls = new int[Slots], outermostCalls = new int[Slots], inclusiveTicks = new long[Slots], outermostInclusiveTicks = new long[Slots] };
                Array.Copy(calls, i * Slots, row.calls, 0, Slots); Array.Copy(outerCalls, i * Slots, row.outermostCalls, 0, Slots);
                Array.Copy(ticks, i * Slots, row.inclusiveTicks, 0, Slots); Array.Copy(outerTicks, i * Slots, row.outermostInclusiveTicks, 0, Slots); windows[i] = row;
            }
            Report.windows = windows;
            for (int phase = 0; phase < 2; phase++)
            {
                CallbackAttributionReport report = phase == 0 ? Report.Update : Report.Fixed;
                if (report == null) continue;
                report.cleanupStatus = Report.cleanupStatus; report.activeWindowTicks = phaseTicks[phase]; report.callbackErrors = Report.callbackErrors;
                report.callbacks = accumulators[phase].Finish(phaseTicks[phase], true);
                foreach (CallbackAttributionRow row in report.callbacks) if (row.calls == 0) row.category += "/no-coverage";
                if (report.status != "unavailable") report.status = Report.callbackErrors != 0 || Report.droppedWindows != 0 || Report.cleanupStatus != "removed-owned-patches" ? "invalid" : report.callbacks.Any(row => row.calls > 0) ? "observed" : "no-coverage";
            }
            if (Report.status != "unavailable")
            {
                bool witness = ValidateWitness(); Report.witnessStatus = witness ? "passed" : "invalid";
                if (!witness) Error();
                Report.status = Report.callbackErrors != 0 || Report.originalExceptions != 0 || Report.droppedWindows != 0 || Report.cleanupStatus != "removed-owned-patches" ? "invalid" :
                    Report.completedUpdateWindows > 0 && Report.completedFixedWindows > 0 ? "observed" : "no-window-coverage";
                foreach (CallbackAttributionReport phase in new[] { Report.Update, Report.Fixed })
                { phase.callbackErrors = Report.callbackErrors; if (Report.status == "invalid") phase.status = "invalid"; }
            }
        }
        public bool IsValidFinal(out string reason) { return ValidateFinal(Report, out reason); }
        public static bool ValidateFinal(ReportData report, out string reason)
        {
            reason = null;
            if (report == null) reason = "missing-report";
            else if (report.status != "observed") reason = "status-" + report.status;
            else if (report.cleanupStatus != "removed-owned-patches") reason = "owned-patches-not-removed";
            else if (report.patchedMethods != Slots || report.callbackErrors != 0 || report.contextErrors != 0 || report.originalExceptions != 0 || report.droppedWindows != 0) reason = "observer-errors-or-incomplete-coverage";
            else if (report.witnessStatus != "passed") reason = "window-aggregate-witness-" + report.witnessStatus;
            else if (report.completedUpdateWindows <= 0 || report.completedFixedWindows <= 0) reason = "missing-phase-windows";
            else if (report.Update == null || report.Fixed == null || report.Update.activeWindowTicks <= 0 || report.Fixed.activeWindowTicks <= 0 || report.Update.callbacks == null || report.Fixed.callbacks == null || report.Update.callbacks.Length != Slots || report.Fixed.callbacks.Length != Slots) reason = "missing-phase-denominator-or-rows";
            else if (report.Update.callbacks.Any(row => row.calls <= 0)) reason = "update-seam-no-execution-coverage";
            return reason == null;
        }
        bool ValidateWitness()
        {
            if (Report.patchedMethods != Slots || Report.seamOrder == null || Report.seamOrder.Length != Slots || Report.droppedWindows != 0) return false;
            for (int phase = 0; phase < 2; phase++)
            {
                long denominator = 0; int count = 0;
                var callTotals = new int[Slots]; var outerCallTotals = new int[Slots]; var tickTotals = new long[Slots]; var outerTickTotals = new long[Slots];
                foreach (Window window in Report.windows)
                {
                    if (window.endedTicks < window.startedTicks || window.elapsedTicks < 0) return false;
                    if (window.phase != (phase == 0 ? "update" : "fixed")) continue;
                    denominator += window.elapsedTicks; count++;
                    for (int slot = 0; slot < Slots; slot++)
                    {
                        if (window.outermostCalls[slot] > window.calls[slot] || window.outermostInclusiveTicks[slot] > window.inclusiveTicks[slot] || window.outermostInclusiveTicks[slot] > window.elapsedTicks) return false;
                        callTotals[slot] += window.calls[slot]; outerCallTotals[slot] += window.outermostCalls[slot]; tickTotals[slot] += window.inclusiveTicks[slot]; outerTickTotals[slot] += window.outermostInclusiveTicks[slot];
                    }
                }
                CallbackAttributionReport report = phase == 0 ? Report.Update : Report.Fixed;
                if (denominator != phaseTicks[phase] || count != (phase == 0 ? Report.completedUpdateWindows : Report.completedFixedWindows) || report.callbacks.Length != Slots) return false;
                foreach (CallbackAttributionRow row in report.callbacks)
                {
                    int slot; if (!slots.TryGetValue(row.metadataToken, out slot) || row.calls != callTotals[slot] || row.outermostCalls != outerCallTotals[slot] || row.inclusiveTicks != tickTotals[slot] || row.outermostInclusiveTicks != outerTickTotals[slot]) return false;
                }
            }
            return true;
        }
    }
}
