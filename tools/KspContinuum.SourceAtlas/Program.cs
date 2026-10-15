using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace KspContinuum.SourceAtlas;

public sealed record Input(string RelativePath, string FullPath, string Sha256, long Bytes, bool Managed, string? Error);
public sealed record SourceRow(string Sha256, string[] Inputs, string Status, string? Project,
    string? SourceTreeSha256, int CSharpFiles, int ProjectFiles, int? ExitCode, bool TimedOut,
    bool SourceUnchanged, double WallSeconds, string? Log, string? Error, string ReferencePolicy = "unrecorded");
public sealed record ReusableSource(string Project, string ReferencePolicy);

public static class Atlas
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    static string TreeHash(string root)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            digest.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path) + "\0" + Hash(path) + "\n"));
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    public static void NewOutput(string instance, string output)
    {
        var input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        if (target == input || target.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("output must be outside the installation");
        if (File.Exists(target) || Directory.Exists(target)) throw new ArgumentException("output already exists");
        NoLinkAncestors(target);
        NoLinkAncestors(input);
    }
    static void NoLinkAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("reparse-point ancestor is not admitted: " + current.FullName);
    }
    static IEnumerable<string> BinaryFiles(string instance)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        return Directory.EnumerateFiles(instance, "*", options)
            .Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);
    }
    static Input Inspect(string instance, string path)
    {
        var hash = Hash(path);
        try
        {
            using var file = File.OpenRead(path); using var pe = new PEReader(file);
            return new(Path.GetRelativePath(instance, path), path, hash, file.Length, pe.HasMetadata, null);
        }
        catch (BadImageFormatException error)
        {
            return new(Path.GetRelativePath(instance, path), path, hash, new FileInfo(path).Length, false, error.Message);
        }
    }
    public static string? ReuseProject(string directory, string hash)
    {
        var receipt = Path.Combine(directory, "receipt.json"); var project = Path.Combine(directory, "project");
        if (!File.Exists(receipt) || !Directory.Exists(project)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(receipt)); var row = json.RootElement;
        if (!row.TryGetProperty("sourceSha256", out var source) || source.GetString() != hash
            || !row.TryGetProperty("sourceUnchanged", out var unchanged) || !unchanged.GetBoolean()
            || !row.TryGetProperty("decompilerExitCode", out var exit) || exit.GetInt32() != 0
            || !row.TryGetProperty("decompilerVersion", out var version) || !version.GetString()!.Contains("11.1.0.9782", StringComparison.Ordinal)
            || !Directory.EnumerateFiles(project, "*.csproj", SearchOption.AllDirectories).Any()
            || !Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Any()) return null;
        return project;
    }
    public static Dictionary<string, ReusableSource> ReuseAtlas(string directory, string decompilerHash)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "atlas.json")));
        var atlas = json.RootElement;
        if (atlas.GetProperty("schema").GetString() != "continuum-source-atlas/v1"
            || atlas.GetProperty("ilspySha256").GetString() != decompilerHash)
            throw new ArgumentException("reuse atlas schema or decompiler identity differs");
        var projects = new Dictionary<string, ReusableSource>(StringComparer.Ordinal);
        foreach (var row in atlas.GetProperty("results").EnumerateArray())
        {
            var status = row.GetProperty("Status").GetString();
            if (status is not ("decompiled-rebuild-unqualified" or "reused-decompiled-rebuild-unqualified")
                || !row.GetProperty("SourceUnchanged").GetBoolean()) continue;
            var project = row.GetProperty("Project").GetString();
            if (project == null || !Directory.Exists(project)
                || !Directory.EnumerateFiles(project, "*.csproj", SearchOption.AllDirectories).Any()
                || !Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Any()
                || TreeHash(project) != row.GetProperty("SourceTreeSha256").GetString()) continue;
            projects.Add(row.GetProperty("Sha256").GetString()!, new(project,
                row.TryGetProperty("ReferencePolicy", out var policy) ? policy.GetString()! : "prior-unrecorded"));
        }
        return projects;
    }
    static async Task<(int? Exit, bool Timeout, string Stdout, string Stderr)> Run(string executable,
        IEnumerable<string> arguments, int seconds)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = new Process { StartInfo = start };
        child.Start(); var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); bool timedOut = false;
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
        return (child.ExitCode, timedOut, await stdout, await stderr);
    }
    static SourceRow Row(Input[] inputs, string status, string? project, int? exit, bool timeout,
        bool unchanged, double seconds, string? log, string? error) => new(inputs[0].Sha256,
            inputs.Select(i => i.RelativePath).ToArray(), status, project,
            project != null && Directory.Exists(project) ? TreeHash(project) : null,
            project != null && Directory.Exists(project) ? Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Count() : 0,
            project != null && Directory.Exists(project) ? Directory.EnumerateFiles(project, "*.csproj", SearchOption.AllDirectories).Count() : 0,
            exit, timeout, unchanged, seconds, log, error);

    public static async Task<int> Survey(string instance, string ilspy, string output, string? reuse, string? reuseAtlas,
        int concurrency, int timeout)
    {
        var totalClock = Stopwatch.StartNew();
        instance = Path.GetFullPath(instance); ilspy = Path.GetFullPath(ilspy); output = Path.GetFullPath(output);
        if (!Directory.Exists(instance)) throw new ArgumentException("installation is absent");
        if (concurrency is < 1 or > 8 || timeout is < 1 or > 3600) throw new ArgumentException("jobs must be 1..8 and timeout 1..3600 seconds");
        NewOutput(instance, output);
        var version = await Run(ilspy, new[] { "--version" }, 10);
        if (version.Exit != 0 || version.Timeout || !version.Stdout.Contains("ilspycmd: 11.1.0.9782", StringComparison.Ordinal))
            throw new ArgumentException("requires pinned ilspycmd 11.1.0.9782");
        var decompilerHash = Hash(ilspy);
        var reusable = reuseAtlas == null ? new Dictionary<string, ReusableSource>(StringComparer.Ordinal) : ReuseAtlas(reuseAtlas, decompilerHash);
        var inputs = BinaryFiles(instance).Select(p => Inspect(instance, p)).ToArray();
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "inputs.json"), JsonSerializer.Serialize(inputs, Json));
        var groups = inputs.Where(i => i.Managed).GroupBy(i => i.Sha256, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        var references = inputs.Where(i => i.Managed).Select(i => Path.GetDirectoryName(i.FullPath)!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var results = new SourceRow[groups.Length]; var started = Stopwatch.StartNew();
        double preparationSeconds = totalClock.Elapsed.TotalSeconds;
        await Parallel.ForEachAsync(Enumerable.Range(0, groups.Length), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (n, _) =>
        {
            var group = groups[n]; var source = group[0]; var folder = Path.Combine(output, source.Sha256);
            Directory.CreateDirectory(folder); var clock = Stopwatch.StartNew(); SourceRow result;
            try
            {
                var reusedRow = reusable.GetValueOrDefault(source.Sha256);
                string? reused = reusedRow?.Project ?? (reuse == null ? null : ReuseProject(reuse, source.Sha256));
                if (reused != null)
                {
                    result = Row(group, "reused-decompiled-rebuild-unqualified", reused, 0, false,
                        group.All(i => Hash(i.FullPath) == i.Sha256), clock.Elapsed.TotalSeconds, null, null)
                        with { ReferencePolicy = reusedRow?.ReferencePolicy ?? "legacy-index-unrecorded-tree-provenance" };
                }
                else
                {
                    var project = Path.Combine(folder, "project");
                    var args = new List<string> { "--disable-updatecheck", "-p" };
                    bool launcher = source.RelativePath.StartsWith("KSPLauncher.app/", StringComparison.Ordinal);
                    foreach (var reference in references.Where(r => Path.GetRelativePath(instance, r).StartsWith("KSPLauncher.app/", StringComparison.Ordinal) == launcher))
                    { args.Add("-r"); args.Add(reference); }
                    args.AddRange(new[] { "-o", project, source.FullPath });
                    var process = await Run(ilspy, args, timeout); var log = Path.Combine(folder, "decompiler.log");
                    await File.WriteAllTextAsync(log, process.Stdout + process.Stderr);
                    bool unchanged = group.All(i => Hash(i.FullPath) == i.Sha256);
                    result = Row(group, process.Exit == 0 && !process.Timeout && unchanged ? "decompiled-rebuild-unqualified" : "failed",
                        project, process.Exit, process.Timeout, unchanged, clock.Elapsed.TotalSeconds, log, null)
                        with { ReferencePolicy = launcher ? "launcher-closure-first-input/v1" : "game-and-plugins-closure-first-input/v1" };
                }
                if (!result.SourceUnchanged) result = result with { Status = "failed", Error = "source changed while decompiling" };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                result = Row(group, "failed", null, null, false, false, clock.Elapsed.TotalSeconds, null, error.Message);
            }
            results[n] = result;
            await File.WriteAllTextAsync(Path.Combine(folder, "receipt.json"), JsonSerializer.Serialize(result, Json));
            Console.WriteLine($"{n + 1}/{groups.Length} {Path.GetFileName(source.FullPath)} {result.Status} {result.CSharpFiles} source files");
        });
        double processingSeconds = started.Elapsed.TotalSeconds;
        var verification = Stopwatch.StartNew(); bool allSourcesUnchanged = inputs.All(i => Hash(i.FullPath) == i.Sha256);
        var receipt = new { schema = "continuum-source-atlas/v1", instance, ilspyVersion = version.Stdout.Trim(), ilspySha256 = decompilerHash,
            toolAssemblySha256 = Hash(typeof(Program).Assembly.Location), concurrency, timeoutSeconds = timeout,
            wallSeconds = processingSeconds, preparationSeconds, finalInputVerificationSeconds = verification.Elapsed.TotalSeconds,
            totalBeforeManifestSeconds = totalClock.Elapsed.TotalSeconds,
            discoveredBinaries = inputs.Length, managedInputs = inputs.Count(i => i.Managed), uniqueManagedPayloads = groups.Length,
            decompiled = results.Count(r => r.Status == "decompiled-rebuild-unqualified"), reused = results.Count(r => r.Status.StartsWith("reused", StringComparison.Ordinal)),
            failed = results.Count(r => r.Status == "failed"), sourceFiles = results.Sum(r => r.CSharpFiles),
            allSourcesUnchanged, results,
            qualification = "searchable-source-not-rebuilt-or-runtime-qualified",
            limits = new[] { "Native engine and platform code are not decompiled by this tool.", "Reflection, virtual dispatch and runtime patch behavior require dynamic evidence.",
                "Successful decompilation can retain unresolved or compiler-invalid C#; logs and projects are preserved.",
                "Source tree hashes bind observed generated files, not semantic equivalence to IL.", "Symlinks are excluded; dependency closure must be checked separately." } };
        await File.WriteAllTextAsync(Path.Combine(output, "atlas.json"), JsonSerializer.Serialize(receipt, Json));
        Console.WriteLine($"Atlas: {receipt.uniqueManagedPayloads} unique managed payloads, {receipt.sourceFiles} C# files, {receipt.failed} failed.");
        return receipt.failed == 0 && receipt.allSourcesUnchanged ? 0 : 2;
    }
    public static int SelfTest()
    {
        string temp = Path.Combine(Path.GetTempPath(), "continuum-source-atlas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "project"));
        try
        {
            File.WriteAllText(Path.Combine(temp, "project", "fake.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(temp, "project", "fake.cs"), "class Fake {}");
            File.WriteAllText(Path.Combine(temp, "receipt.json"), "{\"sourceSha256\":\"one\",\"sourceUnchanged\":true,\"decompilerExitCode\":0,\"decompilerVersion\":\"11.1.0.9782\"}");
            if (ReuseProject(temp, "two") != null || ReuseProject(temp, "one") == null) throw new InvalidOperationException("reuse failed hash binding");
            File.Delete(Path.Combine(temp, "project", "fake.csproj"));
            if (ReuseProject(temp, "one") != null) throw new InvalidOperationException("incomplete source project accepted");
            File.WriteAllText(Path.Combine(temp, "project", "fake.csproj"), "<Project />");
            string project = Path.Combine(temp, "project");
            var reusable = new { schema = "continuum-source-atlas/v1", ilspySha256 = "tool", results = new[] {
                new SourceRow("one", new[] { "one.dll" }, "decompiled-rebuild-unqualified", project, TreeHash(project), 1, 1, 0, false, true, 0, null, null) } };
            File.WriteAllText(Path.Combine(temp, "atlas.json"), JsonSerializer.Serialize(reusable, Json));
            if (ReuseAtlas(temp, "tool").Count != 1) throw new InvalidOperationException("complete atlas reuse rejected");
            File.WriteAllText(Path.Combine(project, "fake.cs"), "class Changed {}");
            if (ReuseAtlas(temp, "tool").Count != 0) throw new InvalidOperationException("changed source tree accepted");
            try { NewOutput(temp, Path.Combine(temp, "inside")); throw new InvalidOperationException("installation mutation accepted"); }
            catch (ArgumentException) { }
            try { NewOutput(Path.Combine(temp, "other"), temp); throw new InvalidOperationException("existing evidence overwrite accepted"); }
            catch (ArgumentException) { }
            string alias = Path.Combine(temp, "alias"); Directory.CreateSymbolicLink(alias, Path.Combine(temp, "project"));
            try { NewOutput(Path.Combine(temp, "input"), Path.Combine(alias, "new")); throw new InvalidOperationException("symlink output boundary accepted"); }
            catch (ArgumentException error) when (error.Message.Contains(alias, StringComparison.Ordinal)) { }
            Console.WriteLine("SourceAtlas falsifiers passed: binary hash drift, incomplete reuse, changed source tree, installation output, existing evidence, symlink output boundary."); return 0;
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["self-test"]) return Atlas.SelfTest();
            if (args.Length % 2 != 0) throw new ArgumentException("options need values");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
                if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("duplicate option " + args[i]);
            foreach (var key in options.Keys)
                if (key is not ("--instance" or "--ilspy" or "--output" or "--reuse-index" or "--reuse-atlas" or "--jobs" or "--timeout-seconds"))
                    throw new ArgumentException("unknown option " + key);
            string Get(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("missing " + key);
            return await Atlas.Survey(Get("--instance"), Get("--ilspy"), Get("--output"), options.GetValueOrDefault("--reuse-index"), options.GetValueOrDefault("--reuse-atlas"),
                int.Parse(options.GetValueOrDefault("--jobs", "3")), int.Parse(options.GetValueOrDefault("--timeout-seconds", "900")));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or FormatException or OverflowException or JsonException or KeyNotFoundException)
        { Console.Error.WriteLine("source-atlas: " + error.Message); return 1; }
    }
}
