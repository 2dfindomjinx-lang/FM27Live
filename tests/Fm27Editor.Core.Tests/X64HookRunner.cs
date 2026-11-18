using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fm27Editor.Core.Tests;

internal static class X64HookRunner
{
    static readonly Lazy<string> Executable = new(() =>
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Native hook tests need Windows x64 or macOS with the x64 runner");
        var output = Path.Combine(Path.GetTempPath(), "fm27-x64-hook-" + Guid.NewGuid().ToString("N"));
        Run("/usr/bin/clang", ["-arch", "x86_64", "-O2", Path.Combine(AppContext.BaseDirectory, "Native", "x64-hook-runner.c"), "-o", output]);
        return output;
    });

    public static int Invoke(nint block, nint ctx, nint proposal)
    {
        string file = Path.Combine(Path.GetTempPath(), "fm27-hook-image-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = new byte[0x10000];
            Marshal.Copy(block, bytes, 0, bytes.Length);
            File.WriteAllBytes(file, bytes);
            string result = Run(Executable.Value, [file, ((ulong)block).ToString("X"), ((ulong)ctx).ToString("X"), ((ulong)proposal).ToString("X")]);
            bytes = File.ReadAllBytes(file);
            Assert.Equal(0x10000, bytes.Length);
            Marshal.Copy(bytes, 0, block, bytes.Length);
            return int.Parse(result.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { File.Delete(file); }
    }

    static string Run(string program, string[] args)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Native hook test timed out"); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Native hook test exited {process.ExitCode}: {error.GetAwaiter().GetResult()}");
        return output.GetAwaiter().GetResult();
    }
}
