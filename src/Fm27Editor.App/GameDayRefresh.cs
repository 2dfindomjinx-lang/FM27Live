namespace Fm27Editor.App;

/// <summary>Two observations debounce a moving date; one worker at a time, with retry after failed reads.</summary>
internal sealed class GameDayRefresh
{
    object? session;
    DateOnly? accepted, observed;
    bool running;
    int epoch;
    DateTimeOffset retryAfter;

    public void Reset() { session = null; accepted = observed = null; epoch++; retryAfter = default; }

    public async Task TickAsync(object current, DateOnly? loadedDate, DateOnly today,
        Func<Task<bool>> refresh, DateTimeOffset now)
    {
        if (!ReferenceEquals(session, current))
        {
            Reset(); session = current; accepted = loadedDate;
        }
        if (today == accepted) { observed = today; return; }
        if (observed != today) { observed = today; return; }
        if (running || now < retryAfter) return;
        int started = epoch;
        running = true;
        try
        {
            if (await refresh() && started == epoch) accepted = today;
        }
        finally
        {
            running = false;
            if (started == epoch) retryAfter = now.AddSeconds(15);
        }
    }
}
