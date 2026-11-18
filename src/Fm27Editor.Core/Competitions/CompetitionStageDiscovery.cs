using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Competitions;

/// <summary>
/// Finds a competition's stages by back-pointer when the profile's holder chain is unavailable (Mac 27.1.0 keeps
/// no stage vector on the competition: the holder at +0xB0 carries no rows). The map is built by one heap scan
/// per save (keyed by the players vector begin, which changes on every save load) and every entry is revalidated
/// live on use, so stale entries fail closed.
/// </summary>
public sealed class CompetitionStageDiscovery(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    static readonly Dictionary<(ulong Module, ulong Epoch), IReadOnlyDictionary<ulong, ulong[]>> _cache = [];
    static readonly object _lock = new();

    /// <summary>Validated stage addresses owned by the competition, in heap order.</summary>
    public IReadOnlyList<ulong> StagesFor(ulong competition)
    {
        var offsets = profile.Competition?.PrizeMoney;
        var vtables = offsets?.StageVtables;
        if (offsets is null || vtables is null || vtables.Count == 0 || competition == 0) return [];
        if (!Map().TryGetValue(competition, out var stages)) return [];
        var result = new List<ulong>();
        var head = new byte[8];
        foreach (ulong stage in stages)
        {
            if (!memory.TryRead(stage, head)) continue;
            ulong vt = BitConverter.ToUInt64(head) - moduleBase;
            bool known = false;
            foreach (uint v in vtables) if (vt == v) { known = true; break; }
            if (!known) continue;
            var ob = new byte[8];
            if (!memory.TryRead(stage + (ulong)offsets.StageOwner, ob)) continue;
            if (BitConverter.ToUInt64(ob) != competition) continue;
            result.Add(stage);
        }
        return result;
    }

    IReadOnlyDictionary<ulong, ulong[]> Map()
    {
        ulong epoch = 0;
        try { epoch = memory.ReadUInt64(moduleBase + (ulong)profile.PlayerListRootRva + (ulong)profile.PlayerListBeginOffset); }
        catch (MemoryAccessException) { }
        lock (_lock)
        {
            if (_cache.TryGetValue((moduleBase, epoch), out var map)) return map;
            map = Scan();
            _cache[(moduleBase, epoch)] = map;
            return map;
        }
    }

    IReadOnlyDictionary<ulong, ulong[]> Scan()
    {
        var result = new Dictionary<ulong, List<ulong>>();
        var offsets = profile.Competition?.PrizeMoney;
        if (offsets?.StageVtables is not { } vtables || vtables.Count == 0 || memory is not IMemoryRegions) return new Dictionary<ulong, ulong[]>();
        var wanted = new ulong[vtables.Count];
        for (int i = 0; i < vtables.Count; i++) wanted[i] = moduleBase + vtables[i];
        var ob = new byte[8];
        foreach (var match in HeapVtableScanner.Scan(memory, wanted))
        {
            if (!memory.TryRead(match.Address + (ulong)offsets.StageOwner, ob)) continue;
            ulong owner = BitConverter.ToUInt64(ob);
            if (owner < 0x10000) continue;
            if (!result.TryGetValue(owner, out var list)) result[owner] = list = new List<ulong>();
            list.Add(match.Address);
        }
        return result.ToDictionary(kv => kv.Key, kv => (ulong[])kv.Value.ToArray());
    }
}
