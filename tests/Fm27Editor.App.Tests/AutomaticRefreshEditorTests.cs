using Avalonia.Controls;
using Avalonia.Input;
using Fm27Editor.App;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App.Tests;

public sealed class AutomaticRefreshEditorTests
{
    sealed record Item(ulong Address, int Value);
    sealed class Draft { public int? Value; }
    sealed class Screen(Host host) : EntityScreen<Item, Item, Draft>(host, "test", "Test", "items", "Search")
    {
        public Item Live = new(100, 10);
        public TextBox Input = null!;
        public int Builds, Writes;
        public bool StageText = true;
        public Item? DraftOriginal;
        public TaskCompletionSource? ReadStarted, ReadRelease;
        protected override void ConfigureColumns(DataGrid grid) { }
        protected override IReadOnlyList<Item> LoadRows(GameSession session, LoadedGameData? data)
        {
            var snapshot = Live;
            ReadStarted?.TrySetResult();
            ReadRelease?.Task.GetAwaiter().GetResult();
            return [snapshot];
        }
        protected override bool Matches(Item row, string query) => true;
        protected override Item RecordOf(Item row) => row;
        protected override ulong AddressOf(Item row) => row.Address;
        protected override string NameOf(Item row) => "Fixture";
        protected override Draft NewDraft() => new();
        protected override bool HasChanges(Item item, Draft draft) => draft.Value.HasValue;
        protected override void BuildEditor(Item item, Draft draft, Action changed)
        {
            Builds++;
            DraftOriginal = item;
            Input = new TextBox { Text = (draft.Value ?? item.Value).ToString() };
            Input.TextChanged += (_, _) =>
            {
                if (StageText && int.TryParse(Input.Text, out var value) && value != item.Value) { draft.Value = value; changed(); }
            };
            Editor.Children.Add(Input);
        }
        protected override string? Validate(Item item, Draft draft, out Func<int> write) { write = () => ++Writes; return null; }
        protected override Item? Reload(Item item) => Live;
        protected override Item Refresh(Item row, Item fresh) => fresh;
    }

    sealed class Host : IScreenHost
    {
        public EditorBackend Backend { get; } = new();
        public Control? Showing;
        public bool Dirty;
        public Action? Restore;
        public GraphicsCatalog Graphics { get; } = new();
        public void SetStatus(string text) { }
        public void SetUnsaved(bool dirty) => Dirty = dirty;
        public bool HasUnsavedBody(Control body) => Dirty;
        public void AddModule(string title, Control root, object navItem) { }
        public object? SelectedNav => null;
        public event EventHandler? NavChanged { add { } remove { } }
        public bool IsShowing(Control body) => ReferenceEquals(Showing, body);
        public void OpenTab(string kind, ulong address, string title, Control body, Action restore) { Showing = body; Restore = restore; }
        public void SwitchModule(string title) { }
        public IReadOnlyList<NationRecord> NationCatalogue => [];
        public Task OpenNationAsync(ulong address) => Task.CompletedTask;
        public Task OpenClubAsync(ulong address) => Task.CompletedTask;
        public void OpenCity(ulong address) { }
        public void OpenStadium(ulong address) { }
        public void OpenStaff(StaffRecord staff) { }
        public Window Window => throw new NotSupportedException();
        public Control BuildStaffTable(IReadOnlyList<StaffListRow> rows, IStaffContractScope scope, Action refresh) => new Control();
        public Control BuildKits(ulong owner, IReadOnlyList<ClubKitItem> items, Action changed) => new Control();
        public IReadOnlyDictionary<ulong, KitDraft>? KitDraftOf(ulong owner) => null;
        public void ClearKitDraft(ulong owner) { }
        public Control BuildSquad(Func<IReadOnlyList<PlayerRecord>> getSquad) => new Control();
        public IReadOnlyList<PlayerRecord> Players() => [];
        public IReadOnlyList<ClubEntry> Clubs() => [];
    }

    static async Task<(Host Host, Screen Screen)> Open()
    {
        Host host = null!; Screen screen = null!; Task load = null!;
        AvaloniaTestThread.Run(() =>
        {
            host = new Host(); screen = new Screen(host); screen.Register();
            load = screen.PreloadAsync(new LoadedGameData(null!));
        });
        await load.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() => screen.Pick(screen.Live));
        return (host, screen);
    }

    [Theory]
    [InlineData("17", true)]
    [InlineData("-", false)]
    [InlineData("", false)]
    public async Task Refresh_updates_rows_but_keeps_staged_or_uncommitted_text_and_original_snapshot(string text, bool stage)
    {
        var (host, screen) = await Open();
        var data = new LoadedGameData(null!);
        TextBox input = null!; Item original = null!; Task refresh = null!;
        AvaloniaTestThread.Run(() =>
        {
            input = screen.Input; original = screen.DraftOriginal!;
            screen.StageText = stage;
            input.RaiseEvent(new GotFocusEventArgs { RoutedEvent = InputElement.GotFocusEvent });
            input.Text = text;
            screen.Live = new(100, 13);
            refresh = screen.RefreshDataAsync(data, () => true);
        });
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() =>
        {
            screen.RefreshCleanEditor(data);
            host.Restore!(); // leaving and reopening the same dirty page also keeps local controls
            Assert.Same(input, screen.Input);
            Assert.Equal(text, input.Text);
            Assert.Same(original, screen.DraftOriginal);
            Assert.Equal(13, Assert.Single(screen.Visible).Value);
            Assert.Equal(1, screen.Builds);
            Assert.Equal(0, screen.Writes);
        });
    }

    [Fact]
    public async Task Reverting_uncommitted_text_allows_the_waiting_refresh()
    {
        var (_, screen) = await Open();
        var data = new LoadedGameData(null!);
        AvaloniaTestThread.Run(() =>
        {
            screen.StageText = false;
            screen.Input.RaiseEvent(new GotFocusEventArgs { RoutedEvent = InputElement.GotFocusEvent });
            screen.Input.Text = "-";
            screen.Live = new(100, 13);
            screen.RefreshCleanEditor(data);
            Assert.Equal("-", screen.Input.Text);
            screen.Input.Text = "10";
            screen.RefreshCleanEditor(data);
            Assert.Equal("13", screen.Input.Text);
            Assert.Equal(0, screen.Writes);
        });
    }

    [Fact]
    public void Delayed_template_text_initialization_does_not_block_refresh()
    {
        AvaloniaTestThread.Run(() =>
        {
            var root = new StackPanel();
            var input = new TextBox();
            using (EditorRefreshGuard.Rebuilding(root)) root.Children.Add(input);
            input.Text = "10"; // NumericUpDown initializes its internal textbox during a later layout pass.
            Assert.False(EditorRefreshGuard.Protects(root));
            input.RaiseEvent(new GotFocusEventArgs { RoutedEvent = InputElement.GotFocusEvent });
            input.Text = "-";
            Assert.True(EditorRefreshGuard.Protects(root));
        });
    }

    [Fact]
    public async Task Clean_editor_receives_new_values_without_any_write()
    {
        var (_, screen) = await Open();
        var data = new LoadedGameData(null!);
        Task refresh = null!;
        AvaloniaTestThread.Run(() => { screen.Live = new(100, 13); refresh = screen.RefreshDataAsync(data, () => true); });
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() =>
        {
            screen.RefreshCleanEditor(data);
            screen.RefreshCleanEditor(data);
            Assert.Equal("13", screen.Input.Text);
            Assert.Equal(2, screen.Builds);
            Assert.Equal(0, screen.Writes);
        });
    }

    [Fact]
    public async Task Save_change_during_read_prevents_row_publication()
    {
        var (_, screen) = await Open();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        bool current = true;
        Task refresh = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen.ReadStarted = started; screen.ReadRelease = release; screen.Live = new(100, 99);
            refresh = screen.RefreshDataAsync(new LoadedGameData(null!), () => current);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() => current = false);
        release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() =>
        {
            Assert.Equal(10, Assert.Single(screen.Rows).Value);
            Assert.Equal("10", screen.Input.Text);
            Assert.Equal(0, screen.Writes);
        });
    }
}
