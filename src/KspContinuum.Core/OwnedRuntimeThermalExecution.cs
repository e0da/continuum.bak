using System;
using System.Threading.Tasks;

namespace KspContinuum
{
    internal sealed class OwnedRuntimeThermalExecution
    {
        internal const int ParallelPartThreshold = 32768, PartRangeSize = 4096, MaximumWorkers = 8;
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeTopology topology;
        readonly long configuration;
        readonly double[] capacities, surfaceAreas, surfaceEmissivities, conductances;
        readonly int[] surfaceOffsets, linkOffsets, linkA, linkB, activeLinkRegions, partRegions;
        readonly int originalLinkRows;
        readonly double stefanBoltzmann;
        readonly ParallelOptions parallel;
        readonly Action<int> partRangeAction, linkRegionAction;
        double[] temperature, heat;
        OwnedRuntimeEnvironmentSample[] environments;
        double delta;
        bool running;

        internal OwnedRuntimeThermalExecution(OwnedRuntimeBank bank, OwnedRuntimeSurfaceSpec[] surfaces,
            OwnedRuntimeThermalLink[] links, long configuration, double stefanBoltzmann)
        {
            definition = bank.Definition; topology = bank.Topology; this.configuration = configuration;
            this.stefanBoltzmann = stefanBoltzmann; originalLinkRows = links.Length;
            capacities = new double[definition.PartCount]; surfaceOffsets = new int[capacities.Length + 1];
            partRegions = topology.PartRegions;
            for (int p = 0; p < capacities.Length; p++) capacities[p] = definition.GetPart(p).HeatCapacity;
            foreach (var row in surfaces) surfaceOffsets[row.Part + 1]++;
            for (int p = 1; p < surfaceOffsets.Length; p++) surfaceOffsets[p] += surfaceOffsets[p - 1];
            surfaceAreas = new double[surfaces.Length]; surfaceEmissivities = new double[surfaces.Length];
            var nextSurface = (int[])surfaceOffsets.Clone();
            foreach (var row in surfaces)
            { int slot = nextSurface[row.Part]++; surfaceAreas[slot] = row.ThermalArea; surfaceEmissivities[slot] = row.Emissivity; }

            linkOffsets = new int[topology.RegionCount + 1];
            foreach (var row in links) if (partRegions[row.A] == partRegions[row.B]) linkOffsets[partRegions[row.A] + 1]++;
            int groups = 0;
            for (int r = 1; r < linkOffsets.Length; r++) { if (linkOffsets[r] != 0) groups++; linkOffsets[r] += linkOffsets[r - 1]; }
            int count = linkOffsets[linkOffsets.Length - 1];
            linkA = new int[count]; linkB = new int[count]; conductances = new double[count]; activeLinkRegions = new int[groups];
            var nextLink = (int[])linkOffsets.Clone();
            foreach (var row in links)
            {
                int region = partRegions[row.A]; if (region != partRegions[row.B]) continue;
                int slot = nextLink[region]++; linkA[slot] = row.A; linkB[slot] = row.B; conductances[slot] = row.ConductanceWattsPerKelvin;
            }
            for (int r = 0, g = 0; r < topology.RegionCount; r++) if (linkOffsets[r + 1] != linkOffsets[r]) activeLinkRegions[g++] = r;
            parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, MaximumWorkers)) };
            partRangeAction = ApplyPartRange; linkRegionAction = ApplyLinkRegion;
        }
        internal bool Matches(OwnedRuntimeBank bank, long revision)
        { return ReferenceEquals(definition, bank.Definition) && ReferenceEquals(topology, bank.Topology) && configuration == revision; }

        internal void Execute(OwnedRuntimeBank bank, double[] heat, OwnedRuntimeEnvironmentSample[] environments,
            double delta, OwnedRuntimeSystemCounters counters)
        {
            if (running || !ReferenceEquals(bank.Definition, definition) || !ReferenceEquals(bank.Topology, topology)
                || heat == null || heat.Length != capacities.Length || bank.Temperature.Length != capacities.Length
                || environments == null || environments.Length < topology.RegionCount)
                throw new InvalidOperationException("Compiled thermal owner/input differs or reenters.");
            running = true; this.temperature = bank.Temperature; this.heat = heat; this.environments = environments; this.delta = delta;
            try
            {
                int ranges = (capacities.Length + PartRangeSize - 1) / PartRangeSize;
                bool large = capacities.Length >= ParallelPartThreshold && parallel.MaxDegreeOfParallelism > 1;
                if (large) Parallel.For(0, ranges, parallel, partRangeAction);
                else for (int range = 0; range < ranges; range++) ApplyPartRange(range);

                // Each region is a separate ordered link fold; cross-region rows are counted but remain skipped.
                if (large && linkA.Length >= ParallelPartThreshold && activeLinkRegions.Length > 1)
                    Parallel.For(0, activeLinkRegions.Length, parallel, linkRegionAction);
                else for (int group = 0; group < activeLinkRegions.Length; group++) ApplyLinkRegion(group);
                counters.ThermalLinks += originalLinkRows;
                for (int p = 0; p < temperature.Length; p++) AssemblyModel.Positive(temperature[p]);
            }
            finally
            {
                // Parallel.For has joined before either normal completion or propagation; no retained bank survives this call.
                this.temperature = null; this.heat = null; this.environments = null; this.delta = 0; running = false;
            }
        }
        void ApplyPartRange(int range)
        {
            int end = Math.Min(capacities.Length, (range + 1) * PartRangeSize);
            for (int p = range * PartRangeSize; p < end; p++)
            {
                temperature[p] += heat[p] / capacities[p];
                var environment = environments[partRegions[p]];
                for (int row = surfaceOffsets[p]; row < surfaceOffsets[p + 1]; row++)
                {
                    double t = temperature[p], ambient = environment.RadiationTemperature;
                    double radiative = stefanBoltzmann * surfaceEmissivities[row] * surfaceAreas[row]
                        * (t * t * t + t * t * ambient + t * ambient * ambient + ambient * ambient * ambient);
                    double convective = environment.ConvectiveCoefficient * surfaceAreas[row], capacity = capacities[p];
                    double denominator = capacity + delta * (radiative + convective);
                    temperature[p] = (capacity * t + delta * (radiative * ambient + convective * environment.Temperature)) / denominator;
                }
            }
        }
        void ApplyLinkRegion(int group)
        {
            int region = activeLinkRegions[group];
            for (int row = linkOffsets[region]; row < linkOffsets[region + 1]; row++)
            {
                int a = linkA[row], b = linkB[row]; double ca = capacities[a], cb = capacities[b];
                double difference = temperature[a] - temperature[b], conductance = conductances[row];
                double energy = difference * conductance * delta / (1 + conductance * delta * (1 / ca + 1 / cb));
                temperature[a] -= energy / ca; temperature[b] += energy / cb;
            }
        }
    }
}
