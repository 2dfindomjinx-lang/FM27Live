namespace Fm27Editor.Core.Memory;

/// <summary>
/// Read-ahead for one bounded, read-only operation. Nearby fields share a page read instead of each crossing the
/// process boundary. Dispose after materializing the result; never use this memory for live writes or reloads.
/// A scope and any nested scopes are confined to one thread; the shared cache is not synchronized.
/// </summary>
public sealed class BulkReadScope : IDisposable
{
    readonly ReadAheadMemory? owned;

    /// <summary>A read-only view. Nested scopes share the outer scope's cache and do not dispose it.</summary>
    public IProcessMemory Memory { get; }

    public BulkReadScope(IProcessMemory memory) : this(memory, 4096) { }

    internal BulkReadScope(IProcessMemory memory, int pageCount, int failedPageHintCount = 0)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(failedPageHintCount);
        Memory = memory is ReadAheadMemory ? memory : owned = new ReadAheadMemory(memory, pageCount, failedPageHintCount);
    }

    public void Dispose() => owned?.Dispose();
}

/// <summary>At most 16 MiB with the default scope; no cache survives the bulk operation.</summary>
internal sealed class ReadAheadMemory(IProcessMemory source, int pageCount, int failedPageHintCount) : IProcessMemory, IDisposable
{
    internal const int PageSize = 4096;
    readonly Dictionary<ulong, byte[]> pages = new();
    readonly Queue<ulong> order = new();
    readonly HashSet<ulong> failedPages = new();
    readonly Queue<ulong> failedOrder = new();
    byte[]? spare;
    bool disposed;

    internal int CachedBytes => pages.Count * PageSize;

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty) return true;
        // Vector arrays and code scans already read in bulk. Avoid both copying and evicting useful small reads.
        if (buffer.Length > PageSize) return source.TryRead(address, buffer);
        if (address > ulong.MaxValue - (ulong)(buffer.Length - 1)) return false;

        int copied = 0;
        while (copied < buffer.Length)
        {
            ulong at = address + (ulong)copied;
            ulong page = at & ~(ulong)(PageSize - 1);
            if (!pages.TryGetValue(page, out var bytes))
            {
                // Discovery follows many speculative pointers. A failed wide probe only disables read-ahead;
                // the precise requested bytes are always retried, including if memory has become readable.
                if (failedPages.Contains(page)) return source.TryRead(address, buffer);
                if (pages.Count == pageCount)
                {
                    ulong oldest = order.Dequeue();
                    spare = pages[oldest];
                    pages.Remove(oldest);
                }
                bytes = spare ?? new byte[PageSize];
                spare = null;
                if (!source.TryRead(page, bytes))
                {
                    spare = bytes;
                    if (failedPageHintCount > 0 && failedPages.Add(page))
                    {
                        if (failedOrder.Count == failedPageHintCount) failedPages.Remove(failedOrder.Dequeue());
                        failedOrder.Enqueue(page);
                    }
                    // A valid object/string can sit at an allocation boundary. A failed speculative page read
                    // must not turn a readable requested range into missing data, including across two pages.
                    return source.TryRead(address, buffer);
                }
                pages.Add(page, bytes);
                order.Enqueue(page);
            }
            int offset = (int)(at - page);
            int take = Math.Min(PageSize - offset, buffer.Length - copied);
            bytes.AsSpan(offset, take).CopyTo(buffer[copied..]);
            copied += take;
        }
        return true;
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> data) =>
        throw new NotSupportedException("Bulk-read memory cannot write; use the live guarded writer");

    public void Dispose()
    {
        disposed = true;
        pages.Clear();
        order.Clear();
        failedPages.Clear();
        failedOrder.Clear();
        spare = null;
    }
}
