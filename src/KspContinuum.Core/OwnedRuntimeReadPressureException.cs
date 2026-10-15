using System;

namespace KspContinuum
{
    public sealed class OwnedRuntimeReadPressureException : InvalidOperationException
    {
        public OwnedRuntimeReadPressureException(string message) : base(message) { }
    }
}
