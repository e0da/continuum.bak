using System;
using System.IO;
using System.Collections.Generic;

namespace KspContinuum
{
    public static partial class OwnedRuntimeStateStore
    {
        sealed class Counts
        {
            internal int Parts, Bodies, Resources, Providers, Connections, Regions, Actuators, Surfaces, Links, History;
            internal int[] Values { get { return new[] { Parts, Bodies, Resources, Providers, Connections, Regions, Actuators, Surfaces, Links, History }; } }
            internal void Validate(OwnedRuntimeStateLimits l)
            {
                Need(Parts > 0 && Parts <= l.MaximumParts && Bodies > 0 && Bodies <= Parts && Bodies <= l.MaximumBodies && Resources >= 0 && Resources <= l.MaximumResources && Providers >= 0 && Providers <= l.MaximumProviderStates
                    && Connections >= 0 && Connections <= l.MaximumConnections && Regions > 0 && Regions <= Bodies && Actuators >= 0 && Actuators <= l.MaximumSystemRows && Surfaces >= 0 && Surfaces <= l.MaximumSystemRows && Links >= 0 && Links <= l.MaximumSystemRows && History >= 0 && History <= 256, "State dimension budget exceeded.");
            }
            internal void Reserve(Budget b)
            {
                // Includes temporary columns, retained objects, three bank copies and restore/system scratch; strings are charged separately.
                b.Reserve(checked(2048L * Parts + 4096L * Bodies + 256L * Resources + 64L * Providers + 128L * Connections + 8192L * Regions + 2048L * Actuators + 1024L * Surfaces + 256L * Links + 256L * History),
                    checked(64L * Parts + 48L * Bodies + 8L * Resources + 2L * Providers + 4L * Connections + 20L * Regions + 32L * Actuators + 12L * Surfaces + 3L * Links + 10L * History));
            }
        }
        sealed class Metadata
        {
            internal string DefinitionId, ImageSha; internal long CodeGeneration, ConfigurationRevision; internal int Execution, Deadline;
            internal OwnedRuntimeStamp Stamp; internal OwnedRuntimeInput Input; internal Rotation ControlOrientation;
            internal OwnedRuntimeCapacity Capacity;
            internal OwnedRuntimeProvenance Provenance; internal OwnedRuntimeMechanicalState Mechanical; internal OwnedRuntimeThermalState Thermal;
            internal OwnedRuntimeControls Controls; internal OwnedRuntimeEnvironment Environment; internal OwnedRuntimeStepPlan Plan;
        }
        sealed class Loaded
        {
            internal readonly Counts C; internal readonly Budget Budget;
            internal long[] PartIds, PartBodies, BodyIds, ResourceIds, ResourceParts, ProviderKeys, RegionIds;
            internal string[] Names, Kinds; internal Vec[] Positions; internal Rotation[] Rotations; internal double[] Capacity, MassPerUnit, InitialQuantity, HeatCapacity, InitialTemperature;
            internal bool[] Surplus, Flows, Enabled; internal int[] WheelSlots;
            internal RigidAssemblyMember[] Dry, Wet; internal OwnedRuntimeConnection[] Connections; internal OwnedRuntimeMotion[] Motions;
            internal double[] Quantity, Temperature, Providers;
            internal OwnedRuntimeInputRecord[] History;
            internal OwnedRuntimeActuatorSpec[] Actuators; internal OwnedRuntimeSurfaceSpec[] Surfaces; internal OwnedRuntimeThermalLink[] Links;
            internal string[][] FuelKinds; internal double[][] FuelFractions;
            internal ActuatorValue[] ActuatorValues;
            internal Loaded(Counts c, Budget budget)
            {
                C = c; Budget = budget;
                PartIds = new long[c.Parts]; PartBodies = new long[c.Parts]; Names = new string[c.Parts]; Positions = new Vec[c.Parts]; Rotations = new Rotation[c.Parts]; HeatCapacity = new double[c.Parts]; InitialTemperature = new double[c.Parts];
                BodyIds = new long[c.Bodies]; Dry = new RigidAssemblyMember[c.Bodies]; Wet = new RigidAssemblyMember[c.Bodies];
                ResourceIds = new long[c.Resources]; ResourceParts = new long[c.Resources]; Kinds = new string[c.Resources]; Capacity = new double[c.Resources]; MassPerUnit = new double[c.Resources]; InitialQuantity = new double[c.Resources]; Surplus = new bool[c.Resources]; Flows = new bool[c.Resources]; Quantity = new double[c.Resources];
                ProviderKeys = new long[c.Providers]; Providers = new double[c.Providers]; RegionIds = new long[c.Regions]; Motions = new OwnedRuntimeMotion[c.Regions]; Connections = new OwnedRuntimeConnection[c.Connections];
                Temperature = new double[c.Parts]; History = new OwnedRuntimeInputRecord[c.History]; Actuators = new OwnedRuntimeActuatorSpec[c.Actuators]; ActuatorValues = new ActuatorValue[c.Actuators]; FuelKinds = new string[c.Actuators][]; FuelFractions = new double[c.Actuators][];
                Enabled = new bool[c.Actuators]; WheelSlots = new int[c.Actuators]; Surfaces = new OwnedRuntimeSurfaceSpec[c.Surfaces]; Links = new OwnedRuntimeThermalLink[c.Links];
            }
        }
        struct ActuatorValue
        { internal int Part, Kind, Fuel, Electric; internal Vec Point, Axis; internal double Maximum, Isp, ElectricRate, Heat, WheelCapacity; }

        public static void Write(Stream destination, OwnedRuntimeHost host, OwnedRuntimeProvenance provenance, OwnedRuntimeControls controls,
            OwnedRuntimeEnvironment environment, OwnedRuntimeStepPlan plan, OwnedRuntimeStateLimits limits = null)
        {
            if (destination == null || !destination.CanWrite || host == null || provenance == null || environment == null || plan == null) throw new ArgumentException("State write requires a writable stream and complete owner/context.");
            limits = limits ?? new OwnedRuntimeStateLimits(); controls.Validate(); OwnedRuntimeProvenance source = provenance.Copy();
            Need(source.Native == null || string.IsNullOrEmpty(source.Native.supplyCaptureSha256), "Segmented builtin scope does not represent scalar-supply configuration.");
            host.WithStateRead(read =>
            {
                var bank = read.Bank; var d = bank.Definition; var s = host.Systems;
                var counts = new Counts { Parts = d.PartCount, Bodies = d.BodyCount, Resources = d.ResourceCount, Providers = d.ProviderStateCount, Connections = bank.Topology.ConnectionCount, Regions = bank.Topology.RegionCount, Actuators = s.ActuatorCount, Surfaces = s.SurfaceCount, Links = s.ThermalLinkCount, History = bank.HistoryCount };
                counts.Validate(limits); var budget = new Budget(limits); counts.Reserve(budget); bank.Thermal.Validate(d.PartCount); bank.Mechanical.Validate(); ValidateNative(source, d);
                var metadata = new Metadata { DefinitionId = d.Id, Capacity = d.Capacity, Stamp = bank.Stamp, ImageSha = host.ScheduleSourceSha256, CodeGeneration = host.CodeGeneration, ConfigurationRevision = s.ConfigurationRevision, Execution = (int)s.ExecutionStrategy, Deadline = (int)s.DeadlineStrategy,
                    Provenance = source, Input = bank.Input, Mechanical = bank.Mechanical, Thermal = bank.Thermal, ControlOrientation = s.ControlOrientation, Controls = controls, Environment = environment, Plan = plan };
                byte[] meta;
                using (var memory = new MemoryStream()) using (var bounded = new Wire(memory, limits.MaximumMetadataBytes)) using (var writer = new BinaryWriter(bounded, Utf8, true))
                { WriteMetadata(writer, metadata, limits); writer.Flush(); meta = memory.ToArray(); }
                using (var wire = new Wire(destination, limits.MaximumEncodedBytes)) using (var writer = new BinaryWriter(wire, Utf8, true))
                {
                    writer.Write(Magic); writer.Write(Version); writer.Write(Schema); foreach (int value in counts.Values) writer.Write(value); writer.Write(limits.SegmentBytes); writer.Write(meta.Length); writer.Write(meta);
                    long segments = 0; WriteColumns(writer, Columns(counts, bank, s, null, metadata, limits), limits, ref segments);
                    writer.Write(-1); writer.Write(checked(wire.Count + 16)); writer.Write(segments); writer.Flush(); byte[] hash = wire.Finish(); writer.Write(hash); writer.Flush();
                }
                return true;
            });
        }

        public static OwnedRuntimeStateRestored Read(Stream source, OwnedRuntimeStateLimits limits = null)
        {
            if (source == null || !source.CanRead) throw new ArgumentException("State read requires a readable stream."); limits = limits ?? new OwnedRuntimeStateLimits(); var budget = new Budget(limits);
            budget.Reserve(checked(4L * limits.SegmentBytes + 2L * limits.MaximumMetadataBytes));
            Counts c; Metadata m; Loaded loaded;
            using (var wire = new Wire(source, limits.MaximumEncodedBytes)) using (var reader = new BinaryReader(wire, Utf8, true))
            {
                Need(Equal(Exact(reader, 8), Magic) && reader.ReadInt32() == Version && Equal(Exact(reader, 32), Schema), "State magic/version/schema differs.");
                c = new Counts { Parts = reader.ReadInt32(), Bodies = reader.ReadInt32(), Resources = reader.ReadInt32(), Providers = reader.ReadInt32(), Connections = reader.ReadInt32(), Regions = reader.ReadInt32(), Actuators = reader.ReadInt32(), Surfaces = reader.ReadInt32(), Links = reader.ReadInt32(), History = reader.ReadInt32() };
                c.Validate(limits); c.Reserve(budget); int segmentBytes = reader.ReadInt32(), bytes = reader.ReadInt32();
                Need(segmentBytes >= 256 && segmentBytes <= limits.SegmentBytes && bytes >= 0 && bytes <= limits.MaximumMetadataBytes, "State scratch/metadata budget exceeded.");
                using (var memory = new MemoryStream(Exact(reader, bytes), false)) using (var metadataReader = new BinaryReader(memory, Utf8, true))
                { m = ReadMetadata(metadataReader, budget); Need(memory.Position == memory.Length, "State metadata trailing bytes."); }
                OwnedRuntimeHost.RequireBuiltinStateImage(m.ImageSha,m.CodeGeneration);
                Need(c.Parts<=m.Capacity.MaxParts&&c.Resources<=m.Capacity.MaxResources&&c.Providers<=m.Capacity.MaxProviderStates&&c.Connections<=m.Capacity.MaxConnections,"State dimensions exceed its original admission policy.");
                loaded = new Loaded(c, budget); PrepareThermal(m.Thermal, c.Parts);
                long segments = 0; ReadColumns(reader, Columns(c, null, null, loaded, m, limits), segmentBytes, ref segments);
                Need(reader.ReadInt32() == -1, "State footer absent."); long bodyBytes = reader.ReadInt64(), declaredSegments = reader.ReadInt64(); Need(bodyBytes == wire.Count && declaredSegments == segments, "State footer byte/segment accounting differs.");
                byte[] hash = wire.Finish(); Need(Equal(Exact(reader, 32), hash), "Complete state checksum differs."); Need(source.ReadByte() == -1, "Trailing state bytes.");
            }
            return Restore(c, m, loaded, limits);
        }
        static void WriteMetadata(BinaryWriter w, Metadata m, OwnedRuntimeStateLimits l)
        {
            Text(w, m.Provenance.Kind, l); Text(w, m.Provenance.SourceId, l); Text(w, m.Provenance.SourceSha256, l); Text(w, m.Provenance.Recipe, l); Small(w, typeof(OwnedRuntimeSourceBinding), m.Provenance.Native, l);
            Text(w, m.DefinitionId, l); w.Write(m.Capacity.MaxParts);w.Write(m.Capacity.MaxResources);w.Write(m.Capacity.MaxProviderStates);w.Write(m.Capacity.MaxConnections);
            Text(w, m.Stamp.FrameId, l); w.Write(m.Stamp.Epoch); w.Write(m.Stamp.StateRevision); w.Write(m.Stamp.TopologyRevision); w.Write(m.Stamp.DefinitionRevision); w.Write(m.Stamp.FrameRevision); w.Write(m.Stamp.Time);
            Text(w, m.ImageSha, l); w.Write(m.CodeGeneration); w.Write(m.ConfigurationRevision); w.Write(m.Execution); w.Write(m.Deadline); Input(w, m.Input); Quaternion(w, m.ControlOrientation);
            Small(w, typeof(OwnedRuntimeMechanicalState), m.Mechanical, l); Small(w, typeof(OwnedRuntimeThermalState), m.Thermal, l, true);
            Input(w, new OwnedRuntimeInput(m.Controls.Throttle, m.Controls.Attitude.X, m.Controls.Attitude.Y, m.Controls.Attitude.Z, m.Controls.Translation.X, m.Controls.Translation.Y, m.Controls.Translation.Z)); w.Write(m.Controls.EnginesEnabled); w.Write(m.Controls.RcsEnabled); w.Write(m.Controls.WheelsEnabled);
            Text(w, m.Environment.SourceId, l); w.Write(m.Environment.Revision); w.Write(m.Environment.Epoch); w.Write(m.Environment.Density); w.Write(m.Environment.Temperature); Vector(w, m.Environment.WindVelocity); Vector(w, m.Environment.Gravity); w.Write(m.Environment.ConvectiveCoefficient); w.Write(m.Environment.RadiationTemperature); w.Write(m.Plan.FixedDelta); w.Write(m.Plan.WarpRate);
        }
        static Metadata ReadMetadata(BinaryReader r, Budget b)
        {
            string kind = Text(r, b), id = Text(r, b), sha = Text(r, b), recipe = Text(r, b); var native = (OwnedRuntimeSourceBinding)Small(r, typeof(OwnedRuntimeSourceBinding), b);
            var m = new Metadata { Provenance = new OwnedRuntimeProvenance(kind, id, sha, recipe, native).Copy(), DefinitionId = Text(r, b) };
            m.Capacity=new OwnedRuntimeCapacity(r.ReadInt32(),r.ReadInt32(),r.ReadInt32(),r.ReadInt32());string frame = Text(r, b);
            m.Stamp = new OwnedRuntimeStamp(r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadDouble(), frame);
            m.ImageSha = Text(r, b); OwnedRuntimeImport.Digest(m.ImageSha); m.CodeGeneration = r.ReadInt64(); m.ConfigurationRevision = r.ReadInt64(); m.Execution = r.ReadInt32(); m.Deadline = r.ReadInt32();
            Need(m.CodeGeneration >= 0 && m.ConfigurationRevision >= 0 && m.Execution >= 0 && m.Execution <= 1 && m.Deadline >= 0 && m.Deadline <= 1, "State builtin/configuration metadata differs.");
            m.Input = Input(r); m.ControlOrientation = Quaternion(r); m.Mechanical = (OwnedRuntimeMechanicalState)Small(r, typeof(OwnedRuntimeMechanicalState), b); m.Thermal = (OwnedRuntimeThermalState)Small(r, typeof(OwnedRuntimeThermalState), b, true);
            Need(m.Mechanical != null && m.Thermal != null, "State mechanical/thermal continuation absent."); var input = Input(r);
            m.Controls = new OwnedRuntimeControls { Throttle = input.Throttle, Attitude = new Vec(input.Pitch, input.Yaw, input.Roll), Translation = new Vec(input.X, input.Y, input.Z), EnginesEnabled = Boolean(r), RcsEnabled = Boolean(r), WheelsEnabled = Boolean(r) };
            string sourceId = Text(r, b); long revision = r.ReadInt64(); double epoch = r.ReadDouble(), density = r.ReadDouble(), temperature = r.ReadDouble(); Vec wind = Vector(r), gravity = Vector(r); double convection = r.ReadDouble(), radiation = r.ReadDouble();
            m.Environment = new OwnedRuntimeEnvironment(sourceId, revision, epoch, density, temperature, wind, gravity, convection, radiation); m.Plan = new OwnedRuntimeStepPlan(r.ReadDouble(), r.ReadDouble());
            Need(m.Provenance.Native == null || string.IsNullOrEmpty(m.Provenance.Native.supplyCaptureSha256), "Scalar-supply state is not represented by this format version."); return m;
        }
        static void PrepareThermal(OwnedRuntimeThermalState t, int parts)
        {
            t.heatJoules = new double[parts]; t.convectionExposure = new double[parts]; t.convectionSource = new double[parts]; t.radiationExposure = new double[parts]; t.radiationSource = new double[parts]; t.segmentDuration = new double[8];
            t.segmentHeat = t.segmentConvection = t.segmentConvectionSource = t.segmentRadiation = t.segmentRadiationSource = new double[0];
            if(t.segmentCount>0)HydrateThermal(t);
        }
        static void HydrateThermal(OwnedRuntimeThermalState t)
        { int count=t.segmentCount;t.segmentCount=0;try{t.EnsureSegmentStorage();}finally{t.segmentCount=count;} }
        static OwnedRuntimeStateRestored Restore(Counts c, Metadata m, Loaded a, OwnedRuntimeStateLimits limits)
        {
            Ordered(a.PartIds); Ordered(a.BodyIds); Ordered(a.ResourceIds);
            var parts = new OwnedRuntimePartDefinition[c.Parts]; for (int i = 0; i < parts.Length; i++) parts[i] = new OwnedRuntimePartDefinition(a.PartIds[i], a.Names[i], a.PartBodies[i], a.Positions[i], a.Rotations[i], a.HeatCapacity[i], a.InitialTemperature[i]);
            var bodies = new OwnedRuntimeBodyDefinition[c.Bodies]; for (int i = 0; i < bodies.Length; i++) bodies[i] = new OwnedRuntimeBodyDefinition(a.BodyIds[i], a.Dry[i]);
            var resources = new OwnedRuntimeResourceDefinition[c.Resources]; for (int i = 0; i < resources.Length; i++) resources[i] = new OwnedRuntimeResourceDefinition(a.ResourceIds[i], a.ResourceParts[i], a.Kinds[i], a.Capacity[i], a.MassPerUnit[i], a.InitialQuantity[i], a.Surplus[i]);
            var definition = new OwnedRuntimeDefinition(m.DefinitionId, m.Stamp.DefinitionRevision, parts, bodies, resources, a.ProviderKeys, m.Capacity); ValidateNative(m.Provenance, definition);
            for (int i = 1; i < a.Connections.Length; i++) Need(a.Connections[i - 1].Id < a.Connections[i].Id, "State connection order differs.");
            var snapshot = new OwnedRuntimeSnapshot { Definition = definition, Stamp = m.Stamp, BodyMembers = a.Wet, RegionIds = a.RegionIds, RegionMotion = a.Motions, Connections = a.Connections, ResourceQuantity = a.Quantity, Temperature = a.Temperature, ProviderState = a.Providers, Input = m.Input, InputHistory = a.History, Mechanical = m.Mechanical, Thermal = m.Thermal };
            var world = OwnedRuntimeWorld.RestoreSnapshot(snapshot);
            for (int i = 0; i < a.Actuators.Length; i++)
            {
                var v = a.ActuatorValues[i]; Need(v.Part >= 0 && v.Part < c.Parts && v.Kind >= 0 && v.Kind <= 2 && v.Fuel >= -1 && v.Electric >= -1, "State actuator identity/sentinel differs.");
                a.Actuators[i] = a.FuelKinds[i].Length == 0 ? OwnedRuntimeActuatorSpec.RestoreUnitAxis(v.Part, (OwnedRuntimeActuatorKind)v.Kind, v.Point, v.Axis, v.Maximum, v.Fuel, v.Isp, v.Electric, v.ElectricRate, v.Heat, v.WheelCapacity)
                    : OwnedRuntimeActuatorSpec.RestoreUnitAxis(v.Part, (OwnedRuntimeActuatorKind)v.Kind, v.Point, v.Axis, v.Maximum, a.FuelKinds[i], a.FuelFractions[i], v.Isp, v.Electric, v.ElectricRate, v.Heat);
                Need(a.FuelKinds[i].Length == 0 || v.Fuel == -1, "State mixture also carries an explicit fuel resource.");
            }
            var systems = new OwnedRuntimeSystems(world, a.Actuators, a.Surfaces, a.Links, a.Flows, false, a.WheelSlots, m.ControlOrientation);
            for (int i = 0; i < a.Enabled.Length; i++) systems.SetEnabled(i, a.Enabled[i]); systems.SetExecutionStrategy((OwnedRuntimeExecutionStrategy)m.Execution); systems.SetDeadlineStrategy(world.Stamp, (OwnedRuntimeDeadlineStrategy)m.Deadline); systems.RestoreConfigurationRevision(m.ConfigurationRevision);
            var host = OwnedRuntimeHost.RestoreBuiltinState(world, systems, m.ImageSha, m.CodeGeneration);
            return new OwnedRuntimeStateRestored { Host = host, Provenance = m.Provenance, Controls = m.Controls, Environment = m.Environment, Plan = m.Plan };
        }
        static void Ordered(long[] ids) { for (int i = 1; i < ids.Length; i++) Need(ids[i - 1] < ids[i], "State identity order differs."); }
        static void ValidateNative(OwnedRuntimeProvenance p, OwnedRuntimeDefinition d)
        { if (p.Native == null) return; Need(p.Native.bodies.Length == d.BodyCount, "Native sidecar body coverage differs."); for (int i = 0; i < d.BodyCount; i++) Need(p.Native.bodies[i].bodyId == d.GetBody(i).Id, "Native sidecar body identity differs."); foreach (var r in p.Native.renderers) d.PartSlot(r.partId); }
    }
}
