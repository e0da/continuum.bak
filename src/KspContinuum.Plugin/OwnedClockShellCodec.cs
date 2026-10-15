using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace KspContinuum
{
    public static class OwnedClockShellCodec
    {
        public const int MaximumBytes = 64 * 1024 * 1024;
        static class Serializer<T>
        {
            internal static readonly object Sync = new object();
            internal static readonly XmlSerializer Value = new XmlSerializer(typeof(T), new XmlAttributeOverrides(), Type.EmptyTypes, null, null);
        }
        sealed class BoundedBuffer : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count < 0 || Position > MaximumBytes - count) throw new InvalidDataException("Owned shell XML exceeds its byte bound.");
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Position >= MaximumBytes) throw new InvalidDataException("Owned shell XML exceeds its byte bound.");
                base.WriteByte(value);
            }
        }
        public static byte[] Encode<T>(T data) where T : class
        {
            if (data == null) throw new ArgumentNullException("data");
            using (var buffer = new BoundedBuffer())
            {
                var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false, true), Indent = false,
                    NewLineHandling = NewLineHandling.Entitize, CheckCharacters = true, CloseOutput = false };
                using (var writer = XmlWriter.Create(buffer, settings))
                    lock (Serializer<T>.Sync) Serializer<T>.Value.Serialize(writer, data);
                return buffer.ToArray();
            }
        }
        public static T Decode<T>(byte[] bytes) where T : class
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("Owned shell XML size outside bound.");
            using (var stream = new MemoryStream(bytes, false)) return ReadStream<T>(stream);
        }
        static T ReadStream<T>(Stream stream) where T : class
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes, IgnoreWhitespace = false, CloseInput = false };
            using (var reader = XmlReader.Create(stream, settings))
            {
                T result;
                lock (Serializer<T>.Sync) result = Serializer<T>.Value.Deserialize(reader) as T;
                if (result == null) throw new InvalidDataException("Owned shell XML has no typed document.");
                while (reader.Read()) { }
                return result;
            }
        }
        public static T Read<T>(string path) where T : class
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length <= 0 || file.Length > MaximumBytes) throw new InvalidDataException("Owned shell XML size outside bound.");
                return ReadStream<T>(file);
            }
        }
        public static void WriteNew<T>(string path, T data) where T : class
        {
            byte[] bytes = Encode(data);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Write(bytes, 0, bytes.Length);
        }
    }
}
