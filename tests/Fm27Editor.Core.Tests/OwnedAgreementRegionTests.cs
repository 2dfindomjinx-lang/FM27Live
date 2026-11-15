using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public sealed class OwnedAgreementRegionTests
{
    const ulong Module = 0x7FF000000000, Holder = 0x100000, Pointers = 0x200000, First = 0x300000, Second = 0x400000;
    sealed class ObservedMemory : IProcessMemory
    {
        public FakeMemory Inner { get; } = new();
        public Action<ulong, int>? BeforeRead;
        public Dictionary<ulong, int> Reads { get; } = new();
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            Reads.TryGetValue(address, out int count);
            Reads[address] = ++count;
            BeforeRead?.Invoke(address, count);
            return Inner.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new Exception("Read-only discovery wrote memory");
    }
    static (ObservedMemory Memory, NationAgreements Reader, byte[] HolderBytes, byte[] Root, byte[] Second) Fixture(string build, uint rootRva, uint regionVtable)
    {
        var memory = new ObservedMemory();
        var root = memory.Inner.Map(Module + rootRva, 8);
        BitConverter.GetBytes(Holder).CopyTo(root, 0);
        var holder = memory.Inner.Map(Holder, 0x810);
        Vector(holder, 0x760, Pointers, 2);
        var pointers = memory.Inner.Map(Pointers, 16);
        BitConverter.GetBytes(First).CopyTo(pointers, 0);
        BitConverter.GetBytes(Second).CopyTo(pointers, 8);
        Record(memory.Inner.Map(First, 24), regionVtable, 23);
        var second = memory.Inner.Map(Second, 24);
        Record(second, regionVtable, 14);
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build + ".json"));
        return (memory, new NationAgreements(memory, profile, Module), holder, root, second);
    }
    static void Vector(byte[] holder, int offset, ulong begin, int count)
    {
        BitConverter.GetBytes(begin).CopyTo(holder, offset);
        BitConverter.GetBytes(begin + (ulong)count * 8).CopyTo(holder, offset + 8);
        BitConverter.GetBytes(begin + (ulong)count * 8).CopyTo(holder, offset + 16);
    }
    static void Record(byte[] bytes, uint vtable, int id)
    {
        BitConverter.GetBytes(Module + vtable).CopyTo(bytes, 0);
        BitConverter.GetBytes(id).CopyTo(bytes, 0x10);
    }
    [Theory]
    [InlineData("27.0.1+2503222-steam", 0x58120B0U, 0x4ECD708U)]
    [InlineData("27.0.0+2493426-gamepass", 0x56743C8U, 0x4D78508U)]
    public void Profile_owned_vector_preserves_all_rows_without_heap_fallback(string build, uint root, uint vtable)
    {
        var (_, reader, _, _, _) = Fixture(build, root, vtable);
        var rows = reader.PreloadRegions(() => throw new Exception("Unexpected heap scan"));
        Assert.Equal(new[] { new AgreementRegion(Second, 14, "Uk And Ireland"), new AgreementRegion(First, 23, "23") }, rows);
        Assert.Same(rows, reader.Regions());
    }
    [Fact]
    public void Two_valid_different_vectors_are_ambiguous()
    {
        var (memory, reader, holder, _, _) = Fixture("27.0.1+2503222-steam", 0x58120B0, 0x4ECD708);
        Vector(holder, 0x700, 0x500000, 1);
        BitConverter.GetBytes(First).CopyTo(memory.Inner.Map(0x500000, 8), 0);
        Assert.Null(reader.TryReadOwnedRegions());
    }
    [Fact]
    public void Unreadable_candidate_makes_discovery_unknown()
    {
        var (_, reader, holder, _, _) = Fixture("27.0.1+2503222-steam", 0x58120B0, 0x4ECD708);
        Vector(holder, 0x700, 0x500000, 1);
        Assert.Null(reader.TryReadOwnedRegions());
    }
    [Fact]
    public void Invalid_record_disproves_candidate_even_with_another_unreadable_record()
    {
        var (memory, reader, holder, _, _) = Fixture("27.0.1+2503222-steam", 0x58120B0, 0x4ECD708);
        Vector(holder, 0x700, 0x500000, 2);
        var pointers = memory.Inner.Map(0x500000, 16);
        BitConverter.GetBytes(0x600000UL).CopyTo(pointers, 0); // unreadable
        BitConverter.GetBytes(0x700000UL).CopyTo(pointers, 8); // readable wrong type
        memory.Inner.Map(0x700000, 24);
        Assert.Equal(2, reader.TryReadOwnedRegions()!.Count);
    }
    [Theory]
    [InlineData("root")]
    [InlineData("header")]
    [InlineData("pointers")]
    [InlineData("record")]
    public void Changed_publication_path_rejects_the_result(string changed)
    {
        var (memory, reader, holder, root, second) = Fixture("27.0.1+2503222-steam", 0x58120B0, 0x4ECD708);
        memory.BeforeRead = (address, count) =>
        {
            if (changed == "root" && address == Module + 0x58120B0 && count == 2) Array.Clear(root);
            if (changed == "header" && address == Holder + 0x760) holder[0x760]++;
            if (changed == "pointers" && address == Pointers && count == 2)
                memory.Inner.TryWrite(Pointers, BitConverter.GetBytes(Second));
            if (changed == "record" && address == Second && count == 2) second[0x10]++;
        };
        Assert.Null(reader.TryReadOwnedRegions());
    }
    [Fact]
    public void Duplicate_ids_reject_the_entire_candidate()
    {
        var (_, reader, _, _, second) = Fixture("27.0.1+2503222-steam", 0x58120B0, 0x4ECD708);
        BitConverter.GetBytes(23).CopyTo(second, 0x10);
        Assert.Null(reader.TryReadOwnedRegions());
    }
}
