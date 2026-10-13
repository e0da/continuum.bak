using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace KspContinuum
{
    public static class OwnedRuntimeCheckpointBinary
    {
        public const int Version=1,HeaderBytes=76,MaximumPayloadBytes=64*1024*1024;
        public const int MaximumArrayLength=1048576,MaximumArrayElements=8388608,MaximumStringBytes=65536,MaximumDepth=32;
        static readonly byte[] Magic={79,82,67,66};
        static readonly Encoding Utf8=new UTF8Encoding(false,true);
        static readonly Type[] Types={typeof(OwnedRuntimeHostCheckpoint),typeof(OwnedRuntimeCheckpointData),
            typeof(OwnedRuntimeCheckpointPart),typeof(OwnedRuntimeCheckpointBody),typeof(OwnedRuntimeCheckpointMember),
            typeof(OwnedRuntimeCheckpointResource),typeof(OwnedRuntimeCheckpointConnection),typeof(OwnedRuntimeCheckpointMotion),
            typeof(OwnedRuntimeCheckpointInput),typeof(OwnedRuntimeCheckpointActuator),typeof(OwnedRuntimeCheckpointSurface),
            typeof(OwnedRuntimeCheckpointThermalLink),typeof(OwnedRuntimeCheckpointEnvironment),typeof(OwnedRuntimeSourceBinding),
            typeof(OwnedRuntimeBodyBinding),typeof(OwnedRuntimeRendererBinding)};
        static readonly Dictionary<Type,FieldInfo[]> Fields=BuildFields();
        static readonly byte[] SchemaDigest=Digest(Utf8.GetBytes(string.Join("\n",Types.OrderBy(t=>t.FullName,StringComparer.Ordinal)
            .Select(t=>t.FullName+":"+string.Join(",",Fields[t].Select(f=>f.Name+"="+f.FieldType.FullName))))));
        sealed class Budget {public long Elements;}
        static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
        static Dictionary<Type,FieldInfo[]> BuildFields()
        {
            var result=new Dictionary<Type,FieldInfo[]>();
            foreach(Type type in Types)
            {
                var fields=type.GetFields(BindingFlags.Public|BindingFlags.Instance).OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
                foreach(var field in fields)Need(!field.IsInitOnly&&Allowed(field.FieldType),"Checkpoint binary schema contains an unsupported field.");
                result.Add(type,fields);
            }
            return result;
        }
        static bool Allowed(Type type)
        {return type==typeof(string)||type==typeof(double)||type==typeof(long)||type==typeof(int)||type==typeof(bool)
            ||Types.Contains(type)||(type.IsArray&&type.GetArrayRank()==1&&Allowed(type.GetElementType()));}
        static byte[] Digest(byte[] bytes){using(var sha=SHA256.Create())return sha.ComputeHash(bytes);}
        static bool Same(byte[] a,byte[] b)
        {if(a.Length!=b.Length)return false;int different=0;for(int i=0;i<a.Length;i++)different|=a[i]^b[i];return different==0;}
        static void Space(Stream stream,int count)
        {Need(count>=0&&stream.Length<=MaximumPayloadBytes-count,"Checkpoint binary payload exceeds its byte bound.");}
        static void Elements(Budget budget,int count)
        {Need(count>=0&&count<=MaximumArrayLength,"Checkpoint binary array length exceeds its bound.");budget.Elements+=count;Need(budget.Elements<=MaximumArrayElements,"Checkpoint binary aggregate element bound exceeded.");}
        public static byte[] Encode(OwnedRuntimeHostCheckpoint checkpoint)
        {
            byte[] payload;
            using(var stream=new MemoryStream())
            {
                using(var writer=new BinaryWriter(stream,Utf8,true))Write(writer,typeof(OwnedRuntimeHostCheckpoint),checkpoint,new Budget(),0);
                payload=stream.ToArray();
            }
            using(var stream=new MemoryStream(HeaderBytes+payload.Length))
            {
                using(var writer=new BinaryWriter(stream,Utf8,true))
                {writer.Write(Magic);writer.Write(Version);writer.Write(SchemaDigest);writer.Write(payload.Length);writer.Write(Digest(payload));writer.Write(payload);}
                return stream.ToArray();
            }
        }
        public static OwnedRuntimeHostCheckpoint Decode(byte[] bytes)
        {
            Need(bytes!=null&&bytes.Length>=HeaderBytes+1&&bytes.Length<=HeaderBytes+MaximumPayloadBytes,"Checkpoint binary size invalid.");
            try
            {
                using(var stream=new MemoryStream(bytes,false))using(var reader=new BinaryReader(stream,Utf8,true))
                {
                    Need(Same(reader.ReadBytes(4),Magic)&&reader.ReadInt32()==Version,"Checkpoint binary magic/version differs.");
                    Need(Same(reader.ReadBytes(32),SchemaDigest),"Checkpoint binary typed schema differs.");
                    int length=reader.ReadInt32();Need(length>0&&length<=MaximumPayloadBytes&&bytes.Length==HeaderBytes+length,"Checkpoint binary payload length/trailing bytes differ.");
                    byte[] expected=reader.ReadBytes(32),actual;using(var sha=SHA256.Create())actual=sha.ComputeHash(bytes,HeaderBytes,length);
                    Need(Same(expected,actual),"Checkpoint binary digest differs.");
                    var value=(OwnedRuntimeHostCheckpoint)Read(reader,typeof(OwnedRuntimeHostCheckpoint),new Budget(),0);
                    Need(stream.Position==stream.Length,"Checkpoint binary payload has trailing fields.");return value;
                }
            }
            catch(EndOfStreamException ex){throw new InvalidDataException("Checkpoint binary is truncated.",ex);}
            catch(DecoderFallbackException ex){throw new InvalidDataException("Checkpoint binary UTF8 is invalid.",ex);}
        }
        static void Write(BinaryWriter writer,Type type,object value,Budget budget,int depth)
        {
            Need(depth<=MaximumDepth,"Checkpoint binary depth exceeded.");var stream=writer.BaseStream;
            if(type==typeof(double)){Space(stream,8);writer.Write(BitConverter.DoubleToInt64Bits((double)value));return;}
            if(type==typeof(long)){Space(stream,8);writer.Write((long)value);return;}
            if(type==typeof(int)){Space(stream,4);writer.Write((int)value);return;}
            if(type==typeof(bool)){Space(stream,1);writer.Write((byte)((bool)value?1:0));return;}
            if(type==typeof(string))
            {
                Space(stream,4);if(value==null){writer.Write(-1);return;}
                string text=(string)value;int count=Utf8.GetByteCount(text);Need(count<=MaximumStringBytes,"Checkpoint binary string exceeds its bound.");Space(stream,4+count);writer.Write(count);writer.Write(Utf8.GetBytes(text));return;
            }
            if(type.IsArray)
            {
                Space(stream,4);if(value==null){writer.Write(-1);return;}Need(value.GetType()==type,"Checkpoint binary array type differs.");
                var array=(Array)value;Elements(budget,array.Length);writer.Write(array.Length);Type element=type.GetElementType();
                for(int i=0;i<array.Length;i++)Write(writer,element,array.GetValue(i),budget,depth+1);return;
            }
            FieldInfo[] fields;Need(Fields.TryGetValue(type,out fields),"Checkpoint binary DTO is not whitelisted.");Space(stream,1);
            if(value==null){writer.Write((byte)0);return;}Need(value.GetType()==type,"Checkpoint binary polymorphic DTO rejected.");writer.Write((byte)1);
            foreach(var field in fields)Write(writer,field.FieldType,field.GetValue(value),budget,depth+1);
        }
        static object Read(BinaryReader reader,Type type,Budget budget,int depth)
        {
            Need(depth<=MaximumDepth,"Checkpoint binary depth exceeded.");
            if(type==typeof(double))return BitConverter.Int64BitsToDouble(reader.ReadInt64());
            if(type==typeof(long))return reader.ReadInt64();if(type==typeof(int))return reader.ReadInt32();
            if(type==typeof(bool)){byte flag=reader.ReadByte();Need(flag<=1,"Checkpoint binary bool encoding differs.");return flag==1;}
            if(type==typeof(string))
            {
                int length=reader.ReadInt32();if(length==-1)return null;Need(length>=0&&length<=MaximumStringBytes&&length<=reader.BaseStream.Length-reader.BaseStream.Position,"Checkpoint binary string size differs.");
                byte[] bytes=reader.ReadBytes(length);Need(bytes.Length==length,"Checkpoint binary string truncated.");return Utf8.GetString(bytes);
            }
            if(type.IsArray)
            {
                int length=reader.ReadInt32();if(length==-1)return null;Elements(budget,length);Type element=type.GetElementType();
                // Every element needs at least one encoded byte, even a null DTO/string/array.
                Need(length<=reader.BaseStream.Length-reader.BaseStream.Position,"Checkpoint binary array cannot fit payload.");
                var array=Array.CreateInstance(element,length);for(int i=0;i<length;i++)array.SetValue(Read(reader,element,budget,depth+1),i);return array;
            }
            FieldInfo[] fields;Need(Fields.TryGetValue(type,out fields),"Checkpoint binary DTO is not whitelisted.");byte presence=reader.ReadByte();Need(presence<=1,"Checkpoint binary DTO marker differs.");if(presence==0)return null;
            object value=Activator.CreateInstance(type);foreach(var field in fields)field.SetValue(value,Read(reader,field.FieldType,budget,depth+1));return value;
        }
    }
}
