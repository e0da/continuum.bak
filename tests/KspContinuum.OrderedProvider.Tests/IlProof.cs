using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
static class IlProof
{
    static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    public static void Run(string managed, string mod, string plugin)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string file = new AssemblyName(args.Name).Name + ".dll";
            foreach (string directory in new[] { managed, System.IO.Path.GetDirectoryName(mod), System.IO.Path.GetDirectoryName(plugin) })
            { string path = System.IO.Path.Combine(directory, file); if (System.IO.File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        var target = Assembly.LoadFrom(mod).GetType("MuMech.VesselState", true).GetMethod("AnalyzeParts", BindingFlags.Instance | BindingFlags.NonPublic);
        var candidate = Assembly.LoadFrom(plugin).GetType("KspContinuum.MechJebProviderPlan", true);
        object instance = RuntimeHelpers.GetUninitializedObject(candidate);
        Type reportType = candidate.GetNestedType("Report"); object report = Activator.CreateInstance(reportType);
        candidate.GetField("report", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, report);
        candidate.GetField("active", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, instance);
        var original = PatchProcessor.GetOriginalInstructions(target);
        var rewrite = candidate.GetMethod("Rewrite", BindingFlags.Static | BindingFlags.NonPublic);
        var rewritten = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] { original })).ToList();
        Require(original.Count == rewritten.Count, "Instruction count changed.");
        int casts = 0, forwards = 0, preserved = 0;
        for (int i = 0; i < original.Count; i++)
        {
            var prior = original[i]; var next = rewritten[i];
            Require(prior.labels.SequenceEqual(next.labels) && prior.blocks.SequenceEqual(next.blocks), "Boundary metadata drift.");
            if (i + 1 < original.Count && original[i + 1].opcode == OpCodes.Isinst)
            {
                Require(prior.opcode == OpCodes.Ldloc_S && next.opcode == OpCodes.Ldsfld && next.operand is FieldInfo && ((FieldInfo)next.operand).Name == "CurrentEntry", "Classification cursor differs.");
                i++; var field = rewritten[i].operand as FieldInfo;
                Require(rewritten[i].opcode == OpCodes.Ldfld && field != null && field.FieldType == original[i].operand as Type && original[i].labels.SequenceEqual(rewritten[i].labels) && original[i].blocks.SequenceEqual(rewritten[i].blocks), "Typed field/branch contract differs.");
                casts++;
            }
            else if (next.operand is MethodInfo && ((MethodInfo)next.operand).DeclaringType == candidate)
            {
                var old = prior.operand as MethodInfo; var forward = (MethodInfo)next.operand;
                Require(prior.opcode == OpCodes.Callvirt && next.opcode == OpCodes.Call && old != null && old.Name == "get_Item" && (forward.Name == "GetPart" || forward.Name == "GetModule"), "Unexpected operation substitution.");
                Require(forward.ReturnType == old.ReturnType && forward.GetParameters()[0].ParameterType == old.DeclaringType && forward.GetParameters()[1].ParameterType == old.GetParameters()[0].ParameterType, "Forwarding stack contract changed."); forwards++;
            }
            else { Require(prior.opcode == next.opcode && Equals(prior.operand, next.operand), "Nonclassification operation changed."); preserved++; }
        }
        Require(casts == 15 && forwards == 2 && preserved + 2 * casts + forwards == original.Count, "Unexpected instruction coverage.");
        Console.WriteLine("Pinned original IL preservation passed: " + original.Count + " instructions; " + preserved + " unchanged; 15 typed field substitutions and 2 exact retrieval forwards; labels/exception boundaries preserved.");
        var changed = original.Select(x => new CodeInstruction(x)).ToList(); var site = changed.First(x => x.opcode == OpCodes.Isinst); site.operand = typeof(string);
        bool rejected = false; try { rewrite.Invoke(null, new object[] { changed }); } catch (TargetInvocationException e) { rejected = e.InnerException is InvalidOperationException; }
        Require(rejected, "Unknown classification modification accepted.");
        Console.WriteLine("Unknown classification seam fails closed before patched invocation.");
        Type entryType = candidate.GetNestedType("TypedEntry"); var constructor = entryType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(object) }, null);
        var typedFields = entryType.GetFields(BindingFlags.Instance | BindingFlags.Public);
        foreach (object module in typedFields.Where(x => !x.FieldType.IsInterface).Select(x => RuntimeHelpers.GetUninitializedObject(x.FieldType)).Concat(new object[] { null }))
        {
            object entryValue = constructor.Invoke(new[] { module });
            foreach (var field in typedFields)
                Require(ReferenceEquals(field.GetValue(entryValue), field.FieldType.IsInstanceOfType(module) ? module : null), "Typed dispatch differs from CLR classification: " + field.Name);
        }
        Console.WriteLine("Nine typed provider fields agree with independent CLR classification, including inherited overlaps and null; no provider calls executed.");
        var assembly = target.Module.Assembly;
        Type moduleType = typedFields.First(x => x.Name == "Wheel").FieldType;
        object first = RuntimeHelpers.GetUninitializedObject(moduleType), replacement = RuntimeHelpers.GetUninitializedObject(moduleType);
        Type collectionType = Assembly.LoadFrom(System.IO.Path.Combine(managed, "Assembly-CSharp.dll")).GetType("PartModuleList", true);
        object collection = RuntimeHelpers.GetUninitializedObject(collectionType);
        FieldInfo storage = collectionType.GetField("modules", BindingFlags.Instance | BindingFlags.NonPublic);
        object members = Activator.CreateInstance(storage.FieldType); storage.SetValue(collection, members);
        storage.FieldType.GetMethod("Add").Invoke(members, new[] { first });
        Type rowType = candidate.Assembly.GetType("KspContinuum.OrderedProviderRow", true);
        object row = Activator.CreateInstance(rowType, new object[] { new object(), collection, new[] { first } });
        Type cursorType = candidate.GetNestedType("Cursor", BindingFlags.NonPublic); object cursor = Activator.CreateInstance(cursorType, true);
        cursorType.GetField("Owner", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cursor, instance);
        cursorType.GetField("Row", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cursor, row);
        var perPart = Array.CreateInstance(entryType, 1); perPart.SetValue(constructor.Invoke(new[] { first }), 0);
        var entries = Array.CreateInstance(perPart.GetType(), 1); entries.SetValue(perPart, 0);
        cursorType.GetField("Entries", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cursor, entries);
        candidate.GetField("cursor", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, cursor);
        MethodInfo getModule = candidate.GetMethod("GetModule");
        Require(ReferenceEquals(getModule.Invoke(null, new object[] { collection, 0 }), first), "Cached retrieval changed module.");
        storage.FieldType.GetProperty("Item").SetValue(members, replacement, new object[] { 0 });
        Require(ReferenceEquals(getModule.Invoke(null, new object[] { collection, 0 }), replacement), "Mid-call fallback changed live retrieval.");
        object liveEntry = candidate.GetField("CurrentEntry").GetValue(null);
        Require(ReferenceEquals(entryType.GetField("Wheel").GetValue(liveEntry), replacement), "Fallback published stale typed reference.");
        Require((long)reportType.GetField("cachedModules").GetValue(report) == 1 && (long)reportType.GetField("fallbackModules").GetValue(report) == 1 && (bool)candidate.GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance), "Fallback/invalidation evidence differs.");
        Console.WriteLine("Actual forwarding wrapper preserves module identity and replaces stale classification on mid-call mutation, marking rebuild; no providers invoked.");
        FieldInfo currentCursor = candidate.GetField("cursor", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo depth = candidate.GetField("invocationDepth", BindingFlags.Static | BindingFlags.NonPublic);
        currentCursor.SetValue(null, null); depth.SetValue(null, 0);
        cursorType.GetField("Row", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cursor, row);
        Type planType = candidate.Assembly.GetType("KspContinuum.OrderedProviderPlan", true);
        var oldRows = Array.CreateInstance(rowType, 1); oldRows.SetValue(row, 0);
        object olderPlan = Activator.CreateInstance(planType, new object[] { 1L, instance, collection, oldRows });
        cursorType.GetField("Plan", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cursor, olderPlan);
        candidate.GetField("primaryCursor", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, cursor);
        MethodInfo enter = candidate.GetMethod("EnterContext", BindingFlags.Static | BindingFlags.NonPublic), borrow = candidate.GetMethod("BorrowCursor", BindingFlags.Static | BindingFlags.NonPublic), end = candidate.GetMethod("End", BindingFlags.Static | BindingFlags.NonPublic);
        object outerState = enter.Invoke(null, null); object outerCursor = borrow.Invoke(null, new object[] { instance, 0 }); currentCursor.SetValue(null, outerCursor);
        object middleState = enter.Invoke(null, null); Require(currentCursor.GetValue(null) == null, "Nonowned invocation did not clear cursor.");
        object innerState = enter.Invoke(null, null); object innerCursor = borrow.Invoke(null, new object[] { instance, 2 });
        Require(!ReferenceEquals(innerCursor, outerCursor), "Deep owned invocation reused busy primary cursor.");
        object newerRow = Activator.CreateInstance(rowType, new object[] { new object(), collection, new[] { replacement } });
        var newRows = Array.CreateInstance(rowType, 1); newRows.SetValue(newerRow, 0);
        object newerPlan = Activator.CreateInstance(planType, new object[] { 2L, instance, collection, newRows });
        cursorType.GetField("Plan", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(innerCursor, newerPlan);
        cursorType.GetField("Row", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(innerCursor, newerRow); currentCursor.SetValue(null, innerCursor);
        Require(ReferenceEquals(cursorType.GetField("Row", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(outerCursor), row), "Rebuild changed outer captured generation.");
        Require(ReferenceEquals(cursorType.GetField("Plan", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(outerCursor), olderPlan), "Rebuild redirected outer plan.");
        end.Invoke(null, new[] { innerState, null }); Require(currentCursor.GetValue(null) == null && (int)depth.GetValue(null) == 2, "Inner restoration differs.");
        end.Invoke(null, new[] { middleState, null }); Require(ReferenceEquals(currentCursor.GetValue(null), outerCursor) && (int)depth.GetValue(null) == 1, "Nonowned restoration lost outer cursor.");
        end.Invoke(null, new[] { outerState, null }); Require(currentCursor.GetValue(null) == null && (int)depth.GetValue(null) == 0, "Outer restoration differs.");
        Console.WriteLine("Active→nonactive→active reentrance retains outer generation and restores every context depth.");
    }
}
