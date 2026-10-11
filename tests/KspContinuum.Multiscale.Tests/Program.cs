using System;
using System.IO;
using KspContinuum;

static class Program
{
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid input was admitted."); }
    static int Main()
    {
        float x, y, z;
        MultiscaleSceneFormat.RelativePosition(new[] { 1e12 + .25, -1e12 + .5, 1e12 - .125 }, new[] { 1e12, -1e12, 1e12 }, out x, out y, out z);
        Check(x == .25f && y == .5f && z == -.125f, "Common origin was converted to float before cancellation.");
        Reject(() => { float a,b,c; MultiscaleSceneFormat.RelativePosition(new[] { double.NaN, 0d, 0d }, new double[3], out a,out b,out c); });
        Reject(() => { float a,b,c; MultiscaleSceneFormat.RelativePosition(new[] { double.MaxValue, 0d, 0d }, new[] { -double.MaxValue, 0d, 0d }, out a,out b,out c); });
        foreach (string path in new[] { "../escape", "sub//file", "/absolute", "C:/drive", "sub\\file" }) Reject(() => MultiscaleSceneFormat.PortablePath(path));
        MultiscaleSceneFormat.PortablePath("vessel/manifest.json");
        var rows = new[] {
            new PresentationQualityOverride { scope="asset", assetId="craft-1", pass="world", mipSet=true, textureMipBias=-1 },
            new PresentationQualityOverride { scope="context", context="flight", mipSet=true, textureMipBias=2 },
            new PresentationQualityOverride { scope="class", assetClass="craft", mipSet=true, textureMipBias=1 },
            new PresentationQualityOverride { scope="asset", assetId="craft-1", pass="shadow", geometrySet=true, geometryErrorPixels=.25f }
        };
        var world = PresentationQualityPolicy.Resolve("balanced", "flight", "craft", "world", "craft-1", rows);
        Check(world.textureMipBias == -1 && world.provenance == "preset:balanced;context:1;class:2;asset:0", "Quality precedence depended on input array order across scopes.");
        var shadow = PresentationQualityPolicy.Resolve("balanced", "flight", "craft", "shadow", "craft-1", rows);
        Check(shadow.textureMipBias == 1 && shadow.geometryErrorPixels == .25f, "World policy leaked into another pass.");
        var body = PresentationQualityPolicy.Resolve("balanced", "map", "body", "world", "kerbin", rows);
        Check(body.textureMipBias == 0, "Unrelated asset or view inherited quality override.");
        rows[0].textureMipBias = float.NaN;
        Reject(() => PresentationQualityPolicy.Resolve("balanced", "map", "body", "world", "kerbin", rows));
        Console.WriteLine("Multiscale precision, input admission and quality precedence witnesses passed."); return 0;
    }
}
