using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace KspContinuum
{
    internal static class StartupPreparedBundleBoundary
    {
        public static void Inject(List<CodeInstruction> body, ILGenerator generator)
        {
            Type iterator = typeof(Expansions.BundleLoader).GetNestedType("<LoadAssetBundle>d__16", BindingFlags.NonPublic);
            FieldInfo bundle = AccessTools.DeclaredField(iterator, "<bundle>5__3");
            MethodInfo cleanup = AccessTools.DeclaredMethod(iterator, "<>m__Finally1");
            int[] resets = body.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.opcode == OpCodes.Stfld && Equals(item.instruction.operand, bundle)).Select(item => item.index).ToArray();
            int[] cleanupCalls = body.Select((instruction, index) => new { instruction, index })
                .Where(item => cleanup != null && item.instruction.Calls(cleanup)).Select(item => item.index).ToArray();
            MethodInfo loadedCheck = AccessTools.DeclaredMethod(typeof(Expansions.BundleLoader), "IsBundleLoaded", new[] { typeof(string) });
            MethodInfo send = AccessTools.DeclaredMethod(typeof(UnityWebRequest), "SendWebRequest", Type.EmptyTypes);
            if (bundle == null || cleanup == null || resets.Length != 2 || cleanupCalls.Length != 1 ||
                resets[0] < 1 || body[resets[0] - 1].opcode != OpCodes.Ldnull ||
                !body.Take(resets[0]).Any(instruction => instruction.Calls(loadedCheck)) ||
                !body.Skip(resets[0]).Take(cleanupCalls[0] - resets[0]).Any(instruction => instruction.Calls(send)))
                throw new InvalidOperationException("Prepared bundle boundary shape differs.");
            int start = resets[0] + 1, cleanupStart = cleanupCalls[0] - 1;
            if (cleanupStart <= start || body[start].opcode != OpCodes.Ldarg_0 || body[cleanupStart].opcode != OpCodes.Ldarg_0 ||
                body[start].blocks.Count != 0 || body[cleanupStart].blocks.Count != 0 ||
                body.Skip(start).Take(cleanupStart - start).Any(instruction => instruction.blocks.Count != 0))
                throw new InvalidOperationException("Prepared bundle boundary stack or exception region differs.");
            MethodInfo helper = AccessTools.DeclaredMethod(typeof(StartupBundleAttribution), "TryPreparedBundle");
            if (helper == null) throw new MissingMethodException("Prepared bundle helper missing.");
            // The stock publication instructions remain the sole owner of asset, scene and bundle registration.
            Label preparedLabel = generator.DefineLabel();
            body[cleanupStart].labels.Add(preparedLabel);
            var entry = new CodeInstruction(OpCodes.Ldarg_0);
            entry.labels.AddRange(body[start].labels); body[start].labels.Clear();
            body.InsertRange(start, new[] { entry, new CodeInstruction(OpCodes.Call, helper), new CodeInstruction(OpCodes.Brtrue, preparedLabel) });
        }
    }
}
