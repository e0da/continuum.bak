using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using KspContinuum;
using UnityEngine;

static class Stock
{
    public const string Hash = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
    public static readonly Guid Mvid = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
    public static void Admit()
    {
        Assembly assembly = typeof(OcclusionData).Assembly;
        if (assembly.GetName().Name != "Assembly-CSharp" || assembly.ManifestModule.ModuleVersionId != Mvid ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() != Hash)
            throw new InvalidOperationException("Original stock assembly pin differs.");
        Pin(typeof(OcclusionData), nameof(OcclusionData.GetShockStats), 100677560);
        Pin(typeof(OcclusionCone), nameof(OcclusionCone.Setup), 100677569);
        Pin(typeof(OcclusionCone), nameof(OcclusionCone.GetShockRadius), 100677570);
    }
    static void Pin(Type owner, string name, int token)
    {
        MethodInfo method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (method == null || method.DeclaringType != owner || method.MetadataToken != token || method.Module.ModuleVersionId != Mvid)
            throw new InvalidOperationException("Original scalar identity differs: " + name);
    }
    public static object Uninitialized(Type type)
    {
#pragma warning disable SYSLIB0050
        return FormatterServices.GetUninitializedObject(type);
#pragma warning restore SYSLIB0050
    }
    public static PartThermalData Thermal()
    {
        var data = (PartThermalData)Uninitialized(typeof(PartThermalData));
        data.convectionTempMultiplier = data.convectionCoeffMultiplier = data.convectionAreaMultiplier = 1;
        return data;
    }
    public static OcclusionData Input(ConvectionRow row)
    {
        var data = new OcclusionData(Thermal()) {
            projectedArea = row.projectedArea, projectedRadius = row.projectedRadius, invFineness = row.invFineness,
            minimumDot = row.minimumDot, maximumDot = row.maximumDot, centroidDot = row.centroidDot, maxWidthDepth = row.maxWidthDepth,
            projectedCenter = V3(row.projectedCenter), center = V2(row.center), minExtents = V2(row.minExtents),
            maxExtents = V2(row.maxExtents), extents = V2(row.extents) };
        return data;
    }
    public static Vector3 V3(float[] value) { return new Vector3(value[0], value[1], value[2]); }
    public static Vector2 V2(float[] value) { return new Vector2(value[0], value[1]); }
    public static float[] V(Vector3 value) { return new[] { value.x, value.y, value.z }; }
    public static float[] V(Vector2 value) { return new[] { value.x, value.y }; }
    public static ConvectionCone Capture(OcclusionCone value, int row)
    {
        return new ConvectionCone { rowIndex = row, center = V(value.center), extents = V(value.extents), offset = V(value.offset),
            radius = value.radius, cylNoseDot = value.cylNoseDot, shockNoseDot = value.shockNoseDot, shockAngle = value.shockAngle,
            shockConvectionTempMult = value.shockConvectionTempMult, shockConvectionCoeffMult = value.shockConvectionCoeffMult,
            occludeConvectionTempMult = value.occludeConvectionTempMult, occludeConvectionCoeffMult = value.occludeConvectionCoeffMult,
            occludeConvectionAreaMult = value.occludeConvectionAreaMult };
    }
    public static void Globals(double[] values)
    {
        if (values.Length != 14) throw new InvalidDataException("Convection global shape differs.");
        for (int i = 0; i < values.Length; i++) typeof(OcclusionCone).GetField(ConvectionFormat.GlobalNames[i], BindingFlags.Public | BindingFlags.Static).SetValue(null, values[i]);
    }
    public static double[] Globals()
    {
        var values = new double[14];
        for (int i = 0; i < values.Length; i++) values[i] = (double)typeof(OcclusionCone).GetField(ConvectionFormat.GlobalNames[i], BindingFlags.Public | BindingFlags.Static).GetValue(null);
        return values;
    }
    public static void TinyCall()
    {
        Admit();
        var data = new OcclusionData(Thermal()) { projectedRadius = 1, invFineness = .5, maximumDot = 2, maxWidthDepth = 1,
            extents = new Vector2(1, 1), minExtents = new Vector2(-1, -1), maxExtents = new Vector2(1, 1) };
        data.convCone.Setup(data, 2, Math.Asin(.5), .7);
        var recipient = new OcclusionData(Thermal()) { projectedRadius = 1, projectedCenter = new Vector3(100, 0, 0),
            center = new Vector2(100, 0), minExtents = new Vector2(99, -1), maxExtents = new Vector2(101, 1) };
        double temp = 1, coeff = 1;
        double area = recipient.GetShockStats(data.convCone, ref temp, ref coeff);
        if (area != 1 || temp != 1 || coeff != 1 || !double.IsFinite(data.convCone.shockAngle))
            throw new InvalidOperationException("Tiny original scalar call differs.");
    }
}
