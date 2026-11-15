using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Players;

[Flags]
public enum RetirementReason
{
    None = 0,
    PlannedRetirement = 1 << 0,
    RetiringAfterSpell = 1 << 1,
    WillRetireAtTop = 1 << 2,
}

public sealed record RetiringPlayer(PlayerRecord Player, DateOnly? RetirementDate, RetirementReason Reasons);

/// <summary>
/// Collects players flagged as planning or about to retire from already-measured game structures:
/// planned retirement entries, retiring after spell at current club, and the "Will Retire At Top" trait.
/// </summary>
public sealed class RetiringPlayerReader(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    const ulong WillRetireAtTopBit = 1UL << 48; // preferredMoves + 6, bit 0 (also cleared by KeepPlayingWriter)

    public IReadOnlyList<RetiringPlayer> Read(IEnumerable<PlayerRecord> players, PlayerFieldAccess? fields = null)
    {
        var planned = profile.RetirementManagerRva is not null
            ? new PlannedRetirements(memory, profile, moduleBase).ReadAll()
            : new Dictionary<uint, PlannedRetirement>();
        var fieldAccess = fields ?? new PlayerFieldAccess(memory, profile, moduleBase);

        var result = new List<RetiringPlayer>();
        foreach (var p in players)
        {
            if (string.IsNullOrWhiteSpace(p.DisplayName)) continue;

            var reasons = RetirementReason.None;
            DateOnly? date = null;

            if (p.PersonIndex is > 0 && planned.TryGetValue((uint)p.PersonIndex.Value, out var entry))
            {
                reasons |= RetirementReason.PlannedRetirement;
                date = entry.Date;
            }
            if (profile.PlayerFields?.ContainsKey(PlayerField.RetiringAfterSpellAtCurrentClub) == true
                && fieldAccess.Read(p, PlayerField.RetiringAfterSpellAtCurrentClub) == 1)
            {
                reasons |= RetirementReason.RetiringAfterSpell;
            }
            if (p.PreferredMoveBits is { } bits && (bits & WillRetireAtTopBit) != 0)
            {
                reasons |= RetirementReason.WillRetireAtTop;
            }

            if (reasons != RetirementReason.None)
                result.Add(new RetiringPlayer(p, date, reasons));
        }

        return result
            .OrderBy(r => r.RetirementDate ?? DateOnly.MaxValue)
            .ThenBy(r => r.Player.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
