using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Players;

/// <summary>
/// The object a person-level list (languages, relations, career plans, contract) hangs off. A player keeps its person part
/// at player + 0x288 (<see cref="RelationOffsets.PlayerPerson"/>); a staff member's entry points at its person part directly.
/// The lists sit at the same person offsets for both (Mikel Arteta's six languages at person + 0xF0 = player + 0x378,
/// relations holder at person + 0x78 = player + 0x300; 2/11/2026), so offsets stay profiled relative to the player object
/// and are shifted for staff.
/// </summary>
public sealed record PersonOwner(ulong Address, int Shift, IReadOnlyList<uint> Vtables, string Name)
{
    PlayerInsertion? Insertion { get; init; }

    /// <summary>Where a list profiled at player + <paramref name="playerOffset"/> is for this owner.</summary>
    public ulong At(int playerOffset) => Address + (ulong)((Insertion?.Translate(playerOffset) ?? playerOffset) - Shift);

    public bool IsValid(IProcessMemory memory, ulong moduleBase)
    {
        Span<byte> b = stackalloc byte[8];
        if (Address == 0 || !memory.TryRead(Address, b)) return false;
        ulong vt = BitConverter.ToUInt64(b);
        return Vtables.Any(v => vt == moduleBase + v);
    }

    public static PersonOwner Of(PlayerRecord player, OffsetProfile profile)
    {
        if (profile.PlayerInsertions.Count == 0)
            return new(player.Address, 0, profile.PlayerVtableRvas, player.DisplayName);

        // A subtype-specific address is valid only while the vtable still matches the player's snapshot.
        return new(player.Address, 0, profile.PlayerVtableRvas.Contains(player.VtableRva) ? [player.VtableRva] : [], player.DisplayName)
        {
            Insertion = profile.PlayerInsertions.TryGetValue(player.VtableRva, out var insertion) ? insertion : null,
        };
    }

    public static PersonOwner Of(Staff.StaffRecord staff, OffsetProfile profile)
    {
        var staffOffsets = profile.Staff ?? throw new InvalidOperationException("Profile has no staff offsets");
        // On a build whose staff vector holds staff objects the entry carries the staff vtable, not the person vtable.
        var vtables = staffOffsets.Kinds.SelectMany(k => new[] { k.PersonVtableRva, k.StaffVtableRva }).Distinct().ToList();
        return new(staff.Address + (ulong)staffOffsets.JobPersonAnchor,
            profile.Relations?.PlayerPerson ?? throw new InvalidOperationException("Profile has no person offset"),
            vtables, staff.DisplayName);
    }
}
