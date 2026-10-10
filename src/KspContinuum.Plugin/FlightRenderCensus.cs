using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace KspContinuum
{
    internal static class FlightRenderCensus
    {
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-render-census/v1", runId, vesselId;
            public string graphicsDeviceType, graphicsDeviceVersion;
            public bool supportsInstancing;
            public string scope = "One pre-capture vessel census; runtime instance identities are process-local. Potential groups are opportunities, not proven compatible instancing or measured draw calls.";
            public int processId, parts, renderers, meshRenderers, skinnedRenderers, otherRenderers;
            public int originallyEnabled, currentlyEnabled, diagnosticallyHidden, activeInHierarchy;
            public int estimatedSubmeshSubmissions, propertyBlockRenderers, animatedOwnerRenderers;
            public int potentialRepeatedRenderers, potentialGroups;
            public double censusWallMilliseconds;
            public Row[] rows;
            public Group[] groups;
            public CameraRow[] aeroCameras;
            public ShaderRow[] replacementShaders;
        }
        [Serializable] internal sealed class Row
        {
            public string rendererType, rendererName, ownerFlightId, ownerPartName, meshName, groupKey;
            public int rendererId, meshId, vertices, submeshes, materialSlots, estimatedSubmissions;
            public bool originallyEnabled, enabled, hiddenByDiagnostic, activeInHierarchy, hasPropertyBlock;
            public bool ownerHasAnimator, ownerHasAnimation, skinned, hasMesh;
            public bool meshReadable;
            public string meshIndexFormat;
            public string[] submeshTopology;
            public int layer, lightmapIndex, realtimeLightmapIndex;
            public string shadowCasting, lightProbes, reflectionProbes, motionVectors;
            public bool receiveShadows;
            public double scaleX, scaleY, scaleZ;
            public string[] ownerModules;
            public MaterialRow[] materials;
        }
        [Serializable] internal sealed class MaterialRow
        {
            public int instanceId, shaderId, renderQueue;
            public string name, shaderName;
            public string renderType;
            public int passCount;
            public bool enableInstancing, shaderSupported;
            public string[] shaderKeywords;
        }
        [Serializable] internal sealed class Group
        {
            public string key, meshName;
            public int meshId, rendererCount, originallyEnabledCount, currentEnabledCount, originallyActiveCount, currentActiveCount, estimatedSubmissions;
            public int propertyBlockCount, skinnedCount, animatedOwnerCount, ownerModuleCount;
            public bool allMaterialsInstancingEnabled = true;
            public int[] rendererIds;
        }
        [Serializable] internal sealed class CameraRow
        {
            public string role, name, clearFlags, depthTextureMode, targetName, targetFormat;
            public int instanceId, cullingMask, pixelWidth, pixelHeight, targetWidth, targetHeight, targetDepthBits, targetAntiAliasing;
            public bool enabled, activeInHierarchy, orthographic, hasTarget, targetCreated;
            public float depth, nearClip, farClip, fieldOfView, orthographicSize;
        }
        [Serializable] internal sealed class ShaderRow
        {
            public string role, name;
            public string passCountScope = "Default supported subshader of temporary material; camera RenderType replacement may select another subshader";
            public int instanceId, arrayIndex, defaultMaterialPassCount;
            public bool supported;
        }
        sealed class Accumulator { public Group Group; public readonly List<int> Ids = new List<int>(); }
        sealed class Owner
        {
            public string Id, Name;
            public string[] Modules;
            public bool Animator, Animation;
        }
        static string Identity(int value) { return value.ToString(CultureInfo.InvariantCulture); }

        public static void Record(Vessel vessel, string directory, string runId, ICollection<Renderer> hidden)
        {
            if (vessel == null || vessel.parts == null) throw new InvalidOperationException("Render census requires active vessel.");
            Stopwatch timer = Stopwatch.StartNew();
            var renderers = new HashSet<Renderer>();
            var owners = new Dictionary<Part, Owner>();
            foreach (Part part in vessel.parts) if (part != null)
            {
                var moduleNames = new List<string>();
                if (part.Modules != null) foreach (PartModule module in part.Modules)
                    if (module != null) moduleNames.Add(module.GetType().FullName);
                moduleNames.Sort(StringComparer.Ordinal);
                owners.Add(part, new Owner { Id = part.flightID.ToString(CultureInfo.InvariantCulture),
                    Name = part.partInfo == null ? part.name : part.partInfo.name, Modules = moduleNames.ToArray(),
                    Animator = part.GetComponentsInChildren<Animator>(true).Length > 0,
                    Animation = part.GetComponentsInChildren<Animation>(true).Length > 0 });
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true)) if (renderer != null) renderers.Add(renderer);
            }
            var ordered = new List<Renderer>(renderers);
            ordered.Sort((a,b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            var rows = new List<Row>(); var groups = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
            var report = new Report { runId = runId, vesselId = vessel.id.ToString("D"), parts = vessel.parts.Count,
                processId = Process.GetCurrentProcess().Id, renderers = ordered.Count, supportsInstancing = SystemInfo.supportsInstancing,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion };
            foreach (Renderer renderer in ordered)
            {
                var meshRenderer = renderer as MeshRenderer; var skinned = renderer as SkinnedMeshRenderer;
                Mesh mesh = skinned == null ? null : skinned.sharedMesh;
                if (meshRenderer != null) { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null) mesh = filter.sharedMesh; report.meshRenderers++; }
                else if (skinned != null) report.skinnedRenderers++; else report.otherRenderers++;
                Part part = renderer.GetComponentInParent<Part>(); Owner owner = null;
                if (part != null) owners.TryGetValue(part, out owner);
                Material[] materials = renderer.sharedMaterials;
                Vector3 scale = renderer.transform.lossyScale;
                var row = new Row { rendererType = renderer.GetType().FullName, rendererName = renderer.name,
                    rendererId = renderer.GetInstanceID(), meshId = mesh == null ? 0 : mesh.GetInstanceID(), meshName = mesh == null ? null : mesh.name,
                    hasMesh = mesh != null, vertices = mesh == null ? 0 : mesh.vertexCount, submeshes = mesh == null ? 0 : mesh.subMeshCount,
                    meshReadable = mesh != null && mesh.isReadable, meshIndexFormat = mesh == null ? null : mesh.indexFormat.ToString(),
                    materialSlots = materials.Length, skinned = skinned != null, enabled = renderer.enabled,
                    hiddenByDiagnostic = hidden.Contains(renderer), originallyEnabled = renderer.enabled || hidden.Contains(renderer),
                    activeInHierarchy = renderer.gameObject.activeInHierarchy, hasPropertyBlock = renderer.HasPropertyBlock(),
                    layer = renderer.gameObject.layer, lightmapIndex = renderer.lightmapIndex, realtimeLightmapIndex = renderer.realtimeLightmapIndex,
                    shadowCasting = renderer.shadowCastingMode.ToString(), receiveShadows = renderer.receiveShadows,
                    lightProbes = renderer.lightProbeUsage.ToString(), reflectionProbes = renderer.reflectionProbeUsage.ToString(),
                    motionVectors = renderer.motionVectorGenerationMode.ToString(), scaleX = scale.x, scaleY = scale.y, scaleZ = scale.z,
                    ownerFlightId = owner == null ? null : owner.Id, ownerPartName = owner == null ? null : owner.Name,
                    ownerHasAnimator = owner != null && owner.Animator, ownerHasAnimation = owner != null && owner.Animation,
                    ownerModules = owner == null ? new string[0] : owner.Modules, materials = new MaterialRow[materials.Length] };
                row.submeshTopology = new string[row.submeshes];
                for (int submesh = 0; submesh < row.submeshes; submesh++) row.submeshTopology[submesh] = mesh.GetTopology(submesh).ToString();
                row.estimatedSubmissions = mesh == null || materials.Length == 0 ? 0 : Math.Max(mesh.subMeshCount, materials.Length);
                string key = Identity(row.meshId) + "|" + row.layer + "|" + row.shadowCasting + "|" + row.receiveShadows + "|" + row.lightProbes + "|" +
                    row.reflectionProbes + "|" + row.motionVectors + "|" + row.lightmapIndex + "|" + row.realtimeLightmapIndex + "|" +
                    (scale.x * scale.y * scale.z < 0 ? "negative-determinant" : "positive-determinant");
                bool instancing = materials.Length > 0;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i]; var item = new MaterialRow(); row.materials[i] = item;
                    if (material == null) { instancing = false; key += "|null"; continue; }
                    Shader shader = material.shader; string[] keywords = material.shaderKeywords; Array.Sort(keywords, StringComparer.Ordinal);
                    item.instanceId = material.GetInstanceID(); item.name = material.name; item.renderQueue = material.renderQueue;
                    item.renderType = material.GetTag("RenderType", false, ""); item.passCount = material.passCount;
                    item.enableInstancing = material.enableInstancing; item.shaderId = shader == null ? 0 : shader.GetInstanceID();
                    item.shaderName = shader == null ? null : shader.name; item.shaderSupported = shader != null && shader.isSupported; item.shaderKeywords = keywords;
                    key += "|" + Identity(item.instanceId); instancing &= item.enableInstancing && item.shaderSupported;
                }
                row.groupKey = key; rows.Add(row);
                if (row.originallyEnabled) report.originallyEnabled++;
                if (row.enabled) report.currentlyEnabled++;
                if (row.hiddenByDiagnostic) report.diagnosticallyHidden++;
                if (row.activeInHierarchy) report.activeInHierarchy++;
                if (row.originallyEnabled && row.activeInHierarchy) report.estimatedSubmeshSubmissions += row.estimatedSubmissions;
                if (row.hasPropertyBlock) report.propertyBlockRenderers++;
                if (row.ownerHasAnimation || row.ownerHasAnimator) report.animatedOwnerRenderers++;
                if (mesh == null) continue;
                Accumulator accumulator;
                if (!groups.TryGetValue(key, out accumulator))
                { accumulator = new Accumulator { Group = new Group { key = key, meshId = row.meshId, meshName = row.meshName } }; groups.Add(key, accumulator); }
                Group group = accumulator.Group; accumulator.Ids.Add(row.rendererId); group.rendererCount++;
                if (row.originallyEnabled) group.originallyEnabledCount++;
                if (row.enabled) group.currentEnabledCount++;
                if (row.originallyEnabled && row.activeInHierarchy) group.originallyActiveCount++;
                if (row.enabled && row.activeInHierarchy) group.currentActiveCount++;
                if (row.hasPropertyBlock) group.propertyBlockCount++;
                if (row.skinned) group.skinnedCount++;
                if (row.ownerHasAnimation || row.ownerHasAnimator) group.animatedOwnerCount++;
                if (row.ownerModules.Length > 0) group.ownerModuleCount++;
                group.allMaterialsInstancingEnabled &= instancing;
                group.estimatedSubmissions += row.estimatedSubmissions;
            }
            var grouped = new List<Group>();
            foreach (Accumulator accumulator in groups.Values)
            {
                Group group = accumulator.Group; group.rendererIds = accumulator.Ids.ToArray(); grouped.Add(group);
                if (group.originallyActiveCount > 1 && group.propertyBlockCount == 0 && group.skinnedCount == 0 && group.animatedOwnerCount == 0)
                { report.potentialGroups++; report.potentialRepeatedRenderers += group.originallyActiveCount; }
            }
            grouped.Sort((a,b) => a.key.CompareTo(b.key));
            report.rows = rows.ToArray(); report.groups = grouped.ToArray(); report.censusWallMilliseconds = timer.Elapsed.TotalMilliseconds;
            AeroReceipt(report);
            report.censusWallMilliseconds = timer.Elapsed.TotalMilliseconds;
            File.WriteAllText(Path.Combine(directory, "render-census.json"), ReportJson.Encode(report));
        }

        static CameraRow CameraReceipt(string role, Camera camera)
        {
            if (camera == null) return null;
            RenderTexture target = camera.targetTexture;
            return new CameraRow { role = role, name = camera.name, instanceId = camera.GetInstanceID(), enabled = camera.enabled,
                activeInHierarchy = camera.gameObject.activeInHierarchy, cullingMask = camera.cullingMask,
                depth = camera.depth, clearFlags = camera.clearFlags.ToString(), depthTextureMode = camera.depthTextureMode.ToString(),
                pixelWidth = camera.pixelWidth, pixelHeight = camera.pixelHeight, nearClip = camera.nearClipPlane, farClip = camera.farClipPlane,
                fieldOfView = camera.fieldOfView, orthographic = camera.orthographic, orthographicSize = camera.orthographicSize,
                hasTarget = target != null, targetName = target == null ? null : target.name, targetFormat = target == null ? null : target.format.ToString(),
                targetWidth = target == null ? 0 : target.width, targetHeight = target == null ? 0 : target.height,
                targetDepthBits = target == null ? 0 : target.depth, targetAntiAliasing = target == null ? 0 : target.antiAliasing,
                targetCreated = target != null && target.IsCreated() };
        }
        static ShaderRow ShaderReceipt(string role, Shader shader, int index)
        {
            if (shader == null) return null;
            var row = new ShaderRow { role = role, name = shader.name, instanceId = shader.GetInstanceID(), supported = shader.isSupported, arrayIndex = index };
            Material probe = null;
            try { probe = new Material(shader); row.defaultMaterialPassCount = probe.passCount; }
            finally { if (probe != null) UnityEngine.Object.Destroy(probe); }
            return row;
        }
        static void AeroReceipt(Report report)
        {
            FXCamera fx = UnityEngine.Object.FindObjectOfType<FXCamera>();
            FXDepthCamera depth = UnityEngine.Object.FindObjectOfType<FXDepthCamera>();
            var cameras = new List<CameraRow>(); var shaders = new List<ShaderRow>();
            if (fx != null)
            {
                CameraRow camera = CameraReceipt("fx", fx.GetComponent<Camera>()); if (camera != null) cameras.Add(camera);
                FieldInfo velocityField = typeof(FXCamera).GetField("velocityCam", BindingFlags.Instance | BindingFlags.NonPublic);
                camera = CameraReceipt("velocity-occlusion", velocityField == null ? null : velocityField.GetValue(fx) as Camera);
                if (camera != null) cameras.Add(camera);
                if (fx.ReplacementShaders != null) for (int i = 0; i < fx.ReplacementShaders.Length; i++)
                { ShaderRow shader = ShaderReceipt("fx-replacement", fx.ReplacementShaders[i], i); if (shader != null) shaders.Add(shader); }
                ShaderRow debug = ShaderReceipt("fx-depth-debug", fx.depthDebugShader, -1); if (debug != null) shaders.Add(debug);
            }
            if (depth != null)
            {
                CameraRow camera = CameraReceipt("fx-depth", depth.depthCamera); if (camera != null) cameras.Add(camera);
                ShaderRow shader = ShaderReceipt("fx-depth-replacement", depth.ReplacementShader, -1); if (shader != null) shaders.Add(shader);
            }
            report.aeroCameras = cameras.ToArray(); report.replacementShaders = shaders.ToArray();
        }
    }
}
