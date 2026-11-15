using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// Game Pass 27.0.1+2494687 keeps every clause record in its club's vector (club +0xB0), as Steam 27.0.1 does. Measured on the
/// live game with the CLI `clausecheck` (16/11/2026): the 4.4–5.2 s heap scan found 6,011 clause records, and the 5,203 club
/// vectors held exactly those 6,011 (none missing, none extra), next to 5,386 entries of other kinds. 6,002 records point at
/// another club (the beneficiary), so the club's clauses are its vector, not the records that point back at it. The profile
/// therefore reads the vector; a profile that went back to the scan would return nothing here (fake memory has no heap).
/// </summary>
public class GamePassClauseVectorTests
{
    const ulong Module = 0x7FF0_0000_0000, Club = 0x5000_0000, Holder = 0x5001_0000, List = 0x5002_0000,
        Beneficiary = 0x5100_0000, SellOn = 0x6000_0000, Other = 0x6000_1000;

    [Fact]
    public void Game_pass_reads_the_clubs_own_vector()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.1+2494687-gamepass.json"));
        var t = profile.Club!.TransferClauses!;
        Assert.False(t.Scan);
        Assert.Equal(0xB0, t.Pointer);

        var memory = new FakeMemory();
        foreach (ulong club in new[] { Club, Beneficiary })
            TestWorld.Set64(memory.Map(club, 8), 0, Module + profile.Club.ClubVtableRva);
        TestWorld.Set64(memory.Map(Club + 0xB0, 8), 0, Holder);
        var holder = memory.Map(Holder, 0x18);
        TestWorld.Set64(holder, 0, List); TestWorld.Set64(holder, 8, List + 16); TestWorld.Set64(holder, 0x10, List + 16);
        var list = memory.Map(List, 16);
        TestWorld.Set64(list, 0, Other); TestWorld.Set64(list, 8, SellOn);
        TestWorld.Set64(memory.Map(Other, 0x38), 0, Module + 0x1234);   // an entry of another kind, skipped
        var record = memory.Map(SellOn, 0x28);
        TestWorld.Set64(record, 0, Module + t.SellOnVtableRva);
        BitConverter.GetBytes(28049320).CopyTo(record, 8);
        TestWorld.Set64(record, 0x10, Beneficiary);
        BitConverter.GetBytes(-1).CopyTo(record, 0x18);
        BitConverter.GetBytes((short)20).CopyTo(record, 0x22);

        var clause = Assert.Single(new TransferClauseReader(memory, profile, Module).Read(Club));
        Assert.Equal(TransferClauseKind.SellOn, clause.Kind);
        Assert.Equal(28049320, clause.PlayerId);
        Assert.Equal(Beneficiary, clause.Beneficiary?.Address);
        Assert.Equal((short)20, clause.Percentage);
        Assert.Equal(new TransferClauseOrigin(Club, Holder, List, List + 16, List + 16, List + 8), clause.Origin);
    }
}
