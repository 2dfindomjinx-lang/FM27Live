using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fm27Editor.Core.CodeGen;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Presets;

/// <summary>
/// <c>academy-check</c>: single-command end-to-end verification of Wonderkid Academy and Development Speed
/// against the live game save (read-only), with automatic baseline capture and before/after holiday diffing.
/// Usage:
///   academy-check [--reset] [--update] [--baseline path] [--club id|name] [--limit n]
/// </summary>
static partial class AcademyCheckCli
{
    internal sealed record BaselinePlayer(int Id, string Name, int? Age, int? ClubId, string ClubName, short Ca, short Pa);

    internal sealed record AcademyBaseline(
        string Build,
        string CapturedAtUtc,
        string? GameDate,
        int TotalPlayers,
        long? GoldenCalls,
        long? GoldenRaised,
        int? GoldenWonderkids,
        long? AccelCalls,
        long? AccelHits,
        int[] FocusClubIds,
        IReadOnlyList<BaselinePlayer> Players);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AcademyBaseline))]
    internal sealed partial class BaselineJsonContext : JsonSerializerContext;

    static readonly string DefaultBaselinePath = Path.Combine(Path.GetTempPath(), "fm27-academy-baseline.json");

    public static int Run(string[] args, Func<bool, GameSession> attach)
    {
        bool reset = false, update = false;
        int limit = 30;
        string baselinePath = DefaultBaselinePath;
        var clubQueries = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--reset": reset = true; break;
                case "--update": update = true; break;
                case "--baseline" when i + 1 < args.Length: baselinePath = args[++i]; break;
                case "--club" when i + 1 < args.Length: clubQueries.Add(args[++i]); break;
                case "--limit" when i + 1 < args.Length: limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Usage: academy-check [--reset] [--update] [--baseline path] [--club id|name] [--limit n]");
            }
        }

        int pass = 0, warn = 0, fail = 0;
        void Pass(string msg) { pass++; Console.WriteLine($"  PASS  {msg}"); }
        void Warn(string msg) { warn++; Console.WriteLine($"  WARN  {msg}"); }
        void Fail(string msg) { fail++; Console.WriteLine($"  FAIL  {msg}"); }

        using var session = attach(false);
        var profile = session.Profile;
        var mem = session.Memory;
        var mb = session.Game.ModuleBase;

        var gameDate = session.ReadGameDate();
        var allPlayers = session.Players.LoadAll();
        foreach (var p in allPlayers) p.GameDate = gameDate;
        var clubs = session.LoadClubs(allPlayers);
        var nations = session.Nations.LoadAll();
        var staff = session.Staff.LoadAll();
        var continents = session.NationLinks.Continents(nations);

        var clubByAddr = clubs.ToDictionary(c => c.Address);
        var clubById = clubs.Where(c => c.Id is not null).GroupBy(c => c.Id!.Value).ToDictionary(g => g.Key, g => g.First());
        var playerByAddr = allPlayers.ToDictionary(p => p.Address);
        var playerById = allPlayers.Where(p => p.Id is not null).GroupBy(p => p.Id!.Value).ToDictionary(g => g.Key, g => g.First());
        var nationByAddr = nations.ToDictionary(n => n.Address);
        var nationById = nations.Where(n => n.Id is not null).GroupBy(n => n.Id!.Value).ToDictionary(g => g.Key, g => g.First());

        var continentIdByAddr = new Dictionary<ulong, int>();
        var continentById = new Dictionary<int, ContinentRecord>();
        foreach (var cont in continents)
        {
            if (TryReadInt32(mem, cont.Address + 0xC, out int cid))
            {
                continentIdByAddr[cont.Address] = cid;
                continentById.TryAdd(cid, cont);
            }
        }

        AcademyBaseline? baseline = null;
        if (!reset && File.Exists(baselinePath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(baselinePath), BaselineJsonContext.Default.AcademyBaseline);
                if (loaded is not null && string.Equals(loaded.Build, profile.Build, StringComparison.Ordinal))
                    baseline = loaded;
                else if (loaded is not null)
                    Console.WriteLine($"(Existing baseline build '{loaded.Build}' != '{profile.Build}'; treating as fresh baseline)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(Could not read baseline '{baselinePath}': {ex.Message}; capturing fresh baseline)");
            }
        }

        // ------------------------------------------------------------------
        // [1] GAME, SAVE & OFFSET HEALTH
        // ------------------------------------------------------------------
        Console.WriteLine("=================================================================");
        Console.WriteLine("[1] GAME & SAVE SNAPSHOT");
        Console.WriteLine("=================================================================");
        Console.WriteLine($"  Build       : {profile.Build} ({profile.Architecture}) moduleBase 0x{mb:X}");
        if (baseline?.GameDate is { } bDateStr && DateOnly.TryParseExact(bDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bDate) && gameDate is { } curDate)
            Console.WriteLine($"  Game Date   : {bDate:yyyy-MM-dd} -> {curDate:yyyy-MM-dd} ({curDate.DayNumber - bDate.DayNumber:+#;-#;0} days)");
        else
            Console.WriteLine($"  Game Date   : {gameDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown"}");
        Console.WriteLine($"  Entities    : Players={allPlayers.Count:N0}" +
            (baseline is not null ? $" ({allPlayers.Count - baseline.TotalPlayers:+#;-#;0})" : "") +
            $", Clubs={clubs.Count:N0}, Nations={nations.Count:N0}, Staff={staff.Count:N0}");

        if (allPlayers.Count == 0 || gameDate is null) Fail("Save does not appear loaded cleanly (0 players or unreadable gameDate).");
        else Pass($"Save loaded cleanly ({allPlayers.Count:N0} players at {gameDate:yyyy-MM-dd}).");

        int condEqCa = 0, badMorale = 0, badNatInfo = 0, badCaPa = 0;
        int sampleCount = Math.Min(1000, allPlayers.Count);
        int step = Math.Max(1, allPlayers.Count / Math.Max(1, sampleCount));
        for (int idx = 0; idx < allPlayers.Count; idx += step)
        {
            var p = allPlayers[idx];
            if (p.CurrentAbility is < 0 or > 200 || p.PotentialAbility is < 0 or > 200) badCaPa++;
            var pf = session.PlayerFields.ReadAll(p);
            if (pf.TryGetValue(PlayerField.Condition, out long cond) && cond == p.CurrentAbility) condEqCa++;
            if (pf.TryGetValue(PlayerField.Morale, out long morale) && (morale < 0 || morale > 20)) badMorale++;
            if (pf.TryGetValue(PlayerField.NationalityInfo, out long natInfo) && natInfo != 0 && (natInfo < 80 || natInfo > 90)) badNatInfo++;
        }
        if (condEqCa > 10 || badMorale > 0 || badNatInfo > 0 || badCaPa > 0)
            Fail($"Player field sanity failed: cond==CA:{condEqCa}, badMorale:{badMorale}, badNatInfo:{badNatInfo}, badCaPa:{badCaPa}");
        else
            Pass($"Player field sanity ok on sample (cond==CA:{condEqCa}, badMorale:0, badNatInfo:0, badCaPa:0).");

        // ------------------------------------------------------------------
        // [2] WONDERKID ACADEMY HOOK (GoldenGenerationHook)
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine("[2] WONDERKID ACADEMY HOOK");
        Console.WriteLine("=================================================================");
        var hook = session.GoldenGenerationHook;
        GoldenGenerationState? gState = null;
        try
        {
            ulong site = hook.FindSite();
            var siteBytes = mem.Read(site, 8);
            Console.WriteLine($"  Site RVA    : 0x{site - mb:X} (VA 0x{site:X}), bytes: {Convert.ToHexString(siteBytes)}");
            if (profile.Team?.Club is null || profile.Club?.Nation is null || profile.Nation?.Continent is null)
                Fail("Profile is missing one of Team.Club / Club.Nation / Nation.Continent required for scope filtering.");
            else
                Pass($"Hook pointer chain configured (Player+{profile.Team.PlayerTeam:X} -> Team+{profile.Team.Club:X} -> Club+{profile.Club.Nation:X} -> Nation+{profile.Nation.Continent:X} -> +0xC).");

            gState = hook.Read();
            if (gState is null)
            {
                Warn("Wonderkid Academy hook is NOT INSTALLED yet in game memory.");
            }
            else
            {
                var st = gState.Settings;
                ulong codeTarget = gState.Cave + (ulong)(profile.IsArm64 ? 0xC000 : 0x40);
                if (profile.IsArm64 && !Arm64CodePatch.IsBranchReachable(site, codeTarget))
                    Fail($"ARM64 branch unreachable: site 0x{site:X} -> code 0x{codeTarget:X}");
                else
                    Pass($"Hook installed cleanly (cave 0x{gState.Cave:X}, code 0x{codeTarget:X}).");

                var idNames = st.Ids.Select(id => FormatScopeId(st.Scope, id, clubById, nationById, continentById));
                string dCalls = baseline?.GoldenCalls is { } bc ? $" ({gState.Calls - bc:+#;-#;0})" : "";
                string dRaised = baseline?.GoldenRaised is { } br ? $" ({gState.Raised - br:+#;-#;0})" : "";
                string dWk = baseline?.GoldenWonderkids is { } bwk ? $" ({gState.Wonderkids - bwk:+#;-#;0})" : "";
                Console.WriteLine($"  Status      : {(st.Enabled ? "ON" : "OFF")} | PA {st.MinPa}..{st.MaxPa} | Wonderkid {st.WonderkidChance}%");
                Console.WriteLine($"  Scope       : {st.Scope}" + (st.Ids.Count > 0 ? $" -> [{string.Join(", ", idNames)}]" : " (Every club)"));
                Console.WriteLine($"  Counters    : Calls={gState.Calls}{dCalls}, Raised={gState.Raised}{dRaised}, Wonderkids={gState.Wonderkids}{dWk}");

                if (st.Enabled && st.Scope != GoldenGenerationScope.All && st.Ids.Count == 0)
                    Fail($"Hook is ON with scope {st.Scope} but Id list is empty!");
                if (baseline?.GoldenCalls is { } prevCalls && gState.Calls < prevCalls)
                    Warn($"Hook Calls decreased ({prevCalls} -> {gState.Calls}); the game was likely restarted or save reloaded cleanly.");
            }
        }
        catch (Exception ex)
        {
            Fail($"Wonderkid Academy hook check failed: {ex.GetType().Name}: {ex.Message}");
        }

        // ------------------------------------------------------------------
        // [3] ACCELERATE DEVELOPMENT HOOK
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine("[3] DEVELOPMENT SPEED HOOK");
        Console.WriteLine("=================================================================");
        AcceleratedState? aState = null;
        try
        {
            var accel = session.AcceleratedDevelopment;
            ulong aSite = accel.FindSite();
            aState = accel.Read();
            Console.WriteLine($"  Site RVA    : 0x{aSite - mb:X} (VA 0x{aSite:X})");
            if (aState is null)
            {
                Console.WriteLine("  Status      : NOT INSTALLED");
            }
            else
            {
                string dCalls = baseline?.AccelCalls is { } ac ? $" ({aState.Calls - ac:+#;-#;0})" : "";
                string dHits = baseline?.AccelHits is { } ah ? $" ({aState.Hits - ah:+#;-#;0})" : "";
                Console.WriteLine($"  Status      : INSTALLED (cave 0x{aState.Cave:X}, LiftZero={aState.LiftZeroGains})");
                Console.WriteLine($"  Counters    : Calls={aState.Calls}{dCalls}, Hits={aState.Hits}{dHits}, TrackedPlayers={aState.Players.Count}");
                int staleCount = 0;
                foreach (var entry in aState.Players)
                {
                    if (!playerByAddr.TryGetValue(entry.Address, out var live) || live.Id != entry.PlayerId)
                        staleCount++;
                }
                if (staleCount > 0)
                    Warn($"Development Speed table has {staleCount}/{aState.Players.Count} stale/unmatched player pointers (normal only if players retired/deleted; clear stale rows if switching saves).");
                else
                    Pass($"All {aState.Players.Count} Development Speed table entries match live player pointers.");
            }
        }
        catch (Exception ex)
        {
            Fail($"Development Speed check failed: {ex.GetType().Name}: {ex.Message}");
        }

        // ------------------------------------------------------------------
        // [4] WONDERKID HOOK RING BUFFER AUDIT
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine("[4] RING BUFFER AUDIT (newest regens seen by hook)");
        Console.WriteLine("=================================================================");
        if (gState is null)
        {
            Console.WriteLine("  (skipped — Wonderkid Academy hook not installed)");
        }
        else if (gState.Recent.Count == 0)
        {
            if (gState.Calls > 0)
                Fail($"Hook reports Calls={gState.Calls} but ring buffer is empty.");
            else
                Console.WriteLine("  No regens have passed through the site yet (Calls=0).");
        }
        else
        {
            var st = gState.Settings;
            var targetIds = st.Ids.ToHashSet();
            int freshBudget = baseline?.GoldenCalls is { } bc && gState.Calls >= bc
                ? (int)Math.Min(gState.Recent.Count, gState.Calls - bc)
                : gState.Recent.Count;

            int missingFromList = 0, outOfRangeCaPa = 0, freshInScopeTotal = 0, freshInScopePaOk = 0, freshInScopePaBad = 0;
            int idx = 0;
            foreach (var hit in gState.Recent)
            {
                bool isFresh = idx < freshBudget;
                idx++;
                bool hasPlayer = playerByAddr.TryGetValue(hit.Player, out var p);
                if (!hasPlayer) missingFromList++;

                ClubEntry? club = hit.Club != 0 && clubByAddr.TryGetValue(hit.Club, out var rc) ? rc
                    : (p?.Club is { } pc && clubByAddr.TryGetValue(pc.Address, out var lc) ? lc : null);
                int? clubId = club?.Id;
                int? clubNationId = null, clubContId = null;
                if (club is not null && profile.Club?.Nation is { } cnOff && TryReadUInt64(mem, club.Address + (ulong)cnOff, out ulong natAddr)
                    && nationByAddr.TryGetValue(natAddr, out var natRec))
                {
                    clubNationId = natRec.Id;
                    ulong contAddr = session.NationLinks.ContinentPointer(natAddr);
                    if (contAddr != 0 && continentIdByAddr.TryGetValue(contAddr, out int cid)) clubContId = cid;
                }

                bool? inScope = st.Scope switch
                {
                    GoldenGenerationScope.All => true,
                    GoldenGenerationScope.Club => clubId is { } ci ? targetIds.Contains(ci) : null,
                    GoldenGenerationScope.Nation => clubNationId is { } ni ? targetIds.Contains(ni) : null,
                    GoldenGenerationScope.Continent => clubContId is { } coi ? targetIds.Contains(coi) : null,
                    _ => null,
                };

                bool caPaValid = p is null || (p.CurrentAbility is >= 1 and <= 200 && p.PotentialAbility is >= 1 and <= 200);
                if (!caPaValid) outOfRangeCaPa++;

                string verdict = "ring";
                if (isFresh && st.Enabled && p is not null && inScope == true)
                {
                    freshInScopeTotal++;
                    int floor = Math.Min(Math.Max((int)p.CurrentAbility, st.MinPa), GoldenGenerationHook.MaxPa);
                    int ceil = Math.Max(floor, st.MaxPa);
                    if (p.PotentialAbility >= floor && p.PotentialAbility <= ceil)
                    {
                        freshInScopePaOk++;
                        verdict = "OK";
                    }
                    else
                    {
                        freshInScopePaBad++;
                        verdict = $"PA_MISMATCH(want {floor}..{ceil})";
                    }
                }
                else if (inScope == false) verdict = "out-of-scope";

                if (idx <= limit)
                {
                    string who = p is not null ? $"{p.Id,-10} {Trunc(p.DisplayName, 22),-22} age={p.Age?.ToString() ?? "?":2} CA={p.CurrentAbility,3} PA={p.PotentialAbility,3}"
                        : $"0x{hit.Player:X} (not in player list)";
                    string clubLabel = club is not null ? $"{club.DisplayName}({club.Id})" : (hit.Club == 0 ? "no-club" : $"0x{hit.Club:X}");
                    Console.WriteLine($"  [{idx,2}]{(isFresh ? "*" : " ")} {who} | {Trunc(clubLabel, 24),-24} | {verdict}");
                }
            }

            if (outOfRangeCaPa > 0) Fail($"Found {outOfRangeCaPa} ring players with CA/PA out of 1..200.");
            if (missingFromList > 0) Warn($"{missingFromList}/{gState.Recent.Count} ring entries are not in current LoadAll() (e.g., temporary/unretained players).");
            if (freshInScopePaBad > 0)
                Fail($"Wonderkid PA mismatch on {freshInScopePaBad}/{freshInScopeTotal} fresh in-scope regens (expected within {gState.Settings.MinPa}..{gState.Settings.MaxPa}).");
            else if (freshInScopeTotal > 0)
                Pass($"All {freshInScopePaOk}/{freshInScopeTotal} fresh in-scope regens in ring match configured PA range.");
            else
                Console.WriteLine("  (No fresh in-scope regens in ring during this window.)");
        }

        // ------------------------------------------------------------------
        // [5] FOCUS CLUB YOUTH ROSTER & BASELINE DIFF
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine("[5] FOCUS CLUB YOUTH & BASELINE DIFF");
        Console.WriteLine("=================================================================");
        var focusClubs = ResolveFocusClubs(clubQueries, gState, clubs, baseline?.FocusClubIds);
        var focusClubIds = focusClubs.Where(c => c.Id is not null).Select(c => c.Id!.Value).Distinct().ToHashSet();
        Console.WriteLine("  Focus Clubs : " + (focusClubs.Count == 0 ? "(none matched)" : string.Join(", ", focusClubs.Select(c => $"{c.DisplayName} ({c.Id})"))));

        int? PlayerClubId(PlayerRecord p) => p.Club is { } ci && clubByAddr.TryGetValue(ci.Address, out var ce) ? ce.Id : null;

        if (baseline is not null)
        {
            var oldById = baseline.Players.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            var newPlayers = allPlayers.Where(p => p.Id is { } id && !oldById.ContainsKey(id))
                .Select(p => (Player: p, ClubId: PlayerClubId(p))).ToList();
            Console.WriteLine($"  New Players : {newPlayers.Count} appeared across save since baseline.");

            if (newPlayers.Count > 0)
            {
                int minPa = newPlayers.Min(x => x.Player.PotentialAbility);
                int maxPa = newPlayers.Max(x => x.Player.PotentialAbility);
                double avgPa = newPlayers.Average(x => x.Player.PotentialAbility);
                int pa150Plus = newPlayers.Count(x => x.Player.PotentialAbility >= 150);
                int pa170Plus = newPlayers.Count(x => x.Player.PotentialAbility >= 170);
                Console.WriteLine($"  New PA Dist : min={minPa}, avg={avgPa:F1}, max={maxPa}, PA>=150: {pa150Plus}, PA>=170: {pa170Plus}");

                var newInFocus = newPlayers.Where(x => x.ClubId is { } cid && focusClubIds.Contains(cid))
                    .OrderByDescending(x => x.Player.PotentialAbility).ThenByDescending(x => x.Player.CurrentAbility).ToList();
                Console.WriteLine($"  New In Focus: {newInFocus.Count} player(s):");
                foreach (var (p, cid) in newInFocus.Take(limit))
                    Console.WriteLine($"    + {p.Id,-10} {Trunc(p.DisplayName, 24),-24} age={p.Age?.ToString() ?? "?":2} CA={p.CurrentAbility,3} PA={p.PotentialAbility,3} | {p.ClubName} ({cid})");

                Console.WriteLine($"  Top New Save-Wide (by PA):");
                foreach (var (p, cid) in newPlayers.OrderByDescending(x => x.Player.PotentialAbility).ThenByDescending(x => x.Player.CurrentAbility).Take(12))
                    Console.WriteLine($"    * {p.Id,-10} {Trunc(p.DisplayName, 24),-24} age={p.Age?.ToString() ?? "?":2} CA={p.CurrentAbility,3} PA={p.PotentialAbility,3} | {(string.IsNullOrEmpty(p.ClubName) ? "Free Agent" : p.ClubName)} ({cid})");
            }

            var focusChanges = new List<(PlayerRecord Player, BaselinePlayer Old, int? NewClubId)>();
            foreach (var p in allPlayers)
            {
                if (p.Id is not { } pid || !oldById.TryGetValue(pid, out var old)) continue;
                int? newCid = PlayerClubId(p);
                bool inFocus = (newCid is { } nc && focusClubIds.Contains(nc)) || (old.ClubId is { } oc && focusClubIds.Contains(oc));
                if (!inFocus) continue;
                if (old.Ca != p.CurrentAbility || old.Pa != p.PotentialAbility || old.ClubId != newCid)
                    focusChanges.Add((p, old, newCid));
            }
            Console.WriteLine($"  Focus Changes (existing players CA/PA/Club changed): {focusChanges.Count}");
            foreach (var (p, old, newCid) in focusChanges
                .OrderByDescending(x => Math.Abs(x.Player.CurrentAbility - x.Old.Ca) + Math.Abs(x.Player.PotentialAbility - x.Old.Pa))
                .Take(limit))
            {
                string clubDiff = old.ClubId == newCid ? p.ClubName : $"{old.ClubName} ({old.ClubId}) -> {p.ClubName} ({newCid})";
                Console.WriteLine($"    ~ {p.Id,-10} {Trunc(p.DisplayName, 22),-22} age={p.Age?.ToString() ?? "?":2} CA {old.Ca}->{p.CurrentAbility} ({p.CurrentAbility - old.Ca:+#;-#;0}) PA {old.Pa}->{p.PotentialAbility} ({p.PotentialAbility - old.Pa:+#;-#;0}) | {clubDiff}");
            }
        }
        else
        {
            Console.WriteLine("  (No previous baseline yet — saving current state as baseline for next diff.)");
        }

        var focusYouth = allPlayers
            .Where(p => PlayerClubId(p) is { } cid && focusClubIds.Contains(cid) && (p.Age is null or <= 20))
            .OrderByDescending(p => p.PotentialAbility).ThenByDescending(p => p.CurrentAbility)
            .Take(limit).ToList();
        Console.WriteLine($"  Current Focus Youth (Age <= 20, top {focusYouth.Count}):");
        foreach (var p in focusYouth)
            Console.WriteLine($"    . {p.Id,-10} {Trunc(p.DisplayName, 24),-24} age={p.Age?.ToString() ?? "?":2} CA={p.CurrentAbility,3} PA={p.PotentialAbility,3} gap={p.PotentialAbility - p.CurrentAbility,3} | {p.ClubName}");

        if (baseline is null || reset || update)
        {
            var snapPlayers = allPlayers
                .Where(p => p.Id is not null)
                .Select(p => new BaselinePlayer(p.Id!.Value, p.DisplayName, p.Age, PlayerClubId(p), p.ClubName, p.CurrentAbility, p.PotentialAbility))
                .ToList();
            var doc = new AcademyBaseline(
                profile.Build,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                gameDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                allPlayers.Count,
                gState?.Calls,
                gState?.Raised,
                gState?.Wonderkids,
                aState?.Calls,
                aState?.Hits,
                [.. focusClubIds.Order()],
                snapPlayers);
            SaveBaseline(baselinePath, doc);
            Console.WriteLine($"  Baseline saved -> {baselinePath} ({snapPlayers.Count:N0} players, date={doc.GameDate})");
        }
        else
        {
            Console.WriteLine($"  Baseline kept  -> {baselinePath} (date={baseline.GameDate}; pass --update or --reset to advance baseline)");
        }

        // ------------------------------------------------------------------
        // [6] SUMMARY
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine($"SUMMARY: {pass} passed, {warn} warnings, {fail} failed");
        Console.WriteLine("=================================================================");
        return fail > 0 ? 1 : 0;
    }

    static List<ClubEntry> ResolveFocusClubs(IReadOnlyList<string> queries, GoldenGenerationState? gState,
        IReadOnlyList<ClubEntry> clubs, int[]? baselineFocusIds)
    {
        var result = new List<ClubEntry>();
        var seen = new HashSet<int>();
        void AddClub(ClubEntry? c) { if (c?.Id is { } id && seen.Add(id)) result.Add(c); }

        foreach (var q in queries)
        {
            if (int.TryParse(q, out int id)) AddClub(clubs.FirstOrDefault(c => c.Id == id));
            else AddClub(clubs.FirstOrDefault(c => (c.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.ShortName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)));
        }
        if (result.Count > 0) return result;

        if (gState?.Settings.Scope == GoldenGenerationScope.Club)
            foreach (int id in gState.Settings.Ids)
                AddClub(clubs.FirstOrDefault(c => c.Id == id));
        if (baselineFocusIds is not null)
            foreach (int id in baselineFocusIds)
                AddClub(clubs.FirstOrDefault(c => c.Id == id));
        if (result.Count == 0)
            AddClub(clubs.FirstOrDefault(c => c.Id == 680)); // Manchester United fallback
        return result;
    }

    static string FormatScopeId(GoldenGenerationScope scope, int id,
        IReadOnlyDictionary<int, ClubEntry> clubs,
        IReadOnlyDictionary<int, NationRecord> nations,
        IReadOnlyDictionary<int, ContinentRecord> continents) => scope switch
    {
        GoldenGenerationScope.Club => clubs.TryGetValue(id, out var c) ? $"{id} ({c.DisplayName})" : $"{id}",
        GoldenGenerationScope.Nation => nations.TryGetValue(id, out var n) ? $"{id} ({n.DisplayName})" : $"{id}",
        GoldenGenerationScope.Continent => continents.TryGetValue(id, out var co) ? $"{id} ({co.Name})" : $"{id}",
        _ => $"{id}",
    };

    static void SaveBaseline(string path, AcademyBaseline doc)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, BaselineJsonContext.Default.AcademyBaseline));
            File.Move(tmp, full, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    static bool TryReadInt32(IProcessMemory mem, ulong addr, out int value)
    {
        Span<byte> buf = stackalloc byte[4];
        if (!mem.TryRead(addr, buf)) { value = 0; return false; }
        value = BinaryPrimitives.ReadInt32LittleEndian(buf);
        return true;
    }

    static bool TryReadUInt64(IProcessMemory mem, ulong addr, out ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        if (!mem.TryRead(addr, buf)) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt64LittleEndian(buf);
        return true;
    }

    static string Trunc(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
