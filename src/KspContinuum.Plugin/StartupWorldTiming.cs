using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupWorldTiming : MonoBehaviour
    {
        const string Owner = "continuum.startup-world-timing";
        static StartupWorldTiming active;
        [ThreadStatic] static int sphereDepth;
        Harmony harmony;
        FieldInfo stateField;
        long epoch, firstMove, lastMoveEnd;
        string run;
        bool exported;
        readonly Dictionary<MethodBase, Metric> scopes = new Dictionary<MethodBase, Metric>();
        readonly List<Point> points = new List<Point>();
        readonly List<Wake> wakes = new List<Wake>();
        Metric move;
        [Serializable] public sealed class Metric
        {
            public string name, firstInstance, maximumInstance;
            public long calls, ticks, firstTicks, maximumTicks, errors;
            public int maximumQuadsBefore, maximumQuadsAfter;
        }
        [Serializable] public sealed class Point { public string name; public long ticks; public float engineSeconds; }
        [Serializable] public sealed class Wake { public int resumeState; public long ticks; public string meaning; }
        [Serializable] public sealed class Report
        {
            public string schema = "continuum-startup-world-timing/v1", status, runId, observedUtc;
            public string clock = "Stopwatch-monotonic", interpretation = "Scopes are inclusive synchronous elapsed times and overlap; scene callbacks are boundaries, not native exclusive costs. Wake gaps include scheduling, child work and waits; state 5 awaits the whole launchsite coroutine, not just its stock two-second wait. PQS child scopes admit only calls nested in StartSphere; first-call time includes actual work and possible JIT, not independently attributed JIT. Zero calls do not prove zero cost: hook/inlining coverage must be assessed.";
            public long frequency, setupSystemElapsedTicks;
            public bool setupSystemCompleted;
            public Metric[] scopes; public Point[] points; public Wake[] wakeGaps;
        }
        public struct ScopeState { public long started; public Metric metric; public PQS sphere; public string instanceName; public int quadsBefore; public bool sphereEntry; }
        public void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!args.Contains("--continuum-startup-world-timing")) return;
            DontDestroyOnLoad(gameObject); epoch = Stopwatch.GetTimestamp();
            try
            {
                if (active != null || !File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json"))) throw new InvalidOperationException("One private laboratory observer required.");
                string[] identities = args.Where(x => x.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                if (identities.Length != 1 || identities[0].Length == "--continuum-research-run=".Length) throw new InvalidOperationException("One nonempty run identity required.");
                run = identities[0].Substring("--continuum-research-run=".Length);
                Assembly game = typeof(PSystemManager).Assembly;
                StartupPinnedGameIdentity.Require(game);
                active = this; harmony = new Harmony(Owner);
                AddScope(typeof(PSystemManager), "SetupPSystem", 100670643);
                AddScope(typeof(PSystemManager), "SpawnBody", 100670646);
                AddScope(typeof(PSystemSetup), "SetupFacilities", 100670667);
                AddScope(typeof(PQS), "StartSphere", 100676830);
                AddSphereChild("SetupMods", 100676903);
                AddSphereChild("Mod_OnSetup", 100676882);
                AddSphereChild("SetupBuildDelegates", 100676906);
                AddSphereChild("UpdateVisual", 100676860);
                AddSphereChild("UpdateQuadsInit", 100676863);
                AddSphereChild("CreateQuads", 100676846);
                AddSphereChild("Mod_OnPostSetup", 100676883);
                AddSphereChild("Mod_OnSphereStart", 100676885);
                AddSphereChild("Mod_OnSphereStarted", 100676886);
                AddScope(typeof(PQSCity), "Orientate", 100676987);
                AddScope(typeof(PQSCity2), "Orientate", 100677006);
                AddScope(typeof(MainMenu), "Start", 100669619);
                AddScope(game.GetType("AnalyticsUtil", true), "LogGameStart", 100669859);
                Type iterator = typeof(PSystemSetup).GetNestedType("<SetupSystem>d__35", BindingFlags.NonPublic);
                MethodInfo target = iterator == null ? null : AccessTools.DeclaredMethod(iterator, "MoveNext");
                Require(target, 100705874);
                stateField = AccessTools.DeclaredField(iterator, "<>1__state");
                if (stateField == null || stateField.FieldType != typeof(int)) throw new InvalidOperationException("SetupSystem state differs.");
                move = new Metric { name = "SetupSystem.MoveNext-active-inclusive" }; scopes.Add(target, move);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(StartupWorldTiming), nameof(MovePrefix)), postfix: new HarmonyMethod(typeof(StartupWorldTiming), nameof(MovePostfix)), finalizer: new HarmonyMethod(typeof(StartupWorldTiming), nameof(Finish)), transpiler: new HarmonyMethod(typeof(StartupWorldTiming), nameof(MenuTranspile)));
                Type loading = typeof(LoadingScreen).GetNestedType("<LoadSystems>d__11", BindingFlags.NonPublic);
                MethodInfo load = loading == null ? null : AccessTools.DeclaredMethod(loading, "MoveNext"); Require(load, 100705068);
                harmony.Patch(load, transpiler: new HarmonyMethod(typeof(StartupWorldTiming), nameof(SystemTranspile)));
                SceneManager.sceneLoaded += SceneLoaded; Mark("observer-installed");
                UnityEngine.Debug.Log("[Continuum startup world timing] status=installed");
            }
            catch (Exception error) { UnityEngine.Debug.LogError("[Continuum startup world timing] status=rejected reason=" + error); Export("rejected"); Cleanup(); Application.Quit(2); }
        }
        static void Require(MethodInfo method, int token)
        {
            if (method == null || method.MetadataToken != token) throw new InvalidOperationException("Pinned timing seam differs: " + token);
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null || patches.Owners.Count == 0) return;
            bool deferredOrientation = Environment.GetCommandLineArgs().Contains("--continuum-startup-deferred-terrain") &&
                (token == 100676987 || token == 100677006) && patches.Owners.Count == 1 && patches.Owners[0] == "continuum.startup-deferred-terrain" &&
                patches.Prefixes.Count == 1 && patches.Prefixes[0].PatchMethod.DeclaringType == typeof(StartupDeferredTerrain) &&
                patches.Prefixes[0].PatchMethod.Name == "OrientPrefix" && patches.Postfixes.Count == 0 && patches.Transpilers.Count == 0 && patches.Finalizers.Count == 0;
            if (!deferredOrientation) throw new InvalidOperationException("Pinned timing seam already owned: " + token);
        }
        void AddScope(Type type, string name, int token)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name); Require(method, token);
            scopes.Add(method, new Metric { name = type.Name + "." + name + "-inclusive" });
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(StartupWorldTiming), type == typeof(PQS) && name == "StartSphere" ? nameof(SphereBegin) : nameof(Begin)), finalizer: new HarmonyMethod(typeof(StartupWorldTiming), nameof(Finish)));
        }
        void AddSphereChild(string name, int token)
        {
            MethodInfo method = AccessTools.DeclaredMethod(typeof(PQS), name); Require(method, token);
            scopes.Add(method, new Metric { name = "PQS." + name + "-within-StartSphere-inclusive" });
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(StartupWorldTiming), nameof(SphereChildBegin)), finalizer: new HarmonyMethod(typeof(StartupWorldTiming), nameof(Finish)));
        }
        static void Begin(MethodBase __originalMethod, out ScopeState __state) { __state = new ScopeState { started = Stopwatch.GetTimestamp(), metric = active.scopes[__originalMethod] }; }
        static void SphereBegin(PQS __instance, bool force, MethodBase __originalMethod, out ScopeState __state)
        {
            Begin(__originalMethod, out __state); __state.sphereEntry = true; sphereDepth++;
            BindSphere(__instance, ref __state);
            active.Mark("pqs-start-enter:" + __state.instanceName + ":force=" + force + ":started=" + __instance.isStarted + ":depth=" + sphereDepth);
        }
        static void SphereChildBegin(PQS __instance, MethodBase __originalMethod, out ScopeState __state)
        {
            __state = default(ScopeState); if (sphereDepth == 0) return;
            Begin(__originalMethod, out __state); BindSphere(__instance, ref __state);
        }
        static void BindSphere(PQS sphere, ref ScopeState state)
        { state.sphere = sphere; state.instanceName = sphere.name; state.quadsBefore = sphere.quadCount; }
        static Exception Finish(ScopeState __state, Exception __exception)
        {
            if (__state.metric == null) return __exception;
            long elapsed = Stopwatch.GetTimestamp() - __state.started;
            if (__state.metric.calls == 0) { __state.metric.firstTicks = elapsed; __state.metric.firstInstance = __state.instanceName; }
            __state.metric.calls++; __state.metric.ticks += elapsed;
            if (elapsed > __state.metric.maximumTicks)
            {
                __state.metric.maximumTicks = elapsed; __state.metric.maximumInstance = __state.instanceName;
                __state.metric.maximumQuadsBefore = __state.quadsBefore;
                __state.metric.maximumQuadsAfter = __state.sphere == null ? -1 : __state.sphere.quadCount;
            }
            if (__exception != null) __state.metric.errors++;
            if (ReferenceEquals(__state.metric, active.move)) active.lastMoveEnd = Stopwatch.GetTimestamp();
            if (__state.sphereEntry) { active.Mark("pqs-start-exit:" + __state.instanceName + ":depth=" + sphereDepth); sphereDepth--; }
            return __exception;
        }
        static void MovePrefix(object __instance, MethodBase __originalMethod, out ScopeState __state)
        {
            Begin(__originalMethod, out __state); int state = (int)active.stateField.GetValue(__instance);
            if (active.firstMove == 0) { active.firstMove = __state.started; active.Mark("setup-system-first-step"); }
            if (active.lastMoveEnd != 0) active.wakes.Add(new Wake { resumeState = state, ticks = __state.started - active.lastMoveEnd, meaning = state == 5 ? "launchsites-child-await-inclusive-gap" : "frame-or-scheduler-gap" });
        }
        static void MovePostfix(bool __result) { if (!__result) { active.completed = true; active.setupEnd = Stopwatch.GetTimestamp(); active.Mark("setup-system-complete"); } }
        bool completed; long setupEnd;
        static IEnumerable<CodeInstruction> SystemTranspile(IEnumerable<CodeInstruction> source) { return Replace(source, AccessTools.DeclaredMethod(typeof(SceneManager), "LoadScene", new[] { typeof(int) }), nameof(LoadSystem)); }
        static IEnumerable<CodeInstruction> MenuTranspile(IEnumerable<CodeInstruction> source) { return Replace(source, AccessTools.DeclaredMethod(typeof(HighLogic), "LoadScene", new[] { typeof(GameScenes) }), nameof(LoadMenu)); }
        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> source, MethodInfo original, string replacement)
        {
            var body = source.ToList(); if (original == null || body.Count(x => x.Calls(original)) != 1) throw new InvalidOperationException("Unique scene call seam differs.");
            foreach (CodeInstruction instruction in body) if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupWorldTiming), replacement); }
            return body;
        }
        static void LoadSystem(int scene) { active.Mark("before-load-scene-" + scene); SceneManager.LoadScene(scene); active.Mark("after-load-scene-invocation-" + scene); }
        static void LoadMenu(GameScenes scene) { active.Mark("before-highlogic-load-" + scene); HighLogic.LoadScene(scene); active.Mark("after-highlogic-load-invocation-" + scene); }
        void Mark(string name) { points.Add(new Point { name = name, ticks = Stopwatch.GetTimestamp() - epoch, engineSeconds = Time.realtimeSinceStartup }); }
        void SceneLoaded(Scene scene, LoadSceneMode mode) { Mark("scene-loaded-" + scene.buildIndex + "-" + mode); }
        public static void ObserveForMenu() { StartupPinnedGameIdentity.ObserveForMenu(); StartupModelTiming.ObserveForMenu(); if (active != null && !active.exported) { active.Mark("owned-mainmenu-observation"); active.Export("menu-observed"); active.Cleanup(); } }
        public void Update() { if (active == this && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) ObserveForMenu(); }
        void Export(string status)
        {
            if (exported) return; exported = true;
            try
            {
                var report = new Report { status = status, runId = run, observedUtc = DateTime.UtcNow.ToString("o"), frequency = Stopwatch.Frequency, setupSystemCompleted = completed, setupSystemElapsedTicks = firstMove == 0 ? 0 : (completed ? setupEnd : Stopwatch.GetTimestamp()) - firstMove, scopes = scopes.Values.ToArray(), points = points.ToArray(), wakeGaps = wakes.ToArray() };
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "startup-world-timing-" + Guid.NewGuid().ToString("N") + ".json");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream)) writer.Write(ReportJson.Encode(report));
                UnityEngine.Debug.Log("[Continuum startup world timing] status=" + status + " path=" + path + " setupSystemCompleted=" + completed);
            }
            catch (Exception error) { UnityEngine.Debug.LogError("[Continuum startup world timing] export-failed " + error); Application.Quit(2); }
        }
        void Cleanup() { SceneManager.sceneLoaded -= SceneLoaded; harmony?.UnpatchAll(Owner); harmony = null; }
        public void OnApplicationQuit() { if (active == this) { Export("interrupted"); Cleanup(); } }
        public void OnDestroy() { if (active == this) { Export("destroyed"); Cleanup(); active = null; } }
    }
}
