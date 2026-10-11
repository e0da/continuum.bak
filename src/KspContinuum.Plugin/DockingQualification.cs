using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class DockingQualification : IDisposable
    {
        const string Lock = "continuum.docking.roundtrip";
        readonly ModuleDockingNode a, b;
        readonly Part[] groupA, groupB, parts;
        readonly HashSet<Vessel> hooked = new HashSet<Vessel>();
        readonly Dictionary<string, double> resources;
        readonly string[] crew;
        readonly double ecRate;
        readonly List<SetupRow> setup = new List<SetupRow>();
        int stabilizationSteps;
        readonly string directory;
        readonly int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        bool disposed, lockOwned;
        string phase = "stabilize";
        double phaseUt, startedWall, mergedAt, splitDistance;
        readonly List<EventRow> events = new List<EventRow>();
        readonly List<string> images = new List<string>();
        [Serializable] public sealed class SetupRow { public string phase, vesselId; public double[] position, velocity, rotation; }
        [Serializable] public sealed class ResourceRow { public string name; public double amount; }
        [Serializable] public sealed class EventRow
        {
            public string phase, activeVesselId, targetVesselId, activePortState, targetPortState;
            public double universalTime, wallSeconds, distanceMeters, relativeSpeedMetersPerSecond, alignmentDot;
            public int activeParts, targetParts, jointId;
        }
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-docking-roundtrip/v1", status = "running", cleanupStatus = "owned", reason, runId, sourceSha256;
            public string activeVesselId, targetVesselId, mergedVesselId, splitActiveVesselId, splitTargetVesselId;
            public string scope = "Already aligned paired checkpoint; bounded distributed diagnostic translation forces; actual stock capture/docking/undocking. Not RCS, rendezvous, fuel controller or complete mission qualification.";
            public int processId, initialActiveParts, initialTargetParts, faults, mergeCount, undockCalls, splitCount, controlledBodies, controlCallbacks, ticks;
            public uint activePortId, targetPortId;
            public bool preparationRequested, collisionsRestored, zeroCrewScope, initialSeparate, actualJointObserved, exactPartPartitionsRestored, crewConserved, resourcesConserved, sourceUnchanged, hooksRemoved, controlLockRemoved;
            public double initialUniversalTime, electricChargeRateBudget, electricChargeConsumed, minimumGapMeters, finalGapMeters, finalSeparationSpeedMetersPerSecond, maxAccelerationMetersPerSecondSquared = .01;
            public long driverTicks, clockFrequency = System.Diagnostics.Stopwatch.Frequency;
            public uint[] activePartIds, targetPartIds;
            public ResourceRow[] initialResources, finalResources;
            public string[] crewNames, screenshots;
            public EventRow[] events;
            public SetupRow[] setup;
        }
        public ReportData Report { get; private set; }
        public static bool Requested(string[] args) { return args.Any(arg => arg.StartsWith("--continuum-docking=", StringComparison.Ordinal)); }
        static string Value(string[] args, string name)
        {
            string prefix = name + "="; string[] values = args.Where(v => v.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (values.Length != 1 || values[0].Length == prefix.Length) throw new InvalidOperationException("One explicit docking input required: " + name);
            return values[0].Substring(prefix.Length);
        }
        public static DockingQualification Create(string[] args, string directory)
        {
            if (!Requested(args)) return null;
            if (Value(args, "--continuum-docking") != "roundtrip") throw new InvalidOperationException("Only docking roundtrip is supported.");
            Guid active = Guid.Parse(Value(args, "--continuum-docking-active-vessel")), target = Guid.Parse(Value(args, "--continuum-docking-target-vessel"));
            uint pa = uint.Parse(Value(args, "--continuum-docking-active-port"), CultureInfo.InvariantCulture), pb = uint.Parse(Value(args, "--continuum-docking-target-port"), CultureInfo.InvariantCulture);
            Vessel va = FlightGlobals.Vessels.Single(v => v != null && v.id == active), vb = FlightGlobals.Vessels.Single(v => v != null && v.id == target);
            return new DockingQualification(args, va, pa, vb, pb, directory);
        }
        DockingQualification(string[] args, Vessel va, uint pa, Vessel vb, uint pb, string directory)
        {
            this.directory = directory;
            if (va == vb || va != FlightGlobals.ActiveVessel || !Ready(va) || !Ready(vb) || va.mainBody != vb.mainBody || va.mainBody.bodyName != "Kerbin" ||
                va.altitude < va.mainBody.atmosphereDepth || vb.altitude < vb.mainBody.atmosphereDepth || MapView.MapIsEnabled || FlightDriver.Pause || TimeWarp.CurrentRate != 1 ||
                !ScaleCheckpointLoadState.Ready || !ScaleCheckpointLoadState.SourceUnchanged())
                throw new InvalidOperationException("Docking needs an owned, normal-time paired physical Kerbin orbital checkpoint.");
            a = Port(va, pa); b = Port(vb, pb);
            groupA = va.parts.ToArray(); groupB = vb.parts.ToArray(); parts = groupA.Concat(groupB).ToArray();
            if (parts.Select(p => p.flightID).Distinct().Count() != parts.Length || a.nodeTransform == null || b.nodeTransform == null ||
                a.state != "Ready" || b.state != "Ready" || a.otherNode != null || b.otherNode != null || a.dockedPartUId != 0 || b.dockedPartUId != 0 ||
                a.nodeTypes == null || b.nodeTypes == null || !a.nodeTypes.Overlaps(b.nodeTypes) || a.gendered != b.gendered || (a.gendered && a.genderFemale == b.genderFemale))
                throw new InvalidOperationException("Distinct Ready compatible docking ports and unique part identities are required.");
            if (va.Autopilot.Enabled || vb.Autopilot.Enabled || va.GroupOverride != 0 || vb.GroupOverride != 0 || va.ActionGroups[KSPActionGroup.SAS] || vb.ActionGroups[KSPActionGroup.SAS] || va.ActionGroups[KSPActionGroup.RCS] || vb.ActionGroups[KSPActionGroup.RCS] ||
                parts.Any(p => p.Modules.OfType<ModuleEngines>().Any(e => e.EngineIgnited)) || !Zero(va.ctrlState) || !Zero(vb.ctrlState))
                throw new InvalidOperationException("Seed must have idle controls, SAS/RCS off and no ignited engines.");
            ValidateCallbacks(va); ValidateCallbacks(vb);
            foreach (Part part in parts) foreach (PartModule module in part.Modules)
                foreach (ModuleResource input in module.resHandler.inputResources)
                {
                    if (module is ModuleReactionWheel && ((ModuleReactionWheel)module).wheelState == ModuleReactionWheel.WheelState.Disabled) continue;
                    if (input.rate < 0 || double.IsNaN(input.rate) || double.IsInfinity(input.rate) ||
                        (input.rate != 0 && (input.name != "ElectricCharge" || !(module is ModuleCommand))))
                        throw new InvalidOperationException("Only frozen stock command EC consumption is supported; seed reaction wheels must be Disabled.");
                    ecRate += input.rate;
                }
            bool prepared = args.Contains("--continuum-docking-prepare-pair");
            if (prepared) PreparePair();
            Vector3 delta = b.nodeTransform.position - a.nodeTransform.position;
            double distance = delta.magnitude;
            if (distance < 1 || distance > 3 || Vector3.Dot(delta.normalized, a.nodeTransform.forward) < .9998f ||
                Vector3.Dot(a.nodeTransform.forward, -b.nodeTransform.forward) < .9998f || Relative().magnitude > (prepared ? .06 : .02) ||
                Vector3.ProjectOnPlane(delta, a.nodeTransform.forward).magnitude > .02)
                throw new InvalidOperationException("Seed needs1–3m facing gap,≤2cm lateral error and≤.02m/s relative speed.");
            resources = Totals(); crew = Crew();
            Report = new ReportData { runId = Value(args, "--continuum-research-run"), sourceSha256 = ScaleCheckpointLoadState.SourceSha256,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, activeVesselId = va.id.ToString("D"), targetVesselId = vb.id.ToString("D"),
                activePortId = pa, targetPortId = pb, initialActiveParts = groupA.Length, initialTargetParts = groupB.Length, initialSeparate = true,
                activePartIds = groupA.Select(p => p.flightID).ToArray(), targetPartIds = groupB.Select(p => p.flightID).ToArray(), crewNames = crew,
                initialResources = Rows(resources), minimumGapMeters = distance, preparationRequested = prepared, collisionsRestored = true,
                zeroCrewScope = crew.Length == 0, initialUniversalTime = Planetarium.GetUniversalTime(), electricChargeRateBudget = ecRate };
            startedWall = Time.realtimeSinceStartup; phaseUt = Planetarium.GetUniversalTime();
            try { InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.TIMEWARP, Lock); lockOwned = true; Attach(va); Attach(vb); Record("seed"); }
            catch { Dispose(); throw; }
        }
        static ModuleDockingNode Port(Vessel v, uint id)
        {
            Part part = v.parts.Single(p => p.flightID == id);
            return part.Modules.OfType<ModuleDockingNode>().Single();
        }
        void SetupSnapshot(string phaseName, Vessel v)
        {
            Vector3 pos = v.transform.position, vel = v.rootPart.Rigidbody.velocity; Quaternion q = v.transform.rotation;
            setup.Add(new SetupRow { phase = phaseName, vesselId = v.id.ToString("D"), position = new[] { (double)pos.x,pos.y,pos.z },
                velocity = new[] { (double)vel.x,vel.y,vel.z }, rotation = new[] { (double)q.x,q.y,q.z,q.w } });
        }
        void PreparePair()
        {
            Vessel va = a.vessel, vb = b.vessel;
            var bodies = parts.Select(p=>p.Rigidbody).Where(r=>r!=null).Distinct().ToArray();
            if (bodies.Length == 0 || bodies.Any(r=>r.isKinematic)) throw new InvalidOperationException("Preparation needs dynamic original rigidbodies.");
            bool[] collision = bodies.Select(r=>r.detectCollisions).ToArray();
            SetupSnapshot("before",va); SetupSnapshot("before",vb);
            try
            {
                foreach (Rigidbody rb in bodies) rb.detectCollisions=false;
                Vector3 axis = (vb.transform.position-va.transform.position).normalized;
                Vector3 up = Vector3.ProjectOnPlane((Vector3)(va.GetWorldPos3D()-va.mainBody.position),axis).normalized;
                if (axis.sqrMagnitude < .9 || up.sqrMagnitude < .9) throw new InvalidOperationException("Preparation basis is degenerate.");
                va.SetRotation(Quaternion.LookRotation(axis,up)*Quaternion.Inverse(a.nodeTransform.rotation)*va.transform.rotation);
                vb.SetRotation(Quaternion.LookRotation(-axis,up)*Quaternion.Inverse(b.nodeTransform.rotation)*vb.transform.rotation);
                vb.SetPosition((Vector3d)vb.transform.position + (Vector3d)(a.nodeTransform.position+axis*2-b.nodeTransform.position),false);
                Vector3 velocity = va.rootPart.Rigidbody.velocity;
                vb.SetWorldVelocity((Vector3d)velocity); va.SetWorldVelocity((Vector3d)(velocity+axis*.05f));
                foreach (Rigidbody rb in bodies) rb.angularVelocity=Vector3.zero;
                SetupSnapshot("after",va); SetupSnapshot("after",vb);
            }
            finally
            {
                for (int i=0;i<bodies.Length;i++) if(bodies[i]!=null) bodies[i].detectCollisions=collision[i];
                if(bodies.Select((r,i)=>r!=null&&r.detectCollisions==collision[i]).Any(ok=>!ok)) throw new InvalidOperationException("Preparation collision restoration failed.");
            }
        }
        static bool Ready(Vessel v) { return v != null && v.loaded && !v.packed && !v.HoldPhysics && v.rootPart != null ; }
        static bool Zero(FlightCtrlState c) { return c != null && c.mainThrottle == 0 && c.X == 0 && c.Y == 0 && c.Z == 0 && c.pitch == 0 && c.yaw == 0 && c.roll == 0 && c.pitchTrim == 0 && c.yawTrim == 0 && c.rollTrim == 0; }
        void ValidateCallbacks(Vessel v)
        {
            FlightInputCallback[] chains = { v.OnPreAutopilotUpdate, v.OnAutopilotUpdate, v.OnPostAutopilotUpdate, v.OnFlyByWire };
            for (int index = 0; index < chains.Length; index++)
            {
                FlightInputCallback chain = chains[index]; if (chain == null) continue;
                foreach (Delegate callback in chain.GetInvocationList())
                    if (callback != (Delegate)(FlightInputCallback)Quiet &&
                        (callback.Method.MetadataToken != 0x0600aa76 + index || callback.Method.Module.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf")))
                        throw new InvalidOperationException("Unowned control provider in docking seed.");
                if (hooked.Contains(v) && index == 3 && !chain.GetInvocationList().Last().Equals((Delegate)(FlightInputCallback)Quiet))
                    throw new InvalidOperationException("Docking control callback lost tail ownership.");
            }
        }
        void Attach(Vessel v)
        {
            if (hooked.Contains(v)) return;
            ValidateCallbacks(v); v.OnFlyByWire += Quiet; hooked.Add(v);
        }
        void Quiet(FlightCtrlState c)
        {
            c.mainThrottle = c.pitch = c.yaw = c.roll = c.pitchTrim = c.yawTrim = c.rollTrim = 0;
            c.X = c.Y = c.Z = c.wheelSteer = c.wheelSteerTrim = c.wheelThrottle = c.wheelThrottleTrim = 0;
            if (Report != null) Report.controlCallbacks++;
        }
        Vector3 Relative()
        {
            Rigidbody ra = a.part.Rigidbody, rb = b.part.Rigidbody;
            if (ra == null || rb == null) throw new InvalidOperationException("Port rigidbody is missing.");
            return ra.GetPointVelocity(a.nodeTransform.position) - rb.GetPointVelocity(b.nodeTransform.position);
        }
        public IEnumerator Run()
        {
            yield return new WaitForEndOfFrame();
            if (!TryImage("seed")) yield break;
            while (!disposed && phase != "done")
            {
                yield return new WaitForFixedUpdate();
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try { Step(); }
                catch (Exception error) { Fail(error); }
                finally { Report.driverTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start; }
                if (phase == "merged-image" || phase == "split-image")
                {
                    string stage = phase == "merged-image" ? "docked" : "split";
                    yield return new WaitForEndOfFrame();
                    if (!TryImage(stage)) yield break;
                    if (stage == "docked") { phase = "merged-settle"; phaseUt = Planetarium.GetUniversalTime(); }
                    else phase = "done";
                }
            }
            if (!disposed) { Dispose(); Report.status = Report.faults == 0 ? "complete-experimental" : "invalid"; Export(); }
        }
        void Step()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread || MapView.MapIsEnabled || TimeWarp.CurrentRate != 1 ||
                a == null || b == null || parts.Any(p => p == null) || !Ready(a.vessel) || !Ready(b.vessel) || a.vessel.mainBody != b.vessel.mainBody)
                throw new InvalidOperationException("Docking lost physical scene/part ownership.");
            if (Time.realtimeSinceStartup - startedWall > 90) throw new InvalidOperationException("Docking roundtrip wall timeout.");
            if (Planetarium.GetUniversalTime() - phaseUt > 65) throw new InvalidOperationException("Docking phase simulation timeout.");
            Attach(a.vessel); Attach(b.vessel); ValidateCallbacks(a.vessel); ValidateCallbacks(b.vessel);
            var live = a.vessel == b.vessel ? a.vessel.parts : a.vessel.parts.Concat(b.vessel.parts).ToList();
            if (live.Count != parts.Length || !new HashSet<uint>(live.Select(p => p.flightID)).SetEquals(parts.Select(p => p.flightID)))
                throw new InvalidOperationException("Unexpected docking part topology.");
            CheckConservation(); Report.ticks++;
            double gap = (b.nodeTransform.position - a.nodeTransform.position).magnitude;
            Report.minimumGapMeters = Math.Min(Report.minimumGapMeters, gap);
            if (phase == "stabilize") { if (++stabilizationSteps >= 2) { phase = "approach"; phaseUt = Planetarium.GetUniversalTime(); Record("admitted"); } return; }
            if (phase == "approach")
            {
                if (a.vessel == b.vessel)
                {
                    if (!a.state.Contains("Docked") || !b.state.Contains("Docked") || a.otherNode != b || b.otherNode != a)
                        throw new InvalidOperationException("Vessel merge lacks reciprocal stock docking state.");
                    Part seam = a.part.parent == b.part ? a.part : b.part.parent == a.part ? b.part : null;
                    if (seam == null || seam.attachJoint == null || seam.attachJoint.joints == null || !seam.attachJoint.joints.Any(j => j != null && j.connectedBody != null))
                        throw new InvalidOperationException("Stock cross-port physical joint was not observed.");
                    Report.actualJointObserved = true; Report.mergeCount++; Report.mergedVesselId = a.vessel.id.ToString("D");
                    mergedAt = Planetarium.GetUniversalTime(); Record("merged"); phase = "merged-image"; return;
                }
                if (Vector3.Dot(a.nodeTransform.forward, -b.nodeTransform.forward) < .995f)
                    throw new InvalidOperationException("Docking approach alignment escaped bounded seed.");
                if (a.state == "Ready" && b.state == "Ready")
                {
                    Vector3 delta = b.nodeTransform.position - a.nodeTransform.position;
                    Vector3 desired = delta.normalized * .04f;
                    Vector3 acceleration = Vector3.ClampMagnitude((desired - Relative()) * .5f, .01f);
                    var bodies = groupA.Select(p => p.Rigidbody).Where(r => r != null && !r.isKinematic).Distinct().ToArray();
                    if (bodies.Length == 0) throw new InvalidOperationException("Docker has no owned dynamic rigidbodies.");
                    Report.controlledBodies = bodies.Length;
                    foreach (Rigidbody rb in bodies) rb.AddForce(acceleration * rb.mass, ForceMode.Force);
                }
            }
            else if (phase == "merged-settle" && Planetarium.GetUniversalTime() - mergedAt >= .5)
            {
                Record("before-undock"); Report.undockCalls++; a.Undock(); phase = "separating"; phaseUt = Planetarium.GetUniversalTime(); splitDistance = gap;
            }
            else if (phase == "separating" && a.vessel != b.vessel)
            {
                bool partition = new HashSet<uint>(a.vessel.parts.Select(p => p.flightID)).SetEquals(Report.activePartIds) &&
                    new HashSet<uint>(b.vessel.parts.Select(p => p.flightID)).SetEquals(Report.targetPartIds);
                if (!partition) throw new InvalidOperationException("Undock did not restore original part partitions.");
                if (a.otherNode == b || b.otherNode == a || a.state.Contains("Docked") || b.state.Contains("Docked")) return;
                Vector3 delta = a.nodeTransform.position - b.nodeTransform.position;
                double separation = Vector3.Dot(Relative(), delta.normalized);
                if (gap >= Math.Max(1.1, splitDistance + .5) && separation > .001)
                {
                    Report.exactPartPartitionsRestored = true; Report.splitCount++; Report.splitActiveVesselId = a.vessel.id.ToString("D"); Report.splitTargetVesselId = b.vessel.id.ToString("D");
                    Report.finalGapMeters = gap; Report.finalSeparationSpeedMetersPerSecond = separation; Record("split"); phase = "split-image";
                }
            }
        }
        Dictionary<string, double> Totals()
        {
            var totals = new Dictionary<string, double>();
            foreach (Part part in parts) foreach (PartResource resource in part.Resources)
            {
                if (double.IsNaN(resource.amount) || double.IsInfinity(resource.amount) || resource.amount < 0) throw new InvalidOperationException("Invalid resource amount.");
                double total; totals.TryGetValue(resource.resourceName, out total); totals[resource.resourceName] = total + resource.amount;
            }
            return totals;
        }
        string[] Crew() { return parts.SelectMany(p => p.protoModuleCrew).Select(c => c.name).OrderBy(n => n, StringComparer.Ordinal).ToArray(); }
        static ResourceRow[] Rows(Dictionary<string, double> source) { return source.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ResourceRow { name = p.Key, amount = p.Value }).ToArray(); }
        void CheckConservation()
        {
            var current = Totals(); Report.resourcesConserved = current.Count == resources.Count && resources.All(p => current.ContainsKey(p.Key) &&
                (p.Key == "ElectricCharge" ? current[p.Key] <= p.Value + 1e-6 && p.Value-current[p.Key] <= ecRate * (Planetarium.GetUniversalTime()-Report.initialUniversalTime) + 1e-4 : Math.Abs(current[p.Key]-p.Value)<=1e-6));
            if (current.ContainsKey("ElectricCharge")) Report.electricChargeConsumed = resources["ElectricCharge"]-current["ElectricCharge"];
            Report.crewConserved = Crew().SequenceEqual(crew);
            if (!Report.resourcesConserved || !Report.crewConserved) throw new InvalidOperationException("Crew/resource conservation failed; seed exceeded explicit frozen stock-command EC budget or changed other resources.");
        }
        void Record(string eventPhase)
        {
            int joint = 0; Part seam = a.part.parent == b.part ? a.part : b.part.parent == a.part ? b.part : null;
            if (seam != null && seam.attachJoint != null && seam.attachJoint.joints != null) { var j = seam.attachJoint.joints.FirstOrDefault(x => x != null); if (j != null) joint = j.GetInstanceID(); }
            events.Add(new EventRow { phase = eventPhase, universalTime = Planetarium.GetUniversalTime(), wallSeconds = Time.realtimeSinceStartup - startedWall,
                activeVesselId = a.vessel.id.ToString("D"), targetVesselId = b.vessel.id.ToString("D"), activeParts = a.vessel.parts.Count, targetParts = b.vessel.parts.Count,
                activePortState = a.state, targetPortState = b.state, jointId = joint, distanceMeters = (a.nodeTransform.position - b.nodeTransform.position).magnitude,
                relativeSpeedMetersPerSecond = Relative().magnitude, alignmentDot = Vector3.Dot(a.nodeTransform.forward, -b.nodeTransform.forward) });
        }
        bool TryImage(string stage)
        {
            Texture2D image = null; RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = null;
                image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0); image.Apply(false, false);
                string file = stage + ".png"; File.WriteAllBytes(Path.Combine(directory, file), image.EncodeToPNG()); images.Add(file); return true;
            }
            catch (Exception error) { Fail(error); return false; }
            finally { RenderTexture.active = previous; if (image != null) UnityEngine.Object.Destroy(image); }
        }
        void Fail(Exception error) { Report.faults++; Report.reason = error.GetType().Name + ": " + error.Message; Dispose(); Report.status = "invalid"; Export(); }
        public void Export()
        {
            Report.setup = setup.ToArray(); Report.events = events.ToArray(); Report.screenshots = images.ToArray();
            if (parts.All(p => p != null)) Report.finalResources = Rows(Totals());
            Report.sourceUnchanged = ScaleCheckpointLoadState.SourceUnchanged();
            if (!Report.sourceUnchanged) { Report.faults++; Report.status = "invalid"; }
            File.WriteAllText(Path.Combine(directory, "docking.json"), ReportJson.Encode(Report));
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (Vessel vessel in hooked) if (!ReferenceEquals(vessel, null)) vessel.OnFlyByWire -= Quiet;
            Report.hooksRemoved = hooked.All(v => ReferenceEquals(v, null) || v.OnFlyByWire == null || !v.OnFlyByWire.GetInvocationList().Contains((Delegate)(FlightInputCallback)Quiet));
            if (lockOwned) { InputLockManager.RemoveControlLock(Lock); lockOwned = false; }
            Report.controlLockRemoved = !lockOwned;
            Report.cleanupStatus = Report.hooksRemoved && Report.controlLockRemoved ? "removed-owned-controls" : "cleanup-error";
            if (Report.cleanupStatus == "cleanup-error") Report.faults++;
        }
    }
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class DockingQualificationAddon : MonoBehaviour
    {
        DockingQualification owner;
        string directory;
        bool finished;
        IEnumerator Start()
        {
            string[] args = Environment.GetCommandLineArgs(); if (!DockingQualification.Requested(args)) yield break;
            directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData", "docking-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); double start = Time.realtimeSinceStartup;
            while (!ScaleCheckpointLoadState.Ready && Time.realtimeSinceStartup - start < 60) yield return null;
            try { owner = DockingQualification.Create(args, directory); }
            catch (Exception error) { Rejected(args, error); }
            if (owner == null) yield break;
            yield return owner.Run();
            finished = true;
            Application.Quit(owner.Report.status == "complete-experimental" ? 0 : 2);
        }
        void Rejected(string[] args, Exception error)
        {
            string runId = args.Where(a => a.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).Select(a => a.Substring("--continuum-research-run=".Length)).FirstOrDefault();
            var report = new DockingQualification.ReportData { runId = runId, processId = System.Diagnostics.Process.GetCurrentProcess().Id, sourceSha256 = ScaleCheckpointLoadState.SourceSha256,
                status = "invalid", faults = 1, reason = error.GetType().Name + ": " + error.Message, cleanupStatus = "no-owned-controls", hooksRemoved = true, controlLockRemoved = true };
            File.WriteAllText(Path.Combine(directory, "docking.json"), ReportJson.Encode(report)); finished = true; Application.Quit(2);
        }
        void OnDestroy()
        {
            if (owner == null) return;
            owner.Dispose();
            if (!finished) { owner.Report.status = "interrupted"; owner.Report.reason = "Owning component destroyed before terminal sequence."; owner.Export(); }
        }
    }
}
