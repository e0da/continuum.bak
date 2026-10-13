using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

public static class BuildOwnedRuntimeWorkbench
{
    [Serializable] sealed class Source {public string file,sha256;}
    [Serializable] sealed class Receipt
    {
        public string schema="continuum-owned-runtime-build/v1",unity,output,playerAssemblySha256;
        public string qualification="Matching-host player compiled; original captured craft, controls, schedule reload, topology and checkpoint continuation require actual runtime evidence.";
        public Source[] sources;
    }
    static string Hash(string path) {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    public static void Build()
    {
        string output=Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_PLAYER");
        if(string.IsNullOrEmpty(output)||!File.Exists("Assets/OwnedRuntimeWorkbench.cs"))throw new InvalidOperationException("Owned runtime host/source missing.");
        var rows=new List<Source>();
        foreach(string source in Directory.GetFiles("Assets","*.cs",SearchOption.AllDirectories))rows.Add(new Source {file=source,sha256=Hash(source)});
        rows.Add(new Source {file="Assets/FixtureOpaque.shader",sha256=Hash("Assets/FixtureOpaque.shader")});
        rows.Sort((a,b)=>string.CompareOrdinal(a.file,b.file));
        BuildAeroFieldWorkbench.Build();
        string player=Path.Combine(output,"Contents/Resources/Data/Managed/Assembly-CSharp.dll");
        File.WriteAllText(output+".owned-build.json",JsonUtility.ToJson(new Receipt {unity=Application.unityVersion,output=output,playerAssemblySha256=Hash(player),sources=rows.ToArray()},true));
    }
}
