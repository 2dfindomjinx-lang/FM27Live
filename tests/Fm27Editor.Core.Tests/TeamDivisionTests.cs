using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public sealed class TeamDivisionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Reads_the_profile_team_league_and_rejects_changed_object_types(bool invalidTeam, bool invalidCompetition)
    {
        // Fixture layout from the measured 27.0.0 Steam profile, not values calculated by the reader.
        var world = new TestWorld();
        const ulong team = 0x70000000, competition = 0x71000000, name = 0x72000000;
        var bytes = world.Memory.Map(team, 0x58);
        TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + (invalidTeam ? 0x1234u : 0x4CE1108u));
        TestWorld.Set64(bytes, 0x50, competition);
        var comp = world.Memory.Map(competition, 0x50);
        TestWorld.Set64(comp, 0, TestWorld.ModuleBase + (invalidCompetition ? 0x1234u : 0x4CDFAD8u));
        TestWorld.Set64(comp, 0x40, name);
        world.Memory.MapString(name, "Test League");
        var reader = new PlayerReader(world.Memory, world.Profile) { ModuleBase = TestWorld.ModuleBase };
        var found = reader.ReadTeamDivision(team);
        if (invalidTeam || invalidCompetition) Assert.Null(found);
        else { Assert.Equal(competition, found!.Address); Assert.Equal("Test League", found.DisplayName); }
        Assert.Null(reader.ReadTeamDivision(0));
        Assert.Null(reader.ReadTeamDivision(0x77777777));
    }
}
