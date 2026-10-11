using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Security.Cryptography;
using System.Text.Json;

static class MenuShaderGraft
{
    static string Hash(string path) { using var stream = File.OpenRead(path); return Hex(SHA256.HashData(stream)); }
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    static string Name(AssetTypeValueField field) => field["m_ParsedForm"]["m_Name"].AsString;
    static int[] Platforms(AssetTypeValueField field) => field["platforms"].Children.SelectMany(c => c.Children).Select(c => c.AsInt).ToArray();
    static string[] PropertyContract(AssetTypeValueField properties)
    {
        var values = properties["m_Props"];
        if (values.IsDummy || values.Children.Count != 1) throw new InvalidDataException("Pinned shader property vector absent.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        return values.Children[0].Children.Select(value =>
        {
            string name = value["m_Name"].AsString;
            int type = value["m_Type"].AsInt;
            if (string.IsNullOrEmpty(name) || !names.Add(name)) throw new InvalidDataException("Shader property name missing or duplicated.");
            return name + ":" + type + ":" + (type == 4 ? value["m_DefTexture"]["m_TexDim"].AsInt : 0);
        }).ToArray();
    }
    static byte[] Raw(AssetsFileInstance file, AssetFileInfo asset)
    {
        file.file.Reader.Position = asset.GetAbsoluteByteOffset(file.file);
        byte[] bytes = file.file.Reader.ReadBytes(checked((int)asset.ByteSize));
        if (bytes.Length != asset.ByteSize) throw new InvalidDataException("Truncated serialized object.");
        return bytes;
    }
    static byte[] Metadata(AssetsFileInstance file)
    {
        using var stream = new MemoryStream(); using var writer = new AssetsFileWriter(stream);
        file.file.Metadata.Write(writer, file.file.Header.Version); return stream.ToArray();
    }
    public static int Run(string[] args)
    {
        string classData = Path.GetFullPath(args[1]), sourcePath = Path.GetFullPath(args[2]), donorPath = Path.GetFullPath(args[3]), output = Path.GetFullPath(args[4]);
        if (!Path.IsPathRooted(args[4]) || File.Exists(output) || File.Exists(output + ".receipt.json") || output == sourcePath || output == donorPath || output.StartsWith(Path.GetDirectoryName(sourcePath)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("New absolute output outside source Data required.");
        string[][] mappings = File.ReadAllLines(args[5]).Where(v => v.Length > 0 && !v.StartsWith('#')).Select(v => v.Split('\t')).ToArray();
        if (mappings.Any(v => v.Length < 1 || v.Length > 2 || v.Any(string.IsNullOrWhiteSpace))) throw new InvalidDataException("One exact target name and optional tab-separated donor name required.");
        string[] names = mappings.Select(v => v[0]).ToArray();
        var donorNames = mappings.ToDictionary(v => v[0], v => v.Length == 2 ? v[1] : v[0], StringComparer.Ordinal);
        if (names.Length == 0 || names.Length > 64 || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("One to64 exact unique shader names required.");
        string sourceHash = Hash(sourcePath), donorHash = Hash(donorPath), classHash = Hash(classData), namesHash = Hash(args[5]);
        var manager = new AssetsManager(); manager.LoadClassPackage(classData);
        var source = manager.LoadAssetsFile(sourcePath, false); var bundle = manager.LoadBundleFile(donorPath, true);
        if (bundle.file.BlockAndDirInfo.DirectoryInfos.Count != 1 || !bundle.file.BlockAndDirInfo.DirectoryInfos[0].IsSerialized) throw new InvalidDataException("Single serialized donor required.");
        var donor = manager.LoadAssetsFileFromBundle(bundle, 0, false);
        if (source.file.Metadata.UnityVersion != "2019.4.18f1" || donor.file.Metadata.UnityVersion != source.file.Metadata.UnityVersion) throw new InvalidDataException("Matching pinned Unity required.");
        manager.LoadClassDatabaseFromPackage(source.file.Metadata.UnityVersion);
        var dependencies = new List<object>(); var implicitEngineDependencies = new List<object>(); var sourceFiles = new Dictionary<string, string> { [sourcePath] = sourceHash };
        var destinations = new List<(int FileId, long PathId, string Name)>();
        void Inventory(AssetsFileInstance file, int fileId)
        {
            foreach (var asset in file.file.GetAssetsOfType(AssetClassID.Shader)) destinations.Add((fileId, asset.PathId, Name(manager.GetBaseField(file, asset))));
        }
        Inventory(source, 0);
        string root = Path.GetDirectoryName(sourcePath)!;
        if (Path.GetFileName(sourcePath) == "unity_builtin_extra" && Path.GetFileName(root) == "Resources") root = Path.GetDirectoryName(root)!;
        for (int i = 0; i < source.file.Metadata.Externals.Count; i++)
        {
            string relative = source.file.Metadata.Externals[i].PathName.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Unsafe serialized dependency path.");
            if (relative == "Resources/unity default resources" || relative == "Library/unity default resources")
            {
                implicitEngineDependencies.Add(new { fileId = i + 1, relative, scope = "Original virtual engine dependency retained; no donor binding may resolve through an uninventoried object." });
                continue;
            }
            string path = relative.StartsWith("Library/", StringComparison.Ordinal) ? Path.Combine(root, "Resources", Path.GetFileName(relative)) : Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) throw new InvalidDataException("Missing serialized dependency: " + relative);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked source dependency rejected.");
            var file = manager.LoadAssetsFile(path, false);
            if (file.file.Metadata.UnityVersion != source.file.Metadata.UnityVersion) throw new InvalidDataException("Dependency Unity version differs.");
            sourceFiles[path] = Hash(path); Inventory(file, i + 1);
        }
        var inventory = source.file.AssetInfos.Select(a => new { a.PathId, a.ByteOffset, a.ByteSize, type = a.GetTypeId(source.file), sha256 = Hex(SHA256.HashData(Raw(source, a))) }).ToArray();
        byte[] metadata = Metadata(source); var replacements = new Dictionary<long, byte[]>(); var replacementRows = new List<object>();
        foreach (string name in names)
        {
            string donorName = donorNames[name];
            var original = source.file.GetAssetsOfType(AssetClassID.Shader).Where(a => Name(manager.GetBaseField(source, a)) == name).ToArray();
            var cooked = donor.file.GetAssetsOfType(AssetClassID.Shader).Where(a => Name(manager.GetBaseField(donor, a)) == donorName).ToArray();
            if (original.Length != 1 || cooked.Length != 1) throw new InvalidDataException("Exactly one local target/donor shader required: " + name);
            var field = manager.GetBaseField(donor, cooked[0]); var originalField = manager.GetBaseField(source, original[0]);
            if (!Platforms(field).Contains(14)) throw new InvalidDataException("Metal compiler platform14 missing: " + name);
            var originalProperties = originalField["m_ParsedForm"]["m_PropInfo"];
            var donorProperties = field["m_ParsedForm"]["m_PropInfo"];
            if (originalProperties.IsDummy || donorProperties.IsDummy || !PropertyContract(originalProperties).SequenceEqual(PropertyContract(donorProperties)))
                throw new InvalidDataException("Cooked shader changes material property exposure/types/order: " + name);
            byte[] originalPropertyBytes = originalProperties.WriteToByteArray();
            int propertyIndex = field["m_ParsedForm"].Children.FindIndex(c => c.TemplateField.Name == "m_PropInfo");
            if (propertyIndex < 0) throw new InvalidDataException("Shader property field missing.");
            field["m_ParsedForm"].Children[propertyIndex] = originalProperties;
            field["m_ParsedForm"]["m_Name"].AsString = name;
            int nodes = 0;
            void Rewrite(AssetTypeValueField node, string path, int depth)
            {
                if (++nodes > 1000000 || depth > 64) throw new InvalidDataException("Shader dependency traversal bound exceeded.");
                if (node.TemplateField.Type.StartsWith("PPtr<", StringComparison.Ordinal))
                {
                    int fileId = node["m_FileID"].AsInt; long id = node["m_PathID"].AsLong;
                    if (id == 0) { if (fileId != 0) throw new InvalidDataException("Noncanonical null donor pointer."); return; }
                    if (fileId != 0) throw new InvalidDataException("External donor dependency requires explicit closure cook: " + path);
                    var asset = donor.file.GetAssetInfo(id);
                    if (asset == null || asset.GetTypeId(donor.file) != (int)AssetClassID.Shader) throw new InvalidDataException("Nonshader donor dependency rejected.");
                    string dependencyName = Name(manager.GetBaseField(donor, asset));
                    var matches = destinations.Where(d => d.Name == dependencyName).ToArray();
                    // Preserve the original root's dependency graph instead of choosing an arbitrary same-name object.
                    if (matches.Length != 1) throw new InvalidDataException("Ambiguous/missing original dependency: " + dependencyName);
                    node["m_FileID"].AsInt = matches[0].FileId; node["m_PathID"].AsLong = matches[0].PathId;
                    dependencies.Add(new { shader = name, field = path, donorPathId = id, dependencyName, fileId = matches[0].FileId, pathId = matches[0].PathId }); return;
                }
                if (node.Value != null) return;
                foreach (var child in node.Children) Rewrite(child, path + "/" + child.TemplateField.Name, depth + 1);
            }
            Rewrite(field, "Shader", 0);
            var originalDependencies = originalField["m_Dependencies"];
            var donorDependencies = field["m_Dependencies"];
            if (originalDependencies.IsDummy || donorDependencies.IsDummy) throw new InvalidDataException("Pinned shader dependency table absent.");
            byte[] originalDependencyBytes = originalDependencies.WriteToByteArray();
            if (originalDependencies.Children.Count != 1 || donorDependencies.Children.Count != 1) throw new InvalidDataException("Pinned dependency vector shape differs.");
            var mergedDependencies = originalDependencies.Children[0].Children;
            int originalDependencyCount = mergedDependencies.Count;
            string[] preservedDependencies = mergedDependencies.Select(c => Hex(SHA256.HashData(c.WriteToByteArray()))).ToArray();
            // Retain every original edge in order, adding only donor edges required by the cooked recipe.
            var known = preservedDependencies.ToHashSet(StringComparer.Ordinal);
            foreach (var child in donorDependencies.Children[0].Children) if (known.Add(Hex(SHA256.HashData(child.WriteToByteArray())))) mergedDependencies.Add(child);
            int dependencyIndex = field.Children.FindIndex(c => c.TemplateField.Name == "m_Dependencies");
            if (dependencyIndex < 0) throw new InvalidDataException("Shader dependency field missing.");
            field.Children[dependencyIndex] = originalDependencies;
            byte[] bytes = field.WriteToByteArray(); replacements.Add(original[0].PathId, bytes); original[0].SetNewData(bytes);
            if (!mergedDependencies.Take(originalDependencyCount).Select(c => Hex(SHA256.HashData(c.WriteToByteArray()))).SequenceEqual(preservedDependencies)) throw new InvalidDataException("Original shader dependency edges changed.");
            if (!field["m_ParsedForm"]["m_PropInfo"].WriteToByteArray().SequenceEqual(originalPropertyBytes)) throw new InvalidDataException("Original property schema changed.");
            replacementRows.Add(new { name, donorName, sourcePathId = original[0].PathId, donorPathId = cooked[0].PathId, sourceObjectSha256 = inventory.Single(a => a.PathId == original[0].PathId).sha256, derivedObjectSha256 = Hex(SHA256.HashData(bytes)), originalPropertySchemaSha256 = Hex(SHA256.HashData(originalPropertyBytes)), originalPropertySchemaPreserved = true, originalDependencyTableSha256 = Hex(SHA256.HashData(originalDependencyBytes)), originalDependencyCount, appendedDonorDependencies = mergedDependencies.Count - originalDependencyCount, originalDependencyEdgesPreserved = true, platforms = Platforms(field) });
        }
        string temporary = output + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new AssetsFileWriter(temporary)) source.file.Write(writer);
            var verify = new AssetsManager(); verify.LoadClassPackage(classData); var check = verify.LoadAssetsFile(temporary, false); verify.LoadClassDatabaseFromPackage(check.file.Metadata.UnityVersion);
            if (!check.file.AssetInfos.Select(a => a.PathId).SequenceEqual(inventory.Select(a => a.PathId)) || check.file.Header.Version != source.file.Header.Version || check.file.Header.Endianness != source.file.Header.Endianness) throw new InvalidDataException("Object/header identity changed.");
            foreach (var row in inventory)
            {
                var asset = check.file.GetAssetInfo(row.PathId); byte[] bytes = Raw(check, asset);
                if (asset.GetTypeId(check.file) != row.type || (replacements.TryGetValue(row.PathId, out byte[]? expected) ? !bytes.SequenceEqual(expected) : Hex(SHA256.HashData(bytes)) != row.sha256)) throw new InvalidDataException("Object preservation failed: " + row.PathId);
                if (expected != null && !verify.GetBaseField(check, asset).WriteToByteArray().SequenceEqual(expected)) throw new InvalidDataException("Reparsed selected shader changed.");
                asset.ByteOffset = row.ByteOffset; asset.ByteSize = row.ByteSize;
            }
            if (!Metadata(check).SequenceEqual(metadata)) throw new InvalidDataException("Non-layout metadata/dependencies changed.");
            if (Hash(donorPath) != donorHash || Hash(classData) != classHash || Hash(args[5]) != namesHash || sourceFiles.Any(p => Hash(p.Key) != p.Value)) throw new InvalidDataException("Cook/graft input changed.");
            string receipt = JsonSerializer.Serialize(new { schema = "continuum-private-menu-shader-graft/v1", source = sourcePath, sourceSha256 = sourceHash, donor = donorPath, donorSha256 = donorHash, classDataSha256 = classHash, output, derivedSha256 = Hash(temporary), replacements = replacementRows, dependencies, implicitEngineDependencies, sourceFiles, unchangedOtherObjects = inventory.Length - replacements.Count, nonLayoutMetadataPreserved = true, inputUnchanged = true, remainingGate = "Dependency identities preserved; every reachable shader still needs Metal program coverage and native menu image qualification. No texture/material/source object rewritten." }, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, output); File.WriteAllText(output + ".receipt.json", receipt); Console.WriteLine("Verified menu graft: " + replacements.Count + " shaders; unrelated objects and dependencies preserved."); return 0;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
