using Avalonia.Controls;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App.Tests;

public sealed class BulkPlayerActionsTests
{
    sealed class FakeEditService : IEditService
    {
        int writes;
        public List<(string Kind, string Entity, string Title)> Groups { get; } = [];
        public IReadOnlyList<EditRecord> History => [];
        public long WriteCount => writes;
        public PlayerRecord Apply(PlayerRecord player, FieldDescriptor<PlayerRecord> field, int value) => player;
        public PlayerRecord Undo(PlayerRecord player, FieldDescriptor<PlayerRecord> field, EditRecord record) => player;
        public void Execute(Action action) => action();
        public void RecordWrite() => writes++;

        public IDisposable Group(string kind, string entity, string title, Func<ulong, int, ChangeLabel?>? labeler = null)
        {
            Groups.Add((kind, entity, title));
            return new GroupScope();
        }

        sealed class GroupScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    static PlayerRecord Player(ulong address, string name) => new()
    {
        Address = address,
        VtableRva = 0x4CD6318,
        CommonName = name,
        CurrentAbility = 120,
        PotentialAbility = 140,
        HomeReputation = 4000,
        CurrentReputation = 4000,
        WorldReputation = 4000,
        Height = 180,
        RawAttributes = new byte[50],
        AttributeScale = 5,
        Positions = new byte[20],
        Personality = new byte[10],
    };

    [Fact]
    public void RunBulk_deduplicates_targets_groups_edits_and_collects_failures()
    {
        var edits = new FakeEditService();
        var p1 = Player(0x1000, "Player One");
        var p1Dup = Player(0x1000, "Player One Dup");
        var p2 = Player(0x2000, "Player Two");
        var p3 = Player(0x3000, "Player Three");
        int labelCalls = 0;

        var report = MainWindow.RunBulk(edits, [p1, p2, p1Dup, p3], "Test Action", p =>
        {
            if (p.Address == 0x2000) throw new InvalidOperationException("boom");
            return p.Address == 0x1000;
        }, _ => labelCalls++);

        Assert.Equal(1, report.Changed);
        Assert.Equal(1, report.Unchanged);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("Player Two", failure.Player);
        Assert.Equal("boom", failure.Error);
        Assert.Equal(3, labelCalls);
        Assert.Equal(4, edits.Groups.Count);
        Assert.Equal(("Players", "Bulk Edit", "Test Action"), edits.Groups[0]);
        Assert.Equal(["Player One", "Player Two", "Player Three"], edits.Groups.Skip(1).Select(g => g.Entity));
    }

    [Fact]
    public void RunBulkVoid_tracks_write_count_changes()
    {
        var edits = new FakeEditService();
        var p1 = Player(0x1000, "Changed");
        var p2 = Player(0x2000, "Unchanged");

        var report = MainWindow.RunBulkVoid(edits, [p1, p2], "Heal", p =>
        {
            if (p.Address == 0x1000) edits.RecordWrite();
        });

        Assert.Equal(1, report.Changed);
        Assert.Equal(1, report.Unchanged);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void FormatStatus_covers_all_result_shapes()
    {
        Assert.Equal("Nothing changed (3 checked)", new BulkActionReport(0, 3, []).FormatStatus("Freeze"));
        Assert.Equal("Freeze: applied to 2 players", new BulkActionReport(2, 0, []).FormatStatus("Freeze"));
        Assert.Equal("Freeze: applied to 2, 1 unchanged", new BulkActionReport(2, 1, []).FormatStatus("Freeze"));
        var failures = new BulkActionReport(1, 0, [
            ("A", "e1"), ("B", "e2"), ("C", "e3"), ("D", "e4"),
        ]).FormatStatus("Heal");
        Assert.Equal("Heal: 1 ok, 4 failed (A: e1; B: e2; C: e3, +1)", failures);
    }

    [Fact]
    public void SelectedPlayersFrom_unwraps_multiselect_and_falls_back_to_single_selection()
    {
        AvaloniaTestThread.Run(() =>
        {
            var p1 = Player(0x1000, "First");
            var p2 = Player(0x2000, "Second");
            var r1 = new RetiringPlayer(p1, new DateOnly(2027, 6, 30), RetirementReason.PlannedRetirement);
            var r1Dup = new RetiringPlayer(p1, new DateOnly(2027, 6, 30), RetirementReason.PlannedRetirement);
            var r2 = new RetiringPlayer(p2, null, RetirementReason.WillRetireAtTop);

            var grid = new DataGrid { ItemsSource = new[] { r1, r1Dup, r2 }, SelectionMode = DataGridSelectionMode.Extended };
            grid.SelectedItems.Add(r1);
            grid.SelectedItems.Add(r1Dup);
            grid.SelectedItems.Add(r2);

            var multi = MainWindow.SelectedPlayersFrom(grid, item => (item as RetiringPlayer)?.Player);
            Assert.Equal([0x1000UL, 0x2000UL], multi.Select(p => p.Address));

            grid.SelectedItems.Clear();
            grid.SelectedItem = r2;
            var fallback = MainWindow.SelectedPlayersFrom(grid, item => (item as RetiringPlayer)?.Player);
            Assert.Equal([0x2000UL], fallback.Select(p => p.Address));
        });
    }
}
