using System.Text.Json.Nodes;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class WorkPermitManagerDiscoveryTests
{
    [Theory]
    [InlineData("27.0.1+2494687-gamepass.json", 0x53A4D68u, 0x49D5AD8u, 0x4A21168u)]
    [InlineData("27.0.1+2494687-steam.json", 0x580EA58u, 0x4D90898u, 0x4DE36F8u)]
    public void Both_storefront_profiles_discover_a_fully_valid_vector_without_new_offsets(
        string profileName, uint rootRva, uint managerVtable, uint nationVtable)
    {
        WorkPermitTable.Forget();
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", profileName));
        var raw = new FakeMemory();
        const ulong manager = 0xA00000, nation = 0xA10000, vector = 0xA20000, records = 0xA30000;
        TestWorld.Set64(raw.Map(TestWorld.ModuleBase + rootRva, 8), 0, manager);
        var owner = raw.Map(manager, 0x7D8); // Existing Windows offer serial is +0x7D4.
        TestWorld.Set64(owner, 0, TestWorld.ModuleBase + managerVtable);
        TestWorld.Set64(owner, 0x138, vector);
        TestWorld.Set64(owner, 0x140, vector + 800);
        TestWorld.Set64(owner, 0x148, vector + 800);
        TestWorld.Set64(raw.Map(nation, 8), 0, TestWorld.ModuleBase + nationVtable);
        var pointers = raw.Map(vector, 800);
        var bytes = raw.Map(records, 3200);
        for (int i = 0; i < 100; i++)
        {
            TestWorld.Set64(pointers, i * 8, records + (ulong)i * 32);
            TestWorld.Set64(bytes, i * 32, nation);
            BitConverter.GetBytes((uint)(1000 + i)).CopyTo(bytes, i * 32 + 8);
        }

        Assert.Equal(manager + 0x138, new WorkPermitTable(raw, profile, TestWorld.ModuleBase).Locate());
        Assert.Equal(0, raw.Writes);
    }

    [Fact]
    public void A_unique_fully_valid_table_in_the_measured_owner_avoids_a_heap_scan()
    {
        var world = new World();
        world.Vector(0x20, 200, invalidRecord: 0);
        world.Vector(0x130, 1300);

        Assert.Equal(World.Manager + 0x130, world.Table().Locate());
        Assert.Equal(0, world.Memory.Enumerations);
        Assert.Equal(1300, world.Memory.RecordReads.Where(a => a >= 0x910000 && a < 0x910000 + 1300 * 32).Distinct().Count());
    }

    [Fact]
    public void Invalid_record_outside_the_original_seven_samples_cannot_win_owner_discovery()
    {
        var world = new World();
        world.Vector(0x20, 200, invalidRecord: 25);
        world.Vector(0x130, 100);

        Assert.Equal(World.Manager + 0x130, world.Table().Locate());
        Assert.Equal(0, world.Memory.Enumerations);
    }

    [Fact]
    public void Two_fully_valid_owner_vectors_keep_the_global_largest_table_rule()
    {
        var world = new World();
        world.Vector(0x20, 100);
        world.Vector(0x130, 200);

        Assert.Equal(World.Manager + 0x130, world.Table().Locate());
        Assert.Equal(1, world.Memory.Enumerations);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-vtable")]
    [InlineData("old-profile")]
    public void Unavailable_owner_metadata_or_identity_keeps_the_heap_fallback(string problem)
    {
        var world = new World(problem);
        world.Vector(0x130, 100);

        Assert.Equal(World.Manager + 0x130, world.Table().Locate());
        Assert.Equal(1, world.Memory.Enumerations);
    }

    [Fact]
    public void Unreadable_owner_candidate_does_not_establish_uniqueness()
    {
        var world = new World();
        world.Vector(0x130, 100);
        TestWorld.Set64(world.Owner, 0x20, 0xDEAD0000);
        TestWorld.Set64(world.Owner, 0x28, 0xDEAD0000 + 800);
        TestWorld.Set64(world.Owner, 0x30, 0xDEAD0000 + 800);

        Assert.Equal(World.Manager + 0x130, world.Table().Locate());
        Assert.Equal(1, world.Memory.Enumerations);
    }

    [Fact]
    public void A_proven_invalid_record_disproves_a_vector_despite_another_unreadable_record()
    {
        var world = new World();
        world.Vector(0x20, 200, invalidRecord: 25);
        world.Vector(0x130, 100);
        world.Memory.FailRead = (address, _) => address == 0x810000;

        Assert.Equal(World.Manager + 0x130, world.Table().Locate());
        Assert.Equal(0, world.Memory.Enumerations);
    }

    [Fact]
    public void Missing_record_without_any_disproof_preserves_global_fallback()
    {
        var world = new World();
        world.Vector(0x20, 200);
        world.Vector(0x130, 100);
        world.Memory.FailRead = (address, _) => address == 0x810000;

        Assert.Equal(World.Manager + 0x130, world.Table().Locate());
        Assert.Equal(1, world.Memory.Enumerations);
    }

    [Fact]
    public void Changed_manager_root_is_not_published_even_when_the_old_records_still_validate()
    {
        var world = new World();
        world.Vector(0x130, 100);
        int rootReads = 0;
        world.Memory.BeforeRead = (address, length) =>
        {
            if (address == TestWorld.ModuleBase + World.RootRva && ++rootReads == 2)
                TestWorld.Set64(world.Root, 0, World.Manager + 8);
        };

        Assert.Throws<StaleObjectException>(() => world.Table().Locate());
    }

    sealed class World
    {
        public const ulong Manager = 0x700000;
        public const uint RootRva = 0x6000000, ManagerVtable = 0x6001000;
        const ulong Nation = 0x710000;
        readonly FakeMemory raw = new();
        readonly OffsetProfile profile;
        int vectors;
        public byte[] Owner { get; }
        public byte[] Root { get; }
        public Probe Memory { get; }

        public World(string? problem = null)
        {
            WorkPermitTable.Forget();
            var json = JsonNode.Parse(File.ReadAllText(TestWorld.ProfilePath))!;
            if (problem != "old-profile")
            {
                json["player"]!["proposals"]!["managerRva"] = "0x6000000";
                json["player"]!["proposals"]!["managerVtable"] = "0x6001000";
            }
            profile = OffsetProfile.Load(json.ToJsonString());
            Root = raw.Map(TestWorld.ModuleBase + RootRva, 8);
            TestWorld.Set64(Root, 0, problem == "missing" ? 0 : Manager);
            Owner = raw.Map(Manager, 4096);
            TestWorld.Set64(Owner, 0, TestWorld.ModuleBase + (problem == "wrong-vtable" ? 0x1234UL : ManagerVtable));
            TestWorld.Set64(raw.Map(Nation, 4096), 0, TestWorld.ModuleBase + 0x4CDF658);
            Memory = new Probe(raw, new(Manager, 4096, false));
        }

        public void Vector(int offset, int count, int invalidRecord = -1)
        {
            ulong begin = 0x800000 + (ulong)vectors++ * 0x100000;
            ulong records = begin + 0x10000;
            var pointers = raw.Map(begin, count * 8);
            var bytes = raw.Map(records, count * 32);
            for (int i = 0; i < count; i++)
            {
                TestWorld.Set64(pointers, i * 8, records + (ulong)i * 32);
                TestWorld.Set64(bytes, i * 32, i == invalidRecord ? 0 : Nation);
                BitConverter.GetBytes((uint)(i + 10)).CopyTo(bytes, i * 32 + 8);
            }
            TestWorld.Set64(Owner, offset, begin);
            TestWorld.Set64(Owner, offset + 8, begin + (ulong)count * 8);
            TestWorld.Set64(Owner, offset + 16, begin + (ulong)count * 8);
        }

        public WorkPermitTable Table() => new(Memory, profile, TestWorld.ModuleBase);
    }

    sealed class Probe(IProcessMemory memory, MemoryRegion region) : IProcessMemory, IMemoryRegions
    {
        public int Enumerations { get; private set; }
        public List<ulong> RecordReads { get; } = [];
        public Action<ulong, int>? BeforeRead { get; set; }
        public Func<ulong, int, bool>? FailRead { get; set; }
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
        {
            Enumerations++;
            return [region];
        }
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            if (buffer.Length == 32) RecordReads.Add(address);
            BeforeRead?.Invoke(address, buffer.Length);
            if (FailRead?.Invoke(address, buffer.Length) == true) return false;
            return memory.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new NotSupportedException();

    }
}
