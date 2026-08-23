namespace JobPlatform.Api.Features.Postings;

/// <summary>
/// A posting in a list response.
/// </summary>
/// <remarks>
/// Note what is absent: <c>Description</c>. It is unbounded <c>nvarchar(max)</c> and is the
/// bulk of a row, so including it here would turn a 100-row page into megabytes. The full
/// text is available from the detail endpoint, which returns one posting. This is the reason
/// contracts exist separately from entities rather than being ceremony.
/// </remarks>
public sealed record PostingSummary
{
    public required long Id { get; init; }
    public required string SourceKey { get; init; }
    public required string Site { get; init; }
    public required string Title { get; init; }
    public string? Company { get; init; }

    public string? Location { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }

    /// <summary>Null where the board said nothing, which is most of the corpus.</summary>
    public bool? IsRemote { get; init; }
    public string? JobType { get; init; }
    public DateOnly? DatePosted { get; init; }

    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public string? Currency { get; init; }
    public string? SalaryInterval { get; init; }

    public string? JobUrl { get; init; }

    /// <summary>Length of the description without the description itself, so a client can
    /// tell a substantive posting from a stub before fetching it.</summary>
    public int DescriptionLength { get; init; }

    /// <summary>
    /// Whether the posting is a real, current opening — <c>fresh</c>, <c>stale</c> or
    /// <c>likely-evergreen</c>. Only freehire supplies these; null on every scraped board,
    /// which is why they sit in the list contract: they are triage, not detail.
    /// </summary>
    public string? FreshnessClass { get; init; }

    public int? PostingAgeDays { get; init; }

    /// <summary>How many times the role has been reposted.</summary>
    public int? RepostCount { get; init; }

    /// <summary>
    /// True when the stated posting date looks refreshed rather than real. Null means
    /// nobody checked, which is not the same as false.
    /// </summary>
    public bool? FakeFreshness { get; init; }

    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public int SeenCount { get; init; }

    /// <summary>
    /// Every configured search that turned this posting up, not just the one being viewed.
    /// A posting can match several, and a single value here would have to pick one.
    /// </summary>
    public required IReadOnlyList<string> SearchTerms { get; init; }
}

/// <summary>One posting in full. The only contract carrying the description.</summary>
public sealed record PostingDetail
{
    public required PostingSummary Summary { get; init; }
    public string? Description { get; init; }
    public string? JobUrlDirect { get; init; }
    public string? CompanyUrl { get; init; }
    public string? JobLevel { get; init; }
    public string? JobFunction { get; init; }
    public string? CompanyIndustry { get; init; }
    public string? SalarySource { get; init; }

    /// <summary>
    /// freehire's one or two sentence synopsis. Named Synopsis rather than Summary
    /// because <see cref="Summary"/> on this record is already the list contract.
    /// </summary>
    public string? Synopsis { get; init; }

    public string? ExperienceRange { get; init; }
    public string? CompanyNumEmployees { get; init; }

    public required string ContentHash { get; init; }
    public int FirstSeenRunId { get; init; }
    public int LastSeenRunId { get; init; }
}

public sealed record PageResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required bool HasMore { get; init; }
    public int? Total { get; init; }
    public required int Limit { get; init; }
    public required int Offset { get; init; }
}

public sealed record NamedCount(string Name, int Count);

public sealed record FacetsResponse
{
    public string? SearchTerm { get; init; }
    public int Total { get; init; }
    public int RemoteCount { get; init; }
    public int WithSalaryCount { get; init; }
    public DateOnly? EarliestDatePosted { get; init; }
    public DateOnly? LatestDatePosted { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public IReadOnlyList<NamedCount> Sites { get; init; } = [];
    public IReadOnlyList<NamedCount> JobTypes { get; init; } = [];
    public IReadOnlyList<NamedCount> Countries { get; init; } = [];
    public IReadOnlyList<NamedCount> Cities { get; init; } = [];
    public IReadOnlyList<NamedCount> Companies { get; init; } = [];
}

/// <summary>
/// One search term the platform holds data for.
/// </summary>
/// <remarks>
/// Sourced from the latest daily rollup in Cosmos rather than from SQL. Clients fetch this
/// before they can fetch anything else, so it must not depend on a database that spends most
/// of the day paused - see the endpoint for the failure that caused.
/// </remarks>
public sealed record SearchTermResponse(
    string SearchTerm,
    /// <summary>Every distinct posting recorded for this term, as of the latest rollup.</summary>
    int PostingCount,
    string? LastScrapeDate,
    DateTimeOffset? UpdatedAtUtc);
