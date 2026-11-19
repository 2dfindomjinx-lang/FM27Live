using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App;

/// <summary>Updates only strings emitted by Loc. Never touches editable values or recreates controls.</summary>
internal static class LiveText
{
    sealed record Translation(Func<string> Render);
    static readonly ConditionalWeakTable<string, Translation> outputs = new();
    static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, Translation>> properties = new();
    static readonly List<WeakReference<AvaloniaObject>> controls = [];
    static bool initialized, applying;
    static int uiThreadId;

    public static string Remember(string text, Func<string> render)
    {
        if (text.Length != 0) outputs.GetValue(text, _ => new(render));
        return text;
    }

    public static object? CurrentArgument(object? value) => value is string text && outputs.TryGetValue(text, out var translation) ? translation.Render() : value;

    public static void OnUi(Action action)
    {
        if (uiThreadId == 0 || Environment.CurrentManagedThreadId == uiThreadId) action();
        else Dispatcher.UIThread.Post(action);
    }

    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        uiThreadId = Environment.CurrentManagedThreadId;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>(Track);
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>(Track);
        TextBox.WatermarkProperty.Changed.AddClassHandler<TextBox>(Track);
        Window.TitleProperty.Changed.AddClassHandler<Window>(Track);
        DataGridColumn.HeaderProperty.Changed.AddClassHandler<DataGridColumn>(Track);
        ToolTip.TipProperty.Changed.AddClassHandler<Control>(Track);
        MenuItem.HeaderProperty.Changed.AddClassHandler<MenuItem>(Track);
        NativeMenuItem.HeaderProperty.Changed.AddClassHandler<NativeMenuItem>(Track);
        // Native windows belong to the actual creating thread, even with a permissive dispatcher.
        LanguageSetting.Changed += () => OnUi(Refresh);
    }

    static void Track(AvaloniaObject control, AvaloniaPropertyChangedEventArgs e)
    {
        if (applying) return;
        if (e.NewValue is not string text || !outputs.TryGetValue(text, out var translation))
        {
            if (properties.TryGetValue(control, out var existing)) existing.Remove(e.Property);
            return;
        }
        var entries = properties.GetValue(control, key =>
        {
            controls.Add(new(key));
            return new();
        });
        entries[e.Property] = translation;
        // A combo/list may realize a cached item only after the language has already changed.
        // Translate that newly created label immediately as well.
        if (translation.Render() != text)
            OnUi(() =>
            {
                if (!Equals(control.GetValue(e.Property), text)) return;
                bool previous = applying;
                applying = true;
                try { control.SetCurrentValue(e.Property, translation.Render()); }
                finally { applying = previous; }
            });
    }

    internal static void Refresh()
    {
        applying = true;
        try
        {
            for (int i = controls.Count - 1; i >= 0; i--)
            {
                if (!controls[i].TryGetTarget(out var control)) { controls.RemoveAt(i); continue; }
                if (!properties.TryGetValue(control, out var entries)) continue;
                foreach (var (property, translation) in entries.ToArray())
                    control.SetCurrentValue(property, translation.Render());
            }
        }
        finally { applying = false; }
    }
}
