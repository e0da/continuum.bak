using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class ConvectionCandidateIndex
    {
        const double FloatEpsilon = 1.1920928955078125e-7;
        struct Box
        {
            internal double x0, y0, z0, x1, y1, z1;
            internal static Box Union(Box a, Box b) { return new Box { x0 = Math.Min(a.x0, b.x0), y0 = Math.Min(a.y0, b.y0), z0 = Math.Min(a.z0, b.z0), x1 = Math.Max(a.x1, b.x1), y1 = Math.Max(a.y1, b.y1), z1 = Math.Max(a.z1, b.z1) }; }
            internal bool Intersects(Box b) { return x1 >= b.x0 && x0 <= b.x1 && y1 >= b.y0 && y0 <= b.y1 && z1 >= b.z0 && z0 <= b.z1; }
            internal double Center(int axis) { return axis == 0 ? (x0 + x1) * .5 : axis == 1 ? (y0 + y1) * .5 : (z0 + z1) * .5; }
        }
        struct Node { internal Box Bounds; internal int Left, Right, Row, LastRow; }
        sealed class Tree
        {
            readonly Node[] nodes;
            readonly int root;
            int next;
            internal Tree(Box[] boxes)
            {
                var order = new int[boxes.Length]; Box all = boxes[0];
                for (int i = 0; i < order.Length; i++) { order[i] = i; all = Box.Union(all, boxes[i]); }
                int axis = all.x1 - all.x0 >= all.y1 - all.y0 ? 0 : 1;
                if (all.z1 - all.z0 > (axis == 0 ? all.x1 - all.x0 : all.y1 - all.y0)) axis = 2;
                Array.Sort(order, (a, b) => { int comparison = boxes[a].Center(axis).CompareTo(boxes[b].Center(axis)); return comparison == 0 ? a.CompareTo(b) : comparison; });
                nodes = new Node[checked(boxes.Length * 2 - 1)]; root = Build(boxes, order, 0, order.Length);
            }
            int Build(Box[] boxes, int[] order, int start, int count)
            {
                int at = next++;
                if (count == 1) { int row = order[start]; nodes[at] = new Node { Bounds = boxes[row], Row = row, LastRow = row, Left = -1, Right = -1 }; }
                else
                {
                    int left = Build(boxes, order, start, count / 2), right = Build(boxes, order, start + count / 2, count - count / 2);
                    nodes[at] = new Node { Bounds = Box.Union(nodes[left].Bounds, nodes[right].Bounds), Row = -1, LastRow = Math.Max(nodes[left].LastRow, nodes[right].LastRow), Left = left, Right = right };
                }
                return at;
            }
            internal void Query(Box box, int afterRow, ConvectionCandidateIndex owner) { Query(root, box, afterRow, owner); }
            void Query(int at, Box box, int afterRow, ConvectionCandidateIndex owner)
            {
                Node node = nodes[at]; owner.nodeVisits++;
                if (node.LastRow <= afterRow || !node.Bounds.Intersects(box)) return;
                if (node.Row >= 0) owner.AddRecipient(node.Row);
                else { Query(node.Left, box, afterRow, owner); Query(node.Right, box, afterRow, owner); }
            }
        }
        readonly Tree rectangles, centers;
        readonly List<int>[] candidates;
        readonly int[] stamps;
        readonly double minDot, maxDot, maxRadius, maxRectangleCoordinate, maxProjectedCoordinate;
        int stamp, insertingIndex, lastRow = -1;
        public bool Admitted { get; private set; } = true;
        public string FallbackReason { get; private set; }
        public long candidatePairs { get; private set; }
        public long possiblePairs { get; private set; }
        public long nodeVisits { get; private set; }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static bool Numbers(double[] values) { foreach (double value in values) if (!Finite(value)) return false; return true; }
        static bool Floats(float[] values) { foreach (float value in values) if (!Finite(value) || Math.Abs((double)value) > 1e15) return false; return true; }
        static bool Nonnegative(double value) { return Finite(value) && value >= 0 && !(value == 0 && BitConverter.DoubleToInt64Bits(value) < 0); }
        public static bool TryCreate(ConvectionFrame frame, out ConvectionCandidateIndex index, out string reason)
        {
            index = null; reason = null;
            try { ConvectionFormat.ValidateFrame(frame); } catch (Exception error) { reason = "shape: " + error.Message; return false; }
            if (!Finite(frame.mach) || frame.mach <= 1 || !Numbers(frame.globals)) { reason = "nonfinite-or-subsonic-frame"; return false; }
            foreach (int i in new[] { 0, 1, 2, 3, 10, 11, 12, 13 })
                if (!Nonnegative(frame.globals[i])) { reason = "unsupported-negative-or-signed-zero-multiplier"; return false; }
            foreach (ConvectionRow row in frame.rows)
            {
                if (!Numbers(new[] { row.projectedArea, row.projectedRadius, row.invFineness, row.minimumDot, row.maximumDot, row.centroidDot, row.maxWidthDepth }) ||
                    !Floats(row.projectedCenter) || !Floats(row.center) || !Floats(row.minExtents) || !Floats(row.maxExtents) || !Floats(row.extents) ||
                    row.projectedRadius < 0 || row.projectedRadius > 1e100 || row.minimumDot > row.maximumDot || row.minExtents[0] > row.maxExtents[0] || row.minExtents[1] > row.maxExtents[1] || row.extents[0] < 0 || row.extents[1] < 0)
                { reason = "nonfinite-or-unsupported-row"; return false; }
            }
            index = new ConvectionCandidateIndex(frame); return true;
        }
        ConvectionCandidateIndex(ConvectionFrame frame)
        {
            int count = frame.rows.Length;
            candidates = new List<int>[count]; stamps = new int[count];
            var rects = new Box[count]; var points = new Box[count];
            minDot = double.PositiveInfinity; maxDot = double.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                ConvectionRow row = frame.rows[i]; candidates[i] = new List<int>();
                rects[i] = new Box { x0 = row.minExtents[0], y0 = row.minExtents[1], x1 = row.maxExtents[0], y1 = row.maxExtents[1] };
                points[i] = new Box { x0 = row.projectedCenter[0], y0 = row.projectedCenter[1], z0 = row.projectedCenter[2], x1 = row.projectedCenter[0], y1 = row.projectedCenter[1], z1 = row.projectedCenter[2] };
                minDot = Math.Min(minDot, row.centroidDot); maxDot = Math.Max(maxDot, row.centroidDot); maxRadius = Math.Max(maxRadius, row.projectedRadius);
                foreach (float value in row.minExtents) maxRectangleCoordinate = Math.Max(maxRectangleCoordinate, Math.Abs((double)value));
                foreach (float value in row.maxExtents) maxRectangleCoordinate = Math.Max(maxRectangleCoordinate, Math.Abs((double)value));
                foreach (float value in row.projectedCenter) maxProjectedCoordinate = Math.Max(maxProjectedCoordinate, Math.Abs((double)value));
            }
            rectangles = new Tree(rects); centers = new Tree(points);
        }
        bool Reject(string reason) { Admitted = false; FallbackReason = reason; return false; }
        public bool Insert(ConvectionCone cone, int acceptedIndex)
        {
            if (!Admitted) return false;
            if (cone == null || cone.rowIndex <= lastRow || cone.rowIndex >= candidates.Length || acceptedIndex != stamp ||
                cone.center == null || cone.center.Length != 3 || cone.extents == null || cone.extents.Length != 2 || cone.offset == null || cone.offset.Length != 2 ||
                !Floats(cone.center) || !Floats(cone.extents) || !Floats(cone.offset) ||
                !Numbers(new[] { cone.radius, cone.cylNoseDot, cone.shockNoseDot, cone.shockAngle, cone.shockConvectionTempMult, cone.shockConvectionCoeffMult, cone.occludeConvectionTempMult, cone.occludeConvectionCoeffMult, cone.occludeConvectionAreaMult }) ||
                cone.radius < 0 || cone.extents[0] < 0 || cone.extents[1] < 0 ||
                !Nonnegative(cone.shockConvectionTempMult) || !Nonnegative(cone.shockConvectionCoeffMult) || !Nonnegative(cone.occludeConvectionTempMult) || !Nonnegative(cone.occludeConvectionCoeffMult)) return Reject("nonfinite-or-unsupported-accepted-cone");
            double tangent = Math.Tan(cone.shockAngle);
            double endpoint0 = cone.radius + (cone.shockNoseDot - minDot) * tangent;
            double endpoint1 = cone.radius + (cone.shockNoseDot - maxDot) * tangent;
            double radius = Math.Max(Math.Abs(endpoint0), Math.Abs(endpoint1)) + maxRadius;
            double arithmetic = Math.Abs(cone.radius) + (Math.Abs(cone.shockNoseDot) + Math.Max(Math.Abs(minDot), Math.Abs(maxDot))) * Math.Abs(tangent) + maxRadius;
            // Envelopes cover the whole receiver depth domain and binary32 subtraction/dot rounding.
            radius += arithmetic * 1e-13;
            double centerMagnitude = Math.Max(Math.Abs((double)cone.center[0]), Math.Max(Math.Abs((double)cone.center[1]), Math.Abs((double)cone.center[2])));
            radius += 64 * FloatEpsilon * (maxProjectedCoordinate + centerMagnitude + radius + 1);
            double padding = 64 * FloatEpsilon * (maxRectangleCoordinate + Math.Max(Math.Abs((double)cone.offset[0]), Math.Abs((double)cone.offset[1])) + Math.Max(cone.extents[0], cone.extents[1]) + 1);
            if (!Finite(radius) || radius > 1e100 || !Finite(padding)) return Reject("shock-envelope-overflow");
            lastRow = cone.rowIndex; insertingIndex = acceptedIndex; stamp++;
            possiblePairs += candidates.Length - 1 - cone.rowIndex;
            rectangles.Query(new Box { x0 = -(double)cone.offset[0] - cone.extents[0] - padding, x1 = -(double)cone.offset[0] + cone.extents[0] + padding,
                y0 = -(double)cone.offset[1] - cone.extents[1] - padding, y1 = -(double)cone.offset[1] + cone.extents[1] + padding }, cone.rowIndex, this);
            centers.Query(new Box { x0 = cone.center[0] - radius, x1 = cone.center[0] + radius, y0 = cone.center[1] - radius, y1 = cone.center[1] + radius,
                z0 = cone.center[2] - radius, z1 = cone.center[2] + radius }, cone.rowIndex, this);
            return true;
        }
        void AddRecipient(int row)
        {
            if (stamps[row] == stamp) return;
            stamps[row] = stamp; candidates[row].Add(insertingIndex); candidatePairs++;
        }
        public IReadOnlyList<int> Candidates(int row)
        {
            if (row < 0 || row >= candidates.Length) throw new ArgumentOutOfRangeException("row");
            if (!Admitted) throw new InvalidOperationException("Convection index rejected; use stock enumeration.");
            return candidates[row];
        }
    }
}
