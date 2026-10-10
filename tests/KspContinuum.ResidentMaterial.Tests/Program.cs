using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static int checks;
    static readonly string Digest = new string('a', 64);
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
    static void Reject(Action action, string reason)
    {
        bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; }
        Check(rejected, reason);
    }
    static float[] Identity() { return new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 }; }
    static ResidentGeometry Geometry()
    {
        return new ResidentGeometry { meshes = new[] { new ResidentMeshData {
            positions = new float[] { 0,0,0, 1,0,0, 0,1,0 }, normals = new float[] { 0,0,1, 0,0,1, 0,0,1 },
            uv = new float[] { 0,0, 2,-1, 0,1 }, tangents = new float[] { 1,0,0,1, 1,0,0,-1, 1,0,0,1 }, indices = new[] { 0,1,2 } } } };
    }
    static ResidentSnapshot Snapshot()
    {
        float[] matrix = Identity(); matrix[0] = 2; matrix[1] = .25f; matrix[3] = 7; matrix[5] = 3; matrix[10] = 4;
        float[] sh = new float[27]; sh[0] = 11; sh[9] = 22; sh[18] = 33;
        return new ResidentSnapshot { topologyId = Digest, generation = 7, instances = new[] { new ResidentInstanceState {
            matrix = matrix, temperature = new float[] { 1,2,3,4 }, rim = new float[4], emission = new float[4],
            falloff = 2, opacityRaw = 0, opacityEffective = 1, sh = sh, occlusion = new float[] { 1,1,1,1 } } } };
    }
    static ResidentMaterialManifest Manifest()
    {
        return new ResidentMaterialManifest { schema = "continuum-resident-material-fixture/v2", runId = "analytic", vesselId = "vessel", processId = 1,
            engine = "2019.4.18f1", colorSpace = "Gamma", family = "KSP/Emissive/Bumped Specular (Mapped)", topologyId = Digest,
            geometry = "geometry.bin", geometrySha256 = Digest, state = "state.bin", stateSha256 = Digest, rootToWorld = Identity(),
            cameraPosition = new float[] { 0,0,-10 }, cameraRotation = new float[] { 0,0,0,1 }, fov = 60, near = .1f, far = 100,
            lighting = new ResidentLighting { direction = new float[] { 0,0,1 }, color = new float[] { 1,1,1,1 }, ambientSky = new float[4], ambientEquator = new float[4], ambientGround = new float[4], intensity = 1, ambientIntensity = 1, shadowDistance = 100, shadows = 0, ambientMode = 3, shadowCascades = 0 },
            textures = new[] { new ResidentTextureAsset { id = "texture", payload = "texture.bin", payloadSha256 = Digest, payloadLength = 4, width = 1, height = 1, format = 4, graphicsFormat = 8, mipCount = 1, filter = 1, wrap = 0, aniso = 1, linear = true } },
            materials = new[] { new ResidentMaterialAsset { id = "material", shader = "Continuum/FlightPartSpecularInstanced", queue = 2000, properties = new[] {
                new ResidentMaterialProperty { name = "_Color", kind = 0, textureIndex = -1, value = new float[] { .25f,.5f,.75f,1 }, scaleOffset = new float[0] },
                new ResidentMaterialProperty { name = "_Shininess", kind = 2, textureIndex = -1, value = new float[] { .5f }, scaleOffset = new float[0] },
                new ResidentMaterialProperty { name = "_MainTex", kind = 4, textureIndex = 0, value = new float[0], scaleOffset = new float[] { 2,3,.25f,.5f } } } } },
            instances = new[] { new ResidentInstanceAsset { id = "instance", meshIndex = 0, materialIndex = 0, layer = 0, shadowCasting = 0 } } };
    }
    static string Mutate(string root, string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
    static string Sha(string path)
    {
        using (SHA256 hash = SHA256.Create()) using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    static string Payload(string manifestPath, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains("\\") || relative.Contains(":")) throw new InvalidDataException("Payload path is not relative.");
        foreach (string segment in relative.Split('/')) if (segment.Length == 0 || segment == "." || segment == "..") throw new InvalidDataException("Payload path escapes fixture.");
        return Path.Combine(Path.GetDirectoryName(manifestPath), relative);
    }
    static int Snapshots(string manifestPath, string output)
    {
        if (!Path.IsPathRooted(manifestPath) || !Path.IsPathRooted(output)) throw new InvalidDataException("Manifest and new output directory must be absolute.");
        manifestPath = Path.GetFullPath(manifestPath); output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists.");
        if (new FileInfo(manifestPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Manifest exceeds bounded input size.");
        ResidentMaterialManifest manifest = JsonSerializer.Deserialize<ResidentMaterialManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { IncludeFields = true });
        if (manifest == null) throw new InvalidDataException("Manifest absent.");
        string geometryPath = Payload(manifestPath, manifest.geometry), statePath = Payload(manifestPath, manifest.state);
        if (Sha(geometryPath) != manifest.geometrySha256 || Sha(statePath) != manifest.stateSha256) throw new InvalidDataException("Captured payload hash differs.");
        ResidentGeometry geometry = ResidentMaterialFormat.ReadGeometry(geometryPath);
        ResidentSnapshot initial = ResidentMaterialFormat.ReadSnapshot(statePath);
        ResidentMaterialFormat.ValidateManifest(manifest, geometry); ResidentMaterialFormat.ValidateSnapshot(manifest, initial);
        if (initial.generation != 0) throw new InvalidDataException("Reload fixtures require captured generation zero.");
        ResidentSnapshot unchanged = ResidentMaterialFormat.ReadSnapshot(statePath); unchanged.generation = 1;
        ResidentSnapshot visible = ResidentMaterialFormat.ReadSnapshot(statePath); visible.generation = 2;
        foreach (ResidentInstanceState row in visible.instances) { row.matrix[3] += 3f; row.emission[0] += .25f; }
        ResidentMaterialFormat.ValidateSnapshot(manifest, unchanged); ResidentMaterialFormat.ValidateSnapshot(manifest, visible);
        ResidentSnapshot wrong = ResidentMaterialFormat.ReadSnapshot(statePath); wrong.generation = 3;
        wrong.topologyId = (manifest.topologyId[0] == 'a' ? "b" : "a") + manifest.topologyId.Substring(1);
        if (Sha(geometryPath) != manifest.geometrySha256 || Sha(statePath) != manifest.stateSha256) throw new InvalidDataException("Captured payload changed while deriving fixtures.");
        Directory.CreateDirectory(output);
        string one = Path.Combine(output, "state-generation-1.bin"), two = Path.Combine(output, "state-generation-2.bin"), three = Path.Combine(output, "state-wrong-topology-3.bin"), truncated = Path.Combine(output, "state-truncated.bin");
        ResidentMaterialFormat.WriteSnapshot(one, unchanged); ResidentMaterialFormat.WriteSnapshot(two, visible); ResidentMaterialFormat.WriteSnapshot(three, wrong);
        byte[] bytes = File.ReadAllBytes(two);
        using (FileStream stream = new FileStream(truncated, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length - 1);
        string receipt = JsonSerializer.Serialize(new { schema = "continuum-resident-reload-fixtures/v1", manifest = manifestPath, manifestSha256 = Sha(manifestPath), originalState = statePath, originalStateSha256 = Sha(statePath), originalGeneration = initial.generation, topologyId = manifest.topologyId, instances = initial.instances.Length,
            visibleChange = "Every row: root-local matrix translation x += 3 meters; emission red += 0.25; all other fields unchanged.",
            outputs = new[] { new { path = one, sha256 = Sha(one), purpose = "unchanged-newer-generation-1" }, new { path = two, sha256 = Sha(two), purpose = "visible-change-generation-2" }, new { path = three, sha256 = Sha(three), purpose = "reject-wrong-topology-generation-3" }, new { path = truncated, sha256 = Sha(truncated), purpose = "reject-truncated-generation-2" } } }, new JsonSerializerOptions { WriteIndented = true });
        using (StreamWriter writer = new StreamWriter(new FileStream(Path.Combine(output, "fixtures.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))) writer.Write(receipt);
        Console.WriteLine("Offline reload fixtures: " + output); return 0;
    }
    static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            if (args.Length != 3 || args[0] != "--snapshots") throw new ArgumentException("Usage: --snapshots ABS_MANIFEST ABS_NEW_DIRECTORY");
            return Snapshots(args[1], args[2]);
        }
        string root = Path.Combine(Path.GetTempPath(), "continuum-resident-proof-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string geometryPath = Path.Combine(root, "geometry.bin"), statePath = Path.Combine(root, "state.bin");
            ResidentGeometry geometry = Geometry(); ResidentSnapshot snapshot = Snapshot(); ResidentMaterialManifest manifest = Manifest();
            ResidentMaterialFormat.ValidateManifest(manifest, geometry); ResidentMaterialFormat.ValidateSnapshot(manifest, snapshot);
            ResidentMaterialManifest jsonManifest = JsonSerializer.Deserialize<ResidentMaterialManifest>(ReportJson.Encode(manifest), new JsonSerializerOptions { IncludeFields = true });
            ResidentMaterialFormat.ValidateManifest(jsonManifest, geometry);
            Check(jsonManifest.materials.Length == 1 && jsonManifest.materials[0].shader == "Continuum/FlightPartSpecularInstanced" && jsonManifest.family == manifest.family, "Prepared shader/family JSON identity lost.");
            Check(jsonManifest.instances.Length == 1 && jsonManifest.instances[0].id == "instance" && jsonManifest.instances[0].meshIndex == 0 && jsonManifest.instances[0].materialIndex == 0, "Instance JSON identity/references lost.");
            Check(jsonManifest.textures.Length == 1 && jsonManifest.textures[0].id == "texture" && jsonManifest.textures[0].linear && jsonManifest.textures[0].payloadLength == 4 && jsonManifest.textures[0].payloadSha256 == Digest, "Texture JSON payload identity lost.");
            ResidentMaterialProperty[] properties = jsonManifest.materials[0].properties;
            Check(properties.Length == 3 && properties[0].value[2] == .75f && properties[1].value[0] == .5f && properties[2].textureIndex == 0 && properties[2].value.Length == 0 && properties[2].scaleOffset[2] == .25f, "Nested material JSON values/binding lost.");
            Check(jsonManifest.rootToWorld.Length == 16 && jsonManifest.rootToWorld[15] == 1 && jsonManifest.cameraPosition[2] == -10 && jsonManifest.cameraRotation[3] == 1 && jsonManifest.fov == 60 && jsonManifest.near == .1f && jsonManifest.far == 100 && jsonManifest.lighting.direction[2] == 1 && jsonManifest.lighting.color[3] == 1 && jsonManifest.lighting.ambientMode == 3, "Root/camera/lighting JSON fields lost.");
            ResidentMaterialFormat.WriteGeometry(geometryPath, geometry); ResidentMaterialFormat.WriteSnapshot(statePath, snapshot);
            byte[] meshBytes = File.ReadAllBytes(geometryPath), stateBytes = File.ReadAllBytes(statePath);
            // Fixed analytic wire offsets are independent of the codec's read implementation.
            Check(meshBytes.Length == 176 && BitConverter.ToInt32(meshBytes, 0) == 0x32474d52 && BitConverter.ToInt32(meshBytes, 8) == 1 && BitConverter.ToInt32(meshBytes, 12) == 3 && BitConverter.ToInt32(meshBytes, 16) == 3, "Analytic geometry wire shape differs.");
            Check(BitConverter.ToSingle(meshBytes, 20 + 48) == 1 && BitConverter.ToSingle(meshBytes, 20 + 48 + 28) == -1 && BitConverter.ToInt32(meshBytes, 168) == 1, "Analytic interleaving/UV/index differs.");
            Check(stateBytes.Length == 300 && BitConverter.ToInt32(stateBytes, 0) == 0x32534d52 && stateBytes[8] == 0xaa && BitConverter.ToInt64(stateBytes, 40) == 7 && BitConverter.ToInt32(stateBytes, 48) == 1, "Analytic snapshot wire shape differs.");
            Check(BitConverter.ToSingle(stateBytes, 52 + 29 * 4) == 0 && BitConverter.ToSingle(stateBytes, 52 + 30 * 4) == 1 && BitConverter.ToSingle(stateBytes, 52 + 40 * 4) == 22, "Raw/effective opacity or channel-major SH conflated.");
            ResidentGeometry decoded = ResidentMaterialFormat.ReadGeometry(geometryPath); ResidentSnapshot state = ResidentMaterialFormat.ReadSnapshot(statePath);
            Check(decoded.meshes[0].tangents[7] == -1 && decoded.meshes[0].uv[3] == -1, "Signed tangents/UV lost.");
            float[] a = state.instances[0].matrix; Check(a[0] + 2 * a[1] + 3 * a[2] + a[3] == 9.5f && a[5] * 2 == 6 && a[10] * 3 == 12, "Affine shear/scale/translation analytic point changed.");
            Reject(() => ResidentMaterialFormat.WriteGeometry(geometryPath, geometry), "Writer overwrote immutable geometry.");
            Reject(() => ResidentMaterialFormat.WriteSnapshot(statePath, snapshot), "Writer overwrote state publication.");

            byte[] truncated = new byte[meshBytes.Length - 1]; System.Array.Copy(meshBytes, truncated, truncated.Length);
            Reject(() => ResidentMaterialFormat.ReadGeometry(Mutate(root, "truncated-mesh", truncated)), "Truncated mesh admitted.");
            byte[] trailing = new byte[stateBytes.Length + 1]; System.Array.Copy(stateBytes, trailing, stateBytes.Length);
            Reject(() => ResidentMaterialFormat.ReadSnapshot(Mutate(root, "trailing-state", trailing)), "Trailing state admitted.");

            byte[] index = (byte[])meshBytes.Clone(); System.Array.Copy(BitConverter.GetBytes(3), 0, index, 164, 4);
            Reject(() => ResidentMaterialFormat.ReadGeometry(Mutate(root, "bad-index", index)), "Out-of-range triangle admitted.");
            byte[] budget = (byte[])meshBytes.Clone(); System.Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, budget, 12, 4);
            Reject(() => ResidentMaterialFormat.ReadGeometry(Mutate(root, "bad-capacity", budget)), "Untrusted count allocated/admitted.");

            byte[] affine = (byte[])stateBytes.Clone(); System.Array.Copy(BitConverter.GetBytes(1f), 0, affine, 52 + 12 * 4, 4);
            Reject(() => ResidentMaterialFormat.ReadSnapshot(Mutate(root, "projective-state", affine)), "Projective matrix admitted.");
            byte[] nan = (byte[])stateBytes.Clone(); System.Array.Copy(BitConverter.GetBytes(float.NaN), 0, nan, 52 + 31 * 4, 4);
            Reject(() => ResidentMaterialFormat.ReadSnapshot(Mutate(root, "nan-state", nan)), "Nonfinite probe admitted.");

            ResidentSnapshot wrong = Snapshot(); wrong.topologyId = new string('b', 64);
            Reject(() => ResidentMaterialFormat.ValidateSnapshot(manifest, wrong), "Wrong asset generation admitted.");
            wrong = Snapshot(); wrong.instances = new[] { wrong.instances[0], wrong.instances[0] };
            Reject(() => ResidentMaterialFormat.ValidateSnapshot(manifest, wrong), "Wrong instance count admitted.");
            manifest.instances = new[] { manifest.instances[0], manifest.instances[0] };
            Reject(() => ResidentMaterialFormat.ValidateManifest(manifest, geometry), "Duplicate stable instance identity admitted.");
            Console.WriteLine("Resident material codec proofs: " + checks + " checks."); return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
