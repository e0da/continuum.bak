using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KspContinuum
{
    [Serializable] public sealed class ResidentMaterialManifest
    {
        public string schema, runId, vesselId, engine, colorSpace, family, topologyId;
        public string geometry, geometrySha256, state, stateSha256;
        public int processId; public float[] rootToWorld, cameraPosition, cameraRotation;
        public float fov, near, far; public ResidentLighting lighting;
        public ResidentMaterialProperty[] shaderGlobals;
        public ResidentTextureAsset[] textures; public ResidentMaterialAsset[] materials, originalMaterials; public ResidentInstanceAsset[] instances;
    }
    [Serializable] public sealed class ResidentTextureAsset
    {
        public string id, payload, payloadSha256; public long payloadLength;
        public int width, height, format, graphicsFormat, mipCount, filter, wrap, aniso; public float mipBias; public bool linear;
    }
    [Serializable] public sealed class ResidentMaterialAsset { public string id, shader; public int queue; public ResidentMaterialProperty[] properties; }
    [Serializable] public sealed class ResidentMaterialProperty { public string name; public int kind, textureIndex; public float[] value, scaleOffset; public int[] valueBits; }
    [Serializable] public sealed class ResidentInstanceAsset { public string id; public int meshIndex, materialIndex, layer, shadowCasting; public bool receiveShadows; }
    [Serializable] public sealed class ResidentLighting
    {
        public float[] direction, color, ambientSky, ambientEquator, ambientGround;
        public float intensity, ambientIntensity, shadowStrength, shadowDistance; public int ambientMode, shadows, shadowCascades;
    }
    [Serializable] public sealed class ResidentMeshData { public float[] positions, normals, uv, tangents; public int[] indices; }
    [Serializable] public sealed class ResidentGeometry { public ResidentMeshData[] meshes; }
    [Serializable] public sealed class ResidentInstanceState { public float[] matrix, temperature, rim, emission, sh, occlusion; public float falloff, opacityRaw, opacityEffective; }
    [Serializable] public sealed class ResidentSnapshot { public string topologyId; public long generation; public ResidentInstanceState[] instances; }

    public static class ResidentMaterialFormat
    {
        const int GeometryMagic = 0x32474d52, SnapshotMagic = 0x32534d52, Version = 2;
        const int MaxRows = 4096, MaxVertices = 4000000, MaxIndices = 12000000, FloatsPerState = 62;
        static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static void Scalar(float value) { Require(Finite(value), "Nonfinite resident scalar."); }
        static void Array(float[] value, int length)
        {
            Require(value != null && value.Length == length, "Resident numeric array shape differs."); foreach (float item in value) Scalar(item);
        }
        static void Text(string value)
        {
            Require(!string.IsNullOrEmpty(value) && value.Length <= 512, "Resident identity absent/oversize.");
            foreach (char c in value) Require(!char.IsControl(c), "Resident identity contains control characters.");
        }
        static void Digest(string value)
        {
            Require(value != null && value.Length == 64, "Resident SHA256 length differs.");
            foreach (char c in value) Require(c >= '0' && c <= '9' || c >= 'a' && c <= 'f', "Resident SHA256 must be lowercase hex.");
        }
        static void PathValue(string value)
        {
            Text(value); Require(!System.IO.Path.IsPathRooted(value) && value.IndexOf(':') < 0 && value.IndexOf('\\') < 0, "Resident payload path must be portable relative.");
            foreach (string segment in value.Split('/')) Require(segment.Length > 0 && segment != "." && segment != "..", "Resident payload path traversal/empty component.");
        }
        static void Matrix(float[] a)
        {
            Array(a, 16); Require(Math.Abs(a[12]) <= 0.000001f && Math.Abs(a[13]) <= 0.000001f && Math.Abs(a[14]) <= 0.000001f && Math.Abs(a[15] - 1) <= 0.000001f, "Resident matrix is not affine.");
            double determinant = (double)a[0] * (a[5] * (double)a[10] - a[6] * (double)a[9]) - (double)a[1] * (a[4] * (double)a[10] - a[6] * (double)a[8]) + (double)a[2] * (a[4] * (double)a[9] - a[5] * (double)a[8]);
            Require(!double.IsNaN(determinant) && !double.IsInfinity(determinant) && determinant > 0, "Resident matrix determinant is not positive.");
        }
        static void Geometry(ResidentGeometry value)
        {
            Require(value != null && value.meshes != null && value.meshes.Length > 0 && value.meshes.Length <= MaxRows, "Resident mesh count outside bounds.");
            int vertices = 0, indices = 0;
            foreach (ResidentMeshData mesh in value.meshes)
            {
                Require(mesh != null && mesh.positions != null && mesh.positions.Length % 3 == 0, "Resident position shape differs.");
                int count = mesh.positions.Length / 3; Require(count > 0 && count <= MaxVertices, "Resident vertex count outside bounds.");
                vertices = checked(vertices + count); Require(vertices <= MaxVertices, "Resident cumulative vertex budget exceeded.");
                Array(mesh.positions, checked(count * 3)); Array(mesh.normals, checked(count * 3)); Array(mesh.uv, checked(count * 2)); Array(mesh.tangents, checked(count * 4));
                for (int i = 0; i < count; i++) Require(Math.Abs(Math.Abs(mesh.tangents[i * 4 + 3]) - 1) <= 0.001f, "Resident tangent handedness differs.");
                Require(mesh.indices != null && mesh.indices.Length > 0 && mesh.indices.Length % 3 == 0 && mesh.indices.Length <= MaxIndices, "Resident triangle index count outside bounds.");
                indices = checked(indices + mesh.indices.Length); Require(indices <= MaxIndices, "Resident cumulative index budget exceeded.");
                foreach (int index in mesh.indices) Require(index >= 0 && index < count, "Resident triangle index outside mesh.");
            }
        }
        static void Snapshot(ResidentSnapshot value)
        {
            Require(value != null, "Resident snapshot absent."); Digest(value.topologyId); Require(value.generation >= 0, "Resident snapshot generation negative.");
            Require(value.instances != null && value.instances.Length > 0 && value.instances.Length <= MaxRows, "Resident snapshot count outside bounds.");
            foreach (ResidentInstanceState row in value.instances)
            {
                Require(row != null, "Resident instance state absent."); Matrix(row.matrix); Array(row.temperature, 4); Array(row.rim, 4); Array(row.emission, 4); Array(row.sh, 27); Array(row.occlusion, 4);
                Scalar(row.falloff); Scalar(row.opacityRaw); Scalar(row.opacityEffective); Require(row.opacityRaw >= 0 && row.opacityRaw <= 1 && row.opacityEffective >= 0 && row.opacityEffective <= 1, "Resident opacity outside bounds.");
            }
        }
        static void WriteFloats(BinaryWriter writer, float[] values) { foreach (float value in values) writer.Write(value); }
        static float[] ReadFloats(BinaryReader reader, int length) { var value = new float[length]; for (int i = 0; i < length; i++) value[i] = reader.ReadSingle(); return value; }
        static void BytesRemaining(BinaryReader reader, long required) { Require(required >= 0 && reader.BaseStream.Length - reader.BaseStream.Position >= required, "Truncated resident binary."); }
        public static void WriteGeometry(string path, ResidentGeometry value)
        {
            Geometry(value);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(GeometryMagic); writer.Write(Version); writer.Write(value.meshes.Length);
                foreach (ResidentMeshData mesh in value.meshes)
                {
                    int count = mesh.positions.Length / 3; writer.Write(count); writer.Write(mesh.indices.Length);
                    for (int i = 0; i < count; i++)
                    {
                        for (int j = 0; j < 3; j++) writer.Write(mesh.positions[i * 3 + j]); for (int j = 0; j < 3; j++) writer.Write(mesh.normals[i * 3 + j]);
                        for (int j = 0; j < 2; j++) writer.Write(mesh.uv[i * 2 + j]); for (int j = 0; j < 4; j++) writer.Write(mesh.tangents[i * 4 + j]);
                    }
                    foreach (int index in mesh.indices) writer.Write(index);
                }
            }
        }
        public static ResidentGeometry ReadGeometry(string path)
        {
            using (var stream = File.OpenRead(path)) using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                BytesRemaining(reader, 12); Require(reader.ReadInt32() == GeometryMagic && reader.ReadInt32() == Version, "Resident geometry header differs.");
                int count = reader.ReadInt32(); Require(count > 0 && count <= MaxRows, "Resident binary mesh count outside bounds.");
                var result = new ResidentGeometry { meshes = new ResidentMeshData[count] }; int totalVertices = 0, totalIndices = 0;
                for (int m = 0; m < count; m++)
                {
                    BytesRemaining(reader, 8); int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
                    Require(vertices > 0 && vertices <= MaxVertices && indices > 0 && indices <= MaxIndices && indices % 3 == 0, "Resident binary geometry capacity differs.");
                    totalVertices = checked(totalVertices + vertices); totalIndices = checked(totalIndices + indices); Require(totalVertices <= MaxVertices && totalIndices <= MaxIndices, "Resident binary cumulative capacity exceeded.");
                    BytesRemaining(reader, checked((long)vertices * 48 + (long)indices * 4));
                    var mesh = new ResidentMeshData { positions = new float[vertices * 3], normals = new float[vertices * 3], uv = new float[vertices * 2], tangents = new float[vertices * 4], indices = new int[indices] };
                    for (int i = 0; i < vertices; i++)
                    {
                        for (int j = 0; j < 3; j++) mesh.positions[i * 3 + j] = reader.ReadSingle(); for (int j = 0; j < 3; j++) mesh.normals[i * 3 + j] = reader.ReadSingle();
                        for (int j = 0; j < 2; j++) mesh.uv[i * 2 + j] = reader.ReadSingle(); for (int j = 0; j < 4; j++) mesh.tangents[i * 4 + j] = reader.ReadSingle();
                    }
                    for (int i = 0; i < indices; i++) mesh.indices[i] = reader.ReadInt32(); result.meshes[m] = mesh;
                }
                Require(stream.Position == stream.Length, "Resident geometry trailing bytes."); Geometry(result); return result;
            }
        }
        public static void WriteSnapshot(string path, ResidentSnapshot value)
        {
            Snapshot(value);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(SnapshotMagic); writer.Write(Version);
                for (int i = 0; i < 32; i++) writer.Write(Convert.ToByte(value.topologyId.Substring(i * 2, 2), 16));
                writer.Write(value.generation); writer.Write(value.instances.Length);
                foreach (ResidentInstanceState row in value.instances)
                {
                    WriteFloats(writer, row.matrix); WriteFloats(writer, row.temperature); WriteFloats(writer, row.rim); WriteFloats(writer, row.emission);
                    writer.Write(row.falloff); writer.Write(row.opacityRaw); writer.Write(row.opacityEffective); WriteFloats(writer, row.sh); WriteFloats(writer, row.occlusion);
                }
            }
        }
        public static ResidentSnapshot ReadSnapshot(string path)
        {
            using (var stream = File.OpenRead(path)) using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                BytesRemaining(reader, 52); Require(reader.ReadInt32() == SnapshotMagic && reader.ReadInt32() == Version, "Resident state header differs.");
                byte[] digest = reader.ReadBytes(32); var result = new ResidentSnapshot { topologyId = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant(), generation = reader.ReadInt64() };
                int count = reader.ReadInt32(); Require(count > 0 && count <= MaxRows, "Resident binary state count outside bounds.");
                BytesRemaining(reader, checked((long)count * FloatsPerState * 4)); result.instances = new ResidentInstanceState[count];
                for (int i = 0; i < count; i++) result.instances[i] = new ResidentInstanceState {
                    matrix = ReadFloats(reader, 16), temperature = ReadFloats(reader, 4), rim = ReadFloats(reader, 4), emission = ReadFloats(reader, 4),
                    falloff = reader.ReadSingle(), opacityRaw = reader.ReadSingle(), opacityEffective = reader.ReadSingle(), sh = ReadFloats(reader, 27), occlusion = ReadFloats(reader, 4) };
                Require(stream.Position == stream.Length, "Resident snapshot trailing bytes."); Snapshot(result); return result;
            }
        }
        static void Materials(ResidentMaterialAsset[] materials, string shader, int textureCount)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResidentMaterialAsset material in materials)
            {
                Require(material != null, "Resident material absent."); Text(material.id); Require(ids.Add(material.id), "Duplicate resident material identity."); Require(material.shader == shader, "Resident material shader differs."); Require(material.queue >= -1 && material.queue <= 5000 && material.properties != null && material.properties.Length <= 256, "Resident material property count/queue differs.");
                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (ResidentMaterialProperty property in material.properties)
                {
                    Require(property != null, "Resident property absent."); Require(property.valueBits == null || property.valueBits.Length == 0, "Resident material property has shader-global bit encoding."); Text(property.name); Require(properties.Add(property.name), "Duplicate resident material property."); Require(property.kind >= 0 && property.kind <= 4, "Resident property kind differs.");
                    if (property.kind == 4) { Array(property.value, 0); Array(property.scaleOffset, 4); Require(property.textureIndex >= -1 && property.textureIndex < textureCount, "Resident texture reference differs."); }
                    else { Array(property.value, property.kind < 2 ? 4 : 1); Require(property.textureIndex == -1, "Resident numeric property has texture reference."); Require(property.scaleOffset == null || property.scaleOffset.Length == 0, "Resident numeric property has texture scale."); }
                }
            }
        }
        static void Globals(ResidentMaterialProperty[] globals)
        {
            if (globals == null) return;
            string[] floats = { "_SpecularAmbientBoostDiffuse", "_SpecularAmbientBoostEmissive", "_UnderwaterFogFactor", "_UnderwaterMaxAlbedoFog", "_UnderwaterAlbedoDistanceScalar", "_UnderwaterMaxAlphaFog", "_UnderwaterMinAlphaFogDistance", "_UnderwaterAlphaDistanceScalar" };
            string[] vectors = { "_UnderwaterFogColor", "_LocalCameraPos", "_LocalCameraDir" };
            Require(globals.Length == floats.Length + vectors.Length, "Resident stock global count differs.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResidentMaterialProperty global in globals)
            {
                Require(global != null, "Resident stock global absent."); Text(global.name); Require(seen.Add(global.name), "Duplicate resident stock global.");
                bool isFloat = System.Array.IndexOf(floats, global.name) >= 0, isVector = System.Array.IndexOf(vectors, global.name) >= 0;
                Require(isFloat || isVector, "Resident stock global outside captured whitelist.");
                Require(global.kind == (isFloat ? 2 : 1) && global.textureIndex == -1 && (global.scaleOffset == null || global.scaleOffset.Length == 0), "Resident stock global type/reference differs.");
                int length = isFloat ? 1 : 4;
                if (global.valueBits == null || global.valueBits.Length == 0) Array(global.value, length);
                else
                {
                    Require(global.value != null && global.value.Length == 0 && global.valueBits.Length == length, "Resident stock global dual encoding/bit shape differs.");
                    bool infinityAllowed = global.name == "_UnderwaterAlbedoDistanceScalar" || global.name == "_UnderwaterAlphaDistanceScalar";
                    for (int component = 0; component < global.valueBits.Length; component++)
                    {
                        uint magnitude = (uint)global.valueBits[component] & 0x7fffffffu;
                        bool unusedCameraW = global.name == "_LocalCameraPos" && component == 3;
                        Require(magnitude <= 0x7f800000u && (magnitude != 0x7f800000u || infinityAllowed || unusedCameraW), "Resident stock global nonfinite bits outside admitted scope: " + global.name + " component=" + component);
                    }
                }
            }
        }
        public static void ValidateManifest(ResidentMaterialManifest manifest, ResidentGeometry geometry)
        {
            Require(manifest != null && manifest.schema == "continuum-resident-material-fixture/v2", "Resident manifest schema differs."); Geometry(geometry);
            Text(manifest.runId); Text(manifest.vesselId); Text(manifest.engine); Require(manifest.processId > 0, "Resident process identity absent.");
            Require(manifest.colorSpace == "Gamma" || manifest.colorSpace == "Linear", "Resident color space differs."); Require(manifest.family == "KSP/Emissive/Bumped Specular (Mapped)", "Resident material family differs.");
            Digest(manifest.topologyId); Digest(manifest.geometrySha256); Digest(manifest.stateSha256); PathValue(manifest.geometry); PathValue(manifest.state);
            Matrix(manifest.rootToWorld); Require(Math.Abs(manifest.rootToWorld[3]) <= 0.000001f && Math.Abs(manifest.rootToWorld[7]) <= 0.000001f && Math.Abs(manifest.rootToWorld[11]) <= 0.000001f, "Resident root translation must be removed.");
            Array(manifest.cameraPosition, 3); Array(manifest.cameraRotation, 4); double norm = 0; foreach (float v in manifest.cameraRotation) norm += v * (double)v; Require(Math.Abs(norm - 1) <= 0.001, "Resident camera quaternion differs.");
            Scalar(manifest.fov); Scalar(manifest.near); Scalar(manifest.far); Require(manifest.fov > 0 && manifest.fov < 180 && manifest.near > 0 && manifest.far > manifest.near, "Resident camera range differs.");
            Require(manifest.lighting != null, "Resident lighting absent."); ResidentLighting light = manifest.lighting;
            Array(light.direction, 3); Array(light.color, 4); Array(light.ambientSky, 4); Array(light.ambientEquator, 4); Array(light.ambientGround, 4);
            Scalar(light.intensity); Scalar(light.ambientIntensity); Scalar(light.shadowStrength); Scalar(light.shadowDistance);
            Require(light.intensity >= 0 && light.ambientIntensity >= 0 && light.shadowStrength >= 0 && light.shadowStrength <= 1 && light.shadowDistance >= 0 && (light.ambientMode == 0 || light.ambientMode == 1 || light.ambientMode == 3) && light.shadows >= 0 && light.shadows <= 2 && (light.shadowCascades == 0 || light.shadowCascades == 2 || light.shadowCascades == 4), "Resident lighting settings differ.");
            Require(manifest.textures != null && manifest.textures.Length <= MaxRows && manifest.materials != null && manifest.materials.Length > 0 && manifest.materials.Length <= MaxRows && manifest.instances != null && manifest.instances.Length > 0 && manifest.instances.Length <= MaxRows, "Resident asset counts outside bounds.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResidentTextureAsset texture in manifest.textures)
            {
                Require(texture != null, "Resident texture absent."); Text(texture.id); Require(ids.Add(texture.id), "Duplicate resident texture identity."); PathValue(texture.payload); Digest(texture.payloadSha256);
                Require(texture.payloadLength > 0 && texture.payloadLength <= int.MaxValue && texture.width > 0 && texture.width <= 16384 && texture.height > 0 && texture.height <= 16384 && texture.mipCount > 0 && texture.mipCount <= 15 && texture.format >= 0 && texture.graphicsFormat >= 0 && texture.filter >= 0 && texture.filter <= 2 && texture.wrap >= 0 && texture.wrap <= 3 && texture.aniso >= 0 && texture.aniso <= 16, "Resident texture metadata outside bounds."); Scalar(texture.mipBias);
            }
            string preparedShader = manifest.materials[0] == null ? null : manifest.materials[0].shader;
            Require(preparedShader == "Continuum/FlightPartSpecularInstanced" || preparedShader == "Continuum/FlightPartMappedInstanced", "Unsupported resident prepared shader.");
            Globals(manifest.shaderGlobals);
            Materials(manifest.materials, preparedShader, manifest.textures.Length);
            if (manifest.originalMaterials != null)
            {
                Require(manifest.originalMaterials.Length == manifest.materials.Length, "Resident original material count/order differs.");
                Materials(manifest.originalMaterials, manifest.family, manifest.textures.Length);
            }
            ids.Clear();
            foreach (ResidentInstanceAsset instance in manifest.instances)
            {
                Require(instance != null, "Resident instance asset absent."); Text(instance.id); Require(ids.Add(instance.id), "Duplicate resident instance identity."); Require(instance.meshIndex >= 0 && instance.meshIndex < geometry.meshes.Length && instance.materialIndex >= 0 && instance.materialIndex < manifest.materials.Length && instance.layer >= 0 && instance.layer <= 31 && instance.shadowCasting >= 0 && instance.shadowCasting <= 3, "Resident instance references/settings differ.");
            }
        }
        public static void ValidateSnapshot(ResidentMaterialManifest manifest, ResidentSnapshot snapshot)
        {
            Require(manifest != null && manifest.instances != null, "Resident manifest instance topology absent."); Digest(manifest.topologyId); Snapshot(snapshot);
            Require(snapshot.topologyId == manifest.topologyId && snapshot.instances.Length == manifest.instances.Length, "Resident snapshot topology/count differs.");
        }
    }
}
