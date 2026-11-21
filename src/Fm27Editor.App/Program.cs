using Avalonia;

namespace Fm27Editor.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-live-update")
        {
            Environment.ExitCode = Live.LiveInstaller.ApplyAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        // One editor session owns the game writes, hotkey and downloaded package.
        using var instance = new Mutex(true, "Local\\FM27Live.Desktop", out bool first);
        if (!first) return;
        if (args.Length == 0 && Live.LiveInstaller.CanSelfUpdate && Live.LivePreferences.Load().AutomaticUpdates)
        {
            try
            {
                var staged = Live.LiveFeed.ReadStagedAsync(Live.LiveFeed.Current).GetAwaiter().GetResult();
                if (staged is not null) { Live.LiveInstaller.Start(staged); return; }
            }
            catch (Exception) { /* A locked install directory must not prevent launching the existing app. */ }
        }
        // The app always runs as the normal user; the privileged helper is installed on first connect by
        // MacElevation.EnsureHelperInstalled (see Backend.ConnectAsync).
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = AppTypography.BodySource });
}
