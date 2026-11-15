using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// Prevent retirement on Steam 27.0.1+2503222, with bytes read from the calibration save (15/11/2026, game date 15/12/2026).
/// A player reported that after Prevent retirement FM still showed players as retiring. The editor removed the planned
/// retirement and set the keep-playing trait, but left the player's flag +0x2A0 bit 0x01 set: it was set on every player
/// with a planned retirement, and FMRTE's keep-playing write on Benzema cleared it. Aspas (planned 30/6/2027) had 2A0=01,
/// 2A1=02. Ronaldo (no planned entry, "retiring after spell at current club") had 2A0=01, 2A1=82. Benzema's entry had been
/// blanked in place by FMRTE (person -1, dates 1/1/1900) and is still listed.
/// </summary>
public class KeepPlayingFlagsTests
{
    const ulong Module = 0x7000_0000_0000, Manager = 0x1000_0000, Pointers = 0x2000_0000, Index = 0x2001_0000,
        AspasEntry = 0x3000_0000, BenzemaEntry = 0x3000_0100, Aspas = 0x4000_0000, Ronaldo = 0x4000_1000;
    const uint AspasPerson = 27638, BenzemaPerson = 6087, RonaldoPerson = 1759;
    readonly FakeMemory memory = new();
    readonly OffsetProfile profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.1+2503222-steam.json"));

    public KeepPlayingFlagsTests()
    {
        TestWorld.Set64(memory.Map(Module + profile.RetirementManagerRva!.Value, 8), 0, Manager);
        var manager = memory.Map(Manager, 0xA8);
        TestWorld.Set64(manager, 0, Module + profile.RetirementManagerVtable!.Value);
        Vector(manager, 0x30, Pointers, 2, 8);
        Vector(manager, 0x90, Index, 2, 16);
        var pointers = memory.Map(Pointers, 16);
        TestWorld.Set64(pointers, 0, BenzemaEntry); TestWorld.Set64(pointers, 8, AspasEntry);
        var index = memory.Map(Index, 32);
        IndexEntry(index, 0, BenzemaPerson, BenzemaEntry); IndexEntry(index, 16, AspasPerson, AspasEntry);
        Convert.FromHexString("F66B0000880400004F01E807B500EB074E01E80700000500").CopyTo(memory.Map(AspasEntry, 24), 0);
        Convert.FromHexString("FFFFFFFFFFFFFFFF4F01E80701006C0701006C0700000500").CopyTo(memory.Map(BenzemaEntry, 24), 0);
        Player(Aspas, 67010455, AspasPerson, 0x01, 0x02);
        Player(Ronaldo, 735216, RonaldoPerson, 0x01, 0x82);
    }

    void Player(ulong address, int uid, uint person, byte flags2A0, byte flags2A1)
    {
        var p = memory.Map(address, 0x3C8);
        TestWorld.Set64(p, 0, Module + profile.PlayerVtableRvas[0]);
        BitConverter.GetBytes(person).CopyTo(p, 0x290);
        BitConverter.GetBytes(uid).CopyTo(p, profile.Fields.Id!.Value);
        p[0x2A0] = flags2A0;
        p[0x2A1] = flags2A1;
        p[profile.PreferredMoves!.Value + 6] = 0x02;
    }

    static void Vector(byte[] owner, int offset, ulong begin, int count, int size)
    {
        TestWorld.Set64(owner, offset, begin); TestWorld.Set64(owner, offset + 8, begin + (ulong)(count * size));
        TestWorld.Set64(owner, offset + 16, begin + (ulong)(count * size));
    }

    static void IndexEntry(byte[] index, int at, uint person, ulong entry)
    {
        BitConverter.GetBytes(person).CopyTo(index, at);
        Convert.FromHexString("F3010000").CopyTo(index, at + 4);
        TestWorld.Set64(index, at + 8, entry);
    }

    PlayerRecord Read(ulong address) => new PlayerRepository(memory, profile, Module).Reload(address)!;

    [Fact]
    public void An_entry_FMRTE_blanked_is_read_as_no_retirement()
    {
        var plans = new PlannedRetirements(memory, profile, Module).ReadAll();
        Assert.Equal(new DateOnly(2027, 6, 30), plans[AspasPerson].Date);
        Assert.False(plans.ContainsKey(BenzemaPerson));
        Assert.Single(plans);
    }

    [Fact]
    public void Aspas_loses_the_planned_retirement_and_the_retirement_flag_and_undo_puts_both_back()
    {
        var writer = new KeepPlayingWriter(memory, profile, Module);
        var listBefore = memory.Read(Pointers, 16);
        var plan = writer.Plan(Read(Aspas));
        writer.Apply(plan);
        Assert.False(new PlannedRetirements(memory, profile, Module).ReadAll().ContainsKey(AspasPerson));
        Assert.Equal(0x00, memory.Read(Aspas + 0x2A0, 1)[0]);
        Assert.Equal(0x02, memory.Read(Aspas + 0x2A1, 1)[0]);          // only bit 0x80 belongs to the flag; it was clear
        writer.Undo(plan);
        Assert.Equal(0x01, memory.Read(Aspas + 0x2A0, 1)[0]);
        Assert.Equal(listBefore, memory.Read(Pointers, 16));
    }

    [Fact]
    public void Ronaldo_without_a_planned_entry_loses_both_flags_and_keeps_the_other_bits()
    {
        var writer = new KeepPlayingWriter(memory, profile, Module);
        var plan = writer.Plan(Read(Ronaldo));
        writer.Apply(plan);
        Assert.Equal(0x00, memory.Read(Ronaldo + 0x2A0, 1)[0]);
        Assert.Equal(0x02, memory.Read(Ronaldo + 0x2A1, 1)[0]);
        writer.Undo(plan);
        Assert.Equal(0x01, memory.Read(Ronaldo + 0x2A0, 1)[0]);
        Assert.Equal(0x82, memory.Read(Ronaldo + 0x2A1, 1)[0]);
    }
}
