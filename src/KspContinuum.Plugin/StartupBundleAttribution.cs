using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupBundleAttribution : MonoBehaviour
    {
        const string Owner = "continuum.startup-bundle-attribution";
        const string Marker = "[Continuum startup bundles] ";
        static readonly Guid ExpectedMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static StartupBundleAttribution instance;
        [ThreadStatic] static CacheFrame cache;
        readonly Metric data = new Metric(), hash = new Metric(), load = new Metric(), move = new Metric();
        readonly Metric fileVerification = new Metric(), fileLoad = new Metric();
        Harmony harmony;
        MethodInfo target;
        FieldInfo folderField, nameField, bundleField, hashField, stateField;
        bool requested, exported, installed, profiling, reuseData, fileBundles, cachedBundles, nativeAdmission, preparedBundles;
        long reusedReads, fileAttempts, fileSuccesses, fileFallbacks;
        string lastFallback = "none";
        string cacheRoot, cacheManifestSha;
        string admissionSha;
        CacheManifest cacheManifest;
        string status = "not-requested";

        [Serializable] public sealed class CacheManifest
        {
            public string schema = null, status = null, engine = null, compression = null, instanceRoot = null;
            public CacheEntry[] bundles = null;
        }
        [Serializable] public sealed class CacheEntry
        {
            public string relativePath = null, originalPath = null, derivedPath = null, sourceSha256 = null, sourceMd5 = null, derivedSha256 = null;
            public long sourceLength = 0, derivedLength = 0;
        }
        [Serializable] public sealed class CacheOwner { public string schema = null, scope = null; }
        [Serializable] public sealed class CacheAdmission
        {
            public string schema = null, runId = null, manifestSha256 = null, instanceRoot = null, cacheRoot = null, engine = null;
            public string makinghistory_assets_sourceSha256 = null, makinghistory_assets_derivedSha256 = null;
            public string makinghistory_scene_sourceSha256 = null, makinghistory_scene_derivedSha256 = null;
        public string serenity_assets_sourceSha256 = null, serenity_assets_derivedSha256 = null;
            public string makinghistory_assets_sourceMd5 = null, makinghistory_scene_sourceMd5 = null, serenity_assets_sourceMd5 = null;
        }

        sealed class CacheFrame
        {
            public DownloadHandler Handler;
            public byte[] Bytes;
            public bool HasValue;
            public object Iterator;
            public byte[] OriginalHash;
        }
        struct MoveState { public long Started; public CacheFrame Previous; public CacheFrame Current; }

        sealed class Metric
        {
            public long Calls, Ticks, Bytes, MaximumTicks, Errors;
            public void Add(long started, int bytes, bool succeeded)
            {
                long elapsed = Stopwatch.GetTimestamp() - started;
                Calls++; Ticks += elapsed; Bytes += bytes;
                if (elapsed > MaximumTicks) MaximumTicks = elapsed;
                if (!succeeded) Errors++;
            }
            public string Summary(string name)
            {
                return name + " calls=" + Calls + " bytes=" + Bytes + " seconds=" +
                    ((double)Ticks / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture) +
                    " maximumSeconds=" + ((double)MaximumTicks / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture) +
                    " errors=" + Errors;
            }
        }

        public void Awake()
        {
            profiling = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-profile") >= 0;
            reuseData = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-reuse-bundle-data") >= 0;
            fileBundles = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-file-bundles") >= 0;
            cachedBundles = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-cached-bundles") >= 0;
            nativeAdmission = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-native-admission") >= 0;
            preparedBundles = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-prepared-bundles") >= 0;
            requested = profiling || reuseData || fileBundles || cachedBundles || nativeAdmission || preparedBundles;
            if (!requested) return;
            DontDestroyOnLoad(gameObject);
            try
            {
                if (instance != null) throw new InvalidOperationException("Attribution owner already exists.");
                if (nativeAdmission && !cachedBundles) throw new InvalidOperationException("Native admission requires the cached bundle candidate.");
                if (preparedBundles && (!cachedBundles || !nativeAdmission)) throw new InvalidOperationException("Prepared bundles require native cache admission.");
                if (cachedBundles && fileBundles) throw new InvalidOperationException("Select one bundle load candidate.");
                if (cachedBundles) ReadCacheManifest();
                if (nativeAdmission) ReadNativeAdmission();
                Assembly assembly = typeof(Expansions.BundleLoader).Assembly;
                if (assembly.ManifestModule.ModuleVersionId != ExpectedMvid)
                    throw new InvalidOperationException("Pinned Assembly-CSharp MVID differs.");
                Type iterator = typeof(Expansions.BundleLoader).GetNestedType("<LoadAssetBundle>d__16", BindingFlags.NonPublic);
                target = iterator == null ? null : AccessTools.DeclaredMethod(iterator, "MoveNext", Type.EmptyTypes);
                if (target == null || target.MetadataToken != 100711452 || target.ReturnType != typeof(bool) ||
                    target.IsStatic || target.GetMethodBody() == null)
                    throw new InvalidOperationException("Pinned bundle iterator differs.");
                folderField = AccessTools.DeclaredField(iterator, "folderPath");
                nameField = AccessTools.DeclaredField(iterator, "bundleName");
                bundleField = AccessTools.DeclaredField(iterator, "<bundle>5__3");
                hashField = AccessTools.DeclaredField(iterator, "<hash>5__2");
                stateField = AccessTools.DeclaredField(iterator, "<>1__state");
                if (folderField == null || nameField == null || folderField.FieldType != typeof(string) || nameField.FieldType != typeof(string))
                    throw new InvalidOperationException("Pinned bundle path fields differ.");
                if (bundleField == null || hashField == null || stateField == null || bundleField.FieldType != typeof(AssetBundle) ||
                    hashField.FieldType != typeof(byte[]) || stateField.FieldType != typeof(int))
                    throw new InvalidOperationException("Pinned prepared bundle fields differ.");
                Patches existing = Harmony.GetPatchInfo(target);
                if (existing != null && existing.Owners.Count != 0)
                    throw new InvalidOperationException("Bundle iterator has an existing Harmony owner.");
                instance = this;
                harmony = new Harmony(Owner);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(StartupBundleAttribution), "MovePrefix")),
                    transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(StartupBundleAttribution), "Transpile")),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(StartupBundleAttribution), "MoveFinalizer")));
                Patches installedPatches = Harmony.GetPatchInfo(target);
                if (installedPatches == null || installedPatches.Prefixes.Count != 1 || installedPatches.Transpilers.Count != 1 ||
                    installedPatches.Finalizers.Count != 1 || installedPatches.Owners.Any(owner => owner != Owner))
                    throw new InvalidOperationException("Bundle attribution ownership verification failed.");
                installed = true; status = "installed";
            }
            catch (Exception error)
            {
                status = "abstained:" + error.GetType().Name + ":" + error.Message;
                Cleanup(); Export();
                Application.Quit(2);
            }
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var result = instructions.ToList();
            MethodInfo dataMethod = AccessTools.PropertyGetter(typeof(DownloadHandler), "data");
            MethodInfo hashMethod = AccessTools.DeclaredMethod(typeof(HashAlgorithm), "ComputeHash", new[] { typeof(byte[]) });
            MethodInfo loadMethod = AccessTools.DeclaredMethod(typeof(AssetBundle), "LoadFromMemory", new[] { typeof(byte[]) });
            MethodInfo[] originals = { dataMethod, hashMethod, loadMethod };
            string[] wrappers = { "ReadData", "ComputeHash", "LoadMemory" };
            int[] expected = { 2, 1, 1 };
            int[] actual = new int[3];
            foreach (CodeInstruction instruction in result)
                for (int i = 0; i < originals.Length; i++)
                    if (originals[i] != null && instruction.Calls(originals[i])) actual[i]++;
            if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("Pinned bundle call shape differs.");
            if (instance != null && instance.preparedBundles)
            {
                StartupPreparedBundleBoundary.Inject(result, generator);
            }
            foreach (CodeInstruction instruction in result)
                for (int i = 0; i < originals.Length; i++)
                    if (instruction.Calls(originals[i]))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.DeclaredMethod(typeof(StartupBundleAttribution), wrappers[i]);
                        break;
                    }
            return result;
        }

        static byte[] ReadData(DownloadHandler handler)
        {
            if (instance != null && instance.reuseData && cache != null && cache.HasValue && ReferenceEquals(cache.Handler, handler))
            {
                if (instance != null) instance.reusedReads++;
                return cache.Bytes;
            }
            bool profile = instance != null && instance.profiling;
            if (!profile)
            {
                byte[] bytes = handler.data;
                if (cache != null) { cache.Handler = handler; cache.Bytes = bytes; cache.HasValue = true; }
                return bytes;
            }
            long started = Stopwatch.GetTimestamp(); byte[] result = null; bool succeeded = false;
            try
            {
                result = handler.data; succeeded = true;
                if (cache != null) { cache.Handler = handler; cache.Bytes = result; cache.HasValue = true; }
                return result;
            }
            finally { if (instance != null) instance.data.Add(started, result == null ? 0 : result.Length, succeeded); }
        }
        static byte[] ComputeHash(HashAlgorithm algorithm, byte[] bytes)
        {
            if (instance == null || !instance.profiling)
            {
                byte[] result = algorithm.ComputeHash(bytes);
                if (cache != null) cache.OriginalHash = result;
                return result;
            }
            long started = Stopwatch.GetTimestamp(); bool succeeded = false;
            try
            {
                byte[] result = algorithm.ComputeHash(bytes); succeeded = true;
                if (cache != null) cache.OriginalHash = result;
                return result;
            }
            finally { if (instance != null) instance.hash.Add(started, bytes == null ? 0 : bytes.Length, succeeded); }
        }
        static AssetBundle LoadMemory(byte[] bytes)
        {
            if (instance != null && (instance.fileBundles || instance.cachedBundles))
            {
                AssetBundle fileResult;
                if (instance.TryFileBundle(bytes, out fileResult)) return fileResult;
            }
            if (instance == null || !instance.profiling) return AssetBundle.LoadFromMemory(bytes);
            long started = Stopwatch.GetTimestamp(); bool succeeded = false;
            try { AssetBundle result = AssetBundle.LoadFromMemory(bytes); succeeded = true; return result; }
            finally { if (instance != null) instance.load.Add(started, bytes == null ? 0 : bytes.Length, succeeded); }
        }
        bool TryFileBundle(byte[] bytes, out AssetBundle result)
        {
            fileAttempts++; result = null;
            long verifyStarted = Stopwatch.GetTimestamp(); bool verified = false;
            string path, originalPath; long length, originalLength; DateTime modified, originalModified;
            try
            {
                if (cache == null || cache.Iterator == null || cache.OriginalHash == null || bytes == null)
                    throw new InvalidOperationException("Missing iterator integrity context.");
                string location = (string)folderField.GetValue(cache.Iterator) + (string)nameField.GetValue(cache.Iterator);
                Uri uri;
                if (!Uri.TryCreate(location, UriKind.Absolute, out uri) || !uri.IsFile || !string.IsNullOrEmpty(uri.Host))
                    throw new InvalidOperationException("Nonlocal bundle URI.");
                path = Path.GetFullPath(uri.LocalPath);
                string root = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "SquadExpansion"));
                if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException("Bundle path escapes expansion root.");
                for (string entry = path; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Symlink bundle path.");
                var before = new FileInfo(path); length = before.Length; modified = before.LastWriteTimeUtc;
                originalPath = path; originalLength = length; originalModified = modified;
                if (length != bytes.LongLength) throw new InvalidOperationException("Bundle length differs.");
                if (!cachedBundles)
                {
                    byte[] digest;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var md5 = MD5.Create()) digest = md5.ComputeHash(stream);
                    if (!digest.SequenceEqual(cache.OriginalHash)) throw new InvalidOperationException("Bundle content hash differs.");
                }
                var afterHash = new FileInfo(path);
                if (afterHash.Length != length || afterHash.LastWriteTimeUtc != modified)
                    throw new InvalidOperationException("Bundle changed during verification.");
                if (cachedBundles)
                {
                    CacheEntry entry = cacheManifest.bundles.SingleOrDefault(item => Path.GetFullPath(item.originalPath) == path);
                    if (entry == null || entry.sourceLength != length || entry.sourceMd5 != Hex(cache.OriginalHash) ||
                        (!nativeAdmission && Digest(path) != entry.sourceSha256))
                        throw new InvalidOperationException("Original cache provenance differs.");
                    string derived = Path.GetFullPath(entry.derivedPath);
                    if (!derived.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidOperationException("Derived path escapes cache root.");
                    RequireNoSymlinks(derived);
                    var beforeDerived = new FileInfo(derived); long derivedLength = beforeDerived.Length;
                    DateTime derivedModified = beforeDerived.LastWriteTimeUtc;
                    if (derivedLength != entry.derivedLength || (!nativeAdmission && Digest(derived) != entry.derivedSha256))
                        throw new InvalidOperationException("Derived cache integrity differs.");
                    var afterDerived = new FileInfo(derived);
                    if (afterDerived.Length != derivedLength || afterDerived.LastWriteTimeUtc != derivedModified)
                        throw new InvalidOperationException("Derived cache changed during verification.");
                    path = derived; length = derivedLength; modified = derivedModified;
                }
                verified = true;
            }
            catch (Exception error)
            {
                fileFallbacks++; lastFallback = error.GetType().Name + ":" + error.Message; return false;
            }
            finally { fileVerification.Add(verifyStarted, bytes == null ? 0 : bytes.Length, verified); }
            long loadStarted = Stopwatch.GetTimestamp(); bool loaded = false;
            try
            {
                result = AssetBundle.LoadFromFile(path);
                var afterLoad = new FileInfo(path);
                var originalAfterLoad = new FileInfo(originalPath);
                if (result == null || afterLoad.Length != length || afterLoad.LastWriteTimeUtc != modified ||
                    originalAfterLoad.Length != originalLength || originalAfterLoad.LastWriteTimeUtc != originalModified)
                    throw new InvalidOperationException("File load failed or bundle changed during load.");
                loaded = true; fileSuccesses++; return true;
            }
            catch (Exception error)
            {
                if (result != null) result.Unload(true);
                result = null; fileFallbacks++; lastFallback = error.GetType().Name + ":" + error.Message; return false;
            }
            finally { fileLoad.Add(loadStarted, bytes.Length, loaded); }
        }
        static void MovePrefix(object __instance, out MoveState __state)
        {
            __state = new MoveState { Started = instance != null && instance.profiling ? Stopwatch.GetTimestamp() : 0, Previous = cache };
            if (instance != null && (instance.reuseData || instance.fileBundles || instance.cachedBundles))
            { __state.Current = new CacheFrame { Iterator = __instance }; cache = __state.Current; }
        }
        static bool TryPreparedBundle(object iterator)
        {
            return instance != null && instance.preparedBundles && instance.PrepareBundle(iterator);
        }
        bool PrepareBundle(object iterator)
        {
            AssetBundle loaded = null; bool published = false, fieldsTouched = false;
            try
            {
                string location = (string)folderField.GetValue(iterator) + (string)nameField.GetValue(iterator);
                Uri uri;
                if (!Uri.TryCreate(location, UriKind.Absolute, out uri) || !uri.IsFile || !string.IsNullOrEmpty(uri.Host)) return false;
                string original = Path.GetFullPath(uri.LocalPath);
                CacheEntry entry = cacheManifest.bundles.SingleOrDefault(item => Path.GetFullPath(item.originalPath) == original);
                if (entry == null) return false;
                fileAttempts++;
                long started = Stopwatch.GetTimestamp(); bool verified = false;
                string derived; long sourceLength, derivedLength; DateTime sourceModified, derivedModified;
                try
                {
                    RequireNoSymlinks(original); derived = Path.GetFullPath(entry.derivedPath);
                    if (!derived.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Prepared cache containment differs.");
                    RequireNoSymlinks(derived);
                    var source = new FileInfo(original); var targetFile = new FileInfo(derived);
                    sourceLength = source.Length; sourceModified = source.LastWriteTimeUtc;
                    derivedLength = targetFile.Length; derivedModified = targetFile.LastWriteTimeUtc;
                    if (sourceLength != entry.sourceLength || derivedLength != entry.derivedLength) throw new InvalidOperationException("Prepared file lengths differ.");
                    verified = true;
                }
                finally { fileVerification.Add(started, 0, verified); }
                long loading = Stopwatch.GetTimestamp(); bool succeeded = false;
                try
                {
                    loaded = AssetBundle.LoadFromFile(derived);
                    var sourceAfter = new FileInfo(original); var derivedAfter = new FileInfo(derived);
                    if (loaded == null || sourceAfter.Length != sourceLength || sourceAfter.LastWriteTimeUtc != sourceModified ||
                        derivedAfter.Length != derivedLength || derivedAfter.LastWriteTimeUtc != derivedModified)
                        throw new InvalidOperationException("Prepared load failed or file changed.");
                    succeeded = true;
                }
                finally { fileLoad.Add(loading, 0, succeeded); }
                var originalMd5 = new byte[16];
                for (int i = 0; i < originalMd5.Length; i++) originalMd5[i] = byte.Parse(entry.sourceMd5.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                fieldsTouched = true;
                hashField.SetValue(iterator, originalMd5); bundleField.SetValue(iterator, loaded); stateField.SetValue(iterator, -3);
                published = true; fileSuccesses++; return true;
            }
            catch (Exception error)
            {
                if (fieldsTouched && !published)
                {
                    hashField.SetValue(iterator, null); bundleField.SetValue(iterator, null); stateField.SetValue(iterator, -1);
                }
                if (loaded != null && !published) loaded.Unload(true);
                fileFallbacks++; lastFallback = error.GetType().Name + ":" + error.Message;
                return false;
            }
        }
        static Exception MoveFinalizer(MoveState __state, Exception __exception)
        {
            if (__state.Current != null)
            { __state.Current.Bytes = null; __state.Current.Handler = null; __state.Current.OriginalHash = null; __state.Current.Iterator = null; __state.Current.HasValue = false; }
            cache = __state.Previous;
            if (instance != null && instance.profiling) instance.move.Add(__state.Started, 0, __exception == null);
            return __exception;
        }

        public void Update()
        {
            if (!requested || exported || HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            status = installed ? "observed-synchronous-scopes-only" : status;
            if ((fileBundles || cachedBundles) && (fileAttempts != 3 || fileSuccesses != 3 || fileFallbacks != 0)) status += ":file-candidate-incomplete";
            Cleanup(); Export();
        }
        void Cleanup()
        {
            if (harmony != null)
            {
                try
                {
                    harmony.UnpatchAll(Owner);
                    Patches remaining = target == null ? null : Harmony.GetPatchInfo(target);
                    if (remaining != null && remaining.Owners.Contains(Owner)) status += ":cleanup-error";
                }
                catch (Exception error) { status += ":cleanup-error:" + error.GetType().Name; }
            }
            if (ReferenceEquals(instance, this)) instance = null;
        }
        void Export()
        {
            if (exported || !requested) return;
            exported = true;
            UnityEngine.Debug.Log(Marker + "status=" + status + " stopwatchFrequency=" + Stopwatch.Frequency +
                " profiling=" + profiling + " reuseData=" + reuseData + " reusedReads=" + reusedReads +
                " fileBundles=" + fileBundles + " fileAttempts=" + fileAttempts + " fileSuccesses=" + fileSuccesses +
                " fileFallbacks=" + fileFallbacks + " lastFallback=" + lastFallback +
                " cachedBundles=" + cachedBundles + " cacheManifestSha256=" + cacheManifestSha +
                " nativeAdmission=" + nativeAdmission + " admissionSha256=" + admissionSha +
                " preparedBundles=" + preparedBundles +
                " scope=managed-synchronous-calls excludes=awaiting-native-workers-and-startup-before-patch");
            UnityEngine.Debug.Log(Marker + data.Summary("download-data"));
            UnityEngine.Debug.Log(Marker + hash.Summary("md5"));
            UnityEngine.Debug.Log(Marker + load.Summary("load-from-memory"));
            UnityEngine.Debug.Log(Marker + move.Summary("move-next-inclusive"));
            if (fileBundles || cachedBundles)
            {
                UnityEngine.Debug.Log(Marker + fileVerification.Summary("file-integrity-verification"));
                UnityEngine.Debug.Log(Marker + fileLoad.Summary("load-from-file"));
            }
        }
        public void OnDestroy()
        {
            if (requested && !exported) { status = "interrupted-before-menu"; Cleanup(); Export(); }
        }

        void ReadCacheManifest()
        {
            string[] values = Environment.GetCommandLineArgs().Where(arg => arg.StartsWith("--continuum-startup-cache=", StringComparison.Ordinal)).ToArray();
            if (values.Length != 1) throw new InvalidOperationException("One cache path is required.");
            string value = values[0].Substring("--continuum-startup-cache=".Length);
            if (!Path.IsPathRooted(value)) throw new InvalidOperationException("Cache path must be absolute.");
            cacheRoot = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            RequireNoSymlinks(cacheRoot);
            string instanceRoot = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
            if (cacheRoot == instanceRoot || cacheRoot.StartsWith(instanceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Cache must be outside the installation.");
            string ownerPath = Path.Combine(cacheRoot, "continuum-bundle-cache.local.json");
            RequireNoSymlinks(ownerPath);
            if (new FileInfo(ownerPath).Length > 4096) throw new InvalidOperationException("Oversize cache marker.");
            CacheOwner owner = JsonUtility.FromJson<CacheOwner>(File.ReadAllText(ownerPath));
            if (owner == null || owner.schema != "continuum-bundle-cache-owner/v1" || owner.scope != "private-derived-cache")
                throw new InvalidOperationException("Cache ownership marker differs.");
            string manifestPath = Path.Combine(cacheRoot, "manifest.json"); RequireNoSymlinks(manifestPath);
            if (new FileInfo(manifestPath).Length > 65536) throw new InvalidOperationException("Oversize cache manifest.");
            cacheManifestSha = Digest(manifestPath);
            string manifestJson = File.ReadAllText(manifestPath);
            cacheManifest = JsonUtility.FromJson<CacheManifest>(manifestJson);
            string[] bundleObjects = StartupCacheManifestReader.ReadBundleObjects(manifestJson);
            if (cacheManifest != null) cacheManifest.bundles = bundleObjects.Select(item => JsonUtility.FromJson<CacheEntry>(item)).ToArray();
            if (cacheManifest == null || cacheManifest.schema != "continuum-bundle-cache/v1" || cacheManifest.status != "complete" ||
                cacheManifest.engine != Application.unityVersion || cacheManifest.compression != "LZ4Runtime" ||
                Path.GetFullPath(cacheManifest.instanceRoot).TrimEnd(Path.DirectorySeparatorChar) != instanceRoot ||
                cacheManifest.bundles == null || cacheManifest.bundles.Length != 3)
                throw new InvalidOperationException("Cache manifest configuration differs: schema=" + (cacheManifest == null ? "null" : cacheManifest.schema) +
                    " status=" + (cacheManifest == null ? "null" : cacheManifest.status) + " engine=" + (cacheManifest == null ? "null" : cacheManifest.engine) +
                    " expectedEngine=" + Application.unityVersion + " compression=" + (cacheManifest == null ? "null" : cacheManifest.compression) +
                    " root=" + (cacheManifest == null ? "null" : cacheManifest.instanceRoot) + " expectedRoot=" + instanceRoot +
                    " count=" + (cacheManifest == null || cacheManifest.bundles == null ? -1 : cacheManifest.bundles.Length));
            string[] expected = { "MakingHistory/AssetBundles/makinghistory_assets", "MakingHistory/AssetBundles/makinghistory_scene", "Serenity/AssetBundles/serenity_assets" };
            foreach (string relative in expected)
            {
                CacheEntry entry = cacheManifest.bundles.Single(item => item != null && item.relativePath == relative);
                if (Path.GetFullPath(entry.originalPath) != Path.GetFullPath(Path.Combine(instanceRoot, "GameData", "SquadExpansion", relative)) ||
                    !IsDigest(entry.sourceSha256, 64) || !IsDigest(entry.sourceMd5, 32) || !IsDigest(entry.derivedSha256, 64) ||
                    entry.sourceLength <= 0 || entry.derivedLength <= 0)
                    throw new InvalidOperationException("Cache entry contract differs.");
            }
        }
        static bool IsDigest(string value, int length)
        { return value != null && value.Length == length && value.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f'); }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        static string Digest(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }
        static void RequireNoSymlinks(string path)
        {
            for (string entry = Path.GetFullPath(path); !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symlink path is not admitted.");
        }
        void ReadNativeAdmission()
        {
            string proofPath = Argument("--continuum-startup-admission=");
            string expectedSha = Argument("--continuum-startup-admission-sha256=");
            string nonce = Argument("--continuum-research-run=");
            if (!Path.IsPathRooted(proofPath) || !IsDigest(expectedSha, 64) || nonce.Length > 256)
                throw new InvalidOperationException("Admission arguments differ from contract.");
            proofPath = Path.GetFullPath(proofPath); RequireNoSymlinks(proofPath);
            if (new FileInfo(proofPath).Length > 16384) throw new InvalidOperationException("Oversize admission proof.");
            byte[] proofBytes = File.ReadAllBytes(proofPath);
            using (var sha = SHA256.Create()) admissionSha = Hex(sha.ComputeHash(proofBytes));
            if (admissionSha != expectedSha) throw new InvalidOperationException("Admission proof hash differs.");
            CacheAdmission proof = JsonUtility.FromJson<CacheAdmission>(new UTF8Encoding(false, true).GetString(proofBytes));
            string instanceRoot = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
            if (proof == null || proof.schema != "continuum-startup-cache-admission/v1" || proof.runId != nonce ||
                proof.manifestSha256 != cacheManifestSha || proof.instanceRoot != instanceRoot ||
                proof.cacheRoot != cacheRoot || proof.engine != Application.unityVersion)
                throw new InvalidOperationException("Admission identity or configuration differs.");
            foreach (CacheEntry entry in cacheManifest.bundles)
            {
                string name = Path.GetFileName(entry.originalPath);
                FieldInfo source = typeof(CacheAdmission).GetField(name + "_sourceSha256");
                FieldInfo derived = typeof(CacheAdmission).GetField(name + "_derivedSha256");
                if (source == null || derived == null || (string)source.GetValue(proof) != entry.sourceSha256 ||
                    (string)derived.GetValue(proof) != entry.derivedSha256)
                    throw new InvalidOperationException("Admission bundle provenance differs.");
                if (preparedBundles)
                {
                    FieldInfo md5 = typeof(CacheAdmission).GetField(name + "_sourceMd5");
                    if (md5 == null || (string)md5.GetValue(proof) != entry.sourceMd5 || !IsDigest(entry.sourceMd5, 32))
                        throw new InvalidOperationException("Prepared original MD5 admission differs.");
                }
            }
        }
        static string Argument(string prefix)
        {
            string[] values = Environment.GetCommandLineArgs().Where(arg => arg.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (values.Length != 1 || values[0].Length == prefix.Length) throw new InvalidOperationException("Exactly one argument is required: " + prefix);
            return values[0].Substring(prefix.Length);
        }
    }
}
