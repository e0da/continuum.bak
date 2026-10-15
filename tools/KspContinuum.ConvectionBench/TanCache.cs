using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

sealed class TanCache : IDisposable
{
    public delegate double ShockCall(OcclusionData data, OcclusionCone cone, ref double temp, ref double coeff);
    public static ShockCall Caller { get; private set; }
    static readonly Dictionary<long, double> cache = new Dictionary<long, double>();
    static bool active;
    static bool twoSlots, valid0, valid1;
    static long key0, key1;
    static double value0, value1;
    public static long Hits { get; private set; }
    public static long Misses { get; private set; }
    public static long Uncached { get; private set; }
    readonly MethodInfo target = typeof(OcclusionData).GetMethod(nameof(OcclusionData.GetShockStats));
    bool disposed;
    public TanCache(bool cached = true)
    {
        if (target.DeclaringType != typeof(OcclusionData) || target.MetadataToken != 100677560 || target.Module.ModuleVersionId != Stock.Mvid || Harmony.GetPatchInfo(target) != null)
            throw new InvalidOperationException("Original shock caller seam differs or is already patched.");
        if (Caller != null) throw new InvalidOperationException("An offline shock delegate already exists.");
        Type emitter = typeof(Harmony).Assembly.GetType("HarmonyLib.MethodPatcher", true);
        var empty = new List<MethodInfo>();
        var transforms = cached ? new List<MethodInfo> { typeof(TanCache).GetMethod(nameof(Transpiler), BindingFlags.NonPublic | BindingFlags.Static) } : empty;
        object patcher = Activator.CreateInstance(emitter, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { target, null, empty, empty, transforms, empty, false }, null);
        object[] output = { null };
        var replacement = (MethodInfo)emitter.GetMethod("CreateReplacement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(patcher, output);
        Caller = (ShockCall)replacement.CreateDelegate(typeof(ShockCall));
    }
    public static void BeginBatch(bool fixedSlots = false) { cache.Clear(); Hits = Misses = Uncached = 0; twoSlots = fixedSlots; valid0 = valid1 = false; active = true; }
    public static void EndBatch() { active = false; valid0 = valid1 = false; cache.Clear(); }
    public static double CachedTan(double angle)
    {
        if (!active) return Math.Tan(angle);
        long bits = BitConverter.DoubleToInt64Bits(angle);
        if (twoSlots)
        {
            if (valid0 && key0 == bits) { Hits++; return value0; }
            if (valid1 && key1 == bits) { Hits++; return value1; }
            double computed = Math.Tan(angle); Misses++;
            if (!valid0) { key0 = bits; value0 = computed; valid0 = true; }
            else if (!valid1) { key1 = bits; value1 = computed; valid1 = true; }
            else Uncached++;
            return computed;
        }
        if (cache.TryGetValue(bits, out double value)) { Hits++; return value; }
        value = Math.Tan(angle); cache.Add(bits, value); Misses++; return value;
    }
    public static double CachedRadius(OcclusionCone cone, double dot)
    {
        if (!active) return cone.GetShockRadius(dot);
        double distance = cone.shockNoseDot - dot;
        double radius = cone.radius;
        double tangent = CachedTan(cone.shockAngle);
        return radius + distance * tangent;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code = source.Select(instruction => new CodeInstruction(instruction)).ToArray();
        var radius = typeof(OcclusionCone).GetMethod(nameof(OcclusionCone.GetShockRadius));
        int replacements = 0;
        foreach (var instruction in code)
            if (instruction.Calls(radius)) { instruction.opcode = OpCodes.Call; instruction.operand = typeof(TanCache).GetMethod(nameof(CachedRadius)); replacements++; }
        if (replacements != 1) throw new InvalidOperationException("Original shock caller must contain exactly one radius call.");
        return code;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; EndBatch(); Caller = null;
        if (Harmony.GetPatchInfo(target) != null) throw new InvalidOperationException("Offline delegate unexpectedly mutated the stock patch graph.");
    }
}
