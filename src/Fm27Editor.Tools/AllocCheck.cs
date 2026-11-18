using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;

/// <summary>
/// <c>alloccheck</c> (read only): for squad buffers and contracts of a few clubs, the qword just before the block. A block from
/// ucrt's _aligned_malloc keeps its original allocation pointer there, a little below the block; a pooled or plain block does not.
/// </summary>
static class AllocCheck
{
    public static int Run(Func<bool, GameSession> attach)
    {
        using var session = attach(false);
        var p = session.Profile; var mem = session.Memory;
        var all = session.Players.LoadAll();
        var clubs = session.LoadClubs(all);
        string Before(ulong at)
        {
            ulong q = mem.ReadUInt64(at - 8);
            long d = (long)at - (long)q;
            return d is > 0 and <= 0x1000 ? $"p-8 = 0x{q:X} (block - 0x{d:X}: aligned header)" : $"p-8 = 0x{q:X}";
        }
        foreach (var name in new[] { "Everton", "Leeds", "Man Utd", "Chelsea", "Aston Villa" })
        {
            var club = clubs.FirstOrDefault(c => (c.ShortName ?? c.Name ?? "").StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (club?.FirstTeam is not { } team) continue;
            ulong sq = team.Address + (ulong)p.Team!.Squad;
            ulong b = mem.ReadUInt64(sq), e = mem.ReadUInt64(sq + 8), c = mem.ReadUInt64(sq + 16);
            Console.WriteLine($"{club.DisplayName}: squad {(e - b) / 8}/{(c - b) / 8} at 0x{b:X}  {Before(b)}");
            foreach (var pl in all.Where(x => x.Club?.Address == club.Address && x.Contract is not null).Take(3))
                Console.WriteLine($"    contract of {pl.DisplayName} 0x{pl.Contract!.Address:X}  {Before(pl.Contract.Address)}");
        }
        return 0;
    }
}
