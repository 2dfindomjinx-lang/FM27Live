using Fm27Editor.Core.Players;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    readonly GameDayRefresh dayRefresh = new();
    bool refreshClosed;

    async void ObserveGameDay()
    {
        if (refreshClosed || busy || backend.SaveChanged || backend.Data is not { } data || backend.GameDate is not { } today) return;
        try
        {
            RefreshCleanEditors(data);
            await dayRefresh.TickAsync(data.Session, data.GameDate, today,
                () => RefreshGameDayAsync(data, today), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            if (!refreshClosed && !busy && !backend.SaveChanged)
                status.Text = Loc.F("Automatic refresh will retry: {0}", UserMessage.For(ex));
        }
    }

    bool CurrentRefresh(LoadedGameData data)
    {
        if (refreshClosed || busy || backend.SaveChanged || !ReferenceEquals(backend.Data, data)) return false;
        data.Session.LoadState.Refresh();
        return !data.Session.LoadState.SaveChanged;
    }

    async Task<bool> RefreshGameDayAsync(LoadedGameData previous, DateOnly date)
    {
        var report = new GameLoadReport();
        var snapshot = await Task.Run(() =>
        {
            var data = LoadedGameData.Read(previous.Session, report, previous);
            var retirements = report.Read("retirements", "Reading…", () => ReadRetiringRows(data));
            var transfers = report.Read("future transfers", "Reading…", () => ReadFutureTransferRows(data));
            return (Data: data, Retirements: retirements, Transfers: transfers);
        });
        var fresh = snapshot.Data;
        if (!CurrentRefresh(previous)) return false;
        if (!report.Succeeded)
        {
            status.Text = Loc.S("Automatic refresh could not finish; existing data and edits are kept. Retrying shortly.");
            return false;
        }
        // Validate date, session and the game's load counter after all reads, before publishing anything.
        if (!backend.TryPublishRefresh(previous, fresh, date)) return false;
        rows = fresh.Players.Cast<object>().ToArray();
        playerSource = fresh.Players.ToArray();
        clubCache = fresh.Clubs;
        nationCatalogue = fresh.Nations.Where(n => n.DisplayName.Length > 0).OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        cityCatalogue = fresh.Cities;
        competitionCache = fresh.Competitions.GroupBy(c => c.Index)
            .ToDictionary(g => g.Key, g => new CompetitionInfo(g.First().Address, g.First().Name, g.First().ShortName));
        regionalCompetitionNames = fresh.Competitions.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name!);
        personNames = null;
        careerCatalogue = null;
        RegenIds.Current = RegenIds.FirstCreatedId(fresh.Players.Where(p => p.Id is not null).Select(p => p.Id!.Value));
        talentedRegensTab?.SetPlayers(fresh.Players);
        ReleaseUnifiedCache();
        EnsureUnifiedCache();
        retiringAllRows = snapshot.Retirements.ToList();
        futureAllRows = snapshot.Transfers.ToList();
        retiringDirty = futureDirty = false;
        ApplyRetiringFilter();
        ApplyFutureFilter();
        favouritesWindow?.Refill(fresh.Players);
        PopulateFilterDropdowns(preserveSelection: true);
        await FilterInternal(reportStatus: false, preserveEditor: true);
        foreach (var screen in screens)
        {
            if (!CurrentRefresh(fresh)) return false;
            await screen.RefreshDataAsync(fresh, () => CurrentRefresh(fresh));
        }
        if (!CurrentRefresh(fresh)) return false;
        RefreshCleanEditors(fresh);
        gameDateText.Text = DateSetting.Format(date);
        status.Text = Loc.F("Data refreshed for {0}. Unsaved tabs are kept until you save or discard them.", DateSetting.Format(date));
        return true;
    }

    bool PlayerEditorProtected(PlayerTab tab) => tab.Dirty || EditorRefreshGuard.Protects(tab.Root) ||
        (drafts.TryGetValue(tab.Player.Address, out var draft) && draft.Count > 0) ||
        (playerScalarDrafts.TryGetValue(tab.Player.Address, out var scalars) && scalars.Count > 0) ||
        (playerDirtySections.TryGetValue(tab.Player.Address, out var sections) && sections.Count > 0) ||
        (ReferenceEquals(tab.Root, bodyHost.Content) && informationDirty?.Invoke() == true);

    void RefreshCleanEditors(LoadedGameData data)
    {
        if (!CurrentRefresh(data) || OwnedWindows.Any(w => w.IsVisible)) return;
        foreach (var tab in playerTabs)
        {
            if (ReferenceEquals(tab.RefreshedData, data) || PlayerEditorProtected(tab)) continue;
            var fresh = data.Players.FirstOrDefault(p => p.Address == tab.Player.Address && p.Id == tab.Player.Id);
            if (fresh is null) continue;
            // Use a live read so a save made since the catalogue read is never visually rolled back.
            fresh = backend.Players.Reload(fresh.Address);
            if (fresh is null || fresh.Id != tab.Player.Id) continue;
            fresh.GameDate = backend.GameDate;
            drafts.Remove(fresh.Address);
            tab.Player = fresh;
            tab.RefreshedData = data;
            if (ReferenceEquals(tab.Root, bodyHost.Content)) { selected = fresh; editor = tab.Panel; RenderEditor(); }
            else tab.Panel.Children.Clear(); // lazy rebuild when this clean tab is next opened
        }
        foreach (var screen in screens) screen.RefreshCleanEditor(data);
    }
}
