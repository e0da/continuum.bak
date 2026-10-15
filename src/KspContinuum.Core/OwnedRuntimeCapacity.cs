using System;

namespace KspContinuum
{
    public sealed class OwnedRuntimeCapacity
    {
        public static readonly OwnedRuntimeCapacity Native = new OwnedRuntimeCapacity(65536, 1048576, 1048576, 1048576);
        public static readonly OwnedRuntimeCapacity Research = new OwnedRuntimeCapacity(1048576, 8388608, 8388608, 16777216);
        public readonly int MaxParts, MaxResources, MaxProviderStates, MaxConnections;
        public OwnedRuntimeCapacity(int maxParts, int maxResources, int maxProviderStates, int maxConnections)
        {
            if (maxParts < 1 || maxParts > 1048576 || maxResources < 0 || maxResources > 8388608
                || maxProviderStates < 0 || maxProviderStates > 8388608 || maxConnections < 0 || maxConnections > 16777216)
                throw new ArgumentOutOfRangeException("capacity", "Explicit runtime admission exceeds the bounded research capacity.");
            MaxParts = maxParts; MaxResources = maxResources; MaxProviderStates = maxProviderStates; MaxConnections = maxConnections;
        }
    }
}
