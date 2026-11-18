namespace Fm27Editor.App;

/// <summary>Keep the FM26-derived implementation for recalibration, but do not expose it as FM27 data.
/// User IGE observations: Mbappe 192 vs 185 here; Bellingham 187 vs 194 here. FM27 also removed roles such as Enganche.</summary>
internal static class LegacyScoutingAvailability
{
    public static bool Enabled => false;
    public static bool IsLegacyFilter(string key) => key is "RCA" or "BestRoleScore" or "ProgressRate" or "CoreMental";
}
