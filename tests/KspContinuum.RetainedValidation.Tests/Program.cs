using System;
using KspContinuum;
static class Program
{
    static void Check(bool value) { if (!value) throw new Exception("Retained validation contract failed."); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } throw new Exception("Invalid audit admitted."); }
    static void Main()
    {
        Reject(() => new RetainedValidationLease(0)); Reject(() => new RetainedValidationLease(2));
        var p = new RetainedValidationLease(16); var cold = p.Consider(0, 1); Check(cold.Required && cold.Reason == RetainedValidationReason.Cold);
        // A failed host audit never calls Complete and cannot establish the lease.
        Check(!p.HasAudit); p.Complete(p.Consider(0, 1)); Check(p.HasAudit && p.FullAudits == 1);
        Check(!p.Consider(0, 1, true).Required); Check(p.DuplicateRequests == 1);
        for (int step = 1; step < 16; step++) Check(!p.Consider(step, step + 1).Required);
        var due = p.Consider(16, 17); Check(due.Required && due.Reason == RetainedValidationReason.Cadence); p.Complete(due);
        Check(p.MaximumStepGap == 16 && p.MaximumPermittedSimulatedGapSeconds == 1.6);
        p.Invalidate(); var invalid = p.Consider(16, 17); Check(invalid.Required && invalid.Reason == RetainedValidationReason.Invalidation);
        p.Invalidate(); Reject(() => p.Complete(invalid)); Check(p.FullAudits == 2 && p.Invalidated);
        p.Complete(p.Consider(16, 17)); Check(!p.Invalidated && p.InvalidationAudits == 1);
        var end = p.Consider(17, 18, true); Check(end.Required && end.Reason == RetainedValidationReason.Handback); p.Complete(end);
        Reject(() => p.Complete(end)); Reject(() => p.Complete(new RetainedValidationLease(16).Consider(0, 0)));
        long requests = p.Requests; Reject(() => p.Consider(16, 18)); Reject(() => p.Consider(18, -1)); Check(p.Requests == requests);
        Reject(() => p.Consider(34, 35)); Check(p.LastAuditedStep == 17 && p.MaximumStepGap == 16);
        var proof = new RetainedValidationLease(1); proof.Complete(proof.Consider(0, 0));
        for (int step = 1; step <= 4; step++) { proof.Complete(proof.Consider(step, step)); Check(!proof.Consider(step, step).Required); }
        proof.Complete(proof.Consider(4, 5)); Check(proof.FullAudits == 6 && proof.MaximumStepGap == 1 && proof.MaximumPermittedSimulatedGapSeconds == .1);
        Console.WriteLine("Retained audit cold/cadence/invalidation/handback, duplicate, stale-ticket and gap checks passed.");
    }
}
