using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public struct OwnedRuntimeConnection
    {
        public readonly long Id, PartA, PartB;
        public readonly bool Locked;
        public OwnedRuntimeConnection(long id, long partA, long partB, bool locked = true)
        {
            if (partA == partB) throw new ArgumentException("A connection must join distinct authored parts.");
            Id = id; PartA = partA; PartB = partB; Locked = locked;
        }
    }

    public sealed class OwnedRuntimeTopology
    {
        readonly OwnedRuntimeConnection[] connections;
        readonly long[] regionIds;
        internal readonly int[] PartRegions, BodyRegions;
        internal readonly int[][] RegionBodies;
        public OwnedRuntimeDefinition Definition { get; private set; }
        public long Revision { get; private set; }
        public int RegionCount { get { return regionIds.Length; } }
        public int ConnectionCount { get { return connections.Length; } }
        public long GetRegionId(int region) { return regionIds[region]; }
        public int RegionOfPart(int part) { return PartRegions[part]; }
        public int RegionOfBody(int body) { return BodyRegions[body]; }
        public OwnedRuntimeConnection GetConnection(int slot) { return connections[slot]; }
        public OwnedRuntimeConnection[] CopyConnections() { return (OwnedRuntimeConnection[])connections.Clone(); }

        OwnedRuntimeTopology(OwnedRuntimeDefinition definition, OwnedRuntimeConnection[] connections, long revision)
        {
            Definition = definition; Revision = revision; this.connections = connections;
            var parent = new int[definition.PartCount]; var firstBody = new int[definition.BodyCount];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            for (int i = 0; i < firstBody.Length; i++) firstBody[i] = -1;
            for (int p = 0; p < definition.PartCount; p++)
            {
                int body = definition.BodySlot(definition.GetPart(p).BodyId);
                if (firstBody[body] < 0) firstBody[body] = p; else Union(parent, p, firstBody[body]);
            }
            foreach (var connection in connections)
            {
                int a = definition.PartSlot(connection.PartA), b = definition.PartSlot(connection.PartB);
                if (connection.PartA == connection.PartB) throw new ArgumentException("Self connection is invalid.");
                if (connection.Locked) Union(parent, a, b);
            }
            var roots = new SortedDictionary<long, int>();
            for (int p = 0; p < parent.Length; p++)
            {
                int root = Find(parent, p); long id = definition.GetPart(root).Id;
                if (!roots.ContainsKey(id)) roots.Add(id, root);
            }
            regionIds = new long[roots.Count]; var slots = new Dictionary<int, int>(); int next = 0;
            foreach (var root in roots) { regionIds[next] = root.Key; slots.Add(root.Value, next++); }
            PartRegions = new int[parent.Length]; BodyRegions = new int[firstBody.Length];
            for (int p = 0; p < parent.Length; p++) PartRegions[p] = slots[Find(parent, p)];
            var lists = new List<int>[roots.Count]; for (int r = 0; r < lists.Length; r++) lists[r] = new List<int>();
            for (int b = 0; b < firstBody.Length; b++)
            {
                BodyRegions[b] = PartRegions[firstBody[b]]; lists[BodyRegions[b]].Add(b);
            }
            RegionBodies = new int[lists.Length][];
            for (int r = 0; r < lists.Length; r++) RegionBodies[r] = lists[r].ToArray();
        }

        public static OwnedRuntimeTopology Compile(OwnedRuntimeDefinition definition,
            IEnumerable<OwnedRuntimeConnection> source, long revision = 0)
        {
            if (definition == null || source == null) throw new ArgumentNullException("definition/source");
            if (revision < 0) throw new ArgumentException("Topology revision must be nonnegative.");
            var rows = new List<OwnedRuntimeConnection>(); var ids = new HashSet<long>();
            foreach (var row in source)
            {
                if (rows.Count >= OwnedRuntimeDefinition.MaximumParts * 16 || !ids.Add(row.Id))
                    throw new ArgumentException("Connections must be bounded with unique identities.");
                rows.Add(row);
            }
            rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new OwnedRuntimeTopology(definition, rows.ToArray(), revision);
        }

        public OwnedRuntimeTopology WithLocked(long connectionId, bool locked)
        {
            var rows = CopyConnections(); bool found = false;
            for (int i = 0; i < rows.Length; i++) if (rows[i].Id == connectionId)
            { rows[i] = new OwnedRuntimeConnection(rows[i].Id, rows[i].PartA, rows[i].PartB, locked); found = true; }
            if (!found) throw new ArgumentException("Unknown connection identity.");
            return Compile(Definition, rows, checked(Revision + 1));
        }

        public OwnedRuntimeTopology WithDock(long connectionId, long partA, long partB)
        {
            foreach (var row in connections) if (row.Id == connectionId) throw new ArgumentException("Dock connection identity already exists.");
            var rows = new OwnedRuntimeConnection[connections.Length + 1]; Array.Copy(connections, rows, connections.Length);
            rows[connections.Length] = new OwnedRuntimeConnection(connectionId, partA, partB);
            return Compile(Definition, rows, checked(Revision + 1));
        }
        static int Find(int[] parent, int index)
        { while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; } return index; }
        static void Union(int[] parent, int a, int b)
        { a = Find(parent, a); b = Find(parent, b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
    }
}
