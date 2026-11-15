using System.Text.Json;

namespace Fm27Editor.Core.Offsets;

/// <summary>
/// The game-wide officials (referee) array (profile <c>officials</c>). The list can be a direct begin/end pair at
/// [module + ListRootRva] (Mac 27.0.1) or found through the clubs anchor when ListRootRva is absent (the x86-64 builds).
/// Object offsets are the first name, last name and so on; SnapshotSize is the guarded read/write window.
/// </summary>
public sealed class OfficialsOffsets
{
    public required uint VtableRva { get; init; }
    public uint? ListRootRva { get; init; }
    public required int FirstName { get; init; }
    public required int LastName { get; init; }
    public required int CommonName { get; init; }
    public int? Nation { get; init; }
    public int? City { get; init; }
    public required int BirthDay { get; init; }
    public required int BirthYear { get; init; }
    public required int CurrentAbility { get; init; }
    public required int PotentialAbility { get; init; }
    public required int Reputation { get; init; }
    public required int AllowingFlow { get; init; }
    public required int Discipline { get; init; }
    public required int ImportantMatches { get; init; }
    public required int Pressure { get; init; }
    public required int Refereeing { get; init; }
    public required int RunningLine { get; init; }
    public required int TimeKeeping { get; init; }
    public required int EliteList { get; init; }
    public required int Continental { get; init; }
    public required int EuropeanCategory { get; init; }
    public int? UniqueId { get; init; }
    public int SnapshotSize { get; init; } = 0x148;

    static int Hex(JsonElement e, string key) => e.GetProperty(key).GetString() is { } s && s.StartsWith("0x")
        ? Convert.ToInt32(s[2..], 16) : throw new FormatException($"officials.{key}: expected hex");

    static int? Opt(JsonElement e, string key) => e.TryGetProperty(key, out _) ? Hex(e, key) : null;

    internal static OfficialsOffsets Load(JsonElement e) => new()
    {
        VtableRva = (uint)Hex(e, "vtable"),
        ListRootRva = e.TryGetProperty("listRootRva", out _) ? (uint)Hex(e, "listRootRva") : null,
        FirstName = Hex(e, "firstName"), LastName = Hex(e, "lastName"), CommonName = Hex(e, "commonName"),
        Nation = Opt(e, "nation"), City = Opt(e, "city"),
        BirthDay = Hex(e, "birthDay"), BirthYear = Hex(e, "birthYear"),
        CurrentAbility = Hex(e, "currentAbility"), PotentialAbility = Hex(e, "potentialAbility"), Reputation = Hex(e, "reputation"),
        AllowingFlow = Hex(e, "allowingFlow"), Discipline = Hex(e, "discipline"), ImportantMatches = Hex(e, "importantMatches"),
        Pressure = Hex(e, "pressure"), Refereeing = Hex(e, "refereeing"), RunningLine = Hex(e, "runningLine"),
        TimeKeeping = Hex(e, "timeKeeping"), EliteList = Hex(e, "eliteList"), Continental = Hex(e, "continental"),
        EuropeanCategory = Hex(e, "europeanCategory"), UniqueId = Opt(e, "uniqueId"),
        SnapshotSize = e.TryGetProperty("snapshotSize", out _) ? Hex(e, "snapshotSize") : 0x148,
    };

    internal void Write(Utf8JsonWriter w)
    {
        void H(string k, long v) => w.WriteString(k, $"0x{v:X}");
        w.WriteStartObject("officials");
        if (ListRootRva is { } lr) H("listRootRva", lr);
        H("vtable", VtableRva);
        H("firstName", FirstName); H("lastName", LastName); H("commonName", CommonName);
        if (Nation is { } n) H("nation", n); if (City is { } c) H("city", c);
        H("birthDay", BirthDay); H("birthYear", BirthYear);
        H("currentAbility", CurrentAbility); H("potentialAbility", PotentialAbility); H("reputation", Reputation);
        H("allowingFlow", AllowingFlow); H("discipline", Discipline); H("importantMatches", ImportantMatches);
        H("pressure", Pressure); H("refereeing", Refereeing); H("runningLine", RunningLine); H("timeKeeping", TimeKeeping);
        H("eliteList", EliteList); H("continental", Continental); H("europeanCategory", EuropeanCategory);
        if (UniqueId is { } u) H("uniqueId", u);
        H("snapshotSize", SnapshotSize);
        w.WriteEndObject();
    }
}
