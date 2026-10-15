using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace KspContinuum
{
    public static class AeroFieldFixtureCapture
    {
        [Serializable] public sealed class Manifest
        {
            public string schema = "continuum-aero-field-fixture/v1", runId, vesselId, profile, geometrySha256, geometry = "geometry.bin";
            public string contract = "Frozen geometry and stock visual parameters only; gray opaque display, no original material/texture or motion equivalence.";
            public int processId, rows, vertices, indices, declined, bakedSkins;
            public float[] cameraPosition, cameraRotation, flowDirection, effectColor, effectParameters, textureParameters, falloffs;
            public float fov, near, far;
            public string[] sourceRows, declineReasons;
        }
        sealed class Row { public Vector3[] position, normal; public Vector2[] uv; public int[] indices; public string identity; }
        static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        static void Check(Vector3 v) { if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z)) throw new InvalidDataException("Nonfinite fixture geometry."); }
        public static string Capture(Vessel vessel, FXCamera fx, string directory, string runId)
        {
            if (vessel == null || vessel.packed || fx == null || string.IsNullOrEmpty(runId) || !Path.IsPathRooted(directory) || Directory.Exists(directory) || File.Exists(directory))
                throw new InvalidOperationException("Loaded vessel, stock FX state, nonce and new absolute fixture directory required.");
            Camera camera = FlightCamera.fetch == null ? null : FlightCamera.fetch.mainCamera;
            if (camera == null) throw new InvalidOperationException("Flight view unavailable.");
            var result = new Manifest { runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id,
                profile = Application.unityVersion + "/" + SystemInfo.graphicsDeviceType, fov = camera.fieldOfView, near = camera.nearClipPlane, far = camera.farClipPlane };
            Transform root = vessel.transform;
            result.cameraPosition = V(root.InverseTransformPoint(camera.transform.position));
            Quaternion rotation = Quaternion.Inverse(root.rotation) * camera.transform.rotation;
            result.cameraRotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w };
            result.flowDirection = V(root.InverseTransformDirection(fx.effectDirection).normalized);
            result.effectColor = new[] { fx.color.r, fx.color.g, fx.color.b, fx.color.a };
            result.effectParameters = new[] { fx.intensity, fx.length, fx.edgeFade, fx.obliqueness };
            result.textureParameters = new[] { fx.textureSpeed, fx.textureScale, fx.wobble, 0f };
            result.falloffs = new[] { fx.falloff1, fx.falloff2, fx.falloff3 };
            var rows = new List<Row>(); var reasons = new List<string>(); var seen = new HashSet<int>();
            int totalVertices = 0, totalIndices = 0;
            foreach (Part part in vessel.parts)
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                if (!seen.Add(renderer.GetInstanceID()) || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy) continue;
                Mesh baked = null;
                try
                {
                    Mesh mesh; var skin = renderer as SkinnedMeshRenderer;
                    if (skin != null) { baked = new Mesh(); skin.BakeMesh(baked); mesh = baked; result.bakedSkins++; }
                    else { var filter = renderer.GetComponent<MeshFilter>(); mesh = renderer is MeshRenderer && filter != null ? filter.sharedMesh : null; }
                    if (mesh == null || mesh.vertexCount == 0) throw new InvalidOperationException("unsupported renderer/empty mesh");
                    Matrix4x4 transform = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                    if (transform.determinant <= 0) throw new InvalidOperationException("mirrored/singular transform");
                    var vertices = mesh.vertices; var normals = mesh.normals; var uv = mesh.uv;
                    if (normals.Length != vertices.Length) throw new InvalidOperationException("normals absent");
                    var positions = new Vector3[vertices.Length]; var directions = new Vector3[vertices.Length];
                    Matrix4x4 normalTransform = transform.inverse.transpose;
                    for (int i = 0; i < vertices.Length; i++) { positions[i] = transform.MultiplyPoint3x4(vertices[i]); directions[i] = normalTransform.MultiplyVector(normals[i]).normalized; Check(positions[i]); Check(directions[i]); }
                    Material[] materials = renderer.sharedMaterials;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        Material material = sub < materials.Length ? materials[sub] : null;
                        if (material == null || material.renderQueue >= 2450 || material.GetTag("RenderType", false, "Opaque") != "Opaque" || mesh.GetTopology(sub) != UnityEngine.MeshTopology.Triangles)
                        { result.declined++; reasons.Add(renderer.name + ":submesh " + sub + ":nonopaque/nontriangle"); continue; }
                        int[] indices = mesh.GetIndices(sub);
                        if (indices.Length == 0 || indices.Length % 3 != 0) throw new InvalidOperationException("invalid triangle indices");
                        foreach (int index in indices) if (index < 0 || index >= vertices.Length) throw new InvalidOperationException("out-of-range index");
                        totalVertices = checked(totalVertices + vertices.Length); totalIndices = checked(totalIndices + indices.Length);
                        if (totalVertices > 4000000 || totalIndices > 12000000 || rows.Count >= 4096) throw new InvalidDataException("Fixture capacity exceeded.");
                        rows.Add(new Row { position = positions, normal = directions, uv = uv.Length == vertices.Length ? uv : new Vector2[vertices.Length], indices = indices,
                            identity = renderer.name + "#" + renderer.GetInstanceID() + "/submesh" + sub });
                    }
                }
                catch (InvalidDataException) { throw; }
                catch (Exception error) { result.declined++; reasons.Add(renderer.name + ":" + error.GetType().Name + ":" + error.Message); }
                finally { if (baked != null) UnityEngine.Object.Destroy(baked); }
            }
            if (rows.Count == 0) throw new InvalidOperationException("No supported opaque fixture geometry.");
            Directory.CreateDirectory(directory);
            string geometry = Path.Combine(directory, result.geometry);
            using (var writer = new BinaryWriter(new FileStream(geometry, FileMode.CreateNew, FileAccess.Write)))
            {
                writer.Write(0x31464641); writer.Write(1); writer.Write(rows.Count);
                foreach (Row row in rows)
                {
                    writer.Write(row.position.Length); writer.Write(row.indices.Length);
                    for (int i = 0; i < row.position.Length; i++) { writer.Write(row.position[i].x); writer.Write(row.position[i].y); writer.Write(row.position[i].z); writer.Write(row.normal[i].x); writer.Write(row.normal[i].y); writer.Write(row.normal[i].z); writer.Write(row.uv[i].x); writer.Write(row.uv[i].y); }
                    foreach (int index in row.indices) writer.Write(index);
                }
            }
            using (var stream = File.OpenRead(geometry)) using (var sha = SHA256.Create()) result.geometrySha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            result.rows = rows.Count; result.vertices = totalVertices; result.indices = totalIndices;
            result.sourceRows = rows.ConvertAll(row => row.identity).ToArray(); result.declineReasons = reasons.ToArray();
            string manifest = Path.Combine(directory, "manifest.json");
            using (var writer = new StreamWriter(new FileStream(manifest, FileMode.CreateNew, FileAccess.Write))) writer.Write(JsonUtility.ToJson(result, true));
            return manifest;
        }
    }
}
