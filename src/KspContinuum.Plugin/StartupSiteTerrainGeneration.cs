using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace KspContinuum
{
    internal static class StartupSiteTerrainGeneration
    {
        internal sealed class Token
        {
            internal StartupTerrainGenerationWitness.Context Context;
            internal Transform Target;
            internal CelestialBody Body;
            internal PQSCity2 City;
            internal Transform Positioning;
            internal Vector3 TemporaryLocalPosition;
            internal double Latitude, Longitude, Altitude;
            internal Point[] Points;
            internal string Failure;
            internal bool Consumed;
        }
        sealed internal class Point
        {
            internal Collider Actual, Terrain;
            internal PQ Quad;
            internal Mesh ActualMesh, TerrainMesh;
            internal string ActualDigest, TerrainDigest;
            internal float[] Matrices;
        }
        static int requested, admitted, completed, fallback, pending;
        internal static Token Capture(StartupTerrainGenerationWitness.Context context)
        {
            requested++; pending++;
            var token = new Token { Context = context };
            try { Bind(token); token.Points = Read(token); admitted++; }
            catch (Exception error) { token.Failure = error.Message; }
            Log(token, "force-return", token.Failure == null ? "admitted" : "fallback", token.Failure ?? "site-and-terrain-generation-bound");
            return token;
        }
        static void Bind(Token t)
        {
            var c = t.Context;
            if (c == null || c.Sphere == null || c.Temporary == null || c.Site == null || c.Sphere.GetType() != typeof(PQS)) throw new InvalidOperationException("owner-unavailable");
            t.City = c.Site.pqsCity2;
            if (c.Site.positionMobileLaunchPad != null || c.Site.isPQSCity || t.City == null || t.City.GetType() != typeof(PQSCity2) || !ReferenceEquals(t.City.sphere, c.Sphere) || t.City.setOnWaterSurface)
                throw new InvalidOperationException("only-stock-static-city2-supported");
            foreach (PQSMod mod in c.Sphere.GetComponentsInChildren<PQSMod>(true)) if (mod != null && ReferenceEquals(mod.sphere, c.Sphere) && mod.GetType().Assembly != typeof(PQS).Assembly) throw new InvalidOperationException("custom-mod-stock-wait");
            t.Body = c.Sphere.PQSModCBTransform == null ? null : c.Sphere.PQSModCBTransform.body;
            if (t.Body == null || !ReferenceEquals(t.Body.pqsController, c.Sphere) || t.City.PositioningPoint == null) throw new InvalidOperationException("body-or-positioning-owner-unavailable");
            if ((c.Sphere.transform.lossyScale - Vector3.one).sqrMagnitude > 1e-8f) throw new InvalidOperationException("nonunit-sphere");
            t.Positioning = t.City.PositioningPoint; t.TemporaryLocalPosition = c.Temporary.localPosition;
            t.Latitude = t.City.lat; t.Longitude = t.City.lon; t.Altitude = t.City.alt;
            t.Target = c.Sphere.target;
        }
        static Point[] Read(Token t)
        {
            var c = t.Context; PQS sphere = c.Sphere;
            if (sphere == null || c.Temporary == null || t.City == null || t.Body == null || sphere.PQSModCBTransform == null || !ReferenceEquals(c.Site.pqsCity2, t.City) ||
                !ReferenceEquals(sphere.PQSModCBTransform.body, t.Body) || !ReferenceEquals(sphere.target, t.Target)) throw new InvalidOperationException("generation-owner-changed");
            if (!ReferenceEquals(t.City.PositioningPoint, t.Positioning) || t.Positioning == null || !c.Temporary.localPosition.Equals(t.TemporaryLocalPosition) ||
                t.City.lat != t.Latitude || t.City.lon != t.Longitude || t.City.alt != t.Altitude) throw new InvalidOperationException("site-recipe-or-positioning-changed");
            var tree = new HashSet<PQ>(); var queue = new Stack<PQ>();
            if (sphere.quads == null) throw new InvalidOperationException("tree-unpublished");
            foreach (PQ root in sphere.quads) if (root != null) queue.Push(root);
            var associations = new Dictionary<Collider, PQ>();
            while (queue.Count != 0)
            {
                PQ q = queue.Pop(); if (!tree.Add(q) || tree.Count > 40000 || !ReferenceEquals(q.sphereRoot, sphere)) throw new InvalidOperationException("tree-ownership-invalid");
                if (q.meshCollider != null) { if (associations.ContainsKey(q.meshCollider)) throw new InvalidOperationException("collider-multiple-owners"); associations.Add(q.meshCollider, q); }
                if (q.subNodes != null) foreach (PQ child in q.subNodes) if (child != null) queue.Push(child);
            }
            int level = -1;
            foreach (var mod in sphere.GetComponentsInChildren<PQSMod_QuadMeshColliders>(true)) if (ReferenceEquals(mod.sphere, sphere))
            { if (mod.GetType() != typeof(PQSMod_QuadMeshColliders)) throw new InvalidOperationException("custom-collider-policy"); level = Math.Max(level, sphere.maxLevel - Mathf.Abs(mod.maxLevelOffset)); }
            if (level < 0) throw new InvalidOperationException("collider-policy-unavailable");
            var digests = new Dictionary<Mesh, string>();
            return new[] { Ray(t, c.Temporary.position, associations, level, digests), Ray(t, t.Positioning.position, associations, level, digests) };
        }
        static Point Ray(Token t, Vector3 position, Dictionary<Collider, PQ> owners, int level, Dictionary<Mesh, string> digests)
        {
            PQS sphere = t.Context.Sphere; Vector3 radial = position - sphere.transform.position;
            float outside = (float)(sphere.radiusMax + 1000), distance = (float)(sphere.radiusMax - sphere.radiusMin + 3000);
            if (radial.sqrMagnitude == 0 || float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0 || distance > 200000) throw new InvalidOperationException("ray-outside-bound");
            var hits = new RaycastHit[64]; int n = Physics.RaycastNonAlloc(sphere.transform.position + radial.normalized * outside, -radial.normalized, hits, distance, 1 << 15, QueryTriggerInteraction.Ignore);
            if (n == 0 || n == hits.Length) throw new InvalidOperationException("ray-empty-or-saturated");
            int nearest = 0, terrain = -1;
            for (int i = 0; i < n; i++) { if (hits[i].distance < hits[nearest].distance) nearest = i; if (owners.ContainsKey(hits[i].collider) && (terrain < 0 || hits[i].distance < hits[terrain].distance)) terrain = i; }
            if (terrain < 0) throw new InvalidOperationException("owned-terrain-absent");
            Collider actual = hits[nearest].collider, owned = hits[terrain].collider; PQ quad = owners[owned];
            bool actualTerrain = ReferenceEquals(actual, owned);
            if (!actualTerrain && !actual.transform.IsChildOf(t.City.transform)) throw new InvalidOperationException("actual-nearest-not-site-or-owned-terrain");
            if (!quad.isBuilt || !quad.isActive || quad.isCached || quad.isSubdivided || quad.subdivision < level || quad.meshCollider != owned || quad.mesh == null || quad.meshCollider.sharedMesh != quad.mesh)
                throw new InvalidOperationException("owned-terrain-not-live-built-leaf");
            foreach (Collider collider in new[] { actual, owned }) if (!collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger || collider.gameObject.layer != 15) throw new InvalidOperationException("collider-not-live");
            MeshCollider mc = actual as MeshCollider;
            if (mc == null || mc.sharedMesh == null || mc.convex || quad.meshCollider.convex) throw new InvalidOperationException("unsupported-native-collider");
            return new Point { Actual = actual, Terrain = owned, Quad = quad, ActualMesh = mc.sharedMesh, TerrainMesh = quad.mesh,
                ActualDigest = Digest(mc.sharedMesh, digests), TerrainDigest = Digest(quad.mesh, digests), Matrices = MatrixPair(actual.transform.localToWorldMatrix, owned.transform.localToWorldMatrix) };
        }
        static string Digest(Mesh mesh, Dictionary<Mesh, string> cache)
        {
            string result; if (cache.TryGetValue(mesh, out result)) return result;
            if (!mesh.isReadable || mesh.vertexCount > 1000000) throw new InvalidOperationException("mesh-content-unavailable");
            Vector3[] vertices = mesh.vertices; var positions = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++) { positions[i * 3] = vertices[i].x; positions[i * 3 + 1] = vertices[i].y; positions[i * 3 + 2] = vertices[i].z; }
            result = TerrainGeometryFingerprint.Compute(positions, mesh.triangles); cache.Add(mesh, result); return result;
        }
        static float[] MatrixPair(Matrix4x4 a, Matrix4x4 b)
        { var values = new float[32]; for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) { values[r * 4 + c] = a[r, c]; values[16 + r * 4 + c] = b[r, c]; } return values; }
        internal static IEnumerator Wait(Token token, float seconds)
        {
            if (token == null || token.Consumed) { yield return new WaitForSeconds(seconds); yield break; }
            token.Consumed = true;
            IEnumerator inner = RunWait(token, seconds);
            try { while (inner.MoveNext()) yield return inner.Current; }
            finally { (inner as IDisposable)?.Dispose(); pending--; }
        }
        static IEnumerator RunWait(Token token, float seconds)
        {
            float deadline = Time.time + seconds;
            if (token.Failure != null)
            { fallback++; yield return new WaitForSeconds(seconds); yield break; }
            yield return new WaitForFixedUpdate();
            string reason = null;
            Point[] observed = null;
            try
            {
                Point[] current = observed = Read(token);
                for (int i = 0; i < current.Length; i++)
                {
                    Point a = token.Points[i], b = current[i];
                    if (!ReferenceEquals(a.Actual, b.Actual) || !ReferenceEquals(a.Terrain, b.Terrain) || !ReferenceEquals(a.Quad, b.Quad) || !ReferenceEquals(a.ActualMesh, b.ActualMesh) || !ReferenceEquals(a.TerrainMesh, b.TerrainMesh) ||
                        !TerrainGenerationAgreement.Matches(new[] {a.Actual.GetInstanceID(),a.Terrain.GetInstanceID()},new[] {b.Actual.GetInstanceID(),b.Terrain.GetInstanceID()},new[] {a.ActualDigest,a.TerrainDigest},new[] {b.ActualDigest,b.TerrainDigest},a.Matrices,b.Matrices))
                        throw new InvalidOperationException("site-or-terrain-geometry-changed");
                }
            }
            catch (Exception error) { reason = error.Message; }
            if (observed != null) LogPoints(token, "after-fixed", observed);
            if (reason == null && Time.time < deadline)
            { completed++; Log(token, "after-fixed", "completed", "same-site-and-owned-terrain-generation"); yield break; }
            fallback++; Log(token, "after-fixed", "fallback", reason ?? "stock-deadline");
            yield return new WaitForSeconds(Mathf.Max(0, deadline - Time.time));
        }
        static void Log(Token token, string phase, string status, string reason)
        {
            Debug.Log("[Continuum site terrain readiness] serial=" + (token.Context == null ? 0 : token.Context.Serial) + " phase=" + phase + " status=" + status + " reason=" + Uri.EscapeDataString(reason) + " scope=original-force-epoch-static-city2-site-plus-owned-terrain experimental=True");
            if (phase == "force-return" && token.Points != null) LogPoints(token, phase, token.Points);
        }
        static void LogPoints(Token token, string phase, Point[] points)
        {
            for (int i = 0; i < points.Length; i++)
            {
                Point p = points[i];
                Debug.Log("[Continuum site terrain generation owner] serial=" + token.Context.Serial + " phase=" + phase + " point=" + i +
                    " sphere=" + token.Context.Sphere.GetInstanceID() + " body=" + token.Body.GetInstanceID() + " city=" + token.City.GetInstanceID() + " temporary=" + token.Context.Temporary.GetInstanceID() +
                    " actualCollider=" + p.Actual.GetInstanceID() + " ownedTerrainCollider=" + p.Terrain.GetInstanceID() + " quad=" + p.Quad.GetInstanceID() +
                    " actualMesh=" + p.ActualMesh.GetInstanceID() + " terrainMesh=" + p.TerrainMesh.GetInstanceID() + " actualGeometrySha256=" + p.ActualDigest + " terrainGeometrySha256=" + p.TerrainDigest);
            }
        }
        internal static void ObserveMenu()
        { Debug.Log("[Continuum site terrain readiness] phase=menu requested=" + requested + " admitted=" + admitted + " completed=" + completed + " fallback=" + fallback + " pending=" + pending + " cleanup=owned-iterator-completion-no-native-mutations"); }
    }
}
