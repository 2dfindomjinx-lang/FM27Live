using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

/// <summary>Read-only list of the Coach, Club or Nation matches produced by Advanced Search.</summary>
internal sealed class SearchResultsWindow : Window
{
    sealed record StaffRow(string Name, string Age, string Club, string Job, string Nationality, string Ca, string Pa);
    sealed record ClubRow(string Name, string Id);
    sealed record NationRow(string Name, string ShortName, string Id);

    public string StatusMessage { get; } = "";

    public SearchResultsWindow(string scope, IReadOnlyList<StaffRecord> staff, IReadOnlyList<ClubEntry> clubs, DateOnly? gameDate,
        int? totalCount = null, string? filterNote = null, IReadOnlyList<NationRecord>? nations = null)
    {
        int shown = scope switch
        {
            "Coach" => staff.Count,
            "Nation" => nations?.Count ?? 0,
            _ => clubs.Count,
        };
        string noun = Loc.S(scope switch
        {
            "Coach" => "staff",
            "Nation" => "nations",
            _ => "clubs",
        });
        Title = Loc.F("Advanced Search Results - {0:N0} {1}", shown, noun);
        if (totalCount is { } total && shown < total)
        {
            string note = string.IsNullOrEmpty(filterNote) ? "" : $", {Loc.S(filterNote)}";
            StatusMessage = Loc.F("{0:N0} of {1:N0} {2} shown ({3:N0} hidden{4})", shown, total, noun, total - shown, note);
        }
        Width = 900;
        Height = 600;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = UiKit.WindowBg;
        FontSize = 13;
        Foreground = UiKit.TextPrimary;

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserReorderColumns = false,
            CanUserResizeColumns = true,
            RowBackground = UiKit.Surface,
        };

        DateOnly today = gameDate ?? DateOnly.FromDateTime(DateTime.Today);
        if (scope == "Coach")
        {
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Name"), Binding = new Avalonia.Data.Binding(nameof(StaffRow.Name)), Width = new DataGridLength(220) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Age"), Binding = new Avalonia.Data.Binding(nameof(StaffRow.Age)), Width = new DataGridLength(70) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Club"), Binding = new Avalonia.Data.Binding(nameof(StaffRow.Club)), Width = new DataGridLength(220) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Job"), Binding = new Avalonia.Data.Binding(nameof(StaffRow.Job)), Width = new DataGridLength(160) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Nationality"), Binding = new Avalonia.Data.Binding(nameof(StaffRow.Nationality)), Width = new DataGridLength(150) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("CA"), Binding = new Avalonia.Data.Binding(nameof(StaffRow.Ca)), Width = new DataGridLength(70) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("PA"), Binding = new Avalonia.Data.Binding(nameof(StaffRow.Pa)), Width = new DataGridLength(70) });
            grid.ItemsSource = staff.Select(s => new StaffRow(
                s.DisplayName,
                s.DateOfBirth is { } born ? (today.Year - born.Year - (born.AddYears(today.Year - born.Year) > today ? 1 : 0)).ToString() : "",
                s.Club?.DisplayName ?? "",
                Loc.S(StaffLabels.Job(s.JobCode)),
                s.Nationality?.DisplayName ?? "",
                s.CurrentAbility.ToString(),
                s.PotentialAbility.ToString())).ToArray();
        }
        else if (scope == "Nation")
        {
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Name"), Binding = new Avalonia.Data.Binding(nameof(NationRow.Name)), Width = new DataGridLength(320) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Short Name"), Binding = new Avalonia.Data.Binding(nameof(NationRow.ShortName)), Width = new DataGridLength(160) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("ID"), Binding = new Avalonia.Data.Binding(nameof(NationRow.Id)), Width = new DataGridLength(100) });
            grid.ItemsSource = (nations ?? []).Select(n => new NationRow(n.DisplayName, n.ShortName ?? "", n.Id?.ToString() ?? "")).ToArray();
        }
        else
        {
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Club"), Binding = new Avalonia.Data.Binding(nameof(ClubRow.Name)), Width = new DataGridLength(400) });
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("ID"), Binding = new Avalonia.Data.Binding(nameof(ClubRow.Id)), Width = new DataGridLength(100) });
            grid.ItemsSource = clubs.Select(c => new ClubRow(c.DisplayName, c.Id?.ToString() ?? "")).ToArray();
        }

        var close = new Button { Content = Loc.S("Close"), MinWidth = 80 };
        close.Click += (_, _) => Close();
        var bottomDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        bottomDock.Children.Add(close);
        bottomDock.Children.Add(new TextBlock
        {
            Text = StatusMessage,
            Foreground = UiKit.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        var bottom = new Border
        {
            Background = UiKit.SurfaceHeader,
            BorderBrush = UiKit.BorderSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10),
            Child = bottomDock
        };

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(grid);
        Content = root;
    }
}
