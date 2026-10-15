using System;
using System.Reflection;
using KspContinuum;

static class Program
{
    sealed class Fixture
    {
        public OwnedRuntimeWorld World;
        public OwnedRuntimeSystems Systems;
        public OwnedRuntimeSurfaceSpec[] Surfaces;
        public OwnedRuntimeThermalLink[] Links;
        public OwnedRuntimeActuatorSpec[] Actuators;
    }
    static void Need(bool value,string message){if(!value)throw new Exception(message);}
    static void Same(object a,object b,string path)
    {
        if(a==null||b==null){Need(a==b,path+" null");return;}
        Type t=a.GetType();Need(t==b.GetType(),path+" type");
        if(t==typeof(double)){Need(BitConverter.DoubleToInt64Bits((double)a)==BitConverter.DoubleToInt64Bits((double)b),path+" IEEE bits");return;}
        if(t.IsPrimitive||t.IsEnum||t==typeof(string)){Need(a.Equals(b),path+" value");return;}
        if(a is Array x){var y=(Array)b;Need(x.Length==y.Length,path+" length");for(int i=0;i<x.Length;i++)Same(x.GetValue(i),y.GetValue(i),path+"["+i+"]");return;}
        var flags=BindingFlags.Instance|BindingFlags.Public;
        if(t==typeof(RigidAssemblyMember)||t==typeof(RigidAssemblyProperties))flags|=BindingFlags.NonPublic;
        foreach(var f in t.GetFields(flags))
        {if(t==typeof(OwnedRuntimeSnapshot)&&f.Name=="Definition")continue;Same(f.GetValue(a),f.GetValue(b),path+"."+f.Name);}
    }
    static Fixture Make(bool compiled,int parts=4,bool overflow=false)
    {
        var p=new OwnedRuntimePartDefinition[parts];
        for(int i=0;i<parts;i++)p[i]=new OwnedRuntimePartDefinition(i+1,"proof part",i%2==0?10:20,new Vec(i%3*.1,0,0),Rotation.Identity,100+i%5,overflow&&i==parts-1?1e110:280+i%7*10);
        var bodies=new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,new Vec(.1,0,0),new Vec(2,3,4),Rotation.Identity)),
            new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(1,3,new Vec(3,0,0),Rotation.Identity,new Vec(0,.2,0),new Vec(3,4,5),Rotation.Identity))};
        var resources=new[]{new OwnedRuntimeResourceDefinition(1,1,"ElectricCharge",100,0,100),new OwnedRuntimeResourceDefinition(2,1,"Fuel",10,2,10)};
        var definition=new OwnedRuntimeDefinition("compiled-independent",0,p,bodies,resources,new long[]{100});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new[]{new OwnedRuntimeConnection(77,1,2,false)}),new OwnedRuntimeMotion{Orientation=Rotation.Identity,Velocity=new Vec(2,0,0)});
        var surfaces=new[]{new OwnedRuntimeSurfaceSpec(0,new Vec(),new Vec(1,0,0),.1,0,2,.7),
            new OwnedRuntimeSurfaceSpec(parts-1,new Vec(),new Vec(0,1,0),.1,0,3,.6),
            new OwnedRuntimeSurfaceSpec(0,new Vec(),new Vec(0,0,1),.1,0,4,.2),
            new OwnedRuntimeSurfaceSpec(1,new Vec(),new Vec(1,0,0),.1,0,1,.5)};
        var links=parts==4?new[]{new OwnedRuntimeThermalLink(0,2,20),new OwnedRuntimeThermalLink(1,3,30),new OwnedRuntimeThermalLink(2,0,7),new OwnedRuntimeThermalLink(3,1,11),new OwnedRuntimeThermalLink(0,1,40)}:new[]{new OwnedRuntimeThermalLink(0,2,20),new OwnedRuntimeThermalLink(1,3,30)};
        var actuators=new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Engine,new Vec(.5,0,0),new Vec(0,1,0),20,1,100,0,.1,2),new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,0,1),1,-1,0,0,.1,1,10)};
        var systems=new OwnedRuntimeSystems(world,actuators,surfaces,links,wheelProviderSlots:new[]{-1,0});
        if(compiled)systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);
        return new Fixture{World=world,Systems=systems,Surfaces=surfaces,Links=links,Actuators=actuators};
    }
    static OwnedRuntimeEnvironment Env(OwnedRuntimeWorld w)=>new OwnedRuntimeEnvironment("independent-field",0,w.Stamp.Time,.1,290,new Vec(),new Vec(),.8,260);
    static void Advance(Fixture f,OwnedRuntimeControls c)
    {
        var input=new OwnedRuntimeInput(c.Throttle,c.Attitude.X,c.Attitude.Y,c.Attitude.Z,c.Translation.X,c.Translation.Y,c.Translation.Z);
        using(var s=f.World.BeginStep(f.World.Stamp,.01,input)){f.Systems.Step(s,Env(f.World),c);s.Commit();}
    }
    static void Compare(Fixture a,Fixture b)
    {
        Same(a.World.CaptureSnapshot(),b.World.CaptureSnapshot(),"world");
        using(var x=a.World.AcquireRead())using(var y=b.World.AcquireRead())
        {Need(x.RegionCount==y.RegionCount,"region count");for(int r=0;r<x.RegionCount;r++)Same(x.GetRegion(r).Properties,y.GetRegion(r).Properties,"properties");}
        Same(a.Systems.Counters.DeliveredForceNewtonSeconds,b.Systems.Counters.DeliveredForceNewtonSeconds,"force ledger");
        Same(a.Systems.Counters.DeliveredTorqueNewtonMeterSeconds,b.Systems.Counters.DeliveredTorqueNewtonMeterSeconds,"torque ledger");
        Same(a.Systems.Counters.PropellantKilograms,b.Systems.Counters.PropellantKilograms,"fuel ledger");
        Same(a.Systems.Counters.ElectricUnits,b.Systems.Counters.ElectricUnits,"electric ledger");
        Same(a.Systems.Counters.ActuatorHeatJoules,b.Systems.Counters.ActuatorHeatJoules,"heat ledger");
    }
    static void OrderedContinuation()
    {
        var a=Make(false);var b=Make(true);
        var control=new OwnedRuntimeControls{Throttle=.2,EnginesEnabled=true,Attitude=new Vec(0,0,.3),WheelsEnabled=true};
        for(int i=0;i<12;i++)
        {
            if(i==3){a.Systems.SetEnabled(0,false);b.Systems.SetEnabled(0,false);}
            if(i==5){a.Systems.SetResourceFlowEnabled(1,false);b.Systems.SetResourceFlowEnabled(1,false);}
            if(i==7){a.World.SetConnectionLocked(a.World.Stamp,77,true);b.World.SetConnectionLocked(b.World.Stamp,77,true);}
            if(i==9){a.World.Split(a.World.Stamp,77);b.World.Split(b.World.Stamp,77);}
            Advance(a,control);Advance(b,control);Compare(a,b);
        }
        Console.WriteLine("PASS ordered repeated-part surfaces, interleaved region links, skipped cross-region links, sparse actuation and topology continuation");
    }
    static void DisabledWheel()
    {
        var a=Make(false);var b=Make(true);
        foreach(var f in new[]{a,b})
        {using(var s=f.World.BeginStep(f.World.Stamp,.001,new OwnedRuntimeInput())){s.SetProviderState(0,4);s.Commit();}f.Systems.SetEnabled(1,false);}
        for(int i=0;i<3;i++){Advance(a,new OwnedRuntimeControls());Advance(b,new OwnedRuntimeControls());Compare(a,b);}
        using(var r=b.World.AcquireRead())Need(r.GetProviderState(0)==4,"Disabled wheel lost reservoir momentum");
        Console.WriteLine("PASS disabled nonzero wheel reservoir retained in sparse canonical fold");
    }
    static void LateResourceAndRestore()
    {
        var f=Make(true);
        using(var s=f.World.BeginStep(f.World.Stamp,.01,new OwnedRuntimeInput()))
        {
            f.Systems.Step(s,Env(f.World),new OwnedRuntimeControls());
            double before=s.GetRegion(0).Properties.Mass;s.SetResourceQuantity(1,3);s.Commit();
            using(var r=f.World.AcquireRead())Need(r.GetRegion(0).Properties.Mass==before-14,"Late public resource edit reused pre-edit mass");
        }
        using(var r=f.World.AcquireRead())
        {
            var member=r.GetBody(0);var batch=RigidAssemblyProperties.Combine(new[]{member});
            Same(batch,r.GetRegion(0).Properties,"independent batch after late write");
        }
        var restored=OwnedRuntimeWorld.RestoreSnapshot(f.World.CaptureSnapshot());
        using(var r=f.World.AcquireRead())using(var q=restored.AcquireRead())Same(r.GetRegion(0).Properties,q.GetRegion(0).Properties,"restored derived tensor");
        var beforeSnapshot=f.World.CaptureSnapshot();
        using(var s=f.World.BeginStep(f.World.Stamp,.01,new OwnedRuntimeInput())){s.SetResourceQuantity(1,1);s.RefreshMassProperties();}
        Same(beforeSnapshot,f.World.CaptureSnapshot(),"canceled resource cache state");
        using(var s=f.World.BeginStep(f.World.Stamp,.01,new OwnedRuntimeInput())){s.SetResourceQuantity(1,2);s.Commit();}
        using(var r=f.World.AcquireRead())Same(RigidAssemblyProperties.Combine(new[]{r.GetBody(0)}),r.GetRegion(0).Properties,"next bank after cancellation");
        Console.WriteLine("PASS post-Systems resource setter, original batch tensor, restore, canceled bank and next accepted refresh");
    }
    static void ParallelRollback()
    {
        var f=Make(true,32768,true);var before=f.World.CaptureSnapshot();bool rejected=false;
        try{Advance(f,new OwnedRuntimeControls());}catch(ArgumentException){rejected=true;}catch(AggregateException){rejected=true;}
        Need(rejected,"Overflowing thermal region failed to reject");Same(before,f.World.CaptureSnapshot(),"joined parallel failure source");
        var safe=new OwnedRuntimeSystems(f.World,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0]);
        using(var s=f.World.BeginStep(f.World.Stamp,.01,new OwnedRuntimeInput())){safe.Step(s,Env(f.World),new OwnedRuntimeControls());s.Commit();}
        Need(f.World.Stamp.Time>before.Stamp.Time,"Failed parallel phase retained writer lease");
        Console.WriteLine("PASS nonfinite parallel thermal output rejected after joined phases, source rollback and next owner transaction");
    }
    static void GeometryAndSupply()
    {
        var f=Make(true);var old=f.World.Definition;
        var parts=new OwnedRuntimePartDefinition[old.PartCount];
        for(int p=0;p<parts.Length;p++){var row=old.GetPart(p);parts[p]=new OwnedRuntimePartDefinition(row.Id,row.Name,row.BodyId,p==0?new Vec(1,0,0):row.LocalPosition,row.LocalRotation,row.HeatCapacity,row.InitialTemperature);}
        var bodies=new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,4,new Vec(),Rotation.Identity,new Vec(.2,0,0),new Vec(4,5,6),Rotation.Identity)),old.GetBody(1)};
        var definition=new OwnedRuntimeDefinition(old.Id,1,parts,bodies,new[]{old.GetResource(0),old.GetResource(1)},new long[]{100});
        f.World.Reconfigure(f.World.Stamp,definition);
        using(var r=f.World.AcquireRead()){Need(r.GetRegion(0).Properties.Mass==24,"Definition mass ingress left stale properties");Same(RigidAssemblyProperties.Combine(new[]{r.GetBody(0)}),r.GetRegion(0).Properties,"geometry ingress batch tensor");}
        var member=new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity);
        var d=new OwnedRuntimeDefinition("supply-mass-proof",0,new[]{new OwnedRuntimePartDefinition(1,"command",10,new Vec(),Rotation.Identity,100)},new[]{new OwnedRuntimeBodyDefinition(10,member)},new[]{new OwnedRuntimeResourceDefinition(1,1,"ElectricCharge",10,0,10)},new[]{long.MinValue+9});
        var w=new OwnedRuntimeWorld(d,OwnedRuntimeTopology.Compile(d,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var systems=new OwnedRuntimeSystems(w,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0]);systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);
        var sha=new string('a',64);var packet=new OwnedRuntimeSupplyCapturePacket{nativeManifestSha256=sha,partSpecsSha256=sha,epochUnchanged=true,identityUnchanged=true,unsupported=new string[0],solar=new OwnedRuntimeCapturedSolarSupply[0],engines=new OwnedRuntimeCapturedEngineState[0],
            commands=new[]{new OwnedRuntimeCapturedCommandLoad{id=9,partId=1,readinessProviderKey=long.MinValue+9,enabled=true,unitsPerSecond=1,hibernationMultiplier=.01}},
            ephemeris=new OwnedRuntimeSupplyEphemerisData{sourceSha256=sha,epoch=0,validSeconds=100,bodyRadius=1,sunBodyRelative=new[]{10d,0,0},referencePositionBodyMeters=new[]{2d,0,0},referenceFluxMultiplier=1}};
        systems.ConfigureScalarSupply(w.Stamp,packet,new string('b',64),new Vec(2,0,0),0);
        using(var s=w.BeginStep(w.Stamp,.01,new OwnedRuntimeInput())){systems.Step(s,Env(w),new OwnedRuntimeControls());s.Commit();}
        using(var r=w.AcquireRead()){Need(r.GetResourceQuantity(0)<10,"Supply did not execute original command withdrawal");Need(r.GetRegion(0).Properties.Mass==2,"Massless scalar supply changed body mass");Same(RigidAssemblyProperties.Combine(new[]{r.GetBody(0)}),r.GetRegion(0).Properties,"supply batch tensor");}
        Console.WriteLine("PASS definition/resource-point ingress and massless scalar supply preserve independently reduced tensors");
    }
    static void Main(){OrderedContinuation();DisabledWheel();LateResourceAndRestore();ParallelRollback();GeometryAndSupply();Console.WriteLine("PASS independent compiled systems mechanisms; actual captured continuation is a separate consumer gate");}
}
