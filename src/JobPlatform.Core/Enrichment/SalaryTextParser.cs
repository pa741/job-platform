using System.Globalization;
using System.Text.RegularExpressions;

namespace JobPlatform.Core.Enrichment;

/// <summary>A salary recovered from prose, already annualised.</summary>
/// <param name="Min">Null where the text gave only a ceiling ("up to £80,000").</param>
/// <param name="Max">Null where the text gave only a floor ("from £65,000").</param>
/// <param name="Currency">ISO code, from the symbol or code the text used.</param>
/// <param name="StatedInterval">
/// What the text said before annualisation — <c>yearly</c>, <c>daily</c>, <c>hourly</c>,
/// <c>monthly</c>, <c>weekly</c>. Worth keeping: a £600/day contract annualised to £156,000
/// is not the same offer as a £156,000 salary, and only this field can tell them apart.
/// </param>
public readonly record struct ParsedSalary(
    decimal? Min,
    decimal? Max,
    string Currency,
    string StatedInterval);

/// <summary>
/// Pulls a salary out of description text when the board left the salary columns empty.
/// </summary>
/// <remarks>
/// This exists because of a specific, measured hole: a real London run had <b>0%</b> salary
/// coverage. The upstream library does have a description-based extractor, but
/// <c>jobspy/__init__.py</c> gates it on <c>country_enum == Country.USA</c> and this
/// deployment scrapes the UK, so it has never once run. The fork fix and this class overlap
/// deliberately — the fork covers what a scraper can see, this covers what reaches us.
///
/// <b>Annualisation matches the library's multipliers exactly</b> (hourly x2080, weekly x52,
/// monthly x12, daily x260, from <c>jobspy/util.py</c>'s <c>convert_to_annual</c>). If the two
/// disagreed, a salary parsed here and a salary parsed there would be different measurements
/// wearing the same column name, and no query could tell which it had.
///
/// <b>Precision over recall, twice over.</b> A currency symbol or code is required — a bare
/// "65,000 - 85,000" could be pounds or euros and guessing from the posting's country would
/// invent data. And where the text states no period, the figure is only accepted if it is
/// large enough to be unambiguously annual; a bare "£450" is a day rate as often as anything
/// else, and there is no evidence available to settle it.
/// </remarks>
public static class SalaryTextParser
{
    /// <summary>Anything below this, with no period stated, is not safely an annual figure.</summary>
    private const decimal BareFigureAnnualFloor = 10_000m;

    private const decimal PlausibleAnnualMin = 5_000m;
    private const decimal PlausibleAnnualMax = 2_000_000m;

    /// <summary>From <c>jobspy/util.py</c> <c>convert_to_annual</c>. Keep in step with it.</summary>
    private static readonly Dictionary<string, decimal> AnnualMultipliers = new(StringComparer.Ordinal)
    {
        ["yearly"] = 1m,
        ["monthly"] = 12m,
        ["weekly"] = 52m,
        ["daily"] = 260m,
        ["hourly"] = 2080m,
    };

    private static readonly Dictionary<string, string> CurrencyBySymbol = new(StringComparer.OrdinalIgnoreCase)
    {
        ["£"] = "GBP",
        ["€"] = "EUR",
        ["$"] = "USD",
        ["GBP"] = "GBP",
        ["EUR"] = "EUR",
        ["USD"] = "USD",
    };

    /// <summary>
    /// A figure, optionally a range, anchored on a currency marker.
    /// </summary>
    /// <remarks>
    /// The currency has to lead. Anchoring on the number instead matches every "5 years",
    /// "24 hours" and "2026" in the text and then has to reject them, which is the same work
    /// done less reliably.
    /// </remarks>
    private static readonly Regex AmountPattern = new(
        """
        (?<qualifier>up\s+to|from|starting\s+(?:at|from)|circa|c\.)?\s*
        (?<cur>[£€$]|\b(?:GBP|EUR|USD)\b)\s*
        (?<min>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s*(?<mink>k\b)?
        (?:\s*(?:-|–|—|\bto\b|\band\b)\s*
           (?:[£€$]|\b(?:GBP|EUR|USD)\b)?\s*
           (?<max>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s*(?<maxk>k\b)?)?
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
            | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Period words, searched in a window after the figure. Ordered longest-intent first so
    /// "per annum" is not read as "annum" inside a shorter alternative.
    /// </summary>
    private static readonly (Regex Pattern, string Interval)[] IntervalPatterns =
    [
        (new Regex(@"\b(?:per\s+annum|p\.?a\.?|annually|annual|per\s+year|/\s*(?:yr|year)|yearly)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "yearly"),
        (new Regex(@"\b(?:per\s+day|day\s+rate|daily|/\s*day|pd\b|a\s+day)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "daily"),
        (new Regex(@"\b(?:per\s+hour|hourly|/\s*hr|/\s*hour|an\s+hour|ph\b)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "hourly"),
        (new Regex(@"\b(?:per\s+month|monthly|/\s*month|pcm\b|a\s+month)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "monthly"),
        (new Regex(@"\b(?:per\s+week|weekly|/\s*week|a\s+week)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "weekly"),
    ];

    /// <summary>How far past a figure to look for the period word it belongs to.</summary>
    private const int IntervalWindow = 30;

    /// <summary>
    /// Annualises a figure the board itself published, using the library's own multipliers.
    /// </summary>
    /// <remarks>
    /// The scraper is configured with <c>enforce_annual_salary: true</c>, so board figures
    /// usually arrive annual already and this is a no-op. Usually is not always: the flag only
    /// applies where the board stated an interval the library recognised, and a row that
    /// slipped through as a day rate would otherwise sit in the same column as an annual
    /// salary, two orders of magnitude apart, with nothing to mark it. Re-applying the
    /// conversion is idempotent for anything already annual and corrects anything that is not.
    ///
    /// Unknown or absent intervals are treated as annual rather than rejected, because that is
    /// what <c>enforce_annual_salary</c> has already assumed by the time the value reaches us.
    /// </remarks>
    public static decimal? Annualise(decimal? amount, string? statedInterval)
    {
        if (amount is null)
        {
            return null;
        }

        var interval = statedInterval?.Trim().ToLowerInvariant();

        var multiplier = interval is not null && AnnualMultipliers.TryGetValue(interval, out var found)
            ? found
            : 1m;

        var annual = amount.Value * multiplier;

        return IsPlausible(annual) ? decimal.Round(annual, 2) : null;
    }

    /// <summary>
    /// The first salary the text states, annualised, or null if it states none we can trust.
    /// </summary>
    /// <remarks>
    /// The first rather than the largest. A posting that names several figures leads with the
    /// one it is advertising; the later ones are usually a bonus, a pension percentage, or a
    /// second role in a combined ad.
    /// </remarks>
    public static ParsedSalary? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in AmountPattern.Matches(text))
        {
            var parsed = Interpret(match, text);

            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static ParsedSalary? Interpret(Match match, string text)
    {
        if (!CurrencyBySymbol.TryGetValue(match.Groups["cur"].Value.Trim(), out var currency))
        {
            return null;
        }

        var min = ToAmount(match.Groups["min"], match.Groups["mink"]);
        var max = ToAmount(match.Groups["max"], match.Groups["maxk"]);

        if (min is null)
        {
            return null;
        }

        var interval = FindInterval(text, match.Index + match.Length);

        if (interval is null)
        {
            // No period stated. Only a figure too large to be anything else is safe to read
            // as annual — see the class remarks.
            if (min < BareFigureAnnualFloor)
            {
                return null;
            }

            interval = "yearly";
        }

        var multiplier = AnnualMultipliers[interval];
        decimal? annualMin = min.Value * multiplier;
        var annualMax = max * multiplier;

        // "up to £80,000" is a ceiling, not a floor, and reading it as one would understate
        // every posting that phrases its range that way.
        var qualifier = match.Groups["qualifier"].Value.Trim();

        if (max is null && qualifier.StartsWith("up", StringComparison.OrdinalIgnoreCase))
        {
            (annualMin, annualMax) = (null, annualMin);
        }

        if (!IsPlausible(annualMin) || !IsPlausible(annualMax))
        {
            return null;
        }

        if (annualMin is not null && annualMax is not null && annualMin > annualMax)
        {
            return null;
        }

        return new ParsedSalary(
            annualMin is null ? null : decimal.Round(annualMin.Value, 2),
            annualMax is null ? null : decimal.Round(annualMax.Value, 2),
            currency,
            interval);
    }

    private static bool IsPlausible(decimal? annual)
        => annual is null || (annual >= PlausibleAnnualMin && annual <= PlausibleAnnualMax);

    private static decimal? ToAmount(Group figure, Group thousands)
    {
        if (!figure.Success
            || !decimal.TryParse(
                figure.Value.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        return thousands.Success ? value * 1000m : value;
    }

    private static string? FindInterval(string text, int from)
    {
        var length = Math.Min(IntervalWindow, text.Length - from);

        if (length <= 0)
        {
            return null;
        }

        var window = text.Substring(from, length);

        foreach (var (pattern, interval) in IntervalPatterns)
        {
            if (pattern.IsMatch(window))
            {
                return interval;
            }
        }

        return null;
    }
}
