using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class FlightPerturbation
    {
        sealed class Attachment
        {
            public Rigidbody Parent, Child;
            public Vector3 Position;
            public Quaternion Rotation;
        }
        readonly List<Rigidbody> bodies = new List<Rigidbody>();
        readonly List<Attachment> attachments = new List<Attachment>();
        readonly Vessel vessel;
        readonly Rigidbody root, target;
        readonly Vector3 direction;
        readonly double startFixedTime;
        double previousElapsed, sampledAt = -1;
        int awake;
        double angularRms, angularMax, flexPosition, flexAngle;
        public readonly FlightPerturbationReport Report;

        public static FlightPerturbation Create(string[] args, Vessel vessel, string runId)
        {
            string mode = null;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--continuum-flight-perturbation")
            {
                if (mode != null || i + 1 >= args.Length) throw new ArgumentException("Invalid perturbation option.");
                mode = args[++i];
            }
            if (mode == null) return null;
            if (mode != "quiet" && mode != "off-center-pulse") throw new ArgumentException("Unknown perturbation mode.");
            return new FlightPerturbation(args, vessel, runId, mode);
        }
        static double Number(string[] args, string key, double fallback, double min, double max)
        {
            bool found = false;
            foreach (string value in args) if (value.StartsWith(key, StringComparison.Ordinal))
            {
                if (found || !double.TryParse(value.Substring(key.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out fallback) ||
                    double.IsNaN(fallback) || double.IsInfinity(fallback) || fallback < min || fallback > max)
                    throw new ArgumentException("Invalid perturbation option: " + key);
                found = true;
            }
            return fallback;
        }
        FlightPerturbation(string[] args, Vessel vessel, string runId, string mode)
        {
            if (vessel == null || vessel.rootPart == null || vessel.rootPart.rb == null) throw new InvalidOperationException("Perturbation requires physical vessel root.");
            this.vessel = vessel; root = vessel.rootPart.rb;
            double id = Number(args, "--continuum-flight-pulse-part=", 0, 1, uint.MaxValue);
            if (id != Math.Floor(id)) throw new ArgumentException("Pulse part flightID must be an integer.");
            Part selected = null; double distance = -1;
            foreach (Part part in vessel.parts) if (part != null && part.rb != null && !part.rb.isKinematic)
            {
                if (!bodies.Contains(part.rb)) bodies.Add(part.rb);
                double candidate = (part.rb.worldCenterOfMass - root.worldCenterOfMass).sqrMagnitude;
                if (id > 0 ? part.flightID == (uint)id : candidate > distance || candidate == distance && selected != null && part.flightID < selected.flightID)
                { selected = part; distance = candidate; }
                if (part.parent != null && part.parent.rb != null && part.parent.rb != part.rb)
                {
                    Rigidbody parent = part.parent.rb;
                    attachments.Add(new Attachment { Parent = parent, Child = part.rb,
                        Position = Quaternion.Inverse(parent.rotation) * (part.rb.position - parent.position),
                        Rotation = Quaternion.Inverse(parent.rotation) * part.rb.rotation });
                }
            }
            if (selected == null || distance < .01 || selected.rb.mass <= 0 || float.IsNaN(selected.rb.mass) || float.IsInfinity(selected.rb.mass))
                throw new InvalidOperationException("No admitted off-center physical body.");
            target = selected.rb;
            Vector3 lever = Quaternion.Inverse(root.rotation) * (target.worldCenterOfMass - CenterOfMass());
            Vector3 cross = Vector3.Cross(lever, Vector3.up);
            if (cross.sqrMagnitude < .0001f) cross = Vector3.Cross(lever, Vector3.right);
            if (cross.sqrMagnitude < .0001f) throw new InvalidOperationException("Selected body has no off-center lever.");
            direction = cross.normalized;
            double deltaV = Number(args, "--continuum-flight-pulse-delta-v=", .25, .001, 100);
            double duration = Number(args, "--continuum-flight-pulse-duration=", .25, .01, 2);
            double at = Number(args, "--continuum-flight-pulse-at=", 1, 0, 60);
            startFixedTime = Time.fixedTime;
            Report = new FlightPerturbationReport { runId = runId, processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                mode = mode, status = "capturing", targetFlightId = selected.flightID.ToString(CultureInfo.InvariantCulture),
                targetPartName = selected.partInfo == null ? selected.name : selected.partInfo.name,
                selection = id > 0 ? "explicit-flightID" : "farthest-physical-center-from-root-ties-lowest-flightID",
                requestedStartSeconds = at, requestedDurationSeconds = duration, nominalDeltaVMetersPerSecond = deltaV,
                targetMassUnityUnits = target.mass, requestedImpulseUnityUnits = target.mass * deltaV,
                requestedForceUnityUnits = target.mass * deltaV / duration, rootLocalForceDirection = V(direction),
                initialRootLocalLeverMeters = V(lever), cachedBodies = bodies.Count, sampledAttachments = attachments.Count,
                fixedTimeOriginSeconds = startFixedTime };
        }
        static Vec V(Vector3 v) { return new Vec(v.x, v.y, v.z); }
        Vector3 CenterOfMass()
        {
            Vector3 center = Vector3.zero; float mass = 0;
            foreach (Rigidbody body in bodies) if (body != null) { center += body.worldCenterOfMass * body.mass; mass += body.mass; }
            if (mass <= 0) throw new InvalidOperationException("Vessel mass unavailable.");
            return center / mass;
        }
        public void FixedStep()
        {
            double elapsed = Time.fixedTime - startFixedTime;
            if (elapsed < previousElapsed || target == null || root == null || FlightGlobals.ActiveVessel != vessel)
                throw new InvalidOperationException("Perturbation state changed.");
            double overlap = Math.Max(0, Math.Min(elapsed, Report.requestedStartSeconds + Report.requestedDurationSeconds) - Math.Max(previousElapsed, Report.requestedStartSeconds));
            previousElapsed = elapsed;
            if (Report.mode != "off-center-pulse" || overlap <= 0) return;
            if (target.isKinematic || vessel.packed || Time.fixedDeltaTime <= 0) throw new InvalidOperationException("Perturbation target not simulated.");
            Vector3 force = root.rotation * direction * (float)(Report.requestedForceUnityUnits * overlap / Time.fixedDeltaTime);
            Vector3 point = target.worldCenterOfMass;
            Vector3 impulse = force * Time.fixedDeltaTime;
            Vector3 torqueImpulse = Vector3.Cross(point - CenterOfMass(), impulse);
            target.AddForceAtPosition(force, point, ForceMode.Force);
            Report.appliedCount++; Report.appliedDurationSeconds += overlap;
            Report.appliedImpulseUnityUnits += impulse.magnitude;
            Report.appliedImpulseWorld = Report.appliedImpulseWorld + V(impulse);
            Report.appliedAngularImpulseWorld = Report.appliedAngularImpulseWorld + V(torqueImpulse);
            if (Report.appliedCount == 1) Report.firstAppliedFixedSeconds = elapsed;
            Report.lastAppliedFixedSeconds = elapsed;
        }
        public void Observe(ProfileFrame frame)
        {
            double elapsed = Time.fixedTime - startFixedTime;
            if (sampledAt < 0 || elapsed - sampledAt >= Report.observationCadenceSimulatedSeconds)
            {
                sampledAt = elapsed; awake = 0; angularRms = angularMax = flexPosition = flexAngle = 0;
                foreach (Rigidbody body in bodies)
                {
                    if (body == null) throw new InvalidOperationException("Observed physical body destroyed.");
                    if (!body.IsSleeping()) awake++;
                    double rate = body.angularVelocity.magnitude; angularRms += rate * rate; angularMax = Math.Max(angularMax, rate);
                }
                angularRms = Math.Sqrt(angularRms / bodies.Count);
                foreach (Attachment edge in attachments)
                {
                    if (edge.Parent == null || edge.Child == null) throw new InvalidOperationException("Observed attachment destroyed.");
                    Vector3 position = Quaternion.Inverse(edge.Parent.rotation) * (edge.Child.position - edge.Parent.position);
                    Quaternion rotation = Quaternion.Inverse(edge.Parent.rotation) * edge.Child.rotation;
                    flexPosition = Math.Max(flexPosition, (position - edge.Position).magnitude);
                    flexAngle = Math.Max(flexAngle, Quaternion.Angle(rotation, edge.Rotation));
                }
                Report.observationSamples++; Report.peakAwakeBodies = Math.Max(Report.peakAwakeBodies, awake);
                Report.peakAngularRadiansPerSecond = Math.Max(Report.peakAngularRadiansPerSecond, angularMax);
                Report.peakAttachmentPositionDeltaMeters = Math.Max(Report.peakAttachmentPositionDeltaMeters, flexPosition);
                Report.peakAttachmentRotationDeltaDegrees = Math.Max(Report.peakAttachmentRotationDeltaDegrees, flexAngle);
            }
            frame.motionSampleSimulatedSeconds = sampledAt; frame.awakeRigidbodies = awake;
            frame.angularRmsRadiansPerSecond = angularRms; frame.angularMaxRadiansPerSecond = angularMax;
            frame.attachmentPositionDeltaMaxMeters = flexPosition; frame.attachmentRotationDeltaMaxDegrees = flexAngle;
        }
        public bool Finish()
        {
            if (Report.status != "capturing") return false;
            bool covered = Report.mode == "quiet" || Math.Abs(Report.appliedDurationSeconds - Report.requestedDurationSeconds) <= 1e-5 &&
                Math.Abs(Report.appliedImpulseUnityUnits - Report.requestedImpulseUnityUnits) <= Report.requestedImpulseUnityUnits * 1e-4;
            Report.status = covered ? "complete" : "invalid-incomplete-pulse";
            return covered;
        }
    }
}
