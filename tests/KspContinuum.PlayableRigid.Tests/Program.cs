using System;
using KspContinuum;
static class Program
{
    static void Check(bool x) { if (!x) throw new Exception("Lifecycle contract failed."); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } throw new Exception("Invalid transition accepted."); }
    static void Main()
    {
        var p = new PlayableRigidLifecycle(); p.ObserveTarget("vessel-A", 0, 0); p.SetEnabled(true, 0);
        Check(p.Evaluate(true, .5) == PlayableRigidAction.None); p.SetPaused(false, .9); p.SetEnabled(true, .9); Check(p.Evaluate(true, 1) == PlayableRigidAction.Acquire); p.Acquired();
        Reject(() => p.ObserveTarget("vessel-B", 0, 2)); Check(p.Owned && p.Epoch == 1);
        Reject(() => p.Released(null, 2)); Check(p.Owned); p.SetPaused(true, 2); Check(p.Evaluate(true, 2) == PlayableRigidAction.Handback); p.Released("pause", 2);
        p.SetPaused(false, 3); Check(p.Evaluate(true, 3) == PlayableRigidAction.None); p.Acquired();
        p.Boundary("quicksave", 4); p.Released("quicksave", 4); Check(p.Evaluate(true, 4.5) == PlayableRigidAction.None);
        p.Fault("unsupported-contact"); Check(p.Evaluate(true, 100) == PlayableRigidAction.None); p.Retry(101); Check(p.Evaluate(true, 102) == PlayableRigidAction.Acquire);
        p.ObserveTarget("vessel-B", 1, 103); Check(p.Epoch == 2); p.Acquired(); p.SetEnabled(false, 104); Check(p.Evaluate(true, 104) == PlayableRigidAction.Handback);
        p.Released("disable", 104); Check(p.Evaluate(true, 200) == PlayableRigidAction.None); p.SetEnabled(true, 201); p.Stop(202); Check(p.Evaluate(true, 400) == PlayableRigidAction.None); Reject(() => p.SetEnabled(true, 401)); Reject(() => p.ObserveTarget("late", 3, 401));
        long epoch=p.Epoch; Reject(() => p.ObserveTarget("bad", 2, double.NaN)); Check(p.Epoch==epoch); Reject(() => new PlayableRigidLifecycle(-1));
        Console.WriteLine("Playable lifecycle operator/handback/rebind/fault/recovery contracts passed.");
    }
}
