using Avalonia;

namespace Fm27Editor.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The app always runs as the normal user; the privileged helper is installed on first connect by
        // MacElevation.EnsureHelperInstalled (see Backend.ConnectAsync).
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
