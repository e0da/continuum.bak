using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class FixedStepOwnerAttribution : IDisposable, IPlayerLoopBracketObserver
    {
        const string Owner = "continuum.fixed-step-owner-attribution";
        const string StockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const string MechSha = "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929";
        static readonly Guid StockMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf"), MechMvid = new Guid("30fbe3c4-e77e-4abc-895b-a635d5924a08");
        const int Slots = 6, MaxWindows = 1024, MaxDepth = 64, MaxStates = 256;
        static readonly string Phase = typeof(UnityEngine.PlayerLoop.FixedUpdate.ScriptRunBehaviourFixedUpdate).FullName;
        static FixedStepOwnerAttribution active;
        static readonly MethodInfo PrefixFi = AccessTools.DeclaredMethod(typeof(FixedStepOwnerAttribution), nameof(FiPrefix));
        static readonly MethodInfo PrefixFeed = AccessTools.DeclaredMethod(typeof(FixedStepOwnerAttribution), nameof(FeedPrefix));
        static readonly MethodInfo PrefixAnalysis = AccessTools.DeclaredMethod(typeof(FixedStepOwnerAttribution), nameof(AnalysisPrefix));
        static readonly MethodInfo End = AccessTools.DeclaredMethod(typeof(FixedStepOwnerAttribution), nameof(Finalizer));

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-fixed-step-owner-attribution/v1", runId, vesselId, status = "installing", cleanupStatus = "not-installed", detail;
            public string phase = Phase, clock = "System.Diagnostics.Stopwatch monotonic ticks", overheadStatus = "unqualified-intrusive-observer";
            public string scope = "Six declared bulk original owners only: active-vessel FI mass/drag-setup/thermodynamics, Vessel input feed, and pinned MechJeb VesselState Update/AnalyzeParts for actual active-vessel core-owned state objects. Only main-thread entries inside ScriptRunBehaviourFixedUpdate windows whose Before sees captureActive are sampled. No FI.FixedUpdate, recursive Integrate, per-part/module, curve-evaluation or controller hooks. Existing AeroStepContext supplies a separate FI/root envelope; these child rows do not replace that parent clock. Outside/straddling windows must be filtered by exact timestamps. Zero calls is no execution coverage, not zero cost.";
            public string timingScope = "Inclusive wall body brackets preserve original returns/order/exceptions and include uninstrumented/native descendants plus observer dispatch. Self removes only directly observed child brackets, including recursion; rows are not exclusive CPU. Sum of self is bounded by its phase window. MJ inside/outside-feed counters classify the actually observed nesting, not Drive ownership or duplicated analysis. All states/core identities are retained; aggregate methods can contain multiple states. Phase clocks enclose original callbacks but exclude Before/After preparation, whose costs are recorded separately. No FPS or removable-time claim.";
            public string ownershipScope = "Reserved frozen research ownership: exact assembly SHA/MVID, method token/signature/body, empty preinstall graphs, exact owned prefix/finalizer graphs, complete teardown back to the original graphs. Pin/file/owner state is checked at install and cleanup; no per-call reflection/hash/patch-graph scan. Transient foreign mutation between endpoint audits is not proven absent. Unknown active-vessel state/owner, foreign hooks, original exceptions, stack/clock drift or bounds overflow invalidate qualification.";
            public bool requested = true;
            public int processId, mainThreadId, targetCount, patchedMethods, maximumWindows = MaxWindows, maximumNestedFrames = MaxDepth;
            public int errors, ownershipErrors, contextErrors, originalExceptions, droppedWindows, startedWindows, completedWindows, observedTargets;
            public long clockFrequency = Stopwatch.Frequency, capturedCalls, ignoredForeignInstances, admissionStartedTicks, admissionEndedTicks;
            public long storageStartedTicks, storageEndedTicks, storagePayloadBytes, beforeTicks, afterTicks, cleanupStartedTicks, cleanupEndedTicks, exportStartedTicks, exportEndedTicks;
            public string assemblySha256 = StockSha, assemblyMvid = StockMvid.ToString("D"), mechJebSha256, mechJebMvid;
            public Target[] targets = new Target[0];
            public StateOwner[] stateOwners = new StateOwner[0];
            public Window[] windows = new Window[0];
            public string[] preinstallPatchGraph = new string[0], installedPatchGraph = new string[0], precleanupPatchGraph = new string[0], restoredPatchGraph = new string[0];
        }
        [Serializable] public sealed class Target
        {
            public int slot, metadataToken;
            public string assembly, assemblySha256, assemblyMvid, declaringType, method, ilSha256, ownerScope;
        }
        [Serializable] public sealed class StateOwner
        {
            public int slot, coreInstanceId;
            public uint partFlightId;
            public string partName, coreType, stateType;
            public long updateCalls, analyzeCalls, insideFeedUpdateCalls, insideFeedAnalyzeCalls;
        }
        [Serializable] public sealed class Window
        {
            public int sequence, frame;
            public double fixedTimeSeconds;
            public long startedTicks, endedTicks, elapsedTicks;
            public bool captureActiveAtEnd;
            public Entry[] entries;
        }
        [Serializable] public sealed class Entry
        {
            public int slot, calls;
            public long inclusiveTicks, selfTicks, insideFeedCalls, insideFeedTicks, outsideFeedCalls, outsideFeedTicks;
        }
        struct Boundary
        { internal int Frame; internal double Time; internal long Start, End; internal bool CaptureAtEnd; }
        struct StackFrame
        { internal int Slot, Window, State; internal long Id, Start, Child; internal bool InsideFeed; }
        public struct CallState
        { internal FixedStepOwnerAttribution owner; internal long id; internal int depth, window; internal bool entered; }
        sealed class Binding
        { internal PartModule core; internal object state; internal StateOwner report; }
        sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }

        readonly Harmony harmony = new Harmony(Owner);
        readonly Func<bool> capturing;
        readonly Vessel vessel;
        readonly FlightIntegrator integrator;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly MethodInfo[] methods = new MethodInfo[Slots];
        readonly Dictionary<object, Binding> states = new Dictionary<object, Binding>(new References());
        readonly List<Binding> stateBindings = new List<Binding>();
        readonly Dictionary<Assembly, string> assemblyHashes = new Dictionary<Assembly, string>();
        FieldInfo stateField;
        Type coreType;
        Boundary[] boundaries;
        StackFrame[] stack;
        int[] calls;
        long[] inclusive, self, insideCalls, insideTicks, outsideCalls, outsideTicks;
        long serial;
        int windowCount, activeWindow = -1, depth, feedDepth;
        bool installed, disposed, faulted;
        public ReportData Report { get; private set; }

        FixedStepOwnerAttribution(string runId, Func<bool> capturing)
        {
            this.capturing = capturing; vessel = FlightGlobals.ActiveVessel; integrator = FlightIntegrator.ActiveVesselFI;
            Report = new ReportData { runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id,
                mainThreadId = thread, admissionStartedTicks = Stopwatch.GetTimestamp() };
            try
            {
                Require(active == null && integrator != null && integrator.GetType() == typeof(FlightIntegrator) &&
                    ReferenceEquals(AccessTools.Field(typeof(VesselModule), "vessel").GetValue(integrator), vessel), "Active declared FlightIntegrator owner differs.");
                Require(!Harmony.HasAnyPatches("continuum.fixed-callback-attribution") && !Harmony.HasAnyPatches("continuum.update-callback-attribution") &&
                    !Harmony.HasAnyPatches("continuum.aero-integration-attribution") && !Harmony.HasAnyPatches("continuum.fixed-callback-attribution.input") &&
                    !Harmony.HasAnyPatches("continuum.mechjeb-drive-attribution"), "Overlapping broad/input/aero observers are installed.");
                StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);
                methods[0] = Pin(typeof(FlightIntegrator), "UpdateMassStats", 100677455, typeof(void), Type.EmptyTypes, StockMvid);
                methods[1] = Pin(typeof(FlightIntegrator), "DragCubeSetupAndPartAeroStats", 100677504, typeof(void), new[] { typeof(Vessel) }, StockMvid);
                methods[2] = Pin(typeof(FlightIntegrator), "UpdateThermodynamics", 100677477, typeof(void), Type.EmptyTypes, StockMvid);
                methods[3] = Pin(typeof(Vessel), "FeedInputFeed", 100677715, typeof(void), Type.EmptyTypes, StockMvid);
                BindMechJeb();
                Report.targets = methods.Select((method, slot) => new Target { slot = slot, metadataToken = method.MetadataToken,
                    assembly = method.Module.Assembly.GetName().Name, assemblySha256 = DigestAssembly(method.Module.Assembly), assemblyMvid = method.Module.ModuleVersionId.ToString("D"),
                    declaringType = method.DeclaringType.FullName, method = method.Name, ilSha256 = Hash(method.GetMethodBody().GetILAsByteArray()),
                    ownerScope = slot < 3 ? "exact-active-flight-integrator" : slot == 3 ? "exact-active-vessel" : "bound-readonly-core-state" }).ToArray();
                Report.targetCount = Slots;
                foreach (MethodInfo method in methods) RequireEmpty(method);
                Report.preinstallPatchGraph = GraphEvidence();
                Report.storageStartedTicks = Stopwatch.GetTimestamp();
                boundaries = new Boundary[MaxWindows]; stack = new StackFrame[MaxDepth]; calls = new int[MaxWindows * Slots];
                inclusive = new long[MaxWindows * Slots]; self = new long[MaxWindows * Slots]; insideCalls = new long[MaxWindows * Slots];
                insideTicks = new long[MaxWindows * Slots]; outsideCalls = new long[MaxWindows * Slots]; outsideTicks = new long[MaxWindows * Slots];
                Report.storagePayloadBytes = (long)MaxWindows * (4 + 8 + 8 + 8 + 1) + (long)MaxDepth * (4 + 4 + 4 + 8 + 8 + 8 + 1) +
                    (long)MaxWindows * Slots * (4 + 6 * 8);
                Report.storageEndedTicks = Stopwatch.GetTimestamp(); active = this;
                for (int slot = 0; slot < Slots; slot++)
                {
                    MethodInfo before = slot < 3 ? PrefixFi : slot == 3 ? PrefixFeed : PrefixAnalysis;
                    harmony.Patch(methods[slot], prefix: new HarmonyMethod(before), finalizer: new HarmonyMethod(End));
                    Report.patchedMethods++;
                }
                RequireOwned(); Report.installedPatchGraph = GraphEvidence(); installed = true;
                Report.status = "installed"; Report.cleanupStatus = "installed";
            }
            catch (Exception error) { Fail("install", error); Report.status = "unavailable"; Remove(); }
            finally { Report.admissionEndedTicks = Stopwatch.GetTimestamp(); }
        }
        public static FixedStepOwnerAttribution Create(string[] arguments, string runId, Func<bool> capturing)
        {
            string request = Environment.GetEnvironmentVariable("CONTINUUM_FIXED_STEP_OWNER_ATTRIBUTION");
            if (string.IsNullOrEmpty(request)) return null;
            Require(request == "1" && !string.IsNullOrEmpty(runId) && capturing != null && FlightGlobals.ActiveVessel != null &&
                FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed, "Fixed owner attribution requires one bound active loaded flight.");
            return new FixedStepOwnerAttribution(runId, capturing);
        }
        public static bool Requested(string[] arguments)
        { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_FIXED_STEP_OWNER_ATTRIBUTION")); }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        string DigestAssembly(Assembly assembly)
        {
            string digest; if (assemblyHashes.TryGetValue(assembly, out digest)) return digest;
            Require(!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location), "Owner assembly has no file identity.");
            var before = new FileInfo(assembly.Location); long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
            digest = Hash(File.ReadAllBytes(assembly.Location)); before.Refresh();
            Require(before.Length == length && before.LastWriteTimeUtc == modified, "Owner assembly changed during pinning.");
            assemblyHashes.Add(assembly, digest); return digest;
        }
        static MethodInfo Pin(Type type, string name, int token, Type result, Type[] parameters, Guid mvid, string il = null)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
            Require(method != null && method.DeclaringType == type && !method.IsStatic && !method.IsAbstract && !method.ContainsGenericParameters &&
                method.ReturnType == result && method.Module.ModuleVersionId == mvid && method.MetadataToken == token && method.GetMethodBody() != null &&
                (il == null || Hash(method.GetMethodBody().GetILAsByteArray()) == il), "Fixed owner method pin differs: " + type.FullName + "." + name);
            return method;
        }
        void BindMechJeb()
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(value => value.GetName().Name == "MechJeb2");
            Require(assembly != null && assembly.ManifestModule.ModuleVersionId == MechMvid && DigestAssembly(assembly) == MechSha, "Pinned MechJeb owner differs.");
            Report.mechJebSha256 = MechSha; Report.mechJebMvid = MechMvid.ToString("D");
            coreType = assembly.GetType("MuMech.MechJebCore", true); Type stateType = assembly.GetType("MuMech.VesselState", true);
            stateField = AccessTools.DeclaredField(coreType, "VesselState");
            Require(stateField != null && stateField.IsInitOnly && stateField.FieldType == stateType && !stateField.IsStatic, "Readonly core state ownership differs.");
            methods[4] = Pin(stateType, "Update", 100665296, typeof(bool), new[] { typeof(Vessel) }, MechMvid,
                "2f3a6304100e735164c51fc9ea5f64c541e5f301f173bdf4552d5d9136cfa6c9");
            methods[5] = Pin(stateType, "AnalyzeParts", 100665304, typeof(void), new[] { typeof(Vessel),
                stateType.GetNestedType("EngineInfo", BindingFlags.Public | BindingFlags.NonPublic), stateType.GetNestedType("IntakeInfo", BindingFlags.Public | BindingFlags.NonPublic) }, MechMvid,
                "4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b");
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                if (module.GetType() != coreType) continue;
                object state = stateField.GetValue(module);
                Require(stateBindings.Count < MaxStates && state != null && state.GetType() == stateType && !states.ContainsKey(state), "Bound MechJeb state capacity/identity differs.");
                var row = new StateOwner { slot = stateBindings.Count, coreInstanceId = module.GetInstanceID(), partFlightId = part.flightID,
                    partName = part.name, coreType = coreType.FullName, stateType = stateType.FullName };
                var binding = new Binding { core = module, state = state, report = row }; states.Add(state, binding); stateBindings.Add(binding);
            }
            Require(stateBindings.Count > 0, "No actual active-vessel MechJeb state owner.");
            Report.stateOwners = stateBindings.Select(binding => binding.report).ToArray();
        }
        static void RequireEmpty(MethodBase method)
        {
            Patches graph = Harmony.GetPatchInfo(method);
            Require(graph == null || graph.Prefixes.Count + graph.Finalizers.Count + graph.Transpilers.Count + graph.Postfixes.Count == 0,
                "Unexpected fixed owner patch: " + method.DeclaringType.FullName + "." + method.Name);
        }
        void RequireOwned()
        {
            for (int slot = 0; slot < Slots; slot++)
            {
                Patches graph = Harmony.GetPatchInfo(methods[slot]); MethodInfo prefix = slot < 3 ? PrefixFi : slot == 3 ? PrefixFeed : PrefixAnalysis;
                Require(graph != null && graph.Prefixes.Count == 1 && graph.Finalizers.Count == 1 && graph.Transpilers.Count == 0 && graph.Postfixes.Count == 0 &&
                    ExactPatch(graph.Prefixes[0], prefix) && ExactPatch(graph.Finalizers[0], End), "Fixed owner installed graph differs: " + methods[slot].Name);
            }
        }
        void RequireStateOwners()
        {
            int count = 0;
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                if (module.GetType() != coreType) continue;
                Binding binding; object state = stateField.GetValue(module);
                Require(state != null && states.TryGetValue(state, out binding) && ReferenceEquals(binding.core, module), "Actual MechJeb owner census changed.");
                count++;
            }
            Require(count == stateBindings.Count, "Actual MechJeb owner count changed.");
            foreach (Binding binding in stateBindings) Require(binding.core != null && binding.core.vessel == vessel && binding.core.GetType() == coreType &&
                binding.core.GetInstanceID() == binding.report.coreInstanceId && binding.core.part != null && binding.core.part.flightID == binding.report.partFlightId &&
                ReferenceEquals(stateField.GetValue(binding.core), binding.state), "Readonly state owner changed during capture.");
        }
        static bool ExactPatch(Patch patch, MethodInfo method)
        { return patch.owner == Owner && patch.priority == Priority.Normal && patch.before.Length == 0 && patch.after.Length == 0 && FlightIntegratorUpdateAttribution.SameMethod(patch.PatchMethod, method); }
        string[] GraphEvidence()
        { return methods.Where(method => method != null).SelectMany(method => FlightIntegratorUpdateAttribution.DescribeGraph(method, Harmony.GetPatchInfo(method))).ToArray(); }
        int Slot(MethodBase method)
        {
            for (int slot = 0; slot < Slots; slot++) if (FlightIntegratorUpdateAttribution.SameMethod(methods[slot], method)) return slot;
            throw new InvalidOperationException("Unknown fixed owner callback identity.");
        }
        public void Before(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Phase || !installed || faulted || !capturing()) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                Require(Thread.CurrentThread.ManagedThreadId == thread && activeWindow == -1 && depth == 0 && feedDepth == 0 &&
                    !double.IsNaN(fixedTimeSeconds) && !double.IsInfinity(fixedTimeSeconds) && FlightGlobals.ActiveVessel == vessel && vessel.loaded && !vessel.packed &&
                    ReferenceEquals(FlightIntegrator.ActiveVesselFI, integrator), "Fixed phase context/active owner changed.");
                foreach (Binding binding in stateBindings) Require(binding.core != null && binding.core.vessel == vessel, "Bound analysis core moved/died.");
                if (windowCount == MaxWindows) { Report.droppedWindows++; throw new InvalidOperationException("Fixed phase window bound exceeded."); }
                activeWindow = windowCount++; Report.startedWindows++;
                boundaries[activeWindow] = new Boundary { Frame = frame, Time = fixedTimeSeconds, Start = Stopwatch.GetTimestamp() };
            }
            catch (Exception error) { Report.contextErrors++; Fail("Before", error); }
            finally { Report.beforeTicks += Stopwatch.GetTimestamp() - start; }
        }
        public void After(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Phase || activeWindow < 0) return;
            long end = Stopwatch.GetTimestamp();
            try
            {
                Boundary boundary = boundaries[activeWindow];
                Require(Thread.CurrentThread.ManagedThreadId == thread && boundary.Frame == frame && boundary.Time == fixedTimeSeconds && depth == 0 && feedDepth == 0 && end >= boundary.Start,
                    "Fixed phase stack/frame/time drift.");
                boundary.End = end; boundary.CaptureAtEnd = capturing(); boundaries[activeWindow] = boundary; Report.completedWindows++;
            }
            catch (Exception error) { Report.contextErrors++; Fail("After", error); }
            finally { activeWindow = -1; depth = feedDepth = 0; Report.afterTicks += Stopwatch.GetTimestamp() - end; }
        }
        public void Fault(string scope, Exception error)
        { if (scope == Phase) { Report.contextErrors++; Fail("PlayerLoop", error); } }
        bool Sampling { get { return installed && !faulted && activeWindow >= 0; } }
        void Enter(int slot, int state, ref CallState sample)
        {
            if (!Sampling) return;
            try
            {
                Require(Thread.CurrentThread.ManagedThreadId == thread && depth < MaxDepth, "Fixed owner thread/stack bound differs.");
                long start = Stopwatch.GetTimestamp(); long id = checked(++serial);
                stack[depth] = new StackFrame { Id = id, Window = activeWindow, Slot = slot, State = state, Start = start, InsideFeed = feedDepth > 0 };
                sample.owner = this; sample.id = id; sample.depth = depth; sample.window = activeWindow; sample.entered = true; depth++;
                if (slot == 3) feedDepth++;
            }
            catch (Exception error) { Report.contextErrors++; Fail("prefix", error); }
        }
        static void FiPrefix(FlightIntegrator __instance, MethodBase __originalMethod, ref CallState __state)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            if (!ReferenceEquals(__instance, owner.integrator)) { owner.Report.ignoredForeignInstances++; return; }
            try { owner.Enter(owner.Slot(__originalMethod), -1, ref __state); }
            catch (Exception error) { owner.Report.contextErrors++; owner.Fail("FI prefix", error); }
        }
        static void FeedPrefix(Vessel __instance, ref CallState __state)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            if (!ReferenceEquals(__instance, owner.vessel)) { owner.Report.ignoredForeignInstances++; return; }
            owner.Enter(3, -1, ref __state);
        }
        static void AnalysisPrefix(object __instance, Vessel __0, MethodBase __originalMethod, ref CallState __state)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            if (!ReferenceEquals(__0, owner.vessel)) { owner.Report.ignoredForeignInstances++; return; }
            try
            {
                Binding binding;
                Require(owner.states.TryGetValue(__instance, out binding) && binding.core != null && binding.core.vessel == owner.vessel, "Unbound active-vessel analysis state.");
                owner.Enter(owner.Slot(__originalMethod), binding.report.slot, ref __state);
            }
            catch (Exception error) { owner.Report.ownershipErrors++; owner.Fail("analysis prefix", error); }
        }
        static Exception Finalizer(Exception __exception, CallState __state)
        {
            if (!__state.entered) return __exception;
            FixedStepOwnerAttribution owner = __state.owner; long end = Stopwatch.GetTimestamp();
            try
            {
                Require(owner != null && Thread.CurrentThread.ManagedThreadId == owner.thread && owner.activeWindow == __state.window && owner.depth == __state.depth + 1,
                    "Fixed owner finalizer context differs.");
                StackFrame frame = owner.stack[__state.depth];
                Require(frame.Id == __state.id && frame.Window == __state.window && end >= frame.Start, "Fixed owner finalizer identity/clock differs.");
                long elapsed = end - frame.Start; Require(frame.Child >= 0 && frame.Child <= elapsed, "Fixed owner child wall exceeds parent.");
                int cell = frame.Window * Slots + frame.Slot;
                owner.calls[cell] = checked(owner.calls[cell] + 1); owner.inclusive[cell] = checked(owner.inclusive[cell] + elapsed);
                owner.self[cell] = checked(owner.self[cell] + elapsed - frame.Child); owner.Report.capturedCalls = checked(owner.Report.capturedCalls + 1);
                if (frame.Slot >= 4)
                {
                    if (frame.InsideFeed) { owner.insideCalls[cell]++; owner.insideTicks[cell] += elapsed; }
                    else { owner.outsideCalls[cell]++; owner.outsideTicks[cell] += elapsed; }
                    StateOwner state = owner.Report.stateOwners[frame.State];
                    if (frame.Slot == 4) { state.updateCalls++; if (frame.InsideFeed) state.insideFeedUpdateCalls++; }
                    else { state.analyzeCalls++; if (frame.InsideFeed) state.insideFeedAnalyzeCalls++; }
                }
                if (__exception != null) owner.Report.originalExceptions++;
                if (frame.Slot == 3) { Require(owner.feedDepth > 0, "Fixed input depth underflow."); owner.feedDepth--; }
                owner.depth--; if (owner.depth > 0) owner.stack[owner.depth - 1].Child = checked(owner.stack[owner.depth - 1].Child + elapsed);
            }
            catch (Exception error) { if (owner != null) { owner.Report.contextErrors++; owner.Fail("finalizer", error); } }
            return __exception;
        }
        void Fail(string context, Exception error)
        { Report.errors++; faulted = true; if (Report.detail == null) Report.detail = context + ": " + (error == null ? "unknown observer fault" : error.ToString()); }
        void Remove()
        {
            installed = false;
            try
            {
                if (ReferenceEquals(active, this)) harmony.UnpatchAll(Owner);
                foreach (MethodInfo method in methods.Where(method => method != null))
                {
                    Patches graph = Harmony.GetPatchInfo(method); Require(graph == null || !graph.Owners.Contains(Owner), "Owned fixed observer patches remain.");
                }
                Report.cleanupStatus = "removed-owned-patches";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Fail("unpatch", error); }
            finally { if (ReferenceEquals(active, this)) active = null; }
        }
        public void Dispose()
        {
            if (disposed) return; Report.cleanupStartedTicks = Stopwatch.GetTimestamp();
            try
            {
                Require(activeWindow == -1 && depth == 0 && feedDepth == 0, "Fixed observer released during a phase/call.");
                if (installed)
                {
                    RequireOwned(); Report.precleanupPatchGraph = GraphEvidence();
                    Require(Report.installedPatchGraph.SequenceEqual(Report.precleanupPatchGraph), "Fixed graph changed during capture.");
                    foreach (Target row in Report.targets)
                    {
                        MethodInfo method = methods[row.slot];
                        Require(Hash(File.ReadAllBytes(method.Module.Assembly.Location)) == row.assemblySha256 && Hash(method.GetMethodBody().GetILAsByteArray()) == row.ilSha256,
                            "Fixed owner assembly/body changed.");
                    }
                    RequireStateOwners();
                }
            }
            catch (Exception error) { Report.ownershipErrors++; Fail("cleanup admission", error); }
            Remove();
            try
            {
                Report.restoredPatchGraph = GraphEvidence();
                if (Report.preinstallPatchGraph.Length > 0) Require(Report.preinstallPatchGraph.SequenceEqual(Report.restoredPatchGraph), "Fixed original graphs were not restored.");
            }
            catch (Exception error) { Report.ownershipErrors++; Report.cleanupStatus = "cleanup-error"; Fail("restoration", error); }
            Report.cleanupEndedTicks = Stopwatch.GetTimestamp(); Report.exportStartedTicks = Stopwatch.GetTimestamp();
            var exported = new Window[windowCount]; var reached = new bool[Slots];
            for (int window = 0; window < windowCount; window++)
            {
                Boundary boundary = boundaries[window]; var entries = new Entry[Slots];
                for (int slot = 0; slot < Slots; slot++)
                {
                    int cell = window * Slots + slot; if (calls[cell] > 0) reached[slot] = true;
                    entries[slot] = new Entry { slot = slot, calls = calls[cell], inclusiveTicks = inclusive[cell], selfTicks = self[cell],
                        insideFeedCalls = insideCalls[cell], insideFeedTicks = insideTicks[cell], outsideFeedCalls = outsideCalls[cell], outsideFeedTicks = outsideTicks[cell] };
                }
                exported[window] = new Window { sequence = window, frame = boundary.Frame, fixedTimeSeconds = boundary.Time, startedTicks = boundary.Start, endedTicks = boundary.End,
                    elapsedTicks = boundary.End - boundary.Start, captureActiveAtEnd = boundary.CaptureAtEnd, entries = entries };
            }
            Report.windows = exported; Report.observedTargets = reached.Count(value => value); Report.exportEndedTicks = Stopwatch.GetTimestamp(); disposed = true;
            string reason; Report.status = IsValidFinal(out reason) ? "observed" : "invalid"; if (Report.detail == null && reason != null) Report.detail = reason;
        }
        public bool IsValidFinal(out string reason)
        {
            reason = null;
            if (!disposed || Report.cleanupStatus != "removed-owned-patches" || Report.errors != 0 || Report.ownershipErrors != 0 || Report.contextErrors != 0 || Report.originalExceptions != 0 ||
                Report.droppedWindows != 0 || Report.targetCount != Slots || Report.patchedMethods != Slots || Report.startedWindows != Report.completedWindows || Report.completedWindows == 0 ||
                Report.observedTargets != Slots || Report.windows.Length != Report.completedWindows || Report.preinstallPatchGraph.Length == 0 || Report.installedPatchGraph.Length == 0 ||
                !Report.installedPatchGraph.SequenceEqual(Report.precleanupPatchGraph) || !Report.preinstallPatchGraph.SequenceEqual(Report.restoredPatchGraph))
            { reason = "Fixed owner installation, execution coverage or exact cleanup incomplete."; return false; }
            long callTotal = 0; long[] updates = new long[2];
            for (int window = 0; window < Report.windows.Length; window++)
            {
                Window row = Report.windows[window]; long selfSum = 0;
                if (row.sequence != window || row.startedTicks <= 0 || row.endedTicks < row.startedTicks || row.elapsedTicks != row.endedTicks - row.startedTicks || !row.captureActiveAtEnd ||
                    double.IsNaN(row.fixedTimeSeconds) || double.IsInfinity(row.fixedTimeSeconds) || row.entries == null || row.entries.Length != Slots ||
                    window > 0 && Report.windows[window - 1].endedTicks > row.startedTicks) { reason = "Fixed phase clock/enclosure failed."; return false; }
                for (int slot = 0; slot < Slots; slot++)
                {
                    Entry entry = row.entries[slot];
                    if (entry.slot != slot || entry.calls < 0 || entry.inclusiveTicks < 0 || entry.selfTicks < 0 || entry.selfTicks > entry.inclusiveTicks ||
                        entry.inclusiveTicks > row.elapsedTicks * entry.calls || entry.calls == 0 && (entry.inclusiveTicks != 0 || entry.selfTicks != 0) ||
                        slot >= 4 && (entry.insideFeedCalls + entry.outsideFeedCalls != entry.calls || entry.insideFeedTicks + entry.outsideFeedTicks != entry.inclusiveTicks) ||
                        slot < 4 && entry.insideFeedCalls + entry.outsideFeedCalls + entry.insideFeedTicks + entry.outsideFeedTicks != 0)
                    { reason = "Fixed row counters/body clocks failed."; return false; }
                    callTotal += entry.calls; selfSum += entry.selfTicks; if (slot >= 4) updates[slot - 4] += entry.calls;
                }
                if (selfSum > row.elapsedTicks) { reason = "Fixed observed self costs exceed phase wall."; return false; }
            }
            if (callTotal != Report.capturedCalls || updates[0] != Report.stateOwners.Sum(state => state.updateCalls) || updates[1] != Report.stateOwners.Sum(state => state.analyzeCalls))
            { reason = "Fixed state-instance counters differ from windows."; return false; }
            return true;
        }
    }
}
