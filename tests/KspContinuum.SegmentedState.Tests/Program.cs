using System;
using System.IO;
using KspContinuum;

static class Program
{
    static int checks;
    static readonly OwnedRuntimeProvenance Provenance=new OwnedRuntimeProvenance("generated","analytic-state",new string('a',64),"Two rigid bodies, authored alias, point resource, finite wheel and active heat.");
    sealed class Fixture
    {
        public OwnedRuntimeHost Host;
        public readonly OwnedRuntimeControls Controls=new OwnedRuntimeControls{WheelsEnabled=true,Attitude=new Vec(.25,0,0)};
        public readonly OwnedRuntimeEnvironment Environment=new OwnedRuntimeEnvironment("vacuum",0,0,0,300,new Vec(),new Vec());
        public readonly OwnedRuntimeStepPlan Plan=new OwnedRuntimeStepPlan(.015625);
        public Fixture(bool independent=false,OwnedRuntimeCapacity capacity=null)
        {
            var parts=new[]{new OwnedRuntimePartDefinition(1,"left",10,new Vec(),Rotation.Identity,1000),new OwnedRuntimePartDefinition(2,"right",20,new Vec(),Rotation.Identity,1000),new OwnedRuntimePartDefinition(3,"alias",20,new Vec(0,.25,0),Rotation.Identity,1000)};
            var bodies=new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,2,new Vec(-1,0,0),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity)),new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(1,3,new Vec(1,0,0),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity))};
            var resources=new[]{new OwnedRuntimeResourceDefinition(1,1,"ElectricCharge",100,0,50),new OwnedRuntimeResourceDefinition(2,3,"Fuel",10,1,2)};
            var d=new OwnedRuntimeDefinition("analytic-generated",0,parts,bodies,resources,new[]{123L},capacity??OwnedRuntimeCapacity.Native);
            var topology=OwnedRuntimeTopology.Compile(d,new[]{new OwnedRuntimeConnection(1,1,2)});
            var world=new OwnedRuntimeWorld(d,topology,new OwnedRuntimeMotion{RootPosition=new Vec(10,20,30),Orientation=Rotation.Identity,Velocity=new Vec(3,-2,.5)});
            var actuator=new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(1,0,0),.1,electricResource:0,electricUnitsPerSecond:.01,heatWatts:1);
            var systems=new OwnedRuntimeSystems(world,new[]{actuator},new OwnedRuntimeSurfaceSpec[0],wheelProviderSlots:new[]{0});
            if(independent)systems.SetThermalPolicy(world.Stamp,new OwnedRuntimeThermalPolicy(true,.2));
            Host=new OwnedRuntimeHost(world,systems);
        }
        public OwnedRuntimeInput Input {get{return new OwnedRuntimeInput(0,.25);}}
        public OwnedRuntimeImportResult LegacyImport(OwnedRuntimeHost host)
        {return new OwnedRuntimeImportResult{World=host.World,Systems=host.Systems,Source=new OwnedRuntimeSourceBinding{runId="analytic",vesselId="fixture",processId=1,nativeManifestSha256=new string('a',64),partSpecsSha256=new string('b',64),scope="Analytic legacy-schema compatibility fixture, not native capture.",subtractedNativeCenterVelocity=new double[3],sourceFrameVelocity=new double[3],bodies=new[]{new OwnedRuntimeBodyBinding{bodySlot=0,bodyId=10,sourceBodyId=10},new OwnedRuntimeBodyBinding{bodySlot=1,bodyId=20,sourceBodyId=20}},renderers=new OwnedRuntimeRendererBinding[0],omissions=new string[0]}};}
    }
    sealed class WriteOnly : Stream
    {
        readonly MemoryStream bytes=new MemoryStream();public byte[] Bytes {get{return bytes.ToArray();}}
        public override bool CanRead {get{return false;}}public override bool CanWrite {get{return true;}}public override bool CanSeek {get{return false;}}
        public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override void Write(byte[] buffer,int offset,int count){bytes.Write(buffer,offset,count);}public override void Flush(){}
        public override int Read(byte[] b,int o,int c){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long l){throw new NotSupportedException();}
    }
    static void Need(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    static void Reject(Action action){try{action();}catch(ArgumentException){checks++;return;}catch(InvalidDataException){checks++;return;}catch(InvalidOperationException){checks++;return;}catch(EndOfStreamException){checks++;return;}throw new Exception("Invalid segmented state accepted.");}
    static byte[] Save(Fixture f,OwnedRuntimeHost host=null,OwnedRuntimeStateLimits limits=null)
    {using(var stream=new WriteOnly()){OwnedRuntimeStateStore.Write(stream,host??f.Host,Provenance,f.Controls,f.Environment,f.Plan,limits);return stream.Bytes;}}
    static OwnedRuntimeStateRestored Load(byte[] bytes,OwnedRuntimeStateLimits limits=null)
    {using(var stream=new MemoryStream(bytes,false))return OwnedRuntimeStateStore.Read(stream,limits);}
    static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
    static void NominalAndLegacyContinuation()
    {
        var f=new Fixture();for(int i=0;i<3;i++)f.Host.Advance(f.Plan.Delta,f.Input,f.Environment,f.Controls);
        var limits=new OwnedRuntimeStateLimits(segmentBytes:512);byte[] original=Save(f,limits:limits);var restored=Load(original,limits);
        Need(Equal(original,Save(f,restored.Host,limits)),"Canonical restored stream differs.");
        Need(restored.Provenance.Native==null&&restored.Provenance.Kind=="generated","Generated state fabricated a native sidecar.");
        Need(restored.Host.World.Definition.Capacity.MaxParts==OwnedRuntimeCapacity.Native.MaxParts,"Read budget changed source admission policy.");
        var legacy=f.Host.CaptureCheckpoint(f.LegacyImport(f.Host),f.Controls,f.Environment,f.Plan);
        OwnedRuntimeRestoredState oldState;var old=f.Host.RestoreCheckpoint(OwnedRuntimeCheckpointBinary.Decode(OwnedRuntimeCheckpointBinary.Encode(legacy)),new string('a',64),new string('b',64),out oldState);
        for(int i=0;i<5;i++){f.Host.Advance(f.Plan.Delta,f.Input,f.Environment,f.Controls);restored.Host.Advance(restored.Plan.Delta,f.Input,restored.Environment,restored.Controls);old.Advance(oldState.Plan.Delta,f.Input,oldState.Environment,oldState.Controls);}
        Need(Equal(Save(f),Save(f,restored.Host)),"Full new-store continuation differs.");Need(Equal(Save(f),Save(f,old)),"Legacy-v6 continuation differs from direct-store continuation.");
        using(var read=restored.Host.World.AcquireRead()){Need(read.PartCount==3&&read.BodyCount==2,"Authored alias mass identity lost.");Need(read.GetProviderState(0)!=0&&read.GetResourceQuantity(0)<50&&read.GetTemperature(0)>300,"Actual provider/resource/thermal work was zeroed.");}
        byte[] stable=Save(f);using(var step=f.Host.World.BeginStep(f.Host.World.Stamp,.015625,f.Input)){step.SetResourceQuantity(0,0);step.SetProviderState(0,17);}
        Need(Equal(stable,Save(f)),"Canceled transaction changed stream.");
    }
    static void PendingAndCapacityPolicy()
    {
        var f=new Fixture(true,OwnedRuntimeCapacity.Research);f.Host.Advance(f.Plan.Delta,f.Input,f.Environment,f.Controls);
        using(var read=f.Host.World.AcquireRead())Need(read.ThermalPendingDuration>0,"Pending thermal fixture integrated prematurely.");
        byte[] bytes=Save(f);var restored=Load(bytes);Need(Equal(bytes,Save(f,restored.Host)),"Pending thermal stream lost ordered forcing.");
        Need(restored.Host.World.Definition.Capacity.MaxParts==1048576,"Original research admission lost at restore.");
        f.Host.Systems.FlushThermal(f.Host.World.Stamp);restored.Host.Systems.FlushThermal(restored.Host.World.Stamp);
        Need(Equal(Save(f),Save(f,restored.Host)),"Deferred thermal flush continuation differs.");
        Reject(()=>Load(bytes,new OwnedRuntimeStateLimits(maximumParts:2,maximumBodies:2)));
        Reject(()=>Load(bytes,new OwnedRuntimeStateLimits(maximumAllocationBytes:1024)));
        Reject(()=>new OwnedRuntimeStateLimits(maximumResources:8388609));
        var corrupt=(byte[])bytes.Clone();corrupt[corrupt.Length-1]^=1;Reject(()=>Load(corrupt));Reject(()=>Load(new byte[4]));
        var trailing=new byte[bytes.Length+1];Array.Copy(bytes,trailing,bytes.Length);Reject(()=>Load(trailing));
        Need(Equal(bytes,Save(new Fixture(true,OwnedRuntimeCapacity.Research)))==false,"Fixture is not an actual advanced state.");
    }
    static void Main(){NominalAndLegacyContinuation();PendingAndCapacityPolicy();Console.WriteLine("PASS segmented state: "+checks+" checks; direct nonseekable write, canonical stream, full legacy/new continuation, actual work, pending forcing, capacity policy and bounded malformed refusal.");}
}
