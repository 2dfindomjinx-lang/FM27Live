using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Players;

/// <summary>Enumerates the game's player list from its static root in game_plugin.dll.</summary>
public sealed class PlayerRepository(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    const int MaxPlayers = 2_000_000;

    public IReadOnlyList<PlayerRecord> LoadAll()
    {
        using var reads = new BulkReadScope(memory);
        return new PlayerRepository(reads.Memory, profile, moduleBase).LoadAllCore();
    }

    IReadOnlyList<PlayerRecord> LoadAllCore()
    {
        var pointers = ReadPointerArray();
        var reader = new PlayerReader(memory, profile) { ModuleBase = moduleBase };
        var vtables = profile.PlayerVtableRvas.Select(v => moduleBase + v).ToHashSet();
        var obj = new byte[profile.PlayerObjectSize];
        var result = new List<PlayerRecord>(pointers.Length);
        foreach (var p in pointers)
        {
            if (p == 0 || !memory.TryRead(p, obj)) continue;
            ulong vt = BitConverter.ToUInt64(obj);
            if (!vtables.Contains(vt)) continue;
            uint rva = (uint)(vt - moduleBase);
            if (ReadSubtypeTail(p, rva, obj)) result.Add(reader.Decode(p, obj, rva));
        }
        return result;
    }

    /// <summary>Re-reads one player, e.g. after a write or to refresh a stale snapshot.</summary>
    public PlayerRecord? Reload(ulong address)
    {
        var obj = new byte[profile.PlayerObjectSize];
        if (!memory.TryRead(address, obj)) return null;
        ulong vt = BitConverter.ToUInt64(obj);
        if (!profile.PlayerVtableRvas.Any(v => moduleBase + v == vt)) return null;
        uint rva = (uint)(vt - moduleBase);
        if (!ReadSubtypeTail(address, rva, obj)) return null;
        return new PlayerReader(memory, profile) { ModuleBase = moduleBase }.Decode(address, obj, rva);
    }

    // Decode in canonical field order, without reading beyond the shorter record type.
    bool ReadSubtypeTail(ulong address, uint rva, Span<byte> obj) =>
        !profile.PlayerInsertions.TryGetValue(rva, out var insertion) ||
        memory.TryRead(checked(address + (ulong)insertion.At + (ulong)insertion.Length), obj[insertion.At..]);

    ulong[] ReadPointerArray()
    {
        ulong root = moduleBase + profile.PlayerListRootRva;
        ulong begin = memory.ReadUInt64(root + (ulong)profile.PlayerListBeginOffset);
        ulong end = memory.ReadUInt64(root + (ulong)profile.PlayerListEndOffset);
        if (begin == 0 || end < begin || (end - begin) % 8 != 0 || (end - begin) / 8 > MaxPlayers)
            throw new MemoryAccessException($"Player list root looks invalid (begin 0x{begin:X}, end 0x{end:X}); is a save loaded?");
        var raw = memory.Read(begin, (int)(end - begin));
        var pointers = new ulong[raw.Length / 8];
        Buffer.BlockCopy(raw, 0, pointers, 0, raw.Length);
        return pointers;
    }
}
