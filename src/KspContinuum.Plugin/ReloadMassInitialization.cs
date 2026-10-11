using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    public sealed class ReloadMassInitialization : IDisposable
    {
        const string Owner = "continuum.reload-mass-initialization";
        static ReloadMassInitialization active;
        static readonly MethodInfo Transition = AccessTools.DeclaredMethod(typeof(OrbitDriver), "SetOrbitMode", new[] { typeof(OrbitDriver.UpdateMode) });
        static readonly MethodInfo Unpack = AccessTools.DeclaredMethod(typeof(Part), "Unpack", Type.EmptyTypes);
        static readonly MethodInfo Initialize = AccessTools.DeclaredMethod(typeof(Part), "InitializeModules", Type.EmptyTypes);
        static readonly FieldInfo Packed = AccessTools.DeclaredField(typeof(Part), "packed");
        readonly Harmony harmony = new Harmony(Owner);
        readonly MethodInfo seam, massMethod;
        readonly FieldInfo setup, count, heat;
        readonly Action<FlightIntegrator> publish;
        readonly Guid vesselId;
        readonly int oldInstance, expectedParts, thread;
        readonly HashSet<uint> parts;
        readonly bool packedMode;
        bool owned, publishing;
        HashSet<Part> publishingParts;
        internal readonly ReportData Report;

        [Serializable] public sealed class BodyRow
        {
            public uint partId;
            public int partInstance, bodyInstance;
            public bool packed, started, kinematic;
            public double physicsMass, rigidbodyMass;
            public double[] centerOfMass, coMOffset, worldCenterOfMass;
        }
        [Serializable] public sealed class StateRow
        {
            public double universalTime, mass, orbitEpoch;
            public int frame, vesselInstance, orbitMode;
            public double[] weightedWorldCoM, weightedLocalCoM, liveLocalCoM;
            public BodyRow[] bodies;
        }
        [Serializable] public sealed class ReportData
        {
            public bool requested, hooksRemoved;
            public string status = "not-requested", cleanupStatus = "not-installed", error, moduleMvid, mode = "unpacked", scope = "Advance whole original FI UpdateMassStats (resource/thermal/mass/center publication). No orbit, pose, velocity or packed-state reconciliation.";
            public string vesselId;
            public int calls, faults, originalExceptions, seamToken, massToken, integratorInstance;
            public int zeroMassStatsSeen, packedBodyPublications;
            public long startedTicks, endedTicks, clockFrequency;
            public StateRow before, after;
        }

        internal ReloadMassInitialization(Guid selected, int priorInstance, uint[] selectedParts, bool earlyPacked = false)
        {
            Report = new ReportData { requested = true, status = "requested", vesselId = selected.ToString("D") };
            vesselId = selected; oldInstance = priorInstance; parts = new HashSet<uint>(selectedParts); expectedParts = selectedParts.Length;
            thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            packedMode=earlyPacked; Report.mode=packedMode?"packed":"unpacked";
            Report.clockFrequency=System.Diagnostics.Stopwatch.Frequency;
            if (active != null || parts.Count != expectedParts) throw new InvalidOperationException("Reload mass barrier owner/part identities are ambiguous.");
            StartupPinnedGameIdentity.Require(typeof(Vessel).Assembly);
            seam = packedMode ? AccessTools.DeclaredMethod(typeof(VesselPrecalculate), "CalculatePhysicsStats", Type.EmptyTypes) : AccessTools.DeclaredMethod(typeof(Vessel), "GoOffRails", Type.EmptyTypes);
            massMethod = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateMassStats", Type.EmptyTypes);
            MethodInfo fiSetup = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "Setup", Type.EmptyTypes);
            setup = AccessTools.DeclaredField(typeof(FlightIntegrator), "setupRun");
            count = AccessTools.DeclaredField(typeof(FlightIntegrator), "partCount");
            heat = AccessTools.DeclaredField(typeof(FlightIntegrator), "cacheStandardSpecificHeatCapacity");
            foreach (MethodInfo method in new[] { seam, massMethod, fiSetup, Transition, Unpack, Initialize })
                if (method == null || method.IsStatic || method.ReturnType != typeof(void) || method.GetMethodBody() == null || method.Module.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf") || Harmony.GetPatchInfo(method)?.Owners.Any() == true)
                    throw new InvalidOperationException("Reload mass barrier original seam is absent or already patched: " + method);
            if (setup == null || setup.FieldType != typeof(bool) || count == null || count.FieldType != typeof(int) || heat == null || heat.FieldType != typeof(double)) throw new InvalidOperationException("FI setup dependency layout differs.");
            if(Packed==null||Packed.FieldType!=typeof(bool))throw new InvalidOperationException("Original packed publication guard differs.");
            Report.seamToken = seam.MetadataToken; Report.massToken = massMethod.MetadataToken; Report.moduleMvid = seam.Module.ModuleVersionId.ToString("D");
            active = this; owned = true;
            try
            {
                if(packedMode)
                {
                    harmony.Patch(massMethod,transpiler:new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ReloadMassInitialization),"MassTranspile")));
                    harmony.Patch(seam,prefix:new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ReloadMassInitialization),"StatsPrefix")));
                }
                else harmony.Patch(seam, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ReloadMassInitialization), "Transpile")));
                publish = (Action<FlightIntegrator>)Delegate.CreateDelegate(typeof(Action<FlightIntegrator>), massMethod);
                Report.status = "armed";
            }
            catch { Dispose(); throw; }
        }

        static IEnumerable<CodeInstruction> MassTranspile(IEnumerable<CodeInstruction> source)
        {
            var codes=source.ToList();
            int index=codes.FindIndex(c=>c.opcode==OpCodes.Ldfld&&Equals(c.operand,Packed));
            if(index<0||codes.Count(c=>c.opcode==OpCodes.Ldfld&&Equals(c.operand,Packed))!=1||index+1>=codes.Count||(codes[index+1].opcode!=OpCodes.Brfalse&&codes[index+1].opcode!=OpCodes.Brfalse_S))
                throw new InvalidOperationException("Original sole packed mass-publication guard differs.");
            codes[index].opcode=OpCodes.Call;codes[index].operand=AccessTools.DeclaredMethod(typeof(ReloadMassInitialization),"PackedForPublication");
            return codes;
        }
        static bool PackedForPublication(Part part)
        {
            ReloadMassInitialization owner=active;
            if(owner!=null&&owner.publishing&&owner.publishingParts.Contains(part))
            {
                if(!part.packed||part.rb==null||!part.rb.isKinematic)throw new InvalidOperationException("Packed body ownership changed during mass publication.");
                owner.Report.packedBodyPublications++;return false;
            }
            return part.packed;
        }
        static void StatsPrefix(VesselPrecalculate __instance)
        {
            ReloadMassInitialization owner=active;
            if(owner==null||!owner.packedMode||owner.Report.calls!=0)return;
            Vessel vessel=__instance.GetComponent<Vessel>();
            if(vessel==null||vessel.id!=owner.vesselId||vessel.GetInstanceID()==owner.oldInstance)return;
            // Zero-mass stock statistics retains the serialized local CoM; it cannot publish a weighted replacement.
            if(!vessel.parts.Any(p=>p.rb!=null&&p.physicsMass>0)){owner.Report.zeroMassStatsSeen++;return;}
            owner.Complete(vessel,OrbitDriver.UpdateMode.UPDATE);
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> source)
        {
            var codes = source.ToList();
            int u = codes.FindIndex(c => c.Calls(Unpack)), i = codes.FindIndex(c => c.Calls(Initialize)), t = codes.FindIndex(c => c.Calls(Transition));
            if (u < 0 || i <= u || t <= i || codes.Count(c => c.Calls(Unpack)) != 1 || codes.Count(c => c.Calls(Initialize)) != 1 || codes.Count(c => c.Calls(Transition)) != 1)
                throw new InvalidOperationException("Original unpack/module/physical-mode call order differs.");
            codes[t].opcode = OpCodes.Call;
            codes[t].operand = AccessTools.DeclaredMethod(typeof(ReloadMassInitialization), "TransitionAfterMass");
            return codes;
        }

        static void TransitionAfterMass(OrbitDriver driver, OrbitDriver.UpdateMode mode)
        {
            ReloadMassInitialization owner = active;
            Vessel vessel = driver == null ? null : driver.vessel;
            if (owner != null && vessel != null && vessel.id == owner.vesselId && vessel.GetInstanceID() != owner.oldInstance)
                owner.Complete(vessel, mode);
            driver.SetOrbitMode(mode);
        }

        void Complete(Vessel vessel, OrbitDriver.UpdateMode mode)
        {
            try
            {
                if (Report.calls != 0 || System.Threading.Thread.CurrentThread.ManagedThreadId != thread || !HighLogic.LoadedSceneIsFlight || mode != (packedMode?OrbitDriver.UpdateMode.UPDATE:OrbitDriver.UpdateMode.TRACK_Phys) || vessel.packed!=packedMode || !vessel.loaded || (!packedMode&&vessel.HoldPhysics) || vessel.LandedOrSplashed || vessel.mainBody == null || vessel.altitude <= vessel.mainBody.atmosphereDepth || TimeWarp.CurrentRate != 1 || vessel.parts.Count != expectedParts || !parts.SetEquals(vessel.parts.Select(p => p.flightID)) || vessel.parts.Any(p => !p.started || p.packed!=packedMode || (packedMode&&p.rb!=null&&!p.rb.isKinematic)) || (packedMode&&vessel.orbitDriver.updateMode!=OrbitDriver.UpdateMode.UPDATE))
                    throw new InvalidOperationException("Fresh reload mass bootstrap context differs.");
                FlightIntegrator fi = vessel.GetComponent<FlightIntegrator>();
                if (fi == null || fi.GetType() != typeof(FlightIntegrator) || fi.Vessel != vessel || !(bool)setup.GetValue(fi) || (int)count.GetValue(fi) != expectedParts || !Finite((double)heat.GetValue(fi)) || (double)heat.GetValue(fi) <= 0)
                    throw new InvalidOperationException("Original FI setup/dependencies are not ready.");
                Report.before = Snapshot(vessel); Report.integratorInstance = fi.GetInstanceID(); Report.calls++;
                Report.startedTicks=System.Diagnostics.Stopwatch.GetTimestamp();
                publishingParts=packedMode?new HashSet<Part>(vessel.parts):null;publishing=packedMode;
                try { publish(fi); }
                catch { Report.originalExceptions++; throw; }
                finally { publishing=false;publishingParts=null;Report.endedTicks=System.Diagnostics.Stopwatch.GetTimestamp(); }
                Report.after = Snapshot(vessel);
                if(packedMode&&(Report.packedBodyPublications!=Report.before.bodies.Length||Report.before.bodies.Length!=Report.after.bodies.Length||Report.after.bodies.Any(p=>!p.packed||!p.kinematic)))throw new InvalidOperationException("Scoped packed mass publication coverage/state differs.");
                Report.status = "completed";
            }
            catch (Exception error) { Report.faults++; Report.status = "invalid"; Report.error = error.GetType().FullName + ": " + error.Message; throw; }
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static double[] V(Vector3d v) { return new[] { v.x, v.y, v.z }; }
        static StateRow Snapshot(Vessel vessel)
        {
            var rows = new List<BodyRow>(); double mass = 0; Vector3d weighted = Vector3d.zero;
            for (int i = vessel.parts.Count - 1; i >= 0; i--)
            {
                Part p = vessel.parts[i]; if (p.rb == null) continue;
                var row = new BodyRow { partId = p.flightID, partInstance = p.GetInstanceID(), bodyInstance = p.rb.GetInstanceID(), packed = p.packed, started = p.started, kinematic=p.rb.isKinematic, physicsMass = p.physicsMass, rigidbodyMass = p.rb.mass, centerOfMass = V(p.rb.centerOfMass), coMOffset = V(p.CoMOffset), worldCenterOfMass = V(p.rb.worldCenterOfMass) };
                if (!Finite(row.physicsMass) || row.physicsMass < 0 || !Finite(row.rigidbodyMass) || row.rigidbodyMass <= 0 || row.centerOfMass.Concat(row.coMOffset).Concat(row.worldCenterOfMass).Any(x => !Finite(x))) throw new InvalidOperationException("Body mass/center publication is nonfinite.");
                rows.Add(row); mass += p.physicsMass; weighted += (Vector3d)p.rb.worldCenterOfMass * p.physicsMass;
            }
            if (mass <= 0 || !Finite(mass)) throw new InvalidOperationException("Reload mass barrier has no weighted bodies.");
            weighted /= mass;
            Vector3d local = QuaternionD.Inverse((QuaternionD)vessel.transform.rotation) * (weighted - (Vector3d)vessel.transform.position);
            if (V(weighted).Concat(V(local)).Any(x => !Finite(x))) throw new InvalidOperationException("Reload weighted center is nonfinite.");
            return new StateRow { universalTime = Planetarium.GetUniversalTime(), orbitEpoch=vessel.orbit.epoch, frame = Time.frameCount, vesselInstance = vessel.GetInstanceID(), orbitMode = (int)vessel.orbitDriver.updateMode, mass = mass, liveLocalCoM=V(vessel.localCoM), weightedWorldCoM = V(weighted), weightedLocalCoM = V(local), bodies = rows.ToArray() };
        }
        public void Dispose()
        {
            if (owned) { harmony.UnpatchAll(Owner); owned = false; }
            Report.hooksRemoved = (seam == null || Harmony.GetPatchInfo(seam)?.Owners.Contains(Owner) != true)&&(massMethod==null||Harmony.GetPatchInfo(massMethod)?.Owners.Contains(Owner)!=true);
            Report.cleanupStatus = Report.hooksRemoved ? "removed-owned-hook" : "cleanup-error";
            if (!Report.hooksRemoved) { Report.faults++; Report.status = "invalid"; }
            if (ReferenceEquals(active, this)) active = null;
        }
    }
}
