using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace KspContinuum
{
    internal sealed class FeedInputAttribution : IDisposable
    {
        const string Owner = "continuum.fixed-callback-attribution.input";
        static readonly string[] Names = { "OnPreAutopilotUpdate", "OnAutopilotUpdate", "OnPostAutopilotUpdate", "OnFlyByWire" };
        static FeedInputAttribution active;
        [ThreadStatic] static bool ownedFeed;
        [ThreadStatic] static int feedDepth, chainDepth, currentChain;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Func<bool> inFixed;
        readonly FieldInfo[] fields = new FieldInfo[4];
        readonly FlightInputCallback[] originals = new FlightInputCallback[4];
        readonly Dictionary<MethodBase, int>[] slots = new Dictionary<MethodBase, int>[4];
        readonly Dictionary<MethodBase, HashSet<object>>[] registeredTargets = new Dictionary<MethodBase, HashSet<object>>[4];
        readonly CallbackAttributionAccumulator[] timers = new CallbackAttributionAccumulator[4];
        readonly int[][] depths = new int[4][];
        readonly HashSet<MethodInfo> methods = new HashSet<MethodInfo>();
        readonly Dictionary<object, int> targets = new Dictionary<object, int>(new ReferenceComparer());
        readonly Dictionary<Assembly, string> assemblyHashes = new Dictionary<Assembly, string>();
        Vessel vessel; MethodInfo feed; bool disposed;
        public readonly FeedInputAttributionReport Report = new FeedInputAttributionReport { patchOwner = Owner };
        public struct FeedState { public bool Previous; public int Depth; }
        public struct HandlerState { public long Started; public int Chain, Slot; public bool Outermost; }
        sealed class ReferenceComparer : IEqualityComparer<object>
        { public new bool Equals(object a, object b) => ReferenceEquals(a, b); public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value); }

        public FeedInputAttribution(Func<bool> inFixed) { this.inFixed = inFixed; }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static string Digest(byte[] bytes)
        { using (SHA256 sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2"))); }
        string AssemblyDigest(Assembly assembly)
        {
            string digest;
            if (assemblyHashes.TryGetValue(assembly, out digest)) return digest;
            Require(!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location), "Delegate handler assembly has no file identity.");
            string path = Path.GetFullPath(assembly.Location); var before = new FileInfo(path); long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
            digest = Digest(File.ReadAllBytes(path)); var after = new FileInfo(path);
            Require(after.Length == length && after.LastWriteTimeUtc == modified, "Delegate assembly changed while binding.");
            assemblyHashes.Add(assembly, digest); return digest;
        }
        InputDelegateMember[] Snapshot(FlightInputCallback callback)
        {
            Require(callback != null, "Null input delegate unsupported.");
            Delegate[] entries = callback.GetInvocationList(); Require(entries.Length <= 256, "Input subscriber bound exceeded.");
            var rows = new InputDelegateMember[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                MethodInfo method = entries[i].Method; object target = entries[i].Target;
                Require(method.ReturnType == typeof(void) && !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null
                    && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(FlightCtrlState), "Input handler signature/body unsupported.");
                int id = 0;
                if (target != null && !targets.TryGetValue(target, out id)) { id = targets.Count + 1; targets.Add(target, id); }
                var unityTarget = target as UnityEngine.Object;
                rows[i] = new InputDelegateMember { order = i, targetId = id, targetUnityId = unityTarget == null ? 0 : unityTarget.GetInstanceID(),
                    targetType = target == null ? "static" : target.GetType().FullName, assembly = method.Module.Assembly.GetName().Name,
                    assemblyMvid = method.Module.ModuleVersionId.ToString("D"), assemblySha256 = AssemblyDigest(method.Module.Assembly),
                    declaringType = method.DeclaringType.FullName, method = method.Name, metadataToken = method.MetadataToken,
                    ilSha256 = Digest(method.GetMethodBody().GetILAsByteArray()) };
            }
            return rows;
        }
        public void Start()
        {
            Require(active == null, "Input attribution already owned."); active = this;
            vessel = FlightGlobals.ActiveVessel; Require(vessel != null && vessel.loaded && !vessel.packed, "Loaded active vessel required.");
            StartupPinnedGameIdentity.Require(typeof(Vessel).Assembly);
            feed = AccessTools.DeclaredMethod(typeof(Vessel), "FeedInputFeed", Type.EmptyTypes);
            Require(feed != null && feed.MetadataToken == 100677715 && feed.Module.ModuleVersionId == new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf")
                && feed.ReturnType == typeof(void) && !feed.IsStatic, "Pinned input callsite differs.");
            Require(Harmony.GetPatchInfo(feed) == null, "Foreign FeedInputFeed modification.");
            Report.seamIdentity = feed.Module.ModuleVersionId.ToString("D") + ":" + feed.MetadataToken + ":" + Digest(feed.GetMethodBody().GetILAsByteArray());
            Report.vesselId = vessel.id.ToString(); Report.snapshotStartTicks = Stopwatch.GetTimestamp(); Report.chains = new InputDelegateChain[4];
            for (int c = 0; c < 4; c++)
            {
                fields[c] = AccessTools.DeclaredField(typeof(Vessel), Names[c]); Require(fields[c] != null && fields[c].FieldType == typeof(FlightInputCallback), "Input field differs.");
                originals[c] = (FlightInputCallback)fields[c].GetValue(vessel);
                Report.chains[c] = new InputDelegateChain { name = Names[c], members = Snapshot(originals[c]) };
                slots[c] = new Dictionary<MethodBase, int>(); registeredTargets[c] = new Dictionary<MethodBase, HashSet<object>>();
                timers[c] = new CallbackAttributionAccumulator(Stopwatch.Frequency);
                foreach (Delegate entry in originals[c].GetInvocationList())
                {
                    MethodInfo method = entry.Method;
                    HashSet<object> boundTargets;
                    if (!registeredTargets[c].TryGetValue(method, out boundTargets)) { boundTargets = new HashSet<object>(new ReferenceComparer()); registeredTargets[c].Add(method, boundTargets); }
                    boundTargets.Add(entry.Target);
                    if (slots[c].ContainsKey(method)) continue;
                    Require(Harmony.GetPatchInfo(method) == null, "Foreign input-handler modifier: " + method);
                    int slot = timers[c].Register(new CallbackAttributionRow { category = "registered-input-handler", assembly = method.Module.Assembly.GetName().Name,
                        assemblyVersion = method.Module.Assembly.GetName().Version.ToString(), assemblyMvid = method.Module.ModuleVersionId.ToString("D"),
                        declaringType = method.DeclaringType.FullName, method = method.Name, metadataToken = method.MetadataToken });
                    slots[c].Add(method, slot); methods.Add(method);
                }
                depths[c] = new int[slots[c].Count];
                Report.chains[c].registeredMethods = slots[c].Count;
            }
            Require(methods.Count <= 1024, "Input handler method bound exceeded.");
            foreach (MethodInfo method in methods) harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(FeedInputAttribution), nameof(HandlerBegin)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(FeedInputAttribution), nameof(HandlerEnd)) { priority = Priority.Last });
            harmony.Patch(feed, prefix: new HarmonyMethod(typeof(FeedInputAttribution), nameof(FeedBegin)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(FeedInputAttribution), nameof(Calls)),
                finalizer: new HarmonyMethod(typeof(FeedInputAttribution), nameof(FeedEnd)) { priority = Priority.Last });
            Report.status = "installed"; Report.cleanupStatus = "installed";
        }
        static IEnumerable<CodeInstruction> Calls(IEnumerable<CodeInstruction> source)
        {
            var body = source.ToList(); var counts = new int[4]; int pending = -1;
            MethodInfo invoke = AccessTools.DeclaredMethod(typeof(FlightInputCallback), "Invoke", new[] { typeof(FlightCtrlState) });
            MethodInfo observe = AccessTools.DeclaredMethod(typeof(FeedInputAttribution), nameof(Invoke));
            for (int i = 0; i < body.Count; i++)
            {
                FieldInfo field = body[i].operand as FieldInfo; int chain = field == null ? -1 : Array.IndexOf(active.fields, field);
                if (chain >= 0) { Require(pending < 0 && body[i].opcode == OpCodes.Ldfld, "Input load shape differs."); pending = chain; }
                if (!Equals(body[i].operand, invoke)) continue;
                Require(pending >= 0 && body[i].opcode == OpCodes.Callvirt && body[i].blocks.Count == 0, "Input Invoke callsite differs.");
                var index = new CodeInstruction(OpCodes.Ldc_I4, pending); index.labels.AddRange(body[i].labels); body[i].labels.Clear();
                body.Insert(i, index); i++; body[i].opcode = OpCodes.Call; body[i].operand = observe; counts[pending]++; pending = -1;
            }
            Require(pending < 0 && counts.All(count => count == 1), "Expected exactly four original multicast callsites."); return body;
        }
        static void FeedBegin(Vessel __instance, out FeedState __state)
        {
            __state = new FeedState { Previous = ownedFeed, Depth = feedDepth };
            ownedFeed = active != null && active.inFixed() && ReferenceEquals(__instance, active.vessel);
            if (ownedFeed && ++feedDepth > 64) { active.Report.contextErrors++; ownedFeed = false; }
        }
        static Exception FeedEnd(FeedState __state, Exception __exception)
        { ownedFeed = __state.Previous; feedDepth = __state.Depth; return __exception; }
        static void Invoke(FlightInputCallback callback, FlightCtrlState state, int chain)
        {
            FeedInputAttribution owner = active;
            if (owner == null || !ownedFeed) { callback(state); return; }
            if (!ReferenceEquals(callback, owner.originals[chain])) owner.Report.driftErrors++;
            if (chainDepth >= 64) { owner.Report.contextErrors++; callback(state); return; }
            int previous = currentChain; currentChain = chain; chainDepth++; long start = Stopwatch.GetTimestamp(); bool failed = true;
            try { callback(state); failed = false; }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - start; InputDelegateChain row = owner.Report.chains[chain]; row.calls++;
                row.inclusiveTicks += elapsed; row.maximumTicks = Math.Max(row.maximumTicks, elapsed); if (failed) row.errors++;
                chainDepth--; currentChain = previous;
            }
        }
        static void HandlerBegin(object __instance, MethodBase __originalMethod, ref HandlerState __state)
        {
            if (active == null || !ownedFeed || chainDepth == 0 || !active.inFixed()) return;
            int slot; if (!active.slots[currentChain].TryGetValue(__originalMethod, out slot)) return;
            if (!active.registeredTargets[currentChain][__originalMethod].Contains(__instance)) return;
            __state.Chain = currentChain; __state.Slot = slot; __state.Outermost = active.depths[currentChain][slot]++ == 0; __state.Started = Stopwatch.GetTimestamp();
        }
        static Exception HandlerEnd(ref HandlerState __state, Exception __exception)
        {
            if (__state.Started == 0) return __exception;
            long elapsed = Stopwatch.GetTimestamp() - __state.Started; __state.Started = 0;
            try
            {
                if (active == null || elapsed < 0 || active.depths[__state.Chain][__state.Slot]-- <= 0) { if (active != null) active.Report.contextErrors++; }
                else active.timers[__state.Chain].Record(__state.Slot, elapsed, __state.Outermost);
            }
            catch { if (active != null) active.Report.contextErrors++; }
            return __exception;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try
            {
                assemblyHashes.Clear();
                if (Report.chains != null)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        if (Report.chains[c] == null) continue;
                        FlightInputCallback end = vessel == null ? null : (FlightInputCallback)fields[c].GetValue(vessel);
                        Report.chains[c].snapshotUnchanged = ReferenceEquals(end, originals[c]);
                        if (!Report.chains[c].snapshotUnchanged) Report.driftErrors++;
                        Report.chains[c].endMembers = end == null ? new InputDelegateMember[0] : Snapshot(end);
                        InputDelegateMember[] before = Report.chains[c].members, after = Report.chains[c].endMembers;
                        if (before.Length != after.Length || before.Where((row, i) => i < after.Length &&
                            (row.targetId != after[i].targetId || row.metadataToken != after[i].metadataToken || row.assemblyMvid != after[i].assemblyMvid
                             || row.assemblySha256 != after[i].assemblySha256 || row.ilSha256 != after[i].ilSha256)).Any()) Report.driftErrors++;
                        if (timers[c] != null)
                        {
                            Report.chains[c].handlers = timers[c].Finish(Report.chains[c].inclusiveTicks, includeZeroCalls: true);
                            Report.chains[c].observedMethods = Report.chains[c].handlers.Count(row => row.calls > 0);
                            Report.chains[c].unobservedMethods = Report.chains[c].registeredMethods - Report.chains[c].observedMethods;
                        }
                    }
                }
                Report.snapshotEndTicks = Stopwatch.GetTimestamp();
                if (ownedFeed || chainDepth != 0 || (vessel != null && !ReferenceEquals(vessel, FlightGlobals.ActiveVessel))) Report.contextErrors++;
            }
            catch { Report.contextErrors++; }
            finally
            {
                try
                {
                    harmony.UnpatchAll(Owner);
                    bool remains = methods.Concat(feed == null ? new MethodInfo[0] : new[] { feed }).Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) == true);
                    Report.cleanupStatus = remains ? "cleanup-error" : "removed-owned-patches";
                }
                catch { Report.cleanupStatus = "cleanup-error"; }
                Report.status = Report.cleanupStatus == "removed-owned-patches" && Report.driftErrors == 0 && Report.contextErrors == 0
                    && Report.chains != null && Report.chains.Where((row, c) => row == null || row.calls == 0 || row.errors != 0 || !row.snapshotUnchanged
                        || row.handlers == null || row.handlers.Length != slots[c].Count).Any() == false ? "observed" : "invalid";
                if (ReferenceEquals(active, this)) active = null;
            }
        }
    }
}
