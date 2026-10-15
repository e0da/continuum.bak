using System;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupPresentationExperiment : MonoBehaviour
    {
        static StartupPresentationExperiment instance;
        bool active;
        int savedVSync, savedFrameLimit;
        public static bool Requested { get; private set; }
        public static bool Restored { get; private set; }
        public static int AppliedFrames { get; private set; }

        public void Awake()
        {
            Requested = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-unlimited") >= 0;
            if (!Requested) return;
            instance = this; DontDestroyOnLoad(gameObject);
            savedVSync = GameSettings.SYNC_VBL; savedFrameLimit = GameSettings.FRAMERATE_LIMIT;
            active = true; Apply();
        }
        void Apply() { QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; AppliedFrames++; }
        public void Update()
        {
            if (!active) return;
            if (HighLogic.LoadedScene == GameScenes.LOADING) Apply();
            else Restore();
        }
        public static void RestoreForMenu() { if (instance != null) instance.Restore(); }
        void Restore()
        {
            if (!active) return;
            QualitySettings.vSyncCount = savedVSync; Application.targetFrameRate = savedFrameLimit;
            Restored = QualitySettings.vSyncCount == savedVSync && Application.targetFrameRate == savedFrameLimit;
            active = false;
        }
        public void OnDestroy() { Restore(); if (ReferenceEquals(instance, this)) instance = null; }
    }
}
