using System.Text.Json;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Verification;

internal static class VerifyBuildCli
{
    public static int Run(string[] args, string offsetsDir)
    {
        string? profilePath = null;
        string? groundTruthPath = null;
        string? receiptOut = null;
        string? crashGateWritePath = null;
        string? crashGateVerifyPath = null;
        int? expectPlayers = null;
        int? expectStaff = null;
        int? expectClubs = null;
        int? expectHumans = null;
        double tolerance = 0.02;
        bool writes = false;
        bool negativeTest = false;
        bool releaseGate = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length:
                    profilePath = args[++i];
                    break;
                case "--ground-truth" when i + 1 < args.Length:
                    groundTruthPath = args[++i];
                    break;
                case "--receipt" when i + 1 < args.Length:
                    receiptOut = args[++i];
                    break;
                case "--expect-players" when i + 1 < args.Length:
                    expectPlayers = int.Parse(args[++i]);
                    break;
                case "--expect-staff" when i + 1 < args.Length:
                    expectStaff = int.Parse(args[++i]);
                    break;
                case "--expect-clubs" when i + 1 < args.Length:
                    expectClubs = int.Parse(args[++i]);
                    break;
                case "--expect-humans" when i + 1 < args.Length:
                    expectHumans = int.Parse(args[++i]);
                    break;
                case "--tolerance" when i + 1 < args.Length:
                    tolerance = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--writes":
                    writes = true;
                    break;
                case "--negative-test":
                    negativeTest = true;
                    break;
                case "--release-gate":
                    releaseGate = true;
                    break;
                case "--crash-gate" when i + 1 < args.Length:
                    crashGateWritePath = args[++i];
                    break;
                case "--crash-gate-verify" when i + 1 < args.Length:
                    crashGateVerifyPath = args[++i];
                    break;
                case "--help" or "-h":
                    Console.WriteLine("usage: verify-build [--profile <path>] [--expect-players N] [--expect-staff N] [--expect-clubs N] [--expect-humans N] [--ground-truth <json>] [--writes] [--negative-test] [--crash-gate <state.json> | --crash-gate-verify <state.json>] [--release-gate] [--receipt <out.json>]");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'");
                    return 2;
            }
        }

        if (!string.IsNullOrEmpty(groundTruthPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(groundTruthPath));
            var r = doc.RootElement;
            if (expectPlayers is null && r.TryGetProperty("players", out var ep)) expectPlayers = ep.GetInt32();
            if (expectStaff is null && r.TryGetProperty("staff", out var es)) expectStaff = es.GetInt32();
            if (expectClubs is null && r.TryGetProperty("clubs", out var ec)) expectClubs = ec.GetInt32();
            if (expectHumans is null && r.TryGetProperty("humans", out var eh)) expectHumans = eh.GetInt32();
        }

        var layoutResolver = Directory.Exists(offsetsDir) ? ProfileJsonResolver.DirectoryLayoutResolver(offsetsDir) : null;
        string? rawProfileJson = null;
        OffsetProfile? targetProfile = null;
        if (!string.IsNullOrEmpty(profilePath))
        {
            rawProfileJson = File.ReadAllText(profilePath);
            targetProfile = OffsetProfile.Load(rawProfileJson, layoutResolver ?? ProfileJsonResolver.DirectoryLayoutResolver(Path.GetDirectoryName(Path.GetFullPath(profilePath))!));
        }

        var moduleName = targetProfile?.Module ?? (OperatingSystem.IsMacOS() ? "game_plugin" : GameProcess.DefaultModule);
        var games = GameProcess.LocateAll(moduleName);
        var game = games[0];
        bool writable = writes || !string.IsNullOrEmpty(crashGateWritePath);

        if (targetProfile is null)
        {
            var profiles = GameSession.LoadProfiles(offsetsDir);
            IProcessMemory roMem = OperatingSystem.IsMacOS()
                ? MacMemory.Open(game.ProcessId, writable: false)
                : new WindowsProcessMemory(game.ProcessId, writable: false);
            try
            {
                bool store = game.ExePath?.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) == true;
                targetProfile = profiles
                    .OrderBy(p => (p.Variant?.Contains("gamepass", StringComparison.OrdinalIgnoreCase) == true) == store ? 0 : 1)
                    .FirstOrDefault(p => GameSession.ProfileMatches(roMem, game.ModuleBase, game.ModuleSize, p))
                    ?? throw new UnsupportedBuildException("No matching profile found; pass --profile <path>");
            }
            finally
            {
                (roMem as IDisposable)?.Dispose();
            }
        }

        if (writable && targetProfile.ReadOnly)
        {
            Console.Error.WriteLine($"FAIL: profile '{targetProfile.Build}' has readOnly=true; refusing write gates until verified/unlocked in a test copy.");
            return 2;
        }

        IProcessMemory memory = OperatingSystem.IsMacOS()
            ? MacMemory.Open(game.ProcessId, writable)
            : new WindowsProcessMemory(game.ProcessId, writable);
        try
        {
            GroundTruthCounts? gt = (expectPlayers is not null || expectStaff is not null || expectClubs is not null || expectHumans is not null)
                ? new GroundTruthCounts(expectPlayers, expectStaff, expectClubs, expectHumans, tolerance)
                : null;

            var opts = new VerifyOptions(
                GroundTruth: gt,
                RunWrites: writes,
                RunNegativeSelfTest: negativeTest,
                ReleaseGate: releaseGate,
                CrashGateWritePath: crashGateWritePath,
                CrashGateVerifyPath: crashGateVerifyPath,
                ProfileRawJson: rawProfileJson);

            var receipt = BuildVerifier.Verify(memory, game, targetProfile, opts, layoutResolver);
            foreach (var c in receipt.Checks)
                Console.WriteLine($"{(c.Passed ? "PASS" : "FAIL"),-5} {c.Name,-34} {c.Detail}");

            if (!string.IsNullOrEmpty(receiptOut) && receipt.ReceiptJson is not null)
            {
                string fullOut = Path.GetFullPath(receiptOut);
                Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
                File.WriteAllText(fullOut, receipt.ReceiptJson);
                Console.WriteLine($"Receipt: {fullOut}");
            }

            if (receipt.PendingCrashGate)
            {
                Console.WriteLine($"VERDICT: PENDING_CRASH_GATE (Continue + Load Game in FM27, then run verify-build --crash-gate-verify \"{crashGateWritePath}\")");
                return 3;
            }
            Console.WriteLine($"VERDICT: {(receipt.OverallPassed ? "PASS" : "FAIL")}");
            return receipt.OverallPassed ? 0 : 1;
        }
        finally
        {
            (memory as IDisposable)?.Dispose();
        }
    }
}
