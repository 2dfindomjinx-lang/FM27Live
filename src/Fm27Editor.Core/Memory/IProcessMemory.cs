namespace Fm27Editor.Core.Memory;

/// <summary>Raw access to another process's address space.</summary>
public interface IProcessMemory
{
    /// <summary>Fills <paramref name="buffer"/> from <paramref name="address"/>; false if any byte was unreadable.</summary>
    bool TryRead(ulong address, Span<byte> buffer);

    bool TryWrite(ulong address, ReadOnlySpan<byte> data);
}

/// <summary>A provider that supports simultaneous read-only calls from independent worker threads.</summary>
public interface IConcurrentMemoryReads : IProcessMemory { }

/// <summary>Optional enumeration of writable regions for read-only discovery; omissions do not prove absence.</summary>
public interface IMemoryRegions
{
    IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0);
}

public static class ProcessMemoryExtensions
{
    public static byte[] Read(this IProcessMemory memory, ulong address, int length)
    {
        var buffer = new byte[length];
        if (!memory.TryRead(address, buffer))
            throw new MemoryAccessException($"Cannot read {length} bytes at 0x{address:X}");
        return buffer;
    }

    public static ulong ReadUInt64(this IProcessMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        if (!memory.TryRead(address, b)) throw new MemoryAccessException($"Cannot read pointer at 0x{address:X}");
        return BitConverter.ToUInt64(b);
    }
}

public sealed class MemoryAccessException(string message) : Exception(message);

public readonly record struct MemoryRegion(ulong Start, ulong Size, bool IsModuleData)
{
    /// <summary>OS guard pages are reserved access traps, not readable object storage.</summary>
    public bool IsGuard { get; init; }
}
