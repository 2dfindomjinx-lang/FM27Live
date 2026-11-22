using System.Diagnostics;
using Fm27Editor.Core.Diagnostics;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.UnityMetadata;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    CurrentScreenSession? currentGameScreen;
    bool readingGameScreen;

    CurrentScreenSession ScreenSession(GameSession session) => currentGameScreen ??= new(() =>
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Current FM screen detection requires Windows.");
        using var process = Process.GetProcessById(session.Game.ProcessId);
        var module = process.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        string folder = Path.GetDirectoryName(module.FileName)!;
        string metadataPath = Path.Combine(folder, "fm_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        return new CurrentScreenReader(ScreenMetadataReader.Read(module.FileName, metadataPath), (ulong)module.BaseAddress);
    }, () => new WindowsProcessMemory(session.Game.ProcessId, writable: false));

    async Task PrepareCurrentGameScreenAsync(LoadedGameData data)
    {
        if (!OperatingSystem.IsWindows()) return;
        var screen = ScreenSession(data.Session);
        var timer = Stopwatch.StartNew();
        try
        {
            await screen.PrepareAsync();
            FlightRecorder.Current?.Log("screen", $"background preparation: {timer.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Optional preparation must not fail game loading; the shortcut can retry.
            FlightRecorder.Current?.Log("screen", $"preparation will retry: {ex.GetType().Name}; {timer.ElapsedMilliseconds} ms");
        }
    }

    async Task OpenCurrentGameScreenAsync()
    {
        if (readingGameScreen) return;
        if (backend.State.Status != ConnectionStatus.Connected || backend.Data is not { } data)
        { status.Text = Loc.S("Load the game first."); return; }
        var session = data.Session;
        var screen = ScreenSession(session);
        var timer = Stopwatch.StartNew();
        readingGameScreen = true;
        status.Text = Loc.S("Reading the current FM screen…");
        try
        {
            var result = await screen.ReadAsync();
            if (backend.State.Status != ConnectionStatus.Connected || !ReferenceEquals(backend.Session, session) ||
                !ReferenceEquals(currentGameScreen, screen) || backend.Data is not { } latest || !CurrentRefresh(latest)) return;
            // A same-session day refresh can replace the catalogue while preparation runs.
            // Resolve the current entity against that fresh catalogue, not a stale snapshot.
            data = latest;
            string? opened = null;
            switch (result?.Kind)
            {
                case ScreenEntityKind.Person:
                    if (data.Players.FirstOrDefault(p => p.Id == result.Id) is { } player)
                    {
                        // Reuse an open editor, including raw unfinished text and its original snapshot.
                        if (playerTabs.FirstOrDefault(t => t.Player.Address == player.Address) is { } tab) ActivatePlayerTab(tab);
                        else PickPlayer(player);
                        opened = player.DisplayName;
                    }
                    else if (data.Staff.FirstOrDefault(p => p.Id == result.Id) is { } staff)
                    { PickStaff(staff); opened = staff.DisplayName; }
                    break;
                case ScreenEntityKind.Club:
                    if (data.Clubs.FirstOrDefault(c => c.Id == result.Id) is { } club)
                    { PickClub(club); opened = club.DisplayName; }
                    break;
                case ScreenEntityKind.Competition:
                    if (data.Competitions.FirstOrDefault(c => c.Id == result.Id) is { } competition)
                    { competitionScreen.Pick(competition); opened = competition.DisplayName; }
                    break;
                case ScreenEntityKind.Nation:
                    if (data.Nations.FirstOrDefault(c => c.Id == result.Id) is { } nation)
                    { PickNation(nation); opened = nation.DisplayName; }
                    break;
            }
            status.Text = opened is null ? Loc.S("No supported person or club was found on the current FM screen.") : Loc.F("Opened from FM: {0}", opened);
            FlightRecorder.Current?.Log("screen", $"open: {timer.ElapsedMilliseconds} ms; found {opened is not null}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (backend.State.Status == ConnectionStatus.Connected && ReferenceEquals(backend.Session, session))
                status.Text = Loc.F("Could not read the current FM screen: {0}", UserMessage.For(ex));
        }
        finally { readingGameScreen = false; }
    }
}
