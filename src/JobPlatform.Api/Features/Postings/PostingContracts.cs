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

    public bool IsRemote { get; init; }
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

    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public int SeenCount { get; init; }
    public required string SearchTerm { get; init; }
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

public sealed record SearchTermResponse(
    string SearchTerm,
    int PostingCount,
    int RunCount,
    DateOnly? LastScrapeDate,
    DateTimeOffset? LastSeenUtc);
