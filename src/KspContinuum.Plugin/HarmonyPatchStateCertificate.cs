using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace KspContinuum
{
    internal sealed class HarmonyPatchStateCertificate
    {
        const string RuntimeSha = "19ce60ac3280f72ec1751d36a40cb7e2fece2934df8345969dc7feb83bd633e4";
        const string RuntimeMvid = "212c5fc0-752a-439c-8523-73c986c00088";
        const int MaximumEntries = 16384, MaximumEntryBytes = 1024 * 1024, MaximumTotalBytes = 64 * 1024 * 1024;
        readonly Func<Dictionary<MethodBase, byte[]>> readState;
        readonly Dictionary<MethodBase, byte[]> state;
        readonly Entry[] entries;
        bool failed;
        internal int MethodCount { get { return entries.Length; } }
        internal int SerializedBytes { get; private set; }
        internal long Checks { get; private set; }
        internal long ComparedBytes { get; private set; }
        internal string Failure { get; private set; }

        sealed class Entry
        {
            internal MethodBase method;
            internal bool present;
            internal byte[] bytes;
        }

        HarmonyPatchStateCertificate(Func<Dictionary<MethodBase, byte[]>> reader, IEnumerable<MethodBase> methods, Action qualify)
        {
            readState = reader;
            state = readState();
            Require(state != null, "Harmony raw state is absent.");
            var targets = new List<MethodBase>();
            var seen = new HashSet<MethodBase>();
            foreach (MethodBase method in methods)
            {
                Require(method != null && seen.Add(method) && targets.Count < MaximumEntries, "Raw certificate target identity/count differs.");
                targets.Add(method);
            }
            Require(targets.Count > 0, "Raw certificate has no targets.");
            entries = new Entry[targets.Count];
            // The pinned GetPatchInfo and UpdatePatchInfo use this same lock. Qualification and copies form one cold seal.
            lock (state)
            {
                Require(ReferenceEquals(readState(), state), "Harmony state dictionary changed before seal.");
                qualify();
                for (int i = 0; i < entries.Length; i++)
                {
                    byte[] bytes;
                    bool present = state.TryGetValue(targets[i], out bytes);
                    if (bytes != null)
                    {
                        Require(bytes.Length <= MaximumEntryBytes && SerializedBytes <= MaximumTotalBytes - bytes.Length, "Raw patch-state byte bound exceeded.");
                        SerializedBytes += bytes.Length;
                    }
                    entries[i] = new Entry { method = targets[i], present = present, bytes = bytes == null ? null : (byte[])bytes.Clone() };
                }
                Require(ReferenceEquals(readState(), state), "Harmony state dictionary changed during seal.");
            }
        }

        internal static bool TrySeal(IEnumerable<MethodBase> methods, Action qualify, out HarmonyPatchStateCertificate certificate, out string unsupported)
        {
            certificate = null;
            Func<Dictionary<MethodBase, byte[]>> reader;
            try
            {
                Assembly assembly = typeof(Harmony).Assembly;
                Require(assembly.GetName().Name == "0Harmony" && assembly.GetName().Version == new Version(2, 2, 1, 0) &&
                    assembly.ManifestModule.ModuleVersionId == new Guid(RuntimeMvid) && HashFile(assembly.Location) == RuntimeSha,
                    "Loaded Harmony is not the pinned 2.2.1 raw-state ABI.");
                Type type = assembly.GetType("HarmonyLib.HarmonySharedState", false);
                Require(type != null, "Pinned Harmony shared-state type is absent.");
                FieldInfo field = type.GetField("state", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                FieldInfo version = type.GetField("actualVersion", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                MethodInfo get = type.GetMethod("GetPatchInfo", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly, null, new[] { typeof(MethodBase) }, null);
                MethodInfo update = type.GetMethod("UpdatePatchInfo", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                Require(field != null && field.IsInitOnly && field.FieldType == typeof(Dictionary<MethodBase, byte[]>) && field.MetadataToken == 0x0400004b &&
                    version != null && version.FieldType == typeof(int) && (int)version.GetValue(null) == 101 &&
                    get != null && get.MetadataToken == 0x06000090 && get.ReturnType.FullName == "HarmonyLib.PatchInfo" &&
                    update != null && update.MetadataToken == 0x06000092 && update.ReturnType == typeof(void), "Pinned Harmony state/version/method shape differs.");
                var getter = new DynamicMethod("ContinuumReadPinnedHarmonyState", typeof(Dictionary<MethodBase, byte[]>), Type.EmptyTypes, type, true);
                ILGenerator il = getter.GetILGenerator();
                il.Emit(OpCodes.Ldsfld, field); il.Emit(OpCodes.Ret);
                reader = (Func<Dictionary<MethodBase, byte[]>>)getter.CreateDelegate(typeof(Func<Dictionary<MethodBase, byte[]>>));
                Require(reader() != null, "Pinned Harmony state dictionary is absent.");
            }
            catch (Exception error)
            {
                unsupported = error.GetType().Name + ":" + error.Message;
                return false;
            }
            // A graph/IL qualification failure is an admission failure, never an unsupported-ABI fallback.
            certificate = new HarmonyPatchStateCertificate(reader, methods, qualify);
            unsupported = null;
            return true;
        }

        internal bool Matches()
        {
            Checks++;
            if (failed) return false;
            if (!ReferenceEquals(readState(), state)) return Fail("Harmony state dictionary identity changed.");
            lock (state)
            {
                if (!ReferenceEquals(readState(), state)) return Fail("Harmony state dictionary identity changed under lock.");
                for (int i = 0; i < entries.Length; i++)
                {
                    Entry entry = entries[i]; byte[] current;
                    bool present = state.TryGetValue(entry.method, out current);
                    if (present != entry.present || (current == null) != (entry.bytes == null)) return Fail("Harmony raw entry presence/null changed: " + entry.method);
                    if (current == null) continue;
                    if (current.Length != entry.bytes.Length) return Fail("Harmony raw entry length changed: " + entry.method);
                    int compared;
                    bool same = SameBytes(current, entry.bytes, out compared);
                    ComparedBytes += compared;
                    if (!same) return Fail("Harmony raw entry bytes changed: " + entry.method);
                }
                return ReferenceEquals(readState(), state) || Fail("Harmony state dictionary changed during check.");
            }
        }

        internal bool Fail(string reason) { failed = true; if (Failure == null) Failure = reason; return false; }
        internal static bool SameBytes(byte[] a, byte[] b)
        {
            int compared;
            return SameBytes(a, b, out compared);
        }
        internal static unsafe bool SameBytes(byte[] a, byte[] b, out int compared)
        {
            compared = 0;
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != b.Length) return false;
            int length = a.Length;
            if (length == 0) return true;
            fixed (byte* left = a, right = b)
            {
                int offset = 0;
                // Read complete aligned words only; unusual allocator alignment retains the scalar path.
                if ((((ulong)left | (ulong)right) & 7) == 0)
                {
                    while (offset <= length - 32)
                    {
                        if (*(ulong*)(left + offset) != *(ulong*)(right + offset) ||
                            *(ulong*)(left + offset + 8) != *(ulong*)(right + offset + 8) ||
                            *(ulong*)(left + offset + 16) != *(ulong*)(right + offset + 16) ||
                            *(ulong*)(left + offset + 24) != *(ulong*)(right + offset + 24)) break;
                        offset += 32;
                    }
                    while (offset <= length - 8)
                    {
                        if (*(ulong*)(left + offset) != *(ulong*)(right + offset)) break;
                        offset += 8;
                    }
                }
                // No reference shortcut: in-place mutations still compare against the retained seal. The bad-word fallback preserves the earliest-byte counter.
                for (; offset < length; offset++)
                    if (left[offset] != right[offset]) { compared = offset + 1; return false; }
                compared = length; return true;
            }
        }
        static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        const string WitnessOwn = "continuum.raw-patch-state-witness.own", WitnessForeign = "continuum.raw-patch-state-witness.foreign";
        [MethodImpl(MethodImplOptions.NoInlining)] static int WitnessA(int value) { return value + 1; }
        [MethodImpl(MethodImplOptions.NoInlining)] static int WitnessB(int value) { return value + 2; }
        static void WitnessPrefix() { }
        static void WitnessForeignPrefix() { }
        static MethodInfo WitnessMethod(string name) { return typeof(HarmonyPatchStateCertificate).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly); }
        static bool Empty(MethodBase method)
        {
            Patches p = Harmony.GetPatchInfo(method);
            return p == null || p.Prefixes.Count == 0 && p.Postfixes.Count == 0 && p.Transpilers.Count == 0 && p.Finalizers.Count == 0;
        }
        static HarmonyPatchStateCertificate WitnessSeal(MethodInfo a, MethodInfo b, MethodInfo prefix)
        {
            HarmonyPatchStateCertificate certificate; string unsupported;
            Require(TrySeal(new MethodBase[] { a, b }, () => {
                Patches p = Harmony.GetPatchInfo(a);
                Require(p != null && p.Prefixes.Count == 1 && p.Postfixes.Count == 0 && p.Transpilers.Count == 0 && p.Finalizers.Count == 0 &&
                    p.Prefixes[0].owner == WitnessOwn && p.Prefixes[0].priority == Priority.Normal && p.Prefixes[0].before.Length == 0 && p.Prefixes[0].after.Length == 0 &&
                    p.Prefixes[0].PatchMethod.MetadataToken == prefix.MetadataToken && p.Prefixes[0].PatchMethod.DeclaringType == prefix.DeclaringType &&
                    p.Prefixes[0].PatchMethod.Module.ModuleVersionId == prefix.Module.ModuleVersionId && Empty(b), "Cold witness graph differs.");
            }, out certificate, out unsupported), "Witness requires pinned actual raw-state ABI: " + unsupported);
            return certificate;
        }
        internal static void RunRequestedWitness(string runId, IEnumerable<MethodBase> gameMethods, Action qualifyGame)
        {
            string output = Environment.GetEnvironmentVariable("CONTINUUM_PATCH_STATE_WITNESS");
            if (string.IsNullOrEmpty(output)) return;
            Require(!string.IsNullOrEmpty(runId) && Path.IsPathRooted(output) && !File.Exists(output) && Directory.Exists(Path.GetDirectoryName(output)), "Fresh absolute patch-state witness path and run identity required.");
            long begin = Stopwatch.GetTimestamp();
            MethodInfo a = WitnessMethod(nameof(WitnessA)), b = WitnessMethod(nameof(WitnessB)), prefix = WitnessMethod(nameof(WitnessPrefix)), foreignPrefix = WitnessMethod(nameof(WitnessForeignPrefix));
            var own = new Harmony(WitnessOwn); var foreign = new Harmony(WitnessForeign);
            HarmonyPatchStateCertificate game = null;
            bool ownAttempted = false, foreignAAttempted = false, foreignBAttempted = false, restored = false;
            bool foreignRejected = false, removalLatched = false, inplaceRejected = false, revertLatched = false, absentRejected = false, gameUnchanged = false;
            int unchangedChecks = 0;
            string fault = null, cleanupFault = null, unsupported;
            try
            {
                Require(Empty(a) && Empty(b), "Witness methods already owned.");
                Require(TrySeal(gameMethods, qualifyGame, out game, out unsupported), "Game witness requires pinned ABI: " + unsupported);
                Require(game.Matches(), "Game graph changed before isolated witness.");
                ownAttempted = true; own.Patch(a, prefix: new HarmonyMethod(prefix) { priority = Priority.Normal });
                var certificate = WitnessSeal(a, b, prefix);
                Require(certificate.Matches() && certificate.Matches(), "Unchanged witness rejected."); unchangedChecks = 2;
                foreignAAttempted = true; foreign.Patch(a, prefix: new HarmonyMethod(foreignPrefix) { priority = Priority.Normal });
                foreignRejected = !certificate.Matches(); Require(foreignRejected, "Foreign prefix accepted by seal.");
                foreign.Unpatch(a, HarmonyPatchType.Prefix, WitnessForeign);
                removalLatched = !certificate.Matches(); Require(removalLatched, "Foreign removal silently renewed seal.");
                certificate = WitnessSeal(a, b, prefix);
                byte[] bytes;
                lock (certificate.state) Require(certificate.state.TryGetValue(a, out bytes) && bytes != null && bytes.Length > 0, "Witness serialized state absent.");
                int index = bytes.Length - 1; byte original = bytes[index];
                try
                {
                    lock (certificate.state) bytes[index] ^= 1;
                    inplaceRejected = !certificate.Matches(); Require(inplaceRejected, "Same-array mutation accepted.");
                }
                finally { lock (certificate.state) bytes[index] = original; }
                revertLatched = !certificate.Matches(); Require(revertLatched, "Byte restoration silently renewed seal.");
                certificate = WitnessSeal(a, b, prefix);
                foreignBAttempted = true; foreign.Patch(b, prefix: new HarmonyMethod(foreignPrefix) { priority = Priority.Normal });
                absentRejected = !certificate.Matches(); Require(absentRejected, "Previously absent method ownership accepted.");
            }
            catch (Exception error) { fault = error.ToString(); }
            finally
            {
                var cleanup = new StringBuilder();
                foreach (MethodInfo method in new[] { a, b })
                {
                    if (method == a && foreignAAttempted || method == b && foreignBAttempted)
                        try { foreign.Unpatch(method, HarmonyPatchType.Prefix, WitnessForeign); } catch (Exception error) { cleanup.AppendLine(error.ToString()); }
                    if (method == a && ownAttempted)
                        try { own.Unpatch(method, HarmonyPatchType.Prefix, WitnessOwn); } catch (Exception error) { cleanup.AppendLine(error.ToString()); }
                }
                try { restored = Empty(a) && Empty(b); Require(restored, "Isolated witness owned hooks remain."); } catch (Exception error) { cleanup.AppendLine(error.ToString()); }
                try { gameUnchanged = game != null && game.Matches(); Require(gameUnchanged, "Game graph changed during isolated witness."); } catch (Exception error) { cleanup.AppendLine(error.ToString()); }
                if (cleanup.Length > 0) cleanupFault = cleanup.ToString();
            }
            bool passed = fault == null && cleanupFault == null && restored && gameUnchanged && unchangedChecks == 2 && foreignRejected && removalLatched && inplaceRejected && revertLatched && absentRejected;
            long end = Stopwatch.GetTimestamp();
            var json = new StringBuilder("{");
            Json(json, "schema", "continuum-harmony-raw-patch-state-witness/v1"); Json(json, "runId", runId);
            Json(json, "status", passed ? "passed" : "failed"); Json(json, "harmonyPath", typeof(Harmony).Assembly.Location);
            Json(json, "harmonySha256", HashFile(typeof(Harmony).Assembly.Location)); Json(json, "harmonyMvid", typeof(Harmony).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
            Json(json, "helperAssemblySha256", HashFile(typeof(HarmonyPatchStateCertificate).Assembly.Location));
            Json(json, "helperAssemblyMvid", typeof(HarmonyPatchStateCertificate).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
            Json(json, "failure", fault); Json(json, "cleanupFailure", cleanupFault);
            Json(json, "scope", "Cold actual loaded Harmony mutations on two isolated managed methods before the real game ownership seal. Full game graph qualified and raw selected-game entries compared before/after. Original game/native facade hooks unchanged. Public foreign prefix, same-array raw mutation and previously absent method ownership must reject; restoring bytes/foreign removal cannot renew a failed seal. Empty witness graphs restored via owned-prefix-only unpatch. No numerical/performance qualification.");
            json.Append("\"processId\":").Append(Process.GetCurrentProcess().Id).Append(",\"startedTicks\":").Append(begin).Append(",\"endedTicks\":").Append(end).Append(",\"clockFrequency\":").Append(Stopwatch.Frequency);
            json.Append(",\"baselineUnchangedChecks\":").Append(unchangedChecks).Append(",\"foreignMutationRejected\":").Append(foreignRejected ? "true" : "false");
            json.Append(",\"foreignRemovalLatched\":").Append(removalLatched ? "true" : "false").Append(",\"inPlaceMutationRejected\":").Append(inplaceRejected ? "true" : "false");
            json.Append(",\"inPlaceRevertLatched\":").Append(revertLatched ? "true" : "false").Append(",\"absentMethodMutationRejected\":").Append(absentRejected ? "true" : "false");
            json.Append(",\"cleanupRestored\":").Append(restored ? "true" : "false").Append(",\"gameStateUnchanged\":").Append(gameUnchanged ? "true" : "false").Append('}');
            byte[] encoded = Encoding.UTF8.GetBytes(json.ToString());
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(encoded, 0, encoded.Length);
            Require(passed, "Cold actual Harmony raw-state witness failed; retained receipt: " + output);
        }
        static void Json(StringBuilder json, string name, string value)
        {
            json.Append('"').Append(name).Append("\":");
            if (value == null) json.Append("null");
            else
            {
                json.Append('"');
                foreach (char c in value)
                {
                    if (c == '"' || c == '\\') json.Append('\\').Append(c);
                    else if (c < 32) json.Append("\\u").Append(((int)c).ToString("x4"));
                    else json.Append(c);
                }
                json.Append('"');
            }
            json.Append(',');
        }
        static void Require(bool pass, string reason) { if (!pass) throw new InvalidOperationException(reason); }
    }
}
