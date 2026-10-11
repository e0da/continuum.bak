using System;
using System.Collections.Generic;

namespace KspContinuum
{
    [Serializable] public sealed class CallbackAttributionReport
    {
        public string schema = "ksp-continuum-fixed-callback-attribution/v1";
        public string status = "not-started", cleanupStatus = "not-installed", detail;
        public string measurementScope = "Inclusive elapsed wall time for patched managed MonoBehaviour FixedUpdate methods while Unity's ScriptRunBehaviourFixedUpdate subtree is active. Rows can overlap when callbacks invoke one another and must not be summed blindly.";
        public string coverageScope = "Loaded, non-dynamic assemblies referencing Unity and patchable zero-argument instance void FixedUpdate methods declared by MonoBehaviour types.";
        public long clockFrequency, timerReadFloorTicks, activeWindowTicks;
        public int discoveredMethods, patchedMethods, scanErrors, callbackErrors;
        public CallbackAttributionRow[] callbacks;
        public FeedInputAttributionReport feedInput;
        public MechJebDriveAttributionReport mechJebDrive;
    }

    [Serializable] public sealed class MechJebDriveAttributionReport
    {
        public string schema = "continuum-mechjeb-drive-attribution/v1", status = "not-started", detail, cleanupStatus = "not-installed", assemblySha256;
        public string scope = "Observer-lifetime fixed-subtree inclusive wall envelopes. Update and AnalyzeParts are separated inside/outside the matching Core.Drive; Update includes its existing UT cache hits. Controller rows group runtime type and matched Core identity during Drive, including nested controller calls. Original master/Enabled branches, ordering, exceptions and side effects remain untouched. Rows overlap; outermost means recursion of that row only, not exclusive CPU. Harmony, reflection, counters and child observer overhead remain unqualified; timer floor is not total observer tax.";
        public string[] seamIdentities;
        public int[] admittedCoreIds, observedCoreIds;
        public int patchedMethods, controllerTypes, errors;
        public string observerOverheadStatus = "unqualified-intrusive-observer";
        public long clockFrequency, admissionStartedTicks, admissionEndedTicks;
        public CallbackAttributionRow[] rows;
    }

    [Serializable] public sealed class FeedInputAttributionReport
    {
        public string status = "not-started", vesselId, seamIdentity, cleanupStatus, patchOwner;
        public string coverage = "Four pinned original multicast callsites of the captured active vessel; ordered subscribers captured before instrumentation and at disposal. Handler timing requires registered method and exact registered target identity in that chain; static targets are null. Unobserved registered methods retain explicit zero rows and are not execution evidence. Other vessels and calls outside the fixed subtree are excluded. Per-target costs are not separated when registered targets share a method.";
        public string scope = "Observer-lifetime fixed-subtree inclusive multicast and registered-handler timings. Original multicast Invoke order and exceptions retained. Chain, handler, FeedInputFeed and FixedUpdate rows overlap; no exclusive CPU or exact Probe-window claim. Handler rows aggregate identical methods/targets within each chain.";
        public long snapshotStartTicks, snapshotEndTicks;
        public int driftErrors, contextErrors;
        public InputDelegateChain[] chains;
    }
    [Serializable] public sealed class InputDelegateChain
    {
        public string name; public int calls, errors, registeredMethods, observedMethods, unobservedMethods; public long inclusiveTicks, maximumTicks;
        public bool snapshotUnchanged;
        public InputDelegateMember[] members, endMembers;
        public CallbackAttributionRow[] handlers;
    }
    [Serializable] public sealed class InputDelegateMember
    {
        public int order, targetId, targetUnityId, metadataToken;
        public string targetType, assembly, assemblyMvid, assemblySha256, declaringType, method, ilSha256;
    }

    [Serializable] public sealed class CallbackAttributionRow
    {
        public string category, assembly, assemblyVersion, assemblyMvid, declaringType, method;
        public int metadataToken, calls, outermostCalls;
        public long inclusiveTicks, maximumTicks, outermostInclusiveTicks, outermostMaximumTicks;
        public double inclusiveMilliseconds, meanMilliseconds, maximumMilliseconds, fractionOfActiveWindow;
        public double outermostInclusiveMilliseconds, outermostMeanMilliseconds, outermostMaximumMilliseconds,
            outermostFractionOfActiveWindow;
    }

    public sealed class CallbackAttributionAccumulator
    {
        sealed class Slot
        {
            public readonly CallbackAttributionRow Row;
            public Slot(CallbackAttributionRow row) { Row = row; }
        }
        readonly long frequency;
        readonly List<Slot> slots = new List<Slot>();
        bool finished;

        public CallbackAttributionAccumulator(long frequency)
        {
            if (frequency <= 0) throw new ArgumentOutOfRangeException("frequency");
            this.frequency = frequency;
        }

        public int Register(CallbackAttributionRow row)
        {
            if (finished || row == null || string.IsNullOrEmpty(row.assembly) || string.IsNullOrEmpty(row.declaringType) ||
                string.IsNullOrEmpty(row.method)) throw new ArgumentException("Invalid callback attribution row.");
            slots.Add(new Slot(row)); return slots.Count - 1;
        }

        public void Record(int slot, long elapsedTicks, bool outermost)
        {
            if (finished || slot < 0 || slot >= slots.Count || elapsedTicks < 0) throw new ArgumentException("Invalid callback timing sample.");
            CallbackAttributionRow row = slots[slot].Row;
            row.calls++;
            checked { row.inclusiveTicks += elapsedTicks; }
            if (elapsedTicks > row.maximumTicks) row.maximumTicks = elapsedTicks;
            if (outermost)
            {
                row.outermostCalls++;
                checked { row.outermostInclusiveTicks += elapsedTicks; }
                if (elapsedTicks > row.outermostMaximumTicks) row.outermostMaximumTicks = elapsedTicks;
            }
        }

        public CallbackAttributionRow[] Finish(long activeWindowTicks, bool includeZeroCalls = false)
        {
            if (finished || activeWindowTicks < 0) throw new ArgumentException("Invalid callback attribution completion.");
            finished = true;
            var rows = new List<CallbackAttributionRow>();
            foreach (Slot slot in slots)
            {
                CallbackAttributionRow row = slot.Row;
                if (row.calls == 0 && !includeZeroCalls) continue;
                row.inclusiveMilliseconds = row.inclusiveTicks * (1000.0 / frequency);
                row.meanMilliseconds = row.calls == 0 ? 0 : row.inclusiveMilliseconds / row.calls;
                row.maximumMilliseconds = row.maximumTicks * (1000.0 / frequency);
                row.fractionOfActiveWindow = activeWindowTicks == 0 ? 0 : (double)row.inclusiveTicks / activeWindowTicks;
                row.outermostInclusiveMilliseconds = row.outermostInclusiveTicks * (1000.0 / frequency);
                row.outermostMeanMilliseconds = row.outermostCalls == 0 ? 0 : row.outermostInclusiveMilliseconds / row.outermostCalls;
                row.outermostMaximumMilliseconds = row.outermostMaximumTicks * (1000.0 / frequency);
                row.outermostFractionOfActiveWindow = activeWindowTicks == 0 ? 0 : (double)row.outermostInclusiveTicks / activeWindowTicks;
                rows.Add(row);
            }
            rows.Sort((left, right) => right.outermostInclusiveTicks.CompareTo(left.outermostInclusiveTicks));
            return rows.ToArray();
        }
    }
}
