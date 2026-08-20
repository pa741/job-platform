using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Postings;

/// <summary>
/// Browsing and searching stored postings.
/// </summary>
/// <remarks>
/// The only endpoints in the API that read Azure SQL, which is why they all carry an output
/// cache policy and the read rate limit. Metrics deliberately live elsewhere and read Cosmos.
/// </remarks>
public sealed class PostingEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/postings")
            .WithTags("Postings")
            .RequireAuthorization(AuthSetup.PublicReadPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", SearchAsync)
            .WithName("SearchPostings")
            .WithSummary("Search stored postings.")
            .CacheOutput(CacheSetup.PostingsPolicy);

        group.MapGet("/facets", FacetsAsync)
            .WithName("GetPostingFacets")
            .WithSummary("Filter vocabulary and totals, for building a filter UI.")
            .CacheOutput(CacheSetup.FacetsPolicy);

        group.MapGet("/{id:long}", GetAsync)
            .WithName("GetPosting")
            .WithSummary("One posting in full, including its description.")
            .CacheOutput(CacheSetup.PostingsPolicy);

        routes.MapGet("/search-terms", SearchTermsAsync)
            .WithTags("Postings")
            .WithName("ListSearchTerms")
            .WithSummary("Search terms the platform holds data for.")
            .RequireAuthorization(AuthSetup.PublicReadPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy)
            .CacheOutput(CacheSetup.FacetsPolicy);
    }

    private static async Task<IResult> SearchAsync(
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct,
        string? searchTerm = null,
        string? q = null,
        string? site = null,
        string? company = null,
        string? jobType = null,
        string? country = null,
        string? city = null,
        bool? remote = null,
        bool? hasSalary = null,
        decimal? minSalary = null,
        DateOnly? postedFrom = null,
        DateOnly? postedTo = null,
        DateTimeOffset? firstSeenFrom = null,
        DateTimeOffset? firstSeenTo = null,
        string sort = "lastSeen",
        string order = "desc",
        int limit = 25,
        int offset = 0,
        bool includeTotal = false)
    {
        if (!TryParseSort(sort, out var parsedSort))
        {
            return TypedResults.Problem(
                detail: $"Unknown sort '{sort}'. Valid values: lastSeen, firstSeen, datePosted, salary, title.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (offset < 0)
        {
            return TypedResults.Problem(
                detail: "offset must not be negative.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var criteria = new PostingSearchCriteria
        {
            SearchTerm = searchTerm,
            Query = q,
            Site = site,
            Company = company,
            JobType = jobType,
            Country = country,
            City = city,
            IsRemote = remote,
            HasSalary = hasSalary,
            MinSalary = minSalary,
            PostedFrom = postedFrom,
            PostedTo = postedTo,
            FirstSeenFrom = firstSeenFrom,
            FirstSeenTo = firstSeenTo,
            Sort = parsedSort,
            Descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase),
            Limit = limit,
            Offset = offset,
            IncludeTotal = includeTotal,
        };

        var page = await repository.SearchAsync(criteria, ct);

        return TypedResults.Ok(new PageResponse<PostingSummary>
        {
            Items = page.Items.Select(p => p.ToSummary()).ToList(),
            HasMore = page.HasMore,
            Total = page.Total,
            Limit = Math.Clamp(limit, 1, JobPostingQueryRepository.MaxLimit),
            Offset = offset,
        });
    }

    private static async Task<IResult> GetAsync(
        long id,
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct)
    {
        var posting = await repository.GetByIdAsync(id, ct);

        return posting is null
            ? TypedResults.Problem(
                detail: $"No posting with id {id}.",
                statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(posting.ToDetail());
    }

    private static async Task<IResult> FacetsAsync(
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct,
        string? searchTerm = null)
    {
        var facets = await repository.GetFacetsAsync(searchTerm, ct);
        return TypedResults.Ok(facets.ToResponse());
    }

    private static async Task<IResult> SearchTermsAsync(
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct)
    {
        var terms = await repository.ListSearchTermsAsync(ct);

        return TypedResults.Ok(terms
            .Select(t => new SearchTermResponse(
                t.SearchTerm, t.PostingCount, t.RunCount, t.LastScrapeDate, t.LastSeenUtc))
            .ToList());
    }

    private static bool TryParseSort(string value, out PostingSort sort)
    {
        switch (value.ToLowerInvariant())
        {
            case "lastseen": sort = PostingSort.LastSeen; return true;
            case "firstseen": sort = PostingSort.FirstSeen; return true;
            case "dateposted": sort = PostingSort.DatePosted; return true;
            case "salary": sort = PostingSort.Salary; return true;
            case "title": sort = PostingSort.Title; return true;
            default: sort = PostingSort.LastSeen; return false;
        }
    }
}
