using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Transfer;

/// <summary>
/// <c>transfercheck [--write]</c>: runs each kind of player move through the same GameSession/Core calls the app makes, each on its
/// own player and clubs, and reads the game back after every one. Without <c>--write</c> it only picks the players and prints
/// the plans. Use a disposable save (the calibration <c>work-27.fm</c>) and quit FM without saving unless a save is the test.
/// Expected values are worked out from the values read before the move (fee in, fee out, wage moved), not from the editor's plan.
/// </summary>
static class TransferCheck
{
    public static int Run(string[] args, Func<bool, GameSession> attach)
    {
        bool write = args.Contains("--write");
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) pass++; else fail++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}"); }

        using var session = attach(write);
        var p = session.Profile;
        var mem = session.Memory;
        var today = session.ReadGameDate() ?? throw new InvalidOperationException("The in-game date could not be read");
        Console.WriteLine($"{p.Build}, game date {today:dd/MM/yyyy}{(write ? "" : " (plans only)")}");
        var all = session.Players.LoadAll();
        var clubs = session.LoadClubs(all);
        var used = new HashSet<ulong>();
        var fin = p.Club?.Finance ?? throw new InvalidOperationException("Profile has no club finance offsets");
        int balanceOffset = fin.Balance ?? throw new InvalidOperationException("Profile has no balance offset");

        ClubEntry Club(string name) => clubs.FirstOrDefault(c => c.FirstTeam is not null &&
            (string.Equals(c.ShortName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            ?? clubs.FirstOrDefault(c => c.FirstTeam is not null &&
                ((c.ShortName?.StartsWith(name, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (c.Name?.StartsWith(name, StringComparison.OrdinalIgnoreCase) ?? false)))
            ?? throw new InvalidOperationException($"No club named {name}");
        ulong LoanHolder(PlayerRecord pl) => p.FutureTransfers?.CurrentLoanHolder is { } h ? mem.ReadUInt64(PersonOwner.Of(pl, p).At(h)) : 0;
        PlayerRecord Pick(ClubEntry club) => all.First(pl => pl.Club?.Address == club.Address && pl.Contract is not null && pl.Id is not null
            && pl.Gender != Fm27Editor.Core.Offsets.Gender.Women && LoanHolder(pl) == 0 && used.Add(pl.Address)
            && mem.ReadUInt64(pl.Address + (ulong)p.Team!.PlayerTeam) == club.FirstTeam!.Address);
        int Balance(ClubEntry c) => BitConverter.ToInt32(mem.Read(c.Finance + (ulong)balanceOffset, 4));
        PlayerRecord Reload(PlayerRecord pl) => session.Players.Reload(pl.Address) ?? throw new InvalidOperationException($"{pl.DisplayName} could not be reloaded");
        bool InSquad(TeamEntry team, PlayerRecord pl)
        {
            ulong squad = team.Address + (ulong)p.Team!.Squad, begin = mem.ReadUInt64(squad), end = mem.ReadUInt64(squad + 8);
            var items = mem.Read(begin, (int)(end - begin));
            for (int i = 0; i < items.Length; i += 8) if (BitConverter.ToUInt64(items, i) == pl.Address) return true;
            return false;
        }
        // --only move|free|future|loan|release runs just that move (to tell which one leaves the game unable to reload).
        string? only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault();
        void Step(string title, Action body)
        {
            string key = title.Split(' ')[0].ToLowerInvariant() switch { "move" => "move", "free" => "free", "future" => "future", "loan" => "loan", _ => "release" };
            if (only is not null && only != key) return;
            Console.WriteLine(title);
            try { body(); }
            catch (Exception ex) { fail++; Console.WriteLine($"  FAIL  {ex.GetType().Name}: {ex.Message}"); }
        }

        // 1. Move now with a 15% sell-on to the seller.
        Step("Move now + sell-on (Chelsea -> Aston Villa, 12,345,678)", () =>
        {
            var seller = Club("Chelsea"); var buyer = Club("Aston Villa"); const int fee = 12_345_678;
            var player = Pick(seller);
            Console.WriteLine($"  {player.DisplayName} ({player.Id})");
            var plan = session.Transfers.Plan(player, clubs, buyer, fee);
            if (!write) return;
            int sellerBefore = Balance(seller), buyerBefore = Balance(buyer), clausesBefore = session.ReadTransferClauses(buyer.Address).Count;
            session.ApplyTransfer(plan, 15);
            var after = Reload(player);
            Check(after.Club?.Address == buyer.Address, $"contract club is {buyer.DisplayName} (now {after.Club?.DisplayName})");
            Check(InSquad(plan.ToTeam, player) && !InSquad(seller.FirstTeam!, player), "left the seller's squad and joined the buyer's");
            Check(Balance(seller) == sellerBefore + fee, $"seller balance {sellerBefore:N0} + fee = {Balance(seller):N0}");
            Check(Balance(buyer) == buyerBefore - fee, $"buyer balance {buyerBefore:N0} - fee = {Balance(buyer):N0}");
            // FMRTE stores a transfer's sell-on in the offer's 208-byte terms object at +0xC4, not as a record in the
            // club's clause vector (Game Pass 27.1.0, 18/11/2026: 208 bytes, only +0xC4=15). The club-clause route
            // survived Save As but crashed FM on the next load.
            var completed = session.Proposals.Read(after).SingleOrDefault(o => o.Status == 20 && o.Value == fee);
            ulong terms = completed is null ? 0 : mem.ReadUInt64(completed.Address + 0xA8);
            bool termsOk = terms != 0 && mem.Read(terms, 208).Where((b, i) => i != 0xC4).All(b => b == 0) && mem.Read(terms + 0xC4, 1)[0] == 15;
            Check(completed is not null && termsOk, "completed offer terms are 208 bytes with a 15% sell-on at +0xC4");
            Check(session.ReadTransferClauses(buyer.Address).Count == clausesBefore, "no club clause record was added for the sell-on");
        });

        // 2. Free agent, Move now.
        Step("Free agent, Move now (-> Everton)", () =>
        {
            var buyer = Club("Everton");
            var player = all.Where(pl => pl.Contract is null && pl.Club is null && pl.Id is not null && pl.Gender != Fm27Editor.Core.Offsets.Gender.Women)
                .OrderByDescending(pl => pl.CurrentAbility).First(pl => used.Add(pl.Address));
            Console.WriteLine($"  {player.DisplayName} ({player.Id})");
            var plan = session.PlanFreeAgentSigning(player, buyer);
            if (!write) return;
            session.ApplyFreeAgentSigning(plan);
            var after = Reload(player);
            Check(after.Contract is not null && after.Club?.Address == buyer.Address, $"under contract at {buyer.DisplayName} (now {after.Club?.DisplayName ?? "none"})");
            Check(InSquad(plan.ToTeam, player), "in the buyer's squad");
        });

        // 3. Future transfer.
        Step("Future transfer (Newcastle -> Tottenham, 5,000,000, in 30 days)", () =>
        {
            var seller = Club("Newcastle"); var buyer = Club("Tottenham"); const int fee = 5_000_000;
            var player = Pick(seller); var date = today.AddDays(30);
            Console.WriteLine($"  {player.DisplayName} ({player.Id})");
            var plan = session.PlanFutureTransfer(player, buyer.FirstTeam!.Address, fee, date);
            if (!write) return;
            session.ApplyFutureTransfer(plan);
            var after = Reload(player);
            var future = session.FutureTransfers.Read(after, session.Proposals.Read(after));
            Check(future.Count == 1 && future[0].Date == date && future[0].Offer.Value == fee, $"one future transfer on {date:dd/MM/yyyy} for {fee:N0} (found {future.Count})");
            Check(after.Club?.Address == seller.Address, "still at the seller until the date");
        });

        // 4. Loan.
        Step("Loan (Brighton -> Leeds, until 30/06/2027)", () =>
        {
            var parent = Club("Brighton"); var borrower = Club("Leeds");
            var player = Pick(parent);
            Console.WriteLine($"  {player.DisplayName} ({player.Id})");
            int wage = player.Contract!.Wage;
            var plan = session.PlanLoanCreate(player, borrower.FirstTeam!.Address, new LoanOffer(wage, today, new DateOnly(2027, 6, 30)), all);
            if (!write) return;
            session.ApplyLoanCreate(plan);
            var after = Reload(player);
            Check(LoanHolder(after) != 0, "has a loan holder");
            Check(mem.ReadUInt64(player.Address + (ulong)p.Team!.PlayerTeam) == borrower.FirstTeam!.Address, $"plays for {borrower.DisplayName}");
            Check(after.Club?.Address == parent.Address, $"contract stays at {parent.DisplayName}");
        });

        // 5. Release.
        Step("Release (Fulham)", () =>
        {
            var club = Club("Fulham");
            var player = Pick(club);
            Console.WriteLine($"  {player.DisplayName} ({player.Id})");
            var service = new ReleaseService(mem, p, session.ModuleBase, () => session.Staff.LoadAll());
            var plan = service.Plan(player);
            if (!write) return;
            service.Apply(plan);
            var after = Reload(player);
            Check(after.Contract is null && after.Club is null, $"free agent (club {after.Club?.DisplayName ?? "none"})");
            Check(!InSquad(club.FirstTeam!, player), "not in the old squad");
        });

        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
