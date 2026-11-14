using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Fm27Editor.Core.Clubs;

namespace Fm27Editor.App;

/// <summary>Club pages of Advanced Search: Overview and Finances, using only fields mapped in the profile.</summary>
internal sealed class ClubFilterPanel : UserControl
{
    static readonly (ClubField Field, string Label)[] OverviewFields =
    [
        (ClubField.Status, "Status"),
        (ClubField.TeamReputation, "Reputation"),
        (ClubField.AverageAttendance, "Average Attendance"),
        (ClubField.Training, "Training"),
        (ClubField.YouthCoaching, "Youth Coaching"),
        (ClubField.YouthFacilities, "Youth Facilities"),
        (ClubField.YouthRecruitment, "Youth Recruitment"),
        (ClubField.YouthImportance, "Youth Importance"),
        (ClubField.CorporateFacilities, "Corporate Facilities"),
        (ClubField.Morale, "Morale"),
        (ClubField.SeasonTicketHolders, "Season Ticket Holders"),
        (ClubField.SocialMediaFollowers, "Social Media Followers"),
    ];

    static readonly (ClubField Field, string Label)[] FinanceFields =
    [
        (ClubField.Balance, "Balance"),
        (ClubField.TransferBudgetSeason, "Transfer Budget (Season)"),
        (ClubField.TransferBudgetRemaining, "Transfer Budget Remaining"),
        (ClubField.WageBudget, "Wage Budget"),
        (ClubField.RichBenefactor, "Rich Benefactor"),
        (ClubField.AverageTicketPrice, "Average Match Ticket Price"),
        (ClubField.StadiumRentalPerYear, "Stadium Rental Per Year"),
    ];

    readonly IReadOnlyList<ClubEntry> source;
    readonly ClubFieldAccess access;
    readonly Dictionary<string, Control> pages = [];
    readonly List<(ClubField Field, TextBox Min, TextBox Max)> rangeInputs = [];
    readonly TextBox nameBox = new() { Watermark = Loc.S("Name"), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox genderCombo = new() { ItemsSource = new[] { Loc.S("Men"), Loc.S("Women"), Loc.S("All") }, SelectedIndex = 0, Width = 140, MinWidth = 140, MaxWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 260 };

    public event EventHandler? Changed;

    public ClubFilterPanel(IReadOnlyList<ClubEntry> source, ClubFieldAccess access)
    {
        this.source = source;
        this.access = access;

        nameBox.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        genderCombo.SelectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        pages["General"] = Page(OverviewFields, FinanceFields);

        Content = new SectionNav(pages.Select(page => (page.Key, page.Value)).ToArray());
    }

    Control Page(params (ClubField Field, string Label)[][] groups)
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(14, 10) };
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = Loc.S("NAME").ToUpper(Loc.FormatCulture), FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = UiKit.TextMuted },
                        nameBox,
                    }
                },
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = Loc.S("GENDER").ToUpper(Loc.FormatCulture), FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = UiKit.TextMuted },
                        genderCombo,
                    }
                },
            }
        });
        foreach (var group in groups)
        {
            var rows = new StackPanel { Spacing = 6, Margin = new Thickness(10, 8) };
            foreach (var (field, label) in group)
            {
                if (!access.IsMapped(field)) continue;
                var min = new TextBox { Watermark = Loc.S("Min"), Width = 110, HorizontalContentAlignment = HorizontalAlignment.Right };
                var max = new TextBox { Watermark = Loc.S("Max"), Width = 110, HorizontalContentAlignment = HorizontalAlignment.Right };
                rangeInputs.Add((field, min, max));
                foreach (var box in new[] { min, max }) box.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
                var row = new DockPanel { LastChildFill = true, MinHeight = 30 };
                DockPanel.SetDock(max, Dock.Right); row.Children.Add(max);
                DockPanel.SetDock(min, Dock.Right); row.Children.Add(min);
                row.Children.Add(new TextBlock { Text = Loc.S(label), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                rows.Children.Add(row);
            }
            if (rows.Children.Count > 0)
                stack.Children.Add(UiKit.Card(group == OverviewFields ? "Overview" : "Finances", rows));
        }
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack };
    }

    long? Read(ClubEntry club, ClubField field) =>
        access.Root(field) == ClubFieldRoot.Team
            ? access.Read(club.FirstTeam?.Address ?? 0, field)
            : access.Read(club.Address, field);

    public Fm27Editor.Core.Offsets.Gender? SelectedGender() => genderCombo.SelectedIndex switch
    {
        0 => Fm27Editor.Core.Offsets.Gender.Men,
        1 => Fm27Editor.Core.Offsets.Gender.Women,
        _ => null,
    };

    public int TotalCount => source.Count;

    public ClubSearchCriteria? Criteria()
    {
        var ranges = new Dictionary<ClubField, (long, long)>();
        foreach (var (field, minBox, maxBox) in rangeInputs)
        {
            if (!TryParse(minBox.Text, out long? min) || !TryParse(maxBox.Text, out long? max)) return null;
            if (min is null && max is null) continue;
            if (min > max) return null;
            ranges[field] = (min ?? long.MinValue, max ?? long.MaxValue);
        }
        return new ClubSearchCriteria(
            NameContains: string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
            Ranges: ranges,
            Gender: SelectedGender());
    }

    public IReadOnlyList<ClubEntry> Matched()
    {
        var criteria = Criteria();
        return criteria is null
            ? []
            : source.Where(c => ClubSearch.Matches(c, criteria, field => Read(c, field))).ToArray();
    }

    bool HasRangeFilters() =>
        rangeInputs.Any(r => !string.IsNullOrWhiteSpace(r.Min.Text) || !string.IsNullOrWhiteSpace(r.Max.Text));

    public int FilterCount()
    {
        int count = SelectedGender() is not null ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(nameBox.Text)) count++;
        foreach (var (_, min, max) in rangeInputs)
            if (!string.IsNullOrWhiteSpace(min.Text) || !string.IsNullOrWhiteSpace(max.Text)) count++;
        return count;
    }

    public string SummaryStatus()
    {
        int count = FilterCount();
        var gender = SelectedGender();
        string genderTag = gender is { } g ? $" ({Loc.F("Gender: {0}", Loc.S(g.ToString()))})" : "";
        if (HasRangeFilters() || Criteria() is null)
            return count == 0 ? Loc.S("No filters set") : $"{Loc.F("{0} filter(s) set", count)}{genderTag}";

        int shown = Matched().Count;
        int total = source.Count;
        if (count == 0)
            return total > 0 ? Loc.F("All {0:N0} clubs", total) : Loc.S("No filters set");

        string prefix = $"{Loc.F("{0} filter(s)", count)}{genderTag}";
        if (total == 0) return prefix;
        return shown < total
            ? $"{prefix} — {Loc.F("{0:N0} of {1:N0} clubs shown ({2:N0} hidden)", shown, total, total - shown)}"
            : $"{prefix} — {Loc.F("{0:N0} club(s)", shown)}";
    }

    public void Clear()
    {
        nameBox.Text = "";
        genderCombo.SelectedIndex = 0;
        foreach (var (_, min, max) in rangeInputs) { min.Text = ""; max.Text = ""; }
    }

    static bool TryParse(string? text, out long? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) return false;
        value = parsed;
        return true;
    }
}
