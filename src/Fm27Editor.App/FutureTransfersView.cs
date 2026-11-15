using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App;

/// <summary>Players screen tab listing every pending future transfer (FMRTE has the same list), next to "Retiring".</summary>
public sealed partial class MainWindow
{
    public sealed record FutureTransferRow(PlayerRecord Player, FutureTransfer Transfer)
    {
        public string Name => Player.DisplayName;
        public string From => Transfer.Offer.From ?? "";
        public string To => Transfer.Offer.To ?? "";
        public string Type => Transfer.Offer.Type == ProposalType.Loan ? Loc.S("Loan") : Loc.S("Transfer");
        public string FeeText => CurrencySetting.Format(Transfer.Offer.Value);
        public string DateText => DateSetting.Format(Transfer.Date);
        public string AgeText => Player.Age?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    DataGrid? futureGrid;
    Control? futureView;
    TextBox? futureSearch;
    TextBlock? futureCountLabel;
    List<FutureTransferRow> futureAllRows = [];
    bool futureDirty = true;

    Control BuildFutureTransfersView()
    {
        futureSearch = new TextBox { Watermark = Loc.S("Search future transfers…"), HorizontalAlignment = HorizontalAlignment.Stretch };
        futureSearch.TextChanged += (_, _) => ApplyFutureFilter();
        futureCountLabel = new TextBlock { Foreground = UiKit.TextMuted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(futureCountLabel, 1);
        top.Children.Add(futureSearch);
        top.Children.Add(futureCountLabel);
        var filterBar = new Border
        {
            Background = UiKit.Surface, BorderBrush = UiKit.BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 7), Child = top,
        };

        futureGrid = ToolGrid.Create();
        futureGrid.Columns.Add(ToolGrid.Text<FutureTransferRow>("Name", r => r.Name, 210, "Player name", bold: true));
        futureGrid.Columns.Add(ToolGrid.Number<FutureTransferRow>("Age", r => r.AgeText, 52, "Age"));
        futureGrid.Columns.Add(ToolGrid.Text<FutureTransferRow>("From", r => r.From, 175, "Selling club"));
        futureGrid.Columns.Add(ToolGrid.Text<FutureTransferRow>("To", r => r.To, 175, "Buying club"));
        futureGrid.Columns.Add(ToolGrid.Text<FutureTransferRow>("Type", r => r.Type, 80, "Transfer or loan"));
        futureGrid.Columns.Add(ToolGrid.Number<FutureTransferRow>("Fee", r => r.FeeText, 120, "Agreed fee"));
        futureGrid.Columns.Add(ToolGrid.Text<FutureTransferRow>("Date", r => r.DateText, 0, "Date the move goes through", minWidth: 110));
        ToolGrid.EnableSort(futureGrid, new Dictionary<string, Func<FutureTransferRow, IComparable?>>
        {
            ["Name"] = r => r.Name.ToLowerInvariant(),
            ["Age"] = r => r.Player.Age,
            ["From"] = r => r.From.ToLowerInvariant(),
            ["To"] = r => r.To.ToLowerInvariant(),
            ["Type"] = r => r.Type,
            ["Fee"] = r => r.Transfer.Offer.Value,
            ["Date"] = r => r.Transfer.Date,
        });
        futureGrid.DoubleTapped += (_, e) =>
        {
            if (UiKit.OnRow(e) && futureGrid.SelectedItem is FutureTransferRow row) ShowDetailTab(row.Player);
        };
        futureGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && futureGrid.SelectedItem is FutureTransferRow row) { ShowDetailTab(row.Player); e.Handled = true; }
        };
        AttachPlayerContextMenu(futureGrid, item => (item as FutureTransferRow)?.Player, RefreshFutureTransfers);

        var dock = new DockPanel { Background = UiKit.WindowBg };
        DockPanel.SetDock(filterBar, Dock.Top);
        dock.Children.Add(filterBar);
        dock.Children.Add(futureGrid);
        return dock;
    }

    void InvalidateFutureTransfers() => futureDirty = true;

    void RefreshFutureTransfers()
    {
        futureDirty = false;
        if (backend.State.Status != ConnectionStatus.Connected) { futureAllRows = []; ApplyFutureFilter(); return; }
        try
        {
            var session = backend.Session;
            var all = rows.OfType<PlayerRecord>().ToList();
            if (all.Count == 0) all = session.Players.LoadAll().ToList();
            futureAllRows = new FutureTransferReader(session.Memory, session.Profile, session.ModuleBase).ReadAll(all)
                .Select(x => new FutureTransferRow(x.Player, x.Transfer))
                .OrderBy(r => r.Transfer.Date).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ApplyFutureFilter();
        }
        catch (Exception ex) { status.Text = UserMessage.For(ex); }
    }

    void ApplyFutureFilter()
    {
        if (futureGrid is null) return;
        string q = (futureSearch?.Text ?? "").Trim();
        var filtered = futureAllRows.Where(r => q.Length == 0
            || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.From.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.To.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ToolGrid.SetItems(futureGrid, filtered);
        if (futureCountLabel is not null)
            futureCountLabel.Text = filtered.Count == futureAllRows.Count
                ? Loc.F("Showing {0:N0} future transfers", filtered.Count)
                : Loc.F("Showing {0:N0} of {1:N0} future transfers", filtered.Count, futureAllRows.Count);
    }
}
