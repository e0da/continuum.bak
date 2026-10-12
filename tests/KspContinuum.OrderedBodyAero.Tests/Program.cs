using System;
using KspContinuum;

class Program
{
    static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    static OrderedBodyAeroPacket Input()
    {
        return new OrderedBodyAeroPacket { runId = "analytic", vesselId = "vessel", topologySha256 = new string('a', 64), providerEpoch = "original-providers/1", invocation = 1,
            rows = new[] { new OrderedBodyAeroRow { ordinal = 0, partId = 7, recipientBodyId = -10, ownBody = true, applyDrag = true, angularDragByIntegrator = true,
                atmosphericDensity = 2, velocity = new BodyAeroVector(10, 0, 0), angularDrag = 1, angularDragMultiplier = 1, dragMultiplier = 1,
                originalDragValue = 3, pseudoReMultiplier = 1, bodyLiftEligible = true, liftForceEligible = true, liftGeometryCaptured = true,
                bodyLiftMultiplier = 2, globalBodyLiftMultiplier = 1, liftMachSample = 1, rotatedScaledLift = new BodyAeroVector(2, 4, 0),
                dragApplicationPoint = new BodyAeroVector(1, 2, 3), liftApplicationPoint = new BodyAeroVector(4, 5, 6), providerEpoch = "original-providers/1" } } };
    }
    static OrderedBodyAeroObservation Oracle(OrderedBodyAeroPacket p)
    {
        // Analytic axis-aligned drag: q=rho*v^2/2000=.1, D=q*Cd=.3;
        // lift projection removes only X. Values here do not call the candidate.
        return new OrderedBodyAeroObservation { runId = p.runId, vesselId = p.vesselId, topologySha256 = p.topologySha256,
            providerEpoch = p.providerEpoch, invocation = p.invocation, originalReturned = true, originalCommandTapeComplete = true,
            rows = new[] { new OrderedBodyAeroResult { ordinal = 0, partId = 7, speedSquared = 100, speed = 10, direction = new BodyAeroVector(1, 0, 0),
                dynamicPressure = .1, dragScalar = .3f, bodyLiftScalar = .2f, bodyLiftScalarWritten = true, commands = new[] {
                    new BodyAeroCommand { ordinal = 0, partId = 7, recipientBodyId = -10, kind = BodyAeroCommandKind.AngularDrag },
                    new BodyAeroCommand { ordinal = 0, partId = 7, recipientBodyId = -10, kind = BodyAeroCommandKind.AngularDrag, angularDrag = (float)(.1 * .009869232667160128) },
                    new BodyAeroCommand { ordinal = 0, partId = 7, recipientBodyId = -10, kind = BodyAeroCommandKind.DragAtPosition, force = new BodyAeroVector(-.3f, 0, 0), point = new BodyAeroVector(1, 2, 3) },
                    new BodyAeroCommand { ordinal = 0, partId = 7, recipientBodyId = -10, kind = BodyAeroCommandKind.LiftAtPosition, force = new BodyAeroVector(0, 4, 0), point = new BodyAeroVector(4, 5, 6) } } } } };
    }
    static void Main()
    {
        OrderedBodyAeroPacket p = Input(); OrderedBodyAeroObservation reference = Oracle(p); ValidatedOrderedBodyAero token; string reason;
        Require(OrderedBodyAeroGate.TryValidate(p, reference, out token, out reason), "Independent analytic oracle rejected: " + reason);
        p.rows[0].velocity.x = 99; reference.rows[0].commands[2].force.x = 99;
        Require(token.At(0).speed == 10 && token.At(0).commands[2].force.x == -.3f, "Validated generation borrowed mutable caller state.");
        var copy = token.At(0); copy.commands[2].force.x = 88;
        Require(token.At(0).commands[2].force.x == -.3f, "Returned tape mutates validated generation.");
        p = Input(); reference = Oracle(p); reference.invocation++;
        Require(!OrderedBodyAeroGate.TryValidate(p, reference, out token, out reason) && token == null, "Stale invocation accepted.");
        reference = Oracle(p); var commands = reference.rows[0].commands; var swap = commands[2]; commands[2] = commands[3]; commands[3] = swap;
        Require(!OrderedBodyAeroGate.TryValidate(p, reference, out token, out reason), "Drag/lift reorder accepted.");
        reference = Oracle(p); reference.rows[0].commands[2].recipientBodyId = -11;
        Require(!OrderedBodyAeroGate.TryValidate(p, reference, out token, out reason), "Wrong native body accepted.");
        reference = Oracle(p); reference.rows[0].commands[2].forceMode = 5;
        Require(!OrderedBodyAeroGate.TryValidate(p, reference, out token, out reason), "Force/acceleration confusion accepted.");
        reference = Oracle(p); reference.rows[0].commands[2].point.x += .00001f;
        Require(!OrderedBodyAeroGate.TryValidate(p, reference, out token, out reason), "Application point drift accepted.");
        reference = Oracle(p); reference.rows[0].dragScalar *= 1.001f;
        Require(!OrderedBodyAeroGate.TryValidate(p, reference, out token, out reason), "Frozen numerical gate widened.");
        var boundary = Input(); boundary.rows[0].originalDragValue = 10;
        var boundaryReference = Oracle(boundary); boundaryReference.rows[0].dragScalar = 1.0000205f;
        boundaryReference.rows[0].commands[2].force.x = -1;
        Require(!OrderedBodyAeroGate.TryValidate(boundary, boundaryReference, out token, out reason), "Additive absolute tolerance widened the original donor bound above magnitude one.");
        p.rows[0].submergedPortion = .1;
        Require(!OrderedBodyAeroGate.TryValidate(p, Oracle(p), out token, out reason), "Unsupported water path admitted.");
        p = Input(); p.rows[0].originalDragValue = double.NaN; p.rows[0].bodyLiftEligible = false;
        var result = OrderedBodyAeroKernel.Evaluate(p)[0];
        Require(result.dragScalar == 0 && result.commands.Length == 2 && !result.bodyLiftScalarWritten, "Stock NaN drag suppression or unwritten lift mask lost.");
        p = Input(); p.rows[0].ownBody = false; p.rows[0].applyParentDrag = true; p.rows[0].accelerationMode = true;
        result = OrderedBodyAeroKernel.Evaluate(p)[0];
        Require(result.commands.Length == 2 && result.commands[0].forceMode == 5 && result.commands[0].recipientBodyId == -10, "Physicsless parent command ownership changed.");
        Console.WriteLine("Ordered body-aero analytic command and ownership counterexamples passed.");
    }
}
