using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildAeroFieldWorkbench
{
    [Serializable] sealed class Receipt
    {
        public string schema = "continuum-aero-workbench-build/v1", unity, editorRevision, graphicsApi, output, workbenchSourceSha256, residentSourceSha256, residentFormatSourceSha256, buildSourceSha256, fixtureShaderSourceSha256, status, toolAssemblySha256, playerAssemblySha256, packagesSha256,multiscaleSourceSha256,multiscaleFormatSourceSha256;
        public string qualification = "Player built; actual rendering, fixture loading and persistent reload require native consumer proof.";
        public string wholeCraftRuntimeSourceSha256,wholeCraftFormatSourceSha256;
        public string vesselReplaySourceSha256,vesselRuntimeFormatSourceSha256;
        public string persistentTrailRuntimeSourceSha256,persistentTrailFormatSourceSha256,persistentTrailShaderSourceSha256;
        public string trailEmitterFormatSourceSha256,measuredTrailSegmentsSourceSha256;
        public string instancingStrippingName; public int instancingStrippingValue,diagnosticVariantCount;
    }
    static string Hash(string path) { using(var stream=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant(); }
    public static void Build()
    {
        if(Application.unityVersion!="2019.4.18f1"||!UnityEditorInternal.InternalEditorUtility.GetFullUnityVersion().Contains("3310a4d4f880")) throw new InvalidOperationException("Pinned2019.4.18f1 revision3310a4d4f880 required.");
        string output=Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_PLAYER");
        if(string.IsNullOrEmpty(output)||!Path.IsPathRooted(output)||!output.EndsWith(".app",StringComparison.Ordinal)||Directory.Exists(output)||File.Exists(output)) throw new InvalidOperationException("New absolute private .app output required.");
        string apiText=Environment.GetEnvironmentVariable("CONTINUUM_SHADER_GRAPHICS_API")??"OpenGLCore";
        GraphicsDeviceType[] apis;
        if(apiText=="OpenGLCore") apis=new[] {GraphicsDeviceType.OpenGLCore};
        else if(apiText=="Metal") apis=new[] {GraphicsDeviceType.Metal};
        else if(apiText=="Metal,OpenGLCore") apis=new[] {GraphicsDeviceType.Metal,GraphicsDeviceType.OpenGLCore};
        else throw new InvalidOperationException("Explicit supported graphics API required.");
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX,false); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,apis);
        PlayerSettings.defaultScreenWidth=960; PlayerSettings.defaultScreenHeight=540; PlayerSettings.fullScreenMode=FullScreenMode.Windowed; PlayerSettings.runInBackground=true;
        string scenePath="Assets/ContinuumWorkbenchScene-"+Guid.NewGuid().ToString("N")+".unity";
        string materialPath=scenePath+".mat";
        string variantsPath=scenePath+".shadervariants"; SerializedObject graphicsSettings=null; int priorStripping=-1,effectiveStripping=-1; string strippingName=null; int variantCount=0;
        string[] multiscaleShaders={"BodyDiffuse","DepthCopy","Composite","DebugGeometry","DebugDepth","PersistentTrail"};
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        try
        {
            // Serialized reference retains the fixture's visible and depth passes.
            var placeholder=GameObject.CreatePrimitive(PrimitiveType.Cube); placeholder.name="Builtin shader retention reference";
            Shader fixtureShader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/FixtureOpaque.shader");
            if(fixtureShader==null||ShaderUtil.ShaderHasError(fixtureShader)) throw new InvalidOperationException("Fixture shader failed to import.");
            var material=new Material(fixtureShader); AssetDatabase.CreateAsset(material,materialPath); placeholder.GetComponent<Renderer>().sharedMaterial=material;
            placeholder.SetActive(false);
            if(Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_WORKBENCH_BUILD")=="1")
            {
                var settings=AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset"); if(settings.Length!=1) throw new InvalidOperationException("Unique pinned GraphicsSettings required."); graphicsSettings=new SerializedObject(settings[0]); var stripping=graphicsSettings.FindProperty("m_InstancingStripping"); if(stripping==null||stripping.propertyType!=SerializedPropertyType.Enum) throw new InvalidOperationException("Pinned instancing enum unavailable."); int keepAll=-1; for(int i=0;i<stripping.enumNames.Length;i++) if(stripping.enumNames[i].Replace(" ","").Equals("KeepAll",StringComparison.OrdinalIgnoreCase)) keepAll=i; if(keepAll<0) throw new InvalidOperationException("KeepAll enum absent."); priorStripping=stripping.enumValueIndex; stripping.enumValueIndex=keepAll; graphicsSettings.ApplyModifiedPropertiesWithoutUndo(); graphicsSettings.Update(); stripping=graphicsSettings.FindProperty("m_InstancingStripping"); if(stripping.enumValueIndex!=keepAll) throw new InvalidOperationException("KeepAll readback failed."); effectiveStripping=stripping.intValue; strippingName=stripping.enumNames[keepAll];
                var variants=new ShaderVariantCollection(); var diagnostic=AssetDatabase.LoadAssetAtPath<Shader>("Assets/ContinuumMultiscale/DebugGeometry.shader"); if(diagnostic==null) throw new InvalidOperationException("Diagnostic shader absent."); foreach(var keywords in new[] {new string[0],new[] {"INSTANCING_ON"}}) { if(!variants.Add(new ShaderVariantCollection.ShaderVariant(diagnostic,PassType.Normal,keywords))) throw new InvalidOperationException("Diagnostic visible variant rejected."); } foreach(var keywords in new[] {new[] {"SHADOWS_DEPTH"},new[] {"SHADOWS_DEPTH","INSTANCING_ON"}}) { if(!variants.Add(new ShaderVariantCollection.ShaderVariant(diagnostic,PassType.ShadowCaster,keywords))) throw new InvalidOperationException("Diagnostic depth variant rejected."); } variantCount=variants.variantCount; AssetDatabase.CreateAsset(variants,variantsPath); new GameObject("Explicit diagnostic variant retention",typeof(MultiscaleShaderRetention)).GetComponent<MultiscaleShaderRetention>().variants=variants;
                foreach(string name in multiscaleShaders)
                {
                    Shader shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/ContinuumMultiscale/"+name+".shader");
                    if(shader==null||ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Multiscale shader import failed: "+name);
                    var retained=new GameObject("Multiscale retained "+name,typeof(MeshRenderer)); var retainedMaterial=new Material(shader);
                    AssetDatabase.CreateAsset(retainedMaterial,scenePath+"-"+name+".mat"); retained.GetComponent<MeshRenderer>().sharedMaterial=retainedMaterial; retained.SetActive(false);
                }
            }
            EditorSceneManager.SaveScene(scene,scenePath);
            BuildReport report=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=new[] {scenePath},locationPathName=output,target=BuildTarget.StandaloneOSX,options=BuildOptions.Development });
            if(report.summary.result!=BuildResult.Succeeded) throw new InvalidOperationException("Workbench build failed: "+report.summary.result);
            var receipt=new Receipt { unity=Application.unityVersion,editorRevision="3310a4d4f880",graphicsApi=apiText,output=output,status="built",
                workbenchSourceSha256=Hash("Assets/AeroFieldWorkbench.cs"),buildSourceSha256=Hash("Assets/Editor/BuildAeroFieldWorkbench.cs"),fixtureShaderSourceSha256=Hash("Assets/FixtureOpaque.shader"),
                residentSourceSha256=Hash("Assets/ResidentMaterialRuntime.cs"),residentFormatSourceSha256=Hash("Assets/ResidentMaterialFormat.cs"),
                multiscaleSourceSha256=File.Exists("Assets/MultiscaleWorkbench.cs")?Hash("Assets/MultiscaleWorkbench.cs"):null,multiscaleFormatSourceSha256=File.Exists("Assets/MultiscaleSceneFormat.cs")?Hash("Assets/MultiscaleSceneFormat.cs"):null,
                wholeCraftRuntimeSourceSha256=File.Exists("Assets/WholeCraftRuntime.cs")?Hash("Assets/WholeCraftRuntime.cs"):null,wholeCraftFormatSourceSha256=File.Exists("Assets/WholeCraftSceneFormat.cs")?Hash("Assets/WholeCraftSceneFormat.cs"):null,
                vesselReplaySourceSha256=File.Exists("Assets/VesselReplaySession.cs")?Hash("Assets/VesselReplaySession.cs"):null,vesselRuntimeFormatSourceSha256=File.Exists("Assets/VesselRuntimeRecording.cs")?Hash("Assets/VesselRuntimeRecording.cs"):null,
                persistentTrailRuntimeSourceSha256=File.Exists("Assets/PersistentTrailRuntime.cs")?Hash("Assets/PersistentTrailRuntime.cs"):null,persistentTrailFormatSourceSha256=File.Exists("Assets/PersistentTrailOwner.cs")?Hash("Assets/PersistentTrailOwner.cs"):null,persistentTrailShaderSourceSha256=File.Exists("Assets/ContinuumMultiscale/PersistentTrail.shader")?Hash("Assets/ContinuumMultiscale/PersistentTrail.shader"):null,
                trailEmitterFormatSourceSha256=File.Exists("Assets/TrailEmitterRecording.cs")?Hash("Assets/TrailEmitterRecording.cs"):null,measuredTrailSegmentsSourceSha256=File.Exists("Assets/MeasuredTrailSegments.cs")?Hash("Assets/MeasuredTrailSegments.cs"):null,
                instancingStrippingName=strippingName,instancingStrippingValue=effectiveStripping,diagnosticVariantCount=variantCount,
                toolAssemblySha256=Hash(typeof(BuildAeroFieldWorkbench).Assembly.Location),playerAssemblySha256=Hash(Path.Combine(output,"Contents/Resources/Data/Managed/Assembly-CSharp.dll")),packagesSha256=Hash("Packages/manifest.json") };
            using(var writer=new StreamWriter(new FileStream(output+".build.json",FileMode.CreateNew,FileAccess.Write))) writer.Write(JsonUtility.ToJson(receipt,true));
        }
        finally { if(graphicsSettings!=null&&priorStripping>=0) { graphicsSettings.Update(); graphicsSettings.FindProperty("m_InstancingStripping").enumValueIndex=priorStripping; graphicsSettings.ApplyModifiedPropertiesWithoutUndo(); } AssetDatabase.DeleteAsset(scenePath); AssetDatabase.DeleteAsset(materialPath); AssetDatabase.DeleteAsset(variantsPath); foreach(string name in multiscaleShaders) AssetDatabase.DeleteAsset(scenePath+"-"+name+".mat"); }
    }
}
