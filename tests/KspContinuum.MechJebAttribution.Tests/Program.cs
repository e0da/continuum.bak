using System;
using System.Linq;
using System.Text.Json;
using KspContinuum;

internal static class Program
{
    static void Main()
    {
        var timer = new CallbackAttributionAccumulator(1000);
        int recursive = timer.Register(new CallbackAttributionRow { assembly = "analytic", declaringType = "ControllerA", method = "Drive", category = "controller-drive-runtime-type" });
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
            if (split.GetProperty("rows").GetArrayLength() != 2 || split.GetProperty("admittedCoreIds")[0].GetInt32() != -17 ||
                split.GetProperty("observedCoreIds").GetArrayLength() != 1 || split.GetProperty("seamIdentities")[0].GetString() != "analytic:1:bound" ||
                split.GetProperty("observerOverheadStatus").GetString() != "unqualified-intrusive-observer")
                throw new Exception("Bounded report encoder lost owner/coverage or overhead evidence.");
        }
        Console.WriteLine("PASS recursive overlap, zero controller coverage and nested report serialization.");
    }
}
