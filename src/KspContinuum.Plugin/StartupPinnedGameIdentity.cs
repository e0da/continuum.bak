using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspContinuum
{
    public static class StartupPinnedGameIdentity
    {
        const string ExpectedSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        static readonly Guid ExpectedMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static readonly object Gate = new object();
        static Assembly admittedAssembly;
        static Module admittedModule;
        static string admittedPath, admittedRoot, admittedRun;
        static Stamp admittedFile, admittedMarker;
        static bool poisoned, reported;
        static long physicalHashes, cacheHits;

        [Serializable] sealed class LabMarker { public string scope = null; }
        struct Stamp
        {
            public long length, modified;
            public bool Same(Stamp other) { return length == other.length && modified == other.modified; }
        }

        public static void Require(Assembly assembly)
        {
            lock (Gate)
            {
                if (poisoned) throw new InvalidOperationException("Pinned game identity admission previously failed.");
                try
                {
                    if (assembly == null || assembly.GetName().Name != "Assembly-CSharp" || assembly.ManifestModule.ModuleVersionId != ExpectedMvid)
                        throw new InvalidOperationException("Pinned game module differs.");
                    string root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
                    string path = Path.GetFullPath(assembly.Location);
                    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.GetFileName(path) != "Assembly-CSharp.dll")
                        throw new InvalidOperationException("Pinned game location is outside the private instance.");
                    string[] runs = Environment.GetCommandLineArgs().Where(value => value.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                    if (runs.Length != 1 || runs[0].Length == "--continuum-research-run=".Length)
                        throw new InvalidOperationException("One nonempty private run identity required.");
                    string marker = Path.Combine(root, "continuum-lab.local.json");
                    Stamp fileBefore = ReadStamp(path), markerBefore = ReadStamp(marker);
                    if (markerBefore.length > 65536) throw new InvalidOperationException("Private laboratory marker exceeds its bound.");
                    if (admittedAssembly != null)
                    {
                        if (!ReferenceEquals(assembly, admittedAssembly) || !ReferenceEquals(assembly.ManifestModule, admittedModule) ||
                            path != admittedPath || root != admittedRoot || runs[0] != admittedRun ||
                            !fileBefore.Same(admittedFile) || !markerBefore.Same(admittedMarker))
                            throw new InvalidOperationException("Admitted immutable game profile changed.");
                        cacheHits++; return;
                    }
                    if (JsonUtility.FromJson<LabMarker>(File.ReadAllText(marker))?.scope != "disposable-lab")
                        throw new InvalidOperationException("Disposable private laboratory ownership required.");
                    physicalHashes++;
                    string digest = ScaleCheckpointLoadState.HashFile(path);
                    Stamp fileAfter = ReadStamp(path), markerAfter = ReadStamp(marker);
                    if (digest != ExpectedSha || !fileBefore.Same(fileAfter) || !markerBefore.Same(markerAfter) ||
                        assembly.ManifestModule.ModuleVersionId != ExpectedMvid || Path.GetFullPath(assembly.Location) != path)
                        throw new InvalidOperationException("Pinned game differs or changed during validation.");
                    admittedAssembly = assembly; admittedModule = assembly.ManifestModule;
                    admittedPath = path; admittedRoot = root; admittedRun = runs[0];
                    admittedFile = fileAfter; admittedMarker = markerAfter;
                }
                catch { poisoned = true; throw; }
            }
        }

        static Stamp ReadStamp(string path)
        {
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Pinned identity path contains a symbolic link.");
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
            var file = new FileInfo(path); file.Refresh();
            if (!file.Exists || file.Length <= 0) throw new InvalidOperationException("Pinned identity file is missing or empty.");
            return new Stamp { length = file.Length, modified = file.LastWriteTimeUtc.Ticks };
        }

        public static void ObserveForMenu()
        {
            lock (Gate)
            {
                if (reported || physicalHashes == 0) return;
                reported = true;
                Debug.Log("[Continuum pinned game identity] physicalHashes=" + physicalHashes + " cacheHits=" + cacheHits +
                    " rejected=" + poisoned + " contract=process-local-immutable-private-length-mtime-guards qualification=not-hostile-writer-protection");
            }
        }
    }
}
