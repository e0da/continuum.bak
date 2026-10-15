using System;
using KspContinuum;

public static class FoundationCases
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Near(double actual, double expected, double tolerance = 2e-10)
    {
        if (double.IsNaN(actual) || double.IsInfinity(actual)
            || Math.Abs(actual - expected) > tolerance * Math.Max(1, Math.Abs(expected)))
            throw new Exception("Owned foundation mismatch: " + actual + " versus " + expected);
    }
    static void Near(Vec actual, Vec expected) { Near(actual.X, expected.X); Near(actual.Y, expected.Y); Near(actual.Z, expected.Z); }
    static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid/stale owned operation was accepted.");
    }
    static OwnedRuntimeDefinition Definition(long revision = 0, double capacity = 10, double dryA = 2)
    {
        var a = new RigidAssemblyMember(0, dryA, new Vec(-1, 0, 0), Rotation.Identity, new Vec(), new Vec(1, 2, 3), Rotation.Identity);
        var b = new RigidAssemblyMember(1, 3, new Vec(1, 0, 0), Rotation.Identity, new Vec(), new Vec(2, 3, 4), Rotation.Identity);
        return new OwnedRuntimeDefinition("test-vessel", revision,
            new[] {
                new OwnedRuntimePartDefinition(12, "right", 200, new Vec(), Rotation.Identity, 300),
                new OwnedRuntimePartDefinition(11, "physicsless-alias", 100, new Vec(0, .5, 0), Rotation.Identity, 50),
                new OwnedRuntimePartDefinition(10, "left", 100, new Vec(), Rotation.Identity, 200)
            }, new[] { new OwnedRuntimeBodyDefinition(200, b), new OwnedRuntimeBodyDefinition(100, a) },
            new[] { new OwnedRuntimeResourceDefinition(90, 11, "fuel", capacity, .5, Math.Min(4, capacity)) }, new long[] { 7, 8, 9 });
    }
    static OwnedRuntimeWorld World()
    {
        var definition = Definition(); var topology = OwnedRuntimeTopology.Compile(definition,
            new[] { new OwnedRuntimeConnection(1, 10, 12) });
        return new OwnedRuntimeWorld(definition, topology, new OwnedRuntimeMotion {
            RootPosition = new Vec(10, 20, -30), Orientation = Rotation.AxisAngle(new Vec(1, 2, 3), .43),
            Velocity = new Vec(2, -3, 4), AngularMomentum = new Vec(.4, -.7, 6)
        });
    }
    static void Momentum(OwnedRuntimeWorld world, out Vec linear, out Vec angular, out double energy)
    {
        linear = new Vec(); angular = new Vec(); energy = 0;
        using (var read = world.AcquireRead()) for (int r = 0; r < read.RegionCount; r++)
        {
            var region = read.GetRegion(r); var motion = region.Motion; var properties = region.Properties;
            Vec p = motion.Velocity * properties.Mass;
            Vec center = motion.RootPosition + motion.Orientation.Rotate(properties.CenterOfMass);
            Vec omega = OwnedRuntimeDynamics.AngularVelocity(properties, motion.Orientation, motion.AngularMomentum);
            linear += p; angular += motion.AngularMomentum + Vec.Cross(center, p);
            energy += .5 * Dot(p, motion.Velocity) + .5 * Dot(motion.AngularMomentum, omega);
        }
    }
    static double Dot(Vec a, Vec b) { return a.X*b.X + a.Y*b.Y + a.Z*b.Z; }
    static void Same(OwnedRuntimeWorld a, OwnedRuntimeWorld b)
    {
        var sa = a.CaptureSnapshot(); var sb = b.CaptureSnapshot();
        Require(sa.Stamp.Matches(sb.Stamp), "Continuation stamp differs.");
        Require(sa.RegionMotion.Length == sb.RegionMotion.Length, "Continuation topology differs.");
        for (int i = 0; i < sa.RegionMotion.Length; i++)
        {
            Exact(sa.RegionMotion[i].RootPosition, sb.RegionMotion[i].RootPosition);
            Exact(sa.RegionMotion[i].Velocity, sb.RegionMotion[i].Velocity);
            Exact(sa.RegionMotion[i].AngularMomentum, sb.RegionMotion[i].AngularMomentum);
            var qa = sa.RegionMotion[i].Orientation; var qb = sb.RegionMotion[i].Orientation;
            Exact(qa.X,qb.X); Exact(qa.Y,qb.Y); Exact(qa.Z,qb.Z); Exact(qa.W,qb.W);
        }
        for (int i = 0; i < sa.ResourceQuantity.Length; i++) Exact(sa.ResourceQuantity[i], sb.ResourceQuantity[i]);
        for (int i = 0; i < sa.Temperature.Length; i++) Exact(sa.Temperature[i], sb.Temperature[i]);
        for (int i = 0; i < sa.ProviderState.Length; i++) Exact(sa.ProviderState[i], sb.ProviderState[i]);
        ExactInput(sa.Input, sb.Input);
        for (int i = 0; i < sa.BodyMembers.Length; i++)
        {
            var ma = sa.BodyMembers[i]; var mb = sb.BodyMembers[i];
            Require(ma.Id == mb.Id, "Continuation body identity differs."); Exact(ma.Mass,mb.Mass);
            Exact(ma.LocalPosition,mb.LocalPosition); Exact(ma.LocalCenterOfMass,mb.LocalCenterOfMass);
            Exact(ma.PrincipalInertia,mb.PrincipalInertia);
            Exact(ma.LocalRotation.X,mb.LocalRotation.X); Exact(ma.LocalRotation.Y,mb.LocalRotation.Y);
            Exact(ma.LocalRotation.Z,mb.LocalRotation.Z); Exact(ma.LocalRotation.W,mb.LocalRotation.W);
            Exact(ma.PrincipalRotation.X,mb.PrincipalRotation.X); Exact(ma.PrincipalRotation.Y,mb.PrincipalRotation.Y);
            Exact(ma.PrincipalRotation.Z,mb.PrincipalRotation.Z); Exact(ma.PrincipalRotation.W,mb.PrincipalRotation.W);
        }
        Require(sa.InputHistory.Length == sb.InputHistory.Length, "Continuation history length differs.");
        for (int i = 0; i < sa.InputHistory.Length; i++)
        {
            var ha = sa.InputHistory[i]; var hb = sb.InputHistory[i];
            Require(ha.Sequence == hb.Sequence, "Continuation history order differs.");
            Exact(ha.StartedAt,hb.StartedAt); Exact(ha.DeltaTime,hb.DeltaTime); ExactInput(ha.Input,hb.Input);
        }
    }
    static void ExactInput(OwnedRuntimeInput a, OwnedRuntimeInput b)
    { Exact(a.Throttle,b.Throttle); Exact(a.Pitch,b.Pitch); Exact(a.Yaw,b.Yaw); Exact(a.Roll,b.Roll); Exact(a.X,b.X); Exact(a.Y,b.Y); Exact(a.Z,b.Z); }
    static void Exact(double a, double b)
    { Require(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b), "Continuation bits differ."); }
    static void Exact(Vec a, Vec b) { Exact(a.X,b.X); Exact(a.Y,b.Y); Exact(a.Z,b.Z); }

    public static void Run()
    {
        var world = World();
        using (var read = world.AcquireRead())
        {
            Require(read.PartCount == 3 && read.BodyCount == 2 && read.RegionCount == 1, "Authored aliases became duplicate physical bodies.");
            Near(read.GetRegion(0).Properties.Mass, 7);
            Near(read.GetRegion(0).Properties.CenterOfMass, new Vec(-1.0/7, 1.0/7, 0));
            var wet = read.GetBodyMember(world.BodyOfPart(world.PartSlot(10)));
            Near(wet.Mass, 4); Near(wet.LocalCenterOfMass, new Vec(0,.25,0));
            Near(wet.PrincipalInertia, new Vec(1.25,2,3.25));
            Near(read.GetRegion(0).Properties.Inertia.XX, 47.0/14);
            Near(read.GetRegion(0).Properties.Inertia.XY, 6.0/7);
            Near(read.GetRegion(0).Properties.Inertia.YY, 83.0/7);
            Near(read.GetRegion(0).Properties.Inertia.ZZ, 199.0/14);
            Require(world.BodyOfPart(world.PartSlot(10)) == world.BodyOfPart(world.PartSlot(11)), "Alias map was lost.");
        }
        Vec p0, l0, p1, l1; double e0, e1;
        Momentum(world, out p0, out l0, out e0);
        var beforeSplit = world.Stamp; world.Split(beforeSplit, 1);
        Require(world.RegionCount == 2, "Split did not separate physical regions.");
        Momentum(world, out p1, out l1, out e1); Near(p1,p0); Near(l1,l0); Near(e1,e0);
        Require(world.PartCount == 3 && world.ResourceCount == 1, "Split changed authored/resource identity.");
        Reject(() => world.BeginStep(beforeSplit, .02, new OwnedRuntimeInput(0)));
        world.Dock(world.Stamp, 2, 10, 12); Require(world.RegionCount == 1, "Dock did not join regions.");
        Momentum(world, out p1, out l1, out e1); Near(p1,p0); Near(l1,l0); Near(e1,e0);

        // Relative docking removes energy while preserving total P and L about the origin.
        world.Split(world.Stamp, 2);
        using (var step = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(0)))
        {
            var motion = step.GetRegion(1).Motion; motion.Velocity += new Vec(0, 2, -1);
            step.SetRegionMotion(1, motion); step.Commit();
        }
        Momentum(world, out p0, out l0, out e0); world.Dock(world.Stamp, 3, 10, 12);
        Momentum(world, out p1, out l1, out e1); Near(p1,p0); Near(l1,l0);
        Require(e1 < e0 && world.LastTopologyRemovedEnergy > 0, "Inelastic dock did not report removed relative energy.");

        // A cancelled/invalid write changes none of the committed bank.
        var saved = world.CaptureSnapshot();
        using (var step = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(.6, .2)))
        { step.SetResourceQuantity(0, 1); step.SetTemperature(0, 900); step.SetProviderState(0, 4); }
        Same(world, OwnedRuntimeWorld.RestoreSnapshot(saved));
        var closed = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(0)); closed.Dispose();
        Reject(() => closed.SetProviderState(0, 7)); Reject(() => closed.Commit());
        using (var step = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(0)))
        {
            step.SetResourceQuantity(0, 2); Reject(() => step.SetTemperature(0, double.NaN));
            Reject(() => world.Rebase(world.Stamp, new Vec(1,0,0), "other"));
        }
        Same(world, OwnedRuntimeWorld.RestoreSnapshot(saved));

        // All three retained generations can be pinned; none is overwritten.
        var a = world.AcquireRead(); var oldStamp = a.Stamp; double oldFuel = a.GetResourceQuantity(0);
        using (var step = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(0))) { step.SetResourceQuantity(0, 3); step.Commit(); }
        var b = world.AcquireRead();
        using (var step = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(0))) { step.SetResourceQuantity(0, 2); step.Commit(); }
        var c = world.AcquireRead(); Reject(() => world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(0)));
        Require(a.Stamp.Matches(oldStamp), "Pinned reader stamp was overwritten."); Near(a.GetResourceQuantity(0), oldFuel);
        a.Dispose(); using (var step = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(0))) { step.Commit(); }
        b.Dispose(); c.Dispose();

        // Snapshot continuation includes controls, thermal/resource/provider state and ordered history.
        using (var step = world.BeginStep(world.Stamp, .02, new OwnedRuntimeInput(.3, -.2, .1, .4, .2, -.3, .5)))
        { step.SetProviderState(1, .125); step.SetTemperature(2, 450); step.Commit(); }
        var restored = OwnedRuntimeWorld.RestoreSnapshot(world.CaptureSnapshot()); Same(world, restored);
        foreach (var target in new[] { world, restored }) using (var step = target.BeginStep(target.Stamp, .02, new OwnedRuntimeInput(.8)))
        {
            step.SetResourceQuantity(0, 1.5); step.RefreshMassProperties();
            var region = step.GetRegion(0); var motion = region.Motion;
            OwnedRuntimeDynamics.Step(ref motion, region.Properties, new Vec(2,3,4), new Vec(.1,.2,-.3), step.DeltaTime);
            step.SetRegionMotion(0, motion); step.SetProviderState(1, .25); step.Commit();
        }
        Same(world, restored);
        var corrupt = world.CaptureSnapshot(); corrupt.ResourceQuantity[0] = double.NaN;
        Reject(() => OwnedRuntimeWorld.RestoreSnapshot(corrupt));
        corrupt = world.CaptureSnapshot(); corrupt.BodyMembers[0] = corrupt.BodyMembers[1];
        Reject(() => OwnedRuntimeWorld.RestoreSnapshot(corrupt));
        corrupt = world.CaptureSnapshot(); var originalMember = corrupt.BodyMembers[0];
        corrupt.BodyMembers[0] = new RigidAssemblyMember(originalMember.Id, originalMember.Mass, originalMember.LocalPosition,
            originalMember.LocalRotation, originalMember.LocalCenterOfMass, originalMember.PrincipalInertia * 2, originalMember.PrincipalRotation);
        Reject(() => OwnedRuntimeWorld.RestoreSnapshot(corrupt));
        saved = world.CaptureSnapshot(); var oldDefinitionStamp = world.Stamp;
        Reject(() => world.Reconfigure(oldDefinitionStamp, Definition(1, 1)));
        Same(world, OwnedRuntimeWorld.RestoreSnapshot(saved));
        world.Reconfigure(world.Stamp, Definition(1, 10, 2.5));
        Require(world.Stamp.DefinitionRevision == 1 && world.Stamp.Epoch > oldDefinitionStamp.Epoch, "HMR did not invalidate old work.");
        Reject(() => world.BeginStep(oldDefinitionStamp, .02, new OwnedRuntimeInput(0)));
        var frameStamp = world.Stamp; Vec root;
        using (var read = world.AcquireRead()) root = read.GetRegion(0).Motion.RootPosition;
        world.Rebase(frameStamp, new Vec(100, -2, 5), "rebased/inertial");
        using (var read = world.AcquireRead()) Near(read.GetRegion(0).Motion.RootPosition, root + new Vec(100,-2,5));
        Require(world.Stamp.FrameRevision == frameStamp.FrameRevision + 1, "Frame change did not invalidate work.");
        Surplus();
        Console.WriteLine("Owned foundation: aliases, split/join P/L and energy, atomic rollback, retained read leases, complete continuation and HMR/frame invalidation passed.");
    }
    static void Surplus()
    {
        Reject(() => new OwnedRuntimeResourceDefinition(90, 1, "MonoPropellant", 0, 1, 3));
        var parts = new[] { new OwnedRuntimePartDefinition(1,"surplus-source",42,new Vec(),Rotation.Identity,100) };
        var bodies = new[] { new OwnedRuntimeBodyDefinition(42,new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,
            new Vec(),new Vec(2,3,4),Rotation.Identity)) };
        var resources = new[] { new OwnedRuntimeResourceDefinition(90,1,"MonoPropellant",0,1,3,true) };
        var definition = new OwnedRuntimeDefinition("surplus-source",0,parts,bodies,resources);
        var world = new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),
            new OwnedRuntimeMotion { Orientation=Rotation.Identity });
        using (var read = world.AcquireRead()) { Near(read.GetResourceQuantity(0),3); Near(read.GetRegion(0).Properties.Mass,5); }
        using (var step = world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0))) { step.SetResourceQuantity(0,2); step.Commit(); }
        Require(world.Definition.GetResource(0).Capacity==0 && world.Definition.GetResource(0).AcquiredSurplus,"Acquired source facts were changed.");
        Same(world,OwnedRuntimeWorld.RestoreSnapshot(world.CaptureSnapshot()));
        using (var step = world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0))) Reject(() => step.SetResourceQuantity(0,2.5));
        var changed = new OwnedRuntimeDefinition("surplus-source",1,parts,bodies,
            new[] {new OwnedRuntimeResourceDefinition(90,1,"MonoPropellant",1,1,3,true)});
        Reject(() => world.Reconfigure(world.Stamp,changed));
        using (var step = world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0))) { step.SetResourceQuantity(0,0); step.Commit(); }
        using (var step = world.BeginStep(world.Stamp,.02,new OwnedRuntimeInput(0))) Reject(() => step.SetResourceQuantity(0,.1));
        using (var read = world.AcquireRead()) { Near(read.GetResourceQuantity(0),0); Near(read.GetRegion(0).Properties.Mass,2); }
    }
}
