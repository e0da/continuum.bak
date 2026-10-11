using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class DockingQualification : IDisposable
    {
        const string Lock = "continuum.docking.roundtrip";
        readonly ModuleDockingNode a, b;
        readonly Part[] groupA, groupB, parts;
        const string InputOwner = "continuum.docking.input";
        static DockingQualification inputOwner;
        readonly Harmony inputHook = new Harmony(InputOwner);
        readonly System.Reflection.MethodInfo inputFeed = AccessTools.DeclaredMethod(typeof(Vessel), "FeedInputFeed");
        readonly Dictionary<Vessel, ControlRow[]> controls = new Dictionary<Vessel, ControlRow[]>();
        readonly List<ControlRow> controlRows = new List<ControlRow>();
        sealed class CameraPose { public Camera camera; public Vector3 position; public Quaternion rotation; }
        CameraPose[] cameraPoses;
        bool cameraHook;
        static OrbitRow[] preparedOrbits;
        static readonly System.Reflection.FieldInfo TransmitterQueue = typeof(ModuleDataTransmitter).GetField("transmissionQueue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        readonly Dictionary<string, double> resources;
        readonly string[] crew;
        readonly double ecRate;
        readonly double commandEcRate, dockingEcRate;
        readonly int activeMotorTargetBits, targetMotorTargetBits;
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
        [Serializable] public sealed class ControlRow { public string vesselId, chain; public int vesselInstanceId; public string[] suspendedCallbacks, finalCallbacks; public bool destroyed; }
        [Serializable] public sealed class OrbitRow { public string vesselId; public double radiusBefore, radiusAfter, speedBefore, speedAfter, radialBefore, radialAfter; }
        [Serializable] public sealed class EventRow
        {
            public uint activeLinkedPortId, targetLinkedPortId;
            public string phase, activeVesselId, targetVesselId, activePortState, targetPortState;
            public double universalTime, wallSeconds, distanceMeters, relativeSpeedMetersPerSecond, alignmentDot;
            public double sunHorizonClearanceMeters;
            public int activeParts, targetParts, jointId;
        }
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-docking-roundtrip/v1", status = "running", cleanupStatus = "owned", reason, runId, sourceSha256;
            public string activeVesselId, targetVesselId, mergedVesselId, splitActiveVesselId, splitTargetVesselId;
            public string scope = "Paired checkpoint with optional declared alignment; scoped replacement of input delivery with quiet controls and unchanged provider registrations; bounded distributed diagnostic translation forces; actual stock capture/docking/undocking. Not RCS, rendezvous, fuel controller or complete mission qualification.";
            public int processId, initialActiveParts, initialTargetParts, faults, mergeCount, undockCalls, splitCount, controlledBodies, controlCallbacks, ticks, idleStockDataTransmitters;
            public uint activePortId, targetPortId;
            public bool preparationRequested, collisionsRestored, zeroCrewScope, initialSeparate, actualJointObserved, exactPartPartitionsRestored, crewConserved, resourcesConserved, sourceUnchanged, hooksRemoved, controlLockRemoved;
            public bool schedulingRestored;
            public bool inputIngressRestored;
            public bool cameraRestored;
            public int callbackRegistrationMutations = 0;
            public int cameraCallbacks;
            public double maximumDeltaTime, physicsFrameLimit, fixedDeltaTime;
            public int screenWidth, screenHeight;
            public double initialUniversalTime, electricChargeRateBudget, electricChargeCommandRateBudget, electricChargeDockingMotorRateBudget, electricChargeConsumed, minimumGapMeters, finalGapMeters, finalSeparationSpeedMetersPerSecond, maxAccelerationMetersPerSecondSquared = .01;
            public long driverTicks, clockFrequency = System.Diagnostics.Stopwatch.Frequency;
            public uint[] activePartIds, targetPartIds;
            public uint[] splitActivePartIds, splitTargetPartIds;
            public ResourceRow[] initialResources, finalResources;
            public string[] crewNames, screenshots;
            public EventRow[] events;
            public SetupRow[] setup;
            public ControlRow[] controlLeases;
            public OrbitRow[] orbitalPreparation;
        }
        public ReportData Report { get; private set; }
        public static bool Requested(string[] args) { return args.Any(arg => arg.StartsWith("--continuum-docking=", StringComparison.Ordinal)); }
        public static bool PhysicalPortsReady(string[] args)
        {
            Guid active = Guid.Parse(Value(args, "--continuum-docking-active-vessel")), target = Guid.Parse(Value(args, "--continuum-docking-target-vessel"));
            Vessel va = FlightGlobals.Vessels.FirstOrDefault(v => v != null && v.id == active), vb = FlightGlobals.Vessels.FirstOrDefault(v => v != null && v.id == target);
            if (!Ready(va) || !Ready(vb)) return false;
            ModuleDockingNode pa = Port(va, uint.Parse(Value(args, "--continuum-docking-active-port"), CultureInfo.InvariantCulture));
            ModuleDockingNode pb = Port(vb, uint.Parse(Value(args, "--continuum-docking-target-port"), CultureInfo.InvariantCulture));
            return pa.nodeTransform != null && pb.nodeTransform != null && pa.fsm != null && pb.fsm != null && pa.fsm.Started && pb.fsm.Started &&
                ReferenceEquals(pa.fsm.CurrentState, pa.st_ready) && ReferenceEquals(pb.fsm.CurrentState, pb.st_ready) &&
                !pa.IsRotating && !pb.IsRotating && pa.state == "Ready" && pb.state == "Ready";
        }
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
        static Orbit SeedOrbit(ProtoVessel proto, CelestialBody body)
        {
            OrbitSnapshot s = proto.orbitSnapShot;
            return new Orbit(s.inclination, s.eccentricity, s.semiMajorAxis, s.LAN, s.argOfPeriapsis, s.meanAnomalyAtEpoch, s.epoch, body);
        }
        public static void PrepareSeed(Game game, string[] args)
        {
            preparedOrbits = null;
            if (!Requested(args) || !args.Contains("--continuum-docking-prepare-pair")) return;
            Guid aid = Guid.Parse(Value(args, "--continuum-docking-active-vessel")), bid = Guid.Parse(Value(args, "--continuum-docking-target-vessel"));
            var pair = new[] { game.flightState.protoVessels.Single(v => v.vesselID == aid), game.flightState.protoVessels.Single(v => v.vesselID == bid) };
            CelestialBody body = FlightGlobals.Bodies[pair[0].orbitSnapShot.ReferenceBodyIndex];
            if (body.bodyName != "Kerbin" || body.orbit.referenceBody != Planetarium.fetch.Sun || pair.Any(v => v.landed || v.splashed || v.orbitSnapShot.ReferenceBodyIndex != pair[0].orbitSnapShot.ReferenceBodyIndex))
                throw new InvalidOperationException("Paired Sunward preparation requires two Kerbin orbital seeds.");
            double epoch = game.flightState.universalTime;
            var epochZup = new Planetarium.CelestialFrame(); Planetarium.ZupAtT(epoch, body, ref epochZup);
            Vector3d solar = epochZup.WorldToLocal(Planetarium.Zup.LocalToWorld(-body.orbit.getRelativePositionAtUT(epoch))).normalized;
            Vector3d p0, v0; SeedOrbit(pair[0], body).GetOrbitalStateVectorsAtUT(epoch, out p0, out v0);
            Vector3d from = p0.normalized, axis = Vector3d.Cross(from, solar);
            double sine = axis.magnitude, cosine = Vector3d.Dot(from, solar);
            if (sine > 1e-12) axis /= sine;
            else if (cosine < 0) axis = Vector3d.Cross(from, Math.Abs(from.x) < .9 ? Vector3d.right : Vector3d.up).normalized;
            Func<Vector3d, Vector3d> rotate = value => sine > 1e-12 ? value * cosine + Vector3d.Cross(axis, value) * sine + axis * Vector3d.Dot(axis, value) * (1 - cosine) : cosine < 0 ? -value + 2 * axis * Vector3d.Dot(axis, value) : value;
            var rows = new List<OrbitRow>();
            foreach (ProtoVessel proto in pair)
            {
                Orbit orbit = SeedOrbit(proto, body); Vector3d position, velocity; orbit.GetOrbitalStateVectorsAtUT(epoch, out position, out velocity);
                var row = new OrbitRow { vesselId = proto.vesselID.ToString("D"), radiusBefore = position.magnitude, speedBefore = velocity.magnitude, radialBefore = Vector3d.Dot(position.normalized, velocity) };
                position = rotate(position); velocity = rotate(velocity);
                orbit.UpdateFromStateVectors(Planetarium.Zup.WorldToLocal(epochZup.LocalToWorld(position)), Planetarium.Zup.WorldToLocal(epochZup.LocalToWorld(velocity)), body, epoch);
                orbit.GetOrbitalStateVectorsAtUT(epoch, out position, out velocity);
                row.radiusAfter = position.magnitude; row.speedAfter = velocity.magnitude; row.radialAfter = Vector3d.Dot(position.normalized, velocity);
                if (Math.Abs(row.radiusAfter - row.radiusBefore) > .0001 || Math.Abs(row.speedAfter - row.speedBefore) > .0001 || Math.Abs(row.radialAfter - row.radialBefore) > .0001)
                    throw new InvalidOperationException("Paired Sunward orbital invariants failed.");
                proto.orbitSnapShot = new OrbitSnapshot(orbit); proto.altitude = position.magnitude - body.Radius;
                var frame = new Planetarium.CelestialFrame(); Planetarium.CelestialFrame.PlanetaryFrame(0, 90, body.initialRotation + (body.rotationPeriod == 0 ? 0 : 360 * epoch / body.rotationPeriod), ref frame);
                Vector3d local = frame.WorldToLocal(epochZup.LocalToWorld(position)).normalized;
                proto.latitude = Math.Asin(local.z) * 180 / Math.PI; proto.longitude = Math.Atan2(local.y, local.x) * 180 / Math.PI;
                rows.Add(row);
            }
            preparedOrbits = rows.ToArray();
        }
        DockingQualification(string[] args, Vessel va, uint pa, Vessel vb, uint pb, string directory)
        {
            this.directory = directory;
            if (va == vb || va != FlightGlobals.ActiveVessel || !Ready(va) || !Ready(vb) || va.mainBody != vb.mainBody || va.mainBody.bodyName != "Kerbin" ||
                va.altitude < va.mainBody.atmosphereDepth || vb.altitude < vb.mainBody.atmosphereDepth || MapView.MapIsEnabled || FlightDriver.Pause || TimeWarp.CurrentRate != 1 ||
                !ScaleCheckpointLoadState.Ready || !ScaleCheckpointLoadState.SourceUnchanged())
                throw new InvalidOperationException("Docking needs an owned, normal-time paired physical Kerbin orbital checkpoint.");
            a = Port(va, pa); b = Port(vb, pb);
            if (!PhysicalPortsReady(args) || float.IsNaN(a.targetAngle) || float.IsInfinity(a.targetAngle) || float.IsNaN(b.targetAngle) || float.IsInfinity(b.targetAngle))
                throw new InvalidOperationException("Docking motors must be initialized, idle and finite.");
            activeMotorTargetBits = BitConverter.ToInt32(BitConverter.GetBytes(a.targetAngle), 0);
            targetMotorTargetBits = BitConverter.ToInt32(BitConverter.GetBytes(b.targetAngle), 0);
            groupA = va.parts.ToArray(); groupB = vb.parts.ToArray(); parts = groupA.Concat(groupB).ToArray();
            if (parts.Select(p => p.flightID).Distinct().Count() != parts.Length || a.nodeTransform == null || b.nodeTransform == null ||
                a.state != "Ready" || b.state != "Ready" || a.otherNode != null || b.otherNode != null || a.dockedPartUId != 0 || b.dockedPartUId != 0 ||
                a.nodeTypes == null || b.nodeTypes == null || !a.nodeTypes.Overlaps(b.nodeTypes) || a.gendered != b.gendered || (a.gendered && a.genderFemale == b.genderFemale))
                throw new InvalidOperationException("Distinct Ready compatible docking ports and unique part identities are required.");
            if (va.Autopilot.Enabled || vb.Autopilot.Enabled || va.GroupOverride != 0 || vb.GroupOverride != 0 || va.ActionGroups[KSPActionGroup.SAS] || vb.ActionGroups[KSPActionGroup.SAS] || va.ActionGroups[KSPActionGroup.RCS] || vb.ActionGroups[KSPActionGroup.RCS] ||
                parts.Any(p => p.Modules.OfType<ModuleEngines>().Any(e => e.EngineIgnited)) || !Zero(va.ctrlState) || !Zero(vb.ctrlState))
                throw new InvalidOperationException("Seed must have idle controls, SAS/RCS off and no ignited engines.");
            foreach (Part part in parts) foreach (PartModule module in part.Modules)
                foreach (ModuleResource input in module.resHandler.inputResources)
                {
                    if (module is ModuleReactionWheel && ((ModuleReactionWheel)module).wheelState == ModuleReactionWheel.WheelState.Disabled) continue;
                    if (module.GetType() == typeof(ModuleDataTransmitter) && IdleTransmitter((ModuleDataTransmitter)module)) continue;
                    if (input.rate < 0 || double.IsNaN(input.rate) || double.IsInfinity(input.rate) ||
                        (input.rate != 0 && (input.name != "ElectricCharge" || (!(module is ModuleCommand) && !(module is ModuleDockingNode)))))
                        throw new InvalidOperationException("Unsupported resource activity: part=" + part.flightID + ", module=" + module.GetType().FullName + ", resource=" + input.name + ", rate=" + input.rate.ToString("R", CultureInfo.InvariantCulture));
                    if (module is ModuleCommand) commandEcRate += input.rate;
                    if (module is ModuleDockingNode) dockingEcRate += input.rate;
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
                zeroCrewScope = crew.Length == 0, initialUniversalTime = Planetarium.GetUniversalTime(), electricChargeRateBudget = ecRate,
                electricChargeCommandRateBudget = commandEcRate, electricChargeDockingMotorRateBudget = dockingEcRate };
            Report.orbitalPreparation = preparedOrbits;
            ValidateIdleModules();
            Report.idleStockDataTransmitters = parts.Sum(p => p.Modules.OfType<ModuleDataTransmitter>().Count());
            Report.maximumDeltaTime = Time.maximumDeltaTime; Report.physicsFrameLimit = GameSettings.PHYSICS_FRAME_DT_LIMIT;
            Report.fixedDeltaTime = Time.fixedDeltaTime; Report.screenWidth = Screen.width; Report.screenHeight = Screen.height;
            startedWall = Time.realtimeSinceStartup; phaseUt = Planetarium.GetUniversalTime();
            try
            {
                if (inputOwner != null || inputFeed == null || Harmony.GetPatchInfo(inputFeed)?.Owners.Any() == true)
                    throw new InvalidOperationException("Docking input delivery requires one unpatched owner.");
                InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.TIMEWARP, Lock); lockOwned = true;
                Attach(va); Attach(vb); inputOwner = this;
                inputHook.Patch(inputFeed, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(DockingQualification), "InputPrefix")));
                if (FlightCamera.fetch == null || FlightCamera.fetch.cameras == null) throw new InvalidOperationException("Docking camera missing.");
                cameraPoses = FlightCamera.fetch.cameras.Where(c => c != null).Distinct().Select(c => new CameraPose { camera = c, position = c.transform.localPosition, rotation = c.transform.localRotation }).ToArray();
                if (cameraPoses.Length == 0) throw new InvalidOperationException("Docking rendered cameras missing.");
                Camera.onPreCull += RenderCamera; cameraHook = true;
                Record("seed");
            }
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
        static bool IdleTransmitter(ModuleDataTransmitter transmitter)
        {
            if (TransmitterQueue == null) throw new InvalidOperationException("Stock transmitter queue binding missing.");
            var queue = TransmitterQueue.GetValue(transmitter) as List<ScienceData>;
            return queue != null && queue.Count == 0 && !transmitter.IsBusy();
        }
        void ValidateIdleModules()
        {
            if (parts.Any(p => p.Modules.OfType<ModuleReactionWheel>().Any(w => w.wheelState != ModuleReactionWheel.WheelState.Disabled) ||
                p.Modules.OfType<ModuleDataTransmitter>().Any(t => t.GetType() != typeof(ModuleDataTransmitter) || !IdleTransmitter(t))))
                throw new InvalidOperationException("An idle wheel or stock data transmitter became active.");
        }
        void ValidateCallbacks(Vessel v)
        {
            if (!controls.ContainsKey(v) || !ReferenceEquals(inputOwner, this) || Harmony.GetPatchInfo(inputFeed)?.Owners.SingleOrDefault() != InputOwner)
                throw new InvalidOperationException("Docking lost its input-delivery owner.");
        }
        static FlightInputCallback[] Chains(Vessel v) { return new[] { v.OnPreAutopilotUpdate, v.OnAutopilotUpdate, v.OnPostAutopilotUpdate, v.OnFlyByWire }; }
        static string[] CallbackNames(FlightInputCallback chain)
        {
            return chain == null ? new string[0] : chain.GetInvocationList().Select(callback =>
                callback.Method.DeclaringType.FullName + "." + callback.Method.Name + ":" + callback.Method.Module.ModuleVersionId.ToString("D") + ":" +
                callback.Method.MetadataToken.ToString("X8", CultureInfo.InvariantCulture) + ":target=" + (callback.Target == null ? "static" : callback.Target.GetType().FullName)).ToArray();
        }
        void Attach(Vessel v)
        {
            if (controls.ContainsKey(v)) return;
            FlightInputCallback[] prior = Chains(v);
            var rows = new ControlRow[prior.Length];
            string[] names = { "pre-autopilot", "autopilot", "post-autopilot", "fly-by-wire" };
            for (int index = 0; index < prior.Length; index++)
                rows[index] = new ControlRow { vesselId = v.id.ToString("D"), vesselInstanceId = v.GetInstanceID(), chain = names[index],
                    suspendedCallbacks = CallbackNames(prior[index]) };
            controlRows.AddRange(rows);
            controls.Add(v, rows);
        }
        static bool InputPrefix(Vessel __instance)
        {
            DockingQualification owner = inputOwner;
            if (owner == null || owner.disposed || !owner.parts.Any(p => p != null && p.vessel == __instance)) return true;
            try
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != owner.thread) throw new InvalidOperationException("Docking input delivery left the owned thread.");
                owner.Attach(__instance); owner.Quiet(__instance.ctrlState);
                if (__instance.loaded && !__instance.packed && !__instance.HoldPhysics && __instance.IsControllable)
                    __instance.rootPart.propagateControlUpdate(__instance.ctrlState);
            }
            catch (Exception error) { owner.Fail(error); }
            return false;
        }
        void Quiet(FlightCtrlState c)
        {
            c.mainThrottle = c.pitch = c.yaw = c.roll = c.pitchTrim = c.yawTrim = c.rollTrim = 0;
            c.X = c.Y = c.Z = c.wheelSteer = c.wheelSteerTrim = c.wheelThrottle = c.wheelThrottleTrim = 0;
            if (Report != null) Report.controlCallbacks++;
        }
        double HorizonClearance()
        {
            Vector3d position = a.vessel.GetWorldPos3D(), radial = position - a.vessel.mainBody.position;
            Vector3d sun = (Planetarium.fetch.Sun.position - position).normalized;
            double along = -Vector3d.Dot(radial, sun);
            return (along > 0 ? (radial + along * sun).magnitude : radial.magnitude) - a.vessel.mainBody.Radius;
        }
        void RenderCamera(Camera camera)
        {
            if (disposed || cameraPoses == null || !cameraPoses.Any(p => p.camera == camera)) return;
            try
            {
                Vector3 sun = (Vector3)(Planetarium.fetch.Sun.position - a.vessel.GetWorldPos3D()).normalized;
                Vector3 up = Vector3.ProjectOnPlane(a.nodeTransform.forward, sun).normalized;
                if (up.sqrMagnitude < .9 || (Report.preparationRequested && HorizonClearance() <= 0)) throw new InvalidOperationException("Docking inspection lighting invalid.");
                Vector3 center = (a.nodeTransform.position + b.nodeTransform.position) * .5f;
                camera.transform.position = center + sun * 12f; camera.transform.rotation = Quaternion.LookRotation(-sun, up);
                Report.cameraCallbacks++;
            }
            catch (Exception error) { Fail(error); }
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
            ValidateIdleModules();
            if (a.IsRotating || b.IsRotating || activeMotorTargetBits != BitConverter.ToInt32(BitConverter.GetBytes(a.targetAngle), 0) ||
                targetMotorTargetBits != BitConverter.ToInt32(BitConverter.GetBytes(b.targetAngle), 0))
                throw new InvalidOperationException("An unowned docking motor command changed the quiet workload.");
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
                    Vector3 desired = delta.normalized * .1f;
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
                    Report.splitActivePartIds = a.vessel.parts.Select(p => p.flightID).ToArray(); Report.splitTargetPartIds = b.vessel.parts.Select(p => p.flightID).ToArray();
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
                relativeSpeedMetersPerSecond = Relative().magnitude, alignmentDot = Vector3.Dot(a.nodeTransform.forward, -b.nodeTransform.forward),
                sunHorizonClearanceMeters = HorizonClearance(),
                activeLinkedPortId = a.otherNode == null ? 0 : a.otherNode.part.flightID, targetLinkedPortId = b.otherNode == null ? 0 : b.otherNode.part.flightID });
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
            Report.setup = setup.ToArray(); Report.events = events.ToArray(); Report.screenshots = images.ToArray(); Report.controlLeases = controlRows.ToArray();
            if (parts.All(p => p != null)) Report.finalResources = Rows(Totals());
            Report.sourceUnchanged = ScaleCheckpointLoadState.SourceUnchanged();
            if (!Report.sourceUnchanged) { Report.faults++; Report.status = "invalid"; }
            File.WriteAllText(Path.Combine(directory, "docking.json"), ReportJson.Encode(Report));
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            inputHook.UnpatchAll(InputOwner);
            Report.inputIngressRestored = inputFeed != null && Harmony.GetPatchInfo(inputFeed)?.Owners.Contains(InputOwner) != true;
            if (ReferenceEquals(inputOwner, this)) inputOwner = null;
            if (cameraHook) { Camera.onPreCull -= RenderCamera; cameraHook = false; }
            Report.cameraRestored = !cameraHook;
            if (cameraPoses != null) foreach (CameraPose pose in cameraPoses)
            {
                if (pose.camera == null) { Report.cameraRestored = false; continue; }
                pose.camera.transform.localPosition = pose.position; pose.camera.transform.localRotation = pose.rotation;
                Report.cameraRestored &= pose.camera.transform.localPosition == pose.position && pose.camera.transform.localRotation == pose.rotation;
            }
            foreach (var lease in controls)
            {
                bool destroyed = lease.Key == null;
                FlightInputCallback[] current = destroyed ? null : Chains(lease.Key);
                for (int index = 0; index < lease.Value.Length; index++)
                { lease.Value[index].destroyed = destroyed; lease.Value[index].finalCallbacks = destroyed ? null : CallbackNames(current[index]); }
            }
            Report.hooksRemoved = Report.inputIngressRestored;
            if (lockOwned) { InputLockManager.RemoveControlLock(Lock); lockOwned = false; }
            Report.controlLockRemoved = !lockOwned;
            Report.cleanupStatus = Report.hooksRemoved && Report.controlLockRemoved && Report.cameraRestored ? "removed-owned-controls" : "cleanup-error";
            if (Report.cleanupStatus == "cleanup-error") Report.faults++;
        }
    }
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class DockingQualificationAddon : MonoBehaviour
    {
        DockingQualification owner;
        string directory;
        bool finished;
        bool schedulingApplied;
        float priorMaximumDelta, priorPhysicsLimit;
        void RestoreScheduling()
        {
            if (!schedulingApplied) return;
            Time.maximumDeltaTime = priorMaximumDelta; GameSettings.PHYSICS_FRAME_DT_LIMIT = priorPhysicsLimit;
            if (Time.maximumDeltaTime != priorMaximumDelta || GameSettings.PHYSICS_FRAME_DT_LIMIT != priorPhysicsLimit)
                throw new InvalidOperationException("Docking scheduling restoration failed.");
            schedulingApplied = false;
        }
        IEnumerator Start()
        {
            string[] args = Environment.GetCommandLineArgs(); if (!DockingQualification.Requested(args)) yield break;
            directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData", "docking-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); double start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 60)
            {
                bool ready = false;
                try { ready = ScaleCheckpointLoadState.Ready && DockingQualification.PhysicalPortsReady(args); }
                catch (Exception error) { Rejected(args, error); }
                if (finished) yield break;
                if (ready) break;
                yield return null;
            }
            try
            {
                const string prefix = "--continuum-flight-catchup=";
                string value = args.Single(arg => arg.StartsWith(prefix, StringComparison.Ordinal)).Substring(prefix.Length);
                float limit = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (float.IsNaN(limit) || float.IsInfinity(limit) || limit < .001 || limit > 1) throw new InvalidOperationException("Docking catchup limit invalid.");
                priorMaximumDelta = Time.maximumDeltaTime; priorPhysicsLimit = GameSettings.PHYSICS_FRAME_DT_LIMIT; schedulingApplied = true;
                Time.maximumDeltaTime = limit; GameSettings.PHYSICS_FRAME_DT_LIMIT = limit;
                FlightDiagnosticControls.Apply();
                owner = DockingQualification.Create(args, directory);
                if (FlightCamera.fetch != null) FlightCamera.fetch.SetDistanceImmediate(12f);
            }
            catch (Exception error) { Rejected(args, error); }
            if (owner == null) yield break;
            yield return owner.Run();
            RestoreScheduling();
            FlightDiagnosticControls.Restore();
            owner.Report.schedulingRestored = !schedulingApplied; owner.Export();
            finished = true;
            Application.Quit(owner.Report.status == "complete-experimental" ? 0 : 2);
        }
        void Rejected(string[] args, Exception error)
        {
            RestoreScheduling();
            FlightDiagnosticControls.Restore();
            string runId = args.Where(a => a.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).Select(a => a.Substring("--continuum-research-run=".Length)).FirstOrDefault();
            var report = new DockingQualification.ReportData { runId = runId, processId = System.Diagnostics.Process.GetCurrentProcess().Id, sourceSha256 = ScaleCheckpointLoadState.SourceSha256,
                status = "invalid", faults = 1, reason = error.GetType().Name + ": " + error.Message, cleanupStatus = "no-owned-controls", hooksRemoved = true, controlLockRemoved = true };
            File.WriteAllText(Path.Combine(directory, "docking.json"), ReportJson.Encode(report)); finished = true; Application.Quit(2);
        }
        void OnDestroy()
        {
            RestoreScheduling();
            FlightDiagnosticControls.Restore();
            if (owner == null) return;
            owner.Dispose();
            if (!finished) { owner.Report.status = "interrupted"; owner.Report.reason = "Owning component destroyed before terminal sequence."; owner.Export(); }
        }
    }
}
