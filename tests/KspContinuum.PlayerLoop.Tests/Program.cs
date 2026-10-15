using System;
using System.Text.Json;
using KspContinuum;
using UnityEngine.LowLevel;
using Fixed = UnityEngine.PlayerLoop.FixedUpdate;
using Update = UnityEngine.PlayerLoop.Update;
using Late = UnityEngine.PlayerLoop.PreLateUpdate;
using Initialization = UnityEngine.PlayerLoop.Initialization;
using Early = UnityEngine.PlayerLoop.EarlyUpdate;
using Pre = UnityEngine.PlayerLoop.PreUpdate;
using Render = UnityEngine.PlayerLoop.PostLateUpdate;

static class Program
{
    sealed class RecordingObserver : IPlayerLoopBracketObserver
    {
        readonly string name; readonly System.Collections.Generic.List<string> events;
        public RecordingObserver(string name, System.Collections.Generic.List<string> events) { this.name = name; this.events = events; }
        public void Before(string scope, int frame, double time) { if (scope == typeof(Fixed.PhysicsFixedUpdate).FullName) events.Add(name + "-before"); }
        public void After(string scope, int frame, double time) { if (scope == typeof(Fixed.PhysicsFixedUpdate).FullName) events.Add(name + "-after"); }
        public void Fault(string scope, Exception error) { events.Add(name + "-fault"); }
    }
    static int checks;
    static void Check(bool ok) { checks++; if (!ok) throw new Exception("PlayerLoop assertion " + checks); }
    static PlayerLoopSystem Native(Type type) { return new PlayerLoopSystem { type = type, updateFunction = (IntPtr)37, loopConditionFunction = (IntPtr)42 }; }
    static PlayerLoopSystem Tree() { return new PlayerLoopSystem { subSystemList = new[] {
        Native(typeof(Initialization)), Native(typeof(Early)),
        new PlayerLoopSystem { type = typeof(Fixed), subSystemList = new[] { Native(typeof(Fixed.ScriptRunBehaviourFixedUpdate)), Native(typeof(Fixed.PhysicsFixedUpdate)) } },
        Native(typeof(Pre)),
        new PlayerLoopSystem { type = typeof(Update), subSystemList = new[] { Native(typeof(Update.ScriptRunBehaviourUpdate)), Native(typeof(Update.ScriptRunDelayedDynamicFrameRate)), Native(typeof(Update.ScriptRunDelayedTasks)) } },
        new PlayerLoopSystem { type = typeof(Late), subSystemList = new[] { Native(typeof(Late.ScriptRunBehaviourLateUpdate)) } },
        new PlayerLoopSystem { type = typeof(Render), subSystemList = new[] { Native(typeof(Render.UpdateAllRenderers)), Native(typeof(Render.UpdateAllSkinnedMeshes)), Native(typeof(Render.PlayerUpdateCanvases)), Native(typeof(Render.FinishFrameRendering)), Native(typeof(Render.PresentAfterDraw)) } }
    } }; }
    static void Dispatch(PlayerLoopSystem node) { if (node.updateDelegate != null) node.updateDelegate(); if (node.type == typeof(Initialization)) UnityEngine.Time.frameCount++; if (node.subSystemList != null) foreach (var child in node.subSystemList) Dispatch(child); }
    static void DispatchPhysics(PlayerLoopSystem node) { if (node.updateDelegate != null) node.updateDelegate(); if (node.type == typeof(Initialization)) UnityEngine.Time.frameCount++; if (node.type == typeof(Fixed.PhysicsFixedUpdate)) UnityEngine.Rigidbody.Integrate(); if (node.subSystemList != null) foreach (var child in node.subSystemList) DispatchPhysics(child); }
    static PlayerLoopSystem Find(PlayerLoopSystem node, Type type) { if (node.type == type) return node; if (node.subSystemList != null) foreach (var child in node.subSystemList) { try { return Find(child, type); } catch (InvalidOperationException) { } } throw new InvalidOperationException(); }
    static PlayerLoopSystem Replace(PlayerLoopSystem node, Type type, PlayerLoopSystem replacement) { if (node.type == type) return replacement; if (node.subSystemList != null) for (int i = 0; i < node.subSystemList.Length; i++) node.subSystemList[i] = Replace(node.subSystemList[i], type, replacement); return node; }
    static bool SameTree(PlayerLoopSystem left, PlayerLoopSystem right)
    {
        if (left.type != right.type || left.updateDelegate != right.updateDelegate || left.updateFunction != right.updateFunction || left.loopConditionFunction != right.loopConditionFunction) return false;
        if ((left.subSystemList == null) != (right.subSystemList == null)) return false;
        if (left.subSystemList == null) return true;
        if (left.subSystemList.Length != right.subSystemList.Length) return false;
        for (int i = 0; i < left.subSystemList.Length; i++) if (!SameTree(left.subSystemList[i], right.subSystemList[i])) return false;
        return true;
    }

    static void Main()
    {
        var before = Tree(); PlayerLoop.Current = before; var capture = new PlayerLoopTiming(); capture.Start();
        Check(before.subSystemList.Length == 7 && Find(before, typeof(Fixed)).subSystemList.Length == 2);
        Check(PlayerLoop.Current.subSystemList.Length == 21); Check(Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 6);
        Check(Find(PlayerLoop.Current, typeof(Update)).subSystemList.Length == 9); Check(Find(PlayerLoop.Current, typeof(Late)).subSystemList.Length == 3); Check(Find(PlayerLoop.Current, typeof(Render)).subSystemList.Length == 15);
        int initialFrame = UnityEngine.Time.frameCount;
        Dispatch(PlayerLoop.Current); capture.Audit(); capture.Dispose(); var report = capture.Report;
        Check(report.schema == "ksp-continuum-playerloop/v2"); Check(report.integrityStatus == "verified-at-boundaries" && report.cleanupStatus == "removed-owned-hooks"); Check(report.scopes.Length == 18);
        string[] names = {
            typeof(Initialization).FullName, typeof(Early).FullName, typeof(Pre).FullName,
            typeof(Fixed).FullName, typeof(Fixed.PhysicsFixedUpdate).FullName, typeof(Fixed.ScriptRunBehaviourFixedUpdate).FullName,
            typeof(Update).FullName, typeof(Update.ScriptRunBehaviourUpdate).FullName, typeof(Update.ScriptRunDelayedDynamicFrameRate).FullName, typeof(Update.ScriptRunDelayedTasks).FullName,
            typeof(Late).FullName, typeof(Late.ScriptRunBehaviourLateUpdate).FullName,
            typeof(Render).FullName, typeof(Render.UpdateAllRenderers).FullName, typeof(Render.UpdateAllSkinnedMeshes).FullName,
            typeof(Render.PlayerUpdateCanvases).FullName, typeof(Render.FinishFrameRendering).FullName, typeof(Render.PresentAfterDraw).FullName
        };
        for (int i = 0; i < names.Length; i++) { Check(report.scopes[i].name == names[i]); Check(report.scopes[i].samples.Length == 1); Check(report.scopes[i].milliseconds.count == 1); Check(report.scopes[i].timeDomain == (i == 0 ? "frame-turnover" : i >= 3 && i <= 5 ? "fixed" : "frame")); Check(report.scopes[i].samples[0].frame == initialFrame + 1); }
        Check(report.scopes[3].overlap == "contains-fixed-children" && report.scopes[4].overlap == "contained-by-fixed" && report.scopes[11].overlap == "contained-by-pre-late");
        Check(report.scopes[12].overlap == "contains-post-late-children-do-not-sum-with-children" && report.scopes[17].overlap == "contained-by-post-late");
        Check(SameTree(PlayerLoop.Current, before));
        int writes = PlayerLoop.Writes; capture.Dispose(); Check(writes == PlayerLoop.Writes);
        using (var json = JsonDocument.Parse(ReportJson.Encode(report))) Check(json.RootElement.GetProperty("scopes").GetArrayLength() == 18);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); var current = PlayerLoop.Current;
        var expanded = new PlayerLoopSystem[current.subSystemList.Length + 1]; Array.Copy(current.subSystemList, expanded, current.subSystemList.Length); expanded[expanded.Length - 1] = Native(typeof(Program)); current.subSystemList = expanded; PlayerLoop.Current = current;
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "verified-at-boundaries"); Check(PlayerLoop.Current.subSystemList.Length == 8 && PlayerLoop.Current.subSystemList[7].type == typeof(Program));

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); current = PlayerLoop.Current; var fixedNode = Find(current, typeof(Fixed));
        expanded = new PlayerLoopSystem[fixedNode.subSystemList.Length + 1]; Array.Copy(fixedNode.subSystemList, expanded, fixedNode.subSystemList.Length); expanded[expanded.Length - 1] = Native(typeof(decimal)); fixedNode.subSystemList = expanded; PlayerLoop.Current = Replace(current, typeof(Fixed), fixedNode);
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "verified-at-boundaries"); Check(Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 3);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); current = PlayerLoop.Current;
        fixedNode = Find(current, typeof(Fixed)); var swap = fixedNode.subSystemList[1]; fixedNode.subSystemList[1] = fixedNode.subSystemList[4]; fixedNode.subSystemList[4] = swap; PlayerLoop.Current = Replace(current, typeof(Fixed), fixedNode);
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "invalidated"); foreach (var scope in capture.Report.scopes) Check(scope.status == "invalid" && scope.milliseconds == null);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); current = PlayerLoop.Current; int calls = 0; PlayerLoopSystem.UpdateFunction extra = () => calls++;
        current.subSystemList[0].updateDelegate += extra; current.subSystemList[0].subSystemList = new[] { Native(typeof(string)) }; PlayerLoop.Current = current; capture.Audit(); capture.Dispose();
        Check(capture.Report.integrityStatus == "invalidated"); Check(PlayerLoop.Current.subSystemList[0].subSystemList[0].type == typeof(string)); PlayerLoop.Current.subSystemList[0].updateDelegate(); Check(calls == 1);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); PlayerLoop.Current.subSystemList[0].updateDelegate(); capture.Dispose(); Check(capture.Report.status == "invalid"); foreach (var scope in capture.Report.scopes) Check(scope.status == "invalid" && scope.milliseconds == null);

        before = Tree(); PlayerLoop.Current = before; capture = new PlayerLoopTiming(); PlayerLoop.FailAfterWrite = true; capture.Start();
        Check(PlayerLoop.Current.subSystemList.Length == 21 && Find(PlayerLoop.Current, typeof(Render)).subSystemList.Length == 15);
        capture.Dispose(); Check(capture.Report.status == "unavailable" && capture.Report.cleanupStatus == "removed-owned-hooks" && SameTree(PlayerLoop.Current, before));
        PlayerLoop.Current = Tree(); current = PlayerLoop.Current; var updateNode = Find(current, typeof(Update)); updateNode.subSystemList = Array.Empty<PlayerLoopSystem>(); PlayerLoop.Current = Replace(current, typeof(Update), updateNode); writes = PlayerLoop.Writes; capture = new PlayerLoopTiming(); capture.Start(); capture.Dispose(); Check(capture.Report.status == "unavailable" && PlayerLoop.Writes == writes);
        PlayerLoop.Current = Tree(); current = PlayerLoop.Current; fixedNode = Find(current, typeof(Fixed)); fixedNode.subSystemList = new[] { Native(typeof(Fixed.PhysicsFixedUpdate)), Native(typeof(Fixed.PhysicsFixedUpdate)), Native(typeof(Fixed.ScriptRunBehaviourFixedUpdate)) }; PlayerLoop.Current = Replace(current, typeof(Fixed), fixedNode); writes = PlayerLoop.Writes; capture = new PlayerLoopTiming(); capture.Start(); capture.Dispose(); Check(capture.Report.status == "unavailable" && PlayerLoop.Writes == writes);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); current = PlayerLoop.Current; var renderNode = Find(current, typeof(Render));
        var foreignChild = Native(typeof(Guid)); renderNode.subSystemList[0].updateDelegate = null; renderNode.subSystemList[0].subSystemList = new[] { foreignChild }; PlayerLoop.Current = Replace(current, typeof(Render), renderNode);
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "invalidated" && capture.Report.cleanupStatus == "removed-owned-hooks");
        renderNode = Find(PlayerLoop.Current, typeof(Render)); Check(renderNode.subSystemList.Length == 6 && SameTree(renderNode.subSystemList[0].subSystemList[0], foreignChild));
        Check(Find(PlayerLoop.Current, typeof(Render.PresentAfterDraw)).updateFunction == (IntPtr)37);

        before = Tree(); var initialization = Find(before, typeof(Initialization)); initialization.updateDelegate = () => UnityEngine.Time.frameCount++; before = Replace(before, typeof(Initialization), initialization);
        PlayerLoop.Current = before; capture = new PlayerLoopTiming(); capture.Start(); Dispatch(PlayerLoop.Current); capture.Dispose();
        Check(capture.Report.status == "invalid" && capture.Report.scopes[0].status == "invalid" && SameTree(PlayerLoop.Current, before));

        var buffer = new LoopTimingBuffer("test", 2, 1000); buffer.Begin(100, 7, 1, .02); buffer.End(125, 7); buffer.Begin(200, 7, 1.02, .02); buffer.End(250, 7); buffer.Begin(300, 8, 1.04, .02); buffer.End(350, 8);
        var result = buffer.Finish(true); Check(result.samples.Length == 2 && result.droppedSamples == 1); Check(result.milliseconds.mean == 37.5 && result.samples[0].elapsedTicks == 25);
        buffer = new LoopTimingBuffer("bad", 2, 1000); buffer.Begin(100, 1, 0, .02); buffer.End(110, 2); result = buffer.Finish(true); Check(result.status == "invalid" && result.milliseconds == null);
        buffer = new LoopTimingBuffer("missing", 2, 1000); buffer.Begin(100, 1, 0, .02); Check(buffer.Finish(true).status == "invalid");

        PlayerLoop.Current = Tree(); UnityEngine.GameObject.Bodies.Clear(); UnityEngine.Time.frameCount = 30;
        var qualification = new PhysicsBoundaryQualification(); qualification.Start();
        for (int i = 0; i < 3; i++) { UnityEngine.Time.fixedTime = i * UnityEngine.Time.fixedDeltaTime; DispatchPhysics(PlayerLoop.Current); }
        Check(!qualification.IsRunning); qualification.Dispose();
        Check(qualification.Report.status == "qualified" && qualification.Report.retainedTrials == 3);
        LifecycleOrderQualification.Validate(qualification.Report);
        Check(Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 2);

        PlayerLoop.Current = Tree(); UnityEngine.GameObject.Bodies.Clear(); qualification = new PhysicsBoundaryQualification(); qualification.Start();
        for (int i = 0; i < 3; i++) Dispatch(PlayerLoop.Current);
        qualification.Dispose(); Check(qualification.Report.status == "invalid" && qualification.Report.retainedTrials == 0);
        LifecycleOrderQualification.Validate(qualification.Report);

        PlayerLoop.Current = Tree(); int beforeCalls = 0, afterCalls = 0;
        var firstHooks = new PhysicsBoundaryHooks(() => beforeCalls++, () => afterCalls++);
        var secondHooks = new PhysicsBoundaryHooks(() => { }, () => { }); firstHooks.Start();
        bool rejectedOwner = false; try { secondHooks.Start(); } catch (InvalidOperationException) { rejectedOwner = true; }
        Check(rejectedOwner); Dispatch(PlayerLoop.Current); Check(beforeCalls == 1 && afterCalls == 1);
        firstHooks.Dispose(); secondHooks = new PhysicsBoundaryHooks(() => { }, () => { }); secondHooks.Start(); secondHooks.Dispose();
        Check(firstHooks.CleanupStatus == "removed-owned-hooks" && secondHooks.CleanupStatus == "removed-owned-hooks");

        PlayerLoop.Current = Tree(); var failedHooks = new PhysicsBoundaryHooks(() => { }, () => { });
        PlayerLoop.FailAfterWrite = true; bool setterRejected = false; try { failedHooks.Start(); } catch (InvalidOperationException) { setterRejected = true; }
        Check(setterRejected && failedHooks.CleanupStatus == "removed-owned-hooks");
        Check(Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 2);

        PlayerLoop.Current = Tree(); var poisonedHooks = new PhysicsBoundaryHooks(() => { }, () => { }); poisonedHooks.Start();
        PlayerLoop.FailBeforeWrite = true; poisonedHooks.Dispose(); Check(poisonedHooks.CleanupStatus == "cleanup-error");
        var blockedHooks = new PhysicsBoundaryHooks(() => { }, () => { }); bool blockedAfterCleanupFailure = false;
        try { blockedHooks.Start(); } catch (InvalidOperationException) { blockedAfterCleanupFailure = true; }
        Check(blockedAfterCleanupFailure);

        PlayerLoop.Current = Tree(); int candidateCalls = 0;
        var substitution = new PhysicsBoundarySubstitution(() => candidateCalls++); substitution.Start();
        Check(Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 2);
        DispatchPhysics(PlayerLoop.Current); Check(candidateCalls == 1);
        substitution.Audit(); substitution.Dispose();
        Check(substitution.CleanupStatus == "native-node-restored");
        Check(Find(PlayerLoop.Current, typeof(Fixed.PhysicsFixedUpdate)).updateFunction == (IntPtr)37);
        DispatchPhysics(PlayerLoop.Current); Check(candidateCalls == 1);

        PlayerLoop.Current = Tree(); var events = new System.Collections.Generic.List<string>();
        capture = new PlayerLoopTiming(new CompositePlayerLoopObserver(new RecordingObserver("a", events), new RecordingObserver("b", events)));
        capture.Start(); substitution = null;
        substitution = new PhysicsBoundarySubstitution(() => { events.Add("candidate"); substitution.Dispose(); }); substitution.Start();
        DispatchPhysics(PlayerLoop.Current); capture.Dispose();
        Check(string.Join(",", events) == "a-before,b-before,candidate,a-after,b-after");
        Check(substitution.CleanupStatus == "native-node-restored" && Find(PlayerLoop.Current, typeof(Fixed.PhysicsFixedUpdate)).updateFunction == (IntPtr)37);

        PlayerLoop.Current = Tree(); substitution = new PhysicsBoundarySubstitution(() => { }); substitution.Start();
        current = PlayerLoop.Current; fixedNode = Find(current, typeof(Fixed)); fixedNode.subSystemList[1].updateDelegate += () => { }; PlayerLoop.Current = Replace(current, typeof(Fixed), fixedNode);
        substitution.Audit(); substitution.Dispose(); Check(substitution.CleanupStatus == "cleanup-error");
        Console.WriteLine("PlayerLoop: " + checks + " assertions passed.");
    }
}
