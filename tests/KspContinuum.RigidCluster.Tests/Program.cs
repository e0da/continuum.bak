using System;
using System.Threading;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Near(double expected, double actual, string message)
    { Check(Math.Abs(expected - actual) <= 1e-11 * Math.Max(1, Math.Abs(expected)), message + ": " + actual); }
    static void Reject<T>(Action action, string message) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
    static SimulationBody Body(int id, double mass, double x, double velocity, double force)
    { return new SimulationBody(id, mass, new Vec(x, 0, 0), new Vec(velocity, 0, 0), new Vec(force, 0, 0)); }

    static int Main()
    {
        var stamp = new WorkStamp(1, 2, 3);
        var input = new SimulationBatch(stamp, .5, new[] { Body(10, 2, -2, 4, 6), Body(20, 6, 2, -2, 2) });
        var solver = new RigidClusterBackend(new[] { new[] { 10, 20 } });
        SimulationBatch output = solver.Compute(input, CancellationToken.None);
        // COM starts at 1, momentum is -4, force is 8: x=.875 and v=0 after .5 s.
        Near(-2.125, output.GetPosition(0).X, "first offset preserved");
        Near(1.875, output.GetPosition(1).X, "second offset preserved");
        Near(0, output.GetVelocity(0).X, "first body uses cluster velocity");
        Near(0, output.GetVelocity(1).X, "second body uses cluster velocity");
        Near(4, output.GetPosition(1).X - output.GetPosition(0).X, "separation changed");
        Near(0, 2 * output.GetVelocity(0).X + 6 * output.GetVelocity(1).X, "momentum update wrong");
        Check(input.GetVelocity(0).X == 4 && input.GetVelocity(1).X == -2, "input mutated");
        Check(output.Stamp == stamp && output.StepSeconds == .5, "batch identity changed");

        var islands = new RigidClusterBackend(new[] { new[] { 10 }, new[] { 20 } }).Compute(input, CancellationToken.None);
        Near(-2 + 4 * .5 + .5 * 3 * .25, islands.GetPosition(0).X, "first island integration");
        Near(2 - 2 * .5 + .5 * (2.0 / 6) * .25, islands.GetPosition(1).X, "second island integration");

        var repeat = solver.Compute(input, CancellationToken.None);
        for (int i = 0; i < output.Count; i++)
        {
            Near(output.GetPosition(i).X, repeat.GetPosition(i).X, "repeat position differs");
            Near(output.GetVelocity(i).X, repeat.GetVelocity(i).X, "repeat velocity differs");
        }
        var automatic = new RigidClusterBackend().Compute(input, CancellationToken.None);
        for (int i = 0; i < output.Count; i++)
        {
            Near(output.GetPosition(i).X, automatic.GetPosition(i).X, "automatic cluster position differs");
            Near(output.GetVelocity(i).X, automatic.GetVelocity(i).X, "automatic cluster velocity differs");
        }

        Reject<ArgumentException>(() => new RigidClusterBackend(null), "null clusters accepted");
        Reject<ArgumentException>(() => new RigidClusterBackend(new int[0][]), "empty clusters accepted");
        Reject<ArgumentException>(() => new RigidClusterBackend(new[] { new int[0] }), "empty cluster accepted");
        Reject<ArgumentException>(() => new RigidClusterBackend(new[] { new[] { 10 }, new[] { 10 } }), "duplicate body accepted");
        Reject<InvalidOperationException>(() => new RigidClusterBackend(new[] { new[] { 10 } }).Compute(input, CancellationToken.None), "unassigned body accepted");
        Reject<InvalidOperationException>(() => new RigidClusterBackend(new[] { new[] { 10, 30 } }).Compute(input, CancellationToken.None), "missing body accepted");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(() => solver.Compute(input, cancelled.Token), "cancellation ignored");
        checks += RetainedPropertiesChecks.Run();
        Console.WriteLine("PASS " + checks + " rigid-cluster assertions");
        return 0;
    }
}
