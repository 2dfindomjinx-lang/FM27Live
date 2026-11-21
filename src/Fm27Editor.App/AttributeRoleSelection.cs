using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

internal enum AttributeEmphasis { None, Key, Preferable, LowerIsBetter }

internal sealed class AttributeRoleSelection
{
    public PlayerPosition Position { get; set; }
    public PossessionPhase Phase { get; set; }
    public PhaseRole? Role { get; private set; }
    public event Action? Changed;
    public void Select(PhaseRole? role) { Role = role; Changed?.Invoke(); }
    public AttributeEmphasis Emphasis(string fieldId)
    {
        if (!Enum.TryParse<PlayerAttribute>(fieldId, out var attribute) || Role?.Attributes.FirstOrDefault(a => a.Attribute == attribute) is not { } entry) return AttributeEmphasis.None;
        return entry.LowerIsBetter ? AttributeEmphasis.LowerIsBetter : entry.Key ? AttributeEmphasis.Key : AttributeEmphasis.Preferable;
    }
}
