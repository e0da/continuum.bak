using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

// This isolates ordinary camera execution from KSP's multi-camera presentation.
public sealed class MenuShaderExecutionProof : MonoBehaviour
{
    const string ShaderName = "Sprite/Simple Texture (Unlit)";
    [Serializable] sealed class Receipt
    {
        public string schema = "continuum-menu-shader-execution-proof/v1", status = "started", error;
        public string unity, renderer, graphicsVersion, assemblySha256, donorPath, donorSha256, graftPath, graftSha256;
        public string shaderName = ShaderName, donorPngSha256, graftPngSha256, cleanupStatus;
        public string scope = "Identical isolated ordinary-camera white textured quads; no KSP scene, MSAA, menu fidelity or performance claim.";
        public int processId, size = 64, antiAliasing = 1, donorOccupiedPixels, graftOccupiedPixels;
        public bool donorSupported, graftSupported, donorPassed, graftPassed, targetRestored;
        public float[] donorCenter, graftCenter;
        public double minimumRgb = .9, maximumAlphaError = 1.0 / 255.0;
    }
    Receipt receipt;
    string directory;
    AssetBundle loaded;
    Material material;
    Mesh mesh;
    Texture2D white, readback;
    RenderTexture target, prior;
    GameObject cameraObject, quadObject;
    Camera camera;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_MENU_SHADER_PROOF_OUTPUT")))
            new GameObject("Continuum Menu Shader Execution Proof").AddComponent<MenuShaderExecutionProof>();
    }

    IEnumerator Start()
    {
        Exception failure = null;
        try { Prepare(); } catch (Exception error) { failure = error; }
        if (failure == null)
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!UnityEngine.Rendering.SplashScreen.isFinished && Time.realtimeSinceStartup < deadline) yield return null;
            try
            {
                if (!SplashScreen.isFinished) throw new InvalidOperationException("Splash screen did not finish.");
                Execute(receipt.donorPath, false);
            }
            catch (Exception error) { failure = error; }
            ReleaseStage();
            yield return new WaitForEndOfFrame();
            // Bundles deliberately retain identical container identity, so release the first before loading the second.
            if (loaded != null) { loaded.Unload(true); loaded = null; }
            yield return new WaitForEndOfFrame();
            if (failure == null)
            {
                try { Execute(receipt.graftPath, true); } catch (Exception error) { failure = error; }
            }
        }
        ReleaseStage();
        yield return new WaitForEndOfFrame();
        if (loaded != null) { loaded.Unload(true); loaded = null; }
        ReleaseScene();
        yield return new WaitForEndOfFrame();
        try
        {
            if (receipt != null)
            {
                receipt.targetRestored = RenderTexture.active == prior;
                receipt.cleanupStatus = cameraObject == null && quadObject == null && mesh == null && white == null && target == null && material == null && readback == null && loaded == null && receipt.targetRestored
                    ? "released-owned-objects-restored-target" : "cleanup-failed";
                if (Hash(receipt.donorPath) != receipt.donorSha256 || Hash(receipt.graftPath) != receipt.graftSha256)
                    throw new InvalidOperationException("Bundle input changed during proof.");
                if (failure != null) throw failure;
                if (receipt.cleanupStatus != "released-owned-objects-restored-target") throw new InvalidOperationException("Owned cleanup failed.");
                receipt.status = receipt.donorPassed && receipt.graftPassed ? "passed-both" : "execution-rejected";
            }
        }
        catch (Exception error) { failure = error; if (receipt != null) { receipt.status = "failed"; receipt.error = error.ToString(); } }
        if (receipt != null && directory != null) File.WriteAllText(Path.Combine(directory, "shader-execution.json"), JsonUtility.ToJson(receipt, true));
        if (failure != null) Debug.LogException(failure);
        Application.Quit(failure == null && receipt != null && receipt.status == "passed-both" ? 0 : 2);
    }

    void Prepare()
    {
        prior = RenderTexture.active;
        receipt = new Receipt { unity = Application.unityVersion, renderer = SystemInfo.graphicsDeviceType.ToString(), graphicsVersion = SystemInfo.graphicsDeviceVersion,
            processId = System.Diagnostics.Process.GetCurrentProcess().Id, assemblySha256 = Hash(typeof(MenuShaderExecutionProof).Assembly.Location) };
        string proposedDirectory = Absolute("CONTINUUM_MENU_SHADER_PROOF_OUTPUT");
        if (Directory.Exists(proposedDirectory) || File.Exists(proposedDirectory)) throw new InvalidOperationException("New output directory required.");
        directory = proposedDirectory;
        Directory.CreateDirectory(directory);
        receipt.donorPath = Absolute("CONTINUUM_MENU_SHADER_PROOF_DONOR"); receipt.graftPath = Absolute("CONTINUUM_MENU_SHADER_PROOF_GRAFT");
        receipt.donorSha256 = Hash(receipt.donorPath); receipt.graftSha256 = Hash(receipt.graftPath);
        if (receipt.renderer != GraphicsDeviceType.Metal.ToString() || receipt.unity != "2019.4.18f1") throw new InvalidOperationException("Pinned Metal player required.");
        white = new Texture2D(1, 1, TextureFormat.RGBA32, false); white.SetPixel(0, 0, Color.white); white.Apply(false, false);
        mesh = new Mesh { name = "Continuum Proof Quad", vertices = new[] { new Vector3(-.75f,-.75f,0), new Vector3(.75f,-.75f,0), new Vector3(.75f,.75f,0), new Vector3(-.75f,.75f,0) },
            normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back }, colors = new[] { Color.white, Color.white, Color.white, Color.white },
            uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, triangles = new[] { 0,2,1,0,3,2 } };
        mesh.RecalculateBounds();
        quadObject = new GameObject("Continuum Proof Quad") { layer = 30 }; quadObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        quadObject.AddComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        cameraObject = new GameObject("Continuum Proof Camera"); camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false; camera.transform.position = new Vector3(0,0,-3); camera.orthographic = true; camera.orthographicSize = 1;
        camera.nearClipPlane = .1f; camera.farClipPlane = 10; camera.cullingMask = 1 << 30; camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black; camera.allowHDR = false; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
        target = new RenderTexture(64,64,24,RenderTextureFormat.ARGB32) { antiAliasing = 1 }; if (!target.Create()) throw new InvalidOperationException("Target creation failed.");
        camera.targetTexture = target;
    }

    void Execute(string path, bool graft)
    {
        loaded = AssetBundle.LoadFromFile(path); if (loaded == null) throw new InvalidOperationException("Bundle load failed.");
        Shader[] matches = loaded.LoadAllAssets<Shader>().Where(shader => shader.name == ShaderName).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Exactly one explicitly loaded shader required.");
        if (graft) receipt.graftSupported = matches[0].isSupported; else receipt.donorSupported = matches[0].isSupported;
        if (!matches[0].isSupported) throw new InvalidOperationException("Selected shader unsupported.");
        material = new Material(matches[0]); material.SetTexture("_MainTex", white);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Opacity")) material.SetFloat("_Opacity", 1);
        quadObject.GetComponent<MeshRenderer>().sharedMaterial = material;
        RenderTexture previous = RenderTexture.active;
        try
        {
            camera.Render(); RenderTexture.active = target;
            readback = new Texture2D(64,64,TextureFormat.RGBA32,false); readback.ReadPixels(new Rect(0,0,64,64),0,0,false); readback.Apply(false,false);
            Color32[] pixels = readback.GetPixels32(); int occupied = pixels.Count(p => p.r > 32 || p.g > 32 || p.b > 32);
            Color center = readback.GetPixel(32,32);
            bool passed = center.r >= receipt.minimumRgb && center.g >= receipt.minimumRgb && center.b >= receipt.minimumRgb && Math.Abs(center.a - 1) <= receipt.maximumAlphaError && occupied > 1024;
            byte[] png = readback.EncodeToPNG(); File.WriteAllBytes(Path.Combine(directory, graft ? "graft.png" : "donor.png"), png);
            if (graft) { receipt.graftCenter = new[] { center.r,center.g,center.b,center.a }; receipt.graftOccupiedPixels = occupied; receipt.graftPassed = passed; receipt.graftPngSha256 = Digest(png); }
            else { receipt.donorCenter = new[] { center.r,center.g,center.b,center.a }; receipt.donorOccupiedPixels = occupied; receipt.donorPassed = passed; receipt.donorPngSha256 = Digest(png); }
        }
        finally { RenderTexture.active = previous; }
    }

    void ReleaseStage()
    {
        if (quadObject != null) quadObject.GetComponent<MeshRenderer>().sharedMaterial = null;
        if (material != null) Destroy(material); material = null;
        if (readback != null) Destroy(readback); readback = null;
    }
    void ReleaseScene()
    {
        if (camera != null) camera.targetTexture = null;
        if (cameraObject != null) Destroy(cameraObject); if (quadObject != null) Destroy(quadObject);
        if (mesh != null) Destroy(mesh); if (white != null) Destroy(white);
        if (target != null) { target.Release(); Destroy(target); }
    }
    void OnDestroy() { ReleaseStage(); if (loaded != null) loaded.Unload(true); loaded = null; ReleaseScene(); }
    static string Absolute(string name)
    {
        string path = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) throw new InvalidOperationException("Absolute " + name + " required.");
        string full = Path.GetFullPath(path);
        for (string current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Symlink input/output rejected.");
        return full;
    }
    static string Hash(string path) { using (var input = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
    static string Digest(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
}
