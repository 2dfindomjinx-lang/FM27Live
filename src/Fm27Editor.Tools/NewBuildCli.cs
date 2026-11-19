using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

internal static class NewBuildCli
{
    /// <summary>
    /// <c>verifiedWrites</c> is a safety policy decided by people, not something discovery measures, so a regenerated
    /// profile keeps the list from the existing profile of the same build. <c>readOnly</c> is always written true.
    /// </summary>
    static string CarryPolicy(string json, string offsetsDir, string build)
    {
        string existing = Path.Combine(offsetsDir, build + ".json");
        if (!File.Exists(existing)) return json;
        var source = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(existing)) as System.Text.Json.Nodes.JsonObject;
        if (source?["verifiedWrites"] is not System.Text.Json.Nodes.JsonArray writes) return json;
        var target = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;
        target["verifiedWrites"] = writes.DeepClone();
        Console.WriteLine($"Kept verifiedWrites [{string.Join(", ", writes)}] from {existing}");
        return target.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static int Run(string[] args, string offsetsDir)
    {
        string? layout = null;
        string? outPath = null;
        bool full = false;
        bool ignoreProfiles = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--layout" when i + 1 < args.Length:
                    layout = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--full":
                    full = true;
                    break;
                case "--ignore-profiles":
                    ignoreProfiles = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine("usage: newbuild [--layout macos|touch-macos|windows] [--out <path>] [--full] [--ignore-profiles]");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'");
                    return 2;
            }
        }

        var moduleName = layout is "windows" ? GameProcess.DefaultModule : (OperatingSystem.IsMacOS() ? "game_plugin" : GameProcess.DefaultModule);
        var games = GameProcess.LocateAll(moduleName);
        var game = games[0];
        if (!ignoreProfiles)
            Console.WriteLine($"Attached raw to PID {game.ProcessId} ({game.ExePath ?? moduleName}), base 0x{game.ModuleBase:X}, size 0x{game.ModuleSize:X}");
        else
            Console.WriteLine($"Attached raw (--ignore-profiles) to PID {game.ProcessId}, base 0x{game.ModuleBase:X}, size 0x{game.ModuleSize:X}");

        IProcessMemory memory = OperatingSystem.IsMacOS()
            ? MacMemory.Open(game.ProcessId, writable: false)
            : new WindowsProcessMemory(game.ProcessId, writable: false);
        try
        {
            var layoutResolver = Directory.Exists(offsetsDir) ? ProfileJsonResolver.DirectoryLayoutResolver(offsetsDir) : null;
            var result = NewBuildDiscoverer.Discover(memory, game, layout, layoutResolver, Console.WriteLine);

            Console.WriteLine();
            Console.WriteLine($"Layout: {result.LayoutName} | Build: {result.Build}");
            Console.WriteLine($"{"STATUS",-7} {"KEY",-46} {"VALUE",-24} DETAIL");
            foreach (var item in result.Items)
            {
                string tag = item.Status switch
                {
                    DiscoveryStatus.Pass => "PASS",
                    DiscoveryStatus.Warn => "WARN",
                    _ => "MANUAL",
                };
                Console.WriteLine($"{tag,-7} {item.Key,-46} {item.Value,-24} {item.Detail}");
                if (!string.IsNullOrEmpty(item.ManualHint))
                    Console.WriteLine($"        hint: {item.ManualHint}");
            }

            string outputJson = CarryPolicy(full ? result.FullJson : result.CompactJson, offsetsDir, result.Build);
            if (!string.IsNullOrEmpty(outPath))
            {
                string fullOut = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
                File.WriteAllText(fullOut, outputJson);
                Console.WriteLine($"Wrote {(full ? "full" : "compact")} profile to {fullOut}");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("--- DRAFT PROFILE ---");
                Console.WriteLine(outputJson);
            }

            int manuals = result.Items.Count(i => i.Status == DiscoveryStatus.Manual);
            int warns = result.Items.Count(i => i.Status == DiscoveryStatus.Warn);
            Console.WriteLine($"Summary: {result.Items.Count - manuals - warns} PASS, {warns} WARN, {manuals} MANUAL");
            return result.HasManualItems ? 1 : 0;
        }
        finally
        {
            (memory as IDisposable)?.Dispose();
        }
    }
}
