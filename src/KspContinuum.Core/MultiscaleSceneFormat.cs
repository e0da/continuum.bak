using System;
using System.IO;
using System.Text;

namespace KspContinuum
{
    [Serializable] public sealed class MultiscaleSceneManifest
    {
        public string schema, runId, vesselId, engine, colorSpace, sourceFrame, bodyId, bodyName;
        public string vesselFixture, vesselFixtureSha256, bodyGeometry, bodyGeometrySha256;
        public string bodyMaterialRecipe, originalBodyShader, bodyTextureProperty, bodyTextureCapture, initialCameraTarget;
        public int processId;
        public double[] vesselAnchorMeters, bodyAnchorMeters, bodyLocalToMeters, initialCameraOffsetMeters;
        public double bodyRadiusMeters;
        public float fov;
        public ResidentTextureAsset bodyTexture;
        public float[] bodyColor, bodyTextureScaleOffset;
    }

    public static class MultiscaleSceneFormat
    {
        static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static void Text(string value) { Require(!string.IsNullOrEmpty(value) && value.Length <= 512, "Scene identity absent/oversize."); foreach (char c in value) Require(!char.IsControl(c), "Scene identity contains controls."); }
        static void Digest(string value) { Require(value != null && value.Length == 64, "Scene digest differs."); foreach (char c in value) Require(c >= '0' && c <= '9' || c >= 'a' && c <= 'f', "Scene digest must be lowercase hex."); }
        public static void PortablePath(string value)
        {
            Text(value); Require(!Path.IsPathRooted(value) && value.IndexOf(':') < 0 && value.IndexOf('\\') < 0, "Scene payload must be portable relative.");
            foreach (string segment in value.Split('/')) Require(segment.Length > 0 && segment != "." && segment != "..", "Scene payload traversal/empty segment.");
        }
        static void Numbers(double[] value, int count) { Require(value != null && value.Length == count, "Scene double shape differs."); foreach (double n in value) Require(Finite(n), "Scene double is nonfinite."); }
        static void Numbers(float[] value, int count) { Require(value != null && value.Length == count, "Scene float shape differs."); foreach (float n in value) Require(Finite(n), "Scene float is nonfinite."); }
        public static void RelativePosition(double[] anchor, double[] camera, out float x, out float y, out float z)
        {
            Numbers(anchor, 3); Numbers(camera, 3);
            double a = anchor[0] - camera[0], b = anchor[1] - camera[1], c = anchor[2] - camera[2];
            Require(Finite(a) && Finite(b) && Finite(c) && Math.Abs(a) <= float.MaxValue && Math.Abs(b) <= float.MaxValue && Math.Abs(c) <= float.MaxValue, "Camera-relative position exceeds float range.");
            x = (float)a; y = (float)b; z = (float)c;
        }
        public static void Validate(MultiscaleSceneManifest scene)
        {
            Require(scene != null && scene.schema == "continuum-multiscale-scene/v1", "Multiscale scene schema differs.");
            Text(scene.runId); Text(scene.vesselId); Text(scene.bodyId); Text(scene.bodyName); Text(scene.engine); Text(scene.originalBodyShader);
            Require(scene.processId > 0 && scene.bodyId != scene.vesselId, "Scene source/object identity differs.");
            Require(scene.colorSpace == "Gamma" || scene.colorSpace == "Linear", "Scene colour space differs.");
            Require(scene.sourceFrame == "body-centred-world-axes", "Scene reference frame unsupported.");
            PortablePath(scene.vesselFixture); Digest(scene.vesselFixtureSha256); PortablePath(scene.bodyGeometry); Digest(scene.bodyGeometrySha256);
            Numbers(scene.vesselAnchorMeters, 3); Numbers(scene.bodyAnchorMeters, 3); Numbers(scene.initialCameraOffsetMeters, 3); Numbers(scene.bodyLocalToMeters, 16);
            Require(scene.bodyAnchorMeters[0] == 0 && scene.bodyAnchorMeters[1] == 0 && scene.bodyAnchorMeters[2] == 0, "Body-centred origin differs.");
            var m = scene.bodyLocalToMeters; Require(m[12] == 0 && m[13] == 0 && m[14] == 0 && m[15] == 1, "Body matrix is not affine.");
            double determinant = m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
            Require(Finite(determinant) && determinant > 0 && Finite(scene.bodyRadiusMeters) && scene.bodyRadiusMeters > 0, "Body transform/radius invalid.");
            Require(Finite(scene.fov) && scene.fov > 1 && scene.fov < 179, "Scene FOV invalid.");
            Require(scene.initialCameraTarget == scene.vesselId || scene.initialCameraTarget == scene.bodyId, "Initial scene target unavailable.");
            Require(scene.bodyMaterialRecipe == "continuum-body-diffuse-v1" && scene.bodyTextureProperty == "_MainTex", "Body recipe unsupported.");
            Require(scene.bodyTextureCapture == "prepared-raw" || scene.bodyTextureCapture == "readable-raw" || scene.bodyTextureCapture == "rgba-readback-mip0", "Body texture capture mode unsupported.");
            Numbers(scene.bodyColor, 4); Numbers(scene.bodyTextureScaleOffset, 4);
            var t = scene.bodyTexture; Require(t != null, "Body texture absent."); Text(t.id); PortablePath(t.payload); Digest(t.payloadSha256);
            Require(t.width > 0 && t.width <= 16384 && t.height > 0 && t.height <= 16384 && t.payloadLength > 0 && t.payloadLength <= 1073741824 && t.mipCount > 0 && t.mipCount <= 15, "Body texture capacity invalid.");
            Require(t.filter >= 0 && t.filter <= 2 && t.wrap >= 0 && t.wrap <= 3 && t.aniso >= 0 && t.aniso <= 16 && Finite(t.mipBias), "Body texture sampler invalid.");
            if (scene.bodyTextureCapture == "rgba-readback-mip0") Require(t.mipCount == 1 && t.format == 4, "Body readback representation differs.");
        }
    }

    [Serializable] public sealed class PresentationQualityOverride
    {
        public string scope, context, assetClass, assetId, pass;
        public bool geometrySet, mipSet, scaleSet;
        public float geometryErrorPixels, textureMipBias, worldScale;
    }
    public sealed class EffectivePresentationQuality
    {
        public readonly string preset, context, assetClass, pass, assetId, provenance;
        public readonly float geometryErrorPixels, textureMipBias, worldScale;
        internal EffectivePresentationQuality(string p, string c, string kind, string viewPass, string id, string source, float geometry, float mip, float scale)
        { preset = p; context = c; assetClass = kind; pass = viewPass; assetId = id; provenance = source; geometryErrorPixels = geometry; textureMipBias = mip; worldScale = scale; }
    }
    public static class PresentationQualityPolicy
    {
        static void Require(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
        static bool In(float value, float min, float max) { return !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max; }
        static bool Context(string value) { return value == "flight" || value == "eva" || value == "editor" || value == "map"; }
        static bool Kind(string value) { return value == "craft" || value == "body"; }
        static bool Pass(string value) { return value == "world" || value == "shadow"; }
        static bool Match(string filter, string value) { return string.IsNullOrEmpty(filter) || filter == value; }
        public static EffectivePresentationQuality Resolve(string preset, string context, string assetClass, string pass, string assetId, PresentationQualityOverride[] overrides)
        {
            Require(preset == "low" || preset == "balanced" || preset == "high", "Quality preset unsupported.");
            Require(Context(context) && Kind(assetClass) && Pass(pass) && !string.IsNullOrEmpty(assetId), "Quality consumer invalid.");
            float geometry = preset == "low" ? 2 : preset == "high" ? .5f : 1;
            float mip = preset == "low" ? 1 : preset == "high" ? -.5f : 0;
            float scale = pass == "shadow" ? 1 : preset == "low" ? .75f : 1;
            var provenance = new StringBuilder("preset:").Append(preset);
            if (overrides == null) overrides = new PresentationQualityOverride[0]; Require(overrides.Length <= 128, "Quality override capacity exceeded.");
            foreach (var row in overrides)
            {
                Require(row != null && (row.scope == "context" || row.scope == "class" || row.scope == "asset"), "Quality override scope invalid.");
                Require((string.IsNullOrEmpty(row.context) || Context(row.context)) && (string.IsNullOrEmpty(row.assetClass) || Kind(row.assetClass)) && (string.IsNullOrEmpty(row.pass) || Pass(row.pass)), "Quality override selector invalid.");
                Require(row.scope != "context" || !string.IsNullOrEmpty(row.context) && string.IsNullOrEmpty(row.assetClass) && string.IsNullOrEmpty(row.assetId), "Context override selectors differ.");
                Require(row.scope != "class" || !string.IsNullOrEmpty(row.assetClass) && string.IsNullOrEmpty(row.assetId), "Class override selectors differ.");
                Require(row.scope != "asset" || !string.IsNullOrEmpty(row.assetId), "Asset override identity absent.");
                Require(!row.geometrySet || In(row.geometryErrorPixels, .05f, 16), "Geometry error limit invalid.");
                Require(!row.mipSet || In(row.textureMipBias, -2, 4), "Texture mip request invalid.");
                Require(!row.scaleSet || In(row.worldScale, .25f, 1), "World scale request invalid.");
            }
            foreach (string scope in new[] { "context", "class", "asset" }) for (int i = 0; i < overrides.Length; i++)
            {
                var row = overrides[i]; if (row.scope != scope || !Match(row.context, context) || !Match(row.assetClass, assetClass) || !Match(row.pass, pass) || !Match(row.assetId, assetId)) continue;
                if (row.geometrySet) geometry = row.geometryErrorPixels; if (row.mipSet) mip = row.textureMipBias; if (row.scaleSet) scale = row.worldScale;
                provenance.Append(";").Append(scope).Append(":").Append(i);
            }
            return new EffectivePresentationQuality(preset, context, assetClass, pass, assetId, provenance.ToString(), geometry, mip, scale);
        }
    }
}
