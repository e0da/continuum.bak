using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    static int CompareColumnarCapacity(string[] args)
    {
        string baseline = "", candidate = "", output = ""; bool owned = false; string failure = "";
        var comparisons = new List<object>();
        try
        {
            Need(args.Length == 8, "Columnar comparison requires baseline, candidate and output.");
            var seen = new HashSet<string>();
            for (int i = 0; i < args.Length; i += 2)
            {
                Need(seen.Add(args[i]), "Duplicate comparison option.");
                switch (args[i])
                {
                    case "--mode": Need(args[i + 1] == "compare-columnar", "Comparison mode differs."); break;
                    case "--baseline": baseline = args[i + 1]; break;
                    case "--candidate": candidate = args[i + 1]; break;
                    case "--output": output = args[i + 1]; break;
                    default: throw new ArgumentException("Unknown comparison option.");
                }
            }
            Need(Path.IsPathFullyQualified(baseline) && Path.IsPathFullyQualified(candidate) && Path.IsPathFullyQualified(output)
                && Directory.Exists(baseline) && Directory.Exists(candidate) && !Directory.Exists(output), "Comparison paths differ or output exists.");
            Directory.CreateDirectory(output); owned = true;
            using var a = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(baseline, "receipt.json")));
            using var b = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(candidate, "receipt.json")));
            foreach (var root in new[] { a.RootElement, b.RootElement })
                Need(root.GetProperty("Schema").GetString() == "continuum-capacity-runtime/v1" && root.GetProperty("Status").GetString() == "passed"
                    && root.GetProperty("RestoredPacketIdentical").GetBoolean() && root.GetProperty("ContinuationPacketIdentical").GetBoolean()
                    && root.GetProperty("UncommittedBankUnchanged").GetBoolean() && root.GetProperty("Options").GetProperty("StateFormat").GetString() == "columnar", "Comparison requires complete columnar receipts.");
            Need(a.RootElement.GetProperty("FixtureSha256").GetString() == b.RootElement.GetProperty("FixtureSha256").GetString()
                && FileHash(Path.Combine(baseline, "fixture.json")) == a.RootElement.GetProperty("FixtureSha256").GetString()
                && FileHash(Path.Combine(candidate, "fixture.json")) == b.RootElement.GetProperty("FixtureSha256").GetString(), "Actual frozen recipes differ.");
            foreach (string key in new[] { "Family", "Provider", "Strategy", "Parts", "Bodies", "Regions", "Steps", "ContinuationSteps", "Delta", "ActiveFraction", "Throttle", "DeadlineProbe" })
                Need(a.RootElement.GetProperty("Options").GetProperty(key).GetRawText() == b.RootElement.GetProperty("Options").GetProperty(key).GetRawText(), "Case control differs: " + key);
            string[] files = { "checkpoint.ocss", "continuation-original.ocss", "continuation-restored.ocss" };
            foreach (string directory in new[] { baseline, candidate })
                Need(Directory.GetFiles(directory, "*.ocss").Select(Path.GetFileName).OrderBy(x => x).SequenceEqual(files.OrderBy(x => x)), "Image coverage differs.");
            foreach (string name in files)
            {
                string oldPath = Path.Combine(baseline, name), newPath = Path.Combine(candidate, name);
                string oldHash = FileHash(oldPath), newHash = FileHash(newPath);
                string digestField = name == "checkpoint.ocss" ? "CheckpointSha256" : "ContinuationSha256";
                Need(oldHash == a.RootElement.GetProperty(digestField).GetString() && newHash == b.RootElement.GetProperty(digestField).GetString(), "Image digest differs from its qualified receipt.");
                long oldMask = ColumnarImageField(oldPath, a.RootElement.GetProperty("CoreSha256").GetString()!), newMask = ColumnarImageField(newPath, b.RootElement.GetProperty("CoreSha256").GetString()!);
                using var oldFile = File.OpenRead(oldPath); using var newFile = File.OpenRead(newPath);
                Need(oldFile.Length == newFile.Length && oldMask == newMask, "Complete state extent/metadata layout differs.");
                byte[] first = new byte[32768], second = new byte[32768]; long offset = 0;
                var masks=new[]{(start:oldMask,width:64),(start:oldFile.Length-32,width:32)};
                while (offset < oldFile.Length)
                {
                    int count = (int)Math.Min(first.Length, oldFile.Length - offset);
                    oldFile.ReadExactly(first.AsSpan(0, count)); newFile.ReadExactly(second.AsSpan(0, count));
                    foreach(var mask in masks)
                    {
                        long start=Math.Max(0,mask.start-offset),end=Math.Min(count,mask.start+mask.width-offset);
                        if(end>start){Array.Clear(first,(int)start,(int)(end-start));Array.Clear(second,(int)start,(int)(end-start));}
                    }
                    if(!first.AsSpan(0,count).SequenceEqual(second.AsSpan(0,count)))
                    {int i=0;while(first[i]==second[i])i++;throw new InvalidDataException("Complete physical state differs at byte "+(offset+i)+" in "+name);}
                    offset += count;
                }
                comparisons.Add(new { file = name, oldHash, newHash, bytes = offset, completePhysicalStateExact = true });
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        if (owned) WriteNew(Path.Combine(output, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { schema = "continuum-columnar-comparison/v1", status = failure.Length == 0 ? "passed" : "failed", failure,
            baseline, candidate, comparisons, exclusions = new[] { "exact admitted builtin image SHA-256", "verified complete-envelope digest derived from that image field" },
            scope = "Streaming exact comparison of every other serialized byte. Passed original receipts bind actual recipes, controls and raw image digests; each envelope checksum is independently verified. No owner restore under a foreign code image, precision tolerance, physical field exclusion or throughput claim." }, Json));
        Console.WriteLine((failure.Length == 0 ? "passed" : "failed") + ": " + output); if (failure.Length != 0) Console.Error.WriteLine(failure); return failure.Length == 0 ? 0 : 2;
    }
    static long ColumnarImageField(string path, string image)
    {
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file, new UTF8Encoding(false, true), true);
        Need(Encoding.ASCII.GetString(reader.ReadBytes(8)) == "ORSCOL01" && reader.ReadInt32() == 1, "Columnar envelope differs.");
        file.Position = 88; int metadata = reader.ReadInt32(); Need(metadata >= 0 && metadata <= 2097152 && 92L + metadata <= file.Length - 52, "Metadata extent differs.");
        string Text() { int length = reader.ReadInt32(); Need(length >= 0 && length <= 8192 && file.Position + length <= 92L + metadata, "Metadata text extent differs."); return new UTF8Encoding(false, true).GetString(reader.ReadBytes(length)); }
        Need(Text() == "generated", "Comparator admits generated capacity provenance only."); Text(); Text(); Text(); Need(reader.ReadByte() == 0, "Generated provenance contains native state.");
        Text(); file.Position += 16; Text(); file.Position += 48; Need(reader.ReadInt32() == 64, "Builtin image width differs."); long mask = file.Position;
        Need(Encoding.ASCII.GetString(reader.ReadBytes(64)) == image, "Builtin image differs from actual producer receipt.");
        file.Position = file.Length - 52; Need(reader.ReadInt32() == -1 && reader.ReadInt64() == file.Length - 32, "Envelope footer extent differs."); reader.ReadInt64(); byte[] expected = reader.ReadBytes(32);
        file.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[32768]; long remaining = file.Length - 32;
        while (remaining > 0) { int count = (int)Math.Min(buffer.Length, remaining); file.ReadExactly(buffer.AsSpan(0, count)); hash.AppendData(buffer, 0, count); remaining -= count; }
        Need(hash.GetHashAndReset().AsSpan().SequenceEqual(expected), "Complete envelope checksum differs."); return mask;
    }
}
