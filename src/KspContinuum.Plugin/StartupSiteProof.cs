using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace KspContinuum
{
    internal static class StartupSiteProof
    {
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-startup-site-proof/v1", runId, site, body, status, reason;
            public int processId, pendingBefore, pendingAfter, spawnPoints;
            public bool pendingObserved, requestedSitePendingBefore, requestedSitePendingAfter;
            public double localPositionToleranceMeters = 0.01, latitudeLongitudeToleranceDegrees = 1e-8, altitudeToleranceMeters = 0.01;
            public double getterSeconds, repeatGetterSeconds, totalSeconds, processCpuSeconds, worldPositionRepeatErrorMeters;
            public string invariant = "Independent radial altitude: norm(worldPosition-body.position)-body.Radius. Body-local coordinates use body transform basis; no collider/mobile-footprint qualification.";
            public Row[] rows;
        }
        [Serializable] internal sealed class Row
        {
            public string name;
            public double localX, localY, localZ, latitude, longitude, altitude, radialAltitude, radialAltitudeErrorMeters;
            public double repeatLocalPositionErrorMeters, repeatLatitudeErrorDegrees, repeatLongitudeErrorDegrees, repeatAltitudeErrorMeters;
            public bool latlonaltSet;
        }
        static string Argument(string prefix)
        {
            foreach (string arg in Environment.GetCommandLineArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg.Substring(prefix.Length);
            return null;
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static Vector3d Local(CelestialBody body, Vector3 position)
        {
            Vector3d relative = (Vector3d)position - body.position;
            return new Vector3d(Vector3d.Dot(relative, body.transform.right), Vector3d.Dot(relative, body.transform.up), Vector3d.Dot(relative, body.transform.forward));
        }
        static void Pending(LaunchSite site, out int count, out bool sitePending, out bool observed)
        {
            FieldInfo field = typeof(StartupDeferredTerrain).GetField("pending", BindingFlags.Static | BindingFlags.NonPublic);
            IDictionary obligations = field == null ? null : field.GetValue(null) as IDictionary;
            observed = obligations != null; count = obligations == null ? -1 : obligations.Count;
            sitePending = obligations != null && obligations.Contains(site);
        }
        internal static bool RunIfRequested()
        {
            string requested = Argument("--continuum-startup-site-proof=");
            if (requested == null) return false;
            var report = new Report { runId = Argument("--continuum-research-run="), site = requested, status = "preparing", processId = Process.GetCurrentProcess().Id };
            string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "startup-site-proof-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            Stopwatch total = Stopwatch.StartNew();
            var rows = new List<Row>();
            using (Process process = Process.GetCurrentProcess())
            {
                TimeSpan cpuStart = process.TotalProcessorTime;
                try
                {
                    if (requested != "Desert_Airfield" || string.IsNullOrEmpty(report.runId) || HighLogic.LoadedScene != GameScenes.MAINMENU ||
                        !File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json")))
                        throw new InvalidOperationException("Bounded private menu site proof requires Desert_Airfield and run identity.");
                    LaunchSite site = PSystemSetup.Instance.GetLaunchSite(requested);
                    if (site == null || !site.IsSetup || site.spawnPoints == null || site.spawnPoints.Length == 0) throw new InvalidOperationException("Stock site/spawn registration unavailable.");
                    Pending(site, out report.pendingBefore, out report.requestedSitePendingBefore, out report.pendingObserved);
                    Stopwatch getter = Stopwatch.StartNew();
                    Transform world = site.GetWorldPos();
                    getter.Stop(); report.getterSeconds = getter.Elapsed.TotalSeconds;
                    if (world == null || site.Body == null) throw new InvalidOperationException("Stock world-position getter failed.");
                    CelestialBody body = site.Body; report.body = body.bodyName;
                    Vector3d initialWorld = Local(body, world.position);
                    site.SetSpawnPointsLatLonAlt();
                    var positions = new List<Vector3d>();
                    foreach (LaunchSite.SpawnPoint spawn in site.spawnPoints)
                    {
                        Transform transform = spawn.GetSpawnPointTransform();
                        if (transform == null) throw new InvalidOperationException("Spawn transform absent.");
                        Vector3d local = Local(body, transform.position); positions.Add(local);
                        double latitude, longitude, altitude; spawn.GetSpawnPointLatLonAlt(out latitude, out longitude, out altitude);
                        double radial = ((Vector3d)transform.position - body.position).magnitude - body.Radius;
                        var row = new Row { name = spawn.name, localX = local.x, localY = local.y, localZ = local.z, latitude = latitude, longitude = longitude,
                            altitude = altitude, radialAltitude = radial, radialAltitudeErrorMeters = Math.Abs(radial - altitude), latlonaltSet = spawn.latlonaltSet };
                        if (!Finite(local.x) || !Finite(local.y) || !Finite(local.z) || !Finite(latitude) || !Finite(longitude) || !Finite(altitude) ||
                            !Finite(radial) || Math.Abs(latitude) > 90 || !row.latlonaltSet || row.radialAltitudeErrorMeters > report.altitudeToleranceMeters)
                            throw new InvalidOperationException("Nonfinite or inconsistent spawn geometry: " + spawn.name);
                        rows.Add(row);
                    }
                    getter.Restart(); Transform repeated = site.GetWorldPos(); getter.Stop(); report.repeatGetterSeconds = getter.Elapsed.TotalSeconds;
                    if (repeated != world) throw new InvalidOperationException("World transform identity changed on repeated read.");
                    report.worldPositionRepeatErrorMeters = (Local(body, repeated.position) - initialWorld).magnitude;
                    site.SetSpawnPointsLatLonAlt();
                    for (int i = 0; i < site.spawnPoints.Length; i++)
                    {
                        LaunchSite.SpawnPoint spawn = site.spawnPoints[i]; Row row = rows[i];
                        double latitude, longitude, altitude; spawn.GetSpawnPointLatLonAlt(out latitude, out longitude, out altitude);
                        row.repeatLocalPositionErrorMeters = (Local(body, spawn.GetSpawnPointTransform().position) - positions[i]).magnitude;
                        row.repeatLatitudeErrorDegrees = Math.Abs(latitude - row.latitude); row.repeatLongitudeErrorDegrees = Math.Abs(longitude - row.longitude); row.repeatAltitudeErrorMeters = Math.Abs(altitude - row.altitude);
                        if (!Finite(row.repeatLocalPositionErrorMeters) || !Finite(latitude) || !Finite(longitude) || !Finite(altitude) ||
                            row.repeatLocalPositionErrorMeters > report.localPositionToleranceMeters || row.repeatLatitudeErrorDegrees > report.latitudeLongitudeToleranceDegrees ||
                            row.repeatLongitudeErrorDegrees > report.latitudeLongitudeToleranceDegrees || row.repeatAltitudeErrorMeters > report.altitudeToleranceMeters)
                            throw new InvalidOperationException("Repeated spawn read changed: " + spawn.name);
                    }
                    bool observed; Pending(site, out report.pendingAfter, out report.requestedSitePendingAfter, out observed);
                    if (report.worldPositionRepeatErrorMeters > report.localPositionToleranceMeters || !Finite(report.worldPositionRepeatErrorMeters) ||
                        (report.requestedSitePendingBefore && (!observed || report.requestedSitePendingAfter)))
                        throw new InvalidOperationException("Getter failed idempotence or left requested deferred debt pending.");
                    report.spawnPoints = rows.Count; report.status = "complete-site-proof";
                }
                catch (Exception error) { report.status = "invalid"; report.reason = error.ToString(); throw; }
                finally
                {
                    report.totalSeconds = total.Elapsed.TotalSeconds;
                    report.rows = rows.ToArray();
                    report.processCpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
                    File.WriteAllText(path, ReportJson.Encode(report));
                    UnityEngine.Debug.Log("[Continuum startup site proof] status=" + report.status + " site=" + requested + " pendingBefore=" + report.pendingBefore + " pendingAfter=" + report.pendingAfter + " qualification=site-geometry-not-flight-or-footprint-equivalence");
                }
            }
            return true;
        }
    }
}
