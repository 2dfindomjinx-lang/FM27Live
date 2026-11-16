using System.Runtime.CompilerServices;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

/// <summary>Derived values belong to a loaded immutable snapshot, never to a live memory address.</summary>
internal static class ScoutingMetrics
{
    sealed class Entry(PlayerRecord player)
    {
        public int Rca { get; } = RcaCalculator.Calculate(player);
        public RoleScore BestRole { get; } = RoleScores.BestFitRole(player);
        public DateOnly? Date;
        public int? Floor;
        public PersonalityClassification? Personality;
    }
    static ConditionalWeakTable<PlayerRecord, Entry> entries = new();
    static Entry Get(PlayerRecord player) => entries.GetValue(player, p => new(p));
    public static int Rca(PlayerRecord player) => Get(player).Rca;
    public static RoleScore BestRole(PlayerRecord player) => Get(player).BestRole;
    public static PersonalityClassification Personality(PlayerRecord player)
    {
        var e = Get(player);
        lock (e)
        {
            if (e.Personality is null || e.Date != player.GameDate || e.Floor != RegenIds.Current)
            { e.Personality = PersonalityClassifier.Classify(player); e.Date = player.GameDate; e.Floor = RegenIds.Current; }
            return e.Personality;
        }
    }
    public static void Reset() => entries = new();
    public static void Warm(IEnumerable<PlayerRecord> players)
    {
        foreach (var player in players) { _ = Get(player); _ = Personality(player); }
    }
}
