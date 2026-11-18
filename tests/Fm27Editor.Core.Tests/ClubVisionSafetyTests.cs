using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Clubs;

namespace Fm27Editor.Core.Tests;

public class ClubVisionSafetyTests
{
    [Fact]
    public void Every_growth_write_failure_preserves_the_original_vector()
    {
        for (int failure = 1; failure <= 3; failure++)
        {
            var (memory, profile, team, header) = VisionFixture();
            var before = memory.Read(header, 24);
            var reader = new ClubListReader(memory, profile, 0x7FFB18130000);
            var fault = new WriteFaultMemory(memory) { Failure = failure };
            ulong next = 0x71000000;
            ulong Allocate(int n) { ulong at = next; next += 0x10000; memory.Map(at, n); return at; }
            Assert.Throws<MemoryAccessException>(() => new ClubVisionWriter(fault, profile, 0x7FFB18130000)
                .Create(team, reader.ReadClubVision(team).Single(), 44, 5, 0, new(2026, 1, 1), new(2027, 1, 1), Allocate));
            Assert.Equal(before, memory.Read(header, 24));
            Assert.Single(reader.ReadClubVision(team));
        }
    }

    [Fact]
    public void Invalid_age_and_reversed_dates_are_rejected_before_allocation()
    {
        var (memory, profile, team, _) = VisionFixture();
        var reader = new ClubListReader(memory, profile, 0x7FFB18130000);
        var writer = new ClubVisionWriter(memory, profile, 0x7FFB18130000);
        var entry = reader.ReadClubVision(team).Single();
        ulong Allocate(int n) => throw new Exception("Must not allocate");
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Create(team, entry, 104, 5, -100, new(2026, 1, 1), new(2027, 1, 1), Allocate));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Create(team, entry, 104, 5, 21, new(2030, 1, 1), new(2020, 1, 1), Allocate));
    }

    [Fact]
    public void Multi_field_edit_uses_one_snapshot_and_rolls_back_as_one_unit()
    {
        var (memory, profile, team, _) = VisionFixture();
        var reader = new ClubListReader(memory, profile, 0x7FFB18130000);
        var entry = reader.ReadClubVision(team).Single();
        var writer = new ClubVisionWriter(memory, profile, 0x7FFB18130000);
        writer.ApplyEdits(team, [new(entry, Importance: 7, Dates: (new(2027, 2, 3), new(2028, 4, 5)))]);
        var result = reader.ReadClubVision(team).Single();
        Assert.Equal(7, result.Importance);
        Assert.Equal(new DateOnly(2028, 4, 5), result.EndDate);
    }
    static (FakeMemory, OffsetProfile, ulong, ulong) VisionFixture()
    {
        // Captured Sheffield layout used by ClubListReaderTests, with a full one-entry vector.
        const string raw = "C805C41CFB7F0000FFFF7FFF0000000008DD8E9372020000000000000000000001006C0701006C07FFFFFFFF9605000001006C07FFFFFFFF02000000020000000000000000000000D047179600F70590";
        const ulong module = 0x7FFB18130000, team = 0x50010000, detail = 0x50020000, holder = 0x50030000;
        const ulong header = 0x50040000, vector = 0x60000000, record = 0x70000000;
        var memory = new FakeMemory();
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.0+2493426-steam.json"));
        var t = memory.Map(team, 0xB8); TestWorld.Set64(t, 0, module + 0x4CE3108); TestWorld.Set64(t, 0xA0, detail);
        TestWorld.Set64(memory.Map(detail, 0xD0), 0xC8, holder);
        var h = memory.Map(holder, 0x80); TestWorld.Set64(h, 0, module + 0x4DBCC98); TestWorld.Set64(h, 0x78, header);
        var v = memory.Map(header, 24); TestWorld.Set64(v, 0, vector); TestWorld.Set64(v, 8, vector + 8); TestWorld.Set64(v, 16, vector + 8);
        TestWorld.Set64(memory.Map(vector, 8), 0, record); Convert.FromHexString(raw).CopyTo(memory.Map(record, 0x50), 0);
        return (memory, profile, team, header);
    }

}
