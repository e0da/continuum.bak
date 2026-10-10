using System.Reflection;
using KspContinuum.Reconstruction;

StartupCacheManifestReaderTests.Run();

var directory = Path.Combine(Path.GetTempPath(), "continuum-reconstruction-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var observation = new KspContinuum.ManagedReconstructionObservation { gameVersion = "version \"research\"", assemblySha256 = new string('a', 64) };
    using (var encoded = System.Text.Json.JsonDocument.Parse(KspContinuum.ReportJson.Encode(observation)))
        Require(encoded.RootElement.GetProperty("gameVersion").GetString() == observation.gameVersion, "runtime observation serialization failed");
    var source = Assembly.GetExecutingAssembly().Location;
    var hash = ResearchAssembly.Hash(source);
    var unchanged = Path.Combine(directory, "unchanged.dll");
    ResearchAssembly.Rewrite(source, hash, unchanged);
    Require(Invoke(unchanged, true) == "branch-a" && Invoke(unchanged, false) == "branch-b", "unchanged rewrite broke branch behavior");
    Reject(() => ResearchAssembly.Rewrite(source, new string('0', 64), Path.Combine(directory, "bad.dll")));
    Require(!File.Exists(Path.Combine(directory, "bad.dll")), "hash rejection wrote an output");
    Reject(() => ResearchAssembly.Rewrite(source, hash, unchanged));
    var probe = Path.Combine(directory, "probe.dll");
    ResearchAssembly.Rewrite(source, hash, probe, "Fixture.Version", "-research");
    Require(Invoke(probe, true) == "branch-a-research" && Invoke(probe, false) == "branch-b-research", "probe skipped a return branch");
    Require(ResearchAssembly.Hash(source) == hash, "source was mutated");
    Reject(() => ResearchAssembly.Rewrite(source, hash, Path.Combine(directory, "missing.dll"), "Fixture.Missing", "-research"));
    Reject(() => ResearchAssembly.Rewrite(source, hash, Path.Combine(directory, "handlers.dll"), "Fixture.WithHandler", "-research"));
    var signed = Path.Combine(directory, "signed.dll");
    using (var signedAssembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(source))
    {
        signedAssembly.Name.PublicKey = new byte[] { 1, 2, 3, 4 };
        signedAssembly.Write(signed);
    }
    Reject(() => ResearchAssembly.Rewrite(signed, ResearchAssembly.Hash(signed), Path.Combine(directory, "unsigned.dll")));
    Console.WriteLine("reconstruction: unchanged branches, changed branches, source hash, exclusive output, missing method passed");
}
finally { Directory.Delete(directory, true); }
static string Invoke(string path, bool branch)
{
    var assembly = Assembly.Load(File.ReadAllBytes(path));
    var type = assembly.GetType("Fixture")!;
    type.GetField("Branch")!.SetValue(null, branch);
    return (string)type.GetMethod("Version")!.Invoke(null, null)!;
}
static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("invalid rewrite accepted"); }
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
public static class Fixture
{
    public static bool Branch;
    public static string Version() { if (Branch) return "branch-a"; return "branch-b"; }
    public static string WithHandler() { try { return Version(); } catch (Exception) { return "caught"; } }
}
