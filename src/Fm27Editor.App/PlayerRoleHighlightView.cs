using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    readonly Dictionary<ulong, AttributeRoleSelection> attributeRoles = [];
    static readonly Lazy<PhaseRoleRatings?> HighlightCatalog = new(LoadBundledRoleCatalog);
    AttributeRoleSelection AttributeRoleOf(PlayerRecord player)
    {
        if (!attributeRoles.TryGetValue(player.Address, out var state))
            attributeRoles[player.Address] = state = new() { Position = Enum.GetValues<PlayerPosition>().MaxBy(player.Position) };
        return state;
    }
    sealed record HighlightChoice(PhaseRole? Role);

    Control BuildAttributeRoleBar(PlayerRecord player)
    {
        var state = AttributeRoleOf(player);
        var catalog = HighlightCatalog.Value;
        var position = new ComboBox
        {
            ItemsSource = Enum.GetValues<PlayerPosition>(), SelectedItem = state.Position, MinWidth = 85,
            ItemTemplate = new FuncDataTemplate<PlayerPosition>((p, _) => new TextBlock { Text = PositionLabels.Short(p) }),
        };
        var phase = new ComboBox
        {
            ItemsSource = new[] { new ComboBoxItem { Content = Loc.S("In Possession") }, new ComboBoxItem { Content = Loc.S("Out of Possession") } },
            SelectedIndex = (int)state.Phase, MinWidth = 135,
        };
        var roles = new ComboBox
        {
            MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<HighlightChoice>((choice, _) => new TextBlock
            {
                Text = choice?.Role is { } role ? PhaseRoleLabels.Display(role.Id) : Loc.S("No role highlight"),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            }),
        };
        void Refill()
        {
            var roleId = state.Role?.Id;
            state.Position = position.SelectedItem is PlayerPosition p ? p : state.Position;
            state.Phase = (PossessionPhase)Math.Max(0, phase.SelectedIndex);
            var choices = new[] { new HighlightChoice(null) }.Concat((catalog?.At(state.Position, state.Phase) ?? []).Select(r => new HighlightChoice(r))).ToArray();
            roles.ItemsSource = choices;
            roles.SelectedItem = choices.FirstOrDefault(c => c.Role?.Id == roleId) ?? choices[0];
        }
        roles.SelectionChanged += (_, _) => state.Select((roles.SelectedItem as HighlightChoice)?.Role);
        position.SelectionChanged += (_, _) => Refill();
        phase.SelectionChanged += (_, _) => Refill();
        Refill();
        Loc.BindTip(position, "Position"); Loc.BindTip(phase, "Possession phase"); Loc.BindTip(roles, "Highlight attributes for a role");
        var selectors = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
        selectors.Children.Add(position); Grid.SetColumn(phase, 1); selectors.Children.Add(phase); Grid.SetColumn(roles, 2); selectors.Children.Add(roles);
        var legend = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, color) in new[] { ("Key for role", "#4ADE80"), ("Preferable", "#60A5FA"), ("Lower is better", "#FBBF24") })
            legend.Children.Add(new TextBlock { Text = Loc.S(label), Foreground = UiKit.Brush(color), FontSize = 13, FontWeight = AppTypography.BodyWeight, Margin = new Thickness(0, 0, 14, 0) });
        return new StackPanel { Spacing = 4, Children = { selectors, legend } };
    }
}
