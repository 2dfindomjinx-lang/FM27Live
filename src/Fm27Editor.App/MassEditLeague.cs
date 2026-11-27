using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Competitions;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

internal sealed record LeagueBatch(CompetitionInfo League, int? CompetitionId, ClubEntry[] Clubs)
{
    public string Label => $"{League.Name ?? League.DisplayName} · {Loc.F("{0:N0} clubs", Clubs.Length)}";
    public string PickerLabel => CompetitionId is { } id ? $"{Label}  (#{id})" : Label;
    public string SelectionLabel => MissingTeams > 0 ? $"{Label} · {Loc.F("{0:N0} standings teams unmatched", MissingTeams)}" : Label;
    public int MissingTeams { get; init; }

    public static IReadOnlyList<LeagueBatch> Build(IReadOnlyList<ClubEntry> clubs,
        IReadOnlyDictionary<ulong, CompetitionInfo> divisions, IReadOnlyList<CompetitionRecord> competitions)
    {
        var ids = competitions.ToLookup(c => c.Address);
        return clubs.Where(c => c.Id > 0 && c.FirstTeam is not null && divisions.ContainsKey(c.Address))
            .GroupBy(c => divisions[c.Address].Address)
            .Where(g => g.Key != 0)
            .Select(g => new LeagueBatch(divisions[g.First().Address], ids[g.Key].FirstOrDefault()?.Id,
                g.DistinctBy(c => c.Id).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray()))
            .OrderBy(b => b.League.Name ?? b.League.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.CompetitionId).ToArray();
    }
}

internal sealed partial class MassEditWindow
{
    bool planClearDebts;

    async Task ChooseLeagueAsync()
    {
        if (backend.Data is not { } data) return;
        var leagues = LeagueBatch.Build(data.Clubs, data.ClubDivisions, data.Competitions);
        if (leagues.Count == 0) { status.Text = Loc.S("No first-team leagues were found in this save."); return; }
        var picked = await PickerDialog.Show(this, "Choose league…", query =>
        {
            string term = MainWindow.Fold(query);
            return leagues.Where(b => MainWindow.Fold($"{b.League.Name} {b.League.ShortName} {b.CompetitionId}").Contains(term))
                .Select(b => (b.PickerLabel, b)).ToArray();
        }, showAllInitially: true);
        if (picked is null) return;
        if (data.Competitions.FirstOrDefault(c => c.Address == picked.League.Address) is { } competition)
        {
            var standings = await Task.Run(() => data.Session.CompetitionStandings.Read(competition));
            if (standings is not null)
            {
                var teams = picked.Clubs.Select(c => c.FirstTeam!.Address).ToHashSet();
                picked = picked with { MissingTeams = standings.Select(s => s.Team).Distinct().Count(t => !teams.Contains(t)) };
            }
        }
        if (!ReferenceEquals(backend.Data, data)) throw new InvalidOperationException("The game data changed. Choose the league again.");
        chosenLeague = picked;
        leagueSession = data.Session;
        chosenClubs = picked.Clubs;
        UpdateTarget();
    }

    void CheckLeague(GameSession session, LeagueBatch league)
    {
        if (!ReferenceEquals(session, leagueSession) || !ReferenceEquals(session, backend.Session) ||
            backend.SaveChanged || session.LoadState.SaveChanged)
            throw new InvalidOperationException("The game data changed. Choose the league again.");
        if (backend.Data is not { } current || !league.Clubs.Select(c => c.Address).ToHashSet().SetEquals(
            current.Clubs.Where(c => c.Id > 0 && c.FirstTeam is not null && current.ClubDivisions.TryGetValue(c.Address, out var division)
                && division.Address == league.League.Address).DistinctBy(c => c.Id).Select(c => c.Address)))
            throw new InvalidOperationException("League membership changed. Refresh and choose the league again.");
        var reader = new PlayerReader(session.Memory, session.Profile) { ModuleBase = session.ModuleBase };
        foreach (var club in league.Clubs)
            if (club.FirstTeam is not { } team || reader.ReadTeamDivision(team.Address)?.Address != league.League.Address)
                throw new InvalidOperationException("League membership changed. Refresh and choose the league again.");
    }
}
