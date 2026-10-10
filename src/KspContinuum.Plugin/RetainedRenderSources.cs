using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace KspContinuum
{
    internal static class RetainedRenderSources
    {
        sealed class Source
        {
            internal MeshRenderer Renderer;
            internal Part Part;
            internal Matrix4x4 Local, World;
            internal Bounds MeshBounds, WorldBounds;
            internal bool Enabled, ForcedOff, Retired;
        }
        sealed class LocalGuard
        {
            internal Transform Transform, Parent;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
        }
        static readonly Dictionary<MeshRenderer, Source> sources = new Dictionary<MeshRenderer, Source>();
        static readonly Dictionary<Transform, LocalGuard> guards = new Dictionary<Transform, LocalGuard>();
        static readonly Dictionary<Part, Matrix4x4> poses = new Dictionary<Part, Matrix4x4>();
        static int frame = -1;
        internal static int RetiredCount, RestoredCount, PoseSnapshots, SourcePublications, LocalChecks;
        internal static long CaptureTicks;
        internal static double MaxInitialRecoveredPositionErrorMeters;
        internal static bool Requested { get { return Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-flight-part-retained") >= 0; } }
        internal static void Begin()
        {
            if (sources.Count != 0) throw new InvalidOperationException("Retained render ownership already exists.");
            guards.Clear(); poses.Clear(); frame = -1;
            RetiredCount = RestoredCount = PoseSnapshots = SourcePublications = LocalChecks = 0;
            CaptureTicks = 0; MaxInitialRecoveredPositionErrorMeters = 0;
        }
        internal static bool Register(MeshRenderer renderer, Part part, Mesh mesh)
        {
            if (!Requested || renderer == null || part == null || mesh == null || !FlightAeroBatcher.Covers(renderer)) return false;
            var chain = new List<Transform>(); Transform node = renderer.transform;
            while (node != part.transform)
            {
                if (node == null || node.GetComponent<Animation>() != null || node.GetComponent<Animator>() != null) return false;
                chain.Add(node); node = node.parent;
            }
            if (part.GetComponent<Animation>() != null || part.GetComponent<Animator>() != null) return false;
            if (sources.ContainsKey(renderer)) throw new InvalidOperationException("Duplicate retained render owner.");
            foreach (Transform transform in chain) if (!guards.ContainsKey(transform)) guards.Add(transform, new LocalGuard {
                Transform = transform, Parent = transform.parent, Position = transform.localPosition, Rotation = transform.localRotation, Scale = transform.localScale });
            sources.Add(renderer, new Source { Renderer = renderer, Part = part, Local = part.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                MeshBounds = mesh.bounds, Enabled = renderer.enabled, ForcedOff = renderer.forceRenderingOff });
            frame = -1; return true;
        }
        internal static void RetireAll()
        {
            foreach (Source source in sources.Values)
            {
                if (source.Renderer == null || source.Part == null) throw new InvalidOperationException("Source lost before geometry admission.");
                Matrix4x4 original = source.Renderer.transform.localToWorldMatrix;
                Matrix4x4 recovered = source.Part.transform.localToWorldMatrix * source.Local;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = source.MeshBounds.center + Vector3.Scale(source.MeshBounds.extents,
                        new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    double error = (original.MultiplyPoint3x4(point) - recovered.MultiplyPoint3x4(point)).magnitude;
                    if (double.IsNaN(error) || double.IsInfinity(error)) throw new InvalidOperationException("Nonfinite recovered geometry witness.");
                    MaxInitialRecoveredPositionErrorMeters = Math.Max(MaxInitialRecoveredPositionErrorMeters, error);
                }
            }
            if (MaxInitialRecoveredPositionErrorMeters > 0.005) throw new InvalidOperationException("Recovered source geometry exceeds frozen 5mm positional gate.");
            foreach (Source source in sources.Values)
            {
                if (source.Renderer == null || !source.Renderer.enabled || source.Renderer.forceRenderingOff) throw new InvalidOperationException("Source changed before retirement.");
                source.Renderer.enabled = false; source.Retired = true; RetiredCount++;
            }
        }
        internal static bool IsRetired(MeshRenderer renderer)
        { Source source; return renderer != null && sources.TryGetValue(renderer, out source) && source.Retired; }
        internal static bool Visible(MeshRenderer renderer)
        { return renderer != null && renderer.gameObject.activeInHierarchy && !renderer.forceRenderingOff && (IsRetired(renderer) ? !renderer.enabled : renderer.enabled); }
        static bool Equal(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }
        static bool Equal(Quaternion a, Quaternion b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static void Capture()
        {
            if (frame == Time.frameCount) return;
            long started = Stopwatch.GetTimestamp();
            poses.Clear();
            foreach (LocalGuard guard in guards.Values)
            {
                Transform transform = guard.Transform; LocalChecks++;
                if (transform == null || transform.parent != guard.Parent || !Equal(transform.localPosition, guard.Position) || !Equal(transform.localRotation, guard.Rotation) || !Equal(transform.localScale, guard.Scale))
                    throw new InvalidOperationException("Retained rigid child changed; restore stock renderer.");
            }
            foreach (Source source in sources.Values)
            {
                if (source.Part == null || !Visible(source.Renderer)) throw new InvalidOperationException("Retained source owner/visibility changed.");
                Matrix4x4 pose;
                if (!poses.TryGetValue(source.Part, out pose)) { pose = source.Part.transform.localToWorldMatrix; poses.Add(source.Part, pose); PoseSnapshots++; }
                source.World = pose * source.Local;
                for (int element = 0; element < 16; element++) if (!Finite(source.World[element])) throw new InvalidOperationException("Nonfinite retained pose.");
                if (source.World.determinant <= 0) throw new InvalidOperationException("Retained pose determinant changed.");
                Vector3 center = source.World.MultiplyPoint3x4(source.MeshBounds.center), extents = source.MeshBounds.extents;
                Vector3 x = source.World.MultiplyVector(new Vector3(extents.x, 0, 0)), y = source.World.MultiplyVector(new Vector3(0, extents.y, 0)), z = source.World.MultiplyVector(new Vector3(0, 0, extents.z));
                source.WorldBounds = new Bounds(center, 2 * new Vector3(Math.Abs(x.x) + Math.Abs(y.x) + Math.Abs(z.x), Math.Abs(x.y) + Math.Abs(y.y) + Math.Abs(z.y), Math.Abs(x.z) + Math.Abs(y.z) + Math.Abs(z.z)));
                SourcePublications++;
            }
            frame = Time.frameCount; CaptureTicks += Stopwatch.GetTimestamp() - started;
        }
        internal static Matrix4x4 Pose(MeshRenderer renderer) { Capture(); return sources[renderer].World; }
        internal static Bounds Bounds(MeshRenderer renderer) { Capture(); return sources[renderer].WorldBounds; }
        internal static void RestoreAll()
        {
            foreach (Source source in sources.Values) if (source.Retired)
            {
                if (source.Renderer != null) { source.Renderer.enabled = source.Enabled; source.Renderer.forceRenderingOff = source.ForcedOff; RestoredCount++; }
                source.Retired = false;
            }
        }
        internal static void Clear() { RestoreAll(); sources.Clear(); guards.Clear(); poses.Clear(); frame = -1; }
    }
}
