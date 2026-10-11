using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class ConvectionReplacement : IDisposable
    {
        const string Owner = "continuum.convection-sparse-replacement";
        const string StockHash = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        static readonly MethodInfo Target = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateOcclusionConvection");
        static readonly FieldInfo Rows = AccessTools.Field(typeof(FlightIntegrator), "occlusionConv");
        static readonly FieldInfo Count = AccessTools.Field(typeof(FlightIntegrator), "partThermalDataCount");
        static readonly FieldInfo Mach = AccessTools.Field(typeof(FlightIntegrator), "mach");
        static readonly MethodInfo Sqrt = AccessTools.DeclaredMethod(typeof(Math), "Sqrt", new[] { typeof(double) });
        static readonly MethodInfo Setup = AccessTools.DeclaredMethod(typeof(OcclusionCone), "Setup");
        static readonly MethodInfo Stats = AccessTools.DeclaredMethod(typeof(OcclusionData), "GetShockStats");
        static readonly MethodInfo FxGetter = AccessTools.PropertyGetter(typeof(FXCamera), "Instance");
        static readonly MethodInfo FxApply = AccessTools.DeclaredMethod(typeof(FXCamera), "ApplyObliqueness", new[] { typeof(float) });
        static ConvectionReplacement active;
        [ThreadStatic] static Invocation current;
        readonly Harmony harmony = new Harmony(Owner);
        readonly ConvectionSparseKernel kernel;
        readonly Action<FlightIntegrator> stockFold;
        readonly Func<bool> capturing;
        readonly Vessel vessel;
        readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        bool disposed, disabled;
        long sequence;

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-convection-replacement-v1", mode, runId, vesselId;
            public string status = "installed", cleanupStatus = "installed", detail, lastFallback;
            public string assemblySha256 = StockHash, assemblyMvid = ConvectionSparseKernel.Mvid.ToString("D"), integratorIlSha256, scalarIlSha256 = ConvectionSparseKernel.ScalarIlHash;
            public string scope = "bounded experimental convection fold; projection includes original graph/project/sort plus common hook bookkeeping; candidate copy includes candidate-only seam admission; one original FX call; verification/report instrumentation tax is separate, intrusive and unqualified; first stock and candidate calls include their cold JIT; no FPS claim";
            public bool hookShapeVerified;
            public int processId, pairedSamples;
            public long clockFrequency = Stopwatch.Frequency, calls, captureCalls, replacements, captureReplacements, fallbacks, captureFallbacks, faults, recursionFallbacks, verificationMismatches;
            public long candidateCopyTicks, candidateFoldTicks, candidateCommitTicks, candidateCompleteTicks, projectionTicks, commonFxTicks, methodInclusiveTicks;
            public Sample[] samples = new Sample[2];
        }
        [Serializable] public sealed class Sample
        {
            public long sequence;
            public double universalTime, mach;
            public int parts, acceptedCones;
            public string order, mismatch;
            public bool bitwiseMatch, candidateFirstCall, stockFirstCall;
            public long projectionTicks, commonFxTicks, stockDirectFoldTicks, stockCompleteTicks;
            public long candidateCopyTicks, candidateFoldTicks, candidateCommitTicks, candidateCompleteTicks;
            public long snapshotTicks, rollbackTicks, comparisonTicks, methodInclusiveTicks;
            public long scalarCalls, reachedPairs, areaQueries, geometryQueries, certificateProbes, certificateAnchors, certificateSteps, nodeVisits, unitAreaCones;
        }
        sealed class Invocation
        {
            internal ConvectionReplacement owner;
            internal Invocation previous;
            internal FlightIntegrator integrator;
            internal bool captured, eligible, handled;
            internal long started, projected, fxStarted;
            internal Sample sample;
        }
        sealed class RollbackFailure : Exception
        {
            internal RollbackFailure(Exception error) : base("Original live state could not be restored.", error) { }
        }
        public ReportData Report { get; private set; }

        ConvectionReplacement(string mode, string runId, Func<bool> capturing)
        {
            this.capturing = capturing; vessel = FlightGlobals.ActiveVessel;
            Report = new ReportData { mode = mode, runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id };
            StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);
            ConvectionSparseKernel.Pin(Target, typeof(FlightIntegrator), 100677506);
            ConvectionSparseKernel.Require(active == null && Rows != null && Count != null && Mach != null &&
                Target.ReturnType == typeof(void) && Target.GetParameters().Length == 0 && Target.GetMethodBody().ExceptionHandlingClauses.Count == 0, "Convection live seam shape differs.");
            Report.integratorIlSha256 = ConvectionSparseKernel.Hash(Target.GetMethodBody().GetILAsByteArray());
            kernel = new ConvectionSparseKernel();
            stockFold = ConvectionSparseKernel.Emit<Action<FlightIntegrator>>(Target, AccessTools.DeclaredMethod(typeof(ConvectionReplacement), "StockFold"));
            ConvectionSparseKernel.Require(stockFold != null, "Original direct fold delegate unavailable.");
            active = this;
            try
            {
                harmony.Patch(Target, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ConvectionReplacement), "Prefix")),
                    transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ConvectionReplacement), "Hook")),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ConvectionReplacement), "Finalizer")));
                ConvectionSparseKernel.Require(Report.hookShapeVerified, "Convection live hook was not admitted.");
            }
            catch { harmony.UnpatchAll(Owner); active = null; throw; }
        }
        public static ConvectionReplacement Create(string[] args, string runId, Func<bool> capturing)
        {
            const string prefix = "--continuum-convection-replacement=";
            string[] selected = args.Where(arg => arg.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (selected.Length == 0) return null;
            string mode = selected.Length == 1 ? selected[0].Substring(prefix.Length) : "";
            if ((mode != "query" && mode != "verify") || string.IsNullOrEmpty(runId) || capturing == null || FlightGlobals.ActiveVessel == null)
                throw new InvalidOperationException("Convection replacement requires one query/verify mode and a bound flight.");
            return new ConvectionReplacement(mode, runId, capturing);
        }
        static bool LoadLocal(CodeInstruction code) { return code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S || code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Ldloc_3; }
        static CodeInstruction StoreFor(CodeInstruction load)
        {
            if (load.opcode == OpCodes.Ldloc_0) return new CodeInstruction(OpCodes.Stloc_0);
            if (load.opcode == OpCodes.Ldloc_1) return new CodeInstruction(OpCodes.Stloc_1);
            if (load.opcode == OpCodes.Ldloc_2) return new CodeInstruction(OpCodes.Stloc_2);
            if (load.opcode == OpCodes.Ldloc_3) return new CodeInstruction(OpCodes.Stloc_3);
            return new CodeInstruction(OpCodes.Stloc, load.operand);
        }
        static CodeInstruction[] Shape(IEnumerable<CodeInstruction> source, out int seam, out int fx, out int fxEnd, out CodeInstruction rowLocal)
        {
            var code = source.Select(item => new CodeInstruction(item)).ToArray();
            int sqrt = Array.FindIndex(code, item => item.Calls(Sqrt)); seam = sqrt - 2;
            fx = Array.FindIndex(code, item => item.Calls(FxGetter)); fxEnd = Array.FindIndex(code, item => item.Calls(FxApply));
            ConvectionSparseKernel.Require(sqrt >= 2 && code.Count(item => item.Calls(Sqrt)) == 1 && code[seam].opcode == OpCodes.Ldarg_0 &&
                code[seam + 1].opcode == OpCodes.Ldfld && Equals(code[seam + 1].operand, Mach) && fx > sqrt && fxEnd > fx && fxEnd + 1 < code.Length &&
                code.Count(item => item.Calls(FxGetter)) == 1 && code.Count(item => item.Calls(FxApply)) == 1 && code.Count(item => item.Calls(Setup)) == 2 &&
                code.Count(item => item.Calls(Stats)) == 1 && code.All(item => item.blocks.Count == 0), "Convection prelude/fold/FX instruction shape differs.");
            MethodInfo itemGetter = AccessTools.PropertyGetter(typeof(List<OcclusionData>), "Item");
            int firstRow = Array.FindIndex(code, seam, item => item.Calls(itemGetter));
            ConvectionSparseKernel.Require(firstRow > seam && firstRow < fx && LoadLocal(code[firstRow - 1]), "Original processing-order local differs.");
            rowLocal = new CodeInstruction(code[firstRow - 1]);
            ConvectionSparseKernel.Require(code[seam].labels.Count <= 1 && Enumerable.Range(fx + 1, fxEnd - fx).All(i => code[i].labels.Count == 0), "Convection FX expression has unexpected entry labels.");
            return code;
        }
        static IEnumerable<CodeInstruction> StockFold(IEnumerable<CodeInstruction> source)
        {
            int seam, fx, fxEnd; CodeInstruction row;
            CodeInstruction[] code = Shape(source, out seam, out fx, out fxEnd, out row);
            var result = new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Count),
                new CodeInstruction(OpCodes.Ldc_I4_1), new CodeInstruction(OpCodes.Sub), StoreFor(row) };
            code[fxEnd + 1].labels.AddRange(code[fx].labels);
            for (int i = seam; i < code.Length; i++) if (i < fx || i > fxEnd) result.Add(code[i]);
            var labels = new HashSet<Label>(result.SelectMany(item => item.labels));
            foreach (CodeInstruction item in result)
            {
                if (item.operand is Label label) ConvectionSparseKernel.Require(labels.Contains(label), "Original fold branches outside its retained boundary.");
                if (item.operand is Label[] choices) foreach (Label choice in choices) ConvectionSparseKernel.Require(labels.Contains(choice), "Original fold switch leaves its retained boundary.");
            }
            return result;
        }
        static IEnumerable<CodeInstruction> Hook(IEnumerable<CodeInstruction> source, ILGenerator generator)
        {
            int seam, fx, fxEnd; CodeInstruction ignored;
            CodeInstruction[] code = Shape(source, out seam, out fx, out fxEnd, out ignored);
            LocalBuilder handled = generator.DeclareLocal(typeof(bool)); Label fxLabel = generator.DefineLabel(), next = generator.DefineLabel();
            var result = new List<CodeInstruction>();
            for (int i = 0; i < code.Length; i++)
            {
                if (i == seam)
                {
                    var enter = new CodeInstruction(OpCodes.Ldarg_0); enter.labels.AddRange(code[i].labels); code[i].labels.Clear(); result.Add(enter);
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ConvectionReplacement), "TryAtSeam")));
                    result.Add(new CodeInstruction(OpCodes.Stloc, handled)); result.Add(new CodeInstruction(OpCodes.Ldloc, handled)); result.Add(new CodeInstruction(OpCodes.Brtrue, fxLabel));
                }
                if (i == fx)
                {
                    var begin = new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ConvectionReplacement), "BeginFx"));
                    begin.labels.Add(fxLabel); begin.labels.AddRange(code[i].labels); code[i].labels.Clear(); result.Add(begin);
                }
                if (i == fxEnd + 1) code[i].labels.Add(next);
                result.Add(code[i]);
                if (i == fxEnd)
                {
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ConvectionReplacement), "EndFx")));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, handled)); result.Add(new CodeInstruction(OpCodes.Brfalse, next)); result.Add(new CodeInstruction(OpCodes.Ret));
                }
            }
            ConvectionSparseKernel.Require(active != null, "Convection hook owner missing."); active.Report.hookShapeVerified = true; return result;
        }
        static void Prefix(FlightIntegrator __instance, out Invocation __state)
        {
            long start = Stopwatch.GetTimestamp(); var owner = active;
            __state = new Invocation { owner = owner, integrator = __instance, previous = current, started = start }; current = __state;
            if (owner == null || owner.disposed || owner.disabled) return;
            if (Thread.CurrentThread.ManagedThreadId != owner.threadId || !ReferenceEquals(__instance.Vessel, owner.vessel) || FlightGlobals.ActiveVessel != owner.vessel) return;
            __state.captured = owner.capturing(); owner.Report.calls++; if (__state.captured) owner.Report.captureCalls++;
            if (__state.previous != null) { owner.Report.recursionFallbacks++; return; }
            __state.eligible = true;
        }
        static Exception Finalizer(Exception __exception, Invocation __state)
        {
            if (__state == null) return __exception;
            try
            {
                if (__state.eligible && __state.owner != null)
                {
                    long elapsed = Stopwatch.GetTimestamp() - __state.started;
                    if (__state.captured) __state.owner.Report.methodInclusiveTicks += elapsed;
                    if (__state.sample != null) __state.sample.methodInclusiveTicks = elapsed;
                    if (__exception != null) __state.owner.Fault(__exception);
                }
            }
            finally { current = __state.previous; }
            return __exception;
        }
        void Fault(Exception error) { disabled = true; Report.faults++; Report.status = "invalid"; Report.detail = error.GetType().Name + ": " + error.Message; }
        void Fallback(Invocation invocation, string reason)
        {
            Report.fallbacks++; if (invocation.captured) Report.captureFallbacks++; Report.lastFallback = reason;
        }
        static bool TryAtSeam(FlightIntegrator integrator)
        {
            Invocation invocation = current; ConvectionReplacement owner = invocation == null ? null : invocation.owner;
            if (owner == null || !invocation.eligible || owner.disabled || !ReferenceEquals(invocation.integrator, integrator)) return false;
            long admissionStarted = Stopwatch.GetTimestamp(); invocation.projected = admissionStarted - invocation.started;
            if (owner.Report.mode == "verify" && (!invocation.captured || owner.Report.pairedSamples >= 2)) return false;
            try
            {
                var patches = Harmony.GetPatchInfo(Target);
                if (patches == null || patches.Owners.Any(id => id != Owner)) throw new ConvectionSparseKernel.Unsupported("foreign-live-hook");
                var rows = (List<OcclusionData>)Rows.GetValue(integrator);
                if (rows == null || rows.Count < 1 || rows.Count > ConvectionFormat.MaxRows || (int)Count.GetValue(integrator) != rows.Count)
                    throw new ConvectionSparseKernel.Unsupported("live-row-count");
                long admissionTicks = Stopwatch.GetTimestamp() - admissionStarted;
                if (owner.Report.mode == "verify") return owner.Verify(integrator, rows, invocation, admissionTicks);
                long started = Stopwatch.GetTimestamp(), copied, folded;
                var input = new ConvectionSparseKernel.Input(rows, owner.vessel, integrator.mach); copied = Stopwatch.GetTimestamp();
                ConvectionSparseKernel.Result result = owner.kernel.Run(input); folded = Stopwatch.GetTimestamp();
                ConvectionSparseKernel.Publish(integrator, result); long committed = Stopwatch.GetTimestamp();
                if (invocation.captured)
                {
                    owner.Report.candidateCopyTicks += admissionTicks + copied - started; owner.Report.candidateFoldTicks += folded - copied;
                    owner.Report.candidateCommitTicks += committed - folded; owner.Report.candidateCompleteTicks += invocation.projected + admissionTicks + committed - started;
                    owner.Report.projectionTicks += invocation.projected;
                }
                invocation.handled = true; owner.Report.replacements++; if (invocation.captured) owner.Report.captureReplacements++; return true;
            }
            catch (RollbackFailure error) { owner.Fault(error); throw; }
            catch (ConvectionSparseKernel.Unsupported error) { owner.Fallback(invocation, error.Message); return false; }
            catch (Exception error) { owner.Fault(error); owner.Fallback(invocation, "unexpected-exception"); return false; }
        }
        static void BeginFx() { if (current != null && current.handled) current.fxStarted = Stopwatch.GetTimestamp(); }
        static void EndFx()
        {
            Invocation invocation = current; if (invocation == null || !invocation.handled) return;
            long elapsed = Stopwatch.GetTimestamp() - invocation.fxStarted;
            if (invocation.captured)
            {
                invocation.owner.Report.commonFxTicks += elapsed;
                if (invocation.owner.Report.mode == "query") invocation.owner.Report.candidateCompleteTicks += elapsed;
            }
            if (invocation.sample != null)
            {
                invocation.sample.commonFxTicks = elapsed; invocation.sample.stockCompleteTicks += elapsed; invocation.sample.candidateCompleteTicks += elapsed;
            }
        }
        bool Verify(FlightIntegrator integrator, List<OcclusionData> rows, Invocation invocation, long admissionTicks)
        {
            var sample = new Sample { sequence = sequence++, universalTime = Planetarium.GetUniversalTime(), mach = integrator.mach, parts = rows.Count,
                projectionTicks = invocation.projected, order = Report.pairedSamples == 0 ? "stock-candidate" : "candidate-stock", candidateFirstCall = Report.replacements == 0, stockFirstCall = Report.pairedSamples == 0 };
            long start = Stopwatch.GetTimestamp(); var initial = new State(integrator, rows); sample.snapshotTicks += Stopwatch.GetTimestamp() - start;
            State stock = null, candidate = null;
            try
            {
                bool candidateFirst = Report.pairedSamples == 1;
                for (int path = 0; path < 2; path++)
                {
                    if (path != 0) { start = Stopwatch.GetTimestamp(); initial.Restore(integrator); sample.rollbackTicks += Stopwatch.GetTimestamp() - start; }
                    bool isCandidate = (path == 0) == candidateFirst;
                    if (isCandidate)
                    {
                        start = Stopwatch.GetTimestamp(); var input = new ConvectionSparseKernel.Input(rows, vessel, integrator.mach); long copied = Stopwatch.GetTimestamp();
                        ConvectionSparseKernel.Result result = kernel.Run(input); long folded = Stopwatch.GetTimestamp();
                        ConvectionSparseKernel.Publish(integrator, result); long committed = Stopwatch.GetTimestamp();
                        sample.candidateCopyTicks = admissionTicks + copied - start; sample.candidateFoldTicks = folded - copied; sample.candidateCommitTicks = committed - folded;
                        sample.candidateCompleteTicks = invocation.projected + admissionTicks + committed - start;
                        sample.scalarCalls = result.scalarCalls; sample.reachedPairs = result.reachedPairs; sample.areaQueries = result.areaQueries; sample.geometryQueries = result.geometryQueries;
                        sample.certificateProbes = result.certificateProbes; sample.certificateAnchors = result.certificateAnchors; sample.certificateSteps = result.certificateSteps; sample.nodeVisits = result.nodeVisits; sample.unitAreaCones = result.unitAreaCones;
                        start = Stopwatch.GetTimestamp(); candidate = new State(integrator, rows); sample.snapshotTicks += Stopwatch.GetTimestamp() - start;
                    }
                    else
                    {
                        start = Stopwatch.GetTimestamp(); stockFold(integrator); sample.stockDirectFoldTicks = Stopwatch.GetTimestamp() - start;
                        sample.stockCompleteTicks = invocation.projected + sample.stockDirectFoldTicks;
                        start = Stopwatch.GetTimestamp(); stock = new State(integrator, rows); sample.snapshotTicks += Stopwatch.GetTimestamp() - start;
                    }
                }
                start = Stopwatch.GetTimestamp(); string mismatch; sample.bitwiseMatch = stock.Same(candidate, out mismatch); sample.mismatch = mismatch;
                sample.comparisonTicks = Stopwatch.GetTimestamp() - start;
                if (!sample.bitwiseMatch)
                {
                    Report.verificationMismatches++; throw new InvalidOperationException("Stock/candidate bitwise mismatch: " + mismatch);
                }
                if (candidateFirst) { start = Stopwatch.GetTimestamp(); candidate.Restore(integrator); sample.rollbackTicks += Stopwatch.GetTimestamp() - start; }
                sample.acceptedCones = integrator.occludersConvectionCount; Report.samples[Report.pairedSamples++] = sample;
                invocation.sample = sample; invocation.handled = true; Report.replacements++; Report.captureReplacements++; return true;
            }
            catch
            {
                start = Stopwatch.GetTimestamp();
                try { initial.Restore(integrator); }
                catch (Exception rollback) { throw new RollbackFailure(rollback); }
                sample.rollbackTicks += Stopwatch.GetTimestamp() - start;
                invocation.sample = sample; throw;
            }
        }
        sealed class State
        {
            readonly List<OcclusionData> list;
            readonly OcclusionData[] rows;
            readonly PartThermalData[] owners;
            readonly OcclusionCone[] liveCones, values, array, arrayValues;
            readonly double[] thermal;
            readonly int count;
            internal State(FlightIntegrator integrator, List<OcclusionData> list)
            {
                this.list = list; rows = list.ToArray(); owners = new PartThermalData[rows.Length]; liveCones = new OcclusionCone[rows.Length]; values = new OcclusionCone[rows.Length]; thermal = new double[rows.Length * 3];
                for (int i = 0; i < rows.Length; i++)
                {
                    ConvectionSparseKernel.Require(rows[i] != null && rows[i].ptd != null && rows[i].convCone != null, "Verification row owner missing.");
                    owners[i] = rows[i].ptd; liveCones[i] = rows[i].convCone; values[i] = ConvectionSparseKernel.CloneCone(liveCones[i]);
                    thermal[i * 3] = owners[i].convectionTempMultiplier; thermal[i * 3 + 1] = owners[i].convectionCoeffMultiplier; thermal[i * 3 + 2] = owners[i].convectionAreaMultiplier;
                }
                array = integrator.occludersConvection; ConvectionSparseKernel.Require(array != null && array.Length >= rows.Length && array.Length <= ConvectionFormat.MaxRows, "Verification array bound differs.");
                arrayValues = (OcclusionCone[])array.Clone(); count = integrator.occludersConvectionCount;
            }
            internal void Restore(FlightIntegrator integrator)
            {
                ConvectionSparseKernel.Require(ReferenceEquals(integrator.occludersConvection, array) && list.Count == rows.Length, "Verification rollback owner changed.");
                for (int i = 0; i < rows.Length; i++)
                    ConvectionSparseKernel.Require(ReferenceEquals(list[i], rows[i]) && ReferenceEquals(rows[i].ptd, owners[i]) && ReferenceEquals(rows[i].convCone, liveCones[i]), "Verification rollback row changed.");
                for (int i = 0; i < rows.Length; i++)
                {
                    owners[i].convectionTempMultiplier = thermal[i * 3]; owners[i].convectionCoeffMultiplier = thermal[i * 3 + 1]; owners[i].convectionAreaMultiplier = thermal[i * 3 + 2];
                    ConvectionSparseKernel.CopyCone(values[i], liveCones[i], values[i].part);
                }
                Array.Copy(arrayValues, array, array.Length); integrator.occludersConvectionCount = count;
            }
            internal bool Same(State other, out string reason)
            {
                if (other == null || count != other.count || rows.Length != other.rows.Length || arrayValues.Length != other.arrayValues.Length) { reason = "counts"; return false; }
                for (int i = 0; i < thermal.Length; i++) if (!Bits(thermal[i], other.thermal[i])) { reason = "thermal[" + i + "]"; return false; }
                for (int i = 0; i < values.Length; i++) if (!ConeSame(values[i], other.values[i])) { reason = "cone[" + i + "]"; return false; }
                for (int i = 0; i < arrayValues.Length; i++) if (!ReferenceEquals(arrayValues[i], other.arrayValues[i])) { reason = "cone-order[" + i + "]"; return false; }
                reason = null; return true;
            }
            static bool Bits(double x, double y) { return BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y); }
            static bool FloatBits(float x, float y) { return BitConverter.ToInt32(BitConverter.GetBytes(x), 0) == BitConverter.ToInt32(BitConverter.GetBytes(y), 0); }
            static bool V(Vector2 a, Vector2 b) { return FloatBits(a.x, b.x) && FloatBits(a.y, b.y); }
            static bool V(Vector3 a, Vector3 b) { return FloatBits(a.x, b.x) && FloatBits(a.y, b.y) && FloatBits(a.z, b.z); }
            static bool ConeSame(OcclusionCone a, OcclusionCone b)
            {
                return ReferenceEquals(a.part, b.part) && V(a.center, b.center) && V(a.extents, b.extents) && V(a.offset, b.offset) && Bits(a.radius, b.radius) &&
                    Bits(a.cylNoseDot, b.cylNoseDot) && Bits(a.shockNoseDot, b.shockNoseDot) && Bits(a.shockAngle, b.shockAngle) &&
                    Bits(a.shockConvectionTempMult, b.shockConvectionTempMult) && Bits(a.shockConvectionCoeffMult, b.shockConvectionCoeffMult) &&
                    Bits(a.occludeConvectionTempMult, b.occludeConvectionTempMult) && Bits(a.occludeConvectionCoeffMult, b.occludeConvectionCoeffMult) && Bits(a.occludeConvectionAreaMult, b.occludeConvectionAreaMult);
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try
            {
                harmony.UnpatchAll(Owner); var patches = Harmony.GetPatchInfo(Target);
                Report.cleanupStatus = patches != null && patches.Owners.Contains(Owner) ? "cleanup-error" : "removed-owned-hooks";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Fault(error); }
            finally { if (ReferenceEquals(active, this)) active = null; }
            string reason; Report.status = IsValidFinal(out reason) ? "completed-experimental" : "invalid";
            if (Report.status == "invalid" && Report.detail == null) Report.detail = reason;
        }
        public bool IsValidFinal(out string reason)
        {
            if (!disposed || !Report.hookShapeVerified || Report.cleanupStatus != "removed-owned-hooks") { reason = "hook shape/cleanup incomplete"; return false; }
            if (Report.faults != 0 || Report.verificationMismatches != 0 || Report.recursionFallbacks != 0 || Report.captureFallbacks != 0) { reason = "fault/mismatch/fallback present"; return false; }
            if (Report.captureReplacements <= 0 || Report.captureCalls <= 0) { reason = "no captured replacement coverage"; return false; }
            if (Report.mode == "verify" && (Report.pairedSamples != 2 || Report.samples.Any(sample => sample == null || !sample.bitwiseMatch || sample.methodInclusiveTicks <= 0))) { reason = "two paired bitwise witnesses incomplete"; return false; }
            if (Report.mode == "query" && Report.pairedSamples != 0) { reason = "query contains diagnostic pairs"; return false; }
            reason = null; return true;
        }
    }
}
