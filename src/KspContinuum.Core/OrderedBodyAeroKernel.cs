using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public static class OrderedBodyAeroKernel
    {
        // Native pose/application-point capture, SetDrag, area/drag providers and the
        // actual lift-curve call remain original ordered inputs. This dry tail emits
        // a command tape only; it never calls a provider or publishes a force.
        public static OrderedBodyAeroResult[] Evaluate(OrderedBodyAeroPacket input)
        {
            if (input == null) throw new ArgumentNullException("input");
            OrderedBodyAeroPacket packet = input.Freeze();
            var output = new OrderedBodyAeroResult[packet.rows.Length];
            for (int i = 0; i < output.Length; i++) output[i] = EvaluateRow(packet.rows[i]);
            return output;
        }
        static void Require(bool value, string message) { if (!value) throw new ArgumentException(message); }
        static bool Finite(double value) { return BodyAeroVector.Number(value); }
        static OrderedBodyAeroResult EvaluateRow(OrderedBodyAeroRow row)
        {
            Require(!row.servoBody && !row.shielded && row.submergedPortion == 0 && row.atmosphericDensity > 0 && row.applyDrag &&
                (row.ownBody || row.applyParentDrag), "Unsupported servo, shielded, submerged, vacuum or disabled dry-body owner.");
            Require(row.velocity.Finite && row.dragApplicationPoint.Finite && row.liftApplicationPoint.Finite && Finite(row.atmosphericDensity) &&
                Finite(row.pseudoReMultiplier) && Finite(row.angularDrag) && Finite(row.angularDragMultiplier) && Finite(row.dragMultiplier) &&
                Finite(row.bodyLiftMultiplier) && Finite(row.globalBodyLiftMultiplier) && Finite(row.liftMachSample) && !double.IsInfinity(row.originalDragValue),
                "Nonfinite body-aero input.");
            float square = BodyAeroVector.Dot(row.velocity, row.velocity);
            Require(Finite(square) && square > 0, "Zero/overflow velocity requires original branch.");
            float speed = (float)Math.Sqrt(square);
            var direction = new BodyAeroVector(row.velocity.x / speed, row.velocity.y / speed, row.velocity.z / speed);
            double pressure = row.atmosphericDensity * (0.0005 * (double)square);
            double value = row.originalDragValue * row.pseudoReMultiplier;
            float drag = double.IsNaN(value) || value == 0 ? 0 : (float)(pressure * value * 1.0) * row.dragMultiplier;
            Require(Finite(pressure) && Finite(drag), "Overflow body-aero result.");
            var result = new OrderedBodyAeroResult { ordinal = row.ordinal, partId = row.partId, speedSquared = square,
                speed = speed, direction = direction, dynamicPressure = pressure, dragScalar = drag };
            var commands = new List<BodyAeroCommand>(4);
            if (row.ownBody && row.angularDragByIntegrator)
            {
                commands.Add(Command(row, BodyAeroCommandKind.AngularDrag, 0));
                double angularPressure = pressure * 0.009869232667160128;
                if (angularPressure <= 0) angularPressure = 0;
                BodyAeroCommand update = Command(row, BodyAeroCommandKind.AngularDrag, 0);
                update.angularDrag = row.angularDrag * (float)angularPressure * row.angularDragMultiplier;
                Require(Finite(update.angularDrag), "Overflow angular drag."); commands.Add(update);
            }
            if (!double.IsNaN(value) && value != 0)
            {
                BodyAeroCommand command = Command(row, BodyAeroCommandKind.DragAtPosition, row.accelerationMode ? 5 : 0);
                command.force = new BodyAeroVector(-direction.x * drag, -direction.y * drag, -direction.z * drag);
                command.point = row.dragApplicationPoint; commands.Add(command);
            }
            if (row.bodyLiftEligible)
            {
                result.bodyLiftScalarWritten = true;
                result.bodyLiftScalar = (float)pressure * row.bodyLiftMultiplier * row.globalBodyLiftMultiplier * row.liftMachSample;
                Require(Finite(result.bodyLiftScalar), "Overflow body lift.");
                if (result.bodyLiftScalar != 0 && row.liftForceEligible)
                {
                    Require(row.liftGeometryCaptured && row.rotatedScaledLift.Finite, "Original scaled/rotated lift geometry absent.");
                    BodyAeroVector vector = row.rotatedScaledLift;
                    // Unity's component multiplication precedes division by the
                    // normal square; factoring dot/square changes float rounding.
                    var normal = new BodyAeroVector(-direction.x, -direction.y, -direction.z);
                    float norm = BodyAeroVector.Dot(normal, normal), dot = BodyAeroVector.Dot(vector, normal);
                    BodyAeroCommand command = Command(row, BodyAeroCommandKind.LiftAtPosition, row.accelerationMode ? 5 : 0);
                    command.force = norm < float.Epsilon ? vector : new BodyAeroVector(vector.x - normal.x * dot / norm,
                        vector.y - normal.y * dot / norm, vector.z - normal.z * dot / norm);
                    command.point = row.liftApplicationPoint;
                    Require(command.force.Finite, "Overflow projected lift."); commands.Add(command);
                }
            }
            result.commands = commands.ToArray(); return result;
        }
        static BodyAeroCommand Command(OrderedBodyAeroRow row, BodyAeroCommandKind kind, int mode)
        { return new BodyAeroCommand { ordinal = row.ordinal, partId = row.partId, recipientBodyId = row.recipientBodyId, kind = kind, forceMode = mode }; }
    }
}
