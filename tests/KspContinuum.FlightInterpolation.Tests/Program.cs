using System;
using KspContinuum;

static class Program
{
    static void Need(bool p,string message){if(!p)throw new Exception(message);}
    static void Near(double a,double b){Need(Math.Abs(a-b)<=1e-11*Math.Max(1,Math.Abs(b)),a+" != "+b);}
    static void Reject(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Invalid input accepted.");}
    static OwnedFlightInterpolationSource Source(double t,long state=0,long epoch=0,bool paused=false,long code=0,long frame=0)
    {return new OwnedFlightInterpolationSource{Stamp=new OwnedRuntimeStamp(epoch,state,0,0,frame,t,"body-local"),UniversalTime=1000+t,Warp=1,CodeGeneration=code,ConfigurationRevision=0,ContextSha256=new string('a',64),Paused=paused};}
    static OwnedFlightDisplayPose[] Pose(Vec center,Rotation q)
    {return new[]{new OwnedFlightDisplayPose{Id=10,CenterPositionBodyMeters=center,LocalCenterOfMass=new Vec(1,0,0),Orientation=q}};}
    static int Main()
    {
        var interpolation=new OwnedFlightInterpolation(2,.04,.25);
        var first=Pose(new Vec(),Rotation.Identity);interpolation.Accept(Source(0),first,0);
        var source=Source(.1,1);var last=Pose(new Vec(10,4,0),new Rotation(0,0,Math.Sin(Math.PI/4),Math.Cos(Math.PI/4)));
        interpolation.Accept(source,last,.1);var displayed=interpolation.Sample(.1);var p=displayed.GetRegion(0);
        Near(displayed.UniversalTime,1000.06);Near(displayed.PhysicalUniversalTime,1000.1);Near(displayed.Alpha,.6);Near(p.CenterPositionBodyMeters.X,6);Near(p.CenterPositionBodyMeters.Y,2.4);
        Vec x=p.Orientation.Rotate(new Vec(1,0,0));Near(x.X,Math.Cos(.3*Math.PI));Near(x.Y,Math.Sin(.3*Math.PI));
        Near(p.RootPositionBodyMeters.X,6-x.X);Near(p.RootPositionBodyMeters.Y,2.4-x.Y);
        // Caller arrays and the physical source are independent from retained display copies.
        last[0].CenterPositionBodyMeters=new Vec(999,999,999);first[0].CenterPositionBodyMeters=new Vec(888,888,888);
        p=interpolation.Sample(.12).GetRegion(0);Near(p.CenterPositionBodyMeters.X,8);
        displayed=interpolation.Sample(1);Near(displayed.UniversalTime,1000.1);Near(displayed.GetRegion(0).CenterPositionBodyMeters.X,10);Need(!displayed.Interpolated,"Extrapolated beyond latest accepted pose.");
        var cold=Pose(new Vec(20,0,0),Rotation.Identity);interpolation.Accept(Source(.2,2,epoch:1),cold,1.1);displayed=interpolation.Sample(1.1);
        Near(displayed.GetRegion(0).CenterPositionBodyMeters.X,20);Need(displayed.ResetReason=="physical/source discontinuity","Epoch discontinuity was smoothed.");
        interpolation.Accept(Source(.2,2,epoch:1,paused:true),cold,1.2);Need(!interpolation.Sample(1.2).Interpolated,"Paused state remained behind physics.");
        var bad=Pose(new Vec(100,0,0),default(Rotation));Reject(()=>interpolation.Accept(Source(.3,3,epoch:1),bad,1.3));Near(interpolation.Sample(1.3).GetRegion(0).CenterPositionBodyMeters.X,20);
        Reject(()=>interpolation.Accept(Source(.2,2,epoch:1,paused:true),Pose(new Vec(21,0,0),Rotation.Identity),1.4));Near(interpolation.Sample(1.4).GetRegion(0).CenterPositionBodyMeters.X,20);
        Reject(()=>interpolation.Configure(false,10));Need(interpolation.Sample(1.4).Enabled,"Rejected policy partially changed mode.");
        interpolation.Accept(Source(.2,2,epoch:1,paused:true,code:1),cold,1.5);Need(interpolation.Sample(1.5).ResetReason=="physical/source discontinuity","HMR was smoothed.");
        interpolation.Accept(Source(.2,2,epoch:1,paused:true,code:1,frame:1),cold,1.6);Need(interpolation.Sample(1.6).ResetReason=="physical/source discontinuity","Frame rebase was smoothed.");
        var q=new Rotation(.2,-.3,.1,.9);var minus=Rotation.FromUnitComponents(-q.X,-q.Y,-q.Z,-q.W);var midpoint=OwnedFlightInterpolation.InterpolateRotation(q,minus,.5);Near(Math.Abs(Rotation.Dot(q,midpoint)),1);
        Reject(()=>OwnedFlightInterpolation.InterpolateRotation(q,minus,double.NaN));
        var gap=new OwnedFlightInterpolation(1,.04,.25);gap.Accept(Source(0),Pose(new Vec(),Rotation.Identity),0);gap.Accept(Source(1,1),Pose(new Vec(100,0,0),Rotation.Identity),1);
        Near(gap.Sample(1).GetRegion(0).CenterPositionBodyMeters.X,100);Need(gap.Sample(1).ResetReason=="publication gap","Large skipped physical interval was interpolated.");
        Console.WriteLine("Independent center/quaternion interpolation, no extrapolation, source discontinuities and atomic rejection checks passed.");return 0;
    }
}
