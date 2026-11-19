using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;

/// <summary>
/// <c>hybridprobe [n]</c> (read only): for the first n players whose object is a db::ACTUAL_PLAYER_AND_NON_PLAYER (a player who
/// is also a member of staff), every module pointer in the object's first 0x800 bytes with its RTTI class, and where the staff
/// list points into the same object.
/// </summary>
static class HybridProbe
{
    public static int Run(Func<bool, GameSession> attach, int count)
    {
        using var session = attach(false);
        var p = session.Profile; var mem = session.Memory; ulong mb = session.ModuleBase;
        var rtti = Fm27Editor.Core.Offsets.RttiIndex.Build(Fm27Editor.Core.Offsets.ModuleImage.ReadForDiscovery(mem, mb, (int)(session.Game.ModuleEnd - mb)));
        uint hybrid = rtti.Vtables("db::ACTUAL_PLAYER_AND_NON_PLAYER", 0).FirstOrDefault();
        Console.WriteLine($"db::ACTUAL_PLAYER_AND_NON_PLAYER +0 vtable 0x{hybrid:X}; all: {string.Join(", ", rtti.Vtables("db::ACTUAL_PLAYER_AND_NON_PLAYER").Select(v => $"0x{v:X}@+{rtti.EntryOf(v)!.SubobjectOffset}"))}");
        var so = p.Staff!;
        ulong root = so.DirectStaffVector ? mb + so.PersonListRootRva : mem.ReadUInt64(mb + so.PersonListRootRva);
        ulong sb = mem.ReadUInt64(root + (ulong)so.PersonListOffset), se = mem.ReadUInt64(root + (ulong)so.PersonListOffset + 8);
        var staff = mem.Read(sb, (int)(se - sb));
        var staffPtrs = Enumerable.Range(0, staff.Length / 8).Select(i => BitConverter.ToUInt64(staff, i * 8)).ToArray();
        var all = session.Players.LoadAll();
        var players = all.Where(x => x.VtableRva == hybrid).Take(count).Concat(all.Where(x => x.VtableRva != hybrid && x.DisplayName == "Kobbie Mainoo").Take(1)).ToList();
        foreach (var pl in players)
        {
            Console.WriteLine($"player 0x{pl.Address:X} id {pl.Id} name '{pl.DisplayName}'");
            var obj = mem.Read(pl.Address, 0x800);
            for (int o = 0; o < obj.Length; o += 8)
            {
                ulong q = BitConverter.ToUInt64(obj, o);
                if (q < mb || q >= session.Game.ModuleEnd) continue;
                uint rva = (uint)(q - mb);
                string name = rtti.CanonicalNameOf(rva) is { } n ? $"{n} (sub +{rtti.EntryOf(rva)!.SubobjectOffset})" : "(no RTTI)";
                Console.WriteLine($"   +0x{o:X3}  0x{rva:X}  {name}");
            }
            foreach (int person in new[] { 0x288, 0x380 })
            {
                string Str(int at) { ulong ptr = mem.ReadUInt64(pl.Address + (ulong)(person + at)); if (ptr < 0x10000) return "-"; ulong s = mem.ReadUInt64(ptr); if (s < 0x10000) return "-"; int len = BitConverter.ToInt32(mem.Read(s, 4)); return len is > 0 and < 100 ? System.Text.Encoding.UTF8.GetString(mem.Read(s + 4, len)) : "-"; }
                var dob = mem.Read(pl.Address + (ulong)(person + 0x88), 4);
                Console.WriteLine($"   person at +0x{person:X}: id {BitConverter.ToInt32(mem.Read(pl.Address + (ulong)(person + 0xC), 4))}, first '{Str(0x50)}', last '{Str(0x58)}', common '{Str(0x60)}', born day {BitConverter.ToUInt16(dob, 0) & 0x1FF} year {BitConverter.ToInt16(dob, 2)}");
            }
            foreach (ulong s in staffPtrs.Where(s => s >= pl.Address && s < pl.Address + 0x800))
                Console.WriteLine($"   staff list points at +0x{s - pl.Address:X}");
        }
        return 0;
    }
}
