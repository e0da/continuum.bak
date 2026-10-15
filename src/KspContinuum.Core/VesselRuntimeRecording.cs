using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KspContinuum
{
    [Serializable] public sealed class VesselRuntimeRecording
    {
        public string sessionId, vesselId, manifestSha256;
        public long topology;
        public string[] drawIds;
        public uint[] partIds;
        public VesselRuntimeFrame[] frames;
    }
    [Serializable] public sealed class VesselRuntimeFrame
    {
        public long sequence;
        public double universalTime, altitude, density, speed;
        public double[] rootToWorld, poses, loads;
        public bool[] visible;
    }
    [Serializable] public sealed class VesselLoadResult { public double[] force, moment; }

    public static class VesselRuntimeFormat
    {
        const int Magic = 0x31525643, Version = 1;
        public const int MaxDraws = 8192, MaxParts = 4096, MaxFrames = 512;
        public const long MaxBytes = 512L * 1024 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static void Identity(string value)
        {
            Require(!string.IsNullOrEmpty(value) && Utf8.GetByteCount(value) <= 2048, "Runtime identity absent or oversize.");
            foreach (char c in value) Require(!char.IsControl(c), "Runtime identity contains controls.");
        }
        static void Digest(string value)
        {
            Require(value != null && value.Length == 64, "Runtime manifest digest absent.");
            foreach (char c in value) Require(c >= '0' && c <= '9' || c >= 'a' && c <= 'f', "Runtime manifest digest encoding differs.");
        }
        static void Numbers(double[] values, int count)
        {
            Require(values != null && values.Length == count, "Runtime numeric column shape differs.");
            foreach (double value in values) Require(Finite(value), "Runtime numeric column nonfinite.");
        }
        static void Affine(double[] m, int offset, bool translationFree)
        {
            Require(m[offset + 12] == 0 && m[offset + 13] == 0 && m[offset + 14] == 0 && m[offset + 15] == 1, "Runtime pose not affine.");
            if (translationFree) Require(m[offset + 3] == 0 && m[offset + 7] == 0 && m[offset + 11] == 0, "Runtime root includes world translation.");
            double d = m[offset] * (m[offset + 5] * m[offset + 10] - m[offset + 6] * m[offset + 9]) - m[offset + 1] * (m[offset + 4] * m[offset + 10] - m[offset + 6] * m[offset + 8]) + m[offset + 2] * (m[offset + 4] * m[offset + 9] - m[offset + 5] * m[offset + 8]);
            Require(Finite(d) && Math.Abs(d) > 1e-30, "Runtime pose singular.");
        }
        public static void ValidateFrame(VesselRuntimeFrame frame, int draws, int parts)
        {
            Require(draws > 0 && draws <= MaxDraws && parts > 0 && parts <= MaxParts && frame != null && frame.sequence >= 0, "Runtime frame identity invalid.");
            Require(Finite(frame.universalTime) && Finite(frame.altitude) && Finite(frame.density) && frame.density >= 0 && Finite(frame.speed) && frame.speed >= 0, "Runtime environment nonfinite or invalid.");
            Numbers(frame.rootToWorld, 16); Affine(frame.rootToWorld, 0, true);
            Numbers(frame.poses, checked(draws * 16)); Numbers(frame.loads, checked(parts * 6));
            Require(frame.visible != null && frame.visible.Length == draws, "Runtime visibility shape differs.");
            for (int i = 0; i < draws; i++) Affine(frame.poses, i * 16, false);
        }
        public static void Validate(VesselRuntimeRecording recording)
        {
            Require(recording != null, "Runtime recording absent."); Identity(recording.sessionId); Identity(recording.vesselId); Digest(recording.manifestSha256);
            Require(recording.topology > 0 && recording.drawIds != null && recording.drawIds.Length > 0 && recording.drawIds.Length <= MaxDraws && recording.partIds != null && recording.partIds.Length > 0 && recording.partIds.Length <= MaxParts, "Runtime topology invalid.");
            var draws = new HashSet<string>(StringComparer.Ordinal); foreach (string id in recording.drawIds) { Identity(id); Require(draws.Add(id), "Duplicate runtime draw identity."); }
            var parts = new HashSet<uint>(); foreach (uint id in recording.partIds) Require(id != 0 && parts.Add(id), "Duplicate or zero runtime part identity.");
            Require(recording.frames != null && recording.frames.Length > 0 && recording.frames.Length <= MaxFrames, "Runtime frame count invalid.");
            long sequence = -1; double time = double.NegativeInfinity;
            foreach (var frame in recording.frames)
            {
                ValidateFrame(frame, recording.drawIds.Length, recording.partIds.Length);
                Require(frame.sequence > sequence && frame.universalTime >= time, "Runtime frame order stale."); sequence = frame.sequence; time = frame.universalTime;
            }
            Require(FrameBytes(recording.drawIds.Length, recording.partIds.Length) * recording.frames.Length <= MaxBytes, "Runtime recording capacity exceeded.");
        }
        public static void Bind(VesselRuntimeRecording recording, string manifestSha256, WholeCraftSceneManifest manifest)
        {
            Validate(recording); Require(manifest != null && recording.manifestSha256 == manifestSha256 && recording.vesselId == manifest.vesselId && manifest.draws != null && recording.drawIds.Length == manifest.draws.Length, "Runtime cold manifest binding differs.");
            var parts = new HashSet<uint>(recording.partIds);
            for (int i = 0; i < recording.drawIds.Length; i++) Require(manifest.draws[i] != null && recording.drawIds[i] == manifest.draws[i].id && parts.Contains(manifest.draws[i].partId), "Runtime ordered draw binding differs.");
        }
        public static VesselLoadResult Analyze(VesselRuntimeFrame frame)
        {
            Require(frame != null && frame.loads != null && frame.loads.Length > 0 && frame.loads.Length % 6 == 0 && frame.loads.Length <= MaxParts * 6, "Runtime load shape invalid.");
            Numbers(frame.loads, frame.loads.Length); double[] f = new double[3], m = new double[3];
            for (int i = 0; i < frame.loads.Length; i += 6)
            {
                double x = frame.loads[i], y = frame.loads[i + 1], z = frame.loads[i + 2], fx = frame.loads[i + 3], fy = frame.loads[i + 4], fz = frame.loads[i + 5];
                f[0] += fx; f[1] += fy; f[2] += fz;
                m[0] += y * fz - z * fy; m[1] += z * fx - x * fz; m[2] += x * fy - y * fx;
            }
            Numbers(f, 3); Numbers(m, 3); return new VesselLoadResult { force = f, moment = m };
        }
        static long FrameBytes(int draws, int parts) { return 8 + 8 + 16 * 8 + 3 * 8 + draws * (16L * 8 + 1) + parts * 6L * 8; }
        static void String(BinaryWriter writer, string value) { byte[] bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        static string String(BinaryReader reader)
        {
            int length = reader.ReadInt32(); Require(length > 0 && length <= 2048, "Runtime string bound exceeded.");
            byte[] bytes = reader.ReadBytes(length); Require(bytes.Length == length, "Truncated runtime identity."); return Utf8.GetString(bytes);
        }
        static void Values(BinaryWriter writer, double[] values) { foreach (double value in values) writer.Write(value); }
        static double[] Values(BinaryReader reader, int count) { var values = new double[count]; for (int i = 0; i < count; i++) values[i] = reader.ReadDouble(); return values; }
        public static void Write(string path, VesselRuntimeRecording recording)
        {
            Validate(recording);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write(Magic); writer.Write(Version); String(writer, recording.sessionId); String(writer, recording.vesselId); String(writer, recording.manifestSha256); writer.Write(recording.topology);
                writer.Write(recording.drawIds.Length); foreach (string id in recording.drawIds) String(writer, id);
                writer.Write(recording.partIds.Length); foreach (uint id in recording.partIds) writer.Write(id);
                writer.Write(recording.frames.Length);
                foreach (var frame in recording.frames)
                {
                    writer.Write(frame.sequence); writer.Write(frame.universalTime); Values(writer, frame.rootToWorld); writer.Write(frame.altitude); writer.Write(frame.density); writer.Write(frame.speed);
                    Values(writer, frame.poses); foreach (bool visible in frame.visible) writer.Write((byte)(visible ? 1 : 0)); Values(writer, frame.loads);
                }
                Require(stream.Position <= MaxBytes, "Runtime encoded file capacity exceeded.");
            }
        }
        public static VesselRuntimeRecording Read(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                Require(stream.Length <= MaxBytes, "Runtime file capacity exceeded."); Require(reader.ReadInt32() == Magic && reader.ReadInt32() == Version, "Runtime schema differs.");
                var recording = new VesselRuntimeRecording { sessionId = String(reader), vesselId = String(reader), manifestSha256 = String(reader), topology = reader.ReadInt64() };
                int draws = reader.ReadInt32(); Require(draws > 0 && draws <= MaxDraws, "Runtime draw bound exceeded."); recording.drawIds = new string[draws]; for (int i = 0; i < draws; i++) recording.drawIds[i] = String(reader);
                int parts = reader.ReadInt32(); Require(parts > 0 && parts <= MaxParts, "Runtime part bound exceeded."); recording.partIds = new uint[parts]; for (int i = 0; i < parts; i++) recording.partIds[i] = reader.ReadUInt32();
                int frames = reader.ReadInt32(); Require(frames > 0 && frames <= MaxFrames && FrameBytes(draws, parts) * frames == stream.Length - stream.Position, "Runtime frame count or encoded shape differs."); recording.frames = new VesselRuntimeFrame[frames];
                for (int i = 0; i < frames; i++)
                {
                    var frame = new VesselRuntimeFrame { sequence = reader.ReadInt64(), universalTime = reader.ReadDouble(), rootToWorld = Values(reader, 16), altitude = reader.ReadDouble(), density = reader.ReadDouble(), speed = reader.ReadDouble(), poses = Values(reader, draws * 16), visible = new bool[draws] };
                    for (int j = 0; j < draws; j++) { byte flag = reader.ReadByte(); Require(flag <= 1, "Runtime visibility encoding differs."); frame.visible[j] = flag == 1; }
                    frame.loads = Values(reader, parts * 6); recording.frames[i] = frame;
                }
                Require(stream.Position == stream.Length, "Runtime trailing bytes."); Validate(recording); return recording;
            }
        }
    }
}
