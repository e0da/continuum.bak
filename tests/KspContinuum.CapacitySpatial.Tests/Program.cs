using System;
using System.Collections.Generic;
using KspContinuum.CapacityLab;

static class Program
{
    static void Need(bool value,string message){if(!value)throw new Exception(message);}
    static List<(int A,int B)> Oracle(CapacityBroadphase.Aabb[] a,CapacityBroadphase.Aabb[] b)
    {
        var result=new List<(int,int)>();
        for(int i=0;i<a.Length;i++)for(int j=0;j<b.Length;j++)
        {
            var x=a[i];var y=b[j];
            bool separated=x.MaxX<y.MinX||y.MaxX<x.MinX||x.MaxY<y.MinY||y.MaxY<x.MinY||x.MaxZ<y.MinZ||y.MaxZ<x.MinZ;
            if(!separated)result.Add((i,j));
        }
        return result;
    }
    static void Compare(CapacityBroadphase.Aabb[] a,CapacityBroadphase.Aabb[] b,double cellSize)
    {
        var expected=Oracle(a,b);var limits=new CapacityBroadphase.Limits(cellSize:cellSize);var found=CapacityBroadphase.Find(a,b,limits,true);
        Need(found.Complete&&found.CandidateCount==expected.Count,"Exhaustive candidate count differs: "+found.Reason);
        var actual=new List<(int A,int B)>();var unique=new HashSet<(int,int)>();
        for(int i=0;i<found.CandidateCount;i++){var pair=found.GetPair(i);Need(unique.Add((pair.A,pair.B)),"Multicell pair emitted twice.");actual.Add((pair.A,pair.B));}
        actual.Sort((x,y)=>x.A!=y.A?x.A.CompareTo(y.A):x.B.CompareTo(y.B));
        for(int i=0;i<actual.Count;i++)Need(actual[i]==expected[i],"Independent pair set differs.");
        var again=CapacityBroadphase.Find(a,b,limits,false);Need(again.Complete&&again.PairStreamSha256==found.PairStreamSha256,"Retain/no-retain discovery stream differs.");
    }
    static void Main()
    {
        for(int count=1;count<=64;count*=2)
        {
            var a=CapacityBroadphase.GenerateGrid(count);var b=CapacityBroadphase.GenerateGrid(count,2);
            Compare(a,b,1.5);Compare(a,b,.75);
            var dense=CapacityBroadphase.GenerateGrid(count,0,true);Compare(dense,dense,1.5);
        }
        var boundary=new[]{new CapacityBroadphase.Aabb(-2,-1,-1,0,1,1),new CapacityBroadphase.Aabb(-0d,0,0,0,0,0),new CapacityBroadphase.Aabb(0,0,0,2,2,2),new CapacityBroadphase.Aabb(-.2,-.2,-.2,.2,.2,.2)};
        Compare(boundary,boundary,1);Compare(boundary,new[]{new CapacityBroadphase.Aabb(0,1,1,0,1,1),new CapacityBroadphase.Aabb(2,2,2,2,2,2)},.5);
        var denseBoxes=CapacityBroadphase.GenerateGrid(8,0,true);var capped=CapacityBroadphase.Find(denseBoxes,denseBoxes,new CapacityBroadphase.Limits(maximumCandidates:3),true);
        Need(!capped.Complete&&capped.Status=="budget-exhausted"&&capped.FailureStage=="query"&&capped.CandidateCount==3,"Dense output exhaustion was mislabeled passed.");
        Need(capped.GetPair(2).A==0&&capped.GetPair(2).B==2,"Failed prefix lost deterministic identities.");
        var work=CapacityBroadphase.Find(denseBoxes,denseBoxes,new CapacityBroadphase.Limits(maximumPairTests:2));Need(work.Status=="budget-exhausted"&&work.PairTests==2,"Pair work bound exceeded.");
        var cells=CapacityBroadphase.Find(boundary,boundary,new CapacityBroadphase.Limits(maximumQueriedCells:1));Need(cells.Status=="budget-exhausted"&&cells.QueriedCells==1,"Query-cell work bound exceeded.");
        var huge=CapacityBroadphase.Find(new[]{new CapacityBroadphase.Aabb(-100,-100,-100,100,100,100)},denseBoxes,new CapacityBroadphase.Limits(maximumCellsPerBox:8));Need(huge.Status=="budget-exhausted","Multicell admission bound ignored.");
        var invalid=CapacityBroadphase.Find(new[]{new CapacityBroadphase.Aabb(double.NaN,0,0,1,1,1)},denseBoxes);Need(invalid.Status=="invalid","Nonfinite bounds admitted.");
        var memory=CapacityBroadphase.Find(denseBoxes,denseBoxes,new CapacityBroadphase.Limits(maximumMemoryBytes:16));Need(memory.Status=="budget-exhausted"&&memory.Entries==0,"Memory admission occurred after index allocation.");
        Need(boundary[0].MinX==-2&&boundary[1].MaxX==0,"Query mutated source boxes.");
        foreach(string kind in new[]{"separated","sparse-contact","dense-overlap"})Need(CapacityBroadphase.Run(32,kind).Complete,"Generated case failed admission.");
        Console.WriteLine("PASS spatial exhaustive oracle, closed boundaries, negative/zero/multicell dedup, deterministic output and explicit work/memory/dense refusals.");
    }
}
