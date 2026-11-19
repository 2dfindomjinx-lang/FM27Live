using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Verification;

public sealed record AuditWindow(string Name, ulong Address, int Length);

public sealed record ObservedWrite(ulong Address, int Length, byte[] Before, byte[] After);

public sealed record WriteAuditResult(
    bool Passed,
    IReadOnlyList<ObservedWrite> ObservedWrites,
    IReadOnlyList<string> Violations);

/// <summary>
/// Captures surrounding memory windows and vector headers before a write round-trip, tracks every raw
/// <see cref="IProcessMemory.TryWrite"/> performed during the operation, asserts zero collateral bytes
/// were mutated outside the tracked writes, and verifies byte-for-byte restoration after undo.
/// </summary>
public sealed class WriteAudit
{
    public static WriteAuditResult VerifyRoundTrip(
        IProcessMemory memory,
        IReadOnlyList<AuditWindow> windows,
        Action apply,
        Action restore,
        Func<ObservedWrite, bool>? allowWrite = null)
    {
        var baselines = new Dictionary<string, byte[]>();
        foreach (var w in windows)
        {
            if (w.Address == 0 || w.Length <= 0) continue;
            var buf = new byte[w.Length];
            if (!memory.TryRead(w.Address, buf))
                return new(false, [], [$"Could not baseline window '{w.Name}' at 0x{w.Address:X}"]);
            baselines[w.Name] = buf;
        }

        var writes = new List<ObservedWrite>();
        var observer = memory as IMemoryWriteObserver;
        var prevWrite = observer?.OnWrite;
        if (observer is not null)
        {
            observer.OnWrite = (addr, before, after) =>
            {
                writes.Add(new(addr, after.Length, before, after));
                prevWrite?.Invoke(addr, before, after);
            };
        }

        var violations = new List<string>();
        try
        {
            apply();

            foreach (var w in windows)
            {
                if (!baselines.TryGetValue(w.Name, out var before)) continue;
                var current = new byte[w.Length];
                if (!memory.TryRead(w.Address, current))
                {
                    violations.Add($"Could not re-read window '{w.Name}' after apply");
                    continue;
                }
                for (int i = 0; i < w.Length; i++)
                {
                    if (current[i] == before[i]) continue;
                    ulong byteAddr = w.Address + (ulong)i;
                    bool expected = writes.Any(ow =>
                        byteAddr >= ow.Address && byteAddr < ow.Address + (ulong)ow.Length &&
                        (allowWrite is null || allowWrite(ow)));
                    if (!expected)
                        violations.Add($"Unexpected collateral mutation in '{w.Name}' at +0x{i:X} (0x{byteAddr:X}): 0x{before[i]:X2} -> 0x{current[i]:X2}");
                }
            }

            if (allowWrite is not null)
            {
                foreach (var ow in writes.Where(ow => !allowWrite(ow)))
                    violations.Add($"Disallowed write at 0x{ow.Address:X} ({ow.Length} bytes)");
            }

            restore();

            foreach (var w in windows)
            {
                if (!baselines.TryGetValue(w.Name, out var before)) continue;
                var restored = new byte[w.Length];
                if (!memory.TryRead(w.Address, restored))
                {
                    violations.Add($"Could not re-read window '{w.Name}' after restore");
                    continue;
                }
                for (int i = 0; i < w.Length; i++)
                {
                    if (restored[i] != before[i])
                    {
                        violations.Add($"Unrestored byte in '{w.Name}' at +0x{i:X}: expected 0x{before[i]:X2}, got 0x{restored[i]:X2}");
                        break;
                    }
                }
            }
        }
        finally
        {
            if (observer is not null) observer.OnWrite = prevWrite;
        }

        return new(violations.Count == 0, writes, violations);
    }
}
