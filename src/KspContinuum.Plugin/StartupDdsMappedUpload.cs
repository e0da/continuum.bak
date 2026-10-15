using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupDdsMappedUpload : MonoBehaviour
    {
        const string Owner = "continuum.startup-dds-mapped";
        const string Marker = "[Continuum startup DDS] ";
        static StartupDdsMappedUpload active;
        [ThreadStatic] static Frame current;
        public sealed class Frame
        {
            public Frame Previous;
            public FileStream Stream;
            public MemoryMappedFile Mapping;
            public MemoryMappedViewAccessor View;
            public long Started, Length, Modified;
        }
        [Serializable] public sealed class LabOwner { public string scope = null; }
        Harmony harmony;
        MethodInfo target;
        string root, status = "not-requested";
        bool candidate, requested, exported;
        long calls, failures, bytes, ticks, mappedCalls, mappedBytes, mappedTicks, fallbacks;

        public void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            candidate = arguments.Contains("--continuum-startup-dds-mapped");
            requested = candidate || arguments.Contains("--continuum-startup-texture-observe");
            if (!requested) return;
            DontDestroyOnLoad(gameObject);
            try
            {
                if (active != null) throw new InvalidOperationException("DDS observer already exists.");
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
                string marker = Path.Combine(root, "continuum-lab.local.json");
                if (!File.Exists(marker) || new FileInfo(marker).Length > 65536 ||
                    JsonUtility.FromJson<LabOwner>(File.ReadAllText(marker)).scope != "disposable-lab")
                    throw new InvalidOperationException("Disposable laboratory ownership required.");
                string[] identities = arguments.Where(value => value.StartsWith("--continuum-research-run=", StringComparison.Ordinal)).ToArray();
                if (identities.Length != 1 || identities[0].Length == "--continuum-research-run=".Length)
                    throw new InvalidOperationException("One owned run identity required.");
                Assembly game = typeof(DatabaseLoaderTexture_DDS).Assembly;
                if (game.ManifestModule.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf") ||
                    ScaleCheckpointLoadState.HashFile(game.Location) != "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27")
                    throw new InvalidOperationException("Pinned game differs.");
                target = AccessTools.DeclaredMethod(typeof(DatabaseLoaderTexture_DDS), "Read", new[] { typeof(string) });
                if (target == null || target.MetadataToken != 100668596 || target.ReturnType != typeof(Texture2D))
                    throw new InvalidOperationException("Pinned DDS method differs.");
                if (Harmony.GetPatchInfo(target)?.Owners.Count > 0) throw new InvalidOperationException("DDS method has another patch owner.");
                if (AccessTools.DeclaredMethod(typeof(Texture2D), "LoadRawTextureData", new[] { typeof(IntPtr), typeof(int) }) == null)
                    throw new MissingMethodException("Installed pointer upload overload absent.");
                active = this; harmony = new Harmony(Owner);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(StartupDdsMappedUpload), nameof(ReadPrefix)),
                    finalizer: new HarmonyMethod(typeof(StartupDdsMappedUpload), nameof(ReadFinalizer)),
                    transpiler: candidate ? new HarmonyMethod(typeof(StartupDdsMappedUpload), nameof(Transpile)) : null);
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches == null || patches.Prefixes.Count != 1 || patches.Finalizers.Count != 1 ||
                    patches.Transpilers.Count != (candidate ? 1 : 0) || patches.Owners.Any(owner => owner != Owner))
                    throw new InvalidOperationException("DDS ownership verification failed.");
                status = candidate ? "mapped-installed" : "observation-only";
                Debug.Log(Marker + "status=" + status);
            }
            catch (Exception error)
            {
                status = "abstained:" + error.GetType().Name + ":" + error.Message;
                Cleanup(); Export(); Application.Quit(2);
            }
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            MethodInfo read = AccessTools.DeclaredMethod(typeof(File), "ReadAllBytes", new[] { typeof(string) });
            ConstructorInfo memory = typeof(MemoryStream).GetConstructor(new[] { typeof(byte[]) });
            MethodInfo payload = AccessTools.DeclaredMethod(typeof(BinaryReader), "ReadBytes", new[] { typeof(int) });
            MethodInfo upload = AccessTools.DeclaredMethod(typeof(Texture2D), "LoadRawTextureData", new[] { typeof(byte[]) });
            if (body.Count(item => item.Calls(read)) != 1 || body.Count(item => Equals(item.operand, memory)) != 1 ||
                body.Count(item => item.Calls(payload)) != 2 || body.Count(item => item.Calls(upload)) != 2)
                throw new InvalidOperationException("Pinned DDS call counts differ.");
            int start = body.FindIndex(item => item.Calls(read));
            CheckPattern(body, start, memory, true);
            body[start].opcode = OpCodes.Call;
            body[start].operand = AccessTools.DeclaredMethod(typeof(StartupDdsMappedUpload), nameof(OpenStream));
            for (int offset = 1; offset <= 3; offset++) { body[start + offset].opcode = OpCodes.Nop; body[start + offset].operand = null; }
            for (int i = 0; i < body.Count; i++)
            {
                if (!body[i].Calls(payload)) continue;
                CheckPattern(body, i, upload, false);
                body[i].opcode = OpCodes.Call;
                body[i].operand = AccessTools.DeclaredMethod(typeof(StartupDdsMappedUpload), nameof(Upload));
                for (int offset = 1; offset <= 3; offset++) { body[i + offset].opcode = OpCodes.Nop; body[i + offset].operand = null; }
            }
            return body;
        }
        static void CheckPattern(List<CodeInstruction> body, int at, MemberInfo end, bool constructor)
        {
            if (at + 3 >= body.Count || body[at + 1].opcode != OpCodes.Dup || body[at + 2].opcode != OpCodes.Pop ||
                !Equals(body[at + 3].operand, end) || body[at + 3].opcode != (constructor ? OpCodes.Newobj : OpCodes.Callvirt) ||
                Enumerable.Range(1, 3).Any(offset => body[at + offset].labels.Count != 0 || body[at + offset].blocks.Count != 0))
                throw new InvalidOperationException("Pinned DDS stack/control-flow pattern differs.");
        }

        static void ReadPrefix(string filename, out Frame __state)
        {
            __state = new Frame { Previous = current, Started = Stopwatch.GetTimestamp() };
            current = __state;
            active.calls++;
            try { active.bytes += new FileInfo(filename).Length; } catch (IOException) { }
        }
        static Exception ReadFinalizer(Frame __state, Texture2D __result, Exception __exception)
        {
            if (__exception != null || __result == null) active.failures++;
            active.ticks += Stopwatch.GetTimestamp() - __state.Started;
            try
            {
                if (__state.View != null) { __state.View.SafeMemoryMappedViewHandle.ReleasePointer(); __state.View.Dispose(); }
                __state.Mapping?.Dispose(); __state.Stream?.Dispose();
            }
            catch (Exception error) { Debug.LogWarning(Marker + "streamCleanup=" + error.GetType().Name); }
            finally { current = __state.Previous; }
            return __exception;
        }
        static Stream OpenStream(string filename)
        {
            string path = Path.GetFullPath(filename);
            string allowed = Path.Combine(active.root, "GameData") + Path.DirectorySeparatorChar;
            if (!path.StartsWith(allowed, StringComparison.Ordinal)) throw new InvalidOperationException("DDS path outside owned GameData.");
            for (string check = path; check != null; check = Path.GetDirectoryName(check))
                if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("DDS path contains a symbolic link.");
            if (current == null || current.Stream != null) throw new InvalidOperationException("DDS scoped stream owner differs.");
            current.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            current.Length = current.Stream.Length;
            current.Modified = File.GetLastWriteTimeUtc(path).Ticks;
            return current.Stream;
        }
        static unsafe void Upload(Texture2D texture, BinaryReader reader, int count)
        {
            FileStream stream = reader.BaseStream as FileStream;
            MemoryMappedFile mapping = null;
            MemoryMappedViewAccessor view = null;
            byte* pointer = null;
            bool acquired = false, transferred = false;
            long position = reader.BaseStream.Position;
            long started = Stopwatch.GetTimestamp();
            try
            {
                try
                {
                    if (current == null || stream == null || !ReferenceEquals(stream, current.Stream) || count <= 0 ||
                        stream.Length != current.Length || position < 0 || current.Length - position != count ||
                        File.GetLastWriteTimeUtc(stream.Name).Ticks != current.Modified)
                        throw new InvalidOperationException("DDS immutable payload metadata differs.");
                    mapping = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                    view = mapping.CreateViewAccessor(position, count, MemoryMappedFileAccess.Read);
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer); acquired = true;
                    if (pointer == null) throw new InvalidOperationException("DDS mapping pointer absent.");
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException || error is ArgumentException || error is InvalidOperationException)
                {
                    active.fallbacks++;
                    if (active.fallbacks <= 3) Debug.LogWarning(Marker + "mappedFallback=" + error.GetType().Name + ":" + error.Message);
                    texture.LoadRawTextureData(reader.ReadBytes(count));
                    return;
                }
                texture.LoadRawTextureData(new IntPtr(pointer + view.PointerOffset), count);
                stream.Position = checked(position + count);
                current.Mapping = mapping; current.View = view; transferred = true;
                active.mappedCalls++; active.mappedBytes += count;
            }
            finally
            {
                if (!transferred)
                {
                    if (acquired) view.SafeMemoryMappedViewHandle.ReleasePointer();
                    view?.Dispose(); mapping?.Dispose();
                }
                active.mappedTicks += Stopwatch.GetTimestamp() - started;
            }
        }
        public void Update() { if (requested && !exported && HighLogic.LoadedScene == GameScenes.MAINMENU) Export(); }
        public void OnApplicationQuit() { if (requested) Export(); }
        void Export()
        {
            if (exported) return;
            exported = true;
            Debug.Log(Marker + "status=" + status + " mapped=" + candidate + " calls=" + calls + " bytes=" + bytes +
                " seconds=" + ((double)ticks / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture) +
                " failures=" + failures + " mappedCalls=" + mappedCalls + " mappedBytes=" + mappedBytes +
                " uploadAndMappingSeconds=" + ((double)mappedTicks / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture) + " fallbacks=" + fallbacks);
        }
        void Cleanup() { if (harmony != null && target != null) harmony.Unpatch(target, HarmonyPatchType.All, Owner); }
        public void OnDestroy() { if (requested) { Export(); Cleanup(); if (active == this) active = null; } }
    }
}
