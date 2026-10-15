using System;
using System.Collections.Generic;

namespace KspContinuum
{
    internal sealed class ConvectionSparseIndex
    {
        const double FloatEpsilon = 1.1920928955078125e-7;
        struct Box
        {
            internal double x0, y0, x1, y1;
            internal static Box Union(Box a, Box b) { return new Box { x0 = Math.Min(a.x0, b.x0), y0 = Math.Min(a.y0, b.y0), x1 = Math.Max(a.x1, b.x1), y1 = Math.Max(a.y1, b.y1) }; }
            internal bool Intersects(Box b) { return x1 >= b.x0 && x0 <= b.x1 && y1 >= b.y0 && y0 <= b.y1; }
            internal double Center(int axis) { return axis == 0 ? (x0 + x1) * .5 : (y0 + y1) * .5; }
        }
        struct Node { internal Box bounds; internal int left, right, row, lastRow; }
        readonly Node[] nodes;
        readonly List<int>[] candidates;
        readonly int root;
        readonly double coordinateBound;
        int next, insertingIndex, insertingRow;
        internal long nodeVisits, unitAreaCones;
        // Inputs are admitted finite projected rows; insertion occurs only after original Setup passes its domain gate.
        internal ConvectionSparseIndex(OcclusionData[] rows)
        {
            int count = rows.Length; candidates = new List<int>[count]; nodes = new Node[count * 2 - 1];
            var boxes = new Box[count]; var order = new int[count]; Box all = default(Box);
            for (int row = 0; row < count; row++)
            {
                OcclusionData input = rows[row]; candidates[row] = new List<int>(); order[row] = row;
                boxes[row] = new Box { x0 = input.minExtents.x, y0 = input.minExtents.y, x1 = input.maxExtents.x, y1 = input.maxExtents.y };
                all = row == 0 ? boxes[row] : Box.Union(all, boxes[row]);
                coordinateBound = Math.Max(coordinateBound, Math.Max(Math.Abs((double)input.minExtents.x), Math.Abs((double)input.minExtents.y)));
                coordinateBound = Math.Max(coordinateBound, Math.Max(Math.Abs((double)input.maxExtents.x), Math.Abs((double)input.maxExtents.y)));
            }
            int axis = all.x1 - all.x0 >= all.y1 - all.y0 ? 0 : 1;
            Array.Sort(order, (a, b) => { int comparison = boxes[a].Center(axis).CompareTo(boxes[b].Center(axis)); return comparison == 0 ? a.CompareTo(b) : comparison; });
            root = Build(boxes, order, 0, count);
        }
        int Build(Box[] boxes, int[] order, int start, int count)
        {
            int at = next++;
            if (count == 1) { int row = order[start]; nodes[at] = new Node { bounds = boxes[row], row = row, lastRow = row, left = -1, right = -1 }; }
            else
            {
                int left = Build(boxes, order, start, count / 2), right = Build(boxes, order, start + count / 2, count - count / 2);
                nodes[at] = new Node { bounds = Box.Union(nodes[left].bounds, nodes[right].bounds), row = -1, lastRow = Math.Max(nodes[left].lastRow, nodes[right].lastRow), left = left, right = right };
            }
            return at;
        }
        internal void Insert(OcclusionCone cone, int sourceRow, int acceptedIndex)
        {
            if (cone.occludeConvectionAreaMult == 1) { unitAreaCones++; return; }
            double padding = 64 * FloatEpsilon * (coordinateBound + Math.Max(Math.Abs((double)cone.offset.x), Math.Abs((double)cone.offset.y)) + Math.Max(cone.extents.x, cone.extents.y) + 1);
            var box = new Box { x0 = -(double)cone.offset.x - cone.extents.x - padding, x1 = -(double)cone.offset.x + cone.extents.x + padding,
                y0 = -(double)cone.offset.y - cone.extents.y - padding, y1 = -(double)cone.offset.y + cone.extents.y + padding };
            insertingIndex = acceptedIndex; insertingRow = sourceRow; Query(root, box);
        }
        void Query(int at, Box box)
        {
            Node node = nodes[at]; nodeVisits++;
            if (node.lastRow <= insertingRow || !node.bounds.Intersects(box)) return;
            if (node.row >= 0) candidates[node.row].Add(insertingIndex);
            else { Query(node.left, box); Query(node.right, box); }
        }
        internal IReadOnlyList<int> Candidates(int row) { return candidates[row]; }
    }
}
