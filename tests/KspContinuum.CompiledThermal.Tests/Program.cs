using System;
using System.IO;
using System.Collections.Generic;
using KspContinuum;

static class Program
{
    static int checks;
    sealed class RegionalField : IOwnedRuntimeEnvironmentField
    {
        public OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeStamp stamp,int region,OwnedRuntimeMotion motion,RigidAssemblyProperties properties)
        {return new OwnedRuntimeEnvironmentSample("thermal-field",0,stamp.Time,0,450+7*region,new Vec(),new Vec(),2+.1*region,270+10*region);}
    }
    sealed class Fixture
    {
        public readonly OwnedRuntimeHost Host;
        public readonly OwnedRuntimeSurfaceSpec[] Surfaces;
        public readonly OwnedRuntimeThermalLink[] Links;
        public readonly OwnedRuntimeControls Controls=new OwnedRuntimeControls{WheelsEnabled=true,Attitude=new Vec(.125,0,0)};
        public readonly OwnedRuntimeEnvironment Environment=new OwnedRuntimeEnvironment("uniform",0,0,0,450,new Vec(),new Vec(),2,270);
        public readonly OwnedRuntimeStepPlan Plan=new OwnedRuntimeStepPlan(.015625);
        public Fixture(int parts,int regions,bool compiled,bool independent=false,bool reverse=false)
        {
            int perRegion=parts/regions;var partRows=new OwnedRuntimePartDefinition[parts];var bodyRows=new OwnedRuntimeBodyDefinition[regions];
            for(int p=0;p<parts;p++)partRows[p]=new OwnedRuntimePartDefinition(p+1,"part-"+p,p/perRegion+1,new Vec(),Rotation.Identity,100+(p*17%31),250+(p%11)*20);
            for(int r=0;r<regions;r++)bodyRows[r]=new OwnedRuntimeBodyDefinition(r+1,new RigidAssemblyMember(r,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity));
            var resourceRows=new[]{new OwnedRuntimeResourceDefinition(1,1,"ElectricCharge",10,0,10)};
            var d=new OwnedRuntimeDefinition("thermal-fixture",0,partRows,bodyRows,resourceRows,new[]{77L});
            var topology=OwnedRuntimeTopology.Compile(d,regions==1?new OwnedRuntimeConnection[0]:new[]{new OwnedRuntimeConnection(1,1,perRegion+1,false)});
            var world=new OwnedRuntimeWorld(d,topology,new OwnedRuntimeMotion{Orientation=Rotation.Identity});
            var surfaces=new List<OwnedRuntimeSurfaceSpec>();
            for(int p=0;p<parts;p++)surfaces.Add(new OwnedRuntimeSurfaceSpec(p,new Vec(),new Vec(0,1,0),0,0,.3+(p%5)*.2,.15+(p%7)*.1));
            for(int p=parts-1;p>=0;p--)surfaces.Add(new OwnedRuntimeSurfaceSpec(p,new Vec(),new Vec(0,1,0),0,0,1.1+(p%3)*.1,.8));
            if(reverse)surfaces.Reverse();Surfaces=surfaces.ToArray();
            var links=new List<OwnedRuntimeThermalLink>();
            for(int local=0;local<perRegion-1;local++)for(int r=regions-1;r>=0;r--)
            {int p=r*perRegion+local;links.Add(new OwnedRuntimeThermalLink(p,p+1,.3+local%5));links.Add(new OwnedRuntimeThermalLink(p+1,p,.7));}
            if(regions>1)links.Insert(1,new OwnedRuntimeThermalLink(0,perRegion,100));Links=links.ToArray();
            var actuator=new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(1,0,0),.1,electricResource:0,electricUnitsPerSecond:.01,heatWatts:7);
            var systems=new OwnedRuntimeSystems(world,new[]{actuator},Surfaces,Links,wheelProviderSlots:new[]{0});
            if(independent)systems.SetThermalPolicy(world.Stamp,new OwnedRuntimeThermalPolicy(true,.125,1));
            if(compiled)systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);
            Host=new OwnedRuntimeHost(world,systems);
        }
        public OwnedRuntimeInput Input {get{return new OwnedRuntimeInput(0,.125);}}
        public OwnedRuntimeImportResult Import()
        {
            var bindings=new OwnedRuntimeBodyBinding[Host.World.BodyCount];for(int b=0;b<bindings.Length;b++)bindings[b]=new OwnedRuntimeBodyBinding{bodySlot=b,bodyId=b+1,sourceBodyId=b+1};
            return new OwnedRuntimeImportResult{World=Host.World,Systems=Host.Systems,Source=new OwnedRuntimeSourceBinding{runId="thermal-test",vesselId="fixture",processId=1,nativeManifestSha256=new string('a',64),partSpecsSha256=new string('b',64),scope="Analytic thermal compatibility fixture; no native capture.",subtractedNativeCenterVelocity=new double[3],sourceFrameVelocity=new double[3],bodies=bindings,renderers=new OwnedRuntimeRendererBinding[0],omissions=new string[0]}};
        }
        public byte[] Packet()
        {
            var packet=Host.CaptureCheckpoint(Import(),Controls,Environment,Plan);packet.state.executionStrategy=0;packet.state.systemsRevision=0;
            return OwnedRuntimeCheckpointBinary.Encode(packet);
        }
        public void Advance(int count,bool field=true)
        {for(int i=0;i<count;i++)if(field)Host.Advance(Plan.Delta,Input,new RegionalField(),Controls);else Host.Advance(Plan.Delta,Input,Environment,Controls);}
    }
    static void Need(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
    static void Same(Fixture a,Fixture b,string reason)
    {Need(Equal(a.Packet(),b.Packet()),reason);Need(a.Host.Systems.Counters.ThermalLinks==b.Host.Systems.Counters.ThermalLinks,"Skipped and applied link visit counts differ.");}
    static void OrderingTopologyAndContinuation(int parts,int regions)
    {
        var reference=new Fixture(parts,regions,false);var compiled=new Fixture(parts,regions,true);
        reference.Advance(3);compiled.Advance(3);Same(reference,compiled,"Compiled heat/surface/link state differs.");
        if(regions>1)
        {
            reference.Host.World.SetConnectionLocked(reference.Host.World.Stamp,1,true);compiled.Host.World.SetConnectionLocked(compiled.Host.World.Stamp,1,true);
            reference.Advance(2);compiled.Advance(2);Same(reference,compiled,"Merged topology retained stale thermal grouping.");
            reference.Host.World.SetConnectionLocked(reference.Host.World.Stamp,1,false);compiled.Host.World.SetConnectionLocked(compiled.Host.World.Stamp,1,false);
        }
        reference.Host.Systems.SetEnabled(0,false);compiled.Host.Systems.SetEnabled(0,false);
        reference.Advance(2);compiled.Advance(2);Same(reference,compiled,"Configuration invalidation changed thermal continuation.");
        var checkpoint=compiled.Host.CaptureCheckpoint(compiled.Import(),compiled.Controls,compiled.Environment,compiled.Plan);OwnedRuntimeRestoredState state;
        var restored=compiled.Host.RestoreCheckpoint(OwnedRuntimeCheckpointBinary.Decode(OwnedRuntimeCheckpointBinary.Encode(checkpoint)),new string('a',64),new string('b',64),out state);
        for(int i=0;i<2;i++){compiled.Advance(1);restored.Advance(state.Plan.Delta,compiled.Input,new RegionalField(),state.Controls);}
        var restoredImport=new OwnedRuntimeImportResult{World=restored.World,Systems=restored.Systems,Source=state.Source};
        var result=restored.CaptureCheckpoint(restoredImport,state.Controls,state.Environment,state.Plan);result.state.executionStrategy=0;result.state.systemsRevision=0;
        Need(Equal(compiled.Packet(),OwnedRuntimeCheckpointBinary.Encode(result)),"Restored compiled thermal continuation differs.");
    }
    static void SurfaceOrderIsARealDependency()
    {
        var ordered=new Fixture(8,2,false);var reversed=new Fixture(8,2,false,reverse:true);ordered.Advance(1);reversed.Advance(1);
        using(var a=ordered.Host.World.AcquireRead())using(var b=reversed.Host.World.AcquireRead())
        {bool different=false;for(int p=0;p<8;p++)different|=BitConverter.DoubleToInt64Bits(a.GetTemperature(p))!=BitConverter.DoubleToInt64Bits(b.GetTemperature(p));Need(different,"Multiple-surface order fixture does not expose changed arithmetic.");}
    }
    static void DeferredIsUnchanged()
    {var a=new Fixture(16,2,false,true);var b=new Fixture(16,2,true,true);a.Advance(3);b.Advance(3);Same(a,b,"Deferred forcing changed under thermal strategy.");a.Host.Systems.FlushThermal(a.Host.World.Stamp);b.Host.Systems.FlushThermal(b.Host.World.Stamp);Same(a,b,"Deferred solver changed.");}
    static void AnalyticConduction()
    {
        var parts=new[]{new OwnedRuntimePartDefinition(1,"a",1,new Vec(),Rotation.Identity,2,400),new OwnedRuntimePartDefinition(2,"b",1,new Vec(),Rotation.Identity,3,200)};
        var d=new OwnedRuntimeDefinition("conduct",0,parts,new[]{new OwnedRuntimeBodyDefinition(1,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity))},new OwnedRuntimeResourceDefinition[0]);
        var w=new OwnedRuntimeWorld(d,OwnedRuntimeTopology.Compile(d,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});var s=new OwnedRuntimeSystems(w,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0],new[]{new OwnedRuntimeThermalLink(0,1,10)});s.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);
        new OwnedRuntimeHost(w,s).Advance(.015625,new OwnedRuntimeInput(),new OwnedRuntimeEnvironment("vacuum",0,0,0,300,new Vec(),new Vec()),new OwnedRuntimeControls());
        using(var r=w.AcquireRead())
        {double a=r.GetTemperature(0),b=r.GetTemperature(1),difference=200/(1+10*.015625*(.5+1d/3));Need(Math.Abs((a-b)-difference)<1e-12,"Independent pair-conduction attenuation differs.");Need(Math.Abs(2*a+3*b-1400)<1e-12,"Pair-conduction heat capacity weighted energy changed.");}
    }
    static void Main()
    {SurfaceOrderIsARealDependency();OrderingTopologyAndContinuation(32,4);OrderingTopologyAndContinuation(32768,4);DeferredIsUnchanged();AnalyticConduction();Console.WriteLine("PASS compiled thermal: "+checks+" exact/full continuation and independent conduction checks; bounded parallel eligibility on "+Environment.ProcessorCount+" processors.");}
}
