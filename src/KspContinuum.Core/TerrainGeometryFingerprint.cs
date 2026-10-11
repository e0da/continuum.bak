using System;
using System.IO;
using System.Security.Cryptography;

namespace KspContinuum
{
    public static class TerrainGeometryFingerprint
    {
        public static string Compute(float[] positions, int[] triangles)
        {
            if (positions == null || positions.Length % 3 != 0 || positions.Length > 3000000 || triangles == null || triangles.Length % 3 != 0 || triangles.Length > 6000000)
                throw new InvalidDataException("Selected terrain geometry capacity/shape differs.");
            foreach (float value in positions) if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Selected terrain position nonfinite.");
            foreach (int index in triangles) if (index < 0 || index >= positions.Length / 3) throw new InvalidDataException("Selected terrain index outside vertices.");
            using (var hash = SHA256.Create()) using (var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write)) using (var writer = new BinaryWriter(sink))
            {
                writer.Write(positions.Length / 3); foreach (float value in positions) writer.Write(value);
                writer.Write(triangles.Length); foreach (int index in triangles) writer.Write(index);
                writer.Flush(); sink.FlushFinalBlock(); return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
