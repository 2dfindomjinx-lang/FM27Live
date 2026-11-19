using Avalonia;
using Avalonia.Controls;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

internal static class QuickClubCatalog
{
    public static IReadOnlyList<(StaffRecord Manager, ClubEntry Club)> Managed(IReadOnlyList<StaffRecord> staff, IReadOnlyList<ClubEntry> clubs)
    {
        var byAddress = clubs.ToDictionary(c => c.Address);
        return staff.Where(s => s.Kind.Manager && s.Club is not null && byAddress.ContainsKey(s.Club.Address))
            .Select(s => (s, byAddress[s.Club!.Address])).ToArray();
    }

    public static IReadOnlyList<ClubEntry> League(ClubEntry club, IReadOnlyList<ClubEntry> clubs, IReadOnlyDictionary<ulong, CompetitionInfo> divisions)
    {
        if (!divisions.TryGetValue(club.Address, out var league)) return [];
        return clubs.Where(c => divisions.TryGetValue(c.Address, out var division) && division.Address == league.Address)
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}

public sealed partial class MainWindow
{
    bool pickingQuickClub;

    Control BuildQuickClubsBar()
    {
        var bar = new WrapPanel { Margin = new Thickness(10, 2, 10, 5) };
        void Add(string label, Func<Task> action)
        {
            var button = Loc.BindContent(new Button { Margin = new Thickness(0, 0, 6, 3), FontSize = 12 }, label);
            button.Click += async (_, _) =>
            {
                if (pickingQuickClub || busy) return;
                if (backend.State.Status != ConnectionStatus.Connected || backend.Data is null)
                { status.Text = Loc.S("Load game first"); return; }
                pickingQuickClub = true;
                try { if (label == "Choose my club…" || await ConfirmLeaveAsync()) await action(); }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
                finally { pickingQuickClub = false; }
            };
            bar.Children.Add(button);
        }
        Add("My club", async () => { if (await ResolveMyClubAsync() is { } club) OpenQuickClub(club); });
        Add("My squad", async () =>
        {
            if (await ResolveMyClubAsync() is not { } club) return;
            ClearDialogFilters();
            activeTeamClubAddress = club.Address;
            activeTeamClubName = club.DisplayName;
            activePresetName = null;
            suppressFilterEvents = true;
            search.Text = "";
            genderFilter.SelectedIndex = 2;
            suppressFilterEvents = false;
            ShowModule("Players");
            RenderAttrChips();
            Filter();
        });
        Add("Clubs in my league", async () =>
        {
            if (await ResolveMyClubAsync() is not { } club || backend.Data is not { } data) return;
            if (!data.ClubDivisions.TryGetValue(club.Address, out var division))
            { status.Text = Loc.S("The club's first-team league could not be read."); return; }
            var clubs = QuickClubCatalog.League(club, data.Clubs, data.ClubDivisions);
            if (await PickQuickClubAsync(clubs, division.DisplayName) is { } chosen && ReferenceEquals(data, backend.Data)) OpenQuickClub(chosen);
        });
        Add("Player's club", () =>
        {
            var club = backend.Data?.Clubs.FirstOrDefault(c => c.Address == selected?.Club?.Address);
            if (club is null) status.Text = Loc.S("Select a player with a club first.");
            else OpenQuickClub(club);
            return Task.CompletedTask;
        });
        Add("Recent clubs", async () =>
        {
            var data = backend.Data!;
            var clubs = livePreferences.RecentClubIds.Select(id => data.Clubs.FirstOrDefault(c => c.Id == id)).OfType<ClubEntry>().ToArray();
            if (clubs.Length == 0) { status.Text = Loc.S("No recent clubs in this save."); return; }
            if (await PickQuickClubAsync(clubs, "Recent clubs") is { } club && ReferenceEquals(data, backend.Data)) OpenQuickClub(club);
        });
        Add("Choose my club…", async () => { await ResolveMyClubAsync(forceChoice: true); });
        return bar;
    }

    async Task<ClubEntry?> ResolveMyClubAsync(bool forceChoice = false)
    {
        if (backend.Data is not { } data) return null;
        var managed = QuickClubCatalog.Managed(data.Staff, data.Clubs);
        if (!forceChoice)
        {
            if (livePreferences.QuickClubId is { } id && data.Clubs.FirstOrDefault(c => c.Id == id) is { } manual) return manual;
            if (managed.Count == 1) return managed[0].Club;
            if (livePreferences.QuickManagerId is { } managerId && managed.FirstOrDefault(m => m.Manager.Id == managerId).Club is { } previous) return previous;
        }
        if (managed.Count > 0 && !forceChoice)
        {
            var manager = await PickerDialog.Show(this, "Choose your manager", q => managed
                .Where(m => Fold(m.Manager.DisplayName + " " + m.Club.DisplayName).Contains(Fold(q)))
                .Select(m => ($"{m.Manager.DisplayName} · {m.Club.DisplayName}", m.Manager)).ToArray(), showAllInitially: true);
            if (manager is null || !ReferenceEquals(data, backend.Data)) return null;
            livePreferences.QuickManagerId = manager.Id;
            livePreferences.QuickClubId = null;
            SaveQuickPreferences();
            return managed.First(m => m.Manager.Id == manager.Id).Club;
        }
        var chosen = await PickQuickClubAsync(data.Clubs, "Choose my club…");
        if (chosen is null || !ReferenceEquals(data, backend.Data)) return null;
        if (chosen.Id is not { } clubId) { status.Text = Loc.S("This club has no readable ID."); return null; }
        livePreferences.QuickClubId = clubId;
        livePreferences.QuickManagerId = null;
        SaveQuickPreferences();
        status.Text = Loc.F("My club: {0}", chosen.DisplayName);
        return chosen;
    }

    Task<ClubEntry?> PickQuickClubAsync(IReadOnlyList<ClubEntry> clubs, string title) => PickerDialog.Show(this, title,
        q => clubs.Where(c => Fold(c.DisplayName + " " + c.Name + " " + c.Id).Contains(Fold(q)))
            .Take(300).Select(c => ($"{c.DisplayName} · {c.Id}", c)).ToArray(), showAllInitially: true);

    void OpenQuickClub(ClubEntry club)
    {
        PickClub(club);
    }

    void RememberQuickClub(ClubEntry club)
    {
        if (club.Id is not { } id) return;
        livePreferences.RecentClubIds.Remove(id);
        livePreferences.RecentClubIds.Insert(0, id);
        livePreferences.RecentClubIds = livePreferences.RecentClubIds.Take(10).ToList();
        SaveQuickPreferences();
    }

    void SaveQuickPreferences()
    {
        try { livePreferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = Loc.S("Could not save quick access preferences."); }
    }
}
