using System;
using System.Linq;
using System.Reflection;

namespace KspContinuum
{
    internal static class MechJebDeclaredDrive
    {
        public static MethodInfo Canonical(MethodInfo reflected)
        {
            if (reflected == null || reflected.DeclaringType == null)
                throw new InvalidOperationException("Controller Drive declaration absent.");
            var declared = reflected.DeclaringType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(method => method.Module == reflected.Module && method.MetadataToken == reflected.MetadataToken);
            if (declared == null || declared.ReflectedType != declared.DeclaringType || declared.Name != reflected.Name ||
                declared.ReturnType != reflected.ReturnType || !declared.GetParameters().Select(p => p.ParameterType).SequenceEqual(reflected.GetParameters().Select(p => p.ParameterType)))
                throw new InvalidOperationException("Controller Drive declared identity differs.");
            return declared;
        }
    }
}
