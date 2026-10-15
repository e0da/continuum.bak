using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class AeroFieldHotReload
{
    [Serializable] sealed class Publication { public string schema="continuum-aero-shader-publication/v1",generation,bundle,bundleSha256,sourceSha256; }
    [Serializable] sealed class Failure { public string schema="continuum-aero-shader-cook-error/v1",sourceSha256,error; }
    static string root,lastAttempt,observed; static double nextCheck,changedAt; static bool busy;
    static AeroFieldHotReload()
    {
        root=Environment.GetEnvironmentVariable("CONTINUUM_AERO_COOK_ROOT");
        if(!string.IsNullOrEmpty(root)) EditorApplication.update+=Tick;
    }
    static string Hash(string path) { using(var stream=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant(); }
    static string[] SourceFiles()
    {
        return Directory.GetFiles("Assets/ContinuumAeroField","*",SearchOption.AllDirectories).Where(path=>path.EndsWith(".shader",StringComparison.Ordinal)||path.EndsWith(".cginc",StringComparison.Ordinal)||path.EndsWith(".hlsl",StringComparison.Ordinal)).OrderBy(path=>path,StringComparer.Ordinal).ToArray();
    }
    static string Sources()
    {
        var rows=SourceFiles().Select(path=>path+"\t"+Hash(path));
        using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",rows)))).Replace("-","").ToLowerInvariant();
    }
    static void Atomic(string path,string text)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp"; File.WriteAllText(temp,text);
        if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
    }
    static void Tick()
    {
        if(busy||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.timeSinceStartup<nextCheck) return;
        nextCheck=EditorApplication.timeSinceStartup+.5;
        try
        {
            if(!Path.IsPathRooted(root)) throw new InvalidOperationException("Cook root must be absolute and private.");
            string source=Sources();
            if(source!=observed) { observed=source; changedAt=EditorApplication.timeSinceStartup; return; }
            if(source==lastAttempt||EditorApplication.timeSinceStartup-changedAt<.75) return;
            lastAttempt=source; busy=true;
            if(!Directory.Exists(root)) Directory.CreateDirectory(root);
            string owner=Path.Combine(root,"continuum-aero-cook.local.json");
            if(!File.Exists(owner))
            {
                if(Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("New or owned private cook root required.");
                File.WriteAllText(owner,"{\"schema\":\"continuum-aero-cook-owner/v1\",\"scope\":\"private-shader-generations\"}");
            }
            else if(!File.ReadAllText(owner).Contains("\"private-shader-generations\"")) throw new InvalidOperationException("Cook ownership differs.");
            string generation=DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+"-"+Guid.NewGuid().ToString("N"); string directory=Path.Combine(root,generation); Directory.CreateDirectory(directory);
            string previousOutput=Environment.GetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT");
            string previousSource=Environment.GetEnvironmentVariable("CONTINUUM_SHADER_SOURCE_ROOT");
            string stage="Assets/ContinuumAeroCookGenerated/"+generation;
            Publication publication=null;
            try
            {
                if(Directory.Exists(stage)||File.Exists(stage)||File.Exists(stage+".meta")) throw new InvalidOperationException("Generated shader identity already exists.");
                Directory.CreateDirectory(stage);
                foreach(string original in SourceFiles())
                {
                    // Fresh imports quarantine Unity's shader-native diagnostics by asset GUID.
                    string relative=original.Substring("Assets/ContinuumAeroField".Length+1);
                    string target=Path.Combine(stage,relative); Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string before=Hash(original); File.Copy(original,target,false);
                    if(Hash(original)!=before||Hash(target)!=before) throw new InvalidOperationException("Source changed while staging shader generation.");
                }
                if(Sources()!=source) throw new InvalidOperationException("Source changed while staging; await next complete generation.");
                Environment.SetEnvironmentVariable("CONTINUUM_SHADER_GRAPHICS_API","OpenGLCore"); Environment.SetEnvironmentVariable("CONTINUUM_SHADER_SOURCE_ROOT",stage);
                string bundleName="continuum-aero-field-"+generation.ToLowerInvariant();
                Environment.SetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_NAME",bundleName); Environment.SetEnvironmentVariable("CONTINUUM_SHADER_REQUIRE_INSTANCING","1"); Environment.SetEnvironmentVariable("CONTINUUM_SHADER_SELECTIVE_INSTANCING","1"); Environment.SetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT",directory);
                BuildShaderBundle.Build();
                if(Sources()!=source) throw new InvalidOperationException("Source changed during cooking; await next complete generation.");
                string bundle=Path.Combine(directory,bundleName);
                publication=new Publication {generation=generation,bundle=bundle,bundleSha256=Hash(bundle),sourceSha256=source};
            }
            finally
            {
                Environment.SetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT",previousOutput);
                Environment.SetEnvironmentVariable("CONTINUUM_SHADER_SOURCE_ROOT",previousSource);
                AssetDatabase.DeleteAsset(stage);
                if(Directory.Exists(stage)) FileUtil.DeleteFileOrDirectory(stage);
                if(File.Exists(stage+".meta")) FileUtil.DeleteFileOrDirectory(stage+".meta");
                if(Directory.Exists(stage)||File.Exists(stage+".meta")) throw new InvalidOperationException("Generated shader source cleanup incomplete.");
            }
            if(Sources()!=source) throw new InvalidOperationException("Source changed before publication; await next complete generation.");
            Atomic(Path.Combine(root,"current.json"),JsonUtility.ToJson(publication,true));
            Debug.Log("[Continuum Aero HMR] published "+generation);
        }
        catch(Exception error)
        {
            Debug.LogError("[Continuum Aero HMR] retaining last good generation: "+error.Message);
            if(!string.IsNullOrEmpty(root)&&Path.IsPathRooted(root)&&Directory.Exists(root))
                try { Atomic(Path.Combine(root,"last-error.json"),JsonUtility.ToJson(new Failure {sourceSha256=observed,error=error.ToString()},true)); } catch(Exception writeError) { Debug.LogException(writeError); }
        }
        finally { busy=false; }
    }
}
