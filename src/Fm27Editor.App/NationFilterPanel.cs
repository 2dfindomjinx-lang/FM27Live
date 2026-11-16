using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.Nations;

namespace Fm27Editor.App;

/// <summary>Nation page of Advanced Search: filters nations by name, continent, region and mapped NationField values.</summary>
internal sealed class NationFilterPanel : UserControl
{
    sealed record PointerChoice(ulong? Address, string Name) { public override string ToString() => Name; }
    sealed record CodeChoice(long? Code, string Name) { public override string ToString() => Name; }

    readonly IReadOnlyList<NationRecord> source;
    readonly NationFieldAccess? access;
    readonly IReadOnlyDictionary<ulong, IReadOnlyDictionary<NationField, long>> fieldsByNation;
    readonly IReadOnlyDictionary<ulong, ulong> continentsByNation;
    readonly IReadOnlyDictionary<ulong, AgreementRegion?> directRegions;
    readonly IReadOnlyDictionary<ulong, HashSet<ulong>> agreementRegions;

    readonly TextBox nameBox = new() { Watermark = Loc.S("Name"), HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox continentCombo = MakeCombo();
    readonly ComboBox regionCombo = MakeCombo();
    readonly List<(NationField Field, ComboBox Combo)> choiceInputs = [];
    readonly List<(NationField Field, TextBox Min, TextBox Max)> rangeInputs = [];

    public event EventHandler? Changed;

    public NationFilterPanel(
        IReadOnlyList<NationRecord> source,
        NationFieldAccess? access,
        NationLinks? links = null,
        NationAgreements? agreements = null,
        NationSearchCriteria? initial = null)
    {
        this.source = source;
        this.access = access;

        fieldsByNation = access is null
            ? new Dictionary<ulong, IReadOnlyDictionary<NationField, long>>()
            : access.ReadAllBulk(source.Select(n => n.Address));
        continentsByNation = links is null
            ? new Dictionary<ulong, ulong>()
            : links.ContinentPointers(source.Select(n => n.Address));
        if (agreements is not null)
            (directRegions, agreementRegions) = agreements.ReadNationRegionLinks(source);
        else
        {
            directRegions = new Dictionary<ulong, AgreementRegion?>();
            agreementRegions = new Dictionary<ulong, HashSet<ulong>>();
        }

        var continents = (links?.Continents(source) ?? [])
            .Select(c => new PointerChoice(c.Address, Loc.S(c.Name)))
            .Prepend(new PointerChoice(null, Loc.S("All Continents")))
            .ToArray();
        continentCombo.ItemsSource = continents;
        continentCombo.SelectedIndex = 0;
        continentCombo.ItemTemplate = PointerTemplate();
        continentCombo.SelectionChanged += (_, _) => RaiseChanged();

        var regions = (agreements?.RegionsOfNations(source) ?? [])
            .Select(r => new PointerChoice(r.Address, Loc.S(r.Name)))
            .Prepend(new PointerChoice(null, Loc.S("All Regions")))
            .ToArray();
        regionCombo.ItemsSource = regions;
        regionCombo.SelectedIndex = 0;
        regionCombo.ItemTemplate = PointerTemplate();
        regionCombo.SelectionChanged += (_, _) => RaiseChanged();

        nameBox.TextChanged += (_, _) => RaiseChanged();

        var pages = new (string Title, Control Content)[]
        {
            ("General", BuildGeneralPage()),
            ("Women's National Team", BuildWomenPage()),
        };
        Content = new SectionNav(pages);
        if (initial is not null) SetCriteria(initial);
    }

    /// <summary>Restores the same filter when editing the target batch again.</summary>
    internal void SetCriteria(NationSearchCriteria criteria)
    {
        nameBox.Text = criteria.Name ?? "";
        continentCombo.SelectedItem = continentCombo.Items.OfType<PointerChoice>().FirstOrDefault(x => x.Address == criteria.ContinentAddress)
            ?? continentCombo.Items[0];
        regionCombo.SelectedItem = regionCombo.Items.OfType<PointerChoice>().FirstOrDefault(x => x.Address == criteria.RegionAddress)
            ?? regionCombo.Items[0];
        foreach (var (field, combo) in choiceInputs)
        {
            long? code = criteria.Ranges.TryGetValue(field, out var range) && range.Min == range.Max ? range.Min : null;
            combo.SelectedItem = combo.Items.OfType<CodeChoice>().FirstOrDefault(x => x.Code == code) ?? combo.Items[0];
        }
        foreach (var (field, min, max) in rangeInputs)
        {
            criteria.Ranges.TryGetValue(field, out var range);
            min.Text = range.Min?.ToString(CultureInfo.InvariantCulture) ?? "";
            max.Text = range.Max?.ToString(CultureInfo.InvariantCulture) ?? "";
        }
    }

    public int TotalCount => source.Count;

    static ComboBox MakeCombo() => new()
    {
        Height = 28,
        FontSize = 13,
        MaxDropDownHeight = 260,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    static FuncDataTemplate<PointerChoice> PointerTemplate() =>
        new((choice, _) => new TextBlock
        {
            Text = choice?.Name ?? "",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

    static FuncDataTemplate<CodeChoice> CodeTemplate() =>
        new((choice, _) => new TextBlock
        {
            Text = choice?.Name ?? "",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

    ComboBox AddChoiceBox(NationField field, IEnumerable<(long Code, string Label)> options)
    {
        var items = options.Select(o => new CodeChoice(o.Code, Loc.S(o.Label)))
            .Prepend(new CodeChoice(null, Loc.S("All")))
            .ToArray();
        var combo = MakeCombo();
        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
        combo.ItemTemplate = CodeTemplate();
        combo.SelectionChanged += (_, _) => RaiseChanged();
        choiceInputs.Add((field, combo));
        return combo;
    }

    static Control LabeledField(string label, Control control) => new StackPanel
    {
        Spacing = 3,
        Children =
        {
            new TextBlock
            {
                Text = Loc.S(label).ToUpper(Loc.FormatCulture),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = UiKit.TextMuted,
            },
            control,
        },
    };

    Control BuildGeneralPage()
    {
        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 10,
            Margin = new Thickness(10, 8),
        };

        void Put(int row, int col, string label, Control control)
        {
            var cell = LabeledField(label, control);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            topGrid.Children.Add(cell);
        }

        Put(0, 0, "Name", nameBox);
        Put(0, 1, "Continent", continentCombo);
        Put(0, 2, "Region", regionCombo);
        Put(1, 0, "Development", AddChoiceBox(NationField.Development, NationLabels.DevelopmentOptions));
        Put(1, 1, "Importance", AddChoiceBox(NationField.Importance, NationLabels.ImportanceOptions));
        Put(1, 2, "Gain Nationality Type", AddChoiceBox(NationField.GainNationalityType, NationLabels.GainNationalityTypeOptions));

        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(14, 10) };
        stack.Children.Add(UiKit.Card("General", topGrid));

        AddGroupCard(stack, NationLabels.Groups[0]); // Overview
        AddGroupCard(stack, NationLabels.Groups[1]); // National Team
        AddGroupCard(stack, NationLabels.Groups[2]); // Attendance

        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack };
    }

    Control BuildWomenPage()
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(14, 10) };
        AddGroupCard(stack, NationLabels.Groups[3]); // Women's National Team
        AddGroupCard(stack, NationLabels.Groups[4]); // Women's Attendance
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack };
    }

    void AddGroupCard(StackPanel host, (string Title, (NationField Field, string Label)[] Items) group)
    {
        var rows = new StackPanel { Spacing = 6, Margin = new Thickness(10, 8) };
        foreach (var (field, label) in group.Items)
        {
            if (field is NationField.Development or NationField.Importance or NationField.GainNationalityType)
                continue;
            if (access is not null && !access.IsMapped(field))
                continue;

            var row = new DockPanel { LastChildFill = true, MinHeight = 30 };
            if (field is NationField.DoesNotAllowDualNationality or NationField.AllowMultipleAssistantManagers)
            {
                var combo = AddChoiceBox(field, [(1L, "Yes"), (0L, "No")]);
                combo.Width = 140;
                DockPanel.SetDock(combo, Dock.Right);
                row.Children.Add(combo);
            }
            else
            {
                var min = new TextBox { Watermark = Loc.S("Min"), Width = 110, HorizontalContentAlignment = HorizontalAlignment.Right };
                var max = new TextBox { Watermark = Loc.S("Max"), Width = 110, HorizontalContentAlignment = HorizontalAlignment.Right };
                rangeInputs.Add((field, min, max));
                foreach (var box in new[] { min, max }) box.TextChanged += (_, _) => RaiseChanged();
                DockPanel.SetDock(max, Dock.Right); row.Children.Add(max);
                DockPanel.SetDock(min, Dock.Right); row.Children.Add(min);
            }

            row.Children.Add(new TextBlock
            {
                Text = Loc.S(label),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            rows.Children.Add(row);
        }

        if (rows.Children.Count > 0)
            host.Children.Add(UiKit.Card(group.Title, rows));
    }

    void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public NationSearchCriteria? Criteria()
    {
        var ranges = new Dictionary<NationField, (long? Min, long? Max)>();

        foreach (var (field, combo) in choiceInputs)
        {
            if (combo.SelectedItem is CodeChoice { Code: { } code })
                ranges[field] = (code, code);
        }

        foreach (var (field, minBox, maxBox) in rangeInputs)
        {
            if (!TryParse(minBox.Text, out long? min) || !TryParse(maxBox.Text, out long? max)) return null;
            if (min is null && max is null) continue;
            if (min is { } lo && max is { } hi && lo > hi) return null;
            ranges[field] = (min, max);
        }

        return new NationSearchCriteria
        {
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
            ContinentAddress = (continentCombo.SelectedItem as PointerChoice)?.Address,
            RegionAddress = (regionCombo.SelectedItem as PointerChoice)?.Address,
            Ranges = ranges,
        };
    }

    public IReadOnlyList<NationRecord> Matched()
    {
        var criteria = Criteria();
        if (criteria is null) return [];

        return source.Where(nation =>
        {
            fieldsByNation.TryGetValue(nation.Address, out var values);
            continentsByNation.TryGetValue(nation.Address, out ulong continent);
            directRegions.TryGetValue(nation.Address, out var directRegion);
            agreementRegions.TryGetValue(nation.Address, out var treaties);

            return NationSearch.Matches(
                nation,
                criteria,
                readField: values is null ? null : field => values.TryGetValue(field, out long v) ? v : null,
                readContinent: () => continent,
                readRegion: () => directRegion?.Address,
                hasAgreementRegion: want => treaties is not null && treaties.Contains(want));
        }).ToArray();
    }

    public int FilterCount()
    {
        int count = 0;
        if (!string.IsNullOrWhiteSpace(nameBox.Text)) count++;
        if ((continentCombo.SelectedItem as PointerChoice)?.Address is not null) count++;
        if ((regionCombo.SelectedItem as PointerChoice)?.Address is not null) count++;
        foreach (var (_, combo) in choiceInputs)
            if ((combo.SelectedItem as CodeChoice)?.Code is not null) count++;
        foreach (var (_, min, max) in rangeInputs)
            if (!string.IsNullOrWhiteSpace(min.Text) || !string.IsNullOrWhiteSpace(max.Text)) count++;
        return count;
    }

    public string SummaryStatus()
    {
        var criteria = Criteria();
        if (criteria is null) return Loc.S("Invalid range");
        int count = FilterCount();
        if (count == 0)
            return source.Count > 0 ? Loc.F("All {0:N0} nations", source.Count) : Loc.S("No filters set");
        return Loc.F("{0:N0} / {1:N0} nations match", Matched().Count, source.Count);
    }

    public void Clear()
    {
        nameBox.Text = "";
        continentCombo.SelectedIndex = 0;
        regionCombo.SelectedIndex = 0;
        foreach (var (_, combo) in choiceInputs) combo.SelectedIndex = 0;
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
