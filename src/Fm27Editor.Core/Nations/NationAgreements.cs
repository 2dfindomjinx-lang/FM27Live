using System.Text;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Nations;

/// <summary>Which slot of the record holds the target: FMRTE's Type dropdown (Region and Agreement are measured).</summary>
public enum AgreementKind { Unknown, Region, Agreement, Nation, ExcludedNation, Continent }
public sealed record AgreementRegion(ulong Address, int Id, string Name);

/// <summary>
/// One row of FMRTE's Agreements card: the "Nations Treated as EU" and "Nations Treated as Non-Foreign" lists.
/// Measured 5/11 with a Frida write trace on FMRTE 27: the lists are pointer vectors at nation+0xA8 (EU) and nation+0x90
/// (non-foreign); every row is a 0x30-byte record with the target pointer at +0x18 (Continent) or +0x20 (Agreement) and
/// the From/Until dates at +0x28/+0x2C; adding a row allocates a record and a fresh pointer array (FMRTE wrote 48 bytes
/// then 24 bytes then begin/end/cap), exactly what <see cref="Duplicate"/> does.
/// </summary>
public sealed record NationAgreementRow(ulong Address, ulong Target, string? TargetName, AgreementKind Kind, byte[] Snapshot,
    DateOnly? From, DateOnly? Until, ulong Begin, ulong End, ulong Capacity, ulong Slot, int Vector);

public sealed class NationAgreements(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    public const int EuVector = 0xA8, NonForeignVector = 0x90;
    public const int RecordSize = 0x30, RegionSlot = 0x18, AgreementSlot = 0x20, FromOffset = 0x28, UntilOffset = 0x2C;
    const int MaxRows = 64, MaxName = 200;
    List<AgreementRegion>? _regions;
    Exception? regionLoadError;

    /// <summary>Load-time discovery retains errors too, so opening a tab cannot retry a failed heap sweep.</summary>
    public IReadOnlyList<AgreementRegion> PreloadRegions(Func<IReadOnlyList<ulong>> discover)
    {
        if (regionLoadError is not null) throw new InvalidOperationException("Agreement region index is unavailable. Load Game to refresh it.", regionLoadError);
        if (_regions is not null) return _regions;
        try { return TryReadOwnedRegions() ?? Regions(discover()); }
        catch (Exception ex) { regionLoadError = ex; throw; }
    }

    /// <summary>Region catalogue in the game database; unknown names remain numeric IDs.</summary>
    public IReadOnlyList<AgreementRegion> Regions(IReadOnlyList<ulong>? heapCandidates = null)
    {
        if (regionLoadError is not null) throw new InvalidOperationException("Agreement region index is unavailable. Load Game to refresh it.", regionLoadError);
        if (_regions is not null) return _regions;
        if (profile.AgreementRegionVtable is not { } regionVtable || profile.Nation?.ListRootRva is not { } rootRva)
            return _regions = [];
        return TryReadOwnedRegions() ?? (_regions = ScannedRegions(regionVtable, heapCandidates));
    }
    /// <summary>
    /// Fully validated region vector in a profile-owned database holder. Null means discovery is uncertain;
    /// it never turns an unreadable, ambiguous or empty candidate into an empty catalogue.
    /// </summary>
    public IReadOnlyList<AgreementRegion>? TryReadOwnedRegions()
    {
        if (regionLoadError is not null) throw new InvalidOperationException("Agreement region index is unavailable. Load Game to refresh it.", regionLoadError);
        if (_regions is not null) return _regions;
        if (profile.AgreementRegionVtable is not { } regionVtable) return null;
        var slots = new List<ulong>();
        if (profile.Nation?.ListRootRva is { } nationRoot)
            for (long delta = -0x20; delta <= 0x20; delta += 8)
                if ((long)nationRoot + delta >= 0) slots.Add(moduleBase + (ulong)((long)nationRoot + delta));
        // The shared database holder can also be reached through the already measured stadium root.
        if (profile.Stadium is { } stadium) slots.Add(moduleBase + stadium.ListRootRva);
        var seen = new HashSet<ulong>();
        var candidates = new List<(ulong Slot, ulong Holder, ulong Header, byte[] HeaderBytes, ulong Begin, byte[] Pointers, List<AgreementRegion> Rows, List<byte[]> Records, bool Child)>();
        bool unknownTop = false, unknownChild = false;
        Span<byte> pointer = stackalloc byte[8];
        var holders = new List<(ulong Slot, ulong Holder, bool Child)>();
        foreach (ulong root in slots.Distinct())
        {
            if (!memory.TryRead(root, pointer)) continue;
            ulong top = BitConverter.ToUInt64(pointer);
            if (top < 0x10000) continue;
            holders.Add((root, top, false));
            // Mac 27.0.1 keeps the database tables one object further in, at the nation list's child offset (0x88);
            // the child's pointer slot is then the publication path that is re-read before caching.
            if (profile.Nation?.ListObjectOffset is { } childOffset && memory.TryRead(top + (ulong)childOffset, pointer)
                && BitConverter.ToUInt64(pointer) is var child && child >= 0x10000)
                holders.Add((top + (ulong)childOffset, child, true));
        }
        foreach (var (slot, holder, isChild) in holders)
        {
            if (!seen.Add(holder)) continue;
            var prefix = new byte[0x810]; // Same bounded database prefix used by the existing nation discovery.
            if (!memory.TryRead(holder, prefix)) continue;
            for (int at = 0; at + 24 <= prefix.Length; at += 8)
            {
                ulong begin = BitConverter.ToUInt64(prefix, at), end = BitConverter.ToUInt64(prefix, at + 8), capacity = BitConverter.ToUInt64(prefix, at + 16);
                if (begin < 0x10000 || end <= begin || capacity < end || (end - begin) % 8 != 0 || (capacity - begin) % 8 != 0 || (end - begin) / 8 > 64) continue;
                var pointers = new byte[(int)(end - begin)];
                if (!memory.TryRead(begin, pointers)) { if (isChild) unknownChild = true; else unknownTop = true; continue; }
                var rows = new List<AgreementRegion>();
                var records = new List<byte[]>();
                var ids = new HashSet<int>();
                bool invalid = false, unreadable = false;
                for (int i = 0; i < pointers.Length; i += 8)
                {
                    ulong address = BitConverter.ToUInt64(pointers, i);
                    if (address < 0x10000) { invalid = true; continue; }
                    var bytes = new byte[24];
                    if (!memory.TryRead(address, bytes)) { unreadable = true; continue; }
                    int id = BitConverter.ToInt32(bytes, 0x10);
                    if (BitConverter.ToUInt64(bytes) != moduleBase + regionVtable || id is < 1 or > 255 || !ids.Add(id)) { invalid = true; continue; }
                    rows.Add(new(address, id, RegionName(id)));
                    records.Add(bytes);
                }
                if (invalid) continue;
                if (unreadable) { if (isChild) unknownChild = true; else unknownTop = true; continue; }
                candidates.Add((slot, holder, holder + (ulong)at, prefix.AsSpan(at, 24).ToArray(), begin, pointers, rows, records, isChild));
            }
        }
        // When the profile names the child object that holds the database tables (Mac) and it has a valid catalogue, only
        // that object counts: the parent may hold a shorter vector of the same records. Otherwise every candidate must agree.
        bool useChild = candidates.Any(c => c.Child);
        if (useChild) candidates.RemoveAll(c => !c.Child);
        if ((useChild ? unknownChild : unknownTop || unknownChild) || candidates.Count == 0) return null;
        var selected = candidates[0];
        if (candidates.Any(c => !c.Pointers.AsSpan().SequenceEqual(selected.Pointers))) return null;
        // Re-read the entire publication path and records without a bulk cache before retaining any catalogue.
        var rootCheck = new byte[8];
        var headerCheck = new byte[24];
        var pointerCheck = new byte[selected.Pointers.Length];
        if (!memory.TryRead(selected.Slot, rootCheck) || BitConverter.ToUInt64(rootCheck) != selected.Holder
            || !memory.TryRead(selected.Header, headerCheck) || !headerCheck.AsSpan().SequenceEqual(selected.HeaderBytes)
            || !memory.TryRead(selected.Begin, pointerCheck) || !pointerCheck.AsSpan().SequenceEqual(selected.Pointers)) return null;
        for (int i = 0; i < selected.Rows.Count; i++)
        {
            var bytes = new byte[24];
            if (!memory.TryRead(selected.Rows[i].Address, bytes) || !bytes.AsSpan().SequenceEqual(selected.Records[i])) return null;
        }
        return _regions = selected.Rows.OrderBy(r => r.Id).ToList();
    }

    List<AgreementRegion> ScannedRegions(uint regionVtable, IReadOnlyList<ulong>? candidates)
    {
        var byId = new Dictionary<int, AgreementRegion>();
        candidates ??= HeapVtableScanner.Scan(memory, moduleBase + regionVtable).Select(m => m.Address).ToArray();
        foreach (ulong address in candidates)
            if (ReadRegion(address, requireReadable: true) is { } region) byId.TryAdd(region.Id, region);
        return byId.Values.OrderBy(r => r.Id).ToList();
    }
    AgreementRegion? ReadRegion(ulong address, bool requireReadable = false)
    {
        if (profile.AgreementRegionVtable is null || address == 0) return null;
        var bytes = new byte[24];
        if (!memory.TryRead(address, bytes))
        {
            if (requireReadable) throw new MemoryAccessException("An agreement region candidate could not be read");
            return null;
        }
        if (BitConverter.ToUInt64(bytes) != moduleBase + profile.AgreementRegionVtable.Value) return null;
        int id = BitConverter.ToInt32(bytes, 0x10);
        if (id is < 1 or > 255) return null;
        return new(address, id, RegionName(id));
    }

    /// <summary>
    /// FMRTE's region labels, in the order its metadata lists them. Only UK/Ireland = 14 was independently
    /// observed; keep the others tentative and show the numeric ID for anything not mapped.
    /// </summary>
    static string RegionName(int id) => id switch
    {
        1 => "Africa", 2 => "Asia", 3 => "Caribbean", 4 => "Central America", 5 => "Central Europe",
        6 => "Eastern Europe", 7 => "Middle Eastern", 8 => "North Africa", 9 => "North America",
        10 => "Oceania", 11 => "Scandinavia", 12 => "South America", 13 => "South Europe", 14 => "Uk And Ireland",
        15 => "Central Asia", 16 => "East Asia", 17 => "South Asia", 18 => "Southeast Asia",
        19 => "Western Africa", 20 => "Central Africa", 21 => "East Africa", 22 => "Southern Africa",
        _ => id.ToString()
    };

    public IReadOnlyList<NationAgreementRow> Read(ulong nation, int vector, IReadOnlyList<ContinentRecord> continents, IReadOnlyList<NationRecord>? nations = null)
    {
        if (!IsNation(nation) || vector is not (EuVector or NonForeignVector)) return [];
        ulong begin = memory.ReadUInt64(nation + (ulong)vector), end = memory.ReadUInt64(nation + (ulong)vector + 8),
            capacity = memory.ReadUInt64(nation + (ulong)vector + 16);
        if (begin == 0 || end < begin || capacity < end || (end - begin) % 8 != 0
            || (capacity - begin) % 8 != 0 || (end - begin) / 8 > MaxRows) return [];
        var rows = new List<NationAgreementRow>();
        for (ulong slot = begin; slot < end; slot += 8)
        {
            ulong address = memory.ReadUInt64(slot);
            if (address == 0) continue;
            var bytes = new byte[RecordSize];
            if (!memory.TryRead(address, bytes)) continue;
            ulong nationTarget = BitConverter.ToUInt64(bytes, 0), continent = BitConverter.ToUInt64(bytes, 8), excluded = BitConverter.ToUInt64(bytes, 16), region = BitConverter.ToUInt64(bytes, RegionSlot), agreement = BitConverter.ToUInt64(bytes, AgreementSlot);
            int active = new[] { nationTarget, continent, excluded, region, agreement }.Count(x => x != 0);
            var kind = active != 1 ? AgreementKind.Unknown : nationTarget != 0 && IsNation(nationTarget) ? AgreementKind.Nation
                : excluded != 0 && IsNation(excluded) ? AgreementKind.ExcludedNation
                : continent != 0 && profile.CareerPlans is { } cp && memory.TryRead(continent, new byte[8]) && memory.ReadUInt64(continent) == moduleBase + cp.ContinentVtable ? AgreementKind.Continent
                : region != 0 ? AgreementKind.Region : agreement != 0 ? AgreementKind.Agreement : AgreementKind.Unknown;
            ulong target = kind switch { AgreementKind.Nation => nationTarget, AgreementKind.ExcludedNation => excluded, AgreementKind.Continent => continent, AgreementKind.Region => region, AgreementKind.Agreement => agreement, _ => 0 };
            string? targetName = kind is AgreementKind.Nation or AgreementKind.ExcludedNation ? nations?.FirstOrDefault(x => x.Address == target)?.DisplayName : NameOf(target, continents);
            rows.Add(new(address, target, targetName, kind, bytes,
                DateAt(bytes, FromOffset), DateAt(bytes, UntilOffset), begin, end, capacity, slot, vector));
        }
        return rows.AsReadOnly();
    }

    /// <summary>Removes one row: the pointers after it shift left and the end shrinks by one.</summary>
    public void Remove(ulong nation, NationAgreementRow row)
    {
        Require(nation, row);
        ulong next = row.Slot + 8;
        if (next < row.End)
        {
            var tail = memory.Read(next, (int)(row.End - next));
            if (!memory.TryWrite(row.Slot, tail) || !memory.Read(row.Slot, tail.Length).AsSpan().SequenceEqual(tail))
                throw new MemoryAccessException($"Could not move the agreement pointers at 0x{row.Slot:X}");
        }
        ulong header = nation + (ulong)row.Vector;
        var end = BitConverter.GetBytes(row.End - 8);
        if (!memory.TryWrite(header + 8, end) || !memory.Read(header + 8, 8).AsSpan().SequenceEqual(end))
            throw new MemoryAccessException($"Could not shrink the agreement vector at 0x{header:X}");
    }

    public void SetDates(ulong nation, NationAgreementRow row, DateOnly? from, DateOnly? until)
    {
        Require(nation, row);
        byte[] bytes = row.Snapshot.AsSpan(FromOffset, 8).ToArray();
        void Encode(int offset, DateOnly? value)
        {
            var date = value ?? new DateOnly(1900, 1, 1);
            ushort day = (ushort)((BitConverter.ToUInt16(bytes, offset) & 0xFE00) | date.DayOfYear);
            BitConverter.GetBytes(day).CopyTo(bytes, offset);
            BitConverter.GetBytes((short)date.Year).CopyTo(bytes, offset + 2);
        }
        Encode(0, from); Encode(4, until);
        if (!memory.TryWrite(row.Address + FromOffset, bytes) || !memory.Read(row.Address + FromOffset, 8).AsSpan().SequenceEqual(bytes))
            throw new MemoryAccessException("Could not write agreement dates");
    }

    /// <summary>Changes a measured nation or continent target; clears the other mutually exclusive slots.</summary>
    public void SetTarget(ulong nation, NationAgreementRow row, AgreementKind kind, ulong target)
    {
        int offset = TargetOffset(kind, target);
        Require(nation, row);
        var slots = new byte[0x28];
        BitConverter.GetBytes(target).CopyTo(slots, offset);
        if (!memory.TryWrite(row.Address, slots) || !memory.Read(row.Address, slots.Length).AsSpan().SequenceEqual(slots))
            throw new MemoryAccessException("Could not write agreement target");
    }

    int TargetOffset(AgreementKind kind, ulong target) => kind switch
    {
        AgreementKind.Nation when IsNation(target) => 0,
        AgreementKind.ExcludedNation when IsNation(target) => 0x10,
        AgreementKind.Continent when profile.CareerPlans is { } c && target != 0 && memory.TryRead(target, new byte[8]) && memory.ReadUInt64(target) == moduleBase + c.ContinentVtable => 8,
        AgreementKind.Region when ReadRegion(target) is not null => RegionSlot,
        AgreementKind.Agreement when profile.AgreementVtable is { } agreementVtable && target != 0 && memory.TryRead(target, new byte[8]) && memory.ReadUInt64(target) == moduleBase + agreementVtable => 0x20,
        _ => throw new StaleObjectException("Invalid or unsupported agreement target")
    };

    /// <summary>
    /// Adds a row the way FMRTE does: copy the record into a new 0x30-byte object, build a fresh pointer array holding the
    /// old pointers plus the new one, then point begin/end/capacity at it.
    /// </summary>
    public NationAgreementRow? Duplicate(ulong nation, NationAgreementRow row, Func<int, ulong>? allocate, IReadOnlyList<ContinentRecord> continents)
    {
        Require(nation, row);
        if (allocate is null) throw new StaleObjectException("No allocator is available to add an agreement row");
        ulong record = allocate(RecordSize);
        if (record == 0) throw new MemoryAccessException("Could not allocate an agreement record");
        if (!memory.TryWrite(record, row.Snapshot) || !memory.Read(record, row.Snapshot.Length).AsSpan().SequenceEqual(row.Snapshot))
            throw new MemoryAccessException($"Could not write the new agreement record at 0x{record:X}");
        int count = (int)((row.End - row.Begin) / 8);
        int newBytes = (count + 1) * 8;
        ulong array = allocate(newBytes);
        if (array == 0) throw new MemoryAccessException("Could not allocate an agreement pointer array");
        var old = memory.Read(row.Begin, count * 8);
        if (!memory.TryWrite(array, old) || !memory.Read(array, old.Length).AsSpan().SequenceEqual(old))
            throw new MemoryAccessException($"Could not copy the agreement pointers to 0x{array:X}");
        var added = BitConverter.GetBytes(record);
        if (!memory.TryWrite(array + (ulong)(count * 8), added) || !memory.Read(array + (ulong)(count * 8), 8).AsSpan().SequenceEqual(added))
            throw new MemoryAccessException($"Could not write the new agreement pointer at 0x{array + (ulong)(count * 8):X}");
        var header = new byte[24];
        BitConverter.GetBytes(array).CopyTo(header, 0);
        BitConverter.GetBytes(array + (ulong)newBytes).CopyTo(header, 8);
        BitConverter.GetBytes(array + (ulong)newBytes).CopyTo(header, 16);
        ulong at = nation + (ulong)row.Vector;
        if (!memory.TryWrite(at, header) || !memory.Read(at, header.Length).AsSpan().SequenceEqual(header))
            throw new MemoryAccessException($"Could not point the agreement vector at 0x{array:X}");
        return Read(nation, row.Vector, continents).LastOrDefault(r => r.Address == record);
    }

    /// <summary>Copies a verified agreement record into an empty vector of the same kind.</summary>
    public NationAgreementRow? AddToEmpty(ulong nation, byte[] expectedHeader, ulong sourceNation, NationAgreementRow source,
        Func<int, ulong>? allocate, IReadOnlyList<ContinentRecord> continents)
    {
        if (!IsNation(nation) || source.Vector is not (EuVector or NonForeignVector) || source.Kind == AgreementKind.Unknown || expectedHeader.Length != 24)
            throw new StaleObjectException("Invalid nation or agreement template");
        _ = TargetOffset(source.Kind, source.Target);
        ulong headerAddress = nation + (ulong)source.Vector;
        ulong begin = BitConverter.ToUInt64(expectedHeader, 0), end = BitConverter.ToUInt64(expectedHeader, 8), capacity = BitConverter.ToUInt64(expectedHeader, 16);
        if (begin != end || capacity < end || (begin == 0 && capacity != 0) || !memory.Read(headerAddress, 24).AsSpan().SequenceEqual(expectedHeader))
            throw new StaleObjectException("The agreement list is no longer empty");
        Require(sourceNation, source);
        if (allocate is null) throw new StaleObjectException("No allocator is available to add an agreement row");
        ulong record = allocate(RecordSize), array = allocate(8);
        if (record == 0 || array == 0) throw new MemoryAccessException("Could not allocate the agreement record or vector");
        if (!memory.TryWrite(record, source.Snapshot) || !memory.Read(record, RecordSize).AsSpan().SequenceEqual(source.Snapshot))
            throw new MemoryAccessException("Could not write the new agreement record");
        var pointer = BitConverter.GetBytes(record);
        if (!memory.TryWrite(array, pointer) || !memory.Read(array, 8).AsSpan().SequenceEqual(pointer))
            throw new MemoryAccessException("Could not write the agreement pointer");
        Require(sourceNation, source);
        _ = TargetOffset(source.Kind, source.Target);
        if (!memory.Read(headerAddress, 24).AsSpan().SequenceEqual(expectedHeader))
            throw new StaleObjectException("The agreement list changed while adding an agreement");
        var header = new byte[24];
        BitConverter.GetBytes(array).CopyTo(header, 0);
        BitConverter.GetBytes(array + 8).CopyTo(header, 8);
        BitConverter.GetBytes(array + 8).CopyTo(header, 16);
        if (!memory.TryWrite(headerAddress, header) || !memory.Read(headerAddress, 24).AsSpan().SequenceEqual(header))
            throw new MemoryAccessException("Could not write the agreement vector");
        return Read(nation, source.Vector, continents).FirstOrDefault(r => r.Address == record);
    }

    void Require(ulong nation, NationAgreementRow row)
    {
        if (!IsNation(nation) || row.Vector is not (EuVector or NonForeignVector) || row.Snapshot.Length != RecordSize)
            throw new StaleObjectException("The nation or agreement snapshot changed since it was read");
        ulong header = nation + (ulong)row.Vector;
        if (memory.ReadUInt64(header) != row.Begin || memory.ReadUInt64(header + 8) != row.End
            || memory.ReadUInt64(header + 16) != row.Capacity
            || row.Slot < row.Begin || row.Slot >= row.End
            || memory.ReadUInt64(row.Slot) != row.Address
            || !memory.Read(row.Address, RecordSize).AsSpan().SequenceEqual(row.Snapshot))
            throw new StaleObjectException("The agreement row changed since it was read");
    }

    string? NameOf(ulong target, IReadOnlyList<ContinentRecord> continents)
    {
        if (target == 0) return null;
        if (ReadRegion(target) is { } region) return region.Name;
        if (continents.FirstOrDefault(c => c.Address == target) is { } known) return known.Name;
        if (profile.CareerPlans is { } c && memory.TryRead(target, new byte[8]) && memory.ReadUInt64(target) == moduleBase + c.ContinentVtable)
            return StringAt(memory.ReadUInt64(target + (ulong)c.ContinentName));
        // FMRTE printed "23" for the region row, whose object carries 23 at +0x10 and no name string.
        if (StringAt(ReadPtr(target + 0x20)) is { } name) return name;
        if (StringAt(ReadPtr(target + 0x28)) is { } shortName) return shortName;
        return memory.TryRead(target, new byte[24]) ? BitConverter.ToInt32(memory.Read(target, 24), 0x10).ToString() : null;
    }

    DateOnly? DateAt(byte[] bytes, int offset)
    {
        ushort packed = BitConverter.ToUInt16(bytes, offset);
        short year = BitConverter.ToInt16(bytes, offset + 2);
        int day = packed & 0x1FF;
        if (year <= 1900 || day is < 1 or > 366) return null;
        try { return new DateOnly(year, 1, 1).AddDays(day - 1); } catch (ArgumentOutOfRangeException) { return null; }
    }

    ulong ReadPtr(ulong address) => memory.TryRead(address, new byte[8]) ? memory.ReadUInt64(address) : 0;

    string? StringAt(ulong p)
    {
        if (p == 0 || !memory.TryRead(p, new byte[4])) return null;
        int n = BitConverter.ToInt32(memory.Read(p, 4));
        if (n is < 1 or > MaxName) return null;
        var bytes = new byte[n];
        return memory.TryRead(p + 4, bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    bool IsNation(ulong address) =>
        address != 0 && profile.Nation is { } n && memory.TryRead(address, new byte[8]) && memory.ReadUInt64(address) == moduleBase + n.VtableRva;
}
