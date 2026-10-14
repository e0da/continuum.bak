using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class OwnedRuntimePartDefinition
    {
        public OwnedRuntimePartDefinition(long id, string name, long bodyId, Vec localPosition,
            Rotation localRotation, double heatCapacity, double initialTemperature = 300)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Part name is required.");
            RigidAssemblyProperties.RequireVector(localPosition); RigidAssemblyProperties.RequireRotation(localRotation);
            AssemblyModel.Positive(heatCapacity); AssemblyModel.Positive(initialTemperature);
            Id = id; Name = name; BodyId = bodyId; LocalPosition = localPosition;
            LocalRotation = localRotation; HeatCapacity = heatCapacity; InitialTemperature = initialTemperature;
        }
        public long Id { get; private set; }
        public string Name { get; private set; }
        public long BodyId { get; private set; }
        // Part coordinates are relative to its physical body's authored frame.
        public Vec LocalPosition { get; private set; }
        public Rotation LocalRotation { get; private set; }
        public double HeatCapacity { get; private set; }
        public double InitialTemperature { get; private set; }
    }

    public sealed class OwnedRuntimeBodyDefinition
    {
        public OwnedRuntimeBodyDefinition(long id, RigidAssemblyMember member)
        {
            if (member == null) throw new ArgumentNullException("member");
            Id = id; Member = member;
        }
        public long Id { get; private set; }
        // Dry mass/inertia, SI; unique contributors, independent of authored part count.
        public RigidAssemblyMember Member { get; private set; }
    }

    public sealed class OwnedRuntimeResourceDefinition
    {
        public OwnedRuntimeResourceDefinition(long id, long partId, string kind, double capacity,
            double massPerUnit, double initialQuantity = 0, bool acquiredSurplus = false)
        {
            if (string.IsNullOrEmpty(kind)) throw new ArgumentException("Resource kind is required.");
            AssemblyModel.Finite(capacity); AssemblyModel.Finite(massPerUnit); AssemblyModel.Finite(initialQuantity);
            if (capacity < 0 || massPerUnit < 0 || initialQuantity < 0 || (!acquiredSurplus && initialQuantity > capacity))
                throw new ArgumentException("Resource quantity/capacity/mass is outside its domain.");
            Id = id; PartId = partId; Kind = kind; Capacity = capacity;
            MassPerUnit = massPerUnit; InitialQuantity = initialQuantity;
            AcquiredSurplus = acquiredSurplus;
        }
        public long Id { get; private set; }
        public long PartId { get; private set; }
        public string Kind { get; private set; }
        public double Capacity { get; private set; }
        public double MassPerUnit { get; private set; }
        public double InitialQuantity { get; private set; }
        public bool AcquiredSurplus { get; private set; }
        public double QuantityLimit { get { return AcquiredSurplus ? Math.Max(Capacity, InitialQuantity) : Capacity; } }
    }

    public sealed class OwnedRuntimeDefinition
    {
        public const int MaximumParts = 65536, MaximumResources = 1048576, MaximumProviderStates = 1048576;
        readonly OwnedRuntimePartDefinition[] parts;
        readonly OwnedRuntimeBodyDefinition[] bodies;
        readonly OwnedRuntimeResourceDefinition[] resources;
        readonly long[] providerKeys;
        readonly Dictionary<long, int> partSlots = new Dictionary<long, int>();
        readonly Dictionary<long, int> bodySlots = new Dictionary<long, int>();
        readonly Dictionary<long, int> resourceSlots = new Dictionary<long, int>();
        internal readonly int[] ResourceBodies;
        internal readonly Vec[] ResourcePoints;
        internal readonly int[][] BodyResources;
        public OwnedRuntimeDefinition(string id, long revision, OwnedRuntimePartDefinition[] parts,
            OwnedRuntimeBodyDefinition[] bodies, OwnedRuntimeResourceDefinition[] resources, long[] providerKeys = null)
        {
            if (string.IsNullOrEmpty(id) || revision < 0) throw new ArgumentException("Definition identity/revision is invalid.");
            if (parts == null || bodies == null || resources == null || parts.Length == 0 || bodies.Length == 0
                || parts.Length > MaximumParts || bodies.Length > parts.Length || resources.Length > MaximumResources)
                throw new ArgumentException("Definition dimensions are invalid.");
            this.parts = (OwnedRuntimePartDefinition[])parts.Clone(); this.bodies = (OwnedRuntimeBodyDefinition[])bodies.Clone();
            this.resources = (OwnedRuntimeResourceDefinition[])resources.Clone();
            this.providerKeys = providerKeys == null ? new long[0] : (long[])providerKeys.Clone();
            if (this.providerKeys.Length > MaximumProviderStates) throw new ArgumentException("Provider state is unbounded.");
            Array.Sort(this.parts, (a, b) => Compare(a, b, a == null ? 0 : a.Id, b == null ? 0 : b.Id));
            Array.Sort(this.bodies, (a, b) => Compare(a, b, a == null ? 0 : a.Id, b == null ? 0 : b.Id));
            Array.Sort(this.resources, (a, b) => Compare(a, b, a == null ? 0 : a.Id, b == null ? 0 : b.Id));
            for (int i = 0; i < this.parts.Length; i++) Add(partSlots, this.parts[i] == null, this.parts[i] == null ? 0 : this.parts[i].Id, i);
            for (int i = 0; i < this.bodies.Length; i++) Add(bodySlots, this.bodies[i] == null, this.bodies[i] == null ? 0 : this.bodies[i].Id, i);
            for (int i = 0; i < this.resources.Length; i++) Add(resourceSlots, this.resources[i] == null, this.resources[i] == null ? 0 : this.resources[i].Id, i);
            var referenced = new bool[this.bodies.Length];
            foreach (var part in this.parts) referenced[BodySlot(part.BodyId)] = true;
            foreach (bool value in referenced) if (!value) throw new ArgumentException("Every body must have an authored part owner.");
            ResourceBodies = new int[this.resources.Length]; ResourcePoints = new Vec[this.resources.Length];
            var bodyResources = new List<int>[this.bodies.Length];
            for (int b = 0; b < bodyResources.Length; b++) bodyResources[b] = new List<int>();
            for (int i = 0; i < this.resources.Length; i++)
            {
                var part = this.parts[PartSlot(this.resources[i].PartId)]; int body = BodySlot(part.BodyId);
                ResourceBodies[i] = body; ResourcePoints[i] = part.LocalPosition; bodyResources[body].Add(i);
            }
            BodyResources = new int[bodyResources.Length][];
            for (int b = 0; b < BodyResources.Length; b++) BodyResources[b] = bodyResources[b].ToArray();
            var keys = new HashSet<long>(); foreach (long key in this.providerKeys)
                if (!keys.Add(key)) throw new ArgumentException("Provider-state keys must be unique.");
            Id = id; Revision = revision;
        }
        static int Compare(object a, object b, long ai, long bi)
        { if (a == null || b == null) throw new ArgumentException("Definition rows cannot be null."); return ai.CompareTo(bi); }
        static void Add(Dictionary<long, int> target, bool isNull, long id, int slot)
        { if (isNull || target.ContainsKey(id)) throw new ArgumentException("Definition rows require unique nonnull identities."); target.Add(id, slot); }
        public string Id { get; private set; }
        public long Revision { get; private set; }
        public int PartCount { get { return parts.Length; } }
        public int BodyCount { get { return bodies.Length; } }
        public int ResourceCount { get { return resources.Length; } }
        public int ProviderStateCount { get { return providerKeys.Length; } }
        public OwnedRuntimePartDefinition GetPart(int slot) { return parts[slot]; }
        public OwnedRuntimeBodyDefinition GetBody(int slot) { return bodies[slot]; }
        public OwnedRuntimeResourceDefinition GetResource(int slot) { return resources[slot]; }
        public long GetProviderKey(int slot) { return providerKeys[slot]; }
        public int PartSlot(long id) { return Find(partSlots, id); }
        public int BodySlot(long id) { return Find(bodySlots, id); }
        public int ResourceSlot(long id) { return Find(resourceSlots, id); }
        static int Find(Dictionary<long, int> map, long id)
        { int slot; if (!map.TryGetValue(id, out slot)) throw new ArgumentException("Unknown stable identity: " + id); return slot; }
    }

    public struct OwnedRuntimeInput
    {
        public readonly double Throttle, Pitch, Yaw, Roll, X, Y, Z;
        public OwnedRuntimeInput(double throttle, double pitch = 0, double yaw = 0, double roll = 0,
            double x = 0, double y = 0, double z = 0)
        {
            Unit(throttle, true); Unit(pitch, false); Unit(yaw, false); Unit(roll, false);
            Unit(x, false); Unit(y, false); Unit(z, false);
            Throttle = throttle; Pitch = pitch; Yaw = yaw; Roll = roll; X = x; Y = y; Z = z;
        }
        static void Unit(double value, bool positive)
        { AssemblyModel.Finite(value); if (value < (positive ? 0 : -1) || value > 1) throw new ArgumentException("Input is outside its normalized range."); }
        internal void Validate() { Unit(Throttle, true); Unit(Pitch, false); Unit(Yaw, false); Unit(Roll, false); Unit(X, false); Unit(Y, false); Unit(Z, false); }
    }

    public struct OwnedRuntimeStamp
    {
        public readonly long Epoch, StateRevision, TopologyRevision, DefinitionRevision, FrameRevision;
        public readonly double Time;
        public readonly string FrameId;
        public OwnedRuntimeStamp(long epoch, long stateRevision, long topologyRevision, long definitionRevision,
            long frameRevision, double time, string frameId)
        {
            AssemblyModel.Finite(time);
            if (epoch < 0 || stateRevision < 0 || topologyRevision < 0 || definitionRevision < 0 || frameRevision < 0
                || string.IsNullOrEmpty(frameId)) throw new ArgumentException("State stamp is invalid.");
            Epoch = epoch; StateRevision = stateRevision; TopologyRevision = topologyRevision;
            DefinitionRevision = definitionRevision; FrameRevision = frameRevision; Time = time; FrameId = frameId;
        }
        public bool Matches(OwnedRuntimeStamp other)
        { return Epoch == other.Epoch && StateRevision == other.StateRevision && TopologyRevision == other.TopologyRevision
            && DefinitionRevision == other.DefinitionRevision && FrameRevision == other.FrameRevision
            && BitConverter.DoubleToInt64Bits(Time) == BitConverter.DoubleToInt64Bits(other.Time) && FrameId == other.FrameId; }
    }

    public struct OwnedRuntimeInputRecord
    {
        public long Sequence;
        public double StartedAt, DeltaTime;
        public OwnedRuntimeInput Input;
    }

    public struct OwnedRuntimeRegionState
    {
        public long Id;
        public OwnedRuntimeMotion Motion;
        public RigidAssemblyProperties Properties;
    }

    // Cold interchange object. Restore always validates and copies caller-owned arrays.
    public sealed class OwnedRuntimeSnapshot
    {
        public OwnedRuntimeDefinition Definition;
        public OwnedRuntimeConnection[] Connections;
        public OwnedRuntimeStamp Stamp;
        public RigidAssemblyMember[] BodyMembers;
        public long[] RegionIds;
        public OwnedRuntimeMotion[] RegionMotion;
        public double[] ResourceQuantity, Temperature, ProviderState;
        public OwnedRuntimeThermalState Thermal;
        public OwnedRuntimeInput Input;
        public OwnedRuntimeInputRecord[] InputHistory;
    }

    internal sealed class OwnedRuntimeBank
    {
        internal OwnedRuntimeMotion[] RegionMotion;
        internal RigidAssemblyProperties[] Properties;
        internal RigidAssemblyMember[] Members;
        internal double[] ResourceQuantity, Temperature, ProviderState;
        internal OwnedRuntimeThermalState Thermal;
        internal OwnedRuntimeInput Input;
        internal OwnedRuntimeInputRecord[] History;
        internal double[] LastResourceQuantity;
        internal bool[] BodyDirty;
        internal bool[] RegionDirty;
        internal RetainedRigidAssemblyProperties[] PropertyOwners;
        internal int HistoryCount, HistoryHead, Readers;
        internal bool SystemsExecuted;
        internal OwnedRuntimeDefinition Definition;
        internal OwnedRuntimeTopology Topology;
        internal OwnedRuntimeStamp Stamp;
        internal OwnedRuntimeBank(OwnedRuntimeDefinition definition, OwnedRuntimeTopology topology)
        {
            Definition = definition; Topology = topology;
            RegionMotion = new OwnedRuntimeMotion[topology.RegionCount]; Properties = new RigidAssemblyProperties[topology.RegionCount];
            Members = new RigidAssemblyMember[definition.BodyCount]; ResourceQuantity = new double[definition.ResourceCount];
            Temperature = new double[definition.PartCount]; ProviderState = new double[definition.ProviderStateCount];
            Thermal = new OwnedRuntimeThermalState(definition.PartCount);
            History = new OwnedRuntimeInputRecord[256];
            LastResourceQuantity = new double[definition.ResourceCount]; BodyDirty = new bool[definition.BodyCount];
            RegionDirty = new bool[topology.RegionCount];
        }
        internal void CopyFrom(OwnedRuntimeBank source)
        {
            if (RegionMotion.Length != source.RegionMotion.Length || Members.Length != source.Members.Length
                || ResourceQuantity.Length != source.ResourceQuantity.Length || Temperature.Length != source.Temperature.Length
                || ProviderState.Length != source.ProviderState.Length) throw new InvalidOperationException("Bank shapes differ.");
            Array.Copy(source.RegionMotion, RegionMotion, RegionMotion.Length); Array.Copy(source.Properties, Properties, Properties.Length);
            Array.Copy(source.Members, Members, Members.Length); Array.Copy(source.ResourceQuantity, ResourceQuantity, ResourceQuantity.Length);
            Array.Copy(source.LastResourceQuantity, LastResourceQuantity, LastResourceQuantity.Length);
            Array.Copy(source.Temperature, Temperature, Temperature.Length); Array.Copy(source.ProviderState, ProviderState, ProviderState.Length);
            Thermal.CopyFrom(source.Thermal);
            Array.Copy(source.History, History, History.Length); Input = source.Input; HistoryCount = source.HistoryCount; HistoryHead = source.HistoryHead;
            Definition = source.Definition; Topology = source.Topology; Stamp = source.Stamp;
        }
    }
}
