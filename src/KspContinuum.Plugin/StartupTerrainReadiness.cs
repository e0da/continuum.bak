using System;
using System.Collections;
using UnityEngine;

namespace KspContinuum
{
    internal static class StartupTerrainReadiness
    {
        const int TerrainLayer = 15;
        internal sealed class Result
        {
            public string Site, Body, Decision, Reason;
            public int Polls, Rays, FixedBarriers;
            public float ElapsedSeconds;
            public bool ColliderReady;
            public bool StockEpoch, OwnerTargetUnchanged;
            public string OwnerTargetKind;
            public int OwnerTargetId, TemporaryTargetId;
        }

        public static IEnumerator Create(PQS sphere, Transform target, LaunchSite site, float seconds, Action<Result> completed)
        {
            return new Wait(sphere, target, site, seconds, completed);
        }
        public static object CreateAfterStockForce(PQS sphere, Transform temporary, Transform stockReturnedTarget, LaunchSite site, float seconds, Action<Result> completed)
        {
            var wait = new Wait(sphere, temporary, site, seconds, completed, stockReturnedTarget, true);
            if (wait.Prepare()) return wait;
            return new WaitForSeconds(seconds);
        }

        sealed class Wait : IEnumerator, IDisposable
        {
            readonly PQS sphere;
            readonly Transform target;
            readonly Transform ownerTarget;
            readonly bool stockEpoch;
            readonly LaunchSite site;
            readonly float seconds, started, deadline;
            readonly Action<Result> completed;
            readonly RaycastHit[] hits = new RaycastHit[16];
            readonly Result result;
            int phase, minimumLevel;
            bool finished;
            bool preAdmitted;
            object current;
            public object Current { get { return current; } }

            public Wait(PQS sphere, Transform target, LaunchSite site, float seconds, Action<Result> completed, Transform stockReturnedTarget = null, bool stockEpoch = false)
            {
                this.sphere = sphere; this.target = target; this.site = site; this.seconds = seconds;
                this.stockEpoch = stockEpoch; ownerTarget = stockEpoch ? stockReturnedTarget : target;
                this.completed = completed; started = Time.time; deadline = started + seconds;
                result = new Result { Site = site == null ? "unknown" : site.launchSiteName, Body = "unknown", StockEpoch = stockEpoch,
                    OwnerTargetId = ownerTarget == null ? 0 : ownerTarget.GetInstanceID(), TemporaryTargetId = target == null ? 0 : target.GetInstanceID(), OwnerTargetKind = "temporary" };
            }
            public bool Prepare()
            {
                string reason;
                try { preAdmitted = Supported(out reason); }
                catch (Exception error) { preAdmitted = false; reason = "support-error-" + error.GetType().Name; }
                if (!preAdmitted) { result.Reason = reason; Finish(false, "stock-wait-scheduled"); }
                return preAdmitted;
            }

            public bool MoveNext()
            {
                if (finished) return false;
                if (phase == 0)
                {
                    string reason;
                    bool supported;
                    try { reason = "pre-admitted"; supported = preAdmitted || Supported(out reason); }
                    catch (Exception error) { supported = false; reason = "support-error-" + error.GetType().Name; }
                    if (!supported)
                    {
                        result.Reason = reason; phase = 2; current = new WaitForSeconds(Mathf.Max(0, deadline - Time.time)); return true;
                    }
                    phase = 1; result.FixedBarriers = 1;
                    current = new WaitForFixedUpdate(); return true;
                }
                if (phase == 2) { Finish(false, "stock-wait"); return false; }
                if (Time.time >= deadline) { result.Reason = "stock-deadline"; Finish(false, "stock-deadline"); return false; }
                string detail;
                bool ready;
                try { ready = Probe(out detail); }
                catch (Exception error) { ready = false; detail = "probe-error-" + error.GetType().Name; }
                result.Polls++; result.Reason = detail;
                if (ready) { Finish(true, "terrain-collider-ready"); return false; }
                current = null; return true;
            }

            bool Supported(out string reason)
            {
                reason = "unsupported-site";
                if (sphere == null || target == null || site == null || seconds <= 0 || float.IsNaN(seconds) || float.IsInfinity(seconds)) return false;
                if (stockEpoch)
                {
                    if (ownerTarget == null || sphere.target != ownerTarget) { reason = "stock-returned-target-unavailable-or-changed"; return false; }
                    Camera main = Camera.main;
                    if (ownerTarget == target) result.OwnerTargetKind = "temporary";
                    else if (main != null && ownerTarget == main.transform) result.OwnerTargetKind = "stock-main-camera";
                    else { result.OwnerTargetKind = "unsupported"; reason = "stock-returned-target-not-pinned-startup-policy"; return false; }
                    if (sphere.GetType() != typeof(PQS)) { reason = "custom-sphere-stock-wait"; return false; }
                    foreach (PQSMod mod in sphere.GetComponentsInChildren<PQSMod>(true))
                        if (mod != null && ReferenceEquals(mod.sphere, sphere) && mod.GetType().Assembly != typeof(PQS).Assembly)
                        { reason = "custom-terrain-mod-stock-wait"; return false; }
                }
                if (site.positionMobileLaunchPad != null) { reason = "mobile-footprint-stock-wait"; return false; }
                bool city = site.pqsCity != null && site.pqsCity.GetType() == typeof(PQSCity) && ReferenceEquals(site.pqsCity.sphere, sphere);
                bool city2 = site.pqsCity2 != null && site.pqsCity2.GetType() == typeof(PQSCity2) && ReferenceEquals(site.pqsCity2.sphere, sphere);
                if (!city && !city2) return false;
                if (stockEpoch && city && site.pqsCity.randomizeOnSphere) { reason = "randomized-placement-stock-wait"; return false; }
                if (city2 && site.pqsCity2.setOnWaterSurface) { reason = "water-placement-stock-wait"; return false; }
                if (sphere.PQSModCBTransform == null || sphere.PQSModCBTransform.body == null ||
                    !ReferenceEquals(sphere.PQSModCBTransform.body.pqsController, sphere)) { reason = "body-owner-unresolved"; return false; }
                result.Body = sphere.PQSModCBTransform.body.bodyName;
                Vector3 scale = sphere.transform.lossyScale;
                if ((scale - Vector3.one).sqrMagnitude > 1e-8f) { reason = "nonunit-sphere-scale"; return false; }
                var colliderMods = sphere.GetComponentsInChildren<PQSMod_QuadMeshColliders>(true);
                bool found = false; minimumLevel = int.MaxValue;
                foreach (var mod in colliderMods)
                {
                    if (!ReferenceEquals(mod.sphere, sphere)) continue;
                    if (mod.GetType() != typeof(PQSMod_QuadMeshColliders)) { reason = "custom-terrain-collider-policy"; return false; }
                    minimumLevel = Math.Min(minimumLevel, sphere.maxLevel - Mathf.Abs(mod.maxLevelOffset)); found = true;
                }
                if (!found) { reason = "terrain-collider-mod-unresolved"; return false; }
                return true;
            }

            bool Probe(out string reason)
            {
                reason = "sphere-target-not-ready";
                result.OwnerTargetUnchanged = sphere != null && ownerTarget != null && sphere.target == ownerTarget;
                if (sphere == null || target == null || !sphere.isStarted || !sphere.isAlive || sphere.isThinking || sphere.isDisabled || !result.OwnerTargetUnchanged) return false;
                if (!Ray(target.position, out reason)) return false;
                if (site.pqsCity2 != null)
                {
                    Transform positioning = site.pqsCity2.PositioningPoint;
                    if (positioning == null) { reason = "positioning-point-unavailable"; return false; }
                    if ((positioning.position - target.position).sqrMagnitude > 1000000f) { reason = "positioning-point-outside-bounded-site"; return false; }
                    if ((positioning.position - target.position).sqrMagnitude > .0025f && !Ray(positioning.position, out reason)) return false;
                }
                reason = "intended-pqs-mesh-collider-after-fixed-barrier"; return true;
            }

            bool Ray(Vector3 point, out string reason)
            {
                reason = "terrain-ray-unresolved";
                Vector3d center = sphere.PrecisePosition;
                Vector3d radial = (Vector3d)point - center;
                double radius = radial.magnitude;
                double outside = Math.Max(radius + 500.0, sphere.radiusMax + 500.0);
                double distance = outside - sphere.radiusMin + 1000.0;
                if (double.IsNaN(outside) || double.IsInfinity(outside) || radius <= 0 || distance <= 0 || distance > 200000.0) { reason = "radial-range-outside-bound"; return false; }
                Vector3 direction = (Vector3)(radial / radius);
                Vector3 origin = (Vector3)(center + radial / radius * outside);
                int count = Physics.RaycastNonAlloc(origin, -direction, hits, (float)distance, 1 << TerrainLayer, QueryTriggerInteraction.Ignore);
                result.Rays++;
                if (count == 0) { reason = "no-terrain-layer-hit"; return false; }
                if (count == hits.Length) { reason = "terrain-hit-buffer-saturated"; return false; }
                int nearest = 0;
                for (int i = 1; i < count; i++) if (hits[i].distance < hits[nearest].distance) nearest = i;
                Collider collider = hits[nearest].collider;
                PQ quad = collider == null ? null : collider.GetComponent<PQ>();
                if (quad == null || !ReferenceEquals(quad.sphereRoot, sphere) || collider != quad.meshCollider ||
                    collider.gameObject.layer != TerrainLayer || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                    quad.meshCollider.sharedMesh == null || quad.meshCollider.sharedMesh != quad.mesh || quad.subdivision < minimumLevel)
                { reason = "nearest-hit-not-intended-terrain-collider"; return false; }
                return true;
            }

            void Finish(bool ready, string decision)
            {
                if (finished) return; finished = true;
                result.ColliderReady = ready; result.Decision = decision; result.ElapsedSeconds = Time.time - started;
                completed(result); current = null;
            }
            public void Dispose() { if (!finished) { result.Reason = "iterator-disposed"; Finish(false, "interrupted"); } }
            public void Reset() { throw new NotSupportedException(); }
        }
    }
}
