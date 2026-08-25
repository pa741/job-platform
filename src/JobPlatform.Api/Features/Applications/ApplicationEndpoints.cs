using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;
using JobPlatform.Documents;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Applications;

/// <summary>What the candidate wants steered, if anything.</summary>
/// <param name="Instructions">
/// Free text, appended to the prompt as guidance and never as permission. It cannot license a
/// claim the profile does not support - the gap list is passed separately and the prompt treats
/// it as binding.
/// </param>
public sealed record GenerateApplicationRequest([property: MaxLength(2000)] string? Instructions);

public record ApplicationSummary
{
    public required long Id { get; init; }
    public required long PostingId { get; init; }
    public required string PostingTitle { get; init; }
    public string? Company { get; init; }
    public required int Revision { get; init; }
    public string? Instructions { get; init; }
    public string? Model { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>A generated draft, with both documents as markdown.</summary>
public sealed record ApplicationDetail : ApplicationSummary
{
    public required string CurriculumVitaeMarkdown { get; init; }
    public required string CoverLetterMarkdown { get; init; }

    /// <summary>What this draft chose to lead with.</summary>
    public IReadOnlyList<string> Emphasised { get; init; } = [];
}

/// <summary>
/// Generating and downloading a tailored CV and cover letter.
/// </summary>
/// <remarks>
/// <b>The only path in the system that spends money on the expensive deployment</b>, which is
/// why it is the only one gated behind an explicit action rather than running on a schedule.
/// A candidate presses generate; nothing here ever fires because a page was opened.
///
/// Generation is per-match, and the match must already exist. That is not a convenience: the
/// writer is handed the gap list as the set of claims it must not make, and a document written
/// without one has nothing stopping it from inventing the very skills the candidate lacks.
/// Refusing to generate for an unscored posting is what keeps that guarantee real.
/// </remarks>
public sealed class ApplicationEndpoints : IEndpointGroup
{
    private const int MaxLimit = 50;

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/applications")
            .WithTags("Applications")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListApplications")
            .WithSummary("The calling principal's generated drafts, newest first.");

        group.MapPost("/{postingId:long}", GenerateAsync)
            .WithName("GenerateApplication")
            .WithSummary("Writes a tailored CV and cover letter for one matched posting.");

        group.MapGet("/{id:long}", GetAsync)
            .WithName("GetApplication")
            .WithSummary("One draft, as markdown.");

        group.MapGet("/{id:long}/cv.pdf", (ClaimsPrincipal user, long id, CandidateProfileRepository p,
                ApplicationDocumentRepository d, CancellationToken ct)
                => DownloadAsync(user, id, p, d, cv: true, ct))
            .WithName("DownloadCurriculumVitae")
            .WithSummary("The tailored CV as a PDF.")
            .ExcludeFromDescription();

        group.MapGet("/{id:long}/cover-letter.pdf", (ClaimsPrincipal user, long id, CandidateProfileRepository p,
                ApplicationDocumentRepository d, CancellationToken ct)
                => DownloadAsync(user, id, p, d, cv: false, ct))
            .WithName("DownloadCoverLetter")
            .WithSummary("The cover letter as a PDF.")
            .ExcludeFromDescription();
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] ApplicationDocumentRepository documents,
        CancellationToken ct,
        int limit = 25)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.Ok(new { items = Array.Empty<ApplicationSummary>() });
        }

        var rows = await documents.ListAsync(profileId.Value, Math.Clamp(limit, 1, MaxLimit), ct);

        return TypedResults.Ok(new { items = rows.Select(ToSummary).ToList() });
    }

    /// <summary>
    /// Writes a draft for one matched posting.
    /// </summary>
    /// <remarks>
    /// Synchronous, unlike every other model call in this system. A queue would be the
    /// consistent choice and the wrong one: the person is sitting in front of the result, one
    /// call takes tens of seconds rather than minutes, and a fire-and-forget generation would
    /// need a whole polling surface to tell them it had finished. The rate limiter is what
    /// bounds the cost, not a queue.
    /// </remarks>
    private static async Task<IResult> GenerateAsync(
        ClaimsPrincipal user,
        long postingId,
        GenerateApplicationRequest? request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        [FromServices] ApplicationDocumentRepository documents,
        [FromServices] TimeProvider time,
        CancellationToken ct,
        [FromServices] IApplicationWriter? writer = null)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        // Not an error. A deployment with no provider configured is the shape this system
        // ships in, and saying so plainly is more useful than a 500 that looks like a fault -
        // the same answer the extraction backfill gives.
        if (writer is null)
        {
            return TypedResults.Problem(
                detail: "No AI provider is configured, so no documents can be written.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var view = await profiles.GetAsync(subjectId, ct);

        if (view is null)
        {
            return TypedResults.Problem(
                detail: "No profile exists for this principal. A CV is written from the profile, so there is nothing to tailor.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var context = await matches.GetForWritingAsync(view.Id, postingId, ct);

        if (context is null)
        {
            return TypedResults.Problem(
                detail: "This posting has not been matched against your profile yet, so there is no gap list to write against.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var (match, assessment, posting) = context.Value;

        var draft = await writer.WriteAsync(
            new ApplicationRequest(view.Profile, posting, match, assessment, request?.Instructions), ct);

        if (draft is null)
        {
            return TypedResults.Problem(
                detail: "The model did not return a usable draft. Nothing was stored; try again.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var stored = await documents.AddAsync(
            view.Id, postingId, draft, request?.Instructions, time.GetUtcNow(), ct);

        return TypedResults.Created($"/api/v1/applications/{stored.Id}", ToDetail(stored));
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user,
        long id,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] ApplicationDocumentRepository documents,
        CancellationToken ct)
    {
        var stored = await FindAsync(user, id, profiles, documents, ct);

        return stored is null ? TypedResults.NotFound() : TypedResults.Ok(ToDetail(stored));
    }

    /// <summary>
    /// Renders one of the two documents to a PDF.
    /// </summary>
    /// <remarks>
    /// Rendered per request rather than stored. The markdown is the record; a change to the
    /// layout then reaches documents already generated, and a database billed by the second does
    /// not accumulate megabytes of binary. Rendering a page of markdown is milliseconds, so
    /// there is nothing to cache.
    ///
    /// The filename is built from the posting title, sanitised: it becomes a filename on
    /// somebody's disk, and an advert title is text an employer typed.
    /// </remarks>
    private static async Task<IResult> DownloadAsync(
        ClaimsPrincipal user,
        long id,
        CandidateProfileRepository profiles,
        ApplicationDocumentRepository documents,
        bool cv,
        CancellationToken ct)
    {
        var stored = await FindAsync(user, id, profiles, documents, ct);

        if (stored is null)
        {
            return TypedResults.NotFound();
        }

        var markdown = cv ? stored.CurriculumVitaeMarkdown : stored.CoverLetterMarkdown;

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return TypedResults.NotFound();
        }

        var kind = cv ? "CV" : "Cover letter";
        var title = $"{kind} - {stored.PostingTitle}";

        return TypedResults.File(
            MarkdownPdfRenderer.Render(markdown, title),
            contentType: "application/pdf",
            fileDownloadName: FileName(kind, stored));
    }

    private static async Task<StoredApplication?> FindAsync(
        ClaimsPrincipal user,
        long id,
        CandidateProfileRepository profiles,
        ApplicationDocumentRepository documents,
        CancellationToken ct)
    {
        if (user.SubjectId() is not { Length: > 0 } subjectId)
        {
            return null;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        // The profile id is resolved from the token and passed to the repository, which has no
        // method that looks a document up without one. A document id from the route can
        // therefore never reach a stranger's CV.
        return profileId is null ? null : await documents.GetAsync(profileId.Value, id, ct);
    }

    /// <summary>
    /// A filename safe to write to a disk.
    /// </summary>
    /// <remarks>
    /// Built from an advert title, which is text an employer typed and which routinely contains
    /// slashes, colons and quotes. Anything outside a conservative set becomes a hyphen, and the
    /// result is capped - a 200-character job title makes a filename some systems refuse.
    /// </remarks>
    private static string FileName(string kind, StoredApplication stored)
    {
        var span = stored.PostingTitle.AsSpan();
        var builder = new System.Text.StringBuilder(80);

        foreach (var character in span)
        {
            if (builder.Length >= 60)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        var slug = builder.ToString().Trim('-');

        return $"{kind.Replace(' ', '-')}-{(slug.Length == 0 ? "role" : slug)}-v{stored.Revision}.pdf";
    }

    private static ApplicationSummary ToSummary(StoredApplication stored)
        => new()
        {
            Id = stored.Id,
            PostingId = stored.PostingId,
            PostingTitle = stored.PostingTitle,
            Company = stored.Company,
            Revision = stored.Revision,
            Instructions = stored.Instructions,
            Model = stored.Model,
            CreatedAtUtc = stored.CreatedAtUtc,
        };

    private static ApplicationDetail ToDetail(StoredApplication stored)
        => new()
        {
            Id = stored.Id,
            PostingId = stored.PostingId,
            PostingTitle = stored.PostingTitle,
            Company = stored.Company,
            Revision = stored.Revision,
            Instructions = stored.Instructions,
            Model = stored.Model,
            CreatedAtUtc = stored.CreatedAtUtc,
            CurriculumVitaeMarkdown = stored.CurriculumVitaeMarkdown,
            CoverLetterMarkdown = stored.CoverLetterMarkdown,
            Emphasised = stored.Emphasised,
        };
}
