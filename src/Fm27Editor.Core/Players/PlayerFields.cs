using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Players;

/// <summary>
/// Player values beyond attributes, mapped in the profile (<c>player.fields</c>, paths from the player object). Measured
/// by lane C1 against FMRTE 27 (re/squad/player-scalars.md). Names follow docs/squad-plan.md.
/// </summary>
public enum PlayerField
{
    /// <summary>int16, 0–10000 as FMRTE shows it.</summary>
    Condition,
    MatchSharpness,
    Jadedness,
    /// <summary>FM27 keeps no separate value: FMRTE's Match Exercise is the Condition field. Never mapped.</summary>
    MatchExercise,
    /// <summary>byte, 1–20.</summary>
    Morale,
    AskingPrice,
    Caps,
    InternationalGoals,
    /// <summary>Youth (U21/U20) caps; FMRTE labels them "Sub Caps".</summary>
    SubCaps,
    /// <summary>Youth (U21/U20) goals.</summary>
    SubGoals,
    /// <summary>Not stored in the player object (FMRTE derives it from <see cref="ArrivedInCountry"/>). Never mapped.</summary>
    DaysInCountry,
    /// <summary>Days in Country is the game date minus this date; FMRTE writing 2390 → 2500 days moved it 110 days back.</summary>
    ArrivedInCountry,
    // Information tab, diffed against FMRTE 27 on Bruno Fernandes (2/11/2026); codes are FMRTE's dropdown values.
    /// <summary>1 Ectomorph … 5 Endomorph.</summary>
    BodyType,
    /// <summary>FMRTE codes: 80 Eligible, 81 Has Played, 83 Declared … 90; 0 No info.</summary>
    NationalityInfo,
    TreatedAsNonEuInItaly,
    RetiringAfterSpellAtCurrentClub,
    /// <summary>0 Northern European … 10 East Asian.</summary>
    Ethnicity,
    /// <summary>0 Unknown, 1–18 (not in dropdown order: Blonde 1 is 1, Blonde 2 is 7).</summary>
    HairColour,
    /// <summary>0 Fuzz, 1 Short, 2 Medium, 3 Long, 4 Bald, 5 Unknown.</summary>
    HairLength,
    /// <summary>0 Unknown, else Skin Tone n.</summary>
    SkinColour,
    /// <summary>Bit 0x01 of player +0x2A0: set on every player with a planned retirement in the calibration save
    /// (Benzema, Galhardo, Iličič, Zimmermann, Aspas, Boyd) and cleared by FMRTE's keep-playing write on Benzema (15/11/2026).</summary>
    RetirementPlanned,
}

/// <summary>Reads and writes mapped player fields; writes check the player vtable and the caller's snapshot, then read back.</summary>
public sealed class PlayerFieldAccess(IProcessMemory sessionMemory, OffsetProfile profile, ulong moduleBase)
{
    readonly IProcessMemory memory = VerifiedWriteMemory.ForPlayerOrClubEditor(sessionMemory);

    IReadOnlyDictionary<PlayerField, ClubFieldOffset> Map => profile.PlayerFields ?? new Dictionary<PlayerField, ClubFieldOffset>();

    public bool IsMapped(PlayerField field) => Map.ContainsKey(field);

    /// <summary>Check that a value can be encoded before starting a batch of writes.</summary>
    public void ValidateValue(PlayerField field, long value)
    {
        var o = Map.TryGetValue(field, out var x) ? x : throw new InvalidOperationException($"{field} is not mapped in this profile");
        _ = ClubFieldAccess.Encode(value, o, new byte[ClubFieldAccess.Size(o.Type)]);
    }

    public IReadOnlyDictionary<PlayerField, long> ReadAll(PlayerRecord player)
    {
        var values = new Dictionary<PlayerField, long>();
        foreach (var field in Map.Keys)
            if (Read(player, field) is { } v) values[field] = v;
        return values;
    }

    public long? Read(PlayerRecord player, PlayerField field)
    {
        if (!Map.TryGetValue(field, out var o) || !IsPlayer(player) || Resolve(player, o) is not { } at) return null;
        var b = new byte[ClubFieldAccess.Size(o.Type)];
        return memory.TryRead(at, b) ? ClubFieldAccess.Decode(b, o) : null;
    }

    public void Set(PlayerRecord player, PlayerField field, long expected, long value)
    {
        using var _ = Fm27Editor.Core.History.ChangeContext.Describe(Fm27Editor.Core.History.PlayerChangeLabels.Words(field.ToString()));
        var o = Map.TryGetValue(field, out var x) ? x : throw new InvalidOperationException($"{field} is not mapped in this profile");
        if (!IsPlayer(player)) throw new StaleObjectException($"0x{player.Address:X} is no longer a player object");
        ulong at = Resolve(player, o) ?? throw new StaleObjectException($"{field}: pointer path {o.PathText} is broken");
        var current = memory.Read(at, ClubFieldAccess.Size(o.Type));
        if (ClubFieldAccess.Decode(current, o) != expected) throw new StaleObjectException($"{field} changed since it was read");
        var bytes = ClubFieldAccess.Encode(value, o, current);
        if (!memory.TryWrite(at, bytes) || !memory.Read(at, bytes.Length).AsSpan().SequenceEqual(bytes))
            throw new MemoryAccessException($"Write of {field} at 0x{at:X} failed");
    }

    bool IsPlayer(PlayerRecord player) => PersonOwner.Of(player, profile).IsValid(memory, moduleBase);

    ulong? Resolve(PlayerRecord player, ClubFieldOffset o)
    {
        ulong at = player.Address + (ulong)profile.PlayerOffset(player.VtableRva, o.Path[0]);
        Span<byte> b = stackalloc byte[8];
        for (int i = 1; i < o.Path.Count; i++)
        {
            if (!memory.TryRead(at, b)) return null;
            ulong next = BitConverter.ToUInt64(b);
            if (next == 0) return null;
            at = next + (ulong)o.Path[i];
        }
        return at;
    }
}
