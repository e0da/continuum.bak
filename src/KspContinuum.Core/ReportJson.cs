using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace KspContinuum
{
    public static class ReportJson
    {
        public static string Encode(object report)
        {
            var output = new StringBuilder();
            Write(output, report);
            return output.ToString();
        }

        static void Write(StringBuilder output, object value)
        {
            if (value == null) { output.Append("null"); return; }
            if (value is string)
            {
                output.Append('"');
                foreach (char c in (string)value)
                {
                    if (c == '"' || c == '\\') output.Append('\\').Append(c);
                    else if (c < 32 || char.IsSurrogate(c))
                        output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                }
                output.Append('"'); return;
            }
            if (value is bool) { output.Append((bool)value ? "true" : "false"); return; }
            if (value is Enum) { Write(output, value.ToString()); return; }
            if (value is int || value is uint || value is long || value is float || value is double)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new ArgumentException("Report contains a nonfinite number.");
                output.Append(((IFormattable)value).ToString(value is float || value is double ? "R" : null,
                    CultureInfo.InvariantCulture)); return;
            }
            if (value is Array || value is ReadOnlyCollection<ForcePartObservation> ||
                value is ReadOnlyCollection<ForceAtPositionObservation> || value is ReadOnlyCollection<LifecyclePartSample> ||
                value is ReadOnlyCollection<AeroPatchTarget> || value is ReadOnlyCollection<AeroPatchEntry> ||
                value is ReadOnlyCollection<AeroCaptureSample> || value is ReadOnlyCollection<AeroBodyPublication> ||
                value is ReadOnlyCollection<AeroDragCubeState> || value is ReadOnlyCollection<double> ||
                value is ReadOnlyCollection<AeroCurveKey> ||
                value is ReadOnlyCollection<string>)
            {
                output.Append('['); bool first = true;
                foreach (var item in (IEnumerable)value)
                {
                    if (!first) output.Append(','); first = false; Write(output, item);
                }
                output.Append(']'); return;
            }
            var type = value.GetType();
            bool pluginDiagnostic = type.Assembly == typeof(ReportJson).Assembly &&
                (type.FullName == "KspContinuum.FlightDiagnosticControlReport" ||
                 type.FullName == "KspContinuum.FlightLighting+ReportData" ||
                 type.FullName == "KspContinuum.DockingQualification+ReportData" ||
                 type.FullName == "KspContinuum.DockingQualification+EventRow" ||
                 type.FullName == "KspContinuum.DockingQualification+ResourceRow" ||
                 type.FullName == "KspContinuum.DockingQualification+SetupRow" ||
                 type.FullName == "KspContinuum.DockingQualification+ControlRow" ||
                 type.FullName == "KspContinuum.AssetGenerationReport" ||
                 type.FullName == "KspContinuum.AssetGenerationCapability" ||
                 type.FullName == "KspContinuum.VesselRuntimeCapture+CaptureReport" ||
                 type.FullName == "KspContinuum.ConvectionCapture+CaptureReport" ||
                 type.FullName == "KspContinuum.VesselRuntimeCapture+ResultRow" ||
                 type.FullName == "KspContinuum.FlightLocalCamera+Report" ||
                 type.FullName == "KspContinuum.FlightLocalCamera+CameraRow" ||
                 type.FullName == "KspContinuum.FlightLocalCamera+State" ||
                 type.FullName == "KspContinuum.FlightCameraChain+Report" ||
                 type.FullName == "KspContinuum.FlightCameraChain+CamRow" ||
                 type.FullName == "KspContinuum.FlightCameraChain+SceneRow" ||
                 type.FullName == "KspContinuum.FlightWorldDraw+Report" ||
                 type.FullName == "KspContinuum.FlightWorldDraw+OwnerRow" ||
                 type.FullName == "KspContinuum.FlightWorldDraw+HookRow" ||
                 type.FullName == "KspContinuum.FlightRenderCensus+Report" ||
                 type.FullName == "KspContinuum.FlightRenderCensus+CameraRow" ||
                 type.FullName == "KspContinuum.FlightRenderCensus+ShaderRow" ||
                 type.FullName == "KspContinuum.FlightRenderCensus+Row" ||
                 type.FullName == "KspContinuum.FlightRenderCensus+Group" ||
                 type.FullName == "KspContinuum.FlightRenderCensus+MaterialRow" ||
                 type.FullName == "KspContinuum.FlightAeroBatcher+Report" ||
                 type.FullName == "KspContinuum.FlightIntegratorUpdateAttribution+ReportData" ||
                 type.FullName == "KspContinuum.FlightIntegratorUpdateAttribution+Window" ||
                 type.FullName == "KspContinuum.FlightAeroFieldRenderer+Report" ||
                 type.FullName == "KspContinuum.FlightPartBatcher+Report" ||
                 type.FullName == "KspContinuum.ResidentMaterialFixtureCapture+Topology" ||
                 type.FullName == "KspContinuum.StartupPreparedBindings+Report" ||
                 type.FullName == "KspContinuum.StartupPreparedBindings+Row" ||
                 type.FullName == "KspContinuum.MechJebProviderPlan+Report" ||
                 type.FullName == "KspContinuum.BackendMenuPresentation+Report" ||
                 type.FullName == "KspContinuum.BackendMenuPresentation+ShaderRow" ||
                 type.FullName == "KspContinuum.BackendMenuPresentation+MaterialRow" ||
                 type.FullName == "KspContinuum.BackendMenuPresentation+CameraRow" ||
                 type.FullName == "KspContinuum.StartupSiteProof+Report" ||
                 type.FullName == "KspContinuum.StartupSiteProof+Row" ||
                 type.FullName == "KspContinuum.FlightAeroBatcher+Fallback" ||
                 type.FullName == "KspContinuum.FlightAeroBatcher+CameraCount" ||
                 type.FullName == "KspContinuum.FlightMaterialCensus+Report" ||
                 type.FullName == "KspContinuum.FlightMaterialCensus+Row" ||
                 type.FullName == "KspContinuum.FlightMaterialCensus+Property" ||
                 type.FullName == "KspContinuum.FlightMaterialCensus+Group" ||
                 type.FullName == "KspContinuum.AtmosphericEntryPreparation+Report" ||
                 type.FullName == "KspContinuum.AtmosphericEntryPreparation+Sample" ||
                 type.FullName == "KspContinuum.StartupModelTiming+Report" ||
                 type.FullName == "KspContinuum.StartupConstructionTiming+Report" ||
                 type.FullName == "KspContinuum.StartupConstructionTiming+Scope" ||
                 type.FullName == "KspContinuum.StartupConstructionTiming+Resume" ||
                 type.FullName == "KspContinuum.StartupConstructionTiming+Child" ||
                 type.FullName == "KspContinuum.StartupConstructionTiming+Readiness" ||
                 type.FullName == "KspContinuum.FlightGpuTiming+Report" ||
                 type.FullName == "KspContinuum.FlightGpuTiming+CameraRow" ||
                 type.FullName == "KspContinuum.StartupModelTiming+Metric" ||
                 type.FullName == "KspContinuum.StartupWorldTiming+Report" ||
                 type.FullName == "KspContinuum.StartupWorldTiming+Metric" ||
                 type.FullName == "KspContinuum.StartupWorldTiming+Point" ||
                 type.FullName == "KspContinuum.StartupWorldTiming+Wake" ||
                 type.FullName == "KspContinuum.StartupTerrainBuildTiming+Report" ||
                 type.FullName == "KspContinuum.StartupTerrainBuildTiming+Metric" ||
                 type.FullName == "KspContinuum.StartupTerrainDispatchPlan+Report");
            if (type != typeof(BenchReport) && type != typeof(Sample) && type != typeof(QueuedForceIsolationSample) && type != typeof(VesselReport) &&
                type != typeof(PartReport) && type != typeof(ProbeReport) && type != typeof(ResearchPanelReport) && type != typeof(MarkerReport) &&
                type != typeof(CallbackAttributionReport) && type != typeof(CallbackAttributionRow) &&
                type != typeof(FeedInputAttributionReport) && type != typeof(MechJebDriveAttributionReport) && type != typeof(InputDelegateChain) && type != typeof(InputDelegateMember) &&
                type != typeof(DryBuoyancyReport) && type != typeof(FlightPerturbationReport) &&
                type != typeof(CoastingAdapterReport) &&
                type != typeof(ProfileFrame) && type != typeof(ProfileDistribution) && type != typeof(ProfileMarkerSummary) &&
                type != typeof(LoopTimingReport) && type != typeof(LoopTimingScope) && type != typeof(LoopTimingSample) &&
                type != typeof(WriterCensusReport) && type != typeof(WriterCensusInterval) &&
                type != typeof(WriterCensusSnapshot) && type != typeof(WriterCensusBody) &&
                type != typeof(PhysicsSubstitutionCanaryReport) && type != typeof(AeroForceSubstitutionReport) &&
                type != typeof(AeroSetDragSubstitutionReport) &&
                type != typeof(ForceObservationReport) && type != typeof(ForceObservationContext) &&
                type != typeof(LifecycleTraceReport) && type != typeof(LifecycleTraceEvent) &&
                type != typeof(LifecycleTraceContext) && type != typeof(LifecyclePartSample) &&
                type != typeof(LifecycleOrderQualificationReport) && type != typeof(LifecycleOrderTrial) &&
                type != typeof(ForceObservationBatch) && type != typeof(ForcePartObservation) &&
                type != typeof(ForceAtPositionObservation) && type != typeof(Vec) &&
                type != typeof(ShadowReport) && type != typeof(ShadowSample) && type != typeof(ShadowBody) &&
                type != typeof(StructuralLink) && type != typeof(StructuralConfigurableJoint) &&
                type != typeof(StructuralLimit) && type != typeof(StructuralSpring) && type != typeof(StructuralDrive) &&
                type != typeof(StructuralExperimentReport) && type != typeof(StructuralBodySample) &&
                type != typeof(StructuralAdmissionEvidence) && type != typeof(StructuralInjectionWitness) &&
                type != typeof(StructuralTrace) && type != typeof(StructuralTraceSample) && type != typeof(StructuralModeFit) &&
                type != typeof(AeroCaptureReport) && type != typeof(AeroProviderFingerprint) && type != typeof(AeroPatchProvenance) &&
                type != typeof(AeroPatchTarget) && type != typeof(AeroPatchEntry) && type != typeof(AeroCaptureSample) &&
                type != typeof(AeroCaptureContext) && type != typeof(AeroPartContext) && type != typeof(AeroBodyPublication) &&
                type != typeof(AeroDragCubeState) && type != typeof(AeroStockDragScalars) &&
                type != typeof(AeroSetDragInputs) && type != typeof(AeroSurfaceCurveDefinitions) &&
                type != typeof(AeroFloatCurveDefinition) && type != typeof(AeroCurveKey) &&
                type != typeof(StructuralVesselCensusReport) && type != typeof(StructuralVesselPartRow) &&
                type != typeof(StructuralVesselAttachmentRow) && type != typeof(StructuralVesselCandidateSummary) &&
                type != typeof(StructuralVesselCaptureContextReport) &&
                type != typeof(LiveControlIdentity) && type != typeof(LiveVesselSnapshot) &&
                type != typeof(LiveSweepStatus) &&
                type != typeof(LiveControlReply) &&
                type != typeof(NativeBoundaryMonoReport) && type != typeof(NativeBoundaryMonoRow) &&
                type != typeof(MultiscaleSceneManifest) && type != typeof(ResidentMaterialManifest) && type != typeof(ResidentTextureAsset) &&
                type != typeof(WholeCraftSceneManifest) && type != typeof(WholeCraftMaterialAsset) && type != typeof(WholeCraftDraw) && type != typeof(WholeCraftOmission) &&
                type != typeof(ResidentMaterialAsset) && type != typeof(ResidentMaterialProperty) &&
                type != typeof(ResidentInstanceAsset) && type != typeof(ResidentLighting) &&
                type != typeof(NativeBoundaryMonoTiming) && type != typeof(ManagedReconstructionObservation) && !pluginDiagnostic)
                throw new ArgumentException("Unsupported report type: " + type.FullName);
            output.Append('{'); bool firstField = true;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!firstField) output.Append(','); firstField = false;
                Write(output, field.Name); output.Append(':'); Write(output, field.GetValue(value));
            }
            output.Append('}');
        }
    }
}
