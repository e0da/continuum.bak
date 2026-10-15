using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class OwnedClockFlightPanel : MonoBehaviour
    {
        const string LockName = "ContinuumOwnedClockPanel";
        bool requested, locked, simpleKeys;
        float nextPoll;
        Rect bounds;
        OwnedClockFlightService.OperatorState state;
        string status = "Initializing", time = "UT —", warp = "Warp —", debt = "Debt —", error;
        string operationError,lightingOutput,cameraOutput;
        GUIStyle label, warning;

        public void Awake()
        {
            requested = OwnedClockFlightService.Requested;
            if (!requested) { Destroy(this); return; }
            simpleKeys = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-owned-clock-simple-keys") >= 0;
            bounds = new Rect(Math.Max(0, Screen.width - 325), 72, 310, 392);
            Poll();
        }
        public void Update()
        {
            if (!requested) return;
            if (Time.realtimeSinceStartup >= nextPoll) Poll();
            bool hover = HighLogic.LoadedScene == GameScenes.FLIGHT &&
                bounds.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
            if (hover && !locked)
            {
                InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS | ControlTypes.CAMERAMODES, LockName);
                locked = true;
            }
            else if (!hover) ReleaseLock();
        }
        void Poll()
        {
            state = OwnedClockFlightService.GetOperatorState();
            status = "Independent simulation: " + (state.Status ?? "unknown");
            time = "UT " + state.UniversalTime.ToString("F3", CultureInfo.InvariantCulture) + " s";
            warp = "Warp " + state.Warp.ToString("G6", CultureInfo.InvariantCulture) + "×" + (state.Paused ? " · paused" : "");
            debt = "Simulation debt " + state.DebtSeconds.ToString("F4", CultureInfo.InvariantCulture) + " s";
            string detail = operationError ?? state.Error;
            error = string.IsNullOrEmpty(detail) ? null : (detail.Length > 600 ? detail.Substring(0, 600) + "…" : detail);
            nextPoll = Time.realtimeSinceStartup + .05f;
        }
        void Operate(Func<string> action)
        {
            try { operationError = action(); }
            catch (Exception exception) { operationError = exception.Message; }
            Poll();
        }
        public void OnGUI()
        {
            if (!requested || HighLogic.LoadedScene != GameScenes.FLIGHT) return;
            var key = Event.current;
            OwnedClockFlightService.ObserveOperatorEvent(key, state.Available);
            KeyCode operatorKey = key.keyCode;
            bool chord = key.control && key.alt;
            if (simpleKeys && key.modifiers == EventModifiers.None) chord = true;
            if (state.Available && key.type == EventType.KeyDown && chord)
            {
                Func<string> action = null;
                if (operatorKey == KeyCode.Keypad0 && (state.Owned || state.CanEnable)) action = () => OwnedClockFlightService.SetEnabled(!state.Owned);
                else if (operatorKey == KeyCode.Keypad1 && state.Owned) action = () => OwnedClockFlightService.SetPaused(!state.Paused);
                else if (operatorKey == KeyCode.Keypad2) action = InspectLighting;
                else if (operatorKey == KeyCode.Keypad3 && state.Owned) action = OwnedClockFlightService.SaveCheckpoint;
                else if (operatorKey == KeyCode.Keypad4 && state.Owned && state.HasCheckpoint) action = OwnedClockFlightService.RestoreCheckpoint;
                else if (operatorKey == KeyCode.Keypad5) action = Reframe;
                if (action != null) { key.Use(); Operate(action); }
            }
            GUISkin old = GUI.skin;
            bool oldEnabled = GUI.enabled;
            try
            {
                GUI.enabled = state.Available;
                if (HighLogic.Skin != null) GUI.skin = HighLogic.Skin;
                if (label == null)
                {
                    label = new GUIStyle(GUI.skin.label) { wordWrap = true };
                    warning = new GUIStyle(label); warning.normal.textColor = new Color(1, .78f, .35f);
                }
                bounds.x = Math.Max(0, Screen.width - bounds.width - 15);
                bounds = GUILayout.Window(GetInstanceID(), bounds, DrawWindow, "Continuum owned clock");
            }
            finally { GUI.skin = old; GUI.enabled = oldEnabled; }
        }
        void DrawWindow(int id)
        {
            GUILayout.Label("Research model: uncrewed vacuum, no contact.", warning);
            GUILayout.Label(status, label);
            GUILayout.Label(time + "\n" + warp + "\n" + debt, label);
            bool oldEnabled = GUI.enabled;
            try
            {
                GUI.enabled = oldEnabled && state.Available && state.CanEnable;
                if (GUILayout.Button("Enable independent simulation")) Operate(() => OwnedClockFlightService.SetEnabled(true));
                GUI.enabled = oldEnabled && state.Owned;
                if (GUILayout.Button(state.Paused ? "Resume simulation" : "Pause simulation")) Operate(() => OwnedClockFlightService.SetPaused(!state.Paused));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Normal")) Operate(() => OwnedClockFlightService.SetWarp(1));
                if (GUILayout.Button("4×")) Operate(() => OwnedClockFlightService.SetWarp(4));
                if (GUILayout.Button("32×")) Operate(() => OwnedClockFlightService.SetWarp(32));
                if (GUILayout.Button("128×")) Operate(() => OwnedClockFlightService.SetWarp(128));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Save owned state")) Operate(OwnedClockFlightService.SaveCheckpoint);
                GUI.enabled = oldEnabled && state.Owned && state.HasCheckpoint;
                if (GUILayout.Button("Restore saved")) Operate(OwnedClockFlightService.RestoreCheckpoint);
                GUILayout.EndHorizontal();
                GUI.enabled = oldEnabled && state.Owned;
                if (GUILayout.Button("Return to stock simulation")) Operate(() => OwnedClockFlightService.SetEnabled(false));
                GUI.enabled=oldEnabled&&state.Available;
                if(GUILayout.Button("Inspect lighting"))Operate(InspectLighting);
                if(GUILayout.Button("Reframe toward sunlight"))Operate(Reframe);
            }
            finally { GUI.enabled = oldEnabled; }
            if(lightingOutput!=null)GUILayout.Label("Lighting facts: "+lightingOutput,label);
            if(cameraOutput!=null)GUILayout.Label("Camera request: "+cameraOutput,label);
            if (error != null) GUILayout.Label("Last operation error: " + error, warning);
            GUILayout.Label("Save and restore leave simulation paused.", label);
            GUILayout.Label((simpleKeys ? "Lab keypad: " : "Ctrl+Alt+keypad: ") + "0 enable/return, 1 pause/resume, 2 lighting, 3 save, 4 restore, 5 reframe.", label);
        }
        string InspectLighting() { lightingOutput=Path.GetFileName(OwnedClockFlightService.CaptureLightingFacts());return null; }
        string Reframe() { cameraOutput=Path.GetFileName(OwnedClockFlightService.ReframeSunward());return null; }
        void ReleaseLock()
        {
            if (!locked) return;
            InputLockManager.RemoveControlLock(LockName); locked = false;
        }
        public void OnDisable() { ReleaseLock(); }
        public void OnDestroy() { ReleaseLock(); }
    }
}
