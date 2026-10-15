using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using CommNet;
using UnityEngine;
using UnityEngine.LowLevel;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;
using UpdateLoop = UnityEngine.PlayerLoop.Update;

namespace KspContinuum
{
    public sealed class OwnedClockKspBridge : IDisposable
    {
        const string Owner = "continuum.owned-clock-flight";
        const string StockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const string StockMvid = "10657063-2fc3-43a7-84fa-d39e75e877bf";
        const string MechSha = "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929";
        const int MaximumBodies = 4096, MaximumParts = 8192, MaximumHooks = 8192;
        sealed class OwnedFixed { }
        sealed class BeforeOwnedUpdate { }
        static OwnedClockKspBridge active;
        static readonly Dictionary<MethodInfo, MethodInfo> Routes = BuildRoutes();
        static readonly Dictionary<FieldInfo, MethodInfo> FieldRoutes = BuildFieldRoutes();
        static readonly MethodInfo Transpiler = AccessTools.DeclaredMethod(typeof(OwnedClockKspBridge), "Transform");
        static readonly MethodInfo InputMethod = AccessTools.DeclaredMethod(typeof(FlightInputHandler), "FixedUpdate", Type.EmptyTypes);
        static readonly MethodInfo SasMethod = AccessTools.DeclaredMethod(typeof(ModuleSAS), "FixedUpdate", Type.EmptyTypes);
        static readonly MethodInfo ProtoSetPosition=AccessTools.DeclaredMethod(typeof(Vessel),"SetPosition",new[]{typeof(Vector3d),typeof(bool)});
        static readonly MethodInfo ProtoSetRotation=AccessTools.DeclaredMethod(typeof(Vessel),"SetRotation",new[]{typeof(Quaternion),typeof(bool)});
        static readonly MethodInfo ProtoSetVelocity=AccessTools.DeclaredMethod(typeof(Vessel),"SetWorldVelocity",new[]{typeof(Vector3d)});
        static readonly MethodInfo ProtoChangeVelocity=AccessTools.DeclaredMethod(typeof(Vessel),"ChangeWorldVelocity",new[]{typeof(Vector3d)});
        static readonly FieldInfo StatsComplete = AccessTools.Field(typeof(VesselPrecalculate), "firstStatsRunComplete");
        static readonly FieldInfo WarpCurrent = AccessTools.Field(typeof(TimeWarp), "curr_rate");
        static readonly FieldInfo WarpPrevious = AccessTools.Field(typeof(TimeWarp), "last_rate");
        static readonly FieldInfo WarpAutoEngaged = AccessTools.Field(typeof(TimeWarp), "autoWarpEngaged");
        static readonly FieldInfo CargoSettling = AccessTools.DeclaredField(typeof(ModuleCargoPart), "beingSettled");
        static readonly FieldInfo CargoAttached = AccessTools.DeclaredField(typeof(ModuleCargoPart), "beingAttached");
        static readonly FieldInfo CargoConstruction = AccessTools.DeclaredField(typeof(ModuleCargoPart), "inConstructionMode");
        static readonly FieldInfo ThrottleAnimation = AccessTools.DeclaredField(typeof(FXModuleAnimateThrottle), "anim");
        static readonly FieldInfo ThrottleEngine = AccessTools.DeclaredField(typeof(FXModuleAnimateThrottle), "engineReference");
        readonly OwnedClockFlightService service;
        readonly Vessel vessel;
        readonly bool measureCosts;
        OwnedClockThermalHandback thermalHandback;
        Transform controlReference;
        Part controlReferencePart;
        Transform controlReferenceBody;
        ControlReferenceNode[] controlReferenceChain;
        readonly HashSet<Transform> controlReferenceTransforms = new HashSet<Transform>();
        sealed class ControlReferenceNode
        {
            internal Transform transform, parent;
            internal Vector3 position, scale;
            internal Quaternion rotation;
        }
        readonly CelestialBody centralBody;
        readonly OwnedClockFlightBinding binding;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly Harmony harmony = new Harmony(Owner);
        readonly List<MethodBase> attempted = new List<MethodBase>();
        readonly Dictionary<MethodBase, Hook> hooks = new Dictionary<MethodBase, Hook>();
        readonly Dictionary<MethodBase, Callsite> callsites = new Dictionary<MethodBase, Callsite>();
        readonly Dictionary<Rigidbody, BodyRow> bodyLookup = new Dictionary<Rigidbody, BodyRow>();
        readonly Dictionary<Transform, BodyRow> transformLookup = new Dictionary<Transform, BodyRow>();
        readonly Dictionary<Part, PartRow> partLookup = new Dictionary<Part, PartRow>();
        readonly Dictionary<PartModule, ProviderRow> providerLookup = new Dictionary<PartModule, ProviderRow>();
        readonly PlayerLoopSystem.UpdateFunction beforeUpdate;
        PlayerLoopSystem originalFixed, ownedFixed;
        BodyRow[] bodies;
        BodyRow[] publicationOrder;
        PartRow[] parts;
        ResourceRow[] resources;
        ProviderRow[] providers;
        HarmonyPatchStateCertificate certificate;
        float originalTimeScale, originalFixedDelta;
        double originalPlanetariumDelta, originalPlanetariumScale, inputDelta = .02;
        TimeWarp.Modes originalWarpMode;
        int originalWarpIndex;
        float originalWarpCurrent, originalWarpPrevious;
        double publishedUt, publishedRuntimeTime;
        OwnedRuntimeStamp publishedStamp;
        OwnedRuntimeRegionState region;
        Vec regionOmega;
        Vec centerBodyPosition;
        bool prepared, installed, loopInstalled, publishing, collecting, restoring, disposed, nativeMutation;
        bool checkpointPublication;

        [Serializable] public sealed class SourceRow
        { public string declaringType, method, ilSha256, assemblySha256, assemblyMvid, category; public int metadataToken, sites; public bool installed, removed; }
        [Serializable] public sealed class ReportData
        {
            public double independentWarpDemand = 1, stockSchedulerWarpRate = 1;
            public string warpScope = "Independent demand belongs to the numeric timeline. Original scheduler rate/index remain 1x/0 to avoid stock rails transitions; the owned panel displays actual demand.";
            public string schema = "continuum-owned-clock-ksp-bridge/v1", status = "not-installed", cleanupStatus = "not-installed", detail;
            public string stockSha256 = StockSha, stockMvid = StockMvid, fixedScope = "Entire admitted Unity FixedUpdate subtree, including native physics and all script/delayed-fixed callbacks.";
            public string publicationScope = "One retained worker bank supplies committed UT, celestial/orbit views, original native/Transform proxies, resources, temperature and selected provider read facades before rendered script Update. This is acquired-model compatibility, not stock provider/contact equivalence.";
            public string providerMutationScope = "Declared hibernation/engine/deployment/transmission/cargo/variant operations and throttle demand under idle animation hand back before original execution. Other GUI/reflection edits to retained provider fields are detected at the next pre-publication check; transient arbitrary writes are not universally proven absent.";
            public int partCount, bodyCount, resourceCount, providerCount, patchedMethods, routedSites;
            public int discoveryMethods,discoveryInstructions,discoverySelectedMethods,discoveryRoutedSites,harmonyReconstructions;
            public long discoveryTicks,rawDiscoveryTicks,fullInstructionVerificationTicks;
            public long publicationTicks,aliasCheckTicks,providerCheckTicks,loopCheckTicks,certificateCheckTicks,certificateChecks,certificateComparedBytes;
            public int certificateMethodCount,certificateSerializedBytes;
            public bool measureCosts;
            public long admittedProtoFacadeWrites,admittedProtoVelocityNoOps;
            public string handbackOperation,handbackOperationInstance;
            public long publications, publicationGaps, clockWrites, inputPasses, rejectedWriters, partialPublications, aliasChecks, loopChecks, nativePoseWrites, clockFrequency = Stopwatch.Frequency;
            public double admittedUniversalTime, publishedUniversalTime, maximumPoseError, maximumRotationErrorDegrees;
            public bool fixedSuppressed, fixedRestored, nativeRestored, hooksRemoved, partialPublication;
            public SourceRow[] methods;
            public OwnedClockProviderBinding[] providers;
            public OwnedClockThermalHandback.Receipt thermalHandback;
        }
        public readonly ReportData Report = new ReportData();
        public bool Prepared { get { return prepared; } }
        public bool OwnsFixedLoop { get { return loopInstalled && !disposed; } }
        sealed class Hook { internal MethodInfo method, prefix; internal SourceRow row; }
        sealed class Callsite { internal MethodBase method; internal byte[] il; internal int sites; internal SourceRow row; }
        sealed class BodyRow
        {
            internal Rigidbody body; internal int slot, id, depth;
            internal Transform parent; internal int sibling;
            internal Vector3 localScale, initialPosition, initialVelocity, initialOmega, initialCenter, initialInertia;
            internal Quaternion initialRotation, initialInertiaRotation;
            internal float initialMass, initialDrag, initialAngularDrag;
            internal bool initialKinematic, initialGravity, initialCollisions, changed;
            internal Vector3 position, velocity, omega, center, inertia, worldCenter;
            internal Quaternion rotation, inertiaRotation;
            internal float mass;
        }
        sealed class PartRow
        { internal Part part; internal int slot, id; internal uint flightId; internal Part parent; internal int moduleCount; internal double temperature, skinTemperature; internal Vector3d initialForce, initialTorque; }
        sealed class ResourceRow
        { internal PartResource resource; internal Part part; internal int slot; internal double initialAmount, capacity, value; internal bool initialFlow; }
        sealed class ProviderRow
        {
            internal PartModule module; internal OwnedClockProviderBinding source;
            internal bool enabled, isEnabled;
            internal ModuleDeployableSolarPanel solar; internal Transform dot, dotParent;
            internal Vector3 dotPosition, dotScale; internal Quaternion dotRotation;
            internal float efficiency;
            internal double launchUt; internal object deployment;
            internal ModuleEngines engine; internal ModuleDataTransmitter transmitter;
            internal bool engineIgnited, flameout;
            internal float thrustLimiter;
            internal ModuleDeployableAntenna antenna; internal ModuleSAS sas;
            internal ModuleCommand command; internal bool hibernating, hibernateOnWarp; internal double hibernationMultiplier;
            internal ModuleCargoPart cargo;
            internal ModulePartVariants variants; internal object variantList, variant; internal int variantIndex, variantCount; internal bool variantMass;
            internal FXModuleAnimateThrottle throttleAnimation; internal ModuleEngines animationEngine; internal Animation animation; internal AnimationClip clip;
            internal string animationName; internal float animationTime, animationWeight; internal int animationLayer; internal WrapMode animationWrapMode;
            internal Transform[] animationForest, animationParents; internal Vector3[] animationPositions, animationScales; internal Quaternion[] animationRotations;
        }
        public OwnedClockKspBridge(OwnedClockFlightService service, Vessel vessel, OwnedClockFlightBinding binding)
        {
            Need(service != null && vessel != null && binding != null && active == null, "One clock bridge is supported.");
            this.service = service; this.vessel = vessel; this.binding = binding; centralBody = vessel.mainBody; beforeUpdate = service.BeforeScriptUpdate;
            measureCosts=Array.IndexOf(Environment.GetCommandLineArgs(),"--continuum-owned-clock-profile")>=0;Report.measureCosts=measureCosts;
            Need(OwnedClockFlightService.HashFile(typeof(Vessel).Assembly.Location) == StockSha && typeof(Vessel).Module.ModuleVersionId.ToString("D") == StockMvid, "Pinned stock assembly differs.");
            Need(InputMethod != null && StatsComplete != null && WarpCurrent != null && WarpPrevious != null && WarpAutoEngaged != null && WarpAutoEngaged.FieldType == typeof(bool), "Pinned compatibility metadata is unavailable.");
        }
        static void Need(bool ok, string detail) { OwnedClockFlightService.Need(ok, detail); }
        void MainThread() { Need(Thread.CurrentThread.ManagedThreadId == thread, "Clock bridge requires the Unity main thread."); }
        static bool Finite(double x) { return OwnedClockFlightService.Finite(x); }
        static bool Zero(Vector3d x) { return x.x == 0 && x.y == 0 && x.z == 0; }
        static bool Same(MethodBase a, MethodBase b) { return a != null && b != null && a.Module.ModuleVersionId == b.Module.ModuleVersionId && a.MetadataToken == b.MetadataToken && a.DeclaringType == b.DeclaringType; }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return OwnedClockFlightService.Hex(sha.ComputeHash(bytes)); }
        static Vector3 V(Vec v) { Need(Finite(v.X) && Finite(v.Y) && Finite(v.Z) && Math.Abs(v.X) <= float.MaxValue && Math.Abs(v.Y) <= float.MaxValue && Math.Abs(v.Z) <= float.MaxValue, "Native publication vector outside float domain."); return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
        static Vector3d D(Vec v) { Need(Finite(v.X) && Finite(v.Y) && Finite(v.Z), "Publication vector nonfinite."); return new Vector3d(v.X, v.Y, v.Z); }
        static Quaternion Q(Rotation q) { return new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W).normalized; }
        static Vec C(Vector3 v) { return new Vec(v.x, v.y, v.z); }
        static int Depth(Transform value) { int n = 0; for (var p = value.parent; p != null; p = p.parent) Need(++n <= 256, "Body ancestry exceeds bound."); return n; }

        public static string EligibilityReason(Vessel v)
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || v == null || !ReferenceEquals(v, FlightGlobals.ActiveVessel)) return "active-flight-not-ready";
            if (!v.loaded || v.packed || v.HoldPhysics || v.rootPart == null || v.precalc == null || v.orbit == null || v.mainBody == null || v.ctrlState == null) return "unpacked-physical-flight-required";
            if (v.Landed || v.Splashed || v.isEVA || v.GetCrewCount() != 0) return "uncrewed-airborne-probe-domain-required";
            if (FlightDriver.Pause || Planetarium.fetch == null || TimeWarp.fetch == null || FlightInputHandler.fetch == null) return "stock-clock-input-not-ready";
            if (WarpCurrent == null || WarpPrevious == null || WarpAutoEngaged == null) return "stock-warp-metadata-unavailable";
            if (TimeWarp.fetch.current_rate_index != 0 || TimeWarp.CurrentRate != 1 || TimeWarp.fetch.tgt_rate != 1 ||
                (float)WarpCurrent.GetValue(TimeWarp.fetch) != 1 || (float)WarpPrevious.GetValue(TimeWarp.fetch) != 1) return "stock-warp-facade-not-normal";
            if (TimeWarp.fetch.setAutoWarp || (bool)WarpAutoEngaged.GetValue(TimeWarp.fetch)) return "stock-auto-warp-not-admitted";
            if (v.parts == null || v.parts.Count == 0 || v.parts.Count > MaximumParts) return "part-count-outside-bound";
            if (v.mainBody.inverseRotation) return "rotating-native-frame-not-admitted";
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++) if (!ReferenceEquals(FlightGlobals.Vessels[i], v) && FlightGlobals.Vessels[i] != null && FlightGlobals.Vessels[i].loaded) return "other-loaded-vessel-not-proto-only";
            if (!Finite(v.altitude) || !Finite(v.heightFromTerrain) || v.altitude <= 100 || v.heightFromTerrain <= 100) return "terrain-clearance-not-admitted";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_PLAYABLE_RIGID")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_OWNER"))) return "native-rigid-authority-active";
            return null;
        }
        public string CurrentEligibilityReason()
        {
            MainThread(); string reason = EligibilityReason(vessel); if (reason != null && reason != "stock-clock-input-not-ready") return reason;
            if (Planetarium.fetch == null || TimeWarp.fetch == null || FlightInputHandler.fetch == null) return "stock-clock-input-not-ready";
            if (!ReferenceEquals(vessel.mainBody, centralBody) || vessel.parts.Count != parts.Length) return "body-or-topology-transition";
            if(!ControlReferenceUnchanged())return "control-reference-changed";
            if (providers != null && vessel.ctrlState != null && vessel.ctrlState.mainThrottle != 0)
                foreach (ProviderRow row in providers) if (row.throttleAnimation != null) return "idle-engine-animation-throttle-demand";
            try { CheckAliases(); CheckProviders(); CheckLoop(); }
            catch (Exception error) { throw new InvalidOperationException("Owned clock lease changed.", error); }
            return null;
        }
        public void Prepare(OwnedFlightPresentationLease view)
        {
            MainThread(); Need(!prepared && !disposed, "Bridge already prepared.");
            Need(binding.Native != null && binding.Parts != null && binding.Providers != null && binding.PublishProviders != null, "Complete acquired provider/geometry binding required.");
            Need(binding.Native.vesselId == vessel.id.ToString("D") && binding.Parts.vesselId == binding.Native.vesselId && binding.Native.processId == view.Context.ProcessId && view.Context.VesselId == binding.Native.vesselId, "Worker/source vessel identity differs.");
            Need(binding.Native.universalTime == Planetarium.GetUniversalTime() && view.UniversalTime == binding.Native.universalTime && view.Context.NativeManifestSha256 == binding.NativeSha256 && view.Context.PartSpecsSha256 == binding.PartsSha256 && view.Context.SourceSha256 == binding.ContextSha256, "Acquisition epoch or source digests differ.");
            var read = view.Read; Need(read.RegionCount == 1 && read.PartCount == vessel.parts.Count && read.BodyCount == binding.Native.bodies.Length && read.BodyCount > 0 && read.BodyCount <= MaximumBodies, "Single-region acquired coverage differs.");
            Need(Finite(binding.MinimumAltitudeMeters) && binding.MinimumAltitudeMeters >= view.Context.AtmosphereDepth + view.Context.VesselBoundingRadius, "Source-bound vacuum surface envelope required.");
            var actualParts = new Dictionary<uint, Part>(); foreach (Part p in vessel.parts) { Need(p != null && !actualParts.ContainsKey(p.flightID), "Part identity duplicate/missing."); actualParts.Add(p.flightID, p); }
            parts = new PartRow[read.PartCount];
            for (int i = 0; i < parts.Length; i++)
            {
                var d = read.Definition.GetPart(i); Part p = null; Need(d.Id > 0 && d.Id <= uint.MaxValue && actualParts.TryGetValue((uint)d.Id, out p), "Authored part mapping differs.");
                Need(p.servoRb == null && p.forces != null && p.forces.Count == 0 && Zero(p.force) && Zero(p.torque), "Mechanism or pending stock force cannot transfer implicitly.");
                var row = new PartRow { part = p, slot = i, id = p.GetInstanceID(), flightId = p.flightID, parent = p.parent, moduleCount = p.Modules.Count, temperature = p.temperature, skinTemperature = p.skinTemperature, initialForce = p.force, initialTorque = p.torque };
                parts[i] = row; partLookup.Add(p, row);
            }
            var actualBodies = new Dictionary<int, Rigidbody>(); foreach (PartRow p in parts)
            { foreach (Rigidbody rb in p.part.GetComponentsInChildren<Rigidbody>(true)) if (rb != null && !actualBodies.ContainsKey(rb.GetInstanceID())) actualBodies.Add(rb.GetInstanceID(), rb); foreach (Rigidbody rb in new[] { p.part.rb, p.part.Rigidbody }) if (rb != null && !actualBodies.ContainsKey(rb.GetInstanceID())) actualBodies.Add(rb.GetInstanceID(), rb); }
            Need(actualBodies.Count == read.BodyCount, "Native body census differs."); bodies = new BodyRow[read.BodyCount];
            for (int i = 0; i < bodies.Length; i++)
            {
                long id = read.Definition.GetBody(i).Id; Rigidbody rb = null; Need(id >= int.MinValue && id <= int.MaxValue && actualBodies.TryGetValue((int)id, out rb) && rb != null && rb.gameObject.activeInHierarchy && !rb.isKinematic, "Actual dynamic body binding differs.");
                var row = new BodyRow { body = rb, slot = i, id = rb.GetInstanceID(), depth = Depth(rb.transform), parent = rb.transform.parent, sibling = rb.transform.GetSiblingIndex(), localScale = rb.transform.localScale,
                    initialPosition = rb.position, initialRotation = rb.rotation, initialVelocity = rb.velocity, initialOmega = rb.angularVelocity, initialMass = rb.mass, initialCenter = rb.centerOfMass, initialInertia = rb.inertiaTensor, initialInertiaRotation = rb.inertiaTensorRotation,
                    initialKinematic = rb.isKinematic, initialGravity = rb.useGravity, initialCollisions = rb.detectCollisions, initialDrag = rb.drag, initialAngularDrag = rb.angularDrag };
                bodies[i] = row; bodyLookup.Add(rb, row); transformLookup.Add(rb.transform, row);
            }
            resources = new ResourceRow[read.ResourceCount];
            publicationOrder = bodies.OrderBy(x => x.depth).ToArray();
            for (int i = 0; i < resources.Length; i++)
            {
                var d = read.Definition.GetResource(i); Part p = null; Need(d.PartId > 0 && d.PartId <= uint.MaxValue && actualParts.TryGetValue((uint)d.PartId, out p), "Resource part mapping differs.");
                PartResource resource = null; foreach (PartResource r in p.Resources) if (r.resourceName == d.Kind) { Need(resource == null, "Repeated resource kind on part."); resource = r; }
                Need(resource != null && resource.maxAmount == d.Capacity && resource.amount == read.GetResourceQuantity(i), "Resource source/current bank differs.");
                resources[i] = new ResourceRow { resource = resource, part = p, slot = i, initialAmount = resource.amount, capacity = resource.maxAmount, initialFlow = resource.flowState };
            }
            BindProviders(actualParts);controlReference=vessel.ReferenceTransform;controlReferencePart=vessel.GetReferenceTransformPart();Need(controlReference!=null,"Acquired control reference is absent.");CaptureControlReferenceChain(); originalTimeScale = Time.timeScale; originalFixedDelta = Time.fixedDeltaTime; originalPlanetariumDelta = Planetarium.fetch.fixedDeltaTime; originalPlanetariumScale = Planetarium.TimeScale;
            originalWarpMode = TimeWarp.fetch.Mode; originalWarpIndex = TimeWarp.fetch.current_rate_index; originalWarpCurrent = (float)WarpCurrent.GetValue(TimeWarp.fetch); originalWarpPrevious = (float)WarpPrevious.GetValue(TimeWarp.fetch);
            Report.partCount = parts.Length; Report.bodyCount = bodies.Length; Report.resourceCount = resources.Length; Report.providerCount = providers.Length; Report.providers = binding.Providers;
            Report.admittedUniversalTime = view.UniversalTime; publishedUt = view.UniversalTime; publishedRuntimeTime = view.Read.Stamp.Time; publishedStamp = view.Read.Stamp;
            PlanPublication(view); prepared = true;
        }
        void BindProviders(Dictionary<uint, Part> actualParts)
        {
            Need(binding.Providers.Length <= 32768, "Provider capacity exceeded."); providers = new ProviderRow[binding.Providers.Length];
            for (int i = 0; i < providers.Length; i++)
            {
                var source = binding.Providers[i]; Part p = null; Need(source != null && actualParts.TryGetValue(source.partId, out p) && source.moduleIndex >= 0 && source.moduleIndex < p.Modules.Count, "Provider part/module mapping differs.");
                PartModule module = p.Modules[source.moduleIndex]; Need(module != null && module.GetType().FullName == source.sourceType && !providerLookup.ContainsKey(module) && !string.IsNullOrEmpty(source.modelRecipe), "Provider identity/recipe differs.");
                Need(module.GetType().Assembly == typeof(Part).Assembly, "Foreign provider requires an implemented explicit adapter.");
                var row = new ProviderRow { module = module, source = source, enabled = module.enabled, isEnabled = module.isEnabled };
                if (source.kind == "engine-no-response")
                { row.engine = module as ModuleEngines; Need(row.engine != null && (module.GetType() == typeof(ModuleEngines) || module.GetType() == typeof(ModuleEnginesFX)) && !row.engine.useEngineResponseTime, "Engine response/custom class not admitted."); row.engineIgnited = row.engine.EngineIgnited; row.flameout = row.engine.flameout; row.thrustLimiter = row.engine.thrustPercentage; }
                else if (source.kind == "rcs-acquired") Need(module.GetType() == typeof(ModuleRCS) || module.GetType() == typeof(ModuleRCSFX), "RCS class not admitted.");
                else if (source.kind == "wheel-acquired") Need(module.GetType() == typeof(ModuleReactionWheel), "Wheel class not admitted.");
                else if (source.kind == "probe-command")
                { row.command = module as ModuleCommand; Need(module.GetType() == typeof(ModuleCommand) && row.command.minimumCrew == 0, "Uncrewed command model required."); row.hibernating = row.command.hibernation; row.hibernateOnWarp = row.command.hibernateOnWarp; row.hibernationMultiplier = row.command.hibernationMultiplier; }
                else if (source.kind == "fixed-solar")
                {
                    row.solar = module as ModuleDeployableSolarPanel; Need(row.solar != null && module.GetType() == typeof(ModuleDeployableSolarPanel) && !row.solar.isTracking && !row.solar.useAnimation && !row.solar.useRaycastForTrackingDot && row.solar.panelType == ModuleDeployableSolarPanel.PanelType.FLAT && row.solar.trackingDotTransform != null && row.solar.part.submergedPortion == 0, "Fixed nontracking flat solar panel required.");
                    row.dot = row.solar.trackingDotTransform; row.dotParent = row.dot.parent; row.dotPosition = row.dot.localPosition; row.dotRotation = row.dot.localRotation; row.dotScale = row.dot.localScale;
                    row.efficiency = row.solar.efficiencyMult; row.launchUt = row.solar.launchUT; row.deployment = row.solar.deployState;
                    Need(Finite(row.efficiency) && row.efficiency >= 0 && Finite(row.launchUt) && row.launchUt >= 0 && row.dot.gameObject.activeInHierarchy, "Solar source state invalid.");
                }
                else if (source.kind == "idle-transmitter") { row.transmitter = module as ModuleDataTransmitter; Need(row.transmitter != null && module.GetType() == typeof(ModuleDataTransmitter) && !row.transmitter.IsBusy(), "Busy transmitter is unsupported."); }
                else if (source.kind == "fixed-antenna")
                { row.antenna = module as ModuleDeployableAntenna; Need(row.antenna != null && module.GetType() == typeof(ModuleDeployableAntenna) && (row.antenna.deployState == ModuleDeployablePart.DeployState.EXTENDED || row.antenna.deployState == ModuleDeployablePart.DeployState.RETRACTED), "Stable deployed/retracted antenna required."); row.deployment = row.antenna.deployState; }
                else if (source.kind == "sas-service")
                { row.sas = module as ModuleSAS; Need(row.sas != null && module.GetType() == typeof(ModuleSAS) && !row.sas.RequireCrew && !row.sas.standalone, "Uncrewed command-bound SAS service metadata required."); }
                else if (source.kind == "cargo-ui")
                {
                    row.cargo = module as ModuleCargoPart;
                    Need(module.GetType() == typeof(ModuleCargoPart) && CargoSettling != null && CargoSettling.FieldType == typeof(bool) && CargoAttached != null && CargoAttached.FieldType == typeof(bool) && CargoConstruction != null && CargoConstruction.FieldType == typeof(bool), "Pinned cargo state fields required.");
                    Need(!(bool)CargoSettling.GetValue(module) && !(bool)CargoAttached.GetValue(module) && !(bool)CargoConstruction.GetValue(module), "Cargo settling/attachment/construction cannot transfer.");
                    foreach (string name in new[] { "FixedUpdate", "OnFixedUpdate", "Update", "LateUpdate" }) Need(DeclaredCallback(module.GetType(), name) == null, "Cargo role has an unadmitted callback.");
                    var onUpdate = DeclaredCallback(module.GetType(), "OnUpdate"); Need(onUpdate != null && onUpdate.DeclaringType == typeof(ModuleCargoPart), "Pinned cargo highlight callback required.");
                }
                else if (source.kind == "frozen-variant")
                {
                    row.variants = module as ModulePartVariants; Need(module.GetType() == typeof(ModulePartVariants) && row.variants.variantList != null && row.variants.variantList.Count > 0, "Actual variant catalog required.");
                    foreach (string name in new[] { "FixedUpdate", "OnFixedUpdate", "Update", "OnUpdate", "LateUpdate" }) Need(DeclaredCallback(module.GetType(), name) == null, "Frozen variant declares an unadmitted callback.");
                    row.variantIndex = row.variants.GetCurrentVariantIndex(); Need(row.variantIndex >= 0 && row.variantIndex < row.variants.variantList.Count, "Selected variant index outside catalog.");
                    row.variantList = row.variants.variantList; row.variantCount = row.variants.variantList.Count; row.variant = row.variants.SelectedVariant; row.variantMass = row.variants.useVariantMass; Need(row.variant != null, "Selected variant missing.");
                }
                else if (source.kind == "idle-engine-animation")
                {
                    row.throttleAnimation = module as FXModuleAnimateThrottle;
                    Need(module.GetType() == typeof(FXModuleAnimateThrottle) && ThrottleAnimation != null && ThrottleAnimation.FieldType == typeof(Animation) && ThrottleEngine != null && row.throttleAnimation.affectTime && row.throttleAnimation.animState == 0 && !string.IsNullOrEmpty(row.throttleAnimation.animationName), "Pinned idle throttle animation state required.");
                    row.animation = ThrottleAnimation.GetValue(module) as Animation; row.animationEngine = ThrottleEngine.GetValue(module) as ModuleEngines; row.animationName = row.throttleAnimation.animationName;
                    Need(row.animation != null && row.animationEngine != null && ReferenceEquals(row.animationEngine.part, p) && !row.animationEngine.EngineIgnited && !row.animationEngine.isOperational && row.animationEngine.currentThrottle == 0 && row.animationEngine.requestedThrottle == 0 && vessel.ctrlState.mainThrottle == 0, "Idle animation requires the actual inactive zero-throttle engine.");
                    var state = row.animation[row.animationName]; row.clip = row.animation.GetClip(row.animationName);
                    Need(state != null && row.clip != null && state.speed == 0 && Finite(state.normalizedTime), "Actual idle animation clip must be stationary.");
                    row.animationTime = state.normalizedTime; row.animationWeight = state.weight; row.animationLayer = state.layer; row.animationWrapMode = state.wrapMode;
                    var forest = new List<Transform>(); var cursor = row.animation.transform;
                    while (cursor != p.transform) { Need(cursor != null && forest.Count < 256, "Idle animation must belong to the bound part forest."); forest.Add(cursor); cursor = cursor.parent; }
                    row.animationForest = forest.ToArray(); row.animationParents = new Transform[forest.Count]; row.animationPositions = new Vector3[forest.Count]; row.animationScales = new Vector3[forest.Count]; row.animationRotations = new Quaternion[forest.Count];
                    for (int j = 0; j < forest.Count; j++) { var transform = forest[j]; row.animationParents[j] = transform.parent; row.animationPositions[j] = transform.localPosition; row.animationScales[j] = transform.localScale; row.animationRotations[j] = transform.localRotation; }
                }
                else if (source.kind == "passive-no-callback")
                { foreach (string name in new[] { "FixedUpdate", "OnFixedUpdate", "Update", "OnUpdate", "LateUpdate" }) Need(DeclaredCallback(module.GetType(), name) == null, "Passive provider declares a physical/render callback: " + module.GetType().FullName + "." + name); }
                else throw new InvalidOperationException("Unimplemented provider role: " + source.kind);
                providers[i] = row; providerLookup.Add(module, row);
            }
            foreach (ProviderRow row in providers) if (row.throttleAnimation != null)
            { ProviderRow engine; Need(providerLookup.TryGetValue(row.animationEngine, out engine) && engine.engine != null && engine.source.kind == "engine-no-response", "Idle animation engine must have its own admitted acquired engine row."); }
            foreach (PartRow p in parts) foreach (PartModule module in p.part.Modules) Need(module != null && providerLookup.ContainsKey(module), "Every installed module requires an explicit acquired/inert capability row.");
            thermalHandback=new OwnedClockThermalHandback(vessel);Report.thermalHandback=thermalHandback.Report;
        }
        static MethodInfo DeclaredCallback(Type type, string name)
        { for (Type t = type; t != null && t != typeof(PartModule); t = t.BaseType) { var method = AccessTools.DeclaredMethod(t, name, Type.EmptyTypes); if (method != null) return method; } return null; }

        public void Install()
        {
            MainThread(); Need(prepared && !installed && active == null, "Clock bridge installation state differs.");
            active = this;
            try
            {
                AdmitHooks(); AdmitCallsites();
                foreach (Hook hook in hooks.Values)
                { attempted.Add(hook.method); harmony.Patch(hook.method, prefix: new HarmonyMethod(hook.prefix)); hook.row.installed = true; }
                foreach (Callsite plan in callsites.Values)
                { attempted.Add(plan.method); harmony.Patch(plan.method, transpiler: new HarmonyMethod(Transpiler)); plan.row.installed = true; }
                foreach (BodyRow row in bodies)
                {
                    row.changed = true; nativeMutation = true; row.body.isKinematic = true; row.body.useGravity = false; row.body.detectCollisions = false;
                    Need(row.body.isKinematic && !row.body.useGravity && !row.body.detectCollisions, "Native authority suppression readback failed.");
                }
                Time.timeScale = 1; Time.fixedDeltaTime = originalFixedDelta; Planetarium.TimeScale = 1;
                InstallLoop(); installed = true;
                string unsupported;
                Need(HarmonyPatchStateCertificate.TrySeal(attempted.Concat(Routes.Keys).Distinct(), CheckPatches, out certificate, out unsupported), "Pinned raw patch certificate unavailable: " + unsupported);
                Report.certificateMethodCount=certificate.MethodCount;Report.certificateSerializedBytes=certificate.SerializedBytes;
                if(service.Report.acquisitionEpoch==1)HarmonyPatchStateCertificate.RunRequestedWitness(service.Report.runId,attempted.Concat(Routes.Keys).Distinct(),CheckPatches);
                Report.status = "owned"; Report.patchedMethods = attempted.Count; Report.methods = hooks.Values.Select(x => x.row).Concat(callsites.Values.Select(x => x.row)).ToArray();
            }
            catch (Exception error)
            { Report.status = "invalid"; Report.detail = error.ToString(); throw; }
        }
        void AddHook(Type type, string name, string prefix, Type[] parameters = null, bool required = true)
        {
            var method = parameters == null ? AccessTools.DeclaredMethod(type, name) : AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null) { Need(!required, "Pinned method missing: " + type.FullName + "." + name); return; }
            if (hooks.ContainsKey(method)) { Need(hooks[method].prefix.Name == prefix, "Conflicting exact hook."); return; }
            Need(method.Module.ModuleVersionId.ToString("D") == StockMvid && method.GetMethodBody() != null && hooks.Count < MaximumHooks, "Managed compatibility pin differs.");
            var patches = Harmony.GetPatchInfo(method); Need(patches == null || patches.Owners.Count == 0, "Foreign compatibility hook: " + type.FullName + "." + name);
            var callback = AccessTools.DeclaredMethod(typeof(OwnedClockKspBridge), prefix); Need(callback != null, "Bridge callback absent.");
            hooks.Add(method, new Hook { method = method, prefix = callback, row = new SourceRow { declaringType = method.DeclaringType.FullName, method = method.Name, metadataToken = method.MetadataToken, ilSha256 = Hash(method.GetMethodBody().GetILAsByteArray()), assemblySha256 = StockSha, assemblyMvid = StockMvid, category = "prefix:" + prefix } });
        }
        void AdmitHooks()
        {
            AddHook(typeof(Planetarium), "SetUniversalTime", nameof(BeforeUniversalTime), new[] { typeof(double) });
            AddHook(typeof(TimeWarp), "get_fixedDeltaTime", nameof(FixedDelta), Type.EmptyTypes);
            AddHook(typeof(TimeWarp), "get_deltaTime", nameof(Delta), Type.EmptyTypes);
            AddHook(typeof(TimeWarp), "get_CurrentRate", nameof(CurrentRate), Type.EmptyTypes);
            AddHook(typeof(TimeWarp), "get_WarpMode", nameof(WarpMode), Type.EmptyTypes);
            AddHook(typeof(TimeWarp), "setRate", nameof(SetRate), new[] { typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool) });
            AddHook(typeof(TimeWarp), "setMode", nameof(SetMode), new[] { typeof(TimeWarp.Modes) });
            AddHook(typeof(TimeWarp), "updateRate", nameof(UpdateRate), new[] { typeof(float), typeof(bool) });
            AddHook(typeof(TimeWarp), "WarpTo", nameof(BeforeTransition), new[] { typeof(double), typeof(double), typeof(double) });
            AddHook(typeof(VesselPrecalculate), "CalculatePhysicsStats", nameof(Stats), Type.EmptyTypes);
            AddHook(typeof(VesselPrecalculate), "MainPhysics", nameof(SuppressOwned), new[] { typeof(bool) });
            AddHook(typeof(VesselPrecalculate), "Update", nameof(SuppressOwned), Type.EmptyTypes);
            AddHook(typeof(FlightIntegrator), "Update", nameof(SuppressOwned), Type.EmptyTypes);
            AddHook(typeof(OrbitDriver), "UpdateOrbit", nameof(OrbitUpdate), new[] { typeof(bool) });
            AddHook(typeof(OrbitDriver), "TrackRigidbody", nameof(OrbitUpdate), new[] { typeof(CelestialBody), typeof(double) });
            AddHook(typeof(ModuleCommand), "UpdateControlSourceState", nameof(CommandState), Type.EmptyTypes);
            AddHook(typeof(ModuleCommand), "get_IsHibernating", nameof(IsHibernating), Type.EmptyTypes);
            AddHook(typeof(ModuleCommand), "HibernateToggle", nameof(BeforeTransition), new[] { typeof(KSPActionParam) });
            AddHook(typeof(GamePersistence), "SaveGame", nameof(BeforeTransition), new[] { typeof(string), typeof(string), typeof(SaveMode) });
            AddHook(typeof(GamePersistence), "SaveGame", nameof(BeforeTransition), new[] { typeof(Game), typeof(string), typeof(string), typeof(SaveMode) });
            AddHook(typeof(QuickSaveLoad), "doSave", nameof(BeforeTransition), new[] { typeof(string), typeof(string) });
            AddHook(typeof(QuickSaveLoad), "quickLoad", nameof(BeforeTransition), new[] { typeof(string), typeof(string) });
            Type stage = typeof(Vessel).Assembly.GetType("KSP.UI.Screens.StageManager", true);
            AddHook(stage, "ActivateNextStage", nameof(BeforeTransition), Type.EmptyTypes); AddHook(stage, "ActivateStage", nameof(BeforeTransition), new[] { typeof(int) });
            foreach (string name in new[] { "GoOnRails", "GoOffRails", "Die", "Load", "Unload" }) AddHook(typeof(Vessel), name, nameof(BeforeTransition), Type.EmptyTypes);
            AddHook(typeof(FlightGlobals), "SetActiveVessel", nameof(BeforeTransition), new[] { typeof(Vessel) });
            AddHook(typeof(FlightGlobals), "ForceSetActiveVessel", nameof(BeforeTransition), new[] { typeof(Vessel) });
            AddHook(typeof(FlightDriver), "StartAndFocusVessel", nameof(BeforeTransition), new[] { typeof(Game), typeof(int) });
            AddHook(typeof(HighLogic), "LoadScene", nameof(BeforeTransition), new[] { typeof(GameScenes) });
            AddHook(typeof(FlightDriver), "SetPause", nameof(BeforePause), new[] { typeof(bool), typeof(bool) });
            AddHook(typeof(Part), "Couple", nameof(BeforeTransition), new[] { typeof(Part) });
            AddHook(typeof(Vessel),"SetReferenceTransform",nameof(BeforeTransition),new[]{typeof(Part),typeof(bool)});
            AddHook(typeof(Vessel),"RecallReferenceTransform",nameof(BeforeTransition),Type.EmptyTypes);
            AddHook(typeof(Part),"SetReferenceTransform",nameof(BeforeTransition),new[]{typeof(Transform)});
            AddHook(typeof(ModuleCommand),"SetControlPoint",nameof(BeforeTransition),new[]{typeof(string)});
            AddHook(typeof(ModuleCommand),"MakeReference",nameof(BeforeTransition),Type.EmptyTypes);
            AddHook(typeof(ModuleDockingNode),"MakeReferenceTransform",nameof(BeforeTransition),Type.EmptyTypes);
            AddHook(typeof(Part), "decouple", nameof(BeforeTransition), new[] { typeof(float) });
            AddHook(typeof(Part), "Undock", nameof(BeforeTransition), new[] { typeof(DockedVesselInfo) }); AddHook(typeof(Part), "Die", nameof(BeforeTransition), Type.EmptyTypes);
            foreach (string name in new[] { "ChangeWorldVelocity", "SetWorldVelocity", "OffsetVelocity" }) AddHook(typeof(Vessel), name, nameof(BeforeTransition), new[] { typeof(Vector3d) });
            AddHook(typeof(Vessel), "SetPosition", nameof(BeforeTransition), new[] { typeof(Vector3d), typeof(bool) });
            AddHook(typeof(Vessel), "SetRotation", nameof(BeforeTransition), new[] { typeof(Quaternion), typeof(bool) });
            AddHook(typeof(FloatingOrigin), "setOffset", nameof(BeforeFrameTransition), new[] { typeof(Vector3d), typeof(Vector3d) });
            AddHook(typeof(FloatingOrigin), "SetOffset", nameof(BeforeFrameTransition), new[] { typeof(Vector3d) });
            AddHook(typeof(FloatingOrigin), "SetOutOfFrameOffset", nameof(BeforeFrameTransition), new[] { typeof(Vector3d) });
            AddHook(typeof(Krakensbane), "AddExcess", nameof(BeforeFrameTransition), new[] { typeof(Vector3d) });
            AddHook(typeof(Krakensbane), "Zero", nameof(BeforeFrameTransition), Type.EmptyTypes);
            AddHook(typeof(Krakensbane), "ResetVelocityFrame", nameof(BeforeFrameTransition), new[] { typeof(bool) });
            AddHook(typeof(Krakensbane), "AddFrameVelocity", nameof(BeforeFrameTransition), new[] { typeof(Vector3d) });
            foreach (var method in typeof(Part).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name == "RequestResource")) AddHook(typeof(Part), method.Name, nameof(BeforeResource), method.GetParameters().Select(p => p.ParameterType).ToArray());
            foreach (ProviderRow row in providers)
            {
                if (row.source.kind != "passive-no-callback" && row.source.kind != "cargo-ui" && row.source.kind != "frozen-variant")
                    foreach (string name in new[] { "FixedUpdate", "OnFixedUpdate", "Update", "OnUpdate", "LateUpdate" })
                    { var method = DeclaredCallback(row.module.GetType(), name); if (method != null) AddHook(method.DeclaringType, name, nameof(SuppressOwned), Type.EmptyTypes); }
                if (row.transmitter != null)
                    foreach (var method in row.module.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == "StartTransmission" || m.Name == "onStartTransmission"))
                        AddHook(method.DeclaringType, method.Name, nameof(BeforeTransition), method.GetParameters().Select(p => p.ParameterType).ToArray());
                if (row.module is ModuleDeployablePart)
                    foreach (string name in new[] { "Extend", "Retract", "DoExtend", "DoRetract" })
                    { var method = AccessTools.Method(row.module.GetType(), name, Type.EmptyTypes); if (method != null) AddHook(method.DeclaringType, name, nameof(BeforeTransition), Type.EmptyTypes); }
                if (row.engine != null)
                    foreach (string name in new[] { "Activate", "Shutdown" })
                    { var method = AccessTools.Method(row.module.GetType(), name, Type.EmptyTypes); Need(method != null, "Exact engine writer missing."); AddHook(method.DeclaringType, name, nameof(BeforeTransition), Type.EmptyTypes); }
                if (row.cargo != null)
                {
                    foreach (string name in new[] { "OnPartPack", "OnPartUnpack", "EnableModule" }) AddHook(typeof(ModuleCargoPart), name, nameof(BeforeTransition), Type.EmptyTypes);
                    AddHook(typeof(ModuleCargoPart), "MakePartSettle", nameof(BeforeTransition), new[] { typeof(bool) });
                    AddHook(typeof(ModuleCargoPart), "OnEVAConstructionMode", nameof(BeforeTransition), new[] { typeof(bool) });
                    foreach (string name in new[] { "OnWeldStart", "OnWeldFinish" }) AddHook(typeof(ModuleCargoPart), name, nameof(BeforeTransition), new[] { typeof(KerbalEVA) });
                }
                if (row.variants != null)
                {
                    AddHook(typeof(ModulePartVariants), "RefreshVariant", nameof(BeforeTransition), Type.EmptyTypes);
                    foreach (string name in new[] { "SetVariant", "SetVariantTheme", "AssumeDragCubePosition" }) AddHook(typeof(ModulePartVariants), name, nameof(BeforeTransition), new[] { typeof(string) });
                    AddHook(typeof(ModulePartVariants), "SetCubes", nameof(BeforeTransition), new[] { typeof(bool) });
                    AddHook(typeof(ModulePartVariants), "ApplyVariant", nameof(BeforeVariant), new[] { typeof(Part), typeof(Transform), typeof(PartVariant), typeof(Material[]), typeof(bool) });
                    AddHook(typeof(ModulePartVariants), "ApplyVariant", nameof(BeforeVariant), new[] { typeof(Part), typeof(Transform), typeof(PartVariant), typeof(Material[]), typeof(bool), typeof(int) });
                }
                if (row.throttleAnimation != null) AddHook(typeof(FXModuleAnimateThrottle), "OnInventoryModeDisable", nameof(BeforeTransition), Type.EmptyTypes);
            }
        }
        static readonly OpCode[] SingleOpcodes=OpcodeTable(false),DoubleOpcodes=OpcodeTable(true);
        static OpCode[] OpcodeTable(bool doubled)
        {
            var table=new OpCode[256];
            foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))if(field.FieldType==typeof(OpCode))
            {var code=(OpCode)field.GetValue(null);ushort value=unchecked((ushort)code.Value);if(code.Size==(doubled?2:1))table[value&255]=code;}
            return table;
        }
        sealed class TokenRoutes
        {internal readonly Dictionary<int,bool> Methods=new Dictionary<int,bool>(),Fields=new Dictionary<int,bool>();}
        static int Token(byte[] il,int position)
        {return il[position]|il[position+1]<<8|il[position+2]<<16|il[position+3]<<24;}
        static int RawSites(MethodBase method,byte[] il,TokenRoutes routes,ref int instructions)
        {
            int at=0,sites=0;
            while(at<il.Length)
            {
                Need(++instructions<=5000000,"Managed instruction census exceeds bound.");
                byte first=il[at++];OpCode code;
                if(first==254){Need(at<il.Length,"Truncated two-byte IL opcode.");code=DoubleOpcodes[il[at++]];}else code=SingleOpcodes[first];
                Need(code.Size!=0,"Unknown IL opcode during raw discovery.");int size;
                switch(code.OperandType)
                {
                    case OperandType.InlineNone:size=0;break;
                    case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:size=1;break;
                    case OperandType.InlineVar:size=2;break;
                    case OperandType.InlineI8:case OperandType.InlineR:size=8;break;
                    case OperandType.InlineSwitch:
                        Need(il.Length-at>=4,"Truncated switch IL operand.");int count=Token(il,at);Need(count>=0&&count<=(il.Length-at-4)/4,"Invalid switch IL operand.");size=4+count*4;break;
                    case OperandType.InlineBrTarget:case OperandType.InlineField:case OperandType.InlineI:case OperandType.InlineMethod:case OperandType.InlineSig:case OperandType.InlineString:case OperandType.InlineTok:case OperandType.InlineType:case OperandType.ShortInlineR:size=4;break;
                    default:throw new InvalidOperationException("Unsupported IL operand during raw discovery.");
                }
                Need(size<=il.Length-at,"Truncated IL operand during raw discovery.");
                if(code==OpCodes.Call||code==OpCodes.Callvirt||code==OpCodes.Stfld)
                {
                    Need(size==4,"Routed IL token operand differs.");int token=Token(il,at);bool selected;var cache=code==OpCodes.Stfld?routes.Fields:routes.Methods;
                    if(!cache.TryGetValue(token,out selected))
                    {
                        selected=code==OpCodes.Stfld?FieldRoute(method.Module.ResolveField(token))!=null:Route(method.Module.ResolveMethod(token) as MethodInfo)!=null;
                        cache.Add(token,selected);
                    }
                    if(selected)sites++;
                }
                at+=size;
            }
            return sites;
        }
        void AdmitCallsites()
        {
            var assemblies = new List<Assembly> { typeof(Part).Assembly };
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) if (assembly.GetName().Name == "MechJeb2")
            { Need(OwnedClockFlightService.HashFile(assembly.Location) == MechSha, "Pinned MechJeb source differs."); assemblies.Add(assembly); }
            int methods = 0, instructions = 0;long discoveryBegin=Stopwatch.GetTimestamp();var tokens=new Dictionary<Module,TokenRoutes>();
            try
            {
            foreach (Assembly assembly in assemblies) foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodBase method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
                {
                    Need(++methods <= 100000, "Managed callsite discovery exceeds bound.");
                    if (method.IsGenericMethod || type.ContainsGenericParameters) continue;var body=method.GetMethodBody();if(body==null)continue;
                    byte[] il=body.GetILAsByteArray();Need(il!=null,"Managed original IL is absent.");TokenRoutes cache;if(!tokens.TryGetValue(method.Module,out cache)){cache=new TokenRoutes();tokens.Add(method.Module,cache);}
                    long rawBegin=Stopwatch.GetTimestamp();int sites;
                    try{sites=RawSites(method,il,cache,ref instructions);}finally{Report.rawDiscoveryTicks+=Stopwatch.GetTimestamp()-rawBegin;}
                    if (sites == 0) continue; Need(callsites.Count < MaximumHooks, "Selected callsite count exceeds bound.");
                    Report.discoverySelectedMethods++;Report.discoveryRoutedSites+=sites;int verifiedSites=0;long verifyBegin=Stopwatch.GetTimestamp();
                    try
                    {
                        Report.harmonyReconstructions++;
                        foreach(CodeInstruction code in PatchProcessor.GetOriginalInstructions(method))
                            if((code.opcode==OpCodes.Call||code.opcode==OpCodes.Callvirt)&&Route(code.operand as MethodInfo)!=null||code.opcode==OpCodes.Stfld&&FieldRoute(code.operand as FieldInfo)!=null)verifiedSites++;
                        Need(verifiedSites==sites,"Raw screen and complete original instruction route counts differ: "+type.FullName+"."+method.Name);
                    }
                    finally{Report.fullInstructionVerificationTicks+=Stopwatch.GetTimestamp()-verifyBegin;}
                    var patches = Harmony.GetPatchInfo(method); Need(patches == null || patches.Owners.Count == 0, "Foreign read/writer callsite patch: " + type.FullName + "." + method.Name);
                    callsites.Add(method, new Callsite { method = method, il = il, sites = sites, row = new SourceRow { declaringType = method.DeclaringType.FullName, method = method.Name, metadataToken = method.MetadataToken, ilSha256 = Hash(il), assemblySha256 = assembly == typeof(Part).Assembly ? StockSha : MechSha, assemblyMvid = method.Module.ModuleVersionId.ToString("D"), category = "native-callsite", sites = sites } }); Report.routedSites += sites;
                }
            }
            foreach (MethodInfo method in Routes.Keys) { var patches = Harmony.GetPatchInfo(method); Need(patches == null || patches.Owners.Count == 0, "Native API is already patched: " + method.Name); }
            }
            finally{Report.discoveryMethods=methods;Report.discoveryInstructions=instructions;Report.discoveryTicks+=Stopwatch.GetTimestamp()-discoveryBegin;}
        }
        void CheckPatches()
        {
            foreach (Hook hook in hooks.Values)
            {
                var p = Harmony.GetPatchInfo(hook.method); Need(p != null, "Owned prefix removed.");
                Need(p.Prefixes.Count == 1 && Exact(p.Prefixes[0], hook.prefix) && p.Postfixes.Count == 0 && p.Finalizers.Count == 0, "Managed prefix graph differs.");
                Callsite plan; if (callsites.TryGetValue(hook.method, out plan)) Need(p.Transpilers.Count == 1 && Exact(p.Transpilers[0], Transpiler), "Composite bridge graph differs."); else Need(p.Transpilers.Count == 0, "Foreign transpiler appeared.");
            }
            foreach (Callsite plan in callsites.Values)
            {
                var p = Harmony.GetPatchInfo(plan.method); Need(p != null && p.Transpilers.Count == 1 && Exact(p.Transpilers[0], Transpiler), "Owned callsite graph differs.");
                Hook hook; if (!hooks.TryGetValue(plan.method, out hook)) Need(p.Prefixes.Count == 0, "Foreign callsite prefix appeared.");
                Need(p.Postfixes.Count == 0 && p.Finalizers.Count == 0 && plan.method.GetMethodBody().GetILAsByteArray().SequenceEqual(plan.il), "Source or callsite graph changed.");
            }
            foreach (MethodInfo method in Routes.Keys) { var p = Harmony.GetPatchInfo(method); Need(p == null || p.Owners.Count == 0, "Native API graph changed."); }
        }
        static bool Exact(Patch patch, MethodInfo method) { return patch.owner == Owner && Same(patch.PatchMethod, method) && patch.priority == Priority.Normal && patch.before.Length == 0 && patch.after.Length == 0; }
        static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
        {
            var bridge = active; Callsite plan = null; Need(bridge != null && bridge.callsites.TryGetValue(__originalMethod, out plan), "Deterministic original rewrite context absent.");
            var codes = source.ToList(); int changed = 0;
            foreach (CodeInstruction code in codes)
            {
                if (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt)
                { var wrapper = Route(code.operand as MethodInfo); if (wrapper != null) { code.opcode = OpCodes.Call; code.operand = wrapper; changed++; } }
                else if (code.opcode == OpCodes.Stfld)
                { var wrapper = FieldRoute(code.operand as FieldInfo); if (wrapper != null) { code.opcode = OpCodes.Call; code.operand = wrapper; changed++; } }
            }
            Need(changed == plan.sites, "Original native callsite shape differs."); return codes;
        }
        void InstallLoop()
        {
            var current = PlayerLoop.GetCurrentPlayerLoop(); Need(CountType(current, typeof(FixedLoop)) == 1 && CountType(current, typeof(UpdateLoop.ScriptRunBehaviourUpdate)) == 1, "Expected one fixed domain and script-update target.");
            originalFixed = Clone(Find(current, typeof(FixedLoop)));
            Need(CountType(originalFixed, typeof(FixedLoop.ScriptRunBehaviourFixedUpdate)) == 1 && CountType(originalFixed, typeof(FixedLoop.PhysicsFixedUpdate)) == 1, "Actual fixed subtree differs.");
            ownedFixed = new PlayerLoopSystem { type = typeof(OwnedFixed), updateDelegate = SuppressedFixed };
            loopInstalled = true; PlayerLoop.SetPlayerLoop(InstallNode(current)); CheckLoop(); Report.fixedSuppressed = true;
        }
        static void SuppressedFixed() { }
        PlayerLoopSystem InstallNode(PlayerLoopSystem node)
        {
            if (node.type == typeof(FixedLoop)) return ownedFixed;
            if (node.subSystemList != null)
            {
                var children = new List<PlayerLoopSystem>(); foreach (var child in node.subSystemList)
                { if (child.type == typeof(UpdateLoop.ScriptRunBehaviourUpdate)) children.Add(new PlayerLoopSystem { type = typeof(BeforeOwnedUpdate), updateDelegate = beforeUpdate }); children.Add(InstallNode(child)); }
                node.subSystemList = children.ToArray();
            }
            return node;
        }
        void CheckLoop()
        {long started=measureCosts?Stopwatch.GetTimestamp():0;try{CheckLoopCore();}finally{if(measureCosts)Report.loopCheckTicks+=Stopwatch.GetTimestamp()-started;}}
        void CheckLoopCore()
        {
            if (!loopInstalled) return; var current = PlayerLoop.GetCurrentPlayerLoop(); Report.loopChecks++;
            Need(CountType(current, typeof(FixedLoop)) == 0 && CountType(current, typeof(OwnedFixed)) == 1 && CountType(current, typeof(BeforeOwnedUpdate)) == 1, "Fixed domain ownership changed.");
            Need(Equal(Find(current, typeof(OwnedFixed)), ownedFixed), "Owned fixed replacement changed.");
            var parent = Parent(current, typeof(UpdateLoop.ScriptRunBehaviourUpdate)); var children = parent.subSystemList; bool adjacent = false;
            for (int i = 1; i < children.Length; i++) if (children[i].type == typeof(UpdateLoop.ScriptRunBehaviourUpdate) && children[i - 1].type == typeof(BeforeOwnedUpdate) && children[i - 1].updateDelegate == beforeUpdate) adjacent = true;
            Need(adjacent, "Owned publication no longer precedes script Update.");
            if (certificate != null)
            {
                long started=measureCosts?Stopwatch.GetTimestamp():0;
                try{Need(certificate.Matches(), "Sealed bridge graph changed: " + certificate.Failure);}
                finally{if(measureCosts)Report.certificateCheckTicks+=Stopwatch.GetTimestamp()-started;Report.certificateChecks=certificate.Checks;Report.certificateComparedBytes=certificate.ComparedBytes;}
            }
        }
        void RestoreLoop()
        {
            if (!loopInstalled) { Report.fixedRestored = true; return; }
            CheckLoop(); int fixedNodes = 0, updateNodes = 0; var restored = RestoreNode(PlayerLoop.GetCurrentPlayerLoop(), ref fixedNodes, ref updateNodes);
            Need(fixedNodes == 1 && updateNodes == 1, "Original fixed-domain restoration differs."); PlayerLoop.SetPlayerLoop(restored);
            var verified = PlayerLoop.GetCurrentPlayerLoop(); Need(CountType(verified, typeof(OwnedFixed)) == 0 && CountType(verified, typeof(BeforeOwnedUpdate)) == 0 && Equal(Find(verified, typeof(FixedLoop)), originalFixed), "Original fixed subtree not restored exactly.");
            loopInstalled = false; Report.fixedSuppressed = false; Report.fixedRestored = true;
        }
        PlayerLoopSystem RestoreNode(PlayerLoopSystem node, ref int fixedNodes, ref int updateNodes)
        {
            if (node.type == typeof(OwnedFixed)) { fixedNodes++; return Clone(originalFixed); }
            if (node.subSystemList != null)
            {
                var rows = new List<PlayerLoopSystem>(); foreach (var child in node.subSystemList)
                { if (child.type == typeof(BeforeOwnedUpdate)) { Need(child.updateDelegate == beforeUpdate, "Foreign replacement used owned callback type."); updateNodes++; } else rows.Add(RestoreNode(child, ref fixedNodes, ref updateNodes)); }
                node.subSystemList = rows.ToArray();
            }
            return node;
        }
        static int CountType(PlayerLoopSystem node, Type type) { int n = node.type == type ? 1 : 0; if (node.subSystemList != null) foreach (var child in node.subSystemList) n += CountType(child, type); return n; }
        static PlayerLoopSystem Find(PlayerLoopSystem node, Type type) { if (node.type == type) return node; if (node.subSystemList != null) foreach (var child in node.subSystemList) if (CountType(child, type) != 0) return Find(child, type); throw new InvalidOperationException("PlayerLoop target absent."); }
        static PlayerLoopSystem Parent(PlayerLoopSystem node, Type type) { if (node.subSystemList != null) { foreach (var child in node.subSystemList) if (child.type == type) return node; foreach (var child in node.subSystemList) if (CountType(child, type) != 0) return Parent(child, type); } throw new InvalidOperationException("PlayerLoop parent absent."); }
        static PlayerLoopSystem Clone(PlayerLoopSystem node) { if (node.subSystemList != null) { var rows = new PlayerLoopSystem[node.subSystemList.Length]; for (int i = 0; i < rows.Length; i++) rows[i] = Clone(node.subSystemList[i]); node.subSystemList = rows; } return node; }
        static bool Equal(PlayerLoopSystem a, PlayerLoopSystem b)
        { if (a.type != b.type || a.updateDelegate != b.updateDelegate || a.updateFunction != b.updateFunction || a.loopConditionFunction != b.loopConditionFunction) return false; int ac = a.subSystemList == null ? 0 : a.subSystemList.Length, bc = b.subSystemList == null ? 0 : b.subSystemList.Length; if (ac != bc) return false; for (int i = 0; i < ac; i++) if (!Equal(a.subSystemList[i], b.subSystemList[i])) return false; return true; }

        void CheckAliases()
        {long started=measureCosts?Stopwatch.GetTimestamp():0;try{CheckAliasesCore();}finally{if(measureCosts)Report.aliasCheckTicks+=Stopwatch.GetTimestamp()-started;}}
        void CheckAliasesCore()
        {
            Need(ReferenceEquals(vessel.mainBody, centralBody) && ReferenceEquals(vessel, FlightGlobals.ActiveVessel), "Owned vessel/body changed.");
            foreach (Part p in vessel.parts) Need(p != null && partLookup.ContainsKey(p), "Authored part list changed.");
            foreach (PartRow row in parts)
                Need(row.part != null && row.part.GetInstanceID() == row.id && row.part.flightID == row.flightId && ReferenceEquals(row.part.vessel, vessel) && ReferenceEquals(row.part.parent, row.parent) && row.part.Modules.Count == row.moduleCount && row.part.forces.Count == 0 && Zero(row.part.force) && Zero(row.part.torque), "Part alias/topology or pending-force writer changed.");
            foreach (BodyRow row in bodies)
            {
                Need(row.body != null && row.body.GetInstanceID() == row.id && ReferenceEquals(row.body.transform.parent, row.parent) && row.body.transform.GetSiblingIndex() == row.sibling && row.body.transform.localScale == row.localScale, "Native body hierarchy/scale changed.");
                if (installed) Need(row.body.isKinematic && !row.body.useGravity && !row.body.detectCollisions, "Second native authority or contacts became active.");
            }
            foreach (ResourceRow row in resources) Need(row.resource != null && row.resource.maxAmount == row.capacity && row.resource.flowState == row.initialFlow, "Resource capacity/flow changed outside the owned model.");
            Report.aliasChecks++;
        }
        void CheckProviders()
        {long started=measureCosts?Stopwatch.GetTimestamp():0;try{CheckProvidersCore();}finally{if(measureCosts)Report.providerCheckTicks+=Stopwatch.GetTimestamp()-started;}}
        void CheckProvidersCore()
        {
            foreach (ProviderRow row in providers)
            {
                Need(row.module != null && row.module.enabled == row.enabled && row.module.isEnabled == row.isEnabled && ReferenceEquals(row.module.part.Modules[row.source.moduleIndex], row.module), "Provider lifetime/enable mapping changed.");
                if (row.engine != null) Need(!row.engine.useEngineResponseTime && row.engine.EngineIgnited == row.engineIgnited && row.engine.flameout == row.flameout && row.engine.thrustPercentage == row.thrustLimiter, "Fixed engine ignition/flameout/thrust-limit source changed.");
                if (row.solar != null)
                    Need(!row.solar.isTracking && !row.solar.useAnimation && !row.solar.useRaycastForTrackingDot && ReferenceEquals(row.solar.trackingDotTransform, row.dot) && row.dot != null && row.dot.gameObject.activeInHierarchy && ReferenceEquals(row.dot.parent, row.dotParent) && row.dot.localPosition == row.dotPosition && row.dot.localRotation == row.dotRotation && row.dot.localScale == row.dotScale && row.solar.efficiencyMult == row.efficiency && row.solar.launchUT == row.launchUt && Equals(row.solar.deployState, row.deployment) && row.solar.part.submergedPortion == 0, "Fixed solar binding/state changed.");
                if (row.transmitter != null) Need(!row.transmitter.IsBusy(), "Transmission began without original handback.");
                if (row.antenna != null) Need(Equals(row.antenna.deployState, row.deployment), "Antenna deployment changed.");
                if (row.command != null) Need(row.command.minimumCrew == 0 && row.command.hibernation == row.hibernating && row.command.hibernateOnWarp == row.hibernateOnWarp && row.command.hibernationMultiplier == row.hibernationMultiplier, "Acquired command hibernation/load policy changed.");
                if (row.cargo != null) Need(!(bool)CargoSettling.GetValue(row.cargo) && !(bool)CargoAttached.GetValue(row.cargo) && !(bool)CargoConstruction.GetValue(row.cargo), "Cargo settling/attachment/construction state changed.");
                if (row.variants != null) Need(ReferenceEquals(row.variants.variantList, row.variantList) && row.variants.variantList.Count == row.variantCount && row.variants.GetCurrentVariantIndex() == row.variantIndex && ReferenceEquals(row.variants.SelectedVariant, row.variant) && row.variants.useVariantMass == row.variantMass, "Frozen selected variant identity/mass mode changed.");
                if (row.throttleAnimation != null)
                {
                    var animation = row.throttleAnimation; var state = row.animation == null ? null : row.animation[row.animationName];
                    Need(ReferenceEquals(ThrottleAnimation.GetValue(animation), row.animation) && ReferenceEquals(ThrottleEngine.GetValue(animation), row.animationEngine) && row.animation != null && row.animation.GetClip(row.animationName) == row.clip && animation.animationName == row.animationName && animation.affectTime && animation.animState == 0 && state != null && state.speed == 0 && state.normalizedTime == row.animationTime && state.weight == row.animationWeight && state.layer == row.animationLayer && state.wrapMode == row.animationWrapMode && !row.animationEngine.EngineIgnited && !row.animationEngine.isOperational && row.animationEngine.currentThrottle == 0 && row.animationEngine.requestedThrottle == 0, "Idle engine animation became active or changed its actual clip/state binding.");
                    for (int j = 0; j < row.animationForest.Length; j++)
                    { var transform = row.animationForest[j]; Need(transform != null && ReferenceEquals(transform.parent, row.animationParents[j]) && transform.localPosition == row.animationPositions[j] && transform.localRotation == row.animationRotations[j] && transform.localScale == row.animationScales[j], "Idle animation hierarchy/local pose changed."); }
                }
            }
        }
        void PlanPublication(OwnedFlightPresentationLease view)
        {
            var read = view.Read;
            Need(read.RegionCount == 1 && read.PartCount == parts.Length && read.BodyCount == bodies.Length && read.ResourceCount == resources.Length && view.Context.VesselId == vessel.id.ToString("D") && view.Context.SourceSha256 == binding.ContextSha256 && view.FrameId == publishedStamp.FrameId, "Published domain/source coverage differs.");
            Need(Finite(view.UniversalTime) && (checkpointPublication || view.UniversalTime >= publishedUt && view.Read.Stamp.Time >= publishedRuntimeTime), "Owned time moved backwards without a restore transaction.");
            region = read.GetRegion(0); regionOmega = OwnedRuntimeDynamics.AngularVelocity(region.Properties, region.Motion.Orientation, region.Motion.AngularMomentum);
            centerBodyPosition = view.Descriptor.CenterPosition(read, 0); var environment = view.Descriptor.Sample(read, 0);
            Need(Finite(region.Properties.Mass) && region.Properties.Mass > 0 && view.Context.Altitude(centerBodyPosition) >= binding.MinimumAltitudeMeters && view.UniversalTime - view.Context.Epoch <= 300, "Prospective surface/source-validity envelope is unsupported.");
            var reference = region.Motion.Orientation;
            foreach (BodyRow row in bodies)
            {
                Need(read.Definition.GetBody(row.slot).Id == row.id && read.Topology.RegionOfBody(row.slot) == 0, "Native physical identity/region changed.");
                var member = read.GetBodyMember(row.slot);
                Vec bodyPosition = view.Descriptor.BodyPosition(read, 0) + reference.Rotate(member.LocalPosition);
                Vec bodyCenter = bodyPosition + (reference * member.LocalRotation).Rotate(member.LocalCenterOfMass);
                row.position = V(CenterWorld(bodyPosition)); row.rotation = Q(reference * member.LocalRotation);
                row.worldCenter = V(CenterWorld(bodyCenter)); row.center = V(member.LocalCenterOfMass);
                Vec pointVelocity = region.Motion.Velocity + Vec.Cross(regionOmega, bodyCenter + centerBodyPosition * -1);
                Vector3d rotationFrame = vessel.orbit.GetRotFrameVelAtPos(centralBody, D(bodyCenter).xzy).xzy;
                Vector3d velocityFrame = Krakensbane.GetFrameVelocity();
                row.velocity = V(pointVelocity + new Vec(rotationFrame.x + velocityFrame.x, rotationFrame.y + velocityFrame.y, rotationFrame.z + velocityFrame.z) * -1); row.omega = V(regionOmega);
                row.inertia = V(member.PrincipalInertia * .001); row.inertiaRotation = Q(member.PrincipalRotation); row.mass = (float)(member.Mass * .001);
                Need(row.mass > 0 && !float.IsInfinity(row.mass) && row.inertia.x > 0 && row.inertia.y > 0 && row.inertia.z > 0, "Native SI property conversion is unsupported.");
            }
            foreach (ResourceRow row in resources)
            { row.value = read.GetResourceQuantity(row.slot); Need(Finite(row.value) && row.value >= 0 && (row.value <= row.capacity || read.Definition.GetResource(row.slot).AcquiredSurplus), "Owned resource publication outside acquired capacity."); }
            foreach (PartRow row in parts)
            {
                double temperature = read.GetTemperature(row.slot);
                Need(Finite(temperature) && temperature > 0 && temperature < row.part.maxTemp && temperature < row.part.skinMaxTemp, "Owned thermal failure regime requires stock handback.");
            }
            inputDelta = Math.Min(.1, Math.Max(.0001, read.Stamp.Time - publishedRuntimeTime));
        }
        Vec CenterWorld(Vec bodyPosition) { return bodyPosition + new Vec(centralBody.position.x, centralBody.position.y, centralBody.position.z); }
        public void Publish(OwnedFlightPresentationLease view)
        {long started=measureCosts?Stopwatch.GetTimestamp():0;try{PublishCore(view);}finally{if(measureCosts)Report.publicationTicks+=Stopwatch.GetTimestamp()-started;}}
        void PublishCore(OwnedFlightPresentationLease view)
        {
            MainThread(); Need(prepared && !disposed && !publishing, "Publication state differs.");
            Need(ControlReferenceUnchanged(), "Acquired control reference hierarchy changed before publication."); CheckAliases(); CheckProviders();
            PlanPublication(view); bool started = false;
            publishing = true;
            try
            {
                started = true; Planetarium.SetUniversalTime(view.UniversalTime); Report.clockWrites++; Planetarium.fetch.UpdateCBs();
                // Celestial publication can move the native coordinate origin. Recompute targets from the same leased physical epoch.
                PlanPublication(view); ApplyNativePoses(); ApplyNativeProperties();
                foreach (ResourceRow row in resources) row.resource.amount = row.value;
                foreach (PartRow row in parts) { row.part.temperature = view.Read.GetTemperature(row.slot); row.part.skinTemperature = row.part.temperature; }
                PublishStats(); PublishOrbit(view.UniversalTime);
                binding.PublishProviders(view);
                foreach (ResourceRow row in resources) Need(row.resource.amount == row.value && row.resource.maxAmount == row.capacity, "Provider publisher changed the authoritative resource fold.");
                foreach (PartRow row in parts) Need(row.part.temperature == view.Read.GetTemperature(row.slot), "Provider publisher changed authoritative temperature.");
                ValidatePoses();
                publishedUt = view.UniversalTime; publishedRuntimeTime = view.Read.Stamp.Time; publishedStamp = view.Read.Stamp; Report.publishedUniversalTime = publishedUt; Report.publications++;
                checkpointPublication = false;
            }
            catch (Exception error)
            { if (started) { Report.partialPublication = true; Report.partialPublications++; } Report.status = "invalid"; Report.detail = error.ToString(); throw; }
            finally { publishing = false; }
        }
        void ApplyNativePoses()
        {
            foreach (BodyRow row in publicationOrder)
            { row.body.transform.SetPositionAndRotation(row.position, row.rotation); row.body.position = row.position; row.body.rotation = row.rotation; Report.nativePoseWrites++; }
            Physics.SyncTransforms();
        }
        void ApplyNativeProperties()
        {
            foreach (BodyRow row in bodies)
            {
                Rigidbody rb = row.body;
                if (rb.mass != row.mass) rb.mass = row.mass;
                if (rb.centerOfMass != row.center) rb.centerOfMass = row.center;
                if (rb.inertiaTensor != row.inertia) rb.inertiaTensor = row.inertia;
                if (rb.inertiaTensorRotation != row.inertiaRotation) rb.inertiaTensorRotation = row.inertiaRotation;
                rb.velocity = row.velocity; rb.angularVelocity = row.omega;
            }
            // Native property setters can shift a Rigidbody's reported pose. The canonical target remains authoritative.
            ApplyNativePoses();
        }
        void ValidatePoses()
        {
            foreach (BodyRow row in bodies)
            {
                double a = (row.body.position - row.position).magnitude, b = (row.body.transform.position - row.position).magnitude;
                double c = Quaternion.Angle(row.body.rotation, row.rotation), d = Quaternion.Angle(row.body.transform.rotation, row.rotation);
                Report.maximumPoseError = Math.Max(Report.maximumPoseError, Math.Max(a, b)); Report.maximumRotationErrorDegrees = Math.Max(Report.maximumRotationErrorDegrees, Math.Max(c, d));
                Need(Finite(a) && Finite(b) && Finite(c) && Finite(d) && a <= 1e-4 && b <= 1e-4 && c <= .002 && d <= .002, "Native/Transform publication differs from retained targets.");
            }
        }
        void PublishStats()
        {
            Need(ControlReferenceUnchanged(),"Acquired control reference hierarchy changed before stats publication.");
            var motion = region.Motion; var properties = region.Properties;
            vessel.totalMass = properties.Mass * .001; vessel.CoMD = D(CenterWorld(centerBodyPosition)); vessel.CoM = vessel.CoMD; vessel.localCoM = vessel.transform.InverseTransformPoint(vessel.CoM);
            vessel.obt_velocity = D(motion.Velocity); vessel.srf_velocity = vessel.obt_velocity - centralBody.getRFrmVel(vessel.CoMD);
            vessel.velocityD = vessel.obt_velocity - vessel.orbit.GetRotFrameVelAtPos(centralBody, D(centerBodyPosition).xzy).xzy; vessel.rb_velocityD = vessel.velocityD - Krakensbane.GetFrameVelocity(); vessel.rb_velocity = vessel.rb_velocityD;
            var inverse = Quaternion.Inverse(controlReference.rotation); vessel.angularVelocity = inverse * V(regionOmega); vessel.angularVelocityD = vessel.angularVelocity;
            Rotation axesWorld = motion.Orientation * properties.PrincipalRotation; Quaternion axes = inverse * Q(axesWorld);
            Vector3 a = axes * Vector3.right, b = axes * Vector3.up, c = axes * Vector3.forward, i = V(properties.PrincipalInertia * .001);
            vessel.MOI = new Vector3(i.x * a.x * a.x + i.y * b.x * b.x + i.z * c.x * c.x, i.x * a.y * a.y + i.y * b.y * b.y + i.z * c.y * c.y, i.x * a.z * a.z + i.y * b.z * b.z + i.z * c.z * c.z);
            vessel.angularMomentum = inverse * V(motion.AngularMomentum * .001);
            vessel.altitude = centralBody.GetAltitude(vessel.CoMD); vessel.latitude = centralBody.GetLatitude(vessel.CoMD); vessel.longitude = centralBody.GetLongitude(vessel.CoMD);
            StatsComplete.SetValue(vessel.precalc, true);
        }
        void PublishOrbit(double universalTime)
        {
            var driver = vessel.orbitDriver;
            Vector3d position = D(centerBodyPosition).xzy, velocity = D(region.Motion.Velocity).xzy;
            driver.orbit.UpdateFromStateVectors(position, velocity, centralBody, universalTime);
            driver.pos = D(centerBodyPosition); driver.vel = D(region.Motion.Velocity); driver.lastTrackUT = universalTime;
        }
        public void CollectInput()
        {
            MainThread(); if (!installed || collecting) return;
            if(!ControlReferenceUnchanged()){service.HandbackBeforeStockTransition("control-reference-changed-before-input");return;}
            collecting = true;
            try
            {
                foreach (ProviderRow row in providers) if (row.sas != null) SasMethod.Invoke(row.sas, null);
                InputMethod.Invoke(FlightInputHandler.fetch, null); Report.inputPasses++;
                if (installed && vessel.ctrlState.mainThrottle != 0)
                    foreach (ProviderRow row in providers) if (row.throttleAnimation != null) { service.HandbackBeforeStockTransition("idle-engine-animation-throttle-demand"); break; }
            }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            finally { collecting = false; }
        }
        public OwnedRuntimeControls ReadControls()
        {
            Need(ControlReferenceUnchanged(),"Acquired control reference changed before controls were read.");
            MainThread(); var input = vessel.ctrlState; Need(input != null, "Published input chain disappeared.");
            bool ready = false; foreach (ProviderRow row in providers) { var command = row.module as ModuleCommand; if (command != null && command.GetControlSourceState() != VesselControlState.None) ready = true; }
            if (!ready) return new OwnedRuntimeControls();
            var value = new OwnedRuntimeControls { Throttle = input.mainThrottle, Attitude = new Vec(input.pitch, input.yaw, input.roll), Translation = new Vec(input.X, input.Y, input.Z), EnginesEnabled = true, RcsEnabled = vessel.ActionGroups[KSPActionGroup.RCS], WheelsEnabled = true };
            value.Validate(); return value;
        }
        public void RestoreNative(OwnedFlightPresentationLease view)
        {
            MainThread(); Need(prepared && !disposed && !restoring, "Native handback state differs."); restoring = true;
            try
            {
                Publish(view);
                Need(thermalHandback!=null,"Thermal handback history adapter is absent.");
                thermalHandback.Apply(view);
                var failures = new List<string>();
                foreach (BodyRow row in bodies)
                {
                    try
                    {
                        Need(row.body != null, "Source Rigidbody was destroyed.");
                        Need(ReferenceEquals(row.body.transform.parent, row.parent) && row.body.transform.GetSiblingIndex() == row.sibling && row.body.transform.localScale == row.localScale, "Original native hierarchy cannot be restored implicitly.");
                        row.body.isKinematic = row.initialKinematic; row.body.useGravity = row.initialGravity; row.body.detectCollisions = row.initialCollisions;
                        row.body.drag = row.initialDrag; row.body.angularDrag = row.initialAngularDrag;
                        row.body.velocity = row.velocity; row.body.angularVelocity = row.omega;
                        Need(row.body.isKinematic == row.initialKinematic && row.body.useGravity == row.initialGravity && row.body.detectCollisions == row.initialCollisions, "Native flag handback readback failed."); row.changed = false;
                    }
                    catch (Exception error) { failures.Add(row.id + ":" + error.Message); }
                }
                Need(failures.Count == 0, "Native restoration remains incomplete: " + string.Join(";", failures.ToArray())); Report.nativeRestored = true;
            }
            finally { restoring = false; }
        }
        public void RestoreAdmission()
        {
            MainThread(); if (!nativeMutation) { Report.nativeRestored = true; return; }
            var failures = new List<string>();
            foreach (BodyRow row in bodies)
            {
                if (!row.changed) continue;
                try
                {
                    Need(row.body != null, "Admission Rigidbody disappeared."); var rb = row.body;
                    rb.mass = row.initialMass; rb.centerOfMass = row.initialCenter; rb.inertiaTensor = row.initialInertia; rb.inertiaTensorRotation = row.initialInertiaRotation;
                    rb.transform.SetPositionAndRotation(row.initialPosition, row.initialRotation); rb.position = row.initialPosition; rb.rotation = row.initialRotation;
                    rb.isKinematic = row.initialKinematic; rb.useGravity = row.initialGravity; rb.detectCollisions = row.initialCollisions; rb.drag = row.initialDrag; rb.angularDrag = row.initialAngularDrag; rb.velocity = row.initialVelocity; rb.angularVelocity = row.initialOmega;
                    Need(rb.isKinematic == row.initialKinematic && rb.useGravity == row.initialGravity && rb.detectCollisions == row.initialCollisions, "Admission flags were not restored."); row.changed = false;
                }
                catch (Exception error) { failures.Add(row.id + ":" + error.Message); }
            }
            Physics.SyncTransforms(); Need(failures.Count == 0, "Admission rollback incomplete: " + string.Join(";", failures.ToArray())); Report.nativeRestored = true;
        }
        public void Dispose()
        {
            MainThread(); if (disposed) return; Need(Report.nativeRestored, "Native restoration must complete before fixed scheduling returns.");
            RestoreLoop();
            var failures = new List<string>();
            foreach (MethodBase method in attempted)
            {
                try { harmony.Unpatch(method, HarmonyPatchType.All, Owner); }
                catch (Exception error) { failures.Add(method.Name + ":" + error.Message); }
            }
            foreach (MethodBase method in attempted)
            { var p = Harmony.GetPatchInfo(method); if (p != null && p.Owners.Contains(Owner)) failures.Add(method.Name + ":owned-hooks-remain"); }
            Time.timeScale = originalTimeScale; Time.fixedDeltaTime = originalFixedDelta; Planetarium.fetch.fixedDeltaTime = originalPlanetariumDelta; Planetarium.TimeScale = originalPlanetariumScale;
            TimeWarp.fetch.Mode = originalWarpMode; TimeWarp.fetch.current_rate_index = originalWarpIndex; WarpCurrent.SetValue(TimeWarp.fetch, originalWarpCurrent); WarpPrevious.SetValue(TimeWarp.fetch, originalWarpPrevious);
            Need(failures.Count == 0, "Owned patch removal incomplete: " + string.Join(";", failures.ToArray()));
            foreach (Hook hook in hooks.Values) hook.row.removed = true; foreach (Callsite plan in callsites.Values) plan.row.removed = true;
            Report.hooksRemoved = true; Report.cleanupStatus = "original-fixed-native-and-hooks-restored"; Report.status = "handed-back"; disposed = true; installed = false; if (active == this) active = null;
        }

        bool Owns(object instance)
        {
            if (instance == null) return false;
            if (ReferenceEquals(instance, vessel) || ReferenceEquals(instance, vessel.precalc) || ReferenceEquals(instance, vessel.orbitDriver)) return true;
            var part = instance as Part; if (part != null) return partLookup.ContainsKey(part);
            var module = instance as PartModule; if (module != null) return providerLookup.ContainsKey(module);
            var fi = instance as FlightIntegrator; if (fi != null) return ReferenceEquals(fi.Vessel, vessel);
            return false;
        }
        static bool SuppressOwned(object __instance, MethodBase __originalMethod)
        { var b = active; if (b == null || !b.installed || !b.Owns(__instance)) return true; return b.collecting && __instance is ModuleSAS && Same(__originalMethod, SasMethod); }
        static bool Stats(VesselPrecalculate __instance)
        { var b = active; if (b == null || !b.installed || !ReferenceEquals(__instance, b.vessel.precalc)) return true; b.PublishStats(); return false; }
        static bool OrbitUpdate(OrbitDriver __instance)
        { var b = active; if (b == null || !b.installed || !ReferenceEquals(__instance, b.vessel.orbitDriver)) return true; return b.publishing || b.restoring; }
        static bool CommandState(ModuleCommand __instance, ref VesselControlState __result)
        { var b = active; if (b == null || !b.installed || !b.providerLookup.ContainsKey(__instance)) return true; __result = __instance.GetControlSourceState(); return false; }
        static bool IsHibernating(ModuleCommand __instance, ref bool __result)
        { var b = active; if (b == null || !b.installed || !b.providerLookup.ContainsKey(__instance)) return true; __result = __instance.hibernation; return false; }
        static void BeforeFrameTransition(MethodBase __originalMethod)
        {
            var b=active;if(b==null||!b.installed)return;b.MainThread();
            Need(!b.publishing&&!b.restoring,"Unadmitted stock frame mutation during owned publication/restoration: "+__originalMethod.Name);
            b.service.HandbackBeforeStockTransition("external-stock-frame-transition:"+__originalMethod.DeclaringType.FullName+"."+__originalMethod.Name);
            Need(!b.installed,"Stock frame mutation requires completed bridge handback.");
        }
        static void BeforeUniversalTime(double t)
        {
            var b = active; if (b == null || !b.installed || b.publishing || b.restoring || t == b.publishedUt) return;
            b.service.HandbackBeforeStockTransition("external-universal-time-write");
        }
        static bool FixedDelta(ref float __result)
        { var b = active; if (b == null || !b.installed) return true; __result = (float)b.inputDelta; return false; }
        static bool Delta(ref float __result)
        { var b = active; if (b == null || !b.installed) return true; __result = b.collecting ? (float)b.inputDelta : Time.deltaTime; return false; }
        static bool CurrentRate(ref float __result)
        { var b = active; if (b == null || !b.installed) return true; __result = 1; return false; }
        static bool WarpMode(ref TimeWarp.Modes __result)
        { var b = active; if (b == null || !b.installed || !b.collecting) return true; __result = TimeWarp.Modes.LOW; return false; }
        void CaptureControlReferenceChain()
        {
            var chain = new List<ControlReferenceNode>();
            var seen = new HashSet<Transform>();
            Transform cursor = controlReference;
            while (cursor != null && !transformLookup.ContainsKey(cursor))
            {
                Need(chain.Count < 256 && seen.Add(cursor), "Control reference ancestry is cyclic or exceeds its bound.");
                var node = new ControlReferenceNode { transform = cursor, parent = cursor.parent,
                    position = cursor.localPosition, rotation = cursor.localRotation, scale = cursor.localScale };
                Need(Finite(node.position.x) && Finite(node.position.y) && Finite(node.position.z) &&
                    Finite(node.rotation.x) && Finite(node.rotation.y) && Finite(node.rotation.z) && Finite(node.rotation.w) &&
                    Finite(node.scale.x) && Finite(node.scale.y) && Finite(node.scale.z), "Control reference local frame is nonfinite.");
                chain.Add(node); controlReferenceTransforms.Add(cursor); cursor = cursor.parent;
            }
            Need(cursor != null && transformLookup.ContainsKey(cursor), "Control reference is not rooted in an admitted body.");
            controlReferenceBody = cursor;
            controlReferenceChain = chain.ToArray();
        }
        bool ControlReferenceUnchanged()
        {
            if (controlReference == null || controlReferenceBody == null || controlReferenceChain == null ||
                !transformLookup.ContainsKey(controlReferenceBody) || !ReferenceEquals(vessel.ReferenceTransform, controlReference) ||
                !ReferenceEquals(vessel.GetReferenceTransformPart(), controlReferencePart)) return false;
            foreach (var node in controlReferenceChain)
                if (node.transform == null || !ReferenceEquals(node.transform.parent, node.parent) ||
                    !node.transform.localPosition.Equals(node.position) || !node.transform.localRotation.Equals(node.rotation) ||
                    !node.transform.localScale.Equals(node.scale)) return false;
            return true;
        }
        public void SetDisplayWarp(double rate)
        {MainThread();Need(installed&&Finite(rate)&&rate>0&&rate<=1000000,"Owned warp demand outside bounds.");Report.independentWarpDemand=rate;KeepStockWarpNormal(TimeWarp.fetch);}
        static void KeepStockWarpNormal(TimeWarp original)
        { original.current_rate_index=0;WarpCurrent.SetValue(original,1f);WarpPrevious.SetValue(original,1f); }
        static bool SetRate(TimeWarp __instance, int rateIdx, ref bool __result)
        {
            var b = active; if (b == null || !b.installed) return true;
            var rates = __instance.Mode == TimeWarp.Modes.LOW ? __instance.physicsWarpRates : __instance.warpRates;
            Need(rates != null && rateIdx >= 0 && rateIdx < rates.Length, "Selected stock warp index unavailable.");
            b.service.StockWarp(rates[rateIdx]); b.Report.independentWarpDemand = rates[rateIdx]; KeepStockWarpNormal(__instance);
            Time.timeScale = 1; GameEvents.onTimeWarpRateChanged.Fire(); __result = true; return false;
        }
        static bool SetMode(TimeWarp __instance, TimeWarp.Modes mode, ref bool __result)
        {
            var b = active; if (b == null || !b.installed) return true;
            __instance.Mode = mode; b.service.StockWarp(1); b.Report.independentWarpDemand = 1;
            KeepStockWarpNormal(__instance); GameEvents.onTimeWarpRateChanged.Fire(); __result = true; return false;
        }
        static bool UpdateRate(float r)
        { var b = active; if (b == null || !b.installed) return true; b.service.StockWarp(r); b.Report.independentWarpDemand = r; KeepStockWarpNormal(TimeWarp.fetch); return false; }
        static bool BeforePause(bool pauseState)
        {
            var b = active; if (b == null || !b.installed) return true;
            string reason = OwnedClockFlightService.SetPaused(pauseState); Need(reason == null, "Owned pause failed: " + reason);
            // Keep original pause UI and input-lock behavior. Its native scale is unrelated to the independent worker clock.
            return true;
        }
        static void BeforeTransition(object __instance, MethodBase __originalMethod)
        {
            var b = active; if (b == null || !b.installed || b.publishing || b.restoring) return;
            if (__instance != null && (__instance is Vessel || __instance is Part || __instance is PartModule) && !b.Owns(__instance))
            {
                var other=__instance as Vessel;
                if(other!=null)
                {
                    if(!other.loaded&&(Same(__originalMethod,ProtoSetPosition)||Same(__originalMethod,ProtoSetRotation)))
                    {
                        var transform=other.vesselTransform;
                        Need(transform!=null&&!ReferenceEquals(transform,b.vessel.vesselTransform)&&!transform.IsChildOf(b.vessel.vesselTransform),"Unloaded proto facade aliases the owned presentation hierarchy.");
                        b.Report.admittedProtoFacadeWrites++;return;
                    }
                    if(!other.loaded&&other.packed&&(Same(__originalMethod,ProtoSetVelocity)||Same(__originalMethod,ProtoChangeVelocity)))
                    {b.Report.admittedProtoVelocityNoOps++;return;}
                    b.RecordHandbackOperation(other,__originalMethod);b.service.HandbackBeforeStockTransition("other-vessel-load-or-lifecycle:"+__originalMethod.Name);
                }
                return;
            }
            b.RecordHandbackOperation(__instance,__originalMethod);
            b.service.HandbackBeforeStockTransition("stock-before:" + __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name);
        }
        void RecordHandbackOperation(object instance,MethodBase method)
        {
            Report.handbackOperation=method.DeclaringType.FullName+"."+method.Name;
            var actor=instance as Vessel;var part=instance as Part;var module=instance as PartModule;
            if(actor!=null)Report.handbackOperationInstance="vessel:"+actor.id.ToString("D")+":loaded="+actor.loaded+":packed="+actor.packed;
            else if(part!=null)Report.handbackOperationInstance="part:"+part.flightID;
            else if(module!=null)Report.handbackOperationInstance="module:"+(module.part==null?"<null>":module.part.flightID.ToString())+":"+module.GetType().FullName;
            else Report.handbackOperationInstance=instance==null?"static":instance.GetType().FullName;
        }
        static void BeforeVariant(Part part)
        { var b = active; if (b != null && b.installed && !b.publishing && !b.restoring && part != null && b.partLookup.ContainsKey(part)) b.service.HandbackBeforeStockTransition("stock-before:ModulePartVariants.ApplyVariant"); }
        static void BeforeResource(Part __instance)
        { var b = active; if (b != null && b.installed && !b.publishing && !b.restoring && b.partLookup.ContainsKey(__instance)) { b.Report.rejectedWriters++; b.service.HandbackBeforeStockTransition("unsupported-original-resource-writer"); } }
        void Writer(Rigidbody receiver, string kind)
        {
            if (!installed || publishing || restoring || receiver == null || !bodyLookup.ContainsKey(receiver)) return;
            Report.rejectedWriters++; service.HandbackBeforeStockTransition("unsupported-native-writer:" + kind);
        }
        void TransformWriter(Transform receiver, string kind)
        {
            if (!installed || publishing || restoring || receiver == null ||
                (!transformLookup.ContainsKey(receiver) && !controlReferenceTransforms.Contains(receiver))) return;
            Report.rejectedWriters++; service.HandbackBeforeStockTransition("unsupported-transform-writer:" + kind);
        }
        static bool Body(Rigidbody receiver, out BodyRow row)
        {
            row = null; var b = active;
            if (b == null || !b.installed || b.publishing || b.restoring || ReferenceEquals(receiver, null)) return false;
            return b.bodyLookup.TryGetValue(receiver, out row);
        }
        static Dictionary<MethodInfo, MethodInfo> BuildRoutes()
        {
            var rows = new Dictionary<MethodInfo, MethodInfo>();
            AddRoute(rows, typeof(Rigidbody), "get_velocity", nameof(Velocity), Type.EmptyTypes); AddRoute(rows, typeof(Rigidbody), "get_angularVelocity", nameof(AngularVelocity), Type.EmptyTypes);
            AddRoute(rows, typeof(Rigidbody), "get_worldCenterOfMass", nameof(WorldCenter), Type.EmptyTypes); AddRoute(rows, typeof(Rigidbody), "get_centerOfMass", nameof(LocalCenter), Type.EmptyTypes);
            AddRoute(rows, typeof(Rigidbody), "get_mass", nameof(Mass), Type.EmptyTypes); AddRoute(rows, typeof(Rigidbody), "get_inertiaTensor", nameof(Inertia), Type.EmptyTypes);
            AddRoute(rows, typeof(Rigidbody), "get_inertiaTensorRotation", nameof(InertiaRotation), Type.EmptyTypes); AddRoute(rows, typeof(Rigidbody), "get_isKinematic", nameof(IsKinematic), Type.EmptyTypes);
            AddRoute(rows, typeof(Rigidbody), "GetPointVelocity", nameof(PointVelocity), new[] { typeof(Vector3) });
            AddRoute(rows, typeof(Rigidbody), "set_velocity", nameof(SetVelocity), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Rigidbody), "set_angularVelocity", nameof(SetAngularVelocity), new[] { typeof(Vector3) });
            AddRoute(rows, typeof(Rigidbody), "set_position", nameof(SetPosition), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Rigidbody), "set_rotation", nameof(SetRotation), new[] { typeof(Quaternion) });
            AddRoute(rows, typeof(Rigidbody), "MovePosition", nameof(MovePosition), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Rigidbody), "MoveRotation", nameof(MoveRotation), new[] { typeof(Quaternion) });
            AddRoute(rows, typeof(Rigidbody), "set_mass", nameof(SetMass), new[] { typeof(float) }); AddRoute(rows, typeof(Rigidbody), "set_centerOfMass", nameof(SetLocalCenter), new[] { typeof(Vector3) });
            AddRoute(rows, typeof(Rigidbody), "set_inertiaTensor", nameof(SetInertia), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Rigidbody), "set_inertiaTensorRotation", nameof(SetInertiaRotation), new[] { typeof(Quaternion) });
            AddRoute(rows, typeof(Rigidbody), "ResetCenterOfMass", nameof(ResetCenter), Type.EmptyTypes); AddRoute(rows, typeof(Rigidbody), "ResetInertiaTensor", nameof(ResetInertia), Type.EmptyTypes);
            AddRoute(rows, typeof(Rigidbody), "AddForce", nameof(Force), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Rigidbody), "AddForce", nameof(ForceModeCall), new[] { typeof(Vector3), typeof(ForceMode) });
            AddRoute(rows, typeof(Rigidbody), "AddTorque", nameof(Torque), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Rigidbody), "AddTorque", nameof(TorqueModeCall), new[] { typeof(Vector3), typeof(ForceMode) });
            AddRoute(rows, typeof(Rigidbody), "AddForceAtPosition", nameof(PointForce), new[] { typeof(Vector3), typeof(Vector3) }); AddRoute(rows, typeof(Rigidbody), "AddForceAtPosition", nameof(PointForceMode), new[] { typeof(Vector3), typeof(Vector3), typeof(ForceMode) });
            AddRoute(rows, typeof(Transform), "set_position", nameof(TransformPosition), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Transform), "set_rotation", nameof(TransformRotation), new[] { typeof(Quaternion) });
            AddRoute(rows, typeof(Transform), "SetPositionAndRotation", nameof(TransformPose), new[] { typeof(Vector3), typeof(Quaternion) });
            AddRoute(rows, typeof(Transform), "set_localPosition", nameof(TransformLocalPosition), new[] { typeof(Vector3) }); AddRoute(rows, typeof(Transform), "set_localRotation", nameof(TransformLocalRotation), new[] { typeof(Quaternion) }); AddRoute(rows, typeof(Transform), "set_localScale", nameof(TransformScale), new[] { typeof(Vector3) });
            AddRoute(rows, typeof(Transform), "set_parent", nameof(TransformParent), new[] { typeof(Transform) });
            AddRoute(rows, typeof(Transform), "SetParent", nameof(TransformSetParent), new[] { typeof(Transform) });
            AddRoute(rows, typeof(Transform), "SetParent", nameof(TransformSetParentWorld), new[] { typeof(Transform), typeof(bool) });
            return rows;
        }
        static void AddRoute(Dictionary<MethodInfo, MethodInfo> rows, Type type, string name, string wrapperName, Type[] parameters)
        {
            var original = AccessTools.DeclaredMethod(type, name, parameters); var wrapper = AccessTools.DeclaredMethod(typeof(OwnedClockKspBridge), wrapperName);
            Need(original != null && wrapper != null && !original.IsStatic && wrapper.IsStatic && original.ReturnType == wrapper.ReturnType && wrapper.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { type }.Concat(parameters)), "Native wrapper ABI differs: " + name); rows.Add(original, wrapper);
        }
        static MethodInfo Route(MethodInfo called) { if (called == null) return null; foreach (var route in Routes) if (Same(called, route.Key)) return route.Value; return null; }
        static Dictionary<FieldInfo, MethodInfo> BuildFieldRoutes()
        {
            var rows = new Dictionary<FieldInfo, MethodInfo>();
            AddField(rows, typeof(PartResource), "amount", nameof(ResourceAmount)); AddField(rows, typeof(PartResource), "maxAmount", nameof(ResourceCapacity));
            AddField(rows, typeof(Part), "temperature", nameof(PartTemperature)); AddField(rows, typeof(Part), "skinTemperature", nameof(PartSkinTemperature));
            AddField(rows, typeof(Part), "force", nameof(PartForce)); AddField(rows, typeof(Part), "torque", nameof(PartTorque));
            AddField(rows, typeof(Planetarium), "time", nameof(PlanetariumTime)); return rows;
        }
        static void AddField(Dictionary<FieldInfo, MethodInfo> rows, Type type, string name, string replacement)
        {
            var field = AccessTools.DeclaredField(type, name); var method = AccessTools.DeclaredMethod(typeof(OwnedClockKspBridge), replacement);
            Need(field != null && !field.IsStatic && method != null && method.ReturnType == typeof(void) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { type, field.FieldType }), "Managed field guard ABI differs: " + name); rows.Add(field, method);
        }
        static MethodInfo FieldRoute(FieldInfo field)
        { if (field == null) return null; foreach (var row in FieldRoutes) if (field.Module.ModuleVersionId == row.Key.Module.ModuleVersionId && field.MetadataToken == row.Key.MetadataToken && field.DeclaringType == row.Key.DeclaringType) return row.Value; return null; }
        void ResourceWriter(PartResource receiver, string kind)
        {
            if (!installed || publishing || restoring || receiver == null) return;
            foreach (ResourceRow row in resources) if (ReferenceEquals(row.resource, receiver)) { Report.rejectedWriters++; service.HandbackBeforeStockTransition("unsupported-resource-field-writer:" + kind); return; }
        }
        void PartWriter(Part receiver, string kind)
        { if (installed && !publishing && !restoring && receiver != null && partLookup.ContainsKey(receiver)) { Report.rejectedWriters++; service.HandbackBeforeStockTransition("unsupported-part-field-writer:" + kind); } }
        static void ResourceAmount(PartResource p, double x) { if (active != null) active.ResourceWriter(p, "amount"); p.amount = x; }
        static void ResourceCapacity(PartResource p, double x) { if (active != null) active.ResourceWriter(p, "capacity"); p.maxAmount = x; }
        static void PartTemperature(Part p, double x) { if (active != null) active.PartWriter(p, "temperature"); p.temperature = x; }
        static void PartSkinTemperature(Part p, double x) { if (active != null) active.PartWriter(p, "skinTemperature"); p.skinTemperature = x; }
        static void PartForce(Part p, Vector3d x) { if (active != null) active.PartWriter(p, "force"); p.force = x; }
        static void PartTorque(Part p, Vector3d x) { if (active != null) active.PartWriter(p, "torque"); p.torque = x; }
        static void PlanetariumTime(Planetarium p, double x) { var b = active; if (b != null && b.installed && !b.publishing && !b.restoring && ReferenceEquals(p, Planetarium.fetch) && x != b.publishedUt) b.service.HandbackBeforeStockTransition("direct-universal-time-field-writer"); p.time = x; }
        static Vector3 Velocity(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.velocity : b.velocity; }
        static Vector3 AngularVelocity(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.omega : b.angularVelocity; }
        static Vector3 WorldCenter(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.worldCenter : b.worldCenterOfMass; }
        static Vector3 LocalCenter(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.center : b.centerOfMass; }
        static float Mass(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.mass : b.mass; }
        static Vector3 Inertia(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.inertia : b.inertiaTensor; }
        static Quaternion InertiaRotation(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.inertiaRotation : b.inertiaTensorRotation; }
        static bool IsKinematic(Rigidbody b) { BodyRow r; return Body(b, out r) ? r.initialKinematic : b.isKinematic; }
        static Vector3 PointVelocity(Rigidbody b, Vector3 point) { BodyRow r; return Body(b, out r) ? r.velocity + Vector3.Cross(r.omega, point - r.worldCenter) : b.GetPointVelocity(point); }
        static void SetVelocity(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "velocity"); b.velocity = x; }
        static void SetAngularVelocity(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "angularVelocity"); b.angularVelocity = x; }
        static void SetPosition(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "position"); b.position = x; }
        static void SetRotation(Rigidbody b, Quaternion x) { if (active != null) active.Writer(b, "rotation"); b.rotation = x; }
        static void MovePosition(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "MovePosition"); b.MovePosition(x); }
        static void MoveRotation(Rigidbody b, Quaternion x) { if (active != null) active.Writer(b, "MoveRotation"); b.MoveRotation(x); }
        static void SetMass(Rigidbody b, float x) { if (active != null) active.Writer(b, "mass"); b.mass = x; }
        static void SetLocalCenter(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "centerOfMass"); b.centerOfMass = x; }
        static void SetInertia(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "inertiaTensor"); b.inertiaTensor = x; }
        static void SetInertiaRotation(Rigidbody b, Quaternion x) { if (active != null) active.Writer(b, "inertiaTensorRotation"); b.inertiaTensorRotation = x; }
        static void ResetCenter(Rigidbody b) { if (active != null) active.Writer(b, "ResetCenterOfMass"); b.ResetCenterOfMass(); }
        static void ResetInertia(Rigidbody b) { if (active != null) active.Writer(b, "ResetInertiaTensor"); b.ResetInertiaTensor(); }
        static void Force(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "AddForce"); b.AddForce(x); }
        static void ForceModeCall(Rigidbody b, Vector3 x, ForceMode mode) { if (active != null) active.Writer(b, "AddForce"); b.AddForce(x, mode); }
        static void Torque(Rigidbody b, Vector3 x) { if (active != null) active.Writer(b, "AddTorque"); b.AddTorque(x); }
        static void TorqueModeCall(Rigidbody b, Vector3 x, ForceMode mode) { if (active != null) active.Writer(b, "AddTorque"); b.AddTorque(x, mode); }
        static void PointForce(Rigidbody b, Vector3 x, Vector3 point) { if (active != null) active.Writer(b, "AddForceAtPosition"); b.AddForceAtPosition(x, point); }
        static void PointForceMode(Rigidbody b, Vector3 x, Vector3 point, ForceMode mode) { if (active != null) active.Writer(b, "AddForceAtPosition"); b.AddForceAtPosition(x, point, mode); }
        static void TransformPosition(Transform t, Vector3 x) { if (active != null) active.TransformWriter(t, "position"); t.position = x; }
        static void TransformRotation(Transform t, Quaternion x) { if (active != null) active.TransformWriter(t, "rotation"); t.rotation = x; }
        static void TransformPose(Transform t, Vector3 p, Quaternion q) { if (active != null) active.TransformWriter(t, "SetPositionAndRotation"); t.SetPositionAndRotation(p, q); }
        static void TransformLocalPosition(Transform t, Vector3 x) { if (active != null) active.TransformWriter(t, "localPosition"); t.localPosition = x; }
        static void TransformLocalRotation(Transform t, Quaternion q) { if (active != null) active.TransformWriter(t, "localRotation"); t.localRotation = q; }
        static void TransformScale(Transform t, Vector3 x) { if (active != null) active.TransformWriter(t, "localScale"); t.localScale = x; }
        static void TransformParent(Transform t, Transform p) { if (active != null) active.TransformWriter(t, "parent"); t.parent = p; }
        static void TransformSetParent(Transform t, Transform p) { if (active != null) active.TransformWriter(t, "SetParent"); t.SetParent(p); }
        static void TransformSetParentWorld(Transform t, Transform p, bool worldPositionStays) { if (active != null) active.TransformWriter(t, "SetParent(worldPositionStays)"); t.SetParent(p, worldPositionStays); }

        public OwnedClockFlightService.ShellData CaptureShell(string sessionId)
        {
            MainThread(); Need(prepared && !disposed, "Shell capture requires a prepared live lease.");
            var rows = new OwnedClockFlightService.ShellRow[providers.Length]; long length = 0;
            for (int i = 0; i < providers.Length; i++)
            {
                var row = providers[i]; var node = new ConfigNode("MODULE"); row.module.Save(node); string text = node.ToString();
                length += text.Length; Need(text.Length <= 1024 * 1024 && length <= 16 * 1024 * 1024, "Cold module checkpoint exceeds bound.");
                rows[i] = new OwnedClockFlightService.ShellRow { partId = row.source.partId, moduleIndex = row.source.moduleIndex, sourceType = row.source.sourceType, node = text };
            }
            return new OwnedClockFlightService.ShellData { sessionId = sessionId, vesselId = vessel.id.ToString("D"), universalTime = publishedUt, modules = rows };
        }
        public void ValidateShell(OwnedClockFlightService.ShellData data, string sessionId)
        {
            MainThread(); Need(data != null && data.schema == "continuum-owned-clock-shell/v1" && data.sessionId == sessionId && data.vesselId == vessel.id.ToString("D") && Finite(data.universalTime) && data.modules != null && data.modules.Length == providers.Length, "Shell snapshot identity/coverage differs.");
            long length = 0;
            for (int i = 0; i < providers.Length; i++)
            {
                var row = data.modules[i]; var source = providers[i].source;
                Need(row != null && row.partId == source.partId && row.moduleIndex == source.moduleIndex && row.sourceType == source.sourceType && !string.IsNullOrEmpty(row.node) && row.node.Length <= 1024 * 1024, "Shell module mapping differs.");
                length += row.node.Length; Need(length <= 16 * 1024 * 1024 && ParseModule(row.node) != null, "Shell module layout outside bound.");
            }
        }
        static ConfigNode ParseModule(string value)
        { var node = ConfigNode.Parse(value); return node == null ? null : node.name == "MODULE" ? node : node.GetNode("MODULE"); }
        public void RestoreShell(OwnedClockFlightService.ShellData data, string sessionId)
        {
            ValidateShell(data, sessionId); var nodes = new ConfigNode[data.modules.Length];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = ParseModule(data.modules[i].node);
            restoring = true;
            try { for (int i = 0; i < nodes.Length; i++) providers[i].module.Load(nodes[i]); }
            catch (Exception error) { Report.partialPublication = true; Report.partialPublications++; Report.detail = "Shell restore partially published: " + error; throw; }
            finally { restoring = false; }
        }
        internal void AllowCheckpointPublication() { MainThread(); Need(installed && !disposed, "Checkpoint restore lease is unavailable."); checkpointPublication = true; }
    }
}
