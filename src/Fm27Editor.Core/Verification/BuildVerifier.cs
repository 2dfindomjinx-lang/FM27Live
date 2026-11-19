using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Competitions;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Stadiums;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Verification;

public sealed record GroundTruthCounts(
    int? ExpectedPlayers = null,
    int? ExpectedStaff = null,
    int? ExpectedClubs = null,
    int? ExpectedHumans = null,
    double ToleranceFraction = 0.02);

public sealed record VerifyOptions(
    GroundTruthCounts? GroundTruth = null,
    bool RunWrites = false,
    bool RunNegativeSelfTest = false,
    bool ReleaseGate = false,
    string? CrashGateWritePath = null,
    string? CrashGateVerifyPath = null,
    bool KeepMutationsClean = true,
    string? ProfileRawJson = null);

public sealed record GateCheckResult(string Name, bool Passed, string Detail);

public sealed record VerificationReceipt(
    bool OverallPassed,
    bool PendingCrashGate,
    string Build,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<GateCheckResult> Checks,
    HeapGateResult? HeapGate = null,
    string? ReceiptJson = null);

public sealed class CrashGateState
{
    public required int Pid { get; init; }
    public required string Build { get; init; }
    public required ulong ModuleBase { get; init; }
    public required ulong GameUiLoadsBefore { get; init; }
    public required string GameDateBefore { get; init; }
    public required string HeapGateMode { get; init; }
    public required IReadOnlyList<HeapAllocationEntry> Allocations { get; init; }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteNumber("pid", Pid);
        w.WriteString("build", Build);
        w.WriteString("moduleBase", $"0x{ModuleBase:X}");
        w.WriteNumber("gameUiLoadsBefore", GameUiLoadsBefore);
        w.WriteString("gameDateBefore", GameDateBefore);
        w.WriteString("heapGateMode", HeapGateMode);
        w.WriteStartArray("allocations");
        foreach (var a in Allocations)
        {
            w.WriteStartObject();
            w.WriteString("address", $"0x{a.Address:X}");
            w.WriteNumber("requestedBytes", a.RequestedBytes);
            w.WriteString("origin", a.Origin.ToString());
            w.WriteBoolean("passed", a.Verification.Passed);
            w.WriteString("mode", a.Verification.Mode);
            if (a.Verification.UserTag is { } ut) w.WriteNumber("userTag", ut);
            if (a.Verification.MallocSize is { } msVal) w.WriteNumber("mallocSize", msVal);
            w.WriteString("detail", a.Verification.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static CrashGateState Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var allocs = new List<HeapAllocationEntry>();
        foreach (var item in r.GetProperty("allocations").EnumerateArray())
        {
            ulong addr = ulong.Parse(item.GetProperty("address").GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int req = item.GetProperty("requestedBytes").GetInt32();
            var origin = Enum.Parse<AllocationOrigin>(item.GetProperty("origin").GetString()!);
            bool passed = item.GetProperty("passed").GetBoolean();
            string mode = item.GetProperty("mode").GetString() ?? "";
            int? tag = item.TryGetProperty("userTag", out var tv) ? tv.GetInt32() : null;
            ulong? msize = item.TryGetProperty("mallocSize", out var mv) ? mv.GetUInt64() : null;
            string detail = item.TryGetProperty("detail", out var dv) ? dv.GetString() ?? "" : "";
            allocs.Add(new(addr, req, origin, new(passed, mode, tag, msize, detail)));
        }
        return new CrashGateState
        {
            Pid = r.GetProperty("pid").GetInt32(),
            Build = r.GetProperty("build").GetString()!,
            ModuleBase = ulong.Parse(r.GetProperty("moduleBase").GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            GameUiLoadsBefore = r.GetProperty("gameUiLoadsBefore").GetUInt64(),
            GameDateBefore = r.GetProperty("gameDateBefore").GetString()!,
            HeapGateMode = r.GetProperty("heapGateMode").GetString() ?? "unknown",
            Allocations = allocs,
        };
    }
}

/// <summary>
/// Six-layer build verification gate: RTTI audit, 100% subtype completeness + ground truth, per-field semantics +
/// known-entity fixtures, write round-trips under <see cref="WriteAudit"/> + <see cref="HeapGate"/>, live Continue +
/// Load Game crash gate, and deliberate negative self-tests.
/// </summary>
public static class BuildVerifier
{
    public static VerificationReceipt Verify(
        IProcessMemory memory,
        GameProcess game,
        OffsetProfile profile,
        VerifyOptions? options = null,
        Func<string, string?>? resolveLayout = null)
    {
        options ??= new VerifyOptions();
        var checks = new List<GateCheckResult>();
        var rawJson = options.ProfileRawJson ?? profile.ToJson();
        var resolvedNode = OffsetProfile.ResolveJson(rawJson, resolveLayout);

        // Layer 1: 100% RTTI audit of every vtable in profile.
        RunRttiAudit(memory, game, profile, resolvedNode, resolveLayout, checks);

        // Layer 2: 100% subtype completeness scan + optional/release ground truth.
        RunCompletenessAndGroundTruth(memory, game, profile, options, checks);

        // Layer 3: Per-field semantics + known-entity fixtures.
        RunSemanticChecks(memory, game, profile, checks);
        RunKnownEntityFixtures(memory, game, profile, checks);

        // Layer 4/5/6: Negative self-tests, Write round-trips + HeapGate, Crash Gate.
        HeapGateResult? heapResult = null;
        bool pendingCrashGate = false;

        if (options.RunNegativeSelfTest)
            RunNegativeSelfTests(memory, game, rawJson, resolveLayout, checks);

        if (!string.IsNullOrEmpty(options.CrashGateVerifyPath))
            VerifyCrashGate(memory, game, profile, options.CrashGateVerifyPath, checks);

        if (options.RunWrites && string.IsNullOrEmpty(options.CrashGateWritePath))
            heapResult = RunWriteRoundTrips(memory, game, profile, checks);

        if (!string.IsNullOrEmpty(options.CrashGateWritePath))
        {
            heapResult = PrepareCrashGate(memory, game, profile, options.CrashGateWritePath, checks, out pendingCrashGate);
        }

        if (options.ReleaseGate)
        {
            bool hasGt = options.GroundTruth is { } gt &&
                (gt.ExpectedPlayers is not null || gt.ExpectedStaff is not null || gt.ExpectedClubs is not null);
            checks.Add(new("release.groundTruthSupplied", hasGt,
                hasGt ? "ground truth supplied" : "release-gate requires --expect-players/staff/clubs or --ground-truth"));

            bool hasWrites = options.RunWrites || (!string.IsNullOrEmpty(options.CrashGateVerifyPath));
            checks.Add(new("release.writesOrCrashVerify", hasWrites,
                hasWrites ? "write audit / crash verify covered" : "release-gate requires --writes and --crash-gate-verify"));

            bool hasCrashVerify = !string.IsNullOrEmpty(options.CrashGateVerifyPath) &&
                checks.Any(c => c.Name == "crashGate.verify" && c.Passed);
            checks.Add(new("release.crashGateVerified", hasCrashVerify,
                hasCrashVerify ? "crash gate verified after Continue + Load Game" : "release-gate requires passing --crash-gate-verify"));
        }

        bool overall = checks.All(c => c.Passed);
        var receipt = new VerificationReceipt(overall, pendingCrashGate, profile.Build, DateTimeOffset.UtcNow, checks, heapResult);
        return receipt with { ReceiptJson = FormatReceiptJson(receipt) };
    }

    static void RunRttiAudit(
        IProcessMemory memory,
        GameProcess game,
        OffsetProfile profile,
        JsonObject resolvedNode,
        Func<string, string?>? resolveLayout,
        List<GateCheckResult> checks)
    {
        try
        {
            var image = ModuleImage.ReadForDiscovery(memory, game.ModuleBase, game.ModuleSize);
            var rtti = RttiIndex.Build(image);

            string expectedBuild = profile.MatchBuild ?? profile.Build;
            string? liveBuild = RootFinder.ReadAsciiZ(image, profile.BuildStringRva);
            bool buildOk = liveBuild == expectedBuild;
            checks.Add(new("rtti.buildString", buildOk,
                buildOk ? $"matched '{expectedBuild}' at 0x{profile.BuildStringRva:X}"
                        : $"expected '{expectedBuild}' at 0x{profile.BuildStringRva:X}, read '{liveBuild ?? "<null>"}'"));

            var specs = profile.VtableClasses ?? InferLayoutVtableClasses(profile, resolveLayout);
            if (specs is null || specs.Count == 0)
            {
                checks.Add(new("rtti.vtableAudit", false, "No vtableClasses map available to audit vtables"));
                return;
            }

            int checkedCount = 0;
            var failures = new List<string>();
            foreach (var (path, spec) in specs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var node = ProfileJsonResolver.GetByPath(resolvedNode, path);
                if (node is not JsonValue val || val.GetValueKind() != JsonValueKind.String ||
                    !TryParseHexUInt(val.GetValue<string>(), out uint vtRva))
                {
                    if (!spec.Optional) failures.Add($"{path}: missing");
                    continue;
                }
                checkedCount++;
                var entry = rtti.EntryOf(vtRva);
                if (entry is null)
                {
                    failures.Add($"{path} (0x{vtRva:X}): no RTTI entry");
                    continue;
                }
                bool classMatches = string.Equals(entry.CanonicalName, spec.Class, StringComparison.Ordinal) ||
                                    (spec.FallbackClasses?.Any(fb => string.Equals(fb, entry.CanonicalName, StringComparison.Ordinal)) ?? false);
                if (!classMatches)
                {
                    failures.Add($"{path} (0x{vtRva:X}): expected {spec.Class}, got {entry.CanonicalName}");
                    continue;
                }
                if (spec.Offset >= 0 && entry.SubobjectOffset != spec.Offset)
                    failures.Add($"{path} (0x{vtRva:X}): expected subobject +{spec.Offset}, got +{entry.SubobjectOffset}");
            }

            checks.Add(new("rtti.vtableAudit", failures.Count == 0 && checkedCount > 0,
                failures.Count == 0
                    ? $"audited {checkedCount} vtable paths (100% match)"
                    : $"{failures.Count} mismatches across {checkedCount} vtables: {string.Join("; ", failures.Take(5))}"));
        }
        catch (Exception ex)
        {
            checks.Add(new("rtti.vtableAudit", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    static IReadOnlyDictionary<string, VtableClassSpec>? InferLayoutVtableClasses(OffsetProfile profile, Func<string, string?>? resolveLayout)
    {
        string guess = profile.Architecture == "arm64"
            ? (profile.Build.Contains("touch", StringComparison.OrdinalIgnoreCase) ? "layouts/touch-macos.json" : "layouts/macos.json")
            : "layouts/windows.json";
        if (ProfileJsonResolver.LoadLayoutJson(guess, resolveLayout) is not { } json) return null;
        return OffsetProfile.Load(json, resolveLayout).VtableClasses;
    }

    static void RunCompletenessAndGroundTruth(
        IProcessMemory memory,
        GameProcess game,
        OffsetProfile profile,
        VerifyOptions options,
        List<GateCheckResult> checks)
    {
        try
        {
            // Scan playerList directly for unmapped vtables.
            ulong pRoot = game.ModuleBase + profile.PlayerListRootRva;
            ulong begin = ReadU64Safe(memory, pRoot + (ulong)profile.PlayerListBeginOffset);
            ulong end = ReadU64Safe(memory, pRoot + (ulong)profile.PlayerListEndOffset);
            int totalPtrs = begin != 0 && end >= begin && (end - begin) % 8 == 0 && (end - begin) / 8 <= 1_000_000
                ? (int)((end - begin) / 8)
                : 0;
            var knownPlayerVts = profile.PlayerVtableRvas.Select(v => game.ModuleBase + v).ToHashSet();
            int unmappedPlayers = 0;
            if (totalPtrs > 0)
            {
                var raw = memory.Read(begin, totalPtrs * 8);
                for (int i = 0; i < totalPtrs; i++)
                {
                    ulong ptr = BitConverter.ToUInt64(raw, i * 8);
                    if (ptr == 0) continue;
                    ulong vt = ReadU64Safe(memory, ptr);
                    if (!knownPlayerVts.Contains(vt)) unmappedPlayers++;
                }
            }
            checks.Add(new("completeness.playerVtables", totalPtrs > 0 && unmappedPlayers == 0,
                totalPtrs == 0 ? "playerList is empty" : $"{totalPtrs} pointers scanned, {unmappedPlayers} unmapped vtables"));

            var players = new PlayerRepository(memory, profile, game.ModuleBase).LoadAll();
            bool playerCountSane = players.Count >= 100 && Math.Abs(players.Count - totalPtrs) <= Math.Max(5, totalPtrs / 100);
            checks.Add(new("completeness.playerLoadAll", playerCountSane,
                $"loaded {players.Count} / {totalPtrs} entries"));

            // Scan staffList directly for unmapped subtypes.
            int staffLoaded = 0, unmappedStaff = 0, humansLoaded = 0, totalStaffPtrs = 0;
            if (profile.Staff is { } so)
            {
                ulong sRoot = so.DirectStaffVector
                    ? game.ModuleBase + so.PersonListRootRva
                    : ReadU64Safe(memory, game.ModuleBase + so.PersonListRootRva);
                ulong sBegin = sRoot != 0 ? ReadU64Safe(memory, sRoot + (ulong)so.PersonListOffset) : 0;
                ulong sEnd = sRoot != 0 ? ReadU64Safe(memory, sRoot + (ulong)so.PersonListOffset + 8) : 0;
                totalStaffPtrs = sBegin != 0 && sEnd >= sBegin && (sEnd - sBegin) % 8 == 0 && (sEnd - sBegin) / 8 <= 1_000_000
                    ? (int)((sEnd - sBegin) / 8)
                    : 0;
                var knownStaffVts = so.Kinds.Select(k => game.ModuleBase + (so.DirectStaffVector ? k.StaffVtableRva : k.PersonVtableRva)).ToHashSet();
                if (totalStaffPtrs > 0)
                {
                    var raw = memory.Read(sBegin, totalStaffPtrs * 8);
                    for (int i = 0; i < totalStaffPtrs; i++)
                    {
                        ulong ptr = BitConverter.ToUInt64(raw, i * 8);
                        if (ptr == 0) continue;
                        ulong vt = ReadU64Safe(memory, ptr);
                        if (!knownStaffVts.Contains(vt)) unmappedStaff++;
                    }
                }
                var staff = new StaffRepository(memory, profile, game.ModuleBase).LoadAll();
                staffLoaded = staff.Count;
                humansLoaded = staff.Count(s => s.Kind.Manager);
                checks.Add(new("completeness.staffVtables", totalStaffPtrs > 0 && unmappedStaff == 0 && staffLoaded > 0,
                    $"scanned {totalStaffPtrs} pointers ({unmappedStaff} unmapped), decoded {staffLoaded} ({humansLoaded} human managers)"));
            }

            if (options.GroundTruth is { } gt)
            {
                if (gt.ExpectedPlayers is { } ep)
                {
                    int diff = Math.Abs(players.Count - ep);
                    double limit = Math.Max(5.0, ep * gt.ToleranceFraction);
                    checks.Add(new("groundTruth.players", diff <= limit,
                        $"actual={players.Count}, expected={ep}, diff={diff} (limit={limit:F0})"));
                }
                if (gt.ExpectedStaff is { } es)
                {
                    int diff = Math.Abs(staffLoaded - es);
                    double limit = Math.Max(5.0, es * gt.ToleranceFraction);
                    checks.Add(new("groundTruth.staff", diff <= limit,
                        $"actual={staffLoaded}, expected={es}, diff={diff} (limit={limit:F0})"));
                }
                if (gt.ExpectedHumans is { } eh)
                {
                    bool ok = humansLoaded >= eh && humansLoaded <= eh + 2;
                    checks.Add(new("groundTruth.humans", ok,
                        $"actual={humansLoaded}, expected>={eh}"));
                }
                if (gt.ExpectedClubs is { } ec)
                {
                    var clubs = ClubDirectory.FromPlayers(memory, players, profile, game.ModuleBase);
                    int diff = Math.Abs(clubs.Count - ec);
                    double limit = Math.Max(10.0, ec * Math.Max(0.10, gt.ToleranceFraction));
                    int minClubs = ec >= 50 ? 50 : 1;
                    checks.Add(new("groundTruth.clubs", clubs.Count >= minClubs && diff <= limit,
                        $"fromPlayers={clubs.Count}, expected~{ec}"));
                }
            }
        }
        catch (Exception ex)
        {
            checks.Add(new("completeness", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    static void RunSemanticChecks(IProcessMemory memory, GameProcess game, OffsetProfile profile, List<GateCheckResult> checks)
    {
        var expectedEmpty = new HashSet<string>(profile.ExpectedEmpty, StringComparer.OrdinalIgnoreCase);

        // 1. Game Date + CrossCheck
        try
        {
            var date = GameClock.Read(memory, profile, game.ModuleBase);
            bool ok = date is { } d && d.Year is >= 2020 and <= 2100;
            checks.Add(new("semantics.gameDate", ok,
                ok ? $"date={date:yyyy-MM-dd}" : "GameClock.Read returned null or implausible year"));
        }
        catch (Exception ex)
        {
            checks.Add(new("semantics.gameDate", false, ex.Message));
        }

        // 2. Players: attributes 0..100 + GK/ST Handling gap >= 20 + natural-20 positions + subtype fields.
        IReadOnlyList<PlayerRecord> players = [];
        try
        {
            players = new PlayerRepository(memory, profile, game.ModuleBase).LoadAll();
            var sample = players.Take(2000).ToList();
            bool attrsRangeOk = sample.Count > 0 && sample.All(p => p.RawAttributes.All(b => b <= 100));
            var gks = sample.Where(p => p.Positions[(int)PlayerPosition.Goalkeeper] >= 18).ToList();
            var sts = sample.Where(p => p.Positions[(int)PlayerPosition.Striker] >= 18 && p.Positions[(int)PlayerPosition.Goalkeeper] < 5).ToList();
            double gkHandling = gks.Count > 0 ? gks.Average(p => (double)p.Raw(PlayerAttribute.Handling)) : 0;
            double stHandling = sts.Count > 0 ? sts.Average(p => (double)p.Raw(PlayerAttribute.Handling)) : 0;
            double handlingGap = gkHandling - stHandling;
            bool nat20Ok = sample.Any(p => p.Positions.Any(pos => pos == 20));
            bool subtypeCoverageOk = profile.PlayerVtableRvas.Count == 1 ||
                players.GroupBy(p => p.VtableRva).All(g =>
                {
                    int total = g.Count();
                    int valid = g.Count(p => p.DateOfBirth is { Year: >= 1950 and <= 2020 } && !string.IsNullOrWhiteSpace(p.DisplayName));
                    return total > 0 && valid * 10 >= total * 8;
                });

            bool playersSemOk = attrsRangeOk && nat20Ok && gks.Count >= 5 && sts.Count >= 5 && handlingGap >= 20 && subtypeCoverageOk;
            checks.Add(new("semantics.players", playersSemOk,
                $"sample={sample.Count}, GKs={gks.Count} (Handling={gkHandling:F1}), STs={sts.Count} (Handling={stHandling:F1}), gap={handlingGap:F1}, nat20={nat20Ok}, subtypesOk={subtypeCoverageOk}"));
        }
        catch (Exception ex)
        {
            checks.Add(new("semantics.players", false, ex.Message));
        }

        // 3. Staff: coaching/GK gap + jobPersonAnchor match + HumanManager attributes.
        try
        {
            var staff = new StaffRepository(memory, profile, game.ModuleBase).LoadAll();
            bool nonEmpty = staff.Count > 0;
            var gkCoaches = staff.Where(s => s.JobCode == 34).ToList(); // GK Coach
            var nonGk = staff.Where(s => s.JobCode is 2 or 20 or 16).Take(100).ToList();
            double gkGap = (gkCoaches.Count > 0 && nonGk.Count > 0)
                ? gkCoaches.Average(s => (double)s.Attribute(StaffAttribute.CoachingGoalkeeping)) -
                  nonGk.Average(s => (double)s.Attribute(StaffAttribute.CoachingGoalkeeping))
                : 99;
            int jobsWithTeam = staff.Count(s => s.Club is not null || s.TeamName is not null);
            bool mgrsOk = !staff.Any(s => s.Kind.Manager) ||
                          staff.Where(s => s.Kind.Manager).All(s => s.ManagerAttributes is not null);

            bool ok = nonEmpty && gkGap >= 3.0 && jobsWithTeam >= Math.Min(50, staff.Count / 10) && mgrsOk;
            checks.Add(new("semantics.staff", ok,
                $"count={staff.Count}, jobsResolved={jobsWithTeam}, gkCoachGap={gkGap:F1}, managersOk={mgrsOk}"));
        }
        catch (Exception ex)
        {
            checks.Add(new("semantics.staff", false, ex.Message));
        }

        // 4. Mandatory non-empty readers + Individual ClubFinance fields (CorporateFacilities 1..20 w/ variance).
        try
        {
            var clubs = ClubDirectory.FromPlayers(memory, players, profile, game.ModuleBase);
            var clubFields = new ClubFieldAccess(memory, profile, game.ModuleBase);
            var nonCompact = clubs.Take(80).Where(c => clubFields.Read(c.Address, ClubField.TransferBudgetSeason) is not null).ToList();

            bool cfOk = true;
            string cfDetail = "not mapped";
            if (clubFields.IsMapped(ClubField.CorporateFacilities))
            {
                var cfVals = nonCompact.Select(c => clubFields.Read(c.Address, ClubField.CorporateFacilities))
                    .Where(v => v is >= 1 and <= 20)
                    .Select(v => v!.Value)
                    .ToList();
                var distinct = cfVals.Distinct().Count();
                cfOk = nonCompact.Count >= 10 && cfVals.Count * 10 >= nonCompact.Count * 8 && distinct > 1;
                cfDetail = $"valid={cfVals.Count}/{nonCompact.Count}, distinct={distinct}";
            }

            bool balanceOk = nonCompact.Count >= 10 &&
                nonCompact.Any(c => clubFields.Read(c.Address, ClubField.Balance) != 0);
            checks.Add(new("semantics.clubFinance", cfOk && balanceOk && clubs.Count > 0,
                $"clubs={clubs.Count}, nonCompactSample={nonCompact.Count}, BalanceOk={balanceOk}, CorporateFacilities=({cfDetail})"));
        }
        catch (Exception ex)
        {
            checks.Add(new("semantics.clubFinance", false, ex.Message));
        }

        // 5. Nations, Stadiums, Competitions, Awards, Cities, Officials (mandatory unless expectedEmpty).
        void CheckNonEmpty(string key, Func<int> loadCount)
        {
            try
            {
                int n = loadCount();
                bool allowedEmpty = expectedEmpty.Contains(key);
                checks.Add(new($"semantics.{key}", n > 0 || allowedEmpty,
                    n > 0 ? $"count={n}" : allowedEmpty ? "0 (expectedEmpty)" : "empty (0 rows)"));
            }
            catch (Exception ex)
            {
                bool allowedEmpty = expectedEmpty.Contains(key);
                checks.Add(new($"semantics.{key}", allowedEmpty, allowedEmpty ? $"skipped ({ex.Message})" : ex.Message));
            }
        }

        CheckNonEmpty("nations", () => new NationRepository(memory, profile, game.ModuleBase).LoadAll().Count);
        CheckNonEmpty("stadiums", () => new StadiumRepository(memory, profile, game.ModuleBase).LoadAll().Count);
        CheckNonEmpty("competitions", () => new CompetitionRepository(memory, profile, game.ModuleBase).LoadAll(() => players).Count);
        CheckNonEmpty("awards", () => new Awards.AwardRepository(memory, profile, game.ModuleBase).LoadAll().Count);
        CheckNonEmpty("cities", () => new CityRepository(memory, profile, game.ModuleBase).LoadAll().Count);
        CheckNonEmpty("officials", () => new Officials.RefereeDirectory(memory, game.ModuleBase, (ulong)game.ModuleSize, profile.Officials, profile.GlobalArrays).ReadAll().Count);

        // 6. Promises globalRoot overlap when player promises exist; FutureTransfers.DateVector; Proposals.ChangedFlag.
        try
        {
            if (profile.Promises is { } pm)
            {
                var store = new PromiseStore(memory, profile, game.ModuleBase);
                ulong root = ReadU64Safe(memory, game.ModuleBase + pm.GlobalRootRva);
                ulong gBegin = root != 0 ? ReadU64Safe(memory, root + (ulong)pm.GlobalVector) : 0;
                ulong gEnd = root != 0 ? ReadU64Safe(memory, root + (ulong)pm.GlobalVector + 8) : 0;
                bool vecOk = gBegin != 0 && gEnd >= gBegin && (gEnd - gBegin) % 8 == 0 && (gEnd - gBegin) / 8 <= 200_000;
                var globalSet = new HashSet<ulong>();
                if (vecOk && gEnd > gBegin)
                {
                    var raw = memory.Read(gBegin, (int)(gEnd - gBegin));
                    for (int i = 0; i < raw.Length; i += 8) globalSet.Add(BitConverter.ToUInt64(raw, i));
                }
                int playerPromises = 0, overlap = 0;
                foreach (var p in players.Take(1000))
                {
                    foreach (var row in store.Read(PersonOwner.Of(p, profile)))
                    {
                        playerPromises++;
                        if (globalSet.Contains(row.Address)) overlap++;
                    }
                }
                bool ok = vecOk && (playerPromises == 0 || overlap > 0);
                checks.Add(new("semantics.promises", ok,
                    $"globalCount={globalSet.Count}, sampledPlayerPromises={playerPromises}, overlap={overlap}"));
            }
        }
        catch (Exception ex)
        {
            checks.Add(new("semantics.promises", expectedEmpty.Contains("promises"), ex.Message));
        }

        try
        {
            if (profile.FutureTransfers is { } ft && profile.Proposals is { } pr)
            {
                ulong root = ReadU64Safe(memory, game.ModuleBase + pr.ListRootRva);
                ulong fBegin = root != 0 ? ReadU64Safe(memory, root + (ulong)ft.DateVector) : 0;
                ulong fEnd = root != 0 ? ReadU64Safe(memory, root + (ulong)ft.DateVector + 8) : 0;
                bool ok = root != 0 && fBegin >= 0x1_0000_0000UL && fEnd >= fBegin && (fEnd - fBegin) % 8 == 0 && (fEnd - fBegin) / 8 <= 100_000;
                int count = ok ? (int)((fEnd - fBegin) / 8) : -1;
                checks.Add(new("semantics.futureTransfers", ok || expectedEmpty.Contains("futureTransfers"),
                    ok ? $"dateVector count={count}" : "invalid futureTransfers dateVector header"));
            }
        }
        catch (Exception ex)
        {
            checks.Add(new("semantics.futureTransfers", false, ex.Message));
        }

        try
        {
            if (profile.Proposals is { } pr)
            {
                var reader = new TransferProposalReader(memory, profile, game.ModuleBase);
                var sampleOffers = players.Take(400).SelectMany(p => reader.Read(p)).Take(30).ToList();
                bool cfOk = true;
                string cfMsg = pr.ChangedFlag is null ? "changedFlag=null" : $"changedFlag=0x{pr.ChangedFlag:X}";
                if (pr.ChangedFlag is { } cfOff)
                {
                    ulong root = ReadU64Safe(memory, game.ModuleBase + pr.ListRootRva);
                    bool rootValid = root != 0 && (pr.ManagerVtable is not { } mv || ReadU64Safe(memory, root) == game.ModuleBase + mv);
                    bool overlapsList = cfOff >= pr.ListOffset && cfOff < pr.ListOffset + 24;
                    bool overlapsDate = profile.FutureTransfers is { } ft && cfOff >= ft.DateVector && cfOff < ft.DateVector + 24;
                    Span<byte> b = stackalloc byte[1];
                    bool byteValid = rootValid && !overlapsList && !overlapsDate && memory.TryRead(root + (ulong)cfOff, b) && b[0] <= 1;
                    cfOk = byteValid;
                    cfMsg += rootValid
                        ? (overlapsList || overlapsDate ? " (overlaps vector)" : $" (manager={b[0]}, bool={byteValid})")
                        : " (invalid manager)";
                }
                else if (sampleOffers.Count == 0 && !expectedEmpty.Contains("proposals"))
                {
                    // Not an error on new/clean saves without open bids, still note it.
                }
                checks.Add(new("semantics.proposals", cfOk, $"sampledOffers={sampleOffers.Count}, {cfMsg}"));
            }
        }
        catch (Exception ex)
        {
            checks.Add(new("semantics.proposals", false, ex.Message));
        }
    }

    static void RunKnownEntityFixtures(IProcessMemory memory, GameProcess game, OffsetProfile profile, List<GateCheckResult> checks)
    {
        string? json = LoadFixtureJson();
        if (json is null)
        {
            checks.Add(new("fixtures.knownEntities", false, "offsets/fixtures/fm27-known-entities.json missing"));
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var players = new PlayerRepository(memory, profile, game.ModuleBase).LoadAll();
            var clubs = ClubDirectory.FromPlayers(memory, players, profile, game.ModuleBase).ToDictionary(c => c.Id ?? -1, c => c);
            var clubFields = new ClubFieldAccess(memory, profile, game.ModuleBase);
            var nations = new NationRepository(memory, profile, game.ModuleBase).LoadAll().ToDictionary(n => n.Id ?? -1, n => n);
            var nationFields = new NationFieldAccess(memory, profile, game.ModuleBase);
            var stadiums = new StadiumRepository(memory, profile, game.ModuleBase).LoadAll().ToDictionary(s => s.Id, s => s);
            var stadiumWriter = new StadiumWriter(memory, profile, game.ModuleBase);

            int verified = 0;
            var failures = new List<string>();

            foreach (var c in root.GetProperty("clubs").EnumerateArray())
            {
                int id = c.GetProperty("id").GetInt32();
                if (!clubs.TryGetValue(id, out var entry)) continue;
                verified++;
                string namePart = c.GetProperty("nameContains").GetString()!;
                if (!(entry.Name ?? "").Contains(namePart, StringComparison.OrdinalIgnoreCase) &&
                    !(entry.ShortName ?? "").Contains(namePart, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"club {id} name '{entry.DisplayName}' !~ '{namePart}'");
                if (c.TryGetProperty("yearFounded", out var yf) && clubFields.IsMapped(ClubField.YearFounded))
                {
                    long? actual = clubFields.Read(entry.Address, ClubField.YearFounded);
                    if (actual != yf.GetInt64()) failures.Add($"club {id} yearFounded={actual}, expected {yf.GetInt64()}");
                }
            }

            foreach (var s in root.GetProperty("stadiums").EnumerateArray())
            {
                int id = s.GetProperty("id").GetInt32();
                if (!stadiums.TryGetValue(id, out var entry)) continue;
                verified++;
                var f = stadiumWriter.ReadFields(entry.Address);
                if (f.TryGetValue(StadiumField.Capacity, out long cap))
                {
                    if (cap < s.GetProperty("capacityMin").GetInt64() || cap > s.GetProperty("capacityMax").GetInt64())
                        failures.Add($"stadium {id} capacity={cap}");
                }
                if (f.TryGetValue(StadiumField.BuildDate, out long bd) && s.TryGetProperty("buildYear", out var by))
                {
                    long actualYear = bd >= 10000 ? bd / 10000 : bd;
                    if (actualYear != by.GetInt64()) failures.Add($"stadium {id} buildYear={actualYear}, expected {by.GetInt64()}");
                }
            }

            foreach (var n in root.GetProperty("nations").EnumerateArray())
            {
                int id = n.GetProperty("id").GetInt32();
                if (!nations.TryGetValue(id, out var entry)) continue;
                verified++;
                if (n.TryGetProperty("yearAssociationFormed", out var yaf) &&
                    nationFields.Read(entry.Address, NationField.YearAssociationFormed) is { } actualYaf &&
                    actualYaf != yaf.GetInt64())
                    failures.Add($"nation {id} YearAssociationFormed={actualYaf}, expected {yaf.GetInt64()}");
                if (n.TryGetProperty("yearAffiliatedToFifa", out var yfifa) &&
                    nationFields.Read(entry.Address, NationField.YearAffiliatedToFifa) is { } actualFifa &&
                    actualFifa != yfifa.GetInt64())
                    failures.Add($"nation {id} YearAffiliatedToFifa={actualFifa}, expected {yfifa.GetInt64()}");
            }

            var playerById = players.Where(p => p.Id is not null).GroupBy(p => p.Id!.Value).ToDictionary(g => g.Key, g => g.First());
            foreach (var p in root.GetProperty("players").EnumerateArray())
            {
                int id = p.GetProperty("id").GetInt32();
                bool opt = p.TryGetProperty("optional", out var ov) && ov.GetBoolean();
                if (!playerById.TryGetValue(id, out var rec))
                {
                    if (!opt) failures.Add($"player {id} missing");
                    continue;
                }
                verified++;
                string dob = p.GetProperty("dob").GetString()!;
                if (rec.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != dob)
                    failures.Add($"player {id} dob={rec.DateOfBirth}, expected {dob}");
            }

            checks.Add(new("fixtures.knownEntities", verified > 0 && failures.Count == 0,
                failures.Count == 0
                    ? $"verified {verified} known entities"
                    : $"{failures.Count} fixture mismatch(es): {string.Join("; ", failures)}"));
        }
        catch (Exception ex)
        {
            checks.Add(new("fixtures.knownEntities", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    static HeapGateResult RunWriteRoundTrips(IProcessMemory memory, GameProcess game, OffsetProfile profile, List<GateCheckResult> checks)
    {
        using var heapGate = new HeapGate(memory);
        using var session = new GameSession(memory, game, profile, ownMemory: false);
        try
        {
            var players = session.Players.LoadAll();
            var player = players.FirstOrDefault(p => p.Id == 2000123586) ?? players[0];
            var clubs = session.LoadClubs(players);
            var club = clubs.FirstOrDefault(c => c.Id == 1736) ?? clubs.First();
            var nations = session.Nations.LoadAll();
            var england = nations.FirstOrDefault(n => n.Id == 765) ?? nations[0];

            // 1. Scalar player attribute round-trip with full player window WriteAudit.
            int playerSize = profile.PlayerObjectSize + (profile.PlayerInsertions.TryGetValue(player.VtableRva, out var ins) ? ins.Length : 0);
            byte rawBefore = player.Raw(PlayerAttribute.Passing);
            byte rawAlt = rawBefore >= 95 ? (byte)(rawBefore - 5) : (byte)(rawBefore + 5);
            var pWindow = new AuditWindow("player", player.Address, playerSize);
            var scalarAudit = WriteAudit.VerifyRoundTrip(memory, [pWindow],
                () => { session.Writer.SetAttributeRaw(session.Players.Reload(player.Address)!, PlayerAttribute.Passing, rawAlt); },
                () => { session.Writer.SetAttributeRaw(session.Players.Reload(player.Address)!, PlayerAttribute.Passing, rawBefore); });
            checks.Add(new("writes.playerScalarAudit", scalarAudit.Passed,
                scalarAudit.Passed ? $"Passing raw {rawBefore}->{rawAlt}->{rawBefore} (0 collateral bytes)" : string.Join("; ", scalarAudit.Violations)));

            // 2. Club Morale scalar round-trip.
            long? moraleBefore = session.ClubFields.Read(club.Address, ClubField.Morale);
            if (moraleBefore is { } mb)
            {
                long mAlt = mb == 14 ? 13 : 14;
                var clubWindow = new AuditWindow("club", club.Address, 0x200);
                var clubAudit = WriteAudit.VerifyRoundTrip(memory, [clubWindow],
                    () => session.ClubFields.Set(club.Address, ClubField.Morale, mb, mAlt),
                    () => session.ClubFields.Set(club.Address, ClubField.Morale, mAlt, mb));
                checks.Add(new("writes.clubMoraleAudit", clubAudit.Passed,
                    clubAudit.Passed ? $"Morale {mb}->{mAlt}->{mb}" : string.Join("; ", clubAudit.Violations)));
            }

            // 3. Nation EconomicFactor scalar round-trip.
            long? econBefore = session.NationFields.Read(england.Address, NationField.EconomicFactor);
            if (econBefore is { } eb)
            {
                long eAlt = eb == 18 ? 17 : 18;
                var natWindow = new AuditWindow("nation", england.Address, 0x200);
                var natAudit = WriteAudit.VerifyRoundTrip(memory, [natWindow],
                    () => session.NationFields.Set(england.Address, NationField.EconomicFactor, eb, eAlt),
                    () => session.NationFields.Set(england.Address, NationField.EconomicFactor, eAlt, eb));
                checks.Add(new("writes.nationScalarAudit", natAudit.Passed,
                    natAudit.Passed ? $"EconomicFactor {eb}->{eAlt}->{eb}" : string.Join("; ", natAudit.Violations)));
            }

            // 4. Stadium Capacity scalar round-trip.
            var stadiums = session.Stadiums.LoadAll();
            var stadium = stadiums.FirstOrDefault(s => s.Id == 680) ?? stadiums.FirstOrDefault(s => !string.IsNullOrEmpty(s.DisplayName));
            if (stadium is not null && session.StadiumWriter.ReadFields(stadium.Address).TryGetValue(StadiumField.Capacity, out long capBefore))
            {
                var stAudit = WriteAudit.VerifyRoundTrip(memory, [new AuditWindow("stadium", stadium.Address, 0xC0)],
                    () => session.StadiumWriter.SetField(stadium.Address, StadiumField.Capacity, capBefore, capBefore + 1),
                    () => session.StadiumWriter.SetField(stadium.Address, StadiumField.Capacity, capBefore + 1, capBefore));
                checks.Add(new("writes.stadiumCapacityAudit", stAudit.Passed,
                    stAudit.Passed ? $"Capacity {capBefore}->{capBefore + 1}->{capBefore}" : string.Join("; ", stAudit.Violations)));
            }

            // 5. Allocating round-trip: Language Add + Remove (triggers AllocateGameBlock -> HeapGate!).
            // Audit surrounding player bytes outside the 24-byte vector header [Vector..Vector+24) so a genuine
            // vector reallocation does not false-fail while any collateral corruption is immediately caught.
            if (profile.Languages is { } langOff)
            {
                var currentLangs = session.ReadLanguages(player);
                var unused = session.LanguageCatalogue(players).First(l => currentLangs.All(c => c.LanguageId != l.Id));
                var owner = PersonOwner.Of(player, profile);
                ulong ownerBase = owner.Address;
                int vecOff = checked((int)(owner.At(langOff.Vector) - ownerBase));
                var windows = new List<AuditWindow>();
                if (vecOff > 0)
                    windows.Add(new("playerBeforeLangVec", ownerBase, vecOff));
                int afterStart = vecOff + 24;
                if (afterStart < playerSize)
                    windows.Add(new("playerAfterLangVec", ownerBase + (ulong)afterStart, playerSize - afterStart));

                var langAudit = WriteAudit.VerifyRoundTrip(memory, windows,
                    () => session.AddLanguage(player, session.ReadLanguages(player), unused, 9),
                    () =>
                    {
                        var added = session.ReadLanguages(player).First(l => l.LanguageId == unused.Id);
                        session.RemoveLanguage(player, session.ReadLanguages(player), added);
                    });
                bool ok = langAudit.Passed && session.ReadLanguages(player).All(l => l.LanguageId != unused.Id);
                checks.Add(new("writes.languageAddRemove", ok,
                    ok ? $"added/removed language '{unused.Name}'" : string.Join("; ", langAudit.Violations)));
            }

            // 6. Allocating round-trip: WorkPermit Add + Remove.
            if (profile.Bans is not null && profile.Relations is { } relOff)
            {
                var owner = PersonOwner.Of(player, profile);
                ulong ownerBase = owner.Address;
                int ptrOff = checked((int)(owner.At(relOff.Pointer) - ownerBase));
                var windows = new List<AuditWindow>();
                if (ptrOff > 0)
                    windows.Add(new("playerBeforeRelPtr", ownerBase, Math.Min(ptrOff, 0x100)));
                var permAudit = WriteAudit.VerifyRoundTrip(memory, windows,
                    () => session.AddWorkPermit(player, england.Address, 2032),
                    () =>
                    {
                        var grant = session.ReadWorkPermits(player).First(g => g.Nation == england.Address && g.Year == 2032);
                        session.RemoveWorkPermit(player, grant);
                    });
                bool ok = permAudit.Passed && session.ReadWorkPermits(player).All(g => !(g.Nation == england.Address && g.Year == 2032));
                checks.Add(new("writes.workPermitAddRemove", ok,
                    ok ? "added/removed work permit cleanly" : string.Join("; ", permAudit.Violations)));
            }
        }
        catch (Exception ex)
        {
            checks.Add(new("writes.execution", false, $"{ex.GetType().Name}: {ex.Message}"));
        }

        var hg = heapGate.Evaluate();
        bool hgOk = hg.Passed && hg.Allocations.Count > 0;
        checks.Add(new("writes.heapGate", hgOk,
            $"mode={hg.Mode}, allocs={hg.Allocations.Count}, rawVm={hg.RawVmCount}, failedChecks={hg.FailedVerificationCount}"));
        return hg;
    }

    static HeapGateResult PrepareCrashGate(
        IProcessMemory memory,
        GameProcess game,
        OffsetProfile profile,
        string statePath,
        List<GateCheckResult> checks,
        out bool pendingCrashGate)
    {
        pendingCrashGate = false;
        using var heapGate = new HeapGate(memory);
        using var session = new GameSession(memory, game, profile, ownMemory: false);

        if (profile.GameUiRva is null || profile.GameUiLoadsOffset is null)
        {
            checks.Add(new("crashGate.prepare", false, "profile lacks gameUiRva/gameUiLoadsOffset required for Crash Gate"));
            return heapGate.Evaluate();
        }
        ulong? loadsBefore = ReadGameUiLoads(memory, game.ModuleBase, profile);
        DateOnly? dateBefore = session.ReadGameDate();
        if (loadsBefore is null || dateBefore is null)
        {
            checks.Add(new("crashGate.prepare", false, "GAME_UI loads count or gameDate unreadable"));
            return heapGate.Evaluate();
        }

        try
        {
            var players = session.Players.LoadAll();
            var player = players.FirstOrDefault(p => p.Id == 2000123586) ?? players[0];
            var cat = session.LanguageCatalogue(players);
            // Force both new record allocation(s) and vector buffer growth so Load Game tears down live calloc blocks.
            int added = 0;
            for (int k = 0; k < 6; k++)
            {
                var current = session.ReadLanguages(player);
                var pick = cat.FirstOrDefault(l => current.All(c => c.LanguageId != l.Id));
                if (pick is null) break;
                session.AddLanguage(player, current, pick, 5);
                added++;
            }
            var nations = session.Nations.LoadAll();
            if (profile.Bans is not null && nations.Count > 0)
            {
                session.AddWorkPermit(player, nations[0].Address, 2033);
                added++;
            }

            var hg = heapGate.Evaluate();
            bool allocOk = hg.Passed && hg.Allocations.Count > 0 && added > 0;
            if (!allocOk)
            {
                checks.Add(new("crashGate.prepare", false,
                    $"allocOk={allocOk}, added={added}, rawVm={hg.RawVmCount}, failedChecks={hg.FailedVerificationCount}"));
                return hg;
            }

            var state = new CrashGateState
            {
                Pid = game.ProcessId,
                Build = profile.Build,
                ModuleBase = game.ModuleBase,
                GameUiLoadsBefore = loadsBefore.Value,
                GameDateBefore = dateBefore.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                HeapGateMode = hg.Mode,
                Allocations = hg.Allocations,
            };
            File.WriteAllText(Path.GetFullPath(statePath), state.ToJson());
            pendingCrashGate = true;
            checks.Add(new("crashGate.prepare", true,
                $"staged {hg.Allocations.Count} live {hg.Mode} block(s) at loads={loadsBefore.Value}; awaiting Continue + Load Game"));
            return hg;
        }
        catch (Exception ex)
        {
            checks.Add(new("crashGate.prepare", false, $"{ex.GetType().Name}: {ex.Message}"));
            return heapGate.Evaluate();
        }
    }

    static void VerifyCrashGate(IProcessMemory memory, GameProcess game, OffsetProfile profile, string statePath, List<GateCheckResult> checks)
    {
        if (!File.Exists(statePath))
        {
            checks.Add(new("crashGate.verify", false, $"Crash gate state file not found: {statePath}"));
            return;
        }
        try
        {
            var state = CrashGateState.Load(statePath);
            if (game.ProcessId != state.Pid)
            {
                checks.Add(new("crashGate.verify", false, $"PID changed ({state.Pid} -> {game.ProcessId}): process restarted or crashed!"));
                return;
            }
            ulong? loadsNow = ReadGameUiLoads(memory, game.ModuleBase, profile);
            if (loadsNow is null || loadsNow <= state.GameUiLoadsBefore)
            {
                checks.Add(new("crashGate.verify", false,
                    $"GAME_UI loads did not increment (before={state.GameUiLoadsBefore}, now={loadsNow?.ToString() ?? "null"}) — run Continue + Load Game first"));
                return;
            }
            bool prevAllocsOk = state.Allocations.Count > 0 && state.Allocations.All(a => a.Origin != AllocationOrigin.RawVm && a.Verification.Passed);
            checks.Add(new("crashGate.verify", prevAllocsOk,
                $"PID {game.ProcessId} survived Continue+LoadGame (loads {state.GameUiLoadsBefore} -> {loadsNow}, mode={state.HeapGateMode}, stagedAllocs={state.Allocations.Count})"));
        }
        catch (Exception ex)
        {
            checks.Add(new("crashGate.verify", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    static void RunNegativeSelfTests(
        IProcessMemory memory,
        GameProcess game,
        string rawJson,
        Func<string, string?>? resolveLayout,
        List<GateCheckResult> checks)
    {
        var baseNode = OffsetProfile.ResolveJson(rawJson, resolveLayout);
        var mutations = new (string Name, Action<JsonObject> Mutate)[]
        {
            ("stalePlayerVtable", node =>
            {
                var arr = node["playerVtables"]!.AsArray();
                uint v = ParseHexUInt(arr[0]!.GetValue<string>());
                arr[0] = $"0x{v + 8:X}";
            }),
            ("missingHumanManagerKind", node =>
            {
                var kinds = node["staff"]!["kinds"]!.AsArray();
                if (kinds.Count > 2) kinds.RemoveAt(2);
                else kinds.RemoveAt(0);
            }),
            ("shiftedCorporateFacilities", node =>
            {
                var cf = node["club"]!["fields"]!["CorporateFacilities"]!;
                var parts = cf["path"]!.GetValue<string>().Split("->");
                int off = Convert.ToInt32(parts[^1][2..], 16) + 1;
                parts[^1] = $"0x{off:X}";
                cf["path"] = string.Join("->", parts);
            }),
            ("corruptPromisesGlobalRoot", node =>
            {
                var pr = node["player"]!["promises"]!;
                uint rva = ParseHexUInt(pr["globalRootRva"]!.GetValue<string>());
                pr["globalRootRva"] = $"0x{rva + 8:X}";
            }),
            ("shiftedGameDateOffset", node =>
            {
                var gd = node["gameDate"]!["primary"]!;
                uint off = ParseHexUInt(gd["dateOffset"]!.GetValue<string>());
                gd["dateOffset"] = $"0x{off + 2:X}";
            }),
            ("shiftedSubtypeInsertion", node =>
            {
                var arr = node["player"]!["subtypeInsertions"]?.AsArray();
                if (arr is { Count: > 0 })
                {
                    uint len = ParseHexUInt(arr[0]!["length"]!.GetValue<string>());
                    arr[0]!["length"] = $"0x{len + 8:X}";
                }
                else
                {
                    var obj = node["player"]!;
                    uint dob = ParseHexUInt(obj["dateOfBirth"]!.GetValue<string>());
                    obj["dateOfBirth"] = $"0x{dob + 2:X}";
                }
            }),
        };

        var unexpectedPasses = new List<string>();
        foreach (var (name, mutate) in mutations)
        {
            var mutatedNode = (JsonObject)baseNode.DeepClone();
            mutate(mutatedNode);
            string mutatedJson = mutatedNode.ToJsonString();
            bool passed;
            try
            {
                var mutatedProfile = OffsetProfile.Load(mutatedJson, resolveLayout);
                var sub = Verify(memory, game, mutatedProfile, new VerifyOptions(ProfileRawJson: mutatedJson), resolveLayout);
                passed = sub.OverallPassed;
            }
            catch
            {
                passed = false;
            }
            if (passed) unexpectedPasses.Add(name);
        }

        checks.Add(new("negativeSelfTest", unexpectedPasses.Count == 0,
            unexpectedPasses.Count == 0
                ? $"all {mutations.Length} injected mutations rejected with FAIL"
                : $"mutations unexpectedly passed: {string.Join(", ", unexpectedPasses)}"));
    }

    static ulong? ReadGameUiLoads(IProcessMemory memory, ulong moduleBase, OffsetProfile profile)
    {
        if (profile.GameUiRva is not { } rva || profile.GameUiLoadsOffset is not { } off || profile.GameUiVtable is not { } vt)
            return null;
        ulong obj = ReadU64Safe(memory, moduleBase + rva);
        if (obj == 0 || ReadU64Safe(memory, obj) != moduleBase + vt) return null;
        return ReadU64Safe(memory, obj + (ulong)off);
    }

    static string? LoadFixtureJson()
    {
        var asm = typeof(BuildVerifier).Assembly;
        string? resName = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.Equals("offsets/fixtures/fm27-known-entities.json", StringComparison.OrdinalIgnoreCase));
        if (resName is null) return null;
        using var sr = new StreamReader(asm.GetManifestResourceStream(resName)!);
        return sr.ReadToEnd();
    }

    static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        return s is not null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    static uint ParseHexUInt(string s) => uint.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    static ulong ReadU64Safe(IProcessMemory memory, ulong address)
    {
        Span<byte> buf = stackalloc byte[8];
        return address != 0 && memory.TryRead(address, buf) ? BitConverter.ToUInt64(buf) : 0;
    }

    static string FormatReceiptJson(VerificationReceipt r)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("verdict", r.PendingCrashGate ? "PENDING_CRASH_GATE" : r.OverallPassed ? "PASS" : "FAIL");
        w.WriteBoolean("overallPassed", r.OverallPassed);
        w.WriteBoolean("pendingCrashGate", r.PendingCrashGate);
        w.WriteString("build", r.Build);
        w.WriteString("timestampUtc", r.TimestampUtc.ToString("O"));
        if (r.HeapGate is { } hg)
        {
            w.WriteStartObject("heapGate");
            w.WriteBoolean("passed", hg.Passed);
            w.WriteString("mode", hg.Mode);
            w.WriteNumber("allocations", hg.Allocations.Count);
            w.WriteNumber("rawVmCount", hg.RawVmCount);
            w.WriteNumber("failedVerificationCount", hg.FailedVerificationCount);
            w.WriteEndObject();
        }
        w.WriteStartArray("checks");
        foreach (var c in r.Checks)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteBoolean("passed", c.Passed);
            w.WriteString("detail", c.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
