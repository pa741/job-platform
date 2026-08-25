using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Profiles;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Api.Features.Profiles;

/// <summary>
/// The caller's own profile. Read, replace, delete.
/// </summary>
/// <remarks>
/// <b>Never <see cref="AuthSetup.PublicReadPolicy"/>.</b> These routes carry somebody's
/// employment history and contact details, so they require a principal unconditionally - the
/// <c>Api:AllowAnonymousReads</c> switch that opens the posting endpoints during development
/// must not reach them, exactly as it must not reach <c>/me</c>. There is also no route
/// parameter naming whose profile: the subject id comes from the token, so there is nothing for
/// a caller to tamper with.
///
/// <b>These endpoints read and write Azure SQL</b>, which the architecture otherwise reserves
/// for posting browse and search. That is deliberate and it is bounded: a profile is fetched
/// once when the page opens and written when the person presses save, so this is not a polling
/// path and must never become one. Nothing here may be added to a client's bootstrap sequence
/// either - for the same reason <c>/search-terms</c> is served from Cosmos, a call every page
/// waits on must not be able to block behind a waking database.
///
/// No output cache, deliberately. A profile is per-principal and mutable, and a shared cache
/// keyed on a URL with no user in it is precisely how one person gets served another's record.
/// </remarks>
public sealed class ProfileEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/profile")
            .WithTags("Profile")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", GetAsync)
            .WithName("GetProfile")
            .WithSummary("The calling principal's profile.");

        group.MapPut("/", SaveAsync)
            .WithName("SaveProfile")
            .WithSummary("Replaces the calling principal's profile with the submitted form.");

        group.MapDelete("/", DeleteAsync)
            .WithName("DeleteProfile")
            .WithSummary("Erases the calling principal's profile, matches and generated documents.");
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var view = await profiles.GetAsync(subjectId, ct);

        // 404 rather than an empty profile. "You have not created one" and "you created one and
        // left it blank" are different states, and a client that cannot tell them apart cannot
        // decide whether to open an empty form or a filled one.
        return view is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(view.Profile.ToResponse(view.Extracted, view.ExtractedAtUtc));
    }

    /// <summary>
    /// Stores the submitted form and re-reads it when the text actually changed.
    /// </summary>
    /// <remarks>
    /// Extraction runs inline rather than through the queue the posting pipeline uses. The
    /// asymmetry is intentional: an ingest is one blob holding hundreds of postings and a
    /// per-posting call inside it would replay in full on every Event Grid retry, whereas this
    /// is one document, submitted by a person who is waiting, and who should see their skills
    /// appear rather than reload until they do.
    ///
    /// It is allowed to fail without failing the save. A profile that stored but was not
    /// extracted has declared skills and no inferred ones, which the nightly sweep repairs; a
    /// save that answered 500 because a model call timed out would lose somebody's typing.
    /// </remarks>
    private static async Task<IResult> SaveAsync(
        ClaimsPrincipal user,
        ProfileRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] TimeProvider time,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct,
        [FromServices] IDocumentExtractor? extractor = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var (view, textChanged) = await profiles.SaveAsync(request.ToDomain(subjectId), time, ct);

        if (extractor is null || !textChanged)
        {
            return TypedResults.Ok(view.Profile.ToResponse(view.Extracted, view.ExtractedAtUtc));
        }

        var extracted = await ExtractAsync(
            profiles, extractor, view, time, loggerFactory.CreateLogger<ProfileEndpoints>(), ct);

        // Re-read rather than reusing what came back from the model. See ExtractAsync: what it
        // returns is deliberately a bool, so the extraction itself is not in scope here to be
        // handed to a caller by mistake.
        var stored = extracted ? await profiles.GetAsync(subjectId, ct) ?? view : view;

        return TypedResults.Ok(stored.Profile.ToResponse(stored.Extracted, stored.ExtractedAtUtc));
    }

    /// <summary>
    /// Reads the profile for concepts and stores them. Returns whether anything was stored.
    /// </summary>
    /// <remarks>
    /// <b>A bool, and not the extraction, on purpose.</b> Returning the
    /// <see cref="DocumentExtraction"/> is what caused the bug this shape now prevents: the
    /// caller returned it straight to the client, so <c>PUT</c> answered <c>Required</c> where
    /// <c>GET</c> answered <c>Expert</c> for the same skill. The extractor speaks the demand
    /// half of <see cref="AssertionPolarity"/> - it is the same prompt that reads adverts - and
    /// the repository translates to the supply half on the way in, so the model's own output is
    /// never the right thing to show anybody. Making it unavailable to the caller is stronger
    /// than remembering not to use it.
    /// </remarks>
    private static async Task<bool> ExtractAsync(
        CandidateProfileRepository profiles,
        IDocumentExtractor extractor,
        ProfileView view,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct)
    {
        var document = view.Profile.ToDocument();

        if (string.IsNullOrWhiteSpace(document))
        {
            return false;
        }

        try
        {
            var extraction = await extractor.ExtractAsync(
                new ExtractionRequest(DocumentKind.Profile, document, view.Profile.Headline), ct);

            if (extraction is null)
            {
                return false;
            }

            await profiles.ApplyExtractionAsync(
                view.Id, extraction, Hash(document), time.GetUtcNow(), ct);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The profile is already saved. Losing the extraction costs inferred skills until
            // the next sweep; failing the request would cost the candidate their typing.
            logger.LogWarning(ex, "Storing a profile succeeded but extracting it did not.");
            return false;
        }
    }

    /// <summary>
    /// Erases everything held about the caller.
    /// </summary>
    /// <remarks>
    /// Present because the data here is personal, and a system that stores an employment history
    /// without offering a way to remove it is not one anybody should hand a CV to. The cascade
    /// takes the child sections, the concept rows, every scored match and every generated
    /// document with it - which is the reason matches cascade from the profile side and not
    /// from the posting side.
    /// </remarks>
    private static async Task<IResult> DeleteAsync(
        ClaimsPrincipal user,
        [FromServices] JobsDbContext db,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var deleted = await db.CandidateProfiles
            .Where(p => p.SubjectId == subjectId)
            .ExecuteDeleteAsync(ct);

        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static string Hash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
