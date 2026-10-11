using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length == 7 && args[0] == "transplant") return Transplant(args);
if (args.Length == 4 && args[0] == "inspect-player") return InspectPlayer(args);

if (!(args.Length == 3 && args[0] == "inspect") &&
    !(args.Length == 6 && (args[0] == "graft" || args[0] == "graft-list")))
{
    Console.Error.WriteLine("Usage: inspect <classdata.tpk> <assets-or-bundle> | inspect-player <classdata.tpk> <player-Data-directory> <new-report.json> | graft <classdata.tpk> <target-assets> <donor-bundle> <output-assets> <shader-name> | graft-list <classdata.tpk> <target-assets> <donor-bundle> <output-assets> <names-file> | transplant <classdata.tpk> <original-assets> <original-shader-name> <donor-bundle> <donor-shader-name> <new-output-bundle>");
    return 2;
}

var manager = new AssetsManager();
manager.LoadClassPackage(args[1]);

if (args[0] == "inspect")
{
    var path = args[2];
    var file = IsBundle(path)
        ? manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(path, true), 0, false)
        : manager.LoadAssetsFile(path, false);
    manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
    Console.WriteLine($"Unity {file.file.Metadata.UnityVersion}");
    foreach (var asset in file.file.GetAssetsOfType(AssetClassID.Shader))
    {
        var field = manager.GetBaseField(file, asset);
        Console.WriteLine($"{Name(field)} pathId={asset.PathId} platforms=[{string.Join(",", Platforms(field))}]");
    }
    return 0;
}

var targetPath = args[2];
var bundlePath = args[3];
var outputPath = args[4];
var names = args[0] == "graft"
    ? [args[5]]
    : File.ReadAllLines(args[5]).Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
    throw new InvalidOperationException("Expected at least one distinct shader name");
if (Path.GetFullPath(targetPath) == Path.GetFullPath(outputPath) || File.Exists(outputPath))
    throw new InvalidOperationException("Output must be a new file, separate from the source");

var target = manager.LoadAssetsFile(targetPath, false);
var donor = manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(bundlePath, true), 0, false);
if (target.file.Metadata.UnityVersion != donor.file.Metadata.UnityVersion)
    throw new InvalidOperationException("Target and donor Unity asset versions differ");
manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);

var targetShaders = target.file.GetAssetsOfType(AssetClassID.Shader)
    .GroupBy(asset => Name(manager.GetBaseField(target, asset)), StringComparer.Ordinal)
    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
var donorShaders = donor.file.GetAssetsOfType(AssetClassID.Shader)
    .GroupBy(asset => Name(manager.GetBaseField(donor, asset)), StringComparer.Ordinal)
    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
var replacements = new List<(AssetFileInfo targetAsset, AssetTypeValueField donorField)>();
foreach (var shaderName in names)
{
    if (!targetShaders.TryGetValue(shaderName, out var targetMatches) || targetMatches.Length != 1 ||
        !donorShaders.TryGetValue(shaderName, out var donorMatches) || donorMatches.Length != 1)
        throw new InvalidOperationException($"Expected exactly one target and donor shader named {shaderName}");
    var targetField = manager.GetBaseField(target, targetMatches[0]);
    var donorField = manager.GetBaseField(donor, donorMatches[0]);
    if (!Platforms(targetField).SequenceEqual([15]) || !Platforms(donorField).Contains(14))
        throw new InvalidOperationException($"Expected OpenGL-only target and Metal donor for {shaderName}");
    replacements.Add((targetMatches[0], donorField));
}

foreach (var replacement in replacements)
    replacement.targetAsset.SetNewData(replacement.donorField);
using (var writer = new AssetsFileWriter(outputPath))
    target.file.Write(writer);
foreach (var replacement in replacements)
    Console.WriteLine($"Grafted {Name(replacement.donorField)} into path ID {replacement.targetAsset.PathId}");
return 0;

static bool IsBundle(string path)
{
    using var stream = File.OpenRead(path);
    Span<byte> signature = stackalloc byte[7];
    return stream.Read(signature) == signature.Length &&
           System.Text.Encoding.ASCII.GetString(signature) == "UnityFS";
}

static string Name(AssetTypeValueField field) => field["m_ParsedForm"]["m_Name"].AsString;

static int[] Platforms(AssetTypeValueField field) => field["platforms"].Children
    .SelectMany(child => child.Children).Select(child => child.AsInt).ToArray();

static int Transplant(string[] arguments)
{
    string sourcePath = Path.GetFullPath(arguments[2]), donorPath = Path.GetFullPath(arguments[4]), output = Path.GetFullPath(arguments[6]);
    if (!Path.IsPathRooted(arguments[6]) || File.Exists(output) || File.Exists(output + ".receipt.json") || output == sourcePath || output == donorPath)
        throw new InvalidOperationException("New absolute output bundle and receipt required.");
    string sourceHash = FileHash(sourcePath), donorHash = FileHash(donorPath), classHash = FileHash(arguments[1]);
    var manager = new AssetsManager(); manager.LoadClassPackage(arguments[1]);
    var source = manager.LoadAssetsFile(sourcePath, false);
    var bundle = manager.LoadBundleFile(donorPath, true);
    if (bundle.file.BlockAndDirInfo.DirectoryInfos.Count != 1 || !bundle.file.BlockAndDirInfo.DirectoryInfos[0].IsSerialized)
        throw new InvalidOperationException("Single serialized-file donor bundle required.");
    var donor = manager.LoadAssetsFileFromBundle(bundle, 0, false);
    if (source.file.Metadata.UnityVersion != donor.file.Metadata.UnityVersion)
        throw new InvalidOperationException("Source and donor Unity versions differ.");
    manager.LoadClassDatabaseFromPackage(source.file.Metadata.UnityVersion);
    AssetFileInfo Select(AssetsFileInstance file, string name)
    {
        var matches = file.file.GetAssetsOfType(AssetClassID.Shader).Where(a => Name(manager.GetBaseField(file, a)) == name).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Exactly one shader required: " + name);
        return matches[0];
    }
    var original = Select(source, arguments[3]); var selected = Select(donor, arguments[5]);
    var originalField = manager.GetBaseField(source, original);
    string sourceRoot = Path.GetDirectoryName(sourcePath)!;
    var sourceFiles = new Dictionary<string, (AssetsFileInstance File, string Digest)> { [sourcePath] = (source, sourceHash) };
    string Key(AssetsFileInstance file, long id) => Path.GetFullPath(file.path) + "#" + id;
    var dependencies = new List<(string Owner, string Path, int FileId, long PathId, string? Target, AssetTypeValueField Pointer)>();
    var closure = new Dictionary<string, (AssetsFileInstance File, AssetFileInfo Asset, AssetTypeValueField Field)>();
    AssetsFileInstance Resolve(AssetsFileInstance owner, int fileId)
    {
        if (fileId == 0) return owner;
        if (fileId < 1 || fileId > owner.file.Metadata.Externals.Count) throw new InvalidOperationException("External file ID outside metadata.");
        string external = owner.file.Metadata.Externals[fileId - 1].PathName.Replace('\\', '/');
        if (Path.IsPathRooted(external) || external.Contains(':')) throw new InvalidOperationException("Unsafe external shader path: " + external);
        string candidate;
        if (external == "Library/unity_builtin_extra" || external == "Library/unity default resources")
            candidate = Path.Combine(sourceRoot, "Resources", Path.GetFileName(external));
        else candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(owner.path)!, external));
        candidate = Path.GetFullPath(candidate);
        if (!candidate.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(candidate))
            throw new InvalidOperationException("Missing or out-of-root shader dependency: " + external + " -> " + candidate);
        FileSystemInfo current = new FileInfo(candidate);
        while (current != null)
        {
            if (current.LinkTarget != null) throw new InvalidOperationException("Symlink shader dependency rejected: " + current.FullName);
            if (current.FullName == sourceRoot) break;
            current = new DirectoryInfo(Path.GetDirectoryName(current.FullName)!);
        }
        if (!sourceFiles.TryGetValue(candidate, out var loaded))
        {
            string before = FileHash(candidate);
            var next = manager.LoadAssetsFile(candidate, false);
            if (next.file.Metadata.UnityVersion != source.file.Metadata.UnityVersion) throw new InvalidOperationException("Dependency Unity version differs.");
            if (FileHash(candidate) != before) throw new InvalidOperationException("Dependency changed while loading.");
            loaded = (next, before); sourceFiles.Add(candidate, loaded);
        }
        return loaded.File;
    }
    void Collect(AssetsFileInstance file, long id)
    {
        string key = Key(file, id); if (closure.ContainsKey(key)) return;
        if (closure.Count >= 128) throw new InvalidOperationException("Shader dependency closure exceeds128 objects.");
        var asset = file.file.GetAssetInfo(id);
        if (asset == null || asset.GetTypeId(file.file) != (int)AssetClassID.Shader) throw new InvalidOperationException("Required dependency is not a shader: " + key);
        var field = manager.GetBaseField(file, asset); closure.Add(key, (file, asset, field)); Inspect(field, "Shader", key, file);
    }
    void Inspect(AssetTypeValueField field, string path, string owner, AssetsFileInstance ownerFile)
    {
        if (field.TemplateField.Type.StartsWith("PPtr<", StringComparison.Ordinal))
        {
            int fileId = field["m_FileID"].AsInt; long pathId = field["m_PathID"].AsLong;
            string? target = null;
            if (pathId != 0) { var file = Resolve(ownerFile, fileId); target = Key(file, pathId); Collect(file, pathId); }
            else if (fileId != 0) throw new InvalidOperationException("Noncanonical null shader pointer.");
            dependencies.Add((owner, path, fileId, pathId, target, field));
        }
        for (int i = 0; i < field.Children.Count; i++) Inspect(field.Children[i], path + "/" + field.Children[i].TemplateField.Name + "[" + i + "]", owner, ownerFile);
    }
    Collect(source, original.PathId); string rootKey = Key(source, original.PathId);
    byte[] ObjectBytes(AssetsFileInstance file, AssetFileInfo asset)
    {
        file.file.Reader.Position = asset.GetAbsoluteByteOffset(file.file);
        var bytes = file.file.Reader.ReadBytes(checked((int)asset.ByteSize));
        if (bytes.Length != asset.ByteSize) throw new InvalidDataException("Truncated asset object.");
        return bytes;
    }
    string ObjectHash(AssetsFileInstance file, AssetFileInfo asset) => Convert.ToHexString(SHA256.HashData(ObjectBytes(file, asset))).ToLowerInvariant();
    var originalBytes = closure.ToDictionary(p => p.Key, p => ObjectBytes(p.Value.File, p.Value.Asset));
    foreach (var item in closure) if (!item.Value.Field.WriteToByteArray().SequenceEqual(originalBytes[item.Key]))
        throw new InvalidOperationException("Source shader serialization is not byte exact: " + item.Key);
    var untouched = donor.file.AssetInfos.Where(a => a.PathId != selected.PathId)
        .ToDictionary(a => a.PathId, a => (Type: a.GetTypeId(donor.file), Size: a.ByteSize, Digest: ObjectHash(donor, a)));
    string donorEntry = bundle.file.BlockAndDirInfo.DirectoryInfos[0].Name;
    var remap = new Dictionary<string, long> { [rootKey] = selected.PathId };
    long nextId = Math.Max(0, donor.file.AssetInfos.Max(a => a.PathId));
    foreach (string key in closure.Keys.OrderBy(key => key, StringComparer.Ordinal)) if (key != rootKey) remap.Add(key, checked(++nextId));
    foreach (var dependency in dependencies) if (dependency.Target != null) { dependency.Pointer["m_FileID"].AsInt = 0; dependency.Pointer["m_PathID"].AsLong = remap[dependency.Target]; }
    var expected = closure.ToDictionary(p => p.Key, p => p.Value.Field.WriteToByteArray());
    foreach (var dependency in dependencies) { dependency.Pointer["m_FileID"].AsInt = dependency.FileId; dependency.Pointer["m_PathID"].AsLong = dependency.PathId; }
    foreach (var item in closure) if (!item.Value.Field.WriteToByteArray().SequenceEqual(originalBytes[item.Key]))
        throw new InvalidOperationException("Non-pointer shader data changed: " + item.Key);
    selected.SetNewData(expected[rootKey]);
    foreach (var item in closure) if (item.Key != rootKey)
    {
        var added = AssetFileInfo.Create(donor.file, remap[item.Key], (int)AssetClassID.Shader);
        if (added == null) throw new InvalidOperationException("Donor shader type unavailable.");
        added.SetNewData(expected[item.Key]); donor.file.Metadata.AddAssetInfo(added);
    }
    bundle.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(donor.file);
    string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        using (var writer = new AssetsFileWriter(temporary)) bundle.file.Write(writer);
        var verify = new AssetsManager(); verify.LoadClassPackage(arguments[1]);
        var checkBundle = verify.LoadBundleFile(temporary, true);
        var check = verify.LoadAssetsFileFromBundle(checkBundle, 0, false);
        verify.LoadClassDatabaseFromPackage(check.file.Metadata.UnityVersion);
        if (check.file.Metadata.UnityVersion != donor.file.Metadata.UnityVersion || check.file.AssetInfos.Count != donor.file.AssetInfos.Count ||
            checkBundle.file.BlockAndDirInfo.DirectoryInfos.Count != 1 || checkBundle.file.BlockAndDirInfo.DirectoryInfos[0].Name != donorEntry)
            throw new InvalidOperationException("Transplant container/object inventory changed.");
        var readback = check.file.GetAssetInfo(selected.PathId);
        var readbackField = verify.GetBaseField(check, readback);
        if (Name(readbackField) != arguments[3])
            throw new InvalidOperationException("Transplanted shader readback differs.");
        foreach (var item in closure)
        {
            var asset = check.file.GetAssetInfo(remap[item.Key]);
            if (asset == null || asset.GetTypeId(check.file) != (int)AssetClassID.Shader || !ObjectBytes(check, asset).SequenceEqual(expected[item.Key]))
                throw new InvalidOperationException("Shader closure readback differs: " + item.Key);
            var field = verify.GetBaseField(check, asset);
            if (!field.WriteToByteArray().SequenceEqual(expected[item.Key])) throw new InvalidOperationException("Shader closure parsed bytes differ.");
        }
        foreach (var pair in untouched)
        {
            var asset = check.file.GetAssetInfo(pair.Key);
            if (asset.GetTypeId(check.file) != pair.Value.Type || asset.ByteSize != pair.Value.Size || ObjectHash(check, asset) != pair.Value.Digest)
                throw new InvalidOperationException("Unselected donor object changed: " + pair.Key);
        }
        if (FileHash(sourcePath) != sourceHash || FileHash(donorPath) != donorHash || FileHash(arguments[1]) != classHash)
            throw new InvalidOperationException("Transplant input changed during operation.");
        foreach (var file in sourceFiles) if (FileHash(file.Key) != file.Value.Digest) throw new InvalidOperationException("Shader closure source changed: " + file.Key);
        var receipt = new { schema = "continuum-private-shader-transplant/v1", unity = source.file.Metadata.UnityVersion,
            source = sourcePath, sourceSha256 = sourceHash, donor = donorPath, donorSha256 = donorHash, classDataSha256 = classHash,
            output, outputSha256 = FileHash(temporary), originalShader = arguments[3], donorShader = arguments[5],
            originalPathId = original.PathId, preservedDonorPathId = selected.PathId, preservedContainer = donorEntry,
            originalPlatforms = Platforms(originalField), derivedPlatforms = Platforms(readbackField),
            dependencies = dependencies.Select(d => new { ownerSourceKey = d.Owner, ownerDonorId = remap[d.Owner], path = d.Path, sourceFileId = d.FileId, sourcePathId = d.PathId, targetSourceKey = d.Target, donorFileId = 0, donorPathId = d.Target == null ? 0 : remap[d.Target] }),
            shaderClosure = closure.Select(p => new { sourceFile = p.Value.File.path, sourcePathId = p.Value.Asset.PathId, donorPathId = remap[p.Key], name = Name(p.Value.Field), sourceSha256 = Convert.ToHexString(SHA256.HashData(originalBytes[p.Key])).ToLowerInvariant(), derivedSha256 = Convert.ToHexString(SHA256.HashData(expected[p.Key])).ToLowerInvariant(), sourcePlatforms = Platforms(p.Value.Field) }),
            sourceFiles = sourceFiles.Select(p => new { path = p.Key, sha256 = p.Value.Digest }),
            unchangedOtherObjects = untouched.Count, verifiedShaderBytes = true, dependencyContract = "Shader-only recursive closure within source directory; documented Library builtins resolve to source Resources; pointer file IDs/IDs remapped; missing, unsafe and nonshader dependencies rejected." };
        File.Move(temporary, output);
        using (var stream = new FileStream(output + ".receipt.json", FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream)) writer.Write(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Transplanted " + arguments[3] + " at donor path ID " + selected.PathId);
        return 0;
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}

static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

static int InspectPlayer(string[] arguments)
{
    string data = Path.GetFullPath(arguments[2]), output = Path.GetFullPath(arguments[3]);
    string classData = Path.GetFullPath(arguments[1]), input = Path.Combine(data, "globalgamemanagers");
    if (!Directory.Exists(data) || !File.Exists(input) || !Path.IsPathRooted(arguments[3]) || File.Exists(output) ||
        output.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal) || output == classData)
        throw new InvalidOperationException("Existing player Data directory and new absolute report outside inputs required.");
    string inputHash = FileHash(input), classHash = FileHash(classData);
    var manager = new AssetsManager(); manager.LoadClassPackage(classData);
    var file = manager.LoadAssetsFile(input, false); manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
    var settings = file.file.GetAssetsOfType(141);
    if (settings.Count != 1) throw new InvalidOperationException("Exactly one class141 BuildSettings required.");
    var asset = settings[0]; var root = manager.GetBaseField(file, asset);
    var fields = new List<(string Path, string Name, string Type, object? Value)>();
    var summarizedPayloads = new List<(string Path, int Length, string Sha256)>();
    var summarizedArrays = new List<(string Path, string Type, int Children)>();
    void Read(AssetTypeValueField field, string path, int depth, bool graphicsApi)
    {
        graphicsApi |= field.TemplateField.Name.Contains("graphicsAPI", StringComparison.OrdinalIgnoreCase);
        if (depth > 32 || fields.Count + summarizedArrays.Count >= 10000)
            throw new InvalidOperationException("BuildSettings field bound exceeded at " + path + "; type=" + field.TemplateField.Type + "; valueType=" + field.TemplateField.ValueType + "; children=" + field.Children.Count + "; depth=" + depth);
        if (field.TemplateField.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            field.TemplateField.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            field.TemplateField.Name.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            fields.Add((path, field.TemplateField.Name, field.TemplateField.Type, "[redacted]"));
            return;
        }
        if (!graphicsApi && (field.TemplateField.IsArray || field.TemplateField.ValueType == AssetValueType.Array))
        {
            summarizedArrays.Add((path, field.TemplateField.Type, field.Children.Count));
            return;
        }
        if (field.TemplateField.ValueType == AssetValueType.ByteArray)
        {
            byte[] bytes = field.AsByteArray;
            summarizedPayloads.Add((path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            fields.Add((path, field.TemplateField.Name, field.TemplateField.Type, null));
            if (graphicsApi)
            {
                if (bytes.Length > 256) throw new InvalidOperationException("graphicsAPI byte payload exceeds256 at " + path);
                for (int i = 0; i < bytes.Length; i++) fields.Add((path + "/byte[" + i + "]", "byte", "UInt8", bytes[i]));
            }
            return;
        }
        object? value = field.TemplateField.ValueType switch
        {
            AssetValueType.Bool => field.AsBool,
            AssetValueType.Int8 => field.AsSByte,
            AssetValueType.UInt8 => field.AsByte,
            AssetValueType.Int16 => field.AsShort,
            AssetValueType.UInt16 => field.AsUShort,
            AssetValueType.Int32 => field.AsInt,
            AssetValueType.UInt32 => field.AsUInt,
            AssetValueType.Int64 => field.AsLong,
            AssetValueType.UInt64 => field.AsULong,
            AssetValueType.Float => field.AsFloat,
            AssetValueType.Double => field.AsDouble,
            AssetValueType.String => field.AsString,
            _ => null
        };
        fields.Add((path, field.TemplateField.Name, field.TemplateField.Type, value));
        if (value != null) return;
        for (int i = 0; i < field.Children.Count; i++) Read(field.Children[i], path + "/" + field.Children[i].TemplateField.Name + "[" + i + "]", depth + 1, graphicsApi);
    }
    Read(root, "BuildSettings", 0, false);
    var apiFields = fields.Where(f => f.Path.Contains("graphicsAPI", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (apiFields.Length == 0) throw new InvalidOperationException("No readable graphicsAPI field; backend admission remains unresolved.");
    if (FileHash(input) != inputHash || FileHash(classData) != classHash) throw new InvalidOperationException("Player inspection input changed.");
    var receipt = new { schema = "continuum-player-build-settings/v1", dataDirectory = data, source = input, sourceSha256 = inputHash,
        classDataSha256 = classHash, unity = file.file.Metadata.UnityVersion, serializedVersion = file.file.Header.Version,
        metadataTargetPlatform = file.file.Metadata.TargetPlatform, classId = asset.GetTypeId(file.file), pathId = asset.PathId,
        graphicsApiFields = apiFields.Select(f => new { path = f.Path, name = f.Name, type = f.Type, value = f.Value }),
        buildSettingsFields = fields.Select(f => new { path = f.Path, name = f.Name, type = f.Type, value = f.Value }),
        summarizedPayloads = summarizedPayloads.Select(p => new { path = p.Path, bytes = p.Length, sha256 = p.Sha256 }),
        summarizedArrays = summarizedArrays.Select(p => new { path = p.Path, type = p.Type, materializedChildCount = p.Children, sourceSha256 = inputHash }),
        rootFields = root.Children.Select(p => new { name = p.TemplateField.Name, type = p.TemplateField.Type, valueType = p.TemplateField.ValueType.ToString(), materializedChildCount = p.Children.Count }),
        inspectionScope = "graphicsAPI-named subtree expanded; other semantic scalar flags retained with token/password/secret-named fields redacted; non-graphics arrays summarized without enumerating preload/reference elements. Array summaries bind the complete source-file digest, not an independently hashed array payload.",
        interpretation = "Raw serialized BuildSettings values only. No platform enum mapping, binary backend availability, shader coverage or native rendering qualification inferred." };
    using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
    using (var writer = new StreamWriter(stream)) writer.Write(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Inspected class141 BuildSettings: " + apiFields.Length + " graphicsAPI fields.");
    return 0;
}
