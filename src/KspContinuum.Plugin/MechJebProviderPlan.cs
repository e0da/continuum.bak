using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class MechJebProviderPlan : MonoBehaviour
    {
        const string Owner = "continuum.mechjeb-provider-plan";
        const string AssemblySha = "6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929";
        const string IlSha = "4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b";
        static readonly Type[] Types = { typeof(ModuleLiftingSurface), typeof(ModuleReactionWheel), typeof(ModuleEngines), typeof(ModuleResourceIntake), typeof(ModuleParachute), typeof(ModuleControlSurface), typeof(ModuleGimbal), typeof(ModuleRCS), typeof(ITorqueProvider) };
        static readonly int[] ExpectedSites = { 1, 1, 2, 2, 2, 2, 2, 1, 2 };
        static MechJebProviderPlan active;
        [ThreadStatic] static Cursor cursor;
        [ThreadStatic] static int invocationDepth;
        sealed class Cursor { internal MechJebProviderPlan Owner; internal OrderedProviderPlan Plan; internal TypedEntry[][] Entries; internal OrderedProviderRow Row; internal int PartIndex; }
        public sealed class TypedEntry
        {
            public ModuleLiftingSurface Lifting; public ModuleReactionWheel Wheel; public ModuleEngines Engine;
            public ModuleResourceIntake Intake; public ModuleParachute Parachute; public ModuleControlSurface Surface;
            public ModuleGimbal Gimbal; public ModuleRCS Rcs; public ITorqueProvider Torque;
            internal TypedEntry(object member)
            {
                Lifting = member as ModuleLiftingSurface; Wheel = member as ModuleReactionWheel; Engine = member as ModuleEngines;
                Intake = member as ModuleResourceIntake; Parachute = member as ModuleParachute; Surface = member as ModuleControlSurface;
                Gimbal = member as ModuleGimbal; Rcs = member as ModuleRCS; Torque = member as ITorqueProvider;
            }
        }
        [ThreadStatic] public static TypedEntry CurrentEntry;
        static readonly string[] TypedFields = { "Lifting", "Wheel", "Engine", "Intake", "Parachute", "Surface", "Gimbal", "Rcs", "Torque" };
        public struct Invocation { internal object Previous; internal TypedEntry PreviousEntry; internal long Started; internal bool Owned; internal int PreviousDepth; }
        [Serializable] public sealed class Report
        {
            public string schema = "continuum-mechjeb-provider-plan/v1", runId, vesselId, status, reason, cleanupStatus;
            public string assemblySha256, assemblyMvid, methodIlSha256;
            public int processId, methodToken, partRetrievalSites, moduleRetrievalSites, classificationSites, preservedInstructions, originalInstructions, initialParts, initialModules, lastParts, lastModules;
            public long clockFrequency, calls, errors, generations, rebuilds, structuralMismatches, partRetrievals, moduleRetrievals, cachedModules, fallbackModules, validationTicks, rebuildTicks, analysisInclusiveTicks;
            public bool hooksRemoved, ilPreservationVerified, timingEnabled;
            public string scope = "Original AnalyzeParts arithmetic, branches, isEnabled reads and provider/extension calls retained. Timings are inclusive synchronous elapsed; validation/rebuild overlap analysis. Catalog contains ordered references and immutable CLR classification only, not vessel values. Mid-invocation mutation uses original live retrieval and uncached classification without replay.";
        }
        readonly Report report = new Report();
        readonly Cursor primaryCursor = new Cursor();
        Harmony harmony; MethodInfo method; OrderedProviderPlan plan; TypedEntry[][] entries;
        bool dirty, finished;
        public void Awake()
        {
            if (!Environment.GetCommandLineArgs().Contains("--continuum-mechjeb-provider-plan")) return;
            if (active != null) return;
            active = this;
            try
            {
                const string runPrefix = "--continuum-research-run=";
                string[] runs = Environment.GetCommandLineArgs().Where(x => x.StartsWith(runPrefix, StringComparison.Ordinal)).ToArray();
                Require(runs.Length == 1 && runs[0].Length > runPrefix.Length, "One run nonce required."); report.runId = runs[0].Substring(runPrefix.Length);
                Require(report.runId.All(c => char.IsLetterOrDigit(c) || c == '-'), "Unsafe run nonce.");
                report.processId = Process.GetCurrentProcess().Id; report.clockFrequency = Stopwatch.Frequency;
                report.timingEnabled = Environment.GetCommandLineArgs().Contains("--continuum-mechjeb-provider-plan-timing");
                StartupPinnedGameIdentity.Require(typeof(Vessel).Assembly);
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "MechJeb2");
                report.assemblySha256 = Hash(File.ReadAllBytes(assembly.Location)); Require(report.assemblySha256 == AssemblySha, "Installed MechJeb assembly differs.");
                Type state = assembly.GetType("MuMech.VesselState", true); method = state.GetMethod("AnalyzeParts", BindingFlags.NonPublic | BindingFlags.Instance);
                Require(method != null && method.MetadataToken == 100665304 && method.Module.ModuleVersionId == new Guid("30fbe3c4-e77e-4abc-895b-a635d5924a08") && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(Vessel) && method.ReturnType == typeof(void), "AnalyzeParts signature differs.");
                report.methodIlSha256 = Hash(method.GetMethodBody().GetILAsByteArray()); Require(report.methodIlSha256 == IlSha, "AnalyzeParts IL differs.");
                report.assemblyMvid = method.Module.ModuleVersionId.ToString("D"); report.methodToken = method.MetadataToken;
                Require(Harmony.GetPatchInfo(method) == null, "Foreign AnalyzeParts modification.");
                harmony = new Harmony(Owner); harmony.Patch(method, prefix: new HarmonyMethod(typeof(MechJebProviderPlan), nameof(Begin)), transpiler: new HarmonyMethod(typeof(MechJebProviderPlan), nameof(Rewrite)), finalizer: new HarmonyMethod(typeof(MechJebProviderPlan), nameof(End)));
                report.status = "installed";
            }
            catch (Exception error) { report.status = "rejected"; report.reason = error.GetType().Name + ": " + error.Message; Cleanup(); UnityEngine.Debug.LogError("[Continuum provider plan] " + report.reason); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        void Rebuild(Vessel current)
        {
            long start = report.timingEnabled ? Stopwatch.GetTimestamp() : 0;
            try
            {
                Require(current.parts.Count <= 4096, "Part bound exceeded."); var rows = new OrderedProviderRow[current.parts.Count]; var nextEntries = new TypedEntry[rows.Length][]; int total = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    Part part = current.parts[i]; var modules = new object[part.Modules.Count]; total = checked(total + modules.Length); Require(total <= 32768, "Module bound exceeded.");
                    nextEntries[i] = new TypedEntry[modules.Length];
                    for (int j = 0; j < modules.Length; j++) { modules[j] = part.Modules[j]; nextEntries[i][j] = new TypedEntry(modules[j]); }
                    rows[i] = new OrderedProviderRow(part, part.Modules, modules);
                }
                plan = new OrderedProviderPlan(++report.generations, current, current.parts, rows); entries = nextEntries; report.vesselId = current.id.ToString(); dirty = false; report.rebuilds++;
                if (report.rebuilds == 1) { report.initialParts = rows.Length; report.initialModules = total; }
                report.lastParts = rows.Length; report.lastModules = total;
            }
            finally { if (report.timingEnabled) report.rebuildTicks += Stopwatch.GetTimestamp() - start; }
        }
        static void Begin(Vessel __0, out Invocation __state)
        {
            __state = EnterContext();
            var owner = active; if (owner == null || owner.finished || owner.report.status != "installed" || __0 == null || !ReferenceEquals(__0, FlightGlobals.ActiveVessel)) return;
            __state.Owned = true; __state.Started = owner.report.timingEnabled ? Stopwatch.GetTimestamp() : 0; owner.report.calls++;
            try
            {
                if (owner.dirty || owner.plan == null || !owner.plan.Matches(__0, __0.parts, __0.parts.Count)) owner.Rebuild(__0);
                cursor = BorrowCursor(owner, __state.PreviousDepth);
                cursor.Owner = owner; cursor.Plan = owner.plan; cursor.Entries = owner.entries; cursor.Row = null;
            }
            catch (Exception error) { owner.dirty = true; owner.plan = null; owner.report.errors++; owner.report.reason = "catalog-fallback: " + error.GetType().Name; }
        }
        static Invocation EnterContext()
        {
            var state = new Invocation { Previous = cursor, PreviousEntry = CurrentEntry, PreviousDepth = invocationDepth };
            invocationDepth++; cursor = null; CurrentEntry = null; return state;
        }
        static Cursor BorrowCursor(MechJebProviderPlan owner, int previousDepth)
        { return previousDepth == 0 ? owner.primaryCursor : new Cursor(); }
        static Exception End(Invocation __state, Exception __exception)
        {
            if (__state.Owned && active != null) { if (active.report.timingEnabled) active.report.analysisInclusiveTicks += Stopwatch.GetTimestamp() - __state.Started; if (__exception != null) active.report.errors++; }
            cursor = (Cursor)__state.Previous; CurrentEntry = __state.PreviousEntry; invocationDepth = __state.PreviousDepth; return __exception;
        }
        static void Mismatch(Cursor current) { current.Owner.dirty = true; current.Owner.report.structuralMismatches++; current.Row = null; }
        public static Part GetPart(List<Part> parts, int index)
        {
            Part result = parts[index]; Cursor current = cursor; if (current == null) return result;
            var owner = current.Owner; long start = owner.report.timingEnabled ? Stopwatch.GetTimestamp() : 0; owner.report.partRetrievals++;
            current.PartIndex = index; current.Row = current.Plan.At(index);
            if (!ReferenceEquals(parts, current.Plan.Collection) || current.Row == null || !current.Row.Matches(result, result.Modules, result.Modules.Count)) Mismatch(current);
            if (owner.report.timingEnabled) owner.report.validationTicks += Stopwatch.GetTimestamp() - start; return result;
        }
        public static PartModule GetModule(PartModuleList modules, int index)
        {
            PartModule result = modules[index]; Cursor current = cursor;
            if (current == null) { CurrentEntry = new TypedEntry(result); return result; }
            long start = current.Owner.report.timingEnabled ? Stopwatch.GetTimestamp() : 0; current.Owner.report.moduleRetrievals++;
            bool cached = current.Row != null && ReferenceEquals(modules, current.Row.Collection) && modules.Count == current.Row.Count && current.Row.MatchesMember(index, result);
            if (cached) { CurrentEntry = current.Entries[current.PartIndex][index]; current.Owner.report.cachedModules++; }
            else { Mismatch(current); CurrentEntry = new TypedEntry(result); current.Owner.report.fallbackModules++; }
            if (current.Owner.report.timingEnabled) current.Owner.report.validationTicks += Stopwatch.GetTimestamp() - start; return result;
        }
        static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source)
        {
            var body = source.ToList(); var output = new List<CodeInstruction>(); var counts = new int[Types.Length]; int parts = 0, modules = 0, preserved = 0;
            MethodInfo getPart = typeof(List<Part>).GetProperty("Item").GetGetMethod();
            MethodInfo getModule = typeof(PartModuleList).GetProperty("Item", new[] { typeof(int) }).GetGetMethod();
            FieldInfo entry = typeof(MechJebProviderPlan).GetField(nameof(CurrentEntry));
            for (int i = 0; i < body.Count; i++)
            {
                var original = body[i];
                var instruction = new CodeInstruction(original);
                if (Equals(instruction.operand, getPart) || Equals(instruction.operand, getModule))
                {
                    Require(instruction.opcode == OpCodes.Callvirt && instruction.blocks.Count == 0, "Retrieval seam shape differs."); bool part = Equals(instruction.operand, getPart);
                    instruction.opcode = OpCodes.Call; instruction.operand = typeof(MechJebProviderPlan).GetMethod(part ? nameof(GetPart) : nameof(GetModule)); if (part) parts++; else modules++;
                }
                else if (i + 1 < body.Count && body[i + 1].opcode == OpCodes.Isinst)
                {
                    int type = Array.IndexOf(Types, body[i + 1].operand as Type);
                    var local = instruction.operand as LocalVariableInfo;
                    var builder = instruction.operand as LocalBuilder;
                    int index = local != null ? local.LocalIndex : builder != null ? builder.LocalIndex : instruction.operand is byte ? (byte)instruction.operand : instruction.operand is int ? (int)instruction.operand : -1;
                    Require(type >= 0 && instruction.opcode == OpCodes.Ldloc_S && index == 13 && instruction.blocks.Count == 0 && body[i + 1].blocks.Count == 0 && body[i + 1].labels.Count == 0, "Unexpected classification pair.");
                    instruction.opcode = OpCodes.Ldsfld; instruction.operand = entry; output.Add(instruction);
                    instruction = new CodeInstruction(body[++i]); instruction.opcode = OpCodes.Ldfld; instruction.operand = typeof(TypedEntry).GetField(TypedFields[type]); counts[type]++;
                }
                else if (instruction.opcode == OpCodes.Isinst) throw new InvalidOperationException("Unpaired classification seam.");
                else preserved++;
                output.Add(instruction);
            }
            Require(parts == 1 && modules == 1 && counts.SequenceEqual(ExpectedSites), "Expected ordered dispatch sites differ.");
            active.report.partRetrievalSites = parts; active.report.moduleRetrievalSites = modules; active.report.classificationSites = counts.Sum(); active.report.originalInstructions = body.Count; active.report.preservedInstructions = preserved;
            Require(preserved + parts + modules + 2 * counts.Sum() == body.Count && output.Count == body.Count, "Instruction preservation accounting differs."); active.report.ilPreservationVerified = true; return output;
        }
        void Cleanup()
        {
            if (harmony != null) harmony.UnpatchAll(Owner);
            report.hooksRemoved = method == null || Harmony.GetPatchInfo(method) == null || !Harmony.GetPatchInfo(method).Owners.Contains(Owner);
            report.cleanupStatus = report.hooksRemoved ? "removed-owned-hooks" : "owned-hooks-remain"; plan = null; entries = null;
            primaryCursor.Owner = null; primaryCursor.Plan = null; primaryCursor.Entries = null; primaryCursor.Row = null;
        }
        public static void FinishForFlight()
        {
            var owner = active; if (owner == null || owner.finished) return; owner.finished = true;
            try { owner.Cleanup(); if (owner.report.status == "installed") owner.report.status = owner.report.calls > 0 && owner.report.cachedModules > 0 && owner.report.errors == 0 && owner.report.hooksRemoved ? "completed-experimental" : "invalid";
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData"); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "mechjeb-provider-plan-" + owner.report.runId + ".json"), ReportJson.Encode(owner.report));
            }
            catch (Exception error) { UnityEngine.Debug.LogError("[Continuum provider plan] export failed: " + error); }
            finally { active = null; }
        }
        public void OnDestroy() { if (ReferenceEquals(active, this)) FinishForFlight(); }
        public void OnApplicationQuit() { if (ReferenceEquals(active, this)) FinishForFlight(); }
    }
}
