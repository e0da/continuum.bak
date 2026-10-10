using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupAudioPrefetch : MonoBehaviour
    {
        const string Owner = "continuum.startup-audio-prefetch";
        const string AssemblySha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int Slots = 8;
        const long EncodedLimit = 32L * 1024 * 1024;
        static StartupAudioPrefetch active;
        [ThreadStatic] static object currentIterator;
        readonly Queue<Job> waiting = new Queue<Job>();
        readonly Dictionary<string, Job> pending = new Dictionary<string, Job>(StringComparer.Ordinal);
        readonly Dictionary<UnityWebRequest, Job> requests = new Dictionary<UnityWebRequest, Job>();
        readonly Dictionary<object, Job> consumed = new Dictionary<object, Job>();
        readonly HashSet<string> retired = new HashSet<string>(StringComparer.Ordinal);
        Harmony harmony;
        bool candidate, initialized, finished, failed;
        int hits, fallback, skipped, peakSlots, drained, disposed;
        long encoded, peakEncoded;
        string root, runId, status = "not-started";

        sealed class Job
        {
            public string Uri;
            public long Encoded;
            public UnityWebRequest Request;
            public UnityWebRequestAsyncOperation Operation;
        }
        public struct MoveState { public object Previous; }

        public void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            candidate = arguments.Contains("--continuum-startup-audio-prefetch");
            if (!candidate && !arguments.Contains("--continuum-startup-texture-observe")) return;
            DontDestroyOnLoad(gameObject); active = this;
            try
            {
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
                if (!File.Exists(Path.Combine(root, "continuum-lab.local.json"))) throw new InvalidOperationException("Disposable laboratory marker required.");
                string[] identities = arguments.Where(argument => argument.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                if (identities.Length != 1) throw new InvalidOperationException("One owned run identity required.");
                runId = identities[0].Substring("--continuum-research-run=".Length);
                Assembly assembly = typeof(GameDatabase).Assembly;
                if (assembly.ManifestModule.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf") ||
                    ScaleCheckpointLoadState.HashFile(assembly.Location) != AssemblySha) throw new InvalidOperationException("Pinned game differs.");
                if (!candidate) { status = "observation-only"; return; }
                Type iterator = typeof(DatabaseLoaderAudio).GetNestedType("<Load>d__0", BindingFlags.NonPublic);
                MethodInfo move = iterator == null ? null : AccessTools.DeclaredMethod(iterator, "MoveNext", Type.EmptyTypes);
                MethodInfo load = AccessTools.DeclaredMethod(typeof(DatabaseLoaderAudio), "Load", new[] { typeof(UrlDir.UrlFile), typeof(FileInfo) });
                if (move == null || move.ReturnType != typeof(bool) || load == null || load.ReturnType != typeof(IEnumerator))
                    throw new MissingMethodException("Pinned audio loader differs.");
                if (new[] { move, load }.Any(target => Harmony.GetPatchInfo(target)?.Owners.Count > 0))
                    throw new InvalidOperationException("Audio loader has another patch owner.");
                harmony = new Harmony(Owner);
                harmony.Patch(move, prefix: new HarmonyMethod(typeof(StartupAudioPrefetch), nameof(MovePrefix)),
                    postfix: new HarmonyMethod(typeof(StartupAudioPrefetch), nameof(MovePostfix)),
                    transpiler: new HarmonyMethod(typeof(StartupAudioPrefetch), nameof(Transpile)),
                    finalizer: new HarmonyMethod(typeof(StartupAudioPrefetch), nameof(MoveFinalizer)));
                harmony.Patch(load, postfix: new HarmonyMethod(typeof(StartupAudioPrefetch), nameof(LoadPostfix)));
                if (new[] { move, load }.Any(target => Harmony.GetPatchInfo(target) == null || Harmony.GetPatchInfo(target).Owners.Any(owner => owner != Owner)))
                    throw new InvalidOperationException("Audio patch ownership differs.");
                status = "installed";
            }
            catch (Exception error) { failed = true; status = "abstained:" + error.Message; Cleanup(); Debug.LogException(error); Application.Quit(2); }
        }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            MethodInfo get = AccessTools.DeclaredMethod(typeof(UnityWebRequestMultimedia), "GetAudioClip", new[] { typeof(string), typeof(AudioType) });
            MethodInfo send = AccessTools.DeclaredMethod(typeof(UnityWebRequest), "SendWebRequest", Type.EmptyTypes);
            if (get == null || send == null || body.Count(instruction => instruction.Calls(get)) != 1 || body.Count(instruction => instruction.Calls(send)) != 1)
                throw new InvalidOperationException("Pinned audio request call counts differ.");
            foreach (CodeInstruction instruction in body)
            {
                if (instruction.Calls(get)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupAudioPrefetch), nameof(Request)); }
                else if (instruction.Calls(send)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupAudioPrefetch), nameof(Send)); }
            }
            return body;
        }
        static void MovePrefix(object __instance, out MoveState __state)
        { __state = new MoveState { Previous = currentIterator }; currentIterator = __instance; }
        static void MovePostfix(object __instance, bool __result)
        { if (!__result && active != null) active.Release(__instance); }
        static Exception MoveFinalizer(object __instance, MoveState __state, Exception __exception)
        {
            currentIterator = __state.Previous;
            if (__exception != null && active != null) { active.failed = true; active.status = "loader-exception"; active.Release(__instance); active.Cleanup(); }
            return __exception;
        }
        static void LoadPostfix(ref IEnumerator __result)
        { if (active != null && active.candidate) __result = Drain(__result); }
        static IEnumerator Drain(IEnumerator original)
        {
            try
            {
                while (original.MoveNext())
                {
                    var operation = original.Current as UnityWebRequestAsyncOperation;
                    if (operation != null && operation.isDone && active != null && active.candidate) { active.drained++; continue; }
                    yield return original.Current;
                }
            }
            finally { if (active != null) active.Release(original); (original as IDisposable)?.Dispose(); }
        }
        static UnityWebRequest Request(string uri, AudioType audioType)
        {
            StartupAudioPrefetch owner = active;
            if (owner == null) throw new InvalidOperationException("Audio broker unavailable.");
            owner.Initialize(); owner.Pump();
            Job job;
            if (audioType == AudioType.UNKNOWN && currentIterator != null && owner.pending.TryGetValue(uri, out job))
            {
                owner.pending.Remove(uri); owner.consumed.Add(currentIterator, job); owner.hits++; return job.Request;
            }
            owner.fallback++; owner.retired.Add(uri);
            return UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
        }
        static UnityWebRequestAsyncOperation Send(UnityWebRequest request)
        { Job job; return active != null && active.requests.TryGetValue(request, out job) ? job.Operation : request.SendWebRequest(); }

        void Initialize()
        {
            if (initialized) return;
            initialized = true; status = "running";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (UrlDir.UrlFile file in GameDatabase.Instance.root.GetFiles(UrlDir.FileType.Audio))
            {
                if (file.fileExtension != "wav" && file.fileExtension != "ogg") continue;
                string path = Path.GetFullPath(file.fullPath), uri = KSPUtil.ApplicationFileProtocol + file.fullPath;
                if (!seen.Add(uri) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { skipped++; continue; }
                try
                {
                    for (string part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                        if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Symlink audio.");
                    long bytes = new FileInfo(path).Length;
                    if (bytes <= 0 || bytes > EncodedLimit) { skipped++; continue; }
                    waiting.Enqueue(new Job { Uri = uri, Encoded = bytes });
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                { skipped++; }
            }
        }
        void Pump()
        {
            while (!failed && waiting.Count > 0 && requests.Count < Slots)
            {
                Job job = waiting.Peek();
                if (retired.Contains(job.Uri)) { waiting.Dequeue(); skipped++; continue; }
                if (encoded + job.Encoded > EncodedLimit) return;
                waiting.Dequeue();
                try
                {
                    job.Request = UnityWebRequestMultimedia.GetAudioClip(job.Uri, AudioType.UNKNOWN);
                    job.Operation = job.Request.SendWebRequest();
                    requests.Add(job.Request, job); pending.Add(job.Uri, job); encoded += job.Encoded;
                    peakSlots = Math.Max(peakSlots, requests.Count); peakEncoded = Math.Max(peakEncoded, encoded);
                }
                catch { job.Request?.Dispose(); skipped++; }
            }
        }
        void Release(object iterator)
        {
            Job job;
            if (!consumed.TryGetValue(iterator, out job)) return;
            consumed.Remove(iterator); requests.Remove(job.Request); encoded -= job.Encoded;
            if (!job.Request.isDone) job.Request.Abort();
            job.Request.Dispose(); disposed++; Pump();
        }
        void Cleanup()
        {
            if (harmony != null) { harmony.UnpatchAll(Owner); harmony = null; }
            foreach (Job job in pending.Values)
            {
                if (job.Request.isDone && !job.Request.isNetworkError && !job.Request.isHttpError)
                {
                    try { AudioClip clip = DownloadHandlerAudioClip.GetContent(job.Request); if (clip != null) UnityEngine.Object.Destroy(clip); }
                    catch { }
                }
            }
            foreach (Job job in requests.Values) { if (!job.Request.isDone) job.Request.Abort(); job.Request.Dispose(); disposed++; }
            pending.Clear(); requests.Clear(); consumed.Clear(); waiting.Clear(); retired.Clear(); encoded = 0;
        }
        public static void ObserveForMenu() { if (active != null) active.ObserveMenu(); }
        public void Update() { ObserveMenu(); }
        void ObserveMenu()
        {
            if (active != this || finished || failed || HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            finished = true;
            try
            {
                string digest;
                using (var buffer = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(GameDatabase.Instance.databaseAudio.Count);
                        for (int index = 0; index < GameDatabase.Instance.databaseAudio.Count; index++)
                        {
                            AudioClip clip = GameDatabase.Instance.databaseAudio[index];
                            writer.Write(clip != null); if (clip == null) continue;
                            writer.Write(clip.name ?? ""); writer.Write(clip.channels); writer.Write(clip.frequency); writer.Write(clip.samples);
                            writer.Write(clip.length); writer.Write((int)clip.loadType); writer.Write((int)clip.loadState);
                            writer.Write(clip.preloadAudioData); writer.Write(clip.loadInBackground);
                            var files = GameDatabase.Instance.databaseAudioFiles;
                            writer.Write(index < files.Count && files[index] != null ? files[index].url : "");
                        }
                    }
                    buffer.Position = 0;
                    using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(buffer)).Replace("-", "").ToLowerInvariant();
                }
                status = candidate ? "menu-observed" : "observation-only"; Cleanup();
                Debug.Log("[Continuum startup audio] run=" + runId + " status=" + status + " candidate=" + candidate +
                    " count=" + GameDatabase.Instance.databaseAudio.Count + " orderedMetadataSha256=" + digest +
                    " hits=" + hits + " fallback=" + fallback + " skipped=" + skipped + " completedWaitsDrained=" + drained +
                    " peakSlots=" + peakSlots + " peakEncodedBytes=" + peakEncoded + " disposedRequests=" + disposed +
                    " remainingRequests=" + requests.Count + " memoryContract=encoded-budget-not-decoded-memory-cap qualification=metadata-only-not-audio-sample-or-playback-equivalence");
            }
            catch (Exception error) { failed = true; status = "observation-failed"; Cleanup(); Debug.LogException(error); Application.Quit(2); }
        }
        public void OnDestroy() { if (active == this) { Cleanup(); active = null; } }
        public void OnApplicationQuit() { if (active == this) Cleanup(); }
    }
}
