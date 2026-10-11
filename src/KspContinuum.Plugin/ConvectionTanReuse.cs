using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;

namespace KspContinuum
{
    internal sealed class ConvectionTanReuse : IDisposable
    {
        const string Owner = "continuum.convection-tan-reuse";
        const string StockHash = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int Capacity = 8192;
        static readonly Guid StockMvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        static readonly MethodInfo Context = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateOcclusionConvection", Type.EmptyTypes);
        static readonly MethodInfo Pair = AccessTools.DeclaredMethod(typeof(OcclusionData), "GetShockStats", new[] { typeof(OcclusionCone), typeof(double).MakeByRefType(), typeof(double).MakeByRefType() });
        static readonly MethodInfo Radius = AccessTools.DeclaredMethod(typeof(OcclusionCone), "GetShockRadius", new[] { typeof(double) });
        static ConvectionTanReuse active;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Dictionary<long, double> values = new Dictionary<long, double>(Capacity);
        readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        int depth;
        bool disposed;
        [Serializable] public sealed class ReportData
        {
            public string status = "installed", cleanupStatus = "installed";
            public long faults, originalExceptions, convectionCalls, tangentRequests, cacheHits, cacheMisses, boundFallbacks;
        }
        public ReportData Report { get; private set; } = new ReportData();
        public static ConvectionTanReuse Create(string[] arguments)
        {
            if (arguments == null || !arguments.Contains("--continuum-convection-tan-reuse")) return null;
            return new ConvectionTanReuse(arguments);
        }
        ConvectionTanReuse(string[] arguments)
        {
            if (active != null || Context == null || Pair == null || Radius == null ||
                Context.IsStatic || Context.ReturnType != typeof(void) || Pair.IsStatic || Pair.ReturnType != typeof(double) ||
                Radius.IsStatic || Radius.ReturnType != typeof(double) || Context.MetadataToken != 100677506 ||
                Context.Module.ModuleVersionId != StockMvid || Pair.Module != Context.Module || Radius.Module != Context.Module ||
                Digest(typeof(FlightIntegrator).Assembly.Location) != StockHash)
                throw new InvalidOperationException("Exact convection tangent reuse admission failed.");
            RejectOwners(Pair, false); RejectOwners(Radius, false);
            RejectOwners(Context, arguments.Any(a => a.StartsWith("--continuum-convection-capture=", StringComparison.Ordinal)));
            CheckShape(PatchProcessor.GetOriginalInstructions(Pair));
            active = this;
            try
            {
                harmony.Patch(Context, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ConvectionTanReuse), "Enter")),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ConvectionTanReuse), "Leave")));
                harmony.Patch(Pair, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ConvectionTanReuse), "Transpile")));
            }
            catch { Dispose(); throw; }
        }
        static void RejectOwners(MethodInfo method, bool capture)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return;
            if (capture && patches.Prefixes.Count == 0 && patches.Transpilers.Count == 0 && patches.Finalizers.Count == 0 &&
                patches.Postfixes.Count == 1 && patches.Postfixes[0].owner == "continuum.convection-capture" &&
                patches.Postfixes[0].PatchMethod.DeclaringType == typeof(ConvectionCapture) && patches.Postfixes[0].PatchMethod.Name == "Postfix") return;
            if (patches.Owners.Any()) throw new InvalidOperationException("Foreign convection patch: " + method.Name);
        }
        static bool IsRadius(CodeInstruction instruction)
        {
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, Radius);
        }
        static void CheckShape(IEnumerable<CodeInstruction> instructions)
        {
            if (instructions.Count(IsRadius) != 1) throw new InvalidOperationException("Convection pair radius call shape differs.");
        }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> source)
        {
            var instructions = source.ToList(); CheckShape(instructions);
            foreach (CodeInstruction instruction in instructions)
            {
                if (IsRadius(instruction)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(ConvectionTanReuse), "CachedRadius"); }
                yield return instruction;
            }
        }
        static void Enter(out ConvectionTanReuse __state)
        {
            __state = null; ConvectionTanReuse owner = active;
            if (owner == null || owner.disposed || Thread.CurrentThread.ManagedThreadId != owner.threadId) return;
            owner.values.Clear(); owner.depth++; owner.Report.convectionCalls++; __state = owner;
        }
        static Exception Leave(Exception __exception, ConvectionTanReuse __state)
        {
            if (__state != null)
            {
                if (__exception != null) __state.Report.originalExceptions++;
                __state.depth--; if (__state.depth == 0) __state.values.Clear();
            }
            return __exception;
        }
        static double CachedRadius(OcclusionCone cone, double dot)
        {
            double distance = cone.shockNoseDot - dot;
            double radius = cone.radius;
            double angle = cone.shockAngle;
            ConvectionTanReuse owner = active;
            double tangent;
            if (owner == null || owner.disposed || owner.depth == 0 || Thread.CurrentThread.ManagedThreadId != owner.threadId)
                tangent = Math.Tan(angle);
            else
            {
                owner.Report.tangentRequests++;
                long key = BitConverter.DoubleToInt64Bits(angle);
                if (owner.values.TryGetValue(key, out tangent)) owner.Report.cacheHits++;
                else
                {
                    tangent = Math.Tan(angle); owner.Report.cacheMisses++;
                    if (owner.values.Count >= Capacity) owner.Report.boundFallbacks++;
                    else
                    {
                        try { owner.values.Add(key, tangent); }
                        catch { owner.Report.faults++; }
                    }
                }
            }
            return radius + distance * tangent;
        }
        static string Digest(string path)
        {
            using (SHA256 hash = SHA256.Create()) using (Stream stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                harmony.UnpatchAll(Owner);
                bool remains = new[] { Context, Pair }.Any(m => m != null && Harmony.GetPatchInfo(m)?.Owners.Contains(Owner) == true);
                Report.cleanupStatus = remains ? "cleanup-error" : "removed-owned-hooks";
                if (remains) Report.faults++;
            }
            catch { Report.faults++; Report.cleanupStatus = "cleanup-error"; }
            finally { values.Clear(); depth = 0; if (ReferenceEquals(active, this)) active = null; }
            Report.status = Report.faults == 0 && Report.originalExceptions == 0 && Report.cacheHits > 0 ? "complete-experimental" : "invalid";
        }
    }
}
