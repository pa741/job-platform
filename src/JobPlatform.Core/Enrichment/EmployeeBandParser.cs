using System.Globalization;
using System.Text.RegularExpressions;

namespace JobPlatform.Core.Enrichment;

/// <summary>Headcount as numbers rather than a label.</summary>
/// <param name="Max">Null for an open-ended band — "10,000+".</param>
public readonly record struct EmployeeBand(int? Min, int? Max);

/// <summary>
/// Turns <c>"51-200"</c> and <c>"10,000+ employees"</c> into a range.
/// </summary>
/// <remarks>
/// Worth doing because the string form cannot be ordered. <c>"1,001-5,000"</c> sorts before
/// <c>"51-200"</c> lexically, so every "by company size" breakdown built on the raw column is
/// either wrong or needs a hand-maintained ordering table that drifts the moment a board
/// invents a new band. Two integers order themselves.
///
/// Boards do not agree on bands — LinkedIn publishes "11-50", Indeed "51 to 200", freehire a
/// bare "500". Parsing to numbers is what lets them be compared at all; bucketing afterwards
/// is a query's decision, not this parser's.
/// </remarks>
public static class EmployeeBandParser
{
    /// <summary>Beyond this a match is a misread — a revenue figure, a year, a postcode.</summary>
    private const int MaxPlausible = 10_000_000;

    /// <summary>"51-200", "1,001 to 5,000", "51 – 200".</summary>
    private static readonly Regex RangePattern = new(
        @"(?<min>\d{1,3}(?:,\d{3})+|\d+)\s*(?:-|–|—|\bto\b)\s*(?<max>\d{1,3}(?:,\d{3})+|\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>"10,000+", "over 500", "more than 1000".</summary>
    private static readonly Regex OpenPattern = new(
        @"(?:(?<qualifier>over|more\s+than|above)\s+)?(?<min>\d{1,3}(?:,\d{3})+|\d+)\s*\+?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>"fewer than 50", "up to 200", "under 10".</summary>
    private static readonly Regex CeilingPattern = new(
        @"(?:fewer\s+than|less\s+than|under|up\s+to|below)\s+(?<max>\d{1,3}(?:,\d{3})+|\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static EmployeeBand Parse(string? band)
    {
        if (string.IsNullOrWhiteSpace(band))
        {
            return default;
        }

        // Ceiling first: "fewer than 50" also matches the open pattern, which would read the
        // 50 as a floor and invert the meaning.
        var ceiling = CeilingPattern.Match(band);

        if (ceiling.Success)
        {
            var max = ToCount(ceiling.Groups["max"]);
            return max is null ? default : new EmployeeBand(null, max);
        }

        var range = RangePattern.Match(band);

        if (range.Success)
        {
            var min = ToCount(range.Groups["min"]);
            var max = ToCount(range.Groups["max"]);

            if (min is not null && max is not null && min <= max)
            {
                return new EmployeeBand(min, max);
            }
        }

        var open = OpenPattern.Match(band);

        return open.Success && ToCount(open.Groups["min"]) is { } floor
            ? new EmployeeBand(floor, null)
            : default;
    }

    private static int? ToCount(Group group)
        => group.Success
            && int.TryParse(
                group.Value.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count)
            && count is > 0 and <= MaxPlausible
                ? count
                : null;
}
