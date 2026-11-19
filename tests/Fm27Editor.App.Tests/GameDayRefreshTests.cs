using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public sealed class GameDayRefreshTests
{
    static readonly DateOnly Day = new(2026, 11, 19);
    static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Only_a_stable_new_day_triggers_one_read()
    {
        var scheduler = new GameDayRefresh();
        var session = new object();
        int reads = 0;
        Task<bool> Read() { reads++; return Task.FromResult(true); }
        await scheduler.TickAsync(session, Day, Day, Read, Now);
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now);
        Assert.Equal(0, reads);
        await scheduler.TickAsync(session, Day, Day.AddDays(2), Read, Now);
        Assert.Equal(0, reads);
        await scheduler.TickAsync(session, Day, Day.AddDays(2), Read, Now);
        Assert.Equal(1, reads);
        await scheduler.TickAsync(session, Day, Day.AddDays(2), Read, Now.AddMinutes(1));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task In_flight_read_is_not_duplicated_and_next_day_is_not_lost()
    {
        var scheduler = new GameDayRefresh();
        var session = new object();
        var finish = new TaskCompletionSource<bool>();
        int reads = 0;
        Task<bool> Read() { reads++; return finish.Task; }
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now);
        var pending = scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now);
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now);
        await scheduler.TickAsync(session, Day, Day.AddDays(2), Read, Now);
        await scheduler.TickAsync(session, Day, Day.AddDays(2), Read, Now);
        Assert.Equal(1, reads);
        finish.SetResult(true);
        await pending;
        await scheduler.TickAsync(session, Day, Day.AddDays(2), Read, Now.AddSeconds(16));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Failed_read_retries_without_marking_the_day_complete()
    {
        var scheduler = new GameDayRefresh();
        var session = new object();
        int reads = 0;
        Task<bool> Read() { reads++; return Task.FromResult(reads > 1); }
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now);
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now);
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now.AddSeconds(3));
        Assert.Equal(1, reads);
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Read, Now.AddSeconds(16));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Reset_during_a_read_cannot_mark_the_new_sessions_day_complete()
    {
        var scheduler = new GameDayRefresh();
        var first = new object();
        var second = new object();
        var finish = new TaskCompletionSource<bool>();
        int reads = 0;
        Task<bool> OldRead() => finish.Task;
        Task<bool> NewRead() { reads++; return Task.FromResult(true); }
        await scheduler.TickAsync(first, Day, Day.AddDays(1), OldRead, Now);
        var pending = scheduler.TickAsync(first, Day, Day.AddDays(1), OldRead, Now);
        scheduler.Reset();
        await scheduler.TickAsync(second, Day, Day.AddDays(1), NewRead, Now);
        finish.SetResult(true);
        await pending;
        await scheduler.TickAsync(second, Day, Day.AddDays(1), NewRead, Now);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Exception_releases_the_worker_and_is_retryable()
    {
        var scheduler = new GameDayRefresh();
        var session = new object();
        Task<bool> Fail() => throw new IOException("fixture");
        await scheduler.TickAsync(session, Day, Day.AddDays(1), Fail, Now);
        await Assert.ThrowsAsync<IOException>(() => scheduler.TickAsync(session, Day, Day.AddDays(1), Fail, Now));
        bool retried = false;
        await scheduler.TickAsync(session, Day, Day.AddDays(1), () => { retried = true; return Task.FromResult(true); }, Now.AddMinutes(1));
        Assert.True(retried);
    }
}
