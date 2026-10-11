using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace KspContinuum
{
    internal static class FlightDiagnosticControls
    {
        const string LockName = "continuum-flight-diagnostic-camera";
        static bool applied, cameraSeeded;
        static int originalVSync, originalFrameRate, originalAntialiasing, originalCascades, originalAeroQuality;
        static bool aeroQualityChanged;
        static float originalShadowDistance;
        static readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        static FlightDiagnosticControlReport recorded;

        public static void Apply()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (applied || Array.IndexOf(arguments, "--continuum-flight-diagnostic") < 0) return;
            ResearchPanelPresentation.FreezeDiagnosticMode();
            originalVSync = QualitySettings.vSyncCount;
            originalFrameRate = Application.targetFrameRate;
            originalAntialiasing = QualitySettings.antiAliasing;
            originalCascades = QualitySettings.shadowCascades;
            originalShadowDistance = QualitySettings.shadowDistance;
            originalAeroQuality = GameSettings.AERO_FX_QUALITY;
            applied = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            foreach (string argument in arguments)
            {
                const string prefix = "--continuum-flight-resolution=";
                if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string[] dimensions = argument.Substring(prefix.Length).Split('x');
                int width, height;
                if (dimensions.Length != 2 || !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out width) ||
                    !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out height) ||
                    width < 320 || height < 240 || width > 8192 || height > 8192)
                    throw new InvalidOperationException("Invalid diagnostic resolution.");
                Screen.SetResolution(width, height, false);
            }
            if (Array.IndexOf(arguments, "--continuum-flight-no-shadows") >= 0)
            { QualitySettings.shadowDistance = 0; QualitySettings.shadowCascades = 0; }
            if (Array.IndexOf(arguments, "--continuum-flight-no-aa") >= 0) QualitySettings.antiAliasing = 0;
            int? aeroQuality = null;
            foreach (string argument in arguments)
            {
                const string prefix = "--continuum-flight-aero-quality=";
                if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int value;
                if (aeroQuality.HasValue || !int.TryParse(argument.Substring(prefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out value) || value < 0 || value > 3)
                    throw new InvalidOperationException("Invalid diagnostic aero quality.");
                aeroQuality = value;
            }
            if (Array.IndexOf(arguments, "--continuum-flight-no-aero-fx") >= 0)
            {
                if (aeroQuality.HasValue) throw new InvalidOperationException("Conflicting diagnostic aero controls.");
                aeroQuality = 0;
            }
            if (aeroQuality.HasValue)
            {
                aeroQualityChanged = true;
                GameSettings.AERO_FX_QUALITY = aeroQuality.Value;
                UpdateAeroSettings();
            }
            InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS | ControlTypes.CAMERAMODES, LockName);
            SeedCamera();
        }

        public static void SeedCamera()
        {
            if (!applied || cameraSeeded || FlightCamera.fetch == null) return;
            FlightCamera.SetModeImmediate(FlightCamera.Modes.FREE);
            FlightCamera.CamPitch = .2f;
            FlightCamera.CamHdg = .3f;
            FlightCamera.fetch.SetDistanceImmediate(150f);
            cameraSeeded = true;
        }

        public static void Record(string directory)
        {
            if (!applied) return;
            SeedCamera();
            if (!cameraSeeded) throw new InvalidOperationException("Flight camera was unavailable at capture admission.");
            FlightCameraFreeze.Start(FlightGlobals.ActiveVessel);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-flight-hide-vessel") >= 0)
            {
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null || !vessel.loaded || vessel.packed)
                    throw new InvalidOperationException("Vessel renderer diagnostic requires an active physical vessel.");
                foreach (Part part in vessel.parts)
                    foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
                        if (renderer != null && renderer.enabled && !hiddenRenderers.Contains(renderer))
                        { hiddenRenderers.Add(renderer); renderer.enabled = false; }
                if (hiddenRenderers.Count == 0) throw new InvalidOperationException("Vessel renderer diagnostic disabled no renderers.");
            }
            string runId = null;
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.StartsWith("--continuum-research-run=", StringComparison.Ordinal))
                    runId = argument.Substring("--continuum-research-run=".Length);
            FlightRenderCensus.Record(FlightGlobals.ActiveVessel, directory, runId, hiddenRenderers);
            FlightMaterialCensus.Record(FlightGlobals.ActiveVessel, directory, runId);
            var report = new FlightDiagnosticControlReport
            {
                runId = runId, width = Screen.width, height = Screen.height,
                vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                antialiasing = QualitySettings.antiAliasing, shadowCascades = QualitySettings.shadowCascades,
                shadowDistance = QualitySettings.shadowDistance, pixelLights = QualitySettings.pixelLightCount,
                qualityLevel = QualitySettings.GetQualityLevel(), masterTextureLimit = QualitySettings.masterTextureLimit,
                cameraMode = FlightCamera.CamMode, cameraPitch = FlightCamera.CamPitch, cameraHeading = FlightCamera.CamHdg,
                cameraDistance = FlightCamera.fetch.Distance, cameraFieldOfView = FlightCamera.fetch.FieldOfView,
                renderer = SystemInfo.graphicsDeviceVersion, colorSpace = QualitySettings.activeColorSpace.ToString(),
                originalVSync = originalVSync, originalFrameRate = originalFrameRate,
                hiddenVesselRenderers = hiddenRenderers.Count,
                aeroFxQuality = GameSettings.AERO_FX_QUALITY, originalAeroFxQuality = originalAeroQuality
            };
            report.researchPanelMode = ResearchPanelPresentation.Mode;
            report.researchPanelVisible = ResearchPanelPresentation.Visible;
            report.researchPanelComponentPresent = ResearchPanelPresentation.ComponentPresent;
            RecordAeroState(report);
            FlightCameraFreeze.Record(report);
            recorded = report;
            File.WriteAllText(Path.Combine(directory, "diagnostic-controls.json"), ReportJson.Encode(report));
        }

        public static void RecordEnd(string directory)
        {
            if (recorded == null) return;
            recorded.phase = "capture-end";
            foreach (Renderer renderer in hiddenRenderers)
                if (renderer != null && !renderer.enabled) recorded.stillHiddenVesselRenderers++;
            recorded.cameraDistance = FlightCamera.fetch == null ? float.NaN : FlightCamera.fetch.Distance;
            recorded.cameraMode = FlightCamera.CamMode;
            recorded.cameraPitch = FlightCamera.CamPitch;
            recorded.cameraHeading = FlightCamera.CamHdg;
            recorded.cameraFieldOfView = FlightCamera.fetch == null ? float.NaN : FlightCamera.fetch.FieldOfView;
            recorded.aeroFxQuality = GameSettings.AERO_FX_QUALITY;
            recorded.researchPanelMode = ResearchPanelPresentation.Mode;
            recorded.researchPanelVisible = ResearchPanelPresentation.Visible;
            recorded.researchPanelComponentPresent = ResearchPanelPresentation.ComponentPresent;
            RecordAeroState(recorded);
            FlightCameraFreeze.Record(recorded);
            File.WriteAllText(Path.Combine(directory, "diagnostic-controls-end.json"), ReportJson.Encode(recorded));
            if (FlightCameraFreeze.FailureReason != null || recorded.cameraObservedInvocations == 0)
                throw new InvalidOperationException("Camera freeze was not observed intact: " + recorded.cameraFailure);
        }

        public static void Restore()
        {
            if (!applied) return;
            FlightCameraFreeze.Stop();
            foreach (Renderer renderer in hiddenRenderers) if (renderer != null) renderer.enabled = true;
            hiddenRenderers.Clear();
            QualitySettings.vSyncCount = originalVSync;
            Application.targetFrameRate = originalFrameRate;
            QualitySettings.antiAliasing = originalAntialiasing;
            QualitySettings.shadowCascades = originalCascades;
            QualitySettings.shadowDistance = originalShadowDistance;
            if (aeroQualityChanged)
            {
                GameSettings.AERO_FX_QUALITY = originalAeroQuality;
                UpdateAeroSettings();
                aeroQualityChanged = false;
            }
            InputLockManager.RemoveControlLock(LockName);
            applied = false;
            cameraSeeded = false;
            recorded = null;
        }

        static void RecordAeroState(FlightDiagnosticControlReport report)
        {
            report.aeroFxInstances = report.enabledAeroFxCameras = report.enabledAeroDepthCameras = 0;
            report.maxAeroFxScalar = 0;
            foreach (AerodynamicsFX effect in UnityEngine.Object.FindObjectsOfType<AerodynamicsFX>())
            {
                report.aeroFxInstances++;
                report.maxAeroFxScalar = Math.Max(report.maxAeroFxScalar, effect.FxScalar);
                Camera camera = effect.fxCamera == null ? null : effect.fxCamera.GetComponent<Camera>();
                if (camera != null && camera.enabled) report.enabledAeroFxCameras++;
                if (effect.fxCamera != null)
                {
                    report.aeroOcclusionMapResolution = effect.fxCamera.occlusionMapResolution;
                    report.aeroShaderLod = effect.fxCamera.shaderLOD;
                    Shader[] shaders = effect.fxCamera.ReplacementShaders;
                    Shader shader = shaders == null || shaders.Length == 0 ? null :
                        shaders[Math.Max(0, Math.Min(effect.fxCamera.shaderLOD, shaders.Length - 1))];
                    report.aeroReplacementShader = shader == null ? null : shader.name;
                }
                if (effect.fxDepthCamera != null && effect.fxDepthCamera.depthCamera != null && effect.fxDepthCamera.depthCamera.enabled)
                    report.enabledAeroDepthCameras++;
            }
        }

        static void UpdateAeroSettings()
        {
            MethodInfo aero = typeof(AerodynamicsFX).GetMethod("OnSettingsUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo camera = typeof(FXCamera).GetMethod("OnSettingsUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            if (aero == null || camera == null) throw new InvalidOperationException("Stock aero settings boundary unavailable.");
            foreach (AerodynamicsFX effect in UnityEngine.Object.FindObjectsOfType<AerodynamicsFX>()) aero.Invoke(effect, null);
            foreach (FXCamera effect in UnityEngine.Object.FindObjectsOfType<FXCamera>()) camera.Invoke(effect, null);
        }
    }

    [Serializable] internal sealed class FlightDiagnosticControlReport
    {
        public string schema = "continuum-flight-controls/v1", runId, renderer, colorSpace, aeroReplacementShader;
        public string phase = "capture-start", researchPanelMode;
        public bool researchPanelVisible, researchPanelComponentPresent;
        public string cameraScope = "Root-relative view and projection frozen throughout capture, with native camera pre-cull observations; input locked.";
        public string cameraContract, cameraFailure;
        public bool cameraFrozen;
        public int cameraObservedInvocations;
        public double cameraMaxPositionErrorMeters, cameraMaxAngleErrorDegrees;
        public int width, height, vSyncCount, targetFrameRate, antialiasing, shadowCascades, pixelLights,
            qualityLevel, masterTextureLimit, cameraMode, originalVSync, originalFrameRate, hiddenVesselRenderers,
            stillHiddenVesselRenderers, aeroFxQuality, originalAeroFxQuality, aeroFxInstances,
            enabledAeroFxCameras, enabledAeroDepthCameras, aeroOcclusionMapResolution, aeroShaderLod;
        public float shadowDistance, cameraPitch, cameraHeading, cameraDistance, cameraFieldOfView, maxAeroFxScalar;
    }
}
