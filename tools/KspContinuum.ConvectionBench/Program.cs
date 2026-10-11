using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--falsify") { Console.WriteLine(JsonSerializer.Serialize(Falsifiers.Run(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args.Length == 4 && args[0] == "--observe") return Observe(args[1], args[2], args[3]);
            if (args.Length == 1 && args[0] == "--admit")
            {
                Stock.TinyCall();
                Console.WriteLine(JsonSerializer.Serialize(new { schema = "continuum-convection-replay/v1", status = "original-scalar-admitted",
                    assemblySha256 = Stock.Hash, assemblyMvid = Stock.Mvid, framework = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), clockFrequency = Stopwatch.Frequency }));
                return 0;
            }
            if (args.Length != 2 || args[0] != "--verify-stock") throw new ArgumentException("usage: --admit | --falsify | --verify-stock FRAME_FILE | --observe FRAME_FILE stock|index|stock-delegate|tan|tan-two-slot REPORT_FILE");
            Stock.TinyCall();
            ConvectionRecording recording = ConvectionFormat.Read(args[1]);
            if (recording.assemblySha256 != Stock.Hash || recording.assemblyMvid != Stock.Mvid.ToString("D")) throw new InvalidOperationException("Captured source pin differs.");
            var frames = new List<object>();
            foreach (var frame in recording.frames)
            {
                ReplayResult result = Replay.Evaluate(frame, Treatment.Stock);
                string difference = Replay.Compare(result, frame);
                frames.Add(new { frame.sequence, parts = frame.rows.Length, acceptedCones = result.cones.Length, result.scalarPairs, bitwiseRecordedMatch = difference == null, difference });
                if (difference != null) throw new InvalidOperationException(difference);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "continuum-convection-replay/v1", status = "stock-recorded-bitwise-match",
                recording.sessionId, recording.vesselId, assemblySha256 = Stock.Hash, assemblyMvid = Stock.Mvid,
                framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), clockFrequency = Stopwatch.Frequency,
                frames, scope = "Original stock scalar Setup and ordered GetShockStats fold; current captured projection/order, no original projection or FX camera. No tolerance relaxation." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static string HashFile(string path) { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(); }
    static int Observe(string path, string variant, string reportPath)
    {
        Treatment treatment = variant == "stock" ? Treatment.Stock : variant == "index" ? Treatment.CandidateIndex : variant == "tan" ? Treatment.CachedTan : variant == "tan-two-slot" ? Treatment.CachedTanTwoSlot : variant == "stock-delegate" ? Treatment.StockDelegate : throw new ArgumentException("Unknown treatment.");
        if (File.Exists(reportPath)) throw new IOException("Receipt already exists.");
        var preparation = Stopwatch.StartNew();
        Stock.Admit();
        ConvectionRecording recording = ConvectionFormat.Read(path);
        if (recording.assemblySha256 != Stock.Hash || recording.assemblyMvid != Stock.Mvid.ToString("D")) throw new InvalidOperationException("Captured source pin differs.");
        bool cached = treatment == Treatment.CachedTan || treatment == Treatment.CachedTanTwoSlot;
        TanCache patch = cached || treatment == Treatment.StockDelegate ? new TanCache(cached) : null;
        preparation.Stop();
        var observations = new List<object>();
        bool cleanup = false;
        try
        {
            for (int observation = 0; observation < 2; observation++)
            {
                var results = new ReplayResult[recording.frames.Length];
                var timer = Stopwatch.StartNew();
                for (int frame = 0; frame < results.Length; frame++) results[frame] = Replay.Evaluate(recording.frames[frame], treatment);
                timer.Stop();
                var frames = new List<object>();
                long requests = 0, hits = 0;
                for (int frame = 0; frame < results.Length; frame++)
                {
                    var result = results[frame];
                    string difference = Replay.Compare(result, recording.frames[frame]);
                    if (difference != null) throw new InvalidOperationException(difference);
                    requests += result.tanHits + result.tanMisses; hits += result.tanHits;
                    frames.Add(new { recording.frames[frame].sequence, parts = recording.frames[frame].rows.Length, acceptedCones = result.cones.Length,
                        result.scalarPairs, result.possiblePairs, result.candidatePairs, result.indexNodeVisits, result.tanHits, result.tanMisses, result.tanUncached,
                        result.fallbackReason, bitwiseRecordedMatch = true });
                }
                if (cached && (requests == 0 || hits == 0)) throw new InvalidOperationException("Tangent treatment has no cache coverage.");
                observations.Add(new { observation, elapsedTicks = timer.ElapsedTicks, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, frames });
            }
        }
        finally { if (patch != null) patch.Dispose(); cleanup = true; }
        var receipt = new { schema = "continuum-convection-replay/v1", status = "two-observations-recorded-bitwise-match", variant,
            treatmentShape = cached ? "detached delegate of pinned original GetShockStats IL, its single GetShockRadius call replaced by exact ordered CachedRadius; " + (treatment == Treatment.CachedTanTwoSlot ? "two fixed angle-bit/tangent slots, additional angles use original Math.Tan" : "bit-key dictionary") + " cleared per frame; no process detour" : treatment == Treatment.StockDelegate ? "detached delegate of unchanged pinned original GetShockStats IL; no process detour" : treatment == Treatment.CandidateIndex ? "fresh conservative receiver index, original ordered GetShockStats and Setup, full stock fallback" : "original ordered GetShockStats and Setup",
            recording.sessionId, recording.vesselId, inputSha256 = HashFile(path), assemblySha256 = Stock.Hash, assemblyMvid = Stock.Mvid,
            benchSha256 = HashFile(typeof(Program).Assembly.Location), coreSha256 = HashFile(typeof(ConvectionFrame).Assembly.Location),
            framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), clockFrequency = Stopwatch.Frequency,
            preparationMilliseconds = preparation.Elapsed.TotalMilliseconds, observations, ownedPatchRemoved = cleanup,
            scope = "Whole four-frame fold includes fresh inputs, globals reset, index/cache/query, original scalar calls/Setup and output materialization; excludes captured projection, game publication, FX camera and result comparison. First observation includes cold JIT; no full-workload warmup. Captured outputs were separately reproduced by original stock replay bitwise; this identical oracle also gates alternatives." };
        string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(output)) writer.WriteLine(json);
        Console.WriteLine(json); return 0;
    }
}
