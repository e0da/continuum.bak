using System.Text.Json;
using KspContinuum;

internal static partial class Program
{
    static int CompareCapacity(string[] arguments)
    {
        string baseline = "", candidate = "", output = "";
        if (arguments.Length != 8) throw new ArgumentException("Compare mode requires baseline, candidate and a fresh output directory.");
        var keys = new HashSet<string>();
        for (int i = 0; i < arguments.Length; i += 2)
        {
            Need(keys.Add(arguments[i]), "Duplicate comparison option.");
            switch (arguments[i])
            {
                case "--mode": Need(arguments[i + 1] == "compare", "Comparison mode differs."); break;
                case "--baseline": baseline = arguments[i + 1]; break;
                case "--candidate": candidate = arguments[i + 1]; break;
                case "--output": output = arguments[i + 1]; break;
                default: throw new ArgumentException("Unknown comparison option.");
            }
        }
        Need(Path.IsPathFullyQualified(baseline) && Path.IsPathFullyQualified(candidate) && Path.IsPathFullyQualified(output), "Comparison paths must be absolute.");
        Need(Directory.Exists(baseline) && Directory.Exists(candidate) && !Directory.Exists(output) && !File.Exists(output), "Comparison inputs absent or output exists.");
        Directory.CreateDirectory(output);
        var rows = new List<object>(); string failure = ""; bool mutationRejected = false;
        try
        {
            var aReceiptPath = Path.Combine(baseline, "receipt.json"); var bReceiptPath = Path.Combine(candidate, "receipt.json");
            using var aReceipt = JsonDocument.Parse(File.ReadAllBytes(aReceiptPath)); using var bReceipt = JsonDocument.Parse(File.ReadAllBytes(bReceiptPath));
            var aRoot = aReceipt.RootElement; var bRoot = bReceipt.RootElement;
            foreach (var root in new[] { aRoot, bRoot })
                Need(root.GetProperty("Schema").GetString() == "continuum-capacity-runtime/v1" && root.GetProperty("Status").GetString() == "passed"
                    && root.GetProperty("RestoredPacketIdentical").GetBoolean() && root.GetProperty("ContinuationPacketIdentical").GetBoolean(), "Comparison requires completed qualified runtime receipts.");
            Need(FileHash(Path.Combine(baseline, "fixture.json")) == aRoot.GetProperty("FixtureSha256").GetString()
                && FileHash(Path.Combine(candidate, "fixture.json")) == bRoot.GetProperty("FixtureSha256").GetString()
                && aRoot.GetProperty("FixtureSha256").GetString() == bRoot.GetProperty("FixtureSha256").GetString(), "Actual fixture provenance differs.");
            foreach (string key in new[] { "Family", "Provider", "Strategy", "Parts", "Bodies", "Regions", "Steps", "ContinuationSteps", "Delta", "ActiveFraction", "Throttle" })
                Need(aRoot.GetProperty("Options").GetProperty(key).GetRawText() == bRoot.GetProperty("Options").GetProperty(key).GetRawText(), "Case control differs: " + key);
            bool Deadline(JsonElement root) => root.GetProperty("Options").TryGetProperty("DeadlineProbe", out var value) && value.GetBoolean();
            Need(Deadline(aRoot) == Deadline(bRoot), "Deadline treatment differs.");
            var originalFiles = Directory.GetFiles(baseline, "*.ofcb").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var candidateFiles = Directory.GetFiles(candidate, "*.ofcb").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Need(originalFiles.Length > 0 && originalFiles.SequenceEqual(candidateFiles), "Complete checkpoint filename sets differ.");
            foreach (var name in originalFiles)
            {
                string aPath = Path.Combine(baseline, name!), bPath = Path.Combine(candidate, name!);
                byte[] aRaw = File.ReadAllBytes(aPath), bRaw = File.ReadAllBytes(bPath);
                var a = OwnedRuntimeCheckpointBinary.Decode(aRaw); var b = OwnedRuntimeCheckpointBinary.Decode(bRaw);
                Need(a.state != null && b.state != null && a.state.source != null && b.state.source != null, "Comparison source/physical state absent.");
                Need(a.sourceSha256 == aRoot.GetProperty("CoreSha256").GetString() && b.sourceSha256 == bRoot.GetProperty("CoreSha256").GetString()
                    && a.state.source.processId == aRoot.GetProperty("ProcessId").GetInt32() && b.state.source.processId == bRoot.GetProperty("ProcessId").GetInt32()
                    && a.state.source.nativeManifestSha256 == aRoot.GetProperty("FixtureSha256").GetString()
                    && b.state.source.nativeManifestSha256 == bRoot.GetProperty("FixtureSha256").GetString(), "Packet executable/process/source provenance differs from receipt.");
                var originalMetadata = new { sourceSha256 = a.sourceSha256, processId = a.state.source.processId };
                var candidateMetadata = new { sourceSha256 = b.sourceSha256, processId = b.state.source.processId };
                a.sourceSha256 = b.sourceSha256 = new string('0', 64); a.state.source.processId = b.state.source.processId = 1;
                byte[] normalizedA = OwnedRuntimeCheckpointBinary.Encode(a), normalizedB = OwnedRuntimeCheckpointBinary.Encode(b);
                bool equal = normalizedA.AsSpan().SequenceEqual(normalizedB);
                rows.Add(new { file = name, originalSha256 = Hash(aRaw), candidateSha256 = Hash(bRaw), originalMetadata, candidateMetadata,
                    normalizedOriginalSha256 = Hash(normalizedA), normalizedCandidateSha256 = Hash(normalizedB), completeNormalizedEqual = equal });
                Need(equal, "Physical or future-relevant checkpoint fields differ: " + name);
                if (!mutationRejected)
                {
                    Need(b.state.temperature != null && b.state.temperature.Length > 0, "Mutation witness needs a real temperature row.");
                    b.state.temperature[0] += .125;
                    mutationRejected = !normalizedA.AsSpan().SequenceEqual(OwnedRuntimeCheckpointBinary.Encode(b));
                    Need(mutationRejected, "Valid changed physical state was ignored by comparison.");
                }
                Need(Hash(File.ReadAllBytes(aPath)) == Hash(aRaw) && Hash(File.ReadAllBytes(bPath)) == Hash(bRaw), "Inputs changed during comparison.");
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        var receipt = new { schema = "continuum-capacity-comparison/v1", status = failure.Length == 0 ? "passed" : "failed", failure, baseline, candidate,
            excludedFields = new[] { "host.sourceSha256", "host.state.source.processId" }, mutationRejected, rows,
            scope = "Exact complete re-encoded checkpoint equality excluding only executable image provenance and process identity. Every physical definition, bank, resource/provider/thermal/mechanical history, configuration, control and input-history field is retained. No global accuracy or stock-equivalence claim." };
        WriteNew(Path.Combine(output, "comparison.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
        Console.WriteLine(receipt.status + ": " + output); return failure.Length == 0 ? 0 : 2;
    }
}
