using JobPlatform.Core.Metrics;

namespace JobPlatform.Api.Features.Metrics;

/// <summary>
/// The headline numbers a dashboard shows above the fold, assembled from the latest run
/// digest and the recent rollup series.
/// </summary>
/// <remarks>
/// Exists so the frontend makes one call rather than three and then does arithmetic. The
/// deltas in particular belong here: computing "new postings vs. yesterday" client-side means
/// every client reimplements the same off-by-one against a series that can have gaps when the
/// scraper did not run.
/// </remarks>
public sealed record MetricsSummary
{
    public required string SearchTerm { get; init; }

    /// <summary>Null when nothing has ever been ingested for this term.</summary>
    public DateTimeOffset? LastScrapedAtUtc { get; init; }
    public DateTimeOffset? LastIngestedAtUtc { get; init; }
    public string? LastScrapeDate { get; init; }

    public int PostingsInLastRun { get; init; }
    public int NewInLastRun { get; init; }
    public int UpdatedInLastRun { get; init; }
    public int InvalidInLastRun { get; init; }

    public int CumulativePostings { get; init; }

    /// <summary>New postings today minus new postings the previous day with data.
    /// Null when there is no previous day to compare against.</summary>
    public int? NewPostingsDelta { get; init; }

    public double RemoteShare { get; init; }
    public double SalaryCoverage { get; init; }
    public double? MedianAgeDays { get; init; }

    public IReadOnlyDictionary<string, int> BySite { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<NamedCount> TopCompanies { get; init; } = [];
    public IReadOnlyList<NamedCount> TitleKeywords { get; init; } = [];

    /// <summary>
    /// The structured view of the last run: what the postings actually ask for.
    /// </summary>
    /// <remarks>
    /// Served from Cosmos with everything else on this contract. The same questions could be
    /// answered from SQL against live rows, and doing so would put a polling dashboard on a
    /// database billed by the second it spends awake - which is the one thing this API is
    /// most careful not to do.
    /// </remarks>
    public EnrichmentBreakdown Enrichment { get; init; } = new();

    /// <summary>How many days of rollup history back the series.</summary>
    public int DaysOfHistory { get; init; }
}

/// <summary>
/// The scraper's own health, derived from per-column fill rates.
/// </summary>
/// <remarks>
/// A first-class endpoint rather than a field inside the digest blob, because this is the
/// system's earliest warning. A column that silently falls to 0% means a job board changed
/// its markup and the scraper degraded without failing - nothing else in the pipeline reports
/// an error when that happens. Surfacing it separately is what lets a dashboard alert on it.
/// </remarks>
public sealed record ScraperHealth
{
    public required string SearchTerm { get; init; }
    public DateTimeOffset? LastScrapedAtUtc { get; init; }

    /// <summary><c>healthy</c>, <c>degraded</c>, or <c>unknown</c> when there is no data.</summary>
    public required string Status { get; init; }

    /// <summary>Columns populated in no row of the last run, each with when it last was.</summary>
    public IReadOnlyList<EmptyColumn> EmptyColumns { get; init; } = [];

    /// <summary>Columns populated in fewer than a quarter of rows.</summary>
    public IReadOnlyList<FieldFill> SparseColumns { get; init; } = [];

    public IReadOnlyDictionary<string, double> FieldFillRates { get; init; } =
        new Dictionary<string, double>();

    public int RowsInLastRun { get; init; }
    public int InvalidInLastRun { get; init; }
    public IReadOnlyDictionary<string, int> BySite { get; init; } = new Dictionary<string, int>();
}

public sealed record FieldFill(string Field, double FillRate);

/// <summary>
/// A column empty in every row of the last run, and when it was last populated.
/// </summary>
/// <remarks>
/// Two faults present the same symptom and need opposite responses, which is why this is a
/// record rather than a bare column name. A column that was filled and is now empty is a
/// board changing its markup - the regression this endpoint exists to catch. A column that
/// has never been filled is one the scraper does not emit yet, and alerting on it is how a
/// warning becomes something people learn to scroll past.
///
/// <para>
/// <see cref="LastFilledUtc"/> is null when no run <em>within the history window</em> had it
/// populated - not "never", which the digests cannot prove. A column that regressed further
/// back than the window therefore reads as never-shipped; at ninety runs that is a column
/// which has been empty for a quarter of a year, and no longer the early warning this is for.
/// </para>
/// </remarks>
public sealed record EmptyColumn(string Field, DateTimeOffset? LastFilledUtc, double? LastFillRate);
