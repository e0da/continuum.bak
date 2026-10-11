using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class AeroIntegrationAttribution : IDisposable
    {
        const string Owner = "continuum.aero-integration-attribution";
        const string StockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int MaxWindows = 4096, CensusWindows = 4, MaxCurveObjects = 4096, MaxReuseRows = 32768, MaxCensusCalls = 262144, Slots = 11;
        static readonly MethodInfo Fixed = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "FixedUpdate", Type.EmptyTypes);
        static readonly MethodInfo Integrate = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "Integrate", new[] { typeof(Part) });
        static readonly MethodInfo Aero = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateAerodynamics", new[] { typeof(Part) });
        static readonly MethodInfo Faces = AccessTools.DeclaredMethod(typeof(DragCubeList), "AddSurfaceDragDirection", new[] { typeof(Vector3), typeof(float), typeof(DragCubeList.CubeData).MakeByRefType() });
        static readonly MethodInfo Surface = AccessTools.DeclaredMethod(typeof(PhysicsGlobals), "DragCurveValue", new[] { typeof(PhysicsGlobals.SurfaceCurvesList), typeof(float), typeof(float) });
        static readonly MethodInfo SetDrag = AccessTools.DeclaredMethod(typeof(DragCubeList), "SetDrag", new[] { typeof(Vector3), typeof(float) });
        static readonly MethodInfo Evaluate = AccessTools.DeclaredMethod(typeof(FloatCurve), "Evaluate", new[] { typeof(float) });
        static readonly MethodInfo Drag = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "ApplyAeroDrag", new[] { typeof(Part), typeof(Rigidbody), typeof(ForceMode) });
        static readonly MethodInfo Lift = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "ApplyAeroLift", new[] { typeof(Part), typeof(Rigidbody), typeof(ForceMode) });
        static readonly MethodInfo BeginMethod = AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "Begin");
        static readonly MethodInfo EndMethod = AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "End");
        static AeroIntegrationAttribution active;
        [ThreadStatic] static Context current;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Vessel vessel;
        readonly Func<bool> capturing;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly Window[] storage = new Window[MaxWindows];
        readonly Dictionary<object, int> curveObjects = new Dictionary<object, int>(ReferenceComparer.Instance);
        readonly Dictionary<ReuseKey, CurveReuseRow> reuse = new Dictionary<ReuseKey, CurveReuseRow>();
        readonly MethodInfo[] pinned = { Fixed, Integrate, Aero, Faces, Surface, SetDrag, Evaluate, Drag, Lift };
        int count, censusCalls;
        bool disposed, installationComplete, cleanupInProgress;

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-aero-integration-attribution/v1", status = "installed", cleanupStatus = "installed", detail, runId, vesselId, coverageWitness = "not-observed", censusStatus = "not-observed";
            public string assemblySha256 = StockSha, assemblyMvid = ConvectionSparseKernel.Mvid.ToString("D"), observerOverheadStatus = "unqualified-intrusive-observer";
            public string pinScope = "Exact private original assembly SHA/MVID plus declared method/signature/token; original method-body hashes retained before owned instrumentation.";
            public string scope = "Active-vessel original root Integrate callsite inside declared FI.FixedUpdate. Recursive Integrate has no prefix/finalizer. Named rows time direct original callsites, retain original operands/returns/order/exceptions, and include native work reached by those calls. Nested rows overlap and are not additive or exclusive CPU; uninstrumented inline arithmetic, other branches/providers and argument preparation are not attributed.";
            public string windowScope = "Select only completed root windows fully enclosed by Probe capture timestamps. Filter fixedStartedTicks/fixedEndedTicks independently for FixedUpdate inclusive means; those envelopes include other FI work and prefix/admission tax. Root Integrate envelopes contain callsite/census tax; seam admission is also retained separately. No full-frame benchmark or removable-time claim.";
            public string censusScope = "First four captured root invocations only: FloatCurve wrapper and underlying AnimationCurve reference identities plus exact float input bits at reached original Evaluate callsites. Generation is root-window sequence. References/input reuse does not prove immutable curve keys or permit a live cache. No extra Evaluate, curve key reads or force/state writes.";
            public string shapeScope = "Five unique first method admissions during installation. Owned cleanup may recompose an already admitted method while removing prefix/finalizer patches; every such execution must retain the exact pinned method body and ordered original callsite shape. Reapplication outside owned cleanup is invalid.";
            public int processId, patchedMethods, shapeAdmissions, transpilerExecutions, cleanupRecompositions, errors, contextErrors, originalExceptions, droppedWindows, droppedCensusCalls, completedWindows, censusWindows;
            public long clockFrequency = Stopwatch.Frequency, timerReadFloorTicks, admissionStartedTicks, admissionEndedTicks, censusTicks;
            public string[] rowOrder = { "UpdateAerodynamics-call", "SetDrag-call", "aero-kinematic-access-calls", "aero-area-drag-scalar-methods", "ApplyAeroDrag-call", "ApplyAeroLift-call", "integration-rigidbody-force-torque-calls", "aero-angular-drag-write-calls", "mach-curve-evaluate-calls", "direction-or-weight-curve-evaluate-calls", "six-face-Pow-call" };
            public MethodRow[] methods;
            public Window[] windows = new Window[0];
            public CurveReuseRow[] curveReuse = new CurveReuseRow[0];
        }
        [Serializable] public sealed class MethodRow
        {
            public string declaringType, method, assemblyMvid, ilSha256;
            public int metadataToken, instructionCount, shapeApplications, cleanupApplications;
            public bool patched;
            public string[] instrumentedCallsiteMethods = new string[0];
            public int[] callsiteSlots = new int[0];
        }
        [Serializable] public sealed class Window
        {
            public int sequence, frame, parts, sixFaceCalls, surfaceCurveCalls;
            public double universalTime, mach;
            public bool completed, captureActiveAtEnd, census;
            public long startedTicks, endedTicks, elapsedTicks, fixedStartedTicks, fixedEndedTicks, fixedInclusiveTicks, seamAdmissionTicks, censusTicks;
            public int[] calls = new int[Slots], outermostCalls = new int[Slots];
            public long[] inclusiveTicks = new long[Slots], outermostInclusiveTicks = new long[Slots];
        }
        [Serializable] public sealed class CurveReuseRow
        {
            public int generation, slot, curveWrapperId, nativeCurveId, inputBits, calls;
        }
        sealed class Context
        {
            internal AeroIntegrationAttribution owner;
            internal FlightIntegrator integrator;
            internal Context previous;
            internal long fixedStart;
            internal Window window;
            internal readonly int[] depths = new int[Slots];
            internal readonly long[] starts = new long[Slots], nestedStarts = new long[Slots * 4];
        }
        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }
        struct ReuseKey : IEquatable<ReuseKey>
        {
            internal int generation, slot, wrapper, native, bits;
            public bool Equals(ReuseKey b) { return generation == b.generation && slot == b.slot && wrapper == b.wrapper && native == b.native && bits == b.bits; }
            public override bool Equals(object b) { return b is ReuseKey && Equals((ReuseKey)b); }
            public override int GetHashCode() { unchecked { return ((((generation * 397 + slot) * 397 + wrapper) * 397 + native) * 397) + bits; } }
        }
        [StructLayout(LayoutKind.Explicit)] struct FloatBits
        {
            [FieldOffset(0)] public float value;
            [FieldOffset(0)] public int bits;
        }
        public ReportData Report { get; private set; }

        AeroIntegrationAttribution(string runId, Func<bool> capturing)
        {
            this.capturing = capturing; vessel = FlightGlobals.ActiveVessel;
            Report = new ReportData { runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id, admissionStartedTicks = Stopwatch.GetTimestamp() };
            StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);
            int[] tokens = { 100677457, 100677461, 100677495, 100664780, 100665161, 100664779, 100681489, 100677493, 100677494 };
            Type[] types = { typeof(FlightIntegrator), typeof(FlightIntegrator), typeof(FlightIntegrator), typeof(DragCubeList), typeof(PhysicsGlobals), typeof(DragCubeList), typeof(FloatCurve), typeof(FlightIntegrator), typeof(FlightIntegrator) };
            Report.methods = new MethodRow[pinned.Length];
            for (int i = 0; i < pinned.Length; i++)
            {
                ConvectionSparseKernel.Pin(pinned[i], types[i], tokens[i]); MethodBody body = pinned[i].GetMethodBody();
                Require(body != null && body.ExceptionHandlingClauses.Count == 0, "Original aerodynamic method body/exception shape differs.");
                Report.methods[i] = new MethodRow { declaringType = types[i].FullName, method = pinned[i].Name, metadataToken = tokens[i],
                    assemblyMvid = pinned[i].Module.ModuleVersionId.ToString("D"), ilSha256 = ConvectionSparseKernel.Hash(body.GetILAsByteArray()), patched = i < 5 };
            }
            Require(active == null, "One aerodynamic attribution owner required.");
            for (int i = 0; i < storage.Length; i++) storage[i] = new Window();
            long floor = long.MaxValue;
            for (int i = 0; i < 128; i++) { long now = Stopwatch.GetTimestamp(); floor = Math.Min(floor, Stopwatch.GetTimestamp() - now); } Report.timerReadFloorTicks = floor;
            active = this;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    harmony.Patch(pinned[i], prefix: i == 0 ? new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "Prefix")) : null,
                        transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "Transform")),
                        finalizer: i == 0 ? new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "Finalizer")) : null);
                    Report.patchedMethods++;
                }
                Require(Report.shapeAdmissions == 5 && OwnedGraph(), "Original aerodynamic callsite admission/owned graph differs.");
                installationComplete = true;
                Report.admissionEndedTicks = Stopwatch.GetTimestamp();
            }
            catch
            {
                cleanupInProgress = true;
                try { harmony.UnpatchAll(Owner); }
                finally { cleanupInProgress = false; active = null; }
                throw;
            }
        }
        public static AeroIntegrationAttribution Create(string[] args, string runId, Func<bool> capturing)
        {
            string selected = Environment.GetEnvironmentVariable("CONTINUUM_AERO_INTEGRATION_ATTRIBUTION");
            if (string.IsNullOrEmpty(selected)) return null;
            if (selected != "1" || capturing == null || string.IsNullOrEmpty(runId) || FlightGlobals.ActiveVessel == null ||
                args.Count(arg => arg == "--continuum-convection-replacement=query") != 1 ||
                args.Any(arg => arg == "--continuum-callback-attribution" || arg == "--continuum-flight-integrator-update-attribution" ||
                    arg == "--continuum-live-setdrag-provider" || arg == "--continuum-setdrag-stress-candidate" || arg.StartsWith("--continuum-convection-capture=", StringComparison.Ordinal)))
                throw new InvalidOperationException("Aero integration attribution requires exact query with broad/narrow/capture/old SetDrag observers disabled.");
            return new AeroIntegrationAttribution(runId, capturing);
        }
        static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
        bool OwnedGraph()
        {
            for (int i = 0; i < pinned.Length; i++)
            {
                Patches graph = Harmony.GetPatchInfo(pinned[i]);
                if (i >= 5) { if (graph != null && graph.Owners.Any()) return false; continue; }
                if (graph == null || graph.Owners.Any(owner => owner != Owner) || graph.Transpilers.Count != 1 || graph.Postfixes.Count != 0 ||
                    graph.Prefixes.Count != (i == 0 ? 1 : 0) || graph.Finalizers.Count != (i == 0 ? 1 : 0) ||
                    graph.Transpilers[0].owner != Owner || !Equals(graph.Transpilers[0].PatchMethod, AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "Transform"))) return false;
                if (i == 0 && (graph.Prefixes[0].owner != Owner || graph.Finalizers[0].owner != Owner ||
                    !Equals(graph.Prefixes[0].PatchMethod, AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "Prefix")) ||
                    !Equals(graph.Finalizers[0].PatchMethod, AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "Finalizer")))) return false;
            }
            return true;
        }
        static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> source, ILGenerator generator, MethodBase __originalMethod)
        {
            AeroIntegrationAttribution owner = active;
            Require(owner != null, "Callsite owner missing.");
            int methodIndex = Array.FindIndex(owner.pinned, method => Equals(method, __originalMethod));
            Require(methodIndex >= 0 && methodIndex < 5, "Unexpected original aerodynamic instrumentation method identity.");
            var code = source.Select(item => new CodeInstruction(item)).ToArray(); Require(code.All(item => item.blocks.Count == 0), "Unexpected callsite exception blocks.");
            var result = new List<CodeInstruction>(); var sites = new List<string>(); var slots = new List<int>();
            if (Equals(__originalMethod, Faces) || Equals(__originalMethod, Surface))
            {
                var entry = new CodeInstruction(OpCodes.Ldc_I4, Equals(__originalMethod, Faces) ? 0 : 1);
                entry.labels.AddRange(code[0].labels); code[0].labels.Clear(); result.Add(entry);
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "CountEntry")));
            }
            int rootSites = 0, aeroSites = 0, setDragSites = 0, machEvaluates = 0, otherEvaluates = 0;
            LocalBuilder curve = generator.DeclareLocal(typeof(FloatCurve)), input = generator.DeclareLocal(typeof(float));
            for (int i = 0; i < code.Length; i++)
            {
                MethodInfo called = code[i].operand as MethodInfo; int slot = -1; bool root = false;
                if (code[i].Calls(Integrate) && Equals(__originalMethod, Fixed)) { root = true; rootSites++; }
                else if (Equals(__originalMethod, Integrate))
                {
                    if (code[i].Calls(Aero)) { slot = 0; aeroSites++; }
                    else if (called != null && called.DeclaringType == typeof(Rigidbody) && (called.Name == "AddForce" || called.Name == "AddTorque" || called.Name == "AddForceAtPosition")) slot = 6;
                }
                else if (Equals(__originalMethod, Aero))
                {
                    if (code[i].Calls(SetDrag)) { slot = 1; setDragSites++; }
                    else if (code[i].Calls(Drag)) slot = 4;
                    else if (code[i].Calls(Lift)) slot = 5;
                    else if (code[i].Calls(Evaluate)) slot = 8;
                    else if (called != null && called.DeclaringType == typeof(FlightIntegrator) && (called.Name == "CalculateAerodynamicArea" || called.Name == "CalculateDragValue")) slot = 3;
                    else if (called != null && called.DeclaringType == typeof(Rigidbody) && called.Name == "set_angularDrag") slot = 7;
                    else if (called != null && ((called.DeclaringType == typeof(Rigidbody) && called.Name == "get_velocity") ||
                        (called.DeclaringType == typeof(Transform) && called.Name == "InverseTransformDirection") ||
                        (called.DeclaringType == typeof(Part) && called.Name == "get_Rigidbody") || (called.DeclaringType == typeof(Krakensbane) && called.Name == "GetFrameVelocity"))) slot = 2;
                }
                else if (Equals(__originalMethod, Faces))
                {
                    if (code[i].Calls(Evaluate)) slot = i > 0 && code[i - 1].opcode == OpCodes.Ldarg_2 ? 8 : 9;
                    else if (called != null && called.DeclaringType == typeof(Mathf) && called.Name == "Pow") slot = 10;
                }
                else if (Equals(__originalMethod, Surface) && code[i].Calls(Evaluate))
                {
                    Require(i > 0 && code[i - 1].opcode == OpCodes.Ldarg_2, "Surface Mach curve input shape differs."); slot = 8;
                }
                if (!root && slot < 0) { result.Add(code[i]); continue; }
                Require(i == 0 || (code[i - 1].opcode != OpCodes.Tailcall && code[i - 1].opcode != OpCodes.Constrained), "Callsite has unsupported prefix.");
                int first = result.Count;
                if (root) result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "BeginRoot")));
                else if (code[i].Calls(Evaluate))
                {
                    if (slot == 8) machEvaluates++; else otherEvaluates++;
                    result.Add(new CodeInstruction(OpCodes.Stloc, input)); result.Add(new CodeInstruction(OpCodes.Stloc, curve));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, curve)); result.Add(new CodeInstruction(OpCodes.Ldloc, input)); result.Add(new CodeInstruction(OpCodes.Ldc_I4, slot));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "BeforeCurve")));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, curve)); result.Add(new CodeInstruction(OpCodes.Ldloc, input));
                }
                else { result.Add(new CodeInstruction(OpCodes.Ldc_I4, slot)); result.Add(new CodeInstruction(OpCodes.Call, BeginMethod)); }
                result[first].labels.AddRange(code[i].labels); code[i].labels.Clear(); result.Add(code[i]);
                if (root) result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AeroIntegrationAttribution), "EndRoot")));
                else { result.Add(new CodeInstruction(OpCodes.Ldc_I4, slot)); result.Add(new CodeInstruction(OpCodes.Call, EndMethod)); }
                sites.Add(called.DeclaringType.FullName + "." + called.Name); slots.Add(root ? -1 : slot);
            }
            if (Equals(__originalMethod, Fixed)) Require(rootSites == 1, "Unique original root Integrate callsite differs.");
            if (Equals(__originalMethod, Integrate)) Require(aeroSites == 1 && slots.Count(slot => slot == 6) == 5, "Original integration force/aero callsites differ.");
            if (Equals(__originalMethod, Aero)) Require(setDragSites == 1 && machEvaluates == 1 && slots.Contains(2) && slots.Contains(3) && slots.Contains(4) && slots.Contains(5) && slots.Contains(7), "Original aerodynamic callsites differ.");
            if (Equals(__originalMethod, Faces)) Require(machEvaluates == 2 && otherEvaluates == 2 && slots.Count(slot => slot == 10) == 1, "Original six-face curve/Pow callsites differ.");
            if (Equals(__originalMethod, Surface)) Require(machEvaluates == 6 && otherEvaluates == 0, "Original surface Mach curve callsites differ.");
            owner.AdmitShape(methodIndex, __originalMethod, code.Length, sites.ToArray(), slots.ToArray());
            return result;
        }
        void AdmitShape(int methodIndex, MethodBase method, int instructionCount, string[] sites, int[] slots)
        {
            try
            {
                MethodRow row = Report.methods[methodIndex]; MethodBody body = method.GetMethodBody();
                Require(ReferenceEquals(active, this) && Thread.CurrentThread.ManagedThreadId == thread &&
                    method.Module.ModuleVersionId.ToString("D") == row.assemblyMvid && method.MetadataToken == row.metadataToken &&
                    body != null && body.ExceptionHandlingClauses.Count == 0 && ConvectionSparseKernel.Hash(body.GetILAsByteArray()) == row.ilSha256,
                    "Original aerodynamic method identity/body changed during shape admission.");
                if (row.shapeApplications == 0)
                {
                    Require(!installationComplete && !cleanupInProgress && !disposed, "First aerodynamic shape admission outside installation.");
                    row.instructionCount = instructionCount; row.instrumentedCallsiteMethods = sites; row.callsiteSlots = slots;
                    Report.shapeAdmissions++;
                }
                else
                {
                    Require(cleanupInProgress && row.instructionCount == instructionCount &&
                        row.instrumentedCallsiteMethods.SequenceEqual(sites) && row.callsiteSlots.SequenceEqual(slots),
                        "Aerodynamic shape reapplication outside owned cleanup or original callsite shape changed.");
                    row.cleanupApplications++; Report.cleanupRecompositions++;
                }
                row.shapeApplications++; Report.transpilerExecutions++;
            }
            catch (Exception error) { Fail(error); throw; }
        }
        static void Prefix(FlightIntegrator __instance, out Context __state)
        {
            long start = Stopwatch.GetTimestamp();
            __state = new Context { owner = active, integrator = __instance, previous = current, fixedStart = start }; current = __state;
        }
        static Exception Finalizer(Exception __exception, Context __state)
        {
            if (__state == null) return __exception;
            try
            {
                if (__state.window != null)
                {
                    var report = __state.owner.Report; var window = __state.window;
                    window.fixedEndedTicks = Stopwatch.GetTimestamp(); window.fixedInclusiveTicks = window.fixedEndedTicks - window.fixedStartedTicks;
                    if (__exception != null) { report.originalExceptions++; window.completed = false; }
                    if (window.endedTicks == 0 || __state.depths.Any(depth => depth != 0)) { report.errors++; window.completed = false; }
                    if (window.completed) report.completedWindows++;
                }
            }
            finally { current = __state.previous; }
            return __exception;
        }
        static void BeginRoot()
        {
            Context context = current; AeroIntegrationAttribution owner = context == null ? null : context.owner;
            if (owner == null || owner.disposed || !owner.capturing()) return;
            if (!ReferenceEquals(context.integrator.Vessel, owner.vessel)) return;
            long admissionStart = Stopwatch.GetTimestamp();
            try
            {
                if (context.previous != null || context.window != null || Thread.CurrentThread.ManagedThreadId != owner.thread ||
                    FlightGlobals.ActiveVessel != owner.vessel || context.integrator.GetType() != typeof(FlightIntegrator) || owner.vessel.packed || !owner.vessel.loaded)
                { owner.Report.contextErrors++; return; }
                if (owner.count >= MaxWindows) { owner.Report.droppedWindows++; return; }
                if (!owner.OwnedGraph()) { owner.Report.errors++; owner.Report.detail = "Original inner method ownership changed."; return; }
                double time = Planetarium.GetUniversalTime(), mach = context.integrator.mach;
                if (double.IsNaN(time) || double.IsInfinity(time) || double.IsNaN(mach) || double.IsInfinity(mach))
                { owner.Report.contextErrors++; owner.Report.detail = "Nonfinite root context; Mach bits=" + BitConverter.DoubleToInt64Bits(mach); return; }
                Window window = owner.storage[owner.count]; window.sequence = owner.count++; window.frame = Time.frameCount; window.parts = owner.vessel.parts.Count;
                window.universalTime = time; window.mach = mach; window.fixedStartedTicks = context.fixedStart;
                window.census = window.sequence < CensusWindows; if (window.census) owner.Report.censusWindows++;
                context.window = window; window.startedTicks = Stopwatch.GetTimestamp(); window.seamAdmissionTicks = window.startedTicks - admissionStart;
            }
            catch (Exception error) { owner.Fail(error); }
        }
        static void EndRoot()
        {
            Context context = current; if (context == null || context.window == null) return;
            context.window.endedTicks = Stopwatch.GetTimestamp(); context.window.elapsedTicks = context.window.endedTicks - context.window.startedTicks;
            context.window.captureActiveAtEnd = context.owner.capturing(); context.window.completed = true;
            if (context.depths.Any(depth => depth != 0)) { context.owner.Report.errors++; context.window.completed = false; }
            Window window = context.window;
            if (window.sixFaceCalls != window.calls[1] || window.surfaceCurveCalls != window.sixFaceCalls * 6 ||
                window.calls[8] < window.surfaceCurveCalls * 3 + window.sixFaceCalls * 6)
            {
                context.owner.Report.errors++; context.owner.Report.coverageWitness = "failed";
                context.owner.Report.detail = "Original six-face/surface/Mach callsites were not reached as expected; no zero-cost interpretation (possible compiled inlining or changed call coverage).";
            }
            else if (context.owner.Report.coverageWitness != "failed") context.owner.Report.coverageWitness = "passed-original-callsite-counts";
        }
        static void CountEntry(int kind)
        {
            Context context = current; if (context == null || context.window == null || context.window.endedTicks != 0) return;
            if (kind == 0) context.window.sixFaceCalls++; else context.window.surfaceCurveCalls++;
        }
        static void Begin(int slot)
        {
            Context context = current; if (context == null || context.window == null || context.window.endedTicks != 0) return;
            int depth = context.depths[slot];
            if (depth >= 4) { context.owner.Report.errors++; return; }
            long now = Stopwatch.GetTimestamp(); context.nestedStarts[slot * 4 + depth] = now; if (depth == 0) context.starts[slot] = now; context.depths[slot]++;
        }
        static void End(int slot)
        {
            long now = Stopwatch.GetTimestamp(); Context context = current; if (context == null || context.window == null || context.window.endedTicks != 0) return;
            int depth = context.depths[slot]; if (depth < 1) { context.owner.Report.errors++; return; }
            context.depths[slot]--; Window window = context.window; window.calls[slot]++; window.inclusiveTicks[slot] += now - context.nestedStarts[slot * 4 + depth - 1];
            if (depth == 1) { window.outermostCalls[slot]++; window.outermostInclusiveTicks[slot] += now - context.starts[slot]; }
        }
        static void BeforeCurve(FloatCurve curve, float input, int slot)
        {
            Context context = current;
            if (context != null && context.window != null && context.window.census && context.window.endedTicks == 0)
            {
                long start = Stopwatch.GetTimestamp();
                try { context.owner.ObserveCurve(context.window, curve, input, slot); }
                catch (Exception error) { context.owner.Fail(error); }
                finally { long elapsed = Stopwatch.GetTimestamp() - start; context.window.censusTicks += elapsed; context.owner.Report.censusTicks += elapsed; }
            }
            Begin(slot);
        }
        int CurveId(object value)
        {
            Require(value != null, "Reached curve reference is absent."); int id;
            if (!curveObjects.TryGetValue(value, out id)) { Require(curveObjects.Count < MaxCurveObjects, "Curve reference census bound exceeded."); id = curveObjects.Count + 1; curveObjects.Add(value, id); } return id;
        }
        void ObserveCurve(Window window, FloatCurve curve, float input, int slot)
        {
            if (censusCalls++ >= MaxCensusCalls) { Report.droppedCensusCalls++; return; }
            var key = new ReuseKey { generation = window.sequence, slot = slot, wrapper = CurveId(curve), native = CurveId(curve.Curve), bits = new FloatBits { value = input }.bits };
            CurveReuseRow row;
            if (!reuse.TryGetValue(key, out row))
            {
                if (reuse.Count >= MaxReuseRows) { Report.droppedCensusCalls++; return; }
                row = new CurveReuseRow { generation = key.generation, slot = key.slot, curveWrapperId = key.wrapper, nativeCurveId = key.native, inputBits = key.bits }; reuse.Add(key, row);
            }
            row.calls++;
        }
        void Fail(Exception error) { Report.errors++; Report.detail = error.GetType().Name + ": " + error.Message; }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            bool owned = OwnedGraph(); if (!owned) { Report.errors++; Report.detail = "Inner callsite ownership changed before cleanup."; }
            try
            {
                cleanupInProgress = true;
                harmony.UnpatchAll(Owner);
                Report.cleanupStatus = pinned.Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) == true) ? "cleanup-error" : "removed-owned-hooks";
            }
            catch (Exception error) { Fail(error); Report.cleanupStatus = "cleanup-error"; }
            finally { cleanupInProgress = false; if (ReferenceEquals(active, this)) active = null; }
            Report.windows = storage.Take(count).ToArray(); Report.curveReuse = reuse.Values.OrderBy(row => row.generation).ThenBy(row => row.slot).ThenBy(row => row.curveWrapperId).ThenBy(row => row.inputBits).ToArray();
            Report.censusStatus = Report.droppedCensusCalls != 0 ? "truncated" : Report.curveReuse.Length == 0 ? "not-reached-no-curve-evaluation-coverage" : "observed-reference-input-reuse-only";
            string reason; Report.status = IsValidFinal(out reason) ? "observed" : "invalid"; if (Report.status == "invalid" && Report.detail == null) Report.detail = reason;
        }
        public bool IsValidFinal(out string reason)
        {
            if (!disposed || !installationComplete || cleanupInProgress || Report.cleanupStatus != "removed-owned-hooks" || Report.patchedMethods != 5 || Report.shapeAdmissions != 5 ||
                Report.transpilerExecutions != Report.shapeAdmissions + Report.cleanupRecompositions ||
                Report.methods.Take(5).Any(row => row.shapeApplications != 1 + row.cleanupApplications) ||
                Report.methods.Skip(5).Any(row => row.shapeApplications != 0 || row.cleanupApplications != 0) ||
                Report.methods.Sum(row => row.cleanupApplications) != Report.cleanupRecompositions)
            { reason = "Original callsite shape/cleanup incomplete."; return false; }
            if (Report.errors != 0 || Report.contextErrors != 0 || Report.originalExceptions != 0 || Report.droppedWindows != 0 || Report.droppedCensusCalls != 0) { reason = "Observer context, original exception or storage coverage failed."; return false; }
            if (Report.completedWindows < CensusWindows || Report.censusWindows != CensusWindows || Report.coverageWitness != "passed-original-callsite-counts" ||
                Report.windows.Any(window => !window.completed || window.elapsedTicks < 0 || window.fixedInclusiveTicks < window.elapsedTicks)) { reason = "Four reached curve census/root windows incomplete."; return false; }
            reason = null; return true;
        }
    }
}
