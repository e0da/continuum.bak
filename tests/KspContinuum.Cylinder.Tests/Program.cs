using System;
using System.Collections.Generic;
using KspContinuum;

class Program
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static CylinderOcclusionFold.Row Rect(float x, float y, float halfX, float halfY)
    {
        return new CylinderOcclusionFold.Row { minX=x-halfX, maxX=x+halfX, minY=y-halfY, maxY=y+halfY,
            centerX=x, centerY=y, extentX=halfX, extentY=halfY };
    }
    static void Main()
    {
        Require(CylinderOcclusionFold.Intersection(Rect(0,0,1,1),Rect(0,0,1,1)) == 1, "identical rectangles");
        Require(CylinderOcclusionFold.Intersection(Rect(0,0,1,1),Rect(1,0,1,1)) == 0.5, "half overlap");
        Require(CylinderOcclusionFold.Intersection(Rect(0,0,1,1),Rect(2,0,1,1)) == 0, "touching edge");
        Require(CylinderOcclusionFold.Intersection(Rect(0,0,0,1),Rect(0,0,1,1)) == 0, "zero target area");
        var rows = new[] { Rect(0,0,1,1), Rect(1,0,1,1), Rect(-1,0,1,1) };
        var output = new double[3]; var accepted = new int[3]; var pairs = new List<string>();
        int count = CylinderOcclusionFold.Evaluate(rows,3,output,accepted,(t,s,o)=>pairs.Add(t+":"+s));
        Require(count==2 && output[0]==0 && output[1]==1 && output[2]==1, "ordered additive exposure and cutoff");
        Require(string.Join(",",pairs)=="1:2,0:2,0:1" && accepted[0]==2 && accepted[1]==1, "accepted cylinder propagation");
        CompareIndex(new[]{ Rect(0,0,1,1),Rect(.0019999f,0,1,1) });
        CompareIndex(new[]{ Rect(0,0,1,1),Rect(.0020001f,0,1,1) });
        CompareIndex(new[]{ Rect(16777216,0,1,1),Rect(16777218,0,1,1),Rect(16777220,0,1,1) });
        CompareIndex(new[]{ Rect(0,0,float.Epsilon,float.Epsilon),Rect(float.Epsilon,0,float.Epsilon,float.Epsilon) });
        CompareIndex(rows);
        var independent=new CylinderOcclusionIndex(rows,3); rows[0]=Rect(1000,1000,1,1); independent.Evaluate(output,accepted);
        Require(output[0]==0,"index owns an immutable snapshot");
        independent.UpdateSnapshot(rows); independent.Evaluate(output,accepted);
        Require(output[0]==1,"refit publishes current row geometry");
        independent.Rebuild(); independent.Evaluate(output,accepted); Require(output[0]==1,"rebuild preserves current geometry");
        rows[2]=Rect(-2000,1000,1,1); rows[0]=Rect(-2000,1000,1,1);
        independent.UpdateSnapshot(rows); independent.Evaluate(output,accepted); Require(output[0]==0,"refit across old spatial clusters");
        rows[0].maxY=-5000;
        try { independent.UpdateSnapshot(rows); throw new Exception("reversed snapshot admitted"); }
        catch(ArgumentException) { independent.Evaluate(output,accepted); Require(output[0]==0,"failed snapshot admission preserves owned rows"); }
        rows[0]=Rect(1e16f,0,1,1);
        try { independent.UpdateSnapshot(rows); throw new Exception("unsupported magnitude admitted"); }
        catch(ArgumentException) { independent.Evaluate(output,accepted); Require(output[0]==0,"large snapshot rejection preserves owned rows"); }
        bool blocked=false;
        independent.Evaluate(output,accepted,(t,s,v)=> { try { independent.Rebuild(); } catch(InvalidOperationException) { blocked=true; } });
        Require(blocked,"observer cannot change index during evaluation");
        rows[1].minX = float.NaN; output[0]=123; accepted[0]=123;
        try { CylinderOcclusionFold.Evaluate(rows,3,output,accepted); throw new Exception("invalid geometry admitted"); }
        catch (ArgumentException) { Require(output[0]==123 && accepted[0]==123,"admission precedes publication"); }
        Console.WriteLine("Cylinder geometry, order/cutoff, index rounding/subnormal boundaries, snapshot/refit/rebuild and atomic admission passed.");
    }
    static void CompareIndex(CylinderOcclusionFold.Row[] rows)
    {
        var expected=new double[rows.Length]; var output=new double[rows.Length];var stockOrder=new int[rows.Length];var candidateOrder=new int[rows.Length];
        int a=CylinderOcclusionFold.Evaluate(rows,rows.Length,expected,stockOrder),b=new CylinderOcclusionIndex(rows,rows.Length).Evaluate(output,candidateOrder);
        Require(a==b,"indexed accepted count");
        for(int i=0;i<rows.Length;i++) Require(BitConverter.DoubleToInt64Bits(expected[i])==BitConverter.DoubleToInt64Bits(output[i]),"indexed exact multiplier");
        for(int i=0;i<a;i++) Require(stockOrder[i]==candidateOrder[i],"indexed accepted order");
    }
}
