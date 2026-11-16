namespace Fm27Editor.Core.Players;

/// <summary>Change-by saturates at field limits without overflowing; set-to remains strictly validated.</summary>
public static class MassEditValue
{
    public static long Resolve(MassEditMode mode, long before, long value, long minimum, long maximum)
    {
        if (!Enum.IsDefined(mode) || minimum > maximum) throw new ArgumentException("Invalid mass edit mode or range");
        if (mode == MassEditMode.Set)
        {
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value), value, $"Value must be between {minimum} and {maximum}");
            return value;
        }
        // Decimal exactly represents the sum of any two Int64 values, including opposing extreme values.
        return (long)Math.Clamp((decimal)before + value, minimum, maximum);
    }
}
