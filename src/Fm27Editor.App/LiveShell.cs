using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Fm27Editor.App.Live;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    readonly LivePreferences livePreferences = LivePreferences.Load();
    readonly CancellationTokenSource liveCancellation = new();
    readonly DispatcherTimer liveUpdateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    readonly TextBlock liveUpdateStatus = new() { Text = Loc.S("Updates will be checked"), VerticalAlignment = VerticalAlignment.Center, Foreground = UiKit.TextSecondary, FontSize = 12 };
    readonly Button liveInstall = new() { Content = Loc.S("Update and open"), IsVisible = false };
    readonly Button liveMode = new() { Content = "Overlay · F8" };
    readonly Button liveHide = new() { Content = Loc.S("Collapse · F8"), IsVisible = false };
    OverlayHotkey? overlayHotkey;
    TrayIcon? liveTray;
    StagedUpdate? stagedUpdate;
    bool overlayMode, liveChecking, liveInstalling, liveExiting;
    PixelPoint desktopPosition;
    Size desktopSize;
    WindowState desktopState;
    IntPtr gameFocus;

    Control BuildLiveBar()
    {
        var title = new TextBlock { Text = "FM27 LIVE  ·  " + LiveFeed.Current.ToString(3), FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 5) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fullscreen = new Button { Content = Loc.S("Full screen · F11") };
        var updates = new Button { Content = Loc.S("Updates") };
        var exit = new Button { Content = Loc.S("Exit") };
        actions.Children.Add(liveMode); actions.Children.Add(liveHide); actions.Children.Add(fullscreen); actions.Children.Add(updates); actions.Children.Add(liveInstall); actions.Children.Add(exit);
        liveMode.Click += (_, _) => SetOverlay(!overlayMode);
        liveHide.Click += (_, _) => HideOverlay();
        fullscreen.Click += (_, _) => ToggleFullscreen();
        updates.Click += (_, _) => ShowLiveUpdates();
        liveInstall.Click += (_, _) => ApplyLiveUpdate();
        exit.Click += (_, _) => { liveExiting = true; Close(); liveExiting = false; };
        DockPanel.SetDock(actions, Dock.Right); bar.Children.Add(actions);
        bar.Children.Add(title);
        var statusLine = new DockPanel { Margin = new Thickness(10, 0, 10, 5) };
        statusLine.Children.Add(liveUpdateStatus);
        var panel = new StackPanel(); panel.Children.Add(bar); panel.Children.Add(statusLine);
        var surface = new Border { Background = UiKit.SurfaceAlt, BorderBrush = UiKit.BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1), Child = panel };
        liveBarSurface = surface;
        title.PointerPressed += (_, e) => { if (overlayMode && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        Opened += async (_, _) =>
        {
            var menu = new NativeMenu();
            var show = new NativeMenuItem(Loc.S("Open desktop app"));
            show.Click += (_, _) => { SetOverlay(false); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Show(); Activate(); };
            var showOverlay = new NativeMenuItem(Loc.S("Open overlay"));
            showOverlay.Click += (_, _) => { SetOverlay(true); Show(); Activate(); };
            var quit = new NativeMenuItem(Loc.S("Exit"));
            quit.Click += (_, _) => { Show(); liveExiting = true; Close(); liveExiting = false; };
            menu.Items.Add(show); menu.Items.Add(showOverlay); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
            liveTray = new TrayIcon { Icon = Icon, ToolTipText = "FM27 Live — F8 Overlay", Menu = menu, IsVisible = true };
            liveTray.Clicked += (_, _) => { SetOverlay(false); Show(); Activate(); };
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { liveTray });
            if (OperatingSystem.IsWindows())
            {
                overlayHotkey = new OverlayHotkey(() => Dispatcher.UIThread.Post(ToggleOverlayVisibility));
                if (!await overlayHotkey.Ready) liveMode.Content = Loc.S("Overlay (F8 in use)");
            }
            LiveInstaller.ReportHealthy(Environment.GetCommandLineArgs().Skip(1).ToArray());
            liveUpdateTimer.Start();
        };
        liveUpdateTimer.Tick += async (_, _) => { if (livePreferences.AutomaticUpdates) await CheckLiveUpdatesAsync(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
            else if (e.Key == Key.Escape && overlayMode && !OwnedWindows.Any(w => w.IsVisible)) { HideOverlay(); e.Handled = true; }
        };
        Closing += (_, e) =>
        {
            if (liveInstalling) return;
            if (overlayMode && !liveExiting) { e.Cancel = true; HideOverlay(); return; }
            if (busy || OwnedWindows.Any(w => w.IsVisible)) { e.Cancel = true; status.Text = Loc.S("Finish the open operation or window first."); return; }
            if (HasLiveDrafts())
            {
                e.Cancel = true;
                ShowNotice(Loc.S("Unsaved changes"), Loc.S("Save or discard your changes in their tabs before exiting."), (Loc.S("OK"), true, null));
            }
        };
        Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (overlayMode && !overlayCollapsed && !OwnedWindows.Any(w => w.IsVisible) && OperatingSystem.IsWindows() && OverlayHotkey.IsGameForeground()) HideOverlay();
        });
        Closed += (_, _) => { overlayHotkey?.Dispose(); liveTray?.Dispose(); liveUpdateTimer.Stop(); liveCancellation.Cancel(); SaveOverlayGeometry(); };
        return surface;
    }

    bool HasLiveDrafts() => playerTabs.Any(t => t.Dirty) || kindTabs.Values.Any(t => t.Dirty) ||
        playerDirtySections.Values.Any(s => s.Count > 0) || informationDirty?.Invoke() == true;

    void ToggleFullscreen()
    {
        if (overlayMode) { if (overlayCollapsed) ExpandOverlay(); else ToggleOverlaySize(); return; }
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    void ToggleOverlayVisibility()
    {
        if (liveInstalling) return;
        // F8 belongs to this app only while editing or while FM is foreground.
        if (!IsActive && !OverlayHotkey.IsGameForeground()) return;
        if (OwnedWindows.Any(w => w.IsVisible)) return;
        if (overlayMode && !overlayCollapsed && IsVisible) { HideOverlay(); return; }
        if (!IsActive) gameFocus = OverlayHotkey.Foreground;
        if (!overlayMode) SetOverlay(true);
        else ExpandOverlay();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }


    async Task CheckLiveUpdatesAsync(bool manual = false)
    {
        if (liveChecking || liveInstalling || (!manual && !livePreferences.AutomaticUpdates)) return;
        liveChecking = true;
        if (!LiveInstaller.CanSelfUpdate) { liveChecking = false; liveUpdateStatus.Text = Loc.S("Development build · Automatic installation disabled"); return; }
        liveUpdateStatus.Text = Loc.S("Checking for updates…");
        try
        {
            var staged = await LiveFeed.ReadStagedAsync(LiveFeed.Current);
            staged ??= await LiveFeed.CheckAndStageAsync(LiveFeed.Current, liveCancellation.Token);
            if (staged is null) { liveUpdateStatus.Text = Loc.F("v{0} · No new update", LiveFeed.Current.ToString(3)); return; }
            stagedUpdate = staged;
            liveInstall.IsVisible = true;
            liveUpdateStatus.Text = Loc.F("v{0} downloaded · Will install on next launch", staged.Manifest.Version);
            // Automatically install only before an editing session starts. Never interrupt a connected save.
            if (livePreferences.AutomaticUpdates && backend.State.Status == ConnectionStatus.Disconnected && !busy && !HasLiveDrafts() && !OwnedWindows.Any(w => w.IsVisible)) ApplyLiveUpdate();
        }
        catch (OperationCanceledException) { if (!liveCancellation.IsCancellationRequested) liveUpdateStatus.Text = Loc.S("Update server unavailable · Will retry later"); }
        catch (Exception ex) { liveUpdateStatus.Text = Loc.F("Update could not be downloaded: {0}", ex.Message); }
        finally { liveChecking = false; }
    }

    void ApplyLiveUpdate()
    {
        if (stagedUpdate is null || liveInstalling) return;
        if (busy || HasLiveDrafts() || OwnedWindows.Any(w => w.IsVisible))
        { liveUpdateStatus.Text = Loc.S("Installation waiting · Finish open operations and unsaved edits"); return; }
        try
        {
            LiveInstaller.Start(stagedUpdate);
            liveInstalling = true;
            IsEnabled = false;
            timer.Stop(); saveWatcher.Stop(); backend.Dispose();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            else Close();
        }
        catch (Exception ex) { liveInstalling = false; IsEnabled = true; liveUpdateStatus.Text = Loc.F("Installation could not start: {0}", ex.Message); }
    }

    void ShowLiveUpdates()
    {
        var auto = new CheckBox { Content = Loc.S("Download updates automatically and install when safe"), IsChecked = livePreferences.AutomaticUpdates };
        auto.IsCheckedChanged += (_, _) => { livePreferences.AutomaticUpdates = auto.IsChecked == true; try { livePreferences.Save(); } catch (Exception ex) { status.Text = ex.Message; } };
        var check = new Button { Content = Loc.S("Check now") };
        var close = new Button { Content = "Kapat" };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "FM27 Live v" + LiveFeed.Current.ToString(3), FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = Loc.F("Release channel: {0}", LiveFeed.Repository), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(auto);
        panel.Children.Add(new TextBlock { Text = "Oyun bağlıyken güncelleme indirilebilir; kurulumu sonraki açılışa bırakılır.\nF8: Paneli aç/küçült · F11: Paneli genişlet/daralt\nOyuna tıklayınca panel küçük FM27 düğmesine döner.\nOverlay için FM'de kenarlıksız tam ekran veya pencere modu kullan.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(check); panel.Children.Add(close);
        var window = new Window { Title = Loc.S("FM27 Live — Updates"), Width = 580, Height = 330, CanResize = false, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = overlayMode };
        check.Click += async (_, _) => { window.Close(); await CheckLiveUpdatesAsync(true); };
        close.Click += (_, _) => window.Close();
        _ = window.ShowDialog(this);
    }
}
