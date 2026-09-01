using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Searches;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Searches;

/// <summary>
/// The caller's own scraper searches. List, create, replace, delete.
/// </summary>
/// <remarks>
/// <b>Never <see cref="AuthSetup.PublicReadPolicy"/>.</b> These routes decide what a scheduled
/// job on somebody's NAS spends paid residential bandwidth on, so they require a principal
/// unconditionally - the <c>Api:AllowAnonymousReads</c> switch that opens the posting endpoints
/// during development must not reach them, exactly as it must not reach <c>/me</c> or the
/// profile. <c>AuthorizationTests</c> pins that.
///
/// The slug in a route names one of the caller's own searches and nothing else:
/// <see cref="ScraperSearchRepository"/> takes a subject id alongside it and has no overload
/// that does not, so a slug belonging to somebody else answers 404 rather than editing their
/// row. A caller cannot distinguish "no such search" from "not yours", which is the point.
///
/// <b>These endpoints read and write Azure SQL</b>, which the architecture otherwise reserves
/// for posting browse and search. Bounded exactly like the profile's: fetched when the settings
/// page opens, written when somebody presses save. Never a polling path, and <b>nothing here may
/// join a client's bootstrap sequence</b> - that is the rule that keeps opening the dashboard
/// from waiting on a database that pauses when idle.
///
/// No output cache, deliberately: per-principal and mutable, and a shared cache keyed on a URL
/// with no user in it is how one person is served another's settings.
/// </remarks>
public sealed class SearchEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/searches")
            .WithTags("Searches")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListSearches")
            .WithSummary("The calling principal's scraper searches, and when the scraper was last told about them.");

        group.MapGet("/options", GetOptions)
            .WithName("GetSearchOptions")
            .WithSummary("The vocabulary a search form needs: boards, job types, freehire filter keys and bounds.");

        group.MapPost("/", CreateAsync)
            .WithName("CreateSearch")
            .WithSummary("Adds a search and assigns it a slug.");

        group.MapPut("/{slug}", UpdateAsync)
            .WithName("UpdateSearch")
            .WithSummary("Replaces one of the calling principal's searches. The slug is not editable.");

        group.MapDelete("/{slug}", DeleteAsync)
            .WithName("DeleteSearch")
            .WithSummary("Stops scraping one of the calling principal's searches. Its postings stay.");

        group.MapPost("/publish", PublishAsync)
            .WithName("PublishSearches")
            .WithSummary("Rewrites the scraper's configuration from what is stored. The repair path.");
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] ScraperSearchRepository searches,
        CancellationToken ct,
        [FromServices] ScraperConfigPublisher? publisher = null)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        // A read, and it stays one: the last-published time is asked of the blob rather than
        // produced by writing it. A GET that republished would make opening the settings page a
        // write against the scraper's configuration.
        var publishedUtc = publisher is null ? null : await publisher.LastPublishedAsync(ct);

        return TypedResults.Ok(
            await ListResponseAsync(subjectId, searches, publisher is not null, publishedUtc, ct));
    }

    /// <summary>
    /// What a form may offer.
    /// </summary>
    /// <remarks>
    /// Served rather than hard-coded in the client, following <c>/postings/facets</c>. A
    /// dropdown offering a board this build refuses produces a save that fails for a reason the
    /// person cannot see, and a duplicated list in TypeScript is exactly how that arises.
    /// </remarks>
    private static IResult GetOptions()
        => TypedResults.Ok(new ScraperSearchOptionsResponse(
            [.. ScraperSites.All.Select(site => site.ToWireName())],
            [
                JobTypeNormalizer.FullTime,
                JobTypeNormalizer.PartTime,
                JobTypeNormalizer.Contract,
                JobTypeNormalizer.Temporary,
                JobTypeNormalizer.Internship,
                JobTypeNormalizer.Volunteer,
            ],
            [.. ScraperSearchValidation.FreehireFilterKeys.Order(StringComparer.Ordinal)],
            ScraperSearchValidation.MaxHoursOld,
            ScraperSearchValidation.MaxResultsWanted));

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user,
        ScraperSearchRequest request,
        [FromServices] ScraperSearchRepository searches,
        [FromServices] TimeProvider time,
        CancellationToken ct,
        [FromServices] ScraperConfigPublisher? publisher = null)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        // The slug is assigned by the repository, which is the only thing that can see what is
        // already taken. This placeholder never reaches the database.
        if (Reject(request, slug: string.Empty, out var search, out var rejection))
        {
            return rejection;
        }

        if (await searches.NameTakenAsync(subjectId, search.Name, exceptSlug: null, ct))
        {
            return NameConflict(search.Name);
        }

        await searches.CreateAsync(subjectId, search, time, ct);

        return TypedResults.Ok(await PublishedListAsync(subjectId, searches, publisher, ct));
    }

    private static async Task<IResult> UpdateAsync(
        ClaimsPrincipal user,
        string slug,
        ScraperSearchRequest request,
        [FromServices] ScraperSearchRepository searches,
        [FromServices] TimeProvider time,
        CancellationToken ct,
        [FromServices] ScraperConfigPublisher? publisher = null)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        if (Reject(request, slug, out var search, out var rejection))
        {
            return rejection;
        }

        if (await searches.NameTakenAsync(subjectId, search.Name, exceptSlug: slug, ct))
        {
            return NameConflict(search.Name);
        }

        if (await searches.UpdateAsync(subjectId, slug, search, time, ct) is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await PublishedListAsync(subjectId, searches, publisher, ct));
    }

    private static async Task<IResult> DeleteAsync(
        ClaimsPrincipal user,
        string slug,
        [FromServices] ScraperSearchRepository searches,
        CancellationToken ct,
        [FromServices] ScraperConfigPublisher? publisher = null)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        if (!await searches.DeleteAsync(subjectId, slug, ct))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await PublishedListAsync(subjectId, searches, publisher, ct));
    }

    /// <summary>
    /// Rewrites the published configuration from what is stored.
    /// </summary>
    /// <remarks>
    /// The repair path, and the reason a failed publish is allowed not to fail a save. It is
    /// also the only route here whose effect is not scoped to the caller - it republishes
    /// everybody's enabled searches - which is safe because it is idempotent, derives everything
    /// from SQL, and reveals nothing: the response says when, not what.
    ///
    /// 503 rather than 500 where no storage is configured. "Not here" invites a fallback;
    /// "broken" invites a retry loop.
    /// </remarks>
    private static async Task<IResult> PublishAsync(
        ClaimsPrincipal user,
        [FromServices] ScraperSearchRepository searches,
        CancellationToken ct,
        [FromServices] ScraperConfigPublisher? publisher = null)
    {
        if (!user.TryGetSubjectId(out _, out var error))
        {
            return error;
        }

        if (publisher is null)
        {
            return TypedResults.Problem(
                detail: "This deployment has no scraper configuration storage configured, so "
                        + "there is nowhere to publish to. The scraper falls back to its own "
                        + "config.yaml.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var publishedUtc = await publisher.PublishAsync(ct);

        return publishedUtc is null
            ? TypedResults.Problem(
                detail: "Writing the scraper configuration failed. The searches are stored and "
                        + "unaffected; try again in a minute.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : TypedResults.Ok(new { published = true, publishedUtc });
    }

    /// <summary>
    /// The caller's whole set, plus the publish state, after every change.
    /// </summary>
    /// <remarks>
    /// Mutations return the collection rather than the one row they touched, deliberately. A
    /// save rewrites the configuration for every search at once, so the publish state that comes
    /// back describes the set and not the row - and a client handed one row would have to guess
    /// at the rest or make a second call. One response, one render, nothing to drift.
    /// </remarks>
    private static async Task<ScraperSearchListResponse> ListResponseAsync(
        string subjectId,
        ScraperSearchRepository searches,
        bool publisherConfigured,
        DateTimeOffset? publishedUtc,
        CancellationToken ct)
    {
        var stored = await searches.ListAsync(subjectId, ct);

        return new ScraperSearchListResponse(
            [.. stored.Select(view => view.ToResponse())],
            Published: publisherConfigured && publishedUtc is not null,
            publishedUtc);
    }

    /// <summary>Republishes, then answers with the caller's set and how that went.</summary>
    private static async Task<ScraperSearchListResponse> PublishedListAsync(
        string subjectId,
        ScraperSearchRepository searches,
        ScraperConfigPublisher? publisher,
        CancellationToken ct)
    {
        // Publishing before reading back, so the timestamp in the response describes the write
        // this request caused rather than the one before it.
        var publishedUtc = publisher is null ? null : await publisher.PublishAsync(ct);

        return await ListResponseAsync(subjectId, searches, publisher is not null, publishedUtc, ct);
    }

    /// <summary>
    /// Maps and validates, returning true when the request should not be stored.
    /// </summary>
    /// <remarks>
    /// An unknown board is refused rather than dropped, and every problem is reported at once. A
    /// dropped board would run the search against fewer sources than the person asked for and
    /// say so nowhere, which is the failure the posting endpoints already refuse when they
    /// answer 400 for an unrecognised filter rather than ignoring it.
    /// </remarks>
    private static bool Reject(
        ScraperSearchRequest request,
        string slug,
        out ScraperSearch search,
        out IResult rejection)
    {
        var known = request.TryToDomain(slug, out search, out var unknownSites);
        var problems = new List<string>(ScraperSearchValidation.Validate(search));

        if (!known)
        {
            problems.Insert(0, $"Unknown job board(s): {string.Join(", ", unknownSites)}. "
                               + $"Allowed: {string.Join(", ", ScraperSites.All.Select(site => site.ToWireName()))}.");
        }

        if (problems.Count == 0)
        {
            rejection = TypedResults.Empty;
            return false;
        }

        rejection = TypedResults.Problem(
            detail: string.Join(" ", problems),
            statusCode: StatusCodes.Status400BadRequest);

        return true;
    }

    private static IResult NameConflict(string name)
        => TypedResults.Problem(
            detail: $"You already have a search called '{name}'. Names are yours alone, so this "
                    + "says nothing about anybody else's searches - pick another and save again.",
            statusCode: StatusCodes.Status409Conflict);
}
