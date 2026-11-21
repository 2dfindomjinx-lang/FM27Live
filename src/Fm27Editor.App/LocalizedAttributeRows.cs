using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App;

/// <summary>Moves existing rows in the grid, never recreating inputs or disturbing pending edits.</summary>
internal sealed class LocalizedAttributeRows : Grid
{
    // FM's Turkish outfield layout, including the separate set-piece block. Identity, not translated text, determines order.
    static readonly Dictionary<string, string[]> Turkish = new()
    {
        ["Technical"] = ["Finishing", "Dribbling", "FirstTouch", "Heading", "Marking", "Crossing", "Passing", "Technique", "Tackling", "LongShots", "Corners", "PenaltyTaking", "FreeKicks", "LongThrows"],
        ["Mental"] = ["Aggression", "Bravery", "WorkRate", "Decisions", "Determination", "Concentration", "Leadership", "Anticipation", "Flair", "Positioning", "Composure", "Teamwork", "OffTheBall", "Vision"],
        ["Physical"] = ["Agility", "Stamina", "Balance", "Strength", "JumpingReach", "Pace", "Acceleration", "NaturalFitness", "LeftFoot", "RightFoot"],
    };
    static readonly List<WeakReference<LocalizedAttributeRows>> Panels = [];
    readonly string group;
    readonly (string Id, Border Row)[] entries;
    readonly TextBlock? setPieces;
    readonly AttributeRoleSelection? role;

    static LocalizedAttributeRows()
    {
        LanguageSetting.Changed += RefreshAll;
        AttributeOrderSetting.Changed += RefreshAll;
    }

    static void RefreshAll() => LiveText.OnUi(() =>
    {
        for (int i = Panels.Count - 1; i >= 0; i--)
            if (Panels[i].TryGetTarget(out var panel)) panel.ApplyOrder();
            else Panels.RemoveAt(i);
    });

    public LocalizedAttributeRows(string group, IReadOnlyList<string> ids, IReadOnlyList<Control> controls, AttributeRoleSelection? role = null)
    {
        if (ids.Count != controls.Count || ids.Distinct().Count() != ids.Count) throw new ArgumentException("Attribute rows must have distinct matching identities.");
        this.group = group;
        this.role = role;
        if (role is not null)
        {
            // The per-player selection outlives a rendered page; do not retain discarded controls through its event.
            var weak = new WeakReference<LocalizedAttributeRows>(this);
            Action? refresh = null;
            refresh = () => { if (weak.TryGetTarget(out var panel)) panel.ApplyOrder(); else role.Changed -= refresh; };
            role.Changed += refresh;
        }
        entries = ids.Select((id, i) => (id, new Border { Padding = new Thickness(4, 1, 4, 1), Child = controls[i] })).ToArray();
        foreach (var (_, row) in entries)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Children.Add(row);
        }
        if (group == "Technical")
        {
            setPieces = new TextBlock { Text = Loc.S("Set Pieces"), FontSize = AppTypography.HeadingSize, FontWeight = FontWeight.Bold, Foreground = UiKit.TextPrimary, Margin = new Thickness(4, 6, 4, 3), VerticalAlignment = VerticalAlignment.Center };
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Children.Add(setPieces);
        }
        Panels.RemoveAll(p => !p.TryGetTarget(out _));
        Panels.Add(new(this));
        ApplyOrder();
    }

    void ApplyOrder()
    {
        bool turkish = AttributeOrderSetting.UseTurkishOrder;
        var order = turkish && Turkish.TryGetValue(group, out var preferred) ? preferred : [];
        var ordered = entries.OrderBy(e => Array.IndexOf(order, e.Id) is var rank && rank >= 0 ? rank : int.MaxValue).ToArray();
        if (setPieces is not null) setPieces.IsVisible = turkish;
        int line = 0, stripe = 0;
        foreach (var (id, row) in ordered)
        {
            if (turkish && setPieces is not null && id == "Corners")
            {
                SetRow(setPieces, line++);
                stripe = 0;
            }
            SetRow(row, line++);
            row.TabIndex = line;
            row.Background = stripe++ % 2 == 1 ? UiKit.SurfaceZebra : Brushes.Transparent;
            var emphasis = role?.Emphasis(id) ?? AttributeEmphasis.None;
            row.BorderThickness = new Thickness(emphasis == AttributeEmphasis.None ? 0 : 3, 0, 0, 0);
            row.BorderBrush = emphasis switch
            {
                AttributeEmphasis.Key => UiKit.Brush("#4ADE80"),
                AttributeEmphasis.Preferable => UiKit.Brush("#60A5FA"),
                AttributeEmphasis.LowerIsBetter => UiKit.Brush("#FBBF24"),
                _ => Brushes.Transparent,
            };
            if (emphasis != AttributeEmphasis.None)
                row.Background = UiKit.Brush(emphasis == AttributeEmphasis.Key ? "#142B22" : emphasis == AttributeEmphasis.Preferable ? "#15253A" : "#302713");
            ToolTip.SetTip(row, emphasis switch { AttributeEmphasis.Key => Loc.S("Key for role"), AttributeEmphasis.Preferable => Loc.S("Preferable"), AttributeEmphasis.LowerIsBetter => Loc.S("Lower is better"), _ => null });
        }
    }
}
