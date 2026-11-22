using Fm27Editor.Core.Memory;

namespace Fm27Editor.UnityMetadata;

/// <summary>One cancellable preparation per game session, shared by warm-up and button clicks.</summary>
public sealed class CurrentScreenSession(Func<CurrentScreenReader> createReader, Func<IProcessMemory> openMemory) : IDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim reading = new(1, 1);
    CurrentScreenReader reader;
    Task initialization;

    public Task PrepareAsync()
    {
        lock (gate)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            // Keep successfully decoded metadata when a process read fails. A later click
            // retries discovery without reparsing the game files.
            if (initialization is null || initialization.IsFaulted || initialization.IsCanceled)
                initialization = Task.Run(() =>
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    reader ??= createReader();
                    lifetime.Token.ThrowIfCancellationRequested();
                    var memory = openMemory();
                    try { reader.Prepare(memory, lifetime.Token); }
                    finally { (memory as IDisposable)?.Dispose(); }
                }, lifetime.Token);
            return initialization;
        }
    }

    public async Task<CurrentScreenTarget> ReadAsync()
    {
        await PrepareAsync().ConfigureAwait(false);
        await reading.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var memory = openMemory();
                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        var target = reader.Read(memory, lifetime.Token);
                        lifetime.Token.ThrowIfCancellationRequested();
                        if (target is not null || !reader.IsPrepared || attempt == 2) return target;
                        // Navigation can span a few frames. Retry only a known module, not
                        // an ambiguous heap scan, and always read the current selection anew.
                        await Task.Delay(40, lifetime.Token).ConfigureAwait(false);
                    }
                }
                finally { (memory as IDisposable)?.Dispose(); }
            }, lifetime.Token).ConfigureAwait(false);
        }
        finally { reading.Release(); }
    }

    public void Dispose() => lifetime.Cancel();
}
