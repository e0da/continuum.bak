using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupModelTiming : MonoBehaviour
    {
        const string Owner = "continuum.startup-model-timing";
        static StartupModelTiming active;
        Harmony harmony;
        string run;
        int mainThread;
        bool exported;
        long foreignThreadCalls, stackErrors;
        readonly Dictionary<MethodBase, Metric> metrics = new Dictionary<MethodBase, Metric>();
        Frame current;

        [Serializable] public sealed class Metric
        {
            public string name;
            public int token;
            public long calls, inclusiveTicks, exclusiveTicks, maximumTicks, firstTicks, errors;
        }
        public sealed class Frame
        {
            public Metric metric;
            public Frame parent;
            public long start, children;
        }
        [Serializable] public sealed class Report
        {
            public string schema = "continuum-startup-model-timing/v1", status, runId, observedUtc;
            public string interpretation = "Synchronous monotonic elapsed scopes, not CPU or GPU exclusive costs. Exclusive subtracts only directly nested instrumented scopes; uninstrumented Unity/native work, I/O, allocations and callbacks remain. PartReader mixes MU parsing and Unity construction; CompileModel includes model clones and configuration, not exclusively native publication. Inclusive scopes overlap and must not be summed. First call may include JIT. Zero calls can mean Mono inlining or missing coverage. No coroutine suspension is timed. Measurements include observer overhead.";
            public long frequency, foreignThreadCalls, stackErrors;
            public bool stackEmpty;
            public Metric[] scopes;
        }

        public void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!args.Contains("--continuum-startup-world-timing")) return;
            DontDestroyOnLoad(gameObject);
            try
            {
                if (active != null || !File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json")))
                    throw new InvalidOperationException("One private laboratory observer required.");
                string[] identities = args.Where(x => x.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                if (identities.Length != 1 || identities[0].Length == "--continuum-research-run=".Length)
                    throw new InvalidOperationException("One nonempty run identity required.");
                run = identities[0].Substring("--continuum-research-run=".Length);
                Assembly game = typeof(PartLoader).Assembly;
                if (game.ManifestModule.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf") ||
                    ScaleCheckpointLoadState.HashFile(game.Location) != "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27")
                    throw new InvalidOperationException("Pinned game differs.");
                active = this; mainThread = Thread.CurrentThread.ManagedThreadId; harmony = new Harmony(Owner);
                Add(game, "PartReader", "Read", 100668508);
                Add(game, "PartReader", "ReadMesh", 100668514);
                Add(game, "PartReader", "ReadMaterial", 100668515);
                Add(game, "PartReader", "ReadMaterial4", 100668516);
                Add(game, "PartLoader", "ParsePart", 100668679);
                Add(game, "PartLoader", "CompilePartInfo", 100668682);
                Add(game, "PartLoader", "CreatePart", 100668685);
                Add(game, "PartLoader", "CreatePartIcon", 100668688);
                Add(game, "PartLoader", "LoadInternalProp", 100668696);
                Add(game, "PartLoader", "LoadInternalSpace", 100668699);
                Add(game, "PartLoader", "CompileModel", 100668700);
                UnityEngine.Debug.Log("[Continuum startup model timing] status=installed scopes=" + metrics.Count);
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogError("[Continuum startup model timing] status=rejected reason=" + error);
                Export("rejected"); Cleanup(); Application.Quit(2);
            }
        }

        void Add(Assembly game, string typeName, string methodName, int token)
        {
            Type type = game.GetType(typeName, true);
            MethodInfo[] targets = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(x => x.Name == methodName && x.MetadataToken == token).ToArray();
            if (targets.Length != 1) throw new InvalidOperationException("Pinned timing seam differs: " + token);
            MethodInfo target = targets[0];
            Patches patches = Harmony.GetPatchInfo(target);
            if (patches != null && patches.Owners.Count != 0) throw new InvalidOperationException("Timing seam already owned: " + token);
            metrics.Add(target, new Metric { name = typeName + "." + methodName, token = token });
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(StartupModelTiming), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(StartupModelTiming), nameof(Finish)));
        }

        static void Begin(MethodBase __originalMethod, out Frame __state)
        {
            __state = null;
            if (Thread.CurrentThread.ManagedThreadId != active.mainThread)
            { Interlocked.Increment(ref active.foreignThreadCalls); return; }
            __state = new Frame { metric = active.metrics[__originalMethod], parent = active.current, start = Stopwatch.GetTimestamp() };
            active.current = __state;
        }

        static Exception Finish(Frame __state, Exception __exception)
        {
            if (__state == null) return __exception;
            long elapsed = Stopwatch.GetTimestamp() - __state.start;
            Metric metric = __state.metric;
            if (metric.calls == 0) metric.firstTicks = elapsed;
            metric.calls++; metric.inclusiveTicks += elapsed;
            if (elapsed > metric.maximumTicks) metric.maximumTicks = elapsed;
            if (__exception != null) metric.errors++;
            if (!ReferenceEquals(active.current, __state) || elapsed < __state.children)
                active.stackErrors++;
            else metric.exclusiveTicks += elapsed - __state.children;
            active.current = __state.parent;
            if (__state.parent != null) __state.parent.children += elapsed;
            return __exception;
        }

        public static void ObserveForMenu()
        {
            if (active == null || active.exported) return;
            active.Export("menu-observed"); active.Cleanup();
        }
        public void Update()
        { if (active == this && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) ObserveForMenu(); }
        void Export(string status)
        {
            if (exported) return; exported = true;
            try
            {
                var report = new Report { status = status, runId = run, observedUtc = DateTime.UtcNow.ToString("o"),
                    frequency = Stopwatch.Frequency, foreignThreadCalls = foreignThreadCalls, stackErrors = stackErrors,
                    stackEmpty = current == null, scopes = metrics.Values.OrderBy(x => x.token).ToArray() };
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "startup-model-timing-" + Guid.NewGuid().ToString("N") + ".json");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream)) writer.Write(ReportJson.Encode(report));
                UnityEngine.Debug.Log("[Continuum startup model timing] status=" + status + " path=" + path + " stackErrors=" + stackErrors);
            }
            catch (Exception error)
            { UnityEngine.Debug.LogError("[Continuum startup model timing] export-failed " + error); Application.Quit(2); }
        }
        void Cleanup() { harmony?.UnpatchAll(Owner); harmony = null; }
        public void OnApplicationQuit() { if (active == this) { Export("interrupted"); Cleanup(); } }
        public void OnDestroy() { if (active == this) { Export("destroyed"); Cleanup(); active = null; } }
    }
}
