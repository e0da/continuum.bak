using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    // Camera-only physical presets. Checkpoint/orbit preparation remains a separate workload treatment.
    internal sealed class FlightLighting : IDisposable
    {
        const string Owner = "continuum.flight.lighting";
        const double Distance = 150, FieldOfView = 60;
        static FlightLighting active;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Vessel vessel;
        readonly CelestialBody body;
        readonly FlightCamera flight;
        readonly Transform root, pivot;
        readonly Camera[] cameras;
        readonly float[] oldFovs;
        readonly Vector3 oldPosition, oldPivotPosition;
        readonly Quaternion oldRotation, oldPivotRotation;
        readonly float oldDistance, oldFov, oldPitch, oldHeading;
        readonly Vector3 offsetDirection, viewUp;
        readonly MethodInfo update = AccessTools.DeclaredMethod(typeof(FlightCamera), "LateUpdate");
        bool hooks, sceneHook, disposed, window;
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-flight-lighting/v1", mode, runId, vesselId, status = "prepared", cleanupStatus = "owned", reason;
            public string coordinateConvention = "Sun/body/vessel GetWorldPos3D local floating-origin coordinates; double subtraction before normalized direction; no scaled-space transform";
            public string scope = "physical camera-only preset; unchanged vessel/orbit/UT/lighting; no general illumination equivalence";
            public int processId, parts, observedCallbacks, captureCallbacks, faults;
            public bool vesselAdjusted, shadowed, frozenDuringCapture, restorationComplete, illuminatedCriterionRequired;
            public double requestedPhaseDegrees, actualPhaseDegrees, solarIncidenceDegrees, horizonClearanceMeters, bodyCenterViewAngleDegrees;
            public double altitudeMeters, density, surfaceSpeed, universalTime, maxPositionErrorMeters, maxAngleErrorDegrees;
            public double[] toSun, bodyRadial, cameraOffset, lightRayDirection;
            public long captureStartTicks, captureEndTicks, clockFrequency = Stopwatch.Frequency;
        }
        public ReportData Report { get; private set; }
        public static FlightLighting Create(string[] args, Vessel vessel)
        {
            string prefix = "--continuum-flight-lighting=";
            string[] modes = args.Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (modes.Length == 0) return null;
            if (modes.Length != 1) throw new InvalidOperationException("Exactly one flight lighting preset is required.");
            string mode = modes[0].Substring(prefix.Length);
            if (mode != "frontlit" && mode != "side-lit" && mode != "backlit") throw new InvalidOperationException("Unknown physical lighting preset.");
            return new FlightLighting(args, vessel, mode);
        }
        FlightLighting(string[] args, Vessel vessel, string mode)
        {
            this.vessel = vessel; body = vessel == null ? null : vessel.mainBody; flight = FlightCamera.fetch;
            root = vessel == null || vessel.rootPart == null ? null : vessel.rootPart.transform;
            pivot = flight == null ? null : flight.transform.parent;
            if (active != null || root == null || pivot == null || body == null || body.bodyName != "Kerbin" ||
                !vessel.loaded || vessel.packed || MapView.MapIsEnabled || TimeWarp.CurrentRate != 1 ||
                Planetarium.fetch == null || Planetarium.fetch.Sun == null || Sun.Instance == null || Sun.Instance.sunLight == null || update == null ||
                Harmony.GetPatchInfo(update)?.Owners.Any() == true)
                throw new InvalidOperationException("Physical lighting needs one unpatched active normal-time Kerbin flight camera.");
            cameras = flight.cameras.Where(c => c != null).Distinct().ToArray();
            if (cameras.Length == 0 || !cameras.Contains(flight.mainCamera)) throw new InvalidOperationException("Flight camera identity is incomplete.");
            Vector3d location = vessel.GetWorldPos3D();
            Vector3d radial = location - body.position, solar = Planetarium.fetch.Sun.position - location;
            if (!Finite(radial.magnitude) || !Finite(solar.magnitude) || radial.magnitude <= body.Radius || solar.magnitude <= 0)
                throw new InvalidOperationException("Physical celestial geometry is invalid.");
            Vector3 sun = (Vector3)solar.normalized, outward = (Vector3)radial.normalized;
            Vector3 perpendicular = Vector3.ProjectOnPlane(outward, sun);
            if (perpendicular.sqrMagnitude < 1e-10f) perpendicular = Vector3.ProjectOnPlane(body.transform.up, sun);
            if (perpendicular.sqrMagnitude < 1e-10f) perpendicular = Vector3.ProjectOnPlane(Vector3.right, sun);
            perpendicular.Normalize();
            double phase = mode == "frontlit" ? 0 : mode == "side-lit" ? 75 : 150;
            double radians = phase * Math.PI / 180;
            offsetDirection = (sun * (float)Math.Cos(radians) + perpendicular * (float)Math.Sin(radians)).normalized;
            viewUp = Vector3.ProjectOnPlane(outward, -offsetDirection);
            if (viewUp.sqrMagnitude < 1e-10f) viewUp = Vector3.ProjectOnPlane(body.transform.up, -offsetDirection);
            if (viewUp.sqrMagnitude < 1e-10f) viewUp = Vector3.ProjectOnPlane(Vector3.right, -offsetDirection);
            viewUp.Normalize();
            double along = -Vector3d.Dot(radial, solar.normalized);
            double clearance = (along > 0 ? (radial + along * solar.normalized).magnitude : radial.magnitude) - body.Radius;
            Report = new ReportData { mode = mode, processId = Process.GetCurrentProcess().Id, vesselId = vessel.id.ToString("D"), parts = vessel.parts.Count,
                requestedPhaseDegrees = phase, solarIncidenceDegrees = Angle(outward, sun), horizonClearanceMeters = clearance, shadowed = clearance <= 0,
                toSun = V(sun), bodyRadial = V(outward), cameraOffset = V(offsetDirection), lightRayDirection = V(Sun.Instance.sunLight.transform.forward),
                altitudeMeters = vessel.altitude, density = vessel.atmDensity, surfaceSpeed = vessel.srfSpeed, universalTime = Planetarium.GetUniversalTime() };
            foreach (string arg in args) if (arg.StartsWith("--continuum-research-run=", StringComparison.Ordinal)) Report.runId = arg.Substring("--continuum-research-run=".Length);
            Vector3 cameraPosition = root.position + offsetDirection * (float)Distance;
            Report.bodyCenterViewAngleDegrees = Angle(-offsetDirection, (Vector3)(body.position - (Vector3d)cameraPosition));
            Report.illuminatedCriterionRequired = mode != "backlit";
            if ((Report.illuminatedCriterionRequired && Report.shadowed) ||
                (mode == "frontlit" && Report.bodyCenterViewAngleDegrees > FieldOfView / 2 - 2) ||
                Angle(sun, -Sun.Instance.sunLight.transform.forward) > 1)
                throw new InvalidOperationException("Checkpoint cannot supply requested physical preset: frontlit requires Kerbin center in view; illuminated presets require unoccluded sunlight; explicit orbital preparation is required.");
            oldPosition = flight.transform.localPosition; oldRotation = flight.transform.localRotation;
            oldPivotPosition = pivot.localPosition; oldPivotRotation = pivot.localRotation;
            oldDistance = flight.Distance; oldFov = flight.FieldOfView; oldPitch = FlightCamera.CamPitch; oldHeading = FlightCamera.CamHdg;
            oldFovs = cameras.Select(c => c.fieldOfView).ToArray(); active = this;
            try
            {
                harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FlightLighting), "Prefix")));
                Camera.onPreCull += Observe; hooks = true;
                GameEvents.onGameSceneLoadRequested.Add(Scene); sceneHook = true;
                Apply();
            }
            catch { Dispose(); throw; }
        }
        static bool Prefix(FlightCamera __instance)
        {
            FlightLighting owner = active;
            if (owner == null || owner.disposed || __instance != owner.flight) return true;
            try { owner.Check(); owner.Apply(); return false; }
            catch (Exception error) { owner.Fail(error.Message); return true; }
        }
        void Check()
        {
            if (vessel == null || root == null || flight == null || FlightGlobals.ActiveVessel != vessel || vessel.mainBody != body ||
                !vessel.loaded || vessel.packed || MapView.MapIsEnabled || vessel.parts.Count != Report.parts || TimeWarp.CurrentRate != 1)
                throw new InvalidOperationException("Physical lighting ownership/context changed.");
        }
        void Apply()
        {
            pivot.position = root.position; pivot.rotation = Quaternion.LookRotation(-offsetDirection, viewUp);
            flight.transform.localPosition = Vector3.back * (float)Distance; flight.transform.localRotation = Quaternion.identity;
            flight.SetDistanceImmediate((float)Distance); flight.FieldOfView = (float)FieldOfView;
            foreach (Camera camera in cameras) if (camera != null) camera.fieldOfView = (float)FieldOfView;
        }
        void Observe(Camera camera)
        {
            if (disposed || !cameras.Contains(camera)) return;
            try
            {
                Check(); Report.observedCallbacks++; if (window) Report.captureCallbacks++;
                double positionError = (camera.transform.position - (root.position + offsetDirection * (float)Distance)).magnitude;
                double angleError = Angle(camera.transform.forward, -offsetDirection);
                Report.maxPositionErrorMeters = Math.Max(Report.maxPositionErrorMeters, positionError);
                Report.maxAngleErrorDegrees = Math.Max(Report.maxAngleErrorDegrees, angleError);
                Report.actualPhaseDegrees = Angle((Vector3)(Planetarium.fetch.Sun.position - vessel.GetWorldPos3D()), camera.transform.position - root.position);
                if (positionError > .005 || angleError > .005 || Math.Abs(Report.actualPhaseDegrees - Report.requestedPhaseDegrees) > 1 ||
                    Mathf.Abs(camera.fieldOfView - (float)FieldOfView) > .001)
                    throw new InvalidOperationException("Physical lighting rendered pose differs from admitted recipe.");
            }
            catch (Exception error) { Fail(error.Message); }
        }
        public void BeginWindow(long ticks) { Check(); window = true; Report.captureStartTicks = ticks; Report.frozenDuringCapture = true; }
        public void EndWindow(long ticks) { window = false; Report.captureEndTicks = ticks; }
        void Scene(GameScenes scene) { Fail("Scene transition invalidated physical lighting."); }
        void Fail(string reason) { Report.faults++; Report.status = "invalid"; Report.reason = reason; Dispose(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true; window = false;
            try
            {
                if (hooks) { Camera.onPreCull -= Observe; hooks = false; }
                if (sceneHook) { GameEvents.onGameSceneLoadRequested.Remove(Scene); sceneHook = false; }
                harmony.UnpatchAll(Owner);
                if (flight == null || pivot == null) throw new InvalidOperationException("Lighting camera disappeared before restoration.");
                flight.SetDistanceImmediate(oldDistance); flight.FieldOfView = oldFov;
                FlightCamera.CamPitch = oldPitch; FlightCamera.CamHdg = oldHeading;
                pivot.localPosition = oldPivotPosition; pivot.localRotation = oldPivotRotation;
                flight.transform.localPosition = oldPosition; flight.transform.localRotation = oldRotation;
                for (int i = 0; i < cameras.Length; i++) if (cameras[i] != null) cameras[i].fieldOfView = oldFovs[i];
                Report.restorationComplete = Harmony.GetPatchInfo(update)?.Owners.Contains(Owner) != true &&
                    flight.transform.localPosition == oldPosition && flight.transform.localRotation == oldRotation &&
                    pivot.localPosition == oldPivotPosition && pivot.localRotation == oldPivotRotation &&
                    cameras.Select((c, i) => c != null && c.fieldOfView == oldFovs[i]).All(x => x);
                Report.cleanupStatus = Report.restorationComplete ? "restored-camera-removed-owned-hooks" : "cleanup-error";
                if (!Report.restorationComplete) Report.faults++;
            }
            catch (Exception error) { Report.faults++; Report.reason = error.Message; Report.cleanupStatus = "cleanup-error"; }
            finally { if (ReferenceEquals(active, this)) active = null; }
            Report.status = Report.faults == 0 && Report.captureCallbacks > 0 && Report.captureEndTicks > Report.captureStartTicks ? "complete-experimental" : "invalid";
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static double Angle(Vector3 a, Vector3 b) { return Math.Atan2(Vector3.Cross(a.normalized, b.normalized).magnitude, Vector3.Dot(a.normalized, b.normalized)) * 180 / Math.PI; }
        static double[] V(Vector3 value) { return new[] { (double)value.x, value.y, value.z }; }
    }
}
