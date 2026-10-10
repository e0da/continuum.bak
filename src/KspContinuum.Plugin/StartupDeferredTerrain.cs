using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KspContinuum
{
    internal static class StartupDeferredTerrain
    {
        const string Owner = "continuum.startup-deferred-terrain";
        const string Marker = "[Continuum deferred terrain] ";
        sealed class Obligation
        {
            internal PSystemSetup Setup;
            internal PQS Sphere;
            internal LaunchSite Site;
            internal Vector3 LocalPosition;
            internal bool Completing;
            internal bool Publishing;
        }
        static readonly Dictionary<LaunchSite, Obligation> pending = new Dictionary<LaunchSite, Obligation>();
        static readonly HashSet<PQS> fulfilling = new HashSet<PQS>();
        static Action<PSystemSetup, PQS, Transform> force;
        static Func<bool> registering;
        static Harmony harmony;
        static FieldInfo spawnHost;
        static int deferred, materialized, failures, unsupported;
        static long captureTicks, completionTicks;
        public struct PublicationState { public LaunchSite Site; public bool Previous; }

        internal static void Install(Action<PSystemSetup, PQS, Transform> original, Func<bool> inRegistration)
        {
            if (!Environment.GetCommandLineArgs().Contains("--continuum-startup-deferred-terrain")) return;
            var methods = new[] {
                AccessTools.DeclaredMethod(typeof(PQSCity), "Orientate", new[] { typeof(bool) }),
                AccessTools.DeclaredMethod(typeof(PQSCity2), "Orientate", Type.EmptyTypes),
                AccessTools.DeclaredMethod(typeof(LaunchSite), "GetWorldPos", Type.EmptyTypes),
                AccessTools.DeclaredMethod(typeof(LaunchSite), "GetSpawnPoint", new[] { typeof(string) }),
                AccessTools.DeclaredMethod(typeof(LaunchSite), "SetSpawnPointsLatLonAlt", Type.EmptyTypes),
                AccessTools.DeclaredMethod(typeof(PSystemSetup), "SetPQSActive", new[] { typeof(PQS) }) };
            MethodInfo[] spawnMethods = new[] { AccessTools.DeclaredMethod(typeof(LaunchSite.SpawnPoint), "GetSpawnPointTransform", Type.EmptyTypes),
                AccessTools.DeclaredMethod(typeof(LaunchSite.SpawnPoint), "GetSpawnPointLatLonAlt", new[] { typeof(double).MakeByRefType(), typeof(double).MakeByRefType(), typeof(double).MakeByRefType() }),
                AccessTools.DeclaredMethod(typeof(LaunchSite.SpawnPoint), "SetSpawnPointLatLonAlt", Type.EmptyTypes) };
            spawnHost = AccessTools.DeclaredField(typeof(LaunchSite.SpawnPoint), "host");
            if (spawnHost?.FieldType != typeof(LaunchSite) || methods.Concat(spawnMethods).Any(m => m == null || m.Module != typeof(PSystemSetup).Module || Harmony.GetPatchInfo(m)?.Owners.Count > 0))
                throw new InvalidOperationException("Deferred terrain consumers differ or already patched.");
            force = original; registering = inRegistration; harmony = new Harmony(Owner);
            try
            {
                foreach (MethodInfo method in methods.Take(2)) harmony.Patch(method, prefix: new HarmonyMethod(typeof(StartupDeferredTerrain), nameof(OrientPrefix)));
                foreach (MethodInfo method in methods.Skip(2).Take(2)) harmony.Patch(method, prefix: new HarmonyMethod(typeof(StartupDeferredTerrain), nameof(SitePrefix)));
                harmony.Patch(methods[4], prefix: new HarmonyMethod(typeof(StartupDeferredTerrain), nameof(PublicationPrefix)),
                    finalizer: new HarmonyMethod(typeof(StartupDeferredTerrain), nameof(PublicationFinalizer)));
                harmony.Patch(methods[5], prefix: new HarmonyMethod(typeof(StartupDeferredTerrain), nameof(ActivatePrefix)));
                foreach (MethodInfo method in spawnMethods) harmony.Patch(method, prefix: new HarmonyMethod(typeof(StartupDeferredTerrain), nameof(SpawnPrefix)));
                Debug.Log(Marker + "status=installed supported=analytic-static-stock-sites unsupported=mobile-water-raycast-randomized-custom-sites ownership=single-pending-obligation-per-site");
            }
            catch { harmony.UnpatchAll(Owner); harmony = null; throw; }
        }

        internal static bool Defer(PSystemSetup setup, PQS sphere, Transform target, LaunchSite site)
        {
            if (harmony == null) return false;
            long start = Stopwatch.GetTimestamp();
            try
            {
                bool supported = site != null && site.IsSetup && site.positionMobileLaunchPad == null && sphere != null && target != null &&
                    sphere.parentSphere == null && site.launchsitePQS == sphere &&
                    ((site.pqsCity != null && site.pqsCity.GetType() == typeof(PQSCity) && site.pqsCity.sphere == sphere && !site.pqsCity.randomizeOnSphere) ||
                    (site.pqsCity2 != null && site.pqsCity2.GetType() == typeof(PQSCity2) && site.pqsCity2.sphere == sphere && !site.pqsCity2.raycastSurface && !site.pqsCity2.setOnWaterSurface));
                if (!supported) { unsupported++; return false; }
                if (pending.ContainsKey(site)) throw new InvalidOperationException("Duplicate deferred site obligation.");
                pending.Add(site, new Obligation { Setup = setup, Sphere = sphere, Site = site, LocalPosition = target.localPosition });
                deferred++;
                Debug.Log(Marker + "site=" + site.name + " status=deferred obligation=stock-force-postsetup-and-placement geometry=not-built-by-this-call");
                return true;
            }
            finally { captureTicks += Stopwatch.GetTimestamp() - start; }
        }

        internal static bool IsPending(LaunchSite site) { return site != null && pending.ContainsKey(site); }
        internal static void BeforeStockForce(PQS sphere)
        {
            foreach (Obligation obligation in pending.Values.Where(o => o.Sphere == sphere).ToArray()) Complete(obligation, "unsupported-site-shared-sphere-force");
        }
        static bool OrientPrefix(object __instance)
        {
            Obligation obligation = pending.Values.FirstOrDefault(o => ReferenceEquals(o.Site.pqsCity, __instance) || ReferenceEquals(o.Site.pqsCity2, __instance));
            if (obligation == null || fulfilling.Contains(obligation.Sphere)) return true;
            if (registering()) return false;
            Complete(obligation, "orientation-consumer");
            return true;
        }
        static void SitePrefix(LaunchSite __instance)
        {
            Obligation obligation;
            if (!registering() && pending.TryGetValue(__instance, out obligation))
            {
                if (obligation.Completing && !obligation.Publishing)
                    throw new InvalidOperationException("Deferred site consumed before shared-sphere placement publication: " + __instance.name);
                if (!obligation.Publishing) Complete(obligation, "site-geometry-consumer");
            }
        }
        static void SpawnPrefix(LaunchSite.SpawnPoint __instance)
        {
            LaunchSite site = spawnHost.GetValue(__instance) as LaunchSite;
            if (site != null) SitePrefix(site);
        }
        static void PublicationPrefix(LaunchSite __instance, out PublicationState __state)
        {
            __state = default(PublicationState);
            Obligation obligation;
            if (pending.TryGetValue(__instance, out obligation) && obligation.Completing)
            {
                __state.Site = __instance; __state.Previous = obligation.Publishing;
                obligation.Publishing = true;
            }
            else SitePrefix(__instance);
        }
        static Exception PublicationFinalizer(PublicationState __state, Exception __exception)
        {
            Obligation obligation;
            if (__state.Site != null && pending.TryGetValue(__state.Site, out obligation)) obligation.Publishing = __state.Previous;
            return __exception;
        }
        static void ActivatePrefix(PQS pqs)
        {
            foreach (Obligation obligation in pending.Values.Where(o => o.Sphere == pqs).ToArray()) Complete(obligation, "pqs-activation");
        }
        static void Complete(Obligation obligation, string consumer)
        {
            if (fulfilling.Contains(obligation.Sphere)) return;
            Obligation[] batch = pending.Values.Where(o => o.Sphere == obligation.Sphere).ToArray();
            if (batch.Length == 0) return;
            fulfilling.Add(obligation.Sphere);
            foreach (Obligation item in batch) item.Completing = true;
            long start = Stopwatch.GetTimestamp();
            GameObject temporary = null;
            Transform previous = null;
            bool capturedTarget = false, failed = false;
            try
            {
                foreach (Obligation item in batch)
                    if (item.Setup == null || item.Setup != obligation.Setup || item.Sphere == null || item.Site == null || !item.Site.IsSetup ||
                        (item.Site.pqsCity == null && item.Site.pqsCity2 == null))
                        throw new InvalidOperationException("Deferred site lost its original owners.");
                previous = obligation.Sphere.target; capturedTarget = true;
                temporary = new GameObject("ContinuumDeferredSite_" + obligation.Site.name);
                temporary.transform.SetParent(obligation.Sphere.transform);
                temporary.transform.localPosition = obligation.LocalPosition;
                obligation.Sphere.SetTarget(temporary.transform);
                force(obligation.Setup, obligation.Sphere, temporary.transform);
                foreach (Obligation item in batch)
                {
                    temporary.transform.localPosition = item.LocalPosition;
                    item.Sphere.SetTarget(temporary.transform);
                    if (item.Site.pqsCity != null) item.Site.pqsCity.Orientate(); else item.Site.pqsCity2.Orientate();
                    item.Publishing = true;
                    try { item.Site.SetSpawnPointsLatLonAlt(); }
                    finally { item.Publishing = false; }
                }
                foreach (Obligation item in batch)
                {
                    pending.Remove(item.Site); materialized++;
                    Debug.Log(Marker + "site=" + item.Site.name + " status=materialized consumer=" + consumer + " forceScope=one-per-shared-sphere qualification=analytic-placement-no-mobile-footprint-proof");
                }
            }
            catch (Exception error)
            {
                failures++; failed = true;
                Debug.LogError(Marker + "site=" + (obligation.Site == null ? "unknown" : obligation.Site.name) + " status=failed consumer=" + consumer + " reason=" + error.Message);
                Application.Quit(2);
                throw;
            }
            finally
            {
                completionTicks += Stopwatch.GetTimestamp() - start;
                try
                {
                    if (capturedTarget)
                    {
                        if (obligation.Sphere == null) throw new InvalidOperationException("Deferred sphere destroyed before target restoration.");
                        obligation.Sphere.SetTarget(previous);
                    }
                }
                catch (Exception error)
                {
                    failures++;
                    Debug.LogError(Marker + "status=cleanup-failed reason=" + error.Message);
                    Application.Quit(2);
                    if (!failed) throw;
                }
                finally
                {
                    foreach (Obligation item in batch) { item.Completing = false; item.Publishing = false; }
                    fulfilling.Remove(obligation.Sphere);
                    if (temporary != null) UnityEngine.Object.Destroy(temporary); Observe("fulfillment");
                }
            }
        }
        internal static void Observe(string phase)
        {
            if (harmony == null) return;
            Debug.Log(Marker + "status=journal phase=" + phase + " deferred=" + deferred + " materialized=" + materialized + " pending=" + pending.Count +
                " failures=" + failures + " unsupported=" + unsupported + " captureSeconds=" + (captureTicks / (double)Stopwatch.Frequency).ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " fulfillmentSeconds=" + (completionTicks / (double)Stopwatch.Frequency).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " menuPending=explicit-deferred-debt");
        }
        internal static void AbortInstallation()
        {
            harmony?.UnpatchAll(Owner); harmony = null;
            pending.Clear();
            fulfilling.Clear();
        }
        internal static void Shutdown(bool processQuitting)
        {
            if (harmony == null) return;
            Observe("shutdown");
            int discarded = pending.Count;
            if (!processQuitting && discarded != 0)
            {
                failures++;
                Debug.LogError(Marker + "status=owner-destroyed pending=" + discarded + " policy=fail-closed-process-exit debt=not-materialized");
                Application.Quit(2);
            }
            Debug.Log(Marker + "status=" + (processQuitting ? "process-shutdown" : "owner-cleanup") + " discardedPending=" + discarded + " materializedDuringShutdown=0");
            AbortInstallation();
        }
    }
}
