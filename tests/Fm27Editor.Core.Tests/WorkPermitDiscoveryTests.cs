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

    [Fact]
    public void Pipelined_first_samples_match_scalar_discovery_across_candidate_batches()
    {
        var world = new DiscoveryWorld();
        for (int i = 0; i < 1030; i++) world.AddTable(i * 32, i == 1029 ? 200 : 100, badNationAt: i == 1029 ? -1 : 0);
        var expected = 0x100000UL + 1029 * 32;
        Assert.Equal(expected, world.Table().Locate());
        Assert.True(world.Memory.BatchSizes.Count >= 6);
        Assert.All(world.Memory.BatchSizes, count => Assert.InRange(count, 0, 1024));
        WorkPermitTable.Forget();

        var scalar = new WithoutScatter(world.Memory);
        Assert.Equal(expected, new WorkPermitTable(scalar, new TestWorld().Profile, TestWorld.ModuleBase).Locate());
    }

    [Fact]
    public void Duplicate_begin_record_and_nation_addresses_share_stage_reads_without_losing_candidates()
    {
        var world = new DiscoveryWorld();
        var first = world.AddTable(0, 100);
        world.DuplicateHeader(0, 32);
        var larger = world.AddTable(64, 200);
        Assert.True(world.Raw.TryWrite(larger.Begin, BitConverter.GetBytes(first.Begin + 0x1000)));

        Assert.Equal(larger.Header, world.Table().Locate());

        Assert.Equal(1, world.Memory.BatchRequests.Count(r => r == (first.Begin, 8)));
        Assert.Equal(1, world.Memory.BatchRequests.Count(r => r == (first.Begin + 0x1000, 32)));
        Assert.Equal(1, world.Memory.BatchRequests.Count(r => r == (0x110000UL, 8)));
    }

    [Fact]
    public void A_failed_shared_first_sample_is_retried_in_the_next_candidate_batch()
    {
        var world = new DiscoveryWorld();
        var table = world.AddTable(0, 100);
        for (int i = 1; i < 1025; i++) world.DuplicateHeader(0, i * 32);
        bool failed = false;
        world.Memory.FailRead = (address, length) =>
        {
            if (address != table.Begin || length != 8 || failed) return false;
            failed = true;
            return true;
        };

        Assert.Equal(table.Header + 1024 * 32, world.Table().Locate());
        Assert.Equal(2, world.Memory.BatchRequests.Count(r => r == (table.Begin, 8)));
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

    sealed class Probe(IProcessMemory source, MemoryRegion region) : IProcessMemory, IMemoryRegions, IScatterReadMemory
    {
        public List<(ulong Address, int Length)> Reads { get; } = [];
        public Action<ulong, int>? BeforeRead { get; set; }
        public Func<ulong, int, bool>? FailRead { get; set; }
        public List<int> BatchSizes { get; } = [];
        public List<(ulong Address, int Length)> BatchRequests { get; } = [];
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) => [region];
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            Reads.Add((address, buffer.Length));
            BeforeRead?.Invoke(address, buffer.Length);
            if (FailRead?.Invoke(address, buffer.Length) == true) return false;
            return source.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void ReadMany(IReadOnlyList<MemoryReadRequest> requests, Span<bool> succeeded)
        {
            BatchSizes.Add(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                BatchRequests.Add((requests[i].Address, requests[i].Destination.Length));
                succeeded[i] = TryRead(requests[i].Address, requests[i].Destination.Span);
            }
        }
    }

    sealed class WithoutScatter(Probe source) : IProcessMemory, IMemoryRegions
    {
        public bool TryRead(ulong address, Span<byte> buffer) => source.TryRead(address, buffer);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) =>
            source.EnumerateWritableRegions(imageStart, imageEnd);
    }
}
