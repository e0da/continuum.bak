using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.LowLevel;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;
using UpdateLoop = UnityEngine.PlayerLoop.Update;
using LateLoop = UnityEngine.PlayerLoop.PreLateUpdate;
using RenderLoop = UnityEngine.PlayerLoop.PostLateUpdate;

namespace KspContinuum
{
    internal sealed class PlayerLoopTiming : IDisposable
    {
        sealed class BeforeHook { }
        sealed class AfterHook { }
        sealed class Scope
        {
            public string Name, TimeDomain, Overlap; public Type ParentType, Type; public PlayerLoopSystem Target;
            public PlayerLoopSystem.UpdateFunction Before, After; public LoopTimingBuffer Buffer;
        }
        readonly Scope[] scopes;
        readonly IPlayerLoopBracketObserver observer;
        readonly MacThreadWorkTiming threadWork;
        public MacThreadWorkTiming.ReportData MainThreadWork { get { return threadWork == null ? null : threadWork.Snapshot(); } }
        PlayerLoopSystem original;
        bool active, installed, disposed, valid = true, started;
        public LoopTimingReport Report { get; private set; }

        public PlayerLoopTiming(IPlayerLoopBracketObserver observer = null)
        {
            this.observer = observer;
            if (Environment.GetEnvironmentVariable("CONTINUUM_LOOP_THREAD_CPU") == "1") threadWork = new MacThreadWorkTiming();
            Report = new LoopTimingReport { status = "unavailable", integrityStatus = "not-installed", cleanupStatus = "not-installed",
                clockFrequency = Stopwatch.Frequency, timerReadFloorTicks = long.MaxValue };
            Report.measurementScope = "Elapsed wall time around unchanged PlayerLoop subtrees. Update, FixedUpdate, PreLateUpdate and PostLateUpdate parents overlap their children and must not be summed with them. Rendering/presentation brackets include main-thread work and any blocking waits; they do not measure GPU execution duration. Initialization alone requires exactly one native frameCount advance and stores the expected ending frame; other scopes require unchanged frameCount.";
            for (int i = 0; i < 128; i++) { long before = Stopwatch.GetTimestamp(); Report.timerReadFloorTicks = Math.Min(Report.timerReadFloorTicks, Stopwatch.GetTimestamp() - before); }
            scopes = new[] {
                MakeScope(null, typeof(UnityEngine.PlayerLoop.Initialization), "frame-turnover", "separate-frame-phase-exact-counter-advance-one", 1),
                MakeScope(null, typeof(UnityEngine.PlayerLoop.EarlyUpdate), "frame", "separate-frame-phase"),
                MakeScope(null, typeof(UnityEngine.PlayerLoop.PreUpdate), "frame", "separate-frame-phase"),
                MakeScope(null, typeof(FixedLoop), "fixed", "contains-fixed-children"),
                MakeScope(typeof(FixedLoop), typeof(FixedLoop.PhysicsFixedUpdate), "fixed", "contained-by-fixed"),
                MakeScope(typeof(FixedLoop), typeof(FixedLoop.ScriptRunBehaviourFixedUpdate), "fixed", "contained-by-fixed"),
                MakeScope(null, typeof(UpdateLoop), "frame", "contains-update-children-do-not-sum-with-children"),
                MakeScope(typeof(UpdateLoop), typeof(UpdateLoop.ScriptRunBehaviourUpdate), "frame", "separate-frame-phase"),
                MakeScope(typeof(UpdateLoop), typeof(UpdateLoop.ScriptRunDelayedDynamicFrameRate), "frame", "contained-by-update"),
                MakeScope(typeof(UpdateLoop), typeof(UpdateLoop.ScriptRunDelayedTasks), "frame", "contained-by-update"),
                MakeScope(null, typeof(LateLoop), "frame", "contains-pre-late-children-do-not-sum-with-children"),
                MakeScope(typeof(LateLoop), typeof(LateLoop.ScriptRunBehaviourLateUpdate), "frame", "contained-by-pre-late"),
                MakeScope(null, typeof(RenderLoop), "frame", "contains-post-late-children-do-not-sum-with-children"),
                MakeScope(typeof(RenderLoop), typeof(RenderLoop.UpdateAllRenderers), "frame", "contained-by-post-late"),
                MakeScope(typeof(RenderLoop), typeof(RenderLoop.UpdateAllSkinnedMeshes), "frame", "contained-by-post-late"),
                MakeScope(typeof(RenderLoop), typeof(RenderLoop.PlayerUpdateCanvases), "frame", "contained-by-post-late"),
                MakeScope(typeof(RenderLoop), typeof(RenderLoop.FinishFrameRendering), "frame", "contained-by-post-late"),
                MakeScope(typeof(RenderLoop), typeof(RenderLoop.PresentAfterDraw), "frame", "contained-by-post-late")
            };
        }

        Scope MakeScope(Type parentType, Type type, string timeDomain, string overlap, int expectedCounterAdvance = 0)
        {
            var scope = new Scope { Name = type.FullName, ParentType = parentType, Type = type, TimeDomain = timeDomain,
                Overlap = overlap, Buffer = new LoopTimingBuffer(type.FullName, 4096, Stopwatch.Frequency) };
            scope.Before = () => { if (!active) return; try {
                double time = timeDomain == "fixed" ? UnityEngine.Time.fixedTime : UnityEngine.Time.time;
                double delta = timeDomain == "fixed" ? UnityEngine.Time.fixedDeltaTime : UnityEngine.Time.deltaTime;
                if (observer != null) observer.Before(scope.Name, UnityEngine.Time.frameCount, time);
                scope.Buffer.Begin(Stopwatch.GetTimestamp(), checked(UnityEngine.Time.frameCount + expectedCounterAdvance), time, delta);
                if (threadWork != null && ThreadWorkScope(scope.Type)) threadWork.Before(scope.Name, UnityEngine.Time.frameCount);
            } catch (Exception error) { if (observer != null) observer.Fault(scope.Name, error); scope.Buffer.Fault(); Invalidate("Timing callback failed."); } };
            scope.After = () => { if (!active) return; try { if (threadWork != null && ThreadWorkScope(scope.Type)) threadWork.After(scope.Name, UnityEngine.Time.frameCount); scope.Buffer.End(Stopwatch.GetTimestamp(), UnityEngine.Time.frameCount); if (observer != null) observer.After(scope.Name, UnityEngine.Time.frameCount, timeDomain == "fixed" ? UnityEngine.Time.fixedTime : UnityEngine.Time.time); } catch (Exception error) { if (observer != null) observer.Fault(scope.Name, error); scope.Buffer.Fault(); Invalidate("Timing callback failed."); } };
            return scope;
        }
        static bool ThreadWorkScope(Type type) { return type == typeof(UpdateLoop) || type == typeof(UpdateLoop.ScriptRunDelayedDynamicFrameRate) || type == typeof(FixedLoop) || type == typeof(RenderLoop); }

        public void Start()
        {
            if (started || disposed) throw new InvalidOperationException("PlayerLoop capture supports one installation.");
            started = true;
            try
            {
                var current = PlayerLoop.GetCurrentPlayerLoop(); original = Clone(current);
                foreach (Scope scope in scopes)
                {
                    if (Count(current, n => n.type == scope.Type) != 1) throw new InvalidOperationException("Expected one native target: " + scope.Name + ".");
                    var parent = FindDirectParent(current, scope.Type);
                    if (scope.ParentType != null && parent.type != scope.ParentType) throw new InvalidOperationException("Unexpected native parent: " + scope.Name + ".");
                }
                var modified = Insert(current);
                foreach (Scope scope in scopes) scope.Target = Clone(Find(modified, scope.Type));
                installed = true; PlayerLoop.SetPlayerLoop(modified); active = true; Report.status = "no-samples"; Audit();
            }
            catch (Exception error) { valid = false; active = false; Report.detail = "Installation unavailable: " + error.GetType().Name + ". " + error.Message; Report.status = "unavailable"; }
        }

        public void Audit()
        {
            if (!installed || disposed) return;
            try
            {
                var current = PlayerLoop.GetCurrentPlayerLoop();
                if (!PreservesOriginal(current, original)) { Invalidate("Native PlayerLoop hierarchy or order changed."); return; }
                foreach (Scope scope in scopes)
                    if (Count(current, n => n.type == scope.Type) != 1 || CountCallbacks(current, scope.Before) != 1 || CountCallbacks(current, scope.After) != 1 || !Intact(current, scope))
                    { Invalidate("Native target or owned bracket changed, moved, duplicated or removed."); return; }
                if (valid) Report.integrityStatus = "verified-at-boundaries";
            }
            catch (Exception error) { Invalidate("Loop audit failed: " + error.GetType().Name); }
        }
        void Invalidate(string detail) { valid = false; active = false; Report.integrityStatus = "invalidated"; Report.detail = detail; }

        static bool PreservesOriginal(PlayerLoopSystem current, PlayerLoopSystem expected)
        {
            if (!SameHeader(current, expected)) return false;
            var wanted = expected.subSystemList; if (wanted == null || wanted.Length == 0) return true;
            var actual = current.subSystemList; if (actual == null) return false;
            int matched = 0;
            for (int i = 0; i < actual.Length && matched < wanted.Length; i++) if (actual[i].type == wanted[matched].type && PreservesOriginal(actual[i], wanted[matched])) matched++;
            return matched == wanted.Length;
        }
        static bool SameHeader(PlayerLoopSystem a, PlayerLoopSystem b) { return a.type == b.type && a.updateDelegate == b.updateDelegate && a.updateFunction == b.updateFunction && a.loopConditionFunction == b.loopConditionFunction; }

        bool Intact(PlayerLoopSystem root, Scope scope)
        {
            var parent = FindDirectParent(root, scope.Type); if (scope.ParentType != null && parent.type != scope.ParentType) return false;
            var children = parent.subSystemList;
            for (int i = 1; i + 1 < children.Length; i++) if (children[i].type == scope.Type)
                return PreservesOriginal(children[i], scope.Target) && Hook(children[i - 1], scope.Before, typeof(BeforeHook)) && Hook(children[i + 1], scope.After, typeof(AfterHook));
            return false;
        }
        static bool Hook(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback, Type type) { return node.type == type && node.updateDelegate == callback && node.updateFunction == IntPtr.Zero && node.loopConditionFunction == IntPtr.Zero && (node.subSystemList == null || node.subSystemList.Length == 0); }
        PlayerLoopSystem Insert(PlayerLoopSystem node)
        {
            if (node.subSystemList == null) return node;
            var children = new List<PlayerLoopSystem>();
            foreach (var rawChild in node.subSystemList)
            {
                var child = Insert(rawChild); Scope selected = null;
                foreach (var scope in scopes) if (rawChild.type == scope.Type && (scope.ParentType == null || node.type == scope.ParentType)) { selected = scope; break; }
                if (selected != null) children.Add(new PlayerLoopSystem { type = typeof(BeforeHook), updateDelegate = selected.Before });
                children.Add(child);
                if (selected != null) children.Add(new PlayerLoopSystem { type = typeof(AfterHook), updateDelegate = selected.After });
            }
            node.subSystemList = children.ToArray(); return node;
        }

        public void Dispose()
        {
            if (disposed) return; Audit(); active = false; disposed = true;
            if (threadWork != null) threadWork.Dispose();
            if (installed)
            {
                try
                {
                    int removed = 0; var latest = Strip(PlayerLoop.GetCurrentPlayerLoop(), ref removed); if (removed != 0) PlayerLoop.SetPlayerLoop(latest);
                    var verified = PlayerLoop.GetCurrentPlayerLoop();
                    foreach (var scope in scopes) if (CountCallbacks(verified, scope.Before) != 0 || CountCallbacks(verified, scope.After) != 0) throw new InvalidOperationException("Owned callbacks remain.");
                    Report.cleanupStatus = "removed-owned-hooks";
                }
                catch (Exception error) { Invalidate("Loop cleanup failed: " + error.GetType().Name); Report.cleanupStatus = "cleanup-error"; }
            }
            Report.scopes = new LoopTimingScope[scopes.Length]; bool observed = false, invalid = !valid;
            for (int i = 0; i < scopes.Length; i++) { Report.scopes[i] = scopes[i].Buffer.Finish(valid && installed);
                Report.scopes[i].timeDomain = scopes[i].TimeDomain; Report.scopes[i].overlap = scopes[i].Overlap;
                observed |= Report.scopes[i].status == "observed"; invalid |= Report.scopes[i].status == "invalid"; }
            if (Report.status != "unavailable") Report.status = invalid ? "invalid" : observed ? "observed" : "no-samples";
            if (invalid && installed) Report.integrityStatus = "invalidated";
            if (invalid) foreach (var scope in Report.scopes) { scope.status = "invalid"; scope.milliseconds = null; }
        }

        PlayerLoopSystem Strip(PlayerLoopSystem node, ref int removed)
        {
            if (node.updateDelegate != null) foreach (PlayerLoopSystem.UpdateFunction callback in node.updateDelegate.GetInvocationList()) if (Owned(callback)) { node.updateDelegate -= callback; removed++; }
            if (node.subSystemList != null)
            {
                var children = new List<PlayerLoopSystem>();
                foreach (var child in node.subSystemList)
                {
                    bool ours = child.updateDelegate != null && HasOwned(child.updateDelegate); var stripped = Strip(child, ref removed);
                    bool empty = stripped.updateDelegate == null && stripped.updateFunction == IntPtr.Zero && stripped.loopConditionFunction == IntPtr.Zero && (stripped.subSystemList == null || stripped.subSystemList.Length == 0);
                    if (!(ours && empty && (child.type == typeof(BeforeHook) || child.type == typeof(AfterHook)))) children.Add(stripped);
                }
                node.subSystemList = children.ToArray();
            }
            return node;
        }
        bool Owned(PlayerLoopSystem.UpdateFunction callback) { foreach (var scope in scopes) if (callback == scope.Before || callback == scope.After) return true; return false; }
        bool HasOwned(PlayerLoopSystem.UpdateFunction callbacks) { foreach (PlayerLoopSystem.UpdateFunction callback in callbacks.GetInvocationList()) if (Owned(callback)) return true; return false; }
        static int CountCallbacks(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback) { int result = 0; if (node.updateDelegate != null) foreach (Delegate entry in node.updateDelegate.GetInvocationList()) if (entry.Equals(callback)) result++; if (node.subSystemList != null) foreach (var child in node.subSystemList) result += CountCallbacks(child, callback); return result; }
        static int Count(PlayerLoopSystem node, Predicate<PlayerLoopSystem> predicate) { int result = predicate(node) ? 1 : 0; if (node.subSystemList != null) foreach (var child in node.subSystemList) result += Count(child, predicate); return result; }
        static PlayerLoopSystem Find(PlayerLoopSystem node, Type type) { if (node.type == type) return node; if (node.subSystemList != null) foreach (var child in node.subSystemList) if (Count(child, n => n.type == type) > 0) return Find(child, type); throw new InvalidOperationException("Missing loop node."); }
        static PlayerLoopSystem FindDirectParent(PlayerLoopSystem node, Type type) { if (node.subSystemList != null) { foreach (var child in node.subSystemList) if (child.type == type) return node; foreach (var child in node.subSystemList) if (Count(child, n => n.type == type) > 0) return FindDirectParent(child, type); } throw new InvalidOperationException("Missing loop parent."); }
        static PlayerLoopSystem Clone(PlayerLoopSystem node) { if (node.subSystemList != null) { var children = new PlayerLoopSystem[node.subSystemList.Length]; for (int i = 0; i < children.Length; i++) children[i] = Clone(node.subSystemList[i]); node.subSystemList = children; } return node; }
    }
}
