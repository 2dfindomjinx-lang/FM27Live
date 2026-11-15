using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    public sealed record RetiringRow(PlayerRecord Player, DateOnly? RetirementDate, RetirementReason Reasons)
    {
        public string Name => Player.DisplayName;
        public string Club => string.IsNullOrEmpty(Player.ClubName) ? Loc.S("Free") : Player.ClubName;
        public string AgeText => Player.Age?.ToString(CultureInfo.InvariantCulture) ?? "";
        public string DateText => RetirementDate is { } d ? DateSetting.Format(d) : "—";
        public string ReasonText => FormatRetirementReasons(Reasons);
    }

    static string FormatRetirementReasons(RetirementReason reasons)
    {
        var parts = new List<string>(3);
        if (reasons.HasFlag(RetirementReason.PlannedRetirement)) parts.Add(Loc.S("Planned retirement"));
        if (reasons.HasFlag(RetirementReason.RetiringAfterSpell)) parts.Add(Loc.S("Retiring after spell at club"));
        if (reasons.HasFlag(RetirementReason.WillRetireAtTop)) parts.Add(Loc.S("Will retire at top"));
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    sealed record ReasonFilterChoice(RetirementReason? Filter, string Key)
    {
        public override string ToString() => Loc.S(Key);
    }

    DataGrid? retiringGrid;
    TextBox? retiringSearch;
    ComboBox? retiringReasonCombo;
    TextBlock? retiringCountLabel;
    List<RetiringRow> retiringAllRows = [];
    bool retiringDirty = true;

    Control BuildRetiringView()
    {
        retiringSearch = new TextBox { Watermark = Loc.S("Search retiring…"), HorizontalAlignment = HorizontalAlignment.Stretch };
        retiringSearch.TextChanged += (_, _) => ApplyRetiringFilter();

        retiringReasonCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                new ReasonFilterChoice(null, "All reasons"),
                new ReasonFilterChoice(RetirementReason.PlannedRetirement, "Planned retirement"),
                new ReasonFilterChoice(RetirementReason.RetiringAfterSpell, "Retiring after spell at club"),
                new ReasonFilterChoice(RetirementReason.WillRetireAtTop, "Will retire at top"),
            },
            SelectedIndex = 0,
            Width = 220,
        };
        retiringReasonCombo.SelectionChanged += (_, _) => ApplyRetiringFilter();

        retiringCountLabel = new TextBlock
        {
            Foreground = UiKit.TextMuted,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,220,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(retiringSearch, 0);
        Grid.SetColumn(retiringReasonCombo, 1);
        Grid.SetColumn(retiringCountLabel, 2);
        topGrid.Children.Add(retiringSearch);
        topGrid.Children.Add(retiringReasonCombo);
        topGrid.Children.Add(retiringCountLabel);

        var filterBar = new Border
        {
            Background = UiKit.Surface,
            BorderBrush = UiKit.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 7),
            Child = topGrid,
        };

        retiringGrid = ToolGrid.Create();
        ConfigureRetiringColumns();
        retiringGrid.DoubleTapped += (_, e) =>
        {
            if (UiKit.OnRow(e) && retiringGrid.SelectedItem is RetiringRow row)
                ShowDetailTab(row.Player);
        };
        retiringGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && retiringGrid.SelectedItem is RetiringRow row)
            {
                ShowDetailTab(row.Player);
                e.Handled = true;
            }
        };
        AttachPlayerContextMenu(retiringGrid, item => (item as RetiringRow)?.Player, RefreshRetiringPlayers);

        var dock = new DockPanel { Background = UiKit.WindowBg };
        DockPanel.SetDock(filterBar, Dock.Top);
        dock.Children.Add(filterBar);
        dock.Children.Add(retiringGrid);
        return dock;
    }

    void ConfigureRetiringColumns()
    {
        if (retiringGrid is null) return;
        retiringGrid.Columns.Clear();
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Name", r => r.Name, 210, "Player name", bold: true));
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Club", r => r.Club, 175, "Current club"));
        retiringGrid.Columns.Add(ToolGrid.Number<RetiringRow>("Age", r => r.AgeText, 52, "Age"));
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Retirement Date", r => r.DateText, 130, "Planned retirement date"));
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Reason", r => r.ReasonText, 0, "Retirement reason or flag", minWidth: 200));

        var sortKeys = new Dictionary<string, Func<RetiringRow, IComparable?>>
        {
            ["Name"] = r => r.Name.ToLowerInvariant(),
            ["Club"] = r => r.Player.ClubName.Length > 0 ? r.Player.ClubName.ToLowerInvariant() : null,
            ["Age"] = r => r.Player.Age,
            ["Retirement Date"] = r => r.RetirementDate,
            ["Reason"] = r => r.ReasonText.ToLowerInvariant(),
        };
        ToolGrid.EnableSort(retiringGrid, sortKeys);
    }

    void InvalidateRetiringPlayers() => retiringDirty = true;

    void RefreshRetiringPlayers()
    {
        retiringDirty = false;
        if (backend.State.Status != ConnectionStatus.Connected)
        {
            retiringAllRows = [];
            ApplyRetiringFilter();
            return;
        }
        try
        {
            var session = backend.Session;
            var all = rows.OfType<PlayerRecord>().ToList();
            if (all.Count == 0)
                all = session.Players.LoadAll().Where(p => p.Gender != Fm27Editor.Core.Offsets.Gender.Women && p.Club?.Gender != Fm27Editor.Core.Offsets.Gender.Women).ToList();
            var reader = new RetiringPlayerReader(session.Memory, session.Profile, session.ModuleBase);
            retiringAllRows = reader.Read(all, session.PlayerFields)
                .Select(r => new RetiringRow(r.Player, r.RetirementDate, r.Reasons))
                .ToList();
            ApplyRetiringFilter();
        }
        catch (Exception ex)
        {
            status.Text = UserMessage.For(ex);
        }
    }

    void ApplyRetiringFilter()
    {
        if (retiringGrid is null) return;
        string q = (retiringSearch?.Text ?? "").Trim();
        var reasonFilter = (retiringReasonCombo?.SelectedItem as ReasonFilterChoice)?.Filter;
        var filtered = retiringAllRows.Where(r =>
        {
            if (reasonFilter is { } rf && !r.Reasons.HasFlag(rf)) return false;
            if (q.Length > 0
                && !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.Club.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.ReasonText.Contains(q, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }).ToList();

        ToolGrid.SetItems(retiringGrid, filtered);
        if (retiringCountLabel is not null)
        {
            retiringCountLabel.Text = filtered.Count == retiringAllRows.Count
                ? Loc.F("Showing {0:N0} retiring players", filtered.Count)
                : Loc.F("Showing {0:N0} of {1:N0} retiring players", filtered.Count, retiringAllRows.Count);
        }
    }

    void RefreshRetiringLocalization()
    {
        if (retiringSearch is not null) retiringSearch.Watermark = Loc.S("Search retiring…");
        if (retiringReasonCombo is not null)
        {
            int idx = retiringReasonCombo.SelectedIndex;
            var items = retiringReasonCombo.ItemsSource;
            retiringReasonCombo.ItemsSource = null;
            retiringReasonCombo.ItemsSource = items;
            retiringReasonCombo.SelectedIndex = idx;
        }
        ConfigureRetiringColumns();
        ApplyRetiringFilter();
    }
}
