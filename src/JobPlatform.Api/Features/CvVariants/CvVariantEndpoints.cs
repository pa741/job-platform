using System.Security.Claims;
using JobPlatform.Ai.Extraction;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Applications;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Api.Features.CvVariants;

/// <summary>
/// The candidate's own CV library: what they wrote, what it is called, and what is still in use.
/// </summary>
/// <remarks>
/// <b>This feature exists because a model wrote a sentence no candidate would.</b> Asked what else
/// an employer should know, the application writer answered with the candidate's citizenship -
/// correctly, out of their own summary - and added "I am an AI and they should have seen this". It
/// was stored, served through the pack, and one form submission away from a real employer.
/// <c>DraftedAnswerCatalog.IsCandidateVoice</c> now drops that class of sentence, and a guard is a
/// net under a trapeze. This is the other half: the CV is written by the person whose name is on
/// it, and a pass chooses among what they wrote.
///
/// <b>So there is no "regenerate", no "improve with AI", and no route anywhere in this folder that
/// writes a variant's markdown from a model.</b> That is the feature rather than an omission, and
/// it is the thing most likely to be added back by somebody being helpful - a staleness notice is
/// exactly the place a "fix these for me" button looks obvious. It must not be built: rewriting the
/// candidate's document with a model is what this change removes, and doing it on a nudge, or worse
/// on a timer, would put the model back into the one document it was taken out of. The correct
/// response to a stale CV is a sentence on this page and a person deciding whether the change was
/// one their CV needed to mention. Every model call this feature makes <i>reads</i>: the extractor
/// turns markdown into concept keys for selection, and it has no path back to
/// <c>CvVariants.Markdown</c>.
///
/// <b>A variant's concepts feed selection only.</b> They are stored in <c>CvVariantConcepts</c> by
/// <see cref="CvVariantPublisher"/> and they must never reach <c>ProfileConcepts</c>, never move a
/// match score, and never widen what the candidate is judged to have. Nothing in this file returns
/// them, and nothing here calls anything that writes the profile's own rows.
///
/// <b>There is no delete, and the archive route is where that is stated.</b> A submission records
/// <c>CvVariantId</c>, and "what exactly did we send them" is a question about a document that was
/// uploaded into somebody else's system - so it has to stay answerable after the CV has been
/// retired, which is the ordinary end of a CV's life. Archiving removes a variant from selection
/// and from nothing else.
///
/// <b>Never <see cref="AuthSetup.PublicReadPolicy"/>.</b> <c>Api:AllowAnonymousReads</c> exists to
/// open the posting corpus, which is public text. A CV is the opposite: it is somebody's employment
/// history in their own words, and it is the single most personal document this system holds.
///
/// <b>No output cache, and this must never join a client's bootstrap sequence.</b> Per-principal
/// and mutable, so a shared cache keyed on a URL with no user in it is how one person is served
/// another's CVs. It reads and writes Azure SQL, bounded exactly as the profile's routes are:
/// fetched when a page opens, written when somebody presses save. Never a polling path.
///
/// <b>The repository is built from the context rather than resolved</b>, so this feature is a
/// folder plus one line in <see cref="EndpointGroupExtensions"/> and nothing else - the rule
/// <see cref="IEndpointGroup"/> is written around, which exists precisely so that adding a feature
/// never means editing <c>Program.cs</c>. <c>CvVariantRepository</c> holds no state beyond the
/// context it wraps, and the context is the scoped service either way, so the two spellings differ
/// only in where the registration would have to live. <c>ProfileEndpoints</c> already reaches the
/// context directly from a handler for its delete, and <c>QuestionEndpoints</c> for the employer
/// lookup its repositories do not project.
/// </remarks>
public sealed class CvVariantEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/cv-variants")
            .WithTags("CV variants")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListCvVariants")
            .WithSummary("The calling principal's CV library, with how much of it predates their last profile change.");

        group.MapGet("/{id:long}", GetAsync)
            .WithName("GetCvVariant")
            .WithSummary("One CV, with the markdown the candidate wrote.");

        group.MapPost("/", CreateAsync)
            .WithName("CreateCvVariant")
            .WithSummary("Stores a new CV in the candidate's own words, then renders and reads it.");

        group.MapPut("/{id:long}/label", RenameAsync)
            .WithName("RenameCvVariant")
            .WithSummary("Changes what a CV is called, and nothing else about it.");

        group.MapPut("/{id:long}/markdown", ReauthorAsync)
            .WithName("ReauthorCvVariant")
            .WithSummary("Replaces a CV's words with the candidate's own, dates them, and renders again.");

        group.MapPut("/{id:long}/archived", SetArchivedAsync)
            .WithName("SetCvVariantArchived")
            .WithSummary("Retires a CV from selection, or puts it back. There is no delete.");
    }

    /// <summary>
    /// The whole library, with the nudge and the room, from one read.
    /// </summary>
    /// <remarks>
    /// <b>The staleness sentence and the per-row badges ask the same two questions, which is the
    /// point of computing neither here.</b> <c>CvVariantLibrary.Staleness</c> skips an archived
    /// variant and then asks <c>CvVariant.PredatesProfileUpdate</c>, and a row is flagged by that
    /// same pair - see <c>CvVariantMapping</c> - so "three of your CVs predate your last profile
    /// change" cannot appear over four flagged rows. A count done in this handler would be a second
    /// implementation of a rule whose disagreement nobody notices, because a badge that is missing
    /// looks exactly like a row that is fine.
    ///
    /// <b>Archived variants are listed rather than filtered out.</b> They are what makes a past
    /// application explicable and they are where a label comes back from, so a page that hid them
    /// would leave somebody unable to explain a CV they sent or to reuse the name of the one they
    /// replaced. What separates them for the reader is <c>isArchived</c> and <c>isSendable</c>, and
    /// the repository's ordering already puts the live ones first.
    ///
    /// An empty library rather than a 404 for a principal with no profile, following the submission
    /// and question lists: somebody who has not filled the form in has no CVs, which is a complete
    /// and unsurprising answer and not the "something is wrong" path.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobsDbContext db,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.Ok(CvVariantMapping.EmptyLibrary());
        }

        return TypedResults.Ok((await Library(db).ListAsync(profileId.Value, ct)).ToResponse());
    }

    /// <summary>
    /// One CV, including the words, which the list deliberately does not carry.
    /// </summary>
    /// <remarks>
    /// <b>This read ignores <c>IsArchived</c>, and that is the same decision the repository makes
    /// one layer down.</b> Archiving governs selection and nothing else: an application made last
    /// year names this row by id, and a system that answered "that CV is archived" to somebody
    /// asking what they sent would have lost the record it exists to keep.
    ///
    /// 404 rather than 403 for a variant belonging to somebody else. The repository takes the
    /// profile id in the predicate, so "not yours" and "no such CV" are one answer by construction -
    /// a 403 would confirm that a CV with this id exists and belongs to someone, which is a fact
    /// about another person's job search.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user,
        long id,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobsDbContext db,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var variant = await Library(db).GetAsync(profileId.Value, id, ct);

        return variant is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(variant.ToDetail(await ProfileUpdatedAsync(db, profileId.Value, ct)));
    }

    /// <summary>
    /// Stores a new CV, then renders it and reads it for concepts.
    /// </summary>
    /// <remarks>
    /// <b>The bounds are not restated here.</b> <c>CvVariant.Create</c> is the only constructor that
    /// validates and the repository calls it, so a blank CV, a label nobody could pick a CV by and a
    /// twenty-thousand-character paste are refused in one place and mapped onto a 400 in another.
    /// A validator in this file would be a second copy of numbers that have already drifted from a
    /// column width once in this codebase, and the failure after a drift is a write the database
    /// refuses - a 500 on somebody's save, with their document lost, on a page whose whole purpose
    /// is that they typed one.
    ///
    /// <b>The cap and the label rule are the repository's, and the refusal names the numbers.</b>
    /// "No" on its own leaves a person with six CVs guessing what the limit is and why archiving is
    /// the answer; the count and the cap together say what to do next. Both are read from
    /// <c>CvVariantLibrary</c> rather than counted here, and the extra read happens only on the
    /// refusal path.
    ///
    /// <b>The render and the extraction cannot fail this write.</b> See
    /// <see cref="CvVariantPublisher"/>: the markdown is the record and both are derived from it, so
    /// a deployment with no storage or no AI provider stores the CV and answers with a row that says
    /// plainly it is not sendable yet.
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user,
        CreateCvVariantRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobsDbContext db,
        [FromServices] TimeProvider time,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct,
        [FromServices] CvVariantRenderer? renderer = null,
        [FromServices] ICvVariantExtractor? extractor = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        // A CV is rendered into a directory under the profile that owns it and its concepts are
        // stored against that owner, so there is nowhere to put one belonging to nobody. 404 rather
        // than an implicit profile: creating one here would store an employment history the person
        // has not entered.
        if (profileId is null)
        {
            return NoProfile();
        }

        var variants = Library(db);
        CvVariantWriteResult result;

        try
        {
            result = await variants.CreateAsync(
                profileId.Value, request.Label, request.Markdown, time.GetUtcNow(), ct);
        }
        catch (ArgumentException ex)
        {
            return Refused(ex);
        }

        if (result.Variant is not { } stored)
        {
            return await RejectedAsync(variants, profileId.Value, result, request.Label, ct);
        }

        var candidate = await CandidateAsync(db, profileId.Value, ct);

        var published = await CvVariantPublisher.PublishAsync(
            variants,
            profileId.Value,
            stored,
            candidate.FullName,
            renderer,
            extractor,
            loggerFactory.CreateLogger<CvVariantEndpoints>(),
            ct);

        return TypedResults.Created(
            $"/api/v1/cv-variants/{published.Id}", published.ToDetail(candidate.UpdatedUtc));
    }

    /// <summary>
    /// Changes what a CV is called, and touches nothing else.
    /// </summary>
    /// <remarks>
    /// <b>A rename must not move <c>AuthoredAtUtc</c>, which is why this is its own route rather
    /// than a field on a general update.</b> That timestamp is the whole of the staleness signal and
    /// half of the render comparison: moved by a rename, a document nobody has edited looks freshly
    /// written and last week's PDF is quietly marked current, which is a paragraph the candidate may
    /// have deleted going to an employer under their name. The repository keeps the pairing;
    /// splitting the routes is what keeps a client from asking for the broken combination.
    ///
    /// <b>Nothing is re-rendered and nothing is re-read.</b> The words have not changed, so the
    /// stored files still describe them and the stored concepts are still what the document says -
    /// and the label is never in either. Re-rendering here would spend an upload to produce a
    /// byte-identical file; re-extracting would spend a model call to store the same rows.
    /// </remarks>
    private static async Task<IResult> RenameAsync(
        ClaimsPrincipal user,
        long id,
        RenameCvVariantRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobsDbContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var variants = Library(db);
        CvVariantWriteResult result;

        try
        {
            result = await variants.RenameAsync(profileId.Value, id, request.Label, ct);
        }
        catch (ArgumentException ex)
        {
            return Refused(ex);
        }

        return result.Variant is { } stored
            ? TypedResults.Ok(stored.ToDetail(await ProfileUpdatedAsync(db, profileId.Value, ct)))
            : await RejectedAsync(variants, profileId.Value, result, request.Label, ct);
    }

    /// <summary>
    /// Replaces the words with the candidate's own, dates them, and renders again.
    /// </summary>
    /// <remarks>
    /// <b>The words arrive in the request body and from nowhere else.</b> This is the route a
    /// "regenerate" button would have been hung on, and there is deliberately nothing here to hang
    /// it on: no posting id, no instruction field, no flag that would let a caller ask for text
    /// rather than supply it.
    ///
    /// <b>The stored files and the hash survive the edit rather than being cleared.</b> The file at
    /// that path is still the file a previous application uploaded, and blanking the pointer would
    /// make that application unexplainable in order to keep a row tidy. The variant leaves selection
    /// through the timestamps instead - <c>AuthoredAtUtc</c> moves past <c>RenderedAtUtc</c>,
    /// <c>IsRenderCurrent</c> goes false - and comes back the moment the render this route performs
    /// stamps a newer one. If that render fails, the response says so through <c>isSendable</c>
    /// rather than by failing the save.
    ///
    /// <b>Saving an unchanged document is still an edit</b>, and the repository treats it as one on
    /// purpose: somebody who reads a CV the page has flagged as stale, decides it is still accurate
    /// and presses save has answered the nudge, and converging on "nothing changed" would leave the
    /// notice standing after they had dealt with it.
    /// </remarks>
    private static async Task<IResult> ReauthorAsync(
        ClaimsPrincipal user,
        long id,
        ReauthorCvVariantRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobsDbContext db,
        [FromServices] TimeProvider time,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct,
        [FromServices] CvVariantRenderer? renderer = null,
        [FromServices] ICvVariantExtractor? extractor = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var variants = Library(db);
        CvVariantWriteResult result;

        try
        {
            result = await variants.ReauthorAsync(
                profileId.Value, id, request.Markdown, time.GetUtcNow(), ct);
        }
        catch (ArgumentException ex)
        {
            return Refused(ex);
        }

        if (result.Variant is not { } stored)
        {
            return await RejectedAsync(variants, profileId.Value, result, label: null, ct);
        }

        var candidate = await CandidateAsync(db, profileId.Value, ct);

        var published = await CvVariantPublisher.PublishAsync(
            variants,
            profileId.Value,
            stored,
            candidate.FullName,
            renderer,
            extractor,
            loggerFactory.CreateLogger<CvVariantEndpoints>(),
            ct);

        return TypedResults.Ok(published.ToDetail(candidate.UpdatedUtc));
    }

    /// <summary>
    /// Retires a CV from selection, or puts it back into use.
    /// </summary>
    /// <remarks>
    /// <b>One route in two directions rather than an archive verb and a delete verb, and the
    /// absent one is the argument.</b> There is no <c>DELETE</c> on this resource and there must not
    /// be: <c>Submissions.CvVariantId</c> names the row, so an application made last year is
    /// explained by a document that has to still exist, byte for byte, under the hash stored beside
    /// it. Archiving takes a variant out of selection, out of the cap's count and out of the
    /// filtered unique index - so the CV that replaces it may take its name - and it changes nothing
    /// else. The markdown, the paths and the digest stay exactly where they were.
    ///
    /// <b>Unarchiving is refused where archiving never is, and the asymmetry is real.</b> A CV
    /// brought back occupies the cap, and the name it was archived under is very often the name of
    /// the CV that replaced it - so a library at six has no room for a seventh however it arrives,
    /// and a label in use is a label in use. Both come back as conflicts naming the numbers rather
    /// than as an exception on a page somebody is only pressing a toggle on.
    ///
    /// <b>Its dates are untouched in both directions.</b> A CV brought back is as old as it was, and
    /// the staleness nudge should say so on the same afternoon: unarchiving is not authoring, and
    /// nothing here may make an old document look new.
    /// </remarks>
    private static async Task<IResult> SetArchivedAsync(
        ClaimsPrincipal user,
        long id,
        ArchiveCvVariantRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobsDbContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var variants = Library(db);

        // Both directions are idempotent on the repository's side - the answer is the state and not
        // the transition - so a client that presses the toggle twice gets the row it asked for
        // rather than a refusal it can do nothing with.
        var result = request.Archived
            ? await variants.ArchiveAsync(profileId.Value, id, ct)
            : await variants.UnarchiveAsync(profileId.Value, id, ct);

        return result.Variant is { } stored
            ? TypedResults.Ok(stored.ToDetail(await ProfileUpdatedAsync(db, profileId.Value, ct)))
            : await RejectedAsync(variants, profileId.Value, result, label: null, ct);
    }

    /// <summary>
    /// The library, over the context this request already has.
    /// </summary>
    /// <remarks>
    /// See the remarks on the class. <c>CvVariantRepository</c> is a wrapper over the scoped
    /// <see cref="JobsDbContext"/> and holds nothing else, so constructing it here and resolving it
    /// from the container are the same object graph with the registration in a different file - and
    /// the file it would otherwise be in is the one <see cref="IEndpointGroup"/> exists to keep
    /// features out of.
    /// </remarks>
    private static CvVariantRepository Library(JobsDbContext db) => new(db);

    /// <summary>
    /// The two facts about the person that a CV write needs, in one query.
    /// </summary>
    /// <remarks>
    /// <b>Two columns of the profile, never the row.</b> The name is what
    /// <c>ApplicationPackFile.VariantBlobPath</c> spells the filename from, and the timestamp is
    /// what a variant's staleness is measured against; the rest of a profile is an employment
    /// history this feature has no business holding in memory, and
    /// <c>CandidateProfileRepository.GetAsync</c> fetches all of it across several queries because
    /// its callers need all of it. Read from the context for the reason <c>QuestionEndpoints</c>
    /// reads a company id from it: no projection on the repository carries these, and asking for
    /// them through one that does not would mean widening it.
    ///
    /// Keyed on the profile id the caller has already resolved from the token, which is the
    /// authorisation boundary <c>CandidateProfileRepository</c> states as a type by taking a subject
    /// id and never a profile id. Nothing in this file gets a profile id from a route.
    /// </remarks>
    private static async Task<Candidate> CandidateAsync(
        JobsDbContext db, long profileId, CancellationToken ct)
        => await db.CandidateProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == profileId)
            .Select(profile => new Candidate(profile.FullName, (DateTimeOffset?)profile.UpdatedUtc))
            .FirstOrDefaultAsync(ct)
            ?? new Candidate(null, null);

    /// <summary>What staleness is measured against, for the routes that need only that half.</summary>
    private static async Task<DateTimeOffset?> ProfileUpdatedAsync(
        JobsDbContext db, long profileId, CancellationToken ct)
        => (await CandidateAsync(db, profileId, ct)).UpdatedUtc;

    /// <summary>
    /// A write the library refused, answered with the numbers behind the refusal.
    /// </summary>
    /// <remarks>
    /// <b>409 rather than 400 for both, because neither is a fault in the request.</b> The label was
    /// free yesterday and the cap was not spent last week; what refused the write is the state of
    /// the library, and a caller told "bad request" would go looking at what it sent. A 400 is
    /// reserved here for a document or a label that could never be stored, which is what
    /// <see cref="Refused"/> answers.
    ///
    /// <b>The count is read only on this path.</b> Naming the cap without the count says less than
    /// it looks - a person who has six CVs and is told the limit is six still has to work out that
    /// the archived ones do not count - so the extra query buys the sentence that tells them what to
    /// do, and it is paid for only by somebody who has just been refused.
    ///
    /// <c>Written</c> reaching here is a caller that did not check its own result, and it answers
    /// 500: silently returning a refusal for a write that succeeded would be worse than a fault.
    /// </remarks>
    private static async Task<IResult> RejectedAsync(
        CvVariantRepository variants,
        long profileId,
        CvVariantWriteResult result,
        string? label,
        CancellationToken ct)
        => result.Outcome switch
        {
            CvVariantWriteOutcome.NotFound => TypedResults.NotFound(),
            CvVariantWriteOutcome.LibraryFull => Conflict(await FullAsync(variants, profileId, ct)),
            CvVariantWriteOutcome.LabelTaken => Conflict(Taken(label)),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };

    /// <summary>The cap, the count, and what archiving actually does - so "no" is actionable.</summary>
    private static async Task<string> FullAsync(
        CvVariantRepository variants, long profileId, CancellationToken ct)
    {
        var inUse = CvVariantLibrary.ActiveCount((await variants.ListAsync(profileId, ct)).Variants);

        return $"You are keeping {inUse} CVs, and the library holds "
            + $"{CvVariantLimits.MaxPerProfile}. Archive one to make room: archiving retires a CV "
            + "from selection and keeps its words, its rendered files and their hash, so the "
            + "applications that sent it stay explicable. Nothing here deletes a CV. The cap is "
            + "about how many documents one person will keep current rather than about storage - a "
            + "stale CV looks exactly like a fresh one in a picker, and the system will send it.";
    }

    /// <summary>Why a name is unavailable, and the condition under which it comes back.</summary>
    private static string Taken(string? label)
        => (string.IsNullOrWhiteSpace(label)
            ? "Another CV you are using already answers to that name. "
            : $"Another CV you are using is already called '{label.Trim()}'. ")
        + "Names are compared with case and spacing folded away, because two CVs a reader cannot "
        + "tell apart are two CVs nobody can choose between - and the pack explains which one it "
        + "sent in the label's own words. Archived CVs do not hold their names, so a rewrite may "
        + "take the old one's once the old one is archived.";

    /// <summary>
    /// What <c>CvVariant.Create</c> or <c>WithMarkdown</c> refused, as a 400 the person can read.
    /// </summary>
    /// <remarks>
    /// <b>The message is Core's own, deliberately.</b> Core states the bound in the place the bound
    /// lives, and restating it here would be a second copy free to fall out of step with the
    /// constant - and with the column - the first time a number moves. The parameter name the
    /// exception appends is kept rather than stripped: it names the field that was refused, which is
    /// the one thing a client needs in order to put the cursor in the right box.
    /// </remarks>
    private static IResult Refused(ArgumentException ex)
        => TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Conflict(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// There is no profile, so there is nothing for a CV to belong to.
    /// </summary>
    /// <remarks>
    /// 400 with an explanation rather than a 404, because the thing that is missing is not the route
    /// and not the CV: it is a prerequisite the person can satisfy in a minute on another page. A
    /// bare 404 on a create reads as a bug in the client.
    /// </remarks>
    private static IResult NoProfile()
        => TypedResults.Problem(
            detail: "There is no profile for this principal yet, and a CV belongs to one - it is "
                + "stored against it, rendered under it and compared against its last change. Fill "
                + "in the profile form first.",
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>The candidate's name, for the filename, and when their profile last moved.</summary>
    private sealed record Candidate(string? FullName, DateTimeOffset? UpdatedUtc);
}
