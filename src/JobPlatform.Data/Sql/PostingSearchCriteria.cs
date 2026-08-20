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
