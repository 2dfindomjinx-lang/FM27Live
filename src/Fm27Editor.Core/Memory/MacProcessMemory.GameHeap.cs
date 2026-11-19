using System.Runtime.InteropServices;

namespace Fm27Editor.Core.Memory;

/// <summary>
/// <see cref="IGameHeapAllocator"/> for a macOS process: <c>calloc(1, bytes)</c> runs inside the game, so the game can
/// later <c>free</c> the block. A <c>mach_vm_allocate</c> block made the game abort on Load Game ("pointer being freed
/// was not allocated", 19/11/2026). The privileged helper's RemoteCalloc op does the same with the same stub.
/// </summary>
/// <remarks>
/// A raw Mach thread cannot call <c>calloc</c> (it has no pthread state), so it runs a small stub that starts a pthread
/// with <c>pthread_create_from_mach_thread</c>; that pthread calls <c>calloc</c> and stores the result. Both functions
/// live in the dyld shared cache, which every process maps at the same address for one boot; their first bytes in the
/// game are compared with this process's before use. The stub, its data and a stack are allocated once per process and
/// reused; the code page is committed read/execute (W^X), as the code-patch caves are.
/// </remarks>
public sealed partial class MacProcessMemory : IGameHeapAllocator
{
    // clang -arch arm64 of the stub documented in fm27_helper.c (k_calloc_stub). Data: +0 calloc,
    // +8 pthread_create_from_mach_thread, +0x10 nmemb, +0x18 size, +0x20 result, +0x28 worker done, +0x2C creator done.
    static readonly uint[] CallocStub =
    [
        0xAA0003F3, 0x9100C260, 0xD2800001, 0x10000122, 0xAA1303E3, 0xF9400668, 0xD63F0100, 0x9100B269,
        0x52800028, 0x889FFD28, 0xD503205F, 0x17FFFFFF, 0xA9BE7BFD, 0x910003FD, 0xF9000BF3, 0xAA0003F3,
        0xF9400A60, 0xF9400E61, 0xF9400268, 0xD63F0100, 0xF9001260, 0x9100A269, 0x52800028, 0x889FFD28,
        0xF9400BF3, 0xA8C27BFD, 0xD2800000, 0xD65F03C0,
    ];

    const ulong CallocPage = 0x4000, CallocStack = 0x40000;
    const int CallocTimeoutMs = 3000;
    const int ArmThreadState64 = 6, ArmThreadState64Count = 68;

    readonly object _callocLock = new();
    ulong _callocData, _callocCode, _callocStackTop, _callocFn, _mallocSizeFn;

    public ulong AllocateFromGameHeap(int bytes)
    {
        if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
        if (bytes is <= 0 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bytes));
        ulong address = RunWorker(_callocFn == 0 ? null : _callocFn, 1UL, (ulong)bytes);
        if (address == 0) throw new MemoryAccessException($"calloc({bytes}) inside the game returned NULL");
        OnAllocateWithOrigin?.Invoke(address, bytes, AllocationOrigin.GameHeap);
        OnAllocate?.Invoke(address, bytes);
        return address;
    }

    public bool TryQueryMallocSize(ulong address, out ulong mallocSize)
    {
        mallocSize = 0;
        if (!CanWrite || address == 0) return false;
        try
        {
            EnsureCallocCave(_task);
            if (_mallocSizeFn == 0) return false;
            mallocSize = RunWorker(_mallocSizeFn, address, 0UL);
            return mallocSize > 0;
        }
        catch
        {
            return false;
        }
    }

    ulong RunWorker(ulong? fnOverride, ulong arg0, ulong arg1)
    {
        uint task = _task;
        if (task == 0) throw new MemoryAccessException("The process is no longer attached");
        lock (_callocLock)
        {
            EnsureCallocCave(task);
            ulong targetFn = fnOverride ?? _callocFn;
            if (!TryWrite(_callocData, BitConverter.GetBytes(targetFn)))
                throw new MemoryAccessException("Could not write target function pointer");
            var args = new byte[48];
            BitConverter.GetBytes(arg0).CopyTo(args, 0);
            BitConverter.GetBytes(arg1).CopyTo(args, 8);
            if (!TryWrite(_callocData + 0x10, args)) throw new MemoryAccessException("Could not write the remote call arguments");

            var state = new ulong[ArmThreadState64Count / 2];
            state[0] = _callocData;   // x0
            state[31] = _callocStackTop; // sp (after x0..x28, fp, lr)
            state[32] = _callocCode;  // pc
            int kr = thread_create_running(task, ArmThreadState64, state, ArmThreadState64Count, out uint thread);
            if (kr != KernSuccess) throw new MemoryAccessException($"thread_create_running failed with kern_return_t {kr}");
            var block = new byte[24];
            bool done = false;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < CallocTimeoutMs)
                {
                    if (TryRead(_callocData + 0x20, block) && BitConverter.ToUInt32(block, 8) == 1 && BitConverter.ToUInt32(block, 12) == 1)
                    {
                        done = true;
                        break;
                    }
                    Thread.Sleep(1);
                }
            }
            finally
            {
                thread_terminate(thread);
                mach_port_deallocate(mach_task_self(), thread);
            }
            if (!done)
            {
                // A late worker could still store into this data block; never reuse it.
                _callocCode = 0;
                throw new MemoryAccessException("remote call inside the game timed out (is the game paused?)");
            }
            return BitConverter.ToUInt64(block, 0);
        }
    }

    void EnsureCallocCave(uint task)
    {
        if (_callocCode != 0) return;
        nint libSystem = NativeLibrary.Load(LibSystem);
        ulong calloc = (ulong)NativeLibrary.GetExport(libSystem, "calloc");
        ulong create = (ulong)NativeLibrary.GetExport(libSystem, "pthread_create_from_mach_thread");
        ulong mallocSize = NativeLibrary.TryGetExport(libSystem, "malloc_size", out nint msPtr) ? (ulong)msPtr : 0;
        foreach (var (name, address) in new[] { ("calloc", calloc), ("pthread_create_from_mach_thread", create) })
        {
            var local = new byte[32];
            Marshal.Copy((nint)address, local, 0, local.Length);
            var remote = new byte[32];
            if (!TryRead(address, remote) || !remote.AsSpan().SequenceEqual(local))
                throw new MemoryAccessException($"{name} is not at the same address in the game");
        }
        if (mallocSize != 0)
        {
            var local = new byte[32];
            Marshal.Copy((nint)mallocSize, local, 0, local.Length);
            var remote = new byte[32];
            if (!TryRead(mallocSize, remote) || !remote.AsSpan().SequenceEqual(local))
                mallocSize = 0;
        }
        _callocFn = calloc;
        _mallocSizeFn = mallocSize;

        ulong data = 0, code = 0, stack = 0;
        int kr = mach_vm_allocate(task, ref data, CallocPage, VmFlagsAnywhere);
        if (kr == KernSuccess) kr = mach_vm_allocate(task, ref code, CallocPage, VmFlagsAnywhere);
        if (kr == KernSuccess) kr = mach_vm_allocate(task, ref stack, CallocStack, VmFlagsAnywhere);
        var stub = new byte[CallocStub.Length * 4];
        Buffer.BlockCopy(CallocStub, 0, stub, 0, stub.Length);
        var pointers = new byte[16];
        BitConverter.GetBytes(calloc).CopyTo(pointers, 0);
        BitConverter.GetBytes(create).CopyTo(pointers, 8);
        bool ok = kr == KernSuccess && TryWrite(code, stub) && TryWrite(data, pointers);
        if (ok) kr = mach_vm_protect(task, code, CallocPage, 0, VmProtReadExecute);
        if (!ok || kr != KernSuccess)
        {
            if (data != 0) mach_vm_deallocate(task, data, CallocPage);
            if (code != 0) mach_vm_deallocate(task, code, CallocPage);
            if (stack != 0) mach_vm_deallocate(task, stack, CallocStack);
            throw new MemoryAccessException($"Could not set up calloc inside the game (kern_return_t {kr})");
        }
        _callocData = data;
        _callocCode = code;
        _callocStackTop = (stack + CallocStack - 0x100) & ~0xFUL;
    }

    [LibraryImport(LibSystem)]
    private static partial int thread_create_running(uint parentTask, int flavor, ulong[] state, int count, out uint thread);

    [LibraryImport(LibSystem)]
    private static partial int thread_terminate(uint thread);
}
