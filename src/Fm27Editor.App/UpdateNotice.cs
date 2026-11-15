using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// At start: if GitHub has a newer release, offer to install it. Update now downloads the new exe, checks it against the
    /// release checksum, puts it in place of this one and restarts. Anything that goes wrong leaves this version as it was
    /// and offers the download page instead.
    /// </summary>
    static bool IsSelfUpdatable(string? exe) =>
        exe is not null && Path.GetFileName(exe).Equals(UpdateCheck.ExeName, StringComparison.OrdinalIgnoreCase);

    async Task NotifyIfUpdateAsync()
    {
        string? exe = Environment.ProcessPath;
        if (IsSelfUpdatable(exe))
            await Task.Run(() => UpdateCheck.CleanUp(exe!));
        var release = await UpdateCheck.NewerReleaseAsync();
        if (release is null) return;
        bool selfUpdatable = IsSelfUpdatable(exe);
        await Dispatcher.UIThread.InvokeAsync(() =>
            ShowNotice(Loc.S("Update available"),
                selfUpdatable
                    ? Loc.F("Version {0} is out. Update now downloads it, closes the editor and opens the new version. Changes you have not saved yet are lost; anything running in the game (Development Speed, Wonderkid Academy) keeps running.", release.Tag)
                    : Loc.F("Version {0} is out on GitHub. Download the latest release from the release page.", release.Tag),
                (Loc.S("Later"), false, null),
                (Loc.S(selfUpdatable ? "Update now" : "Open GitHub"), true, () => UpdateAsync(release))));
    }

    async Task UpdateAsync(ReleaseInfo release)
    {
        string? exe = Environment.ProcessPath;
        // Only the single-file release exe can replace itself; a development build or a renamed copy goes to the download page.
        if (!IsSelfUpdatable(exe))
        {
            OpenReleasesPage();
            return;
        }
        status.Text = Loc.F("Downloading {0}…", release.Tag);
        try
        {
            string downloaded = await UpdateCheck.DownloadAsync(release, exe!);
            UpdateCheck.Swap(exe!, downloaded);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            status.Text = Loc.F("Update failed: {0}", ex.Message);
            ShowNotice(Loc.S("Update failed"),
                Loc.F("The update could not be installed ({0}). This version is unchanged. You can download {1} from GitHub instead.", ex.Message, release.Tag),
                (Loc.S("Close"), false, null),
                (Loc.S("Open GitHub"), true, () => { OpenReleasesPage(); return Task.CompletedTask; }));
            return;
        }
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Close();
    }

    void OpenReleasesPage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateCheck.ReleasesPage) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { status.Text = Loc.F("Could not open the browser: {0}", UpdateCheck.ReleasesPage); }
    }
}
