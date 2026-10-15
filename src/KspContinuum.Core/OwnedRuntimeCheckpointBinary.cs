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
        public const int Version=6,HeaderBytes=76,MaximumPayloadBytes=64*1024*1024;
        public const int MaximumArrayLength=1048576,MaximumArrayElements=8388608,MaximumStringBytes=65536,MaximumDepth=32;
        static readonly byte[] Magic={79,82,67,66};
        static readonly Encoding Utf8=new UTF8Encoding(false,true);
        static readonly Type[] Types={typeof(OwnedRuntimeHostCheckpoint),typeof(OwnedRuntimeCheckpointData),
            typeof(OwnedRuntimeCheckpointPart),typeof(OwnedRuntimeCheckpointBody),typeof(OwnedRuntimeCheckpointMember),
            typeof(OwnedRuntimeCheckpointResource),typeof(OwnedRuntimeCheckpointConnection),typeof(OwnedRuntimeCheckpointMotion),
            typeof(OwnedRuntimeCheckpointInput),typeof(OwnedRuntimeCheckpointActuator),typeof(OwnedRuntimeCheckpointSurface),
            typeof(OwnedRuntimeCheckpointThermalLink),typeof(OwnedRuntimeCheckpointEnvironment),typeof(OwnedRuntimeSourceBinding),
            typeof(OwnedRuntimeBodyBinding),typeof(OwnedRuntimeRendererBinding),typeof(OwnedRuntimeThermalState),typeof(OwnedRuntimeMechanicalState),
            typeof(OwnedRuntimeSupplyConfiguration),typeof(OwnedRuntimeSupplyCapturePacket),typeof(OwnedRuntimeSupplyEphemerisData),typeof(OwnedRuntimeSupplyCurveData),typeof(OwnedRuntimeCapturedSolarSupply),typeof(OwnedRuntimeCapturedCommandLoad),typeof(OwnedRuntimeCapturedEngineState),typeof(OwnedRuntimeCapturedThruster),typeof(OwnedRuntimeCapturedPropellant)};
        static readonly Type[] FlightTypes=Types.Concat(new[]{typeof(OwnedFlightCheckpointData),typeof(OwnedFlightPilotData),
            typeof(OwnedFlightSessionData),typeof(OwnedFlightCapture),typeof(OwnedFlightManeuverData),typeof(OwnedFlightTimelineState),typeof(OwnedFlightTimelineSegmentState),typeof(OwnedFlightEventRow),typeof(OwnedFlightEventHistory),typeof(OwnedFlightEventQueueData)}).ToArray();
        static readonly Dictionary<Type,FieldInfo[]> Fields=BuildFields();
        static readonly Dictionary<Type,FieldInfo[]> Version5Fields=Fields.Where(pair=>!NewVersion6Type(pair.Key)).ToDictionary(pair=>pair.Key,pair=>pair.Value.Where(field=>(pair.Key!=typeof(OwnedRuntimeCheckpointData)||field.Name!="scalarSupply")&&(pair.Key!=typeof(OwnedRuntimeSourceBinding)||field.Name!="supplyCaptureSha256")&&(pair.Key!=typeof(OwnedFlightSessionData)||field.Name!="minimumAltitudeMeters")).ToArray());
        static readonly Dictionary<Type,FieldInfo[]> Version4Fields=Version5Fields.ToDictionary(pair=>pair.Key,pair=>pair.Value.Where(field=>pair.Key!=typeof(OwnedRuntimeCheckpointData)||field.Name!="deadlineStrategy").ToArray());
        static readonly Dictionary<Type,FieldInfo[]> Version3Fields=Version4Fields.ToDictionary(pair=>pair.Key,pair=>pair.Value.Where(field=>(pair.Key!=typeof(OwnedRuntimeCheckpointData)||field.Name!="executionStrategy")&&(pair.Key!=typeof(OwnedRuntimeMechanicalState)||field.Name!="method")).ToArray());
        static readonly Dictionary<Type,FieldInfo[]> Version2Fields=Version3Fields.Where(pair=>!NewVersion3Type(pair.Key)).ToDictionary(pair=>pair.Key,pair=>pair.Value.Where(field=>(pair.Key!=typeof(OwnedRuntimeCheckpointData)||field.Name!="mechanical")&&(pair.Key!=typeof(OwnedFlightCheckpointData)||field.Name!="timeline")).ToArray());
        static readonly Dictionary<Type,FieldInfo[]> LegacyFields=Version2Fields.Where(pair=>pair.Key!=typeof(OwnedRuntimeThermalState)).ToDictionary(pair=>pair.Key,pair=>pair.Value.Where(field=>pair.Key!=typeof(OwnedRuntimeCheckpointData)||field.Name!="thermal").ToArray());
        static readonly byte[] SchemaDigest=Digest(Utf8.GetBytes(string.Join("\n",Types.OrderBy(t=>t.FullName,StringComparer.Ordinal)
            .Select(t=>t.FullName+":"+string.Join(",",Fields[t].Select(f=>f.Name+"="+f.FieldType.FullName))))));
        static readonly byte[] FlightSchemaDigest=Digest(Utf8.GetBytes(string.Join("\n",FlightTypes.OrderBy(t=>t.FullName,StringComparer.Ordinal)
            .Select(t=>t.FullName+":"+string.Join(",",Fields[t].Select(f=>f.Name+"="+f.FieldType.FullName))))));
        static readonly byte[] LegacySchemaDigest=LegacyDigest(Types);
        static readonly byte[] LegacyFlightSchemaDigest=LegacyDigest(FlightTypes);
        static readonly byte[] Version2SchemaDigest=VersionDigest(Types,Version2Fields);
        static readonly byte[] Version2FlightSchemaDigest=VersionDigest(FlightTypes,Version2Fields);
        static readonly byte[] Version3SchemaDigest=VersionDigest(Types,Version3Fields);
        static readonly byte[] Version3FlightSchemaDigest=VersionDigest(FlightTypes,Version3Fields);
        static readonly byte[] Version4SchemaDigest=VersionDigest(Types,Version4Fields);
        static readonly byte[] Version4FlightSchemaDigest=VersionDigest(FlightTypes,Version4Fields);
        static readonly byte[] Version5SchemaDigest=VersionDigest(Types,Version5Fields);
        static readonly byte[] Version5FlightSchemaDigest=VersionDigest(FlightTypes,Version5Fields);
        static bool NewVersion6Type(Type t)
        {return t==typeof(OwnedRuntimeSupplyConfiguration)||t==typeof(OwnedRuntimeSupplyCapturePacket)||t==typeof(OwnedRuntimeSupplyEphemerisData)||t==typeof(OwnedRuntimeSupplyCurveData)||t==typeof(OwnedRuntimeCapturedSolarSupply)||t==typeof(OwnedRuntimeCapturedCommandLoad)||t==typeof(OwnedRuntimeCapturedEngineState)||t==typeof(OwnedRuntimeCapturedThruster)||t==typeof(OwnedRuntimeCapturedPropellant);}
        static bool NewVersion3Type(Type t)
        {return t==typeof(OwnedRuntimeMechanicalState)||t==typeof(OwnedFlightTimelineState)||t==typeof(OwnedFlightTimelineSegmentState)||t==typeof(OwnedFlightEventRow)||t==typeof(OwnedFlightEventHistory)||t==typeof(OwnedFlightEventQueueData);}
        static byte[] LegacyDigest(Type[] types)
        {return VersionDigest(types,LegacyFields);}
        static byte[] VersionDigest(Type[] types,Dictionary<Type,FieldInfo[]> fields)
        {return Digest(Utf8.GetBytes(string.Join("\n",types.Where(type=>fields.ContainsKey(type)).OrderBy(type=>type.FullName,StringComparer.Ordinal).Select(type=>type.FullName+":"+string.Join(",",fields[type].Select(field=>field.Name+"="+field.FieldType.FullName))))));}
        static readonly byte[] FlightMagic={79,70,67,66};
        sealed class Budget
        {
            public long Elements;
            byte[] scratch;
            public byte[] Scratch {get{return scratch??(scratch=new byte[65536]);}}
        }
        static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
        static Dictionary<Type,FieldInfo[]> BuildFields()
        {
            var result=new Dictionary<Type,FieldInfo[]>();
            foreach(Type type in FlightTypes)
            {
                var fields=type.GetFields(BindingFlags.Public|BindingFlags.Instance).OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
                foreach(var field in fields)Need(!field.IsInitOnly&&Allowed(field.FieldType),"Checkpoint binary schema contains an unsupported field.");
                result.Add(type,fields);
            }
            return result;
        }
        static bool Allowed(Type type)
        {return type==typeof(string)||type==typeof(double)||type==typeof(long)||type==typeof(int)||type==typeof(uint)||type==typeof(bool)
            ||FlightTypes.Contains(type)||(type.IsArray&&type.GetArrayRank()==1&&Allowed(type.GetElementType()));}
        static byte[] Digest(byte[] bytes){using(var sha=SHA256.Create())return sha.ComputeHash(bytes);}
        public static string SupplyPacketDigest(OwnedRuntimeSupplyCapturePacket packet)
        {
            using(var stream=new MemoryStream())
            {
                using(var writer=new BinaryWriter(stream,Utf8,true))Write(writer,typeof(OwnedRuntimeSupplyCapturePacket),packet,new Budget(),0);
                return BitConverter.ToString(Digest(stream.ToArray())).Replace("-","").ToLowerInvariant();
            }
        }
        static bool Same(byte[] a,byte[] b)
        {if(a.Length!=b.Length)return false;int different=0;for(int i=0;i<a.Length;i++)different|=a[i]^b[i];return different==0;}
        static void Space(Stream stream,int count)
        {Need(count>=0&&stream.Length<=MaximumPayloadBytes-count,"Checkpoint binary payload exceeds its byte bound.");}
        static void Elements(Budget budget,int count)
        {Need(count>=0&&count<=MaximumArrayLength,"Checkpoint binary array length exceeds its bound.");budget.Elements+=count;Need(budget.Elements<=MaximumArrayElements,"Checkpoint binary aggregate element bound exceeded.");}
        public static byte[] Encode(OwnedRuntimeHostCheckpoint checkpoint)
        {using(var stream=new MemoryStream()){EncodeTo(stream,checkpoint);return stream.ToArray();}}
        public static byte[] EncodeFlight(OwnedFlightCheckpointData checkpoint)
        {using(var stream=new MemoryStream()){EncodeFlightTo(stream,checkpoint);return stream.ToArray();}}
        public static void EncodeTo(Stream stream,OwnedRuntimeHostCheckpoint checkpoint)
        {EncodeTo(stream,typeof(OwnedRuntimeHostCheckpoint),checkpoint,Magic,SchemaDigest);}
        public static void EncodeFlightTo(Stream stream,OwnedFlightCheckpointData checkpoint)
        {EncodeTo(stream,typeof(OwnedFlightCheckpointData),checkpoint,FlightMagic,FlightSchemaDigest);}
        static void EncodeTo(Stream stream,Type type,object checkpoint,byte[] magic,byte[] schemaDigest)
        {
            Need(stream!=null&&stream.CanWrite&&stream.CanSeek,"Checkpoint output must be writable and seekable.");
            Need(stream.Position==0&&stream.Length==0,"Checkpoint output must be fresh and empty.");
            stream.Write(new byte[HeaderBytes],0,HeaderBytes);
            using(var payload=new PayloadWriter(stream))
            {
                using(var writer=new BinaryWriter(payload,Utf8,true))Write(writer,type,checkpoint,new Budget(),0);
                byte[] hash=payload.Finish();int length=checked((int)payload.Length);
                stream.Position=0;
                using(var header=new BinaryWriter(stream,Utf8,true))
                {header.Write(magic);header.Write(Version);header.Write(schemaDigest);header.Write(length);header.Write(hash);}
                stream.Position=HeaderBytes+(long)length;
            }
        }
        public static OwnedRuntimeHostCheckpoint Decode(byte[] bytes)
        {Need(bytes!=null,"Checkpoint binary size invalid.");using(var stream=new MemoryStream(bytes,false))return DecodeFrom(stream);}
        public static OwnedFlightCheckpointData DecodeFlight(byte[] bytes)
        {Need(bytes!=null,"Checkpoint binary size invalid.");using(var stream=new MemoryStream(bytes,false))return DecodeFlightFrom(stream);}
        public static OwnedRuntimeHostCheckpoint DecodeFrom(Stream stream)
        {return (OwnedRuntimeHostCheckpoint)DecodeFrom(stream,typeof(OwnedRuntimeHostCheckpoint),Magic,SchemaDigest);}
        public static OwnedFlightCheckpointData DecodeFlightFrom(Stream stream)
        {return (OwnedFlightCheckpointData)DecodeFrom(stream,typeof(OwnedFlightCheckpointData),FlightMagic,FlightSchemaDigest);}
        static object DecodeFrom(Stream stream,Type type,byte[] magic,byte[] schemaDigest)
        {
            Need(stream!=null&&stream.CanRead&&stream.CanSeek,"Checkpoint input must be readable and seekable.");
            Need(stream.Position==0&&stream.Length>=HeaderBytes+1&&stream.Length<=HeaderBytes+(long)MaximumPayloadBytes,"Checkpoint binary size invalid.");
            try
            {
                using(var reader=new BinaryReader(stream,Utf8,true))
                {
                    Need(Same(reader.ReadBytes(4),magic),"Checkpoint binary magic differs.");int version=reader.ReadInt32();Need(version>=1&&version<=Version,"Checkpoint binary version differs.");
                    byte[] admitted=version==1?(Same(magic,Magic)?LegacySchemaDigest:LegacyFlightSchemaDigest):version==2?(Same(magic,Magic)?Version2SchemaDigest:Version2FlightSchemaDigest):version==3?(Same(magic,Magic)?Version3SchemaDigest:Version3FlightSchemaDigest):version==4?(Same(magic,Magic)?Version4SchemaDigest:Version4FlightSchemaDigest):version==5?(Same(magic,Magic)?Version5SchemaDigest:Version5FlightSchemaDigest):schemaDigest;
                    Need(Same(reader.ReadBytes(32),admitted),"Checkpoint binary typed schema differs.");
                    int length=reader.ReadInt32();Need(length>0&&length<=MaximumPayloadBytes&&stream.Length==HeaderBytes+(long)length,"Checkpoint binary payload length/trailing bytes differ.");
                    byte[] expected=reader.ReadBytes(32),actual=DigestPayload(stream,length);
                    Need(Same(expected,actual),"Checkpoint binary digest differs.");
                    stream.Position=HeaderBytes;
                    using(var payload=new PayloadReader(stream,length))using(var bodyReader=new BinaryReader(payload,Utf8,true))
                    {
                        var value=Read(bodyReader,type,new Budget(),0,version);
                        Need(payload.Position==payload.Length&&stream.Length==HeaderBytes+(long)length,"Checkpoint binary payload has trailing fields.");
                        Need(Same(expected,payload.Finish()),"Checkpoint binary changed during decoding.");return value;
                    }
                }
            }
            catch(EndOfStreamException ex){throw new InvalidDataException("Checkpoint binary is truncated.",ex);}
            catch(DecoderFallbackException ex){throw new InvalidDataException("Checkpoint binary UTF8 is invalid.",ex);}
        }
        static byte[] DigestPayload(Stream stream,int length)
        {
            var buffer=new byte[65536];int remaining=length;
            using(var sha=SHA256.Create())
            {
                while(remaining>0)
                {int read=stream.Read(buffer,0,Math.Min(buffer.Length,remaining));Need(read>0,"Checkpoint binary payload is truncated.");sha.TransformBlock(buffer,0,read,buffer,0);remaining-=read;}
                sha.TransformFinalBlock(buffer,0,0);return sha.Hash;
            }
        }
        sealed class PayloadWriter : Stream
        {
            readonly Stream target;readonly SHA256 sha=SHA256.Create();readonly byte[] buffer=new byte[65536];
            int buffered,count;bool finished;
            internal PayloadWriter(Stream target){this.target=target;}
            public override bool CanRead{get{return false;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return !finished;}}
            public override long Length{get{return count;}}public override long Position{get{return count;}set{throw new NotSupportedException();}}
            public override void Write(byte[] bytes,int offset,int length)
            {
                Need(!finished&&bytes!=null&&offset>=0&&length>=0&&offset<=bytes.Length-length,"Checkpoint payload write differs.");
                Need(count<=MaximumPayloadBytes-length,"Checkpoint binary payload exceeds its byte bound.");
                count+=length;
                while(length>0){int take=Math.Min(buffer.Length-buffered,length);Buffer.BlockCopy(bytes,offset,buffer,buffered,take);buffered+=take;offset+=take;length-=take;if(buffered==buffer.Length)Drain();}
            }
#if NET8_0_OR_GREATER
            public override void Write(ReadOnlySpan<byte> bytes)
            {
                Need(!finished&&count<=MaximumPayloadBytes-bytes.Length,"Checkpoint binary payload exceeds its byte bound.");
                count+=bytes.Length;
                while(!bytes.IsEmpty)
                {
                    int take=Math.Min(buffer.Length-buffered,bytes.Length);
                    bytes.Slice(0,take).CopyTo(buffer.AsSpan(buffered,take));buffered+=take;bytes=bytes.Slice(take);
                    if(buffered==buffer.Length)Drain();
                }
            }
#endif
            public override void WriteByte(byte value)
            {Need(!finished&&count<MaximumPayloadBytes,"Checkpoint binary payload exceeds its byte bound.");buffer[buffered++]=value;count++;if(buffered==buffer.Length)Drain();}
            void Drain(){if(buffered==0)return;target.Write(buffer,0,buffered);sha.TransformBlock(buffer,0,buffered,buffer,0);buffered=0;}
            public override void Flush(){Drain();target.Flush();}
            internal byte[] Finish(){Need(!finished,"Checkpoint payload already finished.");Drain();sha.TransformFinalBlock(buffer,0,0);finished=true;return sha.Hash;}
            protected override void Dispose(bool disposing){if(disposing)sha.Dispose();base.Dispose(disposing);}
            public override int Read(byte[] bytes,int offset,int length){throw new NotSupportedException();}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long length){throw new NotSupportedException();}
        }
        sealed class PayloadReader : Stream
        {
            readonly Stream source;readonly SHA256 sha=SHA256.Create();readonly byte[] buffer=new byte[65536];readonly int length;
            int buffered,offset,fetched,consumed;
            internal PayloadReader(Stream source,int length){this.source=source;this.length=length;}
            public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
            public override long Length{get{return HeaderBytes+(long)length;}}
            public override long Position{get{return HeaderBytes+(long)consumed;}set{throw new NotSupportedException();}}
            bool Fill()
            {
                if(offset<buffered)return true;if(fetched==length)return false;
                buffered=source.Read(buffer,0,Math.Min(buffer.Length,length-fetched));offset=0;
                Need(buffered>0,"Checkpoint binary payload is truncated.");fetched+=buffered;
                sha.TransformBlock(buffer,0,buffered,buffer,0);return true;
            }
            public override int Read(byte[] bytes,int start,int count)
            {
                if(bytes==null||start<0||count<0||start>bytes.Length-count)throw new ArgumentOutOfRangeException();
                int copied=0;while(copied<count&&Fill()){int take=Math.Min(count-copied,buffered-offset);Buffer.BlockCopy(buffer,offset,bytes,start+copied,take);offset+=take;consumed+=take;copied+=take;}return copied;
            }
#if NET8_0_OR_GREATER
            public override int Read(Span<byte> bytes)
            {
                int copied=0;
                while(copied<bytes.Length&&Fill())
                {
                    int take=Math.Min(bytes.Length-copied,buffered-offset);
                    buffer.AsSpan(offset,take).CopyTo(bytes.Slice(copied,take));offset+=take;consumed+=take;copied+=take;
                }
                return copied;
            }
#endif
            public override int ReadByte(){if(!Fill())return -1;consumed++;return buffer[offset++];}
            internal byte[] Finish(){Need(consumed==length,"Checkpoint binary payload is incomplete.");sha.TransformFinalBlock(buffer,0,0);return sha.Hash;}
            protected override void Dispose(bool disposing){if(disposing)sha.Dispose();base.Dispose(disposing);}
            public override void Flush(){}
            public override void Write(byte[] bytes,int offset,int count){throw new NotSupportedException();}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long length){throw new NotSupportedException();}
        }
        static void Write(BinaryWriter writer,Type type,object value,Budget budget,int depth)
        {
            Need(depth<=MaximumDepth,"Checkpoint binary depth exceeded.");var stream=writer.BaseStream;
            if(type==typeof(double)){Space(stream,8);writer.Write(BitConverter.DoubleToInt64Bits((double)value));return;}
            if(type==typeof(long)){Space(stream,8);writer.Write((long)value);return;}
            if(type==typeof(int)){Space(stream,4);writer.Write((int)value);return;}
            if(type==typeof(uint)){Space(stream,4);writer.Write((uint)value);return;}
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
                if(WritePrimitiveArray(writer,element,array,depth,budget))return;
                for(int i=0;i<array.Length;i++)Write(writer,element,array.GetValue(i),budget,depth+1);return;
            }
            FieldInfo[] fields;Need(Fields.TryGetValue(type,out fields),"Checkpoint binary DTO is not whitelisted.");Space(stream,1);
            if(value==null){writer.Write((byte)0);return;}Need(value.GetType()==type,"Checkpoint binary polymorphic DTO rejected.");writer.Write((byte)1);
            foreach(var field in fields)Write(writer,field.FieldType,field.GetValue(value),budget,depth+1);
        }
        static object Read(BinaryReader reader,Type type,Budget budget,int depth,int version)
        {
            Need(depth<=MaximumDepth,"Checkpoint binary depth exceeded.");
            if(type==typeof(double))return BitConverter.Int64BitsToDouble(reader.ReadInt64());
            if(type==typeof(long))return reader.ReadInt64();if(type==typeof(int))return reader.ReadInt32();
            if(type==typeof(uint))return reader.ReadUInt32();
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
                object primitive=ReadPrimitiveArray(reader,element,length,depth,budget);if(primitive!=null)return primitive;
                var array=Array.CreateInstance(element,length);for(int i=0;i<length;i++)array.SetValue(Read(reader,element,budget,depth+1,version),i);return array;
            }
            FieldInfo[] fields;Need((version==1?LegacyFields:version==2?Version2Fields:version==3?Version3Fields:version==4?Version4Fields:version==5?Version5Fields:Fields).TryGetValue(type,out fields),"Checkpoint binary DTO is not whitelisted.");byte presence=reader.ReadByte();Need(presence<=1,"Checkpoint binary DTO marker differs.");if(presence==0)return null;
            object value=Activator.CreateInstance(type);foreach(var field in fields)field.SetValue(value,Read(reader,field.FieldType,budget,depth+1,version));return value;
        }
        static int PrimitiveBytes(Type element)
        {return element==typeof(double)||element==typeof(long)?8:element==typeof(int)||element==typeof(uint)?4:element==typeof(bool)?1:0;}
        static bool WritePrimitiveArray(BinaryWriter writer,Type element,Array array,int depth,Budget budget)
        {
            int stride=PrimitiveBytes(element);if(stride==0)return false;
            Need(array.Length==0||depth<MaximumDepth,"Checkpoint binary depth exceeded.");Space(writer.BaseStream,checked(array.Length*stride));
            if(array.Length>=1024&&(BitConverter.IsLittleEndian||element==typeof(bool)))
            {
                byte[] scratch=budget.Scratch;int bytes=checked(array.Length*stride);
                for(int offset=0;offset<bytes;)
                {
                    int count=Math.Min(scratch.Length,bytes-offset);
                    if(element==typeof(bool)){var flags=(bool[])array;for(int i=0;i<count;i++)scratch[i]=(byte)(flags[offset+i]?1:0);}
                    else Buffer.BlockCopy(array,offset,scratch,0,count);
                    writer.Write(scratch,0,count);offset+=count;
                }
                return true;
            }
            if(element==typeof(double)){foreach(double value in (double[])array)writer.Write(BitConverter.DoubleToInt64Bits(value));}
            else if(element==typeof(long)){foreach(long value in (long[])array)writer.Write(value);}
            else if(element==typeof(int)){foreach(int value in (int[])array)writer.Write(value);}
            else if(element==typeof(uint)){foreach(uint value in (uint[])array)writer.Write(value);}
            else {foreach(bool value in (bool[])array)writer.Write((byte)(value?1:0));}
            return true;
        }
        static object ReadPrimitiveArray(BinaryReader reader,Type element,int length,int depth,Budget budget)
        {
            int stride=PrimitiveBytes(element);if(stride==0)return null;
            Need(length==0||depth<MaximumDepth,"Checkpoint binary depth exceeded.");
            Need((long)length*stride<=reader.BaseStream.Length-reader.BaseStream.Position,"Checkpoint binary array cannot fit payload.");
            if(length>=1024&&(BitConverter.IsLittleEndian||element==typeof(bool)))
            {
                Array values=element==typeof(double)?(Array)new double[length]:element==typeof(long)?new long[length]:
                    element==typeof(int)?new int[length]:element==typeof(uint)?new uint[length]:(Array)new bool[length];
                byte[] scratch=budget.Scratch;int bytes=checked(length*stride);
                for(int offset=0;offset<bytes;)
                {
                    int count=Math.Min(scratch.Length,bytes-offset);ReadInto(reader.BaseStream,scratch,count);
                    if(element==typeof(bool))
                    {var decodedFlags=(bool[])values;for(int i=0;i<count;i++){Need(scratch[i]<=1,"Checkpoint binary bool encoding differs.");decodedFlags[offset+i]=scratch[i]==1;}}
                    else Buffer.BlockCopy(scratch,0,values,offset,count);
                    offset+=count;
                }
                return values;
            }
            if(element==typeof(double)){var values=new double[length];for(int i=0;i<length;i++)values[i]=BitConverter.Int64BitsToDouble(reader.ReadInt64());return values;}
            if(element==typeof(long)){var values=new long[length];for(int i=0;i<length;i++)values[i]=reader.ReadInt64();return values;}
            if(element==typeof(int)){var values=new int[length];for(int i=0;i<length;i++)values[i]=reader.ReadInt32();return values;}
            if(element==typeof(uint)){var values=new uint[length];for(int i=0;i<length;i++)values[i]=reader.ReadUInt32();return values;}
            var flags=new bool[length];for(int i=0;i<length;i++){byte flag=reader.ReadByte();Need(flag<=1,"Checkpoint binary bool encoding differs.");flags[i]=flag==1;}return flags;
        }
        static void ReadInto(Stream stream,byte[] buffer,int count)
        {
            int read=0;while(read<count){int next=stream.Read(buffer,read,count-read);if(next==0)throw new EndOfStreamException();read+=next;}
        }
    }
}
