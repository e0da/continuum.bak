using System;

namespace KspContinuum
{
    public enum LiveControlOperation { Invalid, Hello, ActiveVesselSnapshot, StartSweep, SweepStatus, SubscribeSweep, QuitWhenIdle, RigidStatus, RigidEnable, RigidDisable, RigidRetry, RigidWarp4, RigidNormal, RigidSave, RigidLoad, RigidCapture }

    public sealed class LiveControlRequest
    {
        public readonly LiveControlOperation operation;
        public readonly string requestId, sessionId, protocolVersion, sweep;
        public readonly long expectedEpoch, minimumFixedCallbacks;
        public readonly string error;

        LiveControlRequest(LiveControlOperation operation, string requestId, string sessionId,
            string protocolVersion, string sweep, long expectedEpoch, long minimumFixedCallbacks, string error)
        {
            this.operation = operation; this.requestId = requestId; this.sessionId = sessionId;
            this.protocolVersion = protocolVersion; this.sweep = sweep;
            this.expectedEpoch = expectedEpoch; this.minimumFixedCallbacks = minimumFixedCallbacks;
            this.error = error;
        }

        // Wire parsing is confined here; operations beyond the stream are typed.
        public static LiveControlRequest Parse(string line)
        {
            if (line == null || line.Length > 256 || line.Length == 0)
                return new LiveControlRequest(LiveControlOperation.Invalid, null, null, null, null, 0, 0, "invalid-request");
            string[] words = line.Split(' ');
            string id = words.Length > 2 && Token(words[2], 64) ? words[2] : null;
            bool supported = words.Length > 1 &&
                (words[1] == "1.0.0" || words[1] == "1.1.0" || words[1] == "1.2.0" || words[1] == "1.3.0");
            if (words.Length < 3 || !supported || id == null)
                return new LiveControlRequest(LiveControlOperation.Invalid, id, null,
                    words.Length > 1 ? words[1] : null, null, 0, 0,
                    words.Length > 1 && !supported ? "unsupported-version" : "invalid-request");
            if (words[0] == "hello" && words.Length == 3)
                return new LiveControlRequest(LiveControlOperation.Hello, id, null, words[1], null, 0, 0, null);
            if (words[0].StartsWith("rigid-", StringComparison.Ordinal) && words.Length == 5 && words[1] == "1.3.0")
            {
                LiveControlOperation operation = words[0] == "rigid-status" ? LiveControlOperation.RigidStatus :
                    words[0] == "rigid-enable" ? LiveControlOperation.RigidEnable :
                    words[0] == "rigid-disable" ? LiveControlOperation.RigidDisable :
                    words[0] == "rigid-retry" ? LiveControlOperation.RigidRetry : words[0] == "rigid-warp4" ? LiveControlOperation.RigidWarp4 :
                    words[0] == "rigid-normal" ? LiveControlOperation.RigidNormal : words[0] == "rigid-save" ? LiveControlOperation.RigidSave :
                    words[0] == "rigid-load" ? LiveControlOperation.RigidLoad : words[0] == "rigid-capture" ? LiveControlOperation.RigidCapture : LiveControlOperation.Invalid;
                Guid session; long epoch;
                if (operation != LiveControlOperation.Invalid && Guid.TryParseExact(words[3], "D", out session) && session != Guid.Empty &&
                    long.TryParse(words[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out epoch) && epoch >= 1)
                    return new LiveControlRequest(operation, id, session.ToString("D"), words[1], null, epoch, 0, null);
            }
            if (words[0] == "snapshot" && words.Length == 6)
            {
                Guid session;
                long epoch, tick;
                if (Guid.TryParseExact(words[3], "D", out session) && session != Guid.Empty &&
                    long.TryParse(words[4], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out epoch) && epoch >= 1 &&
                    long.TryParse(words[5], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out tick))
                    return new LiveControlRequest(LiveControlOperation.ActiveVesselSnapshot, id,
                        session.ToString("D"), words[1], null, epoch, tick, null);
            }
            if ((words[0] == "sweep-start" || words[0] == "sweep-status") && words.Length == 6 &&
                words[1] == "1.1.0" && words[5] == "dry-buoyancy")
            {
                Guid session; long epoch;
                if (Guid.TryParseExact(words[3], "D", out session) && session != Guid.Empty &&
                    long.TryParse(words[4], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out epoch) && epoch >= 1)
                    return new LiveControlRequest(words[0] == "sweep-start" ? LiveControlOperation.StartSweep :
                        LiveControlOperation.SweepStatus, id, session.ToString("D"), words[1], words[5], epoch, 0, null);
            }
            if (words[0] == "sweep-subscribe" && words.Length == 6 && words[1] == "1.2.0" &&
                words[5] == "dry-buoyancy")
            {
                Guid session; long epoch;
                if (Guid.TryParseExact(words[3], "D", out session) && session != Guid.Empty &&
                    long.TryParse(words[4], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out epoch) && epoch >= 1)
                    return new LiveControlRequest(LiveControlOperation.SubscribeSweep, id,
                        session.ToString("D"), words[1], words[5], epoch, 0, null);
            }
            if (words[0] == "quit-when-idle" && words.Length == 5 && words[1] == "1.1.0")
            {
                Guid session; long epoch;
                if (Guid.TryParseExact(words[3], "D", out session) && session != Guid.Empty &&
                    long.TryParse(words[4], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out epoch) && epoch >= 1)
                    return new LiveControlRequest(LiveControlOperation.QuitWhenIdle, id, session.ToString("D"),
                        words[1], null, epoch, 0, null);
            }
            return new LiveControlRequest(LiveControlOperation.Invalid, id, null, words[1], null, 0, 0,
                words[0] == "snapshot" || words[0] == "hello" || words[0] == "sweep-start" ||
                words[0] == "sweep-status" || words[0] == "sweep-subscribe" ||
                words[0] == "quit-when-idle" || words[0] == "rigid-status" || words[0] == "rigid-enable" ||
                words[0] == "rigid-disable" || words[0] == "rigid-retry" || words[0] == "rigid-warp4" ||
                words[0] == "rigid-normal" || words[0] == "rigid-save" || words[0] == "rigid-load" || words[0] == "rigid-capture" ? "invalid-request" : "unknown-operation");
        }

        static bool Token(string value, int maximum)
        {
            if (value.Length == 0 || value.Length > maximum) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
            return true;
        }
    }

    [Serializable] public sealed class LiveControlIdentity
    {
        public string sessionId, scene, vesselId;
        public long epoch, renderFrame, observedFixedCallbacks;
    }

    [Serializable] public sealed class LiveVesselSnapshot
    {
        public string vesselId, name, body, situation;
        public bool loaded, packed;
        public int parts;
        public double universalTime, altitude, surfaceSpeed, orbitalSpeed;
        public double? throttle;
    }

    [Serializable] public sealed class LiveControlReply
    {
        public string schema = "continuum-live-control/v1";
        public string protocolVersion = "1.0.0", supportedProtocolMin = "1.0.0", supportedProtocolMax = "1.3.0";
        public string requestId;
        public string status, reason;
        public string[] capabilities = { "hello", "active-vessel-snapshot", "dry-buoyancy-sweep",
            "dry-buoyancy-sweep-push", "quit-when-idle", "playable-rigid-control" };
        public LiveControlIdentity identity;
        public LiveVesselSnapshot snapshot;
        public LiveSweepStatus sweep;
        public LiveRigidStatus rigid;
        public long observerNanoseconds;
    }

    [Serializable] public sealed class LiveRigidStatus
    {
        public string state, reason, operationError, sessionId, vesselId, receiptDirectory, frameReceiptPath, warpMode;
        public long epoch, physicsSteps, renderFrames, observationTicks, clockFrequency;
        public bool enabled, owned;
        public bool focused, runInBackground;
        public int parts, bodies;
        public int vSyncCount, targetFrameRate, width, height, antialiasing, captureFramerate;
        public double framesPerSecond, simulationRate, fixedTime, universalTime, altitude, mass, electricCharge, monoPropellant;
        public double maximumProxyPositionError, maximumProxyRotationError;
        public double atmosphericDensity, dynamicPressureKPa, surfaceSpeed;
        public float pitch, yaw, roll, x, y, z, throttle, warpRate, fixedDeltaTime, maximumDeltaTime, timeScale;
    }

    [Serializable] public sealed class LiveSweepStatus
    {
        public string name = "dry-buoyancy";
        public string state, reason, directory, window;
        public int windowIndex = -1;
    }

    public sealed class LiveControlPlane
    {
        readonly string sessionId;
        string scene, vesselId;
        long epoch;

        public LiveControlPlane(string sessionId)
        {
            Guid parsed;
            if (!Guid.TryParse(sessionId, out parsed) || parsed == Guid.Empty)
                throw new ArgumentException("A nonempty session GUID is required.", "sessionId");
            this.sessionId = parsed.ToString("D");
        }

        // The host calls this on Unity's main thread before servicing a request.
        public void Observe(string currentScene, string currentVesselId, bool forceNewEpoch = false)
        {
            if (string.IsNullOrEmpty(currentScene) || currentScene.Length > 64)
                throw new ArgumentException("Invalid scene.", "currentScene");
            if (currentVesselId != null)
            {
                Guid parsed;
                if (!Guid.TryParse(currentVesselId, out parsed) || parsed == Guid.Empty)
                    throw new ArgumentException("Invalid vessel GUID.", "currentVesselId");
                currentVesselId = parsed.ToString("D");
            }
            if (epoch == 0 || forceNewEpoch || scene != currentScene || vesselId != currentVesselId)
            {
                epoch = checked(epoch + 1);
                scene = currentScene;
                vesselId = currentVesselId;
            }
        }

        public LiveControlReply Execute(LiveControlRequest request, long renderFrame, long observedFixedCallbacks,
            Func<LiveVesselSnapshot> capture, Func<LiveSweepStatus> startSweep = null,
            Func<LiveSweepStatus> sweepStatus = null, Func<string> requestQuit = null,
            Func<LiveControlOperation, LiveRigidStatus> rigidOperation = null)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (epoch == 0 || renderFrame < 0 || observedFixedCallbacks < 0)
                throw new InvalidOperationException("Observe the current game state before handling a request.");
            var identity = new LiveControlIdentity { sessionId = sessionId, scene = scene, vesselId = vesselId,
                epoch = epoch, renderFrame = renderFrame, observedFixedCallbacks = observedFixedCallbacks };
            var reply = new LiveControlReply { identity = identity, requestId = request.requestId,
                protocolVersion = request.protocolVersion == "1.3.0" ? "1.3.0" : request.protocolVersion == "1.2.0" ? "1.2.0" :
                    request.protocolVersion == "1.1.0" ? "1.1.0" : "1.0.0" };
            if (request.operation == LiveControlOperation.Invalid)
            { reply.status = "rejected"; reply.reason = request.error; return reply; }
            if (request.operation == LiveControlOperation.Hello) { reply.status = "ok"; return reply; }
            if (request.operation >= LiveControlOperation.RigidStatus && request.operation <= LiveControlOperation.RigidCapture)
            {
                if (request.sessionId != sessionId || request.expectedEpoch != epoch)
                { reply.status = "rejected"; reply.reason = "stale-identity"; return reply; }
                if (rigidOperation == null) { reply.status = "unavailable"; reply.reason = "rigid-service-unavailable"; return reply; }
                LiveRigidStatus status = rigidOperation(request.operation);
                if (status == null || !Bounded(status.state, 64) || !OptionalBounded(status.reason, 1024) || !OptionalBounded(status.operationError, 1024) ||
                    !OptionalBounded(status.receiptDirectory, 4096) || !OptionalBounded(status.frameReceiptPath, 4096) || status.physicsSteps < 0 || status.renderFrames < 0 ||
                    status.parts < 0 || status.bodies < 0 || !Finite(status.framesPerSecond) || !Finite(status.simulationRate) ||
                    !Finite(status.fixedTime) || !Finite(status.universalTime) || !Finite(status.altitude) || !Finite(status.mass) ||
                    !Finite(status.electricCharge) || !Finite(status.monoPropellant) || !Finite(status.maximumProxyPositionError) ||
                    !Finite(status.maximumProxyRotationError) || !Finite(status.atmosphericDensity) || !Finite(status.dynamicPressureKPa) || !Finite(status.surfaceSpeed) || !Finite(status.pitch) || !Finite(status.yaw) || !Finite(status.roll) ||
                    !Finite(status.x) || !Finite(status.y) || !Finite(status.z) || !Finite(status.throttle) ||
                    !Finite(status.warpRate) || !Finite(status.fixedDeltaTime) || !Finite(status.maximumDeltaTime) || !Finite(status.timeScale))
                { reply.status = "rejected"; reply.reason = "invalid-rigid-status"; return reply; }
                reply.rigid = status; reply.status = status.operationError != null ? "rejected" : status.state == "unavailable" ? "unavailable" : "ok";
                reply.reason = status.operationError ?? (status.state == "unavailable" ? status.reason : null); return reply;
            }
            if (request.operation == LiveControlOperation.StartSweep || request.operation == LiveControlOperation.SweepStatus ||
                request.operation == LiveControlOperation.SubscribeSweep)
            {
                if (request.sessionId != sessionId || request.expectedEpoch != epoch)
                { reply.status = "rejected"; reply.reason = "stale-identity"; return reply; }
                Func<LiveSweepStatus> operation = request.operation == LiveControlOperation.StartSweep ? startSweep : sweepStatus;
                if (operation == null) { reply.status = "unavailable"; reply.reason = "sweep-unavailable"; return reply; }
                reply.sweep = operation();
                if (!ValidSweep(reply.sweep, request.sweep))
                { reply.sweep = null; reply.status = "rejected"; reply.reason = "invalid-sweep-status"; return reply; }
                reply.status = "ok"; return reply;
            }
            if (request.operation == LiveControlOperation.QuitWhenIdle)
            {
                if (request.sessionId != sessionId || request.expectedEpoch != epoch)
                { reply.status = "rejected"; reply.reason = "stale-identity"; return reply; }
                if (requestQuit == null) { reply.status = "unavailable"; reply.reason = "quit-unavailable"; return reply; }
                string reason = requestQuit();
                reply.status = reason == null ? "ok" : "rejected"; reply.reason = reason; return reply;
            }
            if (request.operation != LiveControlOperation.ActiveVesselSnapshot)
            { reply.status = "rejected"; reply.reason = "unknown-operation"; return reply; }
            if (request.sessionId != sessionId || request.expectedEpoch != epoch)
            { reply.status = "rejected"; reply.reason = "stale-identity"; return reply; }
            if (observedFixedCallbacks < request.minimumFixedCallbacks)
            { reply.status = "rejected"; reply.reason = "tick-not-yet-observed"; return reply; }
            if (vesselId == null) { reply.status = "unavailable"; reply.reason = "no-active-vessel"; return reply; }
            if (capture == null) throw new ArgumentNullException("capture");
            LiveVesselSnapshot snapshot = capture();
            if (snapshot == null || snapshot.vesselId != vesselId || snapshot.parts < 0 ||
                !Bounded(snapshot.name, 128) || !Bounded(snapshot.body, 128) ||
                !Bounded(snapshot.situation, 64) ||
                !Finite(snapshot.universalTime) || !Finite(snapshot.altitude) ||
                !Finite(snapshot.surfaceSpeed) || !Finite(snapshot.orbitalSpeed) ||
                (snapshot.throttle.HasValue && !Finite(snapshot.throttle.Value)))
            { reply.status = "rejected"; reply.reason = "capture-changed-or-invalid"; return reply; }
            reply.status = "ok";
            reply.snapshot = snapshot;
            return reply;
        }

        public LiveControlReply CreateSweepObservation(LiveSweepStatus sweep, long renderFrame,
            long observedFixedCallbacks)
        {
            if (epoch == 0 || renderFrame < 0 || observedFixedCallbacks < 0)
                throw new InvalidOperationException("Observe the current game state before publishing an observation.");
            if (!ValidSweep(sweep, "dry-buoyancy"))
                throw new ArgumentException("Invalid sweep status.", "sweep");
            return new LiveControlReply {
                protocolVersion = "1.2.0", status = "observation", sweep = sweep,
                identity = new LiveControlIdentity { sessionId = sessionId, scene = scene, vesselId = vesselId,
                    epoch = epoch, renderFrame = renderFrame, observedFixedCallbacks = observedFixedCallbacks }
            };
        }

        static bool ValidSweep(LiveSweepStatus sweep, string name)
        {
            return sweep != null && sweep.name == name && Bounded(sweep.state, 64) &&
                OptionalBounded(sweep.reason, 256) && OptionalBounded(sweep.directory, 1024) &&
                OptionalBounded(sweep.window, 64) && sweep.windowIndex >= -1 && sweep.windowIndex <= 5;
        }

        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static bool Bounded(string value, int maximum) { return value != null && value.Length <= maximum; }
        static bool OptionalBounded(string value, int maximum) { return value == null || value.Length <= maximum; }
    }
}
