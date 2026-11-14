using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Transfer;

namespace Fm27Editor.Core.Tests;

public sealed class LoanedTransferTests
{
    const ulong Module = 0x7FF000000000, Player = 0x10000000, Contract = 0x20000000,
        Holder = 0x21000000, Loan = 0x22000000, ParentClub = 0x30000000, BorrowClub = 0x31000000,
        BuyerClub = 0x32000000, ParentTeam = 0x40000000, BorrowTeam = 0x41000000, BuyerTeam = 0x42000000,
        ParentSquad = 0x50000000, BorrowSquad = 0x51000000, BuyerSquad = 0x52000000,
        ParentFinance = 0x60000000, BuyerFinance = 0x61000000, Other = 0x70000000;

    // Steam IGE capture of Mark Shelton's actual loan contract before the transfer, 14/11/2026.
    const string SheltonLoan = "F8DC98F0FA7F0000C84B1E5EEF01000020C28854EF01000020C28854EF010000CA080000000001000000000000000000000000000000000000000000FFFFFFFFFFFFFFFFC21AEA079700EB07C21AEA0701006C070300030000000A3DF61D0000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFFFF0000008C0C0000000000008C0C00000000000006010000200000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFFAE080000AE080000CA0800000000000000000000FFFFFFFF000420000004000001006C0701006C07A4031DFE00070E0EFFFF0000";

    sealed class World
    {
        public readonly FakeMemory Memory = new();
        public readonly OffsetProfile Profile;
        public readonly PlayerRecord Record;
        public readonly ClubEntry Parent, Buyer;
        public readonly TransferService Service;

        public World(string profileFile)
        {
            Profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", profileFile));
            var player = Memory.Map(Player, 0x700);
            Put(player, 0, Module + Profile.PlayerVtableRvas[0]);
            Put(player, 0x130, BorrowTeam); Put(player, 0x138, BorrowTeam);
            Put(player, 0x330, Contract); Put(player, 0x338, Holder);
            var contract = Memory.Map(Contract, 0x100);
            Put(contract, 0, Module + Profile.Contract!.ContractVtableRva);
            Put(contract, 8, Player + 0x288); Put(contract, 0x10, ParentTeam); Put(contract, 0x18, ParentTeam);
            BitConverter.GetBytes(3000).CopyTo(contract, 0x20);
            Put(Memory.Map(Holder, 0x20), 0, Loan);
            var loan = Memory.Map(Loan, 0x100);
            Convert.FromHexString(SheltonLoan).CopyTo(loan, 0);
            Put(loan, 0, Module + Profile.FutureTransfers!.LoanContractVtable);
            Put(loan, 8, Player + 0x288); Put(loan, 0x10, BorrowTeam); Put(loan, 0x18, BorrowTeam);
            MakeClub(ParentClub, ParentFinance); MakeClub(BuyerClub, BuyerFinance); MakeClub(BorrowClub, 0);
            MakeTeam(ParentTeam, ParentClub, ParentSquad, [Player, Other]);
            MakeTeam(BorrowTeam, BorrowClub, BorrowSquad, [Other, Player, Other + 8]);
            MakeTeam(BuyerTeam, BuyerClub, BuyerSquad, [Other + 16]);
            var parentTeam = new TeamEntry(ParentTeam, ParentClub, ParentSquad, 2, 4, true);
            var buyerTeam = new TeamEntry(BuyerTeam, BuyerClub, BuyerSquad, 1, 4, true);
            Parent = new(ParentClub, 1, "Parent", null, ParentFinance, [parentTeam]);
            Buyer = new(BuyerClub, 2, "Buyer", null, BuyerFinance, [buyerTeam]);
            Record = new PlayerRecord { Address = LoanedTransferTests.Player, VtableRva = Profile.PlayerVtableRvas[0],
                Id = 215501, CurrentAbility = 1, PotentialAbility = 1, HomeReputation = 1, CurrentReputation = 1,
                WorldReputation = 1, Height = 180, RawAttributes = [], AttributeScale = 5, Positions = [], Personality = [],
                Contract = new ContractInfo(Contract, 3000, null, null, null) };
            Service = new(Memory, Profile, Module);
        }

        static void Put(byte[] bytes, int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        void MakeClub(ulong address, ulong finance)
        {
            var club = Memory.Map(address, 0x300);
            Put(club, 0, Module + Profile.Club!.ClubVtableRva); Put(club, 0x150, finance);
            if (finance == 0) return;
            var data = Memory.Map(finance, 0x900);
            Put(data, 0, Module + Profile.Club.Finance!.VtableRva!.Value);
            BitConverter.GetBytes(100000000).CopyTo(data, 0xC);
            BitConverter.GetBytes(20000000).CopyTo(data, 0x7C4);
            BitConverter.GetBytes(1000000).CopyTo(data, 0x810);
        }
        void MakeTeam(ulong address, ulong club, ulong squad, ulong[] members)
        {
            var team = Memory.Map(address, 0x100);
            Put(team, 0, Module + Profile.Team!.VtableRva); Put(team, 0x30, club);
            Put(team, 0x38, squad); Put(team, 0x40, squad + (ulong)members.Length * 8); Put(team, 0x48, squad + 32);
            var data = Memory.Map(squad, 32);
            for (int i = 0; i < members.Length; i++) Put(data, i * 8, members[i]);
        }
        public ulong Q(ulong address) => Memory.ReadUInt64(address);
        public TransferPlan Plan() => Service.Plan(Record, [Parent, Buyer], Buyer, 51284922);
    }

    [Theory]
    [InlineData("27.0.1+2503222-steam.json")]
    [InlineData("27.0.1+2494687-gamepass.json")]
    public void Moving_a_loan_without_purchase_clauses_removes_both_old_squad_entries(string profile)
    {
        var w = new World(profile);
        var plan = w.Plan();
        w.Service.Apply(plan);
        // IGE's Shelton move removed him from both old squads, appended him to the destination and cleared the holder.
        Assert.Equal(ParentSquad + 8, w.Q(ParentTeam + 0x40)); Assert.Equal(Other, w.Q(ParentSquad));
        Assert.Equal(BorrowSquad + 16, w.Q(BorrowTeam + 0x40)); Assert.Equal(Other + 8, w.Q(BorrowSquad + 8));
        Assert.Equal(BuyerSquad + 16, w.Q(BuyerTeam + 0x40)); Assert.Equal(Player, w.Q(BuyerSquad + 8));
        Assert.Equal(0UL, w.Q(Player + 0x338)); Assert.Equal(BuyerTeam, w.Q(Player + 0x130));
        Assert.Equal(BorrowTeam, w.Q(Player + 0x138)); Assert.Equal(BuyerTeam, w.Q(Contract + 0x10));
    }

    [Fact]
    public void Changed_loan_terms_refuse_the_entire_move_before_any_write()
    {
        var w = new World("27.0.1+2494687-gamepass.json");
        var plan = w.Plan();
        Assert.True(w.Memory.TryWrite(Loan + 0xCC, BitConverter.GetBytes(1234)));
        int writes = w.Memory.Writes;
        Assert.Throws<StaleObjectException>(() => w.Service.Apply(plan));
        Assert.Equal(writes, w.Memory.Writes); Assert.Equal(Holder, w.Q(Player + 0x338));
        Assert.Equal(ParentTeam, w.Q(Contract + 0x10));
    }

    [Fact]
    public void Changed_parent_squad_refuses_the_entire_move_before_any_write()
    {
        var w = new World("27.0.1+2503222-steam.json");
        var plan = w.Plan();
        Assert.True(w.Memory.TryWrite(ParentSquad + 8, BitConverter.GetBytes(Other + 40)));
        int writes = w.Memory.Writes;
        Assert.Throws<StaleObjectException>(() => w.Service.Apply(plan));
        Assert.Equal(writes, w.Memory.Writes); Assert.Equal(Holder, w.Q(Player + 0x338));
        Assert.Equal(ParentTeam, w.Q(Contract + 0x10));
    }

    [Fact]
    public void Loan_absent_from_parent_squad_is_refused_without_writing()
    {
        var w = new World("27.0.1+2503222-steam.json");
        Assert.True(w.Memory.TryWrite(ParentSquad, BitConverter.GetBytes(Other + 32)));
        int writes = w.Memory.Writes;
        Assert.Throws<StaleObjectException>(() => w.Plan());
        Assert.Equal(writes, w.Memory.Writes);
    }

    [Fact]
    public void Changed_destination_squad_refuses_the_entire_move_before_any_write()
    {
        var w = new World("27.0.1+2494687-gamepass.json");
        var plan = w.Plan();
        Assert.True(w.Memory.TryWrite(BuyerSquad, BitConverter.GetBytes(Other + 48)));
        int writes = w.Memory.Writes;
        Assert.Throws<StaleObjectException>(() => w.Service.Apply(plan));
        Assert.Equal(writes, w.Memory.Writes); Assert.Equal(Holder, w.Q(Player + 0x338));
        Assert.Equal(ParentTeam, w.Q(Contract + 0x10));
    }

    [Fact]
    public void Loan_with_an_agreed_purchase_price_is_refused_without_writing()
    {
        var w = new World("27.0.1+2503222-steam.json");
        // Sarr's IGE move also changed the borrowing club's finances by the agreed purchase price.
        Assert.True(w.Memory.TryWrite(Loan + 0xC0, BitConverter.GetBytes(23623378)));
        int writes = w.Memory.Writes;
        Assert.Throws<InvalidOperationException>(() => w.Plan());
        Assert.Equal(writes, w.Memory.Writes);
    }

    [Fact]
    public void Loan_with_contract_clauses_is_refused_without_writing()
    {
        var w = new World("27.0.1+2494687-gamepass.json");
        Assert.True(w.Memory.TryWrite(Loan + 0x68, BitConverter.GetBytes(0x12340000UL)));
        int writes = w.Memory.Writes;
        Assert.Throws<InvalidOperationException>(() => w.Plan());
        Assert.Equal(writes, w.Memory.Writes);
    }
}
