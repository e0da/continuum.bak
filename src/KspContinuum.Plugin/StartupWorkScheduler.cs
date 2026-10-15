using System;
using System.Collections;
using System.Collections.Generic;
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
    public sealed class StartupWorkScheduler : MonoBehaviour
    {
        const string Owner = "continuum.startup-work-scheduler";
        const string Marker = "[Continuum startup scheduler] ";
        static readonly object CompletedToken = new object();
        static StartupWorkScheduler active;
        [ThreadStatic] static IEnumerator draining;
        [Serializable] public sealed class LabOwner { public string scope = null; }
        Harmony harmony;
        MethodInfo move, partsMove;
        bool requested, exported;
        bool retired, retirementFailed;
        int mainThread, activeChildren;
        string runId;
        readonly HashSet<IEnumerator> pendingRoots = new HashSet<IEnumerator>();
        long databaseCompletions, partCompletions;
        string status = "not-requested";
        long dispatches, dragDispatches, completedJobs, suspendedJobs, resumedSteps, explicitWaits, nullWaits, rootBudgetYields, failures, drainedCompletions, rootReturns;

        public void Awake()
        {
            requested = Environment.GetCommandLineArgs().Contains("--continuum-startup-work-scheduler");
            if (!requested) return;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            DontDestroyOnLoad(gameObject);
            try
            {
                if (active != null) throw new InvalidOperationException("Startup scheduler already exists.");
                string marker = Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json");
                if (!File.Exists(marker) || new FileInfo(marker).Length > 65536 ||
                    JsonUtility.FromJson<LabOwner>(File.ReadAllText(marker)).scope != "disposable-lab")
                    throw new InvalidOperationException("Disposable laboratory ownership required.");
                string[] identities = Environment.GetCommandLineArgs().Where(value => value.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                if (identities.Length != 1 || identities[0].Length == "--continuum-research-run=".Length)
                    throw new InvalidOperationException("One owned run identity required.");
                runId = identities[0].Substring("--continuum-research-run=".Length);
                Assembly game = typeof(GameDatabase).Assembly;
                StartupPinnedGameIdentity.Require(game);
                Type iterator = typeof(GameDatabase).GetNestedType("<LoadObjects>d__90", BindingFlags.NonPublic);
                move = iterator == null ? null : AccessTools.DeclaredMethod(iterator, "MoveNext", Type.EmptyTypes);
                Type partsIterator = typeof(PartLoader).GetNestedType("<CompileParts>d__56", BindingFlags.NonPublic);
                partsMove = partsIterator == null ? null : AccessTools.DeclaredMethod(partsIterator, "MoveNext", Type.EmptyTypes);
                if (move == null || move.MetadataToken != 100704836 || move.ReturnType != typeof(bool) ||
                    partsMove == null || partsMove.MetadataToken != 100705099 || partsMove.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Pinned startup iterator differs.");
                if (new[] { move, partsMove }.Any(target => Harmony.GetPatchInfo(target)?.Owners.Count > 0))
                    throw new InvalidOperationException("Startup iterator has another patch owner.");
                active = this; harmony = new Harmony(Owner);
                foreach (MethodInfo target in new[] { move, partsMove })
                {
                    harmony.Patch(target, transpiler: new HarmonyMethod(typeof(StartupWorkScheduler), nameof(Transpile)),
                        postfix: new HarmonyMethod(typeof(StartupWorkScheduler), nameof(MovePostfix)));
                    Patches patches = Harmony.GetPatchInfo(target);
                    if (!HasExactOwnPatches(patches) || patches.Owners.Any(owner => owner != Owner))
                        throw new InvalidOperationException("Scheduler ownership verification failed.");
                }
                status = "installed"; Debug.Log(Marker + "status=" + status);
            }
            catch (Exception error)
            {
                status = "abstained:" + error.GetType().Name + ":" + error.Message;
                Cleanup(); Export(); Application.Quit(2);
            }
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var body = instructions.ToList();
            MethodInfo original = AccessTools.DeclaredMethod(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(IEnumerator) });
            bool parts = Equals(__originalMethod, active.partsMove);
            if (!parts && !Equals(__originalMethod, active.move)) throw new InvalidOperationException("Scheduler target differs.");
            MethodInfo drag = AccessTools.DeclaredMethod(typeof(DragCubeSystem), "SetupDragCubeCoroutine", new[] { typeof(Part) });
            if (original == null || body.Count(instruction => instruction.Calls(original)) != (parts ? 1 : 3) ||
                (parts && (drag == null || body.Count(instruction => instruction.Calls(drag)) != 1)))
                throw new InvalidOperationException("Pinned child dispatch count differs.");
            for (int i = 0; i < body.Count; i++)
            {
                if (!body[i].Calls(original)) continue;
                FieldInfo field = i + 3 < body.Count ? body[i + 3].operand as FieldInfo : null;
                if (i + 3 >= body.Count || body[i + 1].opcode != OpCodes.Dup || body[i + 2].opcode != OpCodes.Pop ||
                    body[i + 3].opcode != OpCodes.Stfld || field == null || field.Name != "<>2__current" ||
                    field.FieldType != typeof(object) || field.DeclaringType != __originalMethod.DeclaringType ||
                    (parts && (i < 3 || !body[i - 3].Calls(drag) || body[i - 2].opcode != OpCodes.Dup || body[i - 1].opcode != OpCodes.Pop)) ||
                    Enumerable.Range(1, 3).Any(offset => body[i + offset].labels.Count != 0 || body[i + offset].blocks.Count != 0))
                    throw new InvalidOperationException("Pinned child dispatch publication differs.");
                body[i].opcode = OpCodes.Call;
                body[i].operand = AccessTools.DeclaredMethod(typeof(StartupWorkScheduler), parts ? nameof(RunDragChild) : nameof(RunChild));
            }
            return body;
        }
        static void MovePostfix(IEnumerator __instance, ref bool __result)
        {
            if (ReferenceEquals(draining, __instance)) return;
            IEnumerator previous = draining; draining = __instance;
            try
            {
                while (__result && ReferenceEquals(__instance.Current, CompletedToken))
                {
                    active.drainedCompletions++;
                    __result = __instance.MoveNext();
                }
                active.rootReturns++;
                if (__result)
                {
                    active.pendingRoots.Add(__instance);
                    if (__instance.Current == null) active.rootBudgetYields++;
                    else active.explicitWaits++;
                }
                else
                {
                    active.pendingRoots.Remove(__instance);
                    if (__instance.GetType() == active.move.DeclaringType) active.databaseCompletions++;
                    else if (__instance.GetType() == active.partsMove.DeclaringType) active.partCompletions++;
                    else throw new InvalidOperationException("Scheduler root identity differs.");
                }
            }
            catch { active.failures++; DisposeQuietly(__instance); throw; }
            finally { draining = previous; }
        }
        static object RunDragChild(MonoBehaviour owner, IEnumerator child) { active.dragDispatches++; return RunChild(owner, child); }
        static object RunChild(MonoBehaviour owner, IEnumerator child)
        {
            active.dispatches++;
            if (child == null) throw new ArgumentNullException(nameof(child));
            bool suspended;
            try { suspended = child.MoveNext(); }
            catch { active.failures++; DisposeQuietly(child); throw; }
            if (!suspended)
            {
                DisposeIterator(child); active.completedJobs++;
                return CompletedToken;
            }
            active.suspendedJobs++;
            var primed = new PrimedChild(child);
            try { return owner.StartCoroutine(primed); }
            catch { active.failures++; DisposeQuietly(primed); throw; }
        }
        static void DisposeIterator(IEnumerator iterator) { (iterator as IDisposable)?.Dispose(); }
        static void DisposeQuietly(IEnumerator iterator)
        {
            try { DisposeIterator(iterator); }
            catch (Exception error) { Debug.LogWarning(Marker + "cleanupFailure=" + error.GetType().Name); }
        }
        sealed class PrimedChild : IEnumerator, IDisposable
        {
            IEnumerator source;
            readonly StartupWorkScheduler scheduler;
            bool primed = true;
            bool waitCounted;
            public PrimedChild(IEnumerator source) { this.source = source; scheduler = active; scheduler.activeChildren++; }
            public object Current
            {
                get
                {
                    object yielded = source.Current;
                    if (!waitCounted)
                    {
                        if (yielded == null) scheduler.nullWaits++; else scheduler.explicitWaits++;
                        waitCounted = true;
                    }
                    return yielded;
                }
            }
            public bool MoveNext()
            {
                if (source == null) return false;
                if (primed) { primed = false; waitCounted = false; return true; }
                scheduler.resumedSteps++;
                try
                {
                    bool result = source.MoveNext();
                    waitCounted = false;
                    if (!result) Dispose();
                    return result;
                }
                catch { scheduler.failures++; DisposeQuietly(this); throw; }
            }
            public void Reset() { throw new NotSupportedException(); }
            public void Dispose()
            {
                IEnumerator owned = source; source = null;
                if (owned == null) return;
                try { DisposeIterator(owned); }
                finally { scheduler.activeChildren--; }
            }
        }
        public static void ObserveForMenu() { if (active != null) active.Export(); }
        public void Update()
        {
            if (requested && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) Export();
            if (requested && active == this && !retired && !retirementFailed && status == "installed" && failures == 0 &&
                activeChildren == 0 && pendingRoots.Count == 0 && draining == null && HighLogic.LoadedScene == GameScenes.FLIGHT && StartupReady())
            {
                try { RetireAfterStartupForClock(); }
                catch (Exception error) { Debug.LogError(Marker + "retirementFailed=" + error); }
            }
        }
        static bool StartupReady()
        {
            return GameDatabase.Instance != null && GameDatabase.Instance.IsReady() &&
                PartLoader.Instance != null && PartLoader.Instance.IsReady();
        }
        public static void RetireAfterStartupForClock()
        {
            StartupWorkScheduler scheduler = active;
            if (scheduler == null) return;
            if (Thread.CurrentThread.ManagedThreadId != scheduler.mainThread)
                throw new InvalidOperationException("Startup retirement requires its original main thread.");
            if (scheduler.retired)
            {
                foreach (MethodInfo target in new[] { scheduler.move, scheduler.partsMove })
                    if (PatchGroups(Harmony.GetPatchInfo(target)).Any(group => group.Any(patch => patch.owner == Owner)))
                        throw new InvalidOperationException("Retired startup hooks reappeared.");
                return;
            }
            if (scheduler.status != "installed" || scheduler.retirementFailed || !StartupReady() ||
                draining != null || scheduler.activeChildren != 0 || scheduler.pendingRoots.Count != 0 || scheduler.failures != 0)
                throw new InvalidOperationException("Startup retirement requires completed healthy loaders and no outstanding scheduler jobs.");
            scheduler.Export();
            MethodInfo[] targets = { scheduler.move, scheduler.partsMove };
            Patch[][][] foreign = new Patch[targets.Length][][];
            try
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    Patches graph = Harmony.GetPatchInfo(targets[i]);
                    if (!HasExactOwnPatches(graph)) throw new InvalidOperationException("Startup retirement own graph differs.");
                    foreign[i] = PatchGroups(graph).Select(group => group.Where(patch => patch.owner != Owner).ToArray()).ToArray();
                }
                for (int i = 0; i < targets.Length; i++)
                {
                    scheduler.harmony.Unpatch(targets[i], HarmonyPatchType.All, Owner);
                    Patch[][] after = PatchGroups(Harmony.GetPatchInfo(targets[i]));
                    if (after.Any(group => group.Any(patch => patch.owner == Owner)))
                        throw new InvalidOperationException("Startup retirement own hook removal incomplete.");
                    for (int kind = 0; kind < after.Length; kind++)
                        if (after[kind].Length != foreign[i][kind].Length ||
                            after[kind].Where((patch, index) => !SamePatch(patch, foreign[i][kind][index])).Any())
                            throw new InvalidOperationException("Startup retirement foreign graph changed.");
                }
                scheduler.retired = true;
                Debug.Log(Marker + "retirement=completed runId=" + scheduler.runId + " frame=" + Time.frameCount +
                    " thread=" + scheduler.mainThread + " databaseReady=true partsReady=true databaseCompletions=" + scheduler.databaseCompletions +
                    " partCompletions=" + scheduler.partCompletions + " pendingRoots=0 activeChildren=0 ownHooksRemoved=4 foreignGraphsPreserved=true");
            }
            catch (Exception error)
            {
                scheduler.retirementFailed = true;
                Debug.LogError(Marker + "retirement=failed runId=" + scheduler.runId + " error=" + error);
                throw;
            }
        }
        static Patch[][] PatchGroups(Patches graph)
        {
            return graph == null ? new[] { new Patch[0], new Patch[0], new Patch[0], new Patch[0] } :
                new[] { graph.Prefixes.ToArray(), graph.Postfixes.ToArray(), graph.Transpilers.ToArray(), graph.Finalizers.ToArray() };
        }
        static bool SameMethod(MethodInfo a, MethodInfo b)
        {
            return a != null && b != null && (ReferenceEquals(a, b) ||
                (a.Module == b.Module && a.MetadataToken == b.MetadataToken && a.DeclaringType == b.DeclaringType));
        }
        static bool HasExactOwnPatches(Patches graph)
        {
            Patch[][] groups = PatchGroups(graph);
            MethodInfo postfix = AccessTools.DeclaredMethod(typeof(StartupWorkScheduler), nameof(MovePostfix));
            MethodInfo transpiler = AccessTools.DeclaredMethod(typeof(StartupWorkScheduler), nameof(Transpile));
            for (int kind = 0; kind < groups.Length; kind++)
            {
                Patch[] owned = groups[kind].Where(patch => patch.owner == Owner).ToArray();
                if (kind == 0 || kind == 3) { if (owned.Length != 0) return false; }
                else if (owned.Length != 1 || !SameMethod(owned[0].PatchMethod, kind == 1 ? postfix : transpiler) ||
                    owned[0].priority != Priority.Normal || (owned[0].before != null && owned[0].before.Length != 0) ||
                    (owned[0].after != null && owned[0].after.Length != 0)) return false;
            }
            return true;
        }
        static bool SamePatch(Patch a, Patch b)
        {
            return a.owner == b.owner && a.index == b.index && a.priority == b.priority && SameMethod(a.PatchMethod, b.PatchMethod) &&
                (a.before ?? new string[0]).SequenceEqual(b.before ?? new string[0]) &&
                (a.after ?? new string[0]).SequenceEqual(b.after ?? new string[0]);
        }
        public void OnApplicationQuit() { if (requested) Export(); }
        void Export()
        {
            if (exported) return;
            exported = true;
            Debug.Log(Marker + "status=" + status + " dispatches=" + dispatches + " dragDispatches=" + dragDispatches + " completedJobs=" + completedJobs + " suspendedJobs=" + suspendedJobs +
                " resumedSteps=" + resumedSteps + " explicitWaits=" + explicitWaits + " childNullWaits=" + nullWaits +
                " rootBudgetYields=" + rootBudgetYields + " failures=" + failures + " drainedCompletions=" + drainedCompletions + " rootReturns=" + rootReturns);
        }
        void Cleanup()
        {
            if (harmony == null || retired) return;
            if (move != null) harmony.Unpatch(move, HarmonyPatchType.All, Owner);
            if (partsMove != null) harmony.Unpatch(partsMove, HarmonyPatchType.All, Owner);
        }
        public void OnDestroy() { if (requested) { Export(); Cleanup(); if (active == this) active = null; } }
    }
}
