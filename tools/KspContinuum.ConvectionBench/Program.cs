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
            if (args.Length == 3 && args[0] == "--census") return Census(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--verify-reset") return VerifyReset(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--verify-query") return VerifyReset(args[1], args[2], true);
            if (args.Length == 3 && args[0] == "--attribute-reset") return AttributeReset(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--observe-query-matched") return ObserveQueryMatched(args[1], args[2]);
            if (args.Length == 1 && args[0] == "--falsify-reset") { Console.WriteLine(JsonSerializer.Serialize(Falsifiers.RunReset(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args.Length == 1 && args[0] == "--falsify-query") { Console.WriteLine(JsonSerializer.Serialize(Falsifiers.RunQuery(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
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
            if (args.Length != 2 || args[0] != "--verify-stock") throw new ArgumentException("usage: --admit | --falsify | --falsify-reset | --verify-stock FRAME_FILE | --verify-reset FRAME_FILE REPORT_FILE | --census FRAME_FILE REPORT_FILE | --observe FRAME_FILE stock|index|stock-delegate|tan|tan-two-slot|reset-stock|reset REPORT_FILE");
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
    static object ResetCounters(ReplayResult result) { return new { result.resetRectangleCalls, result.resetGeometryCalls, result.resetAnchorProbes, result.resetAnchors, result.resetBoundSteps, result.resetRows, result.resetNoResetRows, result.resetSkippedScalarPairs, result.resetAttemptedScalarPairs, result.areaIndexNodeVisits, result.areaUnitCones, result.areaElidedPairs }; }
    static int ObserveQueryMatched(string path, string reportPath)
    {
        if (File.Exists(reportPath)) throw new IOException("Matched query receipt already exists.");
        var preparation = Stopwatch.StartNew(); Stock.Admit(); ConvectionRecording recording = ConvectionFormat.Read(path);
        if (recording.assemblySha256 != Stock.Hash || recording.assemblyMvid != Stock.Mvid.ToString("D")) throw new InvalidOperationException("Captured source pin differs.");
        var pairs = new List<object>(); double[] globals = Stock.Globals();
        try
        {
            using (var kernel = new ResetKernel())
            {
                preparation.Stop();
                for (int observation = 0; observation < 2; observation++)
                {
                    Treatment[] order = observation == 0 ? new[] { Treatment.ResetStock, Treatment.QueryElision } : new[] { Treatment.QueryElision, Treatment.ResetStock };
                    var measurements = new List<object>();
                    foreach (Treatment treatment in order)
                    {
                        var results = new ReplayResult[recording.frames.Length]; var timer = Stopwatch.StartNew();
                        for (int frame = 0; frame < results.Length; frame++) results[frame] = Replay.Evaluate(recording.frames[frame], treatment);
                        timer.Stop(); var frames = new List<object>();
                        for (int frame = 0; frame < results.Length; frame++)
                        {
                            ReplayResult result = results[frame]; string difference = Replay.Compare(result, recording.frames[frame]);
                            if (difference != null) throw new InvalidOperationException(difference);
                            frames.Add(new { recording.frames[frame].sequence, recording.frames[frame].universalTime, recording.frames[frame].mach,
                                parts = recording.frames[frame].rows.Length, acceptedCones = result.cones.Length, result.scalarPairs, result.possiblePairs, result.candidatePairs,
                                counters = ResetCounters(result), result.fallbackReason, bitwiseRecordedMatch = true });
                        }
                        measurements.Add(new { treatment = treatment == Treatment.ResetStock ? "stock" : "query", elapsedTicks = timer.ElapsedTicks, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, frames });
                    }
                    pairs.Add(new { observation, measurements });
                }
            }
        }
        finally { Stock.Globals(globals); }
        var receipt = new { schema = "continuum-convection-query-matched/v1", status = "two-alternating-matched-pairs-recorded-bitwise-match", recording.sessionId, recording.vesselId,
            inputSha256 = HashFile(path), assemblySha256 = Stock.Hash, assemblyMvid = Stock.Mvid, originalIlSha256 = GeometryCensus.IlHash,
            benchSha256 = HashFile(typeof(Program).Assembly.Location), coreSha256 = HashFile(typeof(ConvectionFrame).Assembly.Location),
            framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), clockFrequency = Stopwatch.Frequency,
            preparationMilliseconds = preparation.Elapsed.TotalMilliseconds, pairs, originalPatchGraphUntouched = true,
            scope = "Exactly two observations per treatment, one process/same admitted unchanged original scalar delegate and binary. First stock then query, second query then stock. Complete four-frame folds include fresh inputs/globals, index construction/insertion/query, finite certificate/reset search, original scalar/Setup and output materialization; comparison and encoding outside timer. Cold JIT charged on each treatment's first execution; no workload warmup, live projection/publication or native effects." };
        string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(output)) writer.WriteLine(json);
        Console.WriteLine(json); return 0;
    }
    static int AttributeReset(string path, string reportPath)
    {
        if (File.Exists(reportPath)) throw new IOException("Attribution receipt already exists.");
        ConvectionRecording recording = ConvectionFormat.Read(path); Stock.Admit();
        if (recording.assemblySha256 != Stock.Hash || recording.assemblyMvid != Stock.Mvid.ToString("D")) throw new InvalidOperationException("Captured source pin differs.");
        var frames = new List<object>(); double[] globals = Stock.Globals(); var totals = new long[ResetKernel.PhaseProfile.Names.Length];
        try
        {
            using (var kernel = new ResetKernel()) foreach (ConvectionFrame frame in recording.frames)
            {
                ReplayResult result = ResetKernel.Attribute(frame, out var profile); string difference = Replay.Compare(result, frame);
                if (difference != null) throw new InvalidOperationException(difference);
                var phases = new List<object>();
                for (int i = 0; i < totals.Length; i++) { totals[i] += profile.ticks[i]; phases.Add(new { name = ResetKernel.PhaseProfile.Names[i], elapsedTicks = profile.ticks[i] }); }
                frames.Add(new { frame.sequence, frame.universalTime, frame.mach, parts = frame.rows.Length, phases, counters = ResetCounters(result), bitwiseRecordedMatch = true });
            }
        }
        finally { Stock.Globals(globals); }
        var totalPhases = new List<object>();
        for (int i = 0; i < totals.Length; i++) totalPhases.Add(new { name = ResetKernel.PhaseProfile.Names[i], elapsedTicks = totals[i], elapsedMilliseconds = totals[i] * 1000.0 / Stopwatch.Frequency });
        var receipt = new { schema = "continuum-convection-reset-attribution/v1", status = "single-attributed-fold-recorded-bitwise-match", recording.sessionId, recording.vesselId,
            inputSha256 = HashFile(path), assemblySha256 = Stock.Hash, assemblyMvid = Stock.Mvid, originalIlSha256 = GeometryCensus.IlHash,
            benchSha256 = HashFile(typeof(Program).Assembly.Location), coreSha256 = HashFile(typeof(ConvectionFrame).Assembly.Location),
            framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), clockFrequency = Stopwatch.Frequency,
            totalPhases, frames, scope = "One instrumented complete sequence, exclusive row-phase timestamp boundaries. Includes cold JIT and intrusive timestamp/null-marker overhead; observer tax unqualified. Not a treatment performance observation. No pair timers, no projection/publication/native effects. All final/cone bits gated against captured originals." };
        string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(output)) writer.WriteLine(json);
        Console.WriteLine(json); return 0;
    }
    static int VerifyReset(string path, string reportPath, bool query = false)
    {
        if (File.Exists(reportPath)) throw new IOException("Reset proof receipt already exists.");
        ConvectionRecording recording = ConvectionFormat.Read(path); Stock.Admit();
        if (recording.assemblySha256 != Stock.Hash || recording.assemblyMvid != Stock.Mvid.ToString("D")) throw new InvalidOperationException("Captured source pin differs.");
        double[] globals = Stock.Globals(); var frames = new List<object>(); bool cleanup = false;
        try
        {
            using (var kernel = new ResetKernel())
            foreach (ConvectionFrame frame in recording.frames)
            {
                ReplayResult result = Replay.Evaluate(frame, query ? Treatment.QueryElision : Treatment.ResetCandidate); string difference = Replay.Compare(result, frame);
                if (difference != null) throw new InvalidOperationException(difference);
                frames.Add(new { frame.sequence, frame.universalTime, frame.mach, parts = frame.rows.Length, acceptedCones = result.cones.Length,
                    result.scalarPairs, result.candidatePairs, result.fallbackReason, counters = ResetCounters(result), bitwiseRecordedMatch = true });
            }
            cleanup = true;
        }
        finally { Stock.Globals(globals); }
        var receipt = new { schema = "continuum-convection-reset-proof/v1", status = "four-state-recorded-bitwise-match", treatment = query ? "query-elision" : "reset", recording.sessionId, recording.vesselId,
            inputSha256 = HashFile(path), assemblySha256 = Stock.Hash, assemblyMvid = Stock.Mvid, originalIlSha256 = GeometryCensus.IlHash,
            benchSha256 = HashFile(typeof(Program).Assembly.Location), coreSha256 = HashFile(typeof(ConvectionFrame).Assembly.Location),
            framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), clockFrequency = Stopwatch.Frequency,
            supportedDomain = ResetKernel.Domain, frames, originalPatchGraphUntouched = cleanup,
            scope = "Fresh original inputs; " + (query ? "unit-area cones and conservative disjoint rectangles elided; rectangle-only receiver tree constructed/insertion charged; " : "") + "original rectangle/area extraction in forward order determines reached cutoff. Exact original geometry extraction finds last full shock and finite-prefix anchors. Original reset/tail ref arithmetic, Setup and ordered cone publication. Full original frame fallback on unsupported domain; recorded outputs are comparison only. No timings, live projection/publication or native effects." };
        string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(output)) writer.WriteLine(json);
        Console.WriteLine(json); return 0;
    }
    static int Census(string path, string reportPath)
    {
        if (File.Exists(reportPath)) throw new IOException("Census receipt already exists.");
        ConvectionRecording recording = ConvectionFormat.Read(path); Stock.Admit();
        if (recording.assemblySha256 != Stock.Hash || recording.assemblyMvid != Stock.Mvid.ToString("D")) throw new InvalidOperationException("Captured source pin differs.");
        var census = GeometryCensus.Run(recording);
        var receipt = new { schema = "continuum-convection-geometry-census/v1", status = "single-fold-recorded-bitwise-match", recording.sessionId, recording.vesselId,
            inputSha256 = HashFile(path), assemblySha256 = Stock.Hash, assemblyMvid = Stock.Mvid, benchSha256 = HashFile(typeof(Program).Assembly.Location),
            coreSha256 = HashFile(typeof(ConvectionFrame).Assembly.Location), framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), clockFrequency = Stopwatch.Frequency, census,
            scope = "One complete recorded sequence. Read-only hooks in a detached pinned original GetShockStats IL delegate observe actual circle return and finalized original geometry locals before ref multiplier updates. Original scalar arithmetic, Setup, order, area cutoff and bitwise final/cone oracle retained. Intrusive instrumented census has no performance timings or live equivalence claim; no original projection/FX/publication or process detours." };
        string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(output)) writer.WriteLine(json);
        Console.WriteLine(JsonSerializer.Serialize(new { receipt.schema, receipt.status, reportPath, receipt.inputSha256, receipt.benchSha256 })); return 0;
    }
    static int Observe(string path, string variant, string reportPath)
    {
        Treatment treatment = variant == "stock" ? Treatment.Stock : variant == "index" ? Treatment.CandidateIndex : variant == "tan" ? Treatment.CachedTan : variant == "tan-two-slot" ? Treatment.CachedTanTwoSlot : variant == "stock-delegate" ? Treatment.StockDelegate : variant == "reset-stock" ? Treatment.ResetStock : variant == "reset" ? Treatment.ResetCandidate : variant == "query" ? Treatment.QueryElision : throw new ArgumentException("Unknown treatment.");
        if (File.Exists(reportPath)) throw new IOException("Receipt already exists.");
        var preparation = Stopwatch.StartNew();
        Stock.Admit();
        ConvectionRecording recording = ConvectionFormat.Read(path);
        if (recording.assemblySha256 != Stock.Hash || recording.assemblyMvid != Stock.Mvid.ToString("D")) throw new InvalidOperationException("Captured source pin differs.");
        bool cached = treatment == Treatment.CachedTan || treatment == Treatment.CachedTanTwoSlot;
        TanCache patch = cached || treatment == Treatment.StockDelegate ? new TanCache(cached) : null;
        ResetKernel kernel = treatment == Treatment.ResetStock || treatment == Treatment.ResetCandidate || treatment == Treatment.QueryElision ? new ResetKernel() : null;
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
                        result.fallbackReason, reset = ResetCounters(result), bitwiseRecordedMatch = true });
                }
                if (cached && (requests == 0 || hits == 0)) throw new InvalidOperationException("Tangent treatment has no cache coverage.");
                observations.Add(new { observation, elapsedTicks = timer.ElapsedTicks, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, frames });
            }
        }
        finally { if (patch != null) patch.Dispose(); if (kernel != null) kernel.Dispose(); cleanup = true; }
        var receipt = new { schema = "continuum-convection-replay/v1", status = "two-observations-recorded-bitwise-match", variant,
            treatmentShape = treatment == Treatment.QueryElision ? "reset fold with exact unit-area identity elision and conservative rectangle-only receiver tree; original area queries preserve reached cutoff; original reset/certificate geometry and scalar tail retained; full original frame fallback" : treatment == Treatment.ResetCandidate ? "forward original rectangle/area extraction, reverse last full shock, bounded finite-prefix certificate, original reset and serial tail; full original frame fallback" : treatment == Treatment.ResetStock ? "same kernel-owned unchanged detached original GetShockStats delegate as reset treatment; original complete serial fold" : cached ? "detached delegate of pinned original GetShockStats IL, its single GetShockRadius call replaced by exact ordered CachedRadius; " + (treatment == Treatment.CachedTanTwoSlot ? "two fixed angle-bit/tangent slots, additional angles use original Math.Tan" : "bit-key dictionary") + " cleared per frame; no process detour" : treatment == Treatment.StockDelegate ? "detached delegate of unchanged pinned original GetShockStats IL; no process detour" : treatment == Treatment.CandidateIndex ? "fresh conservative receiver index, original ordered GetShockStats and Setup, full stock fallback" : "original ordered GetShockStats and Setup",
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
