using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal static class AeroDragTransaction
    {
        internal const double Tolerance = 2e-5;
        static readonly AccessTools.FieldRef<DragCubeList, DragCubeList.CubeData> Data =
            AccessTools.FieldRefAccess<DragCubeList, DragCubeList.CubeData>("cubeData");
        static readonly FieldInfo Directions = AccessTools.DeclaredField(typeof(DragCubeList), "faceDirections");
        static Vector3[] directions;

        internal static int Qualify()
        {
            if (Directions == null || !Directions.IsStatic || Directions.DeclaringType != typeof(DragCubeList) ||
                Directions.FieldType != typeof(Vector3[]) || Directions.Module.ModuleVersionId != ConvectionSparseKernel.Mvid)
                throw new InvalidOperationException("Original drag face field identity differs.");
            directions = Directions.GetValue(null) as Vector3[];
            if (!DirectionLease()) throw new InvalidOperationException("Original drag face basis differs.");
            return Directions.MetadataToken;
        }
        internal static bool DirectionLease()
        {
            if (directions == null || directions.Length != 6) return false;
            for (int face = 0; face < 6; face++)
            {
                int axis = face >> 1; float sign = (face & 1) == 0 ? 1 : -1;
                Vector3 vector = directions[face];
                if (vector.x != (axis == 0 ? sign : 0) || vector.y != (axis == 1 ? sign : 0) || vector.z != (axis == 2 ? sign : 0)) return false;
            }
            return true;
        }
        internal static bool FinalLease() { return ReferenceEquals(Directions.GetValue(null), directions) && DirectionLease(); }

        internal static bool Admit(DragCubeList cubes, FloatCurve dragCd, FloatCurve lift)
        {
            if (cubes.None) return true;
            if (!ReferenceEquals(cubes.DragCurveCd, dragCd) || cubes.BodyLiftCurve == null ||
                !ReferenceEquals(cubes.BodyLiftCurve.liftCurve, lift)) return false;
            return Faces(cubes.AreaOccluded) && Faces(cubes.WeightedDrag) && Faces(cubes.WeightedDepth);
        }
        static bool Faces(float[] values)
        {
            if (values == null || values.Length != 6) return false;
            for (int i = 0; i < 6; i++) if (!Finite(values[i]) || values[i] < 0) return false;
            return true;
        }
        internal static DragCubeList.CubeData Snapshot(DragCubeList cubes) { return Data(cubes); }
        internal static void Publish(DragCubeList cubes, DragCubeList.CubeData value) { Data(cubes) = value; }
        static AeroFaceValues Copy(float[] values)
        {
            return AeroFaceValues.Trusted(values[0], values[1], values[2], values[3], values[4], values[5]);
        }
        struct Samples
        {
            int mask;
            float tail, surface, tip, multiplier, power;
            internal float Get(AeroStepContext.Context context, int role)
            {
                int bit = 1 << role;
                if ((mask & bit) == 0)
                {
                    float value = AeroStepContext.EvaluateRole(context, role);
                    switch (role) { case 0: tail = value; break; case 1: surface = value; break; case 2: tip = value; break; case 3: multiplier = value; break; case 4: power = value; break; default: throw new InvalidOperationException("Unknown drag role."); }
                    mask |= bit;
                }
                switch (role) { case 0: return tail; case 1: return surface; case 2: return tip; case 3: return multiplier; case 4: return power; default: throw new InvalidOperationException("Unknown drag role."); }
            }
        }
        internal static DragCubeList.CubeData Calculate(DragCubeList cubes, Vector3 input, AeroStepContext.Context context)
        {
            Vector3 direction = -input;
            if (cubes.RotateDragVector) direction = cubes.DragVectorRotation * direction;
            double magnitudeSquared = direction.sqrMagnitude;
            if (!Finite(magnitudeSquared) || (magnitudeSquared != 0 && Math.Abs(magnitudeSquared - 1) > 1e-4))
                throw new ArgumentException("Direction must be finite and unit length or zero.");
            AeroFaceValues areas = Copy(cubes.AreaOccluded), drags = Copy(cubes.WeightedDrag), depths = Copy(cubes.WeightedDepth);
            var samples = new Samples();
            double area = 0, areaDrag = 0, section = 0, exposure = 0, dotSum = 0;
            double depth = 0, taper = 0, liftX = 0, liftY = 0, liftZ = 0;
            for (int face = 0; face < 6; face++)
            {
                int axis = face >> 1;
                double sign = (face & 1) == 0 ? 1 : -1;
                double dot = (axis == 0 ? direction.x : axis == 1 ? direction.y : direction.z) * sign;
                float normalized = ((float)dot + 1f) * .5f;
                bool tailSide = normalized <= .5f;
                double left = samples.Get(context, tailSide ? 0 : 1), right = samples.Get(context, tailSide ? 1 : 2);
                double multiplier = samples.Get(context, 3);
                if (multiplier == 0) throw new ArgumentException("Surface multiplier must be nonzero.");
                double amount = tailSide ? normalized * 2f : (normalized - .5f) * 2f;
                double faceArea = areas.At(face), drag = drags.At(face);
                double directionalArea = faceArea * (left + (right - left) * Math.Max(0, Math.Min(1, amount))) * multiplier;
                area += directionalArea;
                double dragCd = drag;
                if (drag < 1)
                {
                    float sample = cubes.DragCurveCd.Evaluate((float)drag);
                    if (!Finite(sample)) throw new ArithmeticException("Direction drag curve output is nonfinite.");
                    dragCd = Math.Pow(sample, samples.Get(context, 4));
                }
                areaDrag += directionalArea * dragCd;
                section += faceArea * Math.Max(0, Math.Min(1, dot));
                double inverseDrag = drag > .01 && drag < 1 ? 1 / drag : 1;
                exposure += directionalArea / multiplier * inverseDrag;
                if (dot <= 0) continue;
                dotSum += dot;
                float lift = cubes.BodyLiftCurve.liftCurve.Evaluate((float)dot);
                if (!Finite(lift)) throw new ArithmeticException("Direction lift curve output is nonfinite.");
                double weightedLift = -dot * faceArea * drag * lift * sign;
                if (!double.IsNaN(weightedLift))
                {
                    if (axis == 0) liftX += weightedLift;
                    else if (axis == 1) liftY += weightedLift;
                    else liftZ += weightedLift;
                }
                depth += dot * depths.At(face); taper += dot * inverseDrag;
            }
            if (dotSum > 0) { depth /= dotSum; taper /= dotSum; }
            double coefficient = area > 0 ? areaDrag / area : 0;
            if (area <= 0) areaDrag = 0;
            var value = new DragCubeList.CubeData {
                dragVector = direction, liftForce = new Vector3((float)liftX, (float)liftY, (float)liftZ),
                area = (float)area, areaDrag = (float)areaDrag, depth = (float)depth,
                crossSectionalArea = (float)section, exposedArea = (float)exposure,
                dragCoeff = (float)coefficient, taperDot = (float)taper
            };
            double error;
            if (!Equivalent(value, value, out error)) throw new ArithmeticException("Complete drag output is nonfinite.");
            return value;
        }
        internal static bool Equivalent(DragCubeList.CubeData candidate, DragCubeList.CubeData stock, out double maximum)
        {
            maximum = Error(candidate.dragVector.x, stock.dragVector.x);
            maximum = Math.Max(maximum, Error(candidate.dragVector.y, stock.dragVector.y));
            maximum = Math.Max(maximum, Error(candidate.dragVector.z, stock.dragVector.z));
            maximum = Math.Max(maximum, Error(candidate.liftForce.x, stock.liftForce.x));
            maximum = Math.Max(maximum, Error(candidate.liftForce.y, stock.liftForce.y));
            maximum = Math.Max(maximum, Error(candidate.liftForce.z, stock.liftForce.z));
            maximum = Math.Max(maximum, Error(candidate.area, stock.area));
            maximum = Math.Max(maximum, Error(candidate.areaDrag, stock.areaDrag));
            maximum = Math.Max(maximum, Error(candidate.depth, stock.depth));
            maximum = Math.Max(maximum, Error(candidate.crossSectionalArea, stock.crossSectionalArea));
            maximum = Math.Max(maximum, Error(candidate.exposedArea, stock.exposedArea));
            maximum = Math.Max(maximum, Error(candidate.dragCoeff, stock.dragCoeff));
            maximum = Math.Max(maximum, Error(candidate.taperDot, stock.taperDot));
            return maximum <= Tolerance;
        }
        static double Error(float candidate, float stock)
        {
            if (!Finite(candidate) || !Finite(stock)) return 1;
            return Math.Abs(candidate - stock) / Math.Max(1, Math.Abs(stock));
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
