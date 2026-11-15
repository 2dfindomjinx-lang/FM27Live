using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Stadiums;

namespace Fm27Editor.Core.Tests;

public class StadiumBulkReadingTests
{
    [Fact]
    public void Repeated_national_stadium_anchors_do_not_rescan_an_accepted_run_or_hide_a_second_run()
    {
        // Synthetic topology using the independently measured classic layout: nation stadium +0x118,
        // stadium stride 0xC0, vtable +0x4DEDC18 and ID +0xC. There is a hole between the two runs.
        const ulong holder = 0x7000_0000, vector = 0x7001_0000, nations = 0x7100_0000, stadiums = 0x7500_0000;
        var world = new TestWorld();
        TestWorld.Set64(world.Memory.Map(TestWorld.ModuleBase + 0x56F5078, 8), 0, holder);
        var head = world.Memory.Map(holder, 0x580);
        TestWorld.Set64(head, 0x20, vector);
        TestWorld.Set64(head, 0x28, vector + 4 * 8);
        var list = world.Memory.Map(vector, 4 * 8);
        int[] anchors = [2, 1, 2, 5];
        for (int i = 0; i < anchors.Length; i++)
        {
            ulong nation = nations + (ulong)i * 0x1000;
            TestWorld.Set64(list, i * 8, nation);
            var bytes = world.Memory.Map(nation, 0x120);
            TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + 0x4CDF658);
            TestWorld.Set64(bytes, 0x118, stadiums + (ulong)anchors[i] * 0xC0);
        }
        for (int i = 0; i < 6; i++)
        {
            var bytes = world.Memory.Map(stadiums + (ulong)i * 0xC0, 0xC0);
            if (i == 3) continue;
            TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + 0x4DEDC18);
            BitConverter.GetBytes(11 + i).CopyTo(bytes, 0xC);
        }
        var probe = new Probe(world.Memory);

        var records = new StadiumRepository(probe, world.Profile, TestWorld.ModuleBase).LoadAll();

        Assert.Equal(new[] { 11, 12, 13, 15, 16 }, records.Select(s => s.Id));
        Assert.Equal(records.Count, records.Select(s => s.Address).Distinct().Count());
        // The first record is checked once while expanding the first anchor. Later anchors inside the accepted
        // run must not walk back to it again, even when speculative page reads cannot be used in sparse memory.
        Assert.Equal(1, probe.Requests.Count(r => r == (stadiums, 8)));
        Assert.Equal(0, world.Memory.Writes);
    }

    sealed class Probe(IProcessMemory memory) : IProcessMemory
    {
        public List<(ulong Address, int Length)> Requests { get; } = [];
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            Requests.Add((address, buffer.Length));
            return memory.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new InvalidOperationException();
    }
}
