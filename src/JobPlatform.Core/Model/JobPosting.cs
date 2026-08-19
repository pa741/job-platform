namespace JobPlatform.Core.Model;

/// <summary>
/// A single job posting as produced by JobSpy, normalised into CLR types.
/// Nearly everything is optional: real scraper output routinely has whole columns
/// empty (a sample run had 0% salary coverage and only 40% <c>date_posted</c>).
/// </summary>
public sealed record JobPosting
{
    /// <summary>Site-local identifier, e.g. <c>in-f793bbe463f08be2</c>.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Originating board: <c>indeed</c>, <c>linkedin</c>, <c>google</c>, …</summary>
    public required string Site { get; init; }

    public required string Title { get; init; }
    public string? Company { get; init; }

    /// <summary>Raw location string, e.g. <c>"London, ENG, GB"</c>.</summary>
    public string? Location { get; init; }

    public DateOnly? DatePosted { get; init; }

    /// <summary>May carry several comma-separated values, e.g. <c>"parttime, fulltime"</c>.</summary>
    public string? JobType { get; init; }

    public bool IsRemote { get; init; }

    public string? SalarySource { get; init; }
    public string? SalaryInterval { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public string? Currency { get; init; }

    public string? JobLevel { get; init; }
    public string? JobFunction { get; init; }
    public string? CompanyIndustry { get; init; }

    public string? JobUrl { get; init; }
    public string? JobUrlDirect { get; init; }
    public string? CompanyUrl { get; init; }

    public string? Description { get; init; }

    public int DescriptionLength => Description?.Length ?? 0;

    /// <summary>Natural key within a source board. Stable across runs.</summary>
    public string SourceKey => $"{Site}:{ExternalId}";
}
