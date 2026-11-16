using System.Globalization;
using Fm27Editor.Core.Nations;

namespace Fm27Editor.App;

/// <summary>Nation screen labels, groups (FMRTE 27's Information / National Teams order) and input ranges.</summary>
public static class NationLabels
{
    public static readonly (long Code, string Label)[] DevelopmentOptions =
    [
        (0, "Not Set"),
        (1, "Developed State"),
        (2, "Developing State"),
        (3, "Lower Developed State"),
    ];

    public static readonly (long Code, string Label)[] ImportanceOptions =
    [
        (1, "Very Important"),
        (2, "Important"),
        (3, "Unimportant"),
        (4, "No One Cares"),
    ];

    public static readonly (long Code, string Label)[] GainNationalityTypeOptions =
    [
        (1, "Continuous"),
        (2, "Accumulative"),
    ];

    public static string DevelopmentName(long code) => code switch
    {
        0 => Loc.S("Not Set"),
        1 => Loc.S("Developed State"),
        2 => Loc.S("Developing State"),
        3 => Loc.S("Lower Developed State"),
        _ => Loc.F("Level {0}", code),
    };
    public static string ImportanceName(long code) => code switch { 1 => Loc.S("Very Important"), 2 => Loc.S("Important"), 3 => Loc.S("Unimportant"), 4 => Loc.S("No One Cares"), _ => Loc.F("Level {0}", code) };
    public static string GainNationalityTypeName(long code) => code switch { 1 => Loc.S("Continuous"), 2 => Loc.S("Accumulative"), _ => Loc.F("Level {0}", code) };

    public static string FormatValue(NationField field, long value) => field switch
    {
        NationField.Development => DevelopmentName(value),
        NationField.Importance => ImportanceName(value),
        NationField.GainNationalityType => GainNationalityTypeName(value),
        NationField.AllowMultipleAssistantManagers or NationField.DoesNotAllowDualNationality => value != 0 ? Loc.S("Yes") : Loc.S("No"),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    public static readonly (string Title, (NationField Field, string Label)[] Items)[] Groups =
    [
        ("Overview", [
            (NationField.Development, "Development"), (NationField.EconomicFactor, "Economic Factor"),
            (NationField.YearsToGainNationality, "Years To Gain Nationality"), (NationField.GainNationalityType, "Gain Nationality Type"),
            (NationField.DoesNotAllowDualNationality, "Does Not Allow Dual Nationality")]),
        ("National Team", [
            (NationField.Importance, "Importance"), (NationField.AllowMultipleAssistantManagers, "Allow Multiple Assistant Managers"),
            (NationField.FaFinancialPower, "FA Financial Power"), (NationField.FifaPosition, "FIFA Position"),
            (NationField.FifaRankingPoints, "FIFA Ranking Points"), (NationField.YearAffiliatedToFifa, "Year Affiliated to FIFA"),
            (NationField.YearAffiliatedToFederation, "Year Affiliated to Federation"), (NationField.YearAssociationFormed, "Year Association Formed"),
            (NationField.ForeignManagerLikelihood, "Foreign Manager Likelihood"), (NationField.FaPatience, "FA Patience"),
            (NationField.YouthRating, "Youth Rating"), (NationField.MaxYouthAge, "Max. Youth Age")]),
        ("Attendance", [
            (NationField.AverageAttendance, "Average"), (NationField.MinimumAttendance, "Minimum"), (NationField.MaximumAttendance, "Maximum")]),
        ("Women's National Team", [
            (NationField.WomenFaFinancialPower, "FA Financial Power"), (NationField.WomenFifaPosition, "FIFA Position"),
            (NationField.WomenFifaRankingPoints, "FIFA Ranking Points"), (NationField.WomenYearAffiliatedToFifa, "Year Affiliated to FIFA"),
            (NationField.WomenYearAffiliatedToFederation, "Year Affiliated to Federation"), (NationField.WomenYearAssociationFormed, "Year Association Formed"),
            (NationField.WomenForeignManagerLikelihood, "Foreign Manager Likelihood"), (NationField.WomenFaPatience, "FA Patience"),
            (NationField.WomenYouthRating, "Youth Rating"), (NationField.WomenMaxYouthAge, "Max. Youth Age")]),
        ("Women's Attendance", [
            (NationField.WomenAverageAttendance, "Average"), (NationField.WomenMinimumAttendance, "Minimum"), (NationField.WomenMaximumAttendance, "Maximum")]),
    ];

    public static (long Min, long Max) Range(NationField f) => NationFieldInfo.Range(f);

    /// <summary>Parses every pending change; the first invalid one is returned as an error and nothing is applied.</summary>
    public static bool TryParse(IReadOnlyDictionary<NationField, string> changes, out Dictionary<NationField, long> values, out string? error)
    {
        values = [];
        error = null;
        foreach (var (f, text) in changes)
        {
            var (min, max) = Range(f);
            if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) || v < min || v > max)
            {
                error = Loc.F("{0}: enter a whole number {1}–{2}", Loc.S(f.ToString()), min, max);
                values = [];
                return false;
            }
            values[f] = v;
        }
        return true;
    }
}
