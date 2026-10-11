using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using KspContinuum;
class Program
{
    sealed class Member { public bool Enabled; public int Value; }
    static string Sha(byte[] bytes) { return BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant(); }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect") { Inspect(args[1]); return; }
        if (args.Length == 4 && args[0] == "--il-proof") { IlProof.Run(args[1], args[2], args[3]); return; }
        var part = new object(); var list = new object(); var owner = new object(); var parts = new object();
        var a = new Member { Enabled = true, Value = 10 }; var b = new Member { Enabled = false, Value = 20 };
        var row = new OrderedProviderRow(part, list, new object[] { a, b });
        var plan = new OrderedProviderPlan(1, owner, parts, new[] { row });
        Require(row.MatchesMember(0, a) && row.MatchesMember(1, b), "Ordered classifications differ.");
        a.Enabled = false; a.Value = 99; b.Enabled = true;
        Require(row.MatchesMember(0, a) && !a.Enabled && b.Enabled && a.Value == 99, "Live changes recaptured/cached improperly.");
        Require(!row.MatchesMember(0, b) && !row.MatchesMember(1, new Member()), "Reorder/replacement accepted stale masks.");
        Require(!row.Matches(part, list, 3) && !row.Matches(part, new object(), 2), "Collection count/identity drift accepted.");
        Require(!plan.Matches(new object(), parts, 1) && !plan.Matches(owner, parts, 2), "Owner/topology drift accepted.");
        var replacement = new OrderedProviderPlan(2, owner, parts, new[] { row });
        Require(plan.Generation != replacement.Generation && plan.At(-1) == null && plan.At(1) == null, "Generation/bounds evidence lost.");
        var input = new object[] { a }; var immutable = new OrderedProviderRow(part, list, input); input[0] = b;
        Require(immutable.MatchesMember(0, a) && !immutable.MatchesMember(0, b), "Caller mutated frozen membership.");
        Console.WriteLine("Ordered provider counterexamples passed: live values, reorder/replacement, collection/owner drift, generation/bounds, immutable membership.");
    }
    static void Inspect(string path)
    {
        using (var stream = File.OpenRead(path)) using (var pe = new PEReader(stream))
        {
            var md = pe.GetMetadataReader(); Console.WriteLine("assemblySha=" + Sha(File.ReadAllBytes(path))); Console.WriteLine("mvid=" + md.GetGuid(md.GetModuleDefinition().Mvid));
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th); if (md.GetString(t.Name) != "VesselState" || md.GetString(t.Namespace) != "MuMech") continue;
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh); if (md.GetString(m.Name) != "AnalyzeParts") continue;
                    byte[] bytes = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); Console.WriteLine("token=" + System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh)); Console.WriteLine("length=" + bytes.Length + " ilSha=" + Sha(bytes));
                }
            }
        }
    }
}
