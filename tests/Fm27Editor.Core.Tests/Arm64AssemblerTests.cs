using System.Runtime.InteropServices;
using Fm27Editor.Core.CodeGen;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// The AArch64 instruction/trampoline builder, verified two ways: the encoding tests compare emitted words against
/// hand-computed values (the expected instruction is written in the test, not read back from the code under test),
/// and the native tests actually run the emitted machine code on Apple Silicon.
/// </summary>
public class Arm64AssemblerTests
{
    static byte[] Enc(uint instruction) =>
        [(byte)instruction, (byte)(instruction >> 8), (byte)(instruction >> 16), (byte)(instruction >> 24)];

    // ----- branch encoding -----

    [Fact]
    public void B_to_itself_is_an_infinite_loop()
    {
        Assert.Equal(Enc(0x14000000), new Arm64Assembler(0x1000).B(0x1000).Build());
    }

    [Fact]
    public void B_forward_adds_the_word_delta()
    {
        // 0x1000 -> 0x2000: delta 0x1000 bytes = 0x400 words.
        Assert.Equal(Enc(0x14000400), new Arm64Assembler(0x1000).B(0x2000).Build());
    }

    [Fact]
    public void B_backward_sign_extends_the_26_bit_immediate()
    {
        // 0x1000 -> 0x900: delta -0x700 bytes = -0x1C0 words = 0x03FFFE40.
        Assert.Equal(Enc(0x17FFFE40), new Arm64Assembler(0x1000).B(0x900).Build());
    }

    [Fact]
    public void B_refuses_an_unaligned_target()
    {
        Assert.Throws<InvalidOperationException>(() => new Arm64Assembler(0x1000).B(0x1002));
    }

    [Fact]
    public void B_refuses_a_target_beyond_128MB()
    {
        Assert.Throws<InvalidOperationException>(() => new Arm64Assembler(0x1000).B(0x1000 + (1UL << 27) + 4));
    }

    [Fact]
    public void BCond_sign_extends_the_19_bit_immediate()
    {
        // B.eq from 0x1000 to 0x1010: delta 0x10 bytes = 4 words.
        Assert.Equal(Enc(0x54000080), new Arm64Assembler(0x1000).BCond(A64.Eq, 0x1010).Build());
    }

    [Fact]
    public void Br_places_the_register_in_bits_9_through_5()
    {
        Assert.Equal(Enc(0xD61F0200), new Arm64Assembler().Br(A64.X16).Build());   // br x16
    }

    [Fact]
    public void Ret_defaults_to_the_link_register()
    {
        Assert.Equal(Enc(0xD65F03C0), new Arm64Assembler().Ret().Build());          // ret (x30)
    }

    // ----- moves -----

    [Fact]
    public void Movz_zeroes_the_register_and_sets_the_low_lane()
    {
        Assert.Equal(Enc(0xD2824690), new Arm64Assembler().Movz(A64.X16, 0x1234).Build());
    }

    [Fact]
    public void Movk_sets_a_higher_lane_without_touching_the_low_one()
    {
        Assert.Equal(Enc(0xF2AACF10), new Arm64Assembler().Movk(A64.X16, 0x5678, shift: 16).Build());
    }

    [Fact]
    public void MovAbsolute_skips_zero_lanes()
    {
        // 0x12345678 has non-zero lanes 0x5678 and 0x1234; the top two zero lanes are skipped.
        Assert.Equal(
            [.. Enc(0xD28ACF10), .. Enc(0xF2A24690)],
            new Arm64Assembler().MovAbsolute(A64.X16, 0x12345678UL).Build());
    }

    [Fact]
    public void Nop_is_the_canonical_hint()
    {
        Assert.Equal(Enc(0xD503201F), new Arm64Assembler().Nop().Build());
    }

    // ----- address materialisation -----

    [Fact]
    public void Adrp_encodes_the_page_delta()
    {
        // From page 0 to target page 0x1000: one page up, low bit of the immediate sits in bit 29.
        Assert.Equal(Enc(0xB0000009), new Arm64Assembler(0).Adrp(A64.X9, 0x1000).Build());
    }

    [Fact]
    public void AddImm_encodes_a_12_bit_immediate()
    {
        Assert.Equal(Enc(0x9108D129), new Arm64Assembler().AddImm(A64.X9, A64.X9, 0x234).Build());
    }

    [Fact]
    public void LdrImm_reads_through_a_register()
    {
        Assert.Equal(Enc(0xF9400120), new Arm64Assembler().LdrImm(A64.X0, A64.X9, offset: 0).Build());
    }

    // ----- arithmetic (the forms the presets' routines use) -----

    [Fact]
    public void AddReg_encodes_three_registers()
    {
        Assert.Equal(Enc(0x8B020020), new Arm64Assembler().AddReg(A64.X0, A64.X1, A64.X2).Build());   // add x0, x1, x2
    }

    [Fact]
    public void AddRegLsl_puts_the_shift_in_bits_15_through_10()
    {
        // add x0, x1, x2, lsl #4 — the shift scales an index by a record size.
        Assert.Equal(Enc(0x8B021020), new Arm64Assembler().AddRegLsl(A64.X0, A64.X1, A64.X2, 4).Build());
    }

    [Fact]
    public void AddRegLsl_refuses_a_shift_above_63()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arm64Assembler().AddRegLsl(A64.X0, A64.X1, A64.X2, 64));
    }

    [Fact]
    public void SubReg32_encodes_the_gain_subtraction()
    {
        // sub w22, w22, w8 — what the accelerate site's stolen instruction sits after.
        Assert.Equal(Enc(0x4B0802D6), new Arm64Assembler().SubReg32(A64.X22, A64.X22, A64.X8).Build());
    }

    [Fact]
    public void AddReg32_and_MovReg_are_the_32_bit_forms()
    {
        Assert.Equal(Enc(0x0B020020), new Arm64Assembler().AddReg32(A64.X0, A64.X1, A64.X2).Build());  // add w0, w1, w2
        Assert.Equal(Enc(0x2A0803E8), new Arm64Assembler().MovReg(A64.X8, A64.X8).Build());            // mov w8, w8
    }

    [Fact]
    public void CmpReg_sets_the_flags_from_a_32_bit_subtraction()
    {
        Assert.Equal(Enc(0x6B09011F), new Arm64Assembler().CmpReg(A64.X8, A64.X9).Build());            // cmp w8, w9
    }

    [Fact]
    public void CmpImm32_encodes_the_cap()
    {
        Assert.Equal(Enc(0x7103211F), new Arm64Assembler().CmpImm32(A64.X8, 200).Build());             // cmp w8, #200
    }

    [Fact]
    public void Mul32_is_a_madd_against_the_zero_register()
    {
        Assert.Equal(Enc(0x1B157ED4), new Arm64Assembler().Mul32(A64.X20, A64.X22, A64.X21).Build());  // mul w20, w22, w21
    }

    [Fact]
    public void Udiv32_encodes_an_unsigned_divide()
    {
        Assert.Equal(Enc(0x1AC80A94), new Arm64Assembler().Udiv32(A64.X20, A64.X20, A64.X8).Build());  // udiv w20, w20, w8
    }

    [Fact]
    public void AndLowBits_encodes_a_low_mask_from_the_bit_count()
    {
        // RingSize 512 is 9 bits: and w8, w8, #0x1ff — the ring index wrap.
        Assert.Equal(Enc(0x12002108), new Arm64Assembler().AndLowBits(A64.X8, A64.X8, 9).Build());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void AndLowBits_refuses_a_bit_count_outside_1_to_31(int bits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arm64Assembler().AndLowBits(A64.X8, A64.X8, bits));
    }

    [Fact]
    public void The_flag_and_counter_reads_use_the_system_register_encodings()
    {
        Assert.Equal(Enc(0xD53BE040), new Arm64Assembler().MrsVirtualCount(A64.X0).Build());   // mrs x0, cntvct_el0
        Assert.Equal(Enc(0xD53B4201), new Arm64Assembler().MrsNzcv(A64.X1).Build());           // mrs x1, nzcv
        Assert.Equal(Enc(0xD51B4201), new Arm64Assembler().MsrNzcv(A64.X1).Build());           // msr nzcv, x1
    }

    // ----- 32, 16 and 8-bit loads and stores -----

    [Fact]
    public void Word_loads_and_stores_scale_the_offset_by_four()
    {
        Assert.Equal(Enc(0xB9425E69), new Arm64Assembler().LdrWord(A64.X9, A64.X19, 0x25C).Build());  // ldr w9, [x19, #0x25c]
        Assert.Equal(Enc(0xB9026676), new Arm64Assembler().StrWord(A64.X22, A64.X19, 0x264).Build()); // str w22, [x19, #0x264]
    }

    [Fact]
    public void Halfword_loads_scale_the_offset_by_two_and_Ldrsh_sign_extends()
    {
        Assert.Equal(Enc(0x7944BAF6), new Arm64Assembler().Ldrh(A64.X22, A64.X23, 0x25C).Build());   // ldrh w22, [x23, #0x25c]
        Assert.Equal(Enc(0x7904BAF6), new Arm64Assembler().Strh(A64.X22, A64.X23, 0x25C).Build());   // strh w22, [x23, #0x25c]
        Assert.Equal(Enc(0x79C4BE69), new Arm64Assembler().Ldrsh(A64.X9, A64.X19, 0x25E).Build());   // ldrsh w9, [x19, #0x25e]
    }

    [Fact]
    public void Byte_loads_and_stores_keep_the_offset_unscaled()
    {
        Assert.Equal(Enc(0x396002A8), new Arm64Assembler().Ldrb(A64.X8, A64.X21, 0x800).Build());    // ldrb w8, [x21, #0x800]
        Assert.Equal(Enc(0x39098EA8), new Arm64Assembler().Strb(A64.X8, A64.X21, 0x263).Build());    // strb w8, [x21, #0x263]
    }

    [Fact]
    public void A_byte_offset_must_stay_below_4096()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arm64Assembler().Ldrb(A64.X8, A64.X21, 0x1000));
    }

    [Fact]
    public void A_halfword_offset_must_be_even()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arm64Assembler().Ldrh(A64.X8, A64.X21, 3));
    }

    [Fact]
    public void A_word_offset_must_be_a_multiple_of_four()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arm64Assembler().LdrWord(A64.X8, A64.X21, 6));
    }

    [Fact]
    public void StrRegScaled8_indexes_a_qword_table()
    {
        // str x20, [x19, x16, lsl #3] — the accelerate routine's ring slot.
        Assert.Equal(Enc(0xF8307A74), new Arm64Assembler().StrRegScaled8(A64.X20, A64.X19, A64.X16).Build());
    }

    // ----- stack pairs (the routine prologue and epilogue) -----

    [Fact]
    public void StpPre_stores_then_moves_the_stack_pointer_down()
    {
        // stp x0, x1, [sp, #-0x40]! — a negative pair offset is stored two's complement in the seven immediate bits.
        Assert.Equal(Enc(0xA9BC07E0), new Arm64Assembler().StpPre(A64.X0, A64.X1, A64.SP, -0x40).Build());
    }

    [Fact]
    public void Stp_and_Ldp_use_a_non_negative_pair_offset()
    {
        Assert.Equal(Enc(0xA9010660), new Arm64Assembler().Stp(A64.X0, A64.X1, A64.X19, 0x10).Build());  // stp x0, x1, [x19, #0x10]
        Assert.Equal(Enc(0xA9410660), new Arm64Assembler().Ldp(A64.X0, A64.X1, A64.X19, 0x10).Build());  // ldp x0, x1, [x19, #0x10]
    }

    [Fact]
    public void LdpPost_loads_then_moves_the_stack_pointer_up()
    {
        Assert.Equal(Enc(0xA8C57BFD), new Arm64Assembler().LdpPost(A64.X29, A64.X30, A64.SP, 0x50).Build());  // ldp x29, x30, [sp], #0x50
    }

    [Fact]
    public void A_pair_offset_must_be_a_multiple_of_eight_within_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arm64Assembler().Stp(A64.X0, A64.X1, A64.SP, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arm64Assembler().StpPre(A64.X0, A64.X1, A64.SP, -0x208));
    }

    [Fact]
    public void Cbz_branches_to_a_label_when_the_register_is_zero()
    {
        var a = new Arm64Assembler(0x1000);
        a.Cbz(A64.X0, "here");       // 0x00: the label is the next word, so the immediate is 1 (llvm-mc: cbz w0, #4)
        a.Label("here");
        Assert.Equal(Enc(0x34000020), a.Build());
    }

    [Fact]
    public void Cbz64_branches_to_a_label_when_the_64bit_register_is_zero()
    {
        var a = new Arm64Assembler(0x1000);
        a.Cbz64(A64.X0, "here");     // 0x00: cbz x0, #4 preserves sf bit 31
        a.Label("here");
        Assert.Equal(Enc(0xB4000020), a.Build());
    }

    [Fact]
    public void Emit_appends_whole_instructions_only()
    {
        Assert.Equal(Enc(0x14000000), new Arm64Assembler().Emit(Enc(0x14000000)).Build());
        Assert.Throws<ArgumentException>(() => new Arm64Assembler().Emit([0x00, 0x00, 0x00]));
    }

    // ----- label resolution (code-cave flow) -----

    [Fact]
    public void Labels_resolve_a_forward_branch()
    {
        var a = new Arm64Assembler(0x1000);
        a.B("skip");                 // 0x00: jump over the next instruction
        a.Movz(A64.X0, 0x1111);      // 0x04
        a.Label("skip");             // 0x08
        a.Movz(A64.X0, 0x2222);      // 0x08
        Assert.Equal(
            [.. Enc(0x14000002), .. Enc(0xD2822220), .. Enc(0xD2844440)],
            a.Build());
    }

    [Fact]
    public void Labels_resolve_a_backward_conditional_branch()
    {
        var a = new Arm64Assembler(0x1000);
        a.Label("loop");             // 0x00
        a.Movz(A64.X0, 1);           // 0x00
        a.BCond(A64.Eq, "loop");     // 0x04: -1 word back = 0x7FFFF
        Assert.Equal([.. Enc(0xD2800020), .. Enc(0x54FFFFE0)], a.Build());
    }

    [Fact]
    public void An_unresolved_label_fails_the_build()
    {
        var a = new Arm64Assembler();
        a.B("nowhere");
        Assert.Throws<InvalidOperationException>(() => a.Build());
    }

    // ----- trampoline helpers -----

    [Fact]
    public void BuildJump_in_reach_is_a_single_B()
    {
        Assert.Equal(Enc(0x14000400), Arm64CodePatch.BuildJump(0x1000, 0x2000));
    }

    [Fact]
    public void BuildJump_beyond_128MB_goes_through_x16()
    {
        Assert.Equal(
            Arm64CodePatch.BuildFarJump(0x1000, 0x10001000),
            Arm64CodePatch.BuildJump(0x1000, 0x10001000));   // far form is distance-independent
    }

    [Fact]
    public void BuildFarJump_materialises_the_address_then_branches()
    {
        // 0x0000000112345678: lanes 0x5678, 0x1234, 0x0001 (top lane zero), then br x16.
        Assert.Equal(
            [.. Enc(0xD28ACF10), .. Enc(0xF2A24690), .. Enc(0xF2C00030), .. Enc(0xD61F0200)],
            Arm64CodePatch.BuildFarJump(0, 0x0000000112345678UL));
    }

    [Fact]
    public void BuildPatch_is_a_B_padded_with_NOPs_to_the_stolen_length()
    {
        Assert.Equal([.. Enc(0x14000400), .. Enc(0xD503201F)], Arm64CodePatch.BuildPatch(0x1000, 0x2000, 8));
    }

    [Fact]
    public void BuildPatch_refuses_a_stolen_length_that_is_not_whole_instructions()
    {
        Assert.Throws<ArgumentException>(() => Arm64CodePatch.BuildPatch(0x1000, 0x2000, 7));
    }

    [Fact]
    public void BuildPatch_refuses_a_target_beyond_B_reach()
    {
        Assert.Throws<InvalidOperationException>(() => Arm64CodePatch.BuildPatch(0x1000, 0x1000 + (1UL << 27) + 4, 4));
    }

    [Fact]
    public void IsBranchReachable_is_the_B_imm26_range()
    {
        Assert.True(Arm64CodePatch.IsBranchReachable(0x1000, 0x1000 + (1UL << 27) - 4));
        Assert.False(Arm64CodePatch.IsBranchReachable(0x1000, 0x1000 + (1UL << 27)));
        Assert.False(Arm64CodePatch.IsBranchReachable(0x1000, 0x1000 + (1UL << 27) + 4));
        Assert.True(Arm64CodePatch.IsBranchReachable(0x8001000, 0x1000));    // exactly the negative reach
    }

    [Fact]
    public void LoadAddress_is_an_ADRP_page_base_and_the_low_twelve_bits()
    {
        Assert.Equal(
            [.. Enc(0xB0000000), .. Enc(0x91000000)],
            Arm64CodePatch.LoadAddress(A64.X0, 0x1000, 0));
        Assert.Equal(
            [.. Enc(0xD0000009), .. Enc(0x9100D129)],
            Arm64CodePatch.LoadAddress(A64.X9, 0x2034, 0));
    }

    // ----- native execution on Apple Silicon -----

    const int ProtRead = 0x1, ProtWrite = 0x2, ProtExec = 0x4;
    const int MapAnon = 0x1000, MapPrivate = 0x2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate ulong FnPtr(nint argument);

    static bool CanRunNatively =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    sealed class NativePage : IDisposable
    {
        public readonly nint Base;
        readonly int _length;

        public NativePage(int length = 0x4000)
        {
            _length = length;
            Base = mmap(0, (nuint)length, ProtRead | ProtWrite, MapAnon | MapPrivate, -1, 0);
            if (Base == -1) throw new InvalidOperationException("mmap failed");
        }

        public void Write(int offset, byte[] code) => Marshal.Copy(code, 0, Base + offset, code.Length);

        public void WriteQword(int offset, ulong value) => Marshal.WriteInt64(Base + offset, unchecked((long)value));

        public void MakeExecutable(int offset = 0, int length = -1) =>
            _ = mprotect(Base + offset, (nuint)(length < 0 ? _length : length), ProtRead | ProtExec);

        public ulong Run(int offset) => Run(offset, 0);

        public ulong Run(int offset, nint argument) =>
            Marshal.GetDelegateForFunctionPointer<FnPtr>(Base + offset)(argument);

        public void Dispose() => _ = munmap(Base, (nuint)_length);

        [DllImport("libc")]
        private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

        [DllImport("libc")]
        private static extern int mprotect(nint addr, nuint length, int prot);

        [DllImport("libc")]
        private static extern int munmap(nint addr, nuint length);
    }

    static byte[] Return(ushort value) => new Arm64Assembler().Movz(A64.X0, value).Ret().Build();

    [Fact]
    public void A_B_trampoline_branches_natively()
    {
        if (!CanRunNatively) return;
        using var page = new NativePage();
        ulong base_ = (ulong)page.Base;
        page.Write(0x00, Return(42));
        page.Write(0x40, new Arm64Assembler(base_ + 0x40).B(base_ + 0x00).Build());
        page.MakeExecutable();

        Assert.Equal(42UL, page.Run(0x40));
    }

    [Fact]
    public void An_absolute_jump_lands_natively()
    {
        if (!CanRunNatively) return;
        using var page = new NativePage();
        ulong base_ = (ulong)page.Base;
        page.Write(0x00, Return(7));
        page.Write(0x80, Arm64CodePatch.BuildFarJump(base_ + 0x80, base_ + 0x00));
        page.MakeExecutable();

        Assert.Equal(7UL, page.Run(0x80));
    }

    [Fact]
    public void LoadAddress_materialises_a_pointer_for_a_load()
    {
        if (!CanRunNatively) return;
        using var page = new NativePage();
        ulong base_ = (ulong)page.Base;
        page.WriteQword(0x1000, 0x1234);
        var load = new Arm64Assembler(base_ + 0xC0)
            .Adrp(A64.X9, base_ + 0x1000)
            .AddImm(A64.X9, A64.X9, (int)((base_ + 0x1000) & 0xFFF))
            .LdrImm(A64.X0, A64.X9, 0)
            .Ret()
            .Build();
        page.Write(0xC0, load);
        page.MakeExecutable();

        Assert.Equal(0x1234UL, page.Run(0xC0));
    }

    [Fact]
    public void Halfword_load_store_round_trip_natively()
    {
        if (!CanRunNatively) return;
        // The code page becomes read/execute, so the data it works on lives in its own read/write mapping, exactly as
        // the installed block does (an anonymous read/execute page is genuinely read-only on Apple Silicon).
        using var page = new NativePage();
        using var data = new NativePage();
        ulong base_ = (ulong)page.Base;
        data.WriteQword(0x1000, 0x0000_0000_00c8_0064);          // halfwords at +0x1000 (100) and +0x1002 (200)
        var code = new Arm64Assembler(base_ + 0xC0)
            .Ldrh(A64.X1, A64.X0, 0x1000)
            .AddImm32(A64.X1, A64.X1, 5)
            .Strh(A64.X1, A64.X0, 0x1002)
            .MovReg(A64.X0, A64.X1)
            .Ret()
            .Build();
        page.Write(0xC0, code);
        page.MakeExecutable();

        Assert.Equal(105UL, page.Run(0xC0, data.Base));
        Assert.Equal(105, Marshal.ReadInt16(data.Base, 0x1002));
    }

    [Fact]
    public void StrRegScaled8_executes_natively()
    {
        if (!CanRunNatively) return;
        using var page = new NativePage();
        using var data = new NativePage();
        ulong base_ = (ulong)page.Base;
        var code = new Arm64Assembler(base_ + 0xC0)
            .Movz(A64.X1, 3)
            .Movz(A64.X2, 0x1234)
            .StrRegScaled8(A64.X2, A64.X0, A64.X1)
            .MovReg(A64.X0, A64.X2)
            .Ret()
            .Build();
        page.Write(0xC0, code);
        page.MakeExecutable();

        Assert.Equal(0x1234UL, page.Run(0xC0, data.Base));
        Assert.Equal(0x1234L, Marshal.ReadInt64(data.Base, 3 * 8));
    }

    [Fact]
    public void AcceleratedDevelopment_routine_executes_natively_without_fault()
    {
        if (!CanRunNatively) return;
        using var block = new NativePage(0x20000);
        ulong base_ = (ulong)block.Base;
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.0+2492889-touch-mac.json"));
        var hook = new AcceleratedDevelopment(new FakeMemory(), null, profile, 0, 0);

        const int playerOffset = 0x2000;
        const int stubOffset = 0x14000;
        const int siteOffset = 0x14100;
        ulong playerAddr = base_ + playerOffset;
        ulong siteAddr = base_ + siteOffset;

        // ldrh w8, [x23, #CA]; add w8, w8, w22
        byte[] stolen = new Arm64Assembler()
            .Ldrh(A64.X8, A64.X23, profile.Fields.CurrentAbility)
            .AddReg32(A64.X8, A64.X8, A64.X22)
            .Build();
        byte[] built = hook.BuildBlockFor(base_, siteAddr, stolen, liftZeroGains: true);
        block.Write(0, built);
        Marshal.WriteInt16(block.Base + playerOffset, profile.Fields.CurrentAbility, 100);
        // Add 1 accelerated row (5.0x -> 50 tenths) at TableOffset (0x200), CountOffset (0x28) = 1.
        block.WriteQword(0x200, playerAddr);
        Marshal.WriteInt16(block.Base + 0x208, 50);
        Marshal.WriteInt32(block.Base + 0x20C, 123);
        Marshal.WriteInt32(block.Base + 0x28, 1);

        var entry = new Arm64Assembler(base_ + stubOffset)
            .StpPre(A64.X22, A64.X23, A64.SP, -0x10)
            .AddImm(A64.X23, A64.X0, 0)
            .Movz(A64.X22, 2)
            .B(siteAddr)
            .Build();
        block.Write(stubOffset, entry);
        block.Write(siteOffset, Arm64CodePatch.BuildPatch(siteAddr, base_ + 0x10000, 8));
        var resume = new Arm64Assembler(siteAddr + 8)
            .MovReg(A64.X0, A64.X8)
            .LdpPost(A64.X22, A64.X23, A64.SP, 0x10)
            .Ret()
            .Build();
        block.Write(siteOffset + 8, resume);
        block.MakeExecutable(0x10000, 0x8000);

        ulong result = block.Run(stubOffset, (nint)playerAddr);
        Assert.Equal(110UL, result);                                            // CA 100 + scaled gain (2 * 5.0 = 10)
        Assert.Equal(1L, Marshal.ReadInt64(block.Base, 0x08));                  // Calls
        Assert.Equal(1L, Marshal.ReadInt64(block.Base, 0x10));                  // Hits
        Assert.Equal(unchecked((long)playerAddr), Marshal.ReadInt64(block.Base, 0x180));
    }

    [Fact]
    public void GoldenGenerationHook_routine_executes_natively_without_fault()
    {
        if (!CanRunNatively) return;
        using var block = new NativePage(0x20000);
        ulong base_ = (ulong)block.Base;
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.0+2492889-touch-mac.json"));

        const int playerOffset = 0x9000, candidateOffset = 0x9800, clubOffset = 0x9C00, nationOffset = 0x9E00;
        const int stubOffset = 0x14000, siteOffset = 0x14100;
        ulong playerAddr = base_ + playerOffset, clubAddr = base_ + clubOffset, nationAddr = base_ + nationOffset;
        ulong siteAddr = base_ + siteOffset;

        // ldrsh w8, [x20, #PA]; cmp w8, #0xb3
        byte[] stolen = new Arm64Assembler()
            .Ldrsh(A64.X8, A64.X20, profile.Fields.PotentialAbility)
            .CmpImm32(A64.X8, 0xB3)
            .Build();
        byte[] built = GoldenGenerationHook.BuildBlock(base_, siteAddr, stolen, profile);
        block.Write(0, built);

        // Configure: Enabled=1, MinPa=150, Range=41 (150..190), Wonderkid=100%, Scope=Club, NationCount=1, Ids=[680]
        Marshal.WriteByte(block.Base + 0x28, 1);
        Marshal.WriteInt16(block.Base + 0x2A, 150);
        Marshal.WriteInt16(block.Base + 0x2C, 41);
        Marshal.WriteByte(block.Base + 0x2E, 100);
        Marshal.WriteByte(block.Base + 0x2F, (byte)GoldenGenerationScope.Club);
        Marshal.WriteInt32(block.Base + 0x30, 1);
        Marshal.WriteInt32(block.Base + 0x300, 680);

        Marshal.WriteInt16(block.Base + playerOffset + profile.Fields.CurrentAbility, 70);
        Marshal.WriteInt16(block.Base + playerOffset + profile.Fields.PotentialAbility, 90);
        block.WriteQword(candidateOffset + (profile.YouthCandidates?.Club ?? 0x78), clubAddr);
        block.WriteQword(candidateOffset + (profile.YouthCandidates?.BasedNation ?? 0x68), nationAddr);
        Marshal.WriteInt32(block.Base + clubOffset + 0xC, 680);
        Marshal.WriteInt32(block.Base + nationOffset + 0xC, 765);

        var entry = new Arm64Assembler(base_ + stubOffset)
            .StpPre(A64.X19, A64.X20, A64.SP, -0x20)
            .StrImm(A64.LR, A64.SP, 0x10)
            .AddImm(A64.X20, A64.X0, 0)                                         // x20 = ACTUAL_PLAYER*
            .AddImm(A64.X19, A64.X0, candidateOffset - playerOffset)            // x19 = YouthCandidate*
            .B(siteAddr)
            .Build();
        block.Write(stubOffset, entry);
        block.Write(siteOffset, Arm64CodePatch.BuildPatch(siteAddr, base_ + 0xC000, 8));
        var resume = new Arm64Assembler(siteAddr + 8)
            .MovReg(A64.X0, A64.X8)                                             // replayed PA in w8
            .LdrImm(A64.LR, A64.SP, 0x10)
            .LdpPost(A64.X19, A64.X20, A64.SP, 0x20)
            .Ret()
            .Build();
        block.Write(siteOffset + 8, resume);
        block.MakeExecutable(0xC000, 0xC000);

        ulong pa = block.Run(stubOffset, (nint)playerAddr);
        Assert.InRange(pa, 150UL, 190UL);
        Assert.Equal((short)pa, Marshal.ReadInt16(block.Base + playerOffset + profile.Fields.PotentialAbility));
        Assert.True(Marshal.ReadInt16(block.Base + playerOffset + profile.Fields.CurrentAbility) > 70);
        Assert.Equal(1L, Marshal.ReadInt64(block.Base, 0x08));                  // Calls
        Assert.Equal(1L, Marshal.ReadInt64(block.Base, 0x10));                  // Raised
        Assert.Equal(1, Marshal.ReadInt32(block.Base, 0x3C));                   // Wonderkids

        // Also verify Nation scope resolves via YouthCandidate.basedNation.
        Marshal.WriteInt16(block.Base + playerOffset + profile.Fields.CurrentAbility, 70);
        Marshal.WriteInt16(block.Base + playerOffset + profile.Fields.PotentialAbility, 90);
        Marshal.WriteByte(block.Base + 0x2F, (byte)GoldenGenerationScope.Nation);
        Marshal.WriteInt32(block.Base + 0x300, 765);
        ulong paNation = block.Run(stubOffset, (nint)playerAddr);
        Assert.InRange(paNation, 150UL, 190UL);
        Assert.Equal(2L, Marshal.ReadInt64(block.Base, 0x10));                  // Raised
    }
}
