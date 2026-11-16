using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Tests;

/// <summary>One partial OS write failure; later calls work so rollback can be verified byte for byte.</summary>
sealed class WriteFaultMemory(IProcessMemory inner) : IProcessMemory
{
    public int Calls { get; private set; }
    public int Failure { get; set; } = int.MaxValue;
    public void Reset(int failure) { Calls = 0; Failure = failure; }
    public bool TryRead(ulong address, Span<byte> data) => inner.TryRead(address, data);
    public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        if (++Calls != Failure) return inner.TryWrite(address, data);
        inner.TryWrite(address, data[..Math.Max(1, data.Length / 2)]);
        return false;
    }
}
