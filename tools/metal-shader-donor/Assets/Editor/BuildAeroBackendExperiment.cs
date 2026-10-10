using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

public static class BuildAeroBackendExperiment
{
    [Serializable] sealed class Receipt
    {
        public string schema="continuum-aero-backend-build/v1", unity, sourceSha256, toolAssemblySha256, player, playerAssemblySha256, metalBundle, openGlBundle, metalBundleSha256, openGlBundleSha256, metalPartBundle, openGlPartBundle;
        public bool reusedFieldBundles;
        public string scope="Same-player static gray-fixture backend experiment; not KSP performance or material equivalence.";
    }
    static string Hash(string path) { using(var stream=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant(); }
    static string Sources()
    {
        string[] extensions={".cs",".shader",".cginc",".hlsl"};
        var rows=Directory.GetFiles("Assets","*",SearchOption.AllDirectories).Where(path=>extensions.Contains(Path.GetExtension(path)))
            .Concat(new[] {"Packages/manifest.json","ProjectSettings/ProjectVersion.txt"}).OrderBy(path=>path,StringComparer.Ordinal).Select(path=>path+"\t"+Hash(path));
        using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",rows)))).Replace("-","").ToLowerInvariant();
    }
    public static void Build()
    {
        string root=Environment.GetEnvironmentVariable("CONTINUUM_AERO_BACKEND_BUILD_ROOT");
        if(string.IsNullOrEmpty(root)||!Path.IsPathRooted(root)||!Directory.Exists(root)||Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Empty absolute private backend build root required.");
        string source=Sources();
        bool resident=Environment.GetEnvironmentVariable("CONTINUUM_RESIDENT_PART_BUNDLES")=="1";
        string existingMetal=Environment.GetEnvironmentVariable("CONTINUUM_AERO_EXISTING_METAL_BUNDLE"),existingOpenGl=Environment.GetEnvironmentVariable("CONTINUUM_AERO_EXISTING_OPENGL_BUNDLE");
        bool reuse=existingMetal!=null||existingOpenGl!=null;
        if(reuse&&(!resident||string.IsNullOrEmpty(existingMetal)||string.IsNullOrEmpty(existingOpenGl)||!Path.IsPathRooted(existingMetal)||!Path.IsPathRooted(existingOpenGl)||!File.Exists(existingMetal)||!File.Exists(existingOpenGl))) throw new InvalidOperationException("Resident build reuse requires both absolute existing field bundles.");
        string previousMetalSha=reuse?Hash(existingMetal):null,previousOpenGlSha=reuse?Hash(existingOpenGl):null;
        if (!File.Exists("Assets/ResidentMaterialFormat.cs")) throw new InvalidOperationException("Copy the exact shared Core ResidentMaterialFormat.cs into the private project before building.");
        Environment.SetEnvironmentVariable("CONTINUUM_SHADER_SOURCE_ROOT","Assets/ContinuumAeroField");
        Environment.SetEnvironmentVariable("CONTINUUM_SHADER_REQUIRE_INSTANCING","1");
        Environment.SetEnvironmentVariable("CONTINUUM_SHADER_SELECTIVE_INSTANCING","1");
        string metal=Path.Combine(root,"metal"),openGl=Path.Combine(root,"opengl");
        if(!reuse) foreach(string api in new[] {"Metal","OpenGLCore"})
        {
            string output=api=="Metal"?metal:openGl; Directory.CreateDirectory(output);
            Environment.SetEnvironmentVariable("CONTINUUM_SHADER_GRAPHICS_API",api);
            Environment.SetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_NAME",api=="Metal"?"continuum-aero-metal":"continuum-aero-opengl");
            Environment.SetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT",output);
            BuildShaderBundle.Build();
            if(Sources()!=source) throw new InvalidOperationException("Source changed during backend bundle build.");
        }
        string metalPart=null,openGlPart=null;
        if(resident)
        {
            Environment.SetEnvironmentVariable("CONTINUUM_SHADER_SOURCE_ROOT","Assets/ContinuumShaders");
            Environment.SetEnvironmentVariable("CONTINUUM_SHADER_REQUIRE_REFERENCE","1");
            foreach(string api in new[] {"Metal","OpenGLCore"})
            {
                string output=Path.Combine(root,api=="Metal"?"part-metal":"part-opengl"); Directory.CreateDirectory(output);
                string name=api=="Metal"?"continuum-resident-metal":"continuum-resident-opengl";
                Environment.SetEnvironmentVariable("CONTINUUM_SHADER_GRAPHICS_API",api);
                Environment.SetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_NAME",name);
                Environment.SetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT",output);
                BuildShaderBundle.Build();
                if(api=="Metal") metalPart=Path.Combine(output,name); else openGlPart=Path.Combine(output,name);
                if(Sources()!=source) throw new InvalidOperationException("Source changed during part bundle build.");
            }
        }
        string player=Path.Combine(root,"Aero Backend Workbench.app");
        Environment.SetEnvironmentVariable("CONTINUUM_SHADER_GRAPHICS_API","Metal,OpenGLCore");
        Environment.SetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_PLAYER",player);
        BuildAeroFieldWorkbench.Build();
        if(Sources()!=source) throw new InvalidOperationException("Source changed during backend player build.");
        var receipt=new Receipt {unity=Application.unityVersion,sourceSha256=source,toolAssemblySha256=Hash(typeof(BuildAeroBackendExperiment).Assembly.Location),player=player,
            playerAssemblySha256=Hash(Path.Combine(player,"Contents/Resources/Data/Managed/Assembly-CSharp.dll")),metalBundle=reuse?existingMetal:Path.Combine(metal,"continuum-aero-metal"),openGlBundle=reuse?existingOpenGl:Path.Combine(openGl,"continuum-aero-opengl"),metalPartBundle=metalPart,openGlPartBundle=openGlPart,reusedFieldBundles=reuse};
        receipt.metalBundleSha256=Hash(receipt.metalBundle); receipt.openGlBundleSha256=Hash(receipt.openGlBundle);
        if(reuse&&(receipt.metalBundleSha256!=previousMetalSha||receipt.openGlBundleSha256!=previousOpenGlSha)) throw new InvalidOperationException("Reused field bundles changed during build.");
        if(resident) receipt.scope="Same-player resident material reference/packet experiment; captured bindings and immutable assets, not stock shader or gameplay equivalence.";
        File.WriteAllText(Path.Combine(root,"build.json"),JsonUtility.ToJson(receipt,true));
    }
}
