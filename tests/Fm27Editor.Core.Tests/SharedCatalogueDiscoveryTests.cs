using System.Text.Json.Nodes;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class SharedCatalogueDiscoveryTests
{
    [Fact]
    public void Region_scan_keeps_a_record_crossing_the_chunk_boundary()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.0+2493426-gamepass.json"));
        var raw = new FakeMemory();
        const ulong start = 0x700000;
        const int chunk = 1 << 22;
        var data = raw.Map(start, chunk + 64);
        foreach (var (offset, id) in new[] { (0x20, 6), (chunk - 8, 15), (chunk + 24, 29) })
        {
            TestWorld.Set64(data, offset, TestWorld.ModuleBase + 0x4D78508);
            BitConverter.GetBytes(id).CopyTo(data, offset + 0x10);
        }
        var memory = new RegionProbe(raw, [new MemoryRegion(start, (ulong)data.Length, false)]);

        var regions = new NationAgreements(memory, profile, TestWorld.ModuleBase).Regions();

        Assert.Equal(new[] { 6, 15, 29 }, regions.Select(r => r.Id));
        Assert.Equal(start + chunk - 8, regions[1].Address);
        Assert.Contains((start + chunk - 8, 24), memory.Reads);
        Assert.Equal(0, raw.Writes);
    }

    [Fact]
    public void Club_offers_use_the_live_manager_vector_with_id_kind_and_status_checks()
    {
        var world = new OfferWorld();
        world.Offer(0, id: 9, status: 10); // Wrong ID for its slot.
        world.Offer(1, id: 1, status: 19, databasePerson: true);
        world.Offer(2, id: 2, status: 20); // Completed: not shown.
        world.Offer(3, id: 3, status: 10, wrongKind: true);
        var reader = world.Reader();

        var first = Assert.Single(reader.ReadForClub(OfferWorld.Club, [world.Player], []));
        Assert.Equal((1, "Kobbie Mainoo"), (first.Proposal.Id, first.PersonName));
        Assert.Equal(0, world.Memory.Enumerations);

        // The same reader sees a later offer without a session cache or heap scan.
        world.SetOfferKind(3);
        Assert.Equal(new[] { 1, 3 }, reader.ReadForClub(OfferWorld.Club, [world.Player], []).Select(o => o.Proposal.Id));
        Assert.Equal(0, world.Memory.Enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_valid_empty_manager_vector_is_loaded_without_a_heap_scan(bool allocated)
    {
        var world = new OfferWorld();
        SetVector(world.Manager, 0x20, allocated ? OfferWorld.Vector : 0, 0);
        var reader = world.Reader();

        reader.EnsureLoaded();
        Assert.True(reader.HasCachedOffers);
        Assert.Empty(reader.ReadForClub(OfferWorld.Club, [world.Player], []));
        Assert.Equal(0, world.Memory.Enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_manager_identity_or_vector_capacity_keeps_the_scan_fallback(bool badVtable)
    {
        var world = new OfferWorld();
        if (badVtable) TestWorld.Set64(world.Manager, 0, 0);
        else TestWorld.Set64(world.Manager, 0x30, OfferWorld.Vector - 8);

        world.Reader().EnsureLoaded();

        Assert.Equal(1, world.Memory.Enumerations);
    }

    static void SetVector(byte[] bytes, int offset, ulong begin, int count)
    {
        TestWorld.Set64(bytes, offset, begin);
        TestWorld.Set64(bytes, offset + 8, begin + (ulong)count * 8);
        TestWorld.Set64(bytes, offset + 16, begin + (ulong)count * 8);
    }

    sealed class OfferWorld
    {
        public const ulong Club = 0x700000, Vector = 0x720000;
        const ulong Team = 0x710000, ManagerAddress = 0x730000, IdHeader = 0x740000, IdList = 0x750000, Offers = 0x800000;
        readonly TestWorld world = new();
        readonly OffsetProfile profile;
        readonly byte[] pointers;
        public byte[] Manager { get; }
        public RegionProbe Memory { get; }
        public PlayerRecord Player { get; }

        public OfferWorld()
        {
            var json = JsonNode.Parse(File.ReadAllText(TestWorld.ProfilePath))!;
            // Synthetic root keys exercise the measured manager/vector schema without relying on an actual process.
            json["player"]!["proposals"]!["managerRva"] = "0x6000000";
            json["player"]!["proposals"]!["managerVtable"] = "0x6001000";
            profile = OffsetProfile.Load(json.ToJsonString());
            TestWorld.Set64(world.Memory.Map(TestWorld.ModuleBase + 0x6000000, 8), 0, ManagerAddress);
            Manager = world.Memory.Map(ManagerAddress, 0x40);
            TestWorld.Set64(Manager, 0, TestWorld.ModuleBase + 0x6001000);
            SetVector(Manager, 0x20, Vector, 4);
            pointers = world.Memory.Map(Vector, 32);
            TestWorld.Set64(world.Memory.Map(Club, 0xD0), 0, TestWorld.ModuleBase + 0x4CDA1F8);
            var team = world.Memory.Map(Team, 0x50);
            TestWorld.Set64(team, 0, TestWorld.ModuleBase + 0x4CE1108);
            TestWorld.Set64(team, 0x30, Club);
            var header = world.Memory.Map(IdHeader, 24);
            TestWorld.Set64(header, 0, IdList); TestWorld.Set64(header, 8, IdList + 4);
            BitConverter.GetBytes(1).CopyTo(world.Memory.Map(IdList, 4), 0);
            Assert.True(world.Memory.TryWrite(TestWorld.Mainoo + 0x320, BitConverter.GetBytes(IdHeader)));
            Player = new PlayerRepository(world.Memory, profile, TestWorld.ModuleBase).Reload(TestWorld.Mainoo)!;
            Memory = new RegionProbe(world.Memory, []);
        }

        public void Offer(int slot, int id, byte status, bool databasePerson = false, bool wrongKind = false)
        {
            ulong address = Offers + (ulong)slot * 0x1000;
            var bytes = world.Memory.Map(address, 0x98);
            TestWorld.Set64(pointers, slot * 8, address);
            TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + (wrongKind ? 0x1234UL : 0x4F392E8UL));
            TestWorld.Set64(bytes, 0x38, databasePerson ? 0x900000 : TestWorld.Mainoo + 0x288);
            TestWorld.Set64(bytes, 0x40, Team);
            BitConverter.GetBytes(id).CopyTo(bytes, 0x5C);
            bytes[0x8D] = status;
        }

        public void SetOfferKind(int slot) => Assert.True(world.Memory.TryWrite(Offers + (ulong)slot * 0x1000,
            BitConverter.GetBytes(TestWorld.ModuleBase + 0x4F392E8)));
        public TransferProposalReader Reader() => new(Memory, profile, TestWorld.ModuleBase);
    }

    sealed class RegionProbe(IProcessMemory source, IReadOnlyList<MemoryRegion> regions) : IProcessMemory, IMemoryRegions
    {
        public int Enumerations { get; private set; }
        public List<(ulong Address, int Length)> Reads { get; } = [];
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
        {
            Enumerations++;
            return regions;
        }
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            Reads.Add((address, buffer.Length));
            return source.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => source.TryWrite(address, data);
    }
}
