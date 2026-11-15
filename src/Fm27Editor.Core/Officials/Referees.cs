using System.Text;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Officials;

/// <summary>Editable referee numbers and flags. Names and birth date are read only.</summary>
public enum RefereeField
{
    CurrentAbility, PotentialAbility, Reputation,
    AllowingFlow, Discipline, ImportantMatches, Pressure, Refereeing, RunningLine, TimeKeeping,
    EliteList, Continental, EuropeanCategory,
}

/// <summary>
/// One referee from the game-wide officials array. Measured on 27.0.0+2493426 (Game Pass) with Daniel Richardson,
/// Daniel Barclay and Neil Hair, 8/11/2026: the array of pointers is 2,078 objects, every object carries vtable
/// +0x4F11568 and the FM26 referee layout still holds — first name +0x28, last name +0x30, common name +0x38,
/// nation +0x40, city +0x58, birth day-of-year +0x60 and year +0x62, CA int16 +0x11C, PA +0x11E, reputation int16
/// +0x120, then bytes at +0x122 Allowing Flow, +0x123 Discipline, +0x124 Important Matches, +0x125 Pressure,
/// +0x126 Refereeing, +0x127 Running Line, +0x128 Time Keeping, +0x129 Elite List, +0x12A Continental,
/// +0x12B European Category; unique id int32 +0x144. Values observed in the 1..20 range for the six skills, 0/1 for
/// the three flags and 0..4 for the European category.
/// </summary>
public sealed record RefereeRecord(ulong Address, uint VtableRva, string? FirstName, string? LastName, string? CommonName,
    ulong NationAddress, ulong CityAddress, DateOnly? DateOfBirth, int CurrentAbility, int PotentialAbility, int Reputation,
    int AllowingFlow, int Discipline, int ImportantMatches, int Pressure, int Refereeing, int RunningLine, int TimeKeeping,
    int EliteList, int Continental, int EuropeanCategory, int UniqueId, string Snapshot)
{
    public string DisplayName => string.IsNullOrWhiteSpace(CommonName) ? $"{FirstName} {LastName}".Trim() : CommonName;
}

/// <summary>
/// Reads the officials array. A measured profile (<see cref="Offsets.OfficialsOffsets"/>) names its module slot and
/// object layout (Mac); without one the array is found through the clubs anchor and the x86-64 layout is used.
/// </summary>
public sealed class RefereeDirectory(IProcessMemory memory, ulong moduleBase, ulong moduleSize, Fm27Editor.Core.Offsets.OfficialsOffsets? offsets = null)
{
    // x86-64 (Windows 27.0.0) defaults, used when the profile has no officials section.
    const int FirstNameDefault = 0x28, LastNameDefault = 0x30, CommonNameDefault = 0x38, NationDefault = 0x40, CityDefault = 0x58;
    const int BirthDayDefault = 0x60, BirthYearDefault = 0x62;
    const int CurrentAbilityDefault = 0x11C, PotentialAbilityDefault = 0x11E, ReputationDefault = 0x120;
    const int AllowingFlowDefault = 0x122, DisciplineDefault = 0x123, ImportantMatchesDefault = 0x124, PressureDefault = 0x125;
    const int RefereeingDefault = 0x126, RunningLineDefault = 0x127, TimeKeepingDefault = 0x128, EliteListDefault = 0x129;
    const int ContinentalDefault = 0x12A, EuropeanCategoryDefault = 0x12B, UniqueIdDefault = 0x144, SnapshotSizeDefault = 0x148;
    const int MaxReferees = 20_000;

    int FirstName => offsets?.FirstName ?? FirstNameDefault;
    int LastName => offsets?.LastName ?? LastNameDefault;
    int CommonName => offsets?.CommonName ?? CommonNameDefault;
    int Nation => offsets?.Nation ?? NationDefault;
    int City => offsets?.City ?? CityDefault;
    int BirthDay => offsets?.BirthDay ?? BirthDayDefault;
    int BirthYear => offsets?.BirthYear ?? BirthYearDefault;
    int CurrentAbility => offsets?.CurrentAbility ?? CurrentAbilityDefault;
    int PotentialAbility => offsets?.PotentialAbility ?? PotentialAbilityDefault;
    int Reputation => offsets?.Reputation ?? ReputationDefault;
    int AllowingFlow => offsets?.AllowingFlow ?? AllowingFlowDefault;
    int Discipline => offsets?.Discipline ?? DisciplineDefault;
    int ImportantMatches => offsets?.ImportantMatches ?? ImportantMatchesDefault;
    int Pressure => offsets?.Pressure ?? PressureDefault;
    int Refereeing => offsets?.Refereeing ?? RefereeingDefault;
    int RunningLine => offsets?.RunningLine ?? RunningLineDefault;
    int TimeKeeping => offsets?.TimeKeeping ?? TimeKeepingDefault;
    int EliteList => offsets?.EliteList ?? EliteListDefault;
    int Continental => offsets?.Continental ?? ContinentalDefault;
    int EuropeanCategory => offsets?.EuropeanCategory ?? EuropeanCategoryDefault;
    int UniqueId => offsets?.UniqueId ?? UniqueIdDefault;
    int SnapshotSize => offsets?.SnapshotSize ?? SnapshotSizeDefault;

    /// <summary>The byte offset and allowed range of an editable field for the given profile layout.</summary>
    public static (int Offset, int Min, int Max) Spec(RefereeField field, Fm27Editor.Core.Offsets.OfficialsOffsets? offsets = null) => field switch
    {
        RefereeField.CurrentAbility => (offsets?.CurrentAbility ?? CurrentAbilityDefault, 1, 200),
        RefereeField.PotentialAbility => (offsets?.PotentialAbility ?? PotentialAbilityDefault, 1, 200),
        RefereeField.Reputation => (offsets?.Reputation ?? ReputationDefault, 0, 10_000),
        RefereeField.AllowingFlow => (offsets?.AllowingFlow ?? AllowingFlowDefault, 1, 20),
        RefereeField.Discipline => (offsets?.Discipline ?? DisciplineDefault, 1, 20),
        RefereeField.ImportantMatches => (offsets?.ImportantMatches ?? ImportantMatchesDefault, 1, 20),
        RefereeField.Pressure => (offsets?.Pressure ?? PressureDefault, 1, 20),
        RefereeField.Refereeing => (offsets?.Refereeing ?? RefereeingDefault, 1, 20),
        RefereeField.RunningLine => (offsets?.RunningLine ?? RunningLineDefault, 1, 20),
        RefereeField.TimeKeeping => (offsets?.TimeKeeping ?? TimeKeepingDefault, 1, 20),
        RefereeField.EliteList => (offsets?.EliteList ?? EliteListDefault, 0, 1),
        RefereeField.Continental => (offsets?.Continental ?? ContinentalDefault, 0, 1),
        RefereeField.EuropeanCategory => (offsets?.EuropeanCategory ?? EuropeanCategoryDefault, 0, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    internal (int Offset, int Min, int Max) Spec(RefereeField field) => Spec(field, offsets);

    /// <summary>Every editable field's byte offset and display name for the given layout, for the edit history.</summary>
    public static IReadOnlyList<(int Offset, string Name)> FieldOffsets(Fm27Editor.Core.Offsets.OfficialsOffsets? offsets = null) =>
        Enum.GetValues<RefereeField>().Select(f => (Spec(f, offsets).Offset, f switch
        {
            RefereeField.CurrentAbility => "Current Ability",
            RefereeField.PotentialAbility => "Potential Ability",
            RefereeField.AllowingFlow => "Allowing Flow",
            RefereeField.ImportantMatches => "Important Matches",
            RefereeField.RunningLine => "Running Line",
            RefereeField.TimeKeeping => "Time Keeping",
            RefereeField.EliteList => "Elite List",
            RefereeField.EuropeanCategory => "European Category",
            _ => f.ToString(),
        })).ToList();

    internal int Window => SnapshotSize;

    /// <summary>Every referee the array holds; empty when the anchor or the array is not readable.</summary>
    public IReadOnlyList<RefereeRecord> ReadAll()
    {
        using var reads = new BulkReadScope(memory);
        return new RefereeDirectory(reads.Memory, moduleBase, moduleSize, offsets).ReadAllCore();
    }

    IReadOnlyList<RefereeRecord> ReadAllCore()
    {
        ulong begin, end;
        if (offsets?.ListRootRva is { } rva)
        {
            // Mac: the module slot itself holds the begin/end pointers.
            begin = memory.TryRead(moduleBase + rva, new byte[8].AsSpan()) ? memory.ReadUInt64(moduleBase + rva) : 0;
            end = memory.TryRead(moduleBase + rva + 8, new byte[8].AsSpan()) ? memory.ReadUInt64(moduleBase + rva + 8) : 0;
        }
        else if (GlobalArrayFinder.Read(memory, moduleBase, moduleSize) is { } arrays)
        {
            begin = arrays.RefereesBegin;
            end = arrays.RefereesEnd;
        }
        else return [];
        int count = GlobalArrays.Count(begin, end);
        if (count is 0 or > MaxReferees) return [];
        var pointers = new byte[count * 8];
        if (!memory.TryRead(begin, pointers)) return [];
        var result = new List<RefereeRecord>(count);
        for (int i = 0; i < count; i++)
            if (Read(BitConverter.ToUInt64(pointers, i * 8)) is { } referee) result.Add(referee);
        return result;
    }

    /// <summary>One referee; null when the object is not a referee of this build.</summary>
    public RefereeRecord? Read(ulong address)
    {
        if (address == 0) return null;
        var bytes = new byte[SnapshotSize];
        if (!memory.TryRead(address, bytes)) return null;
        ulong vtable = BitConverter.ToUInt64(bytes);
        if (offsets is { } o ? vtable != moduleBase + o.VtableRva : vtable < moduleBase || vtable >= moduleBase + moduleSize) return null;
        int day = BitConverter.ToInt16(bytes, BirthDay), year = BitConverter.ToInt16(bytes, BirthYear);
        DateOnly? born = year is >= 1900 and <= 2100 && day is >= 1 and <= 366
            ? DateOnly.FromDayNumber(new DateOnly(year, 1, 1).DayNumber + day - 1) : null;
        return new(address, (uint)(vtable - moduleBase), EntryString(bytes, FirstName), EntryString(bytes, LastName),
            EntryString(bytes, CommonName), BitConverter.ToUInt64(bytes, Nation), BitConverter.ToUInt64(bytes, City), born,
            BitConverter.ToInt16(bytes, CurrentAbility), BitConverter.ToInt16(bytes, PotentialAbility),
            BitConverter.ToInt16(bytes, Reputation), bytes[AllowingFlow], bytes[Discipline], bytes[ImportantMatches],
            bytes[Pressure], bytes[Refereeing], bytes[RunningLine], bytes[TimeKeeping], bytes[EliteList],
            bytes[Continental], bytes[EuropeanCategory], BitConverter.ToInt32(bytes, UniqueId), Convert.ToHexString(bytes));
    }

    string? EntryString(byte[] objectBytes, int offset)
    {
        ulong entry = BitConverter.ToUInt64(objectBytes, offset);
        return entry == 0 ? null : StringAt(EntryPointer(entry));
    }

    ulong EntryPointer(ulong entry)
    {
        Span<byte> b = stackalloc byte[8];
        return memory.TryRead(entry, b) ? BitConverter.ToUInt64(b) : 0;
    }

    /// <summary>Length-prefixed UTF-8, the game's name string form.</summary>
    string? StringAt(ulong address)
    {
        if (address == 0) return null;
        Span<byte> len = stackalloc byte[4];
        if (!memory.TryRead(address, len)) return null;
        int n = BitConverter.ToInt32(len);
        if (n <= 0 || n > 200) return null;
        var text = new byte[n];
        return memory.TryRead(address + 4, text) ? Encoding.UTF8.GetString(text) : null;
    }
}

/// <summary>
/// Guarded writes for one referee: the whole 0x148-byte snapshot must still match, the value must be inside the
/// range FMRTE/game use, then read back. Names and the pointer fields are never written.
/// </summary>
public sealed class RefereeWriter(IProcessMemory memory, ulong moduleBase, Fm27Editor.Core.Offsets.OfficialsOffsets? offsets = null)
{
    public void Set(RefereeRecord referee, RefereeField field, int value)
    {
        var (offset, min, max) = new RefereeDirectory(memory, moduleBase, 0, offsets).Spec(field);
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{field} must be {min}..{max}");
        var before = Convert.FromHexString(referee.Snapshot);
        if (before.Length != new RefereeDirectory(memory, moduleBase, 0, offsets).Window || BitConverter.ToUInt64(before) != moduleBase + referee.VtableRva)
            throw new StaleObjectException("The referee snapshot has an invalid vtable");
        var now = memory.Read(referee.Address, before.Length);
        if (!now.AsSpan().SequenceEqual(before))
            throw new StaleObjectException("The referee changed since it was read; nothing written");

        var after = (byte[])before.Clone();
        if (field is RefereeField.CurrentAbility or RefereeField.PotentialAbility or RefereeField.Reputation)
            BitConverter.GetBytes((short)value).CopyTo(after, offset);
        else after[offset] = (byte)value;
        int size = field is RefereeField.CurrentAbility or RefereeField.PotentialAbility or RefereeField.Reputation ? 2 : 1;
        if (!memory.TryWrite(referee.Address + (ulong)offset, after.AsSpan(offset, size)) ||
            !memory.Read(referee.Address, after.Length).AsSpan().SequenceEqual(after))
            throw new InvalidOperationException($"Could not write the referee at 0x{referee.Address:X}");
    }
}
