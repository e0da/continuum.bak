using System;
using System.Collections.Generic;
using System.Threading;

namespace KspContinuum
{
    public struct OwnedRuntimeStep : IDisposable
    {
        internal readonly OwnedRuntimeBank Bank;
        internal readonly long Token;
        public OwnedRuntimeWorld World { get; private set; }
        public double DeltaTime { get; private set; }
        public OwnedRuntimeStamp ExpectedStamp { get; private set; }
        internal void EnsureOpen() { World.CheckStep(this); }
        internal void ClaimSystemsExecution() { World.ClaimSystemsStep(this); }
        internal void BeginMechanicalPublication(){World.CheckStep(this);if(Bank.MechanicalPublicationPending)throw new InvalidOperationException("Mechanical transaction already open.");Bank.MechanicalPublicationPending=true;}
        internal void CompleteMechanicalPublication(){World.CheckStep(this);if(!Bank.MechanicalPublicationPending)throw new InvalidOperationException("Mechanical transaction is not open.");Bank.MechanicalPublicationPending=false;}
        internal OwnedRuntimeStep(OwnedRuntimeWorld world, OwnedRuntimeBank bank, long token,
            OwnedRuntimeStamp stamp, double deltaTime)
        { World = world; Bank = bank; Token = token; ExpectedStamp = stamp; DeltaTime = deltaTime; }
        public void SetResourceQuantity(int slot, double quantity)
        {
            World.CheckStep(this); World.CheckResourcePublication(Bank, slot, quantity);
            Bank.ResourceQuantity[slot] = quantity;
        }
        public double GetResourceQuantity(int slot) { World.CheckStep(this); return Bank.ResourceQuantity[slot]; }
        public void SetTemperature(int part, double kelvin)
        { World.CheckStep(this); if(Bank.Thermal.pendingDuration!=0)throw new InvalidOperationException("Flush pending thermal energy before a temperature overwrite."); AssemblyModel.Positive(kelvin); Bank.Temperature[part] = kelvin; }
        public void SetProviderState(int slot, double value)
        { World.CheckStep(this); AssemblyModel.Finite(value); Bank.ProviderState[slot] = value; }
        public void SetRegionMotion(int region, OwnedRuntimeMotion motion)
        { World.CheckStep(this); OwnedRuntimeWorld.ValidateMotion(motion); Bank.RegionMotion[region] = motion; }
        public OwnedRuntimeRegionState GetRegion(int region)
        { World.CheckStep(this); return new OwnedRuntimeRegionState { Id = Bank.Topology.GetRegionId(region), Motion = Bank.RegionMotion[region], Properties = Bank.Properties[region] }; }
        public void RefreshMassProperties() { World.CheckStep(this); OwnedRuntimeWorld.RefreshMass(Bank, true); }
        public OwnedRuntimeStamp Commit() { return World.CommitStep(this); }
        public void Dispose() { if (World != null) World.CancelStep(this); }
    }

    public sealed class OwnedRuntimeReadLease : IDisposable
    {
        OwnedRuntimeWorld world;
        readonly OwnedRuntimeBank bank;
        int disposed;
        internal OwnedRuntimeReadLease(OwnedRuntimeWorld world, OwnedRuntimeBank bank) { this.world = world; this.bank = bank; }
        void Check() { if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("OwnedRuntimeReadLease"); }
        public OwnedRuntimeStamp Stamp { get { Check(); return bank.Stamp; } }
        public OwnedRuntimeDefinition Definition { get { Check(); return bank.Definition; } }
        public OwnedRuntimeTopology Topology { get { Check(); return bank.Topology; } }
        public int PartCount { get { Check(); return bank.Definition.PartCount; } }
        public int BodyCount { get { Check(); return bank.Definition.BodyCount; } }
        public int RegionCount { get { Check(); return bank.Topology.RegionCount; } }
        public int ResourceCount { get { Check(); return bank.Definition.ResourceCount; } }
        public OwnedRuntimeInput Input { get { Check(); return bank.Input; } }
        public OwnedRuntimeRegionState GetRegion(int region)
        { Check(); return new OwnedRuntimeRegionState { Id = bank.Topology.GetRegionId(region), Motion = bank.RegionMotion[region], Properties = bank.Properties[region] }; }
        public RigidAssemblyMember GetBody(int slot) { Check(); return bank.Members[slot]; }
        public RigidAssemblyMember GetBodyMember(int slot) { Check(); return bank.Members[slot]; }
        public double GetResourceQuantity(int slot) { Check(); return bank.ResourceQuantity[slot]; }
        public double GetTemperature(int part) { Check(); return bank.Temperature[part]; }
        public double ThermalIntegratedTime {get {Check();return bank.Thermal.integratedTime;}}
        public double ThermalPendingDuration {get {Check();return bank.Thermal.pendingDuration;}}
        public double GetProviderState(int slot) { Check(); return bank.ProviderState[slot]; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { world.Release(bank); world = null; }
        }
    }

    public sealed class OwnedRuntimeWorld
    {
        readonly object gate = new object();
        OwnedRuntimeWork work=new OwnedRuntimeWork();
        OwnedRuntimeBank[] banks;
        OwnedRuntimeBank current, writing;
        long token, activeToken, hostedToken;
        int writerThread, readLeases;
        int boundWriterThread;
        bool admitting;
        int admissionThread;
        public double LastTopologyRemovedEnergy { get; private set; }
        public OwnedRuntimeWork Work {get{lock(gate)return work.Read();}}
        public void SetWorkObservation(OwnedRuntimeStamp expected,bool enabled)
        {lock(gate){RequireMutation(expected);work.ObserveClock=enabled;}}
        public OwnedRuntimeDefinition Definition { get { return current.Definition; } }
        public OwnedRuntimeTopology Topology { get { return current.Topology; } }
        public OwnedRuntimeStamp Stamp { get { lock (gate) return current.Stamp; } }
        public int PartCount { get { return Definition.PartCount; } }
        public int BodyCount { get { return Definition.BodyCount; } }
        public int ResourceCount { get { return Definition.ResourceCount; } }
        public int RegionCount { get { return Topology.RegionCount; } }
        public bool AdaptiveMechanicalEnabled {get{lock(gate)return current.Mechanical.adaptive;}}
        public double SuggestedMechanicalInterval {get{lock(gate)return current.Mechanical.nextInterval;}}
        public double MaximumMechanicalInterval {get{lock(gate)return current.Mechanical.maximumInterval;}}
        public OwnedRuntimeMechanicalPolicy MechanicalPolicy {get{lock(gate)return current.Mechanical.Policy;}}
        public OwnedRuntimeMechanicalState Mechanical {get{lock(gate)return current.Mechanical.Clone();}}
        internal OwnedRuntimeMechanicalState MechanicalInternal {get{return current.Mechanical;}}
        internal OwnedRuntimeBank CurrentForTrial {get{return current;}}
        public int PartSlot(long id) { return Definition.PartSlot(id); }
        public int ResourceSlot(long id) { return Definition.ResourceSlot(id); }
        public int BodyOfPart(int part) { return Definition.BodySlot(Definition.GetPart(part).BodyId); }

        public OwnedRuntimeWorld(OwnedRuntimeDefinition definition, OwnedRuntimeTopology topology,
            OwnedRuntimeMotion initialMotion, double time = 0, string frameId = "inertial/metres")
        {
            if (definition == null || topology == null || !ReferenceEquals(definition, topology.Definition))
                throw new ArgumentException("Topology must be compiled from the exact definition.");
            ValidateMotion(initialMotion);
            banks = NewBanks(definition, topology); current = banks[0];
            current.Stamp = new OwnedRuntimeStamp(0, 0, topology.Revision, definition.Revision, 0, time, frameId);
            current.Thermal.integratedTime=time;
            for (int b = 0; b < definition.BodyCount; b++) current.Members[b] = ReId(definition.GetBody(b).Member, b);
            for (int i = 0; i < definition.ResourceCount; i++) current.ResourceQuantity[i] = definition.GetResource(i).InitialQuantity;
            for (int i = 0; i < definition.PartCount; i++) current.Temperature[i] = definition.GetPart(i).InitialTemperature;
            RefreshMass(current, false);
            for (int r = 0; r < topology.RegionCount; r++) current.RegionMotion[r] = initialMotion;
            ValidateBank(current); banks[1].CopyFrom(current); banks[2].CopyFrom(current);
            EnsurePropertyOwners(banks[1]); EnsurePropertyOwners(banks[2]);
        }

        OwnedRuntimeBank[] NewBanks(OwnedRuntimeDefinition definition, OwnedRuntimeTopology topology)
        { return new[] { new OwnedRuntimeBank(definition, topology){Work=work}, new OwnedRuntimeBank(definition, topology){Work=work}, new OwnedRuntimeBank(definition, topology){Work=work} }; }

        public OwnedRuntimeStep BeginStep(OwnedRuntimeStamp expected, double deltaTime, OwnedRuntimeInput input)
        { return BeginStepCore(expected, deltaTime, input, false); }
        internal OwnedRuntimeStep BeginHostedStep(OwnedRuntimeStamp expected, double deltaTime, OwnedRuntimeInput input)
        { return BeginStepCore(expected, deltaTime, input, true); }
        OwnedRuntimeStep BeginStepCore(OwnedRuntimeStamp expected, double deltaTime, OwnedRuntimeInput input, bool hosted)
        {
            AssemblyModel.Positive(deltaTime); input.Validate();
            lock (gate)
            {
                RequireWriterThread();RequireCurrent(expected); if (writing != null || hostedToken != 0 || admitting) throw new InvalidOperationException("A world step, hosted schedule, or admission already owns mutation.");
                OwnedRuntimeBank available = null;
                foreach (var bank in banks) if (!ReferenceEquals(bank, current) && bank.Readers == 0) { available = bank; break; }
                if (available == null) throw new OwnedRuntimeReadPressureException("All retained write banks are leased by readers.");
                available.CopyFrom(current); available.Input = input; available.SystemsExecuted = false;
                writing = available; activeToken = checked(++token); writerThread = Thread.CurrentThread.ManagedThreadId;
                if (hosted) hostedToken = activeToken;
                return new OwnedRuntimeStep(this, available, activeToken, expected, deltaTime);
            }
        }

        public OwnedRuntimeReadLease AcquireRead()
        {
            lock (gate)
            {
                if (readLeases >= 64) throw new OwnedRuntimeReadPressureException("Read lease bound exceeded.");
                current.Readers++; readLeases++; return new OwnedRuntimeReadLease(this, current);
            }
        }
        internal void Release(OwnedRuntimeBank bank)
        { lock (gate) { if (bank.Readers <= 0 || readLeases <= 0) throw new InvalidOperationException("Read lease accounting underflow."); bank.Readers--; readLeases--; } }
        public bool TryAcquireRead(OwnedRuntimeStamp expected,out OwnedRuntimeReadLease lease)
        {lock(gate){lease=null;if(!current.Stamp.Matches(expected))return false;if(readLeases>=64)throw new OwnedRuntimeReadPressureException("Read lease bound exceeded.");current.Readers++;readLeases++;lease=new OwnedRuntimeReadLease(this,current);return true;}}
        public void BindWriterThread()
        {lock(gate){int thread=Thread.CurrentThread.ManagedThreadId;if(boundWriterThread!=0&&boundWriterThread!=thread)throw new InvalidOperationException("World writer already bound to another thread.");if(writing!=null||hostedToken!=0||admitting)throw new InvalidOperationException("Cannot bind world during active mutation.");boundWriterThread=thread;}}
        internal void RequireWriterThread()
        {if(boundWriterThread!=0&&boundWriterThread!=Thread.CurrentThread.ManagedThreadId)throw new InvalidOperationException("World mutation belongs to its bound writer thread.");}
        internal void CheckStep(OwnedRuntimeStep step)
        {
            if (!ReferenceEquals(step.World, this) || writing == null || !ReferenceEquals(writing, step.Bank)
                || activeToken != step.Token || writerThread != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Step is stale, closed, foreign, or accessed by a different writer.");
        }
        internal void ClaimSystemsStep(OwnedRuntimeStep step)
        {
            CheckStep(step);
            if (step.Bank.SystemsExecuted) throw new InvalidOperationException("Systems already advanced this transaction's physical interval.");
            step.Bank.SystemsExecuted = true;
        }
        internal OwnedRuntimeStamp CommitStep(OwnedRuntimeStep step)
        { return CommitStepCore(step, false); }
        internal OwnedRuntimeStamp CommitHostedStep(OwnedRuntimeStep step)
        { return CommitStepCore(step, true); }
        OwnedRuntimeStamp CommitStepCore(OwnedRuntimeStep step, bool hosted)
        {
            lock (gate)
            {
                CheckStep(step); RequireCurrent(step.ExpectedStamp);
                if ((hostedToken != 0 && (!hosted || hostedToken != step.Token)) || (hosted && hostedToken != step.Token))
                    throw new InvalidOperationException("Only the runtime host may publish its scheduled transaction.");
                try
                {
                    if(writing.MechanicalPublicationPending)throw new InvalidOperationException("Unfinished mechanical transaction cannot publish scalar-only state.");
                    for (int i = 0; i < writing.ResourceQuantity.Length; i++) CheckResourcePublication(writing, i, writing.ResourceQuantity[i]);
                    Interlocked.Add(ref work.ValidationResourceRows,writing.ResourceQuantity.Length);
                    RefreshMass(writing, true); ValidateBank(writing);
                    double nextTime = current.Stamp.Time + step.DeltaTime; AssemblyModel.Finite(nextTime);
                    if (!(nextTime > current.Stamp.Time)) throw new ArgumentException("Step must advance representable physical time.");
                    if(!writing.SystemsExecuted)
                    {if(writing.Thermal.pendingDuration!=0)throw new InvalidOperationException("Manual motion step cannot skip pending thermal evolution.");writing.Thermal.integratedTime=nextTime;writing.Thermal.passiveFrozenSeconds+=step.DeltaTime;}
                    writing.Thermal.ValidateClock(nextTime);
                    var stamp = new OwnedRuntimeStamp(current.Stamp.Epoch, checked(current.Stamp.StateRevision + 1),
                        current.Stamp.TopologyRevision, current.Stamp.DefinitionRevision, current.Stamp.FrameRevision, nextTime, current.Stamp.FrameId);
                    writing.History[writing.HistoryHead] = new OwnedRuntimeInputRecord {
                        Sequence = stamp.StateRevision, StartedAt = current.Stamp.Time, DeltaTime = step.DeltaTime, Input = writing.Input };
                    writing.HistoryHead = (writing.HistoryHead + 1) % writing.History.Length;
                    if (writing.HistoryCount < writing.History.Length) writing.HistoryCount++;
                    writing.Stamp = stamp; current = writing; writing = null; activeToken = 0; return stamp;
                }
                catch { writing = null; activeToken = 0; throw; }
            }
        }
        internal void CancelStep(OwnedRuntimeStep step)
        {
            lock (gate)
            {
                if (ReferenceEquals(step.World, this) && ReferenceEquals(step.Bank, writing) && activeToken == step.Token)
                { if (writerThread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Only the admitted writer can cancel its step."); writing = null; activeToken = 0; }
            }
        }
        internal void EndHostedStep(OwnedRuntimeStep step)
        {
            lock (gate)
            {
                if (!ReferenceEquals(step.World, this) || hostedToken != step.Token || writerThread != Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("Only the admitted host can close its schedule scope.");
                if (ReferenceEquals(writing, step.Bank) && activeToken == step.Token) { writing = null; activeToken = 0; }
                hostedToken = 0;
            }
        }
        void RequireCurrent(OwnedRuntimeStamp expected)
        { if (!current.Stamp.Matches(expected)) throw new InvalidOperationException("World revision/frame/time is stale."); }
        void RequireMutation(OwnedRuntimeStamp expected)
        { RequireWriterThread();RequireCurrent(expected); if (writing != null || hostedToken != 0 || admitting) throw new InvalidOperationException("Topology/frame/definition cannot change during a step, hosted schedule, or admission."); }
        internal OwnedRuntimeStamp MutateThermal(OwnedRuntimeStamp expected,Action<OwnedRuntimeBank> mutation)
        {lock(gate){RequireMutation(expected);var candidate=new OwnedRuntimeBank(current.Definition,current.Topology);candidate.CopyFrom(current);mutation(candidate);ValidateBank(candidate);candidate.Thermal.ValidateClock(expected.Time);candidate.Stamp=new OwnedRuntimeStamp(expected.Epoch,checked(expected.StateRevision+1),expected.TopologyRevision,expected.DefinitionRevision,expected.FrameRevision,expected.Time,expected.FrameId);PublishCold(candidate);return current.Stamp;}}
        public OwnedRuntimeThermalPolicy ThermalPolicy {get{lock(gate)return current.Thermal.Policy;}}
        internal OwnedRuntimeStamp MutateMechanical(OwnedRuntimeStamp expected,OwnedRuntimeMechanicalPolicy policy)
        {
            if(policy==null)throw new ArgumentNullException("policy");lock(gate)
            {
                RequireMutation(expected);var candidate=new OwnedRuntimeBank(current.Definition,current.Topology);candidate.CopyFrom(current);candidate.Mechanical.Apply(policy);ValidateBank(candidate);
                candidate.Stamp=new OwnedRuntimeStamp(expected.Epoch,checked(expected.StateRevision+1),expected.TopologyRevision,expected.DefinitionRevision,expected.FrameRevision,expected.Time,expected.FrameId);PublishCold(candidate);return current.Stamp;
            }
        }
        internal OwnedRuntimeWorld CreateTrialWorld()
        {
            lock(gate)
            {
                RequireMutation(current.Stamp);var trial=new OwnedRuntimeWorld(current.Definition,current.Topology,current.RegionMotion[0],current.Stamp.Time,current.Stamp.FrameId);
                trial.work=work;foreach(var bank in trial.banks)bank.Work=work;
                ResetTrial(trial);if(boundWriterThread!=0)trial.BindWriterThread();return trial;
            }
        }
        internal void ResetTrial(OwnedRuntimeWorld trial)
        {
            if(trial==null||ReferenceEquals(trial,this))throw new ArgumentException("Trial owner differs.");lock(gate)lock(trial.gate)
            {
                RequireWriterThread();trial.RequireMutation(trial.current.Stamp);
                if(!ReferenceEquals(current.Definition,trial.current.Definition)||!ReferenceEquals(current.Topology,trial.current.Topology))throw new InvalidOperationException("Trial topology/definition must be recompiled.");
                foreach(var bank in trial.banks)
                {if(bank.Readers!=0)throw new InvalidOperationException("Private trial bank leaked a reader.");bank.CopyFrom(current);}
                trial.current=trial.banks[0];
            }
        }
        internal OwnedRuntimeStamp PublishAdaptive(OwnedRuntimeStamp expected,OwnedRuntimeWorld trial,double interval,OwnedRuntimeInput input,double error,double next,long rejected,long trials)
        {
            lock(gate)lock(trial.gate)
            {
                RequireWriterThread();RequireCurrent(expected);
                if(!admitting||admissionThread!=Thread.CurrentThread.ManagedThreadId||writing!=null||hostedToken!=0)throw new InvalidOperationException("Adaptive publication lacks its exclusive admission.");
                if(!ReferenceEquals(current.Definition,trial.current.Definition)||!ReferenceEquals(current.Topology,trial.current.Topology)||trial.current.Stamp.Time!=expected.Time+interval)
                    throw new InvalidOperationException("Refined trial time/ownership differs.");
                OwnedRuntimeBank candidate=null;foreach(var bank in banks)if(!ReferenceEquals(bank,current)&&bank.Readers==0){candidate=bank;break;}
                if(candidate==null)throw new OwnedRuntimeReadPressureException("No retained bank is available for adaptive publication.");
                candidate.CopyFrom(trial.current);for(int i=0;i<candidate.ResourceQuantity.Length;i++)CheckResourcePublication(candidate,i,candidate.ResourceQuantity[i]);
                Interlocked.Add(ref work.ValidationResourceRows,candidate.ResourceQuantity.Length);
                Array.Copy(current.History,candidate.History,current.History.Length);candidate.HistoryHead=current.HistoryHead;candidate.HistoryCount=current.HistoryCount;
                var stamp=new OwnedRuntimeStamp(expected.Epoch,checked(expected.StateRevision+1),expected.TopologyRevision,expected.DefinitionRevision,expected.FrameRevision,trial.current.Stamp.Time,expected.FrameId);
                candidate.Input=input;candidate.History[candidate.HistoryHead]=new OwnedRuntimeInputRecord {Sequence=stamp.StateRevision,StartedAt=expected.Time,DeltaTime=interval,Input=input};
                candidate.HistoryHead=(candidate.HistoryHead+1)%candidate.History.Length;if(candidate.HistoryCount<candidate.History.Length)candidate.HistoryCount++;
                var m=candidate.Mechanical;m.accepted=checked(current.Mechanical.accepted+1);m.rejected=checked(current.Mechanical.rejected+rejected);m.trialExecutions=checked(current.Mechanical.trialExecutions+trials);m.lastAcceptedInterval=interval;m.lastError=error;m.nextInterval=next;
                candidate.Stamp=stamp;ValidateBank(candidate);candidate.Thermal.ValidateClock(stamp.Time);current=candidate;return stamp;
            }
        }
        internal void EnterAdmission(OwnedRuntimeStamp expected)
        {
            lock (gate) { RequireMutation(expected); admitting = true; admissionThread = Thread.CurrentThread.ManagedThreadId; }
        }
        internal void ExitAdmission()
        {
            lock (gate)
            {
                if (!admitting || admissionThread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Admission scope is not owned by this thread.");
                admitting = false; admissionThread = 0;
            }
        }
        internal void CheckResourcePublication(OwnedRuntimeBank bank, int slot, double quantity)
        {
            AssemblyModel.Finite(quantity); var resource = bank.Definition.GetResource(slot);
            if (quantity < 0 || quantity > resource.QuantityLimit || (quantity > resource.Capacity && quantity > current.ResourceQuantity[slot]))
                throw new ArgumentException("Resource cannot refill above its original capacity or acquired committed surplus.");
        }

        internal static void ValidateMotion(OwnedRuntimeMotion motion)
        {
            RigidAssemblyProperties.RequireVector(motion.RootPosition); RigidAssemblyProperties.RequireVector(motion.Velocity);
            RigidAssemblyProperties.RequireVector(motion.AngularMomentum); RigidAssemblyProperties.RequireRotation(motion.Orientation);
        }

        internal static void RefreshMass(OwnedRuntimeBank bank, bool preserveMaterialMotion)
        {
            long begin=bank.Work==null?0:bank.Work.Begin();
            try
            {
            var definition = bank.Definition;
            Array.Clear(bank.BodyDirty, 0, bank.BodyDirty.Length);
            Array.Clear(bank.RegionDirty, 0, bank.RegionDirty.Length);
            for (int b = 0; b < definition.BodyCount; b++)
                if (bank.Properties[bank.Topology.BodyRegions[b]] == null) bank.BodyDirty[b] = true;
            for (int i = 0; i < definition.ResourceCount; i++)
            {
                var resource = definition.GetResource(i); double quantity = bank.ResourceQuantity[i];
                AssemblyModel.Finite(quantity);
                if (quantity < 0 || quantity > resource.QuantityLimit) throw new ArgumentException("Resource quantity is outside its declared retained domain.");
                if (quantity != bank.LastResourceQuantity[i] && resource.MassPerUnit != 0)
                    bank.BodyDirty[definition.ResourceBodies[i]] = true;
            }
            for (int b = 0; b < definition.BodyCount; b++)
            {
                if (bank.BodyDirty[b])
                {
                    bank.Members[b] = WetMember(bank, b);
                    bank.RegionDirty[bank.Topology.BodyRegions[b]] = true;
                }
            }
            Array.Copy(bank.ResourceQuantity, bank.LastResourceQuantity, bank.ResourceQuantity.Length);
            EnsurePropertyOwners(bank);
            for (int r = 0; r < bank.Topology.RegionCount; r++)
            {
                if (bank.Properties[r] == null || bank.RegionDirty[r])
                {
                    var owner = bank.PropertyOwners[r]; foreach (int b in bank.Topology.RegionBodies[r]) owner.Update(bank.Members[b]);
                    if(bank.Work!=null)Interlocked.Add(ref bank.Work.ReducerMemberVisits,bank.Topology.RegionBodies[r].Length);
                    RigidAssemblyProperties before = bank.Properties[r], after = owner.Combine();
                    RequireCondition(after);
                    if (before != null && preserveMaterialMotion)
                        OwnedRuntimeDynamics.ChangeProperties(ref bank.RegionMotion[r], before, after);
                    bank.Properties[r] = after;
                }
            }
            }
            finally{if(bank.Work!=null)bank.Work.Mass(bank.ResourceQuantity.Length,begin);}
        }
        static RigidAssemblyMember WetMember(OwnedRuntimeBank bank, int body)
        {
            var definition = bank.Definition; var dry = definition.GetBody(body).Member;
            var geometry = bank.Members[body] ?? dry;
            var total = new RigidAssemblyProperties.Sum(); total.Add(dry.Mass);
            double resourceMass = 0;
            foreach (int i in definition.BodyResources[body])
            {
                double mass = bank.ResourceQuantity[i] * definition.GetResource(i).MassPerUnit;
                AssemblyModel.Finite(mass); total.Add(mass); resourceMass += mass; AssemblyModel.Finite(resourceMass);
            }
            if (resourceMass == 0)
                return new RigidAssemblyMember(body, dry.Mass, geometry.LocalPosition, geometry.LocalRotation,
                    dry.LocalCenterOfMass, dry.PrincipalInertia, dry.PrincipalRotation);
            double wetMass = total.Value; AssemblyModel.Positive(wetMass);
            var cx = new RigidAssemblyProperties.Sum(); var cy = new RigidAssemblyProperties.Sum(); var cz = new RigidAssemblyProperties.Sum();
            double dryWeight = dry.Mass / wetMass;
            cx.Add(dry.LocalCenterOfMass.X * dryWeight); cy.Add(dry.LocalCenterOfMass.Y * dryWeight); cz.Add(dry.LocalCenterOfMass.Z * dryWeight);
            foreach (int i in definition.BodyResources[body])
            {
                double weight = (bank.ResourceQuantity[i] * definition.GetResource(i).MassPerUnit) / wetMass;
                Vec p = definition.ResourcePoints[i]; cx.Add(p.X * weight); cy.Add(p.Y * weight); cz.Add(p.Z * weight);
            }
            Vec center = new Vec(cx.Value, cy.Value, cz.Value); RigidAssemblyProperties.RequireVector(center);
            Vec ax = dry.PrincipalRotation.Rotate(new Vec(1,0,0));
            Vec ay = dry.PrincipalRotation.Rotate(new Vec(0,1,0));
            Vec az = dry.PrincipalRotation.Rotate(new Vec(0,0,1)); Vec inertia = dry.PrincipalInertia;
            var xx = new RigidAssemblyProperties.Sum(); var xy = new RigidAssemblyProperties.Sum(); var xz = new RigidAssemblyProperties.Sum();
            var yy = new RigidAssemblyProperties.Sum(); var yz = new RigidAssemblyProperties.Sum(); var zz = new RigidAssemblyProperties.Sum();
            xx.Add(inertia.X*ax.X*ax.X + inertia.Y*ay.X*ay.X + inertia.Z*az.X*az.X);
            xy.Add(inertia.X*ax.X*ax.Y + inertia.Y*ay.X*ay.Y + inertia.Z*az.X*az.Y);
            xz.Add(inertia.X*ax.X*ax.Z + inertia.Y*ay.X*ay.Z + inertia.Z*az.X*az.Z);
            yy.Add(inertia.X*ax.Y*ax.Y + inertia.Y*ay.Y*ay.Y + inertia.Z*az.Y*az.Y);
            yz.Add(inertia.X*ax.Y*ax.Z + inertia.Y*ay.Y*ay.Z + inertia.Z*az.Y*az.Z);
            zz.Add(inertia.X*ax.Z*ax.Z + inertia.Y*ay.Z*ay.Z + inertia.Z*az.Z*az.Z);
            AddPoint(dry.Mass, dry.LocalCenterOfMass + center * -1, ref xx,ref xy,ref xz,ref yy,ref yz,ref zz);
            foreach (int i in definition.BodyResources[body])
                AddPoint(bank.ResourceQuantity[i] * definition.GetResource(i).MassPerUnit,
                    definition.ResourcePoints[i] + center * -1, ref xx,ref xy,ref xz,ref yy,ref yz,ref zz);
            var tensor = new InertiaTensor6Dof(xx.Value,xy.Value,xz.Value,yy.Value,yz.Value,zz.Value);
            Vec principal; Rotation rotation; RigidAssemblyProperties.Diagonalize(tensor, out principal, out rotation);
            return new RigidAssemblyMember(body, wetMass, geometry.LocalPosition, geometry.LocalRotation, center, principal, rotation);
        }
        static void AddPoint(double mass, Vec displacement, ref RigidAssemblyProperties.Sum xx, ref RigidAssemblyProperties.Sum xy,
            ref RigidAssemblyProperties.Sum xz, ref RigidAssemblyProperties.Sum yy, ref RigidAssemblyProperties.Sum yz, ref RigidAssemblyProperties.Sum zz)
        {
            if (mass == 0) return;
            Vec u = displacement * Math.Sqrt(mass); RigidAssemblyProperties.RequireVector(u);
            xx.Add(u.Y*u.Y + u.Z*u.Z); xy.Add(-u.X*u.Y); xz.Add(-u.X*u.Z);
            yy.Add(u.X*u.X + u.Z*u.Z); yz.Add(-u.Y*u.Z); zz.Add(u.X*u.X + u.Y*u.Y);
        }
        static void EnsurePropertyOwners(OwnedRuntimeBank bank)
        {
            if (bank.PropertyOwners != null) return;
            bank.PropertyOwners = new RetainedRigidAssemblyProperties[bank.Topology.RegionCount];
            for (int r = 0; r < bank.Topology.RegionCount; r++)
            {
                var indices = bank.Topology.RegionBodies[r]; var members = new RigidAssemblyMember[indices.Length];
                for (int i = 0; i < indices.Length; i++) members[i] = bank.Members[indices[i]];
                bank.PropertyOwners[r] = new RetainedRigidAssemblyProperties(members);
            }
        }
        static void RequireCondition(RigidAssemblyProperties properties)
        { if (!(properties.PrincipalInertia.X / properties.PrincipalInertia.Z >= RigidAssemblyProjection.MinimumPrincipalMomentRatio)) throw new ArgumentException("Region inertia is ill-conditioned."); }
        static void ValidateBank(OwnedRuntimeBank bank)
        {
            long begin=bank.Work==null?0:bank.Work.Begin();try
            {
            bank.Mechanical.Validate();
            bank.Input.Validate();
            for (int r = 0; r < bank.RegionMotion.Length; r++) { ValidateMotion(bank.RegionMotion[r]); RequireCondition(bank.Properties[r]); }
            foreach (double value in bank.Temperature) AssemblyModel.Positive(value);
            bank.Thermal.Validate(bank.Temperature.Length);
            foreach (double value in bank.ProviderState) AssemblyModel.Finite(value);
            }
            finally{if(bank.Work!=null)bank.Work.Validate(0,begin);}
        }
        static RigidAssemblyMember ReId(RigidAssemblyMember member, int id)
        { return new RigidAssemblyMember(id, member.Mass, member.LocalPosition, member.LocalRotation, member.LocalCenterOfMass, member.PrincipalInertia, member.PrincipalRotation); }

        public OwnedRuntimeStamp Split(OwnedRuntimeStamp expected, long connectionId)
        { lock (gate) { RequireMutation(expected); return ChangeTopology(current.Topology.WithLocked(connectionId, false)); } }
        public OwnedRuntimeStamp Dock(OwnedRuntimeStamp expected, long connectionId, long partA, long partB)
        { lock (gate) { RequireMutation(expected); return ChangeTopology(current.Topology.WithDock(connectionId, partA, partB)); } }
        public OwnedRuntimeStamp SetConnectionLocked(OwnedRuntimeStamp expected, long connectionId, bool locked)
        { lock (gate) { RequireMutation(expected); return ChangeTopology(current.Topology.WithLocked(connectionId, locked)); } }
        public OwnedRuntimeStamp SetRegionMotions(OwnedRuntimeStamp expected,OwnedRuntimeMotion[] motions,double time,string frameId)
        {
            if(motions==null)throw new ArgumentNullException("motions");AssemblyModel.Finite(time);
            if(string.IsNullOrWhiteSpace(frameId)||frameId.Length>2048)throw new ArgumentException("Motion frame identity is required.");
            lock(gate)
            {
                RequireMutation(expected);
                if(motions.Length!=current.RegionMotion.Length||time<expected.Time)throw new ArgumentException("Cold motion shape/time is incompatible.");
                if(time>expected.Time&&current.Thermal.pendingDuration!=0)throw new InvalidOperationException("Flush pending thermal interval before a clock jump.");
                var candidate=new OwnedRuntimeBank(current.Definition,current.Topology);candidate.CopyFrom(current);
                if(time>expected.Time){candidate.Thermal.passiveFrozenSeconds+=time-expected.Time;candidate.Thermal.integratedTime=time;}
                for(int r=0;r<motions.Length;r++){ValidateMotion(motions[r]);candidate.RegionMotion[r]=motions[r];}
                ValidateBank(candidate);
                candidate.Stamp=new OwnedRuntimeStamp(checked(expected.Epoch+1),checked(expected.StateRevision+1),expected.TopologyRevision,expected.DefinitionRevision,
                    frameId==expected.FrameId?expected.FrameRevision:checked(expected.FrameRevision+1),time,frameId);
                PublishCold(candidate);return current.Stamp;
            }
        }

        OwnedRuntimeStamp ChangeTopology(OwnedRuntimeTopology topology)
        {
            if(current.Thermal.pendingDuration!=0)throw new InvalidOperationException("Flush pending thermal interval before topology mutation.");
            OwnedRuntimeBank before = current, candidate = new OwnedRuntimeBank(before.Definition, topology);
            CopyCommon(before, candidate); double removedEnergy = 0;
            for (int r = 0; r < topology.RegionCount; r++)
            {
                int[] bodies = topology.RegionBodies[r]; int anchor = before.Topology.BodyRegions[bodies[0]];
                OwnedRuntimeMotion frame = before.RegionMotion[anchor]; var motions = new RigidAssemblyMotion[bodies.Length];
                for (int i = 0; i < bodies.Length; i++)
                {
                    int body = bodies[i], oldRegion = before.Topology.BodyRegions[body];
                    RigidAssemblyMember old = before.Members[body]; OwnedRuntimeMotion oldMotion = before.RegionMotion[oldRegion];
                    RigidAssemblyProperties oldProperties = before.Properties[oldRegion];
                    Vec worldLocalArm = oldMotion.Orientation.Rotate(old.LocalPosition + old.LocalRotation.Rotate(old.LocalCenterOfMass) + oldProperties.CenterOfMass * -1);
                    Vec omega = OwnedRuntimeDynamics.AngularVelocity(oldProperties, oldMotion.Orientation, oldMotion.AngularMomentum);
                    Vec relativeVelocity = oldMotion.Velocity + frame.Velocity * -1 + Vec.Cross(omega, worldLocalArm);
                    RigidAssemblyMember member = old;
                    if (oldRegion != anchor)
                    {
                        Vec localOrigin = frame.Orientation.Inverse.Rotate((oldMotion.RootPosition + frame.RootPosition * -1) + oldMotion.Orientation.Rotate(old.LocalPosition));
                        Rotation localRotation = frame.Orientation.Inverse * oldMotion.Orientation * old.LocalRotation;
                        member = new RigidAssemblyMember(body, old.Mass, localOrigin, localRotation,
                            old.LocalCenterOfMass, old.PrincipalInertia, old.PrincipalRotation);
                    }
                    candidate.Members[body] = member;
                    motions[i] = new RigidAssemblyMotion(member, frame.Orientation.Inverse.Rotate(relativeVelocity), frame.Orientation.Inverse.Rotate(omega));
                }
                var projection = RigidAssemblyProjection.Project(motions); candidate.Properties[r] = projection.Properties;
                candidate.RegionMotion[r] = new OwnedRuntimeMotion { RootPosition = frame.RootPosition, Orientation = frame.Orientation,
                    Velocity = frame.Velocity + frame.Orientation.Rotate(projection.Velocity), AngularMomentum = frame.Orientation.Rotate(projection.AngularMomentumAboutCenter) };
                removedEnergy += projection.RemovedKineticEnergy; AssemblyModel.Finite(removedEnergy);
            }
            EnsurePropertyOwners(candidate); ValidateBank(candidate);
            candidate.Stamp = new OwnedRuntimeStamp(checked(before.Stamp.Epoch + 1), checked(before.Stamp.StateRevision + 1),
                topology.Revision, before.Stamp.DefinitionRevision, before.Stamp.FrameRevision, before.Stamp.Time, before.Stamp.FrameId);
            PublishCold(candidate); LastTopologyRemovedEnergy = removedEnergy; return current.Stamp;
        }

        static void CopyCommon(OwnedRuntimeBank source, OwnedRuntimeBank target)
        {
            target.Mechanical.CopyFrom(source.Mechanical);
            Array.Copy(source.Members, target.Members, source.Members.Length);
            Array.Copy(source.ResourceQuantity, target.ResourceQuantity, source.ResourceQuantity.Length);
            Array.Copy(source.LastResourceQuantity, target.LastResourceQuantity, source.LastResourceQuantity.Length);
            Array.Copy(source.Temperature, target.Temperature, source.Temperature.Length);
            target.Thermal.CopyFrom(source.Thermal);
            Array.Copy(source.ProviderState, target.ProviderState, source.ProviderState.Length);
            Array.Copy(source.History, target.History, source.History.Length);
            target.HistoryCount = source.HistoryCount; target.HistoryHead = source.HistoryHead; target.Input = source.Input;
        }
        void PublishCold(OwnedRuntimeBank candidate)
        {
            var next = NewBanks(candidate.Definition, candidate.Topology);
            next[0].CopyFrom(candidate); next[1].CopyFrom(candidate); next[2].CopyFrom(candidate);
            EnsurePropertyOwners(next[0]); EnsurePropertyOwners(next[1]); EnsurePropertyOwners(next[2]);
            banks = next; current = next[0];
        }

        public OwnedRuntimeStamp Rebase(OwnedRuntimeStamp expected, Vec translation, string frameId)
        {
            RigidAssemblyProperties.RequireVector(translation);
            lock (gate)
            {
                RequireMutation(expected); var candidate = new OwnedRuntimeBank(current.Definition, current.Topology); candidate.CopyFrom(current);
                for (int r = 0; r < candidate.RegionMotion.Length; r++) candidate.RegionMotion[r].RootPosition += translation;
                ValidateBank(candidate);
                candidate.Stamp = new OwnedRuntimeStamp(checked(expected.Epoch + 1), checked(expected.StateRevision + 1), expected.TopologyRevision,
                    expected.DefinitionRevision, checked(expected.FrameRevision + 1), expected.Time, frameId);
                PublishCold(candidate); return current.Stamp;
            }
        }

        public OwnedRuntimeStamp Reconfigure(OwnedRuntimeStamp expected, OwnedRuntimeDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            lock (gate)
            {
                RequireMutation(expected); var old = current.Definition;
                if(current.Thermal.pendingDuration!=0)throw new InvalidOperationException("Flush pending thermal interval before definition mutation.");
                if (definition.Id != old.Id || definition.Revision <= old.Revision || definition.PartCount != old.PartCount
                    || definition.BodyCount != old.BodyCount || definition.ResourceCount != old.ResourceCount
                    || definition.ProviderStateCount != old.ProviderStateCount) throw new ArgumentException("HMR requires the same stable identity/storage schema and a newer revision.");
                for (int p = 0; p < old.PartCount; p++)
                    if (old.GetPart(p).Id != definition.GetPart(p).Id || old.GetPart(p).BodyId != definition.GetPart(p).BodyId)
                        throw new ArgumentException("HMR cannot silently remap part/body identities.");
                for (int b = 0; b < old.BodyCount; b++) if (old.GetBody(b).Id != definition.GetBody(b).Id)
                    throw new ArgumentException("HMR body identities differ.");
                for (int i = 0; i < old.ResourceCount; i++) if (old.GetResource(i).Id != definition.GetResource(i).Id
                    || old.GetResource(i).PartId != definition.GetResource(i).PartId || old.GetResource(i).Kind != definition.GetResource(i).Kind)
                    throw new ArgumentException("HMR resource identities differ.");
                for (int i = 0; i < old.ResourceCount; i++)
                {
                    var before = old.GetResource(i); var after = definition.GetResource(i);
                    if (before.AcquiredSurplus != after.AcquiredSurplus || (before.AcquiredSurplus
                        && (!Same(before.Capacity, after.Capacity) || !Same(before.InitialQuantity, after.InitialQuantity))))
                        throw new ArgumentException("HMR cannot alter acquired surplus source facts or admission policy.");
                }
                for (int i = 0; i < old.ProviderStateCount; i++) if (old.GetProviderKey(i) != definition.GetProviderKey(i))
                    throw new ArgumentException("HMR provider state keys differ.");
                var topology = OwnedRuntimeTopology.Compile(definition, current.Topology.CopyConnections(), checked(current.Topology.Revision + 1));
                var candidate = new OwnedRuntimeBank(definition, topology); CopyCommon(current, candidate);
                Array.Copy(current.RegionMotion, candidate.RegionMotion, current.RegionMotion.Length);
                Array.Copy(current.Properties, candidate.Properties, current.Properties.Length);
                for (int b = 0; b < definition.BodyCount; b++) candidate.Members[b] = WetMember(candidate, b);
                EnsurePropertyOwners(candidate);
                for (int r = 0; r < topology.RegionCount; r++)
                {
                    var updated = candidate.PropertyOwners[r].Combine(); RequireCondition(updated);
                    OwnedRuntimeDynamics.ChangeProperties(ref candidate.RegionMotion[r], candidate.Properties[r], updated); candidate.Properties[r] = updated;
                }
                RefreshMass(candidate, true); ValidateBank(candidate);
                candidate.Stamp = new OwnedRuntimeStamp(checked(expected.Epoch + 1), checked(expected.StateRevision + 1), topology.Revision,
                    definition.Revision, expected.FrameRevision, expected.Time, expected.FrameId);
                PublishCold(candidate); return current.Stamp;
            }
        }

        public OwnedRuntimeSnapshot CaptureSnapshot()
        {
            lock (gate)
            {
                var snapshot = new OwnedRuntimeSnapshot { Definition = current.Definition, Connections = current.Topology.CopyConnections(),
                    Stamp = current.Stamp, BodyMembers = (RigidAssemblyMember[])current.Members.Clone(),
                    RegionMotion = (OwnedRuntimeMotion[])current.RegionMotion.Clone(), RegionIds = new long[current.Topology.RegionCount],
                    ResourceQuantity = (double[])current.ResourceQuantity.Clone(), Temperature = (double[])current.Temperature.Clone(),
                    Thermal = current.Thermal.Clone(), Mechanical=current.Mechanical.Clone(),
                    ProviderState = (double[])current.ProviderState.Clone(), Input = current.Input, InputHistory = new OwnedRuntimeInputRecord[current.HistoryCount] };
                for (int r = 0; r < snapshot.RegionIds.Length; r++) snapshot.RegionIds[r] = current.Topology.GetRegionId(r);
                for (int i = 0; i < current.HistoryCount; i++) snapshot.InputHistory[i] = current.History[(current.HistoryHead - current.HistoryCount + i + current.History.Length) % current.History.Length];
                return snapshot;
            }
        }

        public static OwnedRuntimeWorld RestoreSnapshot(OwnedRuntimeSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Definition == null || snapshot.Connections == null) throw new ArgumentException("Snapshot is incomplete.");
            var definition = snapshot.Definition; var stamp = snapshot.Stamp;
            if (stamp.DefinitionRevision != definition.Revision) throw new ArgumentException("Snapshot definition revision differs.");
            var topology = OwnedRuntimeTopology.Compile(definition, snapshot.Connections, stamp.TopologyRevision);
            RequireLength(snapshot.BodyMembers, definition.BodyCount); RequireLength(snapshot.RegionIds, topology.RegionCount);
            RequireLength(snapshot.RegionMotion, topology.RegionCount); RequireLength(snapshot.ResourceQuantity, definition.ResourceCount);
            RequireLength(snapshot.Temperature, definition.PartCount); RequireLength(snapshot.ProviderState, definition.ProviderStateCount);
            if (snapshot.InputHistory == null || snapshot.InputHistory.Length > 256) throw new ArgumentException("Snapshot history is invalid.");
            var world = new OwnedRuntimeWorld(definition, topology, snapshot.RegionMotion[0], stamp.Time, stamp.FrameId);
            OwnedRuntimeBank bank = world.current; bank.Stamp = new OwnedRuntimeStamp(stamp.Epoch, stamp.StateRevision, stamp.TopologyRevision,
                stamp.DefinitionRevision, stamp.FrameRevision, stamp.Time, stamp.FrameId); bank.Input = snapshot.Input;
            Array.Copy(snapshot.BodyMembers, bank.Members, bank.Members.Length); Array.Copy(snapshot.RegionMotion, bank.RegionMotion, bank.RegionMotion.Length);
            Array.Copy(snapshot.ResourceQuantity, bank.ResourceQuantity, bank.ResourceQuantity.Length); Array.Copy(snapshot.Temperature, bank.Temperature, bank.Temperature.Length);
            Array.Copy(snapshot.ProviderState, bank.ProviderState, bank.ProviderState.Length);
            if(snapshot.Thermal==null)bank.Thermal.integratedTime=stamp.Time;
            else {snapshot.Thermal.Validate(definition.PartCount);bank.Thermal.CopyFrom(snapshot.Thermal);}
            if(snapshot.Mechanical!=null){snapshot.Mechanical.Validate();bank.Mechanical.CopyFrom(snapshot.Mechanical);}
            for (int r = 0; r < topology.RegionCount; r++) if (snapshot.RegionIds[r] != topology.GetRegionId(r)) throw new ArgumentException("Snapshot region identities differ.");
            for (int b = 0; b < definition.BodyCount; b++)
            {
                var member = bank.Members[b]; if (member == null || member.Id != b) throw new ArgumentException("Snapshot body slots differ.");
            }
            bank.PropertyOwners = null; EnsurePropertyOwners(bank);
            for (int r = 0; r < topology.RegionCount; r++) { bank.Properties[r] = bank.PropertyOwners[r].Combine(); RequireCondition(bank.Properties[r]); }
            ValidateRestoredMass(bank); ValidateBank(bank);bank.Thermal.ValidateClock(stamp.Time);
            long lastSequence = -1; double lastEnd = double.NegativeInfinity;
            foreach (var record in snapshot.InputHistory)
            {
                record.Input.Validate(); AssemblyModel.Finite(record.StartedAt); AssemblyModel.Positive(record.DeltaTime);
                double end = record.StartedAt + record.DeltaTime; AssemblyModel.Finite(end);
                if (record.Sequence <= lastSequence || record.Sequence > stamp.StateRevision || record.StartedAt < lastEnd || end > stamp.Time)
                    throw new ArgumentException("Snapshot input history is unordered or outside committed time.");
                bank.History[bank.HistoryCount++] = record; lastSequence = record.Sequence; lastEnd = end;
            }
            bank.HistoryHead = bank.HistoryCount % bank.History.Length;
            world.banks[1].CopyFrom(bank); world.banks[2].CopyFrom(bank);
            world.banks[1].PropertyOwners = null; world.banks[2].PropertyOwners = null;
            EnsurePropertyOwners(world.banks[1]); EnsurePropertyOwners(world.banks[2]); return world;
        }
        static void RequireLength(Array array, int length)
        { if (array == null || array.Length != length) throw new ArgumentException("Snapshot bank dimensions differ."); }
        static void ValidateRestoredMass(OwnedRuntimeBank bank)
        {
            for (int i = 0; i < bank.ResourceQuantity.Length; i++)
            {
                var resource = bank.Definition.GetResource(i); double quantity = bank.ResourceQuantity[i]; AssemblyModel.Finite(quantity);
                if (quantity < 0 || quantity > resource.QuantityLimit) throw new ArgumentException("Restored resource is outside its declared retained domain.");
            }
            for (int b = 0; b < bank.Members.Length; b++)
            {
                var expected = WetMember(bank, b); var member = bank.Members[b];
                if (!Same(member.Mass, expected.Mass) || !Same(member.PrincipalInertia, expected.PrincipalInertia)
                    || !Same(member.LocalCenterOfMass, expected.LocalCenterOfMass) || !Same(member.PrincipalRotation, expected.PrincipalRotation))
                    throw new ArgumentException("Restored physical properties do not match the declared resource mass model at body "+b
                        +"; massBits="+Bits(member.Mass)+"/"+Bits(expected.Mass)+"; inertiaBits="+Bits(member.PrincipalInertia)+"/"+Bits(expected.PrincipalInertia)
                        +"; centerBits="+Bits(member.LocalCenterOfMass)+"/"+Bits(expected.LocalCenterOfMass)+"; principalRotationBits="+Bits(member.PrincipalRotation)+"/"+Bits(expected.PrincipalRotation));
            }
            Array.Copy(bank.ResourceQuantity, bank.LastResourceQuantity, bank.ResourceQuantity.Length);
        }
        static bool Same(double a, double b) { return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b); }
        static string Bits(double value){return BitConverter.DoubleToInt64Bits(value).ToString("x16");}
        static string Bits(Vec value){return Bits(value.X)+","+Bits(value.Y)+","+Bits(value.Z);}
        static string Bits(Rotation value){return Bits(value.X)+","+Bits(value.Y)+","+Bits(value.Z)+","+Bits(value.W);}
        static bool Same(Vec a, Vec b) { return Same(a.X,b.X) && Same(a.Y,b.Y) && Same(a.Z,b.Z); }
        static bool Same(Rotation a, Rotation b) { return Same(a.X,b.X) && Same(a.Y,b.Y) && Same(a.Z,b.Z) && Same(a.W,b.W); }
    }
}
