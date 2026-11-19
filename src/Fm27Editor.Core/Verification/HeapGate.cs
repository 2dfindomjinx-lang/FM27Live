using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Verification;

public sealed record HeapAllocationEntry(
    ulong Address,
    int RequestedBytes,
    AllocationOrigin Origin,
    HeapBlockCheck Verification);

/// <summary>
/// Records every allocation during a guarded write session and verifies that zero raw VM allocations
/// occurred and every allocated block belongs to the game's own C heap (<c>calloc</c>).
/// </summary>
public sealed class HeapGate : IDisposable
{
    readonly IProcessMemory _memory;
    readonly IMemoryAllocationObserver? _originObserver;
    readonly IMemoryWriteObserver? _writeObserver;
    readonly Action<ulong, int, AllocationOrigin>? _prevOrigin;
    readonly Action<ulong, int>? _prevAlloc;
    readonly List<(ulong Address, int Bytes, AllocationOrigin Origin)> _rawRecords = [];
    readonly object _lock = new();
    bool _disposed;

    public HeapGate(IProcessMemory memory)
    {
        _memory = memory;
        _originObserver = memory as IMemoryAllocationObserver;
        _writeObserver = memory as IMemoryWriteObserver;
        if (_originObserver is not null)
        {
            _prevOrigin = _originObserver.OnAllocateWithOrigin;
            _originObserver.OnAllocateWithOrigin = HandleOriginAllocate;
        }
        else if (_writeObserver is not null)
        {
            _prevAlloc = _writeObserver.OnAllocate;
            _writeObserver.OnAllocate = HandlePlainAllocate;
        }
    }

    void HandleOriginAllocate(ulong address, int bytes, AllocationOrigin origin)
    {
        lock (_lock) _rawRecords.Add((address, bytes, origin));
        _prevOrigin?.Invoke(address, bytes, origin);
    }

    void HandlePlainAllocate(ulong address, int bytes)
    {
        lock (_lock) _rawRecords.Add((address, bytes, AllocationOrigin.Unknown));
        _prevAlloc?.Invoke(address, bytes);
    }

    public HeapGateResult Evaluate()
    {
        (ulong Address, int Bytes, AllocationOrigin Origin)[] snapshot;
        lock (_lock) snapshot = [.. _rawRecords];

        var entries = new List<HeapAllocationEntry>(snapshot.Length);
        var verifier = _memory as IGameHeapVerifier;
        foreach (var (addr, bytes, origin) in snapshot)
        {
            HeapBlockCheck check;
            if (origin == AllocationOrigin.RawVm)
            {
                check = verifier is not null
                    ? verifier.VerifyGameHeapBlock(addr, bytes) with { Passed = false, Detail = $"RawVm allocation at 0x{addr:X} forbidden for game objects" }
                    : new HeapBlockCheck(false, "origin-only", null, null, $"RawVm allocation at 0x{addr:X} forbidden for game objects");
            }
            else if (verifier is not null)
            {
                check = verifier.VerifyGameHeapBlock(addr, bytes);
            }
            else if (origin == AllocationOrigin.GameHeap)
            {
                check = new HeapBlockCheck(true, "origin-only", null, null, "AllocationOrigin.GameHeap");
            }
            else
            {
                check = new HeapBlockCheck(false, "unverified", null, null, "No IGameHeapVerifier and AllocationOrigin.Unknown");
            }
            entries.Add(new(addr, bytes, origin, check));
        }

        var rawVm = entries.Where(e => e.Origin == AllocationOrigin.RawVm).ToList();
        var failedChecks = entries.Where(e => !e.Verification.Passed).ToList();
        bool passed = rawVm.Count == 0 && failedChecks.Count == 0;
        string mode = entries.Select(e => e.Verification.Mode).FirstOrDefault() ?? (verifier is not null ? "verifier-idle" : "origin-only");
        return new HeapGateResult(passed, mode, entries, rawVm.Count, failedChecks.Count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_originObserver is not null) _originObserver.OnAllocateWithOrigin = _prevOrigin;
        if (_writeObserver is not null && _originObserver is null) _writeObserver.OnAllocate = _prevAlloc;
    }
}

public sealed record HeapGateResult(
    bool Passed,
    string Mode,
    IReadOnlyList<HeapAllocationEntry> Allocations,
    int RawVmCount,
    int FailedVerificationCount);
