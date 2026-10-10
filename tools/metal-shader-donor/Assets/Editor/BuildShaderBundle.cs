using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildShaderBundle
{
    [Serializable] sealed class Input { public string path, sha256; }
    [Serializable] sealed class Receipt
    {
        public string schema = "continuum-shader-build/v1", unityVersion, revision, graphicsApi, bundleName, bundleSha256, editorFullVersion, editorBinarySha256, toolAssemblySha256;
        public bool instancingRequired, referenceVariantsRequired, compilerFailureProbePassed;
        public int instancingStrippingValue = -1;
        public string instancingStrippingName, generatedPassSourceDirectory;
        public string[] instancingStrippingEnumNames, instancingStrippingDisplayNames;
        public string[] shaders;
        public string[] retainedVariants;
        public Input[] inputs;
    }
    static string Hash(string path)
    {
        using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    public static void Build()
    {
        string editorVersion = UnityEditorInternal.InternalEditorUtility.GetFullUnityVersion();
        if (Application.unityVersion != "2019.4.18f1" || !editorVersion.Contains("3310a4d4f880") ||
            !File.ReadAllText("ProjectSettings/ProjectVersion.txt").Contains("2019.4.18f1 (3310a4d4f880)"))
            throw new Exception("Matching Unity 2019.4.18f1 revision 3310a4d4f880 required.");
        string apiText = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_GRAPHICS_API") ?? "Metal";
        GraphicsDeviceType api;
        if (apiText == "Metal") api = GraphicsDeviceType.Metal;
        else if (apiText == "OpenGLCore") api = GraphicsDeviceType.OpenGLCore;
        else throw new Exception("Graphics API must be Metal or OpenGLCore.");
        string source = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_SOURCE_ROOT") ?? "Assets/MetalShaders";
        if (!source.StartsWith("Assets/", StringComparison.Ordinal) || source.Contains("..") || !Directory.Exists(source))
            throw new Exception("Shader source root must be an existing directory inside Assets.");
        string name = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_NAME") ?? (api == GraphicsDeviceType.Metal ? "continuum-metal-ui" : "continuum-opengl-aero");
        if (string.IsNullOrWhiteSpace(name) || !(name[0] >= 'a' && name[0] <= 'z' || name[0] >= '0' && name[0] <= '9') || name.Any(c => !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.')) || name.Contains(".."))
            throw new Exception("Bundle name must be one lowercase filename.");
        bool instancing = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_REQUIRE_INSTANCING") == "1";
        bool reference = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_REQUIRE_REFERENCE") == "1";
        if (reference && !instancing) throw new Exception("Reference comparison requires the instanced variant contract too.");
        bool selectiveInstancing = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_SELECTIVE_INSTANCING") == "1";
        if (selectiveInstancing && !instancing) throw new Exception("Selective instancing requires retained instancing variants.");
        var output = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output) || !Directory.Exists(output) || Directory.EnumerateFileSystemEntries(output).Any())
            throw new Exception("Set CONTINUUM_SHADER_BUNDLE_OUTPUT to an empty absolute directory.");
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { api });
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var paths = AssetDatabase.FindAssets("t:Shader", new[] { source })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
            throw new Exception("No shader sources under " + source);
        var variants = new ShaderVariantCollection();
        var names = new List<string>();
        var retained = new List<string>();
        foreach (var path in paths)
        {
            Shader previous = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (previous != null) ShaderUtil.ClearShaderMessages(previous);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
                throw new Exception("Unable to import shader " + path);
            names.Add(shader.name);
            if (instancing)
            {
                if (!File.ReadAllText(path).Contains("#pragma multi_compile_instancing"))
                {
                    if (selectiveInstancing) continue;
                    throw new Exception("Instancing pragma required for " + path);
                }
                bool surface = File.ReadAllText(path).Contains("#pragma surface");
                PassType pass = surface ? PassType.ForwardBase : PassType.Normal;
                string[] keywords = surface ? new[] { "DIRECTIONAL", "INSTANCING_ON" } : new[] { "INSTANCING_ON" };
                if (!variants.Add(new ShaderVariantCollection.ShaderVariant(shader, pass, keywords)))
                    throw new Exception("Unable to retain instancing variant for " + path);
                retained.Add(shader.name + ":" + pass + ":" + string.Join(",", keywords));
                if (surface && File.ReadAllText(path).Contains("addshadow"))
                {
                    if (!variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ShadowCaster, "SHADOWS_DEPTH", "INSTANCING_ON")))
                        throw new Exception("Unable to retain shadow instancing variant for " + path);
                    retained.Add(shader.name + ":ShadowCaster:SHADOWS_DEPTH,INSTANCING_ON");
                }
                if (reference && surface)
                {
                    // Both consumers use captured SH and one directional light.
                    foreach (bool packet in new[] { false, true }) foreach (bool probes in new[] { false, true })
                    foreach (int shadow in new[] { 0, 1 })
                    {
                        var k = new List<string> { "DIRECTIONAL" };
                        if (packet) k.Add("INSTANCING_ON");
                        if (probes) k.Add("LIGHTPROBE_SH");
                        if (shadow > 0) k.Add("SHADOWS_SCREEN");
                        string tuple = shader.name + ":ForwardBase:" + string.Join(",", k);
                        if (retained.Contains(tuple)) continue;
                        try
                        {
                            if (!variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ForwardBase, k.ToArray())))
                                throw new Exception("Unable to retain reference/probe variant " + tuple);
                        }
                        catch (ArgumentException error) { throw new Exception("Unsupported reference/probe variant " + tuple, error); }
                        retained.Add(tuple);
                    }
                    if (File.ReadAllText(path).Contains("addshadow"))
                    {
                        if (!variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ShadowCaster, "SHADOWS_DEPTH")))
                            throw new Exception("Unable to retain reference shadow variant for " + path);
                        retained.Add(shader.name + ":ShadowCaster:SHADOWS_DEPTH");
                    }
                }
            }
        }
        if (instancing && retained.Count == 0) throw new Exception("No instancing shader variants retained.");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw new Exception("Duplicate shader names.");
        const string variantPath = "Assets/ContinuumBuildVariants.shadervariants";
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(variantPath) != null)
            throw new Exception("Generated variant path already exists.");
        if (instancing) AssetDatabase.CreateAsset(variants, variantPath);
        SerializedObject graphicsSettings = null;
        int originalInstancingStripping = -1, effectiveInstancingStripping = -1;
        string strippingName = null;
        string[] strippingNames = null, strippingDisplayNames = null;
        string generatedSource = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_GENERATED_SOURCE_OUTPUT");
        var builds = new[]
        {
            new AssetBundleBuild
            {
                assetBundleName = name,
                assetNames = instancing ? paths.Concat(new[] { variantPath }).ToArray() : paths
            }
        };
        try
        {
            if (instancing)
            {
                var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
                if (settings.Length != 1) throw new Exception("Unique graphics settings required.");
                graphicsSettings = new SerializedObject(settings[0]);
                var stripping = graphicsSettings.FindProperty("m_InstancingStripping");
                if (stripping == null || stripping.propertyType != SerializedPropertyType.Enum) throw new Exception("Pinned instancing stripping enum unavailable.");
                strippingNames = stripping.enumNames; strippingDisplayNames = stripping.enumDisplayNames;
                int keepAll = Array.FindIndex(strippingNames, n => new string(n.Where(char.IsLetter).ToArray()).Equals("KeepAll", StringComparison.OrdinalIgnoreCase));
                if (keepAll < 0) throw new Exception("KeepAll instancing stripping enum unavailable: " + string.Join(",", strippingNames));
                originalInstancingStripping = stripping.enumValueIndex;
                stripping.enumValueIndex = keepAll;
                graphicsSettings.ApplyModifiedPropertiesWithoutUndo(); graphicsSettings.Update();
                stripping = graphicsSettings.FindProperty("m_InstancingStripping");
                if (stripping.enumValueIndex != keepAll) throw new Exception("KeepAll instancing admission failed.");
                effectiveInstancingStripping = stripping.intValue; strippingName = stripping.enumNames[keepAll];
            }
            if (!string.IsNullOrEmpty(generatedSource))
            {
                if (!Path.IsPathRooted(generatedSource) || Directory.Exists(generatedSource) || File.Exists(generatedSource)) throw new Exception("Generated source output must be a new absolute directory.");
                var method = typeof(ShaderUtil).GetMethod("GetShaderPassSourceCode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(Shader), typeof(int), typeof(int) }, null);
                if (method == null) throw new Exception("Pinned generated pass source API unavailable.");
                Directory.CreateDirectory(generatedSource);
                for (int shaderIndex = 0; shaderIndex < paths.Length; shaderIndex++)
                {
                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(paths[shaderIndex]);
                    var probe = new Material(shader);
                    try { for (int pass = 0; pass < probe.passCount; pass++) File.WriteAllText(Path.Combine(generatedSource, "shader-" + shaderIndex + "-subshader-0-pass-" + pass + ".txt"), (string)method.Invoke(null, new object[] { shader, 0, pass }) ?? ""); }
                    finally { UnityEngine.Object.DestroyImmediate(probe); }
                }
            }
            var manifest = BuildPipeline.BuildAssetBundles(output, builds, BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneOSX);
            if (manifest == null || !File.Exists(Path.Combine(output, name))) throw new Exception("Shader bundle build failed.");
            foreach (string path in paths) if (ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(path))) throw new Exception("Shader error after bundle build: " + path);
            var inputs = Directory.GetFiles("Assets", "*", SearchOption.AllDirectories)
                .Where(p => new[] { ".shader", ".cginc", ".hlsl", ".cs" }.Contains(Path.GetExtension(p)))
                .Concat(Directory.GetFiles(Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes"), "*", SearchOption.AllDirectories))
                .Concat(new[] { "Packages/manifest.json", "ProjectSettings/ProjectVersion.txt" })
                .Concat(File.Exists("Packages/packages-lock.json") ? new[] { "Packages/packages-lock.json" } : new string[0])
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => new Input { path = p, sha256 = Hash(p) }).ToArray();
            var receipt = new Receipt { unityVersion = Application.unityVersion, editorFullVersion = editorVersion, editorBinarySha256 = Hash(Path.Combine(EditorApplication.applicationContentsPath, "MacOS/Unity")), toolAssemblySha256 = Hash(typeof(BuildShaderBundle).Assembly.Location), revision = "3310a4d4f880", graphicsApi = apiText,
                bundleName = name, bundleSha256 = Hash(Path.Combine(output, name)), instancingRequired = instancing, referenceVariantsRequired = reference, shaders = names.ToArray(), retainedVariants = retained.ToArray(), inputs = inputs,
                instancingStrippingValue = effectiveInstancingStripping, instancingStrippingName = strippingName, instancingStrippingEnumNames = strippingNames, instancingStrippingDisplayNames = strippingDisplayNames, generatedPassSourceDirectory = generatedSource };
            File.WriteAllText(Path.Combine(output, "build-receipt.json"), JsonUtility.ToJson(receipt, true));
            Debug.Log("Built " + apiText + " shader bundle with " + paths.Length + " shaders; instancing=" + instancing);
        }
        finally
        {
            if (graphicsSettings != null && originalInstancingStripping >= 0) { graphicsSettings.Update(); graphicsSettings.FindProperty("m_InstancingStripping").enumValueIndex = originalInstancingStripping; graphicsSettings.ApplyModifiedPropertiesWithoutUndo(); }
            if (instancing) AssetDatabase.DeleteAsset(variantPath); else UnityEngine.Object.DestroyImmediate(variants);
        }
    }

    public static void VerifyCompilerFailure()
    {
        const string path = "Assets/ContinuumCompilerFailureProbe.shader";
        if (File.Exists(path)) throw new Exception("Compiler failure probe path already exists.");
        try
        {
            string output = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_PROBE_REPORT");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output) || File.Exists(output)) throw new Exception("New absolute compiler probe report required.");
            string bundleOutput = output + ".bundle";
            if (Directory.Exists(bundleOutput)) throw new Exception("Compiler probe bundle output already exists.");
            Directory.CreateDirectory(bundleOutput);
            File.WriteAllText(path, "Shader \"Continuum/CompilerFailureProbe\" { SubShader { Pass { CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\nfloat4 vert(float4 v:POSITION):SV_POSITION { return v; }\nfloat4 frag():SV_Target { return continuum_intentionally_undefined; }\nENDCG } } }");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.OpenGLCore });
            BuildPipeline.BuildAssetBundles(bundleOutput, new[] { new AssetBundleBuild { assetBundleName = "compiler-failure-probe", assetNames = new[] { path } } },
                BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneOSX);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null || !ShaderUtil.ShaderHasError(shader) || !ShaderUtil.GetShaderMessages(shader).Any(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
                throw new Exception("Compiler error observation failed.");
            File.WriteAllText(output, JsonUtility.ToJson(new Receipt { unityVersion = Application.unityVersion,
                editorFullVersion = UnityEditorInternal.InternalEditorUtility.GetFullUnityVersion(), compilerFailureProbePassed = true }, true));
            Debug.Log("Continuum compiler failure probe passed; intentional shader error detected.");
        }
        finally { AssetDatabase.DeleteAsset(path); }
    }
}
