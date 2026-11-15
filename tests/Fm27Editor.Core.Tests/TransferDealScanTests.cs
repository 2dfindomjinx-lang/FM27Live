using System.Text.Json.Nodes;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public sealed class TransferDealScanTests
{
    const ulong Module = 0x7FFB18130000, Heap = 0x60000000;
    const int Chunk = 1 << 22;

    [Theory]
    [InlineData("27.0.0+2493426-gamepass.json", 0x4EE4098u)]
    [InlineData("27.0.0+2493426-steam.json", 0x4F3B2E8u)]
    public void Finds_every_existing_deal_class_in_order_across_chunk_and_region_boundaries(string profileFile, uint transferVtable)
    {
        var profile = Load(profileFile);
        var raw = new FakeMemory();
        var bytes = raw.Map(Heap, Chunk + 21);
        TestWorld.Set64(bytes, 0, Module + transferVtable);
        TestWorld.Set64(bytes, 24, Module + 74_494_248);
        TestWorld.Set64(bytes, Chunk - 8, Module + 83_071_720);
        TestWorld.Set64(bytes, Chunk, Module + transferVtable);
        TestWorld.Set64(bytes, Chunk + 8, Module + 74_494_248);
        const ulong laterRegion = Heap - 0x10000, moduleRegion = Heap - 0x20000;
        TestWorld.Set64(raw.Map(laterRegion, 8), 0, Module + 83_071_720);
        TestWorld.Set64(raw.Map(moduleRegion, 8), 0, Module + transferVtable);
        var memory = new ScanMemory(raw, [new(Heap, (ulong)bytes.Length, false), new(moduleRegion, 8, true), new(laterRegion, 8, false)]);

        Assert.Equal(new[] { Heap, Heap + 24, Heap + Chunk - 8, Heap + Chunk, Heap + Chunk + 8, laterRegion },
            new TransferDealReader(memory, profile, Module).Scan());
        Assert.Equal(3, memory.Reads.Count);
        Assert.Equal(0, raw.Writes);
    }

    [Fact]
    public void Duplicate_recognized_vtable_values_produce_one_match_per_address()
    {
        var json = JsonNode.Parse(File.ReadAllText(TestWorld.ProfilePath))!;
        json["player"]!["proposals"]!["transferVtable"] = $"0x{74_494_248:X}";
        var profile = OffsetProfile.Load(json.ToJsonString());
        var raw = new FakeMemory();
        var bytes = raw.Map(Heap, 24);
        TestWorld.Set64(bytes, 0, Module + 74_494_248);
        TestWorld.Set64(bytes, 16, Module + 74_494_248);

        Assert.Equal(new[] { Heap, Heap + 16 }, new TransferDealReader(new ScanMemory(raw, [new(Heap, 24, false)]), profile, Module).Scan());
    }

    [Fact]
    public void Profiles_without_proposals_still_scan_both_preexisting_legacy_classes()
    {
        var json = JsonNode.Parse(File.ReadAllText(TestWorld.ProfilePath))!;
        json["player"]!.AsObject().Remove("proposals");
        var profile = OffsetProfile.Load(json.ToJsonString());
        var raw = new FakeMemory();
        var bytes = raw.Map(Heap, 24);
        TestWorld.Set64(bytes, 0, Module + 74_494_248);
        TestWorld.Set64(bytes, 8, Module + 83_071_720);
        TestWorld.Set64(bytes, 16, Module + 0x1234);

        Assert.Equal(new[] { Heap, Heap + 8 }, new TransferDealReader(new ScanMemory(raw, [new(Heap, 24, false)]), profile, Module).Scan());
    }

    [Fact]
    public void A_failed_wide_read_recovers_every_exact_page_before_scanning()
    {
        var raw = new FakeMemory();
        TestWorld.Set64(raw.Map(Heap, 4096), 8, Module + 74_494_248);
        TestWorld.Set64(raw.Map(Heap + 4096, 4096), 4088, Module + 83_071_720);
        var memory = new ScanMemory(raw, [new(Heap, 8192, false)]);

        Assert.Equal(new[] { Heap + 8, Heap + 8184 }, new TransferDealReader(memory, Load(), Module).Scan());
        Assert.Equal(new[] { (Heap, 8192), (Heap, 4096), (Heap + 4096, 4096) }, memory.Reads);
    }

    [Fact]
    public void A_real_unreadable_range_throws_instead_of_returning_the_prior_partial_hits()
    {
        var raw = new FakeMemory();
        TestWorld.Set64(raw.Map(Heap, 8), 0, Module + 74_494_248);
        var memory = new ScanMemory(raw, [new(Heap, 8, false), new(Heap + 0x10000, 4096, false)]);

        Assert.Throws<MemoryAccessException>(() => new TransferDealReader(memory, Load(), Module).Scan());
    }

    [Fact]
    public void No_enumeration_capability_is_unavailable_instead_of_a_successful_empty_scan()
    {
        Assert.Throws<NotSupportedException>(() => new TransferDealReader(new FakeMemory(), Load(), Module).Scan());
    }

    static OffsetProfile Load(string file = "27.0.0+2493426-gamepass.json") =>
        OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", file));

    sealed class ScanMemory(IProcessMemory source, IReadOnlyList<MemoryRegion> regions) : IProcessMemory, IMemoryRegions
    {
        public List<(ulong Address, int Length)> Reads { get; } = [];
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) => regions;
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            Reads.Add((address, buffer.Length));
            return source.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new NotSupportedException();
    }
}
