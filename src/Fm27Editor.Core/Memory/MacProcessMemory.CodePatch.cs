using System.Runtime.InteropServices;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.Core.Memory;

/// <summary>
/// <see cref="ICodePatchHost"/> for a macOS process, built on Mach VM. Allocates code blocks with
/// <c>mach_vm_map</c> as read/write with a read/write/execute <em>maximum</em> (so the page can later be flipped to
/// read/execute without ever being write+execute at once — macOS W^X), patches <c>__TEXT</c> through
/// <c>mach_vm_protect</c> with the copy-on-write flag, and pauses the game by suspending its threads.
/// </summary>
/// <remarks>
/// <para>The FM27 Mac app runs with the Hardened Runtime (<c>flags=0x10000(runtime)</c>) and the
/// <c>com.apple.security.cs.allow-dyld-environment-variables</c> and
/// <c>com.apple.security.cs.disable-library-validation</c> entitlements, but <em>not</em>
/// <c>com.apple.security.cs.allow-jit</c> (nor <c>allow-unsigned-executable-memory</c>). Without one of those the
/// kernel refuses to make unsigned, freshly-mapped memory executable: the <c>mach_vm_protect</c> call in
/// <see cref="CommitExecutable"/> returns <c>KERN_PROTECTION_FAILURE</c> (0x10000006), and patching a sealed
/// <c>__TEXT</c> page then executing the copy is killed by code-signature enforcement. So on the shipped build this
/// host can allocate and write blocks but cannot flip them to executable; see docs/ARM64-CODE-PATCHING.md.</para>
/// <para>There is also no cross-process instruction-cache invalidation primitive on macOS
/// (<c>sys_icache_invalidate</c> affects only the calling task), which is the other reason
/// <see cref="FlushCode"/> is a no-op here and an in-process (injected) library is needed.</para>
/// </remarks>
public sealed partial class MacProcessMemory : ICodePatchHost
{
    const int VmProtRead = 0x01, VmProtExecute = 0x04, VmProtCopy = 0x10;
    const int VmProtReadWrite = VmProtRead | VmProtWrite;              // 0x03
    const int VmProtReadExecute = VmProtRead | VmProtExecute;          // 0x05
    const int VmProtReadWriteCopy = VmProtRead | VmProtWrite | VmProtCopy; // 0x13: writable view of a code page
    const int VmProtReadWriteExecute = VmProtRead | VmProtWrite | VmProtExecute; // 0x07: maximum only, never current

    const int VmFlagsFixed = 0x0000, VmInheritDefault = 1;
    const int KernProtectionFailure = 0x10000006;

    // The Windows PAGE_* constants the presets pass to Protect, translated to Mach VM protections below.
    const uint PageExecuteReadWrite = 0x40, PageExecuteRead = 0x20, PageReadWrite = 0x04, PageReadOnly = 0x02;

    // Alignment used for candidate addresses and block sizes: one Apple Silicon 16 KB page (also a multiple of 4 KB x86_64
    // pages), avoiding rounding 80 KB blocks up to 128 KB and missing 80-112 KB holes near the image.
    const ulong AllocationGranularity = 0x4000;

    // How far from the site a block may sit: the reach of an ARM64 B, and the only jump a one-instruction patch has.
    const ulong BranchReach = 0x0800_0000;

    readonly object _allocationLock = new();
    readonly Dictionary<ulong, int> _allocations = [];

    /// <summary>
    /// A read/write block near <paramref name="near"/>, with a read/write/execute maximum so
    /// <see cref="CommitExecutable"/> can later make it read/execute. Written as RW first (W^X): the caller fills the
    /// block, then calls <see cref="CommitExecutable"/> before the game's threads can reach it. The hole is taken from
    /// the task's own region map, because the image is wider than any fixed step and a guessed address is usually
    /// already mapped.
    /// </summary>
    ulong ICodePatchHost.AllocateNear(ulong near, int bytes)
    {
        if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        uint task = _task;
        if (task == 0) throw new MemoryAccessException("The process is no longer attached");

        ulong size = RoundUp((ulong)bytes, AllocationGranularity);
        foreach (ulong candidate in Holes(near, size))
        {
            ulong at = candidate;
            if (mach_vm_map(task, ref at, size, 0, VmFlagsFixed, 0, 0, 0, VmProtReadWrite, VmProtReadWriteExecute, VmInheritDefault) == KernSuccess)
                return Track(at, bytes);
        }
        ulong anywhere = 0;
        int kr = mach_vm_map(task, ref anywhere, size, 0, VmFlagsAnywhere, 0, 0, 0, VmProtReadWrite, VmProtReadWriteExecute, VmInheritDefault);
        if (kr != KernSuccess)
            throw new MemoryAccessException($"mach_vm_map({bytes} bytes) failed with kern_return_t {kr}");
        if (anywhere > near ? anywhere - near > BranchReach : near - anywhere > BranchReach)
        {
            mach_vm_deallocate(task, anywhere, size);
            throw new MemoryAccessException(
                $"No free memory within {(long)BranchReach >> 20} MB of 0x{near:X}, so the jump to it would be out of reach");
        }
        return Track(anywhere, bytes);
    }

    /// <summary>Flips a block from <see cref="AllocateNear"/> from read/write to read/execute, once its contents are in place.</summary>
    public void CommitExecutable(ulong address, int bytes)
    {
        uint task = _task;
        if (task == 0) throw new MemoryAccessException("The process is no longer attached");
        int kr = mach_vm_protect(task, address, (ulong)bytes, 0, VmProtReadExecute);
        if (kr != KernSuccess)
            throw new MemoryAccessException(ProtectFailure(address, bytes, VmProtReadExecute, kr));
    }

    /// <summary>
    /// Changes the protection of a range and returns the previous value, so a patch can be written and then restored.
    /// When the previous value cannot be read, <see cref="PageExecuteRead"/> is returned instead of read-only: every
    /// call site patches code, and a code page left read-only would fault on the next instruction fetch.
    /// </summary>
    public uint Protect(ulong address, int bytes, uint protect)
    {
        uint task = _task;
        if (task == 0) throw new MemoryAccessException("The process is no longer attached");
        int previous = CurrentProtection(address);
        int desired = ToVmProtection(protect);
        int kr = mach_vm_protect(task, address, (ulong)bytes, 0, desired);
        if (kr != KernSuccess)
            throw new MemoryAccessException(ProtectFailure(address, bytes, desired, kr));
        return FromVmProtection(previous);
    }

    /// <summary>
    /// Suspends every thread of the game until the result is disposed, so no thread runs half-patched code. The thread
    /// list is a snapshot: a thread the game starts between <c>task_threads</c> and the last <c>thread_suspend</c> keeps
    /// running and can, in principle, execute a half-written site. That window is a few microseconds and the callers
    /// write the site last, so a pause is kept as short as one write and is never held across the whole patch.
    /// </summary>
    public IDisposable Pause()
    {
        uint task = _task;
        if (task == 0) throw new MemoryAccessException("The process is no longer attached");
        int kr = task_threads(task, out nint list, out uint count);
        if (kr != KernSuccess || list == 0 || count == 0)
            throw new MemoryAccessException($"Cannot enumerate the game's threads (kern_return_t {kr})");
        var threads = new uint[count];
        for (int i = 0; i < count; i++) threads[i] = unchecked((uint)Marshal.ReadInt32(list, i * 4));
        mach_vm_deallocate(mach_task_self(), (ulong)list, count * 4);
        for (int i = 0; i < threads.Length; i++)
        {
            int sr = thread_suspend(threads[i]);
            if (sr == KernSuccess) continue;
            for (int j = 0; j < i; j++) thread_resume(threads[j]);
            foreach (uint thread in threads) mach_port_deallocate(mach_task_self(), thread);
            throw new MemoryAccessException($"thread_suspend failed with kern_return_t {sr}");
        }
        return new Resume(threads);
    }

    /// <summary>
    /// No cross-process instruction-cache invalidation exists on macOS (<c>sys_icache_invalidate</c> acts only on the
    /// calling task), so this is a no-op and an in-process (injected) library must flush the cache after patching. The
    /// Hardened Runtime blocker in <see cref="CommitExecutable"/> stops a patch before this point anyway.
    /// </summary>
    public void FlushCode(ulong address, int bytes)
    {
    }

    /// <summary>Releases a block from <see cref="AllocateNear"/>.</summary>
    public void Free(ulong address)
    {
        int bytes;
        lock (_allocationLock)
        {
            if (!_allocations.Remove(address, out bytes)) return;
        }
        uint task = _task;
        if (task != 0) mach_vm_deallocate(task, address, RoundUp((ulong)bytes, AllocationGranularity));
    }

    ulong Track(ulong address, int bytes)
    {
        lock (_allocationLock) _allocations[address] = bytes;
        OnAllocate?.Invoke(address, bytes);
        return address;
    }

    static ulong RoundUp(ulong value, ulong granularity) => (value + granularity - 1) & ~(granularity - 1);

    /// <summary>Page-aligned starts of unmapped holes of at least <paramref name="size"/> bytes, nearest to
    /// <paramref name="near"/> first, all within the reach of a branch. Submaps are entered only to their maximum depth
    /// and then treated as mapped, which is what a fixed <c>mach_vm_map</c> meets as well.</summary>
    IEnumerable<ulong> Holes(ulong near, ulong size)
    {
        ulong low = RoundUp(near > BranchReach ? near - BranchReach : AllocationGranularity, AllocationGranularity);
        ulong high = near + BranchReach;
        var mapped = new List<(ulong Start, ulong End)>();
        lock (_regionLock)
        {
            ulong address = low;
            uint depth = 1;
            while (address <= high && TryQueryRegion(ref address, ref depth, out ulong regionSize, out _, out bool isSubmap))
            {
                if (isSubmap)
                {
                    if (depth >= MaxSubmapDepth) break;
                    depth++;
                    continue;
                }
                ulong end = address + regionSize;
                mapped.Add((address, end));
                if (end <= address) break;
                address = end;
                depth = 1;
            }
        }
        var holes = new List<ulong>();
        ulong cursor = low;
        foreach (var (start, end) in mapped)
        {
            ulong candidate = RoundUp(cursor, AllocationGranularity);
            if (start > candidate && start - candidate >= size) holes.Add(candidate);
            if (end > cursor) cursor = end;
        }
        ulong tail = RoundUp(cursor, AllocationGranularity);
        if (high > tail && high - tail >= size) holes.Add(tail);
        holes.Sort((a, b) => Distance(a, near).CompareTo(Distance(b, near)));
        return holes;
    }

    static ulong Distance(ulong a, ulong b) => a > b ? a - b : b - a;

    static int ToVmProtection(uint protect) => protect switch
    {
        PageExecuteReadWrite => VmProtReadWriteCopy,     // writable view of a code page: RW + copy-on-write, never RWX
        PageExecuteRead => VmProtReadExecute,
        PageReadWrite => VmProtReadWrite,
        PageReadOnly => VmProtRead,
        _ => throw new NotSupportedException($"Unsupported page protection 0x{protect:X}"),
    };

    /// <summary>
    /// The protection to pass back to <see cref="Protect"/> for a value read before the patch. Every call site patches
    /// code, and a code page left read-only would fault on the next instruction fetch, so an unreadable previous value
    /// falls back to <see cref="PageExecuteRead"/> rather than read-only.
    /// </summary>
    static uint FromVmProtection(int protection) =>
        (protection & VmProtExecute) != 0 ? PageExecuteRead :
        (protection & VmProtWrite) != 0 ? PageReadWrite :
        protection == 0 ? PageExecuteRead :
        PageReadOnly;

    /// <summary>The protection of the region that contains <paramref name="address"/>, walked from the task map base.</summary>
    int CurrentProtection(ulong address)
    {
        lock (_regionLock)
        {
            ulong addr = 0;
            uint depth = 1;
            while (TryQueryRegion(ref addr, ref depth, out ulong size, out int protection, out bool isSubmap))
            {
                if (isSubmap)
                {
                    if (depth >= MaxSubmapDepth) break;
                    depth++;
                    continue;
                }
                if (address >= addr && address < addr + size) return protection;
                ulong next = addr + size;
                if (next <= addr) break;
                addr = next;
                depth = 1;
            }
        }
        return 0;
    }

    static string ProtectFailure(ulong address, int bytes, int desired, int kr)
    {
        string message = $"mach_vm_protect(0x{address:X}, {bytes} bytes, VM_PROT 0x{desired:X}) failed with kern_return_t {kr}";
        return kr == KernProtectionFailure
            ? message + ". The game runs with the Hardened Runtime without the allow-jit entitlement, so unsigned " +
              "executable memory is refused; see docs/ARM64-CODE-PATCHING.md"
            : message;
    }

    sealed class Resume(uint[] threads) : IDisposable
    {
        bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            foreach (var thread in threads)
            {
                thread_resume(thread);
                mach_port_deallocate(mach_task_self(), thread);
            }
        }
    }

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_map(uint targetTask, ref ulong address, ulong size, ulong mask, int flags,
        uint objectHandle, ulong offset, int copy, int curProtection, int maxProtection, int inheritance);

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_protect(uint targetTask, ulong address, ulong size, int setMaximum, int newProtection);

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_deallocate(uint targetTask, ulong address, ulong size);

    [LibraryImport(LibSystem)]
    private static partial int task_threads(uint targetTask, out nint threadList, out uint threadCount);

    [LibraryImport(LibSystem)]
    private static partial int thread_suspend(uint thread);

    [LibraryImport(LibSystem)]
    private static partial int thread_resume(uint thread);
}
