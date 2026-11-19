using System.Runtime.InteropServices;

namespace Fm27Editor.App.Live;

/// <summary>A native message queue receives F8 even when FM owns keyboard focus. No game injection.</summary>
public sealed class OverlayHotkey : IDisposable
{
    readonly Thread thread;
    readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    uint threadId;
    public Task<bool> Ready => ready.Task;
    public OverlayHotkey(Action toggle)
    {
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            bool registered = RegisterHotKey(IntPtr.Zero, 1, 0x4000, 0x77); // MOD_NOREPEAT + F8
            ready.TrySetResult(registered);
            if (!registered) return;
            try { while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) if (message.Message == 0x0312) toggle(); }
            finally { UnregisterHotKey(IntPtr.Zero, 1); }
        }) { IsBackground = true, Name = "FM27Live F8" };
        thread.Start();
    }
    public void Dispose() { if (threadId != 0) PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); }

    public static IntPtr Foreground => GetForegroundWindow();
    public static void Restore(IntPtr window) { if (window != IntPtr.Zero && IsWindow(window)) SetForegroundWindow(window); }
    public static bool IsGameForeground()
    {
        GetWindowThreadProcessId(Foreground, out uint pid);
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName.Equals("fm", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    [StructLayout(LayoutKind.Sequential)] struct MessageData { public IntPtr Hwnd; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MessageData msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MessageData msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, UIntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
