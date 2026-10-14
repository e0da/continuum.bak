using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KspContinuum;

class Program
{
    static readonly OwnedRuntimeControls Quiet=new OwnedRuntimeControls();
    static readonly OwnedRuntimeStepPlan Plan=new OwnedRuntimeStepPlan(.02);
    static readonly OwnedRuntimeProvenance Provenance=new OwnedRuntimeProvenance("generated","independent-state-proof",new string('a',64),"analytic SI fixture; no native bindings");
    static void Need(bool value,string reason){if(!value)throw new Exception(reason);}
    static void Reject(Action action,string reason)
    {
        bool failed=false;
        try{action();}catch(InvalidDataException){failed=true;}catch(ArgumentException){failed=true;}catch(InvalidOperationException){failed=true;}
        Need(failed,reason);
    }
    static OwnedRuntimeHost Fixture(bool pending=false)
    {
        var member=new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity);
        var definition=new OwnedRuntimeDefinition("store-proof",0,
            new[]{new OwnedRuntimePartDefinition(1,"actual authored alias A",10,new Vec(),Rotation.Identity,100),new OwnedRuntimePartDefinition(2,"actual authored alias B",10,new Vec(1,0,0),Rotation.Identity,200)},
            new[]{new OwnedRuntimeBodyDefinition(10,member)},
            new[]{new OwnedRuntimeResourceDefinition(10,1,"ElectricCharge",10,0,10),new OwnedRuntimeResourceDefinition(20,2,"Fuel",5,1,5)},new long[]{100,101});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new[]{new OwnedRuntimeConnection(1,1,2,true)}),
            new OwnedRuntimeMotion{Orientation=Rotation.Identity,RootPosition=new Vec(BitConverter.Int64BitsToDouble(long.MinValue),0,0),Velocity=new Vec(1,0,0)});
        var systems=new OwnedRuntimeSystems(world,new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,0,1),1,-1,0,0,.01,100,100)},
            new OwnedRuntimeSurfaceSpec[0],new[]{new OwnedRuntimeThermalLink(0,1,10)},wheelProviderSlots:new[]{0});
        var host=new OwnedRuntimeHost(world,systems);
        using(var step=world.BeginStep(world.Stamp,.001,new OwnedRuntimeInput(0)))
        {step.SetProviderState(1,BitConverter.Int64BitsToDouble(long.MinValue));step.Commit();}
        if(pending)
        {
            systems.SetThermalPolicy(world.Stamp,new OwnedRuntimeThermalPolicy(true,1,5));
            var controls=new OwnedRuntimeControls{Attitude=new Vec(0,0,.1),WheelsEnabled=true};
            host.Advance(.01,new OwnedRuntimeInput(0,0,0,.1),Environment(world),controls);
            using(var read=world.AcquireRead())Need(read.ThermalPendingDuration>0,"Fixture did not create chronological pending forcing.");
        }
        return host;
    }
    static OwnedRuntimeEnvironment Environment(OwnedRuntimeWorld world)
    {return new OwnedRuntimeEnvironment("independent-vacuum",0,world.Stamp.Time,0,300,new Vec(),new Vec());}
    static byte[] Save(OwnedRuntimeHost host,OwnedRuntimeProvenance provenance=null)
    {using(var stream=new MemoryStream()){OwnedRuntimeStateStore.Write(stream,host,provenance??Provenance,Quiet,Environment(host.World),Plan);Need(stream.CanWrite,"Store closed output.");return stream.ToArray();}}
    static OwnedRuntimeStateRestored Load(byte[] bytes,OwnedRuntimeStateLimits limits=null)
    {using(var stream=new MemoryStream(bytes,false)){var result=OwnedRuntimeStateStore.Read(stream,limits);Need(stream.CanRead,"Store closed input.");return result;}}
    static void SameState(OwnedRuntimeHost source,OwnedRuntimeHost restored,string reason)
    {Need(Save(source).SequenceEqual(Save(restored)),reason);}
    static void ExactContinuation()
    {
        foreach(bool pending in new[]{false,true})
        {
            var source=Fixture(pending);byte[] before=Save(source);var restored=Load(before);
            Need(restored.Provenance.Kind=="generated"&&restored.Provenance.Native==null&&restored.Provenance.SourceSha256==Provenance.SourceSha256,"Generated provenance invented native identities.");
            Need(Save(restored.Host).SequenceEqual(before),"Canonical rewritten image differs.");
            using(var a=source.World.AcquireRead())using(var b=restored.Host.World.AcquireRead())
            {
                Need(BitConverter.DoubleToInt64Bits(b.GetProviderState(1))==long.MinValue,"Logical zero collapsed a negative-zero provider.");
                Need(a.ThermalPendingDuration==b.ThermalPendingDuration&&a.ThermalIntegratedTime==b.ThermalIntegratedTime,"Thermal causal clock changed.");
            }
            if(pending){source.Systems.FlushThermal(source.World.Stamp);restored.Host.Systems.FlushThermal(restored.Host.World.Stamp);SameState(source,restored.Host,"Ordered pending flush continuation differs.");}
            for(int i=0;i<3;i++)
            {source.Advance(.02,new OwnedRuntimeInput(0),Environment(source.World),Quiet);restored.Host.Advance(.02,new OwnedRuntimeInput(0),Environment(restored.Host.World),Quiet);SameState(source,restored.Host,"Subsequent complete transaction differs.");}
        }
        Console.WriteLine("PASS direct store exact bits, numeric provenance, pending thermal flush and complete continuation");
    }
    static void DormantSignedZeroHistory()
    {
        var seed=Fixture();var snapshot=seed.World.CaptureSnapshot();double negativeZero=BitConverter.Int64BitsToDouble(long.MinValue);
        snapshot.Thermal.segmentHeat[^1]=negativeZero;snapshot.Thermal.segmentDuration[7]=negativeZero;
        var world=OwnedRuntimeWorld.RestoreSnapshot(snapshot);
        var systems=new OwnedRuntimeSystems(world,new[]{seed.Systems.GetActuator(0)},new OwnedRuntimeSurfaceSpec[0],new[]{new OwnedRuntimeThermalLink(0,1,10)},wheelProviderSlots:new[]{0});
        var host=new OwnedRuntimeHost(world,systems);byte[] wire=Save(host);var restored=Load(wire);var actual=restored.Host.World.CaptureSnapshot();
        Need(actual.Thermal.segmentHeat.Length==16&&actual.Thermal.segmentConvection.Length==16&&actual.Thermal.segmentConvectionSource.Length==16&&actual.Thermal.segmentRadiation.Length==16&&actual.Thermal.segmentRadiationSource.Length==16,"Dormant signed-zero history hydration did not materialize all columns.");
        Need(BitConverter.DoubleToInt64Bits(actual.Thermal.segmentHeat[^1])==long.MinValue&&BitConverter.DoubleToInt64Bits(actual.Thermal.segmentDuration[7])==long.MinValue,"Dormant signed-zero history/duration collapsed.");
        Need(Save(restored.Host).SequenceEqual(wire),"Dormant signed-zero rewritten image differs.");
        var legacy=OwnedRuntimeCheckpointBinary.Decode(OwnedRuntimeCheckpointBinary.Encode(new OwnedRuntimeHostCheckpoint{state=new OwnedRuntimeCheckpointData{thermal=actual.Thermal.Clone()}}));
        Need(legacy.state.thermal.segmentHeat.Length==16&&BitConverter.DoubleToInt64Bits(legacy.state.thermal.segmentHeat[^1])==long.MinValue&&BitConverter.DoubleToInt64Bits(legacy.state.thermal.segmentDuration[7])==long.MinValue,"Dense legacy Clone lost signed-zero padding/duration.");
        Console.WriteLine("PASS dormant negative-zero forcing/duration and dense legacy Clone boundary");
    }
    sealed class OtherSchedule : IOwnedRuntimeSchedule
    {
        public string Schema=>"continuum-owned-runtime-schedule/v1";public string StateSchema=>"world-bank-state-only/v1";public string ImplementationId=>"unrepresented-test-owner";
        public void Step(OwnedRuntimeSystems s,OwnedRuntimeStep step,OwnedRuntimeEnvironment e,OwnedRuntimeControls c){s.Step(step,e,c);}
    }
    sealed class CallbackStream : MemoryStream
    {
        internal Action Callback;internal bool Called,Disposed;
        void Observe(){if(Called)return;Called=true;Callback();}
        public override void Write(byte[] buffer,int offset,int count){Observe();base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Observe();base.Write(buffer);}
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
    static void Ownership()
    {
        foreach(int operation in new[]{0,1,2,3})
        {
            var host=Fixture();byte[] before=Save(host);bool refused=false;
            using(var stream=new CallbackStream{Callback=()=>
            {
                Action mutation=operation==0?(Action)(()=>host.Advance(.02,new OwnedRuntimeInput(0),Environment(host.World),Quiet)):
                    operation==1?()=>host.Systems.SetEnabled(0,false):operation==2?()=>host.World.Split(host.World.Stamp,1):()=>host.Systems.SetResourceFlowEnabled(0,false);
                try{mutation();}catch(InvalidOperationException){refused=true;}
            }})
            {
                OwnedRuntimeStateStore.Write(stream,host,Provenance,Quiet,Environment(host.World),Plan);
                Need(stream.Called&&refused&&!stream.Disposed,"Writer allowed reentrant physical/config ownership or closed caller stream.");
                Need(stream.ToArray().SequenceEqual(before)&&Save(host).SequenceEqual(before),"Reentry changed source or captured mixed epochs.");
            }
        }
        var custom=Fixture();custom.SwapSchedule(new OtherSchedule(),new string('b',64),custom.World.Stamp);
        Reject(()=>Save(custom),"Unrepresented custom schedule was silently stored.");
        var original=Fixture();byte[] state=Save(original);
        using(var stream=new CallbackStream{Callback=()=>throw new IOException("injected cold output failure")})
        {
            bool failed=false;try{OwnedRuntimeStateStore.Write(stream,original,Provenance,Quiet,Environment(original.World),Plan);}catch(IOException){failed=true;}
            Need(failed&&!stream.Disposed&&Save(original).SequenceEqual(state),"I/O failure changed source or leaked caller ownership.");
        }
        original.Advance(.02,new OwnedRuntimeInput(0),Environment(original.World),Quiet);
        Reject(()=>Save(Fixture(),new OwnedRuntimeProvenance("generated","bad",new string('a',64),"no invented native ledger",new OwnedRuntimeSourceBinding())),"Generated state accepted a native sidecar.");
        Reject(()=>Save(Fixture(),new OwnedRuntimeProvenance("ksp","bad",new string('a',64),"missing source binding")),"KSP provenance accepted absent native binding.");
        Console.WriteLine("PASS gated direct-bank ownership, reentry/config rejection, I/O recovery and provenance separation");
    }
    struct Chunk{internal int Offset,Id,Payload,PayloadBytes,Count,Width;internal long Start;internal byte Encoding;}
    static List<Chunk> Columns(byte[] bytes,out int footer)
    {
        Need(bytes.Length>=144&&System.Text.Encoding.ASCII.GetString(bytes,0,8)=="ORSCOL01","Independent wire magic differs.");
        int metadata=BitConverter.ToInt32(bytes,88),cursor=checked(92+metadata);var result=new List<Chunk>();
        while(true)
        {
            Need(cursor>=92&&cursor<=bytes.Length-4,"Independent chunk walk escaped envelope.");int id=BitConverter.ToInt32(bytes,cursor);
            if(id==-1){footer=cursor;Need(bytes.Length-cursor==52,"Independent footer size differs.");return result;}
            Need(cursor<=bytes.Length-57,"Independent chunk header truncated.");
            int payload=BitConverter.ToInt32(bytes,cursor+21);
            var chunk=new Chunk{Offset=cursor,Id=id,Start=BitConverter.ToInt64(bytes,cursor+4),Count=BitConverter.ToInt32(bytes,cursor+12),Width=BitConverter.ToInt32(bytes,cursor+16),Encoding=bytes[cursor+20],Payload=cursor+57,PayloadBytes=payload};
            Need(payload>=0&&chunk.Payload<=bytes.Length-payload,"Independent chunk payload escapes envelope.");result.Add(chunk);cursor=checked(chunk.Payload+payload);
        }
    }
    static void Seal(byte[] bytes)
    {
        int footer;var columns=Columns(bytes,out footer);
        foreach(var c in columns)Array.Copy(SHA256.HashData(bytes.AsSpan(c.Payload,c.PayloadBytes)),0,bytes,c.Offset+25,32);
        Array.Copy(SHA256.HashData(bytes.AsSpan(0,bytes.Length-32)),0,bytes,bytes.Length-32,32);
    }
    static void Rejections()
    {
        var host=Fixture(true);byte[] pristine=Save(host);int footer;var columns=Columns(pristine,out footer);
        foreach(Action<byte[]> mutate in new Action<byte[]>[]{b=>b[^1]^=1,b=>BitConverter.GetBytes(int.MaxValue).CopyTo(b,44),
            b=>BitConverter.GetBytes(columns[0].Id).CopyTo(b,columns[1].Offset),b=>BitConverter.GetBytes(long.MaxValue).CopyTo(b,columns[0].Offset+4),
            b=>BitConverter.GetBytes(-1).CopyTo(b,columns[0].Offset+12),b=>BitConverter.GetBytes(int.MaxValue).CopyTo(b,columns[0].Offset+16)})
        {
            byte[] changed=(byte[])pristine.Clone();mutate(changed);
            if(changed[^1]==pristine[^1])Seal(changed);
            Reject(()=>Load(changed),"Corrupt/malformed authenticated state returned an owner.");Need(Save(host).SequenceEqual(pristine),"Rejected reader mutated live source.");
        }
        var ids=columns.Single(c=>c.Id==1);Need(ids.Encoding==0&&ids.Count==2,"Duplicate-ID semantic fixture did not reach raw authored IDs.");
        byte[] duplicate=(byte[])pristine.Clone();Array.Copy(duplicate,ids.Payload,duplicate,ids.Payload+8,8);Seal(duplicate);
        Reject(()=>Load(duplicate),"Authenticated duplicate authored identity returned an owner.");
        var quantities=columns.Single(c=>c.Id==22);Need(quantities.Encoding==0&&quantities.PayloadBytes==16,"Negative-quantity semantic fixture did not reach raw scalar rows.");
        byte[] negative=(byte[])pristine.Clone();BitConverter.GetBytes(-1d).CopyTo(negative,quantities.Payload);Seal(negative);
        Reject(()=>Load(negative),"Authenticated negative quantity returned an owner.");
        Need(Save(host).SequenceEqual(pristine),"Rejected semantic read changed the live source.");
        Reject(()=>Load(pristine.Take(pristine.Length-1).ToArray()),"Truncated envelope returned an owner.");
        Reject(()=>Load(pristine.Concat(new byte[]{0}).ToArray()),"Trailing data returned an owner.");
        Reject(()=>Load(pristine,new OwnedRuntimeStateLimits(maximumParts:1,maximumBodies:1)),"Reader dimensional budget was ignored.");
        Reject(()=>Load(pristine,new OwnedRuntimeStateLimits(maximumAllocationBytes:1024)),"Reader allocation budget was ignored.");
        Console.WriteLine("PASS independent wire walk, authenticated shape/identity corruption, truncation and budget rejection");
    }
    static void Main(){ExactContinuation();DormantSignedZeroHistory();Ownership();Rejections();Console.WriteLine("PASS independent segmented-state seam proofs");}
}
