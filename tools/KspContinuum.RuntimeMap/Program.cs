using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using FieldDefinition = Mono.Cecil.FieldDefinition;

namespace KspContinuum.RuntimeMap;

sealed class InstalledResolver : IAssemblyResolver
{
    readonly Dictionary<string,List<AssemblyDefinition>> assemblies = new(StringComparer.Ordinal);
    public void Add(AssemblyDefinition assembly)
    {
        if (!assemblies.TryGetValue(assembly.Name.FullName,out var rows)) assemblies.Add(assembly.Name.FullName,rows=new());
        rows.Add(assembly);
    }
    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name,new ReaderParameters());
    public AssemblyDefinition Resolve(AssemblyNameReference name,ReaderParameters parameters)
    {
        if (!assemblies.TryGetValue(name.FullName,out var rows) || rows.Count!=1) throw new AssemblyResolutionException(name);
        return rows[0];
    }
    public object Reference(AssemblyNameReference name)
    {
        assemblies.TryGetValue(name.FullName,out var rows);
        return new { identity=name.FullName,status=rows==null?"missing":rows.Count==1?"resolved":"ambiguous",candidateMvids=rows?.Select(a=>a.MainModule.Mvid.ToString("D")).ToArray()??Array.Empty<string>() };
    }
    public void Dispose() { }
}

sealed class Ledger : IDisposable
{
    readonly StreamWriter writer;
    public string Name {get;}
    public long Count {get;private set;}
    public Ledger(string directory,string name) { Name=name;writer=new StreamWriter(new FileStream(Path.Combine(directory,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)); }
    public void Write(object row) { writer.WriteLine(JsonSerializer.Serialize(row));Count++; }
    public void Dispose()=>writer.Dispose();
}

static class Program
{
    sealed class Counts
    {
        public long assemblies, types, methods, ilMethods, unityMessages, stateMachines, newobj, newarr, box, backwardBranches, staticReads, staticWrites;
        public Dictionary<string,long> unityMessageNames=new(StringComparer.Ordinal);
    }
    static readonly Dictionary<ModuleDefinition,string> ModuleHashes=new();
    static readonly HashSet<string> UnityMessages=new(StringComparer.Ordinal) {"Awake","Start","Update","FixedUpdate","LateUpdate","OnGUI","OnEnable","OnDisable","OnDestroy","OnApplicationQuit","OnApplicationPause","OnApplicationFocus","OnPreCull","OnPreRender","OnPostRender","OnRenderImage","OnCollisionEnter","OnCollisionStay","OnCollisionExit","OnTriggerEnter","OnTriggerStay","OnTriggerExit","OnTransformParentChanged","OnTransformChildrenChanged","OnValidate","Reset"};
    static string Hash(string path) {using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)=>roots.SelectMany(t=>new[]{t}.Concat(Types(t.NestedTypes)));
    static string Id(IMetadataTokenProvider definition,ModuleDefinition module)=>ModuleHashes[module]+"/"+module.Mvid.ToString("D")+"/"+definition.MetadataToken.ToUInt32().ToString("x8");
    static string[] Attributes(ICustomAttributeProvider provider)=>provider.HasCustomAttributes?provider.CustomAttributes.Select(a=>a.AttributeType.FullName).ToArray():Array.Empty<string>();
    static bool SerializationName(string name)=>name.Contains("Persistent",StringComparison.Ordinal)||name.Contains("Serialize",StringComparison.Ordinal)||name.Contains("DataMember",StringComparison.Ordinal)||name.Contains("Json",StringComparison.Ordinal)||name.Contains("Xml",StringComparison.Ordinal)||name.Contains("KSPField",StringComparison.Ordinal);
    static string OwnerScope(string relative)=>relative.StartsWith("GameData/",StringComparison.Ordinal)?relative.StartsWith("GameData/SquadExpansion/",StringComparison.Ordinal)?"dlc-plugin":relative.StartsWith("GameData/Squad/",StringComparison.Ordinal)?"stock-plugin":"mod-plugin":relative.StartsWith("KSPLauncher.app/",StringComparison.Ordinal)?"launcher-not-game-runtime":relative.Contains("/Managed/",StringComparison.Ordinal)?"game-managed-runtime":"native-runtime-or-plugin";
    static string ResolutionError(Exception error)=>error.GetType().Name+": "+error.Message;
    static string Group(string name,string path)=>path.StartsWith("GameData/",StringComparison.Ordinal)?path.StartsWith("GameData/SquadExpansion/",StringComparison.Ordinal)?"dlc":"mod-or-stock-plugin":path.StartsWith("KSPLauncher.app/",StringComparison.Ordinal)?"launcher":name.StartsWith("Unity",StringComparison.Ordinal)?"engine-managed":name.StartsWith("Assembly-CSharp",StringComparison.Ordinal)||name.StartsWith("KSP",StringComparison.Ordinal)?"game":name=="mscorlib"||name=="netstandard"||name.StartsWith("System",StringComparison.Ordinal)||name.StartsWith("Mono",StringComparison.Ordinal)||name.StartsWith("I18N",StringComparison.Ordinal)?"framework":"other-managed";
    static bool UnityComponent(TypeDefinition type,out string? unresolved)
    {
        unresolved=null;TypeReference? current=type;var seen=new HashSet<string>();
        while(current!=null&&seen.Add(current.FullName)&&seen.Count<=128)
        {
            if(current.FullName is "UnityEngine.MonoBehaviour" or "UnityEngine.Component")return true;
            try {current=current.Resolve()?.BaseType;}catch(Exception e) when(e is AssemblyResolutionException or ResolutionException){unresolved=ResolutionError(e);return false;}
        }
        return false;
    }
    static void Require(bool value,string reason){if(!value)throw new InvalidDataException(reason);}
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length==2&&args[0]=="self-check-paths") {PathAdmissionChecks.Run(Path.GetFullPath(args[1]));return 0;}
            Require(args.Length==3&&args[0]=="map","usage: map INSTALLED_ROOT NEW_PRIVATE_OUTPUT | self-check-paths NEW_PRIVATE_FIXTURE");
            Map(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static void Map(string root,string output)
    {
        ValidatePaths(root,output);
        Directory.CreateDirectory(output);var timer=Stopwatch.StartNew();
        var sources=new List<(string path,string sha)>();var modules=new List<(string path,string sha,AssemblyDefinition assembly)>();var canonical=new Dictionary<string,AssemblyDefinition>();
        using var gameResolver=new InstalledResolver();using var launcherResolver=new InstalledResolver();var errors=new List<object>();
        string[] names={"assemblies.ndjson","types.ndjson","methods.ndjson","fields.ndjson","call-edges.ndjson","field-accesses.ndjson","native-binaries.ndjson","errors.ndjson"};
        var ledgers=names.ToDictionary(n=>n,n=>new Ledger(output,n));
        var groups=new Dictionary<string,Counts>(StringComparer.Ordinal);
        long ilMethods=0,unresolvedCalls=0,dispatchObligations=0,nativeMethods=0,reflectionCalls=0;int managedFiles=0,nativeFiles=0;
        try
        {
            string[] binaries=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(p=>Path.GetExtension(p).ToLowerInvariant() is ".dll" or ".dylib" or ".so" || Path.GetFileName(p) is "KSP" or "KSPLauncher" or "UnityPlayer").Order(StringComparer.Ordinal).ToArray();
            Require(binaries.Length<=4096,"Installed binary inventory exceeds4096.");
            foreach(string path in binaries)
            {
                string relative=Path.GetRelativePath(root,path).Replace('\\','/'),sha=Hash(path);sources.Add((path,sha));
                try
                {
                    using var file=File.OpenRead(path);byte[] prefix=new byte[4];Require(file.Read(prefix)==4,"Truncated binary.");file.Position=0;
                    if(prefix[0]=='M'&&prefix[1]=='Z')
                    {
                        using var pe=new PEReader(file,PEStreamOptions.LeaveOpen);
                        if(pe.HasMetadata)
                        {
                            managedFiles++;
                            bool launcher=relative.StartsWith("KSPLauncher.app/",StringComparison.Ordinal);
                            string canonicalKey=sha+(launcher?"/launcher":"/game");
                            if(canonical.TryGetValue(canonicalKey,out var duplicate))
                            {
                                ledgers["assemblies.ndjson"].Write(new {path=relative,sha256=sha,scope=OwnerScope(relative),status="byte-identical-copy",identity=duplicate.Name.FullName,mvid=duplicate.MainModule.Mvid,canonicalPath=modules.Single(m=>ReferenceEquals(m.assembly,duplicate)).path});continue;
                            }
                            InstalledResolver resolver=launcher?launcherResolver:gameResolver;
                            var assembly=AssemblyDefinition.ReadAssembly(path,new ReaderParameters {InMemory=true,AssemblyResolver=resolver,ReadSymbols=false});
                            canonical.Add(canonicalKey,assembly);modules.Add((relative,sha,assembly));resolver.Add(assembly);continue;
                        }
                        nativeFiles++;ledgers["native-binaries.ndjson"].Write(new {path=relative,sha256=sha,scope=OwnerScope(relative),format="native-PE",machine=pe.PEHeaders.CoffHeader.Machine.ToString(),sections=pe.PEHeaders.SectionHeaders.Select(s=>new {s.Name,s.VirtualAddress,s.VirtualSize,s.SizeOfRawData}).ToArray(),coverage="headers-only-no-native-callgraph"});continue;
                    }
                    nativeFiles++;ledgers["native-binaries.ndjson"].Write(new {path=relative,sha256=sha,scope=OwnerScope(relative),format="Mach-O-or-other",slices=MachSlices(path),coverage="load-commands-and-sections-only-no-native-callgraph"});
                }
                catch(Exception e) {var row=new {path=relative,stage="inventory",error=ResolutionError(e)};errors.Add(row);ledgers["errors.ndjson"].Write(row);}
            }
            foreach(var item in modules)ModuleHashes.Add(item.assembly.MainModule,item.sha);
            foreach(var item in modules)
            {
                var module=item.assembly.MainModule;
                string environment=item.path.StartsWith("KSPLauncher.app/",StringComparison.Ordinal)?"launcher":"game";
                InstalledResolver resolver=environment=="launcher"?launcherResolver:gameResolver;
                string group=Group(item.assembly.Name.Name,item.path);
                if(!groups.TryGetValue(group,out var counts))groups.Add(group,counts=new());counts.assemblies++;
                ledgers["assemblies.ndjson"].Write(new {path=item.path,sha256=item.sha,scope=OwnerScope(item.path),environment,status="indexed",identity=item.assembly.Name.FullName,mvid=module.Mvid,runtime=module.RuntimeVersion,architecture=module.Architecture.ToString(),module.Attributes,references=module.AssemblyReferences.Select(resolver.Reference).ToArray(),nativeReferences=module.ModuleReferences.Select(r=>r.Name).ToArray(),resources=module.Resources.Select(r=>new {r.Name,kind=r.ResourceType.ToString()}).ToArray()});
                using var peFile=File.OpenRead(Path.Combine(root,item.path));using var pe=new PEReader(peFile);
                foreach(var type in Types(module.Types))
                {
                    counts.types++;
                    string typeId=Id(type,module);string[] attrs=Attributes(type);bool unity=UnityComponent(type,out var baseError);
                    ledgers["types.ndjson"].Write(new {id=typeId,assemblySha256=item.sha,name=type.FullName,baseType=type.BaseType?.FullName,interfaces=type.Interfaces.Select(i=>i.InterfaceType.FullName).ToArray(),flags=type.Attributes.ToString(),attributes=attrs,serialization=attrs.Where(SerializationName).ToArray(),unityComponent=unity,baseResolutionError=baseError,genericParameters=type.GenericParameters.Select(p=>p.Name).ToArray()});
                    foreach(var field in type.Fields)
                        ledgers["fields.ndjson"].Write(new {id=Id(field,module),owner=typeId,field=field.FullName,flags=field.Attributes.ToString(),isStatic=field.IsStatic,isReadOnly=field.IsInitOnly,isLiteral=field.IsLiteral,type=field.FieldType.FullName,attributes=Attributes(field),serializationCandidate=!field.IsStatic&&!field.IsNotSerialized&&(field.IsPublic||Attributes(field).Any(SerializationName)),constant=field.HasConstant?Convert.ToString(field.Constant,CultureInfo.InvariantCulture):null});
                    foreach(var method in type.Methods)
                    {
                        try
                        {
                            string methodId=Id(method,module);string[] methodAttrs=Attributes(method);string? ilHash=null;
                            if(method.HasBody){ilMethods++;ilHash=Hash(pe.GetMethodBody((int)method.RVA).GetILBytes()??Array.Empty<byte>());}
                            if(!method.HasBody)nativeMethods++;
                            var instructions=method.HasBody?method.Body.Instructions.ToArray():Array.Empty<Instruction>();
                            var references=instructions.Select(i=>i.Operand).OfType<MethodReference>().ToArray();
                            counts.methods++;if(method.HasBody)counts.ilMethods++;
                            if(unity&&!method.IsStatic&&UnityMessages.Contains(method.Name))
                            {counts.unityMessages++;counts.unityMessageNames.TryGetValue(method.Name,out long callbacks);counts.unityMessageNames[method.Name]=callbacks+1;}
                            if(methodAttrs.Any(a=>a.EndsWith("StateMachineAttribute",StringComparison.Ordinal)))counts.stateMachines++;
                            counts.newobj+=instructions.Count(i=>i.OpCode.Code==Code.Newobj);counts.newarr+=instructions.Count(i=>i.OpCode.Code==Code.Newarr);counts.box+=instructions.Count(i=>i.OpCode.Code==Code.Box);
                            counts.backwardBranches+=instructions.Count(i=>i.Operand is Instruction target&&target.Offset<=i.Offset||i.Operand is Instruction[] targets&&targets.Any(t=>t.Offset<=i.Offset));
                            ledgers["methods.ndjson"].Write(new {id=methodId,owner=typeId,signature=method.FullName,flags=method.Attributes.ToString(),implementation=method.ImplAttributes.ToString(),method.HasBody,method.IsStatic,method.IsVirtual,method.IsFinal,attributes=methodAttrs,unityMessageCandidate=unity&&!method.IsStatic&&UnityMessages.Contains(method.Name),asyncOrIterator=methodAttrs.Any(a=>a.EndsWith("StateMachineAttribute",StringComparison.Ordinal)),stateMachineTypes=method.CustomAttributes.Where(a=>a.AttributeType.FullName.EndsWith("StateMachineAttribute",StringComparison.Ordinal)).SelectMany(a=>a.ConstructorArguments.Select(v=>v.Value is TypeReference t?t.FullName:v.Value?.ToString())).ToArray(),ilSha256=ilHash,instructions=instructions.Length,newobj=instructions.Count(i=>i.OpCode.Code==Code.Newobj),newarr=instructions.Count(i=>i.OpCode.Code==Code.Newarr),box=instructions.Count(i=>i.OpCode.Code==Code.Box),backwardBranchInstructions=instructions.Count(i=>i.Operand is Instruction target&&target.Offset<=i.Offset||i.Operand is Instruction[] targets&&targets.Any(t=>t.Offset<=i.Offset)),reflectionCalls=references.Count(r=>r.DeclaringType.FullName.StartsWith("System.Reflection.",StringComparison.Ordinal)||r.DeclaringType.FullName=="System.Activator"),serializationCalls=references.Count(r=>SerializationName(r.DeclaringType.FullName)||r.DeclaringType.FullName.Contains("ConfigNode",StringComparison.Ordinal)),ioCalls=references.Count(r=>r.DeclaringType.FullName.StartsWith("System.IO.",StringComparison.Ordinal)),engineCalls=references.Count(r=>r.DeclaringType.FullName.StartsWith("UnityEngine.",StringComparison.Ordinal)),exceptionRegions=method.HasBody?method.Body.ExceptionHandlers.Select(h=>new {kind=h.HandlerType.ToString(),catchType=h.CatchType?.FullName,tryStart=h.TryStart?.Offset,tryEnd=h.TryEnd?.Offset,handlerStart=h.HandlerStart?.Offset,handlerEnd=h.HandlerEnd?.Offset}).ToArray():null,pinvoke=method.HasPInvokeInfo?new {module=method.PInvokeInfo.Module.Name,entry=method.PInvokeInfo.EntryPoint,flags=method.PInvokeInfo.Attributes.ToString()}:null});
                            if(!method.HasBody)continue;
                            foreach(var instruction in method.Body.Instructions)
                            {
                                if(instruction.Operand is MethodReference reference && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn or Code.Jmp)
                                {
                                    MethodDefinition? target=null;string? reason=null;
                                    try {target=reference.Resolve();if(target==null)reason="method-definition-unresolved";else if(!ModuleHashes.ContainsKey(target.Module)){target=null;reason="resolved-outside-inventoried-module";}}
                                    catch(Exception e) when(e is AssemblyResolutionException or ResolutionException or NotSupportedException){reason=ResolutionError(e);}
                                    string dispatch=instruction.OpCode.Code==Code.Callvirt&&target?.IsVirtual==true&&!target.IsFinal?"virtual-runtime-target-unresolved":instruction.OpCode.Code==Code.Ldvirtftn?"virtual-delegate-runtime-target-unresolved":instruction.OpCode.Code is Code.Ldftn?"delegate-construction-runtime-invocation-unresolved":"declared-target";
                                    if(target==null)unresolvedCalls++;if(dispatch!="declared-target")dispatchObligations++;
                                    bool reflection=reference.DeclaringType.FullName.StartsWith("System.Reflection.",StringComparison.Ordinal)||reference.DeclaringType.FullName=="System.Activator"||reference.Name is "GetComponent" or "GetComponents" or "SendMessage" or "BroadcastMessage";
                                    if(reflection)reflectionCalls++;
                                    ledgers["call-edges.ndjson"].Write(new {caller=methodId,offset=instruction.Offset,opcode=instruction.OpCode.Name,declared=reference.FullName,scope=reference.DeclaringType.Scope?.ToString(),target=target==null?null:Id(target,target.Module),targetAssemblySha256=target==null?null:ModuleHashes[target.Module],status=target==null?"unresolved":"declared-target-resolved",reason,dispatch,reflectionOrUnityLookup=reflection});
                                }
                                else if(instruction.OpCode.Code==Code.Calli)
                                {unresolvedCalls++;dispatchObligations++;ledgers["call-edges.ndjson"].Write(new {caller=methodId,offset=instruction.Offset,opcode="calli",declared=instruction.Operand?.ToString(),status="unresolved",reason="indirect-function-pointer-runtime-target"});}
                                if(instruction.Operand is FieldReference fieldRef && instruction.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Stfld or Code.Ldsfld or Code.Ldsflda or Code.Stsfld)
                                {
                                    if(instruction.OpCode.Code is Code.Ldsfld or Code.Ldsflda)counts.staticReads++;
                                    if(instruction.OpCode.Code==Code.Stsfld)counts.staticWrites++;
                                    FieldDefinition? field=null;string? reason=null;
                                    try {field=fieldRef.Resolve();if(field==null)reason="field-definition-unresolved";else if(!ModuleHashes.ContainsKey(field.Module)){field=null;reason="field-outside-inventory";}}
                                    catch(Exception e) when(e is AssemblyResolutionException or ResolutionException or NotSupportedException){reason=ResolutionError(e);}
                                    ledgers["field-accesses.ndjson"].Write(new {method=methodId,offset=instruction.Offset,opcode=instruction.OpCode.Name,declared=fieldRef.FullName,target=field==null?null:Id(field,field.Module),isStatic=field?.IsStatic,write=instruction.OpCode.Code is Code.Stfld or Code.Stsfld,addressEscape=instruction.OpCode.Code is Code.Ldflda or Code.Ldsflda,reason});
                                }
                            }
                        }
                        catch(Exception e){var row=new {path=item.path,stage="method",method=method.FullName,error=ResolutionError(e)};errors.Add(row);ledgers["errors.ndjson"].Write(row);}
                    }
                }
            }
        }
        finally {foreach(var ledger in ledgers.Values)ledger.Dispose();foreach(var item in modules)item.assembly.Dispose();}
        bool unchanged=sources.All(s=>File.Exists(s.path)&&Hash(s.path)==s.sha);
        using(var file=new FileStream(Path.Combine(output,"overview.json"),FileMode.CreateNew,FileAccess.Write))JsonSerializer.Serialize(file,groups,new JsonSerializerOptions {WriteIndented=true,IncludeFields=true});
        var artifacts=ledgers.Values.Select(l=>new {file=l.Name,rows=l.Count,sha256=Hash(Path.Combine(output,l.Name)),bytes=new FileInfo(Path.Combine(output,l.Name)).Length}).ToArray();
        var summary=new {schema="continuum-runtime-map/v1",status=errors.Count==0&&unchanged?"completed":"incomplete",installedRoot=root,utc=DateTime.UtcNow.ToString("o"),toolAssemblySha256=Hash(typeof(Program).Assembly.Location),cecilVersion=typeof(AssemblyDefinition).Assembly.GetName().Version?.ToString(),managedFiles,canonicalManagedAssemblies=modules.Count,nativeFiles,ilMethods,methodsWithoutIL=nativeMethods,unresolvedCalls,runtimeDispatchObligations=dispatchObligations,reflectionOrUnityLookupCalls=reflectionCalls,errors=errors.Count,sourceUnchanged=unchanged,wallSeconds=timer.Elapsed.TotalSeconds,overviewSha256=Hash(Path.Combine(output,"overview.json")),artifacts,limits=new[]{"Metadata/IL declaration graph, not live reachability or measured cost.","Instruction counts are structural evidence, not executed counts, heap-allocation proof or timings.","Virtual/interface/delegate/reflection/calli targets require runtime/provider evidence.","Unity-message and serialization labels are metadata candidates, not runtime invocation/persistence proof.","Static field accesses expose declared readers/writers; native/reflection/unmanaged mutation and alias ownership remain unresolved.","Native headers/load commands do not recover Unity/PhysX/Mono implementation or licensed build sources.","Byte-identical launcher/runtime copies are canonicalized but every installed path is retained.","No installed binaries executed, modified, recompiled or decompiled by this tool."}};
        using(var file=new FileStream(Path.Combine(output,"manifest.json"),FileMode.CreateNew,FileAccess.Write))JsonSerializer.Serialize(file,summary,new JsonSerializerOptions {WriteIndented=true});
        Console.WriteLine(JsonSerializer.Serialize(summary));Require(errors.Count==0&&unchanged,"Map incomplete; private manifest retains failures.");
    }

    internal static void ValidatePaths(string root,string output)
    {
        RejectReparseAncestors(root);RejectReparseAncestors(output);
        Require(Directory.Exists(root)&&!Directory.Exists(output)&&!File.Exists(output),"Installed root must exist and output must be new.");
        Require(!output.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&output!=root,"Output cannot mutate the installed input tree.");
    }
    static void RejectReparseAncestors(string path)
    {
        for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
        {
            var directory=new DirectoryInfo(current);var file=new FileInfo(current);
            Require(directory.LinkTarget==null&&file.LinkTarget==null,"Input/output ancestor is a symbolic link: "+current);
            if(directory.Exists)Require((directory.Attributes&FileAttributes.ReparsePoint)==0,"Input/output ancestor is a reparse point: "+current);
            else if(file.Exists)Require((file.Attributes&FileAttributes.ReparsePoint)==0,"Input/output ancestor is a reparse point: "+current);
        }
    }

    static object[] MachSlices(string path)
    {
        using var file=File.OpenRead(path);byte[] header=new byte[32];Require(file.Read(header)==32,"Native header truncated.");
        uint magic=BinaryPrimitives.ReadUInt32BigEndian(header);var slices=new List<object>();
        if(magic is 0xcafebabe or 0xcafebabf)
        {
            uint count=BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));Require(count<=32,"Fat architecture count bound.");
            int width=magic==0xcafebabe?20:32;file.Position=8;byte[] table=new byte[checked((int)count*width)];file.ReadExactly(table);
            for(int i=0;i<count;i++){var entry=table.AsSpan(i*width,width);ulong offset=width==20?BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(8)):BinaryPrimitives.ReadUInt64BigEndian(entry.Slice(8));slices.Add(MachSlice(file,checked((long)offset)));}
        }
        else if(magic is 0xcffaedfe or 0xcefaedfe or 0xfeedfacf or 0xfeedface)slices.Add(MachSlice(file,0));
        else slices.Add(new {format="unrecognized-native-container",magic=magic.ToString("x8")});
        return slices.ToArray();
    }
    static object MachSlice(FileStream file,long offset)
    {
        Require(offset>=0&&offset<=file.Length-32,"Mach slice offset bound.");file.Position=offset;byte[] header=new byte[32];file.ReadExactly(header);
        uint magic=BinaryPrimitives.ReadUInt32BigEndian(header);bool little=magic is 0xcffaedfe or 0xcefaedfe;bool wide=magic is 0xcffaedfe or 0xfeedfacf;
        uint Read(byte[] bytes,int position)=>little?BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)):BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
        uint cpu=Read(header,4),commands=Read(header,16),bytes=Read(header,20);Require(commands<=8192&&bytes<=16*1024*1024,"Mach commands bound.");
        file.Position=offset+(wide?32:28);byte[] data=new byte[(int)bytes];file.ReadExactly(data);var dependencies=new List<string>();var segments=new List<object>();string? uuid=null;int cursor=0;
        for(uint n=0;n<commands;n++)
        {
            Require(cursor<=data.Length-8,"Mach load command truncated.");uint command=Read(data,cursor),size=Read(data,cursor+4);Require(size>=8&&size<=data.Length-cursor,"Mach load command size bound.");
            if(command==0x1b&&size>=24)uuid=Convert.ToHexString(data.AsSpan(cursor+8,16)).ToLowerInvariant();
            if((command&0x7fffffff) is 0xc or 0xd or 0x18 or 0x1f or 0x23 && size>=24)
            {int start=checked(cursor+(int)Read(data,cursor+8));Require(start>=cursor&&start<cursor+size,"Dylib command name bound.");int end=start;while(end<cursor+size&&data[end]!=0)end++;dependencies.Add(Encoding.UTF8.GetString(data,start,end-start));}
            if(command is 0x1 or 0x19 && size>=(wide?72:56))
            {string name=Encoding.ASCII.GetString(data,cursor+8,16).TrimEnd('\0');uint sections=Read(data,cursor+(wide?64:48));segments.Add(new {name,sections});}
            cursor+=checked((int)size);
        }
        return new {offset,cpu=cpu==0x01000007?"x86_64":cpu==0x0100000c?"arm64":cpu.ToString("x8"),uuid,loadCommands=commands,dependencies,segments};
    }
}
