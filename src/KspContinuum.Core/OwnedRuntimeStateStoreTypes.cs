using System;

namespace KspContinuum
{
    public sealed class OwnedRuntimeProvenance
    {
        public string Kind, SourceId, SourceSha256, Recipe;
        public OwnedRuntimeSourceBinding Native;
        public OwnedRuntimeProvenance(string kind, string sourceId, string sourceSha256, string recipe, OwnedRuntimeSourceBinding native = null)
        { Kind = kind; SourceId = sourceId; SourceSha256 = sourceSha256; Recipe = recipe; Native = native; }
        internal OwnedRuntimeProvenance Copy()
        {
            if (Kind != "generated" && Kind != "numeric" && Kind != "ksp") throw new ArgumentException("State provenance kind is unsupported.");
            OwnedRuntimeImport.Text(SourceId); OwnedRuntimeImport.Digest(SourceSha256); OwnedRuntimeImport.Text(Recipe);
            if ((Kind == "ksp") != (Native != null)) throw new ArgumentException("Only KSP provenance carries the optional native binding.");
            return new OwnedRuntimeProvenance(Kind, SourceId, SourceSha256, Recipe, Native == null ? null : OwnedRuntimeCheckpoint.CopySource(Native));
        }
    }

    public sealed class OwnedRuntimeStateLimits
    {
        public readonly int MaximumParts, MaximumBodies, MaximumResources, MaximumProviderStates, MaximumConnections, MaximumSystemRows;
        public readonly int SegmentBytes, MaximumMetadataBytes, MaximumStringBytes;
        public readonly long MaximumEncodedBytes, MaximumLogicalElements, MaximumAllocationBytes;
        public OwnedRuntimeStateLimits(int maximumParts = 65536, int maximumBodies = 65536, int maximumResources = 1048576,
            int maximumProviderStates = 1048576, int maximumConnections = 1048576, int maximumSystemRows = 1048576,
            long maximumEncodedBytes = 1073741824L, long maximumLogicalElements = 134217728L,
            long maximumAllocationBytes = 2147483648L, int segmentBytes = 32768, int maximumMetadataBytes = 2097152, int maximumStringBytes = 8192)
        {
            if (maximumParts < 1 || maximumParts > 1048576 || maximumBodies < 1 || maximumBodies > maximumParts || maximumResources < 0 || maximumResources > 8388608
                || maximumProviderStates < 0 || maximumProviderStates > 8388608 || maximumConnections < 0 || maximumConnections > 16777216 || maximumSystemRows < 0 || maximumSystemRows > 1048576
                || maximumEncodedBytes < 1024 || maximumEncodedBytes > 17179869184L || maximumLogicalElements < 1 || maximumAllocationBytes < 1024
                || segmentBytes < 256 || segmentBytes > 1048576 || maximumMetadataBytes < 1024 || maximumMetadataBytes > 16777216 || maximumStringBytes < 1 || maximumStringBytes > 65536)
                throw new ArgumentException("Segmented state limits are outside their bounded domain.");
            MaximumParts = maximumParts; MaximumBodies = maximumBodies; MaximumResources = maximumResources; MaximumProviderStates = maximumProviderStates;
            MaximumConnections = maximumConnections; MaximumSystemRows = maximumSystemRows; MaximumEncodedBytes = maximumEncodedBytes;
            MaximumLogicalElements = maximumLogicalElements; MaximumAllocationBytes = maximumAllocationBytes; SegmentBytes = segmentBytes;
            MaximumMetadataBytes = maximumMetadataBytes; MaximumStringBytes = maximumStringBytes;
        }
    }

    public sealed class OwnedRuntimeStateRestored
    {
        public OwnedRuntimeHost Host { get; internal set; }
        public OwnedRuntimeProvenance Provenance { get; internal set; }
        public OwnedRuntimeControls Controls { get; internal set; }
        public OwnedRuntimeEnvironment Environment { get; internal set; }
        public OwnedRuntimeStepPlan Plan { get; internal set; }
    }
}
