using Avalonia.Controls;
using Fm27Editor.App;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App.Tests;

public sealed class CompleteLoadTests
{
    sealed record Item(ulong Address, string Name);

    sealed class Screen(IScreenHost host, Func<IReadOnlyList<Item>> source, Action? publish = null)
        : EntityScreen<Item, Item, object>(host, "test", "Test", "items", "Search")
    {
        protected override void ConfigureColumns(DataGrid grid) { }
        protected override void RowsLoaded() => publish?.Invoke();
        protected override IReadOnlyList<Item> LoadRows(GameSession session, LoadedGameData? data) => source();
        protected override bool Matches(Item row, string query) => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        protected override Item RecordOf(Item row) => row;
        protected override ulong AddressOf(Item item) => item.Address;
        protected override string NameOf(Item item) => item.Name;
        protected override object NewDraft() => new();
        protected override bool HasChanges(Item item, object draft) => false;
        protected override void BuildEditor(Item item, object draft, Action changed) { }
        protected override string? Validate(Item item, object draft, out Func<int> write) { write = () => 0; return null; }
        protected override Item? Reload(Item item) => item;
        protected override Item Refresh(Item row, Item fresh) => fresh;
    }

    sealed class Host : IScreenHost
    {
        public EditorBackend Backend { get; } = new();
        public GraphicsCatalog Graphics { get; } = new();
        public void SetStatus(string text) { }
        public void SetUnsaved(bool dirty) { }
        public void AddModule(string title, Control root, object navItem) { }
        public object? SelectedNav => null;
        public event EventHandler? NavChanged { add { } remove { } }
        public bool IsShowing(Control body) => false;
        public void OpenTab(string kind, ulong address, string title, Control body, Action restore) { }
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

    sealed class ClubPanels : IClubPanels
    {
        public ListBox SectionRail { get; } = new();
        public IReadOnlyList<ClubEntry>? CachedClubs => null;
        public IReadOnlyList<PlayerRecord> ShownPlayers => [];
        public List<(IReadOnlyList<ClubEntry> Clubs, DateOnly? Date)> Publications { get; } = [];
        public void ClubsLoaded(IReadOnlyList<ClubEntry> clubs, IReadOnlyList<PlayerRecord>? freshPlayers, DateOnly? date) => Publications.Add((clubs, date));
        public void Reconnected() { }
        public void BuildClub(ClubEntry club, ClubDraft draft) { }
        public bool HasPending(ClubEntry club) => false;
        public int SaveClub(ClubEntry club, ClubDraft draft) => 0;
        public void SaveFailed(ClubEntry club, Exception error) { }
        public ulong TeamOf(ClubEntry club) => 0;
        public bool IsValid(ulong address) => true;
    }

    sealed class DelayedClubScreen(IScreenHost host, IClubPanels panels, Func<IReadOnlyList<MainWindow.ClubPickerItem>> source)
        : ClubScreen(host, panels)
    {
        protected override IReadOnlyList<MainWindow.ClubPickerItem> LoadRows(GameSession session, LoadedGameData? data) => source();
    }

    // This screen's source is controlled by the test and never accesses a game session or process memory.
    static LoadedGameData Data() => new(null!);

    [Fact]
    public async Task Inactive_empty_list_is_ready_only_after_its_read_finishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Screen screen = null!;
        Task loading = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return []; });
            screen.Register();
            loading = screen.PreloadAsync(Data());
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        AvaloniaTestThread.Run(() => { Assert.False(screen.IsLoaded); Assert.False(loading.IsCompleted); });
        release.Set();
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() => { Assert.True(screen.IsLoaded); Assert.Empty(screen.Rows); Assert.Empty(screen.Visible); });
    }

    [Fact]
    public async Task Delayed_old_connection_rows_cannot_overwrite_the_reloaded_screen()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int reads = 0;
        Screen screen = null!;
        Task oldLoad = null!, newLoad = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    return [new Item(101, "Old save")];
                }
                return [new Item(202, "New save")];
            });
            screen.Register();
            oldLoad = screen.PreloadAsync(Data());
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        AvaloniaTestThread.Run(() =>
        {
            screen.ResetForConnection();
            Assert.False(screen.IsLoaded);
            newLoad = screen.PreloadAsync(Data());
        });
        await newLoad.WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() =>
        {
            Assert.True(screen.IsLoaded);
            Assert.Equal(new Item(202, "New save"), Assert.Single(screen.Rows));
            Assert.Equal(new Item(202, "New save"), Assert.Single(screen.Visible));
        });
    }

    [Fact]
    public async Task Failed_list_read_is_not_published_as_an_empty_success()
    {
        Screen screen = null!;
        Task loading = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () => throw new IOException("Cannot read fixture"));
            screen.Register();
            loading = screen.PreloadAsync(Data());
        });
        await Assert.ThrowsAsync<IOException>(() => loading);
        AvaloniaTestThread.Run(() => Assert.False(screen.IsLoaded));
    }

    [Fact]
    public async Task Publication_failure_does_not_mark_a_screen_ready()
    {
        Screen screen = null!;
        Task loading = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () => [new(101, "Player")], () => throw new IOException("Cannot publish fixture"));
            screen.Register();
            loading = screen.PreloadAsync(Data());
        });
        await Assert.ThrowsAsync<IOException>(() => loading);
        AvaloniaTestThread.Run(() => Assert.False(screen.IsLoaded));
    }

    [Fact]
    public async Task Club_metadata_from_a_delayed_old_load_never_reaches_the_current_save()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var oldClub = new ClubEntry(101, 1, "Old club", "Old club", 0, []);
        var newClub = new ClubEntry(202, 2, "New club", "New club", 0, []);
        var oldDate = new DateOnly(2026, 1, 1);
        var newDate = new DateOnly(2027, 2, 2);
        ClubPanels panels = null!;
        DelayedClubScreen screen = null!;
        Task oldLoad = null!, newLoad = null!;
        int calls = 0;
        AvaloniaTestThread.Run(() =>
        {
            panels = new ClubPanels();
            screen = new DelayedClubScreen(new Host(), panels, () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    return new ClubScreen.LoadedRows([oldClub], null, oldDate) { new(oldClub) };
                }
                return new ClubScreen.LoadedRows([newClub], null, newDate) { new(newClub) };
            });
            screen.Register();
            oldLoad = screen.PreloadAsync(Data());
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        AvaloniaTestThread.Run(() =>
        {
            screen.ResetForConnection();
            newLoad = screen.PreloadAsync(Data());
        });
        await newLoad.WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() =>
        {
            var published = Assert.Single(panels.Publications);
            Assert.Equal(newDate, published.Date);
            Assert.Same(newClub, Assert.Single(published.Clubs));
            Assert.Same(newClub, Assert.Single(screen.Clubs));
        });
    }

    [Fact]
    public void A_failed_catalogue_keeps_the_full_load_incomplete_after_other_stages_succeed()
    {
        var report = new GameLoadReport();
        Assert.Empty(report.Read<Item>("staff", "Reading staff…", () => throw new IOException("Cannot read staff fixture")));
        Assert.Equal(new Item(101, "Known player"), Assert.Single(report.Read<Item>("players", "Reading players…", () => [new(101, "Known player")])));
        Assert.False(report.Succeeded);
        Assert.Contains("staff", Assert.Single(report.Errors));
    }
}
