using JobPlatform.Core.Enrichment;

namespace JobPlatform.Data.Sql;

public enum PostingSort
{
    /// <summary>Most recently seen by the scraper. The default: it is the freshest signal
    /// that a posting is still live, which <c>DatePosted</c> is not (40% coverage).</summary>
    LastSeen,
    FirstSeen,
    DatePosted,
    Salary,
    Title,
}

public sealed record PostingSearchCriteria
{
    public string? SearchTerm { get; init; }

    /// <summary>Free text over title and company.</summary>
    public string? Query { get; init; }

    public string? Site { get; init; }
    public string? Company { get; init; }
    public string? JobType { get; init; }
    public string? Country { get; init; }
    public string? City { get; init; }

    public bool? IsRemote { get; init; }
    public bool? HasSalary { get; init; }
    public decimal? MinSalary { get; init; }

    // --- the structured axes ------------------------------------------------

    /// <summary>
    /// A concept key, matched through the closure.
    /// </summary>
    /// <remarks>
    /// <c>skill.kubernetes</c> returns postings that named Kubernetes; <c>area.backend</c>
    /// returns everything that named anything under it, without the caller having to know
    /// what is under it. That is the whole point of materialising the closure - the query is
    /// the same shape either way.
    /// </remarks>
    public string? Concept { get; init; }

    /// <summary>Minimum inclusive, on the ordinal scale. 4 is Senior.</summary>
    public Seniority? MinSeniority { get; init; }

    public Seniority? MaxSeniority { get; init; }

    public RoleFamily? RoleFamily { get; init; }

    public WorkArrangement? WorkArrangement { get; init; }

    /// <summary>
    /// Salary on the annualised column rather than the board's raw one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MinSalary"/>, which filters what the scraper delivered. This
    /// one covers the postings whose salary was recovered from prose as well, and puts day
    /// rates on the same scale, so a threshold means the same thing for a contract and a
    /// permanent role.
    /// </remarks>
    public decimal? MinAnnualSalary { get; init; }

    /// <summary>
    /// Whether to include salaries recovered from description text. Default true.
    /// </summary>
    /// <remarks>
    /// A figure from prose is weaker evidence than a figure the employer typed into a salary
    /// field, and an analysis that wants only the latter has to be able to say so.
    /// </remarks>
    public bool IncludeTextSalary { get; init; } = true;

    public bool? RequiresSecurityClearance { get; init; }

    /// <summary><c>inside</c> or <c>outside</c>. UK contract market only.</summary>
    public string? Ir35 { get; init; }

    public DateOnly? PostedFrom { get; init; }
    public DateOnly? PostedTo { get; init; }
    public DateTimeOffset? FirstSeenFrom { get; init; }
    public DateTimeOffset? FirstSeenTo { get; init; }

    public PostingSort Sort { get; init; } = PostingSort.LastSeen;
    public bool Descending { get; init; } = true;

    public int Limit { get; init; } = 25;
    public int Offset { get; init; }

    /// <summary>
    /// Opt-in only. A total requires a second aggregate query against a database that bills
    /// by wall-clock second and may be asleep; most callers only need "is there more".
    /// </summary>
    public bool IncludeTotal { get; init; }
}

/// <summary>
/// A page of results. <paramref name="Total"/> is null unless the caller asked for it.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, bool HasMore, int? Total);
