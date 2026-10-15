using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    internal static class FlightMaterialCensus
    {
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-material-census/v1", runId, vesselId;
            public int processId, renderers, activeMeshRenderers, propertyBlockRenderers, exactRepeatedRenderers, prospectiveSubmissionSavings;
            public bool supportsInstancing;
            public double wallMilliseconds;
            public string contract = "One pre-capture snapshot. Groups compare declared shader/material inputs and renderer state, not material clone identity. Arbitrary MaterialPropertyBlock overrides cannot be enumerated; these rows are excluded from groups. Prospective groups may still have instancing/probe/shader blockers, counted separately; no executable draw replacement is claimed. Shader globals, native instancing variant and future mutation remain unproven.";
            public string dynamicInputs = "Stock MaterialColorUpdater writes _TemperatureColor through part.mpb; Part highlighting/opacity writes _RimColor, _RimFalloff, _Opacity through the same property block. Known MPB reads do not prove override presence or exhaustiveness.";
            public Row[] rows; public Group[] groups;
        }
        [Serializable] internal sealed class Row
        {
            public int rendererId, meshId, materialId, shaderId, renderQueue, passCount;
            public string rendererName, ownerFlightId, shaderName, renderType, groupKey;
            public bool enabled, active, forceRenderingOff, hasPropertyBlock, instancingEnabled, shaderSupported;
            public string[] fallbackReasons, keywords, passes;
            public Property[] properties;
            public float[] observedTemperatureColor, observedRimColor;
            public float observedRimFalloff, observedOpacity;
        }
        [Serializable] internal sealed class Property
        {
            public string name, type, value, textureName; public int textureId, textureWidth, textureHeight;
        }
        [Serializable] internal sealed class Group
        {
            public string key, shaderName; public int meshId, rendererCount, blockerFreeRenderers, potentialSavedSubmissions; public int[] rendererIds;
        }
        sealed class Accumulator { public Group Group; public List<int> Ids = new List<int>(); }
        static string F(float value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        static string V(Vector4 value) { return F(value.x)+","+F(value.y)+","+F(value.z)+","+F(value.w); }
        static float[] C(Color value) { return new[] { value.r, value.g, value.b, value.a }; }
        static void Add(StringBuilder key, string value) { value = value ?? ""; key.Append(value.Length).Append(':').Append(value); }

        public static Report Record(Vessel vessel, string directory, string runId)
        {
            if (vessel == null || vessel.parts == null) throw new InvalidOperationException("Material census requires loaded vessel.");
            Stopwatch timer = Stopwatch.StartNew();
            var report = new Report { runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id, supportsInstancing = SystemInfo.supportsInstancing };
            var unique = new HashSet<Renderer>();
            foreach (Part part in vessel.parts) if (part != null)
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true)) if (renderer != null) unique.Add(renderer);
            var ordered = new List<Renderer>(unique); ordered.Sort((a,b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            var rows = new List<Row>(); var groups = new Dictionary<string,Accumulator>(StringComparer.Ordinal);
            var block = new MaterialPropertyBlock();
            foreach (Renderer renderer in ordered)
            {
                var reasons = new List<string>();
                Part owner = renderer.GetComponentInParent<Part>();
                var row = new Row { rendererId = renderer.GetInstanceID(), rendererName = renderer.name,
                    ownerFlightId = owner == null ? null : owner.flightID.ToString(CultureInfo.InvariantCulture),
                    enabled = renderer.enabled, active = renderer.gameObject.activeInHierarchy, forceRenderingOff = renderer.forceRenderingOff,
                    hasPropertyBlock = renderer.HasPropertyBlock() };
                if (!(renderer is MeshRenderer)) reasons.Add("not-mesh-renderer");
                if (!row.enabled || !row.active || row.forceRenderingOff) reasons.Add("not-currently-visible");
                var filter = renderer.GetComponent<MeshFilter>(); Mesh mesh = filter == null ? null : filter.sharedMesh;
                if (mesh == null || mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles) reasons.Add("not-single-triangle-submesh");
                else row.meshId = mesh.GetInstanceID();
                if (renderer is MeshRenderer && row.enabled && row.active && !row.forceRenderingOff) report.activeMeshRenderers++;
                if (row.hasPropertyBlock)
                {
                    report.propertyBlockRenderers++; reasons.Add("unenumerable-property-block");
                    block.Clear(); renderer.GetPropertyBlock(block);
                    row.observedTemperatureColor = C(block.GetColor(PhysicsGlobals.TemperaturePropertyID));
                    row.observedRimColor = C(block.GetColor(PropertyIDs._RimColor));
                    row.observedRimFalloff = block.GetFloat(PropertyIDs._RimFalloff); row.observedOpacity = block.GetFloat(PropertyIDs._Opacity);
                }
                Material[] materials = renderer.sharedMaterials;
                Material material = materials.Length == 1 ? materials[0] : null;
                if (material == null || material.shader == null) reasons.Add("not-single-material");
                var key = new StringBuilder();
                if (material != null && material.shader != null)
                {
                    Shader shader = material.shader; row.materialId = material.GetInstanceID(); row.shaderId = shader.GetInstanceID();
                    row.shaderName = shader.name; row.shaderSupported = shader.isSupported; row.instancingEnabled = material.enableInstancing;
                    row.renderType = material.GetTag("RenderType", false, ""); row.renderQueue = material.renderQueue; row.passCount = material.passCount;
                    row.keywords = material.shaderKeywords; Array.Sort(row.keywords, StringComparer.Ordinal);
                    row.passes = new string[material.passCount];
                    Add(key, row.shaderId.ToString(CultureInfo.InvariantCulture)); Add(key, row.renderType); Add(key, row.renderQueue.ToString(CultureInfo.InvariantCulture));
                    Add(key, material.doubleSidedGI.ToString()); Add(key, material.globalIlluminationFlags.ToString());
                    foreach (string keyword in row.keywords) Add(key, keyword);
                    for (int pass = 0; pass < material.passCount; pass++)
                    { string name = material.GetPassName(pass); row.passes[pass] = name + "=" + material.GetShaderPassEnabled(name); Add(key, row.passes[pass]); }
                    var properties = new List<Property>();
                    for (int index = 0; index < shader.GetPropertyCount(); index++)
                    {
                        string name = shader.GetPropertyName(index); ShaderPropertyType type = shader.GetPropertyType(index);
                        var property = new Property { name = name, type = type.ToString() };
                        switch (type)
                        {
                            case ShaderPropertyType.Color: property.value = V(material.GetColor(name)); break;
                            case ShaderPropertyType.Vector: property.value = V(material.GetVector(name)); break;
                            case ShaderPropertyType.Float: case ShaderPropertyType.Range: property.value = F(material.GetFloat(name)); break;
                            case ShaderPropertyType.Texture:
                                Texture texture = material.GetTexture(name); Vector2 scale = material.GetTextureScale(name), offset = material.GetTextureOffset(name);
                                property.textureId = texture == null ? 0 : texture.GetInstanceID(); property.textureName = texture == null ? null : texture.name;
                                property.textureWidth = texture == null ? 0 : texture.width; property.textureHeight = texture == null ? 0 : texture.height;
                                property.value = property.textureId.ToString(CultureInfo.InvariantCulture)+"/"+F(scale.x)+","+F(scale.y)+"/"+F(offset.x)+","+F(offset.y); break;
                            default: reasons.Add("unknown-shader-property-type"); break;
                        }
                        properties.Add(property); Add(key, property.name); Add(key, property.type); Add(key, property.value);
                    }
                    row.properties = properties.ToArray();
                    if (row.renderType != "Opaque") reasons.Add("not-opaque");
                    if (!row.shaderSupported) reasons.Add("unsupported-shader");
                    if (!row.instancingEnabled) reasons.Add("instancing-not-enabled");
                }
                if (!SystemInfo.supportsInstancing) reasons.Add("device-instancing-unavailable");
                if (renderer.lightmapIndex >= 0 || renderer.realtimeLightmapIndex >= 0) reasons.Add("lightmapped-renderer");
                if (renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off) reasons.Add("probe-inputs-not-reconstructed");
                Add(key, row.meshId.ToString(CultureInfo.InvariantCulture)); Add(key, renderer.gameObject.layer.ToString(CultureInfo.InvariantCulture));
                Add(key, renderer.shadowCastingMode.ToString()); Add(key, renderer.receiveShadows.ToString()); Add(key, renderer.motionVectorGenerationMode.ToString());
                Add(key, renderer.lightProbeUsage.ToString()); Add(key, renderer.reflectionProbeUsage.ToString());
                row.fallbackReasons = reasons.ToArray();
                bool comparable = renderer is MeshRenderer && row.enabled && row.active && !row.forceRenderingOff && !row.hasPropertyBlock &&
                    mesh != null && mesh.subMeshCount == 1 && mesh.GetTopology(0) == MeshTopology.Triangles && material != null && material.shader != null &&
                    row.renderType == "Opaque" && !reasons.Contains("unknown-shader-property-type");
                if (comparable)
                {
                    row.groupKey = key.ToString(); Accumulator group;
                    if (!groups.TryGetValue(row.groupKey, out group))
                    { group = new Accumulator { Group = new Group { key = row.groupKey, meshId = row.meshId, shaderName = row.shaderName } }; groups.Add(row.groupKey, group); }
                    group.Ids.Add(row.rendererId);
                    if (reasons.Count == 0) group.Group.blockerFreeRenderers++;
                }
                rows.Add(row);
            }
            var finalGroups = new List<Group>();
            foreach (Accumulator value in groups.Values)
            {
                value.Group.rendererIds = value.Ids.ToArray(); value.Group.rendererCount = value.Ids.Count;
                value.Group.potentialSavedSubmissions = value.Ids.Count - (value.Ids.Count + 1022) / 1023;
                if (value.Ids.Count > 1) report.exactRepeatedRenderers += value.Ids.Count;
                report.prospectiveSubmissionSavings += value.Group.potentialSavedSubmissions; finalGroups.Add(value.Group);
            }
            finalGroups.Sort((a,b) => string.CompareOrdinal(a.key,b.key)); report.renderers = rows.Count;
            report.rows = rows.ToArray(); report.groups = finalGroups.ToArray(); report.wallMilliseconds = timer.Elapsed.TotalMilliseconds;
            File.WriteAllText(Path.Combine(directory, "material-census.json"), ReportJson.Encode(report)); return report;
        }
    }
}
