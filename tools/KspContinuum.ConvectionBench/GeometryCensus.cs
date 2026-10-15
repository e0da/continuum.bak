using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using KspContinuum;

static class GeometryCensus
{
    public const string IlHash = "2698e2f424c4ddb4faff403c13ee670bc8db8834efcd662b00402100bd07fa29";
    const int MaxCoefficientBins = 65536;
    static readonly MethodInfo Target = typeof(OcclusionData).GetMethod(nameof(OcclusionData.GetShockStats), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    static readonly string[] Coefficients = { "normalizedRectangle", "effectiveCylinder", "effectiveShock", "remainingWeight", "shockWeight" };
    static FrameStats current;
    static int ownerThread, circles, observed;
    static long circleBits;
    sealed class FrameStats
    {
        public readonly long[] regions = new long[16], actualShockRegions = new long[4];
        public readonly Dictionary<long, long>[] bits = Coefficients.Select(_ => new Dictionary<long, long>()).ToArray();
        public readonly List<object> rows = new List<object>();
        public long reached, circleCalls, fullCylinderCircleSkipped, rawCylinderClamped, cutoffs, available, initializedRows, fullShockResets;
        public int activePair, rowFullShockResets;
        public int? lastResetAcceptedIndex;
        public void Add(int column, double value)
        {
            long key = BitConverter.DoubleToInt64Bits(value);
            if (bits[column].TryGetValue(key, out long count)) bits[column][key] = count + 1;
            else { Require(bits[column].Count < MaxCoefficientBins, "Coefficient bit histogram capacity exceeded."); bits[column].Add(key, 1); }
        }
        public object Receipt(ConvectionFrame frame, ReplayResult result)
        {
            var names = new[] { "none", "partial", "full", "other" };
            return new { frame.sequence, frame.universalTime, frame.mach, parts = frame.rows.Length, acceptedCones = result.cones.Length, reached, available, circleCalls,
                fullCylinderCircleSkipped, rawCylinderClamped, cutoffs, initializedRows, fullShockResets, bitwiseRecordedMatch = true,
                effectiveRegions = Enumerable.Range(0, 16).Select(index => new { cylinder = names[index / 4], shock = names[index % 4], count = regions[index] }),
                actualCircleRegions = Enumerable.Range(0, 4).Select(index => new { shock = names[index], count = actualShockRegions[index] }),
                coefficients = Enumerable.Range(0, bits.Length).Select(index => new { name = Coefficients[index],
                    bins = bits[index].OrderBy(pair => unchecked((ulong)pair.Key)).Select(pair => new { bits = unchecked((ulong)pair.Key).ToString("x16"), count = pair.Value }) }), rows };
        }
    }
    static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    static void Admit()
    {
        Stock.Admit(); var body = Target.GetMethodBody();
        Require(Target.MetadataToken == 100677560 && Target.Module.ModuleVersionId == Stock.Mvid &&
            Convert.ToHexString(SHA256.HashData(body.GetILAsByteArray())).ToLowerInvariant() == IlHash, "Census original IL pin differs.");
        Type[] locals = { typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector2), typeof(double), typeof(double), typeof(double), typeof(double), typeof(double), typeof(UnityEngine.Vector3) };
        Require(body.LocalVariables.Count == locals.Length && body.ExceptionHandlingClauses.Count == 0, "Census original local/exception shape differs.");
        for (int index = 0; index < locals.Length; index++) Require(body.LocalVariables[index].LocalType == locals[index], "Census original local type differs.");
        Require(Harmony.GetPatchInfo(Target) == null && current == null && TanCache.Caller == null, "Census observer overlaps another execution owner.");
    }
    static int Region(double value) { return value == 0 ? 0 : value > 0 && value < 1 ? 1 : value == 1 ? 2 : 3; }
    public static void ObserveCircle(double value)
    {
        Require(current != null && Environment.CurrentManagedThreadId == ownerThread && circles == 0 && observed == 0, "Circle observer context differs.");
        circles++; circleBits = BitConverter.DoubleToInt64Bits(value); current.circleCalls++; current.actualShockRegions[Region(value)]++;
    }
    public static void ObserveGeometry(double rectangle, double cylinder, double shock, double remaining, double shockWeight)
    {
        Require(current != null && Environment.CurrentManagedThreadId == ownerThread && observed == 0, "Geometry observer context differs.");
        Require(circles == (rectangle < .99 ? 1 : 0), "Actual circle branch differs from admitted original branch.");
        if (circles == 1) Require(circleBits == BitConverter.DoubleToInt64Bits(shock), "Original circle result does not feed admitted shock local.");
        else { Require(cylinder == 1 && shock == 1, "Full-cylinder branch local values differ."); current.fullCylinderCircleSkipped++; }
        if (BitConverter.DoubleToInt64Bits(rectangle) != BitConverter.DoubleToInt64Bits(cylinder)) current.rawCylinderClamped++;
        double[] values = { rectangle, cylinder, shock, remaining, shockWeight };
        for (int index = 0; index < values.Length; index++) current.Add(index, values[index]);
        current.regions[Region(cylinder) * 4 + Region(shock)]++; current.reached++; observed++;
        if (BitConverter.DoubleToInt64Bits(shock) == 0x3ff0000000000000 && BitConverter.DoubleToInt64Bits(remaining) == 0)
        { current.fullShockResets++; current.rowFullShockResets++; current.lastResetAcceptedIndex = current.activePair; }
    }
    static int Local(CodeInstruction instruction)
    {
        if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
        if (instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Stloc_0) return 0;
        if (instruction.opcode == OpCodes.Ldloc_1 || instruction.opcode == OpCodes.Stloc_1) return 1;
        if (instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Stloc_2) return 2;
        if (instruction.opcode == OpCodes.Ldloc_3 || instruction.opcode == OpCodes.Stloc_3) return 3;
        return Convert.ToInt32(instruction.operand);
    }
    static CodeInstruction Load(CodeInstruction original) { return new CodeInstruction(original.opcode, original.operand); }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code = source.Select(instruction => new CodeInstruction(instruction)).ToArray();
        MethodInfo area = typeof(OcclusionData).GetMethod("AreaOfIntersection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Require(code.Length == 155 && code[99].Calls(area) && code[113].opcode == OpCodes.Stloc_S && code[114].opcode == OpCodes.Ldarg_2 &&
            code[114].labels.Count == 0 && code[114].blocks.Count == 0 && code[107].opcode == OpCodes.Ldloc_S && code[115].opcode == OpCodes.Ldloc_S && code[119].opcode == OpCodes.Ldloc_S,
            "Census original observation boundaries differ.");
        Require(Local(code[39]) == Local(code[40]) && Local(code[70]) == Local(code[73]) &&
            Local(code[102]) == Local(code[107]) && Local(code[109]) == Local(code[115]) && Local(code[113]) == Local(code[119]),
            "Census original coefficient local mapping differs.");
        for (int index = 0; index < code.Length; index++)
        {
            if (index == 114)
            {
                foreach (int load in new[] { 40, 73, 107, 115, 119 }) yield return Load(code[load]);
                yield return new CodeInstruction(OpCodes.Call, typeof(GeometryCensus).GetMethod(nameof(ObserveGeometry)));
            }
            yield return code[index];
            if (index == 99)
            {
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Call, typeof(GeometryCensus).GetMethod(nameof(ObserveCircle)));
            }
        }
    }
    static TanCache.ShockCall CreateObserver()
    {
        Type emitter = typeof(Harmony).Assembly.GetType("HarmonyLib.MethodPatcher", true); var empty = new List<MethodInfo>();
        object patcher = Activator.CreateInstance(emitter, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { Target, null, empty, empty, new List<MethodInfo> { typeof(GeometryCensus).GetMethod(nameof(Transpiler), BindingFlags.NonPublic | BindingFlags.Static) }, empty, false }, null);
        object[] output = { null };
        var replacement = (MethodInfo)emitter.GetMethod("CreateReplacement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(patcher, output);
        return (TanCache.ShockCall)replacement.CreateDelegate(typeof(TanCache.ShockCall));
    }
    static ReplayResult Fold(ConvectionFrame frame, TanCache.ShockCall call)
    {
        Stock.Globals(frame.globals); var data = frame.rows.Select(Stock.Input).ToArray();
        var accepted = new List<OcclusionCone>(); var cones = new List<ConvectionCone>();
        var result = new ReplayResult { multipliers = new double[data.Length * 3] };
        double sqrtMach = Math.Sqrt(frame.mach), machAngle = Math.Asin(1 / sqrtMach), detachAngle = .7957 * (1 - 1 / (frame.mach * sqrtMach));
        Require(frame.mach > 1 && double.IsFinite(frame.mach), "Census requires captured supersonic frame.");
        for (int row = 0; row < data.Length; row++)
        {
            OcclusionData input = data[row]; int available = accepted.Count, reached = 0; int? cutoff = null; long? cutoffAreaBits = null;
            Require(input.ptd.convectionTempMultiplier == 1 && input.ptd.convectionCoeffMultiplier == 1 && input.ptd.convectionAreaMultiplier == 1, "Row reset differs.");
            current.available += available; current.initializedRows++; current.rowFullShockResets = 0; current.lastResetAcceptedIndex = null;
            for (int pair = 0; pair < available; pair++)
            {
                circles = observed = 0; circleBits = 0; current.activePair = pair;
                double area = call(input, accepted[pair], ref input.ptd.convectionTempMultiplier, ref input.ptd.convectionCoeffMultiplier);
                Require(observed == 1, "Reached pair missing coefficient observation."); reached++; result.scalarPairs++;
                input.ptd.convectionAreaMultiplier *= area;
                if (input.ptd.convectionAreaMultiplier <= .001)
                {
                    cutoff = pair; cutoffAreaBits = BitConverter.DoubleToInt64Bits(input.ptd.convectionAreaMultiplier);
                    input.ptd.convectionAreaMultiplier = 0; current.cutoffs++; break;
                }
            }
            current.rows.Add(new { row, frame.rows[row].flightId, available, reached, unreachedAfterCutoff = available - reached, cutoffAcceptedIndex = cutoff,
                cutoffSourceRow = cutoff.HasValue ? (int?)cones[cutoff.Value].rowIndex : null, cutoffCumulativeAreaBits = cutoffAreaBits.HasValue ? unchecked((ulong)cutoffAreaBits.Value).ToString("x16") : null,
                fullShockResetCount = current.rowFullShockResets, lastFullShockResetAcceptedIndex = current.lastResetAcceptedIndex,
                lastFullShockResetSourceRow = current.lastResetAcceptedIndex.HasValue ? (int?)cones[current.lastResetAcceptedIndex.Value].rowIndex : null });
            if (input.ptd.convectionAreaMultiplier > 0)
            {
                input.convCone.Setup(input, sqrtMach, machAngle, detachAngle); accepted.Add(input.convCone); cones.Add(Stock.Capture(input.convCone, row));
            }
        }
        for (int row = 0; row < data.Length; row++)
        {
            result.multipliers[row * 3] = data[row].ptd.convectionTempMultiplier;
            result.multipliers[row * 3 + 1] = data[row].ptd.convectionCoeffMultiplier;
            result.multipliers[row * 3 + 2] = data[row].ptd.convectionAreaMultiplier;
        }
        result.cones = cones.ToArray(); Require(result.scalarPairs == current.reached, "Pair and observation denominators differ."); return result;
    }
    public static object Run(ConvectionRecording recording)
    {
        Admit(); var call = CreateObserver(); ownerThread = Environment.CurrentManagedThreadId; var receipts = new List<object>();
        double[] savedGlobals = Stock.Globals(); long totalReached = 0, totalCircles = 0, totalSkipped = 0, totalCutoffs = 0, totalFullShockResets = 0;
        var regions = new long[16];
        try
        {
            foreach (ConvectionFrame frame in recording.frames)
            {
                current = new FrameStats(); ReplayResult result = Fold(frame, call); string difference = Replay.Compare(result, frame);
                Require(difference == null, "Census changed captured bits: " + difference); receipts.Add(current.Receipt(frame, result));
                totalReached += current.reached; totalCircles += current.circleCalls; totalSkipped += current.fullCylinderCircleSkipped; totalCutoffs += current.cutoffs; totalFullShockResets += current.fullShockResets;
                for (int index = 0; index < regions.Length; index++) regions[index] += current.regions[index]; current = null;
            }
        }
        finally { current = null; circles = observed = ownerThread = 0; circleBits = 0; call = null; Stock.Globals(savedGlobals); }
        Require(Harmony.GetPatchInfo(Target) == null, "Census altered original process patch graph.");
        return new { totalReached, totalCircles, totalSkipped, totalCutoffs, totalFullShockResets, regions, frames = receipts, originalPatchGraphUntouched = true,
            coefficientAdmission = new { originalIlSha256 = IlHash, instructions = 155, originalLocalCount = 8, locals = new { normalizedRectangle = 2, effectiveCylinder = 3, effectiveShock = 4, remainingWeight = 5, shockWeight = 6 },
                circleReturnSite = 99, coefficientBoundaryBeforeInstruction = 114, maxDistinctBitsPerCoefficientPerFrame = MaxCoefficientBins },
            classification = "none is +/-0; partial is finite0<x<1; full is exact1; other retains outside-domain/nonfinite bits. Effective shock is assumed1 when full-cylinder branch skips the circle routine; actualCircleRegions includes only reached original circle calls. Full-shock reset requires effectiveShock bits3ff0000000000000 and remainingWeight bits0000000000000000; reached resets include the pair whose returned area triggers cutoff, excluding all uncalled later pairs." };
    }
}
