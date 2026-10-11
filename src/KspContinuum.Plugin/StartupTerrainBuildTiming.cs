using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupTerrainBuildTiming : MonoBehaviour
    {
        const string Owner = "continuum.startup-terrain-build-timing";
        static StartupTerrainBuildTiming active;
        [ThreadStatic] static Frame current;
        [ThreadStatic] static PQS initialization;
        Harmony harmony;
        MethodInfo build, initialize;
        int mainThread;
        bool installed, exported;
        long errors, built, skipped;
        string run;
        readonly Metric[] metrics = new[] { "BuildQuad-envelope", "other-setup", "quad-prebuild-callbacks", "vertex-construction-and-delegates", "native-mesh-positions-triangles-bounds", "mesh-channel-and-mod-callbacks", "quad-built-and-collider-callbacks", "other-tail" }
            .Select(name => new Metric { name = name }).ToArray();
        [Serializable] public sealed class Metric { public string name; public long calls, inclusiveTicks, exclusiveTicks, maximumTicks, errors; }
        [Serializable] public sealed class Report
        {
            public string schema = "continuum-startup-terrain-build-timing/v1", status, runId, cleanupStatus;
            public string scope = "BuildQuad only inside original UpdateQuadsInit on the admitted main thread. One sequential phase transition per quad; vertex loop is not instrumented per vertex. Envelope overlaps phases. Exclusive subtracts only nested BuildQuad work, not CPU/GPU attribution. Native mesh setters and bounds share one phase; Mod_OnQuadBuilt includes every stock callback, not only collider cooking. Default-off intrusive observer; source operations/order/arguments/results/exceptions unchanged.";
            public int processId;
            public long frequency, completedBuilds, skippedBuilds, errors;
            public bool callsitesInstalled, hooksRemoved, stackEmpty;
            public Metric[] metrics;
        }
        public sealed class Frame { internal Frame parent; internal PQS sphere; internal long start, phaseStart, children, phaseChildren; internal int phase; }
        public struct InitState { public PQS previous; public bool entered; }
        public void Awake()
        {
            if (Environment.GetEnvironmentVariable("CONTINUUM_STARTUP_TERRAIN_BUILD_TIMING") != "1") return;
            DontDestroyOnLoad(gameObject);
            try
            {
                if (active != null || !File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json"))) throw new InvalidOperationException("One private laboratory observer required.");
                string[] ids = Environment.GetCommandLineArgs().Where(a => a.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                if (ids.Length != 1 || ids[0].Length == "--continuum-research-run=".Length) throw new InvalidOperationException("One run identity required.");
                run = ids[0].Substring("--continuum-research-run=".Length);
                StartupPinnedGameIdentity.Require(typeof(PQS).Assembly);
                build = AccessTools.DeclaredMethod(typeof(PQS), "BuildQuad", new[] { typeof(PQ) });
                initialize = AccessTools.DeclaredMethod(typeof(PQS), "UpdateQuadsInit", Type.EmptyTypes);
                if (build == null || build.ReturnType != typeof(bool) || build.IsStatic || initialize == null || initialize.ReturnType != typeof(void)) throw new InvalidOperationException("Pinned terrain construction signatures differ.");
                if (Harmony.GetPatchInfo(build)?.Owners.Count > 0 && !StartupTerrainDispatchPlan.IsOwnedBuildObserver(build)) throw new InvalidOperationException("Terrain construction already modified.");
                Patches patches = Harmony.GetPatchInfo(initialize);
                if (patches != null && patches.Owners.Count > 0 && !(patches.Owners.Count == 1 && patches.Owners[0] == "continuum.startup-world-timing" && patches.Transpilers.Count == 0 && patches.Postfixes.Count == 0 && patches.Prefixes.Count == 1 && patches.Finalizers.Count == 1 && patches.Prefixes[0].PatchMethod.DeclaringType == typeof(StartupWorldTiming) && patches.Finalizers[0].PatchMethod.DeclaringType == typeof(StartupWorldTiming)))
                    throw new InvalidOperationException("Terrain initialization has an unrecognized owner.");
                active = this; mainThread = Thread.CurrentThread.ManagedThreadId; harmony = new Harmony(Owner);
                harmony.Patch(initialize, prefix: new HarmonyMethod(typeof(StartupTerrainBuildTiming), nameof(InitPrefix)), finalizer: new HarmonyMethod(typeof(StartupTerrainBuildTiming), nameof(InitFinalizer)));
                harmony.Patch(build, prefix: new HarmonyMethod(typeof(StartupTerrainBuildTiming), nameof(BuildPrefix)), finalizer: new HarmonyMethod(typeof(StartupTerrainBuildTiming), nameof(BuildFinalizer)), transpiler: new HarmonyMethod(typeof(StartupTerrainBuildTiming), nameof(Transpile)));
                installed = true;
                UnityEngine.Debug.Log("[Continuum terrain build timing] status=installed scope=per-quad-phases-no-per-vertex-hooks");
            }
            catch (Exception error) { errors++; UnityEngine.Debug.LogError("[Continuum terrain build timing] rejected " + error); Export("rejected"); Application.Quit(2); }
        }
        static void InitPrefix(PQS __instance, out InitState __state)
        {
            __state = default(InitState);
            if (active == null || !active.installed) return;
            if (Thread.CurrentThread.ManagedThreadId != active.mainThread) { active.errors++; return; }
            __state.previous = initialization; __state.entered = true; initialization = __instance;
        }
        static Exception InitFinalizer(InitState __state, Exception __exception)
        { if (__state.entered) initialization = __state.previous; return __exception; }
        static void BuildPrefix(PQS __instance, out Frame __state)
        {
            __state = null;
            if (active == null || !active.installed || !ReferenceEquals(initialization, __instance)) return;
            if (Thread.CurrentThread.ManagedThreadId != active.mainThread) { active.errors++; return; }
            long now = Stopwatch.GetTimestamp(); __state = new Frame { parent = current, sphere = __instance, start = now, phaseStart = now, phase = 1 }; current = __state;
        }
        static void Record(int slot, long elapsed, long child, bool failed)
        {
            Metric metric = active.metrics[slot]; metric.calls++; metric.inclusiveTicks += elapsed;
            if (elapsed < child) active.errors++; else metric.exclusiveTicks += elapsed - child;
            metric.maximumTicks = Math.Max(metric.maximumTicks, elapsed); if (failed) metric.errors++;
        }
        static void Phase(PQS sphere, int next)
        {
            if (active == null || current == null || !ReferenceEquals(current.sphere, sphere)) return;
            try
            {
                long now = Stopwatch.GetTimestamp(); Frame frame = current;
                Record(frame.phase, now - frame.phaseStart, frame.children - frame.phaseChildren, false);
                frame.phase = next; frame.phaseStart = now; frame.phaseChildren = frame.children;
            }
            catch { if (active != null) active.errors++; }
        }
        static Exception BuildFinalizer(Frame __state, bool __result, Exception __exception)
        {
            if (__state == null) return __exception;
            try
            {
                if (!ReferenceEquals(current, __state)) active.errors++;
                long now = Stopwatch.GetTimestamp(), elapsed = now - __state.start;
                Record(__state.phase, now - __state.phaseStart, __state.children - __state.phaseChildren, __exception != null);
                Record(0, elapsed, __state.children, __exception != null);
                if (__exception != null) active.errors++; else if (__result) active.built++; else active.skipped++;
                if (__state.parent != null) __state.parent.children += elapsed;
            }
            catch { if (active != null) active.errors++; }
            finally { current = __state.parent; }
            return __exception;
        }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            MethodInfo pre = AccessTools.DeclaredMethod(typeof(PQS), "Mod_OnQuadPreBuild", new[] { typeof(PQ) }), channels = AccessTools.DeclaredMethod(typeof(PQS), "Mod_OnMeshBuild", Type.EmptyTypes), callbacks = AccessTools.DeclaredMethod(typeof(PQS), "Mod_OnQuadBuilt", new[] { typeof(PQ) });
            MethodInfo vertices = AccessTools.PropertySetter(typeof(Mesh), "vertices"), triangles = AccessTools.PropertySetter(typeof(Mesh), "triangles"), bounds = AccessTools.DeclaredMethod(typeof(Mesh), "RecalculateBounds", Type.EmptyTypes);
            foreach (MethodInfo method in new[] { pre, vertices, triangles, bounds, channels, callbacks })
                if (method == null || body.Count(i => i.Calls(method)) != 1) throw new InvalidOperationException("Exact terrain construction call shape differs.");
            int preAt = body.FindIndex(i => i.Calls(pre)), verticesAt = body.FindIndex(i => i.Calls(vertices)), trianglesAt = body.FindIndex(i => i.Calls(triangles)), boundsAt = body.FindIndex(i => i.Calls(bounds)), channelsAt = body.FindIndex(i => i.Calls(channels)), callbacksAt = body.FindIndex(i => i.Calls(callbacks));
            if (!(preAt < verticesAt && verticesAt < trianglesAt && trianglesAt < boundsAt && boundsAt < channelsAt && channelsAt < callbacksAt)) throw new InvalidOperationException("Terrain publication order differs.");
            for (int i = 0; i < body.Count; i++)
            {
                CodeInstruction instruction = body[i]; int before = i == preAt ? 2 : i == verticesAt ? 4 : i == channelsAt ? 5 : i == callbacksAt ? 6 : 0;
                if (before != 0)
                {
                    var marker = new CodeInstruction(OpCodes.Ldarg_0); marker.labels.AddRange(instruction.labels); marker.blocks.AddRange(instruction.blocks); instruction.labels.Clear(); instruction.blocks.Clear();
                    yield return marker; yield return new CodeInstruction(OpCodes.Ldc_I4, before); yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(StartupTerrainBuildTiming), nameof(Phase)));
                }
                yield return instruction;
                int after = i == preAt ? 3 : i == callbacksAt ? 7 : 0;
                if (after != 0) { yield return new CodeInstruction(OpCodes.Ldarg_0); yield return new CodeInstruction(OpCodes.Ldc_I4, after); yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(StartupTerrainBuildTiming), nameof(Phase))); }
            }
            active.installedCallsites = true;
        }
        public void Update() { if (active == this && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) Export("observed"); }
        public static void ObserveForMenu() { if (active != null) active.Export("observed"); }
        void Export(string status)
        {
            if (exported) return; exported = true;
            bool empty = current == null && initialization == null, removed = false;
            try { harmony?.UnpatchAll(Owner); removed = new[] { build, initialize }.Where(m => m != null).All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(Owner) != true); }
            catch (Exception error) { errors++; UnityEngine.Debug.LogError("[Continuum terrain build timing] cleanup-error " + error); }
            installed = false;
            try
            {
                var report = new Report { status = status == "observed" && (errors != 0 || !empty || !removed || !installedCallsites || metrics[0].calls == 0) ? "invalid" : status, runId = run,
                    processId = Process.GetCurrentProcess().Id, frequency = Stopwatch.Frequency, completedBuilds = built, skippedBuilds = skipped, errors = errors,
                    callsitesInstalled = installedCallsites, hooksRemoved = removed, stackEmpty = empty, cleanupStatus = removed ? "removed-owned-hooks" : "cleanup-error", metrics = metrics };
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "startup-terrain-build-" + report.processId + ".json");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream)) writer.Write(ReportJson.Encode(report));
                UnityEngine.Debug.Log("[Continuum terrain build timing] status=" + report.status + " path=" + path);
            }
            catch (Exception error) { UnityEngine.Debug.LogError("[Continuum terrain build timing] export-error " + error); Application.Quit(2); }
        }
        bool installedCallsites;
        public void OnApplicationQuit() { if (active == this) Export("interrupted"); }
        public void OnDestroy() { if (active == this) { Export("destroyed"); active = null; } }
    }
}
