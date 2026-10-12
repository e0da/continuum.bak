using System;

namespace KspContinuum
{
    [Serializable]
    public sealed class OrderedBodyAeroObservation
    {
        public string runId, vesselId, topologySha256, providerEpoch;
        public string boundary = "after-original-queued-force-consumption";
        public long invocation;
        public bool originalReturned, originalCommandTapeComplete;
        public OrderedBodyAeroResult[] rows;
    }

    public sealed class ValidatedOrderedBodyAero
    {
        readonly OrderedBodyAeroResult[] rows;
        public readonly string RunId, VesselId, TopologySha256, ProviderEpoch;
        public readonly long Invocation;
        internal ValidatedOrderedBodyAero(OrderedBodyAeroPacket input, OrderedBodyAeroResult[] result)
        { RunId = input.runId; VesselId = input.vesselId; TopologySha256 = input.topologySha256; ProviderEpoch = input.providerEpoch;
            Invocation = input.invocation; rows = result; }
        public int Count { get { return rows.Length; } }
        public OrderedBodyAeroResult At(int ordinal) { return OrderedBodyAeroGate.Copy(rows[ordinal]); }
    }

    public static class OrderedBodyAeroGate
    {
        public const double RelativeTolerance = 2e-5, AbsoluteTolerance = 1e-6;
        // A validation token is not a publication owner. It authorizes no Unity
        // operation; later adapters must recheck topology/epoch before any commit.
        public static bool TryValidate(OrderedBodyAeroPacket input, OrderedBodyAeroObservation original,
            out ValidatedOrderedBodyAero validated, out string reason)
        {
            validated = null; reason = null;
            try
            {
                Require(input != null, "Body-aero packet absent.");
                OrderedBodyAeroPacket packet = input.Freeze();
                Require(original != null && original.originalReturned && original.originalCommandTapeComplete && original.runId == packet.runId &&
                    original.vesselId == packet.vesselId && original.invocation == packet.invocation && original.topologySha256 == packet.topologySha256 &&
                    original.providerEpoch == packet.providerEpoch && original.boundary == packet.boundary && original.rows != null && original.rows.Length == packet.rows.Length,
                    "Original-return observation is absent, incomplete or from a different invocation.");
                var observed = new OrderedBodyAeroResult[original.rows.Length];
                for (int i = 0; i < observed.Length; i++) observed[i] = Copy(original.rows[i]);
                OrderedBodyAeroResult[] candidate = OrderedBodyAeroKernel.Evaluate(packet);
                for (int i = 0; i < candidate.Length; i++) Match(candidate[i], observed[i]);
                validated = new ValidatedOrderedBodyAero(packet, candidate); return true;
            }
            catch (ArgumentException error) { reason = error.Message; return false; }
        }
        internal static OrderedBodyAeroResult Copy(OrderedBodyAeroResult value)
        {
            Require(value != null && value.commands != null && value.commands.Length <= 4, "Original body-aero row/tape missing or unbounded.");
            return new OrderedBodyAeroResult { ordinal = value.ordinal, partId = value.partId, speedSquared = value.speedSquared, speed = value.speed,
                direction = value.direction, dynamicPressure = value.dynamicPressure, dragScalar = value.dragScalar, bodyLiftScalar = value.bodyLiftScalar,
                bodyLiftScalarWritten = value.bodyLiftScalarWritten, commands = (BodyAeroCommand[])value.commands.Clone() };
        }
        static void Match(OrderedBodyAeroResult a, OrderedBodyAeroResult b)
        {
            Require(a.ordinal == b.ordinal && a.partId == b.partId && a.bodyLiftScalarWritten == b.bodyLiftScalarWritten && a.commands.Length == b.commands.Length,
                "Ordered body-aero identity, field-write mask or command count differs.");
            Number(a.speedSquared, b.speedSquared); Number(a.speed, b.speed); Vector(a.direction, b.direction, false);
            Number(a.dynamicPressure, b.dynamicPressure); Number(a.dragScalar, b.dragScalar);
            if (a.bodyLiftScalarWritten) Number(a.bodyLiftScalar, b.bodyLiftScalar);
            for (int i = 0; i < a.commands.Length; i++)
            {
                BodyAeroCommand x = a.commands[i], y = b.commands[i];
                Require(x.ordinal == y.ordinal && x.partId == y.partId && x.recipientBodyId == y.recipientBodyId && x.kind == y.kind && x.forceMode == y.forceMode,
                    "Original command order, recipient or force mode differs.");
                if (x.kind == BodyAeroCommandKind.AngularDrag) Number(x.angularDrag, y.angularDrag);
                else { Vector(x.force, y.force, false); Vector(x.point, y.point, true); }
            }
        }
        static void Vector(BodyAeroVector a, BodyAeroVector b, bool exact)
        {
            Require(a.Finite && b.Finite, "Nonfinite command vector.");
            if (exact) Require(Bits(a.x) == Bits(b.x) && Bits(a.y) == Bits(b.y) && Bits(a.z) == Bits(b.z), "Captured application point changed.");
            else { Number(a.x, b.x); Number(a.y, b.y); Number(a.z, b.z); }
        }
        static int Bits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
        static void Number(double a, double b)
        { Require(BodyAeroVector.Number(a) && BodyAeroVector.Number(b) && Math.Abs(a - b) <= AbsoluteTolerance + RelativeTolerance * Math.Abs(b), "Body-aero scalar/force tolerance exceeded."); }
        static void Require(bool value, string reason) { if (!value) throw new ArgumentException(reason); }
    }
}
