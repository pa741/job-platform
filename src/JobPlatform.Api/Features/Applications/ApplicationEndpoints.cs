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
///
/// <b>Generation now renders as well as writes, and rendering is deliberately the part allowed
/// to fail.</b> The model call took tens of seconds and cost real money; a MigraDoc page and an
/// OOXML package take milliseconds and cost nothing, and losing one costs a re-render rather
/// than a regeneration. So the draft is stored first, the files are rendered and uploaded
/// afterwards, and whatever survived is recorded on the row - the pack then reports what it has.
/// Failing the request because a container's role assignment has not finished propagating would
/// throw away the expensive half to protect the cheap one. It is the argument
/// <c>ProfileEndpoints.ExtractAsync</c> already makes about a save whose extraction failed.
///
/// <b>The download routes still render per request, and that is not a duplicate.</b> They serve
/// a person clicking a link in the dashboard, where the markdown is the record and a layout
/// change should reach documents already generated. What is stored serves the other consumer: an
/// agent needs a URL an employer's upload box can fetch, and that cannot be a route behind this
/// API's bearer token.
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
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct,
        [FromServices] IApplicationWriter? writer = null,

        // Nullable for the reason the writer above is: a deployment with no storage configured
        // registers no store, and that is a capability it does not have rather than a dependency
        // it is missing. Generation then produces markdown and no files, which is what the pack
        // already says when a posting's documents were never written.
        [FromServices] IApplicationPackStore? packs = null)
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

        // After the row exists, never before it. A stored path names the document it was rendered
        // from, so there is nothing to name until the draft has an id - and a file uploaded
        // against an id that never landed is a blob nothing will ever look for again.
        await RenderAsync(
            packs,
            documents,
            view.Id,
            view.Profile.FullName,
            stored,
            loggerFactory.CreateLogger<ApplicationEndpoints>(),
            ct);

        return TypedResults.Created($"/api/v1/applications/{stored.Id}", ToDetail(stored));
    }

    /// <summary>
    /// Renders this draft, stores what rendered, and records where it went.
    /// </summary>
    /// <remarks>
    /// <b>Every step is allowed to fail on its own and none of them may fail the caller.</b> The
    /// PDF, the DOCX and the cover letter are three independent renders and three independent
    /// uploads, so the ordinary partial outcome - a backend that threw on one document, a role
    /// assignment that has not propagated yet - is recorded as what it is rather than discarded
    /// wholesale. <c>RenderedDocuments</c> reads a null member as "nothing to say about this
    /// file" and never as "clear the one on the row", which is what makes recording a partial
    /// result safe to do repeatedly.
    ///
    /// <b>The hash is paired with the PDF and with nothing else.</b> <c>CvSha256</c> sits beside
    /// <c>CvBlobPath</c> and describes the bytes at it; carrying the DOCX's hash there when the
    /// PDF had failed would leave a row asserting that the file at a path it does not have hashes
    /// to something. A checksum that describes a different file is worse than no checksum,
    /// because the whole point of storing it is that somebody may check a document against it
    /// after it has been sent.
    ///
    /// <b>Nothing is written when nothing was stored.</b> <c>RecordRenderedAsync</c> would answer
    /// true and change no column, but only after a round trip to a database billed on wall-clock
    /// time - which is the round trip <c>RenderedDocuments.IsEmpty</c> exists to let a caller
    /// skip.
    /// </remarks>
    private static async Task RenderAsync(
        IApplicationPackStore? packs,
        ApplicationDocumentRepository documents,
        long profileId,
        string? candidateName,
        StoredApplication stored,
        ILogger logger,
        CancellationToken ct)
    {
        if (packs is null)
        {
            return;
        }

        var cvPdf = await StoreAsync(
            packs, profileId, candidateName, stored, PackDocument.CurriculumVitae, PackFormat.Pdf, logger, ct);

        var cvDocx = await StoreAsync(
            packs, profileId, candidateName, stored, PackDocument.CurriculumVitae, PackFormat.Docx, logger, ct);

        var letterPdf = await StoreAsync(
            packs, profileId, candidateName, stored, PackDocument.CoverLetter, PackFormat.Pdf, logger, ct);

        var rendered = new RenderedDocuments
        {
            CvBlobPath = cvPdf?.BlobPath,
            CvDocxBlobPath = cvDocx?.BlobPath,
            CoverLetterBlobPath = letterPdf?.BlobPath,
            CvSha256 = cvPdf?.Sha256,
        };

        if (rendered.IsEmpty)
        {
            return;
        }

        try
        {
            await documents.RecordRenderedAsync(profileId, stored.Id, rendered, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Error rather than warning, and the only line in this path that is. The two above
            // report a file that was never made; this reports files that exist, were paid for and
            // now have nothing pointing at them - and the reference validation this can throw on
            // is one ApplicationPackFile promises never to produce, so a throw here is a bug in
            // this system rather than a service having a bad minute. It still does not fail the
            // request: the draft is saved, it is what the candidate reads, and a re-generation
            // re-renders and records again.
            logger.LogError(
                ex,
                "Rendered files for draft {DocumentId} were stored but could not be recorded "
                + "against it. The blobs exist and no row references them; regenerating will "
                + "write them again.",
                stored.Id);
        }
    }

    /// <summary>
    /// Renders one document in one format and uploads it. Null where either half did not happen.
    /// </summary>
    /// <remarks>
    /// <b>Only the render is wrapped, because only the render can throw.</b> A renderer walks
    /// model output, and the failures it can have - a construct the AST maps onto nothing, a font
    /// resolver that did not install, an OOXML part the SDK refused - are exactly the failures a
    /// generated document is most likely to produce and least likely to have been tested against.
    /// The upload below needs no guard of its own: <c>IApplicationPackStore</c> answers null for
    /// every storage failure by contract, which is the half of this that was already safe.
    ///
    /// The title is the document's own metadata rather than its filename: it is what a PDF reader
    /// puts in a window title and what Word shows in properties. The filename is
    /// <c>ApplicationPackFile</c>'s, derived from the candidate's name, and the two are
    /// deliberately different - one is read by a person looking at an open document, the other by
    /// a recruiter looking at a list of forty.
    /// </remarks>
    private static async Task<StoredPackFile?> StoreAsync(
        IApplicationPackStore packs,
        long profileId,
        string? candidateName,
        StoredApplication stored,
        PackDocument document,
        PackFormat format,
        ILogger logger,
        CancellationToken ct)
    {
        var markdown = document == PackDocument.CoverLetter
            ? stored.CoverLetterMarkdown
            : stored.CurriculumVitaeMarkdown;

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var kind = document == PackDocument.CoverLetter ? "Cover letter" : "CV";
        byte[] content;

        try
        {
            content = format == PackFormat.Docx
                ? MarkdownDocxRenderer.Render(markdown, $"{kind} - {stored.PostingTitle}")
                : MarkdownPdfRenderer.Render(markdown, $"{kind} - {stored.PostingTitle}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warning rather than error, and the draft id rather than the markdown: the document
            // itself is saved and is what the candidate reads, and this log line is read by
            // somebody deciding whether a renderer has a bug rather than by somebody recovering
            // data. The markdown is a tailored CV and does not belong in a log at all.
            logger.LogWarning(
                ex,
                "Could not render the {Kind} of draft {DocumentId} as {Format}. The draft is "
                + "saved and the markdown is the record; the pack will report that no file of "
                + "that format is available for it.",
                kind,
                stored.Id,
                format);

            return null;
        }

        return await packs.StoreAsync(
            new PackFileRequest
            {
                ProfileId = profileId,
                DocumentId = stored.Id,
                Document = document,
                Format = format,
                Content = content,
                CandidateName = candidateName,
            },
            ct);
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
