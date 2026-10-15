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
    public sealed class StartupPreparedBindings : MonoBehaviour
    {
        const string Owner = "continuum.startup-prepared-bindings";
        const BindingFlags Enumeration = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        const BindingFlags Lookup = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static StartupPreparedBindings active;
        [ThreadStatic] static State current;
        readonly Dictionary<Type, Plan> plans = new Dictionary<Type, Plan>();
        readonly Dictionary<MemberInfo, Attributes> attributes = new Dictionary<MemberInfo, Attributes>();
        Harmony harmony; ConstructorInfo constructor, attributeConstructor, fieldItemConstructor; MethodInfo fieldNameMethod; int mainThread; bool exported;
        Report report;

        [Serializable] public sealed class Report
        {
            public string schema = "continuum-startup-prepared-bindings/v1", runId, status, cleanupStatus, failure;
            public string scope = "Startup-only ordered metadata binding. Original ReadFieldList and FieldItem constructors, live hosts, conversions, persistence links, callbacks and execution order remain original. Arrays and Persistent objects are fresh; only metadata discovery is reused. Timing, when selected, is intrusive inclusive wall time, not exclusive CPU.";
            public int processId, constructorToken = 100707352;
            public string gameMvid = "10657063-2fc3-43a7-84fa-d39e75e877bf";
            public bool timingRequested, installed, hooksRemoved;
            public long clockFrequency, fieldEnumerationsBuilt, fieldEnumerationsReused, fieldLookupsBuilt, fieldLookupsReused;
            public long attributePlansBuilt, attributePlansReused, freshArrays, freshAttributes, fallbacks, errors;
            public Row[] types;
        }
        [Serializable] public sealed class Row
        {
            public string type, assembly, assemblyMvid;
            public long constructorCalls, constructorErrors, constructorTicks, maximumConstructorTicks, discoveryTicks;
            public long fieldCount, fieldEnumerationsBuilt, fieldEnumerationsReused, fieldLookupsBuilt, fieldLookupsReused;
            public long attributePlansBuilt, attributePlansReused, fallbacks;
        }
        sealed class Plan
        {
            public Type type; public Row row; public FieldInfo[] fields;
            public readonly Dictionary<string, FieldInfo> bindings = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        }
        sealed class Attributes
        {
            public Type arrayType; public AttributeValue[] values;
        }
        struct AttributeValue
        {
            public string name, collectionIndex; public bool isPersistant, link; public int pass; public PersistentRelation relationship;
            public AttributeValue(Persistent value)
            { name = value.name; collectionIndex = value.collectionIndex; isPersistant = value.isPersistant; link = value.link; pass = value.pass; relationship = value.relationship; }
            public Persistent Fresh()
            { return new Persistent { name = name, collectionIndex = collectionIndex, isPersistant = isPersistant, link = link, pass = pass, relationship = relationship }; }
        }
        public sealed class State
        {
            internal State previous; internal object plan; internal long started; internal bool attributesEligible;
        }

        public void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!args.Contains("--continuum-startup-prepared-bindings")) return;
            DontDestroyOnLoad(gameObject);
            report = new Report { processId = Process.GetCurrentProcess().Id, status = "initializing", timingRequested = args.Contains("--continuum-startup-bindings-timing"), clockFrequency = Stopwatch.Frequency };
            try
            {
                if (active != null) throw new InvalidOperationException("Prepared bindings already owned.");
                StartupPinnedGameIdentity.Require(typeof(ConfigNode).Assembly);
                string[] identities = args.Where(value => value.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                if (identities.Length != 1 || identities[0].Length == "--continuum-research-run=".Length) throw new InvalidOperationException("One run identity required.");
                report.runId = identities[0].Substring("--continuum-research-run=".Length);
                Type list = typeof(ConfigNode).GetNestedType("ReadFieldList", BindingFlags.NonPublic);
                constructor = list?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(object) }, null);
                if (constructor == null || constructor.MetadataToken != 100707352 || Harmony.GetPatchInfo(constructor)?.Owners.Count > 0)
                    throw new InvalidOperationException("Pinned persistence constructor absent or modified.");
                ConstructorInfo persistent = typeof(Persistent).GetConstructor(Type.EmptyTypes);
                if (persistent == null || typeof(Persistent).Assembly != typeof(ConfigNode).Assembly || Harmony.GetPatchInfo(persistent)?.Owners.Count > 0)
                    throw new InvalidOperationException("Original Persistent construction is modified.");
                RequireAttributeShape();
                attributeConstructor = persistent;
                Type item = list.GetNestedType("FieldItem", BindingFlags.Public | BindingFlags.NonPublic);
                fieldItemConstructor = item?.GetConstructor(new[] { typeof(FieldInfo), typeof(Persistent), typeof(object) });
                fieldNameMethod = typeof(ConfigNode).GetMethod("RetrieveFieldName", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(MemberInfo), typeof(Persistent) }, null);
                if (fieldItemConstructor == null || fieldItemConstructor.MetadataToken != 100712181 || fieldNameMethod == null)
                    throw new InvalidOperationException("Pinned metadata-building helpers absent.");
                active = this; mainThread = Thread.CurrentThread.ManagedThreadId; harmony = new Harmony(Owner);
                harmony.Patch(constructor, prefix: new HarmonyMethod(typeof(StartupPreparedBindings), nameof(Begin)),
                    finalizer: new HarmonyMethod(typeof(StartupPreparedBindings), nameof(End)),
                    transpiler: new HarmonyMethod(typeof(StartupPreparedBindings), nameof(Bindings)));
                report.installed = true; report.status = "installed";
                UnityEngine.Debug.Log("[Continuum startup prepared bindings] status=installed timing=" + report.timingRequested + " token=" + report.constructorToken);
            }
            catch (Exception error) { report.status = "rejected"; report.failure = error.ToString(); Remove(); Export("rejected"); Application.Quit(2); }
        }
        static void RequireAttributeShape()
        {
            var expected = new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                { "name", typeof(string) }, { "isPersistant", typeof(bool) }, { "pass", typeof(int) },
                { "collectionIndex", typeof(string) }, { "relationship", typeof(PersistentRelation) }, { "link", typeof(bool) }
            };
            int count = 0;
            for (Type type = typeof(Persistent); type != typeof(object); type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Type value;
                    if (type != typeof(Persistent) || !expected.TryGetValue(field.Name, out value) || field.FieldType != value || field.IsInitOnly)
                        throw new InvalidOperationException("Persistent instance-state shape differs: " + type.FullName + "." + field.Name);
                    count++;
                }
            }
            if (count != expected.Count || typeof(Persistent).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Length != 0)
                throw new InvalidOperationException("Persistent fields or declared properties differ.");
        }
        static IEnumerable<CodeInstruction> Bindings(IEnumerable<CodeInstruction> source)
        {
            var body = source.ToList(); int fields = 0, custom = 0, field = 0;
            MethodInfo enumerate = typeof(Type).GetMethod("GetFields", new[] { typeof(BindingFlags) });
            MethodInfo attribute = typeof(MemberInfo).GetMethod("GetCustomAttributes", new[] { typeof(Type), typeof(bool) });
            MethodInfo lookup = typeof(Type).GetMethod("GetField", new[] { typeof(string), typeof(BindingFlags) });
            foreach (CodeInstruction instruction in body)
            {
                if (instruction.opcode != OpCodes.Callvirt) continue;
                string replacement = null;
                if (Equals(instruction.operand, enumerate)) { replacement = nameof(Fields); fields++; }
                else if (Equals(instruction.operand, attribute)) { replacement = nameof(Custom); custom++; }
                else if (Equals(instruction.operand, lookup)) { replacement = nameof(Field); field++; }
                if (replacement != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupPreparedBindings), replacement); }
            }
            if (fields != 1 || custom != 1 || field != 1) throw new InvalidOperationException("Pinned persistence reflection call shape differs.");
            return body;
        }
        Plan GetPlan(Type type)
        {
            Plan plan;
            if (!plans.TryGetValue(type, out plan))
            {
                if (plans.Count >= 1024) return null;
                plan = new Plan { type = type, row = new Row { type = type.FullName, assembly = type.Assembly.GetName().Name, assemblyMvid = type.Module.ModuleVersionId.ToString("D") } };
                plans.Add(type, plan);
            }
            return plan;
        }
        static void Begin(object __0, out State __state)
        {
            __state = null;
            if (active == null || Thread.CurrentThread.ManagedThreadId != active.mainThread || __0 == null) return;
            Type type = __0.GetType();
            Plan plan = type.Assembly.IsDynamic || type.ContainsGenericParameters ? null : active.GetPlan(type);
            if (plan == null) active.report.fallbacks++;
            __state = new State { previous = current, plan = plan, started = active.report.timingRequested ? Stopwatch.GetTimestamp() : 0,
                attributesEligible = !(Harmony.GetPatchInfo(active.attributeConstructor)?.Owners.Count > 0)
                    && !(Harmony.GetPatchInfo(active.fieldItemConstructor)?.Owners.Count > 0)
                    && !(Harmony.GetPatchInfo(active.fieldNameMethod)?.Owners.Count > 0) };
            if (plan != null) plan.row.constructorCalls++; current = __state;
        }
        static Exception End(State __state, Exception __exception)
        {
            if (__state == null) return __exception;
            Plan plan = (Plan)__state.plan;
            if (__exception != null) { active.report.errors++; if (plan != null) plan.row.constructorErrors++; }
            if (__state.started != 0 && plan != null) { long ticks = Stopwatch.GetTimestamp() - __state.started; plan.row.constructorTicks += ticks; plan.row.maximumConstructorTicks = Math.Max(plan.row.maximumConstructorTicks, ticks); }
            current = __state.previous; return __exception;
        }
        static Plan Current(Type type = null)
        {
            Plan plan = current == null ? null : (Plan)current.plan;
            return active != null && plan != null && (type == null || ReferenceEquals(type, plan.type)) ? plan : null;
        }
        static long Clock() { return active != null && active.report.timingRequested ? Stopwatch.GetTimestamp() : 0; }
        static void Cost(Plan plan, long started) { if (plan != null && started != 0) plan.row.discoveryTicks += Stopwatch.GetTimestamp() - started; }
        static void Fallback(Plan plan) { if (active != null) active.report.fallbacks++; if (plan != null) plan.row.fallbacks++; }
        static FieldInfo[] Fields(Type type, BindingFlags flags)
        {
            Plan plan = Current(type); long started = Clock();
            try
            {
                if (plan == null || flags != Enumeration) { Fallback(plan); return type.GetFields(flags); }
                if (plan.fields == null) { plan.fields = type.GetFields(flags); plan.row.fieldCount = plan.fields.Length; plan.row.fieldEnumerationsBuilt++; active.report.fieldEnumerationsBuilt++; }
                else { plan.row.fieldEnumerationsReused++; active.report.fieldEnumerationsReused++; }
                active.report.freshArrays++; return (FieldInfo[])plan.fields.Clone();
            }
            finally { Cost(plan, started); }
        }
        static FieldInfo Field(Type type, string name, BindingFlags flags)
        {
            Plan plan = Current(type); long started = Clock();
            try
            {
                if (plan == null || flags != Lookup || name == null) { Fallback(plan); return type.GetField(name, flags); }
                FieldInfo field;
                if (plan.bindings.TryGetValue(name, out field)) { plan.row.fieldLookupsReused++; active.report.fieldLookupsReused++; return field; }
                field = type.GetField(name, flags); plan.bindings.Add(name, field); plan.row.fieldLookupsBuilt++; active.report.fieldLookupsBuilt++; return field;
            }
            finally { Cost(plan, started); }
        }
        static object[] Custom(MemberInfo member, Type attribute, bool inherit)
        {
            Plan plan = Current(); long started = Clock();
            try
            {
                if (plan == null || attribute != typeof(Persistent) || !inherit || member == null || member.Module.Assembly.IsDynamic
                    || !current.attributesEligible)
                { Fallback(plan); return member.GetCustomAttributes(attribute, inherit); }
                Attributes descriptor;
                if (!active.attributes.TryGetValue(member, out descriptor))
                {
                    object[] values = member.GetCustomAttributes(attribute, inherit);
                    if (values.Any(value => value == null || value.GetType() != typeof(Persistent)))
                    { current.attributesEligible = false; Fallback(plan); return values; }
                    descriptor = new Attributes { arrayType = values.GetType().GetElementType(), values = values.Cast<Persistent>().Select(value => new AttributeValue(value)).ToArray() };
                    active.attributes.Add(member, descriptor); plan.row.attributePlansBuilt++; active.report.attributePlansBuilt++;
                    active.report.freshArrays++; active.report.freshAttributes += values.Length;
                    return values;
                }
                Array result = Array.CreateInstance(descriptor.arrayType, descriptor.values.Length);
                for (int i = 0; i < descriptor.values.Length; i++) result.SetValue(descriptor.values[i].Fresh(), i);
                plan.row.attributePlansReused++; active.report.attributePlansReused++; active.report.freshArrays++; active.report.freshAttributes += descriptor.values.Length;
                return (object[])result;
            }
            finally { Cost(plan, started); }
        }
        void Remove()
        {
            try
            {
                harmony?.UnpatchAll(Owner); harmony = null;
                report.hooksRemoved = constructor == null || Harmony.GetPatchInfo(constructor)?.Owners.Contains(Owner) != true;
                report.cleanupStatus = report.hooksRemoved ? "removed-owned-hooks" : "owned-hooks-remain";
            }
            catch (Exception error) { report.cleanupStatus = "cleanup-failed"; report.failure = report.failure ?? error.ToString(); }
        }
        public static void ObserveForMenu()
        {
            if (active == null || active.exported) return;
            active.Remove(); active.report.status = active.report.errors == 0 && active.report.hooksRemoved ? "observed" : "invalid"; active.Export("menu");
            active.plans.Clear(); active.attributes.Clear();
        }
        void Export(string phase)
        {
            if (exported) return; exported = true;
            report.types = plans.Values.Select(plan => plan.row).OrderBy(row => row.assembly, StringComparer.Ordinal).ThenBy(row => row.type, StringComparer.Ordinal).ToArray();
            string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "startup-prepared-bindings-" + report.processId + "-" + phase + ".json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) writer.Write(ReportJson.Encode(report));
            UnityEngine.Debug.Log("[Continuum startup prepared bindings] status=" + report.status + " path=" + path + " enumerationsBuilt=" + report.fieldEnumerationsBuilt + " enumerationsReused=" + report.fieldEnumerationsReused + " attributeReuses=" + report.attributePlansReused + " fallbacks=" + report.fallbacks);
        }
        public void Update() { if (active == this && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) ObserveForMenu(); }
        public void OnApplicationQuit() { if (active == this && !exported) { Remove(); report.status = "interrupted"; Export("shutdown"); } }
        public void OnDestroy() { if (active == this) { Remove(); if (!exported) { report.status = "interrupted"; Export("destroyed"); } active = null; } }
    }
}
