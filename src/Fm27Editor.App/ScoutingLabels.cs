using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

internal static class ScoutingLabels
{
    public static string Role(string english)
    {
        int at = english.LastIndexOf(" (", StringComparison.Ordinal);
        if (at < 0 || !english.EndsWith(')')) return Loc.S(english);
        return $"{Loc.S(english[..at])} ({Loc.S(english[(at + 2)..^1])})";
    }
    public static string Attribute(PlayerAttribute attribute) =>
        Loc.Attribute(PlayerFields.All.FirstOrDefault(f => f.Id == attribute.ToString())?.Label ?? ContractLabels.Spaced(attribute.ToString()));
}
