using System;

namespace KspContinuum
{
    [Serializable]
    public struct BodyAeroVector
    {
        public float x, y, z;
        public BodyAeroVector(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        internal bool Finite { get { return Number(x) && Number(y) && Number(z); } }
        internal static bool Number(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static float Dot(BodyAeroVector a, BodyAeroVector b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
    }

    [Serializable]
    public sealed class OrderedBodyAeroRow
    {
        public int ordinal, recipientBodyId;
        public uint partId;
        public bool ownBody, servoBody, shielded, applyDrag, applyParentDrag, angularDragByIntegrator;
        public bool accelerationMode, bodyLiftEligible, liftForceEligible, liftGeometryCaptured;
        public double submergedPortion, atmosphericDensity, originalDragValue, pseudoReMultiplier;
        public float angularDrag, angularDragMultiplier, dragMultiplier, bodyLiftMultiplier, globalBodyLiftMultiplier, liftMachSample;
        public BodyAeroVector velocity, dragApplicationPoint, liftApplicationPoint, rotatedScaledLift;
        public string providerEpoch;

        public OrderedBodyAeroRow Copy() { return (OrderedBodyAeroRow)MemberwiseClone(); }
    }

    [Serializable]
    public sealed class OrderedBodyAeroPacket
    {
        public const string Scope = "Ordered dry body-aero scalar/command tail only. Original queued-force consumption, recursive preorder, SetDrag/area/drag/provider/curve dispatch, native transforms, body-lift local-field publication, wings and Unity integration remain outside this kernel. Original command tape must record actual angularDrag setters and AddForceAtPosition operands; helper-prefix force reconstructions alone are incomplete. Commands use original Unity force units and original Unity world application points, not SI-converted donor vectors.";
        public string schema = "continuum-ordered-dry-body-aero/v1";
        public string boundary = "after-original-queued-force-consumption";
        public string runId, vesselId, topologySha256, providerEpoch;
        public long invocation;
        public OrderedBodyAeroRow[] rows;
        public const int MaximumRows = 4096;

        public OrderedBodyAeroPacket Freeze()
        {
            if (schema != "continuum-ordered-dry-body-aero/v1" || boundary != "after-original-queued-force-consumption" || string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(vesselId) ||
                string.IsNullOrEmpty(providerEpoch) || invocation < 0 || !Digest(topologySha256) || rows == null || rows.Length == 0 || rows.Length > MaximumRows)
                throw new ArgumentException("Body-aero invocation identity or bounds absent.");
            var frozen = (OrderedBodyAeroPacket)MemberwiseClone(); frozen.rows = new OrderedBodyAeroRow[rows.Length];
            var parts = new System.Collections.Generic.HashSet<uint>();
            for (int i = 0; i < rows.Length; i++)
            {
                OrderedBodyAeroRow row = rows[i];
                if (row == null || row.ordinal != i || row.partId == 0 || row.recipientBodyId == 0 || !parts.Add(row.partId) || row.providerEpoch != providerEpoch)
                    throw new ArgumentException("Ordered part, recipient or provider epoch differs.");
                frozen.rows[i] = row.Copy();
            }
            return frozen;
        }
        static bool Digest(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
    }

    public enum BodyAeroCommandKind { AngularDrag, DragAtPosition, LiftAtPosition }
    [Serializable]
    public struct BodyAeroCommand
    {
        public int ordinal, recipientBodyId, forceMode;
        public uint partId;
        public BodyAeroCommandKind kind;
        public BodyAeroVector force, point;
        public float angularDrag;
    }
    [Serializable]
    public sealed class OrderedBodyAeroResult
    {
        public int ordinal;
        public uint partId;
        public float speedSquared, speed, dragScalar, bodyLiftScalar;
        public BodyAeroVector direction;
        public double dynamicPressure;
        public bool bodyLiftScalarWritten;
        public BodyAeroCommand[] commands;
    }
}
