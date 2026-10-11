#if UNITY_6000_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildModernVesselProbe
{
    [Serializable] sealed class Receipt
    {
        public string schema="continuum-modern-vessel-build/v1", status="built-unqualified", unity, editorFullVersion, architecture, backend="Mono", graphicsApi="Metal", renderPipeline="Built-in", output, bundlePath, bundleSha256, builderSha256, playerAssemblySha256, sourcesSha256;
        public string qualification="Build only; cold fixture admission, decoded rendering and changing-vessel replay remain native gates.";
        public int antiAliasing, diagnosticVariantCount;
    }
    static string Hash(string path)
    {
        using(var input=File.OpenRead(path)) using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant();
    }
    static string NewPath(string variable,bool app)
    {
        string value=Environment.GetEnvironmentVariable(variable);
        if(string.IsNullOrEmpty(value)||!Path.IsPathRooted(value)||Directory.Exists(value)||File.Exists(value)||(app&&!value.EndsWith(".app",StringComparison.Ordinal))) throw new InvalidOperationException("New absolute output required: "+variable);
        return Path.GetFullPath(value);
    }
    public static void Build()
    {
        string full=UnityEditorInternal.InternalEditorUtility.GetFullUnityVersion();
        if(Application.unityVersion!="6000.3.26f1"||!full.Contains("de8167e7440c")) throw new InvalidOperationException("Pinned Unity6000.3.26f1/de8167e7440c required.");
        if(GraphicsSettings.defaultRenderPipeline!=null||QualitySettings.renderPipeline!=null) throw new InvalidOperationException("Built-in render pipeline required.");
        string output=NewPath("CONTINUUM_MODERN_VESSEL_PLAYER",true), bundles=NewPath("CONTINUUM_MODERN_VESSEL_BUNDLES",false);
        string architecture=Environment.GetEnvironmentVariable("CONTINUUM_MODERN_VESSEL_ARCHITECTURE");
        OSArchitecture selected;
        if(architecture=="ARM64") selected=OSArchitecture.ARM64;
        else if(architecture=="x64") selected=OSArchitecture.x64;
        else throw new InvalidOperationException("Explicit ARM64 or x64 architecture required; no Universal treatment.");
        const string mappedPath="Assets/ContinuumShaders/FlightPartMapped.shader";
        var mapped=AssetDatabase.LoadAssetAtPath<Shader>(mappedPath);
        if(mapped==null||mapped.name!="Continuum/FlightPartMappedInstanced"||ShaderUtil.ShaderHasError(mapped)) throw new InvalidOperationException("Exact mapped shader source required.");
        var priorArchitecture=UnityEditor.OSXStandalone.UserBuildSettings.architecture;
        var priorBackend=PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
        var priorApis=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
        bool priorDefault=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX);
        int priorAA=QualitySettings.antiAliasing;
        var priorScene=EditorSceneManager.GetActiveScene();
        string scenePath="Assets/ModernVesselProbe-"+Guid.NewGuid().ToString("N")+".unity";
        var temporary=new List<string>(); temporary.Add(scenePath);
        SerializedObject graphics=null; int priorStripping=-1;
        try
        {
            UnityEditor.OSXStandalone.UserBuildSettings.architecture=selected;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,new[] {GraphicsDeviceType.Metal});
            QualitySettings.antiAliasing=0;
            if(UnityEditor.OSXStandalone.UserBuildSettings.architecture!=selected||PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone)!=ScriptingImplementation.Mono2x) throw new InvalidOperationException("Architecture/backend readback differs.");
            var settings=AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if(settings.Length!=1) throw new InvalidOperationException("Unique GraphicsSettings required.");
            graphics=new SerializedObject(settings[0]); var stripping=graphics.FindProperty("m_InstancingStripping");
            if(stripping==null||stripping.propertyType!=SerializedPropertyType.Enum) throw new InvalidOperationException("Instancing stripping contract unavailable.");
            priorStripping=stripping.enumValueIndex; int keep=-1;
            for(int i=0;i<stripping.enumNames.Length;i++) if(stripping.enumNames[i].Replace(" ","").Equals("KeepAll",StringComparison.OrdinalIgnoreCase)) keep=i;
            if(keep<0) throw new InvalidOperationException("KeepAll unavailable.");
            stripping.enumValueIndex=keep; graphics.ApplyModifiedPropertiesWithoutUndo();
            Directory.CreateDirectory(bundles);
            var bundle=BuildPipeline.BuildAssetBundles(bundles,new[] {new AssetBundleBuild {assetBundleName="continuum-modern-mapped",assetNames=new[] {mappedPath}}},BuildAssetBundleOptions.ChunkBasedCompression|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneOSX);
            if(bundle==null) throw new InvalidOperationException("Modern shader bundle failed.");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var variants=new ShaderVariantCollection();
            Shader diagnostic=null;
            foreach(string name in new[] {"BodyDiffuse","DepthCopy","Composite","DebugGeometry","DebugDepth"})
            {
                var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/ContinuumMultiscale/"+name+".shader");
                if(shader==null||ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Multiscale shader import failed: "+name);
                if(name=="DebugGeometry") diagnostic=shader;
                string path=scenePath+"-"+name+".mat"; var material=new Material(shader); AssetDatabase.CreateAsset(material,path); temporary.Add(path);
                var go=new GameObject("Retained "+name,typeof(MeshRenderer)); go.GetComponent<MeshRenderer>().sharedMaterial=material; go.SetActive(false);
            }
            foreach(var keywords in new[] {new string[0],new[] {"INSTANCING_ON"}}) if(!variants.Add(new ShaderVariantCollection.ShaderVariant(diagnostic,PassType.Normal,keywords))) throw new InvalidOperationException("Diagnostic visible variant rejected.");
            foreach(var keywords in new[] {new[] {"SHADOWS_DEPTH"},new[] {"SHADOWS_DEPTH","INSTANCING_ON"}}) if(!variants.Add(new ShaderVariantCollection.ShaderVariant(diagnostic,PassType.ShadowCaster,keywords))) throw new InvalidOperationException("Diagnostic depth variant rejected.");
            string variantPath=scenePath+".shadervariants"; AssetDatabase.CreateAsset(variants,variantPath); temporary.Add(variantPath);
            new GameObject("Diagnostic retention",typeof(MultiscaleShaderRetention)).GetComponent<MultiscaleShaderRetention>().variants=variants;
            EditorSceneManager.SaveScene(scene,scenePath);
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions {scenes=new[] {scenePath},locationPathName=output,target=BuildTarget.StandaloneOSX,subtarget=(int)StandaloneBuildSubtarget.Player,options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded) throw new InvalidOperationException("Modern player build failed: "+report.summary.result);
            var paths=new List<string>(Directory.GetFiles("Assets","*.cs",SearchOption.AllDirectories)); paths.AddRange(Directory.GetFiles("Assets","*.shader",SearchOption.AllDirectories)); paths.AddRange(Directory.GetFiles("Assets","*.cginc",SearchOption.AllDirectories)); paths.Sort(StringComparer.Ordinal);
            string identity=""; foreach(string path in paths) identity+=path.Replace('\\','/')+" "+Hash(path)+"\n";
            string sourceHash; using(var hash=SHA256.Create()) sourceHash=BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-","").ToLowerInvariant();
            var receipt=new Receipt {unity=Application.unityVersion,editorFullVersion=full,architecture=architecture,output=output,bundlePath=Path.Combine(bundles,"continuum-modern-mapped"),builderSha256=Hash("Assets/Editor/BuildModernVesselProbe.cs"),playerAssemblySha256=Hash(Path.Combine(output,"Contents/Resources/Data/Managed/Assembly-CSharp.dll")),sourcesSha256=sourceHash,diagnosticVariantCount=variants.variantCount,antiAliasing=0};
            receipt.bundleSha256=Hash(receipt.bundlePath);
            using(var writer=new StreamWriter(new FileStream(output+".build.json",FileMode.CreateNew,FileAccess.Write))) writer.Write(JsonUtility.ToJson(receipt,true));
        }
        finally
        {
            foreach(string path in temporary) AssetDatabase.DeleteAsset(path);
            if(graphics!=null&&priorStripping>=0) {graphics.Update(); graphics.FindProperty("m_InstancingStripping").enumValueIndex=priorStripping; graphics.ApplyModifiedPropertiesWithoutUndo();}
            UnityEditor.OSXStandalone.UserBuildSettings.architecture=priorArchitecture;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,priorBackend);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,priorApis); PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX,priorDefault); QualitySettings.antiAliasing=priorAA;
            if(priorScene.IsValid()&&priorScene.isLoaded) EditorSceneManager.SetActiveScene(priorScene);
        }
    }
}
#endif
