using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KspContinuum
{
    internal static class MonoJitSampleMap
    {
        const int MaximumBytes = 32 * 1024 * 1024, MaximumAddresses = 8192;
        [StructLayout(LayoutKind.Sequential)] struct DlInfo { public IntPtr fileName, imageBase, symbolName, symbolAddress; }
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string name);
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern int dladdr(IntPtr address, out DlInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NoArgument();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PointerArgument(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Lookup(IntPtr domain, IntPtr address);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntegerArgument(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint TokenArgument(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FullName(IntPtr method, int signature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Free(IntPtr value);

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-mono-jit-sample-map/v1", runId, status = "failed", detail;
            public int processId, sampleHeaderPid, addressCount, mappedCount, unresolvedCount;
            public long sampleBytes, captureEndTicks, mapBeginTicks, mapEndTicks, clockFrequency = Stopwatch.Frequency;
            public string samplePath, sampleSha256, signalPath, signalSha256, runtimeLibraryPath, runtimeLibrarySha256;
            public string scope = "Cold same-process lookup of exact raw call-graph PCs in current/root Mono domains, after capture-end publication and diagnostic teardown. No forced compilation, native symbol guessing, nearest-PC attribution or sample-count-to-time conversion. Existing JIT metadata may be absent after dynamic wrapper reclamation; late lookup does not prove sampled-time code lifetime/address reuse absent. Dynamic wrappers and token-zero methods retain their actual metadata rather than stock identity. Opaque handles and metadata strings are borrowed; only mono_method_full_name result is owned and mono_free releases it.";
            public DomainRow[] domains = new DomainRow[0];
            public AddressRow[] rows = new AddressRow[0];
        }
        [Serializable] public sealed class DomainRow
        {
            public int id;
            public string name, address;
            public bool current, root;
        }
        [Serializable] public sealed class AddressRow
        {
            public string pc, status = "unresolved", reason, domainName, codeStart, codeEnd, methodName, imageName, imageMvid;
            public int occurrences, domainId, codeSize, nativeOffset;
            public uint methodToken;
            public bool dynamicWrapper;
        }
        [Serializable] sealed class SignalData
        {
            public string schema = null, runId = null, status = null;
            public int processId = 0;
            public long clockTimestamp = 0, clockFrequency = 0;
        }
        sealed class Api
        {
            readonly ReportData report;
            public readonly NoArgument current, root;
            public readonly IntegerArgument domainId, codeSize;
            public readonly Lookup lookup;
            public readonly PointerArgument domainName, codeStart, method, methodClass, classImage, imageName, imageGuid;
            public readonly TokenArgument token;
            public readonly FullName fullName;
            public readonly Free free;
            public Api(ReportData report)
            {
                this.report = report;
                current = Bind<NoArgument>("mono_domain_get"); root = Bind<NoArgument>("mono_get_root_domain");
                domainId = Bind<IntegerArgument>("mono_domain_get_id"); domainName = Bind<PointerArgument>("mono_domain_get_friendly_name");
                lookup = Bind<Lookup>("mono_jit_info_table_find"); codeStart = Bind<PointerArgument>("mono_jit_info_get_code_start"); codeSize = Bind<IntegerArgument>("mono_jit_info_get_code_size");
                method = Bind<PointerArgument>("mono_jit_info_get_method"); fullName = Bind<FullName>("mono_method_full_name"); token = Bind<TokenArgument>("mono_method_get_token");
                methodClass = Bind<PointerArgument>("mono_method_get_class"); classImage = Bind<PointerArgument>("mono_class_get_image");
                imageName = Bind<PointerArgument>("mono_image_get_name"); imageGuid = Bind<PointerArgument>("mono_image_get_guid"); free = Bind<Free>("mono_free");
            }
            T Bind<T>(string name) where T : class
            {
                IntPtr pointer = dlsym(new IntPtr(-2), name); DlInfo info;
                Need(pointer != IntPtr.Zero, "Loaded Mono export unavailable: " + name);
                Need(dladdr(pointer, out info) != 0, "Loaded Mono export image unavailable: " + name);
                string path = Text(info.fileName);
                Need(Path.IsPathRooted(path) && Path.GetFileName(path) == "libmonobdwgc-2.0.dylib", "Export is not the actual embedded Unity Mono: " + name);
                if (report.runtimeLibraryPath == null) { report.runtimeLibraryPath = path; report.runtimeLibrarySha256 = Hash(File.ReadAllBytes(path)); }
                else Need(path == report.runtimeLibraryPath, "Mono exports originate from different libraries.");
                return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
            }
        }

        public static bool ResolveRequested(string runId, long captureEndTicks, string signalDirectory, bool captureActive)
        {
            string sample = Environment.GetEnvironmentVariable("CONTINUUM_MONO_JIT_SAMPLE"), output = Environment.GetEnvironmentVariable("CONTINUUM_MONO_JIT_MAP");
            if (string.IsNullOrEmpty(sample) && string.IsNullOrEmpty(output)) return true;
            var report = new ReportData { runId = runId, processId = Process.GetCurrentProcess().Id, samplePath = sample, captureEndTicks = captureEndTicks, mapBeginTicks = Stopwatch.GetTimestamp() };
            try
            {
                Need(!captureActive && captureEndTicks > 0 && report.mapBeginTicks >= captureEndTicks, "Cold mapping requires closed capture.");
                Need(!string.IsNullOrEmpty(runId) && Path.IsPathRooted(sample) && Path.IsPathRooted(output) && Path.GetFullPath(sample) != Path.GetFullPath(output) && !File.Exists(output), "Absolute input and fresh distinct output required.");
                Need(signalDirectory != null, "Owned capture-end signal directory absent.");
                report.signalPath = Path.Combine(signalDirectory, "capture-end.json");
                byte[] signalBytes = File.ReadAllBytes(report.signalPath); Need(signalBytes.Length > 0 && signalBytes.Length <= 65536, "Capture-end signal bound differs.");
                report.signalSha256 = Hash(signalBytes);
                SignalData signal = JsonUtility.FromJson<SignalData>(Encoding.UTF8.GetString(signalBytes));
                Need(signal != null && signal.schema == "continuum-flight-capture-signal/v1" && signal.runId == runId && signal.processId == report.processId && signal.clockTimestamp == captureEndTicks && signal.clockFrequency == Stopwatch.Frequency && signal.status == "complete", "Foreign/incomplete capture-end signal.");
                byte[] bytes = ReadSample(sample); report.sampleBytes = bytes.Length; report.sampleSha256 = Hash(bytes);
                string text = new UTF8Encoding(false, true).GetString(bytes);
                Match header = Regex.Match(text, @"(?m)^Process:\s+[^\r\n]*\[(\d+)\]\s*$");
                Need(header.Success && int.TryParse(header.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out report.sampleHeaderPid) && report.sampleHeaderPid == report.processId, "Sample header does not belong to this live PID.");
                Need(text.StartsWith("Analysis of sampling ", StringComparison.Ordinal) && text.Contains("(pid " + report.processId + ")") && text.Contains("Analysis Tool:   /usr/bin/sample"), "Unexpected sample producer/header.");
                int begin = text.IndexOf("Call graph:\n", StringComparison.Ordinal), end = text.IndexOf("Total number in stack", StringComparison.Ordinal), images = text.IndexOf("Binary Images:", StringComparison.Ordinal);
                Need(begin >= 0 && end > begin && images > end && text.EndsWith("\n", StringComparison.Ordinal), "Incomplete native sample structure.");
                MatchCollection matches = Regex.Matches(text.Substring(begin, end - begin), @"\[0x([0-9a-fA-F]{1,16})\]");
                var addresses = new SortedDictionary<ulong, int>();
                foreach (Match match in matches)
                {
                    ulong pc; Need(ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pc), "Invalid unsigned sample PC.");
                    int count; addresses.TryGetValue(pc, out count); addresses[pc] = count + 1; Need(addresses.Count <= MaximumAddresses, "Unique sampled PC bound exceeded.");
                }
                Need(addresses.Count > 0, "No actual call-graph PCs."); report.addressCount = addresses.Count;
                var api = new Api(report); var handles = new List<IntPtr>(); var domains = new List<DomainRow>();
                IntPtr current = api.current(), root = api.root();
                foreach (IntPtr domain in new[] { current, root })
                {
                    Need(domain != IntPtr.Zero, "Live Mono domain absent."); if (handles.Contains(domain)) continue;
                    handles.Add(domain); domains.Add(new DomainRow { id = api.domainId(domain), name = Text(api.domainName(domain)), address = Hex(domain), current = domain == current, root = domain == root });
                }
                report.domains = domains.ToArray(); var rows = new List<AddressRow>(addresses.Count);
                foreach (var address in addresses)
                {
                    AddressRow row = Resolve(api, handles, domains, address.Key); row.occurrences = address.Value; rows.Add(row);
                    if (row.status == "mapped") report.mappedCount++; else report.unresolvedCount++;
                }
                report.rows = rows.ToArray();
                Need(report.sampleSha256 == Hash(ReadSample(sample)) && report.signalSha256 == Hash(File.ReadAllBytes(report.signalPath)) && report.runtimeLibrarySha256 == Hash(File.ReadAllBytes(report.runtimeLibraryPath)), "Input/signal/runtime changed during mapping.");
                Need(report.mappedCount > 0, "No sampled PCs have live managed JIT metadata.");
                report.status = "completed";
            }
            catch (Exception error) { report.status = "failed"; report.detail = error.ToString(); }
            report.mapEndTicks = Stopwatch.GetTimestamp();
            try
            {
                Need(Path.IsPathRooted(output) && !File.Exists(output), "Fresh map destination absent/occupied.");
                byte[] encoded = Encoding.UTF8.GetBytes(ReportJson.Encode(report));
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(encoded, 0, encoded.Length);
            }
            catch (Exception error) { UnityEngine.Debug.LogError("[Continuum Mono JIT map] Export failed: " + error); return false; }
            return report.status == "completed";
        }
        static AddressRow Resolve(Api api, List<IntPtr> handles, List<DomainRow> domains, ulong pc)
        {
            var row = new AddressRow { pc = Hex(pc), reason = "no-live-JIT-entry-in-current-or-root-domain" };
            if (pc == 0 || pc > long.MaxValue) { row.reason = "ineligible-non-user-or-sentinel-address"; return row; }
            for (int i = 0; i < handles.Count; i++)
            {
                IntPtr info = api.lookup(handles[i], new IntPtr((long)pc)); if (info == IntPtr.Zero) continue;
                ulong start = (ulong)api.codeStart(info).ToInt64(); int size = api.codeSize(info);
                Need(start > 0 && size > 0 && size <= 64 * 1024 * 1024 && start <= pc && pc - start < (ulong)size, "Live JIT interval does not contain exact sampled PC.");
                IntPtr method = api.method(info); if (method == IntPtr.Zero) { row.reason = "JIT-entry-without-method"; continue; }
                IntPtr name = api.fullName(method, 1); string fullName;
                try { fullName = Text(name); } finally { if (name != IntPtr.Zero) api.free(name); }
                IntPtr klass = api.methodClass(method), image = klass == IntPtr.Zero ? IntPtr.Zero : api.classImage(klass);
                Need(!string.IsNullOrEmpty(fullName) && image != IntPtr.Zero, "Live JIT method metadata unavailable.");
                uint token = api.token(method); string imageName = Text(api.imageName(image)), guid = Text(api.imageGuid(image));
                if (row.status == "mapped")
                {
                    if (row.codeStart != Hex(start) || row.codeSize != size || row.methodName != fullName || row.methodToken != token || row.imageName != imageName || row.imageMvid != guid)
                    { row.status = "unresolved"; row.reason = "ambiguous-domain-JIT-mapping"; return row; }
                    continue;
                }
                row.status = "mapped"; row.reason = null; row.domainId = domains[i].id; row.domainName = domains[i].name;
                row.codeStart = Hex(start); row.codeEnd = Hex(checked(start + (ulong)size)); row.codeSize = size; row.nativeOffset = (int)(pc - start);
                row.methodName = fullName; row.methodToken = token; row.imageName = imageName; row.imageMvid = guid;
                row.dynamicWrapper = token == 0 || fullName.IndexOf("wrapper", StringComparison.OrdinalIgnoreCase) >= 0 || fullName.IndexOf("DMD<", StringComparison.Ordinal) >= 0;
            }
            return row;
        }
        static byte[] ReadSample(string path)
        {
            var file = new FileInfo(path); Need(file.Exists && file.Length > 0 && file.Length <= MaximumBytes, "Native sample file bound differs.");
            byte[] bytes = File.ReadAllBytes(path); Need(bytes.Length <= MaximumBytes, "Native sample grew beyond bound."); return bytes;
        }
        static string Text(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            int length = 0; while (length < 16384 && Marshal.ReadByte(pointer, length) != 0) length++;
            Need(length < 16384, "Mono metadata string bound exceeded.");
            var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return new UTF8Encoding(false, true).GetString(bytes);
        }
        static string Hex(IntPtr value) { return Hex((ulong)value.ToInt64()); }
        static string Hex(ulong value) { return "0x" + value.ToString("x", CultureInfo.InvariantCulture); }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void Need(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    }
}
