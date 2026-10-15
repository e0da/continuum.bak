using System;
using System.Diagnostics;
using UnityEngine;

namespace KspContinuum
{
    public sealed class ResearchPanelView : MonoBehaviour
    {
        internal Panel Owner;
        public void OnGUI()
        {
            long start = Stopwatch.GetTimestamp();
            EventType kind = Event.current == null ? EventType.Ignore : Event.current.type;
            bool failed = false;
            try { if (Owner != null) Owner.RenderResearchView(); }
            catch { failed = true; throw; }
            finally { ResearchPanelPresentation.Observe(this, kind, start, Stopwatch.GetTimestamp(), failed); }
        }
    }

    internal static class ResearchPanelPresentation
    {
        static Panel owner;
        static ResearchPanelView view;
        static string mode = "visible";
        static bool initialized, diagnostic, capturing;
        static ResearchPanelReport capture;

        internal static string Mode { get { return mode; } }
        internal static bool ComponentPresent { get { return owner != null && owner.GetComponent<ResearchPanelView>() != null; } }
        internal static bool Visible { get { return ComponentPresent && view != null && view.Owner == owner && view.enabled && owner.enabled && view.gameObject.activeInHierarchy; } }
        static void Initialize()
        {
            if (initialized) return;
            bool found = false;
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                const string prefix = "--continuum-research-panel=";
                if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string value = argument.Substring(prefix.Length);
                if (found || (value != "visible" && value != "hidden")) throw new ArgumentException("Research panel mode must be a single visible|hidden selection.");
                mode = value; found = true;
            }
            diagnostic = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-flight-diagnostic") >= 0;
            initialized = true;
        }
        internal static void FreezeDiagnosticMode() { Initialize(); diagnostic = true; }
        internal static void Attach(Panel service)
        {
            Initialize();
            if (owner != null && owner != service) throw new InvalidOperationException("Multiple active research panel services.");
            owner = service;
            if (mode == "visible" && view == null) { view = service.gameObject.AddComponent<ResearchPanelView>(); view.Owner = service; }
        }
        internal static void Detach(Panel service)
        {
            if (owner != service) return;
            if (capturing && capture != null) capture.errors++;
            RemoveView(); owner = null;
        }
        static void RemoveView()
        {
            if (view == null) return;
            view.enabled = false; view.Owner = null; UnityEngine.Object.Destroy(view); view = null;
        }
        internal static void PollToggle(Panel service)
        {
            if (owner != service || !(Input.GetKeyDown(KeyCode.BackQuote) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))) return;
            if (diagnostic || capturing) { if (capturing && capture != null) capture.blockedModeChangeAttempts++; return; }
            mode = mode == "visible" ? "hidden" : "visible";
            if (mode == "hidden") RemoveView();
            else { view = service.gameObject.AddComponent<ResearchPanelView>(); view.Owner = service; }
        }
        internal static ResearchPanelReport BeginCapture(long start)
        {
            Initialize();
            if (capturing) throw new InvalidOperationException("Research panel capture already active.");
            capture = new ResearchPanelReport { mode = mode, startVisible = Visible, startComponentPresent = ComponentPresent,
                captureStartFrame = Time.frameCount, captureStartClockTimestamp = start, clockFrequency = Stopwatch.Frequency };
            if (owner == null || (mode == "visible" ? !Visible : ComponentPresent)) capture.errors++;
            capturing = true; return capture;
        }
        internal static void ObserveService(Panel service, bool fixedUpdate)
        {
            if (!capturing || capture == null) return;
            if (service != owner) { capture.errors++; return; }
            if (fixedUpdate) capture.serviceFixedCalls++; else capture.serviceUpdateCalls++;
        }
        internal static void Observe(ResearchPanelView source, EventType kind, long start, long end, bool failed)
        {
            if (!capturing || capture == null || start < capture.captureStartClockTimestamp) return;
            if (source != view || failed || mode != capture.mode) capture.errors++;
            long ticks = end - start; capture.guiCalls++; capture.guiTicks += ticks;
            if (kind == EventType.Layout) { capture.layoutCalls++; capture.layoutTicks += ticks; }
            else if (kind == EventType.Repaint) { capture.repaintCalls++; capture.repaintTicks += ticks; }
            else { capture.otherCalls++; capture.otherTicks += ticks; }
        }
        internal static void EndCapture(ResearchPanelReport report, long end)
        {
            if (report == null || report.captureEndClockTimestamp != 0) return;
            capturing = false;
            report.captureEndClockTimestamp = end; report.captureEndFrame = Time.frameCount;
            report.endVisible = Visible; report.endComponentPresent = ComponentPresent;
            if (mode != report.mode) report.modeChanges++;
            bool expected = report.mode == "visible";
            bool valid = report.errors == 0 && report.modeChanges == 0 && owner != null &&
                report.startVisible == expected && report.endVisible == expected &&
                report.startComponentPresent == expected && report.endComponentPresent == expected &&
                (expected ? report.layoutCalls > 0 && report.repaintCalls > 0 : report.guiCalls == 0 && report.guiTicks == 0);
            report.status = valid ? "complete" : "invalid";
        }
    }
}
