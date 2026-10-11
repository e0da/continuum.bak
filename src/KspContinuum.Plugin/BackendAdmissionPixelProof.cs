using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    public static class BackendAdmissionPixelProof
    {
        [Serializable]
        public sealed class Report
        {
            public string schema = "continuum-backend-admission-pixel/v1";
            public string runId, unity, actualBackend, colorSpace, bundlePath, bundleSha256, shaderName;
            public string status = "pending", error, cleanupStatus, pngPath, pngSha256;
            public int processId, width = 64, height = 64, checkedPixels, mismatchedPixels;
            public int releasedMaterials, releasedTextures, releasedTargets, releasedBundles;
            public bool shaderSupported, originalRenderTargetRestored;
            public float tolerance = 1f / 255f;
            public float[] expected = { .125f, .5f, .875f, 1f };
            public float[] centerPixels, maximumChannelErrors;
            public string scope = "Owned offscreen analytic shader pixel proof only; excludes stock UI/world/vessel shader closure and performance.";
        }

        static string Argument(string name)
        {
            string[] matches = Environment.GetCommandLineArgs().Where(arg => arg.StartsWith(name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].Length == name.Length) throw new InvalidOperationException("Exactly one " + name + " argument required.");
            return matches[0].Substring(name.Length);
        }

        static void Cleanup(Action action, Report report)
        {
            try { action(); }
            catch (Exception error) { report.status = "failed"; report.error = (report.error ?? "") + " Cleanup: " + error; Debug.LogException(error); }
        }

        public static IEnumerator Run()
        {
            var report = new Report { unity = Application.unityVersion, actualBackend = SystemInfo.graphicsDeviceType.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(), processId = System.Diagnostics.Process.GetCurrentProcess().Id };
            string directory = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData"));
            string output = Path.Combine(directory, "backend-admission-pixel-failed-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json");
            AssetBundle bundle = null; Shader ownedShader = null; Material material = null; Texture2D pixels = null; RenderTexture target = null;
            RenderTexture previous = null;
            bool targetCaptured = false;
            // MainMenu observers execute before the owned end-of-frame proof and its final exit.
            yield return new WaitForEndOfFrame();
            try
            {
                previous = RenderTexture.active; targetCaptured = true;
                report.runId = Argument("--continuum-research-run=");
                if (report.runId.Length > 96 || report.runId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                    throw new InvalidOperationException("Invalid owned run identity.");
                Directory.CreateDirectory(directory);
                output = Path.Combine(directory, "backend-admission-pixel-" + report.runId + ".json");
                report.pngPath = Path.ChangeExtension(output, ".png");
                if (File.Exists(output) || File.Exists(report.pngPath)) throw new InvalidOperationException("Pixel proof outputs already exist.");
                report.bundlePath = Argument("--continuum-backend-pixel-proof-bundle=");
                if (!Path.IsPathRooted(report.bundlePath) || !File.Exists(report.bundlePath)) throw new InvalidOperationException("Existing absolute proof bundle required.");
                report.bundleSha256 = ScaleCheckpointLoadState.HashFile(report.bundlePath);
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal) throw new InvalidOperationException("Actual Metal backend required.");
                if (QualitySettings.activeColorSpace != ColorSpace.Gamma) throw new InvalidOperationException("Frozen Gamma pixel recipe required.");
                bundle = AssetBundle.LoadFromFile(report.bundlePath);
                if (bundle == null) throw new InvalidOperationException("Proof bundle failed to load.");
                Shader[] shaders = bundle.LoadAllAssets<Shader>().Where(shader => shader.name == "Hidden/Continuum/BackendAdmissionProof").ToArray();
                if (shaders.Length != 1) throw new InvalidOperationException("Unique proof shader required.");
                ownedShader = shaders[0]; report.shaderName = ownedShader.name; report.shaderSupported = ownedShader.isSupported;
                if (!report.shaderSupported) throw new InvalidOperationException("Native proof shader unsupported.");
                material = new Material(shaders[0]);
                target = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { antiAliasing = 1, useMipMap = false };
                if (!target.Create()) throw new InvalidOperationException("Owned target creation failed.");
                Graphics.Blit(null, target, material, 0);
                RenderTexture.active = target;
                pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0, false); pixels.Apply(false, false);
                Color32[] observed = pixels.GetPixels32();
                report.maximumChannelErrors = new float[4]; report.centerPixels = new float[16];
                for (int i = 0; i < observed.Length; i++)
                {
                    Color32 pixel = observed[i]; float[] channels = { pixel.r / 255f, pixel.g / 255f, pixel.b / 255f, pixel.a / 255f };
                    bool mismatch = false;
                    for (int c = 0; c < 4; c++)
                    {
                        float difference = Mathf.Abs(channels[c] - report.expected[c]);
                        report.maximumChannelErrors[c] = Mathf.Max(report.maximumChannelErrors[c], difference);
                        mismatch |= difference > report.tolerance;
                    }
                    int x = i % 64, y = i / 64;
                    if (x >= 31 && x <= 32 && y >= 31 && y <= 32)
                        Array.Copy(channels, 0, report.centerPixels, ((y - 31) * 2 + x - 31) * 4, 4);
                    report.checkedPixels++;
                    if (mismatch) report.mismatchedPixels++;
                }
                using (var stream = new FileStream(report.pngPath, FileMode.CreateNew, FileAccess.Write))
                {
                    byte[] png = pixels.EncodeToPNG(); stream.Write(png, 0, png.Length);
                }
                report.pngSha256 = ScaleCheckpointLoadState.HashFile(report.pngPath);
                if (ScaleCheckpointLoadState.HashFile(report.bundlePath) != report.bundleSha256) throw new InvalidOperationException("Proof bundle changed.");
                if (report.checkedPixels != 4096 || report.mismatchedPixels != 0) throw new InvalidOperationException("Frozen analytic pixel contract failed.");
                report.status = "pixels-verified";
            }
            catch (Exception error)
            {
                report.status = "failed"; report.error = error.ToString(); Debug.LogException(error);
            }
            finally
            {
                if (targetCaptured) Cleanup(() => { RenderTexture.active = previous; report.originalRenderTargetRestored = RenderTexture.active == previous; }, report);
                else report.originalRenderTargetRestored = true;
                if (material != null) Cleanup(() => { UnityEngine.Object.Destroy(material); report.releasedMaterials = 1; }, report);
                if (pixels != null) Cleanup(() => { UnityEngine.Object.Destroy(pixels); report.releasedTextures = 1; }, report);
                if (target != null) Cleanup(() => { target.Release(); UnityEngine.Object.Destroy(target); report.releasedTargets = 1; }, report);
                if (bundle != null) Cleanup(() => { bundle.Unload(true); report.releasedBundles = 1; }, report);
            }
            yield return new WaitForEndOfFrame();
            bool cleaned = material == null && pixels == null && target == null && ownedShader == null &&
                (bundle == null || report.releasedBundles == 1) && report.originalRenderTargetRestored;
            report.cleanupStatus = cleaned ? "released-owned-resources-restored-target" : "cleanup-failed";
            if (!cleaned) { report.status = "failed"; report.error = (report.error ?? "") + " Owned cleanup not observed."; }
            if (report.status == "pixels-verified") report.status = "complete";
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream)) writer.Write(JsonUtility.ToJson(report, true));
            }
            catch (Exception error) { Debug.LogException(error); report.status = "failed"; }
            Debug.Log("[Continuum backend pixel proof] run=" + report.runId + " status=" + report.status + " backend=" + report.actualBackend);
            Application.Quit(report.status == "complete" ? 0 : 2);
        }
    }
}
