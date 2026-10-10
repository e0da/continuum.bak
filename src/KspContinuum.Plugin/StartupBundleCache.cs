using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public sealed class StartupBundleCache : MonoBehaviour
    {
        const string Marker = "[Continuum startup cache] ";
        static readonly string[] Sources = {
            "MakingHistory/AssetBundles/makinghistory_assets",
            "MakingHistory/AssetBundles/makinghistory_scene",
            "Serenity/AssetBundles/serenity_assets" };
        readonly List<Row> rows = new List<Row>();
        string root, instanceRoot;
        int journal;
        bool ownsOutput, finished;

        sealed class Row
        {
            public string Relative, Original, Temporary, Derived, SourceSha, SourceMd5, DerivedSha, Result;
            public long SourceLength, DerivedLength;
            public DateTime SourceModified;
        }

        public void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "--continuum-startup-build-cache") < 0) return;
            try
            {
                string[] paths = args.Where(arg => arg.StartsWith("--continuum-startup-cache=", StringComparison.Ordinal)).ToArray();
                if (paths.Length != 1) throw new InvalidOperationException("Exactly one explicit cache path is required.");
                string requested = paths[0].Substring("--continuum-startup-cache=".Length);
                if (!Path.IsPathRooted(requested)) throw new InvalidOperationException("Cache path must be absolute.");
                root = Path.GetFullPath(requested); instanceRoot = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
                if (root == instanceRoot || root.StartsWith(instanceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException("Derived cache must be outside the game installation.");
                if (!File.Exists(Path.Combine(instanceRoot, "continuum-lab.local.json")))
                    throw new InvalidOperationException("Laboratory installation marker is required.");
                if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("Cache output must not exist.");
                RequireNoSymlinks(Path.GetDirectoryName(root));
                Directory.CreateDirectory(root);
                WriteExclusive(Path.Combine(root, "continuum-bundle-cache.local.json"), "{\"schema\":\"continuum-bundle-cache-owner/v1\",\"scope\":\"private-derived-cache\"}");
                ownsOutput = true;
                Save("building", "none", false);
                StartCoroutine(Build());
            }
            catch (Exception error) { Fail(error); }
        }

        IEnumerator Build()
        {
            foreach (string relative in Sources)
            {
                Row row = null; AssetBundleRecompressOperation operation = null;
                try
                {
                    row = Prepare(relative); rows.Add(row); Save("building", "none", false);
                    operation = AssetBundle.RecompressAssetBundleAsync(row.Original, row.Temporary, BuildCompression.LZ4Runtime);
                    if (operation == null) throw new InvalidOperationException("Recompression operation is unavailable.");
                }
                catch (Exception error) { Fail(error); }
                if (finished) yield break;
                yield return operation;
                try
                {
                    row.Result = operation.result + ":" + operation.humanReadableResult;
                    if (!operation.success) throw new InvalidOperationException("Recompression failed: " + row.Result);
                    RequireNoSymlinks(row.Original); RequireNoSymlinks(row.Temporary);
                    var source = new FileInfo(row.Original);
                    if (source.Length != row.SourceLength || source.LastWriteTimeUtc != row.SourceModified ||
                        Digest(row.Original, true) != row.SourceSha || Digest(row.Original, false) != row.SourceMd5)
                        throw new InvalidOperationException("Original bundle changed during recompression.");
                    row.DerivedSha = Digest(row.Temporary, true); row.DerivedLength = new FileInfo(row.Temporary).Length;
                    if (row.DerivedLength == 0) throw new InvalidOperationException("Derived bundle is empty.");
                    File.Move(row.Temporary, row.Derived);
                    Save("building", "none", false);
                    UnityEngine.Debug.Log(Marker + "converted=" + relative + " derivedSha256=" + row.DerivedSha);
                }
                catch (Exception error) { Fail(error); }
                if (finished) yield break;
            }
            try { Save("complete", "none", true); finished = true; UnityEngine.Debug.Log(Marker + "status=complete qualification=derived-cache-only"); Application.Quit(0); }
            catch (Exception error) { Fail(error); }
        }

        Row Prepare(string relative)
        {
            string expansionRoot = Path.Combine(instanceRoot, "GameData", "SquadExpansion");
            string original = Path.GetFullPath(Path.Combine(expansionRoot, relative));
            if (!original.StartsWith(Path.GetFullPath(expansionRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Source escaped expansion root.");
            RequireNoSymlinks(original);
            var source = new FileInfo(original);
            var row = new Row { Relative = relative, Original = original, SourceLength = source.Length,
                SourceModified = source.LastWriteTimeUtc, SourceSha = Digest(original, true), SourceMd5 = Digest(original, false),
                Derived = Path.Combine(root, Path.GetFileName(original) + ".lz4"), Result = "pending" };
            var after = new FileInfo(original);
            if (after.Length != row.SourceLength || after.LastWriteTimeUtc != row.SourceModified)
                throw new InvalidOperationException("Source changed during initial hashing.");
            row.Temporary = row.Derived + ".pending";
            if (File.Exists(row.Temporary) || File.Exists(row.Derived)) throw new InvalidOperationException("Derived output already exists.");
            return row;
        }

        static void RequireNoSymlinks(string path)
        {
            for (string entry = Path.GetFullPath(path); !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symlink path is not admitted.");
        }
        static string Digest(string path, bool sha)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (HashAlgorithm algorithm = sha ? (HashAlgorithm)SHA256.Create() : MD5.Create())
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static string Json(string value)
        {
            if (value == null) return "null";
            var result = new StringBuilder("\"");
            foreach (char character in value)
                if (character == '\\' || character == '"') result.Append('\\').Append(character);
                else if (character < 32) result.Append("\\u").Append(((int)character).ToString("x4"));
                else result.Append(character);
            return result.Append('"').ToString();
        }
        void Save(string status, string error, bool final)
        {
            var json = new StringBuilder("{\"schema\":\"continuum-bundle-cache/v1\",\"status\":").Append(Json(status))
                .Append(",\"engine\":").Append(Json(Application.unityVersion)).Append(",\"compression\":\"LZ4Runtime\",\"qualification\":\"derived-cache-only\",\"error\":")
                .Append(Json(error)).Append(",\"instanceRoot\":").Append(Json(instanceRoot)).Append(",\"bundles\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i != 0) json.Append(','); Row row = rows[i];
                json.Append("{\"relativePath\":").Append(Json(row.Relative)).Append(",\"originalPath\":").Append(Json(row.Original))
                    .Append(",\"derivedPath\":").Append(Json(row.Derived)).Append(",\"sourceSha256\":").Append(Json(row.SourceSha))
                    .Append(",\"sourceMd5\":").Append(Json(row.SourceMd5)).Append(",\"derivedSha256\":").Append(Json(row.DerivedSha))
                    .Append(",\"sourceLength\":").Append(row.SourceLength).Append(",\"derivedLength\":").Append(row.DerivedLength)
                    .Append(",\"sourceModifiedUtc\":").Append(Json(row.SourceModified.ToString("O"))).Append(",\"result\":").Append(Json(row.Result)).Append('}');
            }
            json.Append("]}");
            string destination = Path.Combine(root, final ? "manifest.json" : "journal-" + (journal++).ToString("D4") + ".json");
            string temporary = destination + ".pending"; WriteExclusive(temporary, json.ToString()); File.Move(temporary, destination);
        }
        static void WriteExclusive(string path, string text)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
        }
        void Fail(Exception error)
        {
            if (finished) return; finished = true;
            if (ownsOutput)
                try { Save("failed", error.GetType().Name + ":" + error.Message, true); }
                catch (Exception receiptError) { UnityEngine.Debug.LogError(Marker + "receipt-error=" + receiptError.Message); }
            UnityEngine.Debug.LogError(Marker + "status=failed error=" + error.GetType().Name + ":" + error.Message);
            Application.Quit(2);
        }
    }
}
