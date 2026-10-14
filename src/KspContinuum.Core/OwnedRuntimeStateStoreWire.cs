using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace KspContinuum
{
    public static partial class OwnedRuntimeStateStore
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("ORSCOL01");
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        const int Version = 1;
        static readonly byte[] Schema = Digest(Encoding.UTF8.GetBytes("ORSCOL01/v1/capacity-policy:1-7,10-12,20-23,30-31,40,50-51,60-61,70-71,80-81,100-110:" + Shape(typeof(OwnedRuntimeMechanicalState), false) + Shape(typeof(OwnedRuntimeThermalState), true) + Shape(typeof(OwnedRuntimeSourceBinding), false)));
        sealed class Wire : Stream
        {
            readonly Stream source; readonly long maximum;readonly byte[] single=new byte[1];
#if NET8_0_OR_GREATER
            readonly IncrementalHash hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
#else
            readonly SHA256 hash=SHA256.Create();
#endif
            internal long Count; internal bool Hashing = true;
            internal Wire(Stream source, long maximum) { this.source = source; this.maximum = maximum; }
            internal byte[] Finish()
            {
                Hashing=false;
#if NET8_0_OR_GREATER
                return hash.GetHashAndReset();
#else
                hash.TransformFinalBlock(new byte[0],0,0);return hash.Hash;
#endif
            }
            void Charge(int count){if(count<0||Count>maximum-count)throw new InvalidDataException("State encoded-byte budget exceeded.");Count+=count;}
            void Add(byte[] buffer, int offset, int count)
            {
                Charge(count);if(!Hashing||count==0)return;
#if NET8_0_OR_GREATER
                hash.AppendData(buffer,offset,count);
#else
                hash.TransformBlock(buffer,offset,count,buffer,offset);
#endif
            }
            public override int Read(byte[] buffer, int offset, int count) { int n = source.Read(buffer, offset, count); Add(buffer, offset, n); return n; }
            public override void Write(byte[] buffer, int offset, int count) { Add(buffer, offset, count); source.Write(buffer, offset, count); }
            public override int ReadByte(){int value=source.ReadByte();if(value>=0){single[0]=(byte)value;Add(single,0,1);}return value;}
            public override void WriteByte(byte value){single[0]=value;Add(single,0,1);source.WriteByte(value);}
#if NET8_0_OR_GREATER
            public override int Read(Span<byte> buffer){int count=source.Read(buffer);Charge(count);if(Hashing)hash.AppendData(buffer.Slice(0,count));return count;}
            public override void Write(ReadOnlySpan<byte> buffer){Charge(buffer.Length);if(Hashing)hash.AppendData(buffer);source.Write(buffer);}
#endif
            public override bool CanRead { get { return source.CanRead; } } public override bool CanWrite { get { return source.CanWrite; } } public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { return Count; } set { throw new NotSupportedException(); } }
            public override void Flush() { source.Flush(); } public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
        }
        sealed class Budget
        {
            internal readonly OwnedRuntimeStateLimits Limits; internal long Allocations, Elements;
            internal Budget(OwnedRuntimeStateLimits limits) { Limits = limits; }
            internal void Reserve(long bytes, long elements = 0)
            { if (bytes < 0 || elements < 0 || Allocations > Limits.MaximumAllocationBytes - bytes || Elements > Limits.MaximumLogicalElements - elements) throw new InvalidDataException("State allocation/logical-element budget exceeded."); Allocations += bytes; Elements += elements; }
        }
        sealed class Column
        {
            internal readonly int Id, Width; internal readonly long Count; internal readonly Action<long, BinaryWriter> Write; internal readonly Action<long, BinaryReader> Read;
            internal Column(int id, long count, int width, Action<long, BinaryWriter> write, Action<long, BinaryReader> read)
            { Id = id; Count = count; Width = width; Write = write; Read = read; }
        }
        static byte[] Digest(byte[] bytes) { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes); }
        static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        static bool Zero(byte[] bytes) { for (int i = 0; i < bytes.Length; i++) if (bytes[i] != 0) return false; return true; }
        static bool Zero(byte[] bytes,int count) { for (int i = 0; i < count; i++) if (bytes[i] != 0) return false; return true; }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        static byte[] Exact(BinaryReader reader, int count) { byte[] bytes = reader.ReadBytes(count); Need(bytes.Length == count, "Truncated state stream."); return bytes; }
        static void Text(BinaryWriter writer, string text, OwnedRuntimeStateLimits limits)
        { if (text == null) { writer.Write(-1); return; } byte[] bytes = Utf8.GetBytes(text); Need(bytes.Length <= limits.MaximumStringBytes, "State string budget exceeded."); writer.Write(bytes.Length); writer.Write(bytes); }
        static string Text(BinaryReader reader, Budget budget)
        { int length = reader.ReadInt32(); if (length == -1) return null; Need(length >= 0 && length <= budget.Limits.MaximumStringBytes, "State string extent invalid."); budget.Reserve(checked(3L * length + 48)); return Utf8.GetString(Exact(reader, length)); }
        static void Vector(BinaryWriter w, Vec v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        static Vec Vector(BinaryReader r) { return new Vec(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()); }
        static void Quaternion(BinaryWriter w, Rotation q) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
        static Rotation Quaternion(BinaryReader r) { return Rotation.FromUnitComponents(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble()); }
        static void Input(BinaryWriter w, OwnedRuntimeInput i) { w.Write(i.Throttle); w.Write(i.Pitch); w.Write(i.Yaw); w.Write(i.Roll); w.Write(i.X); w.Write(i.Y); w.Write(i.Z); }
        static OwnedRuntimeInput Input(BinaryReader r) { return new OwnedRuntimeInput(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble()); }
        static void Member(BinaryWriter w, RigidAssemblyMember m)
        { w.Write(m.Id); w.Write(m.Mass); Vector(w, m.LocalPosition); Quaternion(w, m.LocalRotation); Vector(w, m.LocalCenterOfMass); Vector(w, m.PrincipalInertia); Quaternion(w, m.PrincipalRotation); }
        static RigidAssemblyMember Member(BinaryReader r)
        { return new RigidAssemblyMember(r.ReadInt32(), r.ReadDouble(), Vector(r), Quaternion(r), Vector(r), Vector(r), Quaternion(r)); }
        static FieldInfo[] Fields(Type type, bool noArrays)
        { var fields = new List<FieldInfo>(); foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public)) if (!noArrays || !f.FieldType.IsArray) fields.Add(f); fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name)); return fields.ToArray(); }
        static string Shape(Type type, bool noArrays)
        {
            if(type.IsArray)return "array:"+Shape(type.GetElementType(),false);
            var b = new StringBuilder(type.FullName); if(type.IsPrimitive||type==typeof(string))return b.ToString();
            foreach (var f in Fields(type, noArrays)) b.Append('|').Append(f.Name).Append(':').Append(Shape(f.FieldType,false)); return b.ToString();
        }
        static void Small(BinaryWriter w, Type type, object value, OwnedRuntimeStateLimits limits, bool noArrays = false)
        {
            if (type == typeof(string)) { Text(w, (string)value, limits); return; }
            if (type == typeof(int)) { w.Write((int)value); return; } if (type == typeof(long)) { w.Write((long)value); return; }
            if (type == typeof(double)) { w.Write((double)value); return; } if (type == typeof(bool)) { w.Write((bool)value); return; }
            w.Write(value != null); if (value == null) return;
            if (type.IsArray) { var a = (Array)value; Need(a.Length <= 200000, "Small metadata array budget exceeded."); w.Write(a.Length); for (int i = 0; i < a.Length; i++) Small(w, type.GetElementType(), a.GetValue(i), limits); return; }
            foreach (var f in Fields(type, noArrays)) Small(w, f.FieldType, f.GetValue(value), limits);
        }
        static object Small(BinaryReader r, Type type, Budget budget, bool noArrays = false)
        {
            if (type == typeof(string)) return Text(r, budget); if (type == typeof(int)) return r.ReadInt32(); if (type == typeof(long)) return r.ReadInt64();
            if (type == typeof(double)) return r.ReadDouble(); if (type == typeof(bool)) return Boolean(r);
            if (!Boolean(r)) return null;
            if (type.IsArray)
            { int length = r.ReadInt32(); Need(length >= 0 && length <= 200000 && length <= r.BaseStream.Length - r.BaseStream.Position, "Small metadata array extent invalid."); Type element = type.GetElementType(); budget.Reserve(checked(32L + 16L * length), length); var a = Array.CreateInstance(element, length); for (int i = 0; i < length; i++) a.SetValue(Small(r, element, budget), i); return a; }
            budget.Reserve(256); object value = Activator.CreateInstance(type); foreach (var f in Fields(type, noArrays)) f.SetValue(value, Small(r, f.FieldType, budget)); return value;
        }
        static bool Boolean(BinaryReader r) { byte b = r.ReadByte(); Need(b <= 1, "Noncanonical state boolean."); return b != 0; }
        sealed class SegmentFull : Exception { }
        sealed class SegmentBuffer : Stream
        {
            readonly int maximum;readonly MemoryStream inner;
            internal SegmentBuffer(int maximum){this.maximum=maximum;inner=new MemoryStream(maximum);}
            internal byte[] GetBuffer(){return inner.GetBuffer();}
            void RequireSpace(int count){if(count<0||Position>maximum-count)throw new SegmentFull();}
            public override void Write(byte[] buffer,int offset,int count){RequireSpace(count);inner.Write(buffer,offset,count);}
            public override void WriteByte(byte value){RequireSpace(1);inner.WriteByte(value);}
#if NET8_0_OR_GREATER
            public override void Write(ReadOnlySpan<byte> buffer){RequireSpace(buffer.Length);inner.Write(buffer);}
#endif
            public override bool CanRead{get{return false;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return true;}}
            public override long Length{get{return inner.Length;}}public override long Position{get{return inner.Position;}set{inner.Position=value;}}
            public override void Flush(){}
            public override void SetLength(long value){if(value<0||value>maximum)throw new SegmentFull();inner.SetLength(value);}
            public override long Seek(long offset,SeekOrigin origin){return inner.Seek(offset,origin);}
            public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
        }
        static void WriteColumns(BinaryWriter writer, List<Column> columns, OwnedRuntimeStateLimits limits, ref long segments)
        {
            using(var buffer=new SegmentBuffer(limits.SegmentBytes))
            using(var rowWriter=new BinaryWriter(buffer,Utf8,true))
            using(var sha=SHA256.Create())
            {
            byte[] scratch=buffer.GetBuffer();byte[] emptyHash=sha.ComputeHash(new byte[0]);
            foreach (var column in columns)
            {
                long next = 0;
                while (next < column.Count)
                {
                    int rows = 0;buffer.SetLength(0);buffer.Position=0;
                        int maximum = column.Width == 0 ? int.MaxValue : Math.Max(1, limits.SegmentBytes / column.Width);
                        while (next + rows < column.Count && rows < maximum)
                        {
                            long before = buffer.Length;
                            try{column.Write(next + rows, rowWriter);rowWriter.Flush();}
                            catch(SegmentFull){Need(rows!=0,"One state row exceeds segment budget.");buffer.SetLength(before);buffer.Position=before;break;}
                            if (column.Width != 0) Need(buffer.Length - before == column.Width, "State writer row width differs."); rows++;
                        }
                    int length=checked((int)buffer.Length);bool zero=column.Width!=0&&Zero(scratch,length);int encoded=zero?0:length;
                    writer.Write(column.Id); writer.Write(next); writer.Write(rows); writer.Write(column.Width); writer.Write((byte)(zero ? 1 : 0)); writer.Write(encoded); writer.Write(zero?emptyHash:sha.ComputeHash(scratch,0,encoded));writer.Write(scratch,0,encoded);
                    next += rows; segments++;
                }
            }
            }
        }
        static void ReadColumns(BinaryReader reader, List<Column> columns, int segmentBytes, ref long segments)
        {
            byte[] scratch=new byte[segmentBytes];
            using(var sha=SHA256.Create())
            {
            foreach (var column in columns)
            {
                long next = 0; int previousVariableBytes = -1;
                while (next < column.Count)
                {
                    int id = reader.ReadInt32(); long start = reader.ReadInt64(); int rows = reader.ReadInt32(), width = reader.ReadInt32(); byte encoding = reader.ReadByte(); int bytes = reader.ReadInt32();
                    Need(id == column.Id && start == next && rows > 0 && rows <= column.Count - next && width == column.Width, "State section order/extent/width differs.");
                    Need(bytes >= 0 && bytes <= segmentBytes && (encoding == 0 || encoding == 1 && width != 0 && bytes == 0), "State section encoding/byte extent differs.");
                    if (width != 0) Need(rows == Math.Min(column.Count - next, Math.Max(1, segmentBytes / width)) && (encoding == 1 || bytes == checked(rows * width)), "Noncanonical fixed-column chunk.");
                    byte[] expected = Exact(reader, 32);int filled=0;
                    while(filled<bytes){int n=reader.Read(scratch,filled,bytes-filled);Need(n>0,"Truncated state section.");filled+=n;}
                    Need(Equal(expected,sha.ComputeHash(scratch,0,bytes)), "State section checksum differs.");
                    Need(encoding == 1 || width == 0 || !Zero(scratch,bytes), "Raw positive-zero column is noncanonical.");
                    int logicalBytes=encoding==1?checked(rows*width):bytes;if(encoding==1)Array.Clear(scratch,0,logicalBytes);
                    using (var buffer = new MemoryStream(scratch,0,logicalBytes,false)) using (var rowReader = new BinaryReader(buffer, Utf8, true))
                    { for (int row = 0; row < rows; row++) { long before = buffer.Position; column.Read(next + row, rowReader); if (row == 0 && width == 0 && previousVariableBytes >= 0) Need(previousVariableBytes + buffer.Position - before > segmentBytes, "Noncanonical variable-column split."); } Need(buffer.Position == buffer.Length, "State section payload does not match its row extent."); }
                    previousVariableBytes = width == 0 ? bytes : -1; next += rows; segments++;
                }
            }
            }
        }
    }
}
