using System;
using System.Collections.Generic;
using KspContinuum;

sealed class RectangleAreaIndex
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
    public long nodeVisits, unitAreaCones;
    public RectangleAreaIndex(ConvectionFrame frame)
    {
        int count = frame.rows.Length; candidates = new List<int>[count]; nodes = new Node[Math.Max(0, count * 2 - 1)];
        if (count == 0) { root = -1; return; }
        var boxes = new Box[count]; var order = new int[count]; Box all = default;
        for (int row = 0; row < count; row++)
        {
            var input = frame.rows[row]; candidates[row] = new List<int>(); order[row] = row;
            boxes[row] = new Box { x0 = input.minExtents[0], y0 = input.minExtents[1], x1 = input.maxExtents[0], y1 = input.maxExtents[1] };
            all = row == 0 ? boxes[row] : Box.Union(all, boxes[row]);
            foreach (float value in input.minExtents) coordinateBound = Math.Max(coordinateBound, Math.Abs((double)value));
            foreach (float value in input.maxExtents) coordinateBound = Math.Max(coordinateBound, Math.Abs((double)value));
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
    public void Insert(ConvectionCone cone, int acceptedIndex)
    {
        if (cone.occludeConvectionAreaMult == 1) { unitAreaCones++; return; }
        double padding = 64 * FloatEpsilon * (coordinateBound + Math.Max(Math.Abs((double)cone.offset[0]), Math.Abs((double)cone.offset[1])) + Math.Max(cone.extents[0], cone.extents[1]) + 1);
        var box = new Box { x0 = -(double)cone.offset[0] - cone.extents[0] - padding, x1 = -(double)cone.offset[0] + cone.extents[0] + padding,
            y0 = -(double)cone.offset[1] - cone.extents[1] - padding, y1 = -(double)cone.offset[1] + cone.extents[1] + padding };
        insertingIndex = acceptedIndex; insertingRow = cone.rowIndex; if (root >= 0) Query(root, box);
    }
    void Query(int at, Box box)
    {
        Node node = nodes[at]; nodeVisits++;
        if (node.lastRow <= insertingRow || !node.bounds.Intersects(box)) return;
        if (node.row >= 0) candidates[node.row].Add(insertingIndex);
        else { Query(node.left, box); Query(node.right, box); }
    }
    public IReadOnlyList<int> Candidates(int row) { return candidates[row]; }
}
