using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class ConvectionSparseKernel
    {
        internal const string ScalarIlHash = "2698e2f424c4ddb4faff403c13ee670bc8db8834efcd662b00402100bd07fa29";
        internal static readonly Guid Mvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        internal delegate double Scalar(OcclusionData data, OcclusionCone cone, ref double temp, ref double coeff);
        static readonly MethodInfo Target = AccessTools.DeclaredMethod(typeof(OcclusionData), "GetShockStats");
        readonly Scalar full, rectangle, geometry;
        internal sealed class Unsupported : Exception { internal Unsupported(string reason) : base(reason) { } }
        internal sealed class Input
        {
            internal readonly List<OcclusionData> source;
            internal readonly OcclusionData[] real, shadow;
            internal readonly PartThermalData[] owners;
            internal readonly OcclusionCone[] cones;
            internal readonly Vessel vessel;
            internal readonly double[] globals;
            internal readonly double mach;
            internal Input(List<OcclusionData> source, Vessel vessel, double mach)
            {
                Support(source != null && source.Count > 0 && source.Count <= ConvectionFormat.MaxRows, "row-count");
                this.source = source; this.vessel = vessel; this.mach = mach; globals = Globals();
                real = new OcclusionData[source.Count]; shadow = new OcclusionData[source.Count]; owners = new PartThermalData[source.Count]; cones = new OcclusionCone[source.Count];
                var seenRows = new HashSet<OcclusionData>(); var seenThermal = new HashSet<PartThermalData>(); var seenCones = new HashSet<OcclusionCone>();
                for (int i = 0; i < real.Length; i++)
                {
                    OcclusionData row = source[source.Count - 1 - i];
                    Support(row != null && row.part != null && row.ptd != null && row.convCone != null && row.part.vessel == vessel &&
                        ReferenceEquals(row.part, row.ptd.part) && seenRows.Add(row) && seenThermal.Add(row.ptd) && seenCones.Add(row.convCone), "row-ownership");
                    real[i] = row; owners[i] = row.ptd; cones[i] = row.convCone;
                    var ptd = (PartThermalData)FormatterServices.GetUninitializedObject(typeof(PartThermalData));
                    ptd.part = row.ptd.part; ptd.convectionTempMultiplier = ptd.convectionCoeffMultiplier = ptd.convectionAreaMultiplier = 1;
                    var copy = new OcclusionData(ptd) { projectedArea = row.projectedArea, projectedRadius = row.projectedRadius, invFineness = row.invFineness,
                        minimumDot = row.minimumDot, maximumDot = row.maximumDot, centroidDot = row.centroidDot, maxWidthDepth = row.maxWidthDepth,
                        projectedCenter = row.projectedCenter, center = row.center, minExtents = row.minExtents, maxExtents = row.maxExtents, extents = row.extents };
                    CopyCone(row.convCone, copy.convCone, row.convCone.part); shadow[i] = copy;
                }
            }
            internal bool Matches()
            {
                if (source.Count != real.Length || FlightGlobals.ActiveVessel != vessel || vessel == null || !vessel.loaded || vessel.packed) return false;
                for (int i = 0; i < real.Length; i++) if (!ReferenceEquals(source[source.Count - 1 - i], real[i]) || !ReferenceEquals(real[i].ptd, owners[i]) ||
                    !ReferenceEquals(real[i].convCone, cones[i]) || real[i].part == null || real[i].part.vessel != vessel) return false;
                return SameGlobals(globals);
            }
        }
        internal sealed class Result
        {
            internal Input input;
            internal readonly List<int> accepted = new List<int>();
            internal long scalarCalls, reachedPairs, areaQueries, geometryQueries, certificateProbes, certificateAnchors, certificateSteps, nodeVisits, unitAreaCones;
        }
        internal ConvectionSparseKernel()
        {
            Pin(Target, typeof(OcclusionData), 100677560); Pin(AccessTools.DeclaredMethod(typeof(OcclusionCone), "Setup"), typeof(OcclusionCone), 100677569);
            Pin(AccessTools.DeclaredMethod(typeof(OcclusionCone), "GetShockRadius"), typeof(OcclusionCone), 100677570);
            MethodBody body = Target.GetMethodBody(); Require(Hash(body.GetILAsByteArray()) == ScalarIlHash && body.LocalVariables.Count == 8 && body.ExceptionHandlingClauses.Count == 0, "Scalar IL pin differs.");
            Type[] locals = { typeof(Vector2), typeof(Vector2), typeof(double), typeof(double), typeof(double), typeof(double), typeof(double), typeof(Vector3) };
            for (int i = 0; i < locals.Length; i++) Require(body.LocalVariables[i].LocalType == locals[i], "Scalar local shape differs.");
            full = Emit<Scalar>(Target, null); rectangle = Emit<Scalar>(Target, typeof(ConvectionSparseKernel).GetMethod("Rectangle", BindingFlags.NonPublic | BindingFlags.Static));
            geometry = Emit<Scalar>(Target, typeof(ConvectionSparseKernel).GetMethod("Geometry", BindingFlags.NonPublic | BindingFlags.Static));
        }
        internal static void Pin(MethodInfo method, Type declaring, int token)
        {
            Require(method != null && method.DeclaringType == declaring && method.MetadataToken == token && method.Module.ModuleVersionId == Mvid &&
                Harmony.GetPatchInfo(method)?.Owners.Any() != true, "Original method identity/ownership differs.");
        }
        internal static string Hash(byte[] data) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        internal static T Emit<T>(MethodInfo target, MethodInfo transpiler) where T : class
        {
            Type emitter = typeof(Harmony).Assembly.GetType("HarmonyLib.MethodPatcher", true); var empty = new List<MethodInfo>();
            var transforms = transpiler == null ? new List<MethodInfo>() : new List<MethodInfo> { transpiler };
            object patcher = Activator.CreateInstance(emitter, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new object[] { target, null, empty, empty, transforms, empty, false }, null);
            object[] output = { null };
            var replacement = (MethodInfo)emitter.GetMethod("CreateReplacement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(patcher, output);
            return replacement.CreateDelegate(typeof(T)) as T;
        }
        internal static int Local(CodeInstruction instruction)
        {
            if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
            if (instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Stloc_0) return 0;
            if (instruction.opcode == OpCodes.Ldloc_1 || instruction.opcode == OpCodes.Stloc_1) return 1;
            if (instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Stloc_2) return 2;
            if (instruction.opcode == OpCodes.Ldloc_3 || instruction.opcode == OpCodes.Stloc_3) return 3;
            return Convert.ToInt32(instruction.operand);
        }
        static CodeInstruction Load(CodeInstruction original) { return new CodeInstruction(original.opcode, original.operand); }
        static CodeInstruction[] PinCode(IEnumerable<CodeInstruction> source)
        {
            var code = source.Select(item => new CodeInstruction(item)).ToArray();
            Require(code.Length == 155 && code[99].Calls(AccessTools.DeclaredMethod(typeof(OcclusionData), "AreaOfIntersection")) &&
                code[114].opcode == OpCodes.Ldarg_2 && code[114].labels.Count == 0 && code[73].opcode == OpCodes.Ldloc_3 &&
                code[74].opcode == OpCodes.Ldc_R8 && (double)code[74].operand == .99 && code[75].opcode == OpCodes.Bge_Un_S &&
                code[104].opcode == OpCodes.Ldc_R8 && (double)code[104].operand == 1 && code[105].opcode == OpCodes.Stloc_3 &&
                Local(code[70]) == Local(code[73]) && Local(code[102]) == Local(code[107]) && code[146].opcode == OpCodes.Ldc_R8 &&
                code[154].opcode == OpCodes.Ret && code.All(item => item.blocks.Count == 0), "Scalar extraction shape differs."); return code;
        }
        static IEnumerable<CodeInstruction> Rectangle(IEnumerable<CodeInstruction> source, ILGenerator generator)
        {
            var code = PinCode(source); Label area = generator.DefineLabel();
            for (int i = 0; i <= 75; i++) yield return code[i];
            yield return new CodeInstruction(OpCodes.Br, area); yield return code[104]; yield return code[105];
            var output = new CodeInstruction(OpCodes.Ldarg_2); output.labels.Add(area); yield return output;
            yield return Load(code[73]); yield return new CodeInstruction(OpCodes.Stind_R8);
            for (int i = 146; i < code.Length; i++) yield return code[i];
        }
        static IEnumerable<CodeInstruction> Geometry(IEnumerable<CodeInstruction> source)
        {
            var code = PinCode(source); for (int i = 0; i <= 113; i++) yield return code[i];
            yield return new CodeInstruction(OpCodes.Ldarg_2); yield return Load(code[73]); yield return new CodeInstruction(OpCodes.Stind_R8);
            yield return new CodeInstruction(OpCodes.Ldarg_3); yield return Load(code[107]); yield return new CodeInstruction(OpCodes.Stind_R8);
            for (int i = 146; i < code.Length; i++) yield return code[i];
        }
        internal static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static void Support(bool value, string reason) { if (!value) throw new Unsupported(reason); }
        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        static bool Bounded(double x, double max) { return Finite(x) && Math.Abs(x) <= max; }
        static bool Unit(double x) { return Finite(x) && x >= 0 && x <= 1 && BitConverter.DoubleToInt64Bits(x) != long.MinValue; }
        static bool Bounded(Vector2 x) { return Bounded(x.x, 1e10) && Bounded(x.y, 1e10); }
        static bool Bounded(Vector3 x) { return Bounded(x.x, 1e10) && Bounded(x.y, 1e10) && Bounded(x.z, 1e10); }
        internal static double[] Globals()
        {
            return new[] { OcclusionCone.detachedShockHeatMult, OcclusionCone.detachedShockCoeffMult, OcclusionCone.detachedBehindShockHeatMult,
                OcclusionCone.detachedBehindShockCoeffMult, OcclusionCone.detachedShockMachAngleMult, OcclusionCone.detachedShockStartAngle, OcclusionCone.detachedShockEndAngle,
                OcclusionCone.obliqueShockAngleMult, OcclusionCone.obliqueShockPartAngleMult, OcclusionCone.obliqueShockMinAngleMult, OcclusionCone.obliqueShockConeHeatMult,
                OcclusionCone.obliqueShockConeCoeffMult, OcclusionCone.obliqueShockCylHeatMult, OcclusionCone.obliqueShockCylCoeffMult };
        }
        static bool SameGlobals(double[] expected)
        {
            double[] actual = Globals(); for (int i = 0; i < expected.Length; i++) if (BitConverter.DoubleToInt64Bits(expected[i]) != BitConverter.DoubleToInt64Bits(actual[i])) return false; return true;
        }
        internal static OcclusionCone CloneCone(OcclusionCone source) { var copy = new OcclusionCone(); CopyCone(source, copy, source.part); return copy; }
        internal static void CopyCone(OcclusionCone source, OcclusionCone target, OcclusionData part)
        {
            target.part = part; target.center = source.center; target.extents = source.extents; target.offset = source.offset; target.radius = source.radius;
            target.cylNoseDot = source.cylNoseDot; target.shockNoseDot = source.shockNoseDot; target.shockAngle = source.shockAngle;
            target.shockConvectionTempMult = source.shockConvectionTempMult; target.shockConvectionCoeffMult = source.shockConvectionCoeffMult;
            target.occludeConvectionTempMult = source.occludeConvectionTempMult; target.occludeConvectionCoeffMult = source.occludeConvectionCoeffMult; target.occludeConvectionAreaMult = source.occludeConvectionAreaMult;
        }
        internal Result Run(Input input)
        {
            Support(!GameSettings.FI_LOG_TEMP_ERROR && Bounded(input.mach, 1e10) && input.mach > 1, "logging-or-mach-domain");
            for (int i = 0; i < 14; i++) Support(i <= 3 || i >= 10 ? Unit(input.globals[i]) : Bounded(input.globals[i], 1e4), "global-domain");
            OcclusionData[] data = input.shadow; double minRadius = double.PositiveInfinity, minDot = double.PositiveInfinity, maxDot = double.NegativeInfinity;
            foreach (OcclusionData row in data)
            {
                Support(Bounded(row.projectedArea, 1e20) && row.projectedArea >= 0 && Bounded(row.projectedRadius, 1e10) && row.projectedRadius >= 0 &&
                    (row.projectedRadius == 0 || row.projectedRadius >= 1e-6) && Bounded(row.invFineness, 1e4) && row.invFineness >= 0 &&
                    Bounded(row.minimumDot, 1e10) && Bounded(row.maximumDot, 1e10) && Bounded(row.centroidDot, 1e10) && Bounded(row.maxWidthDepth, 1e10) && row.maxWidthDepth >= 0 &&
                    Bounded(row.projectedCenter) && Bounded(row.center) && Bounded(row.minExtents) && Bounded(row.maxExtents) && Bounded(row.extents) &&
                    row.minExtents.x <= row.maxExtents.x && row.minExtents.y <= row.maxExtents.y && row.extents.x >= 0 && row.extents.y >= 0, "projected-row-domain");
                if (row.projectedRadius > 0) minRadius = Math.Min(minRadius, row.projectedRadius);
                minDot = Math.Min(minDot, row.centroidDot); maxDot = Math.Max(maxDot, row.centroidDot);
            }
            if (double.IsPositiveInfinity(minRadius)) minRadius = 1;
            var result = new Result { input = input }; var accepted = new List<OcclusionCone>(); var index = new ConvectionSparseIndex(data);
            var cylinders = new double[data.Length]; var shocks = new double[data.Length]; var cylinderKnown = new bool[data.Length]; var known = new bool[data.Length];
            double maxRadius = 0, sqrtMach = Math.Sqrt(input.mach), machAngle = Math.Asin(1 / sqrtMach), detachAngle = .7957 * (1 - 1 / (input.mach * sqrtMach));
            for (int row = 0; row < data.Length; row++)
            {
                OcclusionData current = data[row]; int reached = accepted.Count; double area = 1;
                Array.Clear(known, 0, accepted.Count); Array.Clear(cylinderKnown, 0, accepted.Count);
                IReadOnlyList<int> candidates = index.Candidates(row);
                for (int query = 0; query < candidates.Count; query++)
                {
                    int pair = candidates[query]; double c = 0, ignored = 0; double factor = rectangle(current, accepted[pair], ref c, ref ignored); result.areaQueries++;
                    Support(Unit(c) && Unit(factor), "rectangle-domain"); cylinders[pair] = c; cylinderKnown[pair] = true; area *= factor;
                    if (area <= .001) { area = 0; reached = pair + 1; break; }
                }
                result.reachedPairs += reached; double ratio = maxRadius / minRadius, shockBound = 8 * (ratio + 1) * (ratio + 1);
                Support(Finite(shockBound) && shockBound >= 1, "shock-envelope"); int last = reached - 1;
                while (last >= 0 && Shock(last) != 1) last--;
                if (last >= 0) Certify(last); else last = 0;
                for (int pair = last; pair < reached; pair++) { full(current, accepted[pair], ref current.ptd.convectionTempMultiplier, ref current.ptd.convectionCoeffMultiplier); result.scalarCalls++; }
                Support(Finite(current.ptd.convectionTempMultiplier) && Finite(current.ptd.convectionCoeffMultiplier), "tail-nonfinite"); current.ptd.convectionAreaMultiplier = area;
                if (area > 0)
                {
                    current.convCone.Setup(current, sqrtMach, machAngle, detachAngle); OcclusionCone cone = current.convCone;
                    Support(Unit(cone.shockConvectionTempMult) && Unit(cone.shockConvectionCoeffMult) && Unit(cone.occludeConvectionTempMult) && Unit(cone.occludeConvectionCoeffMult) && Unit(cone.occludeConvectionAreaMult), "published-multiplier-domain");
                    double left = cone.GetShockRadius(minDot), right = cone.GetShockRadius(maxDot);
                    Support(Bounded(left, 1e20) && Bounded(right, 1e20) && Bounded(cone.radius, 1e10) && Bounded(cone.shockNoseDot, 1e15) && Bounded(cone.shockAngle, 1e8), "published-radius-domain");
                    maxRadius = Math.Max(maxRadius, Math.Max(Math.Abs(left), Math.Abs(right))); index.Insert(cone, row, accepted.Count); accepted.Add(cone); result.accepted.Add(row);
                }
                double Shock(int pair)
                {
                    if (!known[pair])
                    {
                        double c = 0, s = 0; geometry(current, accepted[pair], ref c, ref s); result.geometryQueries++;
                        Support(Unit(c) && (!cylinderKnown[pair] || BitConverter.DoubleToInt64Bits(c) == BitConverter.DoubleToInt64Bits(cylinders[pair])) && Finite(s) && Math.Abs(s) <= shockBound, "geometry-envelope");
                        cylinders[pair] = c; cylinderKnown[pair] = known[pair] = true; shocks[pair] = s;
                    }
                    return shocks[pair];
                }
                void Certify(int end)
                {
                    int first = 0; double bound = 1, growthBits = Math.Log(8 * (shockBound + 4), 2) + 1;
                    while (first < end)
                    {
                        int span = (int)Math.Floor((880 - Math.Log(bound, 2)) / growthBits); Support(span >= 1, "finite-prefix-exhausted");
                        int limit = Math.Min(end - 1, first + span - 1), anchor = limit;
                        while (anchor >= first) { result.certificateProbes++; if (Shock(anchor) == 1) break; anchor--; }
                        if (anchor >= first) { result.certificateAnchors++; bound = 4; first = anchor + 1; }
                        else
                        {
                            for (int pair = first; pair <= limit; pair++)
                            {
                                double s = Shock(pair), c = cylinders[pair]; bound = 2 * (Math.Abs(1 - s) * bound + Math.Abs(s - c) + c + 1); result.certificateSteps++;
                                Support(Finite(bound) && bound <= 1e250, "finite-prefix-exhausted");
                            }
                            first = limit + 1;
                        }
                    }
                }
            }
            Support(input.Matches(), "source-or-global-state-changed"); result.nodeVisits = index.nodeVisits; result.unitAreaCones = index.unitAreaCones; return result;
        }
        internal static void Publish(FlightIntegrator integrator, Result result)
        {
            Input input = result.input; Support(input.Matches() && integrator.occludersConvection != null && integrator.occludersConvection.Length >= result.accepted.Count, "publication-owner-changed");
            for (int row = 0; row < input.real.Length; row++)
            {
                input.owners[row].convectionTempMultiplier = input.shadow[row].ptd.convectionTempMultiplier;
                input.owners[row].convectionCoeffMultiplier = input.shadow[row].ptd.convectionCoeffMultiplier;
                input.owners[row].convectionAreaMultiplier = input.shadow[row].ptd.convectionAreaMultiplier;
            }
            for (int at = 0; at < result.accepted.Count; at++)
            {
                int row = result.accepted[at]; CopyCone(input.shadow[row].convCone, input.cones[row], input.real[row]); integrator.occludersConvection[at] = input.cones[row];
            }
            integrator.occludersConvectionCount = result.accepted.Count;
        }
    }
}
