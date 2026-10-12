using System;
using System.IO;
using KspContinuum;
static class MeasuredEmitterChecks
{
    static void Near(double a,double b){if(Math.Abs(a-b)>1e-9)throw new Exception("Measured span differs.");}
    static double[] P(double x)=>new[]{x,0d,0d};
    public static void Run()
    {
        var a=new PersistentTrailOwner(16,P(0),P(0),30,2,.5,.1,1);var b=new PersistentTrailOwner(16,P(0),P(0),30,2,.5,.1,1);
        a.SetWind(2,0,0);b.SetWind(2,0,0);a.AdvanceMeasured(2,P(100),P(120),true);b.AdvanceMeasured(1,P(100),P(110),true);b.AdvanceMeasured(2,P(110),P(120),true);
        if(a.Count!=2||b.Count!=2)throw new Exception("Measured births absent.");Near(a.At(0).x,112);Near(a.At(1).x,120);for(int i=0;i<2;i++){Near(a.At(i).x,b.At(i).x);Near(a.At(i).width,b.At(i).width);}
        a.AdvanceMeasured(3,P(120),P(130),false);Near(a.At(0).x,114);if(a.Count!=2)throw new Exception("Measured off erased history.");long generation=a.Generation;try{a.AdvanceMeasured(4,P(130),P(double.NaN),true);throw new Exception("Nonfinite accepted.");}catch(ArgumentException){}if(a.Generation!=generation||a.Time!=3||a.Emission)throw new Exception("Measured rejection mutated state.");
        var tiny=new PersistentTrailOwner(1,P(0),P(0),cadence:1);try{tiny.AdvanceMeasured(5,P(0),P(5),true);throw new Exception("Oversize work accepted.");}catch(ArgumentException){}if(tiny.Time!=0||tiny.Generation!=0||tiny.Emission||tiny.Count!=0)throw new Exception("Measured work rejection mutated state.");
        double[] identity={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};var r=new TrailEmitterRecording{runId="test",processId=1,clockFrequency=10000000,vesselId="vessel",bodyName="body",partId=1,mode="trajectory-tracer",emitterIdentity="part-origin",sceneSha256=new string('a',64),wholeCraftSha256=new string('b',64),bodyFixedToScene=identity,frames=new[]{Frame(0,10,identity),Frame(1,11,identity)}};
        TrailEmitterFormat.Validate(r);string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".ter");try{TrailEmitterFormat.Write(path,r);var read=TrailEmitterFormat.Read(path);if(read.runId!=r.runId||read.frames[1].universalTime!=11)throw new Exception("Emitter roundtrip differs.");read.frames[1].rootBodyFixedOrientation[0]=-1;try{TrailEmitterFormat.Validate(read);throw new Exception("Reflection accepted.");}catch(InvalidDataException){}}finally{if(File.Exists(path))File.Delete(path);}
    }
    static TrailEmitterFrame Frame(long sequence,double time,double[] basis)=>new TrailEmitterFrame{sequence=sequence,unityFrame=(int)sequence,universalTime=time,rootBodyFixed=P(sequence),emitterBodyFixed=P(sequence+2),rootBodyFixedOrientation=(double[])basis.Clone(),emission=true};
}
