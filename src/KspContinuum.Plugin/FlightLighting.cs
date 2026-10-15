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
        readonly double cameraDistance;
        readonly bool controlledInspection;
        readonly MethodInfo update = AccessTools.DeclaredMethod(typeof(FlightCamera), "LateUpdate");
        readonly MethodInfo effectUpdate = AccessTools.DeclaredMethod(typeof(FXCamera), "LateUpdate");
        readonly MethodInfo depthUpdate = AccessTools.DeclaredMethod(typeof(FXDepthCamera), "LateUpdate");
        bool hooks, sceneHook, disposed, window;
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-flight-lighting/v1", mode, runId, vesselId, status = "prepared", cleanupStatus = "owned", reason;
            public string coordinateConvention = "Sun/body/vessel GetWorldPos3D local floating-origin coordinates; double subtraction before normalized direction; no scaled-space transform";
            public string scope = "physical camera-only preset; unchanged vessel/orbit/UT/lighting; no general illumination equivalence";
            public int processId, parts, observedCallbacks, captureCallbacks, faults;
            public bool vesselAdjusted = false, shadowed, frozenDuringCapture, restorationComplete, illuminatedCriterionRequired;
            public bool captureShadowed;
            public int captureGeometryChecks;
            public double maxCapturePhaseDegrees, maxCapturePhaseErrorDegrees, maxCaptureBodyCenterViewAngleDegrees, maxCaptureLightAgreementDegrees, minCaptureHorizonClearanceMeters;
            public double requestedPhaseDegrees, actualPhaseDegrees, solarIncidenceDegrees, horizonClearanceMeters, bodyCenterViewAngleDegrees;
            public double altitudeMeters, density, surfaceSpeed, universalTime, maxPositionErrorMeters, maxAngleErrorDegrees;
            public double cameraDistanceMeters, framingRadiusMeters;
            public double bodyAngularRadiusDegrees, bodyLimbViewAngleDegrees, maxCaptureBodyLimbViewAngleDegrees;
            public int framingRenderers;
            public string framingPolicy;
            public string clockScope;
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
            if (mode != "frontlit" && mode != "three-quarter-lit" && mode != "three-quarter-overview" && mode != "three-quarter-inspection" && mode != "side-lit" && mode != "backlit") throw new InvalidOperationException("Unknown physical lighting preset.");
            return new FlightLighting(args, vessel, mode);
        }
        FlightLighting(string[] args, Vessel vessel, string mode)
        {
            this.vessel = vessel; body = vessel == null ? null : vessel.mainBody; flight = FlightCamera.fetch;
            controlledInspection=mode=="three-quarter-inspection";
            if(controlledInspection && (!InspectionDeclared() || !NormalClock()))throw new InvalidOperationException("Camera-only inspection requires declared controlled rigid owner at exact normal-time entry.");
            root = vessel == null || vessel.rootPart == null ? null : vessel.rootPart.transform;
            pivot = flight == null ? null : flight.transform.parent;
            if (active != null || root == null || pivot == null || body == null || body.bodyName != "Kerbin" ||
                !vessel.loaded || vessel.packed || MapView.MapIsEnabled || TimeWarp.CurrentRate != 1 ||
                Planetarium.fetch == null || Planetarium.fetch.Sun == null || Sun.Instance == null || Sun.Instance.sunLight == null ||
                new[] { update, effectUpdate, depthUpdate }.Any(method => method == null || Harmony.GetPatchInfo(method)?.Owners.Any() == true))
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
            if(mode=="three-quarter-lit" || controlledInspection)perpendicular=-perpendicular;
            double phase = mode == "frontlit" ? 0 : (mode == "three-quarter-lit" || mode=="three-quarter-overview" || controlledInspection) ? 45 : mode == "side-lit" ? 75 : 150;
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
            cameraDistance=Distance;
            Report.framingPolicy="Original fixed 150m distance, vessel-root tracking.";
            if(mode=="three-quarter-lit" || controlledInspection)
            {
                cameraDistance=50;
                Report.framingPolicy=controlledInspection?"Camera-only root inspection: fixed50m and real45-degree solar phase; vessel-root tracking with cropped long booms. Planetary limb geometry observed without a planet framing requirement; no orbit preparation or physics writes.":"Root detail: fixed50m, vessel-root tracking; long booms may extend outside frame. Real45-degree solar phase; opposing radial-projected solar-plane offset frames the planetary limb, not the full craft envelope.";
            }
            if(mode=="three-quarter-overview")
            {
                double radius=0;int count=0;
                var seen=new System.Collections.Generic.HashSet<int>();
                foreach(Part part in vessel.parts)foreach(Renderer renderer in part.FindModelComponents<Renderer>())
                {
                    if(renderer==null || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff || renderer.GetComponent<VolumetricObjectBase>()!=null || !seen.Add(renderer.GetInstanceID()))continue;
                    Bounds bounds=renderer.bounds;
                    double extent=(bounds.center-root.position).magnitude+bounds.extents.magnitude;
                    if(!Finite(extent)||extent<=0)throw new InvalidOperationException("Craft framing bounds invalid.");
                    radius=Math.Max(radius,extent);count++;
                }
                if(count==0 || count>50000 || !Finite(radius))throw new InvalidOperationException("Craft framing mesh inventory absent/excessive.");
                double aspect=cameras.Min(c=>(double)c.aspect);
                if(!Finite(aspect)||aspect<=0)throw new InvalidOperationException("Craft framing aspect invalid.");
                double half=FieldOfView*Math.PI/360;
                double limitingHalf=Math.Min(half,Math.Atan(Math.Tan(half)*aspect));
                cameraDistance=Math.Max(Distance,1.25*radius/Math.Sin(limitingHalf)+5);
                if(!Finite(cameraDistance)||cameraDistance>750000)throw new InvalidOperationException("Craft framing distance outside local view range.");
                cameraDistance=(double)(float)cameraDistance;
                Report.framingRadiusMeters=radius;Report.framingRenderers=count;
                Report.framingPolicy="One-time active visible Part model MeshRenderer/SkinnedMeshRenderer bounds; particles and volumetric effect components excluded. Root-centered enclosing sphere, limiting vertical/horizontal 60-degree FOV, 25 percent sphere margin plus 5m, minimum150m. Frozen distance; no future deformation fit guarantee.";
            }
            Report.cameraDistanceMeters=cameraDistance;
            Report.clockScope=controlledInspection?"Declared control-warp owner only: exact normal rate1/index0/timeScale1/native dt0.02 or LOW rate4/index3/timeScale4/native dt0.08; camera-only treatment.":"Original normal-time physical lighting preset.";
            foreach (string arg in args) if (arg.StartsWith("--continuum-research-run=", StringComparison.Ordinal)) Report.runId = arg.Substring("--continuum-research-run=".Length);
            Vector3 cameraPosition = root.position + offsetDirection * (float)cameraDistance;
            Vector3d cameraToBody=body.position-(Vector3d)cameraPosition;
            Report.bodyCenterViewAngleDegrees = Angle(-offsetDirection, (Vector3)cameraToBody);
            if(mode=="three-quarter-lit" || mode=="three-quarter-overview" || controlledInspection)
            {
                Report.bodyAngularRadiusDegrees=BodyAngularRadius(cameraToBody);
                Report.bodyLimbViewAngleDegrees=Math.Abs(Report.bodyCenterViewAngleDegrees-Report.bodyAngularRadiusDegrees);
            }
            Report.illuminatedCriterionRequired = mode != "backlit";
            if ((Report.illuminatedCriterionRequired && Report.shadowed) ||
                ((mode == "frontlit" || mode=="three-quarter-overview") && Report.bodyCenterViewAngleDegrees > FieldOfView / 2 - 2) ||
                (mode=="three-quarter-lit" && Report.bodyLimbViewAngleDegrees > FieldOfView / 2 - 2) ||
                Angle(sun, -Sun.Instance.sunLight.transform.forward) > 1)
                throw new InvalidOperationException("Checkpoint cannot supply requested physical preset: frontlit/overview require Kerbin center in view; root detail requires its limb in view; illuminated presets require unoccluded sunlight; explicit orbital preparation is required.");
            oldPosition = flight.transform.localPosition; oldRotation = flight.transform.localRotation;
            oldPivotPosition = pivot.localPosition; oldPivotRotation = pivot.localRotation;
            oldDistance = flight.Distance; oldFov = flight.FieldOfView; oldPitch = FlightCamera.CamPitch; oldHeading = FlightCamera.CamHdg;
            oldFovs = cameras.Select(c => c.fieldOfView).ToArray(); active = this;
            try
            {
                harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FlightLighting), "Prefix")));
                harmony.Patch(effectUpdate, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FlightLighting), "EffectPrefix")));
                harmony.Patch(depthUpdate, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FlightLighting), "EffectPrefix")));
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
        static void EffectPrefix()
        {
            FlightLighting owner = active;
            if (owner == null || owner.disposed) return;
            try { owner.Check(); owner.Apply(); }
            catch (Exception error) { owner.Fail(error.Message); }
        }
        void Check()
        {
            if (vessel == null || root == null || flight == null || FlightGlobals.ActiveVessel != vessel || vessel.mainBody != body ||
                !vessel.loaded || vessel.packed || MapView.MapIsEnabled || vessel.parts.Count != Report.parts || !ClockAdmitted())
                throw new InvalidOperationException("Physical lighting ownership/context changed.");
            if (Planetarium.fetch == null || Planetarium.fetch.Sun == null || Sun.Instance == null || Sun.Instance.sunLight == null)
                throw new InvalidOperationException("Physical sunlight ownership changed.");
            Vector3d location = vessel.GetWorldPos3D(), radial = location - body.position, solar = Planetarium.fetch.Sun.position - location;
            if (!Finite(radial.magnitude) || !Finite(solar.magnitude) || radial.magnitude <= body.Radius || solar.magnitude <= 0)
                throw new InvalidOperationException("Physical celestial geometry changed.");
            Vector3 sun = (Vector3)solar.normalized;
            double phase = Angle(sun, offsetDirection), phaseError = Math.Abs(phase - Report.requestedPhaseDegrees);
            double along = -Vector3d.Dot(radial, solar.normalized);
            double clearance = (along > 0 ? (radial + along * solar.normalized).magnitude : radial.magnitude) - body.Radius;
            Vector3 cameraPosition = root.position + offsetDirection * (float)cameraDistance;
            Vector3d cameraToBody=body.position-(Vector3d)cameraPosition;
            double bodyAngle = Angle(-offsetDirection, (Vector3)cameraToBody);
            double limbAngle=0;
            if(Report.mode=="three-quarter-lit" || Report.mode=="three-quarter-overview" || controlledInspection)limbAngle=Math.Abs(bodyAngle-BodyAngularRadius(cameraToBody));
            double lightAgreement = Angle(sun, -Sun.Instance.sunLight.transform.forward);
            if (window)
            {
                if (Report.captureGeometryChecks++ == 0) Report.minCaptureHorizonClearanceMeters = clearance;
                else Report.minCaptureHorizonClearanceMeters = Math.Min(Report.minCaptureHorizonClearanceMeters, clearance);
                Report.maxCapturePhaseDegrees = Math.Max(Report.maxCapturePhaseDegrees, phase);
                Report.maxCapturePhaseErrorDegrees = Math.Max(Report.maxCapturePhaseErrorDegrees, phaseError);
                Report.maxCaptureBodyCenterViewAngleDegrees = Math.Max(Report.maxCaptureBodyCenterViewAngleDegrees, bodyAngle);
                Report.maxCaptureBodyLimbViewAngleDegrees=Math.Max(Report.maxCaptureBodyLimbViewAngleDegrees,limbAngle);
                Report.maxCaptureLightAgreementDegrees = Math.Max(Report.maxCaptureLightAgreementDegrees, lightAgreement);
                Report.captureShadowed |= clearance <= 0;
            }
            if (phaseError > 1 || lightAgreement > 1 || (Report.illuminatedCriterionRequired && clearance <= 0) ||
                ((Report.mode == "frontlit" || Report.mode=="three-quarter-overview") && bodyAngle > FieldOfView / 2 - 2) ||
                (Report.mode=="three-quarter-lit" && limbAngle > FieldOfView / 2 - 2))
                throw new InvalidOperationException("Physical lighting illumination/view criterion changed.");
        }
        void Apply()
        {
            pivot.position = root.position; pivot.rotation = Quaternion.LookRotation(-offsetDirection, viewUp);
            flight.transform.localPosition = Vector3.back * (float)cameraDistance; flight.transform.localRotation = Quaternion.identity;
            flight.SetDistanceImmediate((float)cameraDistance); flight.FieldOfView = (float)FieldOfView;
            foreach (Camera camera in cameras) if (camera != null) camera.fieldOfView = (float)FieldOfView;
        }
        void Observe(Camera camera)
        {
            if (disposed || !cameras.Contains(camera)) return;
            try
            {
                Check(); Report.observedCallbacks++; if (window) Report.captureCallbacks++;
                double positionError = (camera.transform.position - (root.position + offsetDirection * (float)cameraDistance)).magnitude;
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
                Report.restorationComplete = new[] { update, effectUpdate, depthUpdate }.All(method => Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) != true) &&
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
        static bool InspectionDeclared()
        {
            string output=Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_OWNER");
            return Environment.GetEnvironmentVariable("CONTINUUM_LIVE_RIGID_SCENARIO")=="control-warp" && !string.IsNullOrEmpty(output) && System.IO.Path.IsPathRooted(output);
        }
        static bool NormalClock(){return TimeWarp.CurrentRate==1 && TimeWarp.CurrentRateIndex==0 && Time.timeScale==1 && Time.fixedDeltaTime==.02f;}
        bool ClockAdmitted()
        {
            if(!controlledInspection)return TimeWarp.CurrentRate==1;
            return InspectionDeclared() && (NormalClock() || TimeWarp.WarpMode==TimeWarp.Modes.LOW && TimeWarp.CurrentRate==4 && TimeWarp.CurrentRateIndex==3 && Time.timeScale==4 && Time.fixedDeltaTime==.08f);
        }
        double BodyAngularRadius(Vector3d cameraToBody)
        {
            double distance=cameraToBody.magnitude;
            if(!Finite(distance)||distance<=body.Radius)throw new InvalidOperationException("Camera must remain outside the planet for limb framing.");
            return Math.Asin(body.Radius/distance)*180/Math.PI;
        }
        static double Angle(Vector3 a, Vector3 b) { return Math.Atan2(Vector3.Cross(a.normalized, b.normalized).magnitude, Vector3.Dot(a.normalized, b.normalized)) * 180 / Math.PI; }
        static double[] V(Vector3 value) { return new[] { (double)value.x, value.y, value.z }; }
    }
}
