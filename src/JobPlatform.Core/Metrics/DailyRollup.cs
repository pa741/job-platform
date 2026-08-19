namespace JobPlatform.Core.Metrics;

/// <summary>
/// One document per search term per day, recomputed from SQL aggregates after every ingest.
/// Recomputing (rather than incrementing) means a replayed blob converges on the right
/// number instead of double-counting.
/// </summary>
public sealed record DailyRollup
{
    public required string Id { get; init; }
    public string Type => "daily-rollup";

    public required string SearchTerm { get; init; }
    public required string Date { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public int RunsIngested { get; init; }
    /// <summary>Postings seen at any point on this date.</summary>
    public int PostingsSeen { get; init; }
    /// <summary>Postings whose very first sighting was on this date.</summary>
    public int NewPostings { get; init; }
    /// <summary>Every distinct posting ever recorded for this search term, as of this date.</summary>
    public int CumulativePostings { get; init; }

    public IReadOnlyDictionary<string, int> BySite { get; init; } = new Dictionary<string, int>();
    public double RemoteShare { get; init; }
    public double SalaryCoverage { get; init; }
    public IReadOnlyList<NamedCount> TopCompanies { get; init; } = [];
}
