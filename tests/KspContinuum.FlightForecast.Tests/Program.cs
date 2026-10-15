using System;
using KspContinuum;
static class Program
{
    static void Need(bool value,string text){if(!value)throw new Exception(text);}
    static void Near(double a,double b){Need(Math.Abs(a-b)<=1e-10*Math.Max(1,Math.Abs(b)),"Crossing differs: "+a+" versus "+b);}
    static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Invalid forecast admitted.");}
    static OwnedFlightCrossing Crossing(Vec p,Vec v,double radius,double horizon=100)
    {OwnedFlightCrossing c;Need(OwnedFlightForecast.TryFirstPositiveCrossing(p,v,radius,horizon,out c),"Expected positive crossing.");Need(c.DeltaSeconds>0,"Zero event emitted.");return c;}
    static void Main()
    {
        Near(Crossing(new Vec(10,0,0),new Vec(-2,0,0),2).DeltaSeconds,4);
        var outbound=Crossing(new Vec(0,0,0),new Vec(2,0,0),2);Near(outbound.DeltaSeconds,1);Need(outbound.Direction==OwnedFlightCrossingDirection.Outbound,"Interior exit direction.");
        var tangent=Crossing(new Vec(5,1,0),new Vec(-1,0,0),1);Near(tangent.DeltaSeconds,5);Need(tangent.Direction==OwnedFlightCrossingDirection.Tangent,"Tangency classification.");
        var current=Crossing(new Vec(2,0,0),new Vec(-1,0,0),2);Near(current.DeltaSeconds,4);Need(current.InitiallyOnBoundary,"Current boundary lost.");
        OwnedFlightCrossing absent;Need(!OwnedFlightForecast.TryFirstPositiveCrossing(new Vec(2,0,0),new Vec(1,0,0),2,10,out absent),"Current outward root must not zero-loop.");
        Need(!OwnedFlightForecast.TryFirstPositiveCrossing(new Vec(5,2,0),new Vec(-1,0,0),1,10,out absent),"Miss must remain a miss.");
        Need(!OwnedFlightForecast.TryFirstPositiveCrossing(new Vec(5,0,0),new Vec(),1,10,out absent),"Stationary no crossing.");
        Near(Crossing(new Vec(1e12+1,0,0),new Vec(-1,0,0),1e12,2).DeltaSeconds,1);
        Reject(()=>Crossing(new Vec(double.NaN,0,0),new Vec(1,0,0),1));
        string a=new string('a',64),b=new string('b',64),csha=new string('c',64);
        var capture=new OwnedFlightCapture{runId="forecast",processId=1,vesselId="craft",bodyId="body",nativeManifestSha256=a,partSpecsSha256=b,sourceEpochSeconds=100,gravitationalParameter=1e6,radiusMeters=1000,atmosphereDepthMeters=100,vesselBoundingRadiusMeters=2,angularVelocity=new double[3],sunDirection=new[]{1d,0,0},bodyRotation=new[]{0d,0,0,1},rootRotation=new[]{0d,0,0,1},rootPositionBodyRelative=new[]{1200d,0,0},centerOfMassVelocityInertial=new[]{-10d,0,0},altitudeMeters=new[]{0d,50,100},densityKilogramsPerCubicMeter=new[]{1d,.5,0},temperatureKelvin=new[]{300d,200,3},omissions=new string[0]};
        var context=OwnedFlightWorld.Admit(capture,a,b,csha);
        var parts=new[]{new OwnedRuntimePartDefinition(1,"one",10,new Vec(),Rotation.Identity,100),new OwnedRuntimePartDefinition(2,"two",20,new Vec(),Rotation.Identity,100)};
        var bodies=new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity)),new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(1,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity))};
        var definition=new OwnedRuntimeDefinition("forecast",0,parts,bodies,new OwnedRuntimeResourceDefinition[0],new long[0]);
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity},0,context.Frame+"/local");
        using(var read=world.AcquireRead())Need(read.RegionCount==2,"Fixture must cover multiple regions.");
        var motions=new[]{new OwnedRuntimeMotion{RootPosition=new Vec(1200,0,0),Orientation=Rotation.Identity,Velocity=new Vec(-10,0,0)},new OwnedRuntimeMotion{RootPosition=new Vec(1300,0,0),Orientation=Rotation.Identity,Velocity=new Vec(-20,0,0)}};
        world.SetRegionMotions(world.Stamp,motions,0,context.Frame+"/local");var session=new OwnedFlightSession(world,context,new Vec(),0);var source=world.Stamp;
        var forecast=OwnedFlightForecast.Capture(session,source,30);forecast.RequireCurrent(session);Near(forecast.FirstPositiveDelta,10);Need(forecast.Count==6&&!forecast.SurfaceGuardAlreadyReached,"Multi-region knot/atmosphere/surface coverage.");
        Need(source.Matches(world.Stamp),"Forecast mutated source.");
        using(var read=world.AcquireRead()){var descriptor=session.CaptureReadDescriptor(read);var detached=OwnedFlightForecast.Capture(descriptor,read,30);detached.RequireMatches(descriptor,read);Near(detached.FirstPositiveDelta,10);OwnedFlightForecastRow first;Need(OwnedFlightForecast.TryFirst(session,read,30,out first),"Fast query absent.");Near(first.DeltaSeconds,forecast.FirstPositiveDelta);Need(first.RegionId==forecast.GetRow(0).RegionId&&first.Kind==forecast.GetRow(0).Kind,"Fast ordering differs.");}
        var foreign=OwnedRuntimeWorld.RestoreSnapshot(world.CaptureSnapshot());var foreignSession=new OwnedFlightSession(foreign,context,new Vec(),0);
        using(var read=foreign.AcquireRead())Reject(()=>forecast.RequireMatches(foreignSession.CaptureReadDescriptor(read),read));
        session.Rebase(source,new Vec(1e6,0,0));Reject(()=>forecast.RequireCurrent(session));Reject(()=>OwnedFlightForecast.Capture(session,source,1));
        var rebased=OwnedFlightForecast.Capture(session,world.Stamp,30);Near(rebased.FirstPositiveDelta,10);
        WindowChecks.Run();
        Console.WriteLine("PASS advisory forecast analytic/current/tangent/large-coordinate/multi-region/source/rebase/immutability and coast/thermal windows checks");
    }
}
