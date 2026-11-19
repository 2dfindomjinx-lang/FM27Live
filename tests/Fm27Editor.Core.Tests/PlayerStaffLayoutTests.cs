using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// A player who is also a member of staff (<c>db::ACTUAL_PLAYER_AND_NON_PLAYER</c>) on Windows 27.1.0. Layout measured on the
/// Game Pass save on 19/11/2026: Keisuke Honda (ID 788585 in the game's profile screen) keeps his staff part at +0x280 and
/// his person part at +0x380, so the person ID sits at +0x38C instead of a plain player's +0x294.
/// </summary>
public sealed class PlayerStaffLayoutTests
{
    const ulong ModuleBase = 0x7FF0_0000_0000;
    const uint PlainVtable = 0x4A95108, PlayerStaffVtable = 0x4C9ECB8;
    const ulong Plain = 0x5000_0000, Honda = 0x5100_0000;

    static OffsetProfile Profile() =>
        OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-gamepass.json"));

    static FakeMemory World()
    {
        var memory = new FakeMemory();
        var plain = memory.Map(Plain, 0x3C8);
        BitConverter.GetBytes(ModuleBase + PlainVtable).CopyTo(plain, 0);
        BitConverter.GetBytes(12345).CopyTo(plain, 0x294);
        var honda = memory.Map(Honda, 0x4C0);
        BitConverter.GetBytes(ModuleBase + PlayerStaffVtable).CopyTo(honda, 0);
        BitConverter.GetBytes(788585).CopyTo(honda, 0x38C);
        honda[0x380 + 0x19] = 0x10; // female flag in the person part only, so a read at the plain offset would miss it
        return memory;
    }

    [Fact]
    public void Person_fields_of_a_player_who_is_also_staff_follow_the_person_part()
    {
        var profile = Profile();
        var memory = World();
        int id = profile.Fields.Id!.Value;

        Assert.Equal(Plain + 0x294, profile.PlayerField(memory, ModuleBase, Plain, id));
        Assert.Equal(Honda + 0x38C, profile.PlayerField(memory, ModuleBase, Honda, id));
        Assert.Equal(788585, BitConverter.ToInt32(memory.Read(profile.PlayerField(memory, ModuleBase, Honda, id), 4)));
        Assert.Equal(Honda + 0x380, profile.PlayerField(memory, ModuleBase, Honda, profile.Relations!.PlayerPerson));
        // Fields of the player part before the staff part do not move.
        Assert.Equal(Honda + 0x130, profile.PlayerField(memory, ModuleBase, Honda, profile.Team!.PlayerTeam));
    }

    [Fact]
    public void Gender_guard_reads_the_person_part_of_a_player_who_is_also_staff()
    {
        var profile = Profile();
        var memory = World();

        Assert.Throws<InvalidOperationException>(() => GenderGuard.EnsureMalePlayer(memory, profile, ModuleBase, Honda));
        GenderGuard.EnsureMalePlayer(memory, profile, ModuleBase, Plain);
    }

    [Theory]
    [InlineData("27.1.0+2505117-gamepass.json", 0x4C9ECB8u)]
    [InlineData("27.1.0+2505117-steam.json", 0x50EAC28u)]
    public void Windows_27_1_profiles_map_the_player_staff_insertion(string file, uint vtable)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", file));
        Assert.True(profile.PlayerInsertions.TryGetValue(vtable, out var insertion));
        Assert.Equal(0x280, insertion!.At);
        Assert.Equal(0xF8, insertion.Length);
        Assert.Contains(vtable, profile.PlayerVtableRvas);
    }
}
