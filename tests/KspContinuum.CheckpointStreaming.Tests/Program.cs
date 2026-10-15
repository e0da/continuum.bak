using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using KspContinuum;

class Program
{
    const string BaselineSha="fb821eaf44b60cc827786361650aeaefdf33b3b0c0993ec8140c0ff32cfc52b2";
    const string HostGoldenSha="afc032650fb5aa02d5fade47d83dd27312e3982d1a21033c8c9029d3a697e965";
    const string FlightGoldenSha="a35100e7d81443d66ae3cb2dd5eeb79b9ac674f97c83254f9fad5c6ffd55f2e1";
    static Assembly baseline;
    static Type oldCodec;
    static void Need(bool value,string reason){if(!value)throw new Exception(reason);}
    static void Reject(Action action,string reason)
    {bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Need(rejected,reason);}
    static string Sha(byte[] bytes){return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();}
    static object Fill(Type type,int depth=0)
    {
        Need(depth<24,"Unexpected DTO cycle.");
        if(type==typeof(string))return "wire-λ-🚀";
        if(type==typeof(double))return BitConverter.Int64BitsToDouble(unchecked((long)0x3ff0000000000001UL));
        if(type==typeof(long))return 9007199254740993L;if(type==typeof(int))return int.MinValue;if(type==typeof(uint))return uint.MaxValue;if(type==typeof(bool))return true;
        if(type==typeof(double[]))return new[]{BitConverter.Int64BitsToDouble(long.MinValue),BitConverter.Int64BitsToDouble(1),BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000042UL)),double.NegativeInfinity,Math.PI};
        if(type.IsArray){var element=type.GetElementType();var array=Array.CreateInstance(element,2);array.SetValue(Fill(element,depth+1),0);array.SetValue(element.IsValueType?Fill(element,depth+1):null,1);return array;}
        object value=Activator.CreateInstance(type);foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.Instance))field.SetValue(value,Fill(field.FieldType,depth+1));return value;
    }
    static object CopyTo(object value,Type target)
    {
        if(value==null)return null;if(target.IsPrimitive||target==typeof(string))return value;
        if(target.IsArray){var old=(Array)value;var next=Array.CreateInstance(target.GetElementType(),old.Length);for(int i=0;i<old.Length;i++)next.SetValue(CopyTo(old.GetValue(i),target.GetElementType()),i);return next;}
        var result=Activator.CreateInstance(target);foreach(var field in target.GetFields(BindingFlags.Public|BindingFlags.Instance))field.SetValue(result,CopyTo(value.GetType().GetField(field.Name).GetValue(value),field.FieldType));return result;
    }
    static object Old(string method,params object[] args)
    {try{return oldCodec.GetMethod(method,BindingFlags.Public|BindingFlags.Static).Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException??e;}}
    static void WireChecks(bool flight)
    {
        Type type=flight?typeof(OwnedFlightCheckpointData):typeof(OwnedRuntimeHostCheckpoint);
        var fixture=Fill(type);object original=baseline==null?null:CopyTo(fixture,baseline.GetType(type.FullName));
        byte[] golden=baseline==null?(flight?OwnedRuntimeCheckpointBinary.EncodeFlight((OwnedFlightCheckpointData)fixture):OwnedRuntimeCheckpointBinary.Encode((OwnedRuntimeHostCheckpoint)fixture)):
            (byte[])Old(flight?"EncodeFlight":"Encode",original);
        Need(Sha(golden)==(flight?FlightGoldenSha:HostGoldenSha),"Frozen golden fixture bytes changed.");
        using(var stream=new MemoryStream())
        {
            if(flight)OwnedRuntimeCheckpointBinary.EncodeFlightTo(stream,(OwnedFlightCheckpointData)fixture);else OwnedRuntimeCheckpointBinary.EncodeTo(stream,(OwnedRuntimeHostCheckpoint)fixture);
            Need(stream.CanWrite&&stream.Position==stream.Length&&stream.ToArray().SequenceEqual(golden),"Streaming wire differs from frozen old producer.");
            stream.Position=0;object decoded=flight?(object)OwnedRuntimeCheckpointBinary.DecodeFlightFrom(stream):OwnedRuntimeCheckpointBinary.DecodeFrom(stream);
            byte[] roundtrip=flight?OwnedRuntimeCheckpointBinary.EncodeFlight((OwnedFlightCheckpointData)decoded):OwnedRuntimeCheckpointBinary.Encode((OwnedRuntimeHostCheckpoint)decoded);
            Need(stream.CanRead&&roundtrip.SequenceEqual(golden),"Stream ownership or exact continuation fields changed.");
            if(baseline!=null)Old(flight?"DecodeFlight":"Decode",roundtrip);
        }
        Console.WriteLine("PASS "+(baseline==null?"immutable golden digest":"frozen old-producer crossdecode")+" "+(flight?"flight":"host")+" wire SHA256 "+Sha(golden));
        if(!flight&&baseline!=null)LegacyChecks(original);
    }
    static object Private(string name){return oldCodec.GetField(name,BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);}
    static void LegacyWrite(BinaryWriter writer,Type type,object value,IDictionary fields)
    {
        if(type==typeof(double)){writer.Write(BitConverter.DoubleToInt64Bits((double)value));return;}
        if(type==typeof(long)){writer.Write((long)value);return;}if(type==typeof(int)){writer.Write((int)value);return;}if(type==typeof(uint)){writer.Write((uint)value);return;}if(type==typeof(bool)){writer.Write((byte)((bool)value?1:0));return;}
        if(type==typeof(string)){if(value==null){writer.Write(-1);return;}byte[] bytes=Encoding.UTF8.GetBytes((string)value);writer.Write(bytes.Length);writer.Write(bytes);return;}
        if(type.IsArray){if(value==null){writer.Write(-1);return;}var array=(Array)value;writer.Write(array.Length);foreach(object item in array)LegacyWrite(writer,type.GetElementType(),item,fields);return;}
        writer.Write((byte)(value==null?0:1));if(value==null)return;
        foreach(FieldInfo field in (FieldInfo[])fields[type])LegacyWrite(writer,field.FieldType,field.GetValue(value),fields);
    }
    static void LegacyChecks(object original)
    {
        for(int version=1;version<=5;version++)
        {
            var fields=(IDictionary)Private(version==1?"LegacyFields":"Version"+version+"Fields");
            byte[] payload;using(var p=new MemoryStream()){using(var w=new BinaryWriter(p,Encoding.UTF8,true))LegacyWrite(w,original.GetType(),original,fields);payload=p.ToArray();}
            byte[] wire;using(var s=new MemoryStream()){using(var w=new BinaryWriter(s,Encoding.UTF8,true)){w.Write(new byte[]{79,82,67,66});w.Write(version);w.Write((byte[])Private(version==1?"LegacySchemaDigest":"Version"+version+"SchemaDigest"));w.Write(payload.Length);w.Write(SHA256.HashData(payload));w.Write(payload);}wire=s.ToArray();}
            var old=Old("Decode",wire);var next=OwnedRuntimeCheckpointBinary.Decode(wire);
            Need(((byte[])Old("Encode",old)).SequenceEqual(OwnedRuntimeCheckpointBinary.Encode(next)),"Historical reader v"+version+" changed defaults/fields.");
        }
        Console.WriteLine("PASS frozen historical layouts v1–v5");
    }
    static void Integrity()
    {
        byte[] wire=OwnedRuntimeCheckpointBinary.Encode(new OwnedRuntimeHostCheckpoint{implementation="x"});
        foreach(int length in new[]{0,4,75,wire.Length-1})Reject(()=>OwnedRuntimeCheckpointBinary.Decode(wire.Take(length).ToArray()),"Truncation accepted.");
        Reject(()=>OwnedRuntimeCheckpointBinary.Decode(wire.Concat(new byte[]{0}).ToArray()),"Trailing byte accepted.");
        byte[] corrupt=(byte[])wire.Clone();corrupt[^1]^=1;Reject(()=>OwnedRuntimeCheckpointBinary.Decode(corrupt),"Wrong digest accepted.");
        corrupt=(byte[])wire.Clone();corrupt[76+1+8+4]=0xff;Redigest(corrupt);Reject(()=>OwnedRuntimeCheckpointBinary.Decode(corrupt),"Invalid UTF8 accepted with valid digest.");
        using(var shortReads=new ControlledStream(new MemoryStream(wire),7))Need(OwnedRuntimeCheckpointBinary.DecodeFrom(shortReads).implementation=="x"&&!shortReads.Disposed,"Short reads or borrowed ownership failed.");
        using(var moving=new ControlledStream(new MemoryStream(wire),65536){ChangeOnPayloadRewind=true})Reject(()=>OwnedRuntimeCheckpointBinary.DecodeFrom(moving),"Changed between hash and parse accepted.");
        using(var nonseek=new ControlledStream(new MemoryStream()){Seekable=false})Reject(()=>OwnedRuntimeCheckpointBinary.EncodeTo(nonseek,null),"Nonseekable output accepted.");
        using(var nonseek=new ControlledStream(new MemoryStream(wire)){Seekable=false})Reject(()=>OwnedRuntimeCheckpointBinary.DecodeFrom(nonseek),"Nonseekable input accepted.");
        using(var occupied=new MemoryStream(new byte[]{0},true))Reject(()=>OwnedRuntimeCheckpointBinary.EncodeTo(occupied,null),"Nonempty output accepted.");
        using(var oversized=new ControlledStream(new MemoryStream(wire)){ReportedLength=OwnedRuntimeCheckpointBinary.HeaderBytes+(long)OwnedRuntimeCheckpointBinary.MaximumPayloadBytes+1})Reject(()=>OwnedRuntimeCheckpointBinary.DecodeFrom(oversized),"Oversized input accepted.");
        using(var failure=new MemoryStream())
        {Reject(()=>OwnedRuntimeCheckpointBinary.EncodeTo(failure,new OwnedRuntimeHostCheckpoint{implementation=new string('x',65537)}),"Oversized string accepted.");Need(failure.CanWrite,"Failed encode closed caller stream.");failure.Position=0;Reject(()=>OwnedRuntimeCheckpointBinary.DecodeFrom(failure),"Failed encode left valid envelope.");}
        var oversizedArray=new OwnedRuntimeHostCheckpoint{state=new OwnedRuntimeCheckpointData{temperature=new double[1048577]}};
        Reject(()=>OwnedRuntimeCheckpointBinary.Encode(oversizedArray),"Oversized primitive array accepted.");
        Console.WriteLine("PASS integrity, UTF8, bounded streams, change detection, short reads and ownership");
    }
    static void Redigest(byte[] bytes){Array.Copy(SHA256.HashData(bytes.AsSpan(76)),0,bytes,44,32);}
    static byte[] ManualPrimitive(Array values)
    {
        using(var stream=new MemoryStream())
        {
            using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
            {
                writer.Write(values.Length);
                foreach(object value in values)
                {
                    if(value is double d)writer.Write(BitConverter.DoubleToInt64Bits(d));
                    else if(value is long l)writer.Write(l);else if(value is int i)writer.Write(i);
                    else if(value is uint u)writer.Write(u);else writer.Write((byte)((bool)value?1:0));
                }
            }
            return stream.ToArray();
        }
    }
    static object InvokePrimitive(Type codec,string method,params object[] args)
    {try{return codec.GetMethod(method,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException??e;}}
    static byte[] PrimitiveWrite(Type codec,Array array)
    {
        object budget=Activator.CreateInstance(codec.GetNestedType("Budget",BindingFlags.NonPublic),true);
        using(var stream=new MemoryStream())
        {using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))InvokePrimitive(codec,"Write",writer,array.GetType(),array,budget,0);return stream.ToArray();}
    }
    static Array PrimitiveRead(Type codec,Type type,Stream stream)
    {
        object budget=Activator.CreateInstance(codec.GetNestedType("Budget",BindingFlags.NonPublic),true);
        using(var reader=new BinaryReader(stream,Encoding.UTF8,true))return (Array)InvokePrimitive(codec,"Read",reader,type,budget,0,6);
    }
    static void LargePrimitives()
    {
        foreach(int count in new[]{2048,65536})
        {
            var doubles=new double[count];var longs=new long[count];var ints=new int[count];var uints=new uint[count];var flags=new bool[count];
            long[] bits={0,long.MinValue,1,unchecked((long)0x7ff8000000000042UL),unchecked((long)0xfff0000000000000UL),0x3ff0000000000001L};
            for(int i=0;i<count;i++){doubles[i]=BitConverter.Int64BitsToDouble(bits[i%bits.Length]);longs[i]=long.MinValue+i;ints[i]=int.MinValue+i;uints[i]=uint.MaxValue-(uint)i;flags[i]=(i%3)==0;}
            foreach(Array values in new Array[]{doubles,longs,ints,uints,flags})
            {
                byte[] manual=ManualPrimitive(values);Need(PrimitiveWrite(typeof(OwnedRuntimeCheckpointBinary),values).SequenceEqual(manual),"Bulk primitive differs from independent little-endian oracle.");
                if(oldCodec!=null)Need(PrimitiveWrite(oldCodec,values).SequenceEqual(manual),"Bulk primitive differs from retained scalar producer.");
                using(var source=new ControlledStream(new MemoryStream(manual),7))
                {
                    Array copy=PrimitiveRead(typeof(OwnedRuntimeCheckpointBinary),values.GetType(),source);
                    Need(ManualPrimitive(copy).SequenceEqual(manual)&&!source.Disposed,"Bulk short-read decoder changed IEEE/integer/bool bits or stream ownership.");
                }
                if(values is bool[])
                {byte[] bad=(byte[])manual.Clone();bad[^1]=2;using(var source=new MemoryStream(bad))Reject(()=>PrimitiveRead(typeof(OwnedRuntimeCheckpointBinary),values.GetType(),source),"Bulk decoder accepted noncanonical bool.");}
            }
        }
        Console.WriteLine("PASS 2048/65536-column independent raw-byte oracles, IEEE bits, signed/unsigned integers, canonical bools and short reads");
    }
    sealed class ControlledStream : Stream
    {
        readonly MemoryStream inner;readonly int readLimit;
        public bool Seekable=true,Disposed,ChangeOnPayloadRewind;public long ReportedLength=-1;
        internal ControlledStream(MemoryStream inner,int readLimit=int.MaxValue){this.inner=inner;this.readLimit=readLimit;}
        public override bool CanRead=>!Disposed;public override bool CanWrite=>!Disposed;public override bool CanSeek=>Seekable;
        public override long Length=>ReportedLength<0?inner.Length:ReportedLength;
        public override long Position{get=>inner.Position;set{if(ChangeOnPayloadRewind&&value==76&&inner.Position==inner.Length){long old=inner.Position;inner.Position=76+1;inner.WriteByte(1);inner.Position=old;ChangeOnPayloadRewind=false;}inner.Position=value;}}
        public override int Read(byte[] b,int o,int n)=>inner.Read(b,o,Math.Min(n,readLimit));
        public override void Write(byte[] b,int o,int n)=>inner.Write(b,o,n);public override void Flush()=>inner.Flush();
        public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);public override void SetLength(long n)=>inner.SetLength(n);
        protected override void Dispose(bool disposing){Disposed=true;if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
    static void Main(string[] args)
    {
        Need(args.Length<=1,"Optionally supply the retained baseline Core DLL path.");
        AssemblyLoadContext context=null;
        if(args.Length==1)
        {
            Need(Sha(File.ReadAllBytes(args[0]))==BaselineSha,"Frozen baseline source image differs.");
            context=new AssemblyLoadContext("frozen-codec",true);baseline=context.LoadFromAssemblyPath(Path.GetFullPath(args[0]));oldCodec=baseline.GetType("KspContinuum.OwnedRuntimeCheckpointBinary");
        }
        else Console.WriteLine("MODE portable golden checks; private old-producer/crossdecode and historical v1–v5 checks NOT RUN");
        BinaryCheckpointTests.Run();WireChecks(false);WireChecks(true);Integrity();LargePrimitives();if(context!=null)context.Unload();
        Console.WriteLine("PASS checkpoint streaming compatibility suite");
    }
}
