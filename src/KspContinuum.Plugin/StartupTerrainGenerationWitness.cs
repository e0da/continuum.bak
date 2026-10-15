using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace KspContinuum
{
    internal static class StartupTerrainGenerationWitness
    {
        internal sealed class Context
        {
            public long Serial;
            public PQS Sphere;
            public Transform Temporary;
            public LaunchSite Site;
            public bool OrientationConsumed;
        }
        static long serial;
        internal static Context Begin(PQS sphere, Transform temporary, LaunchSite site)
        { return new Context { Serial = ++serial, Sphere = sphere, Temporary = temporary, Site = site }; }
        internal static IEnumerator AfterFixed(Context context)
        { yield return new WaitForFixedUpdate(); Observe(context, "after-fixed"); }
        static int Id(UnityEngine.Object value) { return value == null ? 0 : value.GetInstanceID(); }
        internal static void Observe(Context context, string phase)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (context == null || context.Sphere == null || context.Temporary == null || context.Site == null)
                    throw new InvalidOperationException("Destroyed or absent generation owner.");
                PQS sphere = context.Sphere;
                var tree = new HashSet<PQ>(); var pending = new Stack<PQ>();
                if (sphere.quads != null) foreach (PQ root in sphere.quads) if (root != null) pending.Push(root);
                int built = 0, leaves = 0, foreign = 0;
                while (pending.Count != 0)
                {
                    PQ quad = pending.Pop(); if (!tree.Add(quad)) throw new InvalidOperationException("Repeated PQ tree node.");
                    if (tree.Count > 40000) throw new InvalidOperationException("PQ tree exceeds witness capacity.");
                    if (!ReferenceEquals(quad.sphereRoot, sphere)) foreign++;
                    if (quad.isBuilt) built++;
                    bool child = false;
                    if (quad.subNodes != null) foreach (PQ node in quad.subNodes) if (node != null) { pending.Push(node); child = true; }
                    if (!child) leaves++;
                }
                int minimum = -1, policies = 0;
                foreach (var mod in sphere.GetComponentsInChildren<PQSMod_QuadMeshColliders>(true))
                {
                    if (!ReferenceEquals(mod.sphere, sphere)) continue;
                    if (mod.GetType() != typeof(PQSMod_QuadMeshColliders)) throw new InvalidOperationException("Custom collider policy.");
                    int level = sphere.maxLevel - Mathf.Abs(mod.maxLevelOffset);
                    minimum = Math.Max(minimum, level); policies++;
                }
                Vector3 radial = context.Temporary.position - sphere.transform.position;
                if (radial.sqrMagnitude == 0) throw new InvalidOperationException("Absent site radial direction.");
                float outside = (float)(sphere.radiusMax + 1000);
                float distance = (float)(outside - sphere.radiusMin + 2000);
                if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0 || distance > 200000)
                    throw new InvalidOperationException("Unbounded radial ray.");
                var hits = new RaycastHit[64];
                int count = Physics.RaycastNonAlloc(sphere.transform.position + radial.normalized * outside,
                    -radial.normalized, hits, distance, 1 << 15, QueryTriggerInteraction.Ignore);
                if (count == hits.Length) throw new InvalidOperationException("Saturated nearest-hit witness.");
                int nearest = -1; for (int i = 0; i < count; i++) if (nearest < 0 || hits[i].distance < hits[nearest].distance) nearest = i;
                Collider collider = nearest < 0 ? null : hits[nearest].collider;
                var association = new Dictionary<Collider, PQ>();
                foreach (PQ quad in tree) if (quad.meshCollider != null)
                {
                    if (association.ContainsKey(quad.meshCollider)) throw new InvalidOperationException("Collider is shared by multiple PQ tree nodes.");
                    association.Add(quad.meshCollider, quad);
                }
                PQ selected = null;
                if (collider != null) association.TryGetValue(collider, out selected);
                MeshCollider nativeMeshCollider = collider as MeshCollider;
                Mesh mesh = nativeMeshCollider == null ? null : nativeMeshCollider.sharedMesh;
                bool member = selected != null && tree.Contains(selected) && ReferenceEquals(selected.sphereRoot, sphere);
                bool leaf = selected != null && !selected.isSubdivided;
                bool exact = selected != null && selected.meshCollider == collider && mesh != null && selected.mesh == mesh;
                bool live = collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
                string geometryReason; string digest = SafeMeshDigest(mesh, out geometryReason);
                bool geometryComplete = geometryReason == "complete";
                bool eligible = geometryComplete && member && leaf && exact && live && selected.isBuilt && selected.isActive && !selected.isCached && policies > 0 && selected.subdivision >= minimum && foreign == 0;
                CelestialBody body = sphere.PQSModCBTransform == null ? null : sphere.PQSModCBTransform.body;
                DescribeHit(context, phase, "nearest-actual", collider, selected, nearest < 0 ? -1 : hits[nearest].distance, digest, geometryReason);
                int terrainHit = -1;
                for (int i = 0; i < count; i++) if (hits[i].collider != null && association.ContainsKey(hits[i].collider) &&
                    (terrainHit < 0 || hits[i].distance < hits[terrainHit].distance)) terrainHit = i;
                if (terrainHit >= 0)
                {
                    MeshCollider terrainCollider = hits[terrainHit].collider as MeshCollider;
                    string terrainReason = geometryReason; string terrainDigest = terrainHit == nearest ? digest : SafeMeshDigest(terrainCollider == null ? null : terrainCollider.sharedMesh, out terrainReason);
                    DescribeHit(context, phase, "nearest-owned-terrain-separate", hits[terrainHit].collider, association[hits[terrainHit].collider], hits[terrainHit].distance, terrainDigest, terrainReason);
                }
                Debug.Log("[Continuum terrain generation] serial=" + context.Serial + " phase=" + phase + " status=observed sphere=" + Id(sphere) +
                    " body=" + Id(body) + " site=" + Uri.EscapeDataString(context.Site.name ?? "") + " siteOwner=" + Id(context.Site.isPQSCity ? (UnityEngine.Object)context.Site.pqsCity : context.Site.pqsCity2) +
                    " temporary=" + Id(context.Temporary) + " target=" + Id(sphere.target) + " tree=" + tree.Count + " built=" + built + " leaves=" + leaves + " foreign=" + foreign +
                    " collider=" + Id(collider) + " quad=" + Id(selected) + " mesh=" + Id(mesh) + " member=" + member + " leaf=" + leaf + " exactMesh=" + exact + " live=" + live +
                    " selectedBuilt=" + (selected != null && selected.isBuilt) + " selectedActive=" + (selected != null && selected.isActive) + " selectedCached=" + (selected != null && selected.isCached) + " subdivision=" + (selected == null ? -1 : selected.subdivision) +
                    " hitDistance=" + (nearest < 0 ? "none" : hits[nearest].distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) +
                    " minimumColliderLevel=" + minimum + " policies=" + policies + " eligible=" + eligible + " geometryProofComplete=" + geometryComplete + " geometryReason=" + geometryReason + " geometrySha256=" + digest + " ticks=" + (System.Diagnostics.Stopwatch.GetTimestamp() - start) + " clockFrequency=" + System.Diagnostics.Stopwatch.Frequency);
            }
            catch (Exception error) { Debug.LogError("[Continuum terrain generation] serial=" + (context == null ? 0 : context.Serial) + " phase=" + phase + " status=invalid reason=" + Uri.EscapeDataString(error.Message)); }
        }
        static string SafeMeshDigest(Mesh mesh, out string reason)
        {
            if (mesh == null) { reason = "mesh-absent"; return "unavailable"; }
            if (!mesh.isReadable) { reason = "mesh-not-readable"; return "unavailable"; }
            try { string digest = MeshDigest(mesh); reason = "complete"; return digest; }
            catch (Exception error) { reason = Uri.EscapeDataString(error.GetType().Name + ":" + error.Message); return "unavailable"; }
        }
        static void DescribeHit(Context context, string phase, string role, Collider collider, PQ associated, float distance, string digest, string geometryReason)
        {
            var ancestry = new List<string>();
            Transform cursor = collider == null ? null : collider.transform;
            while (cursor != null && ancestry.Count < 16) { ancestry.Add(Id(cursor) + ":" + Uri.EscapeDataString(cursor.name)); cursor = cursor.parent; }
            MeshCollider mc = collider as MeshCollider; PhysicMaterial material = collider == null ? null : collider.sharedMaterial;
            string matrix = "none";
            if (collider != null)
            {
                Matrix4x4 m = collider.transform.localToWorldMatrix; var values = new string[16];
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) values[r * 4 + c] = m[r, c].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                matrix = string.Join(",", values);
            }
            Debug.Log("[Continuum terrain generation hit] serial=" + context.Serial + " phase=" + phase + " role=" + role +
                " collider=" + Id(collider) + " type=" + (collider == null ? "none" : collider.GetType().Name) + " name=" + (collider == null ? "none" : Uri.EscapeDataString(collider.name)) +
                " layer=" + (collider == null ? -1 : collider.gameObject.layer) + " distance=" + distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " associationByReference=" + (associated != null) + " associatedPQ=" + Id(associated) + " componentPQ=" + Id(collider == null ? null : collider.GetComponent<PQ>()) +
                " nativeMesh=" + Id(mc == null ? null : mc.sharedMesh) + " convex=" + (mc != null && mc.convex) + " trigger=" + (collider != null && collider.isTrigger) +
                " built=" + (associated != null && associated.isBuilt) + " active=" + (associated != null && associated.isActive) + " cached=" + (associated != null && associated.isCached) + " subdivision=" + (associated == null ? -1 : associated.subdivision) +
                " meshReadable=" + (mc != null && mc.sharedMesh != null && mc.sharedMesh.isReadable) + " geometryProofComplete=" + (geometryReason == "complete") + " geometryReason=" + geometryReason + " geometrySha256=" + digest +
                " material=" + Id(material) + " dynamicFriction=" + (material == null ? "none" : material.dynamicFriction.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) +
                " staticFriction=" + (material == null ? "none" : material.staticFriction.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) +
                " bounciness=" + (material == null ? "none" : material.bounciness.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) +
                " ancestry=" + string.Join("/", ancestry.ToArray()) + " matrix=" + matrix);
        }
        static string MeshDigest(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices; int[] triangles = mesh.triangles;
            if (vertices.Length > 1000000 || triangles.Length > 6000000) throw new InvalidOperationException("Selected collider geometry exceeds witness capacity.");
            var positions = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++) { positions[i * 3] = vertices[i].x; positions[i * 3 + 1] = vertices[i].y; positions[i * 3 + 2] = vertices[i].z; }
            return TerrainGeometryFingerprint.Compute(positions, triangles);
        }
    }
}
