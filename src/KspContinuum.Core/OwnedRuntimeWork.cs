using System;
using System.Diagnostics;
using System.Threading;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedRuntimeWork
    {
        public long BankCopies, CopiedRegionRows, CopiedMemberReferences, CopiedResourceRows, CopiedHistoryRows;
        public long MassRefreshes, MassResourceRows, ReducerMemberVisits, BankValidations, ValidationResourceRows;
        public long CopyTicks, MassTicks, ValidationTicks, ClockFrequency;
        public bool ObserveClock;
        internal long Begin(){return ObserveClock?Stopwatch.GetTimestamp():0;}
        internal void Copy(int regions,int members,int resources,int history,long start)
        {Interlocked.Increment(ref BankCopies);Interlocked.Add(ref CopiedRegionRows,regions);Interlocked.Add(ref CopiedMemberReferences,members);Interlocked.Add(ref CopiedResourceRows,resources);Interlocked.Add(ref CopiedHistoryRows,history);if(start!=0)Interlocked.Add(ref CopyTicks,Stopwatch.GetTimestamp()-start);}
        internal void Mass(int resources,long start)
        {Interlocked.Increment(ref MassRefreshes);Interlocked.Add(ref MassResourceRows,resources);if(start!=0)Interlocked.Add(ref MassTicks,Stopwatch.GetTimestamp()-start);}
        internal void Validate(int resources,long start)
        {Interlocked.Increment(ref BankValidations);Interlocked.Add(ref ValidationResourceRows,resources);if(start!=0)Interlocked.Add(ref ValidationTicks,Stopwatch.GetTimestamp()-start);}
        internal OwnedRuntimeWork Read()
        {return new OwnedRuntimeWork{BankCopies=Interlocked.Read(ref BankCopies),CopiedRegionRows=Interlocked.Read(ref CopiedRegionRows),CopiedMemberReferences=Interlocked.Read(ref CopiedMemberReferences),CopiedResourceRows=Interlocked.Read(ref CopiedResourceRows),CopiedHistoryRows=Interlocked.Read(ref CopiedHistoryRows),MassRefreshes=Interlocked.Read(ref MassRefreshes),MassResourceRows=Interlocked.Read(ref MassResourceRows),ReducerMemberVisits=Interlocked.Read(ref ReducerMemberVisits),BankValidations=Interlocked.Read(ref BankValidations),ValidationResourceRows=Interlocked.Read(ref ValidationResourceRows),CopyTicks=Interlocked.Read(ref CopyTicks),MassTicks=Interlocked.Read(ref MassTicks),ValidationTicks=Interlocked.Read(ref ValidationTicks),ClockFrequency=Stopwatch.Frequency,ObserveClock=ObserveClock};}
    }
}
