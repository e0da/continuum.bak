using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace KspContinuum.Reconstruction;

public static class ResearchAssembly
{
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(Types(t.NestedTypes)));
    static string Digest(IEnumerable<string> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows.Order(StringComparer.Ordinal))))).ToLowerInvariant();
    public static string Api(ModuleDefinition module) => Digest(Types(module.Types).SelectMany(t =>
        new[] { $"T:{t.FullName}:{t.Attributes}:{t.BaseType}" }
        .Concat(t.Interfaces.Select(i => $"I:{t.FullName}:{i.InterfaceType}"))
        .Concat(t.Fields.Select(f => $"F:{f.FullName}:{f.Attributes}:{f.Constant}"))
        .Concat(t.Methods.Select(m => $"M:{m.FullName}:{m.Attributes}:{m.ImplAttributes}"))
        .Concat(t.Properties.Select(p => $"P:{p.FullName}:{p.GetMethod}:{p.SetMethod}"))
        .Concat(t.Events.Select(e => $"E:{e.FullName}:{e.AddMethod}:{e.RemoveMethod}"))));
    public static string Bodies(ModuleDefinition module, string? excluding = null) => Digest(Types(module.Types).SelectMany(t => t.Methods)
        .Where(m => m.FullName != excluding).Select(m =>
    {
        if (!m.HasBody) return m.FullName + ":extern:" + m.ImplAttributes;
        var instructions = m.Body.Instructions;
        string Operand(object? value) => value switch {
            Instruction i => "@" + instructions.IndexOf(i),
            Instruction[] targets => string.Join(",", targets.Select(i => "@" + instructions.IndexOf(i))),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
        return m.FullName + ":" + m.Body.InitLocals + ":" + string.Join(";", m.Body.Variables.Select(v => v.VariableType.FullName)) + ":" +
            string.Join(";", instructions.Select(i => i.OpCode.Code + "=" + Operand(i.Operand))) + ":" +
            string.Join(";", m.Body.ExceptionHandlers.Select(e => $"{e.HandlerType}:{e.CatchType}:{Operand(e.TryStart)}:{Operand(e.TryEnd)}:{Operand(e.HandlerStart)}:{Operand(e.HandlerEnd)}:{Operand(e.FilterStart)}"));
    }));
    static string Resources(ModuleDefinition module) => Digest(module.Resources.Select(r =>
        $"{r.Name}:{r.Attributes}:{r.ResourceType}:" + (r is EmbeddedResource embedded ?
            Convert.ToHexString(SHA256.HashData(embedded.GetResourceData())) : r.ToString())));
    static string References(ModuleDefinition module) => Digest(module.AssemblyReferences.Select(r => r.FullName));
    static AssemblyDefinition Read(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
    }
    public static object Inspect(string path)
    {
        using var assembly = Read(path);
        var module = assembly.MainModule;
        var methods = Types(module.Types).SelectMany(t => t.Methods).ToArray();
        return new { schema = "continuum-managed-index/v1", sha256 = Hash(path), assembly = assembly.Name.FullName,
            mvid = module.Mvid, runtime = module.RuntimeVersion, apiSha256 = Api(module), ilSha256 = Bodies(module),
            references = module.AssemblyReferences.Select(r => r.FullName).ToArray(),
            resourcesSha256 = Resources(module),
            resources = module.Resources.Select(r => new { r.Name, kind = r.ResourceType.ToString() }).ToArray(),
            methods = methods.Select(m => new { identity = m.FullName, token = m.MetadataToken.ToUInt32(), hasIL = m.HasBody,
                il = m.HasBody ? m.Body.Instructions.Select(i => i.ToString()).ToArray() : null,
                implementation = m.ImplAttributes.ToString(), pinvoke = m.HasPInvokeInfo ? m.PInvokeInfo.Module.Name + ":" + m.PInvokeInfo.EntryPoint : null }).ToArray(),
            qualification = "indexed-not-runtime-qualified" };
    }
    public static object Rewrite(string source, string expectedHash, string output, string? method = null, string? suffix = null)
    {
        if (Hash(source) != expectedHash) throw new ArgumentException("source hash differs from the frozen input");
        if (File.Exists(output)) throw new ArgumentException("output already exists");
        using var assembly = Read(source);
        if (assembly.Name.HasPublicKey) throw new ArgumentException("signed assembly requires an explicit signing contract");
        var beforeApi = Api(assembly.MainModule);
        var beforeBodies = Bodies(assembly.MainModule);
        var beforeResources = Resources(assembly.MainModule);
        var beforeReferences = References(assembly.MainModule);
        var beforeUnchangedMethods = beforeBodies;
        string? excludedBody = null;
        if (method != null)
        {
            if (string.IsNullOrEmpty(suffix)) throw new ArgumentException("probe suffix is empty");
            var candidates = Types(assembly.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.DeclaringType.FullName + "." + m.Name == method &&
                m.IsStatic && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.String" && m.HasBody).ToArray();
            if (candidates.Length != 1) throw new ArgumentException("probe requires one static parameterless string method with IL");
            var target = candidates[0];
            excludedBody = target.FullName;
            beforeUnchangedMethods = Bodies(assembly.MainModule, excludedBody);
            if (target.Body.ExceptionHandlers.Count != 0)
                throw new ArgumentException("probe methods with exception handlers require a separate control-flow proof");
            var concat = new MethodReference("Concat", assembly.MainModule.TypeSystem.String, assembly.MainModule.TypeSystem.String) { HasThis = false };
            concat.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
            concat.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
            foreach (var ret in target.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                var first = Instruction.Create(OpCodes.Ldstr, suffix);
                var processor = target.Body.GetILProcessor();
                processor.InsertBefore(ret, first); processor.InsertBefore(ret, Instruction.Create(OpCodes.Call, concat));
                foreach (var i in target.Body.Instructions)
                {
                    if (ReferenceEquals(i.Operand, ret)) i.Operand = first;
                    if (i.Operand is Instruction[] targets) for (var n = 0; n < targets.Length; n++) if (ReferenceEquals(targets[n], ret)) targets[n] = first;
                }
            }
            target.Body.MaxStackSize = Math.Max(2, target.Body.MaxStackSize + 1);
        }
        using var stream = new MemoryStream(); assembly.Write(stream);
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(stream.ToArray()));
        if (candidate.Name.FullName != assembly.Name.FullName || Api(candidate.MainModule) != beforeApi)
            throw new InvalidOperationException("rewritten assembly changed its identity or API");
        if (Resources(candidate.MainModule) != beforeResources || References(candidate.MainModule) != beforeReferences)
            throw new InvalidOperationException("rewritten assembly changed resources or dependencies");
        if (method == null && Bodies(candidate.MainModule) != beforeBodies)
            throw new InvalidOperationException("unchanged rewrite changed normalized IL");
        if (method != null && (Bodies(candidate.MainModule, excludedBody) != beforeUnchangedMethods || Bodies(candidate.MainModule) == beforeBodies))
            throw new InvalidOperationException("probe must change its target and preserve all other method bodies");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) stream.WriteTo(file);
        return new { schema = "continuum-recomposition/v1", sourceSha256 = expectedHash, candidateSha256 = Hash(output),
            assembly = candidate.Name.FullName, apiSha256 = beforeApi, unchangedIL = method == null, changedMethod = method,
            resourcesSha256 = beforeResources, referencesSha256 = beforeReferences,
            qualification = "structure-verified-runtime-unqualified" };
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            object result = args switch {
                ["inspect", var input] => ResearchAssembly.Inspect(input),
                ["rewrite", var input, var hash, var output] => ResearchAssembly.Rewrite(input, hash, output),
                ["probe", var input, var hash, var output, var method, var suffix] => ResearchAssembly.Rewrite(input, hash, output, method, suffix),
                _ => throw new ArgumentException("usage: inspect ASSEMBLY | rewrite ASSEMBLY SHA256 NEW_DLL | probe ASSEMBLY SHA256 NEW_DLL TYPE.METHOD SUFFIX")
            };
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or BadImageFormatException or InvalidOperationException or NotSupportedException)
        { Console.Error.WriteLine("reconstruction: " + error.Message); return 1; }
    }
}
