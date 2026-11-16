namespace Fm27Editor.Core.Players;

/// <summary>Storage units for the supported contract clauses. Unmapped clauses remain readable, not writable.</summary>
public static class ContractTermRules
{
    public static bool IsPercentage(short type) => type is 3 or 4 or 5 or 14 or 15 or 21;
    public static bool HasValue(short type) => type is not (7 or 11 or 12 or 22 or 29);
    public static bool IsMoney(short type) => HasValue(type) && !IsPercentage(type);
    public static bool HasParameter(short type) => type is 10 or 11 or 13 or 19 or 22 or 40 or 41;
    public static bool IsKnown(short type) => type is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 11 or 12 or 13 or 14 or 15 or 19 or 21 or 22 or 29 or 30 or 32 or 33 or 34 or 37 or 38 or 39 or 40 or 41;

    public static void Validate(short type, int value, short parameter)
    {
        if (!IsKnown(type)) throw new InvalidOperationException("This clause type is not verified for editing");
        // Captured Yearly Wage Rise stores the percentage in Value, with Parameter = -1.
        if (HasValue(type) ? value < 0 || (IsPercentage(type) && value > 100) : value != -1)
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid value for this clause type");
        if (HasParameter(type) ? parameter < 1 : parameter != -1)
            throw new ArgumentOutOfRangeException(nameof(parameter), "Invalid parameter for this clause type");
    }
}
