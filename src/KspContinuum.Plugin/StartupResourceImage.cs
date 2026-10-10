using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Serialization;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [Serializable] public sealed class ResourceImageEntry
    {
        public string source, url, loader, sourceSha256, payloadSha256, payload;
        public long sourceLength, sourceModifiedTicks, payloadLength, payloadModifiedTicks;
        public int width, height, format, graphicsFormat, mipCount, filter, wrap, aniso;
        public float mipBias;
        public bool linear, actualReadable, normalMap, infoReadable, infoCompressed;
    }
    [Serializable] public sealed class ResourceImageManifest
    {
        public string schema = "continuum-startup-resource-image/v1", status, engine, platform, instanceRoot, assemblySha256;
        public string qualification = "private-cooked-raw-textures-no-sample-or-flight-equivalence";
        public string renderer, colorSpace, producerFingerprint;
        public string captureRecipe;
        public int quality, masterTextureLimit;
        public ResourceImageEntry[] entries;
    }
    [Serializable] public sealed class ResourceImageOwner
    {
        public string schema = "continuum-startup-resource-image-owner/v1", scope = "private-derived-texture-image";
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupResourceImage : MonoBehaviour
    {
        const string Owner = "continuum.startup-resource-image";
        const string CaptureRecipe = "stock-raw-mips-png-normal-readability-restored/v2";
        const string AssemblySha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const long MaximumPayload = 512L * 1024 * 1024;
        static StartupResourceImage active;
        [ThreadStatic] static CookFrame currentCook;
        [ThreadStatic] static CookFrame normalCook;
        [ThreadStatic] static bool discardingRetainedPixels;
        readonly List<ResourceImageEntry> cooked = new List<ResourceImageEntry>();
        readonly Dictionary<string, ResourceImageEntry> entries = new Dictionary<string, ResourceImageEntry>(StringComparer.Ordinal);
        Harmony harmony;
        MethodInfo rawBytes, graphicsFormat, isSrgb, setObject, setSuccessful;
        string mode, directory, root, runId, producerFingerprint;
        ResourceImageManifest frozenProfile;
        bool finished, failed;
        int captures, hits, misses, errors, unrepresented;
        int retainedNormalApplies, restoredNormalReadability;
        long capturedBytes, restoredBytes;
        double verificationSeconds, publicationSeconds;

        sealed class Captured
        {
            public byte[] Bytes;
            public int Width, Height, Format, GraphicsFormat, Mips;
            public bool Linear;
        }
        sealed class CookFrame
        {
            public DatabaseLoader<GameDatabase.TextureInfo> Loader;
            public UrlDir.UrlFile File;
            public FileInfo Source;
            public long Length, Modified;
            public string SourceHash;
            public readonly Dictionary<Texture2D, Captured> Textures = new Dictionary<Texture2D, Captured>();
        }
        sealed class ApplyState
        {
            public CookFrame Frame;
            public bool Retained;
        }

        public void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            mode = OneArgument(arguments, "--continuum-startup-resource-image-mode=", false);
            if (mode == null) return;
            DontDestroyOnLoad(gameObject); active = this;
            try
            {
                if (mode != "prepare" && mode != "reuse") throw new InvalidOperationException("Resource image mode must be prepare or reuse.");
                if (arguments.Contains("--continuum-startup-texture-prefetch") || arguments.Contains("--continuum-startup-dds-mapped"))
                    throw new InvalidOperationException("Resource image owns the texture production boundary.");
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
                string marker = File.ReadAllText(Path.Combine(root, "continuum-lab.local.json"));
                if (JsonUtility.FromJson<LabMarker>(marker)?.scope != "disposable-lab") throw new InvalidOperationException("Disposable owned laboratory required.");
                runId = OneArgument(arguments, "--continuum-research-run=", true);
                string path = OneArgument(arguments, "--continuum-startup-resource-image=", true);
                if (!Path.IsPathRooted(path)) throw new InvalidOperationException("Absolute private resource image path required.");
                directory = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                if (directory == root || directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException("Resource image must be outside installation.");
                Assembly assembly = typeof(GameDatabase).Assembly;
                StartupPinnedGameIdentity.Require(assembly);
                rawBytes = typeof(Texture2D).GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(method => method.Name == "GetRawTextureData" && !method.IsGenericMethod && method.GetParameters().Length == 0);
                graphicsFormat = AccessTools.PropertyGetter(typeof(Texture), "graphicsFormat");
                Type formatUtility = typeof(Texture2D).Assembly.GetType("UnityEngine.Experimental.Rendering.GraphicsFormatUtility", false);
                if (graphicsFormat == null || formatUtility == null) throw new MissingMethodException("Exact texture color-format metadata unavailable.");
                isSrgb = AccessTools.DeclaredMethod(formatUtility, "IsSRGBFormat", new[] { graphicsFormat.ReturnType });
                if (isSrgb == null) throw new MissingMethodException("Texture SRGB metadata unavailable.");
                setObject = AccessTools.PropertySetter(typeof(DatabaseLoader<GameDatabase.TextureInfo>), "obj");
                setSuccessful = AccessTools.PropertySetter(typeof(DatabaseLoader<GameDatabase.TextureInfo>), "successful");
                if (setObject == null || setSuccessful == null) throw new MissingMethodException("Stock texture publication setters unavailable.");
                var loads = new List<MethodInfo>();
                foreach (string name in new[] { "DatabaseLoaderTexture_PNG", "DatabaseLoaderTexture_JPEG", "DatabaseLoaderTexture_DDS", "DatabaseLoaderTexture_MBM", "DatabaseLoaderTexture_TGA", "DatabaseLoaderTexture_TRUECOLOR" })
                {
                    Type type = assembly.GetType(name, true);
                    MethodInfo load = AccessTools.DeclaredMethod(type, "Load", new[] { typeof(UrlDir.UrlFile), typeof(FileInfo) });
                    if (load == null || load.ReturnType != typeof(IEnumerator) || Harmony.GetPatchInfo(load)?.Owners.Count > 0)
                        throw new InvalidOperationException("Texture producer differs or has another owner: " + name);
                    loads.Add(load);
                }
                if (mode == "prepare")
                {
                    if (Directory.Exists(directory) || File.Exists(directory)) throw new InvalidOperationException("Prepare requires a fresh image directory.");
                    Directory.CreateDirectory(directory); RequireNoSymlinks(directory);
                    WriteNew(Path.Combine(directory, "owner.json"), System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ResourceImageOwner())));
                    string helper = typeof(StartupResourceImage).Assembly.Location;
                    RequireSource(helper);
                    if (new FileInfo(helper).Length <= 0 || new FileInfo(helper).Length > 16L * 1024 * 1024)
                        throw new InvalidDataException("Producer helper snapshot length differs.");
                    WriteNew(Path.Combine(directory, "producer.dll"), File.ReadAllBytes(helper));
                    producerFingerprint = ProducerFingerprint();
                }
                else { producerFingerprint = ProducerFingerprint(); ReadImage(); }
                harmony = new Harmony(Owner);
                foreach (MethodInfo load in loads)
                    harmony.Patch(load, prefix: new HarmonyMethod(typeof(StartupResourceImage), nameof(LoadPrefix)), postfix: new HarmonyMethod(typeof(StartupResourceImage), nameof(LoadPostfix)));
                if (mode == "prepare")
                {
                    MethodInfo apply = AccessTools.DeclaredMethod(typeof(Texture2D), "Apply", new[] { typeof(bool), typeof(bool) });
                    MethodInfo normal = AccessTools.DeclaredMethod(typeof(GameDatabase), "BitmapToUnityNormalMap", new[] { typeof(Texture2D) });
                    if (apply == null || Harmony.GetPatchInfo(apply)?.Owners.Count > 0) throw new InvalidOperationException("Texture Apply capture boundary has another owner.");
                    if (normal == null || normal.ReturnType != typeof(Texture2D) || Harmony.GetPatchInfo(normal)?.Owners.Count > 0)
                        throw new InvalidOperationException("Stock normal conversion boundary differs or has another owner.");
                    harmony.Patch(normal, prefix: new HarmonyMethod(typeof(StartupResourceImage), nameof(NormalPrefix)), finalizer: new HarmonyMethod(typeof(StartupResourceImage), nameof(NormalFinalizer)));
                    harmony.Patch(apply, prefix: new HarmonyMethod(typeof(StartupResourceImage), nameof(ApplyPrefix)), postfix: new HarmonyMethod(typeof(StartupResourceImage), nameof(ApplyPostfix)),
                        finalizer: new HarmonyMethod(typeof(StartupResourceImage), nameof(ApplyFinalizer)));
                }
                Debug.Log("[Continuum resource image] run=" + runId + " mode=" + mode + " status=installed");
                if (mode == "reuse") StartupAssetGeneration.Admit("texture-image", entries.Count);
            }
            catch (Exception error) { StartupAssetGeneration.Fallback("texture-image", error.GetType().Name); failed = true; Cleanup(); Debug.LogException(error); Application.Quit(2); }
        }
        [Serializable] sealed class LabMarker { public string scope = null; }
        static string OneArgument(string[] arguments, string prefix, bool required)
        {
            string[] values = arguments.Where(argument => argument.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (values.Length == 0 && !required) return null;
            if (values.Length != 1 || values[0].Length == prefix.Length) throw new InvalidOperationException("One nonempty argument required: " + prefix);
            return values[0].Substring(prefix.Length);
        }
        static bool LoadPrefix(DatabaseLoader<GameDatabase.TextureInfo> __instance, UrlDir.UrlFile urlFile, FileInfo file, ref IEnumerator __result)
        {
            if (active == null || active.mode != "reuse") return true;
            GameDatabase.TextureInfo result;
            if (!active.Restore(__instance, urlFile, file, out result)) return true;
            GameDatabase.TextureInfo previousObject = __instance.obj; bool previousSuccess = __instance.successful;
            try
            {
                active.setObject.Invoke(__instance, new object[] { result });
                active.setSuccessful.Invoke(__instance, new object[] { true });
                StartupAssetGeneration.Published("texture-image");
            }
            catch (Exception error)
            {
                active.setObject.Invoke(__instance, new object[] { previousObject });
                active.setSuccessful.Invoke(__instance, new object[] { previousSuccess });
                UnityEngine.Object.Destroy(result.texture); active.errors++;
                StartupAssetGeneration.Fallback("texture-image", "publication-failed");
                Debug.LogWarning("[Continuum resource image] publication fallback: " + error.Message); return true;
            }
            __result = Empty(); return false;
        }
        static IEnumerator Empty() { yield break; }
        static void LoadPostfix(DatabaseLoader<GameDatabase.TextureInfo> __instance, UrlDir.UrlFile urlFile, FileInfo file, ref IEnumerator __result)
        {
            if (active != null && active.mode == "prepare") __result = active.Cook(__instance, urlFile, file, __result);
        }
        IEnumerator Cook(DatabaseLoader<GameDatabase.TextureInfo> loader, UrlDir.UrlFile file, FileInfo source, IEnumerator original)
        {
            var frame = new CookFrame { Loader = loader, File = file, Source = source };
            try
            {
                RequireProfile(); RequireSource(source.FullName); source.Refresh(); frame.Length = source.Length; frame.Modified = source.LastWriteTimeUtc.Ticks; frame.SourceHash = Digest(source.FullName);
            }
            catch (Exception error) { unrepresented++; Debug.LogWarning("[Continuum resource image] uncooked source: " + error.Message); }
            try
            {
                while (true)
                {
                    bool next; CookFrame previous = currentCook; currentCook = frame;
                    try { next = original.MoveNext(); }
                    finally { currentCook = previous; }
                    if (!next) break;
                    yield return original.Current;
                }
                if (loader.successful && loader.obj != null && frame.SourceHash != null) SaveCook(frame);
                else unrepresented++;
            }
            finally { frame.Textures.Clear(); (original as IDisposable)?.Dispose(); }
        }
        static void NormalPrefix(out CookFrame __state)
        {
            __state = normalCook;
            normalCook = active != null && active.mode == "prepare" && currentCook?.Loader.GetType().FullName == "DatabaseLoaderTexture_PNG" ? currentCook : null;
        }
        static Exception NormalFinalizer(CookFrame __state, Exception __exception) { normalCook = __state; return __exception; }
        static void ApplyPrefix(Texture2D __instance, bool updateMipmaps, ref bool makeNoLongerReadable, out ApplyState __state)
        {
            __state = null;
            if (discardingRetainedPixels || active == null || currentCook == null) return;
            if (!updateMipmaps) active.Capture(__instance, currentCook);
            if (!updateMipmaps || !makeNoLongerReadable) return;
            currentCook.Textures.Remove(__instance);
            if (normalCook != currentCook || __instance.format != TextureFormat.RGBA32 || !__instance.isReadable) return;
            int expectedMips = 1;
            for (int dimension = Math.Max(__instance.width, __instance.height); dimension > 1; dimension >>= 1) expectedMips++;
            if (__instance.mipmapCount != expectedMips) return;
            __state = new ApplyState { Frame = currentCook, Retained = true };
            makeNoLongerReadable = false;
            active.retainedNormalApplies++;
        }
        static void ApplyPostfix(Texture2D __instance, ApplyState __state)
        {
            if (discardingRetainedPixels || active == null) return;
            CookFrame frame = __state?.Frame ?? currentCook;
            if (frame != null && __instance.isReadable) active.Capture(__instance, frame);
        }
        static Exception ApplyFinalizer(Texture2D __instance, ApplyState __state, Exception __exception)
        {
            if (__state == null || !__state.Retained) return __exception;
            if (__exception != null) __state.Frame.Textures.Remove(__instance);
            bool previous = discardingRetainedPixels;
            try
            {
                discardingRetainedPixels = true;
                __instance.Apply(false, true);
                if (__instance.isReadable) throw new InvalidOperationException("Stock normal-map unreadable state was not restored.");
                active.restoredNormalReadability++;
            }
            catch (Exception cleanupError)
            {
                __state.Frame.Textures.Remove(__instance);
                active.errors++; active.failed = true;
                Debug.LogError("[Continuum resource image] normal capture cleanup failed: " + cleanupError);
                Application.Quit(2);
                return __exception ?? cleanupError;
            }
            finally { discardingRetainedPixels = previous; }
            return __exception;
        }
        void Capture(Texture2D texture, CookFrame frame)
        {
            try
            {
                if (!texture.isReadable) return;
                object format = graphicsFormat.Invoke(texture, null);
                byte[] bytes = (byte[])rawBytes.Invoke(texture, null);
                if (bytes == null || bytes.LongLength == 0 || bytes.LongLength > MaximumPayload) { frame.Textures.Remove(texture); return; }
                frame.Textures[texture] = new Captured { Bytes = bytes, Width = texture.width, Height = texture.height, Format = (int)texture.format,
                    GraphicsFormat = Convert.ToInt32(format), Mips = texture.mipmapCount, Linear = !(bool)isSrgb.Invoke(null, new[] { format }) };
            }
            catch (Exception error) { frame.Textures.Remove(texture); errors++; Debug.LogWarning("[Continuum resource image] capture unavailable: " + error.Message); }
        }
        void SaveCook(CookFrame frame)
        {
            try
            {
                Texture2D texture = frame.Loader.obj.texture; Captured capture;
                if (texture == null || !frame.Textures.TryGetValue(texture, out capture) || texture.width != capture.Width || texture.height != capture.Height ||
                    (int)texture.format != capture.Format || texture.mipmapCount != capture.Mips || Convert.ToInt32(graphicsFormat.Invoke(texture, null)) != capture.GraphicsFormat)
                { unrepresented++; return; }
                frame.Source.Refresh();
                if (frame.Source.Length != frame.Length || frame.Source.LastWriteTimeUtc.Ticks != frame.Modified || Digest(frame.Source.FullName) != frame.SourceHash)
                    throw new InvalidOperationException("Source changed during cooking.");
                string hash = Digest(capture.Bytes), payload = hash + ".bin";
                string chunk = Path.Combine(directory, payload);
                if (!File.Exists(chunk)) WriteNew(chunk, capture.Bytes);
                else if (Digest(chunk) != hash) throw new InvalidOperationException("Existing cooked payload differs.");
                cooked.Add(new ResourceImageEntry { source = Relative(frame.Source.FullName), url = frame.File.url, loader = frame.Loader.GetType().FullName,
                    sourceSha256 = frame.SourceHash, sourceLength = frame.Length, sourceModifiedTicks = frame.Modified, payload = payload,
                    payloadSha256 = hash, payloadLength = capture.Bytes.LongLength, payloadModifiedTicks = new FileInfo(chunk).LastWriteTimeUtc.Ticks, width = capture.Width, height = capture.Height, format = capture.Format,
                    graphicsFormat = capture.GraphicsFormat, mipCount = capture.Mips, linear = capture.Linear, actualReadable = texture.isReadable,
                    normalMap = frame.Loader.obj.isNormalMap, infoReadable = frame.Loader.obj.isReadable, infoCompressed = frame.Loader.obj.isCompressed,
                    filter = (int)texture.filterMode, wrap = (int)texture.wrapMode, aniso = texture.anisoLevel, mipBias = texture.mipMapBias });
                captures++; capturedBytes += capture.Bytes.LongLength;
            }
            catch (Exception error) { errors++; Debug.LogWarning("[Continuum resource image] cook row unavailable: " + error.Message); }
        }
        void ReadImage()
        {
            RequireNoSymlinks(directory);
            string ownerPath = Path.Combine(directory, "owner.json"), manifestPath = Path.Combine(directory, "manifest.xml");
            RequireNoSymlinks(ownerPath); RequireNoSymlinks(manifestPath);
            if (new FileInfo(ownerPath).Length > 4096 || new FileInfo(manifestPath).Length > 16L * 1024 * 1024) throw new InvalidDataException("Image metadata too large.");
            ResourceImageOwner owner = JsonUtility.FromJson<ResourceImageOwner>(File.ReadAllText(ownerPath));
            if (owner == null || owner.schema != "continuum-startup-resource-image-owner/v1" || owner.scope != "private-derived-texture-image") throw new InvalidDataException("Image ownership differs.");
            ResourceImageManifest manifest;
            using (var stream = File.OpenRead(manifestPath)) manifest = (ResourceImageManifest)new XmlSerializer(typeof(ResourceImageManifest)).Deserialize(stream);
            if (manifest == null || manifest.schema != "continuum-startup-resource-image/v1" || manifest.status != "complete" || manifest.engine != Application.unityVersion ||
                manifest.platform != Application.platform.ToString() || manifest.instanceRoot != root || manifest.assemblySha256 != AssemblySha || manifest.entries == null || manifest.producerFingerprint != producerFingerprint ||
                manifest.captureRecipe != CaptureRecipe)
                throw new InvalidDataException("Image build profile differs.");
            frozenProfile = manifest;
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResourceImageEntry entry in manifest.entries)
            {
                if (entry == null || entry.source == null || entry.url == null || entry.loader == null) throw new InvalidDataException("Image row identity absent.");
                string key = Key(entry.source, entry.url, entry.loader);
                if (entries.ContainsKey(key)) { entries.Remove(key); duplicates.Add(key); }
                else if (!duplicates.Contains(key)) entries.Add(key, entry);
            }
            unrepresented += duplicates.Count;
        }
        bool Restore(DatabaseLoader<GameDatabase.TextureInfo> loader, UrlDir.UrlFile file, FileInfo source, out GameDatabase.TextureInfo result)
        {
            result = null; Texture2D texture = null;
            ResourceImageEntry entry;
            double started = Time.realtimeSinceStartup;
            try
            {
                if (!entries.TryGetValue(Key(Relative(source.FullName), file.url, loader.GetType().FullName), out entry)) { misses++; StartupAssetGeneration.Fallback("texture-image", "source-not-prepared"); return false; }
                RequireProfile(); RequireSource(source.FullName); source.Refresh();
                if (entry.width <= 0 || entry.height <= 0 || entry.width > 32768 || entry.height > 32768 || entry.mipCount < 1 || entry.mipCount > 16 ||
                    entry.payloadLength <= 0 || entry.payloadLength > MaximumPayload || !IsDigest(entry.sourceSha256) || !IsDigest(entry.payloadSha256) ||
                    entry.payload != entry.payloadSha256 + ".bin" || source.Length != entry.sourceLength || source.LastWriteTimeUtc.Ticks != entry.sourceModifiedTicks)
                    throw new InvalidDataException("Cooked source or payload contract differs.");
                string chunk = Path.Combine(directory, entry.payload); RequireNoSymlinks(chunk);
                var payloadInfo = new FileInfo(chunk);
                if (payloadInfo.Length != entry.payloadLength || payloadInfo.LastWriteTimeUtc.Ticks != entry.payloadModifiedTicks) throw new InvalidDataException("Cooked payload identity differs.");
                byte[] bytes = File.ReadAllBytes(chunk);
                payloadInfo.Refresh();
                if (bytes.LongLength != entry.payloadLength || payloadInfo.Length != entry.payloadLength || payloadInfo.LastWriteTimeUtc.Ticks != entry.payloadModifiedTicks)
                    throw new InvalidDataException("Cooked payload changed during restoration.");
                verificationSeconds += Time.realtimeSinceStartup - started; started = Time.realtimeSinceStartup;
                texture = new Texture2D(entry.width, entry.height, (TextureFormat)entry.format, entry.mipCount > 1, entry.linear);
                if (texture.mipmapCount != entry.mipCount || Convert.ToInt32(graphicsFormat.Invoke(texture, null)) != entry.graphicsFormat)
                    throw new InvalidDataException("Cooked texture format or mip layout differs.");
                texture.LoadRawTextureData(bytes);
                texture.filterMode = (FilterMode)entry.filter; texture.wrapMode = (TextureWrapMode)entry.wrap; texture.anisoLevel = entry.aniso; texture.mipMapBias = entry.mipBias;
                texture.Apply(false, !entry.actualReadable);
                source.Refresh();
                if (source.Length != entry.sourceLength || source.LastWriteTimeUtc.Ticks != entry.sourceModifiedTicks) throw new InvalidDataException("Source changed during restoration.");
                result = new GameDatabase.TextureInfo(file, texture, entry.normalMap, entry.infoReadable, entry.infoCompressed);
                publicationSeconds += Time.realtimeSinceStartup - started; restoredBytes += bytes.LongLength; hits++; return true;
            }
            catch (Exception error)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                errors++; misses++; StartupAssetGeneration.Fallback("texture-image", error.GetType().Name); Debug.LogWarning("[Continuum resource image] fallback " + file.url + ": " + error.Message); return false;
            }
        }
        string Relative(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Source escapes installation.");
            return full.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
        }
        void RequireSource(string path) { Relative(path); RequireNoSymlinks(path); }
        static void RequireNoSymlinks(string path)
        {
            for (string part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Image symlink path is not admitted.");
        }
        static bool IsDigest(string value) { return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        static string Key(string source, string url, string loader) { return source + "\n" + url + "\n" + loader; }
        void RequireProfile()
        {
            if (frozenProfile == null) frozenProfile = new ResourceImageManifest { renderer = SystemInfo.graphicsDeviceVersion,
                colorSpace = QualitySettings.activeColorSpace.ToString(), quality = QualitySettings.GetQualityLevel(), masterTextureLimit = QualitySettings.masterTextureLimit };
            if (frozenProfile.renderer != SystemInfo.graphicsDeviceVersion || frozenProfile.colorSpace != QualitySettings.activeColorSpace.ToString() ||
                frozenProfile.quality != QualitySettings.GetQualityLevel() || frozenProfile.masterTextureLimit != QualitySettings.masterTextureLimit)
                throw new InvalidDataException("Texture production profile differs.");
        }
        string ProducerFingerprint()
        {
            string snapshot = Path.Combine(directory, "producer.dll");
            RequireNoSymlinks(snapshot);
            long length = new FileInfo(snapshot).Length;
            if (length <= 0 || length > 16L * 1024 * 1024) throw new InvalidDataException("Producer helper snapshot length differs.");
            string producerHash = Digest(snapshot);
            string helper = Path.GetFullPath(typeof(StartupResourceImage).Assembly.Location);
            RequireSource(helper);
            var metadata = new List<string> { producerHash };
            foreach (string path in Directory.GetFiles(Path.Combine(root, "GameData"), "*.dll", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                RequireSource(path);
                metadata.Add(Relative(path) + "\t" + (Path.GetFullPath(path) == helper ? producerHash : Digest(path)));
            }
            return Digest(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", metadata.ToArray())));
        }
        static string Digest(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string Digest(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static void WriteNew(string path, byte[] bytes)
        {
            string temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
            try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static void ObserveForMenu() { if (active != null) active.ObserveMenu(); }
        void ObserveMenu()
        {
            if (finished || failed || HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            finished = true;
            try
            {
                if (mode == "prepare")
                {
                    RequireProfile();
                    var manifest = new ResourceImageManifest { status = "complete", captureRecipe = CaptureRecipe, engine = Application.unityVersion, platform = Application.platform.ToString(),
                        instanceRoot = root, assemblySha256 = AssemblySha, renderer = frozenProfile.renderer, colorSpace = frozenProfile.colorSpace,
                        quality = frozenProfile.quality, masterTextureLimit = frozenProfile.masterTextureLimit, producerFingerprint = producerFingerprint, entries = cooked.ToArray() };
                    using (var stream = new MemoryStream())
                    { new XmlSerializer(typeof(ResourceImageManifest)).Serialize(stream, manifest); WriteNew(Path.Combine(directory, "manifest.xml"), stream.ToArray()); }
                }
                Cleanup();
                if (mode == "reuse") StartupAssetGeneration.Complete("texture-image");
                Debug.Log("[Continuum resource image] run=" + runId + " mode=" + mode + " status=menu-complete captures=" + captures + " capturedBytes=" + capturedBytes +
                    " hits=" + hits + " restoredBytes=" + restoredBytes + " fallback=" + misses + " unrepresented=" + unrepresented + " errors=" + errors +
                    " retainedNormalApplies=" + retainedNormalApplies + " restoredNormalReadability=" + restoredNormalReadability +
                    " verificationSeconds=" + verificationSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) +
                    " publicationSeconds=" + publicationSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " profile=immutable-private-length-mtime-guards qualification=raw-texture-reconstruction-metadata-and-pixel-proof-required");
            }
            catch (Exception error) { StartupAssetGeneration.Fallback("texture-image", error.GetType().Name); failed = true; Cleanup(); Debug.LogException(error); Application.Quit(2); }
        }
        public void Update() { if (active == this) ObserveMenu(); }
        void Cleanup() { if (harmony != null) { harmony.UnpatchAll(Owner); harmony = null; } }
        public void OnDestroy() { if (active == this) { Cleanup(); active = null; } }
        public void OnApplicationQuit() { if (active == this) Cleanup(); }
    }
}
