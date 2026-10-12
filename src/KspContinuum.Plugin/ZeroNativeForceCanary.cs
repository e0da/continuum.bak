using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KspContinuum
{
    public static class ZeroNativeForceCanary
    {
        const float Dt = .02f;
        const double LinearTolerance = 1e-6, AngularToleranceDegrees = 1e-5;
        static bool running, logOverflow;
        static ReportData pending;
        static AsyncOperation cleanup;
        static List<string> messages;
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-zero-native-force-canary/v1", status = "running", cleanupStatus = "not-created", detail;
            public string runId, unity, unityVersion, graphicsDevice, sceneName;
            public string scope = "Owned local Physics3D scene, paired equal no-collider rigidbodies; exact mode0 +/-zero omission compared with original calls. One .02s local simulation, no main-world/global physics writes. Log messages during zero calls and original/forwarding exception outcomes are compared. This does not qualify arbitrary game callbacks, arbitrary positions, contacts/joints, long-horizon physics or a whole-flight optimization.";
            public int processId, cases, failedCases, destroyedObjects;
            public long clockFrequency = Stopwatch.Frequency, startedTicks, endedTicks;
            public bool isolatedScene, defaultAutoSimulationBefore, defaultAutoSimulationAfter, nativeCasesPassed, cleanupComplete, cleanupPending, passed;
            public double positionToleranceMeters = LinearTolerance, velocityToleranceMetersPerSecond = LinearTolerance, angularVelocityToleranceRadiansPerSecond = LinearTolerance, rotationToleranceDegrees = AngularToleranceDegrees;
            public CaseData[] rows; public ReceiverData[] receivers;
        }
        [Serializable] public sealed class CaseData
        {
            public string state, command, pendingCommands, zeroEncoding, originalException, candidateException;
            public int mode, originalInstanceId, candidateInstanceId; public uint[] inputBits;
            public bool candidateOmitted, setupPassed, statePassed, logPassed, passed, pendingWakePassed, independentOracleRequired, independentOraclePassed;
            public double[] expectedDeltaVelocity, expectedDeltaAngularVelocity; public double originalLinearOracleError, candidateLinearOracleError, originalAngularOracleError, candidateAngularOracleError;
            public float[] input;
            public BodyState originalBefore, candidateBefore, originalAfterPending, candidateAfterPending, originalAfterZero, candidateAfterZero, originalAfterSimulate, candidateAfterSimulate;
            public string[] originalZeroMessages, candidateZeroMessages;
            public double maximumPositionErrorMeters, maximumVelocityErrorMetersPerSecond, maximumAngularVelocityErrorRadiansPerSecond, maximumRotationErrorDegrees;
        }
        [Serializable] public sealed class BodyState
        {
            public bool sleeping, isKinematic, activeSelf, activeInHierarchy, finite;
            public float[] position, rotation, velocity, angularVelocity;
        }
        [Serializable] public sealed class ReceiverData
        { public string receiver, command, originalException, candidateException; public bool candidateOmitted, passed; public string[] originalMessages, candidateMessages; }
        public static ReportData Run()
        {
            if (running || pending != null) throw new InvalidOperationException("Previous canary owner/scene cleanup is not completed.");
            running = true; logOverflow = false;
            var report = new ReportData { processId = Process.GetCurrentProcess().Id, unity = Application.unityVersion, unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceVersion, defaultAutoSimulationBefore = Physics.autoSimulation, startedTicks = Stopwatch.GetTimestamp() };
            Scene scene = default(Scene); var owned = new List<GameObject>(); var rows = new List<CaseData>(); var receivers = new List<ReceiverData>();
            Application.logMessageReceived += CaptureLog;
            try
            {
                scene = SceneManager.CreateScene("Continuum-Zero-Canary-" + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                report.sceneName = scene.name;
                PhysicsScene physics = scene.GetPhysicsScene(); report.isolatedScene = physics.IsValid() && physics != Physics.defaultPhysicsScene;
                if (!report.isolatedScene) throw new InvalidOperationException("Canary Physics3D scene is not isolated.");
                string[] states = { "dynamic-awake", "dynamic-sleeping", "kinematic", "inactive" };
                string[] commands = { "force", "torque", "force-at-position" };
                string[] pendings = { "none", "linear", "angular", "linear-and-angular" };
                float minusZero = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);
                Vector3[] zeros = { Vector3.zero, new Vector3(minusZero, minusZero, minusZero), new Vector3(0, minusZero, 0) };
                string[] names = { "positive-zero", "negative-zero", "mixed-zero" };
                foreach (string state in states) foreach (string command in commands) foreach (string prior in pendings) for (int z = 0; z < zeros.Length; z++)
                    rows.Add(Compare(scene, physics, owned, state, command, prior, zeros[z], names[z], ForceMode.Force, report));
                // Native forwarding controls use one dynamic body pair and the same comparison oracle.
                float tiny = BitConverter.ToSingle(BitConverter.GetBytes(1), 0);
                foreach (string command in commands)
                {
                    rows.Add(Compare(scene, physics, owned, "dynamic-awake", command, "linear-and-angular", new Vector3(tiny, 0, 0), "minimum-positive-subnormal", ForceMode.Force, report));
                    rows.Add(Compare(scene, physics, owned, "dynamic-awake", command, "linear-and-angular", new Vector3(.001f, -.002f, .003f), "nonzero-forwarding", ForceMode.Force, report));
                    foreach (ForceMode mode in new[] { ForceMode.Acceleration, ForceMode.Impulse, ForceMode.VelocityChange })
                        rows.Add(Compare(scene, physics, owned, "dynamic-awake", command, "linear-and-angular", Vector3.zero, "nonzero-mode-forwarding", mode, report));
                    receivers.Add(Receiver(null, "managed-null", command));
                    Rigidbody destroyed = NewBody(scene, owned, "dynamic-awake"); UnityEngine.Object.DestroyImmediate(destroyed.gameObject); report.destroyedObjects++;
                    receivers.Add(Receiver(destroyed, "destroyed-native-object", command));
                }
                report.rows = rows.ToArray(); report.receivers = receivers.ToArray(); report.cases = rows.Count;
                foreach (CaseData row in rows) if (!row.passed) report.failedCases++;
                foreach (ReceiverData row in receivers) if (!row.passed) report.failedCases++;
                if (logOverflow) { report.failedCases++; report.detail = "Native log collector bound exceeded."; }
                report.nativeCasesPassed = report.failedCases == 0;
            }
            catch (Exception error) { report.detail = error.ToString(); report.nativeCasesPassed = false; report.rows = rows.ToArray(); report.receivers = receivers.ToArray(); report.cases = rows.Count; }
            finally
            {
                messages = null; Application.logMessageReceived -= CaptureLog;
                try
                {
                    foreach (GameObject item in owned) if (item != null) { UnityEngine.Object.DestroyImmediate(item); report.destroyedObjects++; }
                    if (scene.IsValid() && scene.isLoaded)
                    {
                        cleanup = SceneManager.UnloadSceneAsync(scene);
                        if (cleanup == null) throw new InvalidOperationException("Owned scene unload request was not accepted.");
                        report.cleanupStatus = "destroyed-owned-objects-unload-requested"; report.cleanupPending = true; pending = report;
                    }
                    else { report.cleanupComplete = true; report.cleanupStatus = "no-owned-scene"; }
                }
                catch (Exception error) { report.detail = (report.detail ?? "") + " Cleanup: " + error; report.cleanupStatus = "cleanup-error"; report.nativeCasesPassed = false; }
                report.defaultAutoSimulationAfter = Physics.autoSimulation;
                if (report.defaultAutoSimulationAfter != report.defaultAutoSimulationBefore) { report.nativeCasesPassed = false; report.detail = (report.detail ?? "") + " Default autoSimulation changed externally during canary."; }
                report.endedTicks = Stopwatch.GetTimestamp(); running = false;
                report.passed = report.cleanupComplete && report.nativeCasesPassed; report.status = report.passed ? "passed" : report.cleanupStatus == "cleanup-error" || report.cleanupComplete ? "failed" : "awaiting-owned-scene-cleanup";
            }
            return report;
        }
        public static bool ObserveCleanup(ReportData report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (report.cleanupComplete) return true;
            if (!ReferenceEquals(report, pending) || cleanup == null) { report.status = "failed"; report.cleanupStatus = "cleanup-error"; return false; }
            if (!cleanup.isDone) return false;
            report.cleanupComplete = true; report.cleanupPending = false; report.cleanupStatus = "scene-unloaded";
            report.passed = report.nativeCasesPassed; report.status = report.passed ? "passed" : "failed"; cleanup = null; pending = null; return true;
        }
        static Rigidbody NewBody(Scene scene, List<GameObject> owned, string state)
        {
            var obj = new GameObject("Continuum-Zero-Canary-Body"); owned.Add(obj); SceneManager.MoveGameObjectToScene(obj, scene);
            obj.transform.SetPositionAndRotation(new Vector3(1, 2, 3), Quaternion.identity);
            var body = obj.AddComponent<Rigidbody>(); body.useGravity = false; body.detectCollisions = false; body.drag = 0; body.angularDrag = 0;
            body.mass = 2; body.inertiaTensor = new Vector3(2, 3, 4); body.inertiaTensorRotation = Quaternion.identity; body.centerOfMass = Vector3.zero;
            body.maxAngularVelocity = 100; body.sleepThreshold = .005f; body.interpolation = RigidbodyInterpolation.None;
            if (state == "dynamic-awake") { body.velocity = new Vector3(.7f, -.2f, .3f); body.angularVelocity = new Vector3(.13f, -.17f, .19f); body.WakeUp(); }
            else if (state == "dynamic-sleeping") { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.Sleep(); }
            else if (state == "kinematic") body.isKinematic = true;
            else if (state == "inactive") obj.SetActive(false);
            return body;
        }
        static void Pending(Rigidbody body, string prior)
        {
            if (prior == "linear" || prior == "linear-and-angular") body.AddForce(new Vector3(2, -1, 3), ForceMode.Force);
            if (prior == "angular" || prior == "linear-and-angular") body.AddTorque(new Vector3(.4f, -.3f, .2f), ForceMode.Force);
        }
        static void Original(Rigidbody body, string command, Vector3 vector, ForceMode mode)
        {
            if (mode == ForceMode.Force)
            {
                if (command == "force") body.AddForce(vector);
                else if (command == "torque") body.AddTorque(vector);
                else body.AddForceAtPosition(vector, new Vector3(1.5f, 2.2f, 2.7f));
            }
            else if (command == "force") body.AddForce(vector, mode);
            else if (command == "torque") body.AddTorque(vector, mode);
            else body.AddForceAtPosition(vector, new Vector3(1.5f, 2.2f, 2.7f), mode);
        }
        static bool Candidate(Rigidbody body, string command, Vector3 vector, ForceMode mode)
        {
            if (body != null && mode == ForceMode.Force && vector.x == 0f && vector.y == 0f && vector.z == 0f) return true;
            Original(body, command, vector, mode); return false;
        }
        static CaseData Compare(Scene scene, PhysicsScene physics, List<GameObject> owned, string state, string command, string prior, Vector3 vector, string encoding, ForceMode mode, ReportData report)
        {
            Rigidbody original = NewBody(scene, owned, state), candidate = NewBody(scene, owned, state);
            var row = new CaseData { state = state, command = command, pendingCommands = prior, zeroEncoding = encoding, mode = (int)mode,
                input = V(vector), inputBits = new[] { Bits(vector.x), Bits(vector.y), Bits(vector.z) }, originalInstanceId = original.GetInstanceID(), candidateInstanceId = candidate.GetInstanceID() };
            try
            {
                row.originalBefore = Snapshot(original); row.candidateBefore = Snapshot(candidate);
                row.setupPassed = row.originalBefore.isKinematic == (state == "kinematic") && row.candidateBefore.isKinematic == (state == "kinematic")
                    && row.originalBefore.activeInHierarchy == (state != "inactive") && row.candidateBefore.activeInHierarchy == (state != "inactive")
                    && (state != "dynamic-sleeping" || row.originalBefore.sleeping && row.candidateBefore.sleeping)
                    && (state != "dynamic-awake" || !row.originalBefore.sleeping && !row.candidateBefore.sleeping);
                Pending(original, prior); Pending(candidate, prior);
                row.originalAfterPending = Snapshot(original); row.candidateAfterPending = Snapshot(candidate);
                row.pendingWakePassed = state != "dynamic-sleeping" || prior == "none" || !row.originalAfterPending.sleeping && !row.candidateAfterPending.sleeping;
                row.originalZeroMessages = Invoke(() => Original(original, command, vector, mode), out row.originalException);
                row.candidateZeroMessages = Invoke(() => row.candidateOmitted = Candidate(candidate, command, vector, mode), out row.candidateException);
                row.originalAfterZero = Snapshot(original); row.candidateAfterZero = Snapshot(candidate);
                physics.Simulate(Dt);
                row.originalAfterSimulate = Snapshot(original); row.candidateAfterSimulate = Snapshot(candidate);
                row.statePassed = Equal(row.originalBefore, row.candidateBefore, row) && Equal(row.originalAfterPending, row.candidateAfterPending, row)
                    && Equal(row.originalAfterZero, row.candidateAfterZero, row) && Equal(row.originalAfterSimulate, row.candidateAfterSimulate, row);
                row.logPassed = SameMessages(row.originalZeroMessages, row.candidateZeroMessages);
                row.independentOracleRequired = (state == "dynamic-awake" || state == "dynamic-sleeping") && mode == ForceMode.Force && vector.x == 0f && vector.y == 0f && vector.z == 0f;
                row.independentOraclePassed = !row.independentOracleRequired;
                if (row.independentOracleRequired)
                {
                    bool linear = prior == "linear" || prior == "linear-and-angular", angular = prior == "angular" || prior == "linear-and-angular";
                    row.expectedDeltaVelocity = new[] { linear ? 2.0 / 2 * Dt : 0, linear ? -1.0 / 2 * Dt : 0, linear ? 3.0 / 2 * Dt : 0 };
                    row.expectedDeltaAngularVelocity = new[] { angular ? .4 / 2 * Dt : 0, angular ? -.3 / 3 * Dt : 0, angular ? .2 / 4 * Dt : 0 };
                    row.originalLinearOracleError = DeltaError(row.originalBefore.velocity, row.originalAfterSimulate.velocity, row.expectedDeltaVelocity);
                    row.candidateLinearOracleError = DeltaError(row.candidateBefore.velocity, row.candidateAfterSimulate.velocity, row.expectedDeltaVelocity);
                    row.originalAngularOracleError = DeltaError(row.originalBefore.angularVelocity, row.originalAfterSimulate.angularVelocity, row.expectedDeltaAngularVelocity);
                    row.candidateAngularOracleError = DeltaError(row.candidateBefore.angularVelocity, row.candidateAfterSimulate.angularVelocity, row.expectedDeltaAngularVelocity);
                    row.independentOraclePassed = row.originalLinearOracleError <= LinearTolerance && row.candidateLinearOracleError <= LinearTolerance
                        && row.originalAngularOracleError <= LinearTolerance && row.candidateAngularOracleError <= LinearTolerance;
                }
                row.passed = row.setupPassed && row.pendingWakePassed && row.independentOraclePassed && row.statePassed && row.logPassed && row.originalException == row.candidateException
                    && row.candidateOmitted == (mode == ForceMode.Force && vector.x == 0f && vector.y == 0f && vector.z == 0f)
                    && (!row.candidateOmitted || row.originalException == null);
            }
            finally
            {
                if (original != null) { UnityEngine.Object.DestroyImmediate(original.gameObject); report.destroyedObjects++; }
                if (candidate != null) { UnityEngine.Object.DestroyImmediate(candidate.gameObject); report.destroyedObjects++; }
            }
            return row;
        }
        static ReceiverData Receiver(Rigidbody body, string receiver, string command)
        {
            var row = new ReceiverData { receiver = receiver, command = command };
            row.originalMessages = Invoke(() => Original(body, command, Vector3.zero, ForceMode.Force), out row.originalException);
            row.candidateMessages = Invoke(() => row.candidateOmitted = Candidate(body, command, Vector3.zero, ForceMode.Force), out row.candidateException);
            row.passed = !row.candidateOmitted && row.originalException == row.candidateException && SameMessages(row.originalMessages, row.candidateMessages); return row;
        }
        static string[] Invoke(Action action, out string error)
        {
            error = null; var list = new List<string>(); messages = list;
            try { action(); } catch (Exception exception) { error = exception.GetType().FullName; }
            finally { messages = null; } return list.ToArray();
        }
        static void CaptureLog(string message, string stack, LogType type)
        { if (messages != null) { if (messages.Count < 64) messages.Add(type + ": " + message); else logOverflow = true; } }
        static bool SameMessages(string[] a, string[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        static uint Bits(float value) { return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0); }
        static double DeltaError(float[] before, float[] after, double[] expected)
        { double x = (double)after[0] - before[0] - expected[0], y = (double)after[1] - before[1] - expected[1], z = (double)after[2] - before[2] - expected[2]; return Math.Sqrt(x * x + y * y + z * z); }
        static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        static BodyState Snapshot(Rigidbody body)
        {
            Vector3 p = body.position, v = body.velocity, a = body.angularVelocity; Quaternion r = body.rotation;
            return new BodyState { sleeping = body.IsSleeping(), isKinematic = body.isKinematic, activeSelf = body.gameObject.activeSelf, activeInHierarchy = body.gameObject.activeInHierarchy,
                position = V(p), velocity = V(v), angularVelocity = V(a), rotation = new[] { r.x, r.y, r.z, r.w }, finite = Finite(p) && Finite(v) && Finite(a) && Finite(r.x) && Finite(r.y) && Finite(r.z) && Finite(r.w) };
        }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static bool Finite(Vector3 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z); }
        static double Distance(float[] a, float[] b)
        { double x = (double)a[0] - b[0], y = (double)a[1] - b[1], z = (double)a[2] - b[2]; return Math.Sqrt(x * x + y * y + z * z); }
        static double RotationDistance(float[] a, float[] b)
        {
            double minus = 0, plus = 0; for (int i = 0; i < 4; i++) { double m = (double)a[i] - b[i], p = (double)a[i] + b[i]; minus += m * m; plus += p * p; }
            double chord = Math.Sqrt(Math.Min(minus, plus)); return 4 * Math.Asin(Math.Min(1, chord / 2)) * 180 / Math.PI;
        }
        static bool Equal(BodyState a, BodyState b, CaseData row)
        {
            double p = Distance(a.position, b.position), v = Distance(a.velocity, b.velocity), w = Distance(a.angularVelocity, b.angularVelocity), r = RotationDistance(a.rotation, b.rotation);
            row.maximumPositionErrorMeters = Math.Max(row.maximumPositionErrorMeters, p); row.maximumVelocityErrorMetersPerSecond = Math.Max(row.maximumVelocityErrorMetersPerSecond, v);
            row.maximumAngularVelocityErrorRadiansPerSecond = Math.Max(row.maximumAngularVelocityErrorRadiansPerSecond, w); row.maximumRotationErrorDegrees = Math.Max(row.maximumRotationErrorDegrees, r);
            return a.finite && b.finite && a.sleeping == b.sleeping && a.isKinematic == b.isKinematic && a.activeSelf == b.activeSelf && a.activeInHierarchy == b.activeInHierarchy
                && p <= LinearTolerance && v <= LinearTolerance && w <= LinearTolerance && r <= AngularToleranceDegrees;
        }
    }
}
