using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class ConvectionCapture : IDisposable
    {
        const string Owner = "continuum.convection-capture";
        const string StockHash = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        static readonly Guid StockMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static ConvectionCapture active;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Func<bool> capturing;
        readonly List<ConvectionFrame> frames = new List<ConvectionFrame>();
        readonly string path;
        readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        readonly FieldInfo rowsField = AccessTools.Field(typeof(FlightIntegrator), "occlusionConv");
        readonly MethodInfo method = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateOcclusionConvection");
        readonly Vessel vessel;
        bool disposed;
        [Serializable] public sealed class CaptureReport
        {
            public string status = "installed", cleanupStatus = "installed", detail, runId, vesselId, path, sha256;
            public int pid, capturedFrames, faults;
            public long copyTicks, clockFrequency = Stopwatch.Frequency;
            public string scope = "post-stock projected inputs/results; binary write after capture; intrusive copy cost; no force or thermal writes";
        }
        public CaptureReport Report { get; private set; }
        ConvectionCapture(string path, string runId, Func<bool> capturing)
        {
            this.path = path; this.capturing = capturing; vessel = FlightGlobals.ActiveVessel;
            Report = new CaptureReport { runId = runId, vesselId = vessel.id.ToString("D"), path = path, pid = Process.GetCurrentProcess().Id };
            if (active != null || method == null || rowsField == null || method.MetadataToken != 100677506 ||
                method.Module.ModuleVersionId != StockMvid || Digest(typeof(FlightIntegrator).Assembly.Location) != StockHash ||
                File.Exists(path) || !Path.IsPathRooted(path) || !Directory.Exists(Path.GetDirectoryName(path)))
                throw new InvalidOperationException("Convection capture admission failed.");
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches != null && patches.Owners.Any()) throw new InvalidOperationException("Convection capture requires unpatched stock seam.");
            active = this;
            try { harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ConvectionCapture), "Postfix"))); }
            catch { active = null; harmony.UnpatchAll(Owner); throw; }
        }
        public static ConvectionCapture Create(string[] args, string runId, Func<bool> capturing)
        {
            string prefix = "--continuum-convection-capture=";
            string[] selected = args.Where(arg => arg.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (selected.Length == 0) return null;
            if (selected.Length != 1 || string.IsNullOrEmpty(runId) || FlightGlobals.ActiveVessel == null)
                throw new InvalidOperationException("Convection capture requires one destination and a bound flight.");
            return new ConvectionCapture(selected[0].Substring(prefix.Length), runId, capturing);
        }
        static void Postfix(FlightIntegrator __instance)
        {
            ConvectionCapture owner = active;
            if (owner == null || owner.disposed || owner.Report.faults != 0 || !owner.capturing() || owner.frames.Count >= 4 || !(__instance.mach > 1)) return;
            long start = Stopwatch.GetTimestamp();
            try { owner.Capture(__instance); }
            catch (Exception error) { owner.Report.faults++; owner.Report.status = "invalid"; owner.Report.detail = error.GetType().Name + ": " + error.Message; }
            finally { owner.Report.copyTicks += Stopwatch.GetTimestamp() - start; }
        }
        void Capture(FlightIntegrator integrator)
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId || FlightGlobals.ActiveVessel != vessel)
                throw new InvalidOperationException("Convection capture lost its owner.");
            var input = (List<OcclusionData>)rowsField.GetValue(integrator);
            if (input.Count < 1 || input.Count > ConvectionFormat.MaxRows || integrator.occludersConvectionCount < 1 || integrator.occludersConvectionCount > input.Count)
                throw new InvalidOperationException("Convection capture row/count bound.");
            var rows = new ConvectionRow[input.Count]; var indices = new Dictionary<OcclusionData, int>();
            for (int i = 0; i < rows.Length; i++)
            {
                OcclusionData row = input[input.Count - 1 - i];
                if (row == null || row.part == null || row.ptd == null || row.part.vessel != vessel || indices.ContainsKey(row))
                    throw new InvalidOperationException("Convection capture row ownership.");
                indices.Add(row, i);
                rows[i] = new ConvectionRow { flightId = row.part.flightID, projectedArea = row.projectedArea, projectedRadius = row.projectedRadius,
                    invFineness = row.invFineness, minimumDot = row.minimumDot, maximumDot = row.maximumDot, centroidDot = row.centroidDot,
                    maxWidthDepth = row.maxWidthDepth, projectedCenter = V(row.projectedCenter), center = V(row.center), minExtents = V(row.minExtents),
                    maxExtents = V(row.maxExtents), extents = V(row.extents), expectedTemp = row.ptd.convectionTempMultiplier,
                    expectedCoeff = row.ptd.convectionCoeffMultiplier, expectedArea = row.ptd.convectionAreaMultiplier };
            }
            var cones = new ConvectionCone[integrator.occludersConvectionCount];
            for (int i = 0; i < cones.Length; i++)
            {
                OcclusionCone cone = integrator.occludersConvection[i]; int index;
                if (cone == null || cone.part == null || !indices.TryGetValue(cone.part, out index)) throw new InvalidOperationException("Convection cone ownership.");
                cones[i] = new ConvectionCone { rowIndex = index, center = V(cone.center), extents = V(cone.extents), offset = V(cone.offset),
                    radius = cone.radius, cylNoseDot = cone.cylNoseDot, shockNoseDot = cone.shockNoseDot, shockAngle = cone.shockAngle,
                    shockConvectionTempMult = cone.shockConvectionTempMult, shockConvectionCoeffMult = cone.shockConvectionCoeffMult,
                    occludeConvectionTempMult = cone.occludeConvectionTempMult, occludeConvectionCoeffMult = cone.occludeConvectionCoeffMult,
                    occludeConvectionAreaMult = cone.occludeConvectionAreaMult };
            }
            frames.Add(new ConvectionFrame { sequence = frames.Count, universalTime = Planetarium.GetUniversalTime(), mach = integrator.mach,
                globals = new[] { OcclusionCone.detachedShockHeatMult, OcclusionCone.detachedShockCoeffMult, OcclusionCone.detachedBehindShockHeatMult,
                    OcclusionCone.detachedBehindShockCoeffMult, OcclusionCone.detachedShockMachAngleMult, OcclusionCone.detachedShockStartAngle,
                    OcclusionCone.detachedShockEndAngle, OcclusionCone.obliqueShockAngleMult, OcclusionCone.obliqueShockPartAngleMult,
                    OcclusionCone.obliqueShockMinAngleMult, OcclusionCone.obliqueShockConeHeatMult, OcclusionCone.obliqueShockConeCoeffMult,
                    OcclusionCone.obliqueShockCylHeatMult, OcclusionCone.obliqueShockCylCoeffMult }, rows = rows, acceptedCones = cones });
            Report.capturedFrames = frames.Count;
        }
        static float[] V(Vector3 value) { return new[] { value.x, value.y, value.z }; }
        static float[] V(Vector2 value) { return new[] { value.x, value.y }; }
        static string Digest(string source)
        {
            using (SHA256 hash = SHA256.Create()) using (FileStream stream = File.OpenRead(source))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try
            {
                harmony.UnpatchAll(Owner);
                Patches graph = Harmony.GetPatchInfo(method);
                Report.cleanupStatus = graph != null && graph.Owners.Contains(Owner) ? "cleanup-error" : "removed-owned-patch";
            }
            finally { if (ReferenceEquals(active, this)) active = null; }
            if (Report.faults != 0 || frames.Count != 4 || Report.cleanupStatus != "removed-owned-patch") { Report.status = "invalid"; return; }
            ConvectionFormat.Write(path, new ConvectionRecording { sessionId = Report.runId, vesselId = Report.vesselId,
                assemblySha256 = StockHash, assemblyMvid = StockMvid.ToString("D"), frames = frames.ToArray() });
            Report.sha256 = Digest(path); Report.status = "complete";
        }
    }
}
