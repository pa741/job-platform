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

    /// <summary>
    /// What the enricher concluded, as opposed to what the scraper delivered.
    /// </summary>
    /// <remarks>
    /// Kept as its own section rather than folded in beside the raw counts, because the two
    /// answer different questions and mixing them is how a dashboard starts reporting on the
    /// enricher while appearing to report on the market. <c>SalaryCoverage</c> above is the
    /// board's own columns; <see cref="EnrichmentBreakdown.SalaryCoverage"/> is what is
    /// actually known once descriptions have been read, and on this corpus they differ by an
    /// order of magnitude.
    /// </remarks>
    public EnrichmentBreakdown Enrichment { get; init; } = new();
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

/// <summary>How the run's postings split across work arrangement.</summary>
/// <param name="Remote">Postings the board said are remote.</param>
/// <param name="OnSite">Postings the board said are not remote.</param>
/// <param name="NotStated">
/// Postings that said nothing. Not a rounding error: freehire returns null whenever it has no
/// work mode, and on Indeed the flag is computed by searching the text for "remote", so its
/// absence means the words were absent rather than that the employer said office-based.
/// </param>
/// <param name="RemoteShare">
/// Remote as a fraction of the postings that <b>stated</b> a mode, not of all postings.
/// Dividing by the whole population would let the share move because coverage changed, which
/// is the metric reporting on the scraper rather than on the market.
/// </param>
public sealed record RemoteBreakdown(int Remote, int OnSite, int NotStated, double RemoteShare);

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

/// <summary>The structured view of a run: what the postings actually ask for.</summary>
public sealed record EnrichmentBreakdown
{
    /// <summary>Keyed by the enum name. <c>Unknown</c> is included and is usually large.</summary>
    /// <remarks>
    /// Including Unknown is deliberate. Dropping it would make every share on this axis
    /// read against a denominator the reader cannot see, and "82% senior" is a very
    /// different claim from "82% of the 18% we could classify".
    /// </remarks>
    public IReadOnlyDictionary<string, int> BySeniority { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> ByWorkArrangement { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> ByRoleFamily { get; init; } = new Dictionary<string, int>();

    /// <summary>The concrete concepts most often asked for.</summary>
    public IReadOnlyList<NamedCount> TopConcepts { get; init; } = [];

    /// <summary>
    /// The same demand rolled up through the closure.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="TopConcepts"/>, not a summary of it. Individual
    /// tools scatter - twelve ways to say "we do cloud" - and the rollup is what shows the
    /// shape underneath. This is the one number that could not exist without the graph.
    /// </remarks>
    public IReadOnlyList<NamedCount> TopDomains { get; init; } = [];

    /// <summary>Share with a salary once descriptions have been read.</summary>
    public double SalaryCoverage { get; init; }

    /// <summary>Share whose salary came from prose rather than a salary field.</summary>
    public double SalaryFromTextShare { get; init; }

    public decimal? MedianAnnualSalary { get; init; }

    /// <summary>
    /// Surface forms seen and not resolved.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than hidden: it is the size of the vocabulary's blind spot, and the
    /// only reason it is knowable at all is that unresolved forms are recorded instead of
    /// dropped. A rising number here is the signal to extend the vocabulary.
    /// </remarks>
    public int UnresolvedMentions { get; init; }
}
