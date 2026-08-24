using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Enrichment;
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
        string? concept = null,
        string? minSeniority = null,
        string? maxSeniority = null,
        string? roleFamily = null,
        string? workArrangement = null,
        decimal? minAnnualSalary = null,
        bool includeTextSalary = true,
        bool? securityClearance = null,
        string? ir35 = null,
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

        // Rejected rather than ignored. A mistyped enum that silently drops its filter
        // returns a plausible page of the wrong postings, and nothing about the response
        // says so - which is worse than an error, because it gets believed.
        if (!TryParseEnum<Seniority>(minSeniority, out var parsedMinSeniority, out var seniorityError)
            || !TryParseEnum<Seniority>(maxSeniority, out var parsedMaxSeniority, out seniorityError))
        {
            return TypedResults.Problem(seniorityError, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseEnum<RoleFamily>(roleFamily, out var parsedRoleFamily, out var familyError))
        {
            return TypedResults.Problem(familyError, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseEnum<WorkArrangement>(workArrangement, out var parsedArrangement, out var arrangementError))
        {
            return TypedResults.Problem(arrangementError, statusCode: StatusCodes.Status400BadRequest);
        }

        if (ir35 is not null and not "inside" and not "outside")
        {
            return TypedResults.Problem(
                detail: $"Unknown ir35 '{ir35}'. Valid values: inside, outside.",
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
            Concept = concept,
            MinSeniority = parsedMinSeniority,
            MaxSeniority = parsedMaxSeniority,
            RoleFamily = parsedRoleFamily,
            WorkArrangement = parsedArrangement,
            MinAnnualSalary = minAnnualSalary,
            IncludeTextSalary = includeTextSalary,
            RequiresSecurityClearance = securityClearance,
            Ir35 = ir35,
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

    /// <summary>
    /// Parses an optional enum query parameter, naming the valid values when it fails.
    /// </summary>
    /// <remarks>
    /// Absent is fine and means "no filter". Present-but-unrecognised is an error, because
    /// the alternative - dropping the filter - answers a different question than the one
    /// asked and looks exactly like a correct answer.
    /// </remarks>
    private static bool TryParseEnum<T>(string? raw, out T? parsed, out string? error)
        where T : struct, Enum
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (Enum.TryParse<T>(raw, ignoreCase: true, out var value))
        {
            parsed = value;
            return true;
        }

        error = $"Unknown {typeof(T).Name} '{raw}'. Valid values: "
            + string.Join(", ", Enum.GetNames<T>());

        return false;
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
