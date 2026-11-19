using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Fm27Editor.App;

/// <summary>Also protects incomplete/invalid text which has not reached a draft's LostFocus handler.</summary>
internal static class EditorRefreshGuard
{
    sealed class State { public readonly Dictionary<TextBox, string?> Original = []; public int Rendering; }
    static readonly ConditionalWeakTable<Control, State> States = new();

    static EditorRefreshGuard()
    {
        // Capture when input starts, not when a template initializes its TextBox after Render returns.
        InputElement.GotFocusEvent.AddClassHandler<TextBox>((box, _) =>
        {
            foreach (var parent in box.GetLogicalAncestors().OfType<Control>().Concat(box.GetVisualAncestors().OfType<Control>()).Distinct())
                if (States.TryGetValue(parent, out var state) && state.Rendering == 0) state.Original.TryAdd(box, box.Text);
        });
    }

    public static bool Protects(Control root) =>
        (States.TryGetValue(root, out var state) && state.Original.Any(pair => pair.Key.Text != pair.Value)) ||
        root.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsKeyboardFocusWithin);

    public static IDisposable Rebuilding(Control root)
    {
        var state = States.GetOrCreateValue(root);
        state.Rendering++;
        return new Scope(state);
    }

    sealed class Scope(State state) : IDisposable
    {
        public void Dispose() { state.Rendering--; state.Original.Clear(); }
    }
}
