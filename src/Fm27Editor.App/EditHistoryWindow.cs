using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.App;

/// <summary>
/// Edit History: every change the editor wrote, grouped by the thing it changed (a club, a player, a nation, a competition,
/// an award...) and, inside that, by save or preset. Each entry and each save can be undone while the game still holds the
/// value the editor wrote.
/// </summary>
public sealed class EditHistoryWindow : Window
{
    const int MaxEntriesShown = 150;

    readonly ChangeJournal journal;
    readonly IEditService edits;
    readonly IProcessMemory memory;
    readonly Action<string> onStatus;
    readonly Action onUndone;
    readonly TextBox search = new() { Watermark = Loc.S("Search changes"), HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly StackPanel list = new() { Spacing = 8 };
    readonly TextBlock message = new() { Foreground = UiKit.Brush("#94A3B8"), VerticalAlignment = VerticalAlignment.Center };
    readonly HashSet<string> collapsed = [];

    public EditHistoryWindow(ChangeJournal journal, IProcessMemory memory, Action<string> onStatus, Action onUndone, IEditService edits)
    {
        this.journal = journal;
        this.edits = edits;
        this.memory = memory;
        this.onStatus = onStatus;
        this.onUndone = onUndone;
        Title = Loc.S("Edit History");
        Width = 760;
        Height = 620;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var clear = new Button { Content = Loc.S("Clear history") };
        clear.Click += (_, _) => journal.Clear();
        var undoEverything = new Button { Content = Loc.S("Undo all"), Margin = new Thickness(0, 0, 8, 0) };
        undoEverything.Click += async (_, _) =>
        {
            int pending = journal.Groups.Sum(g => g.Entries.Count(e => !e.Undone));
            if (pending == 0) { Report(Loc.S("Nothing to undo")); return; }
            if (!await ConfirmAsync(Loc.F("Undo all {0:N0} change(s) in the history?", pending))) return;
            int restored = 0, refused = 0;
            foreach (var group in journal.Groups.OrderByDescending(g => g.Time))
            {
                var (ok, errors) = UndoGroup(group);
                restored += ok; refused += errors.Count;
            }
            Report(refused > 0
                ? Loc.F("Undo all: {0:N0} restored, {1:N0} refused (the game changed them since)", restored, refused)
                : Loc.F("Undo all: {0:N0} restored", restored));
            if (restored > 0) onUndone();
        };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(undoEverything, Dock.Right);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true, Children = { clear, undoEverything, search } };
        search.Margin = new Thickness(0, 0, 8, 0);

        var close = new Button { Content = Loc.S("Close"), Classes = { "accent" } };
        close.Click += (_, _) => Close();
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(message);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = root;

        search.TextChanged += (_, _) => Rebuild();
        void OnChanged() => Dispatcher.UIThread.Post(Rebuild);
        journal.Changed += OnChanged;
        Closed += (_, _) => journal.Changed -= OnChanged;
        Rebuild();
    }

    static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    void Rebuild()
    {
        list.Children.Clear();
        string q = search.Text?.Trim() ?? "";
        bool Matches(ChangeGroup g, ChangeEntry? e = null) => q.Length == 0
            || g.Kind.Contains(q, StringComparison.OrdinalIgnoreCase) || Loc.S(g.Kind).Contains(q, StringComparison.OrdinalIgnoreCase)
            || g.Entity.Contains(q, StringComparison.OrdinalIgnoreCase)
            || g.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || Loc.S(g.Title).Contains(q, StringComparison.OrdinalIgnoreCase)
            || (e is null ? g.Entries.Any(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || Loc.S(x.Name).Contains(q, StringComparison.OrdinalIgnoreCase))
                          : e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || Loc.S(e.Name).Contains(q, StringComparison.OrdinalIgnoreCase));

        var groups = journal.Groups.Where(g => Matches(g)).OrderByDescending(g => g.Time).ToList();
        message.Text = groups.Count == 0 ? "" : Loc.F("{0:N0} change(s) in {1} save(s)", groups.Sum(g => g.Entries.Count), groups.Count);
        foreach (var entity in groups.GroupBy(g => (g.Kind, g.Entity)).OrderByDescending(e => e.Max(g => g.Time)))
            list.Children.Add(EntityBlock(entity.Key.Kind, entity.Key.Entity, entity.ToList(), q, Matches));
    }

    Control EntityBlock(string kind, string entity, List<ChangeGroup> groups, string q, Func<ChangeGroup, ChangeEntry?, bool> matches)
    {
        string key = kind + "\u0001" + entity;
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0), IsVisible = !collapsed.Contains(key) };
        foreach (var group in groups) body.Children.Add(GroupBlock(group, q, matches));

        var toggle = new Button
        {
            Content = $"{(body.IsVisible ? "⌄" : "›")}  {Loc.S(entity)}",
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 2),
        };
        toggle.Click += (_, _) => { if (!collapsed.Remove(key)) collapsed.Add(key); Rebuild(); };
        var badge = new TextBlock
        {
            Text = Loc.F("{0} · {1:N0} change(s)", Loc.S(kind), groups.Sum(g => g.Entries.Count)),
            Foreground = UiKit.Brush("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { toggle, badge } };
        return new Border
        {
            Background = UiKit.SurfaceAlt,
            BorderBrush = UiKit.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new StackPanel { Children = { header, body } },
        };
    }

    Control GroupBlock(ChangeGroup group, string q, Func<ChangeGroup, ChangeEntry?, bool> matches)
    {
        var undoAll = new Button { Content = group.FullyUndone ? Loc.S("Undone") : Loc.S("Undo all"), IsEnabled = !group.FullyUndone };
        undoAll.Click += (_, _) =>
        {
            var (restored, errors) = UndoGroup(group);
            string groupTitle = Loc.S(group.Title);
            string groupEntity = Loc.S(group.Entity);
            Report(errors.Count > 0
                ? Loc.F("Undo {0} ({1}): {2} restored, {3} refused ({4})", groupTitle, groupEntity, restored, errors.Count, errors[0])
                : Loc.F("Undo {0} ({1}): {2} restored", groupTitle, groupEntity, restored));
            if (restored > 0) onUndone();
        };
        var title = new TextBlock
        {
            Text = $"{Time(group.Time)}  ·  {Loc.S(group.Entity)}  ·  {Loc.S(group.Title)}  ·  {group.Entries.Count:N0}",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        DockPanel.SetDock(undoAll, Dock.Right);
        head.Children.Add(undoAll);
        head.Children.Add(title);

        var rows = new StackPanel { Spacing = 3 };
        var shown = group.Entries.Where(e => matches(group, e) || q.Length == 0 || group.Entity.Contains(q, StringComparison.OrdinalIgnoreCase)
            || group.Kind.Contains(q, StringComparison.OrdinalIgnoreCase) || Loc.S(group.Kind).Contains(q, StringComparison.OrdinalIgnoreCase)
            || group.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || Loc.S(group.Title).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var entry in shown.Take(MaxEntriesShown)) rows.Children.Add(EntryRow(entry));
        if (shown.Count > MaxEntriesShown)
            rows.Children.Add(new TextBlock { Text = Loc.F("… {0:N0} more; Undo all restores every one", shown.Count - MaxEntriesShown), Foreground = UiKit.Brush("#94A3B8") });

        return new Border
        {
            Background = UiKit.WindowBg,
            BorderBrush = UiKit.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new StackPanel { Children = { head, rows } },
        };
    }

    (int Restored, List<string> Errors) UndoGroup(ChangeGroup group)
    {
        try
        {
            (int Restored, List<string> Errors) result = (0, []);
            edits.ExecuteUnrecorded(() => result = journal.Undo(group, memory));
            return result;
        }
        catch (Exception ex) { return (0, [UserMessage.For(ex)]); }
    }

    bool UndoEntry(ChangeEntry entry, out string? error)
    {
        string? reason = null;
        bool restored = false;
        try { edits.ExecuteUnrecorded(() => restored = journal.Undo(entry, memory, out reason)); }
        catch (Exception ex) { reason = UserMessage.For(ex); }
        error = reason;
        return restored;
    }

    Control EntryRow(ChangeEntry entry)
    {
        var undo = new Button { Content = entry.Undone ? Loc.S("Undone") : Loc.S("Undo"), IsEnabled = !entry.Undone, MinWidth = 64 };
        undo.Click += (_, _) =>
        {
            if (UndoEntry(entry, out var error)) { Report(Loc.F("Restored {0} to {1}", Loc.S(entry.Name), entry.BeforeText)); onUndone(); }
            else Report(Loc.F("Cannot undo {0}: {1}", Loc.S(entry.Name), error ?? ""));
        };
        var name = new TextBlock { Text = Loc.S(entry.Name), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var values = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = entry.BeforeText, Foreground = UiKit.Brush("#7E8BA3"), TextDecorations = TextDecorations.Strikethrough },
                new TextBlock { Text = "→", Foreground = UiKit.Brush("#7E8BA3") },
                new TextBlock { Text = entry.AfterText },
            },
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(undo, Dock.Right);
        row.Children.Add(undo);
        row.Children.Add(new StackPanel { Children = { name, values } });
        return row;
    }

    async Task<bool> ConfirmAsync(string question)
    {
        var dialog = new Window
        {
            Title = Loc.S("Confirm"), Width = 360, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var yes = new Button { Content = Loc.S("Yes"), MinWidth = 72, Classes = { "accent" } };
        var no = new Button { Content = Loc.S("No"), MinWidth = 72 };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 14,
            Children =
            {
                new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }

    void Report(string text)
    {
        message.Text = text;
        onStatus(text);
    }
}
