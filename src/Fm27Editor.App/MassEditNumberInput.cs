using Avalonia.Controls;

namespace Fm27Editor.App;

/// <summary>The delta is signed; its useful range spans the field's full legal interval.</summary>
internal static class MassEditNumberInput
{
    public static void Configure(NumericUpDown input, bool changeBy, decimal minimum, decimal maximum)
    {
        decimal min = changeBy ? minimum - maximum : minimum;
        decimal max = changeBy ? maximum - minimum : maximum;
        decimal current = input.Value ?? 0;
        // Widen first so switching modes cannot temporarily clamp the value against incompatible bounds.
        input.Minimum = Math.Min(input.Minimum, min);
        input.Maximum = Math.Max(input.Maximum, max);
        input.Value = Math.Clamp(current, min, max);
        input.Minimum = min;
        input.Maximum = max;
    }
}
