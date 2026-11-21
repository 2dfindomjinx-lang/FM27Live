using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Fm27Editor.App.Live;

internal static class WindowDragSurface
{
    internal static bool IsInteractive(Visual source, Control surface)
    {
        foreach (var visual in source.GetVisualAncestors().Prepend(source))
        {
            if (ReferenceEquals(visual, surface)) break;
            if (visual is Button or TextBox or ComboBox or Slider or Thumb or MenuItem) return true;
        }
        return false;
    }

    public static void Attach(Control surface, Action<PointerPressedEventArgs> drag)
    {
        surface.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed || e.Source is not Visual source || IsInteractive(source, surface)) return;
            drag(e);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
