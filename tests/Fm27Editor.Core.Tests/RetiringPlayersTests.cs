using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class RetiringPlayersTests
{
    const ulong Module = 0x7000_0000_0000, Manager = 0x1000_0000, Pointers = 0x2000_0000,
        Index = 0x2001_0000, Entry1 = 0x3000_0100, Entry2 = 0x3000_0200;

    readonly FakeMemory memory = new();
    readonly OffsetProfile profile = GameSession.BuiltInProfiles().Single(p => p.Build == "27.0.0+2493426 Game Pass");

    public RetiringPlayersTests()
    {
        TestWorld.Set64(memory.Map(Module + 0x5675060, 8), 0, Manager);
        var manager = memory.Map(Manager, 0xA8);
        TestWorld.Set64(manager, 0, Module + 0x4E6B440);
        Vector(manager, 0x30, Pointers, 2, 8);
        Vector(manager, 0x90, Index, 2, 16);

        var pointers = memory.Map(Pointers, 16);
        TestWorld.Set64(pointers, 0, Entry1);
        TestWorld.Set64(pointers, 8, Entry2);

        var index = memory.Map(Index, 32);
        IndexEntry(index, 0, 1759, Entry1);
        IndexEntry(index, 16, 20965, Entry2);

        // Real 24-byte records copied from the live Game Pass retirement manager (Entry2 -> 2027-06-30, Entry3 -> 2027-07-01 in KeepPlayingTests).
        Convert.FromHexString("DF0600001E0800004F01E807B500EB074E01E80700000500").CopyTo(memory.Map(Entry1, 24), 0);
        Convert.FromHexString("E5510000330200004F01E807B600EB074E01E80780000500").CopyTo(memory.Map(Entry2, 24), 0);
    }

    static void Vector(byte[] owner, int offset, ulong begin, int count, int size)
    {
        TestWorld.Set64(owner, offset, begin);
        TestWorld.Set64(owner, offset + 8, begin + (ulong)(count * size));
        TestWorld.Set64(owner, offset + 16, begin + (ulong)(count * size));
    }

    static void IndexEntry(byte[] index, int at, uint person, ulong entry)
    {
        BitConverter.GetBytes(person).CopyTo(index, at);
        Convert.FromHexString("72020000").CopyTo(index, at + 4);
        TestWorld.Set64(index, at + 8, entry);
    }

    PlayerRecord MakePlayer(ulong address, string? name, int? personIndex = null, byte retiringAfterSpell = 0, ulong? preferredMoves = null)
    {
        var raw = memory.Map(address, 0x3C8);
        TestWorld.Set64(raw, 0, Module + 0x4CD6318);
        raw[0x2A1] = retiringAfterSpell;
        return new PlayerRecord
        {
            Address = address,
            VtableRva = 0x4CD6318,
            PersonIndex = personIndex,
            CommonName = name,
            CurrentAbility = 130,
            PotentialAbility = 140,
            HomeReputation = 5000,
            CurrentReputation = 5000,
            WorldReputation = 5000,
            Height = 180,
            PreferredMoveBits = preferredMoves,
            RawAttributes = new byte[50],
            AttributeScale = 5,
            Positions = new byte[20],
            Personality = new byte[10],
        };
    }

    [Fact]
    public void Collects_combines_and_sorts_retiring_players_from_all_sources()
    {
        var plannedOnly = MakePlayer(0x4000_0000, "Zeta Planned", personIndex: 20965);
        var earlierAllThree = MakePlayer(0x4000_1000, "Ronaldo Combined", personIndex: 1759, retiringAfterSpell: 0x80, preferredMoves: 1UL << 48);
        var undatedSpell = MakePlayer(0x4000_2000, "bravo spell", retiringAfterSpell: 0x80);
        var undatedTop = MakePlayer(0x4000_3000, "Alpha Top", preferredMoves: 1UL << 48);
        var activePlayer = MakePlayer(0x4000_4000, "Active Player", personIndex: 999);
        var unnamed = MakePlayer(0x4000_5000, "   ", personIndex: 1759, retiringAfterSpell: 0x80, preferredMoves: 1UL << 48);

        var results = new RetiringPlayerReader(memory, profile, Module)
            .Read([undatedSpell, plannedOnly, unnamed, activePlayer, undatedTop, earlierAllThree]);

        Assert.Equal(4, results.Count);
        Assert.Equal(["Ronaldo Combined", "Zeta Planned", "Alpha Top", "bravo spell"], results.Select(r => r.Player.DisplayName));

        Assert.Equal(new DateOnly(2027, 6, 30), results[0].RetirementDate);
        Assert.Equal(RetirementReason.PlannedRetirement | RetirementReason.RetiringAfterSpell | RetirementReason.WillRetireAtTop, results[0].Reasons);

        Assert.Equal(new DateOnly(2027, 7, 1), results[1].RetirementDate);
        Assert.Equal(RetirementReason.PlannedRetirement, results[1].Reasons);

        Assert.Null(results[2].RetirementDate);
        Assert.Equal(RetirementReason.WillRetireAtTop, results[2].Reasons);

        Assert.Null(results[3].RetirementDate);
        Assert.Equal(RetirementReason.RetiringAfterSpell, results[3].Reasons);
    }

    [Fact]
    public void Reads_spell_and_trait_retirements_when_retirement_manager_is_absent()
    {
        string gamePassPath = Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.0+2493426-gamepass.json");
        string json = File.ReadAllText(gamePassPath).Replace("\"retirementManagerRva\":", "\"unusedRetirementManagerRva\":");
        var noManagerProfile = OffsetProfile.Load(json);

        var spell = MakePlayer(0x4001_0000, "Spell Player", retiringAfterSpell: 0x80);
        var top = MakePlayer(0x4001_1000, "Top Player", preferredMoves: 1UL << 48);
        var plannedOnly = MakePlayer(0x4001_2000, "Planned Missing Manager", personIndex: 1759);

        var results = new RetiringPlayerReader(memory, noManagerProfile, Module).Read([spell, top, plannedOnly]);

        Assert.Equal(["Spell Player", "Top Player"], results.Select(r => r.Player.DisplayName));
        Assert.All(results, r => Assert.Null(r.RetirementDate));
    }
}
