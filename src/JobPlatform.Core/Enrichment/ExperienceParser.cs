using System.Globalization;
using System.Text.RegularExpressions;

namespace JobPlatform.Core.Enrichment;

/// <summary>Years of experience a posting asks for.</summary>
public readonly record struct ExperienceRange(int? Min, int? Max);

/// <summary>
/// Turns "3+ Yrs" and "5 to 8 years of experience" into numbers.
/// </summary>
/// <remarks>
/// The board field is preferred where there is one, but it is a string even when the source
/// had a number: freehire's API returns <c>experience_years_min</c> as an integer and the
/// adapter renders it to <c>"3+ Yrs"</c> on the way out. Parsing it back is lossless enough
/// and costs nothing, and it means one code path serves the boards that publish a range,
/// the boards that publish a floor, and the boards that publish nothing at all.
///
/// Where a description names several thresholds — "5+ years engineering, 2+ years with
/// Kubernetes" — the largest floor wins. An advert listing several is asking for all of
/// them, so the binding requirement is the highest one; taking the first would make the
/// answer depend on sentence order.
/// </remarks>
public static class ExperienceParser
{
    /// <summary>Beyond this a match is a misread — a year, a headcount, a salary.</summary>
    private const int MaxPlausibleYears = 40;

    /// <summary>
    /// "3-5 years", "3 to 5 years". Tried before the single-figure pattern, which would
    /// otherwise match the "3" and stop.
    /// </summary>
    private static readonly Regex RangePattern = new(
        @"(?<min>\d{1,2})\s*(?:-|–|—|\bto\b)\s*(?<max>\d{1,2})\s*\+?\s*(?:years?|yrs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// "3+ years", "minimum 3 years", "at least 3 years", "3 years".
    /// </summary>
    /// <remarks>
    /// A trailing <c>+</c>, or a leading "minimum"/"at least", both mean the same thing and
    /// are both optional, because plenty of adverts write a bare "5 years of experience" and
    /// mean a floor rather than an exact figure. Everything here is read as a floor for that
    /// reason; only <see cref="RangePattern"/> produces a ceiling.
    /// </remarks>
    private static readonly Regex FloorPattern = new(
        @"(?:(?<qualifier>minimum(?:\s+of)?|at\s+least|over|more\s+than)\s+)?"
        + @"(?<min>\d{1,2})\s*\+?\s*(?:years?|yrs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The board's stated range if it has one, otherwise whatever the text says.</summary>
    public static ExperienceRange Parse(string? boardExperienceRange, string? description)
    {
        var fromBoard = ParseText(boardExperienceRange);

        return fromBoard.Min is not null || fromBoard.Max is not null
            ? fromBoard
            : ParseText(description);
    }

    public static ExperienceRange ParseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        int? bestMin = null;
        int? bestMax = null;

        // Spans the range pass already accounted for. Without this the floor pass re-reads
        // the upper figure of a range it has just consumed - "5-8 years" matches the floor
        // pattern at "8 years", 8 beats the 5, and the answer comes back as a floor of 8.
        // The bug is invisible in the output: an eight-year floor is perfectly plausible.
        var consumed = new List<(int Start, int End)>();

        foreach (Match match in RangePattern.Matches(text))
        {
            var min = ToYears(match.Groups["min"]);
            var max = ToYears(match.Groups["max"]);

            if (min is null || max is null || min > max)
            {
                continue;
            }

            consumed.Add((match.Index, match.Index + match.Length));

            if (bestMin is null || min > bestMin)
            {
                bestMin = min;
                bestMax = max;
            }
        }

        foreach (Match match in FloorPattern.Matches(text))
        {
            if (Overlaps(consumed, match))
            {
                continue;
            }

            var min = ToYears(match.Groups["min"]);

            if (min is not null && (bestMin is null || min > bestMin))
            {
                bestMin = min;

                // A higher floor invalidates a ceiling that came from a different sentence.
                if (bestMax is not null && bestMax < min)
                {
                    bestMax = null;
                }
            }
        }

        return new ExperienceRange(bestMin, bestMax);
    }

    private static bool Overlaps(List<(int Start, int End)> spans, Match match)
    {
        foreach (var (start, end) in spans)
        {
            if (match.Index < end && start < match.Index + match.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static int? ToYears(Group group)
        => group.Success
            && int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var years)
            && years is >= 0 and <= MaxPlausibleYears
                ? years
                : null;
}
