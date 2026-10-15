using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace KspContinuum
{
    internal sealed class MechJebDriveAttribution
    {
        const string Owner = "continuum.mechjeb-drive-attribution";
        const string AssemblySha = "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929";
        static readonly Guid Mvid = new Guid("30fbe3c4-e77e-4abc-895b-a635d5924a08");
        static MechJebDriveAttribution active;
        readonly Func<bool> inFixed;
        readonly Harmony harmony = new Harmony(Owner);
        readonly CallbackAttributionAccumulator timer = new CallbackAttributionAccumulator(Stopwatch.Frequency);
        readonly Dictionary<object, PartModule> states = new Dictionary<object, PartModule>(new ReferenceComparer());
        readonly HashSet<PartModule> cores = new HashSet<PartModule>();
        readonly HashSet<int> observed = new HashSet<int>();
        readonly Dictionary<Type, int> controllers = new Dictionary<Type, int>();
        readonly List<MethodInfo> patched = new List<MethodInfo>();
        readonly List<string> identities = new List<string>();
        FieldInfo moduleCore;
        MethodInfo drive, update, analyze;
        Vessel vessel;
        PartModule currentCore;
        int[] depths;
        bool installed, finished;
        public MechJebDriveAttributionReport Report { get; } = new MechJebDriveAttributionReport { clockFrequency = Stopwatch.Frequency };
        public struct CallState { public long Started; public int Slot; public bool Outermost, Entered; public PartModule Previous; }
        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object x) { return RuntimeHelpers.GetHashCode(x); }
        }
        public MechJebDriveAttribution(Func<bool> inFixed) { this.inFixed = inFixed; }
        static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static MethodInfo Pinned(Type type, string name, Type returns, int token, string il, params Type[] parameters)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters);
            Require(method != null && !method.IsStatic && !method.IsAbstract && !method.ContainsGenericParameters && method.ReturnType == returns &&
                method.MetadataToken == token && method.Module.ModuleVersionId == Mvid && method.GetMethodBody() != null && Hash(method.GetMethodBody().GetILAsByteArray()) == il,
                "Pinned MechJeb method differs: " + name);
            return method;
        }
        int Register(MethodInfo method, string category, string runtimeType = null)
        {
            var assembly = method.Module.Assembly.GetName();
            return timer.Register(new CallbackAttributionRow { category = category, assembly = assembly.Name, assemblyVersion = assembly.Version.ToString(),
                assemblyMvid = method.Module.ModuleVersionId.ToString("D"), declaringType = method.DeclaringType.FullName, runtimeType = runtimeType,
                method = method.Name, metadataToken = method.MetadataToken, ilSha256 = Hash(method.GetMethodBody().GetILAsByteArray()) });
        }
        void Patch(MethodInfo method, string prefix)
        {
            Require(Harmony.GetPatchInfo(method) == null, "Foreign MechJeb attribution seam modification: " + method.Name);
            identities.Add(method.DeclaringType.FullName + "." + method.Name + ":" + method.MetadataToken + ":" + Hash(method.GetMethodBody().GetILAsByteArray()));
            patched.Add(method);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(MechJebDriveAttribution), prefix) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(MechJebDriveAttribution), nameof(Finalizer)) { priority = Priority.Last });
            Report.patchedMethods++;
        }
        public void Start()
        {
            Report.admissionStartedTicks = Stopwatch.GetTimestamp();
            Require(active == null, "MechJeb split already owned."); active = this;
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "MechJeb2");
            if (assembly == null) { Report.status = "not-present"; Report.cleanupStatus = "not-installed"; Report.admissionEndedTicks = Stopwatch.GetTimestamp(); active = null; return; }
            try
            {
                var path = new FileInfo(Path.GetFullPath(assembly.Location)); long length = path.Length; DateTime modified = path.LastWriteTimeUtc;
                Report.assemblySha256 = Hash(File.ReadAllBytes(path.FullName)); path.Refresh();
                Require(path.Length == length && path.LastWriteTimeUtc == modified && Report.assemblySha256 == AssemblySha && assembly.ManifestModule.ModuleVersionId == Mvid, "Pinned MechJeb assembly differs.");
                Type core = assembly.GetType("MuMech.MechJebCore", true), state = assembly.GetType("MuMech.VesselState", true), module = assembly.GetType("MuMech.ComputerModule", true);
                drive = Pinned(core, "Drive", typeof(void), 100663801, "97a8e59efcfe1cf304a37feb26bd43ed1cb97da4d35a538ee77b80deedf36abb", typeof(FlightCtrlState));
                update = Pinned(state, "Update", typeof(bool), 100665296, "2f3a6304100e735164c51fc9ea5f64c541e5f301f173bdf4552d5d9136cfa6c9", typeof(Vessel));
                analyze = Pinned(state, "AnalyzeParts", typeof(void), 100665304, "4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b", typeof(Vessel), state.GetNestedType("EngineInfo", BindingFlags.Public | BindingFlags.NonPublic), state.GetNestedType("IntakeInfo", BindingFlags.Public | BindingFlags.NonPublic));
                FieldInfo stateField = AccessTools.DeclaredField(core, "VesselState"); moduleCore = AccessTools.DeclaredField(module, "Core");
                Require(stateField != null && stateField.FieldType == state && stateField.IsInitOnly && moduleCore != null && moduleCore.FieldType == core && moduleCore.IsInitOnly, "Core/state ownership fields differ.");
                vessel = FlightGlobals.ActiveVessel; Require(vessel != null && vessel.loaded && !vessel.packed, "Active loaded vessel required.");
                foreach (Part part in vessel.parts) foreach (PartModule member in part.Modules) if (member.GetType() == core)
                { Require(cores.Count < 256, "Core ownership bound exceeded."); object value = stateField.GetValue(member); Require(value != null && !states.ContainsKey(value), "Absent/shared core state."); cores.Add(member); states.Add(value, member); }
                if (cores.Count == 0) { Report.status = "not-present"; Report.detail = "Pinned mod present; active vessel has no MechJeb core."; Report.cleanupStatus = "not-installed"; active = null; return; }
                Report.admittedCoreIds = cores.Select(c => c.GetInstanceID()).OrderBy(id => id).ToArray();
                Register(drive, "core-drive-entry-envelope"); Register(update, "vessel-state-update-inside-drive"); Register(update, "vessel-state-update-outside-drive");
                Register(analyze, "analyze-parts-inside-drive"); Register(analyze, "analyze-parts-outside-drive");
                var methods = new HashSet<MethodInfo>();
                MethodInfo baseDrive = AccessTools.DeclaredMethod(module, "Drive", new[] { typeof(FlightCtrlState) });
                Require(baseDrive != null && baseDrive.IsVirtual && baseDrive.ReturnType == typeof(void), "Controller virtual dispatch differs.");
                foreach (Type type in assembly.GetTypes().Where(t => module.IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters).OrderBy(t => t.FullName, StringComparer.Ordinal))
                {
                    MethodInfo method = MechJebDeclaredDrive.Canonical(type.GetMethod("Drive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(FlightCtrlState) }, null));
                    Require(controllers.Count < 128 && method != null && method.Module.ModuleVersionId == Mvid && method.ReturnType == typeof(void) && !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null && method.GetBaseDefinition() == baseDrive, "Controller method identity/capacity differs.");
                    controllers.Add(type, Register(method, "controller-drive-runtime-type", type.FullName)); methods.Add(method);
                }
                depths = new int[5 + controllers.Count]; Report.controllerTypes = controllers.Count;
                Patch(drive, nameof(DrivePrefix)); Patch(update, nameof(AnalysisPrefix)); Patch(analyze, nameof(AnalysisPrefix));
                foreach (var method in methods.OrderBy(m => m.MetadataToken)) Patch(method, nameof(ControllerPrefix));
                Report.seamIdentities = identities.ToArray(); installed = true; Report.status = "installed"; Report.cleanupStatus = "installed";
            }
            catch (Exception error) { Report.status = "unavailable"; Report.detail = error.GetType().Name + ": " + error.Message; Remove(); }
            finally { Report.admissionEndedTicks = Stopwatch.GetTimestamp(); }
        }
        void Begin(int slot, ref CallState sample)
        {
            Require(depths[slot] < 256, "Timing recursion bound exceeded."); sample.Slot = slot; sample.Outermost = depths[slot]++ == 0; sample.Started = Stopwatch.GetTimestamp();
        }
        bool SameOwner(PartModule core)
        {
            if (vessel != null && FlightGlobals.ActiveVessel == vessel && vessel.loaded && !vessel.packed && core != null && core.vessel == vessel) return true;
            Report.errors++; return false;
        }
        public static void DrivePrefix(object __instance, ref CallState __state)
        {
            var owner = active; if (owner == null || !owner.installed || !owner.inFixed()) return;
            __state.Previous = owner.currentCore; __state.Entered = true; owner.currentCore = __instance as PartModule;
            if (!owner.cores.Contains(owner.currentCore)) { owner.currentCore = null; return; }
            try { if (!owner.SameOwner(owner.currentCore)) return; owner.observed.Add(owner.currentCore.GetInstanceID()); owner.Begin(0, ref __state); } catch { owner.Report.errors++; }
        }
        public static void AnalysisPrefix(object __instance, MethodBase __originalMethod, ref CallState __state)
        {
            var owner = active; if (owner == null || !owner.installed || !owner.inFixed()) return;
            PartModule core; if (!owner.states.TryGetValue(__instance, out core)) return;
            try { if (!owner.SameOwner(core)) return; owner.observed.Add(core.GetInstanceID()); int slot = __originalMethod == owner.update ? 1 : 3; if (!ReferenceEquals(core, owner.currentCore)) slot++; owner.Begin(slot, ref __state); } catch { owner.Report.errors++; }
        }
        public static void ControllerPrefix(object __instance, ref CallState __state)
        {
            var owner = active; if (owner == null || !owner.installed || !owner.inFixed() || owner.currentCore == null) return;
            try { if (!ReferenceEquals(owner.moduleCore.GetValue(__instance), owner.currentCore)) return; int slot;
                if (!owner.controllers.TryGetValue(__instance.GetType(), out slot)) { owner.Report.errors++; return; } owner.Begin(slot, ref __state); }
            catch { owner.Report.errors++; }
        }
        public static Exception Finalizer(Exception __exception, ref CallState __state)
        {
            var owner = active; if (owner == null) return __exception;
            try { if (__state.Started != 0) { long elapsed = Stopwatch.GetTimestamp() - __state.Started; if (owner.depths[__state.Slot] < 1) owner.Report.errors++;
                    else { owner.depths[__state.Slot]--; owner.timer.Record(__state.Slot, elapsed, __state.Outermost); } } }
            catch { owner.Report.errors++; }
            finally { if (__state.Entered) owner.currentCore = __state.Previous; }
            return __exception;
        }
        void Remove()
        {
            installed = false;
            try { harmony.UnpatchAll(Owner); Report.cleanupStatus = patched.Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains(Owner) == true) ? "cleanup-error" : "removed-owned-patches"; }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Report.detail = error.GetType().Name + ": " + error.Message; }
            if (ReferenceEquals(active, this)) active = null;
        }
        public void Finish(long fixedWindowTicks)
        {
            if (finished) return; finished = true;
            if (Report.status == "not-present") return;
            if (currentCore != null || (depths != null && depths.Any(d => d != 0))) Report.errors++;
            Remove(); Report.rows = timer.Finish(fixedWindowTicks, true); Report.observedCoreIds = observed.OrderBy(id => id).ToArray();
            if (Report.status != "unavailable") Report.status = Report.errors == 0 && Report.cleanupStatus == "removed-owned-patches" && Report.rows.Any(r => r.category == "core-drive-entry-envelope" && r.calls > 0) ? "observed" : "invalid";
        }
    }
}
