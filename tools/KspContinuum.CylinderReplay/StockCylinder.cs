using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using KspContinuum;
using UnityEngine;

static class StockCylinder
{
    public const string Sha256 = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
    public static void Admit()
    {
        Assembly assembly = typeof(OcclusionData).Assembly;
        if (assembly.ManifestModule.ModuleVersionId != new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf") ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() != Sha256)
            throw new InvalidDataException("Original assembly identity differs.");
    }
    public static OcclusionData Input(CylinderOcclusionFold.Row row)
    {
        var thermal = (PartThermalData)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PartThermalData));
        return new OcclusionData(thermal) { minExtents = new Vector2(row.minX,row.minY), maxExtents = new Vector2(row.maxX,row.maxY),
            center = new Vector2(row.centerX,row.centerY), extents = new Vector2(row.extentX,row.extentY) };
    }
    public static double Intersection(CylinderOcclusionFold.Row target, CylinderOcclusionFold.Row source)
    {
        OcclusionData input = Input(target), owner = Input(source);
        var cylinder = new OcclusionCylinder(); cylinder.Setup(owner);
        return input.GetCylinderOcclusion(cylinder);
    }
    public static bool Same(double left, double right) { return BitConverter.DoubleToInt64Bits(left)==BitConverter.DoubleToInt64Bits(right); }
    public static void Selfcheck()
    {
        Admit(); var random = new System.Random(4179);
        for (int i=0;i<2048;i++)
        {
            float scale=(float)Math.Pow(2,i%26-12);
            CylinderOcclusionFold.Row target=Rect((float)random.NextDouble()*scale,(float)random.NextDouble()*scale,scale/4,scale/3);
            CylinderOcclusionFold.Row source=Rect((float)random.NextDouble()*scale,(float)random.NextDouble()*scale,scale/5,scale/2);
            double original=Intersection(target,source), candidate=CylinderOcclusionFold.Intersection(target,source);
            if (!Same(original,candidate)) throw new InvalidDataException("Original scalar mismatch at frozen case"+i);
        }
        Console.WriteLine("Pinned original-assembly scalar oracle:2048 deterministic varied-scale pairs matched bit for bit.");
    }
    static CylinderOcclusionFold.Row Rect(float x,float y,float dx,float dy)
    { return new CylinderOcclusionFold.Row { minX=x-dx,maxX=x+dx,minY=y-dy,maxY=y+dy,centerX=x,centerY=y,extentX=dx,extentY=dy }; }
}
