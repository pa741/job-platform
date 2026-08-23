using System.Globalization;
using System.Text.RegularExpressions;

namespace JobPlatform.Core.Enrichment;

/// <summary>Where the work happens, and how much of it is in an office.</summary>
public readonly record struct WorkArrangementResult(WorkArrangement Arrangement, int? HybridDaysInOffice);

/// <summary>
/// Recovers the three-way work arrangement that <c>is_remote</c> cannot express.
/// </summary>
/// <remarks>
/// Sources are consulted in descending order of authority and the first that answers wins:
/// the board's own work-mode field, then <c>is_remote</c> being true, then the location and
/// title, then the description.
///
/// <b><c>is_remote == false</c> is deliberately not treated as an answer.</b> On Indeed it is
/// computed by searching the description, location and attributes for the words "remote",
/// "work from home" and "wfh" — so false means those words were absent, not that the employer
/// said the role is on-site. Reading it as <see cref="WorkArrangement.OnSite"/> would
/// manufacture a stated policy out of silence for most of the corpus, and hybrid roles that
/// never use the word "remote" would be counted as fully office-based. On-site is asserted
/// only where the text asserts it.
/// </remarks>
public static class WorkArrangementClassifier
{
    private static readonly Regex RemotePattern = new(
        @"\b(?:fully\s+remote|100%\s+remote|remote[- ]first|remote[- ]only|work\s+from\s+home|wfh)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HybridPattern = new(
        @"\bhybrid\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OnSitePattern = new(
        @"\b(?:on[- ]?site|in[- ]?office|office[- ]based|fully\s+on[- ]?site|no\s+remote|"
        + @"not\s+a\s+remote\s+role|5\s+days\s+in\s+the\s+office)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// "3 days a week in the office", "in the office 3 days", "2 days on-site".
    /// </summary>
    /// <remarks>
    /// Digits only. Adverts do write "three days a week", but a word-number pattern also
    /// matches "three years" and "two weeks' notice" nearby often enough that the extra
    /// coverage costs more than it returns.
    /// </remarks>
    private static readonly Regex[] HybridDaysPatterns =
    [
        new(@"(?<days>[1-5])\s*days?\s*(?:a|per)\s*week\s*(?:in|at|from)\s*(?:the\s*)?office",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"(?:in|at)\s*(?:the\s*)?office\s*(?<days>[1-5])\s*days?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"(?<days>[1-5])\s*days?\s*(?:a|per)?\s*(?:week\s*)?(?:on[- ]?site|in[- ]?office)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
    ];

    public static WorkArrangementResult Classify(
        string? workFromHomeType,
        bool? isRemote,
        string? location,
        string? title,
        string? description)
    {
        var days = FindHybridDays(description);

        var stated = FromBoardField(workFromHomeType);

        if (stated is not null)
        {
            return new WorkArrangementResult(stated.Value, stated == WorkArrangement.Hybrid ? days : null);
        }

        if (isRemote == true)
        {
            return new WorkArrangementResult(WorkArrangement.Remote, null);
        }

        if (MentionsRemote(location) || MentionsRemote(title))
        {
            return new WorkArrangementResult(WorkArrangement.Remote, null);
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return new WorkArrangementResult(WorkArrangement.Unknown, null);
        }

        // Hybrid before remote: a description saying "hybrid - 2 days remote" is hybrid, and
        // hybrid postings routinely use the word "remote" to describe the other half of the
        // week. Testing remote first would swallow most of them.
        if (HybridPattern.IsMatch(description) || days is not null)
        {
            return new WorkArrangementResult(WorkArrangement.Hybrid, days);
        }

        if (RemotePattern.IsMatch(description))
        {
            return new WorkArrangementResult(WorkArrangement.Remote, null);
        }

        return OnSitePattern.IsMatch(description)
            ? new WorkArrangementResult(WorkArrangement.OnSite, null)
            : new WorkArrangementResult(WorkArrangement.Unknown, null);
    }

    private static WorkArrangement? FromBoardField(string? workFromHomeType)
        => workFromHomeType?.Trim().ToLowerInvariant() switch
        {
            "remote" => WorkArrangement.Remote,
            "hybrid" => WorkArrangement.Hybrid,
            "onsite" or "on-site" or "on site" or "office" => WorkArrangement.OnSite,
            _ => null,
        };

    private static bool MentionsRemote(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && text.Contains("remote", StringComparison.OrdinalIgnoreCase);

    private static int? FindHybridDays(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        foreach (var pattern in HybridDaysPatterns)
        {
            var match = pattern.Match(description);

            if (match.Success
                && int.TryParse(
                    match.Groups["days"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var days))
            {
                return days;
            }
        }

        return null;
    }
}
