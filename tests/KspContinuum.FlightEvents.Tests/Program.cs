using System;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static void Need(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static void Near(double value,double expected,double tolerance=1e-12){Need(Math.Abs(value-expected)<=tolerance,"Independent event oracle mismatch: "+value+" / "+expected);}
    static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Invalid event accepted.");}
    static OwnedRuntimeHost Host()
    {
        var definition=new OwnedRuntimeDefinition("event-oracle",0,new[]{new OwnedRuntimePartDefinition(1,"single",10,new Vec(),Rotation.Identity,100)},
            new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity))},new[]{new OwnedRuntimeResourceDefinition(11,1,"fuel",5,1,0)},new long[0]);
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        return new OwnedRuntimeHost(world,new OwnedRuntimeSystems(world,new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(),new Vec(1,0,0),10,0,100)},new OwnedRuntimeSurfaceSpec[0]));
    }
    static long Region(OwnedRuntimeHost host){using(var read=host.World.AcquireRead())return read.GetRegion(0).Id;}
    static void Main()
    {
        ImpulseOracle();ClockOrderingAndCheckpoint();StaleAndMalformed();CapacityAndConfiguration();CoastAcknowledgment();
        Console.WriteLine("PASS typed event impulse momentum/energy, exact event clocks/order, checkpoint exact-once history and stale rejection");
    }
    static void ImpulseOracle()
    {
        var host=Host();var queue=new OwnedFlightEventQueue(host,"analytic",4);
        queue.Enqueue(new OwnedFlightEvent("impulse",1,0,OwnedFlightEventKind.Impulse,Region(host),new Vec(2,0,0),new Vec(0,1,0),true));
        var before=host.World.Stamp;var applied=queue.ApplyNext(before);Need(applied.Before.Matches(before)&&applied.After.Epoch==before.Epoch+1,"Impulse atomic epoch publication.");
        using(var read=host.World.AcquireRead())
        {
            var r=read.GetRegion(0);Near(r.Motion.Velocity.X,1);Near(r.Motion.Velocity.Y,0);Near(r.Motion.AngularMomentum.Z,-2);
            var omega=OwnedRuntimeDynamics.AngularVelocity(r.Properties,r.Motion.Orientation,r.Motion.AngularMomentum);Near(omega.Z,-.5);
            Near(.5*r.Properties.Mass*r.Motion.Velocity.X*r.Motion.Velocity.X+.5*4*omega.Z*omega.Z,1.5);
            Near(r.Motion.RootPosition.X,0);Near(read.Stamp.Time,0);
        }
        var stamp=host.World.Stamp;Reject(()=>queue.ApplyNext(stamp));Need(host.World.Stamp.Matches(stamp),"Repeated event cannot publish.");
    }
    static void ClockOrderingAndCheckpoint()
    {
        var host=Host();var queue=new OwnedFlightEventQueue(host,"ordered",8);
        queue.Enqueue(new OwnedFlightEvent("later",1,.5,OwnedFlightEventKind.Controls,controls:new OwnedRuntimeControls{Throttle=.3}));
        queue.Enqueue(new OwnedFlightEvent("first",2,.25,OwnedFlightEventKind.Controls,controls:new OwnedRuntimeControls{Throttle=.2}));
        queue.Enqueue(new OwnedFlightEvent("same-time",3,.25,OwnedFlightEventKind.Impulse,Region(host),new Vec(0,2,0)));
        var unchanged=host.World.Stamp;Reject(()=>queue.ApplyNext(unchanged));Need(host.World.Stamp.Matches(unchanged),"Early event cannot advance time.");
        using(var step=host.World.BeginStep(host.World.Stamp,.25,new OwnedRuntimeInput()))step.Commit();
        Need(queue.ApplyNext(host.World.Stamp).Id=="first","Chronological order precedes creation order.");
        var data=queue.Capture();var json=JsonSerializer.Serialize(data,new JsonSerializerOptions{IncludeFields=true});
        var restored=OwnedFlightEventQueue.Restore(host,JsonSerializer.Deserialize<OwnedFlightEventQueueData>(json,new JsonSerializerOptions{IncludeFields=true}),"ordered");
        Need(restored.ApplyNext(host.World.Stamp).Id=="same-time","Same-time sequence survives checkpoint.");
        using(var step=host.World.BeginStep(host.World.Stamp,.25,new OwnedRuntimeInput()))step.Commit();
        Need(restored.ApplyNext(host.World.Stamp).Id=="later","Future event retained through own impulse epoch.");Near(restored.Controls.Throttle,.3);Need(restored.AppliedCount==3,"Complete exact-once history.");
    }
    static void StaleAndMalformed()
    {
        var host=Host();var queue=new OwnedFlightEventQueue(host,"stale",4);queue.Enqueue(new OwnedFlightEvent("control",1,0,OwnedFlightEventKind.Controls));
        var snapshot=host.World.CaptureSnapshot();host.World.SetRegionMotions(host.World.Stamp,snapshot.RegionMotion,host.World.Stamp.Time,host.World.Stamp.FrameId);
        var stamp=host.World.Stamp;Reject(()=>queue.ApplyNext(stamp));Need(host.World.Stamp.Matches(stamp)&&queue.Count==1,"External epoch invalidates without effects.");
        host=Host();queue=new OwnedFlightEventQueue(host,"corrupt",4);queue.Enqueue(new OwnedFlightEvent("control",1,0,OwnedFlightEventKind.Controls));var data=queue.Capture();
        data.pending[0].controls[0]=double.NaN;Reject(()=>OwnedFlightEventQueue.Restore(host,data,"corrupt"));Need(queue.Count==1,"Malformed cold restore cannot mutate original queue.");
        data=queue.Capture();data.sourceId="foreign";Reject(()=>OwnedFlightEventQueue.Restore(host,data,"corrupt"));
    }
    static void CapacityAndConfiguration()
    {
        var host=Host();var queue=new OwnedFlightEventQueue(host,"config",2);
        queue.Enqueue(new OwnedFlightEvent("disable",1,0,OwnedFlightEventKind.ActuatorEnabled,slot:0,enabled:false));
        queue.Enqueue(new OwnedFlightEvent("flow",2,0,OwnedFlightEventKind.ResourceFlowEnabled,slot:0,enabled:false));
        Reject(()=>queue.Enqueue(new OwnedFlightEvent("overflow",3,0,OwnedFlightEventKind.Controls)));Need(queue.Count==2,"Capacity failure preserves queue.");
        queue.ApplyNext(host.World.Stamp);queue.ApplyNext(host.World.Stamp);Need(!host.Systems.IsEnabled(0)&&!host.Systems.IsResourceFlowEnabled(0),"Own configuration events rebind future events.");
        host.Systems.SetEnabled(0,true);queue.AcknowledgeBoundary(host.World.Stamp);
        Need(OwnedFlightEventQueue.Restore(host,queue.Capture(),"config").AppliedCount==2,"Empty boundary acknowledgment preserves applied history.");
        host=Host();queue=new OwnedFlightEventQueue(host,"foreign-config",2);queue.Enqueue(new OwnedFlightEvent("quiet",1,0,OwnedFlightEventKind.Controls));host.Systems.SetEnabled(0,false);
        Reject(()=>queue.AcknowledgeBoundary(host.World.Stamp));Need(queue.Count==1,"Boundary acknowledgment cannot rebind future events.");
        Reject(()=>queue.ApplyNext(host.World.Stamp));Need(queue.AppliedCount==0,"External config invalidates event authority.");
    }
    static void CoastAcknowledgment()
    {
        var host=Host();var queue=new OwnedFlightEventQueue(host,"coast-publication",4);queue.Enqueue(new OwnedFlightEvent("arrival",1,.5,OwnedFlightEventKind.Controls));
        var before=host.World.Stamp;var snapshot=host.World.CaptureSnapshot();var after=host.World.SetRegionMotions(before,snapshot.RegionMotion,.25,before.FrameId);
        queue.AcknowledgeCoastAdvance(before,after);Need(queue.Count==1&&queue.NextTime==.5,"Admitted coast preserves future deadline.");
        before=after;after=host.World.SetRegionMotions(before,snapshot.RegionMotion,.5,before.FrameId);queue.AcknowledgeCoastAdvance(before,after);
        Need(queue.ApplyNext(after).Id=="arrival","Coast reaching exact deadline permits one application.");
        host=Host();queue=new OwnedFlightEventQueue(host,"overshoot",4);queue.Enqueue(new OwnedFlightEvent("arrival",1,.25,OwnedFlightEventKind.Controls));before=host.World.Stamp;snapshot=host.World.CaptureSnapshot();after=host.World.SetRegionMotions(before,snapshot.RegionMotion,.5,before.FrameId);
        Reject(()=>queue.AcknowledgeCoastAdvance(before,after));Need(queue.Count==1&&queue.AppliedCount==0,"Overshot coast cannot consume/rebind event.");
    }
}
