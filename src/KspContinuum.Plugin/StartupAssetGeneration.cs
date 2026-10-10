using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KspContinuum
{
    [Serializable] public sealed class AssetGenerationCapability
    {
        public string name, state = "requested", reason;
        public long publications, expectedPublications, fallbacks;
        public bool admissionPassed;
    }
    [Serializable] public sealed class AssetGenerationReport
    {
        public string schema = "continuum-startup-asset-generation/v1";
        public string runId, instanceRoot, launchProfileSha256, assetGenerationSha256, phase, status;
        public int processId;
        public bool declared, admitted, ready;
        public long recordingFailures;
        public string recordingFailure;
        public AssetGenerationCapability[] capabilities;
    }

    // Launch digests bind the supervisor's declarations; existing providers still verify content.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupAssetGeneration : MonoBehaviour
    {
        static readonly Dictionary<string, AssetGenerationCapability> capabilities = new Dictionary<string, AssetGenerationCapability>(StringComparer.Ordinal);
        static AssetGenerationReport report;
        static bool initialized, observedMenu;
        static bool identityFailed;
        static long recordingFailures;
        static string recordingFailure;
        const string Marker = "[Continuum asset generation] ";

        static string Argument(string[] args, string prefix, bool required)
        {
            string[] values = args.Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (values.Length == 0 && !required) return null;
            if (values.Length != 1 || values[0].Length == prefix.Length) throw new InvalidOperationException("One nonempty generation argument required: " + prefix);
            return values[0].Substring(prefix.Length);
        }
        static bool Digest(string value) { return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        static void Initialize()
        {
            if (initialized) return;
            string[] args = Environment.GetCommandLineArgs();
            string launch = Argument(args, "--continuum-launch-profile-sha256=", false);
            string generation = Argument(args, "--continuum-asset-generation-sha256=", false);
            if (launch == null && generation == null) { initialized = true; return; }
            if (!Digest(launch) || !Digest(generation)) throw new InvalidOperationException("Both canonical generation digests required.");
            string run = Argument(args, "--continuum-research-run=", true);
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar);
            StartupPinnedGameIdentity.Require(typeof(GameDatabase).Assembly);
            report = new AssetGenerationReport { runId = run, instanceRoot = root, launchProfileSha256 = launch, assetGenerationSha256 = generation,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, phase = "startup", status = "declared", declared = true };
            if (args.Contains("--continuum-startup-cached-catalog")) Add("catalog-metadata");
            if (Argument(args, "--continuum-startup-resource-image-mode=", false) == "reuse") Add("texture-image");
            if (args.Contains("--continuum-startup-prepared-bundles")) Add("dlc-bundles");
            initialized = true;
        }
        static void Add(string name) { capabilities.Add(name, new AssetGenerationCapability { name = name }); }
        static AssetGenerationCapability Capability(string name)
        {
            Initialize();
            AssetGenerationCapability value;
            return report != null && capabilities.TryGetValue(name, out value) ? value : null;
        }
        public static void Admit(string name, long expected)
        {
            try
            {
            AssetGenerationCapability value = Capability(name); if (value == null) return;
            if (value.state != "requested" || expected < 1) throw new InvalidOperationException("Generation admission transition differs: " + name);
            value.expectedPublications = expected; value.admissionPassed = true; value.state = "admitted";
            }
            catch (Exception error) { RecordingFailed(name, error); }
        }
        public static void Published(string name, long count = 1)
        {
            try
            {
            AssetGenerationCapability value = Capability(name); if (value == null) return;
            if (value.state == "failed") return;
            if ((value.state != "admitted" && value.state != "publishing" && value.state != "fallback") || count < 1 || count > value.expectedPublications - value.publications)
                throw new InvalidOperationException("Generation publication transition differs: " + name);
            value.publications += count; if (value.state != "fallback") value.state = "publishing";
            }
            catch (Exception error) { RecordingFailed(name, error); }
        }
        public static void Fallback(string name, string reason)
        {
            try
            {
            AssetGenerationCapability value = Capability(name); if (value == null) return;
            value.fallbacks++; if (value.state != "failed") { value.reason = reason; value.state = "fallback"; }
            }
            catch (Exception error) { RecordingFailed(name, error); }
        }
        public static void Complete(string name)
        {
            try
            {
            AssetGenerationCapability value = Capability(name); if (value == null || value.state == "fallback" || value.state == "failed") return;
            if (value.state != "publishing" || value.publications != value.expectedPublications)
            { value.state = "incomplete"; value.reason = "publication-coverage-incomplete"; return; }
            value.state = "ready";
            }
            catch (Exception error) { RecordingFailed(name, error); }
        }
        static void RecordingFailed(string name, Exception error)
        {
            // Diagnostics must never roll back or fall through an already published provider object.
            if (!initialized) { identityFailed = true; initialized = true; }
            recordingFailures++;
            AssetGenerationCapability value;
            if (name != null && capabilities.TryGetValue(name, out value))
            { value.state = "failed"; value.reason = "lifecycle-recording-failed"; }
            if (recordingFailure != null) return;
            recordingFailure = error.GetType().Name;
            try { Debug.LogError(Marker + "status=recording-failed error=" + recordingFailure); }
            catch (Exception) { }
        }
        public void Awake()
        {
            try { Initialize(); if (identityFailed) { Application.Quit(2); return; } if (report != null) DontDestroyOnLoad(gameObject); }
            catch (Exception error) { RecordingFailed(null, error); Application.Quit(2); }
        }
        public static void ObserveMenu()
        {
            if (observedMenu || HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            // The benchmark can quit from Start before lifecycle Update is called.
            StartupTexturePrefetch.ObserveForMenu(); StartupAudioPrefetch.ObserveForMenu(); StartupResourceImage.ObserveForMenu();
            StartupReadiness.ObserveForMenu(); StartupWorkScheduler.ObserveForMenu(); StartupWorldTiming.ObserveForMenu();
            StartupBundleAttribution.ObserveForMenu();
            try { Initialize(); observedMenu = true; Export("menu"); }
            catch (Exception error) { RecordingFailed(null, error); }
        }
        static void Export(string phase)
        {
            if (report == null) return;
            report.phase = phase; report.capabilities = capabilities.Values.OrderBy(c => c.name, StringComparer.Ordinal).ToArray();
            report.recordingFailures = recordingFailures; report.recordingFailure = recordingFailure;
            report.admitted = !identityFailed && report.capabilities.Length > 0 && report.capabilities.All(c => c.admissionPassed);
            report.ready = recordingFailures == 0 && report.capabilities.Length > 0 && report.capabilities.All(c => c.state == "ready");
            report.status = recordingFailures > 0 ? "degraded" : report.capabilities.Length == 0 ? "no-prepared-capabilities-requested" : report.ready ? "ready" : "degraded";
            Debug.Log(Marker + JsonUtility.ToJson(report));
            string directory = Path.Combine(report.instanceRoot, "GameData", "KspContinuum", "PluginData");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "asset-generation-" + report.processId + "-" + phase + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(report));
        }
        public void Update() { ObserveMenu(); }
        public void OnApplicationQuit() { try { Export("shutdown"); } catch (Exception error) { RecordingFailed(null, error); } }
    }
}
