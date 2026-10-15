using System;

namespace KspContinuum
{
    [Flags]
    public enum RetainedValidationReason { None = 0, Cold = 1, Cadence = 2, Invalidation = 4, Handback = 8 }

    public struct RetainedValidationAudit
    {
        public readonly bool Required;
        public readonly long Step, Generation, InvalidationGeneration;
        public readonly RetainedValidationReason Reason;
        internal readonly RetainedValidationLease Owner;
        internal RetainedValidationAudit(RetainedValidationLease owner, bool required, long step, long generation, long invalidation, RetainedValidationReason reason)
        { Owner = owner; Required = required; Step = step; Generation = generation; InvalidationGeneration = invalidation; Reason = reason; }
    }

    // Schedules observation only. It does not certify writers or replace physical ingress.
    public sealed class RetainedValidationLease
    {
        public int Cadence { get; private set; }
        public long Requests { get; private set; }
        public long FullAudits { get; private set; }
        public long DuplicateRequests { get; private set; }
        public long CleanRequests { get; private set; }
        public long ColdAudits { get; private set; }
        public long CadenceAudits { get; private set; }
        public long InvalidationAudits { get; private set; }
        public long HandbackAudits { get; private set; }
        public long MaximumStepGap { get; private set; }
        public long LastAuditedStep { get; private set; }
        public long LastAuditedGeneration { get; private set; }
        public long InvalidationGeneration { get; private set; }
        public bool HasAudit { get { return FullAudits != 0; } }
        public bool Invalidated { get { return InvalidationGeneration != auditedInvalidation; } }
        public double MaximumPermittedSimulatedGapSeconds { get { return Cadence * .1; } }
        long auditedInvalidation, consideredStep = -1, consideredGeneration = -1;

        public RetainedValidationLease(int cadence)
        {
            if (cadence != 1 && cadence != 16) throw new ArgumentException("Validation cadence must be one or sixteen completed steps.");
            Cadence = cadence; LastAuditedStep = LastAuditedGeneration = -1;
        }
        public void Invalidate() { InvalidationGeneration = checked(InvalidationGeneration + 1); }

        public RetainedValidationAudit Consider(long completedSteps, long authoritativeGeneration, bool handback = false)
        {
            if (completedSteps < 0 || authoritativeGeneration < 0 || completedSteps < consideredStep)
                throw new ArgumentException("Validation step/generation must be nonnegative and steps must not regress.");
            long gap = HasAudit ? completedSteps - LastAuditedStep : 0;
            if (HasAudit && gap > Cadence) throw new InvalidOperationException("Retained validation cadence exceeded; ownership must fail rather than retroactively qualify the gap.");
            long requests = checked(Requests + 1);
            bool duplicate = HasAudit && completedSteps == LastAuditedStep && authoritativeGeneration == LastAuditedGeneration && !Invalidated;
            RetainedValidationReason reason = RetainedValidationReason.None;
            if (!duplicate)
            {
                if (!HasAudit) reason |= RetainedValidationReason.Cold;
                if (Invalidated) reason |= RetainedValidationReason.Invalidation;
                if (handback) reason |= RetainedValidationReason.Handback;
                if (HasAudit && (gap >= Cadence || Cadence == 1 && authoritativeGeneration != LastAuditedGeneration)) reason |= RetainedValidationReason.Cadence;
            }
            long duplicates = DuplicateRequests, clean = CleanRequests;
            if (duplicate) duplicates = checked(duplicates + 1);
            else if (reason == RetainedValidationReason.None) clean = checked(clean + 1);
            Requests = requests; DuplicateRequests = duplicates; CleanRequests = clean;
            MaximumStepGap = Math.Max(MaximumStepGap, gap); consideredStep = completedSteps; consideredGeneration = authoritativeGeneration;
            return new RetainedValidationAudit(this, reason != RetainedValidationReason.None, completedSteps, authoritativeGeneration, InvalidationGeneration, reason);
        }

        public void Complete(RetainedValidationAudit audit)
        {
            if (!ReferenceEquals(audit.Owner, this) || !audit.Required || audit.Step != consideredStep || audit.Generation != consideredGeneration || audit.InvalidationGeneration != InvalidationGeneration)
                throw new InvalidOperationException("Only the latest current required audit may commit.");
            if (HasAudit && audit.Step == LastAuditedStep && audit.Generation == LastAuditedGeneration && audit.InvalidationGeneration == auditedInvalidation)
                throw new InvalidOperationException("Audit already committed.");
            long all = checked(FullAudits + 1);
            long cold = ColdAudits, cadence = CadenceAudits, invalidation = InvalidationAudits, handback = HandbackAudits;
            if ((audit.Reason & RetainedValidationReason.Cold) != 0) cold = checked(cold + 1);
            if ((audit.Reason & RetainedValidationReason.Cadence) != 0) cadence = checked(cadence + 1);
            if ((audit.Reason & RetainedValidationReason.Invalidation) != 0) invalidation = checked(invalidation + 1);
            if ((audit.Reason & RetainedValidationReason.Handback) != 0) handback = checked(handback + 1);
            FullAudits = all; ColdAudits = cold; CadenceAudits = cadence; InvalidationAudits = invalidation; HandbackAudits = handback;
            LastAuditedStep = audit.Step; LastAuditedGeneration = audit.Generation; auditedInvalidation = audit.InvalidationGeneration;
        }
    }
}
