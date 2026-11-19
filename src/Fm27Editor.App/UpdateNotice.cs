namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    Task NotifyIfUpdateAsync() => CheckLiveUpdatesAsync();
}
