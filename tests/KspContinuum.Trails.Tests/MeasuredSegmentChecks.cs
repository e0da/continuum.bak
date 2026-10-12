using System;
using KspContinuum;
static class MeasuredSegmentChecks
{
    static void Need(bool ok) {if(!ok)throw new Exception("Measured segment independent check failed.");}
    static void Near(double a,double b) {Need(Math.Abs(a-b)<1e-9);}
    static TrailEmitterRecording Recording(double[][] points,bool[] emission)
    {
        double[] identity={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        var r=new TrailEmitterRecording{runId="segment-test",vesselId="craft",bodyName="body",emitterIdentity="origin",sceneSha256=new string('a',64),wholeCraftSha256=new string('b',64),processId=1,partId=1,clockFrequency=1,mode="engine-readback",bodyFixedToScene=identity,frames=new TrailEmitterFrame[points.Length]};
        for(int i=0;i<points.Length;i++)r.frames[i]=new TrailEmitterFrame{sequence=i,unityFrame=i,universalTime=i*.1,rootBodyFixed=points[i],emitterBodyFixed=points[i],rootBodyFixedOrientation=identity,emission=emission[i],engineIgnited=emission[i],currentThrottle=emission[i]?1:0,finalThrust=emission[i]?1:0};
        return r;
    }
    static void Build(MeasuredTrailSegments s,double time,double[] origin=null,bool orthographic=false) {s.Build(time,30,.5,.1,.05,origin??new double[3],new[]{1d,0,0},new[]{0d,1,0},new[]{0d,0,1},100,.1,.5,orthographic);}
    public static void Run()
    {
        var straight=new MeasuredTrailSegments(Recording(new[]{new[]{0d,0,10},new[]{10d,0,10},new[]{20d,0,10}},new[]{true,true,true}));
        Build(straight,.2);Need(straight.Count==1);Near(straight.Segments[0].a.x,0);Near(straight.Segments[0].b.x,20);Near(straight.Segments[0].b.time,.2);
        Build(straight,.15);Need(straight.Count==1);Near(straight.Segments[0].b.x,15);
        var bent=new MeasuredTrailSegments(Recording(new[]{new[]{0d,0,10},new[]{10d,5,10},new[]{20d,0,10}},new[]{true,true,true}));Build(bent,.2);Need(bent.Count==2&&bent.MaximumRemovedErrorPixels<=.5);
        var foreground=new MeasuredTrailSegments(Recording(new[]{new[]{-1d,0,10},new[]{0d,0,1},new[]{1d,0,10}},new[]{true,true,true}));Build(foreground,.2);Need(foreground.Count==2);Near(foreground.Segments[0].b.z,1);Near(foreground.Segments[1].a.z,1);
        var monotonicDepth=new MeasuredTrailSegments(Recording(new[]{new[]{-1d,0,2},new[]{0d,0,3},new[]{1d,0,10}},new[]{true,true,true}));Build(monotonicDepth,.2);Need(monotonicDepth.Count==2);Near(monotonicDepth.Segments[0].b.z,3);Near(monotonicDepth.Segments[1].a.z,3);
        var perspectiveLine=new MeasuredTrailSegments(Recording(new[]{new[]{-1d,0,2},new[]{0d,0,6},new[]{1d,0,10}},new[]{true,true,true}));Build(perspectiveLine,.2);Need(perspectiveLine.Count==1&&perspectiveLine.MaximumRemovedDepthErrorMeters<1e-9&&perspectiveLine.MaximumRemovedDepthErrorRatio<=1);
        var orthoLine=new MeasuredTrailSegments(Recording(new[]{new[]{-1d,0,2},new[]{0d,0,6},new[]{1d,0,10}},new[]{true,true,true}));Build(orthoLine,.2,null,true);Need(orthoLine.Count==1&&orthoLine.MaximumRemovedDepthErrorMeters<1e-9&&orthoLine.MaximumRemovedDepthErrorRatio<=1);
        var constantScale=new MeasuredTrailSegments(Recording(new[]{new[]{-1d,0,2},new[]{0d,.01,6},new[]{1d,0,10}},new[]{true,true,true}));Build(constantScale,.2,null,true);Need(constantScale.Count==2);Build(constantScale,.2);Need(constantScale.Count==1&&constantScale.MaximumRemovedErrorPixels<=.5);
        var gap=new MeasuredTrailSegments(Recording(new[]{new[]{0d,0,10},new[]{1d,0,10},new[]{2d,0,10},new[]{3d,0,10},new[]{4d,0,10}},new[]{true,false,true,false,false}));Build(gap,.4);Need(gap.Count==2&&gap.Segments[0].run!=gap.Segments[1].run);Near(gap.Segments[0].b.time,.1);Near(gap.Segments[1].a.time,.2);
        var near=new MeasuredTrailSegments(Recording(new[]{new[]{0d,0,-.1},new[]{1d,0,.2}},new[]{true,true}));Build(near,.1);Need(near.Count==1&&near.NearClippedSpans==1);Near(near.Segments[0].a.z,.1);Near(near.Segments[0].a.time,2d/30);
        Build(straight,.1);double prior=straight.Segments[0].a.x;straight.SetWind(.1,2,0,0);Build(straight,.1);Near(straight.Segments[0].a.x,prior);Build(straight,.2);Near(straight.Segments[0].a.x,.2);Near(straight.Segments[0].b.x,20);
        try{straight.SetWind(.2,double.NaN,0,0);throw new Exception("Invalid wind accepted");}catch(ArgumentException){}Build(straight,.2);Near(straight.Segments[0].a.x,.2);
        var rebase=new MeasuredTrailSegments(Recording(new[]{new[]{1000000000d,0,10},new[]{1000000010d,0,10}},new[]{true,true}));Build(rebase,.1,new[]{1000000000d,0,0});Need(rebase.Count==1);Near(rebase.Segments[0].b.x-rebase.Segments[0].a.x,10);
        var missing=Recording(new[]{new[]{0d,0,10},new[]{1d,0,10}},new[]{true,true});missing.frames[1].universalTime=.4;var missingOwner=new MeasuredTrailSegments(missing);Build(missingOwner,.4);Need(missingOwner.Count==0&&missingOwner.GapSpans==1);
    }
}
