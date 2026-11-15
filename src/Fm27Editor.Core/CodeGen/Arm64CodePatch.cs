namespace Fm27Editor.Core.CodeGen;

/// <summary>
/// ARM64 hook primitives: the machine code that replaces a few stolen instructions at a hook site with a jump into a
/// code cave, and the pieces a code cave needs to reach back out to absolute addresses. These are the AArch64
/// equivalents of the x86-64 <c>BuildPatch</c>/<c>BuildRoutine</c> in the presets, and the building blocks the
/// <c>AcceleratedDevelopment</c> and <c>GoldenGenerationHook</c> features use on osx-arm64.
/// </summary>
public static class Arm64CodePatch
{
    /// <summary>Negative reach of <c>B</c>: -2^27 bytes; the positive limit is 2^27 - 4 (signed imm26 words).</summary>
    public const long BranchReach = 1L << 27;

    /// <summary>True when a <c>B</c> from <paramref name="from"/> reaches <paramref name="to"/> without a register.</summary>
    public static bool IsBranchReachable(ulong from, ulong to)
    {
        if (((from | to) & 3) != 0) return false;
        return to >= from ? to - from < (ulong)BranchReach : from - to <= (ulong)BranchReach;
    }

    /// <summary>
    /// A jump from <paramref name="from"/> to <paramref name="to"/>: a single <c>B</c> when in range (4 bytes, clobbers
    /// nothing), otherwise an absolute jump that materialises the address in x16 (IP0) and <c>BR</c>s to it. x16 is the
    /// intra-procedure-call scratch register, so the far form is safe at any point a compiler would not keep a live value
    /// in x16 across a call.
    /// </summary>
    public static byte[] BuildJump(ulong from, ulong to)
    {
        RequireAligned(from, to);
        var a = new Arm64Assembler(from);
        if (IsBranchReachable(from, to)) return a.B(to).Build();
        a.MovAbsolute(A64.IP0, to);
        a.Br(A64.IP0);
        return a.Build();
    }

    /// <summary>
    /// The same absolute jump as the far branch of <see cref="BuildJump"/>, always: MOVZ/MOVK into x16 then <c>BR x16</c>.
    /// Used by tests to exercise the distance-independent path, and by callers that know the target is far.
    /// </summary>
    public static byte[] BuildFarJump(ulong from, ulong to)
    {
        RequireAligned(from, to);
        var a = new Arm64Assembler(from);
        a.MovAbsolute(A64.IP0, to);
        a.Br(A64.IP0);
        return a.Build();
    }

    /// <summary>
    /// The bytes that overwrite <paramref name="stolenLength"/> bytes at <paramref name="from"/>: a <c>B</c> to
    /// <paramref name="to"/> followed by <c>NOP</c>s to fill the slot. ARM64 instructions cannot be split, so the stolen
    /// region must be a whole number of 4-byte instructions. The cave is allocated near the site, so the jump is a
    /// plain <c>B</c>; a site more than ±128 MB away needs <see cref="BuildJump"/> and that many stolen bytes instead.
    /// </summary>
    public static byte[] BuildPatch(ulong from, ulong to, int stolenLength)
    {
        if (stolenLength < 4 || stolenLength % 4 != 0)
            throw new ArgumentException("A stolen region on ARM64 is a whole number of 4-byte instructions", nameof(stolenLength));
        RequireAligned(from, to);
        var a = new Arm64Assembler(from);
        a.B(to);                                    // throws when out of reach of B
        while (a.Position < stolenLength) a.Nop();
        return a.Build();
    }

    static void RequireAligned(ulong from, ulong to)
    {
        if (((from | to) & 3) != 0)
            throw new InvalidOperationException("ARM64 instruction addresses must be 4-byte aligned");
    }

    /// <summary>
    /// <c>ADRP rd, target; ADD rd, rd, #lo12</c>: materialises <paramref name="target"/> in <paramref name="rd"/> using
    /// only PC-relative addressing (±4 GB, position-independent). A code cave uses this the way the x86-64 routine uses
    /// <c>lea rcx, [rip+block]</c> — to point a register at its own header/table without embedding an absolute address.
    /// </summary>
    public static byte[] LoadAddress(int rd, ulong target, ulong from = 0)
    {
        var a = new Arm64Assembler(from);
        a.Adrp(rd, target);
        a.AddImm(rd, rd, (int)(target & 0xFFF));
        return a.Build();
    }
}
