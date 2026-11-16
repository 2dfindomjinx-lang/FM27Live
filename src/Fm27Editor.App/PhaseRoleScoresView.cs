using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    sealed record PhaseRoleRow(string Name, double? Score, string Key, string Preferable, string Avoid);
    static string? phaseRoleCatalogPath;

    internal static string? FindPhaseRoleCatalog()
    {
        if (phaseRoleCatalogPath is { } chosen && File.Exists(chosen)) return chosen;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Ratings", "rolesAndDuties.json"),
            "/Applications/FMRTE 27.app/Contents/MacOS/Assets/Ratings/rolesAndDuties.json",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FMRTE 27", "Assets", "Ratings", "rolesAndDuties.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FMRTE 27", "Assets", "Ratings", "rolesAndDuties.json"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    Control RoleScoresPanel(PlayerRecord player)
    {
        var root = new StackPanel { Spacing = 8 };
        var source = new TextBlock { Foreground = UiKit.TextMuted, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var content = new ContentControl();
        var import = new Button { Content = Loc.S("Choose role catalog…") };
        var position = new ComboBox
        {
            ItemsSource = Enum.GetValues<PlayerPosition>().Select(p => new PhasePosition(p)).ToArray(),
            SelectedIndex = 0, MinWidth = 100,
        };
        position.SelectedItem = position.Items.OfType<PhasePosition>().OrderByDescending(p => player.Position(p.Position)).First();
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Children = { position, import } };
        root.Children.Add(bar); root.Children.Add(source); root.Children.Add(content);
        PhaseRoleRatings? catalog = null;
        void Render()
        {
            if (catalog is null || position.SelectedItem is not PhasePosition selectedPosition) return;
            content.Content = BuildPhaseRoleTables(player, catalog, selectedPosition.Position);
        }
        void Load(string? path)
        {
            try
            {
                catalog = path is null ? null : PhaseRoleRatings.Load(path);
                source.Text = path is null ? Loc.S("Choose an IP/OOP role catalog to show ratings.") : Loc.F("Role catalog: {0}", path);
                ToolTip.SetTip(source, source.Text);
                Render();
            }
            catch (Exception ex) { catalog = null; content.Content = null; source.Text = UserMessage.For(ex); }
        }
        position.SelectionChanged += (_, _) => Render();
        import.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.S("Choose role catalog…"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            Load(path);
            if (catalog is not null) phaseRoleCatalogPath = path;
        };
        Load(FindPhaseRoleCatalog());
        return root;
    }

    sealed record PhasePosition(PlayerPosition Position)
    { public override string ToString() => PositionLabels.Short(Position); }

    internal static Control BuildPhaseRoleTables(PlayerRecord player, PhaseRoleRatings catalog, PlayerPosition position)
    {
        Control Table(PossessionPhase phase)
        {
            var grid = ToolGrid.Create();
            grid.Height = 360;
            grid.Columns.Add(ToolGrid.Text<PhaseRoleRow>("Role", r => r.Name, 260, "Role", minWidth: 160));
            grid.Columns.Add(ToolGrid.Text<PhaseRoleRow>("Score", r => r.Score is { } v ? v.ToString("0.00", Loc.FormatCulture) + "%" : Loc.S("Unknown"), 90, "Score"));
            grid.Columns.Add(ToolGrid.Text<PhaseRoleRow>("Key for role", r => r.Key, 0, "Key for role", minWidth: 160));
            grid.Columns.Add(ToolGrid.Text<PhaseRoleRow>("Preferable", r => r.Preferable, 0, "Preferable", minWidth: 160));
            grid.Columns.Add(ToolGrid.Text<PhaseRoleRow>("Lower is better", r => r.Avoid, 0, "Lower is better", minWidth: 120));
            var rows = catalog.At(position, phase).Select(role => new PhaseRoleRow(
                Loc.S(ContractLabels.Spaced(role.Id)), catalog.Score(player, position, role),
                string.Join(", ", role.Attributes.Where(a => a.Key && !a.LowerIsBetter).Select(a => ScoutingLabels.Attribute(a.Attribute))),
                string.Join(", ", role.Attributes.Where(a => !a.Key && !a.LowerIsBetter).Select(a => ScoutingLabels.Attribute(a.Attribute))),
                string.Join(", ", role.Attributes.Where(a => a.LowerIsBetter).Select(a => ScoutingLabels.Attribute(a.Attribute)))))
                .OrderByDescending(r => r.Score).ToArray();
            ToolGrid.SetItems(grid, rows);
            ToolGrid.EnableSort<PhaseRoleRow>(grid, new Dictionary<string, Func<PhaseRoleRow, IComparable?>> { ["Role"] = r => r.Name, ["Score"] = r => r.Score });
            return grid;
        }
        return UiKit.OptionTabs([("In Possession", Table(PossessionPhase.InPossession)), ("Out of Possession", Table(PossessionPhase.OutOfPossession))]);
    }
}
