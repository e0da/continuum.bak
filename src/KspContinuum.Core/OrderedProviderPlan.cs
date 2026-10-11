using System;

namespace KspContinuum
{
    public sealed class OrderedProviderRow
    {
        readonly object[] members;
        public OrderedProviderRow(object part, object collection, object[] source)
        {
            if (part == null || collection == null || source == null) throw new ArgumentException("Provider row inputs required.");
            Part = part; Collection = collection; members = (object[])source.Clone();
        }
        public object Part { get; private set; }
        public object Collection { get; private set; }
        public int Count { get { return members.Length; } }
        public bool Matches(object part, object collection, int count)
        { return ReferenceEquals(Part, part) && ReferenceEquals(Collection, collection) && count == Count; }
        public bool MatchesMember(int index, object member)
        { return index >= 0 && index < members.Length && ReferenceEquals(members[index], member); }
    }
    public sealed class OrderedProviderPlan
    {
        readonly OrderedProviderRow[] rows;
        public OrderedProviderPlan(long generation, object owner, object collection, OrderedProviderRow[] source)
        {
            if (generation < 1 || owner == null || collection == null || source == null || source.Length > 4096) throw new ArgumentException("Provider plan identity/count differs.");
            rows = (OrderedProviderRow[])source.Clone();
            foreach (var row in rows) if (row == null) throw new ArgumentException("Provider row absent.");
            Generation = generation; Owner = owner; Collection = collection;
        }
        public long Generation { get; private set; }
        public object Owner { get; private set; }
        public object Collection { get; private set; }
        public int Count { get { return rows.Length; } }
        public bool Matches(object owner, object collection, int count)
        { return ReferenceEquals(Owner, owner) && ReferenceEquals(Collection, collection) && count == rows.Length; }
        public OrderedProviderRow At(int index)
        { return index >= 0 && index < rows.Length ? rows[index] : null; }
    }
}
