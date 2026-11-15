using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fm27Editor.App;

/// <summary>
/// The one look of every table that has a tick column and a Tools card beside it (squads, club staff, national staff):
/// the same grid styling, the same header text, the same columns, the same tick column and the same header sorting.
/// </summary>
internal static class ToolGrid
{
    static IBrush Brush(string hex) => UiKit.Brush(hex);

    public static DataGrid Create()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserResizeColumns = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Brush("#1D2130"),
            RowHeight = 29,
            Background = Brush("#11131C"),
            BorderBrush = Brush("#252A3D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        grid.Styles.Add(new Style(x => x.OfType<DataGridColumnHeader>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 0)), new Setter(Layoutable.MinHeightProperty, 26.0) }
        });
        grid.Styles.Add(new Style(x => x.OfType<DataGridColumnHeader>().Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Layoutable.MarginProperty, new Thickness(0)) }
        });
        grid.Styles.Add(new Style(x => x.OfType<DataGridCell>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(0)) }
        });
        EnableRangeSelect(grid);
        return grid;
    }

    public static Control Header(string text, string tooltip, HorizontalAlignment align = HorizontalAlignment.Center)
    {
        var tb = new TextBlock
        {
            Tag = text,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = align,
            TextTrimming = TextTrimming.None,
        };
        Loc.Bind(tb, () =>
        {
            tb.Text = Loc.S(text);
            ToolTip.SetTip(tb, Loc.S(tooltip));
        });
        return tb;
    }

    /// <summary>A right-aligned number column.</summary>
    public static DataGridTemplateColumn Number<T>(string title, Func<T, string> text, double width, string tooltip) => new()
    {
        Header = Header(title, tooltip, HorizontalAlignment.Right),
        Width = new DataGridLength(width),
        CellTemplate = new FuncDataTemplate<T>((row, _) => new TextBlock
        {
            Text = text(row),
            Foreground = Brush("#F1F5F9"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 5, 0),
        }),
    };

    /// <summary>A left-aligned text column; <paramref name="width"/> &lt;= 0 makes it a star column.</summary>
    public static DataGridTemplateColumn Text<T>(string title, Func<T, string> text, double width, string tooltip, string foreground = "#CBD5E1", bool bold = false, double minWidth = 0) => new()
    {
        Header = Header(title, tooltip, HorizontalAlignment.Left),
        Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
        MinWidth = minWidth,
        CellTemplate = new FuncDataTemplate<T>((row, _) =>
        {
            var tb = new TextBlock
            {
                Text = text(row),
                FontWeight = bold ? FontWeight.Medium : FontWeight.Normal,
                Foreground = Brush(foreground),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
            };
            ToolTip.SetTip(tb, text(row));
            return tb;
        }),
    };

    /// <summary>
    /// The first column: a tick per row and a tick in the header for all rows. Every row starts ticked. A click anywhere on a row
    /// leaves only that row ticked; Ctrl+click ticks or unticks that row alone. <paramref name="excluded"/> holds the keys of the
    /// unticked rows; <paramref name="changed"/> runs after any change.
    /// </summary>
    public static void AddTick<T>(DataGrid grid, HashSet<ulong> excluded, Func<T, ulong> key, Func<IEnumerable<T>> rows, Action changed) where T : class
    {
        var syncs = new List<Action>();
        void Changed() { foreach (var sync in syncs.ToArray()) sync(); changed(); }

        var all = new CheckBox { IsChecked = true, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(all, Loc.S("Include every row"));
        all.IsCheckedChanged += (_, _) =>
        {
            if (all.IsChecked == true) excluded.Clear();
            else foreach (var r in rows()) excluded.Add(key(r));
            Changed();
        };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = all,
            Width = new DataGridLength(34),
            CanUserSort = false,
            CellTemplate = new FuncDataTemplate<T>((row, _) =>
            {
                var box = new CheckBox { MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                bool syncing = false;
                void Sync() { syncing = true; box.IsChecked = !excluded.Contains(key(row)); syncing = false; }
                Sync();
                box.IsCheckedChanged += (_, _) =>
                {
                    if (syncing) return;
                    if (box.IsChecked == true) excluded.Remove(key(row)); else excluded.Add(key(row));
                    Changed();
                };
                syncs.Add(Sync);
                box.DetachedFromVisualTree += (_, _) => syncs.Remove(Sync);
                return box;
            }),
        });

        grid.AddHandler(InputElement.PointerPressedEvent, (object? _, Avalonia.Input.PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed || e.Source is not Avalonia.Visual source) return;
            if (source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
            if (source.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is not T row) return;
            ulong id = key(row);
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                if (!excluded.Remove(id)) excluded.Add(id);
            }
            else
            {
                excluded.Clear();
                foreach (var r in rows()) if (key(r) != id) excluded.Add(key(r));
            }
            Changed();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Range selection as in FMRTE's tables: Shift+click selects from the anchor row to the clicked row, and pressing a row then
    /// dragging selects every row the pointer passes, from that row to the row under the pointer (Ctrl+drag adds the range to
    /// the selection). Plain clicks and Ctrl+click are left to the grid.
    /// </summary>
    public static void EnableRangeSelect(DataGrid grid)
    {
        if (grid.SelectionMode != DataGridSelectionMode.Extended) return;
        object? anchor = null;
        bool dragging = false, additive = false;
        object[] before = [];
        object? RowItem(Avalonia.Input.PointerEventArgs e) =>
            e.Source is Avalonia.Visual v && v.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is null
                ? v.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext : null;
        void SelectRange(object from, object to)
        {
            if (grid.ItemsSource is not System.Collections.IEnumerable source) return;
            var items = source.Cast<object>().ToList();
            int a = items.IndexOf(from), b = items.IndexOf(to);
            if (a < 0 || b < 0) return;
            grid.SelectedItems.Clear();
            if (additive) foreach (var item in before) grid.SelectedItems.Add(item);
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                if (!grid.SelectedItems.Contains(items[i])) grid.SelectedItems.Add(items[i]);
        }
        grid.AddHandler(InputElement.PointerPressedEvent, (object? _, Avalonia.Input.PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed || RowItem(e) is not { } item) return;
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) && anchor is not null)
            {
                additive = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
                before = additive ? grid.SelectedItems.Cast<object>().ToArray() : [];
                SelectRange(anchor, item);
                e.Handled = true;
                return;
            }
            anchor = item;
            additive = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
            before = additive ? grid.SelectedItems.Cast<object>().ToArray() : [];
            dragging = e.ClickCount == 1;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(InputElement.PointerMovedEvent, (object? _, Avalonia.Input.PointerEventArgs e) =>
        {
            if (!dragging || anchor is null || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) { dragging = false; return; }
            if (RowItem(e) is not { } item || ReferenceEquals(item, anchor) && grid.SelectedItems.Count <= 1) return;
            SelectRange(anchor, item);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(InputElement.PointerReleasedEvent, (object? _, Avalonia.Input.PointerReleasedEventArgs _) => dragging = false,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Header sorting uses the grid's own: the arrow at the right of the header cell, as in the module lists. Each named column
    /// sorts by its key; rows with no key come first when ascending.
    /// </summary>
    public static void EnableSort<T>(DataGrid grid, IReadOnlyDictionary<string, Func<T, IComparable?>> keys) where T : class
    {
        grid.CanUserSortColumns = true;
        foreach (var column in grid.Columns)
        {
            if (column.Header is TextBlock tb && ((tb.Tag as string ?? tb.Text) is { } name) && keys.TryGetValue(name, out var key))
            {
                column.CustomSortComparer = new KeyComparer<T>(key);
                column.CanUserSort = true;
            }
        }
    }

    /// <summary>Replaces the rows and keeps the sort the user chose.</summary>
    public static void SetItems(DataGrid grid, System.Collections.IEnumerable items)
    {
        var saved = grid.CollectionView?.SortDescriptions.ToList();
        grid.ItemsSource = null;
        grid.ItemsSource = items;
        if (saved is { Count: > 0 } && grid.CollectionView is { } view)
            foreach (var description in saved) view.SortDescriptions.Add(description);
    }

    sealed class KeyComparer<T>(Func<T, IComparable?> key) : System.Collections.IComparer where T : class
    {
        public int Compare(object? x, object? y)
        {
            var a = x is T tx ? key(tx) : null;
            var b = y is T ty ? key(ty) : null;
            if (a is null) return b is null ? 0 : -1;
            return b is null ? 1 : a.CompareTo(b);
        }
    }
}