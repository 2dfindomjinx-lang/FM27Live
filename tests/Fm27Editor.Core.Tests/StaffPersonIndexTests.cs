using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// <c>staff.personIndex</c> is the int32 at the person part +0x8, measured on live Steam FM27 27.0.1 (save 4139481,
/// game date 15/12/2026): Paolo Maldini 0, Michael Reiziger 1, Edgar Davids 2 — the first persons the game registered,
/// each with the index at person +0x8 directly before the id at +0xC (45/47/54), person vtable 0x4E908B8 and staff
/// vtable 0x4E90848 at the record start. Those header ints and vtables are what this fixture recreates.
/// Expected names, Unique ID and Maldini's date come from FMRTE 27's own page for him (search row "Paolo Maldini 58
/// AS Roma", page: Unique ID 45, First Paolo, Last Maldini, Full Name Paolo Cesare Maldini, Born 6/26/1968); FMRTE
/// shows no staff person index anywhere, so the index expectations are the captured +0x8 ints above. The id sits at
/// +0xC as a decoy: reading the wrong field returns 45/47/54 instead of 0/1/2.
/// </summary>
public class StaffPersonIndexTests
{
    // Measured live on both Steam 27.0.1 builds; written as literals so a wrong profile vtable fails the load.
    const ulong ModuleBase = 0x7FF1_0000_0000;
    const uint PersonVtable = 0x4E908B8, StaffVtable = 0x4E90848;

    static readonly (ulong StaffObject, int PersonIndex, int Id, string First, string Last)[] Captured =
    [
        (0x6100_0000, 0, 45, "Paolo", "Maldini"),
        (0x6110_0000, 1, 47, "Michael", "Reiziger"),
        (0x6120_0000, 2, 54, "Edgar", "Davids"),
    ];

    sealed class PersonIndexWorld
    {
        public OffsetProfile Profile { get; }
        public FakeMemory Memory { get; } = new();

        public PersonIndexWorld(string profileFile)
        {
            Profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", profileFile));

            ulong vector = 0x6300_0000;
            var entries = Memory.Map(vector, Captured.Length * 8);
            for (int i = 0; i < Captured.Length; i++)
                TestWorld.Set64(entries, i * 8, Captured[i].StaffObject);

            ulong root = ModuleBase + Profile.Staff!.PersonListRootRva;
            var r = Memory.Map(root, 16);
            TestWorld.Set64(r, 0, vector); TestWorld.Set64(r, 8, vector + (ulong)entries.Length);

            byte[]? maldini = null;
            for (int i = 0; i < Captured.Length; i++)
            {
                var (staffObject, index, id, first, last) = Captured[i];
                ulong person = staffObject + 0x100; // kind.StaffPart on both Steam 27.0.1 profiles

                var st = Memory.Map(staffObject, 0xE0);
                TestWorld.Set64(st, 0, ModuleBase + StaffVtable);

                var p = Memory.Map(person, 0x140);
                TestWorld.Set64(p, 0, ModuleBase + PersonVtable);
                BitConverter.GetBytes(index).CopyTo(p, 0x8);   // staff.personIndex
                BitConverter.GetBytes(id).CopyTo(p, 0xC);      // staff.id (the decoy field)

                ulong firstEntry = 0x6200_0000 + (ulong)(i * 0x100), lastEntry = firstEntry + 0x10;
                Memory.MapNameEntry(firstEntry, firstEntry + 0x20, first);
                Memory.MapNameEntry(lastEntry, lastEntry + 0x40, last);
                TestWorld.Set64(p, 0x50, firstEntry);
                TestWorld.Set64(p, 0x58, lastEntry);

                if (i == 0) maldini = p;
            }

            // FMRTE 27: Paolo Maldini, Full Name "Paolo Cesare Maldini", Born 6/26/1968.
            Memory.MapString(0x6210_0000, "Paolo Cesare Maldini");
            TestWorld.Set64(maldini!, 0x40, 0x6210_0000);
            short day = (short)new DateOnly(1968, 6, 26).DayOfYear; // day-of-year encoding, 1968 is a leap year
            BitConverter.GetBytes(day).CopyTo(maldini!, 0x88);
            BitConverter.GetBytes((short)1968).CopyTo(maldini!, 0x8A);
        }

        public StaffRepository Repo() => new(Memory, Profile, ModuleBase);
    }

    [Theory]
    [InlineData("27.0.1+2494687-steam.json")]
    [InlineData("27.0.1+2503222-steam.json")]
    public void Reads_the_captured_person_index_next_to_the_id(string profileFile)
    {
        var w = new PersonIndexWorld(profileFile);
        Assert.Equal(8, w.Profile.Staff!.PersonIndex);

        var staff = w.Repo().LoadAll();
        Assert.Equal(Captured.Length, staff.Count);
        foreach (var (staffObject, index, id, first, last) in Captured)
        {
            var s = Assert.Single(staff, x => x.Address == staffObject + 0x100);
            Assert.Equal(index, s.PersonIndex);
            Assert.Equal(id, s.Id);
            Assert.Equal($"{first} {last}", s.DisplayName);
        }
    }

    [Theory]
    [InlineData("27.0.1+2494687-steam.json")]
    [InlineData("27.0.1+2503222-steam.json")]
    public void Maldini_carries_the_values_FMRTE_shows_for_him(string profileFile)
    {
        var s = Assert.Single(new PersonIndexWorld(profileFile).Repo().LoadAll(), x => x.Id == 45);
        Assert.Equal(0, s.PersonIndex);
        Assert.Equal("Paolo", s.FirstName);
        Assert.Equal("Maldini", s.LastName);
        Assert.Equal("Paolo Cesare Maldini", s.FullName);
        Assert.Equal("Paolo Maldini", s.DisplayName);
        Assert.Equal(new DateOnly(1968, 6, 26), s.DateOfBirth);
    }
}
