using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public sealed class BackendMenuPresentation : MonoBehaviour
    {
        [Serializable] public sealed class FileRow { public string relativeDataPath, sourceSha256, derivedSha256, derivedPath; }
        [Serializable] public sealed class Recipe { public string schema, unity; public FileRow[] files; public string[] requiredShaders; }
        [Serializable] public sealed class ShaderRow { public string name; public bool present, supported; }
        [Serializable] public sealed class MaterialRow { public string renderer, material, shader; public int rendererId, materialId, shaderId, renderQueue; public bool supported; public string[] textureProperties, textureNames, keywords, floatProperties, vectorProperties; public float[] floatValues; public float[][] vectorValues; }
        [Serializable] public sealed class CameraRow { public string name, clearFlags, renderingPath, target; public int id, mask, targetSamples; public float depth, near, far; public bool allowMSAA; }
        [Serializable] public sealed class Report
        {
            public string schema = "continuum-metal-menu-presentation-observation/v1", runId, status, error, actualBackend, unity;
            public string recipePath, recipeSha256, pngPath, pngSha256, cleanupStatus;
            public int processId, width, height, activeRenderers, unsupportedMaterials, backgroundCameras, releasedReadbackTextures, antiAliasing;
            public bool originalRenderTargetRestored, reference;
            public float presentationWaitSeconds; public bool presentationReady;
            public ShaderRow[] requiredShaders; public MaterialRow[] materials; public CameraRow[] cameras; public string[] menuTexts;
            public string scope = "Actual full-window menu capture and structural shader/material observations. Text, controls, background appearance and interaction require a separate inspected visual qualification; no performance claim.";
        }
        public static bool Requested { get { return Environment.GetCommandLineArgs().Any(a => a.StartsWith("--continuum-backend-menu-presentation=", StringComparison.Ordinal)); } }
        internal static bool MaterialFactsPending { get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_MENU_MATERIAL_FACTS")) && !materialFactsComplete; } }
        static bool materialFactsComplete;
        static string Argument(string prefix)
        {
            string[] values = Environment.GetCommandLineArgs().Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (values.Length != 1 || values[0].Length == prefix.Length) throw new InvalidOperationException("One " + prefix + " argument required.");
            return values[0].Substring(prefix.Length);
        }
        static bool Digest(string value) { return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        IEnumerator Start()
        {
            if (!Requested)
            {
                string facts = Environment.GetEnvironmentVariable("CONTINUUM_MENU_MATERIAL_FACTS");
                if (!string.IsNullOrEmpty(facts)) yield return CaptureMaterialFacts(facts);
                yield break;
            }
            var report = new Report { status = "failed", actualBackend = SystemInfo.graphicsDeviceType.ToString(), unity = Application.unityVersion,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                reference = Environment.GetCommandLineArgs().Contains("--continuum-backend-menu-reference") };
            string directory = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KspContinuum/PluginData"));
            string output = Path.Combine(directory, "menu-presentation-failed-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json");
            float waitStart = Time.realtimeSinceStartup, readyAt = -1;
            while (Time.realtimeSinceStartup - waitStart < 30)
            {
                MainMenu candidate = UnityEngine.Object.FindObjectOfType<MainMenu>();
                bool ready = candidate != null && candidate.envLogic != null && MenuTexts(candidate).Length > 0;
                if (ready && readyAt < 0) readyAt = Time.realtimeSinceStartup;
                if (!ready) readyAt = -1;
                if (readyAt >= 0 && Time.realtimeSinceStartup - readyAt >= 3) { report.presentationReady = true; break; }
                yield return null;
            }
            report.presentationWaitSeconds = Time.realtimeSinceStartup - waitStart;
            yield return new WaitForEndOfFrame();
            Texture2D readback = null; RenderTexture previous = RenderTexture.active;
            try
            {
                report.runId = Argument("--continuum-research-run=");
                if (report.runId.Length > 96 || report.runId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new InvalidOperationException("Owned run identity invalid.");
                output = Path.Combine(directory, "menu-presentation-" + report.runId + ".json"); report.pngPath = Path.ChangeExtension(output, ".png");
                Directory.CreateDirectory(directory);
                if (File.Exists(output) || File.Exists(report.pngPath)) throw new InvalidOperationException("New menu evidence required.");
                report.recipePath = Argument("--continuum-backend-menu-presentation=");
                if (!Path.IsPathRooted(report.recipePath) || !File.Exists(report.recipePath) || new FileInfo(report.recipePath).Length > 1024 * 1024) throw new InvalidOperationException("Bounded absolute menu recipe required.");
                report.recipeSha256 = ScaleCheckpointLoadState.HashFile(report.recipePath);
                string recipeJson = File.ReadAllText(report.recipePath);
                Recipe recipe = JsonUtility.FromJson<Recipe>(recipeJson);
                if (recipe != null)
                {
                    recipe.files = StartupCacheManifestReader.ReadMenuFileObjects(recipeJson).Select(value => JsonUtility.FromJson<FileRow>(value)).ToArray();
                    recipe.requiredShaders = StartupCacheManifestReader.ReadMenuShaderNames(recipeJson);
                }
                if (recipe == null || recipe.schema != "continuum-metal-menu-presentation/v1" || recipe.unity != "2019.4.18f1" ||
                    recipe.files == null || recipe.files.Length == 0 || recipe.files.Length > 16 || recipe.requiredShaders == null || recipe.requiredShaders.Length == 0 || recipe.requiredShaders.Length > 64)
                    throw new InvalidOperationException("Menu recipe schema/closure invalid.");
                if (SystemInfo.graphicsDeviceType != (report.reference ? GraphicsDeviceType.OpenGLCore : GraphicsDeviceType.Metal) || Application.unityVersion != recipe.unity) throw new InvalidOperationException("Actual matching declared runtime required.");
                string data = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "KSP.app/Contents/Resources/Data"));
                foreach (FileRow row in recipe.files)
                {
                    if (row == null || Path.IsPathRooted(row.relativeDataPath) || !Digest(row.sourceSha256) || !Digest(row.derivedSha256)) throw new InvalidOperationException("Menu file identity invalid.");
                    if (report.reference && row.sourceSha256 != row.derivedSha256) throw new InvalidOperationException("Reference requires unchanged original file identities.");
                    string target = Path.GetFullPath(Path.Combine(data, row.relativeDataPath));
                    if (!target.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal) || ScaleCheckpointLoadState.HashFile(target) != row.derivedSha256)
                        throw new InvalidOperationException("Installed menu file identity differs.");
                }
                report.requiredShaders = recipe.requiredShaders.Select(name => { Shader shader = Shader.Find(name); return new ShaderRow { name = name, present = shader != null, supported = shader != null && shader.isSupported }; }).ToArray();
                var materials = new List<MaterialRow>();
                Camera[] activeCameras = Camera.allCameras.Where(c => c.enabled && c.gameObject.activeInHierarchy).ToArray();
                Plane[][] frusta = activeCameras.Select(GeometryUtility.CalculateFrustumPlanes).ToArray();
                foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>().Where(r => r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy && r.gameObject.scene.IsValid()))
                {
                    bool visible = false;
                    for (int i = 0; i < activeCameras.Length; i++) if ((activeCameras[i].cullingMask & (1 << renderer.gameObject.layer)) != 0 && GeometryUtility.TestPlanesAABB(frusta[i], renderer.bounds)) { visible = true; break; }
                    if (!visible) continue;
                    report.activeRenderers++;
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null) continue;
                        materials.Add(MaterialFacts(renderer, material));
                    }
                }
                report.materials = materials.ToArray(); report.unsupportedMaterials = materials.Count(m => !m.supported);
                report.cameras = activeCameras.Select(c => new CameraRow { name = c.name, id = c.GetInstanceID(), mask = c.cullingMask, depth = c.depth, near = c.nearClipPlane, far = c.farClipPlane, clearFlags = c.clearFlags.ToString(), renderingPath = c.actualRenderingPath.ToString(), allowMSAA = c.allowMSAA, target = c.targetTexture == null ? "screen" : c.targetTexture.name, targetSamples = c.targetTexture == null ? 0 : c.targetTexture.antiAliasing }).ToArray();
                report.antiAliasing = QualitySettings.antiAliasing;
                report.backgroundCameras = report.cameras.Length;
                MainMenu menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
                report.menuTexts = MenuTexts(menu); report.width = Screen.width; report.height = Screen.height;
                if (report.width < 640 || report.height < 360 || report.width > 8192 || report.height > 8192) throw new InvalidOperationException("Menu window dimensions invalid.");
                RenderTexture.active = null;
                readback = new Texture2D(report.width, report.height, TextureFormat.RGB24, false);
                readback.ReadPixels(new Rect(0, 0, report.width, report.height), 0, 0, false); readback.Apply(false, false);
                byte[] png = readback.EncodeToPNG(); using (var stream = new FileStream(report.pngPath, FileMode.CreateNew, FileAccess.Write)) stream.Write(png, 0, png.Length);
                report.pngSha256 = ScaleCheckpointLoadState.HashFile(report.pngPath);
                if (ScaleCheckpointLoadState.HashFile(report.recipePath) != report.recipeSha256) throw new InvalidOperationException("Menu recipe changed.");
                if (!report.presentationReady || report.requiredShaders.Any(s => !s.present || !s.supported) || report.unsupportedMaterials > 0 || menu == null || report.menuTexts.Length == 0 || report.backgroundCameras == 0)
                    throw new InvalidOperationException("Menu structural closure incomplete; window evidence retained.");
                report.status = report.reference ? "captured-reference-structural" : "captured-structural";
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally
            {
                RenderTexture.active = previous; report.originalRenderTargetRestored = RenderTexture.active == previous;
                if (readback != null) { UnityEngine.Object.Destroy(readback); report.releasedReadbackTextures = 1; }
            }
            yield return new WaitForEndOfFrame();
            report.cleanupStatus = readback == null && report.originalRenderTargetRestored ? "released-readback-restored-target" : "cleanup-failed";
            if (report.cleanupStatus == "cleanup-failed") report.status = "failed";
            try { Directory.CreateDirectory(directory); using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream)) writer.Write(ReportJson.Encode(report)); }
            catch (Exception error) { report.status = "failed"; Debug.LogException(error); }
            Application.Quit(report.status == "captured-structural" || report.status == "captured-reference-structural" ? 0 : 2);
        }
        static MaterialRow MaterialFacts(Renderer renderer, Material material)
        {
            string[] properties = material.GetTexturePropertyNames(); var floats = new List<string>(); var values = new List<float>(); var vectors = new List<string>(); var vectorValues = new List<float[]>();
            Shader shader = material.shader;
            if (shader != null) for (int i=0; i<shader.GetPropertyCount(); i++)
            {
                string name=shader.GetPropertyName(i); ShaderPropertyType type=shader.GetPropertyType(i);
                if (type==ShaderPropertyType.Float || type==ShaderPropertyType.Range) { floats.Add(name); values.Add(material.GetFloat(name)); }
                else if (type==ShaderPropertyType.Color || type==ShaderPropertyType.Vector) { Vector4 v=type==ShaderPropertyType.Color ? (Vector4)material.GetColor(name) : material.GetVector(name); vectors.Add(name); vectorValues.Add(new[] {v.x,v.y,v.z,v.w}); }
            }
            return new MaterialRow { renderer=renderer.name,rendererId=renderer.GetInstanceID(),material=material.name,materialId=material.GetInstanceID(),shader=shader==null?null:shader.name,
                shaderId=shader==null?0:shader.GetInstanceID(),supported=shader!=null&&shader.isSupported,renderQueue=material.renderQueue,keywords=material.shaderKeywords,
                textureProperties=properties,textureNames=properties.Select(p=>{Texture t=material.GetTexture(p);return t==null?null:t.name;}).ToArray(),floatProperties=floats.ToArray(),floatValues=values.ToArray(),vectorProperties=vectors.ToArray(),vectorValues=vectorValues.ToArray() };
        }
        IEnumerator CaptureMaterialFacts(string output)
        {
            float started=Time.realtimeSinceStartup; while(Time.realtimeSinceStartup-started<3) yield return null;
            yield return new WaitForEndOfFrame();
            var report=new Report { status="material-facts-failed",actualBackend=SystemInfo.graphicsDeviceType.ToString(),unity=Application.unityVersion,processId=System.Diagnostics.Process.GetCurrentProcess().Id,
                scope="Cold actual menu material keywords/parameters before owned flight loading. No screenshot, interaction, fidelity or performance qualification." };
            try
            {
                report.runId=Argument("--continuum-research-run=");
                if(!Path.IsPathRooted(output)||File.Exists(output)||Directory.Exists(output)) throw new InvalidDataException("New absolute menu material evidence path required.");
                report.materials=Resources.FindObjectsOfTypeAll<Renderer>().Where(r=>r.gameObject.activeInHierarchy&&r.gameObject.scene.IsValid()).SelectMany(r=>r.sharedMaterials.Where(m=>m!=null).Select(m=>MaterialFacts(r,m))).ToArray();
                report.activeRenderers=report.materials.Select(m=>m.rendererId).Distinct().Count(); report.status="captured-material-facts";
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                using(var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write)) using(var writer=new StreamWriter(stream)) writer.Write(ReportJson.Encode(report));
            }
            catch(Exception error) { Debug.LogException(error); }
            finally { materialFactsComplete=true; }
        }
        static string[] MenuTexts(MainMenu menu)
        {
            var texts = new List<string>();
            if (menu == null || menu.envLogic == null) return texts.ToArray();
            var field = menu.envLogic.GetType().GetField("uiTexts"); Array values = field == null ? null : field.GetValue(menu.envLogic) as Array;
            if (values != null) foreach (object text in values)
            {
                if (text == null) continue;
                var property = text.GetType().GetProperty("text"); string value = property == null ? null : property.GetValue(text, null) as string;
                if (!string.IsNullOrEmpty(value)) texts.Add(value);
            }
            return texts.ToArray();
        }
    }
}
