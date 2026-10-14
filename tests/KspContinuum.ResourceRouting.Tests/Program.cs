using System;
using System.Collections.Generic;
using KspContinuum;

static class Program
{
    static int checks;
    static void Need(bool value, string why) { checks++; if (!value) throw new Exception(why); }
    static void Near(double actual, double expected, string why)
    { Need(double.IsFinite(actual) && Math.Abs(actual - expected) <= 1e-12 * Math.Max(1, Math.Abs(expected)), why); }
    static OwnedRuntimeWorld World()
    {
        var parts = new OwnedRuntimePartDefinition[4];
        var bodies = new OwnedRuntimeBodyDefinition[2];
        for (int b = 0; b < 2; b++) bodies[b] = new OwnedRuntimeBodyDefinition(b + 1,
            new RigidAssemblyMember(b, 10, new Vec(b * 2, 0, 0), Rotation.Identity, new Vec(), new Vec(1, 1, 1), Rotation.Identity));
        for (int p = 0; p < parts.Length; p++) parts[p] = new OwnedRuntimePartDefinition(p + 1, "tank", p / 2 + 1, new Vec(), Rotation.Identity, 100);
        var resources = new[] {
            new OwnedRuntimeResourceDefinition(1, 1, "Fuel", 10, 1, .125),
            new OwnedRuntimeResourceDefinition(2, 2, "Fuel", 10, 2, .75),
            new OwnedRuntimeResourceDefinition(3, 3, "Fuel", 10, 1, 2),
            new OwnedRuntimeResourceDefinition(4, 4, "Fuel", 10, 1, 3) };
        var definition = new OwnedRuntimeDefinition("routing-oracle", 0, parts, bodies, resources, new long[] { 10, 11 });
        return new OwnedRuntimeWorld(definition, OwnedRuntimeTopology.Compile(definition, Array.Empty<OwnedRuntimeConnection>()),
            new OwnedRuntimeMotion { Orientation = Rotation.Identity });
    }
    static OwnedRuntimeActuatorSpec Engine(int part, bool direct = false)
    { return direct ? new OwnedRuntimeActuatorSpec(part, OwnedRuntimeActuatorKind.Engine, new Vec(), new Vec(1, 0, 0), 9.80665, part, 1)
        : new OwnedRuntimeActuatorSpec(part, OwnedRuntimeActuatorKind.Engine, new Vec(), new Vec(1, 0, 0), 9.80665, new[] { "Fuel" }, new[] { 1d }, 1); }
    static void Step(OwnedRuntimeHost host)
    { host.Advance(.1, new OwnedRuntimeInput(1), new OwnedRuntimeEnvironment("vacuum", 0, 0, 0, 300, new Vec(), new Vec()), new OwnedRuntimeControls { Throttle = 1, EnginesEnabled = true }); }
    static double[] Read(OwnedRuntimeWorld world)
    { using var read = world.AcquireRead(); var result = new double[read.ResourceCount]; for (int i = 0; i < result.Length; i++) result[i] = read.GetResourceQuantity(i); return result; }
    static void Routes(OwnedRuntimeExecutionStrategy strategy)
    {
        var world = World(); var systems = new OwnedRuntimeSystems(world, new[] { Engine(0), Engine(1), Engine(2) }, Array.Empty<OwnedRuntimeSurfaceSpec>());
        systems.SetExecutionStrategy(strategy); var host = new OwnedRuntimeHost(world, systems);
        Step(host); var first = Read(world);
        Near(first[0], 0, "Ordered shared first reservoir depleted");
        Near(first[1], .7125, "Second density converted after first depletion");
        Near(first[2], 1.9, "Independent region withdraws its own resource");
        Near(first[3], 3, "Later ordered reservoir untouched");
        systems.SetResourceFlowEnabled(1, false); Step(host); var blocked = Read(world);
        Near(blocked[1], first[1], "Flow revision removes cached route"); Near(blocked[2], 1.8, "Other region remains live");
        systems.SetResourceFlowEnabled(1, true); Step(host); var reopened = Read(world);
        Near(reopened[1], .6125, "Flow revision restores complete shared route");
        world.Dock(world.Stamp, 1, 2, 3); Step(host); var docked = Read(world);
        Near(docked[1], .4625, "Topology revision rebinds all engines to merged pool");
        var stamp = world.Stamp; var before = Read(world);
        using (var step = world.BeginStep(stamp, .1, new OwnedRuntimeInput(1)))
            systems.Step(step, new OwnedRuntimeEnvironment("vacuum", 0, 0, 0, 300, new Vec(), new Vec()));
        Need(world.Stamp.Matches(stamp), "Uncommitted resource fold preserves stamp");
        var after = Read(world); for (int i = 0; i < before.Length; i++) Need(BitConverter.DoubleToInt64Bits(before[i]) == BitConverter.DoubleToInt64Bits(after[i]), "Uncommitted quantities unchanged");
    }
    static void Rejection()
    {
        var world = World(); var wheel = new OwnedRuntimeActuatorSpec(0, OwnedRuntimeActuatorKind.ReactionWheel, new Vec(), new Vec(1, 0, 0), 1);
        bool duplicate = false; try { new OwnedRuntimeSystems(world, new[] { wheel, wheel }, Array.Empty<OwnedRuntimeSurfaceSpec>(), wheelProviderSlots: new[] { 0, 0 }); } catch (ArgumentException) { duplicate = true; }
        Need(duplicate, "Duplicate wheel reservoir rejected");
        var mixed = new OwnedRuntimeSystems(world, new[] { Engine(0), wheel }, Array.Empty<OwnedRuntimeSurfaceSpec>(), wheelProviderSlots: new[] { -1, 0 });
        Need(mixed.ActuatorCount == 2, "Nonwheel prefix does not claim a reservoir");
        var directWorld = World(); var direct = new OwnedRuntimeSystems(directWorld, new[] { Engine(0, true), Engine(0, true) }, Array.Empty<OwnedRuntimeSurfaceSpec>());
        direct.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled); Step(new OwnedRuntimeHost(directWorld, direct));
        Near(Read(directWorld)[0], 0, "Direct shared singleton partial availability preserved");
        Near(Read(directWorld)[1], .75, "Direct singleton never borrows another tank");
    }
    static int Main()
    { Routes(OwnedRuntimeExecutionStrategy.Reference); Routes(OwnedRuntimeExecutionStrategy.Compiled); Rejection(); Console.WriteLine($"PASS resource routing: {checks} independent ordered, density, flow, topology, rollback and reservoir checks."); return 0; }
}
