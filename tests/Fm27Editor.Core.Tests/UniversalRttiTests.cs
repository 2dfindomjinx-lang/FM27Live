using System.Buffers.Binary;
using System.Text;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public class UniversalRttiTests
{
    [Fact]
    public void Demangle_and_Canonicalize_normalize_Itanium_and_MSVC_names()
    {
        Assert.Equal("db::ACTUAL_PLAYER", RttiIndex.Canonicalize("N2db13ACTUAL_PLAYERE"));
        Assert.Equal("db::ACTUAL_PLAYER", RttiIndex.Canonicalize(".?AVACTUAL_PLAYER@db@@"));
        Assert.Equal("TRANSFER_MANAGER", RttiIndex.Canonicalize("16TRANSFER_MANAGER"));
        Assert.Equal("TRANSFER_MANAGER", RttiIndex.Canonicalize(".?AVTRANSFER_MANAGER@@"));
        Assert.Equal("N2db13ACTUAL_PLAYERE", RttiIndex.ToItanium("db::ACTUAL_PLAYER"));
        Assert.Equal(".?AVACTUAL_PLAYER@db@@", RttiIndex.ToMsvc("db::ACTUAL_PLAYER"));
        Assert.Equal("16TRANSFER_MANAGER", RttiIndex.ToItanium("TRANSFER_MANAGER"));
    }

    [Fact]
    public void BuildItaniumFromMappedImage_handles_non_unique_bit_and_subobject_offsets()
    {
        const ulong moduleBase = 0x100000000UL;
        var bytes = new byte[0x4000];
        // Mach-O 64 header with 2 segments: __TEXT [0x1000..0x2000] (__cstring) and __DATA_CONST [0x2000..0x4000] (__const).
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xFEEDFACFu);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 2); // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 152 * 2); // sizeofcmds (72 + 80 each)

        void WriteSeg(int off, string seg, ulong vm, ulong vmsize, string sect, ulong saddr, ulong ssize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(off), 0x19);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(off + 4), 152);
            Encoding.ASCII.GetBytes(seg).CopyTo(bytes, off + 8);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(off + 24), vm);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(off + 32), vmsize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(off + 64), 1); // nsects
            int so = off + 72;
            Encoding.ASCII.GetBytes(sect).CopyTo(bytes, so);
            Encoding.ASCII.GetBytes(seg).CopyTo(bytes, so + 16);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(so + 32), saddr);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(so + 40), ssize);
        }

        WriteSeg(32, "__TEXT", moduleBase, 0x2000, "__cstring", moduleBase + 0x1000, 0x1000);
        WriteSeg(32 + 152, "__DATA_CONST", moduleBase + 0x2000, 0x2000, "__const", moduleBase + 0x2000, 0x2000);

        Encoding.ASCII.GetBytes("N2db17ACTUAL_NON_PLAYERE\0").CopyTo(bytes, 0x1100);
        // type_info at 0x2100: w0 = external pointer, w1 = mangled ptr | (1 << 63) ARM64 NON_UNIQUE bit.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x2100), 0x7FFF00001234UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x2108), (moduleBase + 0x1100) | (1UL << 63));

        // Primary vtable at 0x2200 (top = 0), subobject vtable at 0x2240 (top = -520).
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x2200 - 16), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x2200 - 8), moduleBase + 0x2100);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x2240 - 16), -520);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x2240 - 8), moduleBase + 0x2100);

        var image = ModuleImage.FromBytes(moduleBase, bytes,
            constRanges: [(0x2000, 0x4000)],
            stringRanges: [(0x1000, 0x2000)]);
        var index = RttiIndex.Build(image);

        Assert.Equal([0x2200u], index.Vtables("db::ACTUAL_NON_PLAYER", 0));
        Assert.Equal([0x2240u], index.Vtables("db::ACTUAL_NON_PLAYER", 520));
        Assert.Equal("db::ACTUAL_NON_PLAYER", index.CanonicalNameOf(0x2240u));

        var fakeMem = new FakeMemory();
        var mapped = fakeMem.Map(moduleBase, bytes.Length);
        bytes.CopyTo(mapped, 0);
        Assert.True(RttiIndex.TryReadClass(fakeMem, moduleBase, moduleBase + (ulong)bytes.Length, moduleBase + 0x2240, out var entry));
        Assert.Equal("db::ACTUAL_NON_PLAYER", entry.CanonicalName);
        Assert.Equal(520, entry.SubobjectOffset);
    }

    [Fact]
    public void BuildMsvc_indexes_locators_and_subobject_offsets()
    {
        const ulong moduleBase = 0x140000000UL;
        var bytes = new byte[0x4000];
        // TypeDescriptor at 0x1000 -> string at 0x1010.
        Encoding.ASCII.GetBytes(".?AVACTUAL_NON_PLAYER@db@@\0").CopyTo(bytes, 0x1010);

        // COL at 0x2000 (subobject offset 256).
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2000), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2004), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x200C), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2014), 0x2000);

        // Vtable slot at 0x2100 points to COL -> vtable RVA is 0x2108.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x2100), moduleBase + 0x2000);

        var image = ModuleImage.FromBytes(moduleBase, bytes,
            constRanges: [(0x2000, 0x3000)],
            stringRanges: [(0x1000, 0x1800)]);
        var index = RttiIndex.BuildMsvc(image);

        Assert.Equal([0x2108u], index.Vtables("db::ACTUAL_NON_PLAYER", 256));
        Assert.Equal(256, index.EntryOf(0x2108u)?.SubobjectOffset);
    }

    /// <summary>
    /// MSVC keeps type descriptors in .data (they start with type_info's vtable pointer, which the loader writes), not in the
    /// read-only sections. Steam 27.1.0+2505117 (19/11/2026): the strings ".?AVACTUAL_PLAYER@db@@" etc. are in the writable
    /// data, and with only the read-only sections searched newbuild resolved none of 62 classes.
    /// </summary>
    [Fact]
    public void BuildMsvc_finds_type_descriptors_in_writable_data()
    {
        const ulong moduleBase = 0x140000000UL;
        var bytes = new byte[0x4000];
        Encoding.ASCII.GetBytes(".?AVACTUAL_PLAYER@db@@\0").CopyTo(bytes, 0x3010);   // descriptor at 0x3000, in .data
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2000), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x200C), 0x3000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2014), 0x2000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x2100), moduleBase + 0x2000);

        var image = ModuleImage.FromBytes(moduleBase, bytes,
            writableDataRanges: [(0x3000, 0x4000)],
            constRanges: [(0x2000, 0x3000)],
            stringRanges: [(0x1000, 0x1800)]);

        Assert.Equal([0x2108u], RttiIndex.BuildMsvc(image).Vtables("db::ACTUAL_PLAYER", 0));
    }

    [Theory]
    [InlineData("/Users/duong/Library/Application Support/Steam/steamapps/common/Football Manager 27/fm.app/Contents/PlugIns/game_plugin.bundle/Contents/MacOS/game_plugin", "layouts/macos.json")]
    [InlineData("/Applications/Football Manager Touch.app/Contents/PlugIns/game_plugin.bundle/Contents/MacOS/game_plugin", "layouts/touch-macos.json")]
    public void OnDisk_MachO_binary_resolves_every_non_optional_layout_vtableClass(string binaryPath, string layoutPath)
    {
        if (!File.Exists(binaryPath)) return;

        var layoutJson = ProfileJsonResolver.LoadLayoutJson(layoutPath)!;
        var layoutProfile = OffsetProfile.Load(layoutJson);
        var rtti = RttiIndex.FromMachOFile(binaryPath);

        var missing = new List<string>();
        foreach (var (key, spec) in layoutProfile.VtableClasses!)
        {
            if (spec.Optional) continue;
            var hits = rtti.Vtables(spec.Class, spec.Offset);
            if (hits.Count == 0 && spec.FallbackClasses is { Length: > 0 })
            {
                foreach (var fb in spec.FallbackClasses)
                {
                    hits = rtti.Vtables(fb, spec.Offset);
                    if (hits.Count > 0) break;
                }
            }
            if (hits.Count == 0) missing.Add($"{key} ({spec.Class}+{spec.Offset})");
        }
        Assert.True(missing.Count == 0, $"Missing RTTI entries in {binaryPath}: {string.Join(", ", missing)}");
    }
}
