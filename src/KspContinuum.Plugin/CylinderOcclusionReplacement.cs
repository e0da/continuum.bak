using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class CylinderOcclusionReplacement : IDisposable
    {
        internal const string PatchOwner = "continuum.cylinder-occlusion-replacement";
        const string StockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int MaxRows = 1024, MaxWindows = 4096;
        static readonly Guid StockMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static readonly MethodInfo[] Targets = {
            AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateOcclusionSolar"),
            AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateOcclusionBody") };
        static readonly FieldInfo[] RowFields = {
            AccessTools.Field(typeof(FlightIntegrator), "occlusionSun"), AccessTools.Field(typeof(FlightIntegrator), "occlusionBody") };
        static readonly FieldInfo CountField = AccessTools.Field(typeof(FlightIntegrator), "partThermalDataCount");
        static readonly FieldInfo VesselField = AccessTools.Field(typeof(VesselModule), "vessel");
        static readonly MethodInfo Setup = AccessTools.DeclaredMethod(typeof(OcclusionCylinder), "Setup");
        static readonly MethodInfo Scalar = AccessTools.DeclaredMethod(typeof(OcclusionData), "GetCylinderOcclusion");
        static readonly MethodInfo Rectangle = AccessTools.DeclaredMethod(typeof(OcclusionData), "RectRectIntersection", Enumerable.Repeat(typeof(double), 6).ToArray());
        static readonly MethodInfo Project = AccessTools.DeclaredMethod(typeof(OcclusionData), "Update", new[] { typeof(Vector3), typeof(bool) });
        static readonly MethodInfo Sort = AccessTools.DeclaredMethod(typeof(List<OcclusionData>), "Sort", Type.EmptyTypes);
        static readonly MethodInfo Item = AccessTools.PropertyGetter(typeof(List<OcclusionData>), "Item");
        static CylinderOcclusionReplacement active;

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-cylinder-occlusion-replacement/v1", mode, runId, vesselId;
            public string status = "installing", cleanupStatus = "not-installed", detail, lastFallback;
            public string assemblySha256 = StockSha, assemblyMvid = StockMvid.ToString("D");
            public string scope = "Pinned solar/body complete pass at original post-projection/post-conditional-sort boundary. Same live cylinder pools, original Setup and serial publication order. Compare alternates dynamic original-control/index-query states per kind; it is not an identical-input pair or a whole-frame benchmark. Complete clocks include prefix, original prelude, admission/copy, index build or refit/fold, validation, publication and finalizer bookkeeping, excluding outer Harmony dispatch. Control scalar calls are unmeasured (-1); candidate calls and node visits are exact. Verification runs one dry index fold and one authoritative original fold, with comparison and scratch Setup charged as proof tax. Logging-enabled, changed owner/count/domain and foreign graph are unsupported; only prepublication fallback is allowed. Frozen installed graph is audited at installation, observer handoff and cleanup; no universal runtime mod-mutation guarantee.";
            public int processId, errors, originalExceptions, verificationMismatches, partialPublicationFailures, recursionStops, droppedWindows;
            public long clockFrequency = Stopwatch.Frequency, installStartedTicks, installEndedTicks, cleanupStartedTicks, cleanupEndedTicks;
            public long calls, captureCalls, replacements, captureReplacements, fallbacks, captureFallbacks;
            public string[] preinstallPatchGraph = new string[0], installedPatchGraph = new string[0], precleanupPatchGraph = new string[0], restoredPatchGraph = new string[0];
            public PassReport[] passes = { new PassReport { kind = "solar", metadataToken = 100677507 }, new PassReport { kind = "body", metadataToken = 100677508 } };
            public Window[] windows = new Window[0], firstInvocations = new Window[2];
        }
        [Serializable] public sealed class PassReport
        {
            public string kind, ilSha256;
            public int metadataToken, instructionCount, seamInstruction, shapeAdmissions, transpilerExecutions;
            public int verifiedPasses, capturedVerifiedPasses, controlPasses, candidatePasses, count, indexPreparations, qualificationResets;
            public long calls, captureCalls, captureReplacements;
        }
        [Serializable] public sealed class Window
        {
            public long sequence, treatmentSequence, universalTimeBits, startedTicks, seamTicks, endedTicks, completeTicks;
            public string kind, treatment, fallback, mismatch;
            public int frame, rowCount, acceptedCylinders;
            public long projectionTicks, admissionCopyTicks, indexTicks, validationTicks, publicationTicks, comparisonTicks, finalizerTicks;
            public long scalarCalls = -1, nodeVisits, orderFingerprint, geometryFingerprint;
            public double conservativeMargin;
            public bool firstInvocation, indexPrepared, bitwiseMatch, completed, captureActiveAtEnd;
        }
        [StructLayout(LayoutKind.Explicit)] struct FloatBits
        { [FieldOffset(0)] internal float value; [FieldOffset(0)] internal int bits; }
        sealed class Binding
        { internal Part part; internal PartThermalData ptd; internal OcclusionCylinder cylinder; }
        struct Snapshot
        {
            internal OcclusionData row; internal Part part; internal PartThermalData ptd; internal OcclusionCylinder cylinder;
            internal Vector3 projectedCenter; internal Vector2 center, extents, min, max;
            internal double radius, maximumDot, maxWidthDepth;
        }
        sealed class Resident
        {
            internal readonly List<OcclusionData> list;
            internal readonly OcclusionCylinder[] liveArray;
            internal readonly Dictionary<OcclusionData, Binding> bindings;
            internal readonly HashSet<OcclusionData> seen;
            internal readonly CylinderOcclusionFold.Row[] geometry;
            internal readonly Snapshot[] snapshot;
            internal readonly double[] multipliers;
            internal readonly int[] accepted;
            internal readonly OcclusionCylinder[] scratch;
            internal CylinderOcclusionIndex index;
            internal int acceptedCount;
            internal Resident(List<OcclusionData> rows, OcclusionCylinder[] liveArray, int kind)
            {
                list = rows; this.liveArray = liveArray; int n = rows.Count;
                geometry = new CylinderOcclusionFold.Row[n]; snapshot = new Snapshot[n]; multipliers = new double[n]; accepted = new int[n];
                scratch = new OcclusionCylinder[n]; bindings = new Dictionary<OcclusionData, Binding>(n); seen = new HashSet<OcclusionData>();
                var parts = new HashSet<Part>(); var cylinders = new HashSet<OcclusionCylinder>();
                foreach (OcclusionData row in rows)
                {
                    Require(row != null && row.part != null && row.ptd != null && !bindings.ContainsKey(row) && parts.Add(row.part), "Duplicate or absent row owner.");
                    OcclusionCylinder cylinder = kind == 0 ? row.sunCyl : row.bodyCyl;
                    Require(cylinder != null && cylinders.Add(cylinder), "Duplicate or absent cylinder pool.");
                    bindings.Add(row, new Binding { part = row.part, ptd = row.ptd, cylinder = cylinder });
                }
                for (int i = 0; i < n; i++) scratch[i] = new OcclusionCylinder();
            }
        }
        sealed class Invocation
        {
            internal FlightIntegrator integrator;
            internal Window window;
            internal Resident resident;
            internal int kind;
            internal bool captured, verify, publicationStarted, handled;
        }
        public struct CallState { internal object invocation; }
        readonly Harmony harmony = new Harmony(PatchOwner);
        readonly Vessel vessel;
        readonly Func<bool> capturing;
        readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        readonly Part[] parts;
        readonly HashSet<Part> members;
        readonly Resident[] residents = new Resident[2];
        readonly Window[] windows = new Window[MaxWindows];
        readonly Window[] coldWindows = { new Window(), new Window() }, spareWindows = { new Window(), new Window() };
        readonly Invocation[] invocations = { new Invocation(), new Invocation() };
        readonly long[] armSequences = new long[2];
        Invocation current;
        FlightIntegrator integrator;
        FlightIntegratorUpdateAttribution attribution;
        int windowCount;
        bool disposed, installed, disabled;
        public ReportData Report { get; private set; }
        public bool ReadyForCapture { get { return Report.mode == "verify" || Report.passes.All(pass => pass.verifiedPasses == 1); } }
        public bool QualificationFailed { get { return disabled || Report.errors != 0 || Report.verificationMismatches != 0; } }

        CylinderOcclusionReplacement(string mode, string runId, Func<bool> capturing)
        {
            this.capturing = capturing; vessel = FlightGlobals.ActiveVessel;
            Report = new ReportData { mode = mode, runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id,
                installStartedTicks = Stopwatch.GetTimestamp() };
            StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);
            Require(active == null && CountField != null && CountField.FieldType == typeof(int) && VesselField != null && VesselField.FieldType == typeof(Vessel) &&
                RowFields.All(field => field != null && field.FieldType == typeof(List<OcclusionData>)), "Cylinder fields/owner differ.");
            Pin(Setup, typeof(OcclusionCylinder), 100677572); Pin(Scalar, typeof(OcclusionData), 100677561);
            Require(Project != null && Project.DeclaringType == typeof(OcclusionData) && Project.Module.ModuleVersionId == StockMvid &&
                Project.ReturnType == typeof(void) && !Project.IsVirtual && Sort != null && Item != null, "Cylinder projection/list boundary differs.");
            Require(Rectangle != null && Rectangle.DeclaringType == typeof(OcclusionData) && Rectangle.Module.ModuleVersionId == StockMvid &&
                Rectangle.ReturnType == typeof(double) && Rectangle.IsStatic && Rectangle.GetMethodBody() != null, "Cylinder rectangle helper differs.");
            RequireEmpty(Setup); RequireEmpty(Scalar); RequireEmpty(Rectangle); RequireEmpty(Project);
            parts = vessel.parts.ToArray(); members = new HashSet<Part>(parts);
            Require(parts.Length > 0 && parts.Length <= MaxRows && members.Count == parts.Length && parts.All(part => part != null), "Cylinder topology bound differs.");
            for (int kind = 0; kind < 2; kind++)
            {
                Pin(Targets[kind], typeof(FlightIntegrator), 100677507 + kind); RequireEmpty(Targets[kind]);
                Report.passes[kind].ilSha256 = Hash(Targets[kind].GetMethodBody().GetILAsByteArray());
            }
            Report.preinstallPatchGraph = GraphEvidence();
            for (int i = 0; i < MaxWindows; i++) windows[i] = new Window();
            active = this;
            try
            {
                foreach (MethodInfo target in Targets) harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(CylinderOcclusionReplacement), nameof(Prefix)),
                    transpiler: new HarmonyMethod(typeof(CylinderOcclusionReplacement), nameof(Hook)),
                    finalizer: new HarmonyMethod(typeof(CylinderOcclusionReplacement), nameof(Finalizer)));
                Require(Report.passes.All(pass => pass.shapeAdmissions == 1), "Cylinder fold seam was not admitted twice.");
                installed = true; Report.status = "installed"; Report.cleanupStatus = "installed";
                RequireGraph(false); Report.installedPatchGraph = GraphEvidence();
            }
            catch (Exception error)
            {
                Fail(error); Report.status = "installation-failed";
                try { harmony.UnpatchAll(PatchOwner); Report.cleanupStatus = "removed-owned-hooks"; }
                catch (Exception cleanup) { Report.cleanupStatus = "cleanup-error"; Report.detail += "\n" + cleanup; }
                finally { if (ReferenceEquals(active, this)) active = null; }
            }
            finally { Report.installEndedTicks = Stopwatch.GetTimestamp(); }
        }
        public static CylinderOcclusionReplacement Create(string[] arguments, string runId, Func<bool> capturing)
        {
            string mode = Environment.GetEnvironmentVariable("CONTINUUM_CYLINDER_OCCLUSION_MODE");
            if (string.IsNullOrEmpty(mode)) return null;
            Require((mode == "verify" || mode == "compare" || mode == "query") && !string.IsNullOrEmpty(runId) && capturing != null && FlightGlobals.ActiveVessel != null,
                "Cylinder mode requires verify/compare/query and a bound active flight.");
            Require(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_CYLINDER_OCCLUSION_CAPTURE")) &&
                !Harmony.HasAnyPatches(CylinderOcclusionCapture.PatchOwner) && !Harmony.HasAnyPatches("continuum.fixed-callback-attribution") &&
                !Harmony.HasAnyPatches("continuum.update-callback-attribution"), "Cylinder replacement conflicts with tape capture/broad observers.");
            return new CylinderOcclusionReplacement(mode, runId, capturing);
        }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static void Pin(MethodInfo method, Type type, int token)
        {
            Require(method != null && method.DeclaringType == type && method.Module.ModuleVersionId == StockMvid && method.MetadataToken == token &&
                !method.IsStatic && !method.IsVirtual && method.GetMethodBody() != null && method.GetMethodBody().ExceptionHandlingClauses.Count == 0,
                "Cylinder original identity differs: " + type.Name + "/" + token);
        }
        static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void RequireEmpty(MethodBase method)
        {
            Patches graph = Harmony.GetPatchInfo(method);
            Require(graph == null || graph.Prefixes.Count + graph.Finalizers.Count + graph.Transpilers.Count + graph.Postfixes.Count == 0,
                "Cylinder foreign patch: " + method.DeclaringType.FullName + "." + method.Name);
        }
        static int Kind(MethodBase method)
        { return FlightIntegratorUpdateAttribution.SameMethod(method, Targets[0]) ? 0 : FlightIntegratorUpdateAttribution.SameMethod(method, Targets[1]) ? 1 : -1; }
        static bool OwnPatch(IEnumerable<Patch> patches, string name)
        {
            Patch[] own = patches.Where(patch => patch.owner == PatchOwner).ToArray();
            return own.Length == 1 && own[0].priority == Priority.Normal && own[0].before.Length == 0 && own[0].after.Length == 0 &&
                FlightIntegratorUpdateAttribution.SameMethod(own[0].PatchMethod, AccessTools.DeclaredMethod(typeof(CylinderOcclusionReplacement), name));
        }
        internal bool MatchesAttributionGraph(MethodBase method, Patches graph, bool observerInstalled)
        {
            if (!ReferenceEquals(active, this) || !installed || disposed || Kind(method) < 0 || graph == null) return false;
            if (observerInstalled && (attribution == null || !attribution.OwnsCylinderReplacement(this) || !FlightIntegratorUpdateAttribution.HasExactObserverPatch(graph, method))) return false;
            int extra = observerInstalled ? 1 : 0;
            return graph.Prefixes.Count == 1 + extra && graph.Finalizers.Count == 1 + extra && graph.Transpilers.Count == 1 && graph.Postfixes.Count == 0 &&
                OwnPatch(graph.Prefixes, nameof(Prefix)) && OwnPatch(graph.Transpilers, nameof(Hook)) && OwnPatch(graph.Finalizers, nameof(Finalizer));
        }
        internal void BeginAttribution(FlightIntegratorUpdateAttribution observer)
        {
            Require(attribution == null && observer != null && observer.OwnsCylinderReplacement(this), "Cylinder attribution lease differs.");
            RequireGraph(false); attribution = observer;
        }
        internal void EndAttribution(FlightIntegratorUpdateAttribution observer)
        { Require(ReferenceEquals(attribution, observer), "Cylinder attribution release differs."); RequireGraph(false); attribution = null; }
        void RequireGraph(bool observerInstalled)
        {
            foreach (MethodInfo target in Targets) Require(MatchesAttributionGraph(target, Harmony.GetPatchInfo(target), observerInstalled), "Cylinder installed graph differs: " + target.Name);
            RequireEmpty(Setup); RequireEmpty(Scalar); RequireEmpty(Rectangle); RequireEmpty(Project);
        }
        static string[] GraphEvidence()
        {
            return Targets.Concat(new[] { Setup, Scalar, Rectangle, Project }).SelectMany(method => FlightIntegratorUpdateAttribution.DescribeGraph(method, Harmony.GetPatchInfo(method))).ToArray();
        }
        static bool LoadLocal(CodeInstruction code)
        { return code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S || code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Ldloc_3; }
        internal static int AdmitShape(IList<CodeInstruction> code, MethodBase method)
        {
            int kind = Kind(method); Require(kind >= 0 && code.Count > 0 && code.All(item => item.blocks.Count == 0), "Cylinder caller/exception shape differs.");
            int sort = -1, getter = -1;
            for (int i = 0; i < code.Count; i++) { if (code[i].Calls(Sort)) sort = i; }
            Require(sort >= 0 && code.Count(item => item.Calls(Sort)) == 1 && code.Count(item => item.Calls(Project)) == 1 &&
                code.Count(item => item.Calls(Setup)) == 2 && code.Count(item => item.Calls(Scalar)) == 1, "Cylinder original callsite shape differs.");
            for (int i = sort + 1; i < code.Count; i++) if (code[i].Calls(Item)) { getter = i; break; }
            int seam = getter - 3;
            Require(seam > sort && code[seam].opcode == OpCodes.Ldarg_0 && code[seam + 1].opcode == OpCodes.Ldfld &&
                FlightIntegratorUpdateAttribution.SameMethod(code[getter].operand as MethodBase, Item) && Equals(code[seam + 1].operand, RowFields[kind]) &&
                LoadLocal(code[seam + 2]) && Enumerable.Range(seam + 1, 3).All(i => code[i].labels.Count == 0), "Cylinder empty-stack fold entry differs.");
            // A conditional sort may branch to this entry, never into its receiver/index stack expression.
            Require(code[seam].labels.Count <= 2 && getter + 1 < code.Count && code[getter + 1].opcode.Name.StartsWith("stloc", StringComparison.Ordinal),
                "Cylinder first row store differs.");
            return seam;
        }
        static IEnumerable<CodeInstruction> Hook(IEnumerable<CodeInstruction> source, ILGenerator generator, MethodBase __originalMethod)
        {
            var code = source.Select(item => new CodeInstruction(item)).ToList();
            int kind = Kind(__originalMethod), seam = AdmitShape(code, __originalMethod);
            CylinderOcclusionReplacement owner = active;
            Require(owner != null && kind >= 0, "Cylinder immutable transpiler owner unavailable.");
            PassReport pass = owner.Report.passes[kind]; pass.transpilerExecutions++;
            if (pass.shapeAdmissions == 0) { pass.shapeAdmissions = 1; pass.instructionCount = code.Count; pass.seamInstruction = seam; }
            else Require(pass.instructionCount == code.Count && pass.seamInstruction == seam, "Cylinder rewritten original shape changed during recomposition.");
            Label original = generator.DefineLabel(); var entry = new CodeInstruction(OpCodes.Ldarg_0);
            entry.labels.AddRange(code[seam].labels); code[seam].labels.Clear(); code[seam].labels.Add(original);
            code.InsertRange(seam, new[] { entry, new CodeInstruction(OpCodes.Ldc_I4, kind),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(CylinderOcclusionReplacement), nameof(TryFold))),
                new CodeInstruction(OpCodes.Brfalse, original), new CodeInstruction(OpCodes.Ret) });
            return code;
        }
        void Topology(FlightIntegrator owner)
        {
            Require(Thread.CurrentThread.ManagedThreadId == threadId && FlightGlobals.ActiveVessel == vessel && vessel.parts.Count == parts.Length &&
                ReferenceEquals(VesselField.GetValue(owner), vessel) && (integrator == null || ReferenceEquals(integrator, owner)), "Cylinder epoch/thread/integrator changed.");
            for (int i = 0; i < parts.Length; i++) Require(parts[i] != null && ReferenceEquals(parts[i], vessel.parts[i]) && parts[i].vessel == vessel, "Cylinder vessel membership changed.");
            integrator = owner;
        }
        static OcclusionCylinder[] Pool(FlightIntegrator owner, int kind) { return kind == 0 ? owner.occludersSun : owner.occludersBody; }
        static int PublishedCount(FlightIntegrator owner, int kind) { return kind == 0 ? owner.occludersSunCount : owner.occludersBodyCount; }
        static double Multiplier(PartThermalData ptd, int kind) { return kind == 0 ? ptd.sunAreaMultiplier : ptd.bodyAreaMultiplier; }
        static void Multiplier(PartThermalData ptd, int kind, double value) { if (kind == 0) ptd.sunAreaMultiplier = value; else ptd.bodyAreaMultiplier = value; }
        static void Count(FlightIntegrator owner, int kind, int value) { if (kind == 0) owner.occludersSunCount = value; else owner.occludersBodyCount = value; }
        static long Bits(double value) { return BitConverter.DoubleToInt64Bits(value); }
        static int Bits(float value) { return new FloatBits { value = value }.bits; }
        static bool Same(Vector2 a, Vector2 b) { return Bits(a.x) == Bits(b.x) && Bits(a.y) == Bits(b.y); }
        static bool Same(Vector3 a, Vector3 b) { return Bits(a.x) == Bits(b.x) && Bits(a.y) == Bits(b.y) && Bits(a.z) == Bits(b.z); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static long Mix(long hash, long value) { unchecked { return (hash ^ value) * 1099511628211L; } }
        Resident Copy(Invocation call)
        {
            FlightIntegrator owner = call.integrator; int kind = call.kind;
            Require(!GameSettings.FI_LOG_TEMP_ERROR, "Logging-enabled domain requires original.");
            Topology(owner);
            var list = RowFields[kind].GetValue(owner) as List<OcclusionData>; var pool = Pool(owner, kind);
            int count = (int)CountField.GetValue(owner);
            Require(list != null && list.GetType() == typeof(List<OcclusionData>) && count > 0 && count <= MaxRows && list.Count == count &&
                owner.partThermalDataList.Count == count && pool != null && pool.Length >= count, "Cylinder current list/count/capacity unsupported.");
            Resident state = residents[kind];
            if (state == null || !ReferenceEquals(state.list, list) || !ReferenceEquals(state.liveArray, pool) || state.geometry.Length != count)
            {
                Require(!call.captured, "Cylinder graph/count changed within capture.");
                state = residents[kind] = new Resident(list, pool, kind);
                if (Report.passes[kind].verifiedPasses != 0) { Report.passes[kind].verifiedPasses = 0; Report.passes[kind].qualificationResets++; }
                call.verify = true; call.window.treatment = "verify-original-return";
            }
            long order = 1469598103934665603L, geometry = order;
            state.seen.Clear();
            for (int i = 0; i < count; i++)
            {
                OcclusionData row = list[i]; Binding binding = null;
                Require(row != null && row.GetType() == typeof(OcclusionData) && state.seen.Add(row) && state.bindings.TryGetValue(row, out binding) && row.part != null &&
                    members.Contains(row.part) && ReferenceEquals(row.part, binding.part) && ReferenceEquals(row.ptd, binding.ptd) && ReferenceEquals(row.part.ptd, row.ptd) &&
                    row.ptd.GetType() == typeof(PartThermalData) && ReferenceEquals(row.ptd.part, row.part) &&
                    ReferenceEquals(kind == 0 ? row.ptd.sunData : row.ptd.bodyData, row) && ReferenceEquals(kind == 0 ? row.sunCyl : row.bodyCyl, binding.cylinder) &&
                    binding.cylinder.GetType() == typeof(OcclusionCylinder), "Cylinder concrete row/PTD/pool generation changed.");
                Require(Finite(row.projectedRadius) && Finite(row.maximumDot) && Finite(row.maxWidthDepth) && Finite(row.maximumDot - row.maxWidthDepth) &&
                    Finite(row.projectedCenter.x) && Finite(row.projectedCenter.y) && Finite(row.projectedCenter.z), "Cylinder Setup domain unsupported.");
                state.geometry[i] = new CylinderOcclusionFold.Row { minX = row.minExtents.x, minY = row.minExtents.y, maxX = row.maxExtents.x, maxY = row.maxExtents.y,
                    centerX = row.center.x, centerY = row.center.y, extentX = row.extents.x, extentY = row.extents.y };
                state.snapshot[i] = new Snapshot { row = row, part = row.part, ptd = row.ptd, cylinder = binding.cylinder, center = row.center, extents = row.extents,
                    min = row.minExtents, max = row.maxExtents, projectedCenter = row.projectedCenter, radius = row.projectedRadius, maximumDot = row.maximumDot, maxWidthDepth = row.maxWidthDepth };
                order = Mix(order, row.part.flightID);
                geometry = Mix(Mix(Mix(Mix(Mix(Mix(Mix(Mix(geometry, Bits(row.minExtents.x)), Bits(row.minExtents.y)), Bits(row.maxExtents.x)), Bits(row.maxExtents.y)),
                    Bits(row.center.x)), Bits(row.center.y)), Bits(row.extents.x)), Bits(row.extents.y));
            }
            call.window.rowCount = count; call.window.orderFingerprint = order; call.window.geometryFingerprint = geometry;
            Report.passes[kind].count = count;
            return state;
        }
        void Validate(Invocation call)
        {
            Topology(call.integrator); Resident state = call.resident;
            Require(ReferenceEquals(RowFields[call.kind].GetValue(call.integrator), state.list) && ReferenceEquals(Pool(call.integrator, call.kind), state.liveArray) &&
                state.list.Count == state.geometry.Length && (int)CountField.GetValue(call.integrator) == state.geometry.Length &&
                call.integrator.partThermalDataList.Count == state.geometry.Length && state.liveArray.Length >= state.geometry.Length && !GameSettings.FI_LOG_TEMP_ERROR,
                "Cylinder owner changed before commit.");
            for (int i = 0; i < state.snapshot.Length; i++)
            {
                Snapshot s = state.snapshot[i]; OcclusionData row = state.list[i];
                Require(ReferenceEquals(row, s.row) && s.part != null && s.part.vessel == vessel && ReferenceEquals(row.part, s.part) && ReferenceEquals(row.ptd, s.ptd) &&
                    ReferenceEquals(s.part.ptd, s.ptd) && ReferenceEquals(s.ptd.part, s.part) && ReferenceEquals(call.kind == 0 ? row.sunCyl : row.bodyCyl, s.cylinder) &&
                    ReferenceEquals(call.kind == 0 ? s.ptd.sunData : s.ptd.bodyData, row) && Same(row.center, s.center) && Same(row.extents, s.extents) &&
                    Same(row.minExtents, s.min) && Same(row.maxExtents, s.max) && Same(row.projectedCenter, s.projectedCenter) && Bits(row.projectedRadius) == Bits(s.radius) &&
                    Bits(row.maximumDot) == Bits(s.maximumDot) && Bits(row.maxWidthDepth) == Bits(s.maxWidthDepth), "Cylinder projected state changed before publication.");
            }
        }
        static void Prefix(FlightIntegrator __instance, MethodBase __originalMethod, out CallState __state)
        {
            __state = new CallState(); CylinderOcclusionReplacement owner = active;
            if (owner == null || !owner.installed || owner.disabled || owner.disposed) return;
            long started = Stopwatch.GetTimestamp(); int kind = Kind(__originalMethod);
            if (owner.current != null || kind < 0) { owner.Report.recursionStops++; owner.Fail(new InvalidOperationException("Nested cylinder pass unsupported.")); return; }
            try
            {
                bool captured = owner.capturing(); PassReport pass = owner.Report.passes[kind];
                bool verify = pass.verifiedPasses == 0 && (owner.Report.mode != "verify" || captured);
                bool eligible = captured || verify || owner.Report.mode == "query";
                if (!eligible) return;
                Window window;
                if (captured)
                {
                    if (owner.windowCount == MaxWindows) { owner.Report.droppedWindows++; owner.Fail(new InvalidOperationException("Cylinder window bound exceeded.")); return; }
                    window = owner.windows[owner.windowCount]; window.sequence = owner.windowCount++;
                }
                else
                {
                    window = owner.Report.firstInvocations[kind] == null ? owner.coldWindows[kind] : owner.spareWindows[kind];
                    Reset(window); window.sequence = -1;
                }
                window.kind = pass.kind; window.frame = Time.frameCount; window.universalTimeBits = Bits(Planetarium.GetUniversalTime()); window.startedTicks = started;
                window.firstInvocation = owner.Report.firstInvocations[kind] == null;
                window.treatmentSequence = captured && !verify ? owner.armSequences[kind]++ : -1;
                window.treatment = verify ? "verify-original-return" : owner.Report.mode == "compare" && (window.treatmentSequence & 1) == 0 ? "control-original" :
                    owner.Report.mode == "verify" ? "original-qualified" : "query";
                Invocation call = owner.invocations[kind];
                call.integrator = __instance; call.kind = kind; call.window = window; call.captured = captured; call.verify = verify;
                call.resident = null; call.publicationStarted = call.handled = false;
                owner.current = call; __state.invocation = call;
                owner.Report.calls++; pass.calls++;
                if (captured) { owner.Report.captureCalls++; pass.captureCalls++; }
            }
            catch (Exception error) { owner.Fail(error); }
        }
        static void Reset(Window window)
        {
            window.sequence = window.treatmentSequence = window.universalTimeBits = window.startedTicks = window.seamTicks = window.endedTicks = window.completeTicks = 0;
            window.kind = window.treatment = window.fallback = window.mismatch = null; window.frame = window.rowCount = window.acceptedCylinders = 0;
            window.projectionTicks = window.admissionCopyTicks = window.indexTicks = window.validationTicks = window.publicationTicks = window.comparisonTicks = window.finalizerTicks = 0;
            window.scalarCalls = -1; window.nodeVisits = window.orderFingerprint = window.geometryFingerprint = 0; window.conservativeMargin = 0;
            window.firstInvocation = window.indexPrepared = window.bitwiseMatch = window.completed = window.captureActiveAtEnd = false;
        }
        static bool TryFold(FlightIntegrator __instance, int kind)
        {
            CylinderOcclusionReplacement owner = active; Invocation call = owner == null ? null : owner.current;
            if (call == null || owner.disabled || !ReferenceEquals(call.integrator, __instance) || call.kind != kind) return false;
            Window window = call.window; window.seamTicks = Stopwatch.GetTimestamp(); window.projectionTicks = window.seamTicks - window.startedTicks;
            long start = window.seamTicks;
            try
            {
                call.resident = owner.Copy(call); window.admissionCopyTicks = Stopwatch.GetTimestamp() - start;
                if (!call.verify && owner.Report.passes[kind].verifiedPasses != 1) throw new InvalidOperationException("Cylinder pass has no original-return qualification.");
                if (window.treatment == "control-original" || window.treatment == "original-qualified") return false;
                start = Stopwatch.GetTimestamp(); Resident state = call.resident;
                if (state.index == null) { state.index = new CylinderOcclusionIndex(state.geometry, state.geometry.Length); window.indexPrepared = true; owner.Report.passes[kind].indexPreparations++; }
                else state.index.UpdateSnapshot(state.geometry);
                state.acceptedCount = state.index.Evaluate(state.multipliers, state.accepted);
                window.indexTicks = Stopwatch.GetTimestamp() - start; window.acceptedCylinders = state.acceptedCount;
                window.scalarCalls = state.index.ScalarCalls; window.nodeVisits = state.index.NodeVisits; window.conservativeMargin = state.index.ConservativeMargin;
                start = Stopwatch.GetTimestamp(); owner.Validate(call); window.validationTicks = Stopwatch.GetTimestamp() - start;
                if (call.verify)
                {
                    start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < state.acceptedCount; i++) { int row = state.accepted[i]; state.scratch[row].Setup(state.snapshot[row].row); }
                    window.comparisonTicks += Stopwatch.GetTimestamp() - start;
                    return false;
                }
            }
            catch (Exception error)
            {
                window.admissionCopyTicks = Math.Max(window.admissionCopyTicks, Stopwatch.GetTimestamp() - window.seamTicks - window.indexTicks - window.validationTicks - window.comparisonTicks);
                owner.Fallback(call, error); return false;
            }
            // No original restart is possible after the first write: Setup exceptions preserve partial publication.
            start = Stopwatch.GetTimestamp(); call.publicationStarted = true;
            try
            {
                Resident state = call.resident; int accepted = 0;
                for (int row = state.geometry.Length - 1; row >= 0; row--)
                {
                    Snapshot s = state.snapshot[row]; Multiplier(s.ptd, kind, state.multipliers[row]);
                    if (state.multipliers[row] > 0)
                    {
                        s.cylinder.Setup(s.row); state.liveArray[accepted] = s.cylinder; accepted++; Count(__instance, kind, accepted);
                    }
                }
                call.handled = true; owner.Report.replacements++;
                if (call.captured) { owner.Report.captureReplacements++; owner.Report.passes[kind].captureReplacements++; }
                return true;
            }
            catch (Exception error) { owner.Report.partialPublicationFailures++; owner.Fail(error); throw; }
            finally { window.publicationTicks = Stopwatch.GetTimestamp() - start; }
        }
        void Fallback(Invocation call, Exception error)
        {
            Report.fallbacks++; if (call.captured) Report.captureFallbacks++;
            Report.lastFallback = call.window.fallback = error.Message;
            // Admission/domain rejection keeps stock authoritative; every fallback disqualifies positive evidence.
            if (!(error is ArgumentException) && !(error is InvalidOperationException)) Fail(error);
        }
        void CompareOriginal(Invocation call)
        {
            Resident state = call.resident; Validate(call);
            Require(PublishedCount(call.integrator, call.kind) == state.acceptedCount, "Cylinder verification accepted count differs.");
            for (int i = 0; i < state.geometry.Length; i++)
                Require(Bits(Multiplier(state.snapshot[i].ptd, call.kind)) == Bits(state.multipliers[i]), "Cylinder verification multiplier differs at row " + i);
            for (int i = 0; i < state.acceptedCount; i++)
            {
                int row = state.accepted[i]; OcclusionCylinder actual = state.snapshot[row].cylinder, expected = state.scratch[row];
                Require(ReferenceEquals(state.liveArray[i], actual) && ReferenceEquals(actual.part, expected.part) && Same(actual.center, expected.center) &&
                    Bits(actual.radius) == Bits(expected.radius) && Same(actual.extents, expected.extents) && Same(actual.offset, expected.offset) &&
                    Bits(actual.cylNoseDot) == Bits(expected.cylNoseDot), "Cylinder verification fields/order differ at accepted slot " + i);
            }
            call.window.bitwiseMatch = true; Report.passes[call.kind].verifiedPasses = 1;
            if (call.captured) Report.passes[call.kind].capturedVerifiedPasses++;
        }
        static Exception Finalizer(Exception __exception, CallState __state)
        {
            CylinderOcclusionReplacement owner = active; Invocation call = __state.invocation as Invocation;
            if (owner == null || call == null) return __exception;
            long start = Stopwatch.GetTimestamp(); Window window = call.window;
            try
            {
                if (!ReferenceEquals(owner.current, call) || Thread.CurrentThread.ManagedThreadId != owner.threadId) throw new InvalidOperationException("Cylinder finalizer context drift.");
                if (__exception != null) { owner.Report.originalExceptions++; owner.Fail(__exception); }
                else if (window.seamTicks == 0) throw new InvalidOperationException("Cylinder fold seam did not execute.");
                else if (call.verify && window.fallback == null)
                {
                    long comparison = Stopwatch.GetTimestamp();
                    try { owner.CompareOriginal(call); }
                    catch (Exception error) { owner.Report.verificationMismatches++; window.mismatch = error.Message; owner.Fail(error); }
                    finally { window.comparisonTicks += Stopwatch.GetTimestamp() - comparison; }
                }
                if (!call.handled) window.acceptedCylinders = PublishedCount(call.integrator, call.kind);
                window.completed = __exception == null && window.fallback == null && window.mismatch == null && window.seamTicks != 0;
                window.captureActiveAtEnd = owner.capturing();
                if (call.captured && window.completed)
                {
                    if (window.treatment == "control-original") owner.Report.passes[call.kind].controlPasses++;
                    else if (window.treatment == "query") owner.Report.passes[call.kind].candidatePasses++;
                }
                if (window.firstInvocation) owner.Report.firstInvocations[call.kind] = window;
            }
            catch (Exception error) { owner.Fail(error); window.completed = false; }
            finally
            {
                owner.current = null; window.endedTicks = Stopwatch.GetTimestamp(); window.completeTicks = window.endedTicks - window.startedTicks;
                window.finalizerTicks = window.endedTicks - start;
            }
            return __exception;
        }
        void Fail(Exception error) { Report.errors++; Report.detail = error.ToString(); disabled = true; }
        public void Dispose()
        {
            if (disposed) return;
            Report.cleanupStartedTicks = Stopwatch.GetTimestamp();
            try
            {
                Require(attribution == null && current == null, "Cylinder owner cannot release before observer/pass.");
                if (installed) { RequireGraph(false); Report.precleanupPatchGraph = GraphEvidence(); Require(Report.installedPatchGraph.SequenceEqual(Report.precleanupPatchGraph), "Cylinder graph changed during owner lease."); }
            }
            catch (Exception error) { Fail(error); }
            try
            {
                harmony.UnpatchAll(PatchOwner);
                foreach (MethodInfo target in Targets) RequireEmpty(target);
                RequireEmpty(Setup); RequireEmpty(Scalar); RequireEmpty(Rectangle); RequireEmpty(Project);
                Report.restoredPatchGraph = GraphEvidence();
                Require(Report.preinstallPatchGraph.SequenceEqual(Report.restoredPatchGraph), "Cylinder original graphs were not restored.");
                Report.cleanupStatus = "removed-owned-hooks";
            }
            catch (Exception error) { Fail(error); Report.cleanupStatus = "cleanup-error"; }
            finally
            {
                installed = false; disposed = true; if (ReferenceEquals(active, this)) active = null;
                Report.windows = windows.Take(windowCount).ToArray(); Report.cleanupEndedTicks = Stopwatch.GetTimestamp();
                string reason; Report.status = FinalCoverage(out reason) ? "completed-experimental" : "invalid";
                if (Report.status == "invalid" && Report.detail == null) Report.detail = reason;
            }
        }
        bool FinalCoverage(out string reason)
        {
            reason = null;
            if (!disposed || Report.cleanupStatus != "removed-owned-hooks" || Report.errors != 0 || Report.originalExceptions != 0 || Report.verificationMismatches != 0 ||
                Report.partialPublicationFailures != 0 || Report.recursionStops != 0 || Report.droppedWindows != 0 || Report.fallbacks != 0 || Report.captureCalls == 0 ||
                Report.passes.Any(pass => pass.shapeAdmissions != 1 || pass.verifiedPasses != 1 || pass.indexPreparations < 1) ||
                Report.windows.Any(window => !window.completed || !window.captureActiveAtEnd || window.startedTicks <= 0 || window.endedTicks < window.seamTicks || window.seamTicks < window.startedTicks || window.completeTicks != window.endedTicks - window.startedTicks))
            { reason = "Cylinder lifecycle, coverage, bitwise proof or window validity failed."; return false; }
            foreach (PassReport pass in Report.passes)
            {
                if (Report.mode == "verify" && pass.capturedVerifiedPasses != 1 || Report.mode == "query" && pass.captureReplacements < 1 ||
                    Report.mode == "compare" && (pass.controlPasses < 1 || pass.candidatePasses < 1 || pass.controlPasses < pass.candidatePasses || pass.controlPasses > pass.candidatePasses + 1 || pass.captureCalls != pass.controlPasses + pass.candidatePasses))
                { reason = "Cylinder per-kind treatment coverage incomplete."; return false; }
            }
            return true;
        }
        public bool IsValidFinal(out string reason)
        { return FinalCoverage(out reason) && Report.status == "completed-experimental"; }
    }
}
