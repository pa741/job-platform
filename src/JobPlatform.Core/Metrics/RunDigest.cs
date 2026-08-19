namespace JobPlatform.Core.Metrics;

/// <summary>
/// The metrics document written to Cosmos for one ingested blob.
/// Partitioned by <see cref="SearchTerm"/>; <see cref="Id"/> is derived from the blob path
/// so reprocessing the same blob upserts rather than duplicating.
/// </summary>
public sealed record RunDigest
{
    public required string Id { get; init; }
    public string Type => "run-digest";

    public required string SearchTerm { get; init; }
    public required string BlobPath { get; init; }
    public long BlobSizeBytes { get; init; }

    public required DateTimeOffset ScrapedAtUtc { get; init; }
    public required DateTimeOffset IngestedAtUtc { get; init; }
    public required string ScrapeDate { get; init; }
    public long DurationMs { get; init; }

    public required RunCounts Counts { get; init; }
    public required IReadOnlyDictionary<string, int> BySite { get; init; }
    public required IReadOnlyDictionary<string, int> ByJobType { get; init; }
    public required RemoteBreakdown Remote { get; init; }
    public required FreshnessBreakdown Freshness { get; init; }
    public required SalaryBreakdown Salary { get; init; }
    public required IReadOnlyList<NamedCount> TopCompanies { get; init; }
    public required IReadOnlyList<NamedCount> TopLocations { get; init; }
    public required IReadOnlyList<NamedCount> TitleKeywords { get; init; }
    public required LengthStats DescriptionLength { get; init; }

    /// <summary>Per-column non-empty ratio. A column falling to zero means the scraper
    /// silently degraded — this is the run's health check, not a curiosity.</summary>
    public required IReadOnlyDictionary<string, double> FieldFillRates { get; init; }
}

public sealed record RunCounts
{
    public int RowsInFile { get; init; }
    public int Parsed { get; init; }
    public int Invalid { get; init; }
    public int InFileDuplicates { get; init; }
    /// <summary>Postings whose content hash collides with another posting in the same run
    /// (the same job cross-posted to more than one board).</summary>
    public int CrossSiteDuplicates { get; init; }
    public int New { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
}

public sealed record RemoteBreakdown(int Remote, int OnSite, double RemoteShare);

public sealed record FreshnessBreakdown
{
    public int WithDatePosted { get; init; }
    public int MissingDatePosted { get; init; }
    public double Coverage { get; init; }
    public int PostedToday { get; init; }
    public int OlderThanSevenDays { get; init; }
    public double? MedianAgeDays { get; init; }
}

public sealed record SalaryBreakdown
{
    public int WithSalary { get; init; }
    public double Coverage { get; init; }
    public IReadOnlyDictionary<string, int> ByCurrency { get; init; } = new Dictionary<string, int>();
    public decimal? MinAnnual { get; init; }
    public decimal? MedianAnnual { get; init; }
    public decimal? MaxAnnual { get; init; }
}

public sealed record LengthStats(int P50, int P90, int Max);

public sealed record NamedCount(string Name, int Count);
