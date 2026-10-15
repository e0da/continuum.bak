using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class LiveControlAddon : MonoBehaviour
    {
        LoopbackSnapshotServer server;
        LiveControlPlane control;
        long observedFixedCallbacks;
        int mainThreadId;
        Vessel lastVessel;
        GameScenes lastScene;
        bool hasScene;
        bool quitRequested;
        string lastSweepObservation;

        public void Start()
        {
            const string prefix = "--continuum-control-port=";
            string portText = null;
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.StartsWith(prefix, StringComparison.Ordinal)) portText = argument.Substring(prefix.Length);
            if (portText == null) return;
            int port;
            if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5 ||
                !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535)
            { UnityEngine.Debug.LogError("[Continuum] Invalid or unsupported read-only control request."); return; }
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            control = new LiveControlPlane(Guid.NewGuid().ToString("D"));
            try { server = new LoopbackSnapshotServer(port); }
            catch (Exception error)
            { UnityEngine.Debug.LogError("[Continuum] Control listener failed: " + error.GetType().Name); control = null; }
        }

        public void FixedUpdate() { if (server != null) observedFixedCallbacks++; }

        public void Update()
        {
            if (server == null) return;
            if (quitRequested) { quitRequested = false; Application.Quit(0); return; }
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                throw new InvalidOperationException("Live control capture must run on Unity's main thread.");
            Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            GameScenes scene = HighLogic.LoadedScene;
            bool replaced = !ReferenceEquals(vessel, lastVessel);
            if (!hasScene || replaced || scene != lastScene)
            {
                control.Observe(scene.ToString(), vessel == null ? null : vessel.id.ToString("D"), replaced);
                lastVessel = vessel;
                lastScene = scene;
                hasScene = true;
                lastSweepObservation = null;
            }
            server.DrainOne(command => Handle(command, vessel));
            PublishSweepChange();
        }

        string Handle(string command, Vessel vessel)
        {
            long started = Stopwatch.GetTimestamp();
            LiveControlRequest request = LiveControlRequest.Parse(command);
            LiveControlReply reply = control.Execute(request, Time.frameCount, observedFixedCallbacks,
                () => Capture(vessel), StartSweep, SweepStatus, RequestQuit, RigidOperation, ClockOperation);
            reply.observerNanoseconds = (long)((Stopwatch.GetTimestamp() - started) *
                (1000000000.0 / Stopwatch.Frequency));
            if (request.operation == LiveControlOperation.SubscribeSweep && reply.status == "ok")
            {
                server.EnablePush();
                lastSweepObservation = SweepKey(reply.sweep);
            }
            return ReportJson.Encode(reply);
        }

        void PublishSweepChange()
        {
            if (!server.PushEnabled) return;
            LiveSweepStatus status = SweepStatus();
            string key = SweepKey(status);
            if (key == lastSweepObservation) return;
            if (server.PublishLatest(ReportJson.Encode(control.CreateSweepObservation(status,
                Time.frameCount, observedFixedCallbacks)), Terminal(status.state)))
                lastSweepObservation = key;
        }

        static string SweepKey(LiveSweepStatus status)
        {
            return status.state + "\n" + status.reason + "\n" + status.directory + "\n" +
                status.window + "\n" + status.windowIndex.ToString(CultureInfo.InvariantCulture);
        }

        static bool Terminal(string state)
        {
            return state == "complete" || state == "invalid" || state == "error" ||
                state == "interrupted" || state == "unavailable";
        }

        static LiveSweepStatus StartSweep()
        {
            string reason;
            bool accepted = ScalingQualification.TryStartDryBuoyancySweep(out reason);
            LiveSweepStatus status = SweepStatus();
            status.reason = accepted ? null : reason;
            return status;
        }

        static LiveSweepStatus SweepStatus()
        {
            return new LiveSweepStatus {
                state = ScalingQualification.GetDryBuoyancySweepState(),
                directory = ScalingQualification.GetDryBuoyancySweepDirectory(),
                window = ScalingQualification.GetDryBuoyancySweepWindow(),
                windowIndex = ScalingQualification.GetDryBuoyancySweepWindowIndex()
            };
        }

        string RequestQuit()
        {
            string state = ScalingQualification.GetDryBuoyancySweepState();
            if (state == "waiting-for-orbit" || state == "running") return "experiment-busy";
            quitRequested = true;
            return null;
        }

        static LiveRigidStatus RigidOperation(LiveControlOperation operation)
        {
            string reason = null;
            if (operation == LiveControlOperation.RigidEnable) reason = PlayableRigidFlightService.SetEnabled(true);
            else if (operation == LiveControlOperation.RigidDisable) reason = PlayableRigidFlightService.SetEnabled(false);
            else if (operation == LiveControlOperation.RigidRetry) reason = PlayableRigidFlightService.Retry();
            else if (operation == LiveControlOperation.RigidWarp4) reason = PlayableRigidFlightService.SetPhysicsWarp(true);
            else if (operation == LiveControlOperation.RigidNormal) reason = PlayableRigidFlightService.SetPhysicsWarp(false);
            else if (operation == LiveControlOperation.RigidSave) reason = PlayableRigidFlightService.Quicksave(false);
            else if (operation == LiveControlOperation.RigidLoad) reason = PlayableRigidFlightService.Quicksave(true);
            LiveRigidStatus status = PlayableRigidFlightService.Snapshot();
            if (operation == LiveControlOperation.RigidCapture) reason = PlayableRigidFlightService.CaptureFrames(out status.frameReceiptPath);
            status.operationError = reason;
            return status;
        }

        static LiveClockStatus ClockOperation(LiveControlOperation operation)
        {
            string reason=null,lightingPath=null;
            if(operation==LiveControlOperation.ClockEnable)reason=OwnedClockFlightService.SetEnabled(true);
            else if(operation==LiveControlOperation.ClockDisable)reason=OwnedClockFlightService.SetEnabled(false);
            else if(operation==LiveControlOperation.ClockNormal)reason=OwnedClockFlightService.SetWarp(1);
            else if(operation==LiveControlOperation.ClockWarp32)reason=OwnedClockFlightService.SetWarp(32);
            else if(operation==LiveControlOperation.ClockWarp128)reason=OwnedClockFlightService.SetWarp(128);
            else if(operation==LiveControlOperation.ClockPause)reason=OwnedClockFlightService.SetPaused(true);
            else if(operation==LiveControlOperation.ClockResume)reason=OwnedClockFlightService.SetPaused(false);
            else if(operation==LiveControlOperation.ClockSave)reason=OwnedClockFlightService.SaveCheckpoint();
            else if(operation==LiveControlOperation.ClockRestore)reason=OwnedClockFlightService.RestoreCheckpoint();
            else if(operation==LiveControlOperation.ClockLighting)
            {
                try { lightingPath=OwnedClockFlightService.CaptureLightingFacts(); }
                catch(Exception error) { reason=error.GetType().Name+":"+error.Message; }
            }
            var report=OwnedClockFlightService.Snapshot();
            if(reason!=null&&reason.Length>1024)reason=reason.Substring(0,1024);
            return new LiveClockStatus{state=report.status,operationError=reason,owned=report.owned,enabled=report.enabled,universalTime=report.publishedUniversalTime,debtSeconds=report.outstandingDemandSeconds,publications=report.publications,reportJson=ReportJson.Encode(report),lightingPath=lightingPath};
        }

        static LiveVesselSnapshot Capture(Vessel vessel)
        {
            if (vessel == null) return null;
            return new LiveVesselSnapshot {
                vesselId = vessel.id.ToString("D"), name = vessel.vesselName,
                body = vessel.mainBody == null ? null : vessel.mainBody.bodyName,
                situation = vessel.situation.ToString(), loaded = vessel.loaded, packed = vessel.packed,
                parts = vessel.parts == null ? 0 : vessel.parts.Count,
                universalTime = Planetarium.GetUniversalTime(), altitude = vessel.altitude,
                surfaceSpeed = vessel.srfSpeed, orbitalSpeed = vessel.obt_speed,
                throttle = vessel.ctrlState == null ? (double?)null : vessel.ctrlState.mainThrottle
            };
        }

        public void OnDestroy()
        {
            if (server != null) { server.Dispose(); server = null; }
        }
    }
}
