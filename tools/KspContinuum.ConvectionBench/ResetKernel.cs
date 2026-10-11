using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using KspContinuum;

sealed class ResetKernel : IDisposable
{
    static readonly MethodInfo Target = typeof(OcclusionData).GetMethod(nameof(OcclusionData.GetShockStats), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    static ResetKernel owner;
    readonly TanCache.ShockCall full, rectangle, geometry;
    readonly int thread = Environment.CurrentManagedThreadId;
    sealed class Fallback : Exception { public Fallback(string reason) : base(reason) { } }
    public const string Domain = "Supersonic finite Mach; bounded finite projections; radius zero or >=1e-6; nonnegative finite unit-bounded heat/coeff globals and published cone multipliers without negative zero; bounded original shock-radius endpoints; FI_LOG_TEMP_ERROR=false. No recorded output participates in admission.";
    public ResetKernel()
    {
        Stock.Admit(); var body = Target.GetMethodBody();
        Require(owner == null && TanCache.Caller == null && Harmony.GetPatchInfo(Target) == null, "Reset kernel overlaps another execution owner.");
        Require(Convert.ToHexString(SHA256.HashData(body.GetILAsByteArray())).ToLowerInvariant() == GeometryCensus.IlHash && body.LocalVariables.Count == 8 && body.ExceptionHandlingClauses.Count == 0, "Reset original IL pin/shape differs.");
        Type[] locals = { typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector2), typeof(double), typeof(double), typeof(double), typeof(double), typeof(double), typeof(UnityEngine.Vector3) };
        for (int i = 0; i < locals.Length; i++) Require(body.LocalVariables[i].LocalType == locals[i], "Reset original local type differs.");
        full = Create(null); rectangle = Create(nameof(Rectangle)); geometry = Create(nameof(Geometry)); owner = this;
    }
    static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    static void Support(bool condition, string reason) { if (!condition) throw new Fallback(reason); }
    static bool Unit(double value) { return double.IsFinite(value) && value >= 0 && value <= 1 && BitConverter.DoubleToInt64Bits(value) != long.MinValue; }
    static bool Bounded(double value, double max) { return double.IsFinite(value) && Math.Abs(value) <= max; }
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
    static CodeInstruction[] Pin(IEnumerable<CodeInstruction> source)
    {
        var code = source.Select(instruction => new CodeInstruction(instruction)).ToArray();
        MethodInfo area = typeof(OcclusionData).GetMethod("AreaOfIntersection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Require(code.Length == 155 && code[99].Calls(area) && code[114].opcode == OpCodes.Ldarg_2 && code[114].labels.Count == 0 &&
            code[73].opcode == OpCodes.Ldloc_3 && code[74].opcode == OpCodes.Ldc_R8 && (double)code[74].operand == .99 && code[75].opcode == OpCodes.Bge_Un_S &&
            code[104].opcode == OpCodes.Ldc_R8 && (double)code[104].operand == 1 && code[105].opcode == OpCodes.Stloc_3 &&
            Local(code[70]) == Local(code[73]) && Local(code[102]) == Local(code[107]) && code[146].opcode == OpCodes.Ldc_R8 &&
            code[154].opcode == OpCodes.Ret && code.All(instruction => instruction.blocks.Count == 0), "Reset extraction boundaries differ.");
        return code;
    }
    static IEnumerable<CodeInstruction> Rectangle(IEnumerable<CodeInstruction> source, ILGenerator generator)
    {
        var code = Pin(source); Label area = generator.DefineLabel();
        for (int i = 0; i <= 75; i++) yield return code[i];
        yield return new CodeInstruction(OpCodes.Br, area);
        yield return code[104]; yield return code[105];
        var output = new CodeInstruction(OpCodes.Ldarg_2); output.labels.Add(area); yield return output;
        yield return Load(code[73]); yield return new CodeInstruction(OpCodes.Stind_R8);
        for (int i = 146; i < code.Length; i++) yield return code[i];
    }
    static IEnumerable<CodeInstruction> Geometry(IEnumerable<CodeInstruction> source)
    {
        var code = Pin(source);
        for (int i = 0; i <= 113; i++) yield return code[i];
        yield return new CodeInstruction(OpCodes.Ldarg_2); yield return Load(code[73]); yield return new CodeInstruction(OpCodes.Stind_R8);
        yield return new CodeInstruction(OpCodes.Ldarg_3); yield return Load(code[107]); yield return new CodeInstruction(OpCodes.Stind_R8);
        for (int i = 146; i < code.Length; i++) yield return code[i];
    }
    static TanCache.ShockCall Create(string transpiler)
    {
        Type emitter = typeof(Harmony).Assembly.GetType("HarmonyLib.MethodPatcher", true); var empty = new List<MethodInfo>();
        var trans = transpiler == null ? new List<MethodInfo>() : new List<MethodInfo> { typeof(ResetKernel).GetMethod(transpiler, BindingFlags.NonPublic | BindingFlags.Static) };
        object patcher = Activator.CreateInstance(emitter, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { Target, null, empty, empty, trans, empty, false }, null);
        object[] output = { null };
        MethodInfo replacement = (MethodInfo)emitter.GetMethod("CreateReplacement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(patcher, output);
        return (TanCache.ShockCall)replacement.CreateDelegate(typeof(TanCache.ShockCall));
    }
    public static double Original(OcclusionData data, OcclusionCone cone, ref double temp, ref double coeff)
    {
        Require(owner != null && owner.thread == Environment.CurrentManagedThreadId, "Reset original delegate context differs.");
        return owner.full(data, cone, ref temp, ref coeff);
    }
    public static ReplayResult EvaluateStock(ConvectionFrame frame)
    {
        Require(owner != null && owner.thread == Environment.CurrentManagedThreadId, "Reset stock context differs.");
        return owner.StockFold(frame);
    }
    ReplayResult StockFold(ConvectionFrame frame)
    {
        Stock.Globals(frame.globals); var data = new OcclusionData[frame.rows.Length];
        for (int i = 0; i < data.Length; i++) data[i] = Stock.Input(frame.rows[i]);
        var accepted = new List<OcclusionCone>(); var cones = new List<ConvectionCone>(); var result = new ReplayResult { multipliers = new double[data.Length * 3] };
        if (frame.mach > 1)
        {
            double sqrtMach = Math.Sqrt(frame.mach), machAngle = Math.Asin(1 / sqrtMach), detachAngle = .7957 * (1 - 1 / (frame.mach * sqrtMach));
            for (int row = 0; row < data.Length; row++)
            {
                OcclusionData input = data[row]; result.possiblePairs += accepted.Count; result.candidatePairs += accepted.Count;
                for (int pair = 0; pair < accepted.Count; pair++)
                {
                    double area = full(input, accepted[pair], ref input.ptd.convectionTempMultiplier, ref input.ptd.convectionCoeffMultiplier); result.scalarPairs++;
                    input.ptd.convectionAreaMultiplier *= area;
                    if (input.ptd.convectionAreaMultiplier <= .001) { input.ptd.convectionAreaMultiplier = 0; break; }
                }
                if (input.ptd.convectionAreaMultiplier > 0)
                {
                    input.convCone.Setup(input, sqrtMach, machAngle, detachAngle); accepted.Add(input.convCone); cones.Add(Stock.Capture(input.convCone, row));
                }
            }
        }
        for (int row = 0; row < data.Length; row++)
        {
            result.multipliers[row * 3] = data[row].ptd.convectionTempMultiplier; result.multipliers[row * 3 + 1] = data[row].ptd.convectionCoeffMultiplier; result.multipliers[row * 3 + 2] = data[row].ptd.convectionAreaMultiplier;
        }
        result.cones = cones.ToArray(); return result;
    }
    public static ReplayResult Evaluate(ConvectionFrame frame)
    {
        Require(owner != null && owner.thread == Environment.CurrentManagedThreadId, "Reset kernel context differs.");
        var result = new ReplayResult();
        try { return owner.Fold(frame, result); }
        catch (Fallback error)
        {
            ReplayResult stock = Replay.Evaluate(frame, Treatment.ResetStock); stock.fallbackReason = error.Message;
            stock.resetRectangleCalls = result.resetRectangleCalls; stock.resetGeometryCalls = result.resetGeometryCalls;
            stock.resetAnchorProbes = result.resetAnchorProbes; stock.resetAnchors = result.resetAnchors; stock.resetBoundSteps = result.resetBoundSteps;
            stock.resetRows = result.resetRows; stock.resetNoResetRows = result.resetNoResetRows; stock.resetSkippedScalarPairs = result.resetSkippedScalarPairs;
            stock.resetAttemptedScalarPairs = result.scalarPairs; return stock;
        }
    }
    static void ArrayBound(float[] values, int length)
    {
        Support(values != null && values.Length == length, "projection-shape");
        foreach (float value in values) Support(Bounded(value, 1e10), "projection-coordinate-domain");
    }
    static double AdmitFrame(ConvectionFrame frame, out double minimumDot, out double maximumDot)
    {
        Support(!GameSettings.FI_LOG_TEMP_ERROR, "stock-thermal-logging-enabled");
        Support(frame != null && frame.rows != null && frame.rows.Length <= ConvectionFormat.MaxRows && frame.globals != null && frame.globals.Length == 14, "frame-shape");
        Support(double.IsFinite(frame.mach) && frame.mach > 1 && frame.mach <= 1e10, "mach-domain");
        for (int i = 0; i < 14; i++) Support(i <= 3 || i >= 10 ? Unit(frame.globals[i]) : Bounded(frame.globals[i], 1e4), "global-domain-" + i);
        double radius = double.PositiveInfinity; minimumDot = double.PositiveInfinity; maximumDot = double.NegativeInfinity;
        foreach (ConvectionRow row in frame.rows)
        {
            Support(row != null && Bounded(row.projectedArea, 1e20) && row.projectedArea >= 0 && Bounded(row.projectedRadius, 1e10) && row.projectedRadius >= 0 &&
                (row.projectedRadius == 0 || row.projectedRadius >= 1e-6) && Bounded(row.invFineness, 1e4) && row.invFineness >= 0 &&
                Bounded(row.minimumDot, 1e10) && Bounded(row.maximumDot, 1e10) && Bounded(row.centroidDot, 1e10) && Bounded(row.maxWidthDepth, 1e10) && row.maxWidthDepth >= 0, "row-scalar-domain");
            ArrayBound(row.projectedCenter, 3); ArrayBound(row.center, 2); ArrayBound(row.minExtents, 2); ArrayBound(row.maxExtents, 2); ArrayBound(row.extents, 2);
            for (int axis = 0; axis < 2; axis++) Support(row.minExtents[axis] <= row.maxExtents[axis] && row.extents[axis] >= 0, "rectangle-domain");
            if (row.projectedRadius > 0) radius = Math.Min(radius, row.projectedRadius);
            minimumDot = Math.Min(minimumDot, row.centroidDot); maximumDot = Math.Max(maximumDot, row.centroidDot);
        }
        return double.IsPositiveInfinity(radius) ? 1 : radius;
    }
    ReplayResult Fold(ConvectionFrame frame, ReplayResult result)
    {
        double minRadius = AdmitFrame(frame, out double minDot, out double maxDot); Stock.Globals(frame.globals);
        var data = new OcclusionData[frame.rows.Length]; for (int i = 0; i < data.Length; i++) data[i] = Stock.Input(frame.rows[i]);
        var accepted = new List<OcclusionCone>(); var cones = new List<ConvectionCone>();
        var cylinders = new double[data.Length]; var shocks = new double[data.Length]; var known = new bool[data.Length];
        result.multipliers = new double[data.Length * 3]; double maxRadius = 0;
        double sqrtMach = Math.Sqrt(frame.mach), machAngle = Math.Asin(1 / sqrtMach), detachAngle = .7957 * (1 - 1 / (frame.mach * sqrtMach));
        for (int row = 0; row < data.Length; row++)
        {
            OcclusionData input = data[row]; int reached = 0; double area = 1; Array.Clear(known, 0, accepted.Count); result.possiblePairs += accepted.Count;
            for (int pair = 0; pair < accepted.Count; pair++)
            {
                double c = 0, ignored = 0; double factor = rectangle(input, accepted[pair], ref c, ref ignored); result.resetRectangleCalls++;
                Support(Unit(c) && Unit(factor), "rectangle-coefficient-domain"); cylinders[pair] = c; reached++; area *= factor;
                if (area <= .001) { area = 0; break; }
            }
            result.candidatePairs += reached;
            double ratio = maxRadius / minRadius, shockBound = 8 * (ratio + 1) * (ratio + 1);
            Support(double.IsFinite(shockBound) && shockBound >= 1, "shock-envelope-domain");
            int last = reached - 1;
            while (last >= 0 && Shock(last) != 1) last--;
            if (last >= 0)
            {
                Certify(last, shockBound); result.resetRows++; result.resetSkippedScalarPairs += last;
            }
            else { result.resetNoResetRows++; last = 0; }
            for (int pair = last; pair < reached; pair++)
            {
                full(input, accepted[pair], ref input.ptd.convectionTempMultiplier, ref input.ptd.convectionCoeffMultiplier); result.scalarPairs++;
            }
            Support(double.IsFinite(input.ptd.convectionTempMultiplier) && double.IsFinite(input.ptd.convectionCoeffMultiplier), "retained-tail-nonfinite");
            input.ptd.convectionAreaMultiplier = area;
            if (area > 0)
            {
                input.convCone.Setup(input, sqrtMach, machAngle, detachAngle); var cone = Stock.Capture(input.convCone, row);
                Support(Unit(cone.shockConvectionTempMult) && Unit(cone.shockConvectionCoeffMult) && Unit(cone.occludeConvectionTempMult) && Unit(cone.occludeConvectionCoeffMult) && Unit(cone.occludeConvectionAreaMult), "published-multiplier-domain");
                double left = input.convCone.GetShockRadius(minDot), right = input.convCone.GetShockRadius(maxDot);
                Support(Bounded(left, 1e20) && Bounded(right, 1e20) && Bounded(cone.radius, 1e10) && Bounded(cone.shockNoseDot, 1e15) && Bounded(cone.shockAngle, 1e8), "published-radius-domain");
                maxRadius = Math.Max(maxRadius, Math.Max(Math.Abs(left), Math.Abs(right))); accepted.Add(input.convCone); cones.Add(cone);
            }
            double Shock(int pair)
            {
                if (!known[pair])
                {
                    double c = 0, s = 0; geometry(input, accepted[pair], ref c, ref s); result.resetGeometryCalls++;
                    Support(BitConverter.DoubleToInt64Bits(c) == BitConverter.DoubleToInt64Bits(cylinders[pair]) && double.IsFinite(s) && Math.Abs(s) <= shockBound, "geometry-envelope-domain");
                    shocks[pair] = s; known[pair] = true;
                }
                return shocks[pair];
            }
            void Certify(int end, double boundS)
            {
                int first = 0; double bound = 1, growthBits = Math.Log2(8 * (boundS + 4)) + 1;
                while (first < end)
                {
                    int span = (int)Math.Floor((880 - Math.Log2(bound)) / growthBits);
                    Support(span >= 1, "prefix-magnitude-certificate-exhausted"); int limit = Math.Min(end - 1, first + span - 1), anchor = limit;
                    while (anchor >= first) { result.resetAnchorProbes++; if (Shock(anchor) == 1) break; anchor--; }
                    if (anchor >= first)
                    {
                        result.resetAnchors++; bound = 4; first = anchor + 1;
                    }
                    else
                    {
                        for (int pair = first; pair <= limit; pair++)
                        {
                            double s = Shock(pair), c = cylinders[pair];
                            bound = 2 * (Math.Abs(1 - s) * bound + Math.Abs(s - c) + c + 1); result.resetBoundSteps++;
                            Support(double.IsFinite(bound) && bound <= 1e250, "prefix-magnitude-certificate-exhausted");
                        }
                        first = limit + 1;
                    }
                }
            }
        }
        for (int row = 0; row < data.Length; row++)
        {
            result.multipliers[row * 3] = data[row].ptd.convectionTempMultiplier; result.multipliers[row * 3 + 1] = data[row].ptd.convectionCoeffMultiplier; result.multipliers[row * 3 + 2] = data[row].ptd.convectionAreaMultiplier;
        }
        result.cones = cones.ToArray(); return result;
    }
    public void Dispose()
    {
        Require(owner == this && thread == Environment.CurrentManagedThreadId && Harmony.GetPatchInfo(Target) == null, "Reset kernel cleanup context differs."); owner = null;
    }
}
