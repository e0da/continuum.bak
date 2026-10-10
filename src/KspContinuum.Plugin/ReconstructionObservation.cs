using System;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public sealed class ReconstructionObservation : MonoBehaviour
    {
        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-native-boundary-bench") < 0) return;
            try
            {
                StartupPresentationExperiment.RestoreForMenu();
                string version = Versioning.GetVersionStringFull();
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
                Directory.CreateDirectory(directory);
                var observation = new ManagedReconstructionObservation
                {
                    observedUtc = DateTime.UtcNow.ToString("o"), gameVersion = version,
                    assemblySha256 = ScaleCheckpointLoadState.HashFile(typeof(Versioning).Assembly.Location),
                    engineRealtimeSeconds = Time.realtimeSinceStartup, screenWidth = Screen.width, screenHeight = Screen.height,
                    vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                    graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                    graphicsDeviceName = SystemInfo.graphicsDeviceName, graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion
                };
                observation.startupUnlimitedRequested = StartupPresentationExperiment.Requested;
                observation.startupPresentationRestored = StartupPresentationExperiment.Restored;
                observation.startupUnlimitedFrames = StartupPresentationExperiment.AppliedFrames;
                observation.observerAssemblySha256 = ScaleCheckpointLoadState.HashFile(typeof(ReconstructionObservation).Assembly.Location);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetName().Name == "KSPAssets") observation.assetsAssemblySha256 = ScaleCheckpointLoadState.HashFile(assembly.Location);
                const string prefix = "--continuum-research-run=";
                foreach (string argument in Environment.GetCommandLineArgs())
                    if (argument.StartsWith(prefix, StringComparison.Ordinal)) observation.runId = argument.Substring(prefix.Length);
                string encoded = ReportJson.Encode(observation);
                string path = Path.Combine(directory, "reconstruction-" + Guid.NewGuid().ToString("N") + ".json");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream)) writer.Write(encoded);
                Debug.Log("[Continuum reconstruction] version=" + version);
            }
            catch (Exception error) { Debug.LogException(error); Application.Quit(2); }
        }

    }
}
