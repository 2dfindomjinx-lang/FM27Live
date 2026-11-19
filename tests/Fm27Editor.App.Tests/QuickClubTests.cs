using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App.Tests;

public sealed class QuickClubTests
{
    static ClubEntry Club(ulong address, int id, string name) => new(address, id, name, name, 0, []);
    static StaffRecord Manager(int id, ulong club, bool human) => new()
    {
        Address = (ulong)id, StaffAddress = (ulong)id + 1000, Id = id, Attributes = [], Personality = [],
        Kind = new StaffKind(0, 0, 0, null, human), JobCode = 16, Club = new(club, "Club", "Club")
    };

    [Fact]
    public void Human_managers_are_distinct_from_AI_managers_and_stale_clubs_are_excluded()
    {
        var clubs = new[] { Club(10, 1, "A"), Club(20, 2, "B") };
        var found = QuickClubCatalog.Managed([Manager(1, 10, true), Manager(2, 20, false), Manager(3, 99, true), Manager(4, 20, true)], clubs);
        Assert.Equal(new[] { 1, 4 }, found.Select(x => x.Manager.Id));
        Assert.Same(clubs[0], found[0].Club);
        Assert.Same(clubs[1], found[1].Club);
    }

    [Fact]
    public void League_selection_uses_first_team_competition_identity_not_ambiguous_names()
    {
        var clubs = new[] { Club(10, 1, "A"), Club(20, 2, "B"), Club(30, 3, "C"), Club(40, 4, "D") };
        var divisions = new Dictionary<ulong, CompetitionInfo> { [10] = new(100, "League", null), [20] = new(100, "League", null), [30] = new(200, "League", null) };
        Assert.Equal(new int?[] { 1, 2 }, QuickClubCatalog.League(clubs[0], clubs, divisions).Select(c => c.Id));
        Assert.Empty(QuickClubCatalog.League(clubs[3], clubs, divisions));
        Assert.Empty(QuickClubCatalog.League(Club(999, 1, "A"), clubs, divisions));
    }
}
