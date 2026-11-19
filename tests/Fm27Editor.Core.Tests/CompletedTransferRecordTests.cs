using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Transfer;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// FM's record of Cole Palmer's £50M move Chelsea → Man Utd (In-Game Editor, Steam 27.0.1+2503222, 14/11/2026, offer id 142).
/// Writing the same record for an editor move (Saka → Liverpool) changed Liverpool's FFP by the same amounts as Man Utd's.
/// </summary>
public class CompletedTransferRecordTests
{
    [Theory]
    [InlineData("27.0.1+2494687-gamepass.json", 2, false)]
    [InlineData("27.0.1+2503222-steam.json", 2, false)]
    [InlineData("27.0.1+2494687-gamepass.json", 1, false)]
    [InlineData("27.0.1+2503222-steam.json", 1, false)]
    [InlineData("27.0.1+2494687-gamepass.json", 2, true)]
    [InlineData("27.0.1+2503222-steam.json", 2, true)]
    public void Reuses_holes_or_appends_and_grows_without_losing_existing_offers(string filename, int capacity, bool hole)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", filename));
        var memory = new FakeMemory();
        const ulong module = 0x7FF000000000, root = 0x100000, offers = 0x200000, player = 0x300000;
        var po = profile.Proposals!;
        TestWorld.Set64(memory.Map(module + po.ListRootRva, 8), 0, root);
        var rootBytes = memory.Map(root, 0x1000);
        TestWorld.Set64(rootBytes, po.ListOffset, offers);
        TestWorld.Set64(rootBytes, po.ListOffset + 8, offers + 8);
        TestWorld.Set64(rootBytes, po.ListOffset + 16, offers + (ulong)capacity * 8);
        TestWorld.Set64(memory.Map(offers, capacity * 8), 0, hole ? 0UL : 0x123456);
        TestWorld.Set64(memory.Map(player, 0x1000), 0, module + profile.PlayerVtableRvas[0]); // a real player starts with his vtable
        ulong next = 0x400000;
        ulong Allocate(int size) { ulong at = next; next += 0x10000; memory.Map(at, size); return at; }
        var record = new CompletedTransferRecord(memory, profile, module);
        var plan = record.Plan(player, 0x500000, 0x600000, 1000, new DateOnly(2026, 11, 16));
        Assert.Equal(hole ? 0 : 1, plan.OfferId);
        record.Apply(plan, Allocate);
        ulong begin = memory.ReadUInt64(root + (ulong)po.ListOffset);
        Assert.Equal(begin + (hole ? 8UL : 16UL), memory.ReadUInt64(root + (ulong)po.ListOffset + 8));
        if (!hole) Assert.Equal(0x123456UL, memory.ReadUInt64(begin));
        ulong offer = memory.ReadUInt64(begin + (hole ? 0UL : 8UL));
        Assert.Equal(1000, BitConverter.ToInt32(memory.Read(offer + 0x58, 4)));
    }

    /// <summary>
    /// Game Pass 27.1.0+2505117 (18/11/2026): after a Move now, FM crashed at 10% of a save, copying 4 bytes from the pointer it read
    /// at +0x78 of the record's terms object. The editor had allocated that object as 0x60 bytes, so +0x78 was the next heap
    /// block's header. FM's terms object is TransferTermsLayout.Size (208) bytes: six vectors and the fields from +0xC0.
    /// </summary>
    [Theory]
    [InlineData("27.1.0+2505117-gamepass.json")]
    [InlineData("27.1.0+2505117-steam.json")]
    public void The_terms_object_is_as_large_as_the_games_and_all_zero(string filename)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", filename));
        var memory = new FakeMemory();
        const ulong module = 0x7FF000000000, root = 0x100000, offers = 0x200000, player = 0x300000;
        var po = profile.Proposals!;
        TestWorld.Set64(memory.Map(module + po.ListRootRva, 8), 0, root);
        var rootBytes = memory.Map(root, 0x1000);
        TestWorld.Set64(rootBytes, po.ListOffset, offers);
        TestWorld.Set64(rootBytes, po.ListOffset + 8, offers);
        TestWorld.Set64(rootBytes, po.ListOffset + 16, offers + 16);
        memory.Map(offers, 16);
        TestWorld.Set64(memory.Map(player, 0x1000), 0, module + profile.PlayerVtableRvas[0]); // a real player starts with his vtable
        var sizes = new Dictionary<ulong, int>();
        ulong next = 0x400000;
        ulong Allocate(int size) { ulong at = next; next += 0x10000; memory.Map(at, size); sizes[at] = size; return at; }
        var record = new CompletedTransferRecord(memory, profile, module);
        record.Apply(record.Plan(player, 0x500000, 0x600000, 12_345_678, new DateOnly(2026, 12, 15)), Allocate);
        ulong offer = memory.ReadUInt64(offers);
        ulong terms = memory.ReadUInt64(offer + 0xA8);
        Assert.True(sizes[terms] >= 208, $"terms object is {sizes[terms]} bytes");
        Assert.All(memory.Read(terms, 208), b => Assert.Equal(0, b));
    }

    /// <summary>
    /// FMRTE 27, Game Pass 27.1.0+2505117 (18/11/2026): a future transfer with a 15% sell-on wrote a 208-byte terms
    /// object whose only non-zero byte was +0xC4 = 0x0F, and no record in the buyer club's clause vector. A transfer's
    /// sell-on belongs in the completed offer terms, not in that vector; a club clause record added there survived Save
    /// As but crashed FM on the next load (the same day).
    /// </summary>
    [Theory]
    [InlineData("27.1.0+2505117-gamepass.json")]
    [InlineData("27.1.0+2505117-steam.json")]
    public void A_move_sell_on_is_written_in_the_208_byte_terms_object_at_c4(string filename)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", filename));
        var memory = new FakeMemory();
        const ulong module = 0x7FF000000000, root = 0x100000, offers = 0x200000, player = 0x300000;
        var po = profile.Proposals!;
        TestWorld.Set64(memory.Map(module + po.ListRootRva, 8), 0, root);
        var rootBytes = memory.Map(root, 0x1000);
        TestWorld.Set64(rootBytes, po.ListOffset, offers);
        TestWorld.Set64(rootBytes, po.ListOffset + 8, offers);
        TestWorld.Set64(rootBytes, po.ListOffset + 16, offers + 16);
        memory.Map(offers, 16);
        TestWorld.Set64(memory.Map(player, 0x1000), 0, module + profile.PlayerVtableRvas[0]); // a real player starts with his vtable
        var sizes = new Dictionary<ulong, int>();
        ulong next = 0x400000;
        ulong Allocate(int size) { ulong at = next; next += 0x10000; memory.Map(at, size); sizes[at] = size; return at; }
        var record = new CompletedTransferRecord(memory, profile, module);
        var terms = new TransferTerms(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 15, false, 0);
        record.Apply(record.Plan(player, 0x500000, 0x600000, 12_345_678, new DateOnly(2026, 12, 15), terms), Allocate);
        ulong offer = memory.ReadUInt64(offers);
        ulong address = memory.ReadUInt64(offer + 0xA8);
        Assert.True(sizes[address] >= 208, $"terms object is {sizes[address]} bytes");
        var bytes = memory.Read(address, 208);
        Assert.Equal(15, bytes[0xC4]);
        Assert.All(bytes.Where((_, i) => i != 0xC4), b => Assert.Equal(0, b));
    }

    const string Palmer =
        "d81cf7ddfa7f00000100000063006200" + "00000000000000000000000000000000" + "00000000000000000000000000000000" +
        "00100000000000002845e95ee9010000" + "90c4f854e901000090c4f854e9010000" + "58a1f854e901000080f0fa028e000000" +
        "4c280000ffffffffffffffff681bea07" + "5e1bea075e1bea070f1aeb07011aeb07" + "01006c07000001000007000000140100" +
        "00000000000000000000000000000000" + "0000000000000000000e475ce9010000" + "00000000000000000000000000000000" +
        "00000000000000000000000000000000";

    [Fact]
    public void Offer_matches_the_game_apart_from_dates_and_two_unknown_words()
    {
        var game = Convert.FromHexString(Palmer);
        var built = CompletedTransferRecord.Offer(0x7FFADDF71CD8, 0x1E95EE94528, 0x1E954F8C490, 0x1E954F8A158, 50_000_000, 142, 10316,
            new DateOnly(2026, 12, 16), 0x1E95C470E00);
        var diff = Enumerable.Range(0, game.Length).Where(i => game[i] != built[i]).ToArray();
        // +0x0C/+0x0E: 0x63 and 0x62 in the game's record, 0 in the staff records (meaning unknown).
        // +0x6C..+0x7F: the dates (flag bits and transfer-window days differ).
        Assert.All(diff, i => Assert.True(i is 0x0C or 0x0E || i is >= 0x6C and < 0x80, $"byte +0x{i:X2} differs"));
        Assert.Equal(50_000_000, BitConverter.ToInt32(built, 0x58));
        Assert.Equal(0x14, built[0x8D]);
    }
}
