using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupReadiness : MonoBehaviour
    {
        const string Owner = "continuum.startup-readiness";
        const string Marker = "[Continuum startup readiness] ";
        static StartupReadiness active;
        [ThreadStatic] static Frame current;
        readonly Dictionary<object, Frame> frames = new Dictionary<object, Frame>();
        Harmony harmony;
        FieldInfo sphereField, temporaryField, siteField;
        static Action<PSystemSetup, PQS, Transform> originalForce;
        int shortened, stockFallbacks, presentationWaits, terrainPolls, terrainRays, fixedBarriers, terrainWaits;
        bool exported;
        public sealed class Frame
        {
            public PQS Sphere;
            public Transform Target;
            public bool ForceCompleted;
            public bool CallerObserved;
        }
        public struct MoveState { public Frame Previous; }

        public void Awake()
        {
            if (!Environment.GetCommandLineArgs().Contains("--continuum-startup-readiness")) return;
            DontDestroyOnLoad(gameObject);
            try
            {
                if (!System.IO.File.Exists(System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "continuum-lab.local.json")))
                    throw new InvalidOperationException("Private laboratory required.");
                Assembly assembly = typeof(PSystemSetup).Assembly;
                if (assembly.ManifestModule.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf") ||
                    ScaleCheckpointLoadState.HashFile(assembly.Location) != "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27")
                    throw new InvalidOperationException("Pinned game differs.");
                Type iterator = typeof(PSystemSetup).GetNestedType("<SetupLaunchSites>d__38", BindingFlags.NonPublic);
                Type screen = typeof(LoadingScreen).GetNestedType("<UpdateLoadingScreen>d__27", BindingFlags.NonPublic);
                MethodInfo move = iterator == null ? null : AccessTools.DeclaredMethod(iterator, "MoveNext");
                MethodInfo display = screen == null ? null : AccessTools.DeclaredMethod(screen, "MoveNext");
                MethodInfo force = AccessTools.DeclaredMethod(typeof(PSystemSetup), "ForceInitPQS", new[] { typeof(PQS), typeof(Transform) });
                if (move == null || move.MetadataToken != 100705880 || display == null || display.MetadataToken != 100705074 ||
                    force == null || force.MetadataToken != 100670696 || new[] { move, display, force }.Any(m => Harmony.GetPatchInfo(m)?.Owners.Count > 0))
                    throw new InvalidOperationException("Startup boundary differs or already patched.");
                sphereField = AccessTools.DeclaredField(iterator, "<sphere>5__6");
                temporaryField = AccessTools.DeclaredField(iterator, "<tempObject>5__7");
                siteField = AccessTools.DeclaredField(iterator, "<launchSite>5__4");
                if (sphereField?.FieldType != typeof(PQS) || temporaryField?.FieldType != typeof(GameObject) || siteField?.FieldType != typeof(LaunchSite))
                    throw new InvalidOperationException("Launch-site state differs.");
                active = this; harmony = new Harmony(Owner);
                originalForce = (Action<PSystemSetup, PQS, Transform>)Delegate.CreateDelegate(typeof(Action<PSystemSetup, PQS, Transform>), force);
                harmony.Patch(move, prefix: new HarmonyMethod(typeof(StartupReadiness), nameof(MovePrefix)), postfix: new HarmonyMethod(typeof(StartupReadiness), nameof(MovePostfix)),
                    finalizer: new HarmonyMethod(typeof(StartupReadiness), nameof(MoveFinalizer)), transpiler: new HarmonyMethod(typeof(StartupReadiness), nameof(LaunchTranspile)));
                harmony.Patch(display, transpiler: new HarmonyMethod(typeof(StartupReadiness), nameof(DisplayTranspile)));
                Debug.Log(Marker + "status=installed scope=static-site-terrain-collider-readiness-and-presentation deadline=stock-scaled-2s raycastPolicy=intended-pqs-layer15-after-one-fixed-barrier mobilePolicy=stock-wait");
            }
            catch (Exception error) { Cleanup(); Debug.LogError(Marker + "status=rejected reason=" + error.Message); Application.Quit(2); }
        }
        static void MovePrefix(object __instance, out MoveState __state)
        {
            __state = new MoveState { Previous = current };
            Frame frame;
            if (!active.frames.TryGetValue(__instance, out frame)) { frame = new Frame(); active.frames.Add(__instance, frame); }
            current = frame;
        }
        static void MovePostfix(object __instance, bool __result) { if (!__result && active != null) active.frames.Remove(__instance); }
        static Exception MoveFinalizer(MoveState __state, Exception __exception) { current = __state.Previous; return __exception; }
        static void ForceAtCaller(PSystemSetup setup, PQS sphere, Transform target, object iterator)
        {
            Frame frame;
            if (!active.frames.TryGetValue(iterator, out frame)) { frame = new Frame(); active.frames.Add(iterator, frame); }
            frame.Sphere = sphere; frame.Target = target; frame.ForceCompleted = false; frame.CallerObserved = true;
            originalForce(setup, sphere, target);
            frame.ForceCompleted = true;
        }
        static IEnumerable<CodeInstruction> LaunchTranspile(IEnumerable<CodeInstruction> source)
        {
            var body = source.ToList(); ConstructorInfo wait = typeof(WaitForSeconds).GetConstructor(new[] { typeof(float) });
            MethodInfo force = AccessTools.DeclaredMethod(typeof(PSystemSetup), "ForceInitPQS", new[] { typeof(PQS), typeof(Transform) });
            int[] forceLocations = body.Select((instruction, index) => new { instruction, index })
                .Where(x => (x.instruction.opcode == OpCodes.Call || x.instruction.opcode == OpCodes.Callvirt) && Equals(x.instruction.operand, force))
                .Select(x => x.index).ToArray();
            if (forceLocations.Length != 2) throw new InvalidOperationException("Launch-site force call pattern differs.");
            foreach (int at in forceLocations.Reverse())
            {
                var instance = new CodeInstruction(OpCodes.Ldarg_0);
                instance.labels.AddRange(body[at].labels); instance.blocks.AddRange(body[at].blocks);
                body[at] = instance;
                body.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(StartupReadiness), nameof(ForceAtCaller))));
            }
            int[] locations = body.Select((instruction, index) => new { instruction, index }).Where(x => x.instruction.opcode == OpCodes.Newobj && Equals(x.instruction.operand, wait)).Select(x => x.index).ToArray();
            if (locations.Length != 2 || locations.Any(i => i < 1 || body[i - 1].opcode != OpCodes.Ldc_R4 || !Equals(body[i - 1].operand, 2f)))
                throw new InvalidOperationException("Launch-site wait pattern differs.");
            foreach (int at in locations.Reverse())
            {
                var call = new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(StartupReadiness), nameof(PlacementWait)));
                call.blocks.AddRange(body[at].blocks);
                var instance = new CodeInstruction(OpCodes.Ldarg_0); instance.labels.AddRange(body[at].labels);
                body[at] = instance; body.Insert(at + 1, call);
            }
            return body;
        }
        static object PlacementWait(float seconds, object iterator)
        {
            PQS sphere = active.sphereField.GetValue(iterator) as PQS;
            GameObject temporary = active.temporaryField.GetValue(iterator) as GameObject;
            LaunchSite site = active.siteField.GetValue(iterator) as LaunchSite;
            Frame frame;
            bool framePresent = active.frames.TryGetValue(iterator, out frame);
            bool sitePresent = site != null, spherePresent = sphere != null, temporaryPresent = temporary != null;
            bool callerObserved = framePresent && frame.CallerObserved;
            bool forceCompleted = framePresent && frame.ForceCompleted;
            bool sphereMatches = framePresent && spherePresent && ReferenceEquals(frame.Sphere, sphere);
            bool targetMatches = framePresent && temporaryPresent && frame.Target == temporary.transform;
            bool sphereTargetMatches = spherePresent && temporaryPresent && sphere.target == temporary.transform;
            bool context = sitePresent && spherePresent && temporaryPresent && callerObserved && forceCompleted && sphereMatches && targetMatches && sphereTargetMatches;
            if (!context)
            {
                active.stockFallbacks++;
                Debug.Log(Marker + "site=" + (site == null ? "unknown" : site.launchSiteName) + " decision=stock-wait reason=force-context-unresolved" +
                    " sitePresent=" + sitePresent + " spherePresent=" + spherePresent + " temporaryPresent=" + temporaryPresent +
                    " framePresent=" + framePresent + " callerObserved=" + callerObserved + " forceCompleted=" + forceCompleted +
                    " sphereMatches=" + sphereMatches + " targetMatches=" + targetMatches + " sphereTargetMatches=" + sphereTargetMatches +
                    " currentFrameMatches=" + (framePresent && ReferenceEquals(current, frame)) +
                    " bodyTransformPresent=" + (spherePresent && sphere.PQSModCBTransform != null));
                return new WaitForSeconds(seconds);
            }
            active.terrainWaits++;
            return StartupTerrainReadiness.Create(sphere, temporary.transform, site, seconds, active.TerrainResult);
        }
        void TerrainResult(StartupTerrainReadiness.Result result)
        {
            if (result.ColliderReady) shortened++; else stockFallbacks++;
            terrainPolls += result.Polls; terrainRays += result.Rays; fixedBarriers += result.FixedBarriers;
            Debug.Log(Marker + "site=" + result.Site + " body=" + result.Body + " decision=" + result.Decision + " reason=" + result.Reason +
                " scaledElapsedSeconds=" + result.ElapsedSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " polls=" + result.Polls + " rays=" + result.Rays + " fixedBarriers=" + result.FixedBarriers +
                " qualification=static-site-collider-boundary-not-mobile-footprint-or-flight-equivalence");
        }
        static IEnumerable<CodeInstruction> DisplayTranspile(IEnumerable<CodeInstruction> source)
        {
            var body = source.ToList(); ConstructorInfo wait = typeof(WaitForSeconds).GetConstructor(new[] { typeof(float) });
            int[] locations = body.Select((instruction, index) => new { instruction, index }).Where(x => x.instruction.opcode == OpCodes.Newobj && Equals(x.instruction.operand, wait)).Select(x => x.index).ToArray();
            if (locations.Length != 1 || locations[0] < 1 || body[locations[0] - 1].opcode != OpCodes.Ldc_R4 || !Equals(body[locations[0] - 1].operand, 1f))
                throw new InvalidOperationException("Inter-screen presentation wait differs.");
            body[locations[0]].opcode = OpCodes.Call; body[locations[0]].operand = AccessTools.DeclaredMethod(typeof(StartupReadiness), nameof(PresentationWait));
            return body;
        }
        static object PresentationWait(float seconds) { active.presentationWaits++; return null; }
        public static void ObserveForMenu() { if (active != null) { active.Export(); active.Cleanup(); } }
        public void Update() { if (active == this && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) { Export(); Cleanup(); } }
        void Export()
        {
            if (exported) return; exported = true;
            Debug.Log(Marker + "status=observed shortenedPlacementWaits=" + shortened + " stockPlacementWaits=" + stockFallbacks + " presentationWaits=" + presentationWaits + " terrainWaits=" + terrainWaits + " terrainPolls=" + terrainPolls + " terrainRays=" + terrainRays + " fixedBarriers=" + fixedBarriers);
        }
        void Cleanup() { harmony?.UnpatchAll(Owner); harmony = null; frames.Clear(); }
        public void OnApplicationQuit() { if (active == this) { Export(); Cleanup(); } }
        public void OnDestroy() { if (active == this) { Export(); Cleanup(); active = null; } }
    }
}
