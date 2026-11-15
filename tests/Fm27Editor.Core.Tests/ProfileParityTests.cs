using System.Globalization;
using System.Text.Json;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// The editor must work on both Game Pass and Steam, so the two profiles for build 27.0.0+2493426 describe the same set of
/// things. A feature measured on one build and missing from the other is a feature that silently does nothing there.
/// </summary>
public class ProfileParityTests
{
    static Dictionary<string, string> Flatten(string file)
    {
        var result = new Dictionary<string, string>();
        void Walk(JsonElement e, string path)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, path + "/" + p.Name); break;
                case JsonValueKind.Array: int i = 0; foreach (var x in e.EnumerateArray()) Walk(x, path + "/" + i++); break;
                default: result[path] = e.ToString(); break;
            }
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "offsets", file)),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        Walk(doc.RootElement, "");
        return result;
    }

    static readonly Dictionary<string, string> GamePass = Flatten("27.0.0+2493426-gamepass.json");
    static readonly Dictionary<string, string> Steam = Flatten("27.0.0+2493426-steam.json");

    // Entries that exist only on Game Pass on purpose.
    static readonly string[] GamePassOnly =
    [
        "/matchBuild", "/variant",                              // tell the two stores apart; Steam is the default
        "/directGameDateRva",                                   // a shortcut to the game date; Steam reads it through the date chain
        "/staff/directStaffVector",                             // Game Pass keeps staff objects in one vector; Steam keeps person parts
        "/club/lists/visionHolderVtables/2", "/club/lists/cultureVtables/2",   // a third class seen on Game Pass only; Steam has none
    ];

    [Fact]
    public void SteamHasEveryEntryGamePassHas()
    {
        var missing = GamePass.Keys.Where(k => !Steam.ContainsKey(k) && !GamePassOnly.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Missing from the Steam profile: " + string.Join(", ", missing));
    }

    [Fact]
    public void NoAddressWasCopiedFromGamePassToSteam()
    {
        // The two binaries differ, so any address or vtable (a hex value of 0x100000 or more) must differ between the profiles.
        var same = GamePass.Where(kv => Steam.TryGetValue(kv.Key, out var v) && v == kv.Value && kv.Value.StartsWith("0x")
            && ulong.TryParse(kv.Value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) && number >= 0x100000).Select(kv => kv.Key).ToList();
        Assert.True(same.Count == 0, "Same address in both profiles: " + string.Join(", ", same));
    }

    static IEnumerable<(string Name, JsonElement Root)> AllProfiles()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "offsets");
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            yield return (Path.GetFileName(path), doc.RootElement.Clone());
        }
    }

    static int ParseHex(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : throw new FormatException($"Expected 0x hex offset, got '{value}'.");

    [Fact]
    public void PlayerStructOffsetsDoNotOverlapUnexpectedlyAcrossProfiles()
    {
        var fieldSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["byte"] = 1,
            ["int16"] = 2,
            ["int32"] = 4,
            ["float"] = 4,
            ["date"] = 4,
            ["int64"] = 8,
        };

        var failures = new List<string>();
        foreach (var (file, root) in AllProfiles())
        {
            var player = root.GetProperty("player");
            int objectSize = ParseHex(player.GetProperty("objectSize").GetString()!);
            var spans = new List<(string Name, int Start, int End, bool Masked)>();

            void AddScalar(string name, int size)
            {
                if (player.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    int off = ParseHex(prop.GetString()!);
                    spans.Add(($"player.{name}", off, off + size, false));
                }
            }

            foreach (var key in new[] { "currentAbility", "potentialAbility", "homeReputation", "currentReputation", "worldReputation", "height" })
                AddScalar(key, 2);
            foreach (var key in new[] { "dateOfBirth", "id", "personIndex" })
                AddScalar(key, 4);
            foreach (var key in new[] { "fullName", "firstName", "lastName", "commonName" })
                AddScalar(key, 8);

            if (player.TryGetProperty("personality", out var personality))
                foreach (var p in personality.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        int off = ParseHex(p.Value.GetString()!);
                        spans.Add(($"player.personality.{p.Name}", off, off + 1, false));
                    }

            if (player.TryGetProperty("attributes", out var attributes))
                foreach (var a in attributes.EnumerateObject())
                    if (a.Value.ValueKind == JsonValueKind.String)
                    {
                        int off = ParseHex(a.Value.GetString()!);
                        spans.Add(($"player.attributes.{a.Name}", off, off + 1, false));
                    }

            if (player.TryGetProperty("positions", out var positions))
                foreach (var p in positions.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        int off = ParseHex(p.Value.GetString()!);
                        spans.Add(($"player.positions.{p.Name}", off, off + 1, false));
                    }

            var fields = player.GetProperty("fields");
            foreach (var f in fields.EnumerateObject())
            {
                var path = f.Value.GetProperty("path").GetString()!;
                if (path.Contains("->", StringComparison.Ordinal)) continue;
                var type = f.Value.GetProperty("type").GetString()!;
                int size = fieldSizes[type];
                int off = ParseHex(path);
                bool masked = f.Value.TryGetProperty("mask", out _);
                spans.Add(($"player.fields.{f.Name}", off, off + size, masked));
                if (off < 0 || off + size > objectSize)
                    failures.Add($"{file}: player.fields.{f.Name} [0x{off:X}..0x{off + size:X}) falls outside player.objectSize 0x{objectSize:X}");
            }

            if (fields.TryGetProperty("TreatedAsNonEuInItaly", out var nonEu)
                && player.TryGetProperty("fullName", out var fullNameProp) && fullNameProp.ValueKind == JsonValueKind.String)
            {
                int nonEuOff = ParseHex(nonEu.GetProperty("path").GetString()!);
                int personDataVtableStart = ParseHex(fullNameProp.GetString()!) - 0x10;
                if (nonEuOff >= personDataVtableStart && nonEuOff < personDataVtableStart + 8)
                    failures.Add($"{file}: TreatedAsNonEuInItaly (0x{nonEuOff:X}) overlaps PERSON_DATA vtable [0x{personDataVtableStart:X}..0x{personDataVtableStart + 8:X})");
            }

            for (int i = 0; i < spans.Count; i++)
            {
                for (int j = i + 1; j < spans.Count; j++)
                {
                    var a = spans[i];
                    var b = spans[j];
                    if (Math.Max(a.Start, b.Start) >= Math.Min(a.End, b.End)) continue;
                    if (a.Masked && b.Masked && a.Start == b.Start && a.End == b.End) continue;
                    failures.Add($"{file}: {a.Name} [0x{a.Start:X}..0x{a.End:X}) overlaps {b.Name} [0x{b.Start:X}..0x{b.End:X})");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void MacProfilesRespectStaffJobAndNationChainInvariants()
    {
        var failures = new List<string>();
        foreach (var (file, root) in AllProfiles())
        {
            var build = root.GetProperty("build").GetString() ?? "";
            if (!build.Contains("-mac", StringComparison.OrdinalIgnoreCase)) continue;

            if (root.TryGetProperty("staff", out var staff)
                && staff.TryGetProperty("directStaffVector", out var direct) && direct.ValueKind == JsonValueKind.True)
            {
                var job = staff.GetProperty("job").GetString();
                var tendencies = staff.GetProperty("tendenciesHolder").GetString();
                if (string.Equals(job, tendencies, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{file}: staff.job ({job}) collides with staff.tendenciesHolder ({tendencies})");
                if (!staff.TryGetProperty("jobPersonAnchor", out var anchor) || ParseHex(anchor.GetString()!) != 0x208)
                    failures.Add($"{file}: directStaffVector Mac build must set staff.jobPersonAnchor to 0x208");
            }

            if (root.TryGetProperty("nation", out var nation) && nation.TryGetProperty("fields", out var nationFields))
            {
                foreach (var nf in nationFields.EnumerateObject())
                {
                    var path = nf.Value.GetProperty("path").GetString() ?? "";
                    if (path.StartsWith("0x108->", StringComparison.OrdinalIgnoreCase))
                        failures.Add($"{file}: nation.fields.{nf.Name} uses Windows men's team pointer 0x108-> ({path}) instead of Mac 0x110->");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
