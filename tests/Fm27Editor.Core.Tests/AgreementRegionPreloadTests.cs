using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public sealed class AgreementRegionPreloadTests
{
    const ulong Module = 0x7FF000000000;
    static OffsetProfile Profile => OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.1+2503222-steam.json"));

    [Fact]
    public void Unreadable_region_candidate_is_not_published_as_an_empty_catalogue()
    {
        var reader = new NationAgreements(new FakeMemory(), Profile, Module);
        Assert.Throws<MemoryAccessException>(() => reader.PreloadRegions(() => [0x10000]));
        Assert.Throws<InvalidOperationException>(() => reader.Regions());
    }
    [Fact]
    public void Retained_candidates_preserve_ids_order_duplicates_and_unknown_labels()
    {
        var memory = new FakeMemory();
        foreach (var (address, id) in new[] { (0x10000UL, 23), (0x20000UL, 14), (0x30000UL, 23) })
        {
            var bytes = memory.Map(address, 24);
            BitConverter.GetBytes(Module + 0x4ECD708).CopyTo(bytes, 0);
            BitConverter.GetBytes(id).CopyTo(bytes, 0x10);
        }
        var reader = new NationAgreements(memory, Profile, Module);
        var rows = reader.PreloadRegions(() => [0x10000, 0x20000, 0x30000]);
        Assert.Equal(new[] { new AgreementRegion(0x20000, 14, "Uk And Ireland"), new AgreementRegion(0x10000, 23, "23") }, rows);
        Assert.Same(rows, reader.Regions());
    }

    [Fact]
    public void Failed_preload_is_retained_instead_of_becoming_a_tab_scan()
    {
        var reader = new NationAgreements(new FakeMemory(), Profile, Module);
        var error = new MemoryAccessException("fixture failed");
        Assert.Same(error, Assert.Throws<MemoryAccessException>(() => reader.PreloadRegions(() => throw error)));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => reader.Regions()).InnerException);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => reader.PreloadRegions(() => throw new Exception("unexpected retry"))).InnerException);
    }

    [Fact]
    public void Empty_preload_stays_empty_without_region_enumeration()
    {
        var reader = new NationAgreements(new FakeMemory(), Profile, Module);
        Assert.Empty(reader.PreloadRegions(() => []));
        Assert.Empty(reader.Regions());
    }
}
