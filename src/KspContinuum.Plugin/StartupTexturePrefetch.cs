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
    public sealed class StartupTexturePrefetch : MonoBehaviour
    {
        const string Owner = "continuum.startup-texture-prefetch";
        const string AssemblySha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int Slots = 8;
        const long DecodedLimit = 128L * 1024 * 1024, EncodedLimit = 32L * 1024 * 1024;
        static StartupTexturePrefetch active;
        [ThreadStatic] static object currentIterator;
        readonly Queue<Job> waiting = new Queue<Job>();
        readonly Dictionary<string, Job> pending = new Dictionary<string, Job>(StringComparer.Ordinal);
        readonly Dictionary<UnityWebRequest, Job> requests = new Dictionary<UnityWebRequest, Job>();
        readonly Dictionary<object, Job> consumed = new Dictionary<object, Job>();
        readonly HashSet<string> retired = new HashSet<string>(StringComparer.Ordinal);
        readonly List<MethodBase> targets = new List<MethodBase>();
        Harmony harmony;
        MethodInfo getTexture, getContent;
        bool candidate, initialized, finished, failed;
        int hits, misses, skipped, peakSlots, completedWaits, cleaned;
        long decodedBytes, encodedBytes, peakDecoded, peakEncoded;
        double stageStarted;
        string root, runId, status = "not-started";

        sealed class Job
        {
            public string Uri;
            public long Decoded, Encoded;
            public UnityWebRequest Request;
            public UnityWebRequestAsyncOperation Operation;
        }
        public struct MoveState { public object Previous; }

        public void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            candidate = arguments.Contains("--continuum-startup-texture-prefetch");
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
                Type textureRequests = Type.GetType("UnityEngine.Networking.UnityWebRequestTexture, UnityEngine.UnityWebRequestTextureModule", true);
                Type textureHandler = Type.GetType("UnityEngine.Networking.DownloadHandlerTexture, UnityEngine.UnityWebRequestTextureModule", true);
                getTexture = AccessTools.DeclaredMethod(textureRequests, "GetTexture", new[] { typeof(string) });
                getContent = AccessTools.DeclaredMethod(textureHandler, "GetContent", new[] { typeof(UnityWebRequest) });
                if (getTexture == null || getContent == null) throw new MissingMethodException("Texture request seam differs.");
                foreach (string name in new[] { "DatabaseLoaderTexture_PNG", "DatabaseLoaderTexture_JPEG" })
                {
                    Type loader = assembly.GetType(name, true);
                    Type iterator = loader.GetNestedType("<Load>d__0", BindingFlags.NonPublic);
                    MethodInfo move = iterator == null ? null : AccessTools.DeclaredMethod(iterator, "MoveNext", Type.EmptyTypes);
                    MethodInfo load = AccessTools.DeclaredMethod(loader, "Load", new[] { typeof(UrlDir.UrlFile), typeof(FileInfo) });
                    if (move == null || load == null || move.ReturnType != typeof(bool) || load.ReturnType != typeof(IEnumerator))
                        throw new MissingMethodException("Pinned texture loader differs.");
                    targets.Add(move); targets.Add(load);
                }
                if (targets.Any(target => Harmony.GetPatchInfo(target)?.Owners.Count > 0)) throw new InvalidOperationException("Texture loader has another patch owner.");
                harmony = new Harmony(Owner);
                for (int i = 0; i < targets.Count; i += 2)
                {
                    harmony.Patch(targets[i], prefix: new HarmonyMethod(typeof(StartupTexturePrefetch), nameof(MovePrefix)),
                        postfix: new HarmonyMethod(typeof(StartupTexturePrefetch), nameof(MovePostfix)),
                        transpiler: new HarmonyMethod(typeof(StartupTexturePrefetch), nameof(Transpile)),
                        finalizer: new HarmonyMethod(typeof(StartupTexturePrefetch), nameof(MoveFinalizer)));
                    harmony.Patch(targets[i + 1], postfix: new HarmonyMethod(typeof(StartupTexturePrefetch), nameof(LoadPostfix)));
                }
                if (targets.Any(target => Harmony.GetPatchInfo(target) == null || Harmony.GetPatchInfo(target).Owners.Any(owner => owner != Owner)))
                    throw new InvalidOperationException("Texture patch ownership differs.");
                status = "installed";
            }
            catch (Exception error) { failed = true; status = "abstained:" + error.Message; Cleanup(); Debug.LogException(error); Application.Quit(2); }
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            MethodInfo send = AccessTools.DeclaredMethod(typeof(UnityWebRequest), "SendWebRequest", Type.EmptyTypes);
            if (active == null || body.Count(instruction => instruction.Calls(active.getTexture)) != 1 || body.Count(instruction => instruction.Calls(send)) != 1)
                throw new InvalidOperationException("Pinned texture request call counts differ.");
            foreach (CodeInstruction instruction in body)
            {
                if (instruction.Calls(active.getTexture)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupTexturePrefetch), nameof(Request)); }
                else if (instruction.Calls(send)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(StartupTexturePrefetch), nameof(Send)); }
            }
            return body;
        }
        static void MovePrefix(object __instance, out MoveState __state)
        { __state = new MoveState { Previous = currentIterator }; currentIterator = __instance; }
        static void MovePostfix(object __instance, bool __result)
        { if (!__result && active != null) active.ReleaseConsumed(__instance); }
        static Exception MoveFinalizer(object __instance, MoveState __state, Exception __exception)
        {
            currentIterator = __state.Previous;
            if (__exception != null && active != null) { active.failed = true; active.status = "loader-exception"; active.ReleaseConsumed(__instance); active.Cleanup(); }
            return __exception;
        }
        static void LoadPostfix(ref IEnumerator __result)
        { if (active != null && active.candidate) __result = DrainCompleted(__result); }
        static IEnumerator DrainCompleted(IEnumerator original)
        {
            try
            {
                while (original.MoveNext())
                {
                    var operation = original.Current as UnityWebRequestAsyncOperation;
                    if (operation != null && operation.isDone && active != null && active.candidate)
                    { active.completedWaits++; continue; }
                    yield return original.Current;
                }
            }
            finally
            {
                if (active != null) active.ReleaseConsumed(original);
                (original as IDisposable)?.Dispose();
            }
        }
        static UnityWebRequest Request(string uri)
        {
            StartupTexturePrefetch owner = active;
            if (owner == null) throw new InvalidOperationException("Texture broker unavailable.");
            owner.Initialize(); owner.Pump();
            Job job;
            if (currentIterator != null && owner.pending.TryGetValue(uri, out job))
            {
                owner.pending.Remove(uri); owner.consumed.Add(currentIterator, job); owner.hits++; return job.Request;
            }
            owner.misses++;
            owner.retired.Add(uri);
            return (UnityWebRequest)owner.getTexture.Invoke(null, new object[] { uri });
        }
        static UnityWebRequestAsyncOperation Send(UnityWebRequest request)
        {
            Job job;
            if (active != null && active.requests.TryGetValue(request, out job)) return job.Operation;
            return request.SendWebRequest();
        }

        void Initialize()
        {
            if (initialized) return;
            initialized = true; stageStarted = Time.realtimeSinceStartup; status = "running";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (UrlDir.UrlFile file in GameDatabase.Instance.root.GetFiles(UrlDir.FileType.Texture))
            {
                if (file.fileExtension != "png") continue;
                string path = Path.GetFullPath(file.fullPath), uri = KSPUtil.ApplicationFileProtocol + file.fullPath;
                if (!seen.Add(uri) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { skipped++; continue; }
                try
                {
                    for (string part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                        if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Symlink texture.");
                    long bytes = new FileInfo(path).Length;
                    byte[] header = new byte[24];
                    using (var stream = File.OpenRead(path))
                    {
                        int offset = 0, amount;
                        while (offset < header.Length && (amount = stream.Read(header, offset, header.Length - offset)) > 0) offset += amount;
                        if (offset != header.Length) throw new InvalidDataException("PNG header is short.");
                    }
                    byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                    if (!header.Take(8).SequenceEqual(signature) || header[12] != 73 || header[13] != 72 || header[14] != 68 || header[15] != 82)
                        throw new InvalidDataException("PNG header differs.");
                    long width = BigEndian(header, 16), height = BigEndian(header, 20);
                    long decoded = checked(width * height * 4);
                    if (width < 1 || height < 1 || width > 8192 || height > 8192 || decoded > DecodedLimit || bytes < 24 || bytes > EncodedLimit)
                        throw new InvalidDataException("Texture exceeds broker budget.");
                    waiting.Enqueue(new Job { Uri = uri, Decoded = decoded, Encoded = bytes });
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is OverflowException)
                { skipped++; }
            }
        }
        static long BigEndian(byte[] bytes, int index)
        { return ((long)bytes[index] << 24) | ((long)bytes[index + 1] << 16) | ((long)bytes[index + 2] << 8) | bytes[index + 3]; }
        void Pump()
        {
            while (!failed && waiting.Count > 0 && requests.Count < Slots)
            {
                Job job = waiting.Peek();
                if (retired.Contains(job.Uri)) { waiting.Dequeue(); skipped++; continue; }
                if (decodedBytes + job.Decoded > DecodedLimit || encodedBytes + job.Encoded > EncodedLimit) return;
                waiting.Dequeue();
                try
                {
                    job.Request = (UnityWebRequest)getTexture.Invoke(null, new object[] { job.Uri });
                    job.Operation = job.Request.SendWebRequest();
                    requests.Add(job.Request, job); pending.Add(job.Uri, job);
                    decodedBytes += job.Decoded; encodedBytes += job.Encoded;
                    peakSlots = Math.Max(peakSlots, requests.Count); peakDecoded = Math.Max(peakDecoded, decodedBytes); peakEncoded = Math.Max(peakEncoded, encodedBytes);
                }
                catch { job.Request?.Dispose(); skipped++; }
            }
        }
        void ReleaseConsumed(object iterator)
        {
            Job job;
            if (!consumed.TryGetValue(iterator, out job)) return;
            consumed.Remove(iterator); requests.Remove(job.Request);
            decodedBytes -= job.Decoded; encodedBytes -= job.Encoded;
            if (!job.Request.isDone) job.Request.Abort();
            job.Request.Dispose(); cleaned++;
            Pump();
        }
        void Cleanup()
        {
            if (harmony != null) { harmony.UnpatchAll(Owner); harmony = null; }
            foreach (Job job in pending.Values)
            {
                if (job.Request.isDone && !job.Request.isNetworkError && !job.Request.isHttpError)
                {
                    try { var texture = getContent.Invoke(null, new object[] { job.Request }) as Texture2D; if (texture != null) UnityEngine.Object.Destroy(texture); }
                    catch { }
                }
            }
            foreach (Job job in requests.Values) { if (!job.Request.isDone) job.Request.Abort(); job.Request.Dispose(); cleaned++; }
            pending.Clear(); requests.Clear(); consumed.Clear(); waiting.Clear(); retired.Clear(); decodedBytes = 0; encodedBytes = 0;
        }
        public static void ObserveForMenu() { if (active != null) active.ObserveMenu(); }
        public void Update() { ObserveMenu(); }
        void ObserveMenu()
        {
            if (active != this || finished || failed) return;
            if (HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            finished = true;
            try
            {
                string digest;
                using (var buffer = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(GameDatabase.Instance.databaseTexture.Count);
                        foreach (GameDatabase.TextureInfo info in GameDatabase.Instance.databaseTexture)
                        {
                            writer.Write(info != null); if (info == null) continue;
                            writer.Write(info.name ?? ""); writer.Write(info.file == null ? "" : info.file.url);
                            writer.Write(info.isNormalMap); writer.Write(info.isReadable); writer.Write(info.isCompressed);
                            Texture2D texture = info.texture; writer.Write(texture != null); if (texture == null) continue;
                            writer.Write(texture.name ?? ""); writer.Write(texture.width); writer.Write(texture.height); writer.Write((int)texture.format);
                            writer.Write(texture.mipmapCount); writer.Write(texture.isReadable); writer.Write((int)texture.filterMode);
                            writer.Write((int)texture.wrapMode); writer.Write(texture.anisoLevel);
                        }
                    }
                    buffer.Position = 0;
                    using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(buffer)).Replace("-", "").ToLowerInvariant();
                }
                double toMenu = initialized ? Time.realtimeSinceStartup - stageStarted : 0;
                status = candidate ? "menu-observed" : "observation-only";
                Cleanup();
                Debug.Log("[Continuum startup textures] run=" + runId + " status=" + status + " candidate=" + candidate +
                    " count=" + GameDatabase.Instance.databaseTexture.Count + " orderedMetadataSha256=" + digest +
                    " hits=" + hits + " fallback=" + misses + " skipped=" + skipped + " completedWaitsDrained=" + completedWaits +
                    " peakSlots=" + peakSlots + " peakEstimatedDecodedBytes=" + peakDecoded + " peakEncodedBytes=" + peakEncoded +
                    " disposedRequests=" + cleaned + " remainingRequests=" + requests.Count + " firstTextureToMenuSeconds=" + toMenu.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) +
                    " qualification=metadata-only-not-pixel-or-flight-equivalence");
            }
            catch (Exception error) { failed = true; status = "observation-failed"; Cleanup(); Debug.LogException(error); Application.Quit(2); }
        }
        public void OnDestroy() { if (active == this) { Cleanup(); active = null; } }
        public void OnApplicationQuit() { if (active == this) Cleanup(); }
    }
}
