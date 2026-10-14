using KspContinuum;

internal static partial class Program
{
    static void RunColumnarLifecycle(string output, Receipt receipt, CapacityFixture fixture, OwnedRuntimeHost host,
        OwnedRuntimeControls controls, OwnedRuntimeEnvironment environment, OwnedRuntimeStepPlan plan)
    {
        var options = fixture.Options;
        var limits = new OwnedRuntimeStateLimits(maximumParts: OwnedRuntimeCapacity.Research.MaxParts,
            maximumBodies: OwnedRuntimeCapacity.Research.MaxParts, maximumResources: OwnedRuntimeCapacity.Research.MaxResources,
            maximumProviderStates: OwnedRuntimeCapacity.Research.MaxProviderStates, maximumConnections: OwnedRuntimeCapacity.Research.MaxConnections,
            maximumSystemRows: 1048576, maximumEncodedBytes: 8589934592L, maximumLogicalElements: 536870912L,
            maximumAllocationBytes: 17179869184L);
        var provenance = new OwnedRuntimeProvenance("generated", host.World.Definition.Id, receipt.FixtureSha256, CapacityFixture.Recipe);
        string checkpoint = Path.Combine(output, "checkpoint.ocss");
        Measure(receipt, "columnar-write", () => { Store(checkpoint, host, provenance, controls, environment, plan, limits); return true; });
        receipt.CheckpointBytes = new FileInfo(checkpoint).Length;
        receipt.CheckpointSha256 = Measure(receipt, "columnar-disk-hash", () => FileHash(checkpoint));
        var restored = Measure(receipt, "columnar-read-restore", () => { using var file = File.OpenRead(checkpoint); return OwnedRuntimeStateStore.Read(file, limits); });
        Measure(receipt, "restored-exact-check", () =>
        {
            VerifyStore(checkpoint, restored.Host, restored.Provenance, restored.Controls, restored.Environment, restored.Plan, limits);
            receipt.RestoredPacketIdentical = true; return true;
        });
        Measure(receipt, "uncommitted-rollback-check", () =>
        {
            using (var step = host.World.BeginStep(host.World.Stamp, options.Delta, fixture.Input))
            { if (host.World.ResourceCount > 0) step.SetResourceQuantity(0, 0); if (host.World.Definition.ProviderStateCount > 0) step.SetProviderState(0, 77); step.SetTemperature(0, 310); }
            VerifyStore(checkpoint, host, provenance, controls, environment, plan, limits);
            receipt.UncommittedBankUnchanged = true; return true;
        });
        receipt.Systems = host.Systems.Counters.Snapshot(); receipt.BankWork = host.World.Work;
        Measure(receipt, "continuation-original", () =>
        { for (int s = 0; s < options.ContinuationSteps; s++) { Cancellation(); host.Advance(options.Delta, fixture.Input, environment, controls); receipt.ContinuationSteps++; Frontier(receipt, host.World); } return true; });
        Measure(receipt, "continuation-restored", () =>
        { for (int s = 0; s < options.ContinuationSteps; s++) { Cancellation(); restored.Host.Advance(options.Delta, fixture.Input, restored.Environment, restored.Controls); } return true; });
        Measure(receipt, "continuation-exact-check", () =>
        {
            string original = Path.Combine(output, "continuation-original.ocss"), copy = Path.Combine(output, "continuation-restored.ocss");
            Store(original, host, provenance, controls, environment, plan, limits);
            Store(copy, restored.Host, restored.Provenance, restored.Controls, restored.Environment, restored.Plan, limits);
            Need(FilesEqual(original, copy), "Complete original/restored continuation differs.");
            receipt.ContinuationPacketIdentical = true; receipt.ContinuationSha256 = FileHash(original);
            Check(fixture, host.World, receipt); Check(fixture, restored.Host.World, receipt); return true;
        });
    }
    static void Store(string path, OwnedRuntimeHost host, OwnedRuntimeProvenance provenance, OwnedRuntimeControls controls,
        OwnedRuntimeEnvironment environment, OwnedRuntimeStepPlan plan, OwnedRuntimeStateLimits limits)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768);
        OwnedRuntimeStateStore.Write(file, host, provenance, controls, environment, plan, limits); file.Flush(true);
    }
    static void VerifyStore(string path, OwnedRuntimeHost host, OwnedRuntimeProvenance provenance, OwnedRuntimeControls controls,
        OwnedRuntimeEnvironment environment, OwnedRuntimeStepPlan plan, OwnedRuntimeStateLimits limits)
    {
        using var expected = File.OpenRead(path); using var output = new StateComparisonStream(expected);
        OwnedRuntimeStateStore.Write(output, host, provenance, controls, environment, plan, limits); output.Complete();
    }
    sealed class StateComparisonStream : Stream
    {
        readonly Stream expected;
        readonly byte[] scratch = new byte[32768];
        public StateComparisonStream(Stream expected) { this.expected = expected; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, scratch.Length);
                expected.ReadExactly(scratch.AsSpan(0, count));
                Need(buffer[..count].SequenceEqual(scratch.AsSpan(0, count)), "Complete streamed columnar image differs.");
                buffer = buffer[count..];
            }
        }
        public override void WriteByte(byte value) { Need(expected.ReadByte() == value, "Complete streamed columnar image differs."); }
        public void Complete() { Need(expected.ReadByte() == -1, "Streamed columnar image ended early."); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    static bool FilesEqual(string left, string right)
    {
        using var a = File.OpenRead(left); using var b = File.OpenRead(right);
        if (a.Length != b.Length) return false;
        byte[] first = new byte[32768], second = new byte[32768];
        while (true)
        {
            int count = a.Read(first); if (count == 0) return b.ReadByte() == -1;
            int got = 0; while (got < count) { int n = b.Read(second, got, count - got); if (n == 0) return false; got += n; }
            if (!first.AsSpan(0, count).SequenceEqual(second.AsSpan(0, count))) return false;
        }
    }
}
