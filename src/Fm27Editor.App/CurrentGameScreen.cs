using System.Diagnostics;
using Fm27Editor.Core.Memory;
using Fm27Editor.UnityMetadata;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    CurrentScreenReader? currentGameScreen;
    bool readingGameScreen;

    async Task OpenCurrentGameScreenAsync()
    {
        if (readingGameScreen) return;
        if (backend.State.Status != ConnectionStatus.Connected || backend.Data is not { } data)
        { status.Text = Loc.S("Load the game first."); return; }
        var session = data.Session;
        var cachedReader = currentGameScreen;
        readingGameScreen = true;
        status.Text = Loc.S("Reading the current FM screen…");
        try
        {
            var result = await Task.Run(() =>
            {
                if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Current FM screen detection requires Windows.");
                using var process = Process.GetProcessById(session.Game.ProcessId);
                var module = process.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
                if (cachedReader is null)
                {
                    string folder = Path.GetDirectoryName(module.FileName)!;
                    string metadataPath = Path.Combine(folder, "fm_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
                    cachedReader = new CurrentScreenReader(ScreenMetadataReader.Read(module.FileName, metadataPath), (ulong)module.BaseAddress);
                }
                using var memory = new WindowsProcessMemory(process.Id, writable: false);
                return cachedReader.Read(memory);
            });
            if (backend.State.Status != ConnectionStatus.Connected || !ReferenceEquals(backend.Session, session) || !CurrentRefresh(data)) return;
            currentGameScreen = cachedReader;
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
        }
        catch (Exception ex)
        {
            if (backend.State.Status == ConnectionStatus.Connected && ReferenceEquals(backend.Session, session))
                status.Text = Loc.F("Could not read the current FM screen: {0}", UserMessage.For(ex));
        }
        finally { readingGameScreen = false; }
    }
}
