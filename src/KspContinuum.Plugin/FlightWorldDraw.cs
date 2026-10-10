using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal static class FlightWorldDraw
    {
        const string Owner = "continuum.flight.world.draw";
        const int Capacity = 50000;
        [Serializable] internal sealed class Report
        {
            public string schema = "continuum-flight-world-draw/v1", runId, vesselId, mode, status, reason, cleanupStatus;
            public string scope = "Positive PQS renderer ancestry or exact PQ.meshRenderer with PQ.sphereRoot, excluding Part/Vessel ancestry; includes local-space PQStorage quads. forceRenderingOff only. All computation, collider/physics, camera and material ownership retained. Uninspected mod creation paths unsupported; endpoint audit cannot prove absence of transient unobserved owners.";
            public string observerScope = "observerTicks measures frame-level context/flag checks over the diagnostic lifecycle only; excludes inventory, dynamic admission/retirement hooks, endpoint audit and cleanup. Complete frame timings charge all in-window work.";
            public int processId, parts, initialEligible, admitted, dynamicAdmissions, destroyed, restored, alreadyOff, initialActive, endActive, faults, coverageChecks, observedFrames;
            public bool hooksRemoved, coverageComplete, restorationComplete;
            public double inventoryMilliseconds;
            public long observerTicks, clockFrequency;
            public OwnerRow[] owners; public HookRow[] hooks;
        }
        [Serializable] internal sealed class OwnerRow
        {
            public int rendererId, pqsId, leaseGeneration;
            public string pqsName, ownershipKind;
            public bool originalForceRenderingOff, initial, initialActive, restored, destroyed;
        }
        [Serializable] internal sealed class HookRow { public string method, identity; }
        static Report report; static Harmony harmony; static Vessel vessel; static Part[] parts;
        static readonly Dictionary<int, int> live = new Dictionary<int, int>();
        static Renderer[] renderers; static PQS[] spheres; static OwnerRow[] rows;
        static string output; static bool running, exported, windowEnded; static int lastFrame = -1;
        static FieldInfo rocObject, meshScatterParent, landObject, landRenderer;
        public static string FailureReason { get; private set; }
        static string Requested()
        {
            string found = null;
            foreach (string argument in Environment.GetCommandLineArgs()) if (argument.StartsWith("--continuum-flight-world-draw=", StringComparison.Ordinal))
            {
                if (found != null) throw new InvalidOperationException("One world-draw mode required.");
                found = argument.Substring("--continuum-flight-world-draw=".Length);
            }
            if (found != null && found != "stock" && found != "suppressed") throw new InvalidOperationException("World-draw mode stock|suppressed required.");
            return found;
        }
        public static void Start(Vessel activeVessel, string directory, string runId)
        {
            string mode = Requested(); if (mode == null) return;
            if (running || (report != null && !exported)) throw new InvalidOperationException("World-draw owner already active.");
            FailureReason = null; exported = false; windowEnded = false; lastFrame = -1; live.Clear(); vessel = activeVessel;
            if (vessel == null || vessel.parts == null || !Path.IsPathRooted(directory) || string.IsNullOrEmpty(runId)) throw new InvalidOperationException("World-draw physical vessel and absolute output required.");
            output = Path.Combine(Path.GetFullPath(directory), "world-draw.json"); if (File.Exists(output)) throw new InvalidOperationException("World-draw output exists.");
            long start = Stopwatch.GetTimestamp();
            report = new Report { runId = runId, vesselId = vessel.id.ToString(), mode = mode, status = "preparing", parts = vessel.parts.Count, processId = Process.GetCurrentProcess().Id, clockFrequency = Stopwatch.Frequency };
            renderers = new Renderer[Capacity]; spheres = new PQS[Capacity]; rows = new OwnerRow[Capacity]; parts = vessel.parts.ToArray();
            try
            {
                Assembly assembly = typeof(PQS).Assembly; StartupPinnedGameIdentity.Require(assembly);
                Type land = assembly.GetType("PQSLandControl+LandClassScatter", true), roc = assembly.GetType("LandClassROC", true), rocQuad = assembly.GetType("PQSMod_ROCScatterQuad", true);
                rocObject = RequiredField(rocQuad, "rocObj", typeof(GameObject)); meshScatterParent = RequiredField(typeof(PQSMod_MeshScatter), "scatterParent", typeof(GameObject));
                landObject = RequiredField(typeof(PQSMod_LandClassScatterQuad), "obj", typeof(GameObject)); landRenderer = RequiredField(typeof(PQSMod_LandClassScatterQuad), "mr", typeof(MeshRenderer));
                MethodInfo[] methods = {
                    Method(typeof(PQS), "AssignQuad", new[] { typeof(int) }, typeof(PQ), 100676842),
                    Method(typeof(PQSCache), "DestroyQuad", new[] { typeof(PQ) }, typeof(void), 100676966),
                    Method(land, "CreateScatterMesh", new[] { typeof(PQSMod_LandClassScatterQuad) }, typeof(void), 100706694),
                    Method(roc, "CreateScatterMesh", new[] { rocQuad }, typeof(void), 100664069),
                    Method(typeof(PQSMod_MeshScatter), "BuildCache", Type.EmptyTypes, typeof(void), 100676581),
                    Method(typeof(PQS), "DestroyQuad", new[] { typeof(PQ) }, typeof(void), 100676844) };
                report.hooks = new HookRow[methods.Length];
                for (int i = 0; i < methods.Length; i++)
                {
                    byte[] il = methods[i].GetMethodBody().GetILAsByteArray(); string digest;
                    using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(il)).Replace("-", "").ToLowerInvariant();
                    report.hooks[i] = new HookRow { method = methods[i].DeclaringType.FullName + "." + methods[i].Name, identity = methods[i].Module.ModuleVersionId + ":" + methods[i].MetadataToken + ":" + digest };
                }
                harmony = new Harmony(Owner);
                harmony.Patch(methods[0], postfix: new HarmonyMethod(typeof(FlightWorldDraw), nameof(Assigned)));
                harmony.Patch(methods[1], prefix: new HarmonyMethod(typeof(FlightWorldDraw), nameof(Returning)));
                harmony.Patch(methods[2], postfix: new HarmonyMethod(typeof(FlightWorldDraw), nameof(LandCreated)));
                harmony.Patch(methods[3], postfix: new HarmonyMethod(typeof(FlightWorldDraw), nameof(RocCreated)));
                harmony.Patch(methods[4], postfix: new HarmonyMethod(typeof(FlightWorldDraw), nameof(MeshCreated)));
                harmony.Patch(methods[5], prefix: new HarmonyMethod(typeof(FlightWorldDraw), nameof(Returning)));
                foreach (Renderer renderer in Candidates()) Admit(renderer, true);
                Context(); running = true; Camera.onPreCull += Observe; report.status = "observing";
                report.inventoryMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            catch (Exception error) { Fault(error); }
        }
        static FieldInfo RequiredField(Type type, string name, Type valueType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.FieldType != valueType) throw new MissingFieldException(type.FullName, name); return field;
        }
        static MethodInfo Method(Type type, string name, Type[] parameters, Type result, int token)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameters, null);
            if (method == null || method.ReturnType != result || method.MetadataToken != token || method.GetMethodBody() == null || Harmony.GetPatchInfo(method)?.Owners.Count > 0)
                throw new InvalidOperationException("Pinned exclusive world-draw seam unavailable: " + type.FullName + "." + name);
            return method;
        }
        static PQS PositiveOwner(Renderer renderer)
        {
            if (renderer == null) return null;
            PQS owner = null, quadOwner = null;
            for (Transform node = renderer.transform; node != null; node = node.parent)
            {
                if (node.GetComponent<Part>() != null || node.GetComponent<Vessel>() != null) return null;
                if (owner == null) owner = node.GetComponent<PQS>();
                PQ quad = node.GetComponent<PQ>();
                if (quad != null && quad.meshRenderer == renderer && quad.sphereRoot != null) quadOwner = quad.sphereRoot;
            }
            if (quadOwner != null && owner != null && quadOwner != owner) throw new InvalidOperationException("Native quad owner differs from PQS ancestry.");
            return quadOwner ?? owner;
        }
        static IEnumerable<Renderer> Candidates()
        {
            foreach (PQS sphere in UnityEngine.Object.FindObjectsOfType<PQS>())
            {
                foreach (Renderer renderer in sphere.GetComponentsInChildren<Renderer>(true)) yield return renderer;
                if (sphere.LocalSpacePQStorage != null) foreach (Renderer renderer in sphere.LocalSpacePQStorage.GetComponentsInChildren<Renderer>(true)) yield return renderer;
            }
            foreach (PQ quad in UnityEngine.Object.FindObjectsOfType<PQ>()) if (quad.meshRenderer != null) yield return quad.meshRenderer;
        }
        static void Admit(Renderer renderer, bool initial)
        {
            PQS pqs = PositiveOwner(renderer); if (pqs == null) return;
            int id = renderer.GetInstanceID(), existing;
            if (live.TryGetValue(id, out existing)) { if (!ReferenceEquals(renderers[existing], renderer) || spheres[existing] != pqs) throw new InvalidOperationException("PQS renderer identity/ancestry changed."); return; }
            int index = report.admitted; if (index == Capacity) throw new InvalidOperationException("World-draw lease capacity exceeded.");
            var row = new OwnerRow { rendererId = id, pqsId = pqs.GetInstanceID(), pqsName = pqs.name, leaseGeneration = index, initial = initial,
                ownershipKind = renderer.GetComponent<PQ>()?.meshRenderer == renderer ? "native-quad-sphere-root" : "pqs-ancestry",
                originalForceRenderingOff = renderer.forceRenderingOff, initialActive = renderer.enabled && renderer.gameObject.activeInHierarchy };
            renderers[index] = renderer; spheres[index] = pqs; rows[index] = row; live.Add(id, index); report.admitted++;
            if (initial) { report.initialEligible++; if (row.originalForceRenderingOff) report.alreadyOff++; if (row.initialActive) report.initialActive++; } else report.dynamicAdmissions++;
            if (report.mode == "suppressed") renderer.forceRenderingOff = true;
        }
        static void AdmitTree(GameObject root) { if (root != null) foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true)) Admit(renderer, false); }
        static void Assigned(PQ __result) { if (running) try { if (__result == null || __result.meshRenderer == null) throw new InvalidOperationException("Assigned quad renderer missing."); Admit(__result.meshRenderer, false); } catch (Exception error) { Fault(error); } }
        static void Returning(PQ __0)
        {
            if (!running || __0 == null) return;
            try { foreach (Renderer renderer in __0.GetComponentsInChildren<Renderer>(true)) { int index; if (live.TryGetValue(renderer.GetInstanceID(), out index)) Release(index); } }
            catch (Exception error) { Fault(error); }
        }
        static void LandCreated(object __0) { if (running) try { Admit(landRenderer.GetValue(__0) as Renderer, false); AdmitTree(landObject.GetValue(__0) as GameObject); } catch (Exception error) { Fault(error); } }
        static void RocCreated(object __0) { if (running) try { AdmitTree(rocObject.GetValue(__0) as GameObject); } catch (Exception error) { Fault(error); } }
        static void MeshCreated(PQSMod_MeshScatter __instance) { if (running) try { AdmitTree(meshScatterParent.GetValue(__instance) as GameObject); } catch (Exception error) { Fault(error); } }
        static void Context()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || vessel == null || FlightGlobals.ActiveVessel != vessel || !vessel.loaded || vessel.packed || vessel.parts.Count != parts.Length) throw new InvalidOperationException("World-draw flight context changed.");
            for (int i = 0; i < parts.Length; i++) if (parts[i] == null || vessel.parts[i] != parts[i]) throw new InvalidOperationException("World-draw part topology changed.");
        }
        static void Observe(Camera camera)
        {
            if (!running || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount; long begin = Stopwatch.GetTimestamp();
            try
            {
                Context(); report.observedFrames++;
                for (int i = 0; i < report.admitted; i++) if (!rows[i].restored && !rows[i].destroyed)
                {
                    Renderer renderer = renderers[i]; if (renderer == null) { Release(i); continue; }
                    bool expected = report.mode == "suppressed" || rows[i].originalForceRenderingOff;
                    if (renderer.forceRenderingOff != expected) throw new InvalidOperationException("World-draw leased flag changed.");
                }
            }
            catch (Exception error) { Fault(error); }
            finally { report.observerTicks += Stopwatch.GetTimestamp() - begin; }
        }
        static void Release(int index)
        {
            OwnerRow row = rows[index]; if (row.restored || row.destroyed) return;
            Renderer renderer = renderers[index];
            if (renderer == null) { row.destroyed = true; report.destroyed++; }
            else { renderer.forceRenderingOff = row.originalForceRenderingOff; if (renderer.forceRenderingOff != row.originalForceRenderingOff) throw new InvalidOperationException("World-draw restoration failed."); row.restored = true; report.restored++; }
            live.Remove(row.rendererId);
        }
        public static void EndWindow()
        {
            if (report == null || exported || !running || windowEnded) return;
            try
            {
                Context(); report.coverageChecks++;
                var seen = new HashSet<int>();
                foreach (Renderer renderer in Candidates())
                {
                    PQS owner = PositiveOwner(renderer); if (owner == null || !seen.Add(renderer.GetInstanceID())) continue;
                    int index; if (!live.TryGetValue(renderer.GetInstanceID(), out index) || !ReferenceEquals(renderers[index], renderer) || spheres[index] != owner) throw new InvalidOperationException("Endpoint PQS coverage missed or changed owner.");
                    if (renderer.forceRenderingOff != (report.mode == "suppressed" || rows[index].originalForceRenderingOff)) throw new InvalidOperationException("Endpoint PQS flag changed.");
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy) report.endActive++;
                }
                for (int i = 0; i < report.admitted; i++) if (!rows[i].restored && !rows[i].destroyed)
                {
                    if (renderers[i] == null) Release(i);
                    else
                    {
                        PQS currentOwner = PositiveOwner(renderers[i]);
                        if (currentOwner != spheres[i]) throw new InvalidOperationException("Endpoint leased ancestry changed: renderer=" + rows[i].rendererId + ":" + renderers[i].name
                            + ", expected=" + rows[i].pqsId + ":" + rows[i].pqsName + ", actual=" + (currentOwner == null ? "none" : currentOwner.GetInstanceID() + ":" + currentOwner.name)
                            + ", parent=" + (renderers[i].transform.parent == null ? "none" : renderers[i].transform.parent.name));
                    }
                }
                if (report.observedFrames == 0 || report.initialEligible == 0) throw new InvalidOperationException("No positive PQS/frame evidence.");
                windowEnded = true; report.coverageComplete = true;
            }
            catch (Exception error) { Fault(error); }
        }
        static void Cleanup()
        {
            running = false; Camera.onPreCull -= Observe;
            try { if (harmony != null) harmony.UnpatchAll(Owner); harmony = null; report.hooksRemoved = true; }
            catch (Exception error) { FailureReason = "World-draw unpatch: " + error.Message; report.faults++; report.hooksRemoved = false; }
            for (int i = 0; i < report.admitted; i++) try { Release(i); } catch (Exception error) { FailureReason = "World-draw cleanup: " + error.Message; report.faults++; }
            report.restorationComplete = report.restored + report.destroyed == report.admitted;
            report.cleanupStatus = report.hooksRemoved && report.restorationComplete ? "removed-owned-hooks-restored-survivors" : "incomplete-cleanup";
        }
        static void Fault(Exception error)
        {
            FailureReason = error.GetType().Name + ": " + error.Message; report.faults++; report.reason = FailureReason; report.status = "invalid"; Cleanup();
        }
        public static void Stop(string status)
        {
            if (report == null || exported) return;
            Cleanup(); report.status = FailureReason != null ? "invalid" : status == "complete" && windowEnded && report.coverageComplete && report.restorationComplete ? "completed" : "interrupted";
            report.reason = FailureReason ?? status; report.owners = new OwnerRow[report.admitted]; Array.Copy(rows, report.owners, report.admitted);
            try { Directory.CreateDirectory(Path.GetDirectoryName(output)); string temp = output + ".tmp"; File.WriteAllText(temp, ReportJson.Encode(report)); File.Move(temp, output); exported = true; }
            catch (Exception error) { FailureReason = "World-draw export: " + error.Message; UnityEngine.Debug.LogError(FailureReason); }
        }
    }
}
