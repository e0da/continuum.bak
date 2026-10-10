using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [Serializable] public sealed class AssetCatalogAsset
    {
        public string name, path, type, url;
        public bool autoLoad;
    }
    [Serializable] public sealed class AssetCatalogBundle
    {
        public string urlName, name, path, author, info;
        public long createdTime;
        public bool autoLoad;
        public AssetCatalogAsset[] assets;
        public string[] dependencyNames;
    }
    [Serializable] public sealed class AssetCatalogFile
    {
        public string path, sha256;
        public long length;
    }
    [Serializable] public sealed class AssetCatalogDocument
    {
        public string schema, engine, platform, instanceRoot, assemblySha256, compiledSha256;
        public string precondition = "private-reserved-immutable-installation";
        public string[] recipe, blacklist, orderedFiles;
        public AssetCatalogFile[] inventory;
        public AssetCatalogBundle[] bundles;
        public int amountDelta, assetCount;
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupAssetCatalog : MonoBehaviour
    {
        const string Owner = "continuum.startup-asset-catalog";
        const string Marker = "[Continuum startup catalog] ";
        const string AssemblySha = "7e49fcb23fb5b5fa4376cf04c48958744ffe59a2a11d0a90cdd85fb43bb7c304";
        static readonly string[] RecipeNames = { "assetDirectory", "coreDirectory", "coreExpansionsDirectory", "assetExtension", "assetDefinitionSuffix", "bundleDependencySuffix" };
        static readonly string[] StateNames = { "coreAndAutoloadDefinitions", "assetDefinitions", "allFilesList", "amountAutoLoadBundles", "ready" };
        static StartupAssetCatalog active;
        Type loaderType, bundleType, assetType;
        MethodInfo loadMethod, compile, flatten;
        Harmony harmony;
        string output, root, assemblyPath;
        bool prepare, metadataOnlyPrepare, captureStarted, captureFailed, originalCompleted, consumed;
        object capturedLoader;
        int initialAmount;
        int skippedAudioEnumerations, skippedTextureEnumerations, skippedModelEnumerations;
        AssetCatalogDocument captured;

        public void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            prepare = args.Contains("--continuum-startup-prepare-catalog");
            metadataOnlyPrepare = args.Contains("--continuum-startup-metadata-only-prepare");
            bool reuse = args.Contains("--continuum-startup-cached-catalog");
            if (!prepare && !reuse && !metadataOnlyPrepare) return;
            DontDestroyOnLoad(gameObject);
            try
            {
                if (prepare == reuse) throw new InvalidOperationException("Select exactly one catalog mode.");
                if (metadataOnlyPrepare && !prepare) throw new InvalidOperationException("Metadata-only preparation requires catalog preparation.");
                string[] paths = args.Where(a => a.StartsWith("--continuum-startup-catalog=", StringComparison.Ordinal)).ToArray();
                if (paths.Length != 1) throw new InvalidOperationException("Exactly one catalog path required.");
                output = paths[0].Substring("--continuum-startup-catalog=".Length);
                if (!Path.IsPathRooted(output)) throw new InvalidOperationException("Catalog path must be absolute.");
                output = Path.GetFullPath(output);
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
                if (output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(Path.Combine(root, "continuum-lab.local.json")))
                    throw new InvalidOperationException("Private laboratory and external catalog required.");
                if (prepare && File.Exists(output)) throw new InvalidOperationException("Catalog output already exists.");
                assemblyPath = Path.Combine(Path.GetDirectoryName(typeof(FlightGlobals).Assembly.Location), "KSPAssets.dll");
                if (Digest(assemblyPath) != AssemblySha) throw new InvalidOperationException("KSPAssets source differs.");
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "KSPAssets") ?? Assembly.LoadFrom(assemblyPath);
                if (assembly.ManifestModule.ModuleVersionId != new Guid("A26F2EA6-6A0A-4BD9-9DEB-1DFFEC173A72")) throw new InvalidOperationException("Loaded KSPAssets identity differs.");
                loaderType = assembly.GetType("KSPAssets.Loaders.AssetLoader", true);
                bundleType = assembly.GetType("KSPAssets.BundleDefinition", true);
                assetType = assembly.GetType("KSPAssets.AssetDefinition", true);
                MethodInfo load = loadMethod = AccessTools.DeclaredMethod(loaderType, "LoadDefinitionsAsync", Type.EmptyTypes);
                compile = AccessTools.DeclaredMethod(loaderType, "CompileBundleDefinitions", Type.EmptyTypes);
                flatten = AccessTools.DeclaredMethod(loaderType, "CreateAssetDefinitionList", Type.EmptyTypes);
                if (load.MetadataToken != 100663328 || compile.MetadataToken != 100663331 || flatten.MetadataToken != 100663333)
                    throw new InvalidOperationException("Loader method contract differs.");
                foreach (MethodInfo method in new[] { load, compile, flatten })
                    if (Harmony.GetPatchInfo(method)?.Owners.Count > 0) throw new InvalidOperationException("Loader already patched.");
                foreach (string name in RecipeNames.Concat(StateNames).Concat(new[] { "assetBlacklist" })) Field(loaderType, name);
                MethodInfo rootFiles = null;
                if (metadataOnlyPrepare)
                {
                    if (typeof(UrlDir).Assembly.ManifestModule.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf"))
                        throw new InvalidOperationException("Preparation GameDatabase source differs.");
                    rootFiles = AccessTools.DeclaredMethod(typeof(UrlDir), "GetFiles", new[] { typeof(UrlDir.FileType) });
                    if (rootFiles == null || rootFiles.ReturnType != typeof(IEnumerable<UrlDir.UrlFile>) || Harmony.GetPatchInfo(rootFiles)?.Owners.Count > 0)
                        throw new InvalidOperationException("Preparation root enumeration contract differs or is already patched.");
                }
                harmony = new Harmony(Owner); active = this;
                harmony.Patch(load, prefix: new HarmonyMethod(typeof(StartupAssetCatalog), nameof(LoadPrefix)), postfix: new HarmonyMethod(typeof(StartupAssetCatalog), nameof(LoadPostfix)));
                if (prepare)
                {
                    harmony.Patch(compile, prefix: new HarmonyMethod(typeof(StartupAssetCatalog), nameof(CompilePrefix)));
                    if (metadataOnlyPrepare) harmony.Patch(rootFiles, prefix: new HarmonyMethod(typeof(StartupAssetCatalog), nameof(PreparationFilesPrefix)));
                }
                Application.logMessageReceived += ObserveError;
                UnityEngine.Debug.Log(Marker + "mode=" + (metadataOnlyPrepare ? "prepare-minimal" : prepare ? "prepare" : "reuse") + " qualification=metadata-only");
            }
            catch (Exception error) { Fail(error); if (prepare || metadataOnlyPrepare) Application.Quit(2); }
        }

        static bool PreparationFilesPrefix(UrlDir __instance, UrlDir.FileType type, ref IEnumerable<UrlDir.UrlFile> __result)
        {
            if (active == null || !active.prepare || !active.metadataOnlyPrepare || GameDatabase.Instance == null || !ReferenceEquals(__instance, GameDatabase.Instance.root)) return true;
            switch (type)
            {
                case UrlDir.FileType.Audio: active.skippedAudioEnumerations++; break;
                case UrlDir.FileType.Texture: active.skippedTextureEnumerations++; break;
                case UrlDir.FileType.Model: active.skippedModelEnumerations++; break;
                default: return true;
            }
            __result = Array.Empty<UrlDir.UrlFile>();
            return false;
        }

        static bool LoadPrefix(object __instance, ref IEnumerator __result)
        {
            if (active == null || active.consumed) return true;
            active.consumed = true;
            if (active.prepare)
            {
                active.captureStarted = true; active.capturedLoader = __instance;
                active.initialAmount = (int)Get(__instance, "amountAutoLoadBundles");
                return true;
            }
            __result = active.Reuse(__instance); return false;
        }
        static void LoadPostfix(object __instance, ref IEnumerator __result)
        {
            if (active != null && active.prepare && ReferenceEquals(active.capturedLoader, __instance) && active.captureStarted)
                __result = active.ObserveCompletion(__result);
        }
        IEnumerator Reuse(object loader)
        {
            if (TryApply(loader)) yield break;
            IEnumerator original = (IEnumerator)loadMethod.Invoke(loader, null);
            try { while (original.MoveNext()) yield return original.Current; }
            finally { (original as IDisposable)?.Dispose(); }
        }
        IEnumerator ObserveCompletion(IEnumerator original)
        {
            try
            {
                while (AdvanceCapture(original)) yield return original.Current;
                PersistCapture();
            }
            finally { (original as IDisposable)?.Dispose(); }
        }
        static void CompilePrefix(object __instance)
        {
            if (active == null || !active.captureStarted || active.captured != null || !ReferenceEquals(active.capturedLoader, __instance)) return;
            try { active.Capture(__instance); }
            catch (Exception error) { active.captureFailed = true; UnityEngine.Debug.LogError(Marker + "capture rejected: " + error.Message); }
        }
        void ObserveError(string message, string stack, LogType type)
        {
            if (captureStarted && !originalCompleted && (type == LogType.Error || type == LogType.Exception) &&
                (message.Contains("AssetLoader") || message.Contains("KSPBundleDefinition"))) captureFailed = true;
        }
        void Capture(object loader)
        {
            var bundles = ((IEnumerable)Get(loader, "coreAndAutoloadDefinitions")).Cast<object>().Select(b => new AssetCatalogBundle {
                urlName = (string)Get(b,"urlName"), name = (string)Get(b,"name"), path = (string)Get(b,"path"), author = (string)Get(b,"author"), info = (string)Get(b,"info"),
                createdTime = (long)Get(b,"createdTime"), autoLoad = (bool)Get(b,"autoLoad"), dependencyNames = ((IEnumerable)Get(b,"dependencyNames")).Cast<string>().ToArray(),
                assets = ((IEnumerable)Get(b,"assets")).Cast<object>().Select(a => new AssetCatalogAsset { name=(string)Get(a,"name"), path=(string)Get(a,"path"), type=(string)Get(a,"type"), url=(string)Get(a,"url"), autoLoad=(bool)Get(a,"autoLoad") }).ToArray()
            }).ToArray();
            captured = new AssetCatalogDocument { schema="continuum-asset-catalog/v1", engine=Application.unityVersion, platform=Application.platform.ToString(), instanceRoot=root,
                assemblySha256=AssemblySha, recipe=Recipe(loader), blacklist=((IEnumerable)Get(loader,"assetBlacklist")).Cast<string>().ToArray(),
                orderedFiles=((IEnumerable)Get(loader,"allFilesList")).Cast<FileInfo>().Select(f=>f.FullName).ToArray(), bundles=bundles,
                amountDelta=(int)Get(loader,"amountAutoLoadBundles")-initialAmount, assetCount=bundles.Sum(b=>b.assets.Length) };
            if (captured.amountDelta != bundles.Length) throw new InvalidOperationException("Original bundle accounting differs.");
        }
        bool AdvanceCapture(IEnumerator original)
        {
            try { return original.MoveNext(); }
            catch (Exception error)
            {
                captureFailed = true;
                UnityEngine.Debug.LogError(Marker + "original metadata load failed: " + error.Message);
                return false;
            }
        }
        void PersistCapture()
        {
            prepare = false;
            string pending = null;
            try
            {
                var timer=Stopwatch.StartNew();
                originalCompleted = (bool)Get(capturedLoader, "ready");
                if (captureFailed || !originalCompleted || captured == null) throw new InvalidOperationException("Original metadata load failed or remained incomplete.");
                captured.compiledSha256 = CompiledDigest(capturedLoader);
                captured.inventory=Inventory(captured.recipe);
                ValidateStructure(captured);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                pending = output + ".pending-" + Guid.NewGuid().ToString("N");
                using (var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    new XmlSerializer(typeof(AssetCatalogDocument)).Serialize(stream,captured);
                    stream.Flush(true);
                }
                File.Move(pending, output); pending = null;
                UnityEngine.Debug.Log(Marker+"status=captured phase=metadata-ready bundles="+captured.bundles.Length+" assets="+captured.assetCount+" files="+captured.orderedFiles.Length+" preparationSeconds="+timer.Elapsed.TotalSeconds.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" catalogSha256="+Digest(output)+" compiledSha256="+captured.compiledSha256+" skippedAudioEnumerations="+skippedAudioEnumerations+" skippedTextureEnumerations="+skippedTextureEnumerations+" skippedModelEnumerations="+skippedModelEnumerations);
                Stop();
                Application.Quit(0);
            }
            catch(Exception error)
            {
                try { if (pending != null && File.Exists(pending)) File.Delete(pending); }
                catch (Exception cleanupError) { UnityEngine.Debug.LogWarning(Marker + "pending cleanup failed: " + cleanupError.Message); }
                Fail(error);
                Application.Quit(2);
            }
        }
        bool TryApply(object loader)
        {
            var timer=Stopwatch.StartNew(); object[] previous=null;
            try
            {
                AssetCatalogDocument document;
                var settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=16000000 };
                using(var stream=File.OpenRead(output)) using(var reader=XmlReader.Create(stream,settings)) document=(AssetCatalogDocument)new XmlSerializer(typeof(AssetCatalogDocument)).Deserialize(reader);
                ValidateStructure(document);
                if(document.engine!=Application.unityVersion || document.platform!=Application.platform.ToString() || document.instanceRoot!=root || document.assemblySha256!=AssemblySha ||
                    !document.recipe.SequenceEqual(Recipe(loader)) || !document.blacklist.SequenceEqual(((IEnumerable)Get(loader,"assetBlacklist")).Cast<string>())) throw new InvalidOperationException("Catalog runtime/recipe differs.");
                AssetCatalogFile[] actual=Inventory(document.recipe);
                string consumer = Path.GetFullPath(typeof(StartupAssetCatalog).Assembly.Location);
                bool consumerOnly = typeof(StartupAssetCatalog).Assembly.GetManifestResourceNames().Length == 0 &&
                    !document.orderedFiles.Contains(consumer) && !document.bundles.Any(bundle => bundle.path == consumer);
                AssetCatalogFile[] expected = document.inventory;
                if (consumerOnly)
                {
                    actual = actual.Where(file => file.path != consumer).ToArray();
                    expected = expected.Where(file => file.path != consumer).ToArray();
                }
                if(actual.Length!=expected.Length || actual.Where((f,i)=>f.path!=expected[i].path || f.sha256!=expected[i].sha256 || f.length!=expected[i].length).Any()) throw new InvalidOperationException("Catalog source inventory differs.");
                IList definitions=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(bundleType));
                foreach(var b in document.bundles)
                {
                    object value=Activator.CreateInstance(bundleType);
                    foreach(string name in new[]{"urlName","name","path","author","info","createdTime","autoLoad"}) Set(value,name,typeof(AssetCatalogBundle).GetField(name).GetValue(b));
                    Set(value,"dependencyNames",new List<string>(b.dependencyNames));
                    IList assets=(IList)Get(value,"assets");
                    foreach(var a in b.assets)
                    {
                        object asset=Activator.CreateInstance(assetType);
                        foreach(string name in new[]{"name","path","type","url","autoLoad"}) Set(asset,name,typeof(AssetCatalogAsset).GetField(name).GetValue(a));
                        assets.Add(asset);
                    }
                    definitions.Add(value);
                }
                previous=StateNames.Select(n=>Get(loader,n)).ToArray();
                Set(loader,"ready",false); Set(loader,"coreAndAutoloadDefinitions",definitions);
                Set(loader,"allFilesList",document.orderedFiles.Select(p=>new FileInfo(p)).ToList());
                Set(loader,"amountAutoLoadBundles",checked((int)previous[3]+document.amountDelta));
                compile.Invoke(loader,null); flatten.Invoke(loader,null);
                if(((IList)Get(loader,"assetDefinitions")).Count!=document.assetCount || CompiledDigest(loader)!=document.compiledSha256) throw new InvalidOperationException("Compiled asset count differs.");
                Set(loader,"ready",true);
                UnityEngine.Debug.Log(Marker+"status=applied bundles="+definitions.Count+" assets="+document.assetCount+" files="+document.orderedFiles.Length+" admissionSeconds="+timer.Elapsed.TotalSeconds.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" qualification=metadata-only");
                return true;
            }
            catch(Exception error)
            {
                if(previous!=null) for(int i=0;i<StateNames.Length;i++) Set(loader,StateNames[i],previous[i]);
                UnityEngine.Debug.LogWarning(Marker+"status=abstained fallback=original admissionSeconds="+timer.Elapsed.TotalSeconds.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" reason="+error.Message);
                return false;
            }
        }
        void ValidateStructure(AssetCatalogDocument d)
        {
            if(d==null || d.schema!="continuum-asset-catalog/v1" || d.precondition!="private-reserved-immutable-installation" || string.IsNullOrEmpty(d.compiledSha256) || d.recipe==null || d.recipe.Length!=RecipeNames.Length || d.inventory==null || d.bundles==null || d.blacklist==null || d.orderedFiles==null || d.bundles.Length>10000 || d.inventory.Length>10000 || d.amountDelta!=d.bundles.Length || d.assetCount!=d.bundles.Sum(b=>b.assets.Length)) throw new InvalidOperationException("Catalog structure differs.");
            var paths=new HashSet<string>(d.inventory.Select(f=>f.path),StringComparer.Ordinal);
            if(paths.Count!=d.inventory.Length || d.orderedFiles.Distinct(StringComparer.Ordinal).Count()!=d.orderedFiles.Length || d.orderedFiles.Any(p=>!paths.Contains(p)) || d.bundles.Any(b=>!paths.Contains(b.path) || b.dependencyNames==null)) throw new InvalidOperationException("Catalog file bindings differ.");
        }
        AssetCatalogFile[] Inventory(string[] recipe)
        {
            string data=Path.GetFullPath(Path.Combine(root,recipe[0]));
            if(!data.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal)) throw new InvalidOperationException("Asset directory escapes installation.");
            return Directory.GetFiles(data,"*",SearchOption.AllDirectories).Where(p=>p.EndsWith("."+recipe[3],StringComparison.Ordinal) || p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.Ordinal).Select(p=> {
                var before=new FileInfo(p); long length=before.Length; DateTime modified=before.LastWriteTimeUtc;
                string hash=Digest(p); var after=new FileInfo(p);
                if(after.Length!=length || after.LastWriteTimeUtc!=modified) throw new InvalidOperationException("Catalog source changed during admission.");
                return new AssetCatalogFile {path=p,length=length,sha256=hash};
            }).ToArray();
        }
        static string CompiledDigest(object loader)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    IList bundles = (IList)Get(loader, "coreAndAutoloadDefinitions");
                    writer.Write(bundles.Count);
                    foreach (object bundle in bundles)
                    {
                        foreach (string field in new[] { "urlName", "name", "path", "author", "info" }) WriteString(writer, (string)Get(bundle, field));
                        writer.Write((long)Get(bundle, "createdTime")); writer.Write((bool)Get(bundle, "autoLoad"));
                        writer.Write((bool)Get(bundle, "dependenciesFound"));
                        IList names = (IList)Get(bundle, "dependencyNames"); writer.Write(names.Count);
                        foreach (string name in names) WriteString(writer, name);
                        IList dependencies = (IList)Get(bundle, "dependencyBundles"); writer.Write(dependencies.Count);
                        foreach (object dependency in dependencies) writer.Write(bundles.IndexOf(dependency));
                        IList assets = (IList)Get(bundle, "assets"); writer.Write(assets.Count);
                        foreach (object asset in assets) WriteAsset(writer, asset, bundles);
                    }
                    IList flat = (IList)Get(loader, "assetDefinitions"); writer.Write(flat.Count);
                    foreach (object asset in flat) WriteAsset(writer, asset, bundles);
                }
                stream.Position = 0;
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }
        static void WriteAsset(BinaryWriter writer, object asset, IList bundles)
        {
            foreach (string field in new[] { "name", "path", "type", "url" }) WriteString(writer, (string)Get(asset, field));
            writer.Write((bool)Get(asset, "autoLoad")); writer.Write(bundles.IndexOf(Get(asset, "bundle")));
        }
        static void WriteString(BinaryWriter writer, string value) { writer.Write(value != null); if (value != null) writer.Write(value); }
        static string[] Recipe(object loader) { return RecipeNames.Select(n=>(string)Get(loader,n)).ToArray(); }
        static FieldInfo Field(Type type,string name) { return AccessTools.Field(type,name) ?? throw new MissingFieldException(type.FullName,name); }
        static object Get(object value,string name) { return Field(value.GetType(),name).GetValue(value); }
        static void Set(object value,string name,object fieldValue) { Field(value.GetType(),name).SetValue(value,fieldValue); }
        static string Digest(string path) { using(var stream=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant(); }
        void Fail(Exception error) { UnityEngine.Debug.LogError(Marker+"status=rejected reason="+error.Message); Stop(); }
        void Stop() { Application.logMessageReceived-=ObserveError; harmony?.UnpatchAll(Owner); if(active==this) active=null; }
        public void OnDestroy() { Stop(); }
    }
}
