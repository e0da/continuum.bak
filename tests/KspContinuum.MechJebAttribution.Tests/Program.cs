using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using KspContinuum;

internal static class Program
{
    class ControllerBase { public virtual void Drive(int state) { } }
    sealed class InheritedController : ControllerBase { }
    sealed class OverrideController : ControllerBase { public override void Drive(int state) { } }
    static void Main()
    {
        var inherited = typeof(InheritedController).GetMethod("Drive");
        var canonical = MechJebDeclaredDrive.Canonical(inherited);
        var overridden = MechJebDeclaredDrive.Canonical(typeof(OverrideController).GetMethod("Drive"));
        if (inherited.ReflectedType != typeof(InheritedController) || canonical.ReflectedType != typeof(ControllerBase) ||
            canonical.DeclaringType != typeof(ControllerBase) || canonical.MetadataToken != inherited.MetadataToken || canonical.Module != inherited.Module ||
            overridden.DeclaringType != typeof(OverrideController) || overridden.GetBaseDefinition() != canonical ||
            new System.Collections.Generic.HashSet<MethodInfo> { canonical, MechJebDeclaredDrive.Canonical(typeof(ControllerBase).GetMethod("Drive")), overridden }.Count != 2)
            throw new Exception("Inherited physical implementation was not canonicalized, or override identity was lost.");
        var timer = new CallbackAttributionAccumulator(1000);
        int recursive = timer.Register(new CallbackAttributionRow { assembly = "analytic", declaringType = "ControllerA", runtimeType = "InheritedControllerA", ilSha256 = "analytic-bound-il", method = "Drive", category = "controller-drive-runtime-type" });
        timer.Register(new CallbackAttributionRow { assembly = "analytic", declaringType = "ControllerB", method = "Drive", category = "controller-drive-runtime-type" });
        timer.Record(recursive, 18, false); timer.Record(recursive, 36, true);
        var rows = timer.Finish(100, true);
        var measured = rows.Single(r => r.declaringType == "ControllerA");
        if (measured.calls != 2 || measured.outermostCalls != 1 || measured.inclusiveTicks != 54 || measured.outermostInclusiveTicks != 36 || measured.outermostMeanMilliseconds != 36)
            throw new Exception("Recursive elapsed was falsely treated as disjoint work.");
        if (rows.Single(r => r.declaringType == "ControllerB").calls != 0) throw new Exception("Unobserved controller lost its zero-coverage row.");
        var report = new CallbackAttributionReport { mechJebDrive = new MechJebDriveAttributionReport {
            status = "observed", rows = rows, admittedCoreIds = new[] { -17, 42 }, observedCoreIds = new[] { 42 }, seamIdentities = new[] { "analytic:1:bound" } } };
        using (var json = JsonDocument.Parse(ReportJson.Encode(report)))
        {
            var split = json.RootElement.GetProperty("mechJebDrive");
            if (split.GetProperty("rows").GetArrayLength() != 2 || split.GetProperty("rows")[0].GetProperty("declaringType").GetString() != "ControllerA" ||
                split.GetProperty("rows")[0].GetProperty("runtimeType").GetString() != "InheritedControllerA" || split.GetProperty("rows")[0].GetProperty("ilSha256").GetString() != "analytic-bound-il" || split.GetProperty("admittedCoreIds")[0].GetInt32() != -17 ||
                split.GetProperty("observedCoreIds").GetArrayLength() != 1 || split.GetProperty("seamIdentities")[0].GetString() != "analytic:1:bound" ||
                split.GetProperty("observerOverheadStatus").GetString() != "unqualified-intrusive-observer")
                throw new Exception("Bounded report encoder lost owner/coverage or overhead evidence.");
        }
        Console.WriteLine("PASS inherited implementation canonicalization, recursive overlap, zero controller coverage and nested report serialization.");
    }
}
