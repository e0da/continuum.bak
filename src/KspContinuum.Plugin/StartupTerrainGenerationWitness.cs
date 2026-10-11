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
                PQ selected = collider == null ? null : collider.GetComponent<PQ>();
                Mesh mesh = selected == null ? null : selected.mesh;
                bool member = selected != null && tree.Contains(selected) && ReferenceEquals(selected.sphereRoot, sphere);
                bool leaf = selected != null && !selected.isSubdivided;
                bool exact = selected != null && selected.meshCollider == collider && mesh != null && selected.meshCollider.sharedMesh == mesh;
                bool live = collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
                string digest = mesh == null ? "none" : MeshDigest(mesh);
                bool eligible = member && leaf && exact && live && selected.isBuilt && selected.isActive && !selected.isCached && policies > 0 && selected.subdivision >= minimum && foreign == 0;
                CelestialBody body = sphere.PQSModCBTransform == null ? null : sphere.PQSModCBTransform.body;
                Debug.Log("[Continuum terrain generation] serial=" + context.Serial + " phase=" + phase + " status=observed sphere=" + Id(sphere) +
                    " body=" + Id(body) + " site=" + Uri.EscapeDataString(context.Site.name ?? "") + " siteOwner=" + Id(context.Site.isPQSCity ? (UnityEngine.Object)context.Site.pqsCity : context.Site.pqsCity2) +
                    " temporary=" + Id(context.Temporary) + " target=" + Id(sphere.target) + " tree=" + tree.Count + " built=" + built + " leaves=" + leaves + " foreign=" + foreign +
                    " collider=" + Id(collider) + " quad=" + Id(selected) + " mesh=" + Id(mesh) + " member=" + member + " leaf=" + leaf + " exactMesh=" + exact + " live=" + live +
                    " selectedBuilt=" + (selected != null && selected.isBuilt) + " selectedActive=" + (selected != null && selected.isActive) + " selectedCached=" + (selected != null && selected.isCached) + " subdivision=" + (selected == null ? -1 : selected.subdivision) +
                    " hitDistance=" + (nearest < 0 ? "none" : hits[nearest].distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) +
                    " minimumColliderLevel=" + minimum + " policies=" + policies + " eligible=" + eligible + " geometrySha256=" + digest + " ticks=" + (System.Diagnostics.Stopwatch.GetTimestamp() - start) + " clockFrequency=" + System.Diagnostics.Stopwatch.Frequency);
            }
            catch (Exception error) { Debug.LogError("[Continuum terrain generation] serial=" + (context == null ? 0 : context.Serial) + " phase=" + phase + " status=invalid reason=" + Uri.EscapeDataString(error.Message)); }
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
