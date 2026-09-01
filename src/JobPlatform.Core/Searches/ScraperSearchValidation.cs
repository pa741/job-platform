using JobPlatform.Core.Enrichment;

namespace JobPlatform.Core.Searches;

/// <summary>
/// What a configured search must satisfy before it is allowed to cost a scrape.
/// </summary>
/// <remarks>
/// Pure and Azure-free, like <c>MetricsCalculator</c> and <c>MatchScorer</c>, so every bound is
/// assertable exactly rather than through an HTTP status.
///
/// <b>Every bound here is a cost or a correctness bound, not a taste one.</b> A search runs on
/// somebody else's schedule through paid residential bandwidth, and the searches run one after
/// another - so a run costs the sum of them. An unbounded <c>results_wanted</c> is not a big
/// number, it is a scheduled run that does not finish.
/// </remarks>
public static class ScraperSearchValidation
{
    public const int MaxNameLength = 80;
    public const int MaxSearchTermLength = 200;
    public const int MaxLocationLength = 200;

    /// <summary>30 days. Beyond it a board returns its whole index and the run stops finishing.</summary>
    public const int MaxHoursOld = 720;

    /// <summary>
    /// Matches the ceiling the scraper's own config documents.
    /// </summary>
    /// <remarks>
    /// With <c>linkedin_fetch_description</c> on, LinkedIn spends roughly one extra request per
    /// posting, so this multiplies across every search in the run rather than bounding one.
    /// </remarks>
    public const int MaxResultsWanted = 1000;

    /// <summary>
    /// Freehire facets a search may set by hand.
    /// </summary>
    /// <remarks>
    /// The live vocabulary is at <c>https://freehire.me/api/v1/jobs/facets</c> and this is a
    /// deliberately conservative subset of it. Adding a key is one line here plus a test case;
    /// the reason it is bounded at all is that these strings become dictionary keys inside a
    /// parameter the scraper forwards, and an open key set is an open parameter set by another
    /// name.
    /// </remarks>
    public static IReadOnlySet<string> FreehireFilterKeys { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "seniority",
            "skills",
            "area",
            "employment_type",
            "source_board",
        };

    /// <summary>
    /// Every problem with <paramref name="search"/>, or an empty list.
    /// </summary>
    /// <remarks>
    /// All of them rather than the first: a form with four empty required fields should say so
    /// once, not four saves in a row.
    /// </remarks>
    public static IReadOnlyList<string> Validate(ScraperSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(search.Name))
        {
            problems.Add("A search needs a name.");
        }
        else if (search.Name.Length > MaxNameLength)
        {
            problems.Add($"A name may be at most {MaxNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(search.SearchTerm))
        {
            problems.Add("A search needs a search term.");
        }
        else if (search.SearchTerm.Length > MaxSearchTermLength)
        {
            problems.Add($"A search term may be at most {MaxSearchTermLength} characters.");
        }

        if (search.Sites.Count == 0)
        {
            problems.Add("A search needs at least one job board.");
        }
        else if (search.Sites.Distinct().Count() != search.Sites.Count)
        {
            problems.Add("A job board may only be named once.");
        }

        if (search.Location is { Length: > MaxLocationLength })
        {
            problems.Add($"A location may be at most {MaxLocationLength} characters.");
        }

        if (search.HoursOld is { } hours and (< 1 or > MaxHoursOld))
        {
            problems.Add($"Hours old must be between 1 and {MaxHoursOld}; got {hours}.");
        }

        if (search.ResultsWanted is { } wanted and (< 1 or > MaxResultsWanted))
        {
            problems.Add($"Results wanted must be between 1 and {MaxResultsWanted}; got {wanted}.");
        }

        // One canonical value, not a set. The posting side reads a multi-valued column off a
        // board; this is a filter somebody picked from a list, and forwarding "fulltime,
        // contract" as one string would ask the boards for a job type none of them has.
        if (search.JobType is { } jobType
            && !string.IsNullOrWhiteSpace(jobType)
            && JobTypeNormalizer.Normalize(jobType).Count != 1)
        {
            problems.Add($"'{jobType}' is not a job type this platform recognises.");
        }

        foreach (var key in search.FreehireFilters.Keys)
        {
            if (!FreehireFilterKeys.Contains(key))
            {
                // Named rather than dropped. A silently discarded filter returns a plausible
                // page of the wrong postings, which is the failure mode the posting endpoints
                // already refuse for the same reason.
                problems.Add(
                    $"'{key}' is not a freehire filter this platform forwards. " +
                    $"Allowed: {string.Join(", ", FreehireFilterKeys.Order(StringComparer.Ordinal))}.");
            }
        }

        return problems;
    }
}
