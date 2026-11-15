using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.Core.ForceAccept;

/// <summary>
/// Writing a preset means writing the game's code pages, which are execute/read-only (confirmed on the live FM27
/// image: <c>PAGE_EXECUTE_READ</c> at every force-accept site, so a plain <c>WriteProcessMemory</c> is refused).
/// A write raises the page protection for its own length, puts the old protection back, and flushes the
/// instruction cache so no thread can run bytes it prefetched before the patch — or before an undo.
/// </summary>
static class CodePage
{
    const uint PageExecuteReadWrite = 0x40;

    /// <summary>Writes one patch to a code page. A memory that cannot patch code pages writes directly.</summary>
    /// <exception cref="MemoryAccessException">The write did not land.</exception>
    public static void Write(IProcessMemory memory, ulong address, byte[] bytes)
    {
        if (memory is not ICodePatchHost host)
        {
            if (!memory.TryWrite(address, bytes))
                throw new MemoryAccessException($"Cannot write {bytes.Length} bytes at 0x{address:X}");
            return;
        }

        uint previous = host.Protect(address, bytes.Length, PageExecuteReadWrite);
        bool ok;
        try
        {
            ok = memory.TryWrite(address, bytes);
        }
        finally
        {
            host.Protect(address, bytes.Length, previous);
        }
        if (!ok)
            throw new MemoryAccessException($"Cannot write {bytes.Length} bytes at 0x{address:X}");
        host.FlushCode(address, bytes.Length);
    }

    /// <summary>
    /// Best-effort write for a rollback: a failure here is swallowed, because the history group still holds the
    /// entry and the user can undo it there.
    /// </summary>
    public static void TryWrite(IProcessMemory memory, ulong address, byte[] bytes)
    {
        try { Write(memory, address, bytes); }
        catch (MemoryAccessException) { }
    }
}
