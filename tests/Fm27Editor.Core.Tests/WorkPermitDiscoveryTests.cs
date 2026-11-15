using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class WorkPermitDiscoveryTests
{
    [Fact]
    public void Selects_the_largest_valid_vector_and_keeps_the_first_header_when_counts_tie()
    {
        var world = new DiscoveryWorld();
        world.AddTable(0, 100);
        var winner = world.AddTable(32, 200);
        world.AddTable(64, 300, badNationAt: 0);
        world.AddTable(96, 200);
        world.DuplicateHeader(32, 128);

        Assert.Equal(winner.Header, world.Table().Locate());
        // The discovery candidates are read from a few shared pages, with a separate fresh validation of the
        // selected table. The former per-field implementation needed over 170 reads for these five headers.
        Assert.True(world.Memory.Reads.Count < 80, $"Unexpected process-read count: {world.Memory.Reads.Count}");
        Assert.Equal(0, world.Raw.Writes);
    }

    [Fact]
    public void Rejects_a_larger_vector_with_out_of_order_person_samples()
    {
        var world = new DiscoveryWorld();
        var winner = world.AddTable(0, 100);
        world.AddTable(32, 200, unsortedAt: 2);
        Assert.Equal(winner.Header, world.Table().Locate());
    }

    [Fact]
    public void Candidate_fields_at_allocation_edges_remain_readable()
    {
        var world = new DiscoveryWorld(sparse: true);
        var expected = world.AddTable(0, 100);
        Assert.Equal(expected.Header, world.Table().Locate());
    }

    [Fact]
    public void Does_not_publish_a_header_reallocated_after_the_scan_snapshot()
    {
        var world = new DiscoveryWorld();
        var candidate = world.AddTable(0, 100);
        world.Memory.BeforeRead = (address, length) =>
        {
            if (address == candidate.Header && length == 24)
                TestWorld.Set64(world.Headers, 0, candidate.Begin + 8);
        };

        Assert.Throws<StaleObjectException>(() => world.Table().Locate());
    }

    [Fact]
    public void Winner_validation_uses_live_records_after_cached_candidate_validation()
    {
        var world = new DiscoveryWorld();
        var candidate = world.AddTable(0, 100);
        world.Memory.BeforeRead = (address, length) =>
        {
            if (address == candidate.Header && length == 24)
                TestWorld.Set64(candidate.Records, 0, 0);
        };

        Assert.Throws<StaleObjectException>(() => world.Table().Locate());
    }

    [Fact]
    public void A_previously_located_header_is_revalidated_against_live_memory()
    {
        var world = new DiscoveryWorld();
        var candidate = world.AddTable(0, 100);
        var table = world.Table();
        Assert.Equal(candidate.Header, table.Locate());
        TestWorld.Set64(world.NationBytes, 0, 0);

        Assert.Throws<InvalidOperationException>(() => table.Locate());
    }

    sealed class DiscoveryWorld
    {
        const ulong HeaderPage = 0x100000, Nation = 0x110000;
        readonly TestWorld profile = new();
        readonly bool sparse;
        int tables;
        public FakeMemory Raw { get; } = new();
        public Probe Memory { get; }
        public byte[] Headers { get; }
        public byte[] NationBytes { get; }

        public DiscoveryWorld(bool sparse = false)
        {
            this.sparse = sparse;
            WorkPermitTable.Forget();
            Headers = Raw.Map(HeaderPage, 65536);
            NationBytes = Raw.Map(Nation, sparse ? 8 : 4096);
            TestWorld.Set64(NationBytes, 0, TestWorld.ModuleBase + 0x4CDF658);
            Memory = new Probe(Raw, new MemoryRegion(HeaderPage, 65536, false));
        }

        public (ulong Header, ulong Begin, byte[] Records) AddTable(int headerOffset, int count, int badNationAt = -1, int unsortedAt = -1)
        {
            ulong begin = 0x200000 + (ulong)tables++ * 0x10000;
            ulong records = begin + 0x1000;
            var pointers = Raw.Map(begin, sparse ? count * 8 : 4096);
            var bytes = Raw.Map(records, sparse ? count * 32 : (count * 32 + 4095) & ~4095);
            for (int i = 0; i < count; i++)
            {
                TestWorld.Set64(pointers, i * 8, records + (ulong)i * 32);
                TestWorld.Set64(bytes, i * 32, i == badNationAt ? 0 : Nation);
                BitConverter.GetBytes((uint)(i == unsortedAt ? 0 : i + 10)).CopyTo(bytes, i * 32 + 8);
            }
            TestWorld.Set64(Headers, headerOffset, begin);
            TestWorld.Set64(Headers, headerOffset + 8, begin + (ulong)count * 8);
            TestWorld.Set64(Headers, headerOffset + 16, begin + (ulong)count * 8);
            return (HeaderPage + (ulong)headerOffset, begin, bytes);
        }

        public void DuplicateHeader(int source, int destination) => Headers.AsSpan(source, 24).CopyTo(Headers.AsSpan(destination));
        public WorkPermitTable Table() => new(Memory, profile.Profile, TestWorld.ModuleBase);
    }

    sealed class Probe(IProcessMemory source, MemoryRegion region) : IProcessMemory, IMemoryRegions
    {
        public List<(ulong Address, int Length)> Reads { get; } = [];
        public Action<ulong, int>? BeforeRead { get; set; }
        public Func<ulong, int, bool>? FailRead { get; set; }
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) => [region];
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            Reads.Add((address, buffer.Length));
            BeforeRead?.Invoke(address, buffer.Length);
            if (FailRead?.Invoke(address, buffer.Length) == true) return false;
            return source.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new NotSupportedException();
    }
}
