using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// The read side. Separate from <see cref="JobPostingRepository"/>, which owns ingestion's
/// writes, because the two have opposite constraints: the writer batches everything into two
/// round trips per run, the reader serves many small queries and must never keep a connection
/// open longer than it needs.
/// </summary>
/// <remarks>
/// Every query is <c>AsNoTracking</c> and written to land on an existing index (see
/// <see cref="JobsDbContext"/>). The database is serverless and billed on wall-clock time
/// online, so a query that degrades to a scan does not merely run slowly - it holds the
/// database awake and spends the monthly grant.
/// </remarks>
public sealed class JobPostingQueryRepository(JobsDbContext db)
{
    /// <summary>Hard ceiling regardless of what a caller asks for.</summary>
    public const int MaxLimit = 100;

    public async Task<PagedResult<JobPostingEntity>> SearchAsync(
        PostingSearchCriteria criteria, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var limit = Math.Clamp(criteria.Limit, 1, MaxLimit);
        var query = Filter(db.JobPostings.AsNoTracking().Include(p => p.SearchTerms), criteria);

        int? total = criteria.IncludeTotal ? await query.CountAsync(ct) : null;

        // limit + 1 rather than a COUNT: one extra row answers "is there more" for the cost
        // of a row, where a count is a second aggregate over the whole filtered set.
        var rows = await Order(query, criteria)
            .Skip(criteria.Offset)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;

        return new PagedResult<JobPostingEntity>(
            hasMore ? rows.Take(limit).ToList() : rows,
            hasMore,
            total);
    }

    public Task<JobPostingEntity?> GetByIdAsync(long id, CancellationToken ct = default)
        => db.JobPostings.AsNoTracking()
            .Include(p => p.SearchTerms)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<JobPostingEntity?> GetBySourceKeyAsync(string sourceKey, CancellationToken ct = default)
        => db.JobPostings.AsNoTracking()
            .Include(p => p.SearchTerms)
            .FirstOrDefaultAsync(p => p.SourceKey == sourceKey, ct);

    /// <summary>
    /// The filter vocabulary a UI needs to build its controls, in one round trip.
    /// </summary>
    /// <remarks>
    /// Deliberately one method rather than five endpoints: five would wake the database five
    /// times. Cached hard by the API - this changes once a day at most.
    /// </remarks>
    public async Task<PostingFacets> GetFacetsAsync(string? searchTerm, CancellationToken ct = default)
    {
        var query = db.JobPostings.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(p => p.SearchTerms.Any(l => l.SearchTerm == searchTerm));
        }

        var sites = await CountByAsync(query, p => p.Site, take: null, ct);

        var jobTypes = await CountByAsync(
            query.Where(p => p.JobType != null), p => p.JobType!, take: 25, ct);

        var countries = await CountByAsync(
            query.Where(p => p.LocationCountry != null), p => p.LocationCountry!, take: 25, ct);

        var cities = await CountByAsync(
            query.Where(p => p.LocationCity != null), p => p.LocationCity!, take: 50, ct);

        var companies = await CountByAsync(
            query.Where(p => p.Company != null), p => p.Company!, take: 50, ct);

        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Remote = g.Count(p => p.IsRemote),
                WithSalary = g.Count(p => p.MinAmount != null || p.MaxAmount != null),
                EarliestPosted = g.Min(p => p.DatePosted),
                LatestPosted = g.Max(p => p.DatePosted),
                LastSeen = g.Max(p => p.LastSeenUtc),
            })
            .FirstOrDefaultAsync(ct);

        return new PostingFacets
        {
            SearchTerm = searchTerm,
            Total = totals?.Total ?? 0,
            RemoteCount = totals?.Remote ?? 0,
            WithSalaryCount = totals?.WithSalary ?? 0,
            EarliestDatePosted = totals?.EarliestPosted,
            LatestDatePosted = totals?.LatestPosted,
            LastSeenUtc = totals?.LastSeen,
            Sites = sites,
            JobTypes = jobTypes,
            Countries = countries,
            Cities = cities,
            Companies = companies,
        };
    }

    /// <summary>The axis everything else partitions on.</summary>
    public async Task<IReadOnlyList<SearchTermSummary>> ListSearchTermsAsync(CancellationToken ct = default)
    {
        // Grouped on the attributions, so a posting shared by two searches counts under
        // both. LastSeen is this search's, not the posting's newest across all of them.
        var postings = await db.JobPostingSearchTerms.AsNoTracking()
            .GroupBy(l => l.SearchTerm)
            .Select(g => new
            {
                SearchTerm = g.Key,
                Postings = g.Count(),
                LastSeen = g.Max(l => l.LastSeenUtc),
            })
            .ToListAsync(ct);

        var runs = await db.ScrapeRuns.AsNoTracking()
            .GroupBy(r => r.SearchTerm)
            .Select(g => new
            {
                SearchTerm = g.Key,
                Runs = g.Count(),
                LastScrape = g.Max(r => r.ScrapeDate),
            })
            .ToListAsync(ct);

        var runsByTerm = runs.ToDictionary(r => r.SearchTerm, StringComparer.OrdinalIgnoreCase);

        return postings
            .Select(p =>
            {
                runsByTerm.TryGetValue(p.SearchTerm, out var run);
                return new SearchTermSummary(
                    p.SearchTerm, p.Postings, run?.Runs ?? 0, run?.LastScrape, p.LastSeen);
            })
            .OrderByDescending(s => s.PostingCount)
            .ToList();
    }

    public async Task<PagedResult<ScrapeRun>> ListRunsAsync(
        string? searchTerm, int limit, int offset, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        var query = db.ScrapeRuns.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(r => r.SearchTerm == searchTerm);
        }

        var rows = await query
            .OrderByDescending(r => r.ScrapedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;

        return new PagedResult<ScrapeRun>(
            hasMore ? rows.Take(limit).ToList() : rows, hasMore, null);
    }

    public Task<ScrapeRun?> GetRunAsync(int id, CancellationToken ct = default)
        => db.ScrapeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>
    /// Groups and counts, newest EF-translatable form.
    /// </summary>
    /// <remarks>
    /// The projection goes through an anonymous type rather than straight into
    /// <see cref="NamedCountRow"/>. EF cannot translate a grouping projected directly into a
    /// positional record's constructor - it fails at runtime with "could not be translated",
    /// not at compile time. The existing daily-rollup aggregates in
    /// <see cref="JobPostingRepository"/> use anonymous types for the same reason.
    /// </remarks>
    private static async Task<IReadOnlyList<NamedCountRow>> CountByAsync(
        IQueryable<JobPostingEntity> query,
        System.Linq.Expressions.Expression<Func<JobPostingEntity, string>> selector,
        int? take,
        CancellationToken ct)
    {
        var grouped = query
            .GroupBy(selector)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name);

        var rows = take is { } limit
            ? await grouped.Take(limit).ToListAsync(ct)
            : await grouped.ToListAsync(ct);

        return rows.Select(r => new NamedCountRow(r.Name, r.Count)).ToList();
    }

    private static IQueryable<JobPostingEntity> Filter(
        IQueryable<JobPostingEntity> query, PostingSearchCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(criteria.SearchTerm))
        {
            // Any() over the attributions rather than a column comparison: a posting can
            // belong to several searches, and filtering by one must not hide it from the
            // others.
            query = query.Where(p => p.SearchTerms.Any(l => l.SearchTerm == criteria.SearchTerm));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Query))
        {
            var text = criteria.Query;
            query = query.Where(p =>
                EF.Functions.Like(p.Title, "%" + text + "%") ||
                (p.Company != null && EF.Functions.Like(p.Company, "%" + text + "%")));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Site))
        {
            query = query.Where(p => p.Site == criteria.Site);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Company))
        {
            query = query.Where(p => p.Company == criteria.Company);
        }

        if (!string.IsNullOrWhiteSpace(criteria.JobType))
        {
            // JobType may hold several comma-separated values ("parttime, fulltime"), so an
            // equality test would miss exactly the multi-valued rows the parser keeps.
            var jobType = criteria.JobType;
            query = query.Where(p => p.JobType != null && EF.Functions.Like(p.JobType, "%" + jobType + "%"));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Country))
        {
            query = query.Where(p => p.LocationCountry == criteria.Country);
        }

        if (!string.IsNullOrWhiteSpace(criteria.City))
        {
            query = query.Where(p => p.LocationCity == criteria.City);
        }

        if (criteria.IsRemote is { } remote)
        {
            query = query.Where(p => p.IsRemote == remote);
        }

        if (criteria.HasSalary is { } hasSalary)
        {
            query = hasSalary
                ? query.Where(p => p.MinAmount != null || p.MaxAmount != null)
                : query.Where(p => p.MinAmount == null && p.MaxAmount == null);
        }

        if (criteria.MinSalary is { } minSalary)
        {
            query = query.Where(p => p.MaxAmount >= minSalary || p.MinAmount >= minSalary);
        }

        if (criteria.PostedFrom is { } postedFrom)
        {
            query = query.Where(p => p.DatePosted >= postedFrom);
        }

        if (criteria.PostedTo is { } postedTo)
        {
            query = query.Where(p => p.DatePosted <= postedTo);
        }

        if (criteria.FirstSeenFrom is { } firstSeenFrom)
        {
            query = query.Where(p => p.FirstSeenUtc >= firstSeenFrom);
        }

        if (criteria.FirstSeenTo is { } firstSeenTo)
        {
            query = query.Where(p => p.FirstSeenUtc <= firstSeenTo);
        }

        return query;
    }

    private static IQueryable<JobPostingEntity> Order(
        IQueryable<JobPostingEntity> query, PostingSearchCriteria criteria)
    {
        // Id is the tiebreaker on every ordering. Without it, paging over rows sharing a sort
        // value can repeat or skip rows between pages: the database is free to return ties in
        // a different order each time it is asked.
        var descending = criteria.Descending;

        return criteria.Sort switch
        {
            PostingSort.FirstSeen => descending
                ? query.OrderByDescending(p => p.FirstSeenUtc).ThenByDescending(p => p.Id)
                : query.OrderBy(p => p.FirstSeenUtc).ThenBy(p => p.Id),
            PostingSort.DatePosted => descending
                ? query.OrderByDescending(p => p.DatePosted).ThenByDescending(p => p.Id)
                : query.OrderBy(p => p.DatePosted).ThenBy(p => p.Id),
            PostingSort.Salary => descending
                ? query.OrderByDescending(p => p.MaxAmount ?? p.MinAmount).ThenByDescending(p => p.Id)
                : query.OrderBy(p => p.MaxAmount ?? p.MinAmount).ThenBy(p => p.Id),
            PostingSort.Title => descending
                ? query.OrderByDescending(p => p.Title).ThenByDescending(p => p.Id)
                : query.OrderBy(p => p.Title).ThenBy(p => p.Id),
            _ => descending
                ? query.OrderByDescending(p => p.LastSeenUtc).ThenByDescending(p => p.Id)
                : query.OrderBy(p => p.LastSeenUtc).ThenBy(p => p.Id),
        };
    }
}

public sealed record NamedCountRow(string Name, int Count);

public sealed record SearchTermSummary(
    string SearchTerm,
    int PostingCount,
    int RunCount,
    DateOnly? LastScrapeDate,
    DateTimeOffset? LastSeenUtc);

public sealed record PostingFacets
{
    public string? SearchTerm { get; init; }
    public int Total { get; init; }
    public int RemoteCount { get; init; }
    public int WithSalaryCount { get; init; }
    public DateOnly? EarliestDatePosted { get; init; }
    public DateOnly? LatestDatePosted { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public IReadOnlyList<NamedCountRow> Sites { get; init; } = [];
    public IReadOnlyList<NamedCountRow> JobTypes { get; init; } = [];
    public IReadOnlyList<NamedCountRow> Countries { get; init; } = [];
    public IReadOnlyList<NamedCountRow> Cities { get; init; } = [];
    public IReadOnlyList<NamedCountRow> Companies { get; init; } = [];
}
