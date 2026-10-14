using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal static class OwnedClockPhysicsSettlement
    {
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-owned-clock-physics-settle/v1", runId, vesselId, status = "invalid", error;
            public string assemblySha256, assemblyMvid, updateIlSha256, correctionIlSha256;
            public int processId, managedThreadId, beginFrame, endFrame, updateToken, correctionToken, originalUpdateCalls;
            public long clockFrequency = Stopwatch.Frequency, beginTicks, endTicks, beforeLastUtBits, afterLastUtBits;
            public double beginUniversalTime, endUniversalTime, beforeLastUt, afterLastUt;
            public bool inFixedTimeStep, disableRunInUpdate, beforePackedInFixed, afterPackedInFixed, beforeCorrectDrift, afterCorrectDrift;
            public bool beforeLastUtFinite, afterLastUtFinite;
            public double[] beforePreOffset, afterPreOffset, beforePostCorrection, afterPostCorrection, beforeVelocity, afterVelocity;
        }
        internal static void Run(Vessel vessel, string directory, string runId)
        {
            var report = new ReportData { runId = runId, vesselId = vessel == null ? null : vessel.id.ToString("D"),
                managedThreadId = Thread.CurrentThread.ManagedThreadId, beginFrame = Time.frameCount,
                beginUniversalTime = Planetarium.GetUniversalTime(), beginTicks = Stopwatch.GetTimestamp() };
            using (var process = Process.GetCurrentProcess()) report.processId = process.Id;
            FieldInfo drift = null, lastUt = null;
            VesselPrecalculate precalc = null;
            try
            {
                report.inFixedTimeStep = Time.inFixedTimeStep;
                Need(!report.inFixedTimeStep, "Physics settlement cannot run inside a native fixed step.");
                Need(vessel != null && vessel.loaded && !vessel.packed && !vessel.HoldPhysics && vessel.precalc != null,
                    "Physics settlement requires the live unpacked stock vessel.");
                precalc = vessel.precalc;
                Need(precalc.GetType() == typeof(VesselPrecalculate), "Physics settlement requires the declared stock precalculation owner.");
                Assembly assembly = typeof(VesselPrecalculate).Assembly;
                StartupPinnedGameIdentity.Require(assembly);
                report.assemblySha256 = ScaleCheckpointLoadState.HashFile(assembly.Location);
                report.assemblyMvid = assembly.ManifestModule.ModuleVersionId.ToString("D");
                MethodInfo update = AccessTools.DeclaredMethod(typeof(VesselPrecalculate), "Update", Type.EmptyTypes);
                MethodInfo correction = AccessTools.DeclaredMethod(typeof(VesselPrecalculate), "ApplyVelocityCorrection", Type.EmptyTypes);
                Need(update != null && update.MetadataToken == 100678120 && update.ReturnType == typeof(void) &&
                    correction != null && correction.MetadataToken == 100678121 && correction.ReturnType == typeof(void),
                    "Original physics settlement signatures differ.");
                RequireUnpatched(update); RequireUnpatched(correction);
                report.updateToken = update.MetadataToken; report.correctionToken = correction.MetadataToken;
                report.updateIlSha256 = Hash(update.GetMethodBody().GetILAsByteArray());
                report.correctionIlSha256 = Hash(correction.GetMethodBody().GetILAsByteArray());
                drift = AccessTools.DeclaredField(typeof(VesselPrecalculate), "correctDriftThisFrame");
                lastUt = AccessTools.DeclaredField(typeof(VesselPrecalculate), "lastUT");
                Need(drift != null && drift.FieldType == typeof(bool) && lastUt != null && lastUt.FieldType == typeof(double),
                    "Original physics settlement field identities differ.");
                report.disableRunInUpdate = VesselPrecalculate.disableRunInUpdate;
                report.beforePackedInFixed = precalc.packedInFixed;
                report.beforeCorrectDrift = (bool)drift.GetValue(precalc); report.beforeLastUt = (double)lastUt.GetValue(precalc);
                report.beforeLastUtBits = BitConverter.DoubleToInt64Bits(report.beforeLastUt); report.beforeLastUtFinite = Finite(report.beforeLastUt);
                report.beforePreOffset = Vector(precalc.preIntegrationVelocityOffset); report.beforePostCorrection = Vector(precalc.postIntegrationVelocityCorrection);
                report.beforeVelocity = Vector(vessel.velocityD);
                Need(report.beforePreOffset != null && report.beforePostCorrection != null && report.beforeVelocity != null &&
                    Finite(report.beforeLastUt), "Original physics settlement input is nonfinite.");
                report.originalUpdateCalls++;
                try { update.Invoke(precalc, null); }
                catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
                ReadAfter(report, vessel, precalc, drift, lastUt);
                Need(report.afterLastUtFinite && report.afterPreOffset != null && report.afterPostCorrection != null && report.afterVelocity != null,
                    "Original physics settlement output is nonfinite.");
                RequireUnpatched(update); RequireUnpatched(correction);
                Need(!report.afterCorrectDrift && Zero(precalc.postIntegrationVelocityCorrection) && Zero(precalc.preIntegrationVelocityOffset),
                    "Original update left a physical correction pending; retry after stock post-physics settlement.");
                Need(!Time.inFixedTimeStep && Time.frameCount == report.beginFrame && Planetarium.GetUniversalTime() == report.beginUniversalTime &&
                    ReferenceEquals(vessel.precalc, precalc) && vessel.loaded && !vessel.packed && !vessel.HoldPhysics,
                    "Physics settlement changed its observation epoch or owner.");
                report.status = "settled";
            }
            catch (Exception exception) { report.error = exception.ToString(); throw; }
            finally
            {
                if (precalc != null && drift != null && lastUt != null)
                {
                    try { ReadAfter(report, vessel, precalc, drift, lastUt); }
                    catch (Exception observation) { report.status = "invalid"; report.error += "\nFinal readback failed: " + observation; }
                }
                if (!Finite(report.beforeLastUt)) report.beforeLastUt = 0;
                if (!Finite(report.afterLastUt)) { report.status = "invalid"; report.error += "\nStock lastUT readback was nonfinite."; report.afterLastUt = 0; }
                report.endFrame = Time.frameCount; report.endUniversalTime = Planetarium.GetUniversalTime(); report.endTicks = Stopwatch.GetTimestamp();
                OwnedClockFlightService.WriteNew(Path.Combine(directory, "physics-settle.json"), ReportJson.Encode(report));
            }
        }
        static void ReadAfter(ReportData report, Vessel vessel, VesselPrecalculate precalc, FieldInfo drift, FieldInfo lastUt)
        {
            report.afterPackedInFixed = precalc.packedInFixed; report.afterCorrectDrift = (bool)drift.GetValue(precalc);
            report.afterLastUt = (double)lastUt.GetValue(precalc); report.afterPreOffset = Vector(precalc.preIntegrationVelocityOffset);
            report.afterLastUtBits = BitConverter.DoubleToInt64Bits(report.afterLastUt); report.afterLastUtFinite = Finite(report.afterLastUt);
            report.afterPostCorrection = Vector(precalc.postIntegrationVelocityCorrection); report.afterVelocity = Vector(vessel.velocityD);
        }
        static void RequireUnpatched(MethodInfo method)
        { var patches = Harmony.GetPatchInfo(method); Need(patches == null || patches.Owners.Count == 0, "Original physics settlement owner has patches."); }
        static bool Zero(Vector3d value) { return value.x == 0 && value.y == 0 && value.z == 0; }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static double[] Vector(Vector3d value) { return Finite(value.x) && Finite(value.y) && Finite(value.z) ? new[] { value.x, value.y, value.z } : null; }
        static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return OwnedClockFlightService.Hex(sha.ComputeHash(bytes)); }
        static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    }
}
