using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal static class ResidentMaterialFixtureCapture
    {
        internal sealed class Source
        {
            public Mesh Mesh; public Material Material, OriginalMaterial; public MeshRenderer Renderer; public Part Part;
            public Matrix4x4 Matrix; public Vector4 Temperature, Rim, Emission, Occlusion;
            public SphericalHarmonicsL2 Lighting; public float Falloff, RawOpacity, EffectiveOpacity;
        }
        static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream)); }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        static float[] V(Vector4 v) { return new[] { v.x, v.y, v.z, v.w }; }
        static float[] C(Color v) { return new[] { v.r, v.g, v.b, v.a }; }
        static float[] M(Matrix4x4 v) { var result = new float[16]; for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) result[r * 4 + c] = v[r,c]; return result; }
        static string Stable(Source source)
        {
            var positions = new List<int>(); Transform cursor = source.Renderer.transform;
            while (cursor != source.Part.transform) { if (cursor == null) throw new InvalidDataException("Renderer is outside its admitted part."); positions.Add(cursor.GetSiblingIndex()); cursor = cursor.parent; }
            positions.Reverse(); return source.Part.flightID + "/" + string.Join("/", positions.Select(i => i.ToString()).ToArray());
        }
        static ResidentMaterialAsset CaptureMaterial(Material material, string id, Dictionary<Texture2D,int> textureIds, Dictionary<string,int> textureDescriptors, List<ResidentTextureAsset> textures, List<string> payloads)
        {
                    var properties = new List<ResidentMaterialProperty>();
                    for (int i = 0; i < material.shader.GetPropertyCount(); i++)
                    {
                        string name = material.shader.GetPropertyName(i); ShaderPropertyType kind = material.shader.GetPropertyType(i);
                        var property = new ResidentMaterialProperty { name = name, kind = (int)kind, textureIndex = -1, value = new float[0], scaleOffset = new float[0] };
                        switch (kind)
                        {
                            case ShaderPropertyType.Color: property.value = C(material.GetColor(name)); break;
                            case ShaderPropertyType.Vector: property.value = V(material.GetVector(name)); break;
                            case ShaderPropertyType.Float: case ShaderPropertyType.Range: property.value = new[] { material.GetFloat(name) }; break;
                            case ShaderPropertyType.Texture:
                                Texture original = material.GetTexture(name); Texture2D texture = original as Texture2D;
                                if (texture == null) throw new InvalidDataException("Mapped material has absent or unsupported texture: " + name);
                                int textureIndex;
                                if (!textureIds.TryGetValue(texture, out textureIndex))
                                {
                                    string payload; ResourceImageEntry entry = StartupResourceImage.PublishedPayload(texture, out payload);
                                    var asset = new ResidentTextureAsset { id = entry.payloadSha256, payload = "descriptor.bin", payloadSha256 = entry.payloadSha256, payloadLength = entry.payloadLength,
                                        width = entry.width, height = entry.height, format = entry.format, graphicsFormat = entry.graphicsFormat, mipCount = entry.mipCount, linear = entry.linear,
                                        filter = entry.filter, wrap = entry.wrap, aniso = entry.aniso, mipBias = entry.mipBias };
                                    string descriptor = ReportJson.Encode(asset), descriptorId;
                                    using (var sha = SHA256.Create()) descriptorId = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(descriptor)));
                                    if (!textureDescriptors.TryGetValue(descriptor, out textureIndex))
                                    {
                                        textureIndex = textures.Count; textureDescriptors.Add(descriptor, textureIndex); payloads.Add(payload);
                                        asset.id = descriptorId; asset.payload = "texture-" + textureIndex + ".bin"; textures.Add(asset);
                                    }
                                    textureIds.Add(texture, textureIndex);
                                }
                                property.textureIndex = textureIndex; Vector2 scale = material.GetTextureScale(name), offset = material.GetTextureOffset(name); property.scaleOffset = new[] { scale.x,scale.y,offset.x,offset.y }; break;
                            default: throw new InvalidDataException("Unsupported mapped property type.");
                        }
                        properties.Add(property);
                    }
            return new ResidentMaterialAsset { id = id, shader = material.shader.name, queue = material.renderQueue, properties = properties.ToArray() };
        }
        static int Bits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
        static ResidentMaterialProperty[] CaptureGlobals()
        {
            var globals = new List<ResidentMaterialProperty>();
            foreach (string name in new[] { "_SpecularAmbientBoostDiffuse", "_SpecularAmbientBoostEmissive", "_UnderwaterFogFactor", "_UnderwaterMaxAlbedoFog", "_UnderwaterAlbedoDistanceScalar", "_UnderwaterMaxAlphaFog", "_UnderwaterMinAlphaFogDistance", "_UnderwaterAlphaDistanceScalar" })
                globals.Add(new ResidentMaterialProperty { name = name, kind = 2, textureIndex = -1, value = new float[0], valueBits = new[] { Bits(Shader.GetGlobalFloat(name)) }, scaleOffset = new float[0] });
            foreach (string name in new[] { "_UnderwaterFogColor", "_LocalCameraPos", "_LocalCameraDir" })
                globals.Add(new ResidentMaterialProperty { name = name, kind = 1, textureIndex = -1, value = new float[0], valueBits = V(Shader.GetGlobalVector(name)).Select(Bits).ToArray(), scaleOffset = new float[0] });
            return globals.ToArray();
        }
        internal static string Capture(Vessel vessel, string directory, string runId, IList<Source> sources)
        {
            if (sources.Count == 0) throw new InvalidDataException("No admitted mapped family sources.");
            if (vessel == null || vessel.packed || !vessel.loaded || HighLogic.LoadedScene != GameScenes.FLIGHT) throw new InvalidDataException("Fixture requires a physical flight vessel.");
            string output = Path.GetFullPath(directory);
            if (!Path.IsPathRooted(directory) || Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Fixture output must be a new absolute directory.");
            var ordered = sources.OrderBy(Stable, StringComparer.Ordinal).ToArray();
            var meshes = new List<ResidentMeshData>(); var materials = new List<ResidentMaterialAsset>(); var originalMaterials = new List<ResidentMaterialAsset>(); var textures = new List<ResidentTextureAsset>();
            var meshIds = new Dictionary<Mesh,int>(); var materialIds = new Dictionary<Material,int>(); var textureIds = new Dictionary<Texture2D,int>(); var textureDescriptors = new Dictionary<string,int>(StringComparer.Ordinal);
            var payloads = new List<string>(); var instances = new List<ResidentInstanceAsset>(); var states = new List<ResidentInstanceState>();
            var stableIds = new HashSet<string>(StringComparer.Ordinal); int meshesWithoutVertexColors = 0, meshesWithWhiteVertexColors = 0;
            Matrix4x4 rootInverse = vessel.transform.worldToLocalMatrix, rootToWorld = vessel.transform.localToWorldMatrix;
            rootToWorld.m03 = rootToWorld.m13 = rootToWorld.m23 = 0;
            foreach (Source source in ordered)
            {
                int meshIndex, materialIndex;
                if (!meshIds.TryGetValue(source.Mesh, out meshIndex))
                {
                    meshIndex = meshes.Count; meshIds.Add(source.Mesh, meshIndex);
                    Mesh mesh = source.Mesh; if (!mesh.isReadable || mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles) throw new InvalidDataException("Source mesh cannot be captured exactly.");
                    Vector3[] positions = mesh.vertices, normals = mesh.normals; Vector2[] uv = mesh.uv; Vector4[] tangents = mesh.tangents;
                    if (positions.Length == 0 || normals.Length != positions.Length || uv.Length != positions.Length || tangents.Length != positions.Length) throw new InvalidDataException("Source mesh lacks complete normal/UV/tangent data.");
                    Color[] colors = mesh.colors;
                    if (colors.Length == 0) meshesWithoutVertexColors++;
                    else
                    {
                        if (colors.Length != positions.Length || colors.Any(color => color.r != 1 || color.g != 1 || color.b != 1 || color.a != 1)) throw new InvalidDataException("Nonwhite/incomplete stock vertex colors require a richer geometry wire.");
                        meshesWithWhiteVertexColors++;
                    }
                    meshes.Add(new ResidentMeshData { positions = positions.SelectMany(v => new[] { v.x,v.y,v.z }).ToArray(), normals = normals.SelectMany(v => new[] { v.x,v.y,v.z }).ToArray(), uv = uv.SelectMany(v => new[] { v.x,v.y }).ToArray(), tangents = tangents.SelectMany(v => V(v)).ToArray(), indices = mesh.GetIndices(0) });
                }
                if (!materialIds.TryGetValue(source.Material, out materialIndex))
                {
                    Material material = source.Material; materialIndex = materials.Count; materialIds.Add(material, materialIndex);
                    if ((material.shader.name != "Continuum/FlightPartSpecularInstanced" && material.shader.name != "Continuum/FlightPartMappedInstanced") || material.shaderKeywords.Any(keyword => keyword != "INSTANCING_ON")) throw new InvalidDataException("Wrong admitted mapped shader family.");
                    Material original = source.OriginalMaterial;
                    if (original == null || original.shader == null || original.shader.name != "KSP/Emissive/Bumped Specular (Mapped)" || original.shaderKeywords.Any(keyword => keyword != "INSTANCING_ON")) throw new InvalidDataException("Original mapped material identity/keywords unavailable.");
                    materials.Add(CaptureMaterial(material, "material-" + materialIndex, textureIds, textureDescriptors, textures, payloads));
                    originalMaterials.Add(CaptureMaterial(original, "original-material-" + materialIndex, textureIds, textureDescriptors, textures, payloads));
                }
                else
                {
                    Material original = source.OriginalMaterial;
                    if (original == null || original.shader == null || original.shader.name != "KSP/Emissive/Bumped Specular (Mapped)" || original.shaderKeywords.Any(keyword => keyword != "INSTANCING_ON")) throw new InvalidDataException("Original mapped material identity/keywords unavailable.");
                    ResidentMaterialAsset equivalent = CaptureMaterial(original, "original-material-" + materialIndex, textureIds, textureDescriptors, textures, payloads);
                    if (ReportJson.Encode(equivalent) != ReportJson.Encode(originalMaterials[materialIndex])) throw new InvalidDataException("Prepared material maps to different original bindings.");
                }
                string stable = Stable(source); if (!stableIds.Add(stable)) throw new InvalidDataException("Duplicate stable source identity.");
                instances.Add(new ResidentInstanceAsset { id = stable, meshIndex = meshIndex, materialIndex = materialIndex, layer = source.Renderer.gameObject.layer, shadowCasting = (int)source.Renderer.shadowCastingMode, receiveShadows = source.Renderer.receiveShadows });
                var sh = new float[27]; for (int channel = 0; channel < 3; channel++) for (int coefficient = 0; coefficient < 9; coefficient++) sh[channel*9+coefficient] = source.Lighting[channel,coefficient];
                states.Add(new ResidentInstanceState { matrix = M(rootInverse * source.Matrix), temperature = V(source.Temperature), rim = V(source.Rim), emission = V(source.Emission), sh = sh, occlusion = V(source.Occlusion), falloff = source.Falloff, opacityRaw = source.RawOpacity, opacityEffective = source.EffectiveOpacity });
            }

            Camera camera = FlightCamera.fetch == null ? null : FlightCamera.fetch.mainCamera;
            Light sun = Sun.Instance == null ? null : Sun.Instance.sunLight;
            if (camera == null || sun == null || sun.type != LightType.Directional) throw new InvalidDataException("Actual main camera/directional light unavailable.");
            var manifest = new ResidentMaterialManifest { schema = "continuum-resident-material-fixture/v2", runId = runId, processId = Process.GetCurrentProcess().Id, vesselId = vessel.id.ToString("D"), engine = Application.unityVersion, colorSpace = QualitySettings.activeColorSpace.ToString(), family = "KSP/Emissive/Bumped Specular (Mapped)", rootToWorld = M(rootToWorld),
                cameraPosition = V(vessel.transform.InverseTransformPoint(camera.transform.position)), cameraRotation = V(Quaternion.Inverse(vessel.transform.rotation) * camera.transform.rotation), fov = camera.fieldOfView, near = camera.nearClipPlane, far = camera.farClipPlane,
                geometry = "geometry-v2.bin", state = "state-v2.bin", shaderGlobals = CaptureGlobals(), textures = textures.ToArray(), materials = materials.ToArray(), originalMaterials = originalMaterials.ToArray(), instances = instances.ToArray(),
                lighting = new ResidentLighting { direction = V(sun.transform.forward), color = C(sun.color), intensity = sun.intensity, ambientSky = C(RenderSettings.ambientSkyColor), ambientEquator = C(RenderSettings.ambientEquatorColor), ambientGround = C(RenderSettings.ambientGroundColor), ambientIntensity = RenderSettings.ambientIntensity, ambientMode = (int)RenderSettings.ambientMode, shadows = (int)sun.shadows, shadowStrength = sun.shadowStrength, shadowDistance = QualitySettings.shadowDistance, shadowCascades = QualitySettings.shadowCascades } };
            Directory.CreateDirectory(output);
            ResidentMaterialFormat.WriteGeometry(Path.Combine(output, manifest.geometry), new ResidentGeometry { meshes = meshes.ToArray() });
            manifest.geometrySha256 = Hash(Path.Combine(output, manifest.geometry));
            var topology = new Topology { shaderGlobals = manifest.shaderGlobals, materials = manifest.materials, originalMaterials = manifest.originalMaterials, textures = manifest.textures, instances = manifest.instances };
            string topologyJson = ReportJson.Encode(topology);
            File.WriteAllText(Path.Combine(output, "topology.json"), topologyJson);
            using (var sha = SHA256.Create()) manifest.topologyId = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest.geometrySha256 + "\n" + topologyJson)));
            var geometry = ResidentMaterialFormat.ReadGeometry(Path.Combine(output, manifest.geometry));
            var snapshot = new ResidentSnapshot { topologyId = manifest.topologyId, generation = 0, instances = states.ToArray() };
            ResidentMaterialFormat.WriteSnapshot(Path.Combine(output, manifest.state), snapshot); manifest.stateSha256 = Hash(Path.Combine(output, manifest.state));
            File.WriteAllText(Path.Combine(output, "pending-manifest.json"), ReportJson.Encode(manifest));
            ResidentMaterialFormat.ValidateManifest(manifest, geometry); ResidentMaterialFormat.ValidateSnapshot(manifest, snapshot);
            for (int i = 0; i < textures.Count; i++) { string path = Path.Combine(output, textures[i].payload); File.Copy(payloads[i], path); if (new FileInfo(path).Length != textures[i].payloadLength || Hash(path) != textures[i].payloadSha256) throw new InvalidDataException("Prepared payload copy changed."); }
            string json = ReportJson.Encode(manifest);
            var coverage = new CoverageEvidence { runId = runId, processId = manifest.processId, instances = instances.Count, meshes = meshes.Count, textures = textures.Count, sourcePublishedTextureObjects = textureIds.Count, meshesWithoutVertexColors = meshesWithoutVertexColors, meshesWithWhiteVertexColors = meshesWithWhiteVertexColors, predictionMatched = instances.Count == 156 && meshes.Count == 5 && textures.Count == 8 };
            File.WriteAllText(Path.Combine(output, "capture-evidence.json"), JsonUtility.ToJson(coverage, true));
            using (var stream = new FileStream(Path.Combine(output, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json);
            UnityEngine.Debug.Log("[Continuum resident material fixture] status=ready instances=" + instances.Count + " meshes=" + meshes.Count + " textures=" + textures.Count + " sourcePublishedTextureObjects=" + textureIds.Count + " predictionMatched=" + coverage.predictionMatched + " predictedInstances=156 predictedMeshes=5 predictedTextures=8 topology=" + manifest.topologyId);
            return Path.Combine(output, "manifest.json");
        }
        [Serializable] public sealed class CoverageEvidence { public string schema = "continuum-resident-material-capture/v1", runId; public string materialBinding = "Exact prepared admitted Continuum packet material; manifest family identifies the original stock recipe and material.shader identifies the actual prepared shader."; public int processId, instances, meshes, textures, sourcePublishedTextureObjects, meshesWithoutVertexColors, meshesWithWhiteVertexColors; public int predictedInstances = 156, predictedMeshes = 5, predictedTextures = 8; public bool predictionMatched; }
        [Serializable] public sealed class Topology { public ResidentMaterialProperty[] shaderGlobals; public ResidentMaterialAsset[] materials, originalMaterials; public ResidentTextureAsset[] textures; public ResidentInstanceAsset[] instances; }
        static float[] V(Vector3 v) { return new[] { v.x,v.y,v.z }; }
        static float[] V(Quaternion v) { return new[] { v.x,v.y,v.z,v.w }; }
    }
}
