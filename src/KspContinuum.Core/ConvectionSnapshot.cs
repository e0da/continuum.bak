using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KspContinuum
{
    [Serializable] public sealed class ConvectionRecording
    {
        public string sessionId, vesselId, assemblySha256, assemblyMvid;
        public ConvectionFrame[] frames;
    }
    [Serializable] public sealed class ConvectionFrame
    {
        public long sequence;
        public double universalTime, mach;
        public double[] globals;
        public ConvectionRow[] rows;
        public ConvectionCone[] acceptedCones;
    }
    [Serializable] public sealed class ConvectionRow
    {
        public uint flightId;
        public double projectedArea, projectedRadius, invFineness, minimumDot, maximumDot, centroidDot, maxWidthDepth;
        public double expectedTemp, expectedCoeff, expectedArea;
        public float[] projectedCenter, center, minExtents, maxExtents, extents;
    }
    [Serializable] public sealed class ConvectionCone
    {
        public int rowIndex;
        public float[] center, extents, offset;
        public double radius, cylNoseDot, shockNoseDot, shockAngle, shockConvectionTempMult, shockConvectionCoeffMult;
        public double occludeConvectionTempMult, occludeConvectionCoeffMult, occludeConvectionAreaMult;
    }
    public static class ConvectionFormat
    {
        const int Magic = 0x31564343, Version = 1;
        public const int MaxFrames = 4, MaxRows = 8192;
        public const long MaxBytes = 32L * 1024 * 1024;
        public static readonly string[] GlobalNames = {
            "detachedShockHeatMult", "detachedShockCoeffMult", "detachedBehindShockHeatMult", "detachedBehindShockCoeffMult",
            "detachedShockMachAngleMult", "detachedShockStartAngle", "detachedShockEndAngle", "obliqueShockAngleMult",
            "obliqueShockPartAngleMult", "obliqueShockMinAngleMult", "obliqueShockConeHeatMult", "obliqueShockConeCoeffMult",
            "obliqueShockCylHeatMult", "obliqueShockCylCoeffMult" };
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
        static void Identity(string value)
        {
            Require(!string.IsNullOrEmpty(value) && Utf8.GetByteCount(value) <= 2048, "Convection identity absent or oversize.");
            foreach (char c in value) Require(!char.IsControl(c), "Convection identity contains controls.");
        }
        static void Shape(Array values, int size)
        { Require(values != null && values.Length == size, "Convection numeric column shape differs."); }
        public static void ValidateFrame(ConvectionFrame frame)
        {
            Require(frame != null && frame.sequence >= 0 && frame.rows != null && frame.rows.Length > 0 && frame.rows.Length <= MaxRows,
                "Convection frame identity or row bound differs.");
            Shape(frame.globals, 14);
            var ids = new HashSet<uint>();
            foreach (ConvectionRow row in frame.rows)
            {
                Require(row != null && row.flightId != 0 && ids.Add(row.flightId), "Convection row identity zero or duplicated.");
                Shape(row.projectedCenter, 3); Shape(row.center, 2); Shape(row.minExtents, 2); Shape(row.maxExtents, 2); Shape(row.extents, 2);
            }
            Require(frame.acceptedCones != null && frame.acceptedCones.Length <= frame.rows.Length, "Convection cone bound differs.");
            int previous = -1;
            foreach (ConvectionCone cone in frame.acceptedCones)
            {
                Require(cone != null && cone.rowIndex > previous && cone.rowIndex < frame.rows.Length, "Convection accepted order differs.");
                Shape(cone.center, 3); Shape(cone.extents, 2); Shape(cone.offset, 2); previous = cone.rowIndex;
            }
        }
        public static void Validate(ConvectionRecording recording)
        {
            Require(recording != null, "Convection recording absent.");
            Identity(recording.sessionId); Identity(recording.vesselId); Identity(recording.assemblySha256); Identity(recording.assemblyMvid);
            Require(recording.assemblySha256.Length == 64 && Guid.TryParse(recording.assemblyMvid, out _), "Convection assembly identity differs.");
            foreach (char c in recording.assemblySha256) Require(c >= '0' && c <= '9' || c >= 'a' && c <= 'f', "Convection digest encoding differs.");
            Require(recording.frames != null && recording.frames.Length > 0 && recording.frames.Length <= MaxFrames, "Convection frame bound differs.");
            long previous = -1, size = 0;
            foreach (ConvectionFrame frame in recording.frames)
            {
                ValidateFrame(frame); Require(frame.sequence > previous, "Convection frame order differs."); previous = frame.sequence;
                size = checked(size + 144L + frame.rows.Length * 128L + frame.acceptedCones.Length * 104L);
            }
            Require(size <= MaxBytes, "Convection recording capacity exceeded.");
        }
        static void String(BinaryWriter writer, string value) { byte[] bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        static string String(BinaryReader reader)
        {
            int length = reader.ReadInt32(); Require(length > 0 && length <= 2048, "Convection string bound exceeded.");
            byte[] bytes = reader.ReadBytes(length); Require(bytes.Length == length, "Truncated convection identity."); return Utf8.GetString(bytes);
        }
        static void Floats(BinaryWriter writer, float[] values) { foreach (float value in values) writer.Write(value); }
        static float[] Floats(BinaryReader reader, int count) { var values = new float[count]; for (int i = 0; i < count; i++) values[i] = reader.ReadSingle(); return values; }
        static void Cone(BinaryWriter writer, ConvectionCone cone)
        {
            writer.Write(cone.rowIndex); Floats(writer, cone.center); Floats(writer, cone.extents); Floats(writer, cone.offset);
            writer.Write(cone.radius); writer.Write(cone.cylNoseDot); writer.Write(cone.shockNoseDot); writer.Write(cone.shockAngle);
            writer.Write(cone.shockConvectionTempMult); writer.Write(cone.shockConvectionCoeffMult); writer.Write(cone.occludeConvectionTempMult);
            writer.Write(cone.occludeConvectionCoeffMult); writer.Write(cone.occludeConvectionAreaMult);
        }
        static ConvectionCone Cone(BinaryReader reader)
        {
            return new ConvectionCone { rowIndex = reader.ReadInt32(), center = Floats(reader, 3), extents = Floats(reader, 2), offset = Floats(reader, 2),
                radius = reader.ReadDouble(), cylNoseDot = reader.ReadDouble(), shockNoseDot = reader.ReadDouble(), shockAngle = reader.ReadDouble(),
                shockConvectionTempMult = reader.ReadDouble(), shockConvectionCoeffMult = reader.ReadDouble(), occludeConvectionTempMult = reader.ReadDouble(),
                occludeConvectionCoeffMult = reader.ReadDouble(), occludeConvectionAreaMult = reader.ReadDouble() };
        }
        public static void Write(string path, ConvectionRecording recording)
        {
            Validate(recording);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write(Magic); writer.Write(Version); String(writer, recording.sessionId); String(writer, recording.vesselId);
                String(writer, recording.assemblySha256); String(writer, recording.assemblyMvid); writer.Write(recording.frames.Length);
                foreach (ConvectionFrame frame in recording.frames)
                {
                    writer.Write(frame.sequence); writer.Write(frame.universalTime); writer.Write(frame.mach);
                    foreach (double value in frame.globals) writer.Write(value);
                    writer.Write(frame.rows.Length); writer.Write(frame.acceptedCones.Length);
                    foreach (ConvectionRow row in frame.rows)
                    {
                        writer.Write(row.flightId); writer.Write(row.projectedArea); writer.Write(row.projectedRadius); writer.Write(row.invFineness);
                        writer.Write(row.minimumDot); writer.Write(row.maximumDot); writer.Write(row.centroidDot); writer.Write(row.maxWidthDepth);
                        writer.Write(row.expectedTemp); writer.Write(row.expectedCoeff); writer.Write(row.expectedArea);
                        Floats(writer, row.projectedCenter); Floats(writer, row.center); Floats(writer, row.minExtents); Floats(writer, row.maxExtents); Floats(writer, row.extents);
                    }
                    foreach (ConvectionCone cone in frame.acceptedCones) Cone(writer, cone);
                }
                Require(stream.Position <= MaxBytes, "Convection encoded file capacity exceeded.");
            }
        }
        public static ConvectionRecording Read(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                Require(stream.Length <= MaxBytes, "Convection file capacity exceeded.");
                Require(reader.ReadInt32() == Magic && reader.ReadInt32() == Version, "Convection schema differs.");
                var recording = new ConvectionRecording { sessionId = String(reader), vesselId = String(reader), assemblySha256 = String(reader), assemblyMvid = String(reader) };
                int count = reader.ReadInt32(); Require(count > 0 && count <= MaxFrames, "Convection frame bound exceeded."); recording.frames = new ConvectionFrame[count];
                for (int i = 0; i < count; i++)
                {
                    var frame = new ConvectionFrame { sequence = reader.ReadInt64(), universalTime = reader.ReadDouble(), mach = reader.ReadDouble(), globals = new double[14] };
                    for (int j = 0; j < 14; j++) frame.globals[j] = reader.ReadDouble();
                    int rows = reader.ReadInt32(), cones = reader.ReadInt32();
                    Require(rows > 0 && rows <= MaxRows && cones >= 0 && cones <= rows && 128L * rows + 104L * cones <= stream.Length - stream.Position, "Convection encoded row or cone shape differs.");
                    frame.rows = new ConvectionRow[rows]; frame.acceptedCones = new ConvectionCone[cones];
                    for (int j = 0; j < rows; j++)
                    {
                        frame.rows[j] = new ConvectionRow { flightId = reader.ReadUInt32(), projectedArea = reader.ReadDouble(), projectedRadius = reader.ReadDouble(), invFineness = reader.ReadDouble(),
                            minimumDot = reader.ReadDouble(), maximumDot = reader.ReadDouble(), centroidDot = reader.ReadDouble(), maxWidthDepth = reader.ReadDouble(),
                            expectedTemp = reader.ReadDouble(), expectedCoeff = reader.ReadDouble(), expectedArea = reader.ReadDouble(),
                            projectedCenter = Floats(reader, 3), center = Floats(reader, 2), minExtents = Floats(reader, 2), maxExtents = Floats(reader, 2), extents = Floats(reader, 2) };
                    }
                    for (int j = 0; j < cones; j++) frame.acceptedCones[j] = Cone(reader);
                    recording.frames[i] = frame;
                }
                Require(stream.Position == stream.Length, "Convection trailing bytes."); Validate(recording); return recording;
            }
        }
    }
}
