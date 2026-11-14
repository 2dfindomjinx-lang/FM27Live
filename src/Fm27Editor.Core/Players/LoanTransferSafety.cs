using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Transfer;

namespace Fm27Editor.Core.Players;

/// <summary>Shared validation for the measured three-club transfer of a player on loan.</summary>
internal static class LoanTransferSafety
{
    internal sealed record Squad(ulong Begin, byte[] Items);
    internal sealed record Snapshot(ulong HolderSlot, byte[] HolderBytes, ulong ParentTeam, ulong BorrowingTeam,
        Squad ParentSquad, Squad BorrowingSquad, IReadOnlyList<PlannedWrite> Guards);

    internal static Snapshot Capture(IProcessMemory memory, OffsetProfile profile, ulong moduleBase, PlayerRecord player,
        ulong contractAddress, ulong parentTeam, ulong buyingTeam)
    {
        var c = profile.Contract ?? throw new InvalidOperationException("Profile has no contract offsets");
        var t = profile.Team ?? throw new InvalidOperationException("Profile has no team offsets");
        var f = profile.FutureTransfers ?? throw new InvalidOperationException("Profile has no loan offsets");
        int holderOffset = f.CurrentLoanHolder ?? throw new InvalidOperationException("Profile has no loan holder offset");
        int seniorOffset = c.SeniorTeam ?? throw new InvalidOperationException("Profile has no contract senior team offset");
        int personOffset = profile.Relations?.PlayerPerson ?? throw new InvalidOperationException("Profile has no person offset");
        var guards = new List<PlannedWrite>();
        byte[] Guard(ulong at, int length, string name)
        {
            var bytes = memory.Read(at, length);
            guards.Add(new(at, bytes, [], name));
            return bytes;
        }
        ulong holderSlot = player.Address + (ulong)holderOffset;
        var holderBytes = Guard(holderSlot, 8, "loan holder");
        ulong holder = BitConverter.ToUInt64(holderBytes);
        if (holder == 0) throw new StaleObjectException("The current loan is missing");
        var loan = new CurrentLoanReader(memory, profile, moduleBase).Read(player);
        if (loan is not { Status: CurrentLoanStatus.Present, Contract: { } loanContract, Amounts: not null })
            throw new StaleObjectException("The current loan could not be verified");
        var holderContents = Guard(holder, 8, "loan contract pointer");
        if (BitConverter.ToUInt64(holderContents) != loanContract.Address)
            throw new StaleObjectException("The loan contract changed while planning");
        var loanBytes = Guard(loanContract.Address, LoanCreator.ContractSize, "loan contract");
        var readSnapshot = Convert.FromHexString(loanContract.Snapshot);
        if (!loanBytes.AsSpan(0, readSnapshot.Length).SequenceEqual(readSnapshot))
            throw new StaleObjectException("The loan contract changed while planning");
        if (BitConverter.ToUInt64(loanBytes, LoanCreator.ContractPerson) != player.Address + (ulong)personOffset)
            throw new StaleObjectException("The loan contract belongs to another player");
        if (BitConverter.ToInt32(loanBytes, LoanEditor.AgreedPrice) != -1 ||
            BitConverter.ToInt32(loanBytes, LoanEditor.BuyBackFee) != -1 || loanBytes[LoanCreator.PurchaseAfterMatchesByte] != 0xFE)
            throw new InvalidOperationException("This loan's purchase terms have not been verified for a transfer");
        if (c.Clauses is not { } clauseOffset || clauseOffset < 0 || clauseOffset > loanBytes.Length - 24)
            throw new InvalidOperationException("The loan's contract clauses cannot be verified");
        if (loanBytes.AsSpan(clauseOffset, 24).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidOperationException("Transfers of loans with contract clauses have not been verified");

        var playingTeam = Guard(player.Address + (ulong)t.PlayerTeam, 8, "playing team");
        var employer = Guard(contractAddress + (ulong)c.Team, 8, "contract team");
        var senior = Guard(contractAddress + (ulong)seniorOffset, 8, "contract senior team");
        ulong borrowingTeam = BitConverter.ToUInt64(playingTeam);
        if (borrowingTeam == parentTeam || borrowingTeam != loan.BorrowingTeamAddress ||
            BitConverter.ToUInt64(employer) != parentTeam || BitConverter.ToUInt64(senior) != parentTeam)
            throw new StaleObjectException("The player's teams changed while planning");
        var borrowerClub = Guard(borrowingTeam + (ulong)c.TeamClub, 8, "borrowing club");
        var buyerClub = Guard(buyingTeam + (ulong)c.TeamClub, 8, "buying club");
        if (BitConverter.ToUInt64(borrowerClub) == 0 || BitConverter.ToUInt64(buyerClub) == 0)
            throw new StaleObjectException("The borrowing or buying club could not be verified");
        if (borrowerClub.AsSpan().SequenceEqual(buyerClub))
            throw new InvalidOperationException("A transfer to the borrowing club has not been verified");
        var parent = GuardSquad(memory, profile, moduleBase, guards, parentTeam, "parent");
        var borrowing = GuardSquad(memory, profile, moduleBase, guards, borrowingTeam, "borrowing");
        if (Count(parent.Items, player.Address) != 1 || Count(borrowing.Items, player.Address) != 1)
            throw new StaleObjectException("The player is not uniquely present in both loan squads");
        return new(holderSlot, holderBytes, parentTeam, borrowingTeam, parent, borrowing, guards);
    }

    internal static Squad GuardSquad(IProcessMemory memory, OffsetProfile profile, ulong moduleBase,
        List<PlannedWrite> guards, ulong team, string name)
    {
        var t = profile.Team ?? throw new InvalidOperationException("Profile has no team offsets");
        var vtable = memory.Read(team, 8);
        if (BitConverter.ToUInt64(vtable) != moduleBase + t.VtableRva) throw new StaleObjectException($"The {name} team changed");
        var header = memory.Read(team + (ulong)t.Squad, 24);
        ulong begin = BitConverter.ToUInt64(header, 0), end = BitConverter.ToUInt64(header, 8), cap = BitConverter.ToUInt64(header, 16);
        if (begin == 0 || end < begin || cap < end || (end - begin) % 8 != 0 || (cap - begin) % 8 != 0 || (cap - begin) / 8 > 200)
            throw new StaleObjectException($"The {name} squad is invalid");
        var items = memory.Read(begin, checked((int)(end - begin)));
        guards.Add(new(team, vtable, [], $"{name} team"));
        guards.Add(new(team + (ulong)t.Squad, header, [], $"{name} squad header"));
        if (items.Length != 0) guards.Add(new(begin, items, [], $"{name} squad"));
        return new(begin, items);
    }

    internal static int Count(byte[] items, ulong player) =>
        Enumerable.Range(0, items.Length / 8).Count(i => BitConverter.ToUInt64(items, i * 8) == player);
}
