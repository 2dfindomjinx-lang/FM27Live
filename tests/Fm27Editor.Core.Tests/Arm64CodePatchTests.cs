using Fm27Editor.Core.CodeGen;

namespace Fm27Editor.Core.Tests;

public class Arm64CodePatchTests
{
    const ulong Origin = 0x1_0000_0000;

    [Theory]
    [InlineData(-0x08000004L, false)]
    [InlineData(-0x08000000L, true)]
    [InlineData(0x07FFFFFCL, true)]
    [InlineData(0x08000000L, false)]
    [InlineData(2L, false)]
    public void Reachability_uses_the_signed_imm26_word_range(long delta, bool expected)
    {
        // Signed imm26 is [-2^25, 2^25-1]; scaling by four gives [-0x8000000, 0x7FFFFFC].
        Assert.Equal(expected, Arm64CodePatch.IsBranchReachable(Origin, (ulong)((long)Origin + delta)));
    }

    [Fact]
    public void The_last_forward_B_and_first_far_jump_have_independent_encodings()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x15 },
            Arm64CodePatch.BuildJump(Origin, Origin + 0x07FFFFFC)); // B imm26=0x1FFFFFF
        Assert.Equal(new byte[]
        {
            0x10, 0x00, 0x80, 0xD2, // movz x16, #0
            0x10, 0x00, 0xA1, 0xF2, // movk x16, #0x800, lsl #16
            0x30, 0x00, 0xC0, 0xF2, // movk x16, #1, lsl #32
            0x00, 0x02, 0x1F, 0xD6, // br x16
        }, Arm64CodePatch.BuildJump(Origin, Origin + 0x08000000));
    }

    [Fact]
    public void Misaligned_instruction_addresses_are_not_branch_reachable()
    {
        Assert.False(Arm64CodePatch.IsBranchReachable(Origin + 2, Origin + 6));
    }

    [Theory]
    [InlineData(0UL, 2UL)]
    [InlineData(2UL, 6UL)]
    public void Jump_builders_refuse_misaligned_instruction_addresses(ulong fromOffset, ulong toOffset)
    {
        ulong from = Origin + fromOffset, to = Origin + toOffset;
        Assert.Throws<InvalidOperationException>(() => Arm64CodePatch.BuildJump(from, to));
        Assert.Throws<InvalidOperationException>(() => Arm64CodePatch.BuildFarJump(from, to));
        Assert.Throws<InvalidOperationException>(() => Arm64CodePatch.BuildPatch(from, to, 4));
    }
}
