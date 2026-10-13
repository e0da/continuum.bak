using System;
using KspContinuum;

public static class ProjectionCases
{
    static void Near(double actual, double expected, double tolerance = 1e-10)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance * Math.Max(1, Math.Abs(expected)))
            throw new Exception("Projection mismatch: " + actual + " versus " + expected);
    }
    static void Vector(Vec actual, Vec expected) { Near(actual.X, expected.X); Near(actual.Y, expected.Y); Near(actual.Z, expected.Z); }
    static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid projection input was accepted."); }
    static Vec Subtract(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    static void Same(double a, double b)
    { if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b)) throw new Exception("Projection depends on source enumeration order."); }
    static void Same(Vec a, Vec b) { Same(a.X, b.X); Same(a.Y, b.Y); Same(a.Z, b.Z); }

    public static void Run()
    {
        var a = new RigidAssemblyMember(7, 2, new Vec(1, 1, 3),
            Rotation.AxisAngle(new Vec(0, 0, 1), Math.PI / 2), new Vec(1, 0, 0),
            new Vec(2, 4, 6), Rotation.AxisAngle(new Vec(0, 0, 1), -Math.PI / 4));
        var b = new RigidAssemblyMember(2, 1, new Vec(-2, -1, 0), Rotation.Identity,
            new Vec(), new Vec(1, 2, 3), Rotation.Identity);
        var ma = new RigidAssemblyMotion(a, new Vec(3, -1, 2), new Vec(1, 2, -1));
        var mb = new RigidAssemblyMotion(b, new Vec(-2, 4, 1), new Vec(0, -1, 3));
        var p = RigidAssemblyProjection.Project(new[] { ma, mb });

        // Independent analytic ledger: centers (1,2,3),(-2,-1,0), C=(0,1,2).
        // A's spin tensor is [[3,-1,0],[-1,3,0],[0,0,6]]; total spin L=(1,3,3);
        // orbital L=(12,8,-20). Full aggregate I=[[16,-7,-6],[-7,17,-6],[-6,-6,21]].
        Near(p.Properties.Mass, 3); Vector(p.Properties.CenterOfMass, new Vec(0, 1, 2));
        Vector(p.LinearMomentum, new Vec(4, 2, 5)); Vector(p.AngularMomentumAboutCenter, new Vec(13, 11, -17));
        Near(p.Properties.Inertia.XX, 16); Near(p.Properties.Inertia.XY, -7); Near(p.Properties.Inertia.XZ, -6);
        Near(p.Properties.Inertia.YY, 17); Near(p.Properties.Inertia.YZ, -6); Near(p.Properties.Inertia.ZZ, 21);
        Vector(p.Velocity, new Vec(4.0 / 3, 2.0 / 3, 5.0 / 3));
        Vector(p.AngularVelocity, new Vec(3738.0 / 2991, 3333.0 / 2991, -401.0 / 2991));
        Near(p.SourceKineticEnergy, 47.5); Near(p.RigidKineticEnergy, 7.5 + 46037.0 / 2991);
        Near(p.RemovedKineticEnergy, 40 - 46037.0 / 2991);
        // Copying A's motion gives P=(9,-3,6), not the actual all-body P=(4,2,5).
        Vector(ma.Velocity * p.Properties.Mass, new Vec(9, -3, 6));
        if (Math.Abs(p.Velocity.Y - ma.Velocity.Y) < .1) throw new Exception("Root-only motion counterexample disappeared.");

        var reversed = RigidAssemblyProjection.Project(new[] { mb, ma });
        Same(p.LinearMomentum, reversed.LinearMomentum); Same(p.AngularMomentumAboutCenter, reversed.AngularMomentumAboutCenter);
        Same(p.Velocity, reversed.Velocity); Same(p.AngularVelocity, reversed.AngularVelocity);
        Same(p.SourceKineticEnergy, reversed.SourceKineticEnergy); Same(p.RigidKineticEnergy, reversed.RigidKineticEnergy);

        // Noncommuting body/principal rotations: Rz(90)*Rx(90) maps principal
        // X,Y,Z onto assembly Y,Z,X, giving diag(5,2,3), not diag(3,5,2).
        var turned = new RigidAssemblyMember(4, 4, new Vec(),
            Rotation.AxisAngle(new Vec(0, 0, 1), Math.PI / 2), new Vec(.5, 0, 0),
            new Vec(2, 3, 5), Rotation.AxisAngle(new Vec(1, 0, 0), Math.PI / 2));
        var composed = RigidAssemblyProjection.Project(new[] {
            new RigidAssemblyMotion(turned, new Vec(1, 2, -1), new Vec(2, -3, 4))
        });
        Vector(composed.Properties.CenterOfMass, new Vec(0, .5, 0));
        Vector(composed.LinearMomentum, new Vec(4, 8, -4)); Vector(composed.AngularMomentumAboutCenter, new Vec(10, -6, 12));
        Vector(composed.AngularVelocity, new Vec(2, -3, 4)); Near(composed.SourceKineticEnergy, 55);
        Near(composed.RigidKineticEnergy, 55); Near(composed.RemovedKineticEnergy, 0);

        // A common rigid COM velocity field is a fixed point, including spin.
        Vec velocity = new Vec(.25, -1.5, 2), omega = new Vec(.4, -.7, .9), center = new Vec(0, 1, 2);
        var rigid = RigidAssemblyProjection.Project(new[] {
            new RigidAssemblyMotion(a, velocity + Vec.Cross(omega, Subtract(new Vec(1, 2, 3), center)), omega),
            new RigidAssemblyMotion(b, velocity + Vec.Cross(omega, Subtract(new Vec(-2, -1, 0), center)), omega)
        });
        Vector(rigid.Velocity, velocity); Vector(rigid.AngularVelocity, omega);
        Near(rigid.RemovedKineticEnergy, 0); Near(rigid.SourceKineticEnergy, rigid.RigidKineticEnergy);

        // Uniform inertia/mass scaling must not encounter an absolute determinant cutoff.
        foreach (double scale in new[] { 1e-100, 1e100 })
        {
            var member = new RigidAssemblyMember(1, scale, new Vec(), Rotation.Identity, new Vec(),
                new Vec(2 * scale, 3 * scale, 4 * scale), Rotation.AxisAngle(new Vec(1, 2, 3), .71));
            var single = RigidAssemblyProjection.Project(new[] { new RigidAssemblyMotion(member, velocity, omega) });
            Vector(single.Velocity, velocity); Vector(single.AngularVelocity, omega);
            Near(single.SourceKineticEnergy / scale, single.RigidKineticEnergy / scale);
        }
        Reject(() => RigidAssemblyProjection.Project(null));
        Reject(() => RigidAssemblyProjection.Project(new RigidAssemblyMotion[0]));
        Reject(() => RigidAssemblyProjection.Project(new RigidAssemblyMotion[] { null }));
        Reject(() => RigidAssemblyProjection.Project(new[] { ma, ma }));
        Reject(() => RigidAssemblyProjection.Project(new[] { ma, new RigidAssemblyMotion(a, new Vec(), new Vec()) }));
        Reject(() => new RigidAssemblyMotion(null, new Vec(), new Vec()));
        Reject(() => new RigidAssemblyMotion(a, new Vec(double.NaN, 0, 0), new Vec()));
        Reject(() => new RigidAssemblyMotion(a, new Vec(), new Vec(0, double.PositiveInfinity, 0)));
        var thin = new RigidAssemblyMember(3, 1, new Vec(), Rotation.Identity, new Vec(), new Vec(1e-14, 1, 1), Rotation.Identity);
        Reject(() => RigidAssemblyProjection.Project(new[] { new RigidAssemblyMotion(thin, new Vec(), new Vec()) }));
        Reject(() => RigidAssemblyProjection.Project(new[] { new RigidAssemblyMotion(a, new Vec(1e200, 0, 0), new Vec()) }));
        Console.WriteLine("Rigid projection analytic momentum/full tensor/energy, root-only counterexample, deterministic ordering, rigid-field fixed point, scaling and invalid-input checks passed.");
    }
}
