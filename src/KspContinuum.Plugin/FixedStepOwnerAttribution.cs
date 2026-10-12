using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class FixedStepOwnerAttribution : IDisposable, IPlayerLoopBracketObserver
    {
        const string Owner = "continuum.fixed-step-owner-attribution";
        const string StockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const string MechSha = "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929";
        static readonly Guid StockMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf"), MechMvid = new Guid("30fbe3c4-e77e-4abc-895b-a635d5924a08");
        const int Slots = 10, MaxWindows = 1024, MaxDepth = 64, MaxStates = 256, MaxVessels = 256, MaxModules = 256;
        static readonly string Phase = typeof(UnityEngine.PlayerLoop.FixedUpdate.ScriptRunBehaviourFixedUpdate).FullName;
        static FixedStepOwnerAttribution active;
        static readonly MethodInfo[] Prefixes = { Callback(nameof(MassPrefix)), Callback(nameof(DragSetupPrefix)), Callback(nameof(ThermoPrefix)),
            Callback(nameof(FeedPrefix)), Callback(nameof(UpdateAnalysisPrefix)), Callback(nameof(PartsAnalysisPrefix)), Callback(nameof(OriginPrefix)),
            Callback(nameof(MainPhysicsPrefix)), Callback(nameof(PhysicsStatsPrefix)), Callback(nameof(SolarPrefix)) };
        static readonly MethodInfo End = AccessTools.DeclaredMethod(typeof(FixedStepOwnerAttribution), nameof(Finalizer));
        static MethodInfo Callback(string name) { return AccessTools.DeclaredMethod(typeof(FixedStepOwnerAttribution), name); }

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-fixed-step-owner-attribution/v2", runId, vesselId, status = "installing", cleanupStatus = "not-installed", detail;
            public string phase = Phase, clock = "System.Diagnostics.Stopwatch monotonic ticks", overheadStatus = "unqualified-intrusive-observer";
            public string scope = "Ten declared bulk original seams: active-vessel FI mass/drag-setup/thermodynamics, Vessel input feed, pinned MechJeb VesselState Update/AnalyzeParts, exact FloatingOrigin singleton, current vessel-owned MainPhysics/CalculatePhysicsStats and actual declared solar-panel FixedUpdate dispatch. Only main-thread entries inside ScriptRunBehaviourFixedUpdate windows whose Before sees captureActive are sampled. No FI.FixedUpdate, recursive Integrate, broad per-part/module, curve-evaluation or controller hooks. Solar override includes its original base call without a separate base hook. Existing AeroStepContext supplies a separate FI/root envelope. Outside/straddling windows must be filtered by exact timestamps. Zero calls is no execution coverage, not zero cost.";
            public string timingScope = "Inclusive wall body brackets preserve original returns/order/exceptions and include uninstrumented/native descendants plus observer dispatch. Self removes only directly observed child brackets, including recursion; rows are not exclusive CPU. Sum of self is bounded by its phase window. MJ inside/outside-feed counters classify the actually observed nesting, not Drive ownership or duplicated analysis. All states/core identities are retained; aggregate methods can contain multiple states. Phase clocks enclose original callbacks but exclude Before/After preparation, whose costs are recorded separately. No FPS or removable-time claim.";
            public string ownershipScope = "Reserved frozen research ownership: exact assembly SHA/MVID, method token/signature/body, empty preinstall graphs, exact owned prefix/finalizer graphs, complete teardown back to the original graphs. Pin/file/owner state is checked at install and cleanup; no per-call reflection/hash/patch-graph scan. Transient foreign mutation between endpoint audits is not proven absent. Unknown active-vessel state/owner, foreign hooks, original exceptions, stack/clock drift or bounds overflow invalidate qualification.";
            public string offsetClassificationScope = "Read-only original singleton fields before/after its callback. On a ready successful entry, original SetOffsetThisFrame resets before the body, so its return flag classifies the original setter flag as set/clear. Non-ready or exceptional entries are unclassified. Offset-vector bit changes are field changes only: neither changed nor unchanged fields independently prove a world-origin event or its absence. No origin setter/event is hooked.";
            public bool requested = true;
            public int processId, mainThreadId, targetCount, patchedMethods, maximumWindows = MaxWindows, maximumNestedFrames = MaxDepth;
            public int maximumStateOwners = MaxStates, maximumVesselOwners = MaxVessels, maximumModuleOwners = MaxModules, offsetFlagMetadataToken;
            public int errors, ownershipErrors, contextErrors, originalExceptions, droppedWindows, startedWindows, completedWindows, observedTargets;
            public long clockFrequency = Stopwatch.Frequency, capturedCalls, ignoredForeignInstances, admissionStartedTicks, admissionEndedTicks;
            public long storageStartedTicks, storageEndedTicks, storagePayloadBytes, beforeTicks, afterTicks, cleanupStartedTicks, cleanupEndedTicks, exportStartedTicks, exportEndedTicks;
            public string assemblySha256 = StockSha, assemblyMvid = StockMvid.ToString("D"), mechJebSha256, mechJebMvid;
            public Target[] targets = new Target[0];
            public StateOwner[] stateOwners = new StateOwner[0];
            public SingletonOwner[] singletonOwners = new SingletonOwner[0];
            public VesselOwner[] vesselOwners = new VesselOwner[0];
            public ModuleOwner[] moduleOwners = new ModuleOwner[0];
            public Window[] windows = new Window[0];
            public int[] unreachedSlots = new int[0];
            public string coverageStatus = "pending";
            public string[] preinstallPatchGraph = new string[0], installedPatchGraph = new string[0], precleanupPatchGraph = new string[0], restoredPatchGraph = new string[0];
        }
        [Serializable] public sealed class Target
        {
            public int slot, metadataToken;
            public string assembly, assemblySha256, assemblyMvid, declaringType, method, ilSha256, ownerScope;
        }
        [Serializable] public sealed class StateOwner
        {
            public int slot, coreInstanceId;
            public uint partFlightId;
            public string partName, coreType, stateType;
            public long updateCalls, analyzeCalls, insideFeedUpdateCalls, insideFeedAnalyzeCalls;
        }
        [Serializable] public sealed class SingletonOwner
        {
            public int instanceId;
            public string declaringType;
            public long calls, inclusiveTicks, selfTicks, offsetFlagSetCalls, offsetFlagSetTicks, offsetFlagClearCalls, offsetFlagClearTicks,
                offsetUnclassifiedCalls, offsetUnclassifiedTicks, offsetFieldsChangedCalls, offsetFieldsUnchangedCalls, offsetFlagAtEntrySetCalls;
        }
        [Serializable] public sealed class VesselOwner
        {
            public int slot, instanceId, precalcInstanceId;
            public string vesselId, vesselName, precalcType;
            public bool activeVessel, loadedAtAdmission, packedAtAdmission;
            public long mainPhysicsCalls, mainPhysicsInclusiveTicks, mainPhysicsSelfTicks, physicsStatsCalls, physicsStatsInclusiveTicks, physicsStatsSelfTicks;
        }
        [Serializable] public sealed class ModuleOwner
        {
            public int slot, instanceId, vesselSlot;
            public uint partFlightId;
            public string partName, moduleType;
            public bool activeVessel;
            public long calls, inclusiveTicks, selfTicks;
        }
        [Serializable] public sealed class Window
        {
            public int sequence, frame;
            public double fixedTimeSeconds;
            public long startedTicks, endedTicks, elapsedTicks;
            public bool captureActiveAtEnd;
            public Entry[] entries;
        }
        [Serializable] public sealed class Entry
        {
            public int slot, calls;
            public long inclusiveTicks, selfTicks, insideFeedCalls, insideFeedTicks, outsideFeedCalls, outsideFeedTicks;
            public long activeVesselCalls, activeVesselTicks, otherVesselCalls, otherVesselTicks;
            public long offsetFlagSetCalls, offsetFlagSetTicks, offsetFlagClearCalls, offsetFlagClearTicks, offsetUnclassifiedCalls, offsetUnclassifiedTicks;
        }
        struct Boundary
        { internal int Frame; internal double Time; internal long Start, End; internal bool CaptureAtEnd; }
        struct StackFrame
        { internal int Slot, Window, State; internal long Id, Start, Child; internal bool InsideFeed, ActiveVessel, OriginReady, OriginFlagBefore; internal Vector3d OffsetBefore; }
        public struct CallState
        { internal FixedStepOwnerAttribution owner; internal long id; internal int depth, window; internal bool entered; }
        sealed class Binding
        { internal PartModule core; internal object state; internal StateOwner report; }
        sealed class VesselBinding
        { internal Vessel vessel; internal VesselPrecalculate precalc; internal Guid id; internal VesselOwner report; }
        sealed class ModuleBinding
        { internal ModuleDeployableSolarPanel module; internal VesselBinding vessel; internal Part part; internal ModuleOwner report; }
        sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }

        readonly Harmony harmony = new Harmony(Owner);
        readonly Func<bool> capturing;
        readonly Vessel vessel;
        readonly FlightIntegrator integrator;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly MethodInfo[] methods = new MethodInfo[Slots];
        readonly Dictionary<object, Binding> states = new Dictionary<object, Binding>(new References());
        readonly List<Binding> stateBindings = new List<Binding>();
        readonly Dictionary<object, VesselBinding> precalcs = new Dictionary<object, VesselBinding>(new References());
        readonly List<VesselBinding> vesselBindings = new List<VesselBinding>();
        readonly Dictionary<object, ModuleBinding> modules = new Dictionary<object, ModuleBinding>(new References());
        readonly List<ModuleBinding> moduleBindings = new List<ModuleBinding>();
        readonly Dictionary<Assembly, string> assemblyHashes = new Dictionary<Assembly, string>();
        FloatingOrigin origin;
        AccessTools.FieldRef<FloatingOrigin, bool> offsetFlag;
        FieldInfo stateField;
        Type coreType;
        Boundary[] boundaries;
        StackFrame[] stack;
        int[] calls;
        long[] inclusive, self, insideCalls, insideTicks, outsideCalls, outsideTicks;
        long[] activeCalls, activeTicks, otherCalls, otherTicks, offsetSetCalls, offsetSetTicks, offsetClearCalls, offsetClearTicks, offsetUnknownCalls, offsetUnknownTicks;
        long serial;
        int windowCount, activeWindow = -1, depth, feedDepth;
        bool installed, disposed, faulted;
        public ReportData Report { get; private set; }

        FixedStepOwnerAttribution(string runId, Func<bool> capturing)
        {
            this.capturing = capturing; vessel = FlightGlobals.ActiveVessel; integrator = FlightIntegrator.ActiveVesselFI;
            Report = new ReportData { runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id,
                mainThreadId = thread, admissionStartedTicks = Stopwatch.GetTimestamp() };
            try
            {
                Require(active == null && integrator != null && integrator.GetType() == typeof(FlightIntegrator) &&
                    ReferenceEquals(AccessTools.Field(typeof(VesselModule), "vessel").GetValue(integrator), vessel), "Active declared FlightIntegrator owner differs.");
                Require(!Harmony.HasAnyPatches("continuum.fixed-callback-attribution") && !Harmony.HasAnyPatches("continuum.update-callback-attribution") &&
                    !Harmony.HasAnyPatches("continuum.aero-integration-attribution") && !Harmony.HasAnyPatches("continuum.fixed-callback-attribution.input") &&
                    !Harmony.HasAnyPatches("continuum.mechjeb-drive-attribution"), "Overlapping broad/input/aero observers are installed.");
                StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);
                Require(DigestAssembly(typeof(FlightIntegrator).Assembly) == StockSha, "Bulk owner assembly content differs.");
                methods[0] = Pin(typeof(FlightIntegrator), "UpdateMassStats", 100677455, typeof(void), Type.EmptyTypes, StockMvid);
                methods[1] = Pin(typeof(FlightIntegrator), "DragCubeSetupAndPartAeroStats", 100677504, typeof(void), new[] { typeof(Vessel) }, StockMvid);
                methods[2] = Pin(typeof(FlightIntegrator), "UpdateThermodynamics", 100677477, typeof(void), Type.EmptyTypes, StockMvid);
                methods[3] = Pin(typeof(Vessel), "FeedInputFeed", 100677715, typeof(void), Type.EmptyTypes, StockMvid);
                BindMechJeb();
                methods[6] = Pin(typeof(FloatingOrigin), "FixedUpdate", 100677519, typeof(void), Type.EmptyTypes, StockMvid);
                methods[7] = Pin(typeof(VesselPrecalculate), "MainPhysics", 100678119, typeof(void), new[] { typeof(bool) }, StockMvid);
                methods[8] = Pin(typeof(VesselPrecalculate), "CalculatePhysicsStats", 100678128, typeof(void), Type.EmptyTypes, StockMvid);
                methods[9] = Pin(typeof(ModuleDeployableSolarPanel), "FixedUpdate", 100673096, typeof(void), Type.EmptyTypes, StockMvid);
                BindBulkOwners();
                Report.targets = methods.Select((method, slot) => new Target { slot = slot, metadataToken = method.MetadataToken,
                    assembly = method.Module.Assembly.GetName().Name, assemblySha256 = DigestAssembly(method.Module.Assembly), assemblyMvid = method.Module.ModuleVersionId.ToString("D"),
                    declaringType = method.DeclaringType.FullName, method = method.Name, ilSha256 = Hash(method.GetMethodBody().GetILAsByteArray()),
                    ownerScope = slot < 3 ? "exact-active-flight-integrator" : slot == 3 ? "exact-active-vessel" : slot < 6 ? "bound-readonly-core-state" :
                        slot == 6 ? "bound-floating-origin-singleton" : slot < 9 ? "bound-current-vessel-precalc" : "bound-current-vessel-solar-panel" }).ToArray();
                Report.targetCount = Slots;
                foreach (MethodInfo method in methods) RequireEmpty(method);
                Report.preinstallPatchGraph = GraphEvidence();
                Report.storageStartedTicks = Stopwatch.GetTimestamp();
                boundaries = new Boundary[MaxWindows]; stack = new StackFrame[MaxDepth]; calls = new int[MaxWindows * Slots];
                inclusive = new long[MaxWindows * Slots]; self = new long[MaxWindows * Slots]; insideCalls = new long[MaxWindows * Slots];
                insideTicks = new long[MaxWindows * Slots]; outsideCalls = new long[MaxWindows * Slots]; outsideTicks = new long[MaxWindows * Slots];
                activeCalls = new long[MaxWindows * Slots]; activeTicks = new long[MaxWindows * Slots]; otherCalls = new long[MaxWindows * Slots]; otherTicks = new long[MaxWindows * Slots];
                offsetSetCalls = new long[MaxWindows * Slots]; offsetSetTicks = new long[MaxWindows * Slots]; offsetClearCalls = new long[MaxWindows * Slots]; offsetClearTicks = new long[MaxWindows * Slots];
                offsetUnknownCalls = new long[MaxWindows * Slots]; offsetUnknownTicks = new long[MaxWindows * Slots];
                Report.storagePayloadBytes = (long)MaxWindows * (4 + 8 + 8 + 8 + 1) + (long)MaxDepth * (4 + 4 + 4 + 8 + 8 + 8 + 4 + 3 * 8) +
                    (long)MaxWindows * Slots * (4 + 16 * 8);
                Report.storageEndedTicks = Stopwatch.GetTimestamp(); active = this;
                for (int slot = 0; slot < Slots; slot++)
                {
                    harmony.Patch(methods[slot], prefix: new HarmonyMethod(Prefixes[slot]), finalizer: new HarmonyMethod(End));
                    Report.patchedMethods++;
                }
                RequireOwned(); Report.installedPatchGraph = GraphEvidence(); installed = true;
                Report.status = "installed"; Report.cleanupStatus = "installed";
            }
            catch (Exception error) { Fail("install", error); Report.status = "unavailable"; Remove(); }
            finally { Report.admissionEndedTicks = Stopwatch.GetTimestamp(); }
        }
        public static FixedStepOwnerAttribution Create(string[] arguments, string runId, Func<bool> capturing)
        {
            string request = Environment.GetEnvironmentVariable("CONTINUUM_FIXED_STEP_OWNER_ATTRIBUTION");
            if (string.IsNullOrEmpty(request)) return null;
            Require(request == "1" && !string.IsNullOrEmpty(runId) && capturing != null && FlightGlobals.ActiveVessel != null &&
                FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed, "Fixed owner attribution requires one bound active loaded flight.");
            return new FixedStepOwnerAttribution(runId, capturing);
        }
        public static bool Requested(string[] arguments)
        { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_FIXED_STEP_OWNER_ATTRIBUTION")); }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        string DigestAssembly(Assembly assembly)
        {
            string digest; if (assemblyHashes.TryGetValue(assembly, out digest)) return digest;
            Require(!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location), "Owner assembly has no file identity.");
            var before = new FileInfo(assembly.Location); long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
            digest = Hash(File.ReadAllBytes(assembly.Location)); before.Refresh();
            Require(before.Length == length && before.LastWriteTimeUtc == modified, "Owner assembly changed during pinning.");
            assemblyHashes.Add(assembly, digest); return digest;
        }
        static MethodInfo Pin(Type type, string name, int token, Type result, Type[] parameters, Guid mvid, string il = null)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
            Require(method != null && method.DeclaringType == type && !method.IsStatic && !method.IsAbstract && !method.ContainsGenericParameters &&
                method.ReturnType == result && method.Module.ModuleVersionId == mvid && method.MetadataToken == token && method.GetMethodBody() != null &&
                (il == null || Hash(method.GetMethodBody().GetILAsByteArray()) == il), "Fixed owner method pin differs: " + type.FullName + "." + name);
            return method;
        }
        void BindMechJeb()
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(value => value.GetName().Name == "MechJeb2");
            Require(assembly != null && assembly.ManifestModule.ModuleVersionId == MechMvid && DigestAssembly(assembly) == MechSha, "Pinned MechJeb owner differs.");
            Report.mechJebSha256 = MechSha; Report.mechJebMvid = MechMvid.ToString("D");
            coreType = assembly.GetType("MuMech.MechJebCore", true); Type stateType = assembly.GetType("MuMech.VesselState", true);
            stateField = AccessTools.DeclaredField(coreType, "VesselState");
            Require(stateField != null && stateField.IsInitOnly && stateField.FieldType == stateType && !stateField.IsStatic, "Readonly core state ownership differs.");
            methods[4] = Pin(stateType, "Update", 100665296, typeof(bool), new[] { typeof(Vessel) }, MechMvid,
                "2f3a6304100e735164c51fc9ea5f64c541e5f301f173bdf4552d5d9136cfa6c9");
            methods[5] = Pin(stateType, "AnalyzeParts", 100665304, typeof(void), new[] { typeof(Vessel),
                stateType.GetNestedType("EngineInfo", BindingFlags.Public | BindingFlags.NonPublic), stateType.GetNestedType("IntakeInfo", BindingFlags.Public | BindingFlags.NonPublic) }, MechMvid,
                "4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b");
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                if (module.GetType() != coreType) continue;
                object state = stateField.GetValue(module);
                Require(stateBindings.Count < MaxStates && state != null && state.GetType() == stateType && !states.ContainsKey(state), "Bound MechJeb state capacity/identity differs.");
                var row = new StateOwner { slot = stateBindings.Count, coreInstanceId = module.GetInstanceID(), partFlightId = part.flightID,
                    partName = part.name, coreType = coreType.FullName, stateType = stateType.FullName };
                var binding = new Binding { core = module, state = state, report = row }; states.Add(state, binding); stateBindings.Add(binding);
            }
            Require(stateBindings.Count > 0, "No actual active-vessel MechJeb state owner.");
            Report.stateOwners = stateBindings.Select(binding => binding.report).ToArray();
        }
        void BindBulkOwners()
        {
            origin = FloatingOrigin.fetch;
            Require(origin != null && origin.GetType() == typeof(FloatingOrigin), "Declared FloatingOrigin singleton differs.");
            FieldInfo flag = AccessTools.DeclaredField(typeof(FloatingOrigin), "SetOffsetThisFrame");
            Require(flag != null && flag.DeclaringType == typeof(FloatingOrigin) && flag.FieldType == typeof(bool) && !flag.IsStatic &&
                flag.Module.ModuleVersionId == StockMvid, "Original origin offset flag differs.");
            offsetFlag = AccessTools.FieldRefAccess<FloatingOrigin, bool>(flag.Name); Report.offsetFlagMetadataToken = flag.MetadataToken;
            Report.singletonOwners = new[] { new SingletonOwner { instanceId = origin.GetInstanceID(), declaringType = typeof(FloatingOrigin).FullName } };
            Require(FlightGlobals.Vessels != null && FlightGlobals.Vessels.Count > 0 && FlightGlobals.Vessels.Count <= MaxVessels, "Current vessel census bound differs.");
            foreach (Vessel current in FlightGlobals.Vessels)
            {
                Require(current != null && current.precalc != null && current.precalc.GetType() == typeof(VesselPrecalculate) &&
                    ReferenceEquals(current.precalc.Vessel, current) && !precalcs.ContainsKey(current.precalc), "Declared vessel/precalculation owner differs.");
                var row = new VesselOwner { slot = vesselBindings.Count, instanceId = current.GetInstanceID(), precalcInstanceId = current.precalc.GetInstanceID(),
                    vesselId = current.id.ToString("D"), vesselName = current.vesselName, precalcType = typeof(VesselPrecalculate).FullName,
                    activeVessel = ReferenceEquals(current, vessel), loadedAtAdmission = current.loaded, packedAtAdmission = current.packed };
                var binding = new VesselBinding { vessel = current, precalc = current.precalc, id = current.id, report = row };
                precalcs.Add(binding.precalc, binding); vesselBindings.Add(binding);
                foreach (Part part in current.parts) foreach (PartModule module in part.Modules)
                {
                    var panel = module as ModuleDeployableSolarPanel; if (panel == null) continue;
                    Require(panel.GetType() == typeof(ModuleDeployableSolarPanel) && ReferenceEquals(panel.vessel, current) && ReferenceEquals(panel.part, part) &&
                        moduleBindings.Count < MaxModules && !modules.ContainsKey(panel), "Declared solar-panel instance/domain differs.");
                    var moduleRow = new ModuleOwner { slot = moduleBindings.Count, instanceId = panel.GetInstanceID(), vesselSlot = row.slot,
                        partFlightId = part.flightID, partName = part.name, moduleType = typeof(ModuleDeployableSolarPanel).FullName, activeVessel = row.activeVessel };
                    var moduleBinding = new ModuleBinding { module = panel, vessel = binding, part = part, report = moduleRow };
                    modules.Add(panel, moduleBinding); moduleBindings.Add(moduleBinding);
                }
            }
            Require(vesselBindings.Count(binding => binding.report.activeVessel) == 1, "Active vessel absent/duplicated in the actual census.");
            Report.vesselOwners = vesselBindings.Select(binding => binding.report).ToArray();
            Report.moduleOwners = moduleBindings.Select(binding => binding.report).ToArray();
        }
        static void RequireEmpty(MethodBase method)
        {
            Patches graph = Harmony.GetPatchInfo(method);
            Require(graph == null || graph.Prefixes.Count + graph.Finalizers.Count + graph.Transpilers.Count + graph.Postfixes.Count == 0,
                "Unexpected fixed owner patch: " + method.DeclaringType.FullName + "." + method.Name);
        }
        void RequireOwned()
        {
            for (int slot = 0; slot < Slots; slot++)
            {
                Patches graph = Harmony.GetPatchInfo(methods[slot]); MethodInfo prefix = Prefixes[slot];
                Require(graph != null && graph.Prefixes.Count == 1 && graph.Finalizers.Count == 1 && graph.Transpilers.Count == 0 && graph.Postfixes.Count == 0 &&
                    ExactPatch(graph.Prefixes[0], prefix) && ExactPatch(graph.Finalizers[0], End), "Fixed owner installed graph differs: " + methods[slot].Name);
            }
        }
        void RequireStateOwners()
        {
            int count = 0;
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                if (module.GetType() != coreType) continue;
                Binding binding; object state = stateField.GetValue(module);
                Require(state != null && states.TryGetValue(state, out binding) && ReferenceEquals(binding.core, module), "Actual MechJeb owner census changed.");
                count++;
            }
            Require(count == stateBindings.Count, "Actual MechJeb owner count changed.");
            foreach (Binding binding in stateBindings) Require(binding.core != null && binding.core.vessel == vessel && binding.core.GetType() == coreType &&
                binding.core.GetInstanceID() == binding.report.coreInstanceId && binding.core.part != null && binding.core.part.flightID == binding.report.partFlightId &&
                ReferenceEquals(stateField.GetValue(binding.core), binding.state), "Readonly state owner changed during capture.");
        }
        void RequireBulkOwners()
        {
            Require(origin != null && ReferenceEquals(FloatingOrigin.fetch, origin) && origin.GetInstanceID() == Report.singletonOwners[0].instanceId,
                "FloatingOrigin singleton changed during capture.");
            Require(FlightGlobals.Vessels != null && FlightGlobals.Vessels.Count == vesselBindings.Count, "Actual vessel census changed.");
            int moduleCount = 0; var seenPrecalcs = new HashSet<object>(new References()); var seenModules = new HashSet<object>(new References());
            foreach (Vessel current in FlightGlobals.Vessels)
            {
                VesselBinding binding = null;
                Require(current != null && current.precalc != null && precalcs.TryGetValue(current.precalc, out binding) && seenPrecalcs.Add(current.precalc), "Actual precalculation census changed.");
                Require(ReferenceEquals(binding.vessel, current) && ReferenceEquals(binding.precalc.Vessel, current) && current.id == binding.id &&
                    current.GetInstanceID() == binding.report.instanceId && binding.precalc.GetInstanceID() == binding.report.precalcInstanceId &&
                    current.loaded == binding.report.loadedAtAdmission && current.packed == binding.report.packedAtAdmission, "Bound vessel/precalculation identity/domain changed.");
                foreach (Part part in current.parts) foreach (PartModule module in part.Modules)
                {
                    var panel = module as ModuleDeployableSolarPanel; if (panel == null) continue;
                    ModuleBinding moduleBinding = null;
                    Require(panel.GetType() == typeof(ModuleDeployableSolarPanel) && modules.TryGetValue(panel, out moduleBinding) && seenModules.Add(panel), "Actual solar-panel census changed.");
                    Require(ReferenceEquals(moduleBinding.vessel, binding) && ReferenceEquals(moduleBinding.part, part) && ReferenceEquals(panel.part, part) &&
                        ReferenceEquals(panel.vessel, current) && panel.GetInstanceID() == moduleBinding.report.instanceId && part.flightID == moduleBinding.report.partFlightId,
                        "Bound solar-panel identity/domain changed.");
                    moduleCount++;
                }
            }
            Require(moduleCount == moduleBindings.Count, "Actual solar-panel count changed.");
        }
        static bool ExactPatch(Patch patch, MethodInfo method)
        { return patch.owner == Owner && patch.priority == Priority.Normal && patch.before.Length == 0 && patch.after.Length == 0 && FlightIntegratorUpdateAttribution.SameMethod(patch.PatchMethod, method); }
        string[] GraphEvidence()
        { return methods.Where(method => method != null).SelectMany(method => FlightIntegratorUpdateAttribution.DescribeGraph(method, Harmony.GetPatchInfo(method))).ToArray(); }
        public void Before(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Phase || !installed || faulted || !capturing()) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                Require(Thread.CurrentThread.ManagedThreadId == thread && activeWindow == -1 && depth == 0 && feedDepth == 0 &&
                    !double.IsNaN(fixedTimeSeconds) && !double.IsInfinity(fixedTimeSeconds) && FlightGlobals.ActiveVessel == vessel && vessel.loaded && !vessel.packed &&
                    ReferenceEquals(FlightIntegrator.ActiveVesselFI, integrator), "Fixed phase context/active owner changed.");
                Require(origin != null && ReferenceEquals(FloatingOrigin.fetch, origin), "Bound origin singleton moved/died.");
                foreach (Binding binding in stateBindings) Require(binding.core != null && binding.core.vessel == vessel, "Bound analysis core moved/died.");
                if (windowCount == MaxWindows) { Report.droppedWindows++; throw new InvalidOperationException("Fixed phase window bound exceeded."); }
                activeWindow = windowCount++; Report.startedWindows++;
                boundaries[activeWindow] = new Boundary { Frame = frame, Time = fixedTimeSeconds, Start = Stopwatch.GetTimestamp() };
            }
            catch (Exception error) { Report.contextErrors++; Fail("Before", error); }
            finally { Report.beforeTicks += Stopwatch.GetTimestamp() - start; }
        }
        public void After(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Phase || activeWindow < 0) return;
            long end = Stopwatch.GetTimestamp();
            try
            {
                Boundary boundary = boundaries[activeWindow];
                Require(Thread.CurrentThread.ManagedThreadId == thread && boundary.Frame == frame && boundary.Time == fixedTimeSeconds && depth == 0 && feedDepth == 0 && end >= boundary.Start,
                    "Fixed phase stack/frame/time drift.");
                boundary.End = end; boundary.CaptureAtEnd = capturing(); boundaries[activeWindow] = boundary; Report.completedWindows++;
            }
            catch (Exception error) { Report.contextErrors++; Fail("After", error); }
            finally { activeWindow = -1; depth = feedDepth = 0; Report.afterTicks += Stopwatch.GetTimestamp() - end; }
        }
        public void Fault(string scope, Exception error)
        { if (scope == Phase) { Report.contextErrors++; Fail("PlayerLoop", error); } }
        bool Sampling { get { return installed && !faulted && activeWindow >= 0; } }
        void Enter(int slot, int state, ref CallState sample, bool activeVessel = true)
        {
            if (!Sampling) return;
            try
            {
                Require(Thread.CurrentThread.ManagedThreadId == thread && depth < MaxDepth, "Fixed owner thread/stack bound differs.");
                long start = Stopwatch.GetTimestamp(); long id = checked(++serial);
                stack[depth] = new StackFrame { Id = id, Window = activeWindow, Slot = slot, State = state, Start = start, InsideFeed = feedDepth > 0, ActiveVessel = activeVessel };
                sample.owner = this; sample.id = id; sample.depth = depth; sample.window = activeWindow; sample.entered = true; depth++;
                if (slot == 3) feedDepth++;
            }
            catch (Exception error) { Report.contextErrors++; Fail("prefix", error); }
        }
        static void MassPrefix(FlightIntegrator __instance, ref CallState __state) { EnterFi(__instance, 0, ref __state); }
        static void DragSetupPrefix(FlightIntegrator __instance, ref CallState __state) { EnterFi(__instance, 1, ref __state); }
        static void ThermoPrefix(FlightIntegrator __instance, ref CallState __state) { EnterFi(__instance, 2, ref __state); }
        static void EnterFi(FlightIntegrator instance, int slot, ref CallState sample)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            if (!ReferenceEquals(instance, owner.integrator)) { owner.Report.ignoredForeignInstances++; return; }
            owner.Enter(slot, -1, ref sample);
        }
        static void FeedPrefix(Vessel __instance, ref CallState __state)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            if (!ReferenceEquals(__instance, owner.vessel)) { owner.Report.ignoredForeignInstances++; return; }
            owner.Enter(3, -1, ref __state);
        }
        static void UpdateAnalysisPrefix(object __instance, Vessel __0, ref CallState __state) { EnterAnalysis(__instance, __0, 4, ref __state); }
        static void PartsAnalysisPrefix(object __instance, Vessel __0, ref CallState __state) { EnterAnalysis(__instance, __0, 5, ref __state); }
        static void EnterAnalysis(object instance, Vessel requestedVessel, int slot, ref CallState sample)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            if (!ReferenceEquals(requestedVessel, owner.vessel)) { owner.Report.ignoredForeignInstances++; return; }
            try
            {
                Binding binding;
                Require(owner.states.TryGetValue(instance, out binding) && binding.core != null && binding.core.vessel == owner.vessel, "Unbound active-vessel analysis state.");
                owner.Enter(slot, binding.report.slot, ref sample);
            }
            catch (Exception error) { owner.Report.ownershipErrors++; owner.Fail("analysis prefix", error); }
        }
        static void OriginPrefix(FloatingOrigin __instance, ref CallState __state)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            try
            {
                Require(ReferenceEquals(__instance, owner.origin) && ReferenceEquals(FloatingOrigin.fetch, owner.origin), "Unbound origin singleton callback.");
                owner.Enter(6, 0, ref __state, false);
                if (__state.entered)
                {
                    owner.stack[__state.depth].OffsetBefore = owner.origin.offset;
                    owner.stack[__state.depth].OriginReady = FlightGlobals.ready;
                    owner.stack[__state.depth].OriginFlagBefore = owner.offsetFlag(owner.origin);
                }
            }
            catch (Exception error) { owner.Report.ownershipErrors++; owner.Fail("origin prefix", error); }
        }
        static void MainPhysicsPrefix(VesselPrecalculate __instance, ref CallState __state) { EnterPrecalc(__instance, 7, ref __state); }
        static void PhysicsStatsPrefix(VesselPrecalculate __instance, ref CallState __state) { EnterPrecalc(__instance, 8, ref __state); }
        static bool StableVessel(VesselBinding binding)
        {
            return binding.vessel != null && binding.precalc != null && ReferenceEquals(binding.vessel.precalc, binding.precalc) &&
                ReferenceEquals(binding.precalc.Vessel, binding.vessel) && binding.vessel.id == binding.id &&
                binding.vessel.loaded == binding.report.loadedAtAdmission && binding.vessel.packed == binding.report.packedAtAdmission;
        }
        static void EnterPrecalc(VesselPrecalculate instance, int slot, ref CallState sample)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            try
            {
                VesselBinding binding;
                Require(owner.precalcs.TryGetValue(instance, out binding) && StableVessel(binding), "Unbound or changed vessel/precalculation callback.");
                owner.Enter(slot, binding.report.slot, ref sample, binding.report.activeVessel);
            }
            catch (Exception error) { owner.Report.ownershipErrors++; owner.Fail("precalculation prefix", error); }
        }
        static void SolarPrefix(ModuleDeployableSolarPanel __instance, ref CallState __state)
        {
            FixedStepOwnerAttribution owner = active; if (owner == null || !owner.Sampling) return;
            try
            {
                ModuleBinding binding;
                Require(owner.modules.TryGetValue(__instance, out binding) && StableVessel(binding.vessel) && binding.part != null &&
                    ReferenceEquals(__instance.part, binding.part) && ReferenceEquals(__instance.vessel, binding.vessel.vessel) && binding.part.flightID == binding.report.partFlightId,
                    "Unbound or changed solar-panel callback.");
                owner.Enter(9, binding.report.slot, ref __state, binding.report.activeVessel);
            }
            catch (Exception error) { owner.Report.ownershipErrors++; owner.Fail("solar prefix", error); }
        }
        static Exception Finalizer(Exception __exception, CallState __state)
        {
            if (!__state.entered) return __exception;
            FixedStepOwnerAttribution owner = __state.owner; long end = Stopwatch.GetTimestamp();
            try
            {
                Require(owner != null && Thread.CurrentThread.ManagedThreadId == owner.thread && owner.activeWindow == __state.window && owner.depth == __state.depth + 1,
                    "Fixed owner finalizer context differs.");
                StackFrame frame = owner.stack[__state.depth];
                Require(frame.Id == __state.id && frame.Window == __state.window && end >= frame.Start, "Fixed owner finalizer identity/clock differs.");
                long elapsed = end - frame.Start; Require(frame.Child >= 0 && frame.Child <= elapsed, "Fixed owner child wall exceeds parent.");
                int cell = frame.Window * Slots + frame.Slot;
                owner.calls[cell] = checked(owner.calls[cell] + 1); owner.inclusive[cell] = checked(owner.inclusive[cell] + elapsed);
                owner.self[cell] = checked(owner.self[cell] + elapsed - frame.Child); owner.Report.capturedCalls = checked(owner.Report.capturedCalls + 1);
                if (frame.Slot == 4 || frame.Slot == 5)
                {
                    if (frame.InsideFeed) { owner.insideCalls[cell]++; owner.insideTicks[cell] += elapsed; }
                    else { owner.outsideCalls[cell]++; owner.outsideTicks[cell] += elapsed; }
                    StateOwner state = owner.Report.stateOwners[frame.State];
                    if (frame.Slot == 4) { state.updateCalls++; if (frame.InsideFeed) state.insideFeedUpdateCalls++; }
                    else { state.analyzeCalls++; if (frame.InsideFeed) state.insideFeedAnalyzeCalls++; }
                }
                if (frame.Slot != 6)
                {
                    if (frame.ActiveVessel) { owner.activeCalls[cell]++; owner.activeTicks[cell] += elapsed; }
                    else { owner.otherCalls[cell]++; owner.otherTicks[cell] += elapsed; }
                }
                if (frame.Slot == 6)
                {
                    SingletonOwner singleton = owner.Report.singletonOwners[frame.State]; singleton.calls++; singleton.inclusiveTicks += elapsed; singleton.selfTicks += elapsed - frame.Child;
                    if (frame.OriginFlagBefore) singleton.offsetFlagAtEntrySetCalls++;
                    Vector3d after = owner.origin.offset;
                    if (SameBits(frame.OffsetBefore.x, after.x) && SameBits(frame.OffsetBefore.y, after.y) && SameBits(frame.OffsetBefore.z, after.z)) singleton.offsetFieldsUnchangedCalls++;
                    else singleton.offsetFieldsChangedCalls++;
                    if (!frame.OriginReady || __exception != null)
                    {
                        singleton.offsetUnclassifiedCalls++; singleton.offsetUnclassifiedTicks += elapsed;
                        owner.offsetUnknownCalls[cell]++; owner.offsetUnknownTicks[cell] += elapsed;
                    }
                    else if (owner.offsetFlag(owner.origin))
                    {
                        singleton.offsetFlagSetCalls++; singleton.offsetFlagSetTicks += elapsed;
                        owner.offsetSetCalls[cell]++; owner.offsetSetTicks[cell] += elapsed;
                    }
                    else
                    {
                        singleton.offsetFlagClearCalls++; singleton.offsetFlagClearTicks += elapsed;
                        owner.offsetClearCalls[cell]++; owner.offsetClearTicks[cell] += elapsed;
                    }
                }
                else if (frame.Slot == 7 || frame.Slot == 8)
                {
                    VesselOwner current = owner.Report.vesselOwners[frame.State];
                    if (frame.Slot == 7) { current.mainPhysicsCalls++; current.mainPhysicsInclusiveTicks += elapsed; current.mainPhysicsSelfTicks += elapsed - frame.Child; }
                    else { current.physicsStatsCalls++; current.physicsStatsInclusiveTicks += elapsed; current.physicsStatsSelfTicks += elapsed - frame.Child; }
                }
                else if (frame.Slot == 9)
                {
                    ModuleOwner module = owner.Report.moduleOwners[frame.State]; module.calls++; module.inclusiveTicks += elapsed; module.selfTicks += elapsed - frame.Child;
                }
                if (__exception != null) owner.Report.originalExceptions++;
                if (frame.Slot == 3) { Require(owner.feedDepth > 0, "Fixed input depth underflow."); owner.feedDepth--; }
                owner.depth--; if (owner.depth > 0) owner.stack[owner.depth - 1].Child = checked(owner.stack[owner.depth - 1].Child + elapsed);
            }
            catch (Exception error) { if (owner != null) { owner.Report.contextErrors++; owner.Fail("finalizer", error); } }
            return __exception;
        }
        static bool SameBits(double a, double b) { return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b); }
        void Fail(string context, Exception error)
        { Report.errors++; faulted = true; if (Report.detail == null) Report.detail = context + ": " + (error == null ? "unknown observer fault" : error.ToString()); }
        void Remove()
        {
            installed = false;
            try
            {
                if (ReferenceEquals(active, this)) harmony.UnpatchAll(Owner);
                foreach (MethodInfo method in methods.Where(method => method != null))
                {
                    Patches graph = Harmony.GetPatchInfo(method); Require(graph == null || !graph.Owners.Contains(Owner), "Owned fixed observer patches remain.");
                }
                Report.cleanupStatus = "removed-owned-patches";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Fail("unpatch", error); }
            finally { if (ReferenceEquals(active, this)) active = null; }
        }
        public void Dispose()
        {
            if (disposed) return; Report.cleanupStartedTicks = Stopwatch.GetTimestamp();
            try
            {
                Require(activeWindow == -1 && depth == 0 && feedDepth == 0, "Fixed observer released during a phase/call.");
                if (installed)
                {
                    RequireOwned(); Report.precleanupPatchGraph = GraphEvidence();
                    Require(Report.installedPatchGraph.SequenceEqual(Report.precleanupPatchGraph), "Fixed graph changed during capture.");
                    foreach (Target row in Report.targets)
                    {
                        MethodInfo method = methods[row.slot];
                        Require(Hash(File.ReadAllBytes(method.Module.Assembly.Location)) == row.assemblySha256 && Hash(method.GetMethodBody().GetILAsByteArray()) == row.ilSha256,
                            "Fixed owner assembly/body changed.");
                    }
                    RequireStateOwners();
                    RequireBulkOwners();
                }
            }
            catch (Exception error) { Report.ownershipErrors++; Fail("cleanup admission", error); }
            Remove();
            try
            {
                Report.restoredPatchGraph = GraphEvidence();
                if (Report.preinstallPatchGraph.Length > 0) Require(Report.preinstallPatchGraph.SequenceEqual(Report.restoredPatchGraph), "Fixed original graphs were not restored.");
            }
            catch (Exception error) { Report.ownershipErrors++; Report.cleanupStatus = "cleanup-error"; Fail("restoration", error); }
            Report.cleanupEndedTicks = Stopwatch.GetTimestamp(); Report.exportStartedTicks = Stopwatch.GetTimestamp();
            var exported = new Window[windowCount]; var reached = new bool[Slots];
            for (int window = 0; window < windowCount; window++)
            {
                Boundary boundary = boundaries[window]; var entries = new Entry[Slots];
                for (int slot = 0; slot < Slots; slot++)
                {
                    int cell = window * Slots + slot; if (calls[cell] > 0) reached[slot] = true;
                    entries[slot] = new Entry { slot = slot, calls = calls[cell], inclusiveTicks = inclusive[cell], selfTicks = self[cell],
                        insideFeedCalls = insideCalls[cell], insideFeedTicks = insideTicks[cell], outsideFeedCalls = outsideCalls[cell], outsideFeedTicks = outsideTicks[cell],
                        activeVesselCalls = activeCalls[cell], activeVesselTicks = activeTicks[cell], otherVesselCalls = otherCalls[cell], otherVesselTicks = otherTicks[cell],
                        offsetFlagSetCalls = offsetSetCalls[cell], offsetFlagSetTicks = offsetSetTicks[cell], offsetFlagClearCalls = offsetClearCalls[cell], offsetFlagClearTicks = offsetClearTicks[cell],
                        offsetUnclassifiedCalls = offsetUnknownCalls[cell], offsetUnclassifiedTicks = offsetUnknownTicks[cell] };
                }
                exported[window] = new Window { sequence = window, frame = boundary.Frame, fixedTimeSeconds = boundary.Time, startedTicks = boundary.Start, endedTicks = boundary.End,
                    elapsedTicks = boundary.End - boundary.Start, captureActiveAtEnd = boundary.CaptureAtEnd, entries = entries };
            }
            Report.windows = exported; Report.observedTargets = reached.Count(value => value);
            Report.unreachedSlots = Enumerable.Range(0, Slots).Where(slot => !reached[slot]).ToArray();
            Report.coverageStatus = Report.observedTargets == Slots ? "all-targets-reached" : Report.observedTargets > 0 ? "partial-target-execution" : "no-target-execution";
            Report.exportEndedTicks = Stopwatch.GetTimestamp(); disposed = true;
            string reason; Report.status = IsValidFinal(out reason) ? "observed" : "invalid"; if (Report.detail == null && reason != null) Report.detail = reason;
        }
        public bool IsValidFinal(out string reason)
        {
            reason = null;
            if (!disposed || Report.cleanupStatus != "removed-owned-patches" || Report.errors != 0 || Report.ownershipErrors != 0 || Report.contextErrors != 0 || Report.originalExceptions != 0 ||
                Report.droppedWindows != 0 || Report.targetCount != Slots || Report.patchedMethods != Slots || Report.startedWindows != Report.completedWindows || Report.completedWindows == 0 ||
                Report.observedTargets <= 0 || Report.observedTargets > Slots || Report.windows.Length != Report.completedWindows || Report.preinstallPatchGraph.Length == 0 || Report.installedPatchGraph.Length == 0 ||
                !Report.installedPatchGraph.SequenceEqual(Report.precleanupPatchGraph) || !Report.preinstallPatchGraph.SequenceEqual(Report.restoredPatchGraph) ||
                Report.singletonOwners.Length != 1 || Report.vesselOwners.Length == 0 || Report.vesselOwners.Length > MaxVessels || Report.moduleOwners.Length > MaxModules ||
                Report.stateOwners.Length == 0 || Report.stateOwners.Length > MaxStates || Report.vesselOwners.Count(owner => owner.activeVessel) != 1)
            { reason = "Fixed owner installation, execution coverage or exact cleanup incomplete."; return false; }
            long callTotal = 0; var reached = new bool[Slots]; var totals = new long[Slots, 5]; var ownerTotals = new long[Slots, 5];
            var feedTotals = new long[2]; var offsetTotals = new long[6];
            for (int window = 0; window < Report.windows.Length; window++)
            {
                Window row = Report.windows[window]; long selfSum = 0;
                if (row.sequence != window || row.startedTicks <= 0 || row.endedTicks < row.startedTicks || row.elapsedTicks != row.endedTicks - row.startedTicks || !row.captureActiveAtEnd ||
                    double.IsNaN(row.fixedTimeSeconds) || double.IsInfinity(row.fixedTimeSeconds) || row.entries == null || row.entries.Length != Slots ||
                    window > 0 && Report.windows[window - 1].endedTicks > row.startedTicks) { reason = "Fixed phase clock/enclosure failed."; return false; }
                for (int slot = 0; slot < Slots; slot++)
                {
                    Entry entry = row.entries[slot];
                    if (entry.slot != slot || entry.calls < 0 || entry.inclusiveTicks < 0 || entry.selfTicks < 0 || entry.selfTicks > entry.inclusiveTicks ||
                        entry.inclusiveTicks > row.elapsedTicks * entry.calls || entry.calls == 0 && (entry.inclusiveTicks != 0 || entry.selfTicks != 0) ||
                        (slot == 4 || slot == 5) && (entry.insideFeedCalls + entry.outsideFeedCalls != entry.calls || entry.insideFeedTicks + entry.outsideFeedTicks != entry.inclusiveTicks) ||
                        (slot != 4 && slot != 5) && entry.insideFeedCalls + entry.outsideFeedCalls + entry.insideFeedTicks + entry.outsideFeedTicks != 0 ||
                        entry.activeVesselCalls < 0 || entry.activeVesselTicks < 0 || entry.otherVesselCalls < 0 || entry.otherVesselTicks < 0 ||
                        slot != 6 && (entry.activeVesselCalls + entry.otherVesselCalls != entry.calls || entry.activeVesselTicks + entry.otherVesselTicks != entry.inclusiveTicks) ||
                        slot == 6 && entry.activeVesselCalls + entry.activeVesselTicks + entry.otherVesselCalls + entry.otherVesselTicks != 0 ||
                        slot < 6 && (entry.otherVesselCalls != 0 || entry.otherVesselTicks != 0) ||
                        entry.offsetFlagSetCalls < 0 || entry.offsetFlagSetTicks < 0 || entry.offsetFlagClearCalls < 0 || entry.offsetFlagClearTicks < 0 ||
                        entry.offsetUnclassifiedCalls < 0 || entry.offsetUnclassifiedTicks < 0 ||
                        slot == 6 && (entry.offsetFlagSetCalls + entry.offsetFlagClearCalls + entry.offsetUnclassifiedCalls != entry.calls ||
                            entry.offsetFlagSetTicks + entry.offsetFlagClearTicks + entry.offsetUnclassifiedTicks != entry.inclusiveTicks) ||
                        slot != 6 && entry.offsetFlagSetCalls + entry.offsetFlagSetTicks + entry.offsetFlagClearCalls + entry.offsetFlagClearTicks + entry.offsetUnclassifiedCalls + entry.offsetUnclassifiedTicks != 0)
                    { reason = "Fixed row counters/body clocks failed."; return false; }
                    if (entry.calls > 0) reached[slot] = true;
                    callTotal += entry.calls; selfSum += entry.selfTicks;
                    totals[slot, 0] += entry.calls; totals[slot, 1] += entry.inclusiveTicks; totals[slot, 2] += entry.selfTicks;
                    totals[slot, 3] += entry.activeVesselCalls; totals[slot, 4] += entry.activeVesselTicks;
                    if (slot == 4 || slot == 5) feedTotals[slot - 4] += entry.insideFeedCalls;
                    if (slot == 6)
                    {
                        offsetTotals[0] += entry.offsetFlagSetCalls; offsetTotals[1] += entry.offsetFlagSetTicks;
                        offsetTotals[2] += entry.offsetFlagClearCalls; offsetTotals[3] += entry.offsetFlagClearTicks;
                        offsetTotals[4] += entry.offsetUnclassifiedCalls; offsetTotals[5] += entry.offsetUnclassifiedTicks;
                    }
                }
                if (selfSum > row.elapsedTicks) { reason = "Fixed observed self costs exceed phase wall."; return false; }
            }
            if (callTotal != Report.capturedCalls || totals[4, 0] != Report.stateOwners.Sum(state => state.updateCalls) || totals[5, 0] != Report.stateOwners.Sum(state => state.analyzeCalls) ||
                feedTotals[0] != Report.stateOwners.Sum(state => state.insideFeedUpdateCalls) || feedTotals[1] != Report.stateOwners.Sum(state => state.insideFeedAnalyzeCalls))
            { reason = "Fixed state-instance counters differ from windows."; return false; }
            SingletonOwner singleton = Report.singletonOwners[0]; ownerTotals[6, 0] = singleton.calls; ownerTotals[6, 1] = singleton.inclusiveTicks; ownerTotals[6, 2] = singleton.selfTicks;
            if (singleton.offsetFlagSetCalls != offsetTotals[0] || singleton.offsetFlagSetTicks != offsetTotals[1] || singleton.offsetFlagClearCalls != offsetTotals[2] ||
                singleton.offsetFlagClearTicks != offsetTotals[3] || singleton.offsetUnclassifiedCalls != offsetTotals[4] || singleton.offsetUnclassifiedTicks != offsetTotals[5] ||
                singleton.offsetFieldsChangedCalls < 0 || singleton.offsetFieldsUnchangedCalls < 0 || singleton.offsetFieldsChangedCalls + singleton.offsetFieldsUnchangedCalls != singleton.calls ||
                singleton.offsetFlagAtEntrySetCalls < 0 || singleton.offsetFlagAtEntrySetCalls > singleton.calls)
            { reason = "Fixed singleton offset classification differs from windows."; return false; }
            for (int owner = 0; owner < Report.vesselOwners.Length; owner++)
            {
                VesselOwner current = Report.vesselOwners[owner];
                if (current.slot != owner || current.mainPhysicsCalls < 0 || current.mainPhysicsInclusiveTicks < 0 || current.mainPhysicsSelfTicks < 0 ||
                    current.mainPhysicsSelfTicks > current.mainPhysicsInclusiveTicks || current.physicsStatsCalls < 0 || current.physicsStatsInclusiveTicks < 0 ||
                    current.physicsStatsSelfTicks < 0 || current.physicsStatsSelfTicks > current.physicsStatsInclusiveTicks ||
                    current.mainPhysicsCalls == 0 && (current.mainPhysicsInclusiveTicks != 0 || current.mainPhysicsSelfTicks != 0) ||
                    current.physicsStatsCalls == 0 && (current.physicsStatsInclusiveTicks != 0 || current.physicsStatsSelfTicks != 0))
                { reason = "Fixed vessel-owner counters failed."; return false; }
                ownerTotals[7, 0] += current.mainPhysicsCalls; ownerTotals[7, 1] += current.mainPhysicsInclusiveTicks; ownerTotals[7, 2] += current.mainPhysicsSelfTicks;
                ownerTotals[8, 0] += current.physicsStatsCalls; ownerTotals[8, 1] += current.physicsStatsInclusiveTicks; ownerTotals[8, 2] += current.physicsStatsSelfTicks;
                if (current.activeVessel)
                {
                    ownerTotals[7, 3] += current.mainPhysicsCalls; ownerTotals[7, 4] += current.mainPhysicsInclusiveTicks;
                    ownerTotals[8, 3] += current.physicsStatsCalls; ownerTotals[8, 4] += current.physicsStatsInclusiveTicks;
                }
            }
            for (int owner = 0; owner < Report.moduleOwners.Length; owner++)
            {
                ModuleOwner module = Report.moduleOwners[owner];
                if (module.slot != owner || module.vesselSlot < 0 || module.vesselSlot >= Report.vesselOwners.Length ||
                    module.activeVessel != Report.vesselOwners[module.vesselSlot].activeVessel || module.calls < 0 || module.inclusiveTicks < 0 || module.selfTicks < 0 ||
                    module.selfTicks > module.inclusiveTicks || module.calls == 0 && (module.inclusiveTicks != 0 || module.selfTicks != 0))
                { reason = "Fixed module-owner counters failed."; return false; }
                ownerTotals[9, 0] += module.calls; ownerTotals[9, 1] += module.inclusiveTicks; ownerTotals[9, 2] += module.selfTicks;
                if (module.activeVessel) { ownerTotals[9, 3] += module.calls; ownerTotals[9, 4] += module.inclusiveTicks; }
            }
            for (int slot = 6; slot < Slots; slot++) for (int column = 0; column < 5; column++) if (ownerTotals[slot, column] != totals[slot, column])
            { reason = "Fixed bulk-instance counters differ from windows."; return false; }
            if (Report.observedTargets != reached.Count(value => value) || Report.unreachedSlots == null ||
                !Report.unreachedSlots.SequenceEqual(Enumerable.Range(0, Slots).Where(slot => !reached[slot])) ||
                Report.coverageStatus != (Report.observedTargets == Slots ? "all-targets-reached" : "partial-target-execution"))
            { reason = "Fixed target execution coverage differs from windows."; return false; }
            return true;
        }
    }
}
