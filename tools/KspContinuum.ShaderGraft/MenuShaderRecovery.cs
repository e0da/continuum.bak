using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class MenuShaderRecovery
{
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static IEnumerable<AssetTypeValueField> Numbers(AssetTypeValueField field)
    {
        if (field.TemplateField.ValueType == AssetValueType.UInt32 || field.TemplateField.ValueType == AssetValueType.Int32) { yield return field; yield break; }
        foreach (var child in field.Children) foreach (var value in Numbers(child)) yield return value;
    }
    static long Number(AssetTypeValueField field) => field.TemplateField.ValueType == AssetValueType.UInt32 ? field.AsUInt : field.AsInt;
    static byte[] Bytes(AssetTypeValueField field)
    {
        if (field.IsDummy) throw new InvalidDataException("Shader compressedBlob absent.");
        if (field.TemplateField.ValueType == AssetValueType.ByteArray) return field.AsByteArray;
        if (field.TemplateField.ValueType == AssetValueType.UInt8) return new[] { field.AsByte };
        return field.Children.SelectMany(Bytes).ToArray();
    }
    public static int Materials(string[] args)
    {
        string data = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
        if (!Path.IsPathRooted(args[3]) || File.Exists(output) || output.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("New private output outside Data required.");
        var manager = new AssetsManager(); manager.LoadClassPackage(args[1]);
        var files = new Dictionary<string, AssetsFileInstance>(StringComparer.Ordinal); var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        AssetsFileInstance Load(string path)
        {
            path = Path.GetFullPath(path);
            if (!path.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Material closure source absent/unsafe.");
            if (files.TryGetValue(path, out var cached)) return cached;
            var value = manager.LoadAssetsFile(path, false);
            if (value.file.Metadata.UnityVersion != "2019.4.18f1") throw new InvalidDataException("Pinned Unity source required.");
            files.Add(path, value); hashes.Add(path, Hash(path)); return value;
        }
        (AssetsFileInstance File, AssetFileInfo Asset)? Resolve(AssetsFileInstance owner, AssetTypeValueField pointer)
        {
            int fileId = pointer["m_FileID"].AsInt; long id = pointer["m_PathID"].AsLong; if (id == 0) return null;
            var target = owner;
            if (fileId != 0)
            {
                if (fileId < 1 || fileId > owner.file.Metadata.Externals.Count) throw new InvalidDataException("External material pointer invalid.");
                string path = owner.file.Metadata.Externals[fileId - 1].PathName.Replace('\\', '/');
                if (path.StartsWith("Library/", StringComparison.Ordinal)) path = "Resources/" + Path.GetFileName(path);
                target = Load(Path.Combine(data, path));
            }
            var asset = target.file.GetAssetInfo(id); if (asset == null) throw new InvalidDataException("Missing material dependency object.");
            return (target, asset);
        }
        var rows = new List<object>(); var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string filename in new[] { "level2", "sharedassets2.assets" })
        {
            var file = Load(Path.Combine(data, filename)); manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
            foreach (var asset in file.file.GetAssetsOfType(AssetClassID.Material))
            {
                var field = manager.GetBaseField(file, asset); var shader = Resolve(file, field["m_Shader"]);
                if (shader == null || shader.Value.Asset.GetTypeId(shader.Value.File.file) != (int)AssetClassID.Shader) throw new InvalidDataException("Menu material shader closure invalid.");
                string shaderName = manager.GetBaseField(shader.Value.File, shader.Value.Asset)["m_ParsedForm"]["m_Name"].AsString; names.Add(shaderName);
                var textures = new List<object>();
                foreach (var entry in field["m_SavedProperties"]["m_TexEnvs"].Children[0].Children)
                {
                    var second = entry["second"]; var texture = Resolve(file, second["m_Texture"]);
                    if (texture == null) { textures.Add(new { property = entry["first"].AsString, isNull = true }); continue; }
                    var value = manager.GetBaseField(texture.Value.File, texture.Value.Asset);
                    textures.Add(new { property = entry["first"].AsString, source = texture.Value.File.path, pathId = texture.Value.Asset.PathId,
                        type = texture.Value.Asset.GetTypeId(texture.Value.File.file), name = value["m_Name"].AsString,
                        serializedBytes = texture.Value.Asset.ByteSize });
                }
                int count = 0; rows.Add(new { source = file.path, pathId = asset.PathId, name = field["m_Name"].AsString,
                    shaderName, shaderSource = shader.Value.File.path, shaderPathId = shader.Value.Asset.PathId, textures,
                    material = Tree(field, ref count, 0) });
            }
        }
        foreach (var hash in hashes) if (Hash(hash.Key) != hash.Value) throw new InvalidDataException("Material closure input changed.");
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(new { schema = "continuum-private-menu-material-closure/v1", sourceScene = 2,
            classDataSha256 = Hash(args[1]), files = hashes, shaderNames = names, rows,
            scope = "Serialized mainmenu level2/sharedassets2 material closure including inactive objects; actual active consumer coverage requires native menu census. Texture object references and all material properties preserved; texture payloads unchanged." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Recovered menu material closure: " + rows.Count + " materials; " + names.Count + " shader identities."); return 0;
    }
    static object Tree(AssetTypeValueField field, ref int count, int depth)
    {
        if (++count > 250000 || depth > 40) throw new InvalidDataException("Bounded shader parsed-form tree exceeded.");
        if (field.TemplateField.Name.StartsWith("prog", StringComparison.Ordinal))
            return new { name = field.TemplateField.Name, type = field.TemplateField.Type, summarizedProgramMetadata = true,
                childCount = field.Children.Count, serializedSha256 = Digest(field.WriteToByteArray()) };
        object? value = field.TemplateField.ValueType switch {
            AssetValueType.Bool => field.AsBool, AssetValueType.Int8 => field.AsSByte, AssetValueType.UInt8 => field.AsByte,
            AssetValueType.Int16 => field.AsShort, AssetValueType.UInt16 => field.AsUShort,
            AssetValueType.Int32 => field.AsInt, AssetValueType.UInt32 => field.AsUInt,
            AssetValueType.Int64 => field.AsLong, AssetValueType.UInt64 => field.AsULong,
            AssetValueType.Float => field.AsFloat, AssetValueType.Double => field.AsDouble, AssetValueType.String => field.AsString, _ => null };
        var children = new List<object>();
        if (value == null) foreach (var child in field.Children) children.Add(Tree(child, ref count, depth + 1));
        return new { name = field.TemplateField.Name, type = field.TemplateField.Type, value, children };
    }
    public static int Run(string[] args)
    {
        string data = Path.GetFullPath(args[2]), namesPath = Path.GetFullPath(args[3]), output = Path.GetFullPath(args[4]);
        if (!Path.IsPathRooted(args[4]) || Directory.Exists(output) || File.Exists(output) || output.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("New absolute private recovery directory outside player Data required.");
        string[] names = File.ReadAllLines(namesPath).Where(v => !string.IsNullOrWhiteSpace(v) && !v.StartsWith('#')).ToArray();
        if (names.Length == 0 || names.Length > 64 || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("One to64 exact unique shader names required.");
        var wanted = names.ToHashSet(StringComparer.Ordinal); var rows = new List<object>(); var found = new HashSet<string>(StringComparer.Ordinal);
        var manager = new AssetsManager(); manager.LoadClassPackage(args[1]);
        string classHash = Hash(args[1]), namesHash = Hash(namesPath);
        var containers = Directory.EnumerateFiles(data).Where(path => Path.GetFileName(path) is "globalgamemanagers.assets" or "resources.assets" || Path.GetFileName(path).StartsWith("sharedassets", StringComparison.Ordinal) && path.EndsWith(".assets", StringComparison.Ordinal)).OrderBy(v => v, StringComparer.Ordinal).ToList();
        containers.Add(Path.Combine(data, "Resources", "unity_builtin_extra"));
        Directory.CreateDirectory(output);
        foreach (string path in containers)
        {
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Shader source links rejected.");
            var file = manager.LoadAssetsFile(path, false);
            if (file.file.Metadata.UnityVersion != "2019.4.18f1") throw new InvalidDataException("Pinned source Unity2019.4.18f1 required.");
            manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
            string? sourceHash = null;
            foreach (var asset in file.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var field = manager.GetBaseField(file, asset); string name = field["m_ParsedForm"]["m_Name"].AsString;
                if (!wanted.Contains(name)) continue;
                found.Add(name);
                sourceHash ??= Hash(path);
                string entry = "shader-" + rows.Count.ToString("D2"); string directory = Path.Combine(output, entry); Directory.CreateDirectory(directory);
                int count = 0; File.WriteAllText(Path.Combine(directory, "parsed-form.json"), JsonSerializer.Serialize(Tree(field["m_ParsedForm"], ref count, 0), new JsonSerializerOptions { WriteIndented = true }));
                file.file.Reader.Position = asset.GetAbsoluteByteOffset(file.file);
                byte[] objectBytes = file.file.Reader.ReadBytes(checked((int)asset.ByteSize));
                if (objectBytes.Length != asset.ByteSize) throw new InvalidDataException("Truncated shader object.");
                File.WriteAllBytes(Path.Combine(directory, "original-object.bin"), objectBytes);
                int[] platforms = Numbers(field["platforms"]).Select(v => checked((int)Number(v))).ToArray();
                byte[] compressed = Bytes(field["compressedBlob"]);
                var programs = new List<object>();
                for (int platform = 0; platform < platforms.Length; platform++)
                {
                    if (platforms[platform] != 15) continue;
                    var offsets = Numbers(field["offsets"].Children[0].Children[platform]).Select(v => checked((int)Number(v))).ToArray();
                    var compressedLengths = Numbers(field["compressedLengths"].Children[0].Children[platform]).Select(v => checked((int)Number(v))).ToArray();
                    var decompressedLengths = Numbers(field["decompressedLengths"].Children[0].Children[platform]).Select(v => checked((int)Number(v))).ToArray();
                    if (offsets.Length != compressedLengths.Length || offsets.Length != decompressedLengths.Length) throw new InvalidDataException("Shader compression table shape differs.");
                    for (int chunk = 0; chunk < offsets.Length; chunk++)
                    {
                        if (offsets[chunk] < 0 || compressedLengths[chunk] < 1 || offsets[chunk] > compressed.Length - compressedLengths[chunk] || decompressedLengths[chunk] < 1 || decompressedLengths[chunk] > 64 * 1024 * 1024)
                            throw new InvalidDataException("Shader chunk bounds invalid.");
                        byte[] decoded = LZ4ps.LZ4Codec.Decode32(compressed, offsets[chunk], compressedLengths[chunk], decompressedLengths[chunk]);
                        string rawPath = "opengl-chunk-" + chunk + ".bin"; File.WriteAllBytes(Path.Combine(directory, rawPath), decoded);
                        string printable = Encoding.UTF8.GetString(decoded); int cursor = 0, index = 0;
                        while ((cursor = printable.IndexOf("#version", cursor, StringComparison.Ordinal)) >= 0)
                        {
                            int end = printable.IndexOf('\0', cursor); if (end < 0) throw new InvalidDataException("GLSL program lacks terminator.");
                            string source = printable.Substring(cursor, end - cursor);
                            if (source.Contains("void main", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(directory, "program-" + chunk + "-" + index++ + ".glsl"), source);
                            cursor = end + 1;
                        }
                        programs.Add(new { platform = platforms[platform], chunk, bytes = decoded.Length, sha256 = Digest(decoded), glslPrograms = index, rawPath });
                    }
                }
                rows.Add(new { name, source = path, sourceSha256 = sourceHash, pathId = asset.PathId, objectSha256 = Digest(objectBytes), platforms, entry, programs });
            }
            if (sourceHash != null && Hash(path) != sourceHash) throw new InvalidOperationException("Shader recovery source changed.");
        }
        string[] missing = names.Where(v => !found.Contains(v)).ToArray();
        if (Hash(args[1]) != classHash || Hash(namesPath) != namesHash) throw new InvalidOperationException("Recovery metadata input changed.");
        File.WriteAllText(Path.Combine(output, "recovery.json"), JsonSerializer.Serialize(new { schema = "continuum-private-menu-shader-recovery/v1", classDataSha256 = classHash, namesSha256 = namesHash, rows, missing,
            scope = "Exact named serialized shader objects, parsed pass/property metadata and original OpenGL compiled programs. These are private reconstruction inputs, not Metal shaders or complete menu material closure." }, new JsonSerializerOptions { WriteIndented = true }));
        if (missing.Length > 0) throw new InvalidDataException("Named shader recovery incomplete; private evidence retained.");
        Console.WriteLine("Recovered " + rows.Count + " exact named shader objects into private reconstruction directory."); return 0;
    }
}
