using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

static class MenuShaderCook
{
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static int Run(string[] args)
    {
        string builtin = Path.GetFullPath(args[1]), package = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
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
            bool standard = name.EndsWith("Standard", StringComparison.Ordinal); bool bumped = name.Contains("Bumped", StringComparison.Ordinal) || name.StartsWith("Terrain/", StringComparison.Ordinal);
            string source = "Shader \"" + name + "\" {\n" + Properties + "\nSubShader { Tags { \"RenderType\"=\"" + (transparent ? "Transparent" : "Opaque") + "\" \"Queue\"=\"" + (transparent ? "Transparent" : "Geometry") + "\" }\n" + (transparent ? "ZWrite Off Blend SrcAlpha OneMinusSrcAlpha\n" : "") +
                "CGPROGRAM\n#pragma target 3.0\n#pragma surface surf " + (unlit ? "MenuUnlit" : standard ? "StandardSpecular" : name.Contains("Specular", StringComparison.Ordinal) ? "BlinnPhong" : "Lambert") + (transparent ? " alpha:fade" : "") + "\n#include \"UnityCG.cginc\"\n" + (unlit ? "half4 LightingMenuUnlit(SurfaceOutput s,half3 lightDir,half atten){return half4(s.Albedo,s.Alpha);}\n" : "") + Uniforms +
                "\nvoid surf(Input i,inout " + (standard ? "SurfaceOutputStandardSpecular" : "SurfaceOutput") + " o){ float4 t=tex2D(_MainTex,i.uv_MainTex); o.Albedo=t.rgb*_Color.rgb; o.Alpha=t.a*_Color.a*_Opacity;" +
                (bumped ? "o.Normal=UnpackNormal(tex2D(_BumpMap,i.uv_MainTex));" : "") +
                (standard ? "o.Specular=_SpecColor.rgb; o.Smoothness=_Shininess;" : "o.Specular=_Shininess; o.Gloss=t.a;") +
                "float rim=pow(1-saturate(dot(normalize(i.viewDir),o.Normal)),max(_RimFalloff,.0001)); o.Emission=_RimColor.rgb*_RimColor.a*rim+tex2D(_Emissive,i.uv_MainTex).rgb*_EmissiveColor.rgb*_EmissiveColor.a; }\nENDCG\n}\nFallback Off\n}\n";
            string relative = "ReconstructedBackground-" + index.ToString("D2") + ".shader"; string path = Path.Combine(shaders, relative); File.WriteAllText(path, source);
            generated.Add(new { name, relative, sha256 = Hash(path), shaderBehavior = "reconstructed-experimental/approximate", obligations = "Preserves published material/texture identity; approximate lighting, rim/emission, resource-map, underwater fog, atmosphere/scattering and transparency behavior need original-GL image comparison. Not stock shader equivalence." });
        }
        File.WriteAllText(Path.Combine(output, "cook-recipe.json"), JsonSerializer.Serialize(new { schema = "continuum-private-menu-shader-cook/v1", unity = "2019.4.18f1", tmpPackage = package, tmpPackageSha256 = Hash(package), copied, generated, remaining = "Add the three recovered UI recipes before cooking; backend Metal; source shaders and dependencies stay private; native visible menu qualification required." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Prepared matching vendor/TMP and explicitly approximate original background recipes."); return 0;
    }
    const string Properties = "Properties { _MainTex(\"Main texture\",2D)=\"white\"{} _BumpMap(\"Normal map\",2D)=\"bump\"{} _Emissive(\"Emission map\",2D)=\"black\"{} _Color(\"Color\",Color)=(1,1,1,1) _SpecColor(\"Specular\",Color)=(.5,.5,.5,1) _Shininess(\"Shininess\",Range(.01,1))=.078125 _Opacity(\"Opacity\",Range(0,1))=1 _RimColor(\"Rim\",Color)=(0,0,0,0) _RimFalloff(\"Rim falloff\",Float)=1 _EmissiveColor(\"Emission\",Color)=(0,0,0,0) _ResourceMap(\"Resource\",2D)=\"black\"{} }";
    const string Uniforms = "sampler2D _MainTex,_BumpMap,_Emissive,_ResourceMap; float4 _Color,_SpecColor,_RimColor,_EmissiveColor; float _Shininess,_Opacity,_RimFalloff; struct Input { float2 uv_MainTex; float3 viewDir; };";
}
