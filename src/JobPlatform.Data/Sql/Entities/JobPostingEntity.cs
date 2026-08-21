namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// A posting as stored. Keyed by <see cref="SourceKey"/> (board + site-local id), so the
/// same job cross-posted to two boards is two rows — <see cref="ContentHash"/> is what
/// links them.
/// </summary>
public sealed class JobPostingEntity
{
    public long Id { get; set; }

    /// <summary>"{site}:{externalId}" — stable across runs, unique.</summary>
    public required string SourceKey { get; set; }

    public required string Site { get; set; }
    public required string ExternalId { get; set; }

    /// <summary>SHA-256 of normalised title|company|location, for cross-board matching.</summary>
    public required string ContentHash { get; set; }

    public required string Title { get; set; }
    public string? Company { get; set; }

    public string? LocationRaw { get; set; }
    public string? LocationCity { get; set; }
    public string? LocationRegion { get; set; }
    public string? LocationCountry { get; set; }

    public bool IsRemote { get; set; }
    public string? JobType { get; set; }
    public DateOnly? DatePosted { get; set; }

    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? Currency { get; set; }
    public string? SalaryInterval { get; set; }
    public string? SalarySource { get; set; }

    public string? JobLevel { get; set; }
    public string? JobFunction { get; set; }
    public string? CompanyIndustry { get; set; }

    public string? JobUrl { get; set; }
    public string? JobUrlDirect { get; set; }
    public string? CompanyUrl { get; set; }

    /// <summary>Full text. The API's CV-matching needs it; it is the bulk of the row.</summary>
    public string? Description { get; set; }
    public int DescriptionLength { get; set; }

    public string? CompanyNumEmployees { get; set; }
    public string? ExperienceRange { get; set; }

    /// <summary>freehire's synopsis. Null for every scraped board.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// freehire's read on whether the posting is a real, current opening.
    /// <see cref="FakeFreshness"/> stays nullable: false is a verdict, null is silence.
    /// </summary>
    public string? FreshnessClass { get; set; }

    public int? PostingAgeDays { get; set; }
    public int? RepostCount { get; set; }
    public bool? FakeFreshness { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Runs in which this posting appeared. Drives the "new today" metric.</summary>
    public int FirstSeenRunId { get; set; }
    public int LastSeenRunId { get; set; }
    public int SeenCount { get; set; }

    /// <summary>Search term that surfaced this posting; the Cosmos partition key mirrors it.</summary>
    public required string SearchTerm { get; set; }

    public ScrapeRun? FirstSeenRun { get; set; }
    public ScrapeRun? LastSeenRun { get; set; }
}
