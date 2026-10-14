# Retained validation lease for persistent flight

`RetainedValidationLease` schedules full observation for the persistent motion
owner. It does not certify a writer graph, perform physics, weaken a numerical
comparison or authorize dropping stock property acquisition. Diagnostic v1/v2
owners retain their original exhaustive path.

The owner consumes the policy as follows:

```csharp
var ticket = lease.Consider(completedSteps, authoritativeGeneration);
if (ticket.Required)
{
    AuditCore(); // existing complete forest/alias/native/Transform comparisons
    lease.Complete(ticket);
}
```

Cold admission performs a full audit. Thereafter cadence is exactly one or
sixteen completed native steps. Same-step, same-authoritative-generation requests
with unchanged invalidation revision do not repeat an already successful audit.
Expected own pose/property publications advance the caller's generation; they
do not invalidate the sixteen-step lease. In one-step proof mode a changed
same-step generation requires another complete audit.

Ownership uncertainty calls `Invalidate()`. Handback invalidates once, then
calls `Consider(step, generation, true)` before stock restoration. A newer
invalidation or request makes an old ticket uncommittable. A thrown audit never
calls `Complete`, so it cannot establish or renew the lease. A request beyond the
allowed step gap throws; the owner must fail/hand back rather than retroactively
claim coverage of missed steps. Fresh owners receive fresh policy instances.

Every native step still checks the admitted clock, script/native order,
authoritative aggregate liveness/flags/finite state and complete common-force
coverage. Current source mass, local COM, inertia and principal-frame acquisition
remain functional ingress. Repeated acquisitions may be coalesced only with
proved original-setter revisions, independently of this validation policy.

## Evidence and applicability

Counters distinguish requests, duplicate/clean requests, successful full audits,
audit reasons, last audited step/generation and the maximum observed step gap.
Reason counts can overlap within one audit; they must not be summed as distinct
executions. The caller records actual audit elapsed cost and unchanged source
numeric budgets. The policy allocates no per-request objects.

The maximum sixteen-step observation gap is 0.32 simulated seconds at an admitted
0.02-second step, or 1.28 at nominal LOW4's 0.08-second step. The helper's reported
maximum permitted simulated gap assumes the owner's external maximum-step guard
of 0.1 seconds (therefore an absolute sixteen-step bound of 1.6 simulated
seconds); it does not independently validate a clock. There is no wall-time
bound while paused or stalled.

A pinned Rigidbody routing graph alone does not close direct Transform writers
or native animation. This production cadence requires an explicit forest/writer
lease. Sustained off-lease drift is detected at an audit; transient changes may
influence a stock callback and be overwritten before observation. Unknown
writers must reject that lease or select the exhaustive path, rather than be
silently called compatible. Numerical tolerances remain unchanged, but temporal
observation coverage differs and must be stated in the native receipt.

The isolated checks cover cold/cadence/handback, duplicate suppression, invalidation
between request and completion, foreign/stale tickets, overdue/regressing steps
and one-step proof mode. Source checks do not establish playable cost or behavior;
the owner consumer and source-bound native session supply those gates.
