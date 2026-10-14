using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using KspContinuum;

internal static class BinaryCheckpointTests
{
    static void Need(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static void Reject(Action action,string reason){bool failed=false;try{action();}catch(InvalidDataException){failed=true;}Need(failed,reason);}
    static double[] Bits()
    {return new[]{BitConverter.Int64BitsToDouble(long.MinValue),BitConverter.Int64BitsToDouble(1),BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000001UL)),BitConverter.Int64BitsToDouble(unchecked((long)0xfff0000000000000UL)),Math.PI,1e-300,1e300};}
    static object Fill(Type type,int depth)
    {
        Need(depth<16,"Unexpected recursive checkpoint schema.");
        if(type==typeof(string))return "precise-\u03bb-\ud83d\ude80";
        if(type==typeof(double))return BitConverter.Int64BitsToDouble(unchecked((long)0x3ff0000000000001UL));
        if(type==typeof(long))return 9007199254740993L;if(type==typeof(int))return int.MinValue;if(type==typeof(bool))return true;
        if(type==typeof(double[]))return Bits();
        if(type.IsArray){Type element=type.GetElementType();var a=Array.CreateInstance(element,2);a.SetValue(Fill(element,depth+1),0);a.SetValue(element.IsValueType?Fill(element,depth+1):null,1);return a;}
        object value=Activator.CreateInstance(type);foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.Instance))field.SetValue(value,Fill(field.FieldType,depth+1));return value;
    }
    static void Same(Type type,object a,object b)
    {
        if(a==null||b==null){Need(a==null&&b==null,"Null changed in binary checkpoint.");return;}
        if(type==typeof(double)){Need(BitConverter.DoubleToInt64Bits((double)a)==BitConverter.DoubleToInt64Bits((double)b),"IEEE payload changed.");return;}
        if(type==typeof(string)||type.IsValueType){Need(a.Equals(b),"Primitive changed in binary checkpoint.");return;}
        if(type.IsArray){var x=(Array)a;var y=(Array)b;Need(x.Length==y.Length,"Array shape changed.");for(int i=0;i<x.Length;i++)Same(type.GetElementType(),x.GetValue(i),y.GetValue(i));return;}
        foreach(var f in type.GetFields(BindingFlags.Public|BindingFlags.Instance))Same(f.FieldType,f.GetValue(a),f.GetValue(b));
    }
    static void Redigest(byte[] bytes)
    {using(var sha=SHA256.Create()){byte[] hash=sha.ComputeHash(bytes,OwnedRuntimeCheckpointBinary.HeaderBytes,bytes.Length-OwnedRuntimeCheckpointBinary.HeaderBytes);Array.Copy(hash,0,bytes,44,32);}}
    public static void Run()
    {
        var checkpoint=(OwnedRuntimeHostCheckpoint)Fill(typeof(OwnedRuntimeHostCheckpoint),0);
        checkpoint.sourceSha256=null;checkpoint.state.source.omissions=new[]{null,"", "nonempty"};checkpoint.state.thermalLinks=new OwnedRuntimeCheckpointThermalLink[0];
        byte[] wire=OwnedRuntimeCheckpointBinary.Encode(checkpoint);var copy=OwnedRuntimeCheckpointBinary.Decode(wire);Same(typeof(OwnedRuntimeHostCheckpoint),checkpoint,copy);
        var flight=(OwnedFlightCheckpointData)Fill(typeof(OwnedFlightCheckpointData),0);
        byte[] flightWire=OwnedRuntimeCheckpointBinary.EncodeFlight(flight);var flightCopy=OwnedRuntimeCheckpointBinary.DecodeFlight(flightWire);
        Same(typeof(OwnedFlightCheckpointData),flight,flightCopy);Need(flightWire.SequenceEqual(OwnedRuntimeCheckpointBinary.EncodeFlight(flightCopy)),"Flight binary encoding is not deterministic.");
        Reject(()=>OwnedRuntimeCheckpointBinary.Decode(flightWire),"Flight envelope accepted as legacy runtime.");
        Reject(()=>OwnedRuntimeCheckpointBinary.DecodeFlight(wire),"Legacy envelope accepted as flight.");
        var flightCorrupt=(byte[])flightWire.Clone();flightCorrupt[flightCorrupt.Length-1]^=1;Reject(()=>OwnedRuntimeCheckpointBinary.DecodeFlight(flightCorrupt),"Flight payload corruption accepted.");
        Need(wire.SequenceEqual(OwnedRuntimeCheckpointBinary.Encode(copy)),"Binary encoding is not deterministic.");
        Need(OwnedRuntimeCheckpointBinary.Decode(OwnedRuntimeCheckpointBinary.Encode(null))==null,"Root null changed.");
        foreach(int length in new[]{0,4,75,wire.Length-1})Reject(()=>OwnedRuntimeCheckpointBinary.Decode(wire.Take(length).ToArray()),"Truncated binary accepted.");
        var corrupt=(byte[])wire.Clone();corrupt[corrupt.Length-1]^=1;Reject(()=>OwnedRuntimeCheckpointBinary.Decode(corrupt),"Payload digest corruption accepted.");
        corrupt=(byte[])wire.Clone();corrupt[8]^=1;Reject(()=>OwnedRuntimeCheckpointBinary.Decode(corrupt),"Foreign schema accepted.");
        corrupt=(byte[])wire.Clone();corrupt[4]=5;Reject(()=>OwnedRuntimeCheckpointBinary.Decode(corrupt),"Foreign version accepted.");
        Reject(()=>OwnedRuntimeCheckpointBinary.Decode(wire.Concat(new byte[]{0}).ToArray()),"Trailing bytes accepted.");
        corrupt=(byte[])wire.Clone();corrupt[OwnedRuntimeCheckpointBinary.HeaderBytes]=2;Redigest(corrupt);Reject(()=>OwnedRuntimeCheckpointBinary.Decode(corrupt),"Malformed typed marker with valid digest accepted.");
        var oversized=new OwnedRuntimeHostCheckpoint{implementation=new string('x',OwnedRuntimeCheckpointBinary.MaximumStringBytes+1)};
        Reject(()=>OwnedRuntimeCheckpointBinary.Encode(oversized),"Oversize string accepted.");
        oversized=new OwnedRuntimeHostCheckpoint{state=new OwnedRuntimeCheckpointData{resourceQuantity=new double[OwnedRuntimeCheckpointBinary.MaximumArrayLength+1]}};
        Reject(()=>OwnedRuntimeCheckpointBinary.Encode(oversized),"Oversize array accepted.");
        Console.WriteLine("PASS binary full DTO, exact IEEE/long/null, deterministic schema and corruption/shape bounds");
    }
}
