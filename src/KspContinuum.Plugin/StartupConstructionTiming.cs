using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupConstructionTiming : MonoBehaviour
    {
        const string Owner = "continuum.startup-construction-timing";
        [Serializable] public sealed class Scope
        {
            public string name; public int calls, errors; public long inclusiveTicks, exclusiveTicks, maximumTicks;
        }
        [Serializable] public sealed class Resume
        {
            public int state, calls, nullYields, budgetNullYields, objectYields; public long activeTicks, wakeTicks, maximumWakeTicks;
        }
        [Serializable] public sealed class Child
        {
            public string iteratorType; public Scope synchronous = new Scope();
            public int completed, suspended, errors;
        }
        [Serializable] public sealed class Readiness
        {
            public string systemType; public bool startObserved, readyObserved;
            public int startCalls, polls, notReadyPolls, errors;
            public long firstObservedTicks, lastObservedTicks, startTicks, readyTicks, pollTicks, waitGapTicks;
            public Scope construction = new Scope();
            internal long lastFalseEnd;
        }
        [Serializable] public sealed class Report
        {
            public string schema = "continuum-startup-construction-timing/v1", runId, status, cleanupStatus, cleanupError;
            public string scope = "Intrusive caller-boundary and iterator timing. Initial GameDatabase work may precede addon installation; unobserved bootstrap is explicit. Inclusive scopes overlap. Child scopes measure synchronous factory-product priming and publication, not awaited workers. StartLoad construction is inclusive. Readiness wait gaps and iterator wake gaps include engine scheduling and child work, not CPU attribution. No original operations, callbacks, yields or ordering changed.";
            public long clockFrequency, installedTicks, firstDatabaseTicks, lastDatabaseTicks, databaseOuterActiveTicks;
            public bool databaseInitialStateObserved, databaseCompleted, loaderCompleted, databaseReadyAtMenu, childCallsiteInstalled, hooksRemoved;
            public string bootstrapCoverage, childCoverage;
            public int processId, stackErrors, databaseOuterSteps, loaderSteps;
            public Scope[] scopes; public Resume[] resumes; public Child[] children; public Readiness[] readiness;
        }
        sealed class Frame
        {
            public Frame parent; public Scope scope; public long start, children; public bool database, outer; public int state;
        }
        static StartupConstructionTiming active;
        [ThreadStatic] static Frame current;
        readonly List<Scope> scopes = new List<Scope>();
        readonly Dictionary<int, Resume> resumes = new Dictionary<int, Resume>();
        readonly Dictionary<string, Child> children = new Dictionary<string, Child>();
        readonly Dictionary<LoadingSystem, Readiness> readiness = new Dictionary<LoadingSystem, Readiness>();
        static Func<MonoBehaviour, System.Collections.IEnumerator, object> stockRunChild;
        static object completedToken;
        Harmony harmony; FieldInfo databaseState; MethodInfo databaseMove, loaderMove;
        Report report; bool exported; long lastOuterEnd;

        public void Awake()
        {
            if (!Environment.GetCommandLineArgs().Contains("--continuum-startup-construction-timing") && Environment.GetEnvironmentVariable("CONTINUUM_STARTUP_CONSTRUCTION_TIMING") != "1") return;
            DontDestroyOnLoad(gameObject); active = this;
            report = new Report { runId = Environment.GetCommandLineArgs().FirstOrDefault(x => x.StartsWith("--continuum-research-run=", StringComparison.Ordinal))?.Substring("--continuum-research-run=".Length),
                status = "observing", processId = Process.GetCurrentProcess().Id, clockFrequency = Stopwatch.Frequency, installedTicks = Stopwatch.GetTimestamp() };
        }
        public void Start()
        {
            if (active != this) return;
            try
            {
                report.installedTicks = Stopwatch.GetTimestamp();
                Require(File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json")), "Private laboratory required.");
                Require(!string.IsNullOrEmpty(report.runId), "Run identity required.");
                StartupPinnedGameIdentity.Require(typeof(GameDatabase).Assembly);
                Type database = typeof(GameDatabase).GetNestedType("<LoadObjects>d__90", BindingFlags.NonPublic);
                Type loader = typeof(LoadingScreen).GetNestedType("<LoadSystems>d__11", BindingFlags.NonPublic);
                databaseMove = database == null ? null : AccessTools.DeclaredMethod(database, "MoveNext");
                loaderMove = loader == null ? null : AccessTools.DeclaredMethod(loader, "MoveNext");
                databaseState = database == null ? null : AccessTools.DeclaredField(database, "<>1__state");
                Require(databaseMove?.MetadataToken == 100704836 && loaderMove?.MetadataToken == 100705068 && databaseState?.FieldType == typeof(int), "Pinned iterator shape absent.");
                Owners(databaseMove, "continuum.startup-work-scheduler"); Owners(loaderMove, "continuum.startup-world-timing");
                Require(Environment.GetCommandLineArgs().Contains("--continuum-startup-work-scheduler") && Harmony.GetPatchInfo(databaseMove)?.Owners.Contains("continuum.startup-work-scheduler") == true, "Installed scheduler required for child attribution.");
                MethodInfo runChild = AccessTools.DeclaredMethod(typeof(StartupWorkScheduler), "RunChild");
                Require(runChild != null && Harmony.GetPatchInfo(runChild) == null, "Child dispatch seam already modified or absent.");
                stockRunChild = (Func<MonoBehaviour, System.Collections.IEnumerator, object>)Delegate.CreateDelegate(typeof(Func<MonoBehaviour, System.Collections.IEnumerator, object>), runChild);
                completedToken = AccessTools.DeclaredField(typeof(StartupWorkScheduler), "CompletedToken").GetValue(null);
                scopes.Add(new Scope { name = "GameDatabase.LoadObjects.MoveNext" });
                scopes.Add(new Scope { name = "LoadingScreen.LoadSystems.MoveNext" });
                foreach (string name in new[] { "AssemblyLoader.Initialize", "AssemblyLoader.LoadPluginInfo", "AssemblyLoader.LoadPlugin", "AssemblyLoader.LoadAssemblies", "GameDatabase.SetupMainLoaders", "GameDatabase.translateLoadedNodes", "UrlDir..ctor" }) scopes.Add(new Scope { name = name });
                harmony = new Harmony(Owner);
                harmony.Patch(databaseMove, prefix: new HarmonyMethod(typeof(StartupConstructionTiming), nameof(Begin)), finalizer: new HarmonyMethod(typeof(StartupConstructionTiming), nameof(Finish)),
                    transpiler: new HarmonyMethod(typeof(StartupConstructionTiming), nameof(Calls)));
                harmony.Patch(loaderMove, prefix: new HarmonyMethod(typeof(StartupConstructionTiming), nameof(Begin)), finalizer: new HarmonyMethod(typeof(StartupConstructionTiming), nameof(Finish)),
                    transpiler: new HarmonyMethod(typeof(StartupConstructionTiming), nameof(ReadinessCalls)));
                UnityEngine.Debug.Log("[Continuum startup construction] status=installed initialBootstrapCoverage=unknown-until-state0-observed");
            }
            catch (Exception error) { report.status = "rejected:" + error.Message; Cleanup(); Export(); Application.Quit(2); }
        }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static void Owners(MethodInfo method, string allowed)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            Require(patches == null || patches.Owners.All(owner => owner == allowed), "Foreign iterator modifier: " + method);
        }
        [HarmonyAfter("continuum.startup-work-scheduler")]
        static IEnumerable<CodeInstruction> Calls(IEnumerable<CodeInstruction> source)
        {
            var body = source.ToList(); int[] counts = new int[active.scopes.Count]; int childCalls = 0;
            MethodInfo enter = AccessTools.DeclaredMethod(typeof(StartupConstructionTiming), nameof(EnterCall));
            MethodInfo leave = AccessTools.DeclaredMethod(typeof(StartupConstructionTiming), nameof(LeaveCall));
            for (int i = body.Count - 1; i >= 0; i--)
            {
                MethodBase method = body[i].operand as MethodBase;
                if (method == null || (body[i].opcode != OpCodes.Call && body[i].opcode != OpCodes.Callvirt && body[i].opcode != OpCodes.Newobj)) continue;
                if (method.DeclaringType == typeof(StartupWorkScheduler) && method.Name == "RunChild")
                { body[i].operand = AccessTools.DeclaredMethod(typeof(StartupConstructionTiming), nameof(ObserveChild)); childCalls++; continue; }
                string identity = method.DeclaringType.FullName + "." + method.Name;
                int id = active.scopes.FindIndex(scope => scope.name == identity); if (id < 2) continue;
                Require(body[i].blocks.Count == 0, "Construction call exception boundary unsupported: " + identity);
                counts[id]++;
                var begin = new CodeInstruction(OpCodes.Ldc_I4, id); begin.labels.AddRange(body[i].labels); body[i].labels.Clear();
                body.Insert(i, begin); body.Insert(i + 1, new CodeInstruction(OpCodes.Call, enter));
                body.Insert(i + 3, new CodeInstruction(OpCodes.Ldc_I4, id)); body.Insert(i + 4, new CodeInstruction(OpCodes.Call, leave));
            }
            for (int id = 2; id < counts.Length; id++) Require(counts[id] == (id == counts.Length - 1 ? 2 : 1), "Construction call count differs: " + active.scopes[id].name);
            Require(childCalls == 3, "Pinned scheduler child dispatch count differs."); active.report.childCallsiteInstalled = true;
            return body;
        }
        static IEnumerable<CodeInstruction> ReadinessCalls(IEnumerable<CodeInstruction> source)
        {
            var body = source.ToList(); int ready = 0, start = 0;
            foreach (CodeInstruction instruction in body)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (method?.DeclaringType != typeof(LoadingSystem) || instruction.opcode != OpCodes.Callvirt) continue;
                if (method.Name == "IsReady" && method.ReturnType == typeof(bool) && method.GetParameters().Length == 0)
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupConstructionTiming), nameof(ObserveReady)); ready++; }
                if (method.Name == "StartLoad" && method.ReturnType == typeof(void) && method.GetParameters().Length == 0)
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupConstructionTiming), nameof(ObserveStart)); start++; }
            }
            Require(ready == 1 && start == 1, "Pinned loading readiness calls differ."); return body;
        }
        static Readiness SystemMetric(LoadingSystem system)
        {
            Readiness metric;
            if (!active.readiness.TryGetValue(system, out metric))
            {
                string name = system.GetType().FullName;
                metric = new Readiness { systemType = name, firstObservedTicks = Stopwatch.GetTimestamp(), construction = new Scope { name = "system-start:" + name } };
                active.readiness.Add(system, metric);
            }
            return metric;
        }
        static void ObserveStart(LoadingSystem system)
        {
            Readiness metric = SystemMetric(system); metric.startObserved = true; metric.startCalls++; metric.startTicks = Stopwatch.GetTimestamp();
            Frame frame = Enter(metric.construction); bool failed = true;
            try { system.StartLoad(); failed = false; }
            finally { if (failed) metric.errors++; End(frame, failed); }
        }
        static bool ObserveReady(LoadingSystem system)
        {
            Readiness metric = SystemMetric(system); long start = Stopwatch.GetTimestamp(); metric.polls++;
            if (metric.lastFalseEnd != 0) metric.waitGapTicks += start - metric.lastFalseEnd;
            try
            {
                bool ready = system.IsReady();
                if (ready) { if (!metric.readyObserved) metric.readyTicks = Stopwatch.GetTimestamp(); metric.readyObserved = true; metric.lastFalseEnd = 0; }
                else { metric.notReadyPolls++; metric.lastFalseEnd = Stopwatch.GetTimestamp(); }
                return ready;
            }
            catch { metric.errors++; throw; }
            finally { long end = Stopwatch.GetTimestamp(); metric.pollTicks += end - start; metric.lastObservedTicks = end; }
        }
        static object ObserveChild(MonoBehaviour owner, System.Collections.IEnumerator child)
        {
            string name = child == null ? "null" : child.GetType().FullName;
            Child metric;
            if (!active.children.TryGetValue(name, out metric))
            {
                if (active.children.Count >= 64) name = "other-bounded-types";
                if (!active.children.TryGetValue(name, out metric))
                { metric = new Child { iteratorType = name, synchronous = new Scope { name = "child-prime:" + name } }; active.children.Add(name, metric); }
            }
            Frame frame = Enter(metric.synchronous); bool failed = true;
            try
            {
                object result = stockRunChild(owner, child); failed = false;
                if (ReferenceEquals(result, completedToken)) metric.completed++; else metric.suspended++;
                return result;
            }
            finally { if (failed) metric.errors++; End(frame, failed); }
        }
        static Frame Enter(Scope scope)
        { var frame = new Frame { parent = current, scope = scope, start = Stopwatch.GetTimestamp() }; current = frame; return frame; }
        static void End(Frame frame, bool failed)
        {
            long elapsed = Stopwatch.GetTimestamp() - frame.start;
            frame.scope.calls++; frame.scope.inclusiveTicks += elapsed; frame.scope.maximumTicks = Math.Max(frame.scope.maximumTicks, elapsed);
            if (failed) frame.scope.errors++;
            if (current != frame || elapsed < frame.children) active.report.stackErrors++;
            else frame.scope.exclusiveTicks += elapsed - frame.children;
            current = frame.parent; if (current != null) current.children += elapsed;
        }
        static void EnterCall(int id) { Enter(active.scopes[id]); }
        static void LeaveCall(int id)
        { Require(current != null && current.scope == active.scopes[id], "Construction scope stack differs."); End(current, false); }
        static void Begin(object __instance, MethodBase __originalMethod, out Frame __state)
        {
            bool database = Equals(__originalMethod, active.databaseMove);
            bool outer = database && !AncestorsDatabase();
            __state = Enter(active.scopes[database ? 0 : 1]); __state.database = database; __state.outer = outer;
            if (!database) { active.report.loaderSteps++; return; }
            __state.state = (int)active.databaseState.GetValue(__instance);
            if (__state.state == 0) active.report.databaseInitialStateObserved = true;
            if (!outer) return;
            active.report.databaseOuterSteps++;
            if (active.report.firstDatabaseTicks == 0) active.report.firstDatabaseTicks = __state.start;
            Resume resume; if (!active.resumes.TryGetValue(__state.state, out resume)) { resume = new Resume { state = __state.state }; active.resumes.Add(__state.state, resume); }
            resume.calls++;
            if (active.lastOuterEnd != 0) { long gap = __state.start - active.lastOuterEnd; resume.wakeTicks += gap; resume.maximumWakeTicks = Math.Max(resume.maximumWakeTicks, gap); }
        }
        static bool AncestorsDatabase() { for (Frame frame = current; frame != null; frame = frame.parent) if (frame.database) return true; return false; }
        static Exception Finish(object __instance, bool __result, Frame __state, Exception __exception)
        {
            if (__state == null) return __exception;
            while (current != __state && current != null) End(current, true);
            if (current == null) { active.report.stackErrors++; return __exception; }
            long end = Stopwatch.GetTimestamp();
            if (__state.outer)
            {
                active.report.databaseOuterActiveTicks += end - __state.start; active.report.lastDatabaseTicks = end; active.lastOuterEnd = end;
                Resume resume = active.resumes[__state.state]; resume.activeTicks += end - __state.start;
                if (__result)
                {
                    if (((System.Collections.IEnumerator)__instance).Current == null)
                    { resume.nullYields++; int nextState = (int)active.databaseState.GetValue(__instance); if (nextState == 2 || nextState == 4 || nextState == 6) resume.budgetNullYields++; }
                    else resume.objectYields++;
                }
            }
            if (!__result && __exception == null) { if (__state.database) active.report.databaseCompleted = true; else active.report.loaderCompleted = true; }
            End(__state, __exception != null); return __exception;
        }
        public static void ObserveForMenu() { if (active != null && !active.exported) { active.report.databaseReadyAtMenu = GameDatabase.Instance != null && GameDatabase.Instance.IsReady(); active.report.status = "menu-observed"; active.Cleanup(); active.Export(); } }
        public void Update() { if (active == this && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) ObserveForMenu(); }
        void Export()
        {
            if (exported) return; exported = true; report.scopes = scopes.ToArray(); report.resumes = resumes.Values.OrderBy(value => value.state).ToArray();
            report.children = children.Values.OrderBy(value => value.iteratorType, StringComparer.Ordinal).ToArray();
            report.readiness = readiness.Values.OrderBy(value => value.firstObservedTicks).ToArray();
            report.bootstrapCoverage = report.databaseInitialStateObserved ? "state0-observed-not-earlier-native-bootstrap" : "state0-unobserved-no-bootstrap-cost-attribution";
            report.childCoverage = !report.childCallsiteInstalled ? "not-installed" : children.Count == 0 ? "installed-no-dispatch-observed" : "actual-child-prime-calls-observed-not-asynchronous-worker-cost";
            string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "startup-construction-timing-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, ReportJson.Encode(report));
            UnityEngine.Debug.Log("[Continuum startup construction] status=" + report.status + " path=" + path + " state0Observed=" + report.databaseInitialStateObserved + " outerSteps=" + report.databaseOuterSteps);
        }
        void Cleanup()
        {
            try
            {
                harmony?.UnpatchAll(Owner); harmony = null;
                report.hooksRemoved = !Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) == true);
                report.cleanupStatus = report.hooksRemoved ? "removed-owned-hooks" : "owned-hooks-remain";
            }
            catch (Exception error) { report.hooksRemoved = false; report.cleanupStatus = "cleanup-failed"; report.cleanupError = error.ToString(); }
        }
        public void OnApplicationQuit() { if (active == this) { if (!exported) { report.status = "interrupted"; Cleanup(); Export(); } else Cleanup(); } }
        public void OnDestroy() { if (active == this) { if (!exported) { report.status = "destroyed"; Cleanup(); Export(); } else Cleanup(); active = null; } }
    }
}
