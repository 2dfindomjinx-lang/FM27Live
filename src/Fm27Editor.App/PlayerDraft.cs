using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

// Each write uses the original snapshot, so a game-side change to a pending field
// remains guarded even after another field has been saved and reloaded.
public sealed class PlayerDraft(PlayerRecord original)
{
    readonly Dictionary<FieldDescriptor<PlayerRecord>, int> changes = [];
    public PlayerRecord Original { get; } = original;
    public int Count => changes.Count;
    public event Action? Changed;
    public int Value(FieldDescriptor<PlayerRecord> field) => changes.TryGetValue(field, out var value) ? value : field.Read(Original);
    public void Stage(FieldDescriptor<PlayerRecord> field, int value)
    {
        if (field.Write is null || value < field.Min || value > field.Max) throw new ArgumentOutOfRangeException(nameof(value));
        int previous = Value(field);
        if (value == field.Read(Original)) changes.Remove(field); else changes[field] = value;
        if (previous != value) Changed?.Invoke();
    }

    public int? RecommendedAbility()
    {
        if (Original.AttributeScale != 5) return null;
        var attributes = Original.RawAttributes.ToArray();
        var positions = Original.Positions.ToArray();
        int ca = Original.CurrentAbility;
        foreach (var (field, value) in changes)
        {
            if (field.Id == "CA") ca = value;
            else if (field.Group == "Positions" && Enum.TryParse<PlayerPosition>(field.Id, out var position))
                positions[(int)position] = (byte)value;
            else if (field.Kind == FieldKind.Rating20 && field.Group != "Personality" && Enum.TryParse<PlayerAttribute>(field.Id, out var attribute))
                attributes[(int)attribute] = AttributeScaling.ToRaw(value, Original.AttributeScale);
        }
        return Fm27RcaCalculator.Calculate(attributes, positions, ca);
    }
    public PlayerRecord Save(IEditService edits)
    {
        var latest = Original;
        foreach (var (field, value) in changes.ToArray())
        {
            latest = edits.Apply(Original, field, value);
            changes.Remove(field);
        }
        return latest;
    }
    public PlayerDraft Rebase(PlayerRecord fresh)
    {
        var rebased = new PlayerDraft(fresh);
        foreach (var (field, value) in changes) rebased.Stage(field, value);
        return rebased;
    }
}
