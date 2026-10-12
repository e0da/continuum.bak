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
        rows[1].minX = float.NaN; output[0]=123; accepted[0]=123;
        try { CylinderOcclusionFold.Evaluate(rows,3,output,accepted); throw new Exception("invalid geometry admitted"); }
        catch (ArgumentException) { Require(output[0]==123 && accepted[0]==123,"admission precedes publication"); }
        Console.WriteLine("Cylinder geometry, ordered additive fold, reached pairs, cutoff and atomic admission passed.");
    }
}
