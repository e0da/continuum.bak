using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal static class FlightCameraFreeze
    {
        const string Owner = "continuum.flight.camera.freeze";
        const float Distance = 50f;
        static readonly Quaternion ViewRotation = Quaternion.Euler(11.45916f, 17.18873f, 0);
        static Harmony harmony;
        static FlightCamera cameraOwner;
        static Vessel vessel;
        static Transform root, pivot;
        static Vector3 oldLocalPosition, oldPivotPosition;
        static Quaternion oldLocalRotation, oldPivotRotation;
        static float oldDistance, oldFoV;
        static int observed;
        static double maxPositionError, maxAngleError;
        public static string FailureReason { get; private set; }

        public static void Start(Vessel activeVessel)
        {
            if (harmony != null) throw new InvalidOperationException("Camera freeze already owned.");
            cameraOwner = FlightCamera.fetch; vessel = activeVessel;
            root = vessel == null || vessel.rootPart == null ? null : vessel.rootPart.transform;
            pivot = cameraOwner == null ? null : cameraOwner.transform.parent;
            if (root == null || pivot == null) throw new InvalidOperationException("Physical root and flight camera pivot required.");
            oldLocalPosition = cameraOwner.transform.localPosition; oldLocalRotation = cameraOwner.transform.localRotation;
            oldPivotPosition = pivot.localPosition; oldPivotRotation = pivot.localRotation;
            oldDistance = cameraOwner.Distance; oldFoV = cameraOwner.FieldOfView;
            observed = 0; maxPositionError = maxAngleError = 0; FailureReason = null;
            MethodInfo flight = AccessTools.Method(typeof(FlightCamera), "LateUpdate");
            MethodInfo fx = AccessTools.Method(typeof(FXCamera), "LateUpdate");
            MethodInfo depth = AccessTools.Method(typeof(FXDepthCamera), "LateUpdate");
            foreach (MethodInfo method in new[] { flight, fx, depth })
                if (method == null || Harmony.GetPatchInfo(method)?.Owners.Count > 0)
                    throw new InvalidOperationException("Camera update boundary missing or already patched.");
            harmony = new Harmony(Owner);
            try
            {
                harmony.Patch(flight, prefix: new HarmonyMethod(typeof(FlightCameraFreeze), nameof(FlightPrefix)));
                harmony.Patch(fx, prefix: new HarmonyMethod(typeof(FlightCameraFreeze), nameof(EffectPrefix)));
                harmony.Patch(depth, prefix: new HarmonyMethod(typeof(FlightCameraFreeze), nameof(EffectPrefix)));
                ApplyPose(); Camera.onPreCull += Observe;
            }
            catch { Stop(); throw; }
        }
        static bool FlightPrefix(FlightCamera __instance)
        {
            if (__instance != cameraOwner) return true;
            return !TryPose();
        }
        static void EffectPrefix() { TryPose(); }
        static bool TryPose()
        {
            if (harmony == null || FailureReason != null) return false;
            try { ApplyPose(); return true; }
            catch (Exception error) { FailureReason = error.GetType().Name + ": " + error.Message; return false; }
        }
        static void ApplyPose()
        {
            if (root == null || cameraOwner == null || pivot == null || FlightGlobals.ActiveVessel != vessel ||
                HighLogic.LoadedScene != GameScenes.FLIGHT || vessel.packed || vessel.rootPart.transform != root)
                throw new InvalidOperationException("Frozen camera context changed.");
            pivot.SetPositionAndRotation(root.position, root.rotation * ViewRotation);
            cameraOwner.SetDistanceImmediate(Distance);
            cameraOwner.transform.localRotation = Quaternion.identity;
            cameraOwner.FieldOfView = 60f;
            foreach (Camera camera in cameraOwner.cameras) if (camera != null) camera.fieldOfView = 60f;
            FlightCamera.CamPitch = .2f; FlightCamera.CamHdg = .3f;
        }
        static void Observe(Camera camera)
        {
            if (harmony == null || cameraOwner == null || root == null || camera == null ||
                !camera.transform.IsChildOf(cameraOwner.transform)) return;
            Vector3 expected = root.position + (root.rotation * ViewRotation) * (Vector3.back * Distance);
            double positionError = (camera.transform.position - expected).magnitude;
            Quaternion rotation = root.rotation * ViewRotation;
            double angleError = Math.Max(Angle(camera.transform.forward, rotation * Vector3.forward), Angle(camera.transform.up, rotation * Vector3.up));
            maxPositionError = Math.Max(maxPositionError, positionError); maxAngleError = Math.Max(maxAngleError, angleError); observed++;
            if (positionError > .005 || angleError > .005 || Mathf.Abs(camera.fieldOfView - 60f) > .001)
                FailureReason = "Rendered camera pose/projection differs from frozen root-relative contract.";
        }
        static double Angle(Vector3 actual, Vector3 expected)
        { return Math.Atan2(Vector3.Cross(actual, expected).magnitude, Vector3.Dot(actual, expected)) * (180 / Math.PI); }
        public static void Record(FlightDiagnosticControlReport report)
        {
            report.cameraFrozen = harmony != null;
            report.cameraContract = "continuum-root-relative-camera/v1: physical root position/rotation; yaw 0.3rad, pitch 0.2rad, distance 50m, FOV 60deg; stock FlightCamera LateUpdate replaced only during capture. FX flow-axis camera retains stock orientation.";
            report.cameraObservedInvocations = observed;
            report.cameraMaxPositionErrorMeters = maxPositionError; report.cameraMaxAngleErrorDegrees = maxAngleError;
            report.cameraFailure = FailureReason;
        }
        public static void Stop()
        {
            if (harmony == null) return;
            Camera.onPreCull -= Observe; harmony.UnpatchAll(Owner); harmony = null;
            if (cameraOwner != null)
            {
                cameraOwner.SetDistanceImmediate(oldDistance); cameraOwner.FieldOfView = oldFoV;
                cameraOwner.transform.localPosition = oldLocalPosition; cameraOwner.transform.localRotation = oldLocalRotation;
                foreach (Camera camera in cameraOwner.cameras) if (camera != null) camera.fieldOfView = oldFoV;
            }
            if (pivot != null) { pivot.localPosition = oldPivotPosition; pivot.localRotation = oldPivotRotation; }
            cameraOwner = null; root = pivot = null; vessel = null;
        }
    }
}
