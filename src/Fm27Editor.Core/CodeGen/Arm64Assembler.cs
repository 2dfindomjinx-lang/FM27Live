namespace Fm27Editor.Core.CodeGen;

/// <summary>
/// AArch64 (ARM64) register numbers and branch-condition codes. Registers are plain <c>int</c>s so they can be
/// passed straight into <see cref="Arm64Assembler"/> without an enum round-trip; the numeric values are the ones
/// the instruction encodings use (x0..x30 = 0..30, xzr/sp = 31, w0..w30 share the same numbers).
/// </summary>
public static class A64
{
    public const int X0 = 0, X1 = 1, X2 = 2, X3 = 3, X4 = 4, X5 = 5, X6 = 6, X7 = 7,
        X8 = 8, X9 = 9, X10 = 10, X11 = 11, X12 = 12, X13 = 13, X14 = 14, X15 = 15,
        X16 = 16, X17 = 17, X18 = 18, X19 = 19, X20 = 20, X21 = 21, X22 = 22, X23 = 23,
        X24 = 24, X25 = 25, X26 = 26, X27 = 27, X28 = 28, X29 = 29, X30 = 30;

    /// <summary>The zero register in data-processing instructions (x31 is the stack pointer only for addressing).</summary>
    public const int XZR = 31;

    /// <summary>The stack pointer in load/store/address instructions.</summary>
    public const int SP = 31;

    /// <summary>The link register (x30).</summary>
    public const int LR = X30;

    /// <summary>Intra-procedure-call scratch register 0 (x16); the one a far-jump trampoline may clobber.</summary>
    public const int IP0 = X16;

    /// <summary>Intra-procedure-call scratch register 1 (x17).</summary>
    public const int IP1 = X17;

    // Branch conditions (the low 4 bits of a B.cond instruction).
    public const int Eq = 0x0, Ne = 0x1, Cs = 0x2, Hs = 0x2, Cc = 0x3, Lo = 0x3,
        Mi = 0x4, Pl = 0x5, Vs = 0x6, Vc = 0x7,
        Hi = 0x8, Ls = 0x9, Ge = 0xA, Lt = 0xB, Gt = 0xC, Le = 0xD, Al = 0xE, Nv = 0xF;
}

/// <summary>
/// A small AArch64 instruction emitter. Every instruction is one 4-byte little-endian word, so the buffer is always
/// 4-byte aligned. PC-relative instructions (<see cref="B(ulong)"/>, <see cref="B(string)"/>, <see cref="Adrp"/>,
/// <see cref="LdrLiteral"/>) are computed against <see cref="Here"/> = <see cref="Origin"/> + <see cref="Position"/>,
/// exactly as the CPU sees them, and throw when the target is out of range or misaligned.
/// </summary>
/// <remarks>
/// This is the ARM64 counterpart of the x64 <c>MiniAssembler</c> in the presets. It exists so a hook trampoline or
/// code cave can be built from C# (see <see cref="Arm64CodePatch"/>) with the same confidence as the hand-written
/// x86-64 bytes, and so the emitted machine code can be verified by the unit tests without a game running.
/// </remarks>
public sealed class Arm64Assembler
{
    // Fixed instruction bit patterns. The hex constant holds the high bits of the 32-bit word; the low bits carry the
    // immediates and register fields filled in by each method below.
    const uint OpBUnconditional = 0x14000000;    // 000101 imm26
    const uint OpBLink = 0x94000000;             // 100101 imm26  (BL)
    const uint OpBConditional = 0x54000000;      // 01010100 imm19 cond
    const uint OpBr = 0xD61F0000;                // 1101011000011111000000 00000 Rn
    const uint OpBlr = 0xD63F0000;               // 1101011000111111000000 00000 Rn
    const uint OpRet = 0xD65F0000;               // BR with Rn defaulting to x30
    const uint OpNop = 0xD503201F;
    const uint OpMovz = 0xD2800000;              // 110100101 hw imm16 Rd
    const uint OpMovk = 0xF2800000;              // 111100101 hw imm16 Rd
    const uint OpAddImm = 0x91000000;            // 1001000100 shift imm12 Rn Rd (64-bit)
    const uint OpAddImm32 = 0x11000000;          // 0001000100 shift imm12 Rn Rd (32-bit)
    const uint OpSubImm32 = 0x51000000;          // 0101000100 shift imm12 Rn Rd (32-bit)
    const uint OpAddReg = 0x8B000000;            // 10001011000 Rm shift Rn Rd (64-bit)
    const uint OpLdrImm = 0xF9400000;            // 1111100101 imm12 Rn Rt (64-bit unsigned offset)
    const uint OpStrImm = 0xF9000000;            // 1111100100 imm12 Rn Rt (64-bit unsigned offset)
    const uint OpLdrLiteral = 0x58000000;        // 01011000 imm19 Rt (64-bit)
    const uint OpAdrp = 0x90000000;              // 1 immlo 10000 immhi Rd

    readonly List<byte> _code = [];
    readonly Dictionary<string, int> _labels = [];
    readonly List<(int At, string Label, bool Conditional, int Cond)> _fixups = [];

    /// <summary>Marks a conditional-branch fixup as a CBZ (which has no condition field) in <see cref="Build"/>.</summary>
    const int CbzFixup = 0x1000;

    /// <summary>The address the CPU sees as the first emitted instruction. Defaults to 0, which is fine for
    /// position-independent bodies; set it to a real address before emitting PC-relative jumps.</summary>
    public ulong Origin { get; }

    public Arm64Assembler(ulong origin = 0) => Origin = origin;

    /// <summary>Byte offset of the next instruction to be emitted (always a multiple of 4).</summary>
    public int Position => _code.Count;

    /// <summary>The address the next emitted instruction will occupy: <see cref="Origin"/> + <see cref="Position"/>.</summary>
    public ulong Here => Origin + (ulong)Position;

    /// <summary>Appends whole instruction bytes (an already encoded sequence, such as a stolen instruction).</summary>
    public Arm64Assembler Emit(ReadOnlySpan<byte> words)
    {
        if (words.Length % 4 != 0) throw new ArgumentException("Instruction bytes are a whole number of 4-byte words", nameof(words));
        foreach (byte b in words) _code.Add(b);
        return this;
    }

    /// <summary>Appends a raw 32-bit instruction word (little-endian).</summary>
    public Arm64Assembler Emit(uint instruction)
    {
        _code.Add((byte)instruction);
        _code.Add((byte)(instruction >> 8));
        _code.Add((byte)(instruction >> 16));
        _code.Add((byte)(instruction >> 24));
        return this;
    }

    /// <summary>Returns the emitted bytes once every label has been resolved.</summary>
    public byte[] Build()
    {
        foreach (var (at, label, conditional, cond) in _fixups)
        {
            if (!_labels.TryGetValue(label, out int target))
                throw new InvalidOperationException($"Unresolved label '{label}'");
            long delta = (long)target - at;
            if ((delta & 3) != 0)
                throw new InvalidOperationException($"Label '{label}' is not 4-byte aligned");
            long imm = delta >> 2;
            uint instruction;
            if (conditional)
            {
                if (imm < -(1L << 18) || imm >= 1L << 18)
                    throw new InvalidOperationException($"Label '{label}' is out of reach of a conditional branch");
                instruction = cond == CbzFixup
                    ? 0x34000000u | ((uint)(_code[at + 3] & 0x80) << 24) | ((uint)(imm & 0x7FFFF) << 5) | (uint)(_code[at] & 0x1F)
                    : OpBConditional | ((uint)(imm & 0x7FFFF) << 5) | (uint)cond;
            }
            else
            {
                if (imm < -(1L << 25) || imm >= 1L << 25)
                    throw new InvalidOperationException($"Label '{label}' is out of reach of an unconditional branch");
                instruction = OpBUnconditional | ((uint)(imm & 0x03FFFFFF));
            }
            Write(at, instruction);
        }
        return _code.ToArray();
    }

    void Write(int offset, uint instruction)
    {
        _code[offset] = (byte)instruction;
        _code[offset + 1] = (byte)(instruction >> 8);
        _code[offset + 2] = (byte)(instruction >> 16);
        _code[offset + 3] = (byte)(instruction >> 24);
    }

    // ----- labels -----

    /// <summary>Records the current position under <paramref name="name"/> for a later <see cref="B(string)"/>.</summary>
    public Arm64Assembler Label(string name)
    {
        _labels[name] = Position;
        return this;
    }

    /// <summary>Unconditional branch to a label, resolved by <see cref="Build"/>.</summary>
    public Arm64Assembler B(string label)
    {
        _fixups.Add((Position, label, false, 0));
        return Emit(OpBUnconditional);
    }

    /// <summary>Conditional branch to a label, resolved by <see cref="Build"/>.</summary>
    public Arm64Assembler BCond(int cond, string label)
    {
        _fixups.Add((Position, label, true, cond));
        return Emit(OpBConditional | (uint)cond);
    }

    // ----- branches to absolute addresses -----

    /// <summary><c>B target</c>; ±128 MB. The whole point of <see cref="ICodePatchHost.AllocateNear"/> is to keep the cave in range.</summary>
    public Arm64Assembler B(ulong target) => Emit(OpBUnconditional | BranchImm26(target));

    /// <summary><c>BL target</c>; ±128 MB.</summary>
    public Arm64Assembler Bl(ulong target) => Emit(OpBLink | BranchImm26(target));

    /// <summary><c>B.cond target</c>; ±1 MB.</summary>
    public Arm64Assembler BCond(int cond, ulong target)
    {
        long delta = (long)target - (long)Here;
        if ((delta & 3) != 0) throw new InvalidOperationException($"Target 0x{target:X} is not 4-byte aligned");
        long imm = delta >> 2;
        if (imm < -(1L << 18) || imm >= 1L << 18)
            throw new InvalidOperationException($"Target 0x{target:X} is out of reach of a conditional branch");
        return Emit(OpBConditional | ((uint)(imm & 0x7FFFF) << 5) | (uint)cond);
    }

    uint BranchImm26(ulong target)
    {
        long delta = (long)target - (long)Here;
        if ((delta & 3) != 0) throw new InvalidOperationException($"Target 0x{target:X} is not 4-byte aligned");
        long imm = delta >> 2;
        if (imm < -(1L << 25) || imm >= 1L << 25)
            throw new InvalidOperationException($"Target 0x{target:X} is out of reach of an unconditional branch (±128 MB)");
        return (uint)(imm & 0x03FFFFFF);
    }

    // ----- register / immediate moves -----

    public Arm64Assembler Br(int rn) => Emit(OpBr | ((uint)rn << 5));

    public Arm64Assembler Blr(int rn) => Emit(OpBlr | ((uint)rn << 5));

    public Arm64Assembler Ret(int rn = A64.LR) => Emit(OpRet | ((uint)rn << 5));

    public Arm64Assembler Nop() => Emit(OpNop);

    /// <summary><c>MOVZ rd, #imm16, LSL #shift</c>; shift is 0, 16, 32 or 48. Zeroes the whole register first.</summary>
    public Arm64Assembler Movz(int rd, int imm16, int shift = 0)
    {
        CheckImm16(imm16);
        return Emit(OpMovz | ((uint)(shift / 16) << 21) | ((uint)imm16 << 5) | (uint)rd);
    }

    /// <summary><c>MOVK rd, #imm16, LSL #shift</c>; keeps the other 16-bit lanes.</summary>
    public Arm64Assembler Movk(int rd, int imm16, int shift = 0)
    {
        CheckImm16(imm16);
        return Emit(OpMovk | ((uint)(shift / 16) << 21) | ((uint)imm16 << 5) | (uint)rd);
    }

    /// <summary>Materialises a full 64-bit immediate in <paramref name="rd"/>: MOVZ for the low lane, then one MOVK per
    /// non-zero higher lane. A macOS user-space address (top lane zero) takes 12 bytes; a small offset takes 4.</summary>
    public Arm64Assembler MovAbsolute(int rd, ulong value)
    {
        Movz(rd, (int)(value & 0xFFFF));
        for (int shift = 16; shift < 64; shift += 16)
        {
            int lane = (int)((value >> shift) & 0xFFFF);
            if (lane != 0) Movk(rd, lane, shift);
        }
        return this;
    }

    static void CheckImm16(int imm16)
    {
        if (imm16 is < 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(imm16));
    }

    // ----- arithmetic -----

    /// <summary><c>ADD rd, rn, #imm12</c> (64-bit, unsigned 12-bit immediate).</summary>
    public Arm64Assembler AddImm(int rd, int rn, int imm12)
    {
        if (imm12 is < 0 or > 0xFFF) throw new ArgumentOutOfRangeException(nameof(imm12));
        return Emit(OpAddImm | ((uint)imm12 << 10) | ((uint)rn << 5) | (uint)rd);
    }

    /// <summary><c>ADD rd, rn, rm</c> (64-bit).</summary>
    public Arm64Assembler AddReg(int rd, int rn, int rm) =>
        Emit(OpAddReg | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd);

    /// <summary><c>ADD rd, rn, rm, LSL #shift</c> (64-bit): a register added after a left shift, e.g. indexing a
    /// table of <c>2^shift</c>-byte records.</summary>
    public Arm64Assembler AddRegLsl(int rd, int rn, int rm, int shift)
    {
        if (shift is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(shift));
        return Emit(OpAddReg | ((uint)shift << 10) | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd);
    }

    /// <summary><c>ADD wd, wn, #imm12</c> (32-bit, no flags).</summary>
    public Arm64Assembler AddImm32(int rd, int rn, int imm12)
    {
        CheckImm12(imm12);
        return Emit(OpAddImm32 | ((uint)imm12 << 10) | ((uint)rn << 5) | (uint)rd);
    }

    /// <summary><c>SUB wd, wn, #imm12</c> (32-bit, no flags).</summary>
    public Arm64Assembler SubImm32(int rd, int rn, int imm12)
    {
        CheckImm12(imm12);
        return Emit(OpSubImm32 | ((uint)imm12 << 10) | ((uint)rn << 5) | (uint)rd);
    }

    static void CheckImm12(int imm12)
    {
        if (imm12 is < 0 or > 0xFFF) throw new ArgumentOutOfRangeException(nameof(imm12));
    }

    /// <summary><c>MOV wd, wm</c> (32-bit register move, an ORR against the zero register).</summary>
    public Arm64Assembler MovReg(int rd, int rm) =>
        Emit(0x2A0003E0 | ((uint)rm << 16) | (uint)rd);

    /// <summary><c>ADD wd, wn, wm</c> (32-bit).</summary>
    public Arm64Assembler AddReg32(int rd, int rn, int rm) =>
        Emit(0x0B000000 | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd);

    /// <summary><c>SUB wd, wn, wm</c> (32-bit).</summary>
    public Arm64Assembler SubReg32(int rd, int rn, int rm) =>
        Emit(0x4B000000 | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd);

    /// <summary><c>CMP wn, wm</c>: SUBS with the zero register as destination.</summary>
    public Arm64Assembler CmpReg(int rn, int rm) =>
        Emit(0x6B00001F | ((uint)rm << 16) | ((uint)rn << 5));

    /// <summary><c>CMP wn, #imm12</c>.</summary>
    public Arm64Assembler CmpImm32(int rn, int imm12)
    {
        CheckImm12(imm12);
        return Emit(0x7100001F | ((uint)imm12 << 10) | ((uint)rn << 5));
    }

    /// <summary><c>MUL wd, wn, wm</c> (32-bit, MADD against the zero register).</summary>
    public Arm64Assembler Mul32(int rd, int rn, int rm) =>
        Emit(0x1B000000 | ((uint)rm << 16) | ((uint)A64.XZR << 10) | ((uint)rn << 5) | (uint)rd);

    /// <summary><c>UDIV wd, wn, wm</c> (32-bit unsigned divide); divide by zero yields zero.</summary>
    public Arm64Assembler Udiv32(int rd, int rn, int rm) =>
        Emit(0x1AC00800 | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd);

    /// <summary><c>AND wd, wn, #(2^bits - 1)</c>, for a low-bit mask of 1 to 31 bits.</summary>
    public Arm64Assembler AndLowBits(int rd, int rn, int bits)
    {
        if (bits is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(bits));
        return Emit(0x12000000 | ((uint)(bits - 1) << 10) | ((uint)rn << 5) | (uint)rd);
    }

    /// <summary>Reads the virtual counter into <paramref name="rt"/>; user-space reading is always allowed on macOS and
    /// it is the cheap, unpredictable source a cave uses for its random draws.</summary>
    public Arm64Assembler MrsVirtualCount(int rt) => Emit(0xD53BE040 | (uint)rt);

    /// <summary>Reads the condition flags into <paramref name="rt"/>, for a cave that must leave them as it found them.</summary>
    public Arm64Assembler MrsNzcv(int rt) => Emit(0xD53B4200 | (uint)rt);

    /// <summary>Writes the condition flags back from <paramref name="rt"/>.</summary>
    public Arm64Assembler MsrNzcv(int rt) => Emit(0xD51B4200 | (uint)rt);

    // ----- loads / stores -----

    /// <summary><c>LDR xt, [xn, #offset]</c>; offset is a non-negative byte offset that must be a multiple of 8 and below 32768.</summary>
    public Arm64Assembler LdrImm(int rt, int rn, int offset)
    {
        CheckLdStOffset(offset);
        return Emit(OpLdrImm | ((uint)(offset / 8) << 10) | ((uint)rn << 5) | (uint)rt);
    }

    /// <summary><c>STR xt, [xn, #offset]</c>; same offset rules as <see cref="LdrImm"/>.</summary>
    public Arm64Assembler StrImm(int rt, int rn, int offset)
    {
        CheckLdStOffset(offset);
        return Emit(OpStrImm | ((uint)(offset / 8) << 10) | ((uint)rn << 5) | (uint)rt);
    }

    static void CheckLdStOffset(int offset)
    {
        if (offset < 0 || offset >= 32768 || (offset & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be a non-negative multiple of 8 below 32768");
    }

    /// <summary><c>LDR wt, [xn, #offset]</c> (32-bit); offset a multiple of 4.</summary>
    public Arm64Assembler LdrWord(int rt, int rn, int offset)
    {
        CheckWordOffset(offset);
        return Emit(0xB9400000 | ((uint)(offset / 4) << 10) | ((uint)rn << 5) | (uint)rt);
    }

    /// <summary><c>STR wt, [xn, #offset]</c> (32-bit); offset a multiple of 4.</summary>
    public Arm64Assembler StrWord(int rt, int rn, int offset)
    {
        CheckWordOffset(offset);
        return Emit(0xB9000000 | ((uint)(offset / 4) << 10) | ((uint)rn << 5) | (uint)rt);
    }

    static void CheckWordOffset(int offset)
    {
        if (offset < 0 || offset >= 16384 || (offset & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be a non-negative multiple of 4 below 16384");
    }

    /// <summary><c>LDRH wt, [xn, #offset]</c> (16-bit, zero-extending); offset a multiple of 2.</summary>
    public Arm64Assembler Ldrh(int rt, int rn, int offset)
    {
        CheckHalfOffset(offset);
        return Emit(0x79400000 | ((uint)(offset / 2) << 10) | ((uint)rn << 5) | (uint)rt);
    }

    /// <summary><c>STRH wt, [xn, #offset]</c> (16-bit); offset a multiple of 2.</summary>
    public Arm64Assembler Strh(int rt, int rn, int offset)
    {
        CheckHalfOffset(offset);
        return Emit(0x79000000 | ((uint)(offset / 2) << 10) | ((uint)rn << 5) | (uint)rt);
    }

    /// <summary><c>LDRSH wt, [xn, #offset]</c> (16-bit, sign-extending).</summary>
    public Arm64Assembler Ldrsh(int rt, int rn, int offset)
    {
        CheckHalfOffset(offset);
        return Emit(0x79C00000 | ((uint)(offset / 2) << 10) | ((uint)rn << 5) | (uint)rt);
    }

    static void CheckHalfOffset(int offset)
    {
        if (offset < 0 || offset >= 8192 || (offset & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be a non-negative multiple of 2 below 8192");
    }

    /// <summary><c>LDRB wt, [xn, #offset]</c> (byte, zero-extending).</summary>
    public Arm64Assembler Ldrb(int rt, int rn, int offset)
    {
        CheckByteOffset(offset);
        return Emit(0x39400000 | ((uint)offset << 10) | ((uint)rn << 5) | (uint)rt);
    }

    /// <summary><c>STRB wt, [xn, #offset]</c> (byte).</summary>
    public Arm64Assembler Strb(int rt, int rn, int offset)
    {
        CheckByteOffset(offset);
        return Emit(0x39000000 | ((uint)offset << 10) | ((uint)rn << 5) | (uint)rt);
    }

    static void CheckByteOffset(int offset)
    {
        if (offset is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(offset), "offset must be 0-4095");
    }

    /// <summary><c>STR xt, [xn, xm, LSL #3]</c> (64-bit, index scaled by 8: opc=00, option=011, S=1).</summary>
    public Arm64Assembler StrRegScaled8(int rt, int rn, int rm) =>
        Emit(0xF8207800 | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rt);

    /// <summary><c>STP xt1, xt2, [xn, #-bytes]!</c>: stores, then moves the stack pointer down by <paramref name="bytes"/>.</summary>
    public Arm64Assembler StpPre(int rt1, int rt2, int rn, int bytes)
    {
        int imm7 = CheckPairOffset(bytes);
        return Emit(0xA9800000 | ((uint)(imm7 & 0x7F) << 15) | ((uint)rt2 << 10) | ((uint)rn << 5) | (uint)rt1);
    }

    /// <summary><c>STP xt1, xt2, [xn, #bytes]</c>: stores at a non-negative offset.</summary>
    public Arm64Assembler Stp(int rt1, int rt2, int rn, int bytes)
    {
        int imm7 = CheckPairOffset(bytes);
        return Emit(0xA9000000 | ((uint)(imm7 & 0x7F) << 15) | ((uint)rt2 << 10) | ((uint)rn << 5) | (uint)rt1);
    }

    /// <summary><c>LDP xt1, xt2, [xn, #bytes]</c>: loads from a non-negative offset.</summary>
    public Arm64Assembler Ldp(int rt1, int rt2, int rn, int bytes)
    {
        int imm7 = CheckPairOffset(bytes);
        return Emit(0xA9400000 | ((uint)(imm7 & 0x7F) << 15) | ((uint)rt2 << 10) | ((uint)rn << 5) | (uint)rt1);
    }

    /// <summary><c>LDP xt1, xt2, [xn], #bytes</c>: loads, then moves the stack pointer up.</summary>
    public Arm64Assembler LdpPost(int rt1, int rt2, int rn, int bytes)
    {
        int imm7 = CheckPairOffset(bytes);
        return Emit(0xA8C00000 | ((uint)(imm7 & 0x7F) << 15) | ((uint)rt2 << 10) | ((uint)rn << 5) | (uint)rt1);
    }

    static int CheckPairOffset(int bytes)
    {
        if (bytes < -512 || bytes > 504 || (bytes & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes), "a pair offset is a multiple of 8 within -512..504");
        return bytes / 8;
    }

    /// <summary><c>CBZ wt, label</c> (32-bit): a branch taken when the register is zero, resolved by <see cref="Build"/>.</summary>
    public Arm64Assembler Cbz(int rt, string label)
    {
        _fixups.Add((Position, label, true, CbzFixup));
        return Emit(0x34000000 | (uint)rt);
    }

    /// <summary><c>CBZ xt, label</c> (64-bit): a branch taken when the full 64-bit register is zero, resolved by <see cref="Build"/>.</summary>
    public Arm64Assembler Cbz64(int rt, string label)
    {
        _fixups.Add((Position, label, true, CbzFixup));
        return Emit(0xB4000000 | (uint)rt);
    }

    /// <summary><c>LDR xt, #target</c> (PC-relative literal, ±1 MB).</summary>
    public Arm64Assembler LdrLiteral(int rt, ulong target)
    {
        long delta = (long)target - (long)Here;
        if ((delta & 3) != 0) throw new InvalidOperationException($"Target 0x{target:X} is not 4-byte aligned");
        long imm = delta >> 2;
        if (imm < -(1L << 18) || imm >= 1L << 18)
            throw new InvalidOperationException($"Target 0x{target:X} is out of reach of a literal load (±1 MB)");
        return Emit(OpLdrLiteral | ((uint)(imm & 0x7FFFF) << 5) | (uint)rt);
    }

    /// <summary><c>ADRP rd, #target_page</c>; loads the page-aligned base of <paramref name="target"/> relative to this
    /// instruction's page, ±4 GB. Pair with <see cref="AddImm"/> for the low 12 bits.</summary>
    public Arm64Assembler Adrp(int rd, ulong target)
    {
        long pcPage = (long)(Here & ~0xFFFUL);
        long targetPage = (long)(target & ~0xFFFUL);
        long pages = (targetPage - pcPage) >> 12;
        if (pages < -(1L << 20) || pages >= 1L << 20)
            throw new InvalidOperationException($"Target 0x{target:X} is out of reach of ADRP (±4 GB)");
        int imm = (int)pages;
        return Emit(OpAdrp | ((uint)(imm & 3) << 29) | ((uint)((imm >> 2) & 0x7FFFF) << 5) | (uint)rd);
    }
}
