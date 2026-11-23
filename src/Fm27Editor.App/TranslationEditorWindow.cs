using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fm27Editor.App;

internal sealed class TranslationEditorWindow : Window
{
    sealed class Row(string key, string original, string? custom)
    {
        public string Key { get; } = key;
        public string Original { get; } = original;
        public string? Custom { get; set; } = custom;
        public string Display => Custom ?? Original;
    }

    public TranslationEditorWindow()
    {
        Title = Loc.S("Edit Turkish translations");
        Width = 940; Height = 660; MinWidth = 640; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = UiKit.WindowBg;
        var rows = Loc.EditableTurkishTable.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(p => p.Key).Select(p => new Row(p.Key, p.Value, TranslationOverrides.Current.GetValueOrDefault(p.Key))).ToArray();
        var search = new TextBox { Watermark = Loc.S("Search English or Turkish text…") };
        var roleKeys = MainWindow.LoadBundledRoleCatalog()?.Roles.Select(r => ContractLabels.Spaced(r.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var rolesOnly = new CheckBox { Content = Loc.S("FM roles only") };
        var grid = ToolGrid.Create();
        grid.Columns.Add(ToolGrid.Text<Row>("English key", r => PhaseRoleLabels.English(r.Key), 300, "English key"));
        grid.Columns.Add(ToolGrid.Text<Row>("Turkish translation", r => r.Display, 0, "Turkish translation", minWidth: 220));
        var key = new TextBlock { Foreground = UiKit.TextSecondary, TextWrapping = TextWrapping.Wrap };
        var original = new TextBlock { Foreground = UiKit.TextMuted, TextWrapping = TextWrapping.Wrap };
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 140, IsEnabled = false };
        var message = new TextBlock { Foreground = UiKit.TextMuted, TextWrapping = TextWrapping.Wrap };
        Row? selected = null;
        bool assigning = false, dirty = false;
        void Select()
        {
            assigning = true;
            selected = grid.SelectedItem as Row;
            key.Text = selected?.Key ?? "";
            original.Text = selected is null ? "" : Loc.F("Default: {0}", selected.Original);
            input.IsEnabled = selected is not null;
            input.Text = selected?.Display ?? "";
            assigning = false;
        }
        void Filter()
        {
            var keep = selected;
            string q = search.Text?.Trim() ?? "";
            var candidates = rolesOnly.IsChecked == true ? rows.Where(r => roleKeys.Contains(r.Key)) : rows;
            ToolGrid.SetItems(grid, candidates.Where(r => q.Length == 0 || r.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || PhaseRoleLabels.English(r.Key).Contains(q, StringComparison.OrdinalIgnoreCase) || r.Original.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Display.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToArray());
            grid.SelectedItem = keep;
        }
        grid.SelectionChanged += (_, _) => Select();
        search.TextChanged += (_, _) => Filter();
        rolesOnly.IsCheckedChanged += (_, _) => Filter();
        input.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || assigning || selected is null) return;
            selected.Custom = input.Text == selected.Original ? null : input.Text ?? "";
            dirty = true;
        };
        var save = new Button { Content = Loc.S("Save and apply"), Classes = { "accent" } };
        bool Save()
        {
            try
            {
                var values = new Dictionary<string, string>(TranslationOverrides.Current, StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows) { if (row.Custom is null) values.Remove(row.Key); else values[row.Key] = row.Custom; }
                TranslationOverrides.Save(values);
                dirty = false;
                message.Text = Loc.S("Translations applied. Your changes survive application updates.");
                Filter();
                return true;
            }
            catch (Exception ex) { message.Text = UserMessage.For(ex); return false; }
        }
        save.Click += (_, _) => Save();
        var reset = new Button { Content = Loc.S("Restore selected default") };
        reset.Click += (_, _) => { if (selected is null) return; selected.Custom = null; dirty = true; Select(); };
        var folder = new Button { Content = Loc.S("Open translation folder") };
        folder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TranslationOverrides.FilePath)!);
                if (!File.Exists(TranslationOverrides.FilePath)) TranslationOverrides.Save(TranslationOverrides.Current);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(TranslationOverrides.FilePath)!) { UseShellExecute = true });
            }
            catch (Exception ex) { message.Text = UserMessage.For(ex); }
        };
        var reload = new Button { Content = Loc.S("Reload translation file") };
        reload.Click += (_, _) =>
        {
            if (dirty) { message.Text = Loc.S("Save or close your pending translation edits before reloading the file."); return; }
            TranslationOverrides.Load();
            if (TranslationOverrides.LoadError is { } error) { message.Text = error; return; }
            foreach (var row in rows) row.Custom = TranslationOverrides.Current.GetValueOrDefault(row.Key);
            Filter(); Select();
            message.Text = Loc.S("Translations applied. Your changes survive application updates.");
        };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Children = { save, reset, folder, reload } };
        foreach (var button in buttons.Children) button.Margin = new Thickness(0, 0, 6, 4);
        var bottom = new StackPanel { Spacing = 7, Margin = new Thickness(0, 10, 0, 0), Children = { key, original, input, buttons, message } };
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(search, Dock.Top); search.Margin = new Thickness(0, 0, 0, 8); root.Children.Add(search);
        DockPanel.SetDock(rolesOnly, Dock.Top); root.Children.Add(rolesOnly);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(grid);
        Content = root;
        bool canClose = false, asking = false;
        Closing += async (_, e) =>
        {
            if (!dirty || canClose) return;
            e.Cancel = true;
            if (asking) return;
            asking = true;
            try
            {
                int result = await UnsavedDialog.Ask(this);
                if (result == 1 || result == 0 && Save()) { canClose = true; Close(); }
            }
            finally { asking = false; }
        };
        Filter();
    }
}
