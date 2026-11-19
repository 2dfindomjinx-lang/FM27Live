using System.Runtime.InteropServices;
using System.Text;

namespace Fm27Editor.Core.Memory;

/// <summary>mach_vm read/write access to one macOS process, obtained through task_for_pid.</summary>
public sealed partial class MacProcessMemory : IProcessMemory, IMemoryRegions, IRemoteAllocator, IMemoryWriteObserver, IMemoryAllocationObserver, IGameHeapVerifier, IWriteGate, IMacMemory, IDisposable
{
    const string LibSystem = "/usr/lib/libSystem.B.dylib";
    const int KernSuccess = 0;
    const uint TaskDyldInfoFlavor = 17, TaskDyldInfoCount = 5;
    const int VmFlagsAnywhere = 1;
    const uint VmRegionSubmapInfoCount64 = 19;
    const int VmProtWrite = 0x02, MaxSubmapDepth = 16;
    const int ProtectionOffset = 0, UserTagOffset = 20, IsSubmapOffset = 48;
    const int KernInvalidAddress = 1, VmRegionBasicInfo64 = 9;
    const uint VmRegionBasicInfoCount64 = 9;

    // VM_MEMORY_MALLOC* user_tag values assigned by macOS libmalloc zones (mach/vm_statistics.h).
    static readonly HashSet<int> MallocUserTags = [1, 2, 3, 4, 7, 8, 9, 11, 12, 13];

    readonly object _regionLock = new();
    uint _task;

    public int ProcessId { get; }

    /// <summary>The task port used for every Mach call; MACH_PORT_NULL once disposed.</summary>
    public uint TaskPort => _task;

    public bool CanWrite { get; }

    /// <summary>Called after every successful write with the address, the bytes that were there and the bytes written.</summary>
    public Action<ulong, byte[], byte[]>? OnWrite { get; set; }

    /// <summary>Called with the address and size of each fresh block, so the history can ignore writes into it.</summary>
    public Action<ulong, int>? OnAllocate { get; set; }

    /// <summary>Called with allocation origin (<see cref="AllocationOrigin.RawVm"/> vs <see cref="AllocationOrigin.GameHeap"/>).</summary>
    public Action<ulong, int, AllocationOrigin>? OnAllocateWithOrigin { get; set; }

    /// <summary>Called before each write; throws to refuse it while the game loads a save or has none loaded.</summary>
    public Action? BeforeWrite { get; set; }

    public MacProcessMemory(int processId, bool writable)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Requires macOS");
        int kr = task_for_pid(mach_task_self(), processId, out uint task);
        if (kr != KernSuccess || task == 0)
            throw new MemoryAccessException(
                $"task_for_pid({processId}) failed with kern_return_t {kr}. " +
                "macOS only allows attaching to processes of the same user with the right privileges; " +
                "run the editor with sudo (or from a terminal that has Full Disk Access) and try again.");
        ProcessId = processId;
        CanWrite = writable;
        _task = task;
    }

    public unsafe bool TryRead(ulong address, Span<byte> buffer)
    {
        if (buffer.IsEmpty) return true;
        uint task = _task;
        if (task == 0 || address == 0) return false;
        fixed (byte* p = buffer)
            return mach_vm_read_overwrite(task, address, (ulong)buffer.Length, (ulong)p, out ulong read) == KernSuccess
                   && read == (ulong)buffer.Length;
    }

    public unsafe bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
        BeforeWrite?.Invoke();
        if (data.IsEmpty) return true;
        uint task = _task;
        if (task == 0 || address == 0) return false;
        var listener = OnWrite;
        byte[]? before = null;
        if (listener is not null)
        {
            before = new byte[data.Length];
            if (!TryRead(address, before)) before = null;
        }
        bool ok;
        fixed (byte* p = data)
            ok = mach_vm_write(task, address, (ulong)p, (uint)data.Length) == KernSuccess;
        if (ok && listener is not null && before is not null) listener(address, before, data.ToArray());
        return ok;
    }

    /// <summary>Reads a NUL-terminated UTF-8 string, for dyld paths and other remote strings.</summary>
    public string? TryReadCString(ulong address, int maxLength = 1024)
    {
        if (address == 0 || maxLength <= 0) return null;
        var buffer = new byte[maxLength];
        if (!TryRead(address, buffer)) return null;
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = maxLength;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>
    /// Fresh zeroed read/write memory inside the game process (mach_vm_allocate with VM_FLAGS_ANYWHERE). The game's own
    /// allocator does not know it, so the game may fail when it later frees or regrows a container pointing here.
    /// </summary>
    public ulong Allocate(int bytes)
    {
        if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        uint task = _task;
        if (task == 0) throw new MemoryAccessException("The process is no longer attached");
        ulong address = 0;
        int kr = mach_vm_allocate(task, ref address, (ulong)bytes, VmFlagsAnywhere);
        if (kr != KernSuccess)
            throw new MemoryAccessException($"mach_vm_allocate({bytes}) failed with kern_return_t {kr}");
        OnAllocateWithOrigin?.Invoke(address, bytes, AllocationOrigin.RawVm);
        OnAllocate?.Invoke(address, bytes);
        return address;
    }

    public unsafe bool TryQueryRegionTag(ulong address, out int userTag, out ulong regionStart, out ulong regionSize)
    {
        userTag = 0;
        regionStart = 0;
        regionSize = 0;
        uint task = _task;
        if (task == 0 || address == 0) return false;
        lock (_regionLock)
        {
            ulong query = address;
            uint depth = 1;
            int* info = stackalloc int[(int)VmRegionSubmapInfoCount64];
            for (int step = 0; step < MaxSubmapDepth + 4; step++)
            {
                uint count = VmRegionSubmapInfoCount64;
                ulong size = 0;
                int kr = mach_vm_region_recurse(task, ref query, ref size, ref depth, (nint)info, ref count);
                if (kr != KernSuccess || size == 0) return false;
                if (info[IsSubmapOffset / sizeof(int)] != 0)
                {
                    if (depth >= MaxSubmapDepth) return false;
                    depth++;
                    continue;
                }
                if (address < query || address >= query + size) return false;
                userTag = info[UserTagOffset / sizeof(int)];
                regionStart = query;
                regionSize = size;
                return true;
            }
            return false;
        }
    }

    public HeapBlockCheck VerifyGameHeapBlock(ulong address, int requestedBytes)
    {
        if (address == 0)
            return new(false, "mach-tag+malloc_size", null, null, "null pointer");
        if (!TryQueryRegionTag(address, out int tag, out ulong regStart, out ulong regSize))
            return new(false, "mach-tag+malloc_size", null, null, $"address 0x{address:X} has no mapped VM region");
        if (!MallocUserTags.Contains(tag))
            return new(false, "mach-tag+malloc_size", tag, null,
                $"VM region 0x{regStart:X}..0x{regStart + regSize:X} has user_tag={tag} (not VM_MEMORY_MALLOC*)");
        if (!CanWrite)
            return new(true, "mach-tag-only", tag, null,
                $"user_tag={tag} inside 0x{regStart:X}+0x{regSize:X} (read-only session skipped remote malloc_size)");
        if (!TryQueryMallocSize(address, out ulong mSize))
            return new(false, "mach-tag+malloc_size", tag, null, $"malloc_size(0x{address:X}) could not be queried");
        if (mSize < (ulong)requestedBytes)
            return new(false, "mach-tag+malloc_size", tag, mSize,
                $"malloc_size={mSize} < requested={requestedBytes} (user_tag={tag})");
        return new(true, "mach-tag+malloc_size", tag, mSize,
            $"user_tag={tag}, malloc_size={mSize} >= {requestedBytes}");
    }

    /// <summary>Writable regions of the task: heap and other read/write mappings, plus a module's writable data when
    /// the region starts inside [imageStart, imageEnd).</summary>
    public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
    {
        var regions = new List<MemoryRegion>();
        lock (_regionLock)
        {
            ulong address = 0;
            uint depth = 1;
            while (TryQueryRegion(ref address, ref depth, out ulong size, out int protection, out bool isSubmap))
            {
                if (isSubmap)
                {
                    if (depth >= MaxSubmapDepth) break;
                    depth++;
                    continue;
                }
                if ((protection & VmProtWrite) != 0)
                    regions.Add(new MemoryRegion(address, size, address >= imageStart && address < imageEnd));
                ulong next = address + size;
                if (next <= address) break;
                address = next;
                depth = 1;
            }
        }
        return regions;
    }

    /// <summary>
    /// One region step; advances <paramref name="address"/> to the next region start. The recursion needs an address that
    /// is already mapped and answers <c>KERN_INVALID_ADDRESS</c> from a hole, so a walk that starts in one (every hole
    /// search does) continues with the plain map search for the next mapped region instead.
    /// </summary>
    unsafe bool TryQueryRegion(ref ulong address, ref uint depth, out ulong size, out int protection, out bool isSubmap)
    {
        size = 0;
        protection = 0;
        isSubmap = false;
        uint task = _task;
        if (task == 0) return false;
        int* info = stackalloc int[(int)VmRegionSubmapInfoCount64];
        uint count = VmRegionSubmapInfoCount64;
        int kr = mach_vm_region_recurse(task, ref address, ref size, ref depth, (nint)info, ref count);
        if (kr == KernInvalidAddress) return TryNextRegion(ref address, out size, out protection);
        if (kr != KernSuccess || size == 0) return false;
        protection = info[ProtectionOffset / sizeof(int)];
        isSubmap = info[IsSubmapOffset / sizeof(int)] != 0;
        return true;
    }

    /// <summary>The first mapped region at or above <paramref name="address"/>, and its protection.</summary>
    unsafe bool TryNextRegion(ref ulong address, out ulong size, out int protection)
    {
        size = 0;
        protection = 0;
        uint task = _task;
        if (task == 0) return false;
        int* info = stackalloc int[(int)VmRegionBasicInfoCount64];
        uint count = VmRegionBasicInfoCount64;
        int kr = mach_vm_region(task, ref address, out size, VmRegionBasicInfo64, (nint)info, ref count, out uint objectName);
        if (objectName != 0) mach_port_deallocate(mach_task_self(), objectName);
        if (kr != KernSuccess || size == 0) return false;
        protection = info[ProtectionOffset / sizeof(int)];
        return true;
    }

    public bool TryGetTaskDyldInfo(out ulong allImageInfoAddress, out ulong allImageInfoSize)
    {
        allImageInfoAddress = 0;
        allImageInfoSize = 0;
        uint task = _task;
        if (task == 0) return false;
        uint count = TaskDyldInfoCount;
        if (task_info(task, TaskDyldInfoFlavor, out TaskDyldInfoData info, ref count) != KernSuccess) return false;
        allImageInfoAddress = info.AllImageInfoAddress;
        allImageInfoSize = info.AllImageInfoSize;
        return allImageInfoAddress != 0;
    }

    /// <summary>The task's dyld_all_image_infos address and size; throws when task_info cannot provide them.</summary>
    public void GetTaskDyldInfo(out ulong allImageInfoAddress, out ulong allImageInfoSize)
    {
        if (!TryGetTaskDyldInfo(out allImageInfoAddress, out allImageInfoSize))
            throw new MemoryAccessException($"task_info(TASK_DYLD_INFO) failed for process {ProcessId}");
    }

    public void Dispose()
    {
        uint task = _task;
        if (task == 0) return;
        _task = 0;
        mach_port_deallocate(mach_task_self(), task);
    }

    /// <summary>64-bit task_dyld_info; the kernel's count is 5 naturals (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TaskDyldInfoData
    {
        public ulong AllImageInfoAddress;
        public ulong AllImageInfoSize;
        public int AllImageInfoFormat;
    }

    [LibraryImport(LibSystem)]
    private static partial uint mach_task_self();

    [LibraryImport(LibSystem)]
    private static partial int task_for_pid(uint targetTask, int pid, out uint task);

    [LibraryImport(LibSystem)]
    private static partial int mach_port_deallocate(uint task, uint name);

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_read_overwrite(uint targetTask, ulong address, ulong size, ulong bufferAddress, out ulong outSize);

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_write(uint targetTask, ulong address, ulong data, uint dataCount);

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_allocate(uint targetTask, ref ulong address, ulong size, int flags);

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_region_recurse(uint targetTask, ref ulong address, ref ulong size,
        ref uint depth, nint info, ref uint infoCount);

    [LibraryImport(LibSystem)]
    private static partial int mach_vm_region(uint targetTask, ref ulong address, out ulong size, int flavor,
        nint info, ref uint infoCount, out uint objectName);

    [LibraryImport(LibSystem)]
    private static partial int task_info(uint targetTask, uint flavor, out TaskDyldInfoData info, ref uint infoCount);
}
