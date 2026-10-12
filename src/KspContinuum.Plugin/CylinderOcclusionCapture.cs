using System;
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
    internal sealed class CylinderOcclusionCapture : IDisposable
    {
        internal const string PatchOwner = "continuum.cylinder-occlusion-capture";
        const string StockHash = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        static readonly Guid Mvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        const int MaxRows = 1024, MaxPairs = MaxRows * (MaxRows - 1) / 2;
        static CylinderOcclusionCapture active;
        readonly Harmony harmony = new Harmony(PatchOwner);
        readonly Vessel vessel;
        readonly Func<bool> capturing;
        readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        readonly MethodInfo[] methods;
        readonly FieldInfo[] rowFields;
        readonly FieldInfo sunVector;
        readonly FieldInfo vesselField;
        readonly Pass[] passes = new Pass[2];
        readonly Dictionary<OcclusionData, int> indices = new Dictionary<OcclusionData, int>(MaxRows);
        readonly Part[] members;
        readonly HashSet<Part> memberSet;
        readonly HashSet<uint> initialIds = new HashSet<uint>();
        Pass current;
        FlightIntegrator integrator;
        bool disposed;
        int depth;

        [Serializable] public sealed class CaptureReport
        {
            public string status = "installed", cleanupStatus = "installed", detail, runId, vesselId, path, sha256;
            public int pid, capturedSolar, capturedBody, faults, originalExceptions, tapeOverflow;
            public long copyTicks, clockFrequency = Stopwatch.Frequency;
            public string scope = "intrusive original solar/body passes; pre-order, post-projection order, reached scalar tape and stock Setup outputs; export after owned cleanup";
        }
        [Serializable] public sealed class Recording
        {
            public string schema = "continuum-cylinder-occlusion-recording/v1", runId, vesselId, assemblySha256, assemblyMvid;
            public int processId;
            public long clockFrequency;
            public Pass[] passes;
        }
        [Serializable] public sealed class Pass
        {
            public string kind;
            public int sequence, contextFrame, rowCount, pairCount, cylinderCount;
            public long startTicks, endTicks, universalTimeBits;
            public float directionX, directionY, directionZ;
            public uint[] initialOrder;
            public Row[] rows;
            public Pair[] pairs;
            public Cylinder[] cylinders;
        }
        [Serializable] public sealed class Row
        {
            public uint flightId;
            public int partInstanceId;
            public string partName;
            public float minX, minY, maxX, maxY, centerX, centerY, extentX, extentY;
            public float projectedCenterX, projectedCenterY, projectedCenterZ;
            public long minimumDotBits, maximumDotBits, centroidDotBits, projectedAreaBits, projectedRadiusBits, maxWidthDepthBits, expectedMultiplierBits;
        }
        [Serializable] public struct Pair { public int row, cylinderRow; public long resultBits; }
        [Serializable] public struct Cylinder
        {
            public int row;
            public float centerX, centerY, centerZ, offsetX, offsetY, extentX, extentY;
            public long radiusBits, cylNoseDotBits;
        }
        public CaptureReport Report { get; private set; }

        CylinderOcclusionCapture(string path, string runId, Func<bool> capturing)
        {
            this.capturing = capturing; vessel = FlightGlobals.ActiveVessel;
            Report = new CaptureReport { runId = runId, vesselId = vessel.id.ToString("D"), pid = Process.GetCurrentProcess().Id, path = path };
            methods = new[] { Pin(typeof(FlightIntegrator), "UpdateOcclusionSolar", 100677507, typeof(void), Type.EmptyTypes),
                Pin(typeof(FlightIntegrator), "UpdateOcclusionBody", 100677508, typeof(void), Type.EmptyTypes) };
            Pin(typeof(OcclusionData), "GetCylinderOcclusion", 100677561, typeof(double), new[] { typeof(OcclusionCylinder) });
            Pin(typeof(OcclusionCylinder), "Setup", 100677572, typeof(void), new[] { typeof(OcclusionData) });
            rowFields = new[] { AccessTools.Field(typeof(FlightIntegrator), "occlusionSun"), AccessTools.Field(typeof(FlightIntegrator), "occlusionBody") };
            sunVector = AccessTools.Field(typeof(FlightIntegrator), "sunVector");
            vesselField = AccessTools.Field(typeof(VesselModule), "vessel");
            if (active != null || string.IsNullOrEmpty(runId) || capturing == null || !Path.IsPathRooted(path) || File.Exists(path) ||
                !Directory.Exists(Path.GetDirectoryName(path)) || rowFields.Any(f => f == null || f.FieldType != typeof(List<OcclusionData>)) ||
                sunVector == null || sunVector.FieldType != typeof(Vector3) || vesselField == null || vesselField.FieldType != typeof(Vessel) || Digest(typeof(FlightIntegrator).Assembly.Location) != StockHash)
                throw new InvalidOperationException("Cylinder capture admission differs.");
            foreach (MethodInfo method in methods) RequireEmpty(method);
            RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionData), "GetCylinderOcclusion"));
            RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionCylinder), "Setup"));
            members = vessel.parts.ToArray();
            if (members.Length < 1 || members.Length > MaxRows || members.Any(p => p == null) || members.Distinct().Count() != members.Length)
                throw new InvalidOperationException("Cylinder capture topology exceeds bounds.");
            memberSet = new HashSet<Part>(members);
            for (int i = 0; i < 2; i++)
            {
                passes[i] = new Pass { kind = i == 0 ? "solar" : "body", sequence = i, initialOrder = new uint[MaxRows],
                    rows = new Row[MaxRows], pairs = new Pair[MaxPairs], cylinders = new Cylinder[MaxRows] };
                for (int r = 0; r < MaxRows; r++) passes[i].rows[r] = new Row();
            }
            active = this;
            try
            {
                foreach (MethodInfo method in methods) harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CylinderOcclusionCapture), "Prefix")),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CylinderOcclusionCapture), "Finalizer")),
                    transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CylinderOcclusionCapture), "Transpile")));
            }
            catch { try { harmony.UnpatchAll(PatchOwner); } finally { active = null; } throw; }
        }
        public static CylinderOcclusionCapture Create(string[] arguments, string runId, Func<bool> capturing)
        {
            string path = Environment.GetEnvironmentVariable("CONTINUUM_CYLINDER_OCCLUSION_CAPTURE");
            if (string.IsNullOrEmpty(path)) return null;
            if (FlightGlobals.ActiveVessel == null) throw new InvalidOperationException("Cylinder capture requires an active vessel.");
            return new CylinderOcclusionCapture(path, runId, capturing);
        }
        static MethodInfo Pin(Type type, string name, int token, Type result, Type[] parameters)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null || method.IsStatic || method.DeclaringType != type || method.ReturnType != result ||
                method.Module.ModuleVersionId != Mvid || method.MetadataToken != token)
                throw new InvalidOperationException("Cylinder original method differs: " + name);
            return method;
        }
        static void RequireEmpty(MethodBase method)
        {
            Patches graph = Harmony.GetPatchInfo(method);
            if (graph != null && graph.Owners.Any()) throw new InvalidOperationException("Cylinder capture foreign patches: " + method.Name);
        }
        internal static bool IsCapturePatch(Patch patch, MethodBase method, string category)
        {
            string name = category == "prefix" ? "Prefix" : category == "finalizer" ? "Finalizer" : category == "transpiler" ? "Transpile" : null;
            return name != null && method.DeclaringType == typeof(FlightIntegrator) && (method.MetadataToken == 100677507 || method.MetadataToken == 100677508) &&
                method.Module.ModuleVersionId == Mvid && patch.owner == PatchOwner && patch.priority == Priority.Normal &&
                SameMethod(patch.PatchMethod, AccessTools.DeclaredMethod(typeof(CylinderOcclusionCapture), name)) && patch.before.Length == 0 && patch.after.Length == 0;
        }
        static bool SameMethod(MethodBase left, MethodBase right)
        {
            return left != null && right != null && left.Module == right.Module && left.MetadataToken == right.MetadataToken && left.DeclaringType == right.DeclaringType;
        }
        internal static bool HasExactCapturePatch(Patches graph, MethodBase method, bool observerInstalled)
        {
            CylinderOcclusionCapture owner = active;
            if (owner == null || owner.disposed || graph == null || !owner.methods.Any(m => SameMethod(m, method))) return false;
            if (graph.Prefixes.Count(p => IsCapturePatch(p, method, "prefix")) != 1 ||
                graph.Finalizers.Count(p => IsCapturePatch(p, method, "finalizer")) != 1 ||
                graph.Transpilers.Count(p => IsCapturePatch(p, method, "transpiler")) != 1 || graph.Postfixes.Count != 0) return false;
            if (!observerInstalled) return graph.Prefixes.Count == 1 && graph.Finalizers.Count == 1 && graph.Transpilers.Count == 1;
            return graph.Prefixes.Count == 2 && graph.Finalizers.Count == 2 && graph.Transpilers.Count == 1 &&
                FlightIntegratorUpdateAttribution.HasExactObserverPatch(graph, method);
        }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> source)
        {
            var instructions = source.ToList();
            MethodInfo scalar = AccessTools.DeclaredMethod(typeof(OcclusionData), "GetCylinderOcclusion"), setup = AccessTools.DeclaredMethod(typeof(OcclusionCylinder), "Setup");
            int scalars = 0, setups = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(scalar)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(CylinderOcclusionCapture), "Scalar"); scalars++; }
                else if (instruction.Calls(setup)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(CylinderOcclusionCapture), "Setup"); setups++; }
            }
            if (scalars != 1 || setups != 2) throw new InvalidOperationException("Cylinder original caller shape differs.");
            return instructions;
        }
        void Topology()
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId || FlightGlobals.ActiveVessel != vessel || vessel.parts.Count != members.Length)
                throw new InvalidOperationException("Cylinder capture epoch owner changed.");
            for (int i = 0; i < members.Length; i++) if (members[i] == null || vessel.parts[i] != members[i] || members[i].vessel != vessel)
                throw new InvalidOperationException("Cylinder capture topology changed.");
        }
        static void Prefix(FlightIntegrator __instance, MethodBase __originalMethod, out bool __state)
        {
            __state = false; CylinderOcclusionCapture owner = active;
            if (owner == null || owner.disposed || owner.Report.faults != 0) return;
            int slot = __originalMethod.MetadataToken == 100677507 ? 0 : 1;
            if ((slot == 0 ? owner.Report.capturedSolar : owner.Report.capturedBody) != 0) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (!owner.capturing()) return;
                Patches graph = Harmony.GetPatchInfo(__originalMethod);
                if (!HasExactCapturePatch(graph, __originalMethod, false) && !HasExactCapturePatch(graph, __originalMethod, true))
                    throw new InvalidOperationException("Cylinder caller patch graph changed.");
                RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionData), "GetCylinderOcclusion"));
                RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionCylinder), "Setup"));
                if (owner.depth != 0 || !ReferenceEquals(owner.vesselField.GetValue(__instance), owner.vessel)) throw new InvalidOperationException("Cylinder capture nested or foreign integrator.");
                owner.Topology();
                Pass pass = owner.passes[slot]; var rows = (List<OcclusionData>)owner.rowFields[slot].GetValue(__instance);
                if (rows.Count < 1 || rows.Count > MaxRows) throw new InvalidOperationException("Cylinder capture row bound.");
                pass.rowCount = rows.Count; pass.startTicks = start; pass.contextFrame = Time.frameCount; pass.universalTimeBits = Bits(Planetarium.GetUniversalTime());
                Vector3 direction = slot == 0 ? (Vector3)owner.sunVector.GetValue(__instance) : (Vector3)(-owner.vessel.upAxis);
                pass.directionX = direction.x; pass.directionY = direction.y; pass.directionZ = direction.z;
                owner.initialIds.Clear();
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] == null || rows[i].part == null || !owner.memberSet.Contains(rows[i].part) || rows[i].part.vessel != owner.vessel || !owner.initialIds.Add(rows[i].part.flightID)) throw new InvalidOperationException("Cylinder initial row owner differs.");
                    pass.initialOrder[i] = rows[i].part.flightID;
                }
                owner.current = pass; owner.integrator = __instance; owner.depth = 1; __state = true;
            }
            catch (Exception error) { owner.Fail(error); }
            finally { owner.Report.copyTicks += Stopwatch.GetTimestamp() - start; }
        }
        static double Scalar(OcclusionData row, OcclusionCylinder cylinder)
        {
            double value = row.GetCylinderOcclusion(cylinder);
            CylinderOcclusionCapture owner = active;
            if (owner != null && owner.current != null && owner.Report.faults == 0)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    int at, from;
                    if (!owner.indices.TryGetValue(row, out at) || cylinder == null || !owner.indices.TryGetValue(cylinder.part, out from)) throw new InvalidOperationException("Cylinder scalar owner missing.");
                    if (owner.current.pairCount >= MaxPairs) { owner.Report.tapeOverflow++; throw new InvalidOperationException("Cylinder pair tape bound."); }
                    if (!Finite(value)) throw new InvalidOperationException("Cylinder original scalar returned nonfinite value.");
                    owner.current.pairs[owner.current.pairCount++] = new Pair { row = at, cylinderRow = from, resultBits = Bits(value) };
                }
                catch (Exception error) { owner.Fail(error); }
                finally { owner.Report.copyTicks += Stopwatch.GetTimestamp() - start; }
            }
            return value;
        }
        static void Setup(OcclusionCylinder cylinder, OcclusionData row)
        {
            CylinderOcclusionCapture owner = active;
            if (owner != null && owner.current != null && owner.Report.faults == 0 && owner.current.cylinderCount == 0)
            {
                long start = Stopwatch.GetTimestamp();
                try { owner.Projected(); } catch (Exception error) { owner.Fail(error); }
                finally { owner.Report.copyTicks += Stopwatch.GetTimestamp() - start; }
            }
            cylinder.Setup(row);
            if (owner != null && owner.current != null && owner.Report.faults == 0)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    int index;
                    if (!owner.indices.TryGetValue(row, out index) || cylinder.part != row || owner.current.cylinderCount >= owner.current.rowCount)
                        throw new InvalidOperationException("Cylinder Setup owner/count differs.");
                    if (!Finite(cylinder.radius) || !Finite(cylinder.cylNoseDot) || !Finite(cylinder.center.x) || !Finite(cylinder.center.y) || !Finite(cylinder.center.z) ||
                        !Finite(cylinder.offset.x) || !Finite(cylinder.offset.y) || !Finite(cylinder.extents.x) || !Finite(cylinder.extents.y))
                        throw new InvalidOperationException("Cylinder original Setup published nonfinite fields.");
                    owner.current.cylinders[owner.current.cylinderCount++] = new Cylinder { row = index, centerX = cylinder.center.x, centerY = cylinder.center.y, centerZ = cylinder.center.z,
                        offsetX = cylinder.offset.x, offsetY = cylinder.offset.y, extentX = cylinder.extents.x, extentY = cylinder.extents.y,
                        radiusBits = Bits(cylinder.radius), cylNoseDotBits = Bits(cylinder.cylNoseDot) };
                }
                catch (Exception error) { owner.Fail(error); }
                finally { owner.Report.copyTicks += Stopwatch.GetTimestamp() - start; }
            }
        }
        void Projected()
        {
            Topology(); int slot = current.kind == "solar" ? 0 : 1;
            var rows = (List<OcclusionData>)rowFields[slot].GetValue(integrator);
            if (rows.Count != current.rowCount) throw new InvalidOperationException("Cylinder thermal graph changed within pass.");
            indices.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                OcclusionData source = rows[i];
                if (source == null || source.part == null || source.ptd == null || source.ptd.part != source.part || !memberSet.Contains(source.part) || !initialIds.Contains(source.part.flightID) || source.part.vessel != vessel || indices.ContainsKey(source))
                    throw new InvalidOperationException("Cylinder projected row owner differs.");
                indices.Add(source, i); Row row = current.rows[i];
                row.flightId = source.part.flightID; row.partInstanceId = source.part.GetInstanceID(); row.partName = source.part.name;
                row.minX = source.minExtents.x; row.minY = source.minExtents.y; row.maxX = source.maxExtents.x; row.maxY = source.maxExtents.y;
                row.centerX = source.center.x; row.centerY = source.center.y; row.extentX = source.extents.x; row.extentY = source.extents.y;
                row.projectedCenterX = source.projectedCenter.x; row.projectedCenterY = source.projectedCenter.y; row.projectedCenterZ = source.projectedCenter.z;
                row.minimumDotBits = Bits(source.minimumDot); row.maximumDotBits = Bits(source.maximumDot); row.centroidDotBits = Bits(source.centroidDot);
                row.projectedAreaBits = Bits(source.projectedArea); row.projectedRadiusBits = Bits(source.projectedRadius); row.maxWidthDepthBits = Bits(source.maxWidthDepth);
                if (!Finite(source.minimumDot) || !Finite(source.maximumDot) || !Finite(source.centroidDot) || !Finite(source.projectedArea) || !Finite(source.projectedRadius) || !Finite(source.maxWidthDepth))
                    throw new InvalidOperationException("Cylinder projected double is nonfinite; original remains authoritative.");
                if (!Finite(row.minX) || !Finite(row.minY) || !Finite(row.maxX) || !Finite(row.maxY) || !Finite(row.centerX) || !Finite(row.centerY) ||
                    !Finite(row.extentX) || !Finite(row.extentY) || !Finite(row.projectedCenterX) || !Finite(row.projectedCenterY) || !Finite(row.projectedCenterZ))
                    throw new InvalidOperationException("Cylinder projected float is nonfinite; original remains authoritative.");
            }
        }
        static Exception Finalizer(Exception __exception, bool __state)
        {
            CylinderOcclusionCapture owner = active;
            if (!__state || owner == null) return __exception;
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (__exception != null) { owner.Report.originalExceptions++; owner.Fail(__exception); }
                else if (owner.Report.faults == 0)
                {
                    owner.Topology();
                    if (!owner.capturing() || owner.current.cylinderCount < 1) throw new InvalidOperationException("Cylinder pass not fully enclosed in capture window.");
                    int slot = owner.current.kind == "solar" ? 0 : 1;
                    var rows = (List<OcclusionData>)owner.rowFields[slot].GetValue(owner.integrator);
                    if (rows.Count != owner.current.rowCount) throw new InvalidOperationException("Cylinder final rows changed.");
                    int acceptedCount = slot == 0 ? owner.integrator.occludersSunCount : owner.integrator.occludersBodyCount;
                    OcclusionCylinder[] accepted = slot == 0 ? owner.integrator.occludersSun : owner.integrator.occludersBody;
                    if (acceptedCount != owner.current.cylinderCount) throw new InvalidOperationException("Cylinder accepted count differs from Setup tape.");
                    for (int i = 0; i < acceptedCount; i++)
                    {
                        int index;
                        if (accepted[i] == null || !owner.indices.TryGetValue(accepted[i].part, out index) || index != owner.current.cylinders[i].row)
                            throw new InvalidOperationException("Cylinder accepted order differs from Setup tape.");
                    }
                    for (int i = 0; i < rows.Count; i++)
                    {
                        int index;
                        if (!owner.indices.TryGetValue(rows[i], out index) || index != i || rows[i].part.flightID != owner.current.rows[i].flightId)
                            throw new InvalidOperationException("Cylinder final order changed.");
                        double result = slot == 0 ? rows[i].ptd.sunAreaMultiplier : rows[i].ptd.bodyAreaMultiplier;
                        if (!Finite(result)) throw new InvalidOperationException("Cylinder final multiplier is nonfinite.");
                        owner.current.rows[i].expectedMultiplierBits = Bits(result);
                    }
                    owner.current.endTicks = Stopwatch.GetTimestamp();
                    if (slot == 0) owner.Report.capturedSolar++; else owner.Report.capturedBody++;
                }
            }
            catch (Exception error) { owner.Fail(error); }
            finally { owner.current = null; owner.integrator = null; owner.depth = 0; owner.indices.Clear(); owner.Report.copyTicks += Stopwatch.GetTimestamp() - start; }
            return __exception;
        }
        void Fail(Exception error) { Report.faults++; Report.status = "invalid"; Report.detail = error.GetType().Name + ": " + error.Message; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static long Bits(double value) { return BitConverter.DoubleToInt64Bits(value); }
        static string Digest(string path)
        {
            using (SHA256 hash = SHA256.Create()) using (FileStream file = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        static T[] Trim<T>(T[] input, int count) { var output = new T[count]; Array.Copy(input, output, count); return output; }
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                foreach (MethodInfo method in methods) if (!HasExactCapturePatch(Harmony.GetPatchInfo(method), method, false))
                    throw new InvalidOperationException("Cylinder precleanup graph differs; narrow observer must be disposed first.");
                RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionData), "GetCylinderOcclusion"));
                RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionCylinder), "Setup"));
            }
            catch (Exception error) { Fail(error); }
            disposed = true;
            try
            {
                harmony.UnpatchAll(PatchOwner);
                bool empty = methods.Concat(new[] { AccessTools.DeclaredMethod(typeof(OcclusionData), "GetCylinderOcclusion"), AccessTools.DeclaredMethod(typeof(OcclusionCylinder), "Setup") })
                    .All(method => { Patches graph = Harmony.GetPatchInfo(method); return graph == null || !graph.Owners.Any(); });
                Report.cleanupStatus = empty ? "removed-owned-patches" : "cleanup-error";
                RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionData), "GetCylinderOcclusion"));
                RequireEmpty(AccessTools.DeclaredMethod(typeof(OcclusionCylinder), "Setup"));
                if (!empty || current != null) throw new InvalidOperationException("Cylinder cleanup incomplete.");
                if (Report.faults != 0 || Report.capturedSolar != 1 || Report.capturedBody != 1) { Report.status = "invalid"; return; }
                foreach (Pass pass in passes) { pass.initialOrder = Trim(pass.initialOrder, pass.rowCount); pass.rows = Trim(pass.rows, pass.rowCount); pass.pairs = Trim(pass.pairs, pass.pairCount); pass.cylinders = Trim(pass.cylinders, pass.cylinderCount); }
                var recording = new Recording { runId = Report.runId, vesselId = Report.vesselId, processId = Report.pid, clockFrequency = Stopwatch.Frequency,
                    assemblySha256 = StockHash, assemblyMvid = Mvid.ToString("D"), passes = passes };
                if (File.Exists(Report.path)) throw new InvalidOperationException("Cylinder capture destination appeared during run.");
                string encoded = ReportJson.Encode(recording);
                using (var stream = new FileStream(Report.path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(stream)) writer.Write(encoded);
                Report.sha256 = Digest(Report.path); Report.status = "complete";
            }
            catch (Exception error) { Fail(error); }
            finally { if (ReferenceEquals(active, this)) active = null; }
        }
    }
}
