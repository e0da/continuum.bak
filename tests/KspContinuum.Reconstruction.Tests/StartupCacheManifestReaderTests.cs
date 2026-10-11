using System.Text.Json;
using KspContinuum;

public static class StartupCacheManifestReaderTests
{
    public static void Run()
    {
        const string valid = "{\"nested\":{\"bundles\":[null]},\"bundles\":[{\"name\":\"brace } slash \\\" and \\\\ end\"},{\"n\":-12.5e+3},{\"array\":[true,false,null]}]}";
        Check(valid);
        const string menu = "{\"files\":[{\"relativeDataPath\":\"nested/asset\"}],\"requiredShaders\":[\"shader \\\" name\",\"\\u0041\"]}";
        using (var oracle = JsonDocument.Parse(menu))
        {
            if (StartupCacheManifestReader.ReadMenuFileObjects(menu)[0] != oracle.RootElement.GetProperty("files")[0].GetRawText() ||
                !StartupCacheManifestReader.ReadMenuShaderNames(menu).SequenceEqual(oracle.RootElement.GetProperty("requiredShaders").EnumerateArray().Select(value => value.GetString())))
                throw new Exception("Menu closure differs from independent parser.");
        }
        foreach (string invalid in new[] { "{\"files\":[null]}", "{\"files\":[]}", "{\"files\":[{}],\"files\":[{}]}" })
        {
            try { StartupCacheManifestReader.ReadMenuFileObjects(invalid); }
            catch (FormatException) { continue; }
            throw new Exception("Invalid menu file list accepted.");
        }
        try { StartupCacheManifestReader.ReadMenuShaderNames("{\"requiredShaders\":[null]}"); throw new Exception("Nonstring shader name accepted."); }
        catch (FormatException) { }
        foreach (string invalid in new[] {
            "{}", "{\"bundles\":[]}", "{\"bundles\":[{},{},null]}", "{\"bundles\":[{},{},{}],\"bundles\":[{},{},{}]}",
            "{\"bundles\":[{\"x\":1,\"x\":2},{},{}]}", "{\"bundles\":[{\"x\":1,\"\\u0078\":2},{},{}]}",
            "{\"bundles\":[{\"x\":01},{},{}]}", "{\"bundles\":[{\"x\":1.},{},{}]}",
            "{\"bundles\":[{\"x\":\"\\q\"},{},{}]}", "{\"bundles\":[{\"x\":\"\\u0z00\"},{},{}]}",
            "{\"bundles\":[{},{},{}]} trailing", "{\"bundles\":[{},{},{}", new string(' ', 65537),
            "{\"bundles\":[{\"x\":" + new string('[', 18) + "0" + new string(']', 18) + "},{},{}]}" })
        {
            try { StartupCacheManifestReader.ReadBundleObjects(invalid); }
            catch (FormatException) { continue; }
            throw new Exception("Invalid manifest accepted: " + invalid.Substring(0, Math.Min(invalid.Length, 80)));
        }
        string? actual = Environment.GetEnvironmentVariable("KSP_CACHE_MANIFEST");
        if (actual != null) Check(File.ReadAllText(actual));
        Console.WriteLine("startup-cache-reader: independent object comparison, escaped braces, root selection, duplicate fields, grammar, bounds, truncation passed" + (actual != null ? "; actual private manifest passed" : ""));
    }
    static void Check(string json)
    {
        string[] rows = StartupCacheManifestReader.ReadBundleObjects(json);
        using var oracle = JsonDocument.Parse(json);
        var expected = oracle.RootElement.GetProperty("bundles").EnumerateArray().ToArray();
        if (rows.Length != expected.Length) throw new Exception("Manifest row count differs from independent parser.");
        for (int i = 0; i < rows.Length; i++)
            if (rows[i] != expected[i].GetRawText()) throw new Exception("Manifest object slice differs from independent parser.");
    }
}
