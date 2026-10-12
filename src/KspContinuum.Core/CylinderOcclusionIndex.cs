using System;
using System.Runtime.InteropServices;

namespace KspContinuum
{
    public sealed class CylinderOcclusionIndex
    {
        struct Box { internal double minX, minY, maxX, maxY; }
        struct Node { internal Box box; internal int left, right, row; }
        [StructLayout(LayoutKind.Explicit)] struct FloatBits
        { [FieldOffset(0)] internal float value; [FieldOffset(0)] internal int bits; }
        sealed class CenterOrder : System.Collections.Generic.IComparer<int>
        {
            readonly Box[] boxes; readonly bool x;
            internal CenterOrder(Box[] boxes, bool x) { this.boxes=boxes; this.x=x; }
            public int Compare(int a,int b)
            { double ca=x?boxes[a].minX+boxes[a].maxX:boxes[a].minY+boxes[a].maxY;
                double cb=x?boxes[b].minX+boxes[b].maxX:boxes[b].minY+boxes[b].maxY; int c=ca.CompareTo(cb); return c!=0?c:a.CompareTo(b); }
        }
        readonly CylinderOcclusionFold.Row[] rows;
        readonly Box[] boxes;
        readonly Node[] nodes;
        readonly int[] order, candidates;
        readonly bool[] accepted;
        readonly CenterOrder xOrder,yOrder;
        int nodeCount;
        bool evaluating;
        public int Count { get { return rows.Length; } }
        public double ConservativeMargin { get; private set; }
        public long NodeVisits { get; private set; }
        public long ScalarCalls { get; private set; }

        public CylinderOcclusionIndex(CylinderOcclusionFold.Row[] source,int count)
        {
            if(source==null||count<1||count>4096||count>source.Length) throw new ArgumentException("Complete bounded cylinder snapshot required.");
            rows=new CylinderOcclusionFold.Row[count];
            boxes=new Box[count]; nodes=new Node[2*count-1]; order=new int[count]; candidates=new int[count]; accepted=new bool[count];
            xOrder=new CenterOrder(boxes,true); yOrder=new CenterOrder(boxes,false);
            UpdateSnapshot(source);
            for(int i=0;i<count;i++) order[i]=i;
            Build(0,count);
        }
        public void UpdateSnapshot(CylinderOcclusionFold.Row[] source)
        {
            if(evaluating) throw new InvalidOperationException("Snapshot mutation during evaluation is forbidden.");
            if(source==null||source.Length<Count) throw new ArgumentException("Complete same-count snapshot required.");
            double magnitude=0;
            for(int i=0;i<Count;i++)
            {
                var row=source[i];
                Admit(ref magnitude,row.minX); Admit(ref magnitude,row.minY); Admit(ref magnitude,row.maxX); Admit(ref magnitude,row.maxY);
                Admit(ref magnitude,row.centerX); Admit(ref magnitude,row.centerY); Admit(ref magnitude,row.extentX); Admit(ref magnitude,row.extentY);
                if(row.maxX<row.minX||row.maxY<row.minY||row.extentX<0||row.extentY<0) throw new ArgumentException("Reversed cylinder geometry requires original fallback.");
            }
            // Every stock float subtraction is bounded by 2*magnitude. Two spacings cover its rounding and the double box sums.
            var upper=new FloatBits {value=(float)(2*magnitude)}; var next=new FloatBits {bits=upper.bits+1};
            ConservativeMargin=2*((double)next.value-upper.value);
            Array.Copy(source,rows,Count);
            for(int i=0;i<Count;i++)
            { var row=rows[i]; boxes[i]=new Box { minX=(double)row.centerX-row.extentX-ConservativeMargin,maxX=(double)row.centerX+row.extentX+ConservativeMargin,
                minY=(double)row.centerY-row.extentY-ConservativeMargin,maxY=(double)row.centerY+row.extentY+ConservativeMargin }; }
            if(nodeCount!=0) Refit(0);
        }
        Box Refit(int at)
        {
            Node n=nodes[at]; n.box=n.row>=0?boxes[n.row]:Union(Refit(n.left),Refit(n.right)); nodes[at]=n; return n.box;
        }
        public void Rebuild()
        {
            if(evaluating) throw new InvalidOperationException("Index mutation during evaluation is forbidden.");
            nodeCount=0; for(int i=0;i<Count;i++) order[i]=i; Build(0,Count);
        }
        static void Admit(ref double magnitude,float value)
        { if(float.IsNaN(value)||float.IsInfinity(value)||Math.Abs((double)value)>1e15) throw new ArgumentException("Geometry requires original fallback."); magnitude=Math.Max(magnitude,Math.Abs((double)value)); }
        int Build(int begin,int count)
        {
            int at=nodeCount++; Box box=boxes[order[begin]];
            for(int i=begin+1;i<begin+count;i++) box=Union(box,boxes[order[i]]);
            if(count==1) { nodes[at]=new Node {box=box,left=-1,right=-1,row=order[begin]}; return at; }
            Array.Sort(order,begin,count,box.maxX-box.minX>=box.maxY-box.minY?xOrder:yOrder);
            int split=count/2,left=Build(begin,split),right=Build(begin+split,count-split);
            nodes[at]=new Node {box=box,left=left,right=right,row=-1}; return at;
        }
        static Box Union(Box a,Box b)
        { return new Box {minX=Math.Min(a.minX,b.minX),minY=Math.Min(a.minY,b.minY),maxX=Math.Max(a.maxX,b.maxX),maxY=Math.Max(a.maxY,b.maxY)}; }
        static bool Overlap(Box a,Box b)
        { return !(a.maxX<b.minX||a.minX>b.maxX||a.maxY<b.minY||a.minY>b.maxY); }
        void Query(int node,Box query,ref int count)
        {
            NodeVisits++; Node n=nodes[node]; if(!Overlap(n.box,query)) return;
            if(n.row>=0) { if(accepted[n.row]) candidates[count++]=n.row; return; }
            Query(n.left,query,ref count); Query(n.right,query,ref count);
        }
        public int Evaluate(double[] multipliers,int[] acceptedOrder,Action<int,int,double> observe=null)
        {
            if(multipliers==null||acceptedOrder==null||multipliers.Length<Count||acceptedOrder.Length<Count)
                throw new ArgumentException("Reusable complete output storage required.");
            if(evaluating) throw new InvalidOperationException("Recursive evaluation is forbidden.");
            evaluating=true;
            try
            {
            Array.Clear(accepted,0,Count); NodeVisits=ScalarCalls=0;
            int sources=1; accepted[Count-1]=true; acceptedOrder[0]=Count-1; multipliers[Count-1]=1;
            for(int target=Count-2;target>=0;target--)
            {
                var row=rows[target]; int candidatesCount=0;
                Query(0,new Box {minX=row.minX,minY=row.minY,maxX=row.maxX,maxY=row.maxY},ref candidatesCount);
                Array.Sort(candidates,0,candidatesCount);
                double exposure=1;
                for(int j=candidatesCount-1;j>=0;j--)
                {
                    int source=candidates[j]; double value=CylinderOcclusionFold.Intersection(row,rows[source]); ScalarCalls++;
                    if(observe!=null) observe(target,source,value);
                    exposure-=value; if(exposure<=.001) { exposure=0; break; }
                }
                multipliers[target]=exposure;
                if(exposure>0) { accepted[target]=true; acceptedOrder[sources++]=target; }
            }
            return sources;
            }
            finally { evaluating=false; }
        }
    }
}
