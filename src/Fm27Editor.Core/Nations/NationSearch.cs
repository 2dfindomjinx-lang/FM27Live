namespace Fm27Editor.Core.Nations;

/// <summary>Criteria for the Nation scope of Advanced Search; every set value must match.</summary>
public sealed record NationSearchCriteria
{
    public string? Name { get; init; }
    public ulong? ContinentAddress { get; init; }
    public ulong? RegionAddress { get; init; }
    public IReadOnlyDictionary<NationField, (long? Min, long? Max)> Ranges { get; init; } =
        new Dictionary<NationField, (long? Min, long? Max)>();
}

/// <summary>Filters <see cref="NationRecord"/> entries by name, continent, region and mapped nation fields.</summary>
public static class NationSearch
{
    public static bool Matches(
        NationRecord nation,
        NationSearchCriteria criteria,
        Func<NationField, long?>? readField = null,
        Func<ulong>? readContinent = null,
        Func<ulong?>? readRegion = null,
        Func<ulong, bool>? hasAgreementRegion = null)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Name) && !MatchesName(nation, criteria.Name.Trim()))
            return false;

        if (criteria.ContinentAddress is { } wantContinent &&
            (readContinent is null || readContinent() != wantContinent))
            return false;

        if (criteria.RegionAddress is { } wantRegion)
        {
            ulong? direct = readRegion?.Invoke();
            if (direct is { } r)
            {
                if (r != wantRegion) return false;
            }
            else if (hasAgreementRegion is null || !hasAgreementRegion(wantRegion))
            {
                return false;
            }
        }

        foreach (var (field, (min, max)) in criteria.Ranges)
        {
            if (min is null && max is null) continue;
            if (readField is null || readField(field) is not { } value) return false;
            if ((min is { } lo && value < lo) || (max is { } hi && value > hi)) return false;
        }

        return true;
    }

    public static IReadOnlyList<NationRecord> Filter(
        IEnumerable<NationRecord> nations,
        NationSearchCriteria criteria,
        NationFieldAccess? access = null,
        NationLinks? links = null,
        NationAgreements? agreements = null)
    {
        var matches = new List<NationRecord>();
        foreach (var nation in nations)
        {
            bool ok = Matches(
                nation,
                criteria,
                readField: access is null ? null : field => access.Read(nation.Address, field),
                readContinent: links is null ? null : () => links.ContinentPointer(nation.Address),
                readRegion: agreements is null ? null : () => agreements.RegionOf(nation.Address)?.Address,
                hasAgreementRegion: agreements is null ? null : wantRegion => HasAgreementRegion(nation.Address, wantRegion, agreements));
            if (ok) matches.Add(nation);
        }
        return matches;
    }

    static bool MatchesName(NationRecord nation, string query) =>
        nation.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (nation.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || (nation.ShortName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    static bool HasAgreementRegion(ulong nationAddress, ulong wantRegion, NationAgreements agreements)
    {
        foreach (int vector in new[] { NationAgreements.EuVector, NationAgreements.NonForeignVector })
        {
            if (agreements.Read(nationAddress, vector, []).Any(row => row.Kind == AgreementKind.Region && row.Target == wantRegion))
                return true;
        }
        return false;
    }
}
