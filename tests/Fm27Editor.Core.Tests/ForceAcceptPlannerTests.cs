using Fm27Editor.Core.ForceAccept;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// The byte sequences below are FM27Studio's patch constants (BoardRequestService and NegotiationBypassService),
/// cross-checked against the Cheat Engine table's force-accept preset scripts and found unchanged in the live FM27
/// image. The RVAs here are made up — they only exercise the planner against fake memory, and the shipped profiles
/// carry no force-accept section until the sites are measured on the supported builds.
/// The contract preset is the deliberate exception: its Cheat Engine reference is a guarded hook, so it is not
/// implemented and must stay unavailable whatever bytes the site holds.
/// Planning reads only: every test asserts the fake memory saw no write.
/// </summary>
public class ForceAcceptPlannerTests
{
    const ulong Base = TestWorld.ModuleBase;
    const uint GeneralRequestsRva = 0x1000, ClubVisionRva = 0x1400, ContractRva = 0x1800, TransferRva = 0x1C00;

    static readonly byte[] Gate1Original = [0x0F, 0x85, 0xFD, 0x00, 0x00, 0x00];
    static readonly byte[] Gate1Patched = [0x90, 0x90, 0x90, 0x90, 0x90, 0x90];
    static readonly byte[] Gate2Original = [0x78, 0x7C];
    static readonly byte[] Gate2Patched = [0x90, 0x90];
    static readonly byte[] ContractOriginal =
        [0x41, 0x89, 0xC7, 0x48, 0x8B, 0xB5, 0x78, 0x10, 0x00, 0x00, 0x89, 0x86, 0xD0, 0x1E, 0x00, 0x00];
    static readonly byte[] ContractPatched =
        [0x48, 0x8B, 0xB5, 0x78, 0x10, 0x00, 0x00, 0xB8, 0x87, 0x2F, 0x02, 0x00, 0x41, 0x89, 0xC7, 0x90];
    static readonly byte[] TransferOriginal = [0x44, 0x0F, 0xB6, 0xAF, 0x00, 0x08, 0x00, 0x00];
    static readonly byte[] TransferPatched = [0x41, 0xBD, 0x03, 0x00, 0x00, 0x00, 0x90, 0x90];

    const string AllRvas =
        """{"boardRequestsRva": "0x1000", "clubVisionRva": "0x1400", "contractOffersRva": "0x1800", "transferOffersRva": "0x1C00"}""";

    static string ShippedJson => File.ReadAllText(TestWorld.ProfilePath);

    /// <summary>The shipped profile plus one synthetic <c>forceAccept</c> section.</summary>
    static OffsetProfile ProfileWith(string forceAcceptJson)
    {
        var json = ShippedJson.TrimEnd();
        int close = json.LastIndexOf('}');
        return OffsetProfile.Load(json[..close] + ",\"forceAccept\":" + forceAcceptJson + json[close..]);
    }

    static ForceAcceptPlanner Planner(OffsetProfile profile, FakeMemory memory) => new(profile, memory, Base);

    static void Put(FakeMemory memory, ulong address, byte[] bytes) =>
        bytes.CopyTo(memory.Map(address, bytes.Length), 0);

    [Fact]
    public void Board_plan_reports_both_sites_and_the_nop_bytes_they_would_get()
    {
        var memory = new FakeMemory();
        Put(memory, Base + GeneralRequestsRva, Gate1Original);
        Put(memory, Base + ClubVisionRva, Gate2Original);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.BoardRequests);

        Assert.Equal(ForceAcceptPlanState.Appliable, plan.State);
        Assert.Equal(2, plan.Sites.Count);
        var gate1 = plan.Sites[0];
        Assert.Equal("generalRequests", gate1.Key);
        Assert.Equal(GeneralRequestsRva, gate1.Rva);
        Assert.Equal(Base + GeneralRequestsRva, gate1.Address);
        Assert.Equal(ForceAcceptSiteState.Ready, gate1.State);
        Assert.Equal(Gate1Original, gate1.Original);
        Assert.Equal(Gate1Patched, gate1.Replacement);
        Assert.Equal(Gate1Original, gate1.Current);
        var gate2 = plan.Sites[1];
        Assert.Equal("clubVision", gate2.Key);
        Assert.Equal(Base + ClubVisionRva, gate2.Address);
        Assert.Equal(ForceAcceptSiteState.Ready, gate2.State);
        Assert.Equal(Gate2Original, gate2.Original);
        Assert.Equal(Gate2Patched, gate2.Replacement);
        Assert.Equal(2, plan.Pending.Count);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Board_preset_is_active_when_both_gates_already_hold_the_nops()
    {
        var memory = new FakeMemory();
        Put(memory, Base + GeneralRequestsRva, Gate1Patched);
        Put(memory, Base + ClubVisionRva, Gate2Patched);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.BoardRequests);

        Assert.Equal(ForceAcceptPlanState.Active, plan.State);
        Assert.All(plan.Sites, s => Assert.Equal(ForceAcceptSiteState.AlreadyApplied, s.State));
        Assert.Empty(plan.Pending);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Board_preset_with_one_gate_applied_is_appliable_for_the_other_only()
    {
        var memory = new FakeMemory();
        Put(memory, Base + GeneralRequestsRva, Gate1Patched);
        Put(memory, Base + ClubVisionRva, Gate2Original);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.BoardRequests);

        Assert.Equal(ForceAcceptPlanState.Appliable, plan.State);
        Assert.Equal(ForceAcceptSiteState.AlreadyApplied, plan.Sites[0].State);
        var pending = Assert.Single(plan.Pending);
        Assert.Equal("clubVision", pending.Key);
        Assert.Equal(Gate2Original, pending.Original);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Board_preset_with_foreign_bytes_at_one_gate_is_blocked()
    {
        var foreign = new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC };
        var memory = new FakeMemory();
        Put(memory, Base + GeneralRequestsRva, foreign);
        Put(memory, Base + ClubVisionRva, Gate2Original);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.BoardRequests);

        Assert.Equal(ForceAcceptPlanState.Blocked, plan.State);
        Assert.Equal(ForceAcceptSiteState.Mismatch, plan.Sites[0].State);
        Assert.Equal(foreign, plan.Sites[0].Current);
        Assert.Equal(ForceAcceptSiteState.Ready, plan.Sites[1].State);
        Assert.Empty(plan.Pending);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Contract_preset_is_not_implemented_and_reports_unavailable()
    {
        var memory = new FakeMemory();
        Put(memory, Base + ContractRva, ContractOriginal);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.ContractOffers);

        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        var site = Assert.Single(plan.Sites);
        Assert.Equal("contractStatus", site.Key);
        Assert.Equal(ForceAcceptSiteState.NotImplemented, site.State);
        // The site is never read: no byte pattern can enable a preset that has no implemented write.
        Assert.Null(site.Current);
        Assert.Empty(plan.Pending);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Contract_preset_stays_unavailable_even_when_the_site_holds_the_forced_bytes()
    {
        var memory = new FakeMemory();
        Put(memory, Base + ContractRva, ContractPatched);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.ContractOffers);

        // Not Active: memory content cannot enable the preset, so the forced bytes must not be mistaken for it.
        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        Assert.Equal(ForceAcceptSiteState.NotImplemented, Assert.Single(plan.Sites).State);
        Assert.Empty(plan.Pending);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Contract_preset_stays_unavailable_even_when_the_site_holds_foreign_bytes()
    {
        var foreign = Enumerable.Repeat((byte)0xAA, ContractOriginal.Length).ToArray();
        var memory = new FakeMemory();
        Put(memory, Base + ContractRva, foreign);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.ContractOffers);

        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        Assert.Equal(ForceAcceptSiteState.NotImplemented, Assert.Single(plan.Sites).State);
        Assert.Null(Assert.Single(plan.Sites).Current);
        Assert.Empty(plan.Pending);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Transfer_preset_replaces_the_answer_load_with_three()
    {
        var memory = new FakeMemory();
        Put(memory, Base + TransferRva, TransferOriginal);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.TransferOffers);

        Assert.Equal(ForceAcceptPlanState.Appliable, plan.State);
        var site = Assert.Single(plan.Sites);
        Assert.Equal("transferStatus", site.Key);
        Assert.Equal(ForceAcceptSiteState.Ready, site.State);
        Assert.Equal(TransferOriginal, site.Current);
        Assert.Equal(TransferPatched, site.Replacement);
        // `mov r13d, imm32` at offset 1 forces the answer 3 — the value the Cheat Engine preset forces as well.
        Assert.Equal(0x41, site.Replacement[0]);
        Assert.Equal(3, BitConverter.ToInt32(site.Replacement, 2));
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Transfer_preset_is_blocked_when_the_site_holds_other_bytes()
    {
        var foreign = Enumerable.Repeat((byte)0xAA, TransferOriginal.Length).ToArray();
        var memory = new FakeMemory();
        Put(memory, Base + TransferRva, foreign);

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.TransferOffers);

        Assert.Equal(ForceAcceptPlanState.Blocked, plan.State);
        Assert.Equal(ForceAcceptSiteState.Mismatch, Assert.Single(plan.Sites).State);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void A_profile_without_force_accept_offsets_reports_unavailable()
    {
        var memory = new FakeMemory();

        var plan = Planner(OffsetProfile.Load(ShippedJson), memory).Plan(ForceAcceptPreset.TransferOffers);

        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        var site = Assert.Single(plan.Sites);
        Assert.Equal(ForceAcceptSiteState.MissingOffset, site.State);
        Assert.Null(site.Rva);
        Assert.Null(site.Address);
        Assert.Null(site.Current);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void A_missing_rva_makes_the_whole_preset_unavailable_even_when_the_other_site_is_ready()
    {
        var memory = new FakeMemory();
        Put(memory, Base + GeneralRequestsRva, Gate1Original);

        var plan = Planner(ProfileWith("""{"boardRequestsRva": "0x1000"}"""), memory)
            .Plan(ForceAcceptPreset.BoardRequests);

        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        Assert.Equal(ForceAcceptSiteState.Ready, plan.Sites[0].State);
        Assert.Equal(ForceAcceptSiteState.MissingOffset, plan.Sites[1].State);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void An_unreadable_site_blocks_the_preset()
    {
        var memory = new FakeMemory(); // nothing mapped: every read fails

        var plan = Planner(ProfileWith(AllRvas), memory).Plan(ForceAcceptPreset.TransferOffers);

        Assert.Equal(ForceAcceptPlanState.Blocked, plan.State);
        var site = Assert.Single(plan.Sites);
        Assert.Equal(ForceAcceptSiteState.Unreadable, site.State);
        Assert.Null(site.Current);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Planning_all_three_presets_never_writes_to_memory()
    {
        var memory = new FakeMemory();
        Put(memory, Base + GeneralRequestsRva, Gate1Original);
        Put(memory, Base + ClubVisionRva, Gate2Original);
        Put(memory, Base + ContractRva, ContractOriginal);
        Put(memory, Base + TransferRva, TransferOriginal);
        var planner = Planner(ProfileWith(AllRvas), memory);

        var board = planner.Plan(ForceAcceptPreset.BoardRequests);
        var contract = planner.Plan(ForceAcceptPreset.ContractOffers);
        var transfer = planner.Plan(ForceAcceptPreset.TransferOffers);

        Assert.Equal(ForceAcceptPlanState.Appliable, board.State);
        Assert.Equal(ForceAcceptPlanState.Unavailable, contract.State);   // guarded hook, not implemented
        Assert.Equal(ForceAcceptPlanState.Appliable, transfer.State);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void Force_accept_offsets_survive_a_json_round_trip()
    {
        var copy = OffsetProfile.Load(ProfileWith(AllRvas).ToJson());

        Assert.Equal(0x1000u, copy.ForceAccept!.BoardRequestsRva);
        Assert.Equal(0x1400u, copy.ForceAccept.ClubVisionRva);
        Assert.Equal(0x1800u, copy.ForceAccept.ContractOffersRva);
        Assert.Equal(0x1C00u, copy.ForceAccept.TransferOffersRva);
    }

    [Fact]
    public void Shipped_profiles_carry_no_force_accept_offsets_yet()
    {
        // The sites are not measured on the supported builds, so the section stays out of the shipped profiles and
        // every preset reports Unavailable there instead of planning against a guessed address.
        Assert.Null(OffsetProfile.Load(ShippedJson).ForceAccept);
    }

    [Fact]
    public void Board_preset_uses_per_build_original_bytes_and_restores_them_cleanly()
    {
        // Captured from live FM27 Steam 27.1.0+2505117 at RVA 0x159E0B7 (0F 85 07 01 00 00) and RVA 0x1D95C0F (78 78).
        byte[] fm27Gate1 = [0x0F, 0x85, 0x07, 0x01, 0x00, 0x00];
        byte[] fm27Gate2 = [0x78, 0x78];
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json"));
        Assert.NotNull(profile.ForceAccept);
        Assert.Equal(0x159E0B7u, profile.ForceAccept!.BoardRequestsRva);
        Assert.Equal(fm27Gate1, profile.ForceAccept.BoardRequestsOriginal);
        Assert.Equal(0x1D95C0Fu, profile.ForceAccept.ClubVisionRva);
        Assert.Equal(fm27Gate2, profile.ForceAccept.ClubVisionOriginal);

        var copy = OffsetProfile.Load(profile.ToJson());
        Assert.Equal(fm27Gate1, copy.ForceAccept!.BoardRequestsOriginal);
        Assert.Equal(fm27Gate2, copy.ForceAccept.ClubVisionOriginal);

        var memory = new FakeMemory();
        Put(memory, Base + 0x159E0B7u, fm27Gate1);
        Put(memory, Base + 0x1D95C0Fu, fm27Gate2);
        var planner = Planner(copy, memory);

        var plan = planner.Plan(ForceAcceptPreset.BoardRequests);
        Assert.Equal(ForceAcceptPlanState.Appliable, plan.State);
        Assert.Equal(2, planner.Apply(plan, gamePaused: true));
        Assert.Equal(Gate1Patched, memory.Read(Base + 0x159E0B7u, 6));
        Assert.Equal(Gate2Patched, memory.Read(Base + 0x1D95C0Fu, 2));

        var active = planner.Plan(ForceAcceptPreset.BoardRequests);
        Assert.Equal(ForceAcceptPlanState.Active, active.State);
        Assert.Equal(2, planner.Restore(active, gamePaused: true));
        Assert.Equal(fm27Gate1, memory.Read(Base + 0x159E0B7u, 6));
        Assert.Equal(fm27Gate2, memory.Read(Base + 0x1D95C0Fu, 2));
    }

    [Fact]
    public void Auto_discovers_force_board_sites_from_captured_fm27_code_slices()
    {
        // Real instruction bytes captured around RVA 0x159E0A8 and RVA 0x1D95C05 on Steam 27.1.0+2505117.
        var code = new byte[0x200];
        Convert.FromHexString("4180BE8100000001488985300300000F85070100004989C6").CopyTo(code, 0x20);
        Convert.FromHexString("0FBF46620FB64E6B01C17878C6466704").CopyTo(code, 0x100);

        bool ok = NewBuildDiscoverer.TryFindForceBoardSitesInCode(
            code, 0x1000u, out uint g1Rva, out byte[] g1Orig, out uint g2Rva, out byte[] g2Orig);

        Assert.True(ok);
        Assert.Equal(0x1000u + 0x20u + 15u, g1Rva);
        Assert.Equal([0x0F, 0x85, 0x07, 0x01, 0x00, 0x00], g1Orig);
        Assert.Equal(0x1000u + 0x100u + 10u, g2Rva);
        Assert.Equal([0x78, 0x78], g2Orig);
    }

    [Fact]
    public void Board_preset_supports_gamepass_2710_measured_sites_and_auto_discovery()
    {
        // Captured from live FM27 Game Pass 27.1.0+2505117 at RVA 0x14630B4 (0F 85 F7 00 00 00) and RVA 0x1BD6825 (78 79).
        byte[] gpGate1 = [0x0F, 0x85, 0xF7, 0x00, 0x00, 0x00];
        byte[] gpGate2 = [0x78, 0x79];
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-gamepass.json"));
        Assert.NotNull(profile.ForceAccept);
        Assert.Equal(0x14630B4u, profile.ForceAccept!.BoardRequestsRva);
        Assert.Equal(gpGate1, profile.ForceAccept.BoardRequestsOriginal);
        Assert.Equal(0x1BD6825u, profile.ForceAccept.ClubVisionRva);
        Assert.Equal(gpGate2, profile.ForceAccept.ClubVisionOriginal);

        var memory = new FakeMemory();
        Put(memory, Base + 0x14630B4u, gpGate1);
        Put(memory, Base + 0x1BD6825u, gpGate2);
        var planner = Planner(profile, memory);

        var plan = planner.Plan(ForceAcceptPreset.BoardRequests);
        Assert.Equal(ForceAcceptPlanState.Appliable, plan.State);
        Assert.Equal(2, planner.Apply(plan, gamePaused: true));
        Assert.Equal(Gate1Patched, memory.Read(Base + 0x14630B4u, 6));
        Assert.Equal(Gate2Patched, memory.Read(Base + 0x1BD6825u, 2));

        var active = planner.Plan(ForceAcceptPreset.BoardRequests);
        Assert.Equal(ForceAcceptPlanState.Active, active.State);
        Assert.Equal(2, planner.Restore(active, gamePaused: true));
        Assert.Equal(gpGate1, memory.Read(Base + 0x14630B4u, 6));
        Assert.Equal(gpGate2, memory.Read(Base + 0x1BD6825u, 2));

        var restored = planner.Plan(ForceAcceptPreset.BoardRequests);
        Assert.Equal(ForceAcceptPlanState.Appliable, restored.State);

        var code = new byte[0x200];
        Convert.FromHexString("4180BE8100000001488985480300000F85F7000000").CopyTo(code, 0x20);
        Convert.FromHexString("0FBF46620FB64E6B01C17879C6466704").CopyTo(code, 0x100);
        bool ok = NewBuildDiscoverer.TryFindForceBoardSitesInCode(
            code, 0x1000u, out uint g1Rva, out byte[] g1Orig, out uint g2Rva, out byte[] g2Orig);

        Assert.True(ok);
        Assert.Equal(0x1000u + 0x20u + 15u, g1Rva);
        Assert.Equal(gpGate1, g1Orig);
        Assert.Equal(0x1000u + 0x100u + 10u, g2Rva);
        Assert.Equal(gpGate2, g2Orig);
    }
}
