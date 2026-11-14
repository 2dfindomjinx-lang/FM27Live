using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Transfer;

public sealed record PlannedWrite(ulong Address, byte[] Expected, byte[] Value, string What);

/// <summary>
/// Everything a move changes, computed up front. Nothing is written until <see cref="TransferService.Apply"/>,
/// which re-checks every expected value first.
/// </summary>
public sealed record TransferPlan(
    PlayerRecord Player, ClubEntry Seller, ClubEntry Buyer, ulong FromTeam, TeamEntry ToTeam, int Fee, int Wage,
    IReadOnlyList<PlannedWrite> Writes, IReadOnlyList<ulong>? NewSquad)
{
    /// <summary>The destination squad vector is full and needs a new buffer outside the game's allocator.</summary>
    public bool NeedsAllocation => NewSquad is not null;
    /// <summary>Additional snapshots that must still match before any write.</summary>
    public IReadOnlyList<PlannedWrite> Guards { get; init; } = [];
}

/// <summary>
/// A permanent transfer, replicating what FM26's In-Game Editor "Move to other club" changes (measured with
/// before/after snapshots, then replayed on FM26 by hand and checked in game):
/// <list type="bullet">
/// <item>buyer: balance −fee, transfer spend this month +fee, spent this season +fee, transfer budget −fee (not below 0), wage bill +wage;</item>
/// <item>seller: balance +fee, players sold this month and this season +fee (the season figure is the FFP "Transfer Income"),
/// transfer budget +fee, wage bill −wage;</item>
/// <item>contract team and senior team → the buyer's first team; playing team → the selected buyer team;</item>
/// <item>removed from the old team's squad vector, appended to the new one.</item>
/// </list>
/// For an active loan without purchase or buy-back clauses, also removes the player from the parent squad and
/// clears the loan holder. This is restricted to the measured three-club layout; unverified loan cases are refused.
/// Not done (IGE does): a new contract object, transfer history, news, solidarity/sell-on payments to third clubs.
/// The existing contract keeps its terms; change them afterwards with <see cref="ContractWriter"/>.
/// </summary>
public sealed class TransferService(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    ContractOffsets C => profile.Contract ?? throw new InvalidOperationException("Profile has no contract offsets");
    TeamOffsets T => profile.Team ?? throw new InvalidOperationException("Profile has no team offsets");
    ClubFinanceOffsets F => profile.Club?.Finance ?? throw new InvalidOperationException("Profile has no club finance offsets");

    /// <param name="intoTeam">Destination team; default is the buyer's first team when it has room, else the first buyer team with room.</param>
    public TransferPlan Plan(PlayerRecord player, IReadOnlyList<ClubEntry> clubs, ClubEntry buyer, int fee, TeamEntry? intoTeam = null)
    {
        if (fee < 0) throw new ArgumentOutOfRangeException(nameof(fee));
        if (!profile.PlayerVtableRvas.Any(v => moduleBase + v == memory.ReadUInt64(player.Address)))
            throw new StaleObjectException($"0x{player.Address:X} is no longer a player object");
        GenderGuard.EnsureMalePlayer(memory, profile, player.Address);
        GenderGuard.EnsureMenClub(memory, profile, buyer.Address);
        if (player.Club is { Address: not 0 } sellerClub)
            GenderGuard.EnsureMenClub(memory, profile, sellerClub.Address);
        var contract = player.Contract ?? throw new InvalidOperationException("The player has no contract (free agent)");
        if (memory.ReadUInt64(player.Address + (ulong)C.PlayerContract) != contract.Address)
            throw new StaleObjectException("The player's contract changed since it was read");
        if (memory.ReadUInt64(contract.Address) != moduleBase + C.ContractVtableRva)
            throw new StaleObjectException("The player's contract is no longer a contract object");
        int seniorTeamOffset = C.SeniorTeam ?? throw new InvalidOperationException("Profile has no contract senior team offset");
        ulong contractTeam = memory.ReadUInt64(contract.Address + (ulong)C.Team);
        if (memory.ReadUInt64(contract.Address + (ulong)seniorTeamOffset) != contractTeam)
            throw new InvalidOperationException("The contract's employer and senior team differ; this move is not supported");
        ulong fromTeam = memory.ReadUInt64(player.Address + (ulong)T.PlayerTeam);
        var seller = clubs.FirstOrDefault(c => c.Teams.Any(t => t.Address == contractTeam))
                     ?? throw new InvalidOperationException("Seller club not found for the contract team");
        if (seller.Address == buyer.Address) throw new InvalidOperationException("Player already belongs to that club");
        var firstTeam = buyer.FirstTeam ?? throw new InvalidOperationException($"{buyer.DisplayName} has no first team");
        var to = intoTeam ?? (firstTeam.HasRoom ? firstTeam : buyer.Teams.FirstOrDefault(t => t.HasRoom) ?? firstTeam);
        if (to.Club != buyer.Address) throw new ArgumentException("Team does not belong to the buyer", nameof(intoTeam));

        var writes = new List<PlannedWrite>();
        var guards = new List<PlannedWrite>();
        if (profile.FutureTransfers?.CurrentLoanHolder is { } holderOffset)
        {
            ulong holderSlot = player.Address + (ulong)holderOffset;
            var holderBytes = memory.Read(holderSlot, 8);
            guards.Add(new(holderSlot, holderBytes, holderBytes, "loan holder"));
            ulong holder = BitConverter.ToUInt64(holderBytes);
            if (holder != 0)
            {
                var loan = new CurrentLoanReader(memory, profile, moduleBase).Read(player);
                if (loan is not { Status: CurrentLoanStatus.Present, Contract: { } loanContract, Amounts: { } amounts } ||
                    loan.BorrowingTeamAddress != fromTeam || contractTeam == fromTeam)
                    throw new StaleObjectException("The current loan could not be verified");
                if (buyer.Teams.Any(t => t.Address == fromTeam))
                    throw new InvalidOperationException("A transfer to the borrowing club has not been verified");
                var loanBytes = memory.Read(loanContract.Address, LoanCreator.ContractSize);
                int personOffset = profile.Relations?.PlayerPerson ?? throw new InvalidOperationException("Profile has no person offset");
                if (BitConverter.ToUInt64(loanBytes, LoanCreator.ContractPerson) != player.Address + (ulong)personOffset)
                    throw new StaleObjectException("The loan contract belongs to another player");
                // Steam IGE's no-option sample (Mark Shelton, 14/11/2026). In the Sarr sample, a purchase
                // option coincided with additional payments, so do not guess how to settle it.
                if (amounts.AgreedPrice != -1 || amounts.BuyBackFee != -1 ||
                    loanBytes[LoanCreator.PurchaseAfterMatchesByte] != 0xFE)
                    throw new InvalidOperationException("This loan's purchase terms have not been verified for an immediate transfer");
                if (C.Clauses is not { } clauseOffset || clauseOffset < 0 || clauseOffset > loanBytes.Length - 24)
                    throw new InvalidOperationException("The loan's contract clauses cannot be verified");
                if (loanBytes.AsSpan(clauseOffset, 24).IndexOfAnyExcept((byte)0) >= 0)
                    throw new InvalidOperationException("Transfers of loans with contract clauses have not been verified");
                var holderContents = memory.Read(holder, 8);
                if (BitConverter.ToUInt64(holderContents) != loanContract.Address)
                    throw new StaleObjectException("The loan contract changed while planning");
                guards.Add(new(holder, holderContents, holderContents, "loan contract pointer"));
                guards.Add(new(loanContract.Address, loanBytes, loanBytes, "loan contract"));
                var playingTeam = memory.Read(player.Address + (ulong)T.PlayerTeam, 8);
                var employer = memory.Read(contract.Address + (ulong)C.Team, 8);
                var seniorTeam = memory.Read(contract.Address + (ulong)seniorTeamOffset, 8);
                if (BitConverter.ToUInt64(playingTeam) != fromTeam || BitConverter.ToUInt64(employer) != contractTeam ||
                    BitConverter.ToUInt64(seniorTeam) != contractTeam)
                    throw new StaleObjectException("The player's teams changed while planning");
                guards.Add(new(player.Address + (ulong)T.PlayerTeam, playingTeam, [], "playing team"));
                guards.Add(new(contract.Address + (ulong)C.Team, employer, [], "contract team"));
                guards.Add(new(contract.Address + (ulong)seniorTeamOffset, seniorTeam, [], "contract senior team"));
                AddLoanSquadRemoval(writes, guards, contractTeam, player.Address, "parent");
                var borrowingSquad = GuardLoanSquad(guards, fromTeam, "borrowing");
                if (Enumerable.Range(0, borrowingSquad.Length / 8).Count(i => BitConverter.ToUInt64(borrowingSquad, i * 8) == player.Address) != 1)
                    throw new StaleObjectException("The player is not uniquely present in the borrowing squad");
                var destinationSquad = GuardLoanSquad(guards, to.Address, "destination");
                if (Enumerable.Range(0, destinationSquad.Length / 8).Any(i => BitConverter.ToUInt64(destinationSquad, i * 8) == player.Address))
                    throw new StaleObjectException("The player is already in the destination squad");
                writes.Add(new(holderSlot, holderBytes, BitConverter.GetBytes(0UL), "loan holder"));
            }
        }
        int wage = contract.Wage;
        AddMoney(writes, buyer, F.Balance, -fee, "buyer balance");
        AddMoney(writes, buyer, F.TransferSpendMonth, fee, "buyer transfer spend this month");
        AddMoney(writes, buyer, F.SpentThisSeason, fee, "buyer spent this season");
        AddMoney(writes, buyer, F.TransferBudget, -fee, "buyer transfer budget", floorAtZero: true);
        AddMoney(writes, buyer, F.WageBill, wage, "buyer wage bill");
        AddMoney(writes, seller, F.Balance, fee, "seller balance");
        AddMoney(writes, seller, F.PlayersSold, fee, "seller players sold");
        AddMoney(writes, seller, F.PlayersSoldSeason, fee, "seller players sold this season");
        AddMoney(writes, seller, F.TransferBudget, fee, "seller transfer budget");
        AddMoney(writes, seller, F.WageBill, -wage, "seller wage bill");
        writes.Add(Pointer(contract.Address + (ulong)C.Team, firstTeam.Address, "contract team"));
        writes.Add(Pointer(contract.Address + (ulong)seniorTeamOffset, firstTeam.Address, "contract senior team"));
        writes.Add(Pointer(player.Address + (ulong)T.PlayerTeam, to.Address, "playing team"));

        // Old squad: shift the tail left over the player and move end back by one.
        ulong squad = fromTeam + (ulong)T.Squad;
        ulong begin = memory.ReadUInt64(squad), end = memory.ReadUInt64(squad + 8);
        var old = memory.Read(begin, (int)(end - begin));
        int index = Enumerable.Range(0, old.Length / 8).FirstOrDefault(i => BitConverter.ToUInt64(old, i * 8) == player.Address, -1);
        if (index < 0) throw new StaleObjectException("Player is not in his team's squad");
        for (int i = index; i + 1 < old.Length / 8; i++)
            writes.Add(new PlannedWrite(begin + (ulong)i * 8, old[(i * 8)..(i * 8 + 8)], old[((i + 1) * 8)..((i + 2) * 8)], "old squad shift"));
        writes.Add(Pointer(squad + 8, end - 8, "old squad end"));

        // New squad: append in place when there is room, otherwise Apply allocates a bigger buffer.
        ulong toSquad = to.Address + (ulong)T.Squad;
        ulong tBegin = memory.ReadUInt64(toSquad), tEnd = memory.ReadUInt64(toSquad + 8), tCap = memory.ReadUInt64(toSquad + 16);
        IReadOnlyList<ulong>? newSquad = null;
        if (tEnd < tCap)
        {
            writes.Add(Pointer(tEnd, player.Address, "new squad slot"));
            writes.Add(Pointer(toSquad + 8, tEnd + 8, "new squad end"));
        }
        else
        {
            var items = memory.Read(tBegin, (int)(tEnd - tBegin));
            newSquad = [.. Enumerable.Range(0, items.Length / 8).Select(i => BitConverter.ToUInt64(items, i * 8)), player.Address];
        }
        return new TransferPlan(player, seller, buyer, fromTeam, to, fee, wage, writes, newSquad) { Guards = guards };
    }

    /// <summary>
    /// Checks every expected value, then writes. <paramref name="allocate"/> is required when the plan
    /// <see cref="TransferPlan.NeedsAllocation"/> (for the live game: <see cref="WindowsProcessMemory.Allocate"/>).
    /// </summary>
    public void Apply(TransferPlan plan, Func<int, ulong>? allocate = null)
    {
        var contract = plan.Player.Contract ?? throw new StaleObjectException("The player has no contract snapshot");
        if (!profile.PlayerVtableRvas.Any(v => moduleBase + v == memory.ReadUInt64(plan.Player.Address)) ||
            memory.ReadUInt64(plan.Player.Address + (ulong)C.PlayerContract) != contract.Address ||
            memory.ReadUInt64(contract.Address) != moduleBase + C.ContractVtableRva)
            throw new StaleObjectException("The player or contract changed since the plan was made; nothing written");
        foreach (var guard in plan.Guards)
            if (!memory.Read(guard.Address, guard.Expected.Length).AsSpan().SequenceEqual(guard.Expected))
                throw new StaleObjectException($"{guard.What} changed since the plan was made; nothing written");
        foreach (var w in plan.Writes)
            if (!memory.Read(w.Address, w.Expected.Length).AsSpan().SequenceEqual(w.Expected))
                throw new StaleObjectException($"{w.What} changed since the plan was made; nothing written");
        var writes = plan.Writes.ToList();
        if (plan.NewSquad is { } items)
        {
            if (allocate is null) throw new InvalidOperationException("The destination squad is full; an allocator is needed");
            ulong toSquad = plan.ToTeam.Address + (ulong)T.Squad;
            var oldPointers = memory.Read(toSquad, 24);
            int capacity = items.Count * 2;
            ulong buffer = allocate(capacity * 8);
            var bytes = items.SelectMany(BitConverter.GetBytes).ToArray();
            writes.Add(new PlannedWrite(buffer, memory.Read(buffer, bytes.Length), bytes, "new squad buffer"));
            writes.Add(new PlannedWrite(toSquad, oldPointers,
                [.. BitConverter.GetBytes(buffer), .. BitConverter.GetBytes(buffer + (ulong)bytes.Length), .. BitConverter.GetBytes(buffer + (ulong)capacity * 8)],
                "new squad begin/end/capacity"));
        }
        foreach (var w in writes)
        {
            if (!memory.Read(w.Address, w.Expected.Length).AsSpan().SequenceEqual(w.Expected))
                throw new StaleObjectException($"{w.What} changed while writing");
            if (!memory.TryWrite(w.Address, w.Value) || !memory.Read(w.Address, w.Value.Length).AsSpan().SequenceEqual(w.Value))
                throw new MemoryAccessException($"Write failed: {w.What}");
        }
    }

    byte[] GuardLoanSquad(List<PlannedWrite> guards, ulong team, string name)
    {
        if (memory.ReadUInt64(team) != moduleBase + T.VtableRva)
            throw new StaleObjectException($"The {name} team changed");
        var header = memory.Read(team + (ulong)T.Squad, 24);
        ulong begin = BitConverter.ToUInt64(header, 0), end = BitConverter.ToUInt64(header, 8), cap = BitConverter.ToUInt64(header, 16);
        if (begin == 0 || end < begin || cap < end || (end - begin) % 8 != 0 || (cap - begin) / 8 > 200)
            throw new StaleObjectException($"The {name} squad is invalid");
        var items = memory.Read(begin, checked((int)(end - begin)));
        guards.Add(new(team, memory.Read(team, 8), [], $"{name} team"));
        guards.Add(new(team + (ulong)T.Squad, header, [], $"{name} squad header"));
        guards.Add(new(begin, items, [], $"{name} squad"));
        return items;
    }

    void AddLoanSquadRemoval(List<PlannedWrite> writes, List<PlannedWrite> guards, ulong team, ulong player, string name)
    {
        var items = GuardLoanSquad(guards, team, name);
        var matches = Enumerable.Range(0, items.Length / 8).Where(i => BitConverter.ToUInt64(items, i * 8) == player).ToArray();
        if (matches.Length != 1) throw new StaleObjectException($"The player is not uniquely present in the {name} squad");
        ulong begin = memory.ReadUInt64(team + (ulong)T.Squad);
        for (int i = matches[0]; i + 1 < items.Length / 8; i++)
            writes.Add(new(begin + (ulong)i * 8, items[(i * 8)..(i * 8 + 8)], items[((i + 1) * 8)..((i + 2) * 8)], $"{name} squad shift"));
        ulong endAddress = team + (ulong)T.Squad + 8;
        writes.Add(new(endAddress, BitConverter.GetBytes(begin + (ulong)items.Length), BitConverter.GetBytes(begin + (ulong)items.Length - 8), $"{name} squad end"));
    }

    void AddMoney(List<PlannedWrite> writes, ClubEntry club, int? offset, long delta, string what, bool floorAtZero = false)
    {
        if (offset is not { } o) throw new InvalidOperationException($"Profile has no offset for {what}");
        if (club.Finance == 0) throw new InvalidOperationException($"{club.DisplayName} has no finance object");
        // Small clubs keep only balance and embargo; their budget/wage totals do not exist and must not be written.
        uint vt = (uint)(memory.ReadUInt64(club.Finance) - moduleBase);
        switch (F.Holds(vt, o, 4))
        {
            case null: throw new StaleObjectException($"{club.DisplayName}'s finance object has an unknown layout");
            case false: return;
        }
        ulong a = club.Finance + (ulong)o;
        var current = memory.Read(a, 4);
        long v = BitConverter.ToInt32(current) + delta;
        if (floorAtZero && v < 0) v = 0;
        if (v < int.MinValue || v > int.MaxValue) throw new OverflowException($"{what} would overflow int32");
        writes.Add(new PlannedWrite(a, current, BitConverter.GetBytes((int)v), what));
    }

    PlannedWrite Pointer(ulong address, ulong value, string what) =>
        new(address, memory.Read(address, 8), BitConverter.GetBytes(value), what);
}
