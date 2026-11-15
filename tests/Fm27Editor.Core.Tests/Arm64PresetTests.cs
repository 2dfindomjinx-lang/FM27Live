using System.Buffers.Binary;
using System.Text;
using Fm27Editor.Core.CodeGen;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// The two hook presets on osx-arm64, against fake memory that holds a Mach-O image instead of a PE: the site is found
/// in an executable <c>__TEXT</c> section by masked instruction words, the patch is a <c>B</c> plus a NOP, and the
/// routines are whole ARM64 instructions that must fit their slot in the block.
/// </summary>
/// <remarks>
/// The site words are the real ones, read from the Steam <c>27.0.1+2503222</c> arm64 <c>game_plugin</c> at RVAs
/// 0x292817C (<c>ldrh w8,[x23,#0x25C]; add w8,w8,w22; sxth w8,w8; ldrsh w9,[x23,#0x25E]</c>) and 0x2A4B494
/// (<c>ldrsh w8,[x20,#0x25E]; cmp w8,#0xb3; b.le; mov w8,#9</c>), disassembled with llvm-objdump.
/// </remarks>
public class Arm64PresetTests
{
    // The site instructions of each build, as 32-bit words.
    const uint AccelerateLoad = 0x7944BAE8;   // ldrh w8, [x23, #0x25c]
    const uint AccelerateAdd = 0x0B160108;    // add  w8, w8, w22
    const uint AccelerateSxth = 0x13003D08;   // sxth w8, w8
    const uint AcceleratePaLoad = 0x79C4BEE9; // ldrsh w9, [x23, #0x25e]
    const uint GoldenPaLoad = 0x79C4BE88;     // ldrsh w8, [x20, #0x25e]
    const uint GoldenCmp = 0x7102CD1F;        // cmp  w8, #0xb3
    const uint GoldenBle = 0x5400006D;        // b.le (into the lower PA buckets)
    const uint GoldenMov9 = 0x52800128;       // mov  w8, #9

    // The same instructions with other destination registers, which a rebuild may renumber.
    const uint AccelerateLoadOtherRegisters = 0x7944BAEB;   // ldrh w11, [x23, #0x25c]
    const uint AccelerateAddOtherRegisters = 0x0B16016B;    // add  w11, w11, w22
    const uint AccelerateSxthOtherRegisters = 0x13003D6B;   // sxth w11, w11
    const uint AcceleratePaLoadOtherRegisters = 0x79C4BEEA; // ldrsh w10, [x23, #0x25e]
    const uint GoldenPaLoadOtherRegisters = 0x79C4BE89;     // ldrsh w9, [x20, #0x25e]
    const uint GoldenCmpOtherRegisters = 0x7102CD3F;        // cmp  w9, #0xb3
    const uint GoldenMov9OtherRegisters = 0x52800129;       // mov  w9, #9

    // The same instructions through another base register and another gain register, which is what the second build
    // does (x19 base, w24 gain). Neither is pinned, so the hook has to find and drive these too.
    const uint AccelerateLoadOtherBase = 0x7944BA68;        // ldrh w8, [x19, #0x25c]
    const uint AccelerateAddOtherBase = 0x0B180108;         // add  w8, w8, w24
    const uint AcceleratePaLoadOtherBase = 0x79C4BE69;      // ldrsh w9, [x19, #0x25e]

    // The other CA update in both builds: a call-return path whose add does not write the register it read.
    const uint DecoyLoad = 0x7944BA89;                      // ldrh w9, [x20, #0x25c]
    const uint DecoyAdd = 0x0B080128;                       // add  w8, w9, w8
    const uint DecoySxth = 0x13003D29;                      // sxth w9, w9
    const uint DecoyPaLoad = 0x79C4BE89;                    // ldrsh w9, [x20, #0x25e]

    const uint Nop = 0xD503201F;

    // Block layout (header offsets and the code slots) and the stolen word counts.
    const int OriginalOffset = 0x18, SiteOffset = 0x20, CodeOffset = 0x40;
    // The routine sits on its own 16 KB page above the data region so it can be committed read/execute independently.
    const int CodeBase = 0x10000, GoldenCodeBase = 0xC000, CodePageSize = 0x4000;
    const int AccelerateRingOffset = 0x180, AccelerateLiftZeroOffset = 0x30;
    const int GoldenNationsOffset = 0x300;

    const ulong Module = 0x0000_0002_0000_0000;
    const int CodeRva = 0x1000, CodeSize = 0x1000, ModuleSize = 0x100000, SiteRva = CodeRva + 0x500;
    const ulong Cave = Module + 0x81000;
    static ulong Site => Module + SiteRva;

    static OffsetProfile Profile() =>
        OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.1+2503222-mac.json"));

    static byte[] Words(params uint[] words) => words.SelectMany(BitConverter.GetBytes).ToArray();

    static uint Word(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    sealed class FakeHost(FakeMemory memory) : ICodePatchHost
    {
        public ulong Next = Module + 0x80000;
        public int Allocations;
        public int Commits;
        public ulong AllocateNear(ulong near, int bytes) { Allocations++; ulong at = Next; memory.Map(at, bytes); Next += 0x100000; return at; }
        public uint Protect(ulong address, int bytes, uint protect) => 0x20;
        public IDisposable Pause() => new Done();
        public void FlushCode(ulong address, int bytes) { }
        public void CommitExecutable(ulong address, int bytes) => Commits++;
        sealed class Done : IDisposable { public void Dispose() { } }
    }

    /// <summary>Fake memory holding a 64-bit Mach-O whose <c>__TEXT,__text</c> holds the given site words.</summary>
    static (FakeMemory Memory, OffsetProfile Profile) Arm64Module(byte[] site)
    {
        var memory = new FakeMemory();
        var header = memory.Map(Module, 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 1);      // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 152);    // sizeofcmds: one segment + one section
        var command = header.AsSpan(32);
        BinaryPrimitives.WriteUInt32LittleEndian(command, 0x19);             // LC_SEGMENT_64
        BinaryPrimitives.WriteUInt32LittleEndian(command[4..], 152);
        Encoding.ASCII.GetBytes("__TEXT").CopyTo(command[8..]);
        BinaryPrimitives.WriteUInt64LittleEndian(command[24..], Module);
        BinaryPrimitives.WriteUInt64LittleEndian(command[32..], 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(command[56..], 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(command[64..], 1);          // nsects
        var section = command[72..];
        Encoding.ASCII.GetBytes("__text").CopyTo(section);
        Encoding.ASCII.GetBytes("__TEXT").CopyTo(section[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(section[32..], CodeRva);
        BinaryPrimitives.WriteUInt64LittleEndian(section[40..], CodeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(section[64..], 0x80000400);  // S_ATTR_PURE_INSTRUCTIONS | SOME
        var code = memory.Map(Module + CodeRva, CodeSize);
        site.CopyTo(code, SiteRva - CodeRva);
        return (memory, Profile());
    }

    static byte[] AccelerateSite() => Words(AccelerateLoad, AccelerateAdd, AccelerateSxth, AcceleratePaLoad);
    static byte[] GoldenSite() => Words(GoldenPaLoad, GoldenCmp, GoldenBle, GoldenMov9);

    // ---------- finding the site ----------

    [Fact]
    public void The_arm64_site_is_found_in_the_text_section()
    {
        var (memory, profile) = Arm64Module(AccelerateSite());
        Assert.Equal(Site, new AcceleratedDevelopment(memory, null, profile, Module, ModuleSize).FindSite());

        var (goldenMemory, goldenProfile) = Arm64Module(GoldenSite());
        Assert.Equal(Site, new GoldenGenerationHook(goldenMemory, null, goldenProfile, Module, ModuleSize).FindSite());
    }

    [Fact]
    public void Renumbered_destination_registers_still_match_the_masked_pattern()
    {
        var (memory, profile) = Arm64Module(Words(AccelerateLoadOtherRegisters, AccelerateAddOtherRegisters,
            AccelerateSxthOtherRegisters, AcceleratePaLoadOtherRegisters));
        Assert.Equal(Site, new AcceleratedDevelopment(memory, null, profile, Module, ModuleSize).FindSite());

        var (goldenMemory, goldenProfile) = Arm64Module(
            Words(GoldenPaLoadOtherRegisters, GoldenCmpOtherRegisters, GoldenBle, GoldenMov9OtherRegisters));
        Assert.Equal(Site, new GoldenGenerationHook(goldenMemory, null, goldenProfile, Module, ModuleSize).FindSite());

        // Mismatched register between ldrsh and cmp must be rejected.
        var (badGoldenMemory, badGoldenProfile) = Arm64Module(
            Words(GoldenPaLoadOtherRegisters, GoldenCmp, GoldenBle, GoldenMov9OtherRegisters));
        var error = Assert.Throws<InvalidOperationException>(
            () => new GoldenGenerationHook(badGoldenMemory, null, badGoldenProfile, Module, ModuleSize).FindSite());
        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public void A_site_through_another_base_register_is_found()
    {
        // The second build reads the player through x19 and takes the gain in w24; nothing is pinned, so the pattern
        // has to take it, and the routine takes its registers from the bytes (see the routine test below).
        var (memory, profile) = Arm64Module(Words(AccelerateLoadOtherBase, AccelerateAddOtherBase,
            AccelerateSxth, AcceleratePaLoadOtherBase));
        Assert.Equal(Site, new AcceleratedDevelopment(memory, null, profile, Module, ModuleSize).FindSite());
    }

    [Fact]
    public void The_other_CA_update_is_refused()
    {
        // The decoy keeps CA in w9 and adds through w8, so its add does not write the register it read. Register
        // agreement, not a fixed register number, is what picks out the site the routine can drive.
        var (memory, profile) = Arm64Module(Words(DecoyLoad, DecoyAdd, DecoySxth, DecoyPaLoad));
        var error = Assert.Throws<InvalidOperationException>(
            () => new AcceleratedDevelopment(memory, null, profile, Module, ModuleSize).FindSite());
        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public void A_missing_site_is_refused()
    {
        var (memory, profile) = Arm64Module(Words(AccelerateAdd, AccelerateSxth, AccelerateAdd));
        var error = Assert.Throws<InvalidOperationException>(
            () => new AcceleratedDevelopment(memory, null, profile, Module, ModuleSize).FindSite());
        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public void An_ambiguous_site_is_refused()
    {
        var (memory, profile) = Arm64Module(AccelerateSite());
        Assert.True(memory.TryWrite(Site + 0x100, AccelerateSite()));        // a second copy in the same section
        var error = Assert.Throws<InvalidOperationException>(
            () => new AcceleratedDevelopment(memory, null, profile, Module, ModuleSize).FindSite());
        Assert.Contains("not unique", error.Message);
    }

    // ---------- installing ----------

    [Fact]
    public void Install_writes_a_B_and_a_NOP_on_arm64_and_Uninstall_puts_the_word_back()
    {
        var (memory, profile) = Arm64Module(AccelerateSite());
        var host = new FakeHost(memory);
        var hook = new AcceleratedDevelopment(memory, host, profile, Module, ModuleSize);

        var state = hook.Install();
        var patched = memory.Read(Site, 8);
        Assert.Equal(0x14000000u, Word(patched) & 0xFC000000);                 // B
        Assert.Equal(Nop, Word(patched.AsSpan(4)));                            // the add is gone too
        Assert.Equal(state.Cave + CodeBase, Site + (ulong)((long)((int)(Word(patched) << 6) >> 6) * 4));
        Assert.Equal(1, host.Commits);

        hook.Uninstall();
        Assert.Equal(AccelerateSite().AsSpan(0, 8).ToArray(), memory.Read(Site, 8));
        Assert.Null(hook.Read());
    }

    [Fact]
    public void Install_writes_a_B_and_a_NOP_over_both_golden_loads()
    {
        var (memory, profile) = Arm64Module(GoldenSite());
        var host = new FakeHost(memory);
        var hook = new GoldenGenerationHook(memory, host, profile, Module, ModuleSize);

        var state = hook.Install();
        var patched = memory.Read(Site, 8);
        Assert.Equal(0x14000000u, Word(patched) & 0xFC000000);                 // B
        Assert.Equal(Nop, Word(patched.AsSpan(4)));                            // the second stolen word
        Assert.Equal(GoldenBle, Word(memory.Read(Site + 8, 4)));               // the rest stays
        Assert.Equal(state.Cave + GoldenCodeBase, Site + (ulong)((long)((int)(Word(patched) << 6) >> 6) * 4));

        hook.Uninstall();
        Assert.Equal(GoldenSite(), memory.Read(Site, 16));
        Assert.Null(hook.Read());
    }

    [Fact]
    public void A_patched_site_is_still_found_by_a_fresh_hook()
    {
        var (memory, profile) = Arm64Module(AccelerateSite());
        var state = new AcceleratedDevelopment(memory, new FakeHost(memory), profile, Module, ModuleSize).Install();
        var read = new AcceleratedDevelopment(memory, new FakeHost(memory), profile, Module, ModuleSize).Read();
        Assert.NotNull(read);
        Assert.Equal((state.Site, state.Cave), (read!.Site, read.Cave));

        // Both patches cover two words (a B and a NOP), so the match has to resume after both of them. On the way the
        // stolen words, and with them the registers, are read back from the block.
        var (otherMemory, otherProfile) = Arm64Module(Words(AccelerateLoadOtherBase, AccelerateAddOtherBase,
            AccelerateSxth, AcceleratePaLoadOtherBase));
        var other = new AcceleratedDevelopment(otherMemory, new FakeHost(otherMemory), otherProfile, Module, ModuleSize).Install();
        var otherRead = new AcceleratedDevelopment(otherMemory, new FakeHost(otherMemory), otherProfile, Module, ModuleSize).Read();
        Assert.NotNull(otherRead);
        Assert.Equal((other.Site, other.Cave), (otherRead!.Site, otherRead.Cave));

        var (goldenMemory, goldenProfile) = Arm64Module(GoldenSite());
        var golden = new GoldenGenerationHook(goldenMemory, new FakeHost(goldenMemory), goldenProfile, Module, ModuleSize).Install();
        var goldenRead = new GoldenGenerationHook(goldenMemory, new FakeHost(goldenMemory), goldenProfile, Module, ModuleSize).Read();
        Assert.NotNull(goldenRead);
        Assert.Equal((golden.Site, golden.Cave), (goldenRead!.Site, goldenRead.Cave));
    }

    [Fact]
    public void A_jump_that_does_not_point_at_this_hooks_block_is_not_taken_for_the_site()
    {
        // The two words a patch leaves at the site (b.le / mov w8, #9) also occur in other code, so a foreign B in front
        // of them would pass the pattern. The block the B points at is read for that reason, and one that is not this hook's
        // own (magic, and this very site) is refused rather than patched over.
        var (memory, profile) = Arm64Module([]);
        var block = memory.Map(Cave, 0x20000);
        ulong routine = Cave + GoldenCodeBase;
        uint jump = 0x14000000u | (uint)((((long)routine - (long)Site) >> 2) & 0x03FFFFFF);
        Assert.True(memory.TryWrite(Site, Words(jump, Nop, GoldenBle, GoldenMov9)));
        // The header sits at the block's start, the routine one page up, so the jump has to be undone by CodeBase.
        const int magicOffset = 0x00;
        Words(GoldenPaLoad, GoldenCmp).CopyTo(block, OriginalOffset);
        BitConverter.GetBytes(Site).CopyTo(block, SiteOffset);
        Encoding.ASCII.GetBytes("FM27XXX1").CopyTo(block, magicOffset);       // another tool's block

        var error = Assert.Throws<InvalidOperationException>(
            () => new GoldenGenerationHook(memory, null, profile, Module, ModuleSize).FindSite());
        Assert.Contains("not found", error.Message);

        Encoding.ASCII.GetBytes("FM27GLD3").CopyTo(block, magicOffset);       // this hook's own block
        Assert.Equal(Site, new GoldenGenerationHook(memory, null, profile, Module, ModuleSize).FindSite());
    }

    [Fact]
    public void The_golden_settings_round_trip_on_arm64()
    {
        var (memory, profile) = Arm64Module(GoldenSite());
        var hook = new GoldenGenerationHook(memory, new FakeHost(memory), profile, Module, ModuleSize);
        var state = hook.Configure(true, 150, 190, 25, GoldenGenerationScope.Club, [680]);
        Assert.Equal((true, 150, 190, 25, GoldenGenerationScope.Club),
            (state.Settings.Enabled, state.Settings.MinPa, state.Settings.MaxPa, state.Settings.WonderkidChance, state.Settings.Scope));
        Assert.Equal([680], state.Settings.Ids);
    }

    [Fact]
    public void Setting_multiple_accelerated_players_on_arm64_reuses_one_cave_without_migrating()
    {
        var (memory, profile) = Arm64Module(AccelerateSite());
        var host = new FakeHost(memory);
        var hook = new AcceleratedDevelopment(memory, host, profile, Module, ModuleSize);
        hook.Install();
        ulong vtable = Module + profile.PlayerVtableRvas[0];
        for (int i = 0; i < 10; i++)
        {
            ulong address = Module + 0x20000 + (ulong)(i * 0x400);
            int id = 1000 + i;
            var obj = memory.Map(address, 0x400);
            BinaryPrimitives.WriteUInt64LittleEndian(obj, vtable);
            BinaryPrimitives.WriteInt32LittleEndian(obj.AsSpan(profile.Fields.Id!.Value), id);
            var player = new PlayerRecord
            {
                Address = address,
                VtableRva = profile.PlayerVtableRvas[0],
                Id = id,
                CurrentAbility = 130,
                PotentialAbility = 170,
                HomeReputation = 100,
                CurrentReputation = 100,
                WorldReputation = 100,
                Height = 180,
                RawAttributes = new byte[57],
                AttributeScale = 5,
                Positions = new byte[14],
                Personality = new byte[8],
                FirstName = "Test",
                LastName = $"Player{i}"
            };
            Assert.True(hook.Set(player, 10));
        }

        Assert.Equal(1, host.Allocations);
        Assert.Equal(1, host.Commits);
        Assert.Equal(10, hook.Read()!.Players.Count);
    }

    [Fact]
    public void Reconfiguring_golden_generation_on_arm64_reuses_one_cave()
    {
        var (memory, profile) = Arm64Module(GoldenSite());
        var host = new FakeHost(memory);
        var hook = new GoldenGenerationHook(memory, host, profile, Module, ModuleSize);
        hook.Configure(true, 150, 180, 25, GoldenGenerationScope.Club, [680]);
        hook.Configure(true, 160, 195, 50, GoldenGenerationScope.Nation, [765]);
        var state = hook.Configure(false, 140, 175, 10, GoldenGenerationScope.All, []);
        Assert.Equal(1, host.Allocations);
        Assert.Equal(1, host.Commits);
        Assert.False(state.Settings.Enabled);
        Assert.Equal((140, 175, 10, GoldenGenerationScope.All),
            (state.Settings.MinPa, state.Settings.MaxPa, state.Settings.WonderkidChance, state.Settings.Scope));
    }

    // ---------- the routines ----------

    /// <summary>The block's routine words, from the slot up to and including the branch back to the game.</summary>
    static List<uint> Routine(byte[] block, byte[] stolen, int codeBase = CodeBase)
    {
        var words = new List<uint>();
        int stolenWords = stolen.Length / 4;
        for (int at = codeBase; at + 4 <= block.Length; at += 4)
        {
            uint word = Word(block.AsSpan(at, 4));
            words.Add(word);
            if (words.Count > stolenWords && (word & 0xFC000000) == 0x14000000
                && words.Skip(words.Count - 1 - stolenWords).Take(stolenWords)
                    .SelectMany(BitConverter.GetBytes).SequenceEqual(stolen))
                return words;
        }
        throw new InvalidOperationException("The routine has no branch back to the game");
    }

    static ulong BranchTarget(uint branch, ulong branchAddress) =>
        (ulong)((long)branchAddress + (long)((int)(branch << 6) >> 6) * 4);

    [Fact]
    public void The_accelerate_routine_saves_everything_it_uses_and_branches_back_after_the_stolen_instructions()
    {
        var (memory, profile) = Arm64Module(AccelerateSite());
        var hook = new AcceleratedDevelopment(memory, new FakeHost(memory), profile, Module, ModuleSize);
        var stolen = Words(AccelerateLoad, AccelerateAdd);
        var block = hook.BuildBlockFor(Cave, Site, stolen, liftZeroGains: true);

        Assert.Equal("FM27ACC2", Encoding.ASCII.GetString(block, 0, 8));
        Assert.Equal(stolen, block.AsSpan(OriginalOffset, 8).ToArray());
        Assert.Equal(Site, BitConverter.ToUInt64(block, SiteOffset));
        Assert.Equal(1, block[AccelerateLiftZeroOffset]);

        var words = Routine(block, stolen);
        Assert.Equal(0xA9BA07E0, words[0]);                                    // stp x0, x1, [sp, #-0x60]!
        ulong routine = Cave + CodeBase;
        int load = words.FindIndex(w => (w & 0x9F000000) == 0x90000000);       // the ADRP that points at the block
        Assert.True(load >= 0, "the routine never points a register at its block");
        Assert.Equal(Arm64CodePatch.LoadAddress(A64.X9, Cave, routine + (ulong)(load * 4)),
            words.Skip(load).Take(2).SelectMany(BitConverter.GetBytes).ToArray());
        Assert.True(words.Count * 4 <= CodePageSize, $"the routine overflows its slot ({words.Count * 4} bytes)");
        Assert.Equal(AccelerateAdd, words[^2]);                                // both stolen instructions are re-run
        Assert.Equal(AccelerateLoad, words[^3]);
        Assert.Equal(Site + 8, BranchTarget(words[^1], routine + (ulong)((words.Count - 1) * 4)));
    }

    [Fact]
    public void The_accelerate_routine_takes_both_registers_from_the_stolen_bytes()
    {
        // x19 base and w24 gain instead of the recorded x23/w22: the player goes to the routine's own x7 and the gain
        // to x8 while it runs, and the result is put back in w24 (the site's own register) before the replay.
        var (memory, profile) = Arm64Module(Words(AccelerateLoadOtherBase, AccelerateAddOtherBase,
            AccelerateSxth, AcceleratePaLoadOtherBase));
        var hook = new AcceleratedDevelopment(memory, new FakeHost(memory), profile, Module, ModuleSize);
        var stolen = Words(AccelerateLoadOtherBase, AccelerateAddOtherBase);
        var block = hook.BuildBlockFor(Cave, Site, stolen, liftZeroGains: true);
        var words = Routine(block, stolen);
        ulong routine = Cave + CodeBase;

        Assert.Contains(0x91000267u, words);                                   // add x7, x19, #0 (the player, out of the base)
        Assert.Contains(0x2A1803E8u, words);                                   // mov w8, w24 (the gain, into the copy)
        Assert.Equal(0xB9405BF8u, words[^5]);                                  // ldr w24, [sp, #0x58] (and back out again)
        Assert.Equal(0x910183FFu, words[^4]);                                  // add sp, sp, #0x60
        Assert.Equal(stolen, words.Skip(words.Count - 3).Take(2).SelectMany(BitConverter.GetBytes).ToArray());
        Assert.Equal(Site + 8, BranchTarget(words[^1], routine + (ulong)((words.Count - 1) * 4)));
    }

    [Fact]
    public void The_golden_routine_rewrites_PA_before_re_running_both_stolen_loads()
    {
        var (memory, profile) = Arm64Module(GoldenSite());
        var hook = new GoldenGenerationHook(memory, new FakeHost(memory), profile, Module, ModuleSize);
        var stolen = Words(GoldenPaLoad, GoldenCmp);
        var block = GoldenGenerationHook.BuildBlock(Cave, Site, stolen, profile);

        Assert.Equal("FM27GLD3", Encoding.ASCII.GetString(block, 0, 8));
        Assert.Equal(stolen, block.AsSpan(OriginalOffset, 8).ToArray());

        var words = Routine(block, stolen, GoldenCodeBase);
        Assert.Equal(0xA9BB07E0, words[0]);                                    // stp x0, x1, [sp, #-0x50]!
        ulong routine = Cave + GoldenCodeBase;
        int load = words.FindIndex(w => (w & 0x9F000000) == 0x90000000);       // the ADRP that points at the block
        Assert.True(load >= 0, "the routine never points a register at its block");
        Assert.Equal(Arm64CodePatch.LoadAddress(A64.X0, Cave, routine + (ulong)(load * 4)),
            words.Skip(load).Take(2).SelectMany(BitConverter.GetBytes).ToArray());
        Assert.True(words.Count * 4 <= CodePageSize, $"the routine overflows its slot ({words.Count * 4} bytes)");
        Assert.Contains(0xF9403E63u, words);                                   // ldr x3, [x19, #0x78] (YouthCandidate.club)
        Assert.Contains(0xF9403663u, words);                                   // ldr x3, [x19, #0x68] (YouthCandidate.basedNation)
        Assert.Contains(0x7904BE84u, words);                                   // strh w4, [x20, #0x25e] (PA on ACTUAL_PLAYER)
        Assert.Contains(0x7904BA85u, words);                                   // strh w5, [x20, #0x25c] (CA on ACTUAL_PLAYER)
        Assert.Contains(words, w => (w & 0xFF00001Fu) == 0xB4000013u);         // cbz x19, ... (64-bit check)
        Assert.Contains(words, w => (w & 0xFF00001Fu) == 0xB4000003u);         // cbz x3, ... (64-bit pointer check)
        Assert.Equal(stolen, words.Skip(words.Count - 3).Take(2).SelectMany(BitConverter.GetBytes).ToArray());
        Assert.Equal(Site + 8, BranchTarget(words[^1], routine + (ulong)((words.Count - 1) * 4)));
    }

    /// <summary>
    /// Every arm64 profile must carry the whole chain both routines walk. A missing entry is not reported until the
    /// block is built — after the cave has already been allocated — so `team.club` missing from a new profile threw
    /// "The profile has no team club offset" at install time.
    /// </summary>
    [Fact]
    public void Every_arm64_profile_has_the_offsets_the_routines_need()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "offsets"), "*.json")
            .Where(f => f.Contains("-mac.json", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var profile = OffsetProfile.LoadFile(file);
            Assert.True(profile.IsArm64, file);
            Assert.NotNull(profile.Team);
            Assert.True(profile.Team!.Club is > 0, $"{Path.GetFileName(file)} has no team club offset");
            Assert.NotNull(profile.Club?.Nation);
            Assert.NotNull(profile.Nation?.Continent);
            Assert.NotEqual(0, profile.Fields.CurrentAbility);
            Assert.NotEqual(0, profile.Fields.PotentialAbility);
            // The development routines do not use ForceAccept. Its former ARM64 RVAs are libcurl sites, so even an
            // older profile carrying one must fail closed rather than treating its presence as feature support.
            Assert.Equal(Fm27Editor.Core.ForceAccept.ForceAcceptPlanState.Unavailable,
                new Fm27Editor.Core.ForceAccept.ForceAcceptPlanner(profile, new FakeMemory(), Module)
                    .Plan(Fm27Editor.Core.ForceAccept.ForceAcceptPreset.TransferOffers).State);
        }
    }
}
