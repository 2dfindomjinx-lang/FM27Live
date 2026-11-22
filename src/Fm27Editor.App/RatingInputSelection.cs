using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Fm27Editor.App;

/// <summary>First click replaces the whole rating; subsequent clicks and drags edit individual digits.</summary>
internal static class RatingInputSelection
{
    public static void Enable(TextBox input)
    {
        bool selectOnRelease = false;
        Point pressedAt = default;
        input.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod == NavigationMethod.Pointer) selectOnRelease = true;
            else input.SelectAll();
        };
        input.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(input).Properties.IsLeftButtonPressed || e.KeyModifiers != KeyModifiers.None)
            { selectOnRelease = false; return; }
            pressedAt = e.GetPosition(input);
            selectOnRelease |= !input.IsKeyboardFocusWithin;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        input.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var distance = e.GetPosition(input) - pressedAt;
            if (distance.X * distance.X + distance.Y * distance.Y > 16) selectOnRelease = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        input.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (selectOnRelease && e.InitialPressMouseButton == MouseButton.Left && e.KeyModifiers == KeyModifiers.None)
                input.SelectAll();
            selectOnRelease = false;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        input.LostFocus += (_, _) => selectOnRelease = false;
    }
}
