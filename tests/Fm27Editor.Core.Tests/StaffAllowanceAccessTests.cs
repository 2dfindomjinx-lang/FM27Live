using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// Independent oracle: live bytes captured from FM27 Steam 27.1.0+2505117 on Manchester United (club id 680),
/// where <c>team[0]+0x80</c> points to the human manager's staff part (<c>db::HUMAN_NON_PLAYER</c>, vtable
/// <c>0x4E2170C</c>), <c>staff+0x388</c> points to <c>Mscc</c>, and <c>Mscc+0x116C..0x1180</c> holds the 21-byte
/// extra-staff block (<c>00..00 FF FF FF 00 00 00</c> at baseline; writing <c>+3</c> at <c>+0x116C</c>, <c>+4</c> at
/// <c>+0x116F</c>, and <c>+5</c> at <c>+0x117F</c> moved FM's Staff Overview screen from <c>13/13</c>, <c>7/6</c>,
/// <c>16/16</c> to <c>13/16</c>, <c>7/10</c>, <c>16/21</c>).
/// </summary>
public class StaffAllowanceAccessTests
{
    const ulong Base = TestWorld.ModuleBase;
    const ulong ClubAddr = 0x2765_1736_CD00;
    const ulong TeamsVecAddr = 0x2765_1737_0000;
    const ulong FirstTeamAddr = 0x2765_706D_5C00;
    const ulong HumanStaffAddr = 0x2766_4AB7_3700;
    const ulong AiStaffAddr = 0x2766_4AB8_0000;
    const ulong MsccAddr = 0x2767_0847_F600;

    static readonly string Steam2710Path = Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json");

    // Captured 21-byte Mscc+0x116C..0x1180 block after writing Coach_Sr=3, Physio_Sr=4, Scouts=5 on Man Utd.
    static readonly byte[] CapturedPatchedBlock = Convert.FromHexString("030000040000000000000000000000FFFFFF000500");

    // Captured machine code from game_plugin.dll (Steam 27.1.0+2505117) at RVA 0x3AB9ABA..0x3AB9B85:
    // Coaches clamp [0..10] at +0x116C followed by Physios clamp [0..10] at +0x116F.
    static readonly byte[] CapturedAllowanceCodeSlice = Convert.FromHexString(
        "488B88880300000FB68C296C1100000FB6C984C941B900000000410F4EC980F90A41B90A000000410F4DC9" +
        "488B5E104885DB4889AC2480000000745A448B4B084D39CC735180BC249000000002750A4180FA03720A31EDEB09" +
        "41F6C30174F64C89E54989DF4C8B0B48C1E50441024C29060FB6C984C9BB000000000F4ECB4C89FB41884C2906" +
        "4C8BBC24A8000000488BAC24800000004180FD027E0431C9EB0F" +
        "488B88880300000FB68C296F1100000FB6C984C941B900000000410F4EC980F90A41B90A000000410F4DC9");

    static (OffsetProfile Profile, FakeMemory Memory, StaffAllowanceAccess Access) BuildWorld(byte[]? initialBlock = null)
    {
        var profile = OffsetProfile.LoadFile(Steam2710Path);
        var memory = new FakeMemory();
        uint clubVt = profile.Club!.ClubVtableRva;
        uint humanStaffVt = profile.Staff!.Kinds.First(k => k.Manager).StaffVtableRva;
        uint aiStaffVt = profile.Staff.Kinds.First(k => !k.Manager).StaffVtableRva;

        var club = memory.Map(ClubAddr, 0x40);
        TestWorld.Set64(club, 0x00, Base + clubVt);
        TestWorld.Set64(club, 0x18, TeamsVecAddr);
        TestWorld.Set64(club, 0x20, TeamsVecAddr + 8);

        var teamsVec = memory.Map(TeamsVecAddr, 8);
        TestWorld.Set64(teamsVec, 0x00, FirstTeamAddr);

        var team = memory.Map(FirstTeamAddr, 0x100);
        TestWorld.Set64(team, 0x80, HumanStaffAddr);

        var human = memory.Map(HumanStaffAddr, 0x440);
        TestWorld.Set64(human, 0x00, Base + humanStaffVt);
        TestWorld.Set64(human, 0x388, MsccAddr);

        var ai = memory.Map(AiStaffAddr, 0x100);
        TestWorld.Set64(ai, 0x00, Base + aiStaffVt);

        var mscc = memory.Map(MsccAddr, 0x1200);
        var block = initialBlock ?? CapturedPatchedBlock;
        block.CopyTo(mscc, 0x116C);

        return (profile, memory, new StaffAllowanceAccess(memory, profile, Base));
    }

    [Fact]
    public void Reads_captured_extra_staff_allowances_for_human_managed_club()
    {
        var (_, memory, access) = BuildWorld();

        var snap = access.ReadForClub(ClubAddr);

        Assert.NotNull(snap);
        Assert.Equal(HumanStaffAddr, snap!.HumanStaffAddress);
        Assert.Equal(MsccAddr, snap.MsccAddress);
        Assert.Equal(3, snap[StaffAllowanceField.CoachesFirstTeam]);
        Assert.Equal(0, snap[StaffAllowanceField.CoachesU21]);
        Assert.Equal(0, snap[StaffAllowanceField.CoachesU18]);
        Assert.Equal(4, snap[StaffAllowanceField.PhysiosFirstTeam]);
        Assert.Equal(0, snap[StaffAllowanceField.PhysiosU21]);
        Assert.Equal(0, snap[StaffAllowanceField.PhysiosU18]);
        Assert.Equal(0, snap[StaffAllowanceField.SportsScientistsFirstTeam]);
        Assert.Equal(0, snap[StaffAllowanceField.PerformanceAnalystsFirstTeam]);
        Assert.Equal(5, snap[StaffAllowanceField.Scouts]);
        Assert.Equal(0, snap[StaffAllowanceField.RecruitmentAnalysts]);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Returns_null_for_ai_managed_club()
    {
        var (_, memory, access) = BuildWorld();
        var team = memory.Read(FirstTeamAddr, 0x100);
        TestWorld.Set64(team, 0x80, AiStaffAddr);
        memory.TryWrite(FirstTeamAddr, team);
        int writesBefore = memory.Writes;

        var snap = access.ReadForClub(ClubAddr);

        Assert.Null(snap);
        Assert.Equal(writesBefore, memory.Writes);
    }

    [Fact]
    public void Guarded_write_updates_target_byte_and_preserves_sentinel_bytes()
    {
        var (_, memory, access) = BuildWorld();
        var snap = access.ReadForClub(ClubAddr)!;

        access.Set(snap, StaffAllowanceField.Scouts, 12);
        access.Set(snap, StaffAllowanceField.RecruitmentAnalysts, 5);
        access.Set(snap, StaffAllowanceField.SportsScientistsU18, 4);

        var after = access.ReadForClub(ClubAddr)!;
        Assert.Equal(12, after[StaffAllowanceField.Scouts]);
        Assert.Equal(5, after[StaffAllowanceField.RecruitmentAnalysts]);
        Assert.Equal(4, after[StaffAllowanceField.SportsScientistsU18]);
        Assert.Equal(3, after[StaffAllowanceField.CoachesFirstTeam]);
        Assert.Equal(4, after[StaffAllowanceField.PhysiosFirstTeam]);
        // Sentinel bytes at +0x0F..0x11 (+0x117B..0x117D) remain 0xFF 0xFF 0xFF.
        Assert.Equal(0xFF, after.RawBlock[0x0F]);
        Assert.Equal(0xFF, after.RawBlock[0x10]);
        Assert.Equal(0xFF, after.RawBlock[0x11]);
    }

    [Theory]
    [InlineData(StaffAllowanceField.CoachesFirstTeam, -1)]
    [InlineData(StaffAllowanceField.CoachesFirstTeam, 11)]
    [InlineData(StaffAllowanceField.PhysiosU21, 11)]
    [InlineData(StaffAllowanceField.SportsScientistsFirstTeam, 6)]
    [InlineData(StaffAllowanceField.PerformanceAnalystsU18, 6)]
    [InlineData(StaffAllowanceField.Scouts, 16)]
    [InlineData(StaffAllowanceField.RecruitmentAnalysts, 6)]
    public void Rejects_out_of_range_values_without_writing(StaffAllowanceField field, int badValue)
    {
        var (_, memory, access) = BuildWorld();
        var snap = access.ReadForClub(ClubAddr)!;

        Assert.Throws<ArgumentOutOfRangeException>(() => access.Set(snap, field, badValue));
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Rejects_write_when_snapshot_byte_is_stale()
    {
        var (_, memory, access) = BuildWorld();
        var snap = access.ReadForClub(ClubAddr)!;
        memory.TryWrite(MsccAddr + 0x116C, [7]);
        int writesBefore = memory.Writes;

        Assert.Throws<StaleObjectException>(() => access.Set(snap, StaffAllowanceField.CoachesFirstTeam, 9));
        Assert.Equal(writesBefore, memory.Writes);
        Assert.Equal(7, memory.Read(MsccAddr + 0x116C, 1)[0]);
    }

    [Fact]
    public void Rejects_write_when_human_manager_vtable_changes()
    {
        var (_, memory, access) = BuildWorld();
        var snap = access.ReadForClub(ClubAddr)!;
        memory.TryWrite(HumanStaffAddr, BitConverter.GetBytes(Base + 0x1234560u));
        int writesBefore = memory.Writes;

        Assert.Throws<StaleObjectException>(() => access.Set(snap, StaffAllowanceField.CoachesFirstTeam, 5));
        Assert.Equal(writesBefore, memory.Writes);
    }

    [Fact]
    public void Staff_allowance_offsets_survive_json_round_trip()
    {
        var profile = OffsetProfile.LoadFile(Steam2710Path);
        var copy = OffsetProfile.Load(profile.ToJson());

        Assert.NotNull(copy.Staff?.Allowances);
        Assert.Equal(0x80, copy.Staff!.Allowances!.TeamManager);
        Assert.Equal(0x388, copy.Staff.Allowances.HumanMscc);
        Assert.Equal(0x116C, copy.Staff.Allowances.MsccExtraStaff);
    }

    [Fact]
    public void Auto_discovers_mscc_and_extra_staff_offsets_from_captured_code_slice()
    {
        bool ok = NewBuildDiscoverer.TryFindStaffAllowanceOffsetsInCode(
            CapturedAllowanceCodeSlice, 0x3AB9ABAu, out int mscc, out int mest, out uint matchedRva);

        Assert.True(ok);
        Assert.Equal(0x388, mscc);
        Assert.Equal(0x116C, mest);
        Assert.Equal(0x3AB9ABAu, matchedRva);
    }

    [Fact]
    public void Auto_discovers_mscc_and_extra_staff_offsets_from_captured_gamepass_code_slice()
    {
        // Real machine code captured from Game Pass 27.1.0+2505117 at RVA 0x36FF726..0x36FF7E2.
        byte[] gpSlice = Convert.FromHexString(
            "4C8B9888030000490FBED8458A9C1B6C110000450FB6DB4584DBBF00000000440F4EDF4180FB0ABF0A000000440F4DDF" +
            "4C8B6E104D85ED7448418B5D084939DF733F80BC248800000002750A4180F903720A31DBEB0941F6C20174F64C89FB" +
            "4D8B650048C1E30445025C1C06450FB6DB4584DBBF00000000440F4EDF45885C1C064180F8027E054531DBEB13" +
            "4C8B9888030000490FBED8458A9C1B6F110000450FB6DB4584DBBF00000000440F4EDF4180FB0ABF0A000000440F4DDF");

        bool ok = NewBuildDiscoverer.TryFindStaffAllowanceOffsetsInCode(
            gpSlice, 0x36FF726u, out int mscc, out int mest, out uint matchedRva);

        Assert.True(ok);
        Assert.Equal(0x388, mscc);
        Assert.Equal(0x116C, mest);
        Assert.Equal(0x36FF726u, matchedRva);

        var gpProfile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-gamepass.json"));
        Assert.NotNull(gpProfile.Staff?.Allowances);
        Assert.Equal(0x80, gpProfile.Staff!.Allowances!.TeamManager);
        Assert.Equal(0x388, gpProfile.Staff.Allowances.HumanMscc);
        Assert.Equal(0x116C, gpProfile.Staff.Allowances.MsccExtraStaff);
    }
}
