namespace KspContinuum
{
    public sealed class ManagedReconstructionObservation
    {
        public string schema = "continuum-managed-runtime-observation/v1";
        public string observedUtc, gameVersion, assemblySha256, runId;
        public string assetsAssemblySha256, observerAssemblySha256;
        public string graphicsDeviceType, graphicsDeviceName, graphicsDeviceVersion;
        public double engineRealtimeSeconds;
        public int screenWidth, screenHeight, vSyncCount, targetFrameRate;
        public bool startupUnlimitedRequested, startupPresentationRestored;
        public int startupUnlimitedFrames;
        public string scope = "main-menu-version-call-only";
    }
}
