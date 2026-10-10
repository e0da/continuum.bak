using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace KspContinuum
{
    internal static class AtmosphericEntryPreparation
    {
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-entry/v1", runId, vesselId, sourceSha256, status, phase, reason;
            public string recipe = "Original checkpoint active proto-vessel orbit/altitude/latitude/longitude changed in memory only; original parts, modules, rotation and controller retained. Stock warp and atmospheric simulation execute the approach.";
            public int processId, originalParts, actualParts, topologyEvents;
            public double targetAltitudeMeters, initialAltitudeMeters = 90000, apoapsisMeters = 134500, periapsisMeters = -20000;
            public double semiMajorAxis, eccentricity, inclinationDegrees, lanDegrees, argumentOfPeriapsisDegrees, meanAnomalyRadians, epoch;
            public double preparedLatitude, preparedLongitude, preparedRadialSpeedMetersPerSecond;
            public double preparationUniversalTime, epochBodyRotationCorrectionDegrees;
            public bool bodyInverseRotation;
            public double bodyRotationPeriodSeconds, bodyInitialRotationDegrees, currentInverseRotationDegrees;
            public double rawCurrentFrameLatitude, rawCurrentFrameLongitude, expectedEpochLatitude, expectedEpochLongitude;
            public double latitudeProofErrorDegrees, longitudeProofErrorDegrees;
            public double originalSavedLatitude, originalSavedLongitude, originalSavedAltitudeMeters;
            public double preparedRadiusErrorMeters;
            public string coordinateConvention = "Orbit epoch ZupAtT -> inertial -> epoch body-fixed -> current BodyFrame virtual radial for GetLatitude/GetLongitude; no duplicate body-rotation correction";
            public double admittedAltitudeMeters, admittedUniversalTime, approachWallSeconds;
            public string cleanupStatus = "no-owned-warp-yet";
            public Sample[] samples;
        }
        [Serializable] internal sealed class Sample
        {
            public string phase, warpMode, situation;
            public double utcUniversalTime, wallSeconds, altitudeMeters, verticalSpeedMetersPerSecond, surfaceSpeedMetersPerSecond;
            public double density, dynamicPressureKPa, warpRate, maxPartTemperatureKelvin, maxSkinTemperatureKelvin;
            public int parts;
            public bool packed;
        }
        static readonly MethodInfo ModeSetter = typeof(TimeWarp).GetMethod("setMode", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(TimeWarp.Modes) }, null);
        static readonly List<Sample> samples = new List<Sample>();
        static readonly HashSet<uint> identities = new HashSet<uint>();
        sealed class Listener
        {
            public void Modified(Vessel vessel) { AtmosphericEntryPreparation.Modified(vessel); }
            public void Died(Part part) { AtmosphericEntryPreparation.Died(part); }
        }
        static readonly Listener listener = new Listener();
        static Report report;
        static string loaderDirectory, flightDirectory;
        static double startedWall, lastSampleWall = -1;
        static bool modifiedHook, diedHook, ownedWarp, topologyDirty;
        static int normalFrames;
        public static bool Requested { get { return report != null; } }
        public static bool Admitted { get { return report != null && report.status == "capture-admitted"; } }

        public static void Prepare(Game game, string directory, string[] args)
        {
            double target = 0; bool found = false;
            foreach (string arg in args) if (arg.StartsWith("--continuum-flight-entry-target=", StringComparison.Ordinal))
            {
                if (found || !double.TryParse(arg.Substring("--continuum-flight-entry-target=".Length), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out target) || double.IsNaN(target) || double.IsInfinity(target) || target < 1000 || target > 60000)
                    throw new ArgumentException("Invalid entry target altitude.");
                found = true;
            }
            if (!found) return;
            ProtoVessel proto = game.flightState.protoVessels[game.flightState.activeVesselIdx];
            OrbitSnapshot original = proto.orbitSnapShot;
            if (original == null || original.ReferenceBodyIndex < 0 || original.ReferenceBodyIndex >= FlightGlobals.Bodies.Count)
                throw new InvalidOperationException("Entry source orbit unavailable.");
            CelestialBody body = FlightGlobals.Bodies[original.ReferenceBodyIndex];
            if (body.bodyName != "Kerbin" || proto.landed || proto.splashed || body.Radius <= 20000)
                throw new InvalidOperationException("Entry recipe requires original orbital Kerbin vessel.");
            report = new Report { targetAltitudeMeters = target, vesselId = proto.vesselID.ToString("D"),
                sourceSha256 = ScaleCheckpointLoadState.SourceSha256, processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                originalParts = proto.protoPartSnapshots.Count, actualParts = proto.protoPartSnapshots.Count, status = "prepared", phase = "prepared-orbit",
                originalSavedLatitude = proto.latitude, originalSavedLongitude = proto.longitude, originalSavedAltitudeMeters = proto.altitude };
            loaderDirectory = directory;
            foreach (string arg in args) if (arg.StartsWith("--continuum-research-run=", StringComparison.Ordinal)) report.runId = arg.Substring("--continuum-research-run=".Length);
            identities.Clear(); samples.Clear(); foreach (ProtoPartSnapshot part in proto.protoPartSnapshots) identities.Add(part.flightID);
            if (identities.Count != report.originalParts) throw new InvalidOperationException("Source part identities are not unique.");
            double apo = body.Radius + report.apoapsisMeters, peri = body.Radius + report.periapsisMeters;
            double a = (apo + peri) / 2, e = (apo - peri) / (apo + peri), radius = body.Radius + report.initialAltitudeMeters;
            double anomaly = 2 * Math.PI - Math.Acos((1 - radius / a) / e);
            double mean = anomaly - e * Math.Sin(anomaly), epoch = game.flightState.universalTime;
            var orbit = new Orbit(original.inclination, e, a, original.LAN, original.argOfPeriapsis, mean, epoch, body);
            Vector3d position, velocity; orbit.GetOrbitalStateVectorsAtUT(epoch, out position, out velocity);
            double radial = Vector3d.Dot(position, velocity) / position.magnitude;
            report.semiMajorAxis = a; report.eccentricity = e; report.meanAnomalyRadians = mean; report.epoch = epoch;
            report.preparedRadialSpeedMetersPerSecond = radial; report.preparedRadiusErrorMeters = position.magnitude - radius;
            if (Math.Abs(report.preparedRadiusErrorMeters) > .01 || radial >= 0)
            {
                report.status = "invalid"; report.reason = "descending-orbit-proof-failed"; Write();
                throw new InvalidOperationException("Descending entry orbit proof failed.");
            }
            report.preparationUniversalTime = Planetarium.GetUniversalTime();
            report.bodyInverseRotation = body.inverseRotation; report.bodyRotationPeriodSeconds = body.rotationPeriod;
            report.bodyInitialRotationDegrees = body.initialRotation; report.currentInverseRotationDegrees = Planetarium.InverseRotAngle;
            report.epochBodyRotationCorrectionDegrees = body.rotationPeriod == 0 ? 0 :
                360 * (epoch - report.preparationUniversalTime) / body.rotationPeriod;
            report.rawCurrentFrameLatitude = body.GetLatitude(position.xzy, true);
            report.rawCurrentFrameLongitude = body.GetLongitude(position.xzy, true);
            var epochZup = new Planetarium.CelestialFrame();
            Planetarium.ZupAtT(epoch, body, ref epochZup);
            var epochFrame = new Planetarium.CelestialFrame();
            Planetarium.CelestialFrame.PlanetaryFrame(0, 90, body.initialRotation +
                (body.rotationPeriod == 0 ? 0 : 360 * epoch / body.rotationPeriod), ref epochFrame);
            Vector3d epochLocal = epochFrame.WorldToLocal(epochZup.LocalToWorld(position)).normalized;
            double expectedLatitude = Math.Asin(epochLocal.z) * 180 / Math.PI;
            double expectedLongitude = Math.Atan2(epochLocal.y, epochLocal.x) * 180 / Math.PI;
            Vector3d apiRelative = body.BodyFrame.LocalToWorld(epochLocal).xzy;
            double latitude = body.GetLatitude(apiRelative, true), longitude = body.GetLongitude(apiRelative, true);
            report.expectedEpochLatitude = expectedLatitude; report.expectedEpochLongitude = expectedLongitude;
            report.preparedLatitude = latitude; report.preparedLongitude = longitude;
            report.latitudeProofErrorDegrees = latitude - expectedLatitude;
            report.longitudeProofErrorDegrees = ((longitude - expectedLongitude + 180) % 360 + 360) % 360 - 180;
            report.semiMajorAxis = a; report.eccentricity = e; report.meanAnomalyRadians = mean; report.epoch = epoch;
            report.preparedRadialSpeedMetersPerSecond = radial;
            Write();
            if (Math.Abs(report.latitudeProofErrorDegrees) > .0001 || Math.Abs(report.longitudeProofErrorDegrees) > .0001)
            {
                report.status = "invalid"; report.reason = "epoch-coordinate-proof-failed"; Write();
                throw new InvalidOperationException("Epoch surface-coordinate alignment proof failed.");
            }
            proto.orbitSnapShot = new OrbitSnapshot(orbit);
            proto.altitude = position.magnitude - body.Radius; proto.latitude = latitude; proto.longitude = longitude;
            proto.situation = Vessel.Situations.ORBITING;
            report.semiMajorAxis = a; report.eccentricity = e; report.meanAnomalyRadians = mean; report.epoch = epoch;
            report.inclinationDegrees = original.inclination; report.lanDegrees = original.LAN; report.argumentOfPeriapsisDegrees = original.argOfPeriapsis;
            report.preparedLatitude = proto.latitude; report.preparedLongitude = proto.longitude; report.preparedRadialSpeedMetersPerSecond = radial;
            loaderDirectory = directory; Write();
        }
        public static void Begin(string directory)
        {
            if (!Requested) return;
            flightDirectory = directory; startedWall = Time.realtimeSinceStartup; report.status = "approaching"; report.phase = "waiting-for-flight";
            Write();
            GameEvents.onVesselWasModified.Add(listener.Modified); modifiedHook = true;
            GameEvents.onPartDie.Add(listener.Died); diedHook = true;
        }
        static void Modified(Vessel vessel)
        {
            if (vessel != null && vessel.id.ToString("D") == report.vesselId) { report.topologyEvents++; topologyDirty = true; }
        }
        static void Died(Part part)
        {
            if (part != null && identities.Contains(part.flightID)) { report.topologyEvents++; topologyDirty = true; }
        }
        static bool SameParts(Vessel vessel)
        {
            if (vessel.parts.Count != identities.Count) return false;
            foreach (Part part in vessel.parts) if (part == null || !identities.Contains(part.flightID)) return false;
            return true;
        }
        static void Warp(TimeWarp.Modes mode, float maximum)
        {
            if (TimeWarp.fetch == null || ModeSetter == null) throw new InvalidOperationException("Stock warp unavailable.");
            ownedWarp = true; report.cleanupStatus = "owned-stock-warp";
            if (TimeWarp.WarpMode != mode)
            {
                TimeWarp.SetRate(0, true);
                if (!(bool)ModeSetter.Invoke(TimeWarp.fetch, new object[] { mode })) throw new InvalidOperationException("Stock warp mode rejected.");
            }
            float[] rates = mode == TimeWarp.Modes.HIGH ? TimeWarp.fetch.warpRates : TimeWarp.fetch.physicsWarpRates;
            int index = 0; for (int i = 0; i < rates.Length; i++) if (rates[i] <= maximum) index = i;
            if (TimeWarp.CurrentRateIndex != index) TimeWarp.SetRate(index, true);
        }
        public static bool Tick(Vessel vessel, double settleSeconds)
        {
            if (!Requested || Admitted) return true;
            try
            {
                report.approachWallSeconds = Time.realtimeSinceStartup - startedWall;
                if (report.approachWallSeconds > 180) throw new TimeoutException("approach-timeout");
                if (vessel == null) { if (samples.Count > 0) { report.actualParts = 0; throw new InvalidOperationException("active-vessel-lost"); } return false; }
                if (vessel.id.ToString("D") != report.vesselId) throw new InvalidOperationException("active-vessel-identity-changed");
                report.actualParts = vessel.parts.Count;
                if (vessel.parts.Count != identities.Count || topologyDirty)
                { if (!SameParts(vessel)) throw new InvalidOperationException("entry-topology-changed"); topologyDirty = false; }
                if (vessel.mainBody == null || vessel.mainBody.bodyName != "Kerbin" || vessel.LandedOrSplashed)
                    throw new InvalidOperationException("entry-context-lost");
                if (Time.realtimeSinceStartup - lastSampleWall >= 1 || samples.Count == 0) Snapshot(vessel);
                double margin = Math.Max(1500, Math.Abs(vessel.verticalSpeed) * Math.Max(2, settleSeconds + 1));
                double atmo = vessel.mainBody.atmosphereDepth;
                string previousPhase = report.phase;
                if (vessel.altitude > atmo + 15000) { report.phase = "orbital-high-warp"; Warp(TimeWarp.Modes.HIGH, 100); }
                else if (vessel.altitude > atmo + 5000) { report.phase = "orbital-high-warp-10x"; Warp(TimeWarp.Modes.HIGH, 10); }
                else if (vessel.altitude > report.targetAltitudeMeters + margin) { report.phase = "physics-approach"; Warp(TimeWarp.Modes.LOW, 4); }
                else { report.phase = "normal-time-final-approach"; Warp(TimeWarp.Modes.LOW, 1); }
                if (report.phase != previousPhase) Snapshot(vessel);
                if (vessel.altitude <= report.targetAltitudeMeters && vessel.loaded && !vessel.packed && TimeWarp.CurrentRate == 1)
                {
                    normalFrames++;
                    if (normalFrames >= 3)
                    {
                        ResetWarp(); report.status = "capture-admitted"; report.phase = "capture-admitted";
                        report.admittedAltitudeMeters = vessel.altitude; report.admittedUniversalTime = Planetarium.GetUniversalTime(); Snapshot(vessel); Write(); return true;
                    }
                }
                else normalFrames = 0;
                return false;
            }
            catch (Exception error) { report.status = "invalid"; report.reason = error.Message; Snapshot(vessel); ResetWarp(); Write(); throw; }
        }
        static void Snapshot(Vessel vessel)
        {
            lastSampleWall = Time.realtimeSinceStartup;
            if (vessel == null) { Write(); return; }
            double maxTemperature = 0, maxSkin = 0;
            foreach (Part part in vessel.parts) if (part != null) { maxTemperature = Math.Max(maxTemperature, part.temperature); maxSkin = Math.Max(maxSkin, part.skinTemperature); }
            if (samples.Count < 512) samples.Add(new Sample { phase = report.phase, utcUniversalTime = Planetarium.GetUniversalTime(),
                wallSeconds = report.approachWallSeconds, altitudeMeters = vessel.altitude, verticalSpeedMetersPerSecond = vessel.verticalSpeed,
                surfaceSpeedMetersPerSecond = vessel.srfSpeed, density = vessel.atmDensity, dynamicPressureKPa = vessel.dynamicPressurekPa,
                warpMode = TimeWarp.WarpMode.ToString(), warpRate = TimeWarp.CurrentRate, situation = vessel.situation.ToString(), parts = vessel.parts.Count,
                packed = vessel.packed, maxPartTemperatureKelvin = maxTemperature, maxSkinTemperatureKelvin = maxSkin });
            Write();
        }
        static void Write()
        {
            report.samples = samples.ToArray(); string json = ReportJson.Encode(report);
            if (loaderDirectory != null) File.WriteAllText(Path.Combine(loaderDirectory, "entry.json"), json);
            if (flightDirectory != null) File.WriteAllText(Path.Combine(flightDirectory, "entry.json"), json);
        }
        public static void ResetWarp()
        {
            if (modifiedHook) { GameEvents.onVesselWasModified.Remove(listener.Modified); modifiedHook = false; }
            if (diedHook) { GameEvents.onPartDie.Remove(listener.Died); diedHook = false; }
            if (!ownedWarp) return;
            TimeWarp.SetRate(0, true);
            bool reset = TimeWarp.fetch != null && ModeSetter != null && (TimeWarp.WarpMode == TimeWarp.Modes.HIGH || (bool)ModeSetter.Invoke(TimeWarp.fetch, new object[] { TimeWarp.Modes.HIGH }));
            ownedWarp = false; report.cleanupStatus = reset && TimeWarp.CurrentRate == 1 ? "reset-to-normal-high-mode" : "warp-reset-failed";
            Write();
            if (!reset || TimeWarp.CurrentRate != 1) throw new InvalidOperationException("Entry warp restoration failed.");
        }
    }
}
