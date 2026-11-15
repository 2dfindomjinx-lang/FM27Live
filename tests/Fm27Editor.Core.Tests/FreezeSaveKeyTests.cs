using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Tests;

public sealed class FreezeSaveKeyTests
{
    static StaffRecord Staff(int id, bool manager) => new()
    {
        Address = 0x100000 + (ulong)Math.Max(id, 0), StaffAddress = 0x200000 + (ulong)Math.Max(id, 0), Id = id,
        Attributes = [], Personality = [], Kind = new StaffKind(0, 0, 0, null, manager),
    };

    [Theory]
    [InlineData("27.0.0+2493426-steam.json", "steam:7,42")]
    [InlineData("27.0.0+2493426-gamepass.json", "gamepass:7,42")]
    public void Loaded_staff_produces_the_existing_sorted_manager_identity(string profileName, string expected)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", profileName));
        // Non-manager IDs do not identify a career; manager order in memory does not change the key.
        Assert.Equal(expected, FreezeStore.SaveKey(profile, [Staff(42, true), Staff(123, false), Staff(7, true)]));
    }

    [Fact]
    public void Missing_invalid_or_duplicate_managers_are_not_accepted_as_a_save_identity()
    {
        var profile = OffsetProfile.LoadFile(TestWorld.ProfilePath);
        Assert.Throws<InvalidOperationException>(() => FreezeStore.SaveKey(profile, [Staff(123, false)]));
        Assert.Throws<InvalidOperationException>(() => FreezeStore.SaveKey(profile, [Staff(0, true)]));
        Assert.Throws<InvalidOperationException>(() => FreezeStore.SaveKey(profile, [Staff(7, true), Staff(7, true)]));
    }
}
