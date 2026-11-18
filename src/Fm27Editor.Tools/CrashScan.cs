using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// <c>crashscan</c>: while FM shows its "Application Error" box, the faulting thread's stack still holds the EXCEPTION_RECORD and
/// CONTEXT its crash handler received. Reads every thread's stack (read only) and prints each access-violation record found:
/// code, faulting instruction (as a game_plugin RVA when inside it), the address it read or wrote, and the registers.
/// </summary>
static class CrashScan
{
    const uint ThreadGetContext = 0x0008, ThreadQueryInformation = 0x0040, ProcessVmRead = 0x0010, ProcessQuery = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetThreadContext(nint thread, nint context);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(nint process, ulong address, nint buffer, nint size, out nint read);

    public static int Run()
    {
        var fm = Process.GetProcessesByName("fm").FirstOrDefault() ?? throw new InvalidOperationException("fm.exe is not running");
        var plugin = fm.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("game_plugin.dll", StringComparison.OrdinalIgnoreCase));
        ulong pluginBase = plugin is null ? 0 : (ulong)plugin.BaseAddress, pluginEnd = plugin is null ? 0 : pluginBase + (ulong)plugin.ModuleMemorySize;
        var modules = fm.Modules.Cast<ProcessModule>().Select(m => (Name: m.ModuleName, Base: (ulong)m.BaseAddress, End: (ulong)m.BaseAddress + (ulong)m.ModuleMemorySize)).ToList();
        string Where(ulong a) => modules.FirstOrDefault(m => a >= m.Base && a < m.End) is { Name: not null } m ? $"{m.Name}+0x{a - m.Base:X}" : $"0x{a:X}";
        nint process = OpenProcess(ProcessVmRead | ProcessQuery, false, fm.Id);
        if (process == 0) throw new Win32Exception();
        Console.WriteLine($"fm.exe {fm.Id}, game_plugin 0x{pluginBase:X}..0x{pluginEnd:X}, {fm.Threads.Count} threads");
        nint ctx = Marshal.AllocHGlobal(0x4D0 + 16);
        nint aligned = (ctx + 15) & ~(nint)15;
        int found = 0;
        try
        {
            foreach (ProcessThread t in fm.Threads)
            {
                nint thread = OpenThread(ThreadGetContext | ThreadQueryInformation, false, (uint)t.Id);
                if (thread == 0) continue;
                try
                {
                    Marshal.Copy(new byte[0x4D0], 0, aligned, 0x4D0);
                    Marshal.WriteInt32(aligned, 0x30, 0x10000B);           // CONTEXT_FULL (AMD64)
                    if (!GetThreadContext(thread, aligned)) continue;
                    ulong rsp = (ulong)Marshal.ReadInt64(aligned, 0x98);
                    const int window = 0x100000;
                    var stack = new byte[window];
                    var handle = GCHandle.Alloc(stack, GCHandleType.Pinned);
                    try
                    {
                        // Read page by page so an unreadable tail does not lose the rest.
                        for (int off = 0; off < window; off += 0x1000)
                            ReadProcessMemory(process, (rsp & ~0xFFFUL) + (ulong)off, handle.AddrOfPinnedObject() + off, 0x1000, out _);
                    }
                    finally { handle.Free(); }
                    ulong stackBase = rsp & ~0xFFFUL;
                    for (int i = 0; i + 0x98 <= window; i += 4)
                    {
                        uint code = BitConverter.ToUInt32(stack, i);
                        if (code != 0xC0000005 && code != 0xC0000374 && code != 0xC000041D && code != 0xE06D7363) continue;
                        if (i % 8 != 0) continue;
                        ulong at = BitConverter.ToUInt64(stack, i + 0x10);
                        uint parameters = BitConverter.ToUInt32(stack, i + 0x18);
                        if (parameters > 15 || at == 0) continue;
                        found++;
                        Console.WriteLine($"thread {t.Id}: exception 0x{code:X8} at {Where(at)} (record at stack 0x{stackBase + (ulong)i:X})");
                        for (int k = 0; k < parameters; k++)
                            Console.WriteLine($"    info[{k}] = 0x{BitConverter.ToUInt64(stack, i + 0x20 + k * 8):X}");
                        // A CONTEXT nearby: ContextFlags (+0x30) with the AMD64 bit 0x100000 and Rip (+0xF8) equal to the fault address.
                        for (int j = Math.Max(0, i - 0x2000); j + 0x4D0 <= window && j < i + 0x2000; j += 16)
                        {
                            uint flags = BitConverter.ToUInt32(stack, j + 0x30);
                            if ((flags & 0x100000) == 0 || BitConverter.ToUInt64(stack, j + 0xF8) != at) continue;
                            string[] names = ["Rax", "Rcx", "Rdx", "Rbx", "Rsp", "Rbp", "Rsi", "Rdi", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15"];
                            for (int r = 0; r < names.Length; r++)
                            {
                                ulong v = BitConverter.ToUInt64(stack, j + 0x78 + r * 8);
                                Console.WriteLine($"    {names[r],-4} 0x{v:X16}  {Where(v)}");
                            }
                            // The stack above the fault: return addresses into game_plugin, and pointers to objects whose first
                            // qword is a game_plugin address (a vtable), nearest first.
                            ulong faultRsp = BitConverter.ToUInt64(stack, j + 0x98);
                            int from = (int)Math.Max(0, (long)(faultRsp - stackBase));
                            int shown = 0;
                            var head = new byte[8];
                            var headHandle = GCHandle.Alloc(head, GCHandleType.Pinned);
                            try
                            {
                                for (int s = from; s + 8 <= window && shown < 80; s += 8)
                                {
                                    ulong q = BitConverter.ToUInt64(stack, s);
                                    if (q >= pluginBase && q < pluginEnd) { Console.WriteLine($"    [rsp+0x{s - from:X4}] ret  game_plugin+0x{q - pluginBase:X}"); shown++; continue; }
                                    if (q < 0x10000 || q > 0x7FFF_FFFF_FFFF || (q & 7) != 0) continue;
                                    if (!ReadProcessMemory(process, q, headHandle.AddrOfPinnedObject(), 8, out _)) continue;
                                    ulong vt = BitConverter.ToUInt64(head);
                                    if (vt >= pluginBase && vt < pluginEnd) { Console.WriteLine($"    [rsp+0x{s - from:X4}] obj  0x{q:X} vtable game_plugin+0x{vt - pluginBase:X}"); shown++; }
                                }
                            }
                            finally { headHandle.Free(); }
                            break;
                        }
                    }
                    // Return addresses into game_plugin on this stack, nearest first (only for threads that hold a record).
                }
                finally { CloseHandle(thread); }
            }
        }
        finally { Marshal.FreeHGlobal(ctx); CloseHandle(process); }
        Console.WriteLine(found == 0 ? "no exception record found on any stack" : $"{found} record(s)");
        return found == 0 ? 1 : 0;
    }
}
