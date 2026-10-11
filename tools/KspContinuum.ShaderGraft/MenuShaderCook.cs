using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

static class MenuShaderCook
{
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static int Run(string[] args)
    {
        string builtin = Path.GetFullPath(args[1]), package = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
        if (args.Length != 5 || !Path.IsPathRooted(args[4])) throw new InvalidDataException("Private absolute shader recovery directory required; unbound generic property recipes are not safe.");
        string recovery = Path.GetFullPath(args[4]);
        var contracts = ReadContracts(recovery);
        if (!Path.IsPathRooted(args[3]) || Directory.Exists(output) || File.Exists(output) || output.StartsWith(builtin + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("New absolute private cook directory required.");
        string shaders = Path.Combine(output, "Assets", "MenuShaders"); Directory.CreateDirectory(shaders);
        var copied = new List<object>();
        void Copy(string source, string relative)
        {
            string destination = Path.Combine(shaders, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
            copied.Add(new { source, relative, sha256 = Hash(destination), shaderBehavior = "matching-vendor-source" });
        }
        string extras = Path.Combine(builtin, "DefaultResourcesExtra");
        foreach (string relative in new[] { "StandardSpecular.shader", "Normal-Diffuse.shader", "Internal-GUITextureClip.shader", "Internal-GUITextureClipText.shader", "UI/UI-Default.shader", "TerrainShaders/Splats/FirstPass.shader", "TerrainShaders/Splats/AddPass.shader", "TerrainShaders/Splats/DiffuseBase.shader", "TerrainShaders/Splats/Standard-FirstPass.shader", "TerrainShaders/Splats/Standard-AddPass.shader", "TerrainShaders/Splats/Standard-Base.shader", "TerrainShaders/Splats/Standard-BaseGen.shader" }) Copy(Path.Combine(extras, relative), relative);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(builtin, "CGIncludes"), "*.cginc")) Copy(path, Path.GetFileName(path));
        var contents = new Dictionary<string, (string? Path, byte[]? Asset)>(); long total = 0;
        using (var zipped = File.OpenRead(package)) using (var gzip = new GZipStream(zipped, CompressionMode.Decompress)) using (var tar = new TarReader(gzip))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (entry.DataStream == null) continue;
                string[] parts = entry.Name.Split('/'); if (parts.Length < 2 || !(parts[^1] is "pathname" or "asset")) continue;
                if (entry.Length > 64 * 1024 * 1024 || (total += entry.Length) > 256 * 1024 * 1024) throw new InvalidDataException("TMP package bound exceeded.");
                using var buffer = new MemoryStream(); entry.DataStream.CopyTo(buffer); var value = contents.GetValueOrDefault(parts[0]);
                if (parts[^1] == "pathname") value.Path = System.Text.Encoding.UTF8.GetString(buffer.ToArray()).Trim(); else value.Asset = buffer.ToArray(); contents[parts[0]] = value;
            }
        }
        var tmpSources = contents.Values.Where(v => v.Path != null && v.Asset != null && (v.Path.EndsWith(".shader", StringComparison.Ordinal) || v.Path.EndsWith(".cginc", StringComparison.Ordinal))).ToArray();
        foreach (var value in tmpSources)
        {
            string name = Path.GetFileName(value.Path!); if (name.EndsWith(".shader", StringComparison.Ordinal) && name is not "TMP_SDF-Mobile.shader" and not "TMP_Sprite.shader") continue;
            string destination = Path.Combine(shaders, name); File.WriteAllBytes(destination, value.Asset!); copied.Add(new { source = package + "#" + value.Path, relative = name, sha256 = Hash(destination), shaderBehavior = "matching-tmp-package-source" });
        }
        if (!File.Exists(Path.Combine(shaders, "TMP_SDF-Mobile.shader")) || !File.Exists(Path.Combine(shaders, "TMP_Sprite.shader"))) throw new InvalidDataException("TMP exact shader source missing.");
        string[] names = { "KSP/Bumped", "KSP/Bumped Specular", "KSP/Scenery/Bumped", "KSP/Scenery/Diffuse", "KSP/Scenery/Emissive/Bumped Specular", "KSP/Scenery/Specular", "KSP/Scenery/Unlit/Transparent", "KSP/Unlit", "Sprite/Simple Texture (Unlit)", "Terrain/Scaled Planet (RimAerial) Standard", "Terrain/Scaled Planet (Simple)", "MaskedTexture" };
        var generated = new List<object>();
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index]; bool unlit = name.Contains("Unlit", StringComparison.Ordinal) || name.StartsWith("Sprite/", StringComparison.Ordinal) || name == "MaskedTexture";
            bool transparent = name.EndsWith("Transparent", StringComparison.Ordinal) || name == "MaskedTexture";
            bool standard = name.EndsWith("Standard", StringComparison.Ordinal);
            if (!contracts.TryGetValue(name, out var variants)) throw new InvalidDataException("Missing exact original property contract: " + name);
            for (int variant = 0; variant < variants.Count; variant++)
            {
            var contract = variants[variant];
            var properties = contract.Properties;
            bool Has(string property, int type) => properties.Any(p => p.Name == property && p.Kind == type);
            string Value(string property, string fallback)
            {
                Property? value = properties.SingleOrDefault(p => p.Name == property);
                if (value == null) return fallback;
                if (value.Kind == 4) throw new InvalidDataException("Numeric recipe input is a texture: " + property);
                return property;
            }
            string color = Value("_Color", "float4(1,1,1,1)"), opacity = Value("_Opacity", "1.0"), shininess = Value("_Shininess", "0.078125"), rimColor = Value("_RimColor", "float4(0,0,0,0)"), rimFalloff = Value("_RimFalloff", "1.0"), emissiveColor = Value("_EmissiveColor", "float4(0,0,0,0)"), specColor = Value("_SpecColor", "float4(.5,.5,.5,1)");
            string uniforms = string.Join("\n", properties.Where(p => p.Name != "_SpecColor").Select(p => p.Uniform())) + "\nstruct Input { float2 uv_MainTex; float3 viewDir; };";
            string source = "Shader \"" + contract.DonorName + "\" {\nProperties {\n" + string.Join("\n", properties.Select(p => p.Declaration())) + "\n}\nSubShader { Tags { \"RenderType\"=\"" + (transparent ? "Transparent" : "Opaque") + "\" \"Queue\"=\"" + (transparent ? "Transparent" : "Geometry") + "\" }\n" + (transparent ? "ZWrite Off Blend SrcAlpha OneMinusSrcAlpha\n" : "") +
                "CGPROGRAM\n#pragma target 3.0\n#pragma surface surf " + (unlit ? "MenuUnlit" : standard ? "StandardSpecular" : name.Contains("Specular", StringComparison.Ordinal) ? "BlinnPhong" : "Lambert") + (transparent ? " alpha:fade" : "") + "\n#include \"UnityCG.cginc\"\n" + (unlit ? "half4 LightingMenuUnlit(SurfaceOutput s,half3 lightDir,half atten){return half4(s.Albedo,s.Alpha);}\n" : "") + uniforms +
                "\nvoid surf(Input i,inout " + (standard ? "SurfaceOutputStandardSpecular" : "SurfaceOutput") + " o){ float4 t=" + (Has("_MainTex", 4) ? "tex2D(_MainTex,i.uv_MainTex)" : "float4(1,1,1,1)") + "; float4 color=" + color + "; o.Albedo=t.rgb*color.rgb; o.Alpha=t.a*color.a*" + opacity + ";" +
                (Has("_BumpMap", 4) ? "o.Normal=UnpackNormal(tex2D(_BumpMap,i.uv_MainTex));" : "") +
                (standard ? "o.Specular=(" + specColor + ").rgb; o.Smoothness=" + shininess + ";" : "o.Specular=" + shininess + "; o.Gloss=t.a;") +
                "float4 rimColor=" + rimColor + "; float rim=pow(1-saturate(dot(normalize(i.viewDir),o.Normal)),max(" + rimFalloff + ",.0001)); o.Emission=rimColor.rgb*rimColor.a*rim;" +
                (Has("_Emissive", 4) ? "float4 emissionColor=" + emissiveColor + "; o.Emission+=tex2D(_Emissive,i.uv_MainTex).rgb*emissionColor.rgb*emissionColor.a;" : "") + " }\nENDCG\n}\nFallback Off\n}\n";
            string relative = "ReconstructedBackground-" + index.ToString("D2") + (variant == 0 ? "" : "-" + variant.ToString("D2")) + ".shader"; string path = Path.Combine(shaders, relative); File.WriteAllText(path, source);
            generated.Add(new { name, donorName = contract.DonorName, relative, sha256 = Hash(path), originalPropertyContractSha256 = contract.Digest, originalPropertyCount = properties.Length, targetIdentities = contract.Sources, shaderBehavior = "reconstructed-experimental/approximate", obligations = "Exact original property exposure/types/defaults/attributes; approximate lighting, rim/emission, resource-map, underwater fog, atmosphere/scattering and transparency behavior need original-GL image comparison. Not stock shader equivalence." });
            }
        }
        File.WriteAllText(Path.Combine(output, "cook-recipe.json"), JsonSerializer.Serialize(new { schema = "continuum-private-menu-shader-cook/v1", unity = "2019.4.18f1", recoveryManifest = Path.Combine(recovery, "recovery.json"), recoveryManifestSha256 = Hash(Path.Combine(recovery, "recovery.json")), tmpPackage = package, tmpPackageSha256 = Hash(package), copied, generated, remaining = "Add the three recovered UI recipes before cooking; backend Metal; source shaders and dependencies stay private; native visible menu qualification required." }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine("Prepared matching vendor/TMP and explicitly approximate original background recipes."); return 0;
    }
    sealed record Source(string SourceFile, long PathId, string ObjectSha256, string SourceSha256, string MetadataFile, string MetadataSha256);
    sealed record Contract(Property[] Properties, string Digest, string DonorName, List<Source> Sources);
    sealed record Property(string Name, string Description, int Kind, uint Flags, double[] Default, string[] Attributes, string TextureDefault, int TextureDimension)
    {
        static string Number(double value) => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : throw new InvalidDataException("Nonfinite property default.");
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        public string Declaration()
        {
            if ((Flags & ~15u) != 0) throw new InvalidDataException("Unsupported original property flags: " + Name);
            var attributes = Attributes.ToList();
            foreach (var pair in new[] { (1u, "HideInInspector"), (2u, "PerRendererData"), (4u, "NoScaleOffset"), (8u, "Normal") }) if ((Flags & pair.Item1) != 0 && !attributes.Contains(pair.Item2)) attributes.Add(pair.Item2);
            if (attributes.Any(a => a.Length > 256 || a.Any(c => c is '[' or ']' or '\r' or '\n'))) throw new InvalidDataException("Unsafe property attribute.");
            string type = Kind switch { 0 => "Color", 1 => "Vector", 2 => "Float", 3 => "Range(" + Number(Default[1]) + "," + Number(Default[2]) + ")", 4 when TextureDimension == 2 => "2D", _ => throw new InvalidDataException("Unsupported original property type/dimension: " + Name) };
            string value = Kind switch { 0 or 1 => "(" + string.Join(",", Default.Select(Number)) + ")", 2 or 3 => Number(Default[0]), 4 => Quote(TextureDefault) + "{}", _ => throw new InvalidDataException("Unsupported property.") };
            return string.Join("", attributes.Select(a => "[" + a + "]")) + Name + "(" + Quote(Description) + "," + type + ")=" + value;
        }
        public string Uniform() => (Kind == 4 ? "sampler2D " : Kind is 0 or 1 ? "float4 " : "float ") + Name + ";";
    }
    static JsonElement Child(JsonElement node, string name) => node.GetProperty("children").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
    static Dictionary<string, List<Contract>> ReadContracts(string recovery)
    {
        string manifest = Path.Combine(recovery, "recovery.json");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException("Bounded recovery manifest required.");
        using var index = JsonDocument.Parse(File.ReadAllText(manifest));
        var contracts = new Dictionary<string, List<Contract>>(StringComparer.Ordinal);
        foreach (var row in index.RootElement.GetProperty("rows").EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!, entry = row.GetProperty("entry").GetString()!;
            if (!(name.StartsWith("KSP/", StringComparison.Ordinal) || name.StartsWith("Terrain/Scaled Planet", StringComparison.Ordinal) || name == "Sprite/Simple Texture (Unlit)" || name == "MaskedTexture")) continue;
            if (!Regex.IsMatch(entry, "^shader-[0-9]{2}$")) throw new InvalidDataException("Unsafe recovery entry.");
            string path = Path.Combine(recovery, entry, "parsed-form.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Bounded shader metadata required.");
            using var parsed = JsonDocument.Parse(File.ReadAllText(path));
            var array = Child(Child(Child(parsed.RootElement, "m_PropInfo"), "m_Props"), "Array");
            var properties = new List<Property>();
            foreach (var property in array.GetProperty("children").EnumerateArray())
            {
                string propertyName = Child(property, "m_Name").GetProperty("value").GetString()!;
                if (!Regex.IsMatch(propertyName, "^[A-Za-z_][A-Za-z_0-9]*$") || properties.Any(p => p.Name == propertyName)) throw new InvalidDataException("Invalid/duplicate original property name.");
                var texture = Child(property, "m_DefTexture");
                double[] defaults = Enumerable.Range(0, 4).Select(i => Child(property, "m_DefValue[" + i + "]").GetProperty("value").GetDouble()).ToArray();
                var attributes = Child(Child(property, "m_Attributes"), "Array").GetProperty("children").EnumerateArray().Select(a => a.GetProperty("value").GetString() ?? throw new InvalidDataException("Invalid attribute.")).ToArray();
                var value = new Property(propertyName, Child(property, "m_Description").GetProperty("value").GetString()!, Child(property, "m_Type").GetProperty("value").GetInt32(), Child(property, "m_Flags").GetProperty("value").GetUInt32(), defaults, attributes, Child(texture, "m_DefaultName").GetProperty("value").GetString()!, Child(texture, "m_TexDim").GetProperty("value").GetInt32());
                _ = value.Declaration(); properties.Add(value);
                if (properties.Count > 128) throw new InvalidDataException("Property contract bound exceeded.");
            }
            string digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(properties)))).ToLowerInvariant();
            var identity = new Source(row.GetProperty("source").GetString()!, row.GetProperty("pathId").GetInt64(), row.GetProperty("objectSha256").GetString()!, row.GetProperty("sourceSha256").GetString()!, path, Hash(path));
            if (!contracts.TryGetValue(name, out var variants)) { variants = new List<Contract>(); contracts.Add(name, variants); }
            var previous = variants.SingleOrDefault(v => v.Digest == digest);
            if (previous != null) previous.Sources.Add(identity);
            else
            {
                if (!Regex.IsMatch(identity.ObjectSha256, "^[0-9a-f]{64}$")) throw new InvalidDataException("Original object digest invalid.");
                string donorName = variants.Count == 0 ? name : name + "/ContinuumOriginal-" + identity.ObjectSha256.Substring(0, 16);
                variants.Add(new Contract(properties.ToArray(), digest, donorName, new List<Source> { identity }));
            }
        }
        return contracts;
    }
}
