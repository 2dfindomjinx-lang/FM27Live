using System.Runtime.CompilerServices;
using Fm27Editor.App;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App.Tests;

public sealed class SearchLifetimeTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Source, WeakReference Player) Populate(PlayerSearchIndex index)
    {
        var player = new PlayerRecord
        {
            Address = 101, VtableRva = 1, CommonName = "Previous save", CurrentAbility = 100, PotentialAbility = 120,
            HomeReputation = 0, CurrentReputation = 0, WorldReputation = 0, Height = 180,
            RawAttributes = [], AttributeScale = 5, Positions = [], Personality = [],
        };
        object[] source = [player];
        Assert.Same(player, Assert.Single(index.Filter(source, new PlayersModule(), new PlayerFilter(), new PlayerSortSpec())));
        return (new WeakReference(source), new WeakReference(player));
    }

    static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void Clearing_the_index_releases_the_old_save_without_a_replacement_load()
    {
        var index = new PlayerSearchIndex();
        var old = Populate(index);
        Collect();
        Assert.True(old.Source.IsAlive);
        Assert.True(old.Player.IsAlive);

        index.Clear();
        Collect();
        Assert.False(old.Source.IsAlive);
        Assert.False(old.Player.IsAlive);
        Assert.Empty(index.Filter([], new PlayersModule(), new PlayerFilter(), new PlayerSortSpec()));
        GC.KeepAlive(index);
    }

    [Fact]
    public async Task Cancelling_a_running_search_discards_its_result_without_a_new_query()
    {
        var latest = new LatestSearch();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var old = latest.RunAsync(_ =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            // A worker that has already finished its CPU work may not observe cancellation itself.
            return "previous save";
        }, TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        latest.Cancel();
        release.Set();
        Assert.Null(await old.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("new save", await latest.RunAsync(_ => "new save", TimeSpan.Zero));
    }

    [Fact]
    public async Task Cancelling_before_the_search_delay_elapses_never_starts_the_old_read()
    {
        var latest = new LatestSearch();
        int reads = 0;
        var old = latest.RunAsync(_ => { Interlocked.Increment(ref reads); return "previous save"; }, TimeSpan.FromSeconds(30));
        latest.Cancel();
        Assert.Null(await old.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, reads);
    }
}
