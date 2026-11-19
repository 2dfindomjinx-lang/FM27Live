using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Fm27Editor.App.Live;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    Border? liveBarSurface;
    Control? desktopContent, overlayPanel, overlayHandle;
    ScrollViewer? overlayViewport;
    bool overlayCollapsed, overlayWide;
    double desktopMinWidth, desktopMinHeight;
    int overlayAnchorRight, overlayAnchorTop;
    double overlayScale = 1;
    PixelRect overlayArea;

    void SetOverlay(bool enabled)
    {
        if (enabled == overlayMode) { if (enabled) ExpandOverlay(); return; }
        if (enabled)
        {
            desktopPosition = Position; desktopSize = new Size(Width, Height); desktopState = WindowState;
            desktopMinWidth = MinWidth; desktopMinHeight = MinHeight;
            desktopContent = (Control)Content!;
            Content = null;
            overlayMode = true;
            WindowState = WindowState.Normal;
            SystemDecorations = SystemDecorations.BorderOnly;
            Topmost = true;
            if (livePreferences.OverlayLayoutVersion < 2)
            {
                livePreferences.OverlayWidth = 760; livePreferences.OverlayHeight = 640;
                livePreferences.OverlayX = null; livePreferences.OverlayY = null;
                livePreferences.OverlayLayoutVersion = 2;
            }
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            overlayScale = screen?.Scaling ?? 1;
            overlayArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            overlayAnchorRight = Math.Clamp(livePreferences.OverlayX ?? overlayArea.Right - 16,
                overlayArea.X + (int)(OverlayLayout.HandleWidth * overlayScale), overlayArea.Right);
            overlayAnchorTop = Math.Clamp(livePreferences.OverlayY ?? overlayArea.Y + 80, overlayArea.Y,
                Math.Max(overlayArea.Y, overlayArea.Bottom - (int)(OverlayLayout.HandleHeight * overlayScale)));
            liveBarSurface!.IsVisible = false;
            desktopContent.Width = 1040; desktopContent.Height = 720;
            overlayViewport = new ScrollViewer { Content = desktopContent,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            overlayPanel = BuildOverlayPanel(overlayViewport);
            overlayHandle = BuildOverlayHandle();
            Title = "FM27 Live — Overlay";
            // Overlay starts as a small handle, even when the desktop editor was maximized/fullscreen.
            CollapseOverlay(false);
        }
        else
        {
            SaveOverlayGeometry();
            Content = null;
            overlayViewport!.Content = null;
            desktopContent!.Width = double.NaN; desktopContent.Height = double.NaN;
            liveBarSurface!.IsVisible = true;
            Content = desktopContent;
            overlayPanel = null; overlayHandle = null; overlayViewport = null;
            overlayMode = false; overlayCollapsed = false;
            MaxWidth = double.PositiveInfinity; MaxHeight = double.PositiveInfinity;
            MinWidth = desktopMinWidth; MinHeight = desktopMinHeight; CanResize = true;
            Topmost = false; Opacity = 1; SystemDecorations = SystemDecorations.Full;
            Width = desktopSize.Width; Height = desktopSize.Height; Position = desktopPosition; WindowState = desktopState;
            Title = "FM27 Live";
            Show(); Activate();
        }
    }

    Control BuildOverlayPanel(Control viewport)
    {
        var header = new DockPanel { Margin = new Thickness(6), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void Add(string label, Action action)
        {
            var button = new Button { Content = label, Height = 28 };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        Add("↔", ToggleOverlaySize);
        Add("Uygulama", () => SetOverlay(false));
        Add("Küçült · F8", HideOverlay);
        DockPanel.SetDock(buttons, Dock.Right); header.Children.Add(buttons);
        var grip = new TextBlock { Text = "FM27 LIVE", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0), FontWeight = Avalonia.Media.FontWeight.SemiBold };
        grip.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        header.Children.Add(grip);
        var panel = new DockPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(viewport);
        return new Border { Background = UiKit.WindowBg, BorderBrush = UiKit.Accent, BorderThickness = new Thickness(1), Child = panel };
    }

    Control BuildOverlayHandle()
    {
        var button = new Button { Content = "FM27  ›", Height = 38, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        button.Click += (_, _) => ExpandOverlay();
        ToolTip.SetTip(button, "Paneli aç · F8. Sol kenardan sürükle. Sağ tık: gizle.");
        var menu = new ContextMenu();
        var normal = new MenuItem { Header = "Normal uygulama" }; normal.Click += (_, _) => SetOverlay(false);
        var hide = new MenuItem { Header = "Tamamen gizle (F8 ile aç)" }; hide.Click += (_, _) => { SaveOverlayGeometry(); Hide(); };
        menu.Items.Add(normal); menu.Items.Add(hide); button.ContextMenu = menu;
        // Left edge is an explicit drag grip, independent of the expand button.
        var grip = new Border { Width = 14, Background = UiKit.Accent, Cursor = new Cursor(StandardCursorType.SizeAll) };
        grip.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        var dock = new DockPanel(); DockPanel.SetDock(grip, Dock.Left); dock.Children.Add(grip); dock.Children.Add(button);
        return new Border { BorderBrush = UiKit.Accent, BorderThickness = new Thickness(1), Background = UiKit.WindowBg, Child = dock };
    }

    void ExpandOverlay()
    {
        if (!overlayMode || OwnedWindows.Any(w => w.IsVisible)) return;
        if (overlayCollapsed) SaveOverlayGeometry();
        overlayCollapsed = false;
        var size = OverlayLayout.Fit(overlayArea.Width / overlayScale, overlayArea.Height / overlayScale,
            overlayWide ? 1100 : livePreferences.OverlayWidth, livePreferences.OverlayHeight);
        MaxWidth = double.PositiveInfinity; MaxHeight = double.PositiveInfinity;
        MinWidth = Math.Min(420, size.MaxWidth); MinHeight = Math.Min(300, size.MaxHeight);
        MaxWidth = size.MaxWidth; MaxHeight = size.MaxHeight; CanResize = true;
        WindowState = WindowState.Normal;
        Content = overlayPanel;
        Width = size.Width; Height = size.Height; Opacity = Math.Clamp(livePreferences.OverlayOpacity, 0.75, 1);
        Position = new PixelPoint(Math.Clamp(overlayAnchorRight - (int)(Width * overlayScale), overlayArea.X, overlayArea.Right - (int)(Width * overlayScale)),
            Math.Clamp(overlayAnchorTop, overlayArea.Y, overlayArea.Bottom - (int)(Height * overlayScale)));
        Show(); Activate();
    }

    void ToggleOverlaySize()
    {
        if (!overlayMode) return;
        overlayWide = !overlayWide;
        ExpandOverlay();
    }

    void CollapseOverlay(bool restoreFocus)
    {
        if (!overlayMode || OwnedWindows.Any(w => w.IsVisible)) return;
        if (overlayCollapsed || ReferenceEquals(Content, overlayPanel)) SaveOverlayGeometry();
        overlayCollapsed = true;
        WindowState = WindowState.Normal;
        MinWidth = 0; MinHeight = 0; MaxWidth = OverlayLayout.HandleWidth; MaxHeight = OverlayLayout.HandleHeight;
        CanResize = false; Content = overlayHandle;
        Width = OverlayLayout.HandleWidth; Height = OverlayLayout.HandleHeight; Opacity = 1;
        Position = new PixelPoint(Math.Clamp(overlayAnchorRight - (int)(Width * overlayScale), overlayArea.X, overlayArea.Right - (int)(Width * overlayScale)),
            Math.Clamp(overlayAnchorTop, overlayArea.Y, overlayArea.Bottom - (int)(Height * overlayScale)));
        Show();
        if (restoreFocus) OverlayHotkey.Restore(gameFocus);
    }

    void HideOverlay() => CollapseOverlay(true);

    void SaveOverlayGeometry()
    {
        if (!overlayMode) return;
        // The handle is the anchor. A panel may be shifted upward/left to fit the screen;
        // saving that temporary position must never move the handle on the next collapse.
        if (overlayCollapsed)
        {
            overlayAnchorRight = Position.X + (int)(Width * overlayScale);
            overlayAnchorTop = Position.Y;
        }
        livePreferences.OverlayX = overlayAnchorRight; livePreferences.OverlayY = overlayAnchorTop;
        if (!overlayCollapsed && ReferenceEquals(Content, overlayPanel))
        {
            if (!overlayWide) livePreferences.OverlayWidth = Width;
            livePreferences.OverlayHeight = Height;
        }
        try { livePreferences.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
