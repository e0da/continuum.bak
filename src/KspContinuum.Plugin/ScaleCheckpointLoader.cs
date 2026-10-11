using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace KspContinuum
{
    internal static class ScaleCheckpointLoadState
    {
        public static bool Requested { get; private set; }
        public static bool Ready { get; private set; }
        public static string Save { get; private set; }
        public static string Checkpoint { get; private set; }
        public static string SourcePath { get; private set; }
        public static string SourceSha256 { get; private set; }
        public static Guid VesselId { get; private set; }

        public static void Begin(string save, string checkpoint, string path, string hash)
        { Requested = true; Ready = false; Save = save; Checkpoint = checkpoint; SourcePath = path; SourceSha256 = hash; }
        public static void BindVessel(Guid vesselId) { VesselId = vesselId; }
        public static void Complete() { Ready = true; }
        public static bool SourceUnchanged() { return SourcePath != null && SourceSha256 != null && HashFile(SourcePath) == SourceSha256; }
        public static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class ScaleCheckpointLoader : MonoBehaviour
    {
        const float TimeoutSeconds = 120f;
        int menuReadyFrame = -1;
        float started;
        bool active, loading;
        string receiptDirectory;
        Game loadedGame;

        public void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if ((Array.IndexOf(arguments, "--continuum-scale-profile") < 0 &&
                Array.IndexOf(arguments, "--continuum-experiment-host") < 0) || !ScaleQualificationSelection.Requested(arguments)) return;
            active = true; started = Time.realtimeSinceStartup; DontDestroyOnLoad(gameObject);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnGuiReady);
            try
            {
                receiptDirectory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                    "scale-loader-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(receiptDirectory);
                ScaleQualificationSelection selection = ScaleQualificationSelection.Parse(arguments);
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "saves", selection.Save, selection.Checkpoint + ".sfs");
                RejectSourcePath(path);
                string hash = ScaleCheckpointLoadState.HashFile(path);
                ScaleCheckpointLoadState.Begin(selection.Save, selection.Checkpoint, path, hash);
                File.WriteAllText(Path.Combine(receiptDirectory, "selection.txt"), "save=" + selection.Save + "\ncheckpoint=" +
                    selection.Checkpoint + "\nsourceSha256=" + hash + "\n");
            }
            catch (Exception error) { Fail(error); }
        }

        void OnGuiReady(GameScenes scene) { if (active && !loading && scene == GameScenes.MAINMENU) menuReadyFrame = Time.frameCount; }

        public void Update()
        {
            if (!active) return;
            if (Time.realtimeSinceStartup - started > TimeoutSeconds) { Fail(new TimeoutException("Scale checkpoint did not reach Flight.")); return; }
            try
            {
                if (!loading)
                {
                    if (menuReadyFrame < 0 || Time.frameCount <= menuReadyFrame) return;
                    if (BackendMenuPresentation.MaterialFactsPending) return;
                    if (HighLogic.LoadedScene != GameScenes.MAINMENU) throw new InvalidOperationException("Scene changed before scale checkpoint loading.");
                    loadedGame = GamePersistence.LoadGame(ScaleCheckpointLoadState.Checkpoint, ScaleCheckpointLoadState.Save, true, true);
                    if (loadedGame == null || !loadedGame.compatible || loadedGame.flightState == null ||
                        loadedGame.flightState.activeVesselIdx < 0 || loadedGame.flightState.activeVesselIdx >= loadedGame.flightState.protoVessels.Count)
                        throw new InvalidOperationException("Scale checkpoint is not a compatible flight with an active vessel.");
                    ScaleCheckpointLoadState.BindVessel(loadedGame.flightState.protoVessels[loadedGame.flightState.activeVesselIdx].vesselID);
                    AtmosphericEntryPreparation.Prepare(loadedGame, receiptDirectory, Environment.GetCommandLineArgs());
                    HighLogic.SaveFolder = ScaleCheckpointLoadState.Save; HighLogic.CurrentGame = loadedGame;
                    loadedGame.startScene = GameScenes.FLIGHT; loading = true; loadedGame.Start(); return;
                }
                if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return;
                if (!ReferenceEquals(HighLogic.CurrentGame, loadedGame) || HighLogic.SaveFolder != ScaleCheckpointLoadState.Save ||
                    FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.id != ScaleCheckpointLoadState.VesselId)
                    throw new InvalidOperationException("Loaded scale checkpoint identity does not match its selection.");
                if (!ScaleCheckpointLoadState.SourceUnchanged()) throw new InvalidOperationException("Scale checkpoint changed while loading.");
                ScaleCheckpointLoadState.Complete(); active = false; GameEvents.onLevelWasLoadedGUIReady.Remove(OnGuiReady);
            }
            catch (Exception error) { Fail(error); }
        }

        static void RejectSourcePath(string sourcePath)
        {
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(sourcePath);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                throw new InvalidOperationException("Requested scale checkpoint does not exist inside this KSP instance.");
            for (string current = full; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic scale checkpoint paths are not supported.");
                if (current == root) return;
            }
            throw new InvalidOperationException("Scale checkpoint escaped the KSP instance root.");
        }

        void Fail(Exception error)
        {
            Debug.LogException(error); active = false; GameEvents.onLevelWasLoadedGUIReady.Remove(OnGuiReady);
            try { if (receiptDirectory != null) File.WriteAllText(Path.Combine(receiptDirectory, "status.txt"), "error\nreason=" + error.GetType().Name + "\n"); }
            catch (Exception exportError) { Debug.LogException(exportError); }
            Application.Quit(2);
        }

        public void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnGuiReady);
            if (active) Fail(new InvalidOperationException("Scale checkpoint loader was destroyed before Flight became ready."));
        }
    }
}
