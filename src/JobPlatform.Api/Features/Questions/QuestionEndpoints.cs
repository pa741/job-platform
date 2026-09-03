using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Api.Features.Questions;

/// <summary>
/// The questions this system refused to answer, and where a person answers them.
/// </summary>
/// <remarks>
/// <b>This is the other half of abstention, and without it abstention is a loop.</b> Resolution
/// refuses by default - below the confidence floor, on a sensitive field with no stored answer,
/// or where an option set will not map cleanly - because a confident near-miss on somebody's
/// application is read as a statement they made rather than as a bug in a tool they were using.
/// That is only defensible if the interruption goes somewhere, and this is where. With no surface
/// here a run parks an application for a missing answer, offers the same advert next run, parks
/// it again, and does so forever.
///
/// <b>The answer is stamped as the candidate's own, and this is the only write in the system that
/// may say so.</b> Everything arriving over the tool surface is <see cref="FormAnswerSource.Client"/>;
/// <see cref="FormAnswerSource.Candidate"/> is reachable from here and nowhere else. The source
/// is read from the token rather than from the body, which is why
/// <see cref="AnswerQuestionRequest"/> has no field for it - a parameter a caller could fill in is
/// how an agent's inference gets recorded as a person's own words.
///
/// <b>Nothing here reaches an employer.</b> Answering is a write about the candidate: it stores
/// what they said and closes the question, and the advert parked on it becomes eligible again
/// because it has left the unanswered set. The next unattended pass is what picks it up.
///
/// <b>Never <see cref="AuthSetup.PublicReadPolicy"/>.</b> <c>Api:AllowAnonymousReads</c> exists to
/// open the posting corpus, which is public text. What an employer asked this person and what
/// they answered is the opposite of that - it is the most sensitive thing this system holds,
/// because a sensitive value can exist only where somebody typed it.
///
/// <b>No output cache, and this must never join a client's bootstrap sequence.</b> Per-principal
/// and mutable, so a shared cache keyed on a URL with no user in it is how one person is served
/// another's queue; and it reads Azure SQL, bounded exactly like the submissions' - fetched when
/// a page opens, written when somebody answered something. Never a polling path.
/// </remarks>
public sealed class QuestionEndpoints : IEndpointGroup
{
    /// <summary>
    /// How much of the queue one read returns.
    /// </summary>
    /// <remarks>
    /// A bound because the repository takes one, not because this is paged: the queue is drained
    /// rather than browsed, and a person with a hundred outstanding questions has a problem a
    /// second page would not help with. The same ceiling <c>list_open_questions</c> clamps to, so
    /// the dashboard and an agent cannot disagree about what "the queue" is.
    /// </remarks>
    private const int QueuePage = 100;

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/questions")
            .WithTags("Questions")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListOpenQuestions")
            .WithSummary("The questions waiting on the calling principal, oldest first.");

        group.MapPost("/{id:long}/answer", AnswerAsync)
            .WithName("AnswerQuestion")
            .WithSummary("Records what the candidate answered, as their own, and closes the question.");
    }

    /// <summary>
    /// The caller's queue, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first inverts every other list in this API, and that is the point: those are
    /// histories, where the last thing that happened is the interesting one, and this is a queue
    /// to be drained. The question that has held an application back for three days is the one to
    /// put in front of somebody. The ordering is the repository's, so the dashboard and
    /// <c>list_open_questions</c> cannot disagree about it.
    ///
    /// An empty list rather than a 404 for a principal with no profile, following the submission
    /// list: somebody who has never filled the form in has nothing waiting on them, which is a
    /// complete and unsurprising answer.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] OpenQuestionRepository questions,
        [FromServices] SubmissionRepository submissions,
        [FromServices] JobsDbContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return Empty();
        }

        var rows = await questions.ListUnansweredAsync(profileId.Value, QueuePage, ct);

        if (rows.Count == 0)
        {
            return Empty();
        }

        var postingIds = rows.Select(row => row.PostingId).OfType<long>().Distinct().ToList();

        // Both of these are skipped outright for a queue of questions that name no advert. They
        // answer "which employer is this filed against" and "what is this holding back", and
        // neither has an answer without a posting - so a person writing themselves a note costs
        // one query rather than three.
        var employers = postingIds.Count == 0
            ? []
            : await EmployersAsync(db, postingIds, ct);

        var parked = postingIds.Count == 0
            ? []
            : await ParkedByPostingAsync(submissions, profileId.Value, time.GetUtcNow(), ct);

        return TypedResults.Ok(new
        {
            items = rows
                .Select(row => new OpenQuestionResponse
                {
                    QuestionId = row.Id,
                    PostingId = row.PostingId,
                    PostingTitle = row.PostingTitle,
                    Company = row.Company,
                    CompanyId = Lookup(employers, row.PostingId),
                    RunId = row.RunId,
                    QuestionText = row.QuestionText,
                    Options = row.Options,
                    Sensitive = Sensitive(row),
                    AskedAtUtc = row.AskedAtUtc,
                    Parked = Lookup(parked, row.PostingId),
                })
                .ToList(),
        });
    }

    /// <summary>
    /// Records the answer as the candidate's own, closes the question, and says what that freed.
    /// </summary>
    /// <remarks>
    /// <b>The order of the two writes is the part worth reading.</b> The question's state is
    /// checked before the answer is stored, so the ordinary "somebody answered this in another
    /// tab" case answers 409 having written nothing - which is what lets the dashboard say the
    /// first answer stands and nothing was overwritten. The repository's own
    /// <see cref="OpenQuestionAnswerResult.AlreadyClosed"/> is still honoured after the write,
    /// because the check cannot close the race it narrows; in that case the answer is stored and
    /// supersedes, which is the truthful outcome for a person who typed one.
    ///
    /// <b>The scope's ids come from the question's own advert and never from the body.</b> The
    /// client picks how widely the answer carries; the company and posting behind that choice are
    /// resolved here. That is the whole reason a company-scoped answer is filed against
    /// <c>Companies.Id</c>: the id is not something a caller can name, so it cannot be named
    /// wrongly.
    ///
    /// <b>Sensitivity is taken from the question, and a caller cannot loosen it.</b> The stored
    /// flag or the wording, whichever says yes - so a right-to-work question is stored as
    /// sensitive whether or not anything ticked a box when it was raised.
    /// </remarks>
    private static async Task<IResult> AnswerAsync(
        ClaimsPrincipal user,
        long id,
        AnswerQuestionRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] OpenQuestionRepository questions,
        [FromServices] FormAnswerRepository answers,
        [FromServices] SubmissionRepository submissions,
        [FromServices] JobsDbContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        // Whitespace is not an answer, and storing it as one would tell every later resolution
        // that this question is settled. "Prefer not to say" is a value; nothing is not.
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            return Refused(
                "value is required. If you are declining to answer, record what would actually go "
                + "in the box - 'Prefer not to say' is an answer and closes the question; blank is "
                + "not, and leaves the advert parked.");
        }

        // Refused rather than truncated, and checked here so the caller gets a 400 it can show
        // rather than the exception FormAnswer.Create throws on the same bound.
        if (request.Value.Trim().Length > FormAnswerLimits.MaxValueLength)
        {
            return Refused(
                $"The answer is longer than {FormAnswerLimits.MaxValueLength} characters and is "
                + "refused rather than shortened - a truncated answer is typed into an employer's "
                + "form and reads as a statement rather than as a bug. Shorten it deliberately.");
        }

        if (request.Name is { Length: > 0 } && request.Name.Trim().Length > FormAnswerLimits.MaxNameLength)
        {
            return Refused(
                $"The name is longer than {FormAnswerLimits.MaxNameLength} characters. A name is a "
                + "key, e.g. 'notice_period'; the wording a person reads is the question itself.");
        }

        if (!Enum.TryParse<AnswerScope>(request.Scope, ignoreCase: true, out var scope)
            || !Enum.IsDefined(scope))
        {
            return Invalid<AnswerScope>("scope", request.Scope);
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        // The repository is scoped to the profile id, so an id from the route can never reach a
        // stranger's queue - and "not yours" is indistinguishable from "does not exist". A 403
        // here would confirm that a question with this id exists and belongs to somebody, which
        // is a fact about another person's job search.
        var question = await questions.GetAsync(profileId.Value, id, ct);

        if (question is null)
        {
            return TypedResults.NotFound();
        }

        if (!question.IsOpen)
        {
            return AlreadyClosed();
        }

        int? companyId = null;
        long? postingId = null;

        if (scope is AnswerScope.Posting)
        {
            if (question.PostingId is null)
            {
                return Refused(
                    "This question names no advert, so there is nothing narrower than everywhere "
                    + "to file it under. Record it as 'Global'.");
            }

            postingId = question.PostingId;
        }

        if (scope is AnswerScope.Company)
        {
            companyId = question.PostingId is { } advert ? await EmployerAsync(db, advert, ct) : null;

            // Refused rather than quietly widened to Global. An employer-wide answer filed with
            // no employer applies to everybody, which is the "why do you want to work here"
            // failure with the safety removed - and silently storing something wider than what
            // was asked for is worse than saying no.
            if (companyId is null)
            {
                return Refused(
                    "The advert behind this question names no employer row, so a company-scoped "
                    + "answer has no employer to be filed against - and one filed against no "
                    + "employer applies to every employer. Record it for this advert only, or "
                    + "as 'Global' if it is true wherever it is asked.");
            }
        }

        var now = time.GetUtcNow();

        // Read before the writes, because what this answer released is a difference and not a
        // state: an advert parked for a missing answer that nothing was holding was already
        // eligible, and reporting it as freed by this answer would credit it with work it did
        // not do.
        var parked = await ParkedByPostingAsync(submissions, profileId.Value, now, ct);
        var heldBefore = await HeldAsync(questions, profileId.Value, parked.Keys, ct);

        var answer = FormAnswer.Create(
            // The stored wording, never one supplied by the caller: the hash is taken over this,
            // and hashing anything else would file the answer under a key the question it closes
            // was never written with.
            question.QuestionText,
            request.Value,
            scope,
            // Candidate, and only here. Read from the token rather than from the body - see the
            // remarks on AnswerQuestionRequest for why there is no field to read.
            FormAnswerSource.Candidate,
            now,
            request.Name,
            companyId,
            postingId,
            // The same reading the queue was shown under, so what somebody confirmed and what is
            // stored cannot disagree.
            sensitive: Sensitive(question));

        var (recorded, created) = await answers.RecordAsync(profileId.Value, answer, now, ct);

        var closed = await questions.AnswerAsync(profileId.Value, id, recorded.Answer.Id, now, ct);

        if (Rejected(closed) is { } refusal)
        {
            return refusal;
        }

        var released = await ReleasedAsync(
            questions, profileId.Value, question.PostingId, parked, heldBefore, ct);

        return TypedResults.Ok(new AnswerQuestionResponse
        {
            AnswerId = recorded.Answer.Id,
            Created = created,
            Scope = recorded.Answer.Scope.ToString(),
            Sensitive = recorded.Answer.Sensitive,
            AnsweredAtUtc = recorded.Answer.AnsweredAtUtc,
            ClosedQuestionId = question.Id,
            ReturnedToQueue = released.Returned,
            Note = Note(created, released),
        });
    }

    /// <summary>
    /// Which parked applications this answer freed, and what is still holding the advert it came from.
    /// </summary>
    /// <remarks>
    /// <b>Asked through <see cref="OpenQuestionRepository.ListUnansweredForPostingAsync"/>,
    /// which is the read written to agree with the queue predicate.</b> Deciding it here from "we
    /// just closed the question this advert raised" is the tempting shorthand and it is wrong
    /// twice over: an advert whose form raised three questions is not freed by the first answer,
    /// and - because one wording is one row however many adverts asked it - the question that
    /// frees an advert is frequently one raised by a different advert entirely. Either mistake
    /// reports a release that has not happened, and somebody then waits for a run to pick up an
    /// advert that is still held, with nothing anywhere saying why.
    ///
    /// <b>Restricted to what was held before the write, which is why this takes a snapshot.</b>
    /// A parked row that nothing was holding was already eligible; listing it as returned by this
    /// answer would credit the answer with work it did not do, and the receipt exists to say what
    /// changed.
    ///
    /// The set is the candidate's parked-on-a-missing-answer submissions, which is a handful by
    /// construction - a run parks what it could not fill in, and each park is one advert. There
    /// is no bulk form of this read, and inventing one here would be a third spelling of a rule
    /// that already has two.
    ///
    /// <b>Nothing is unparked.</b> The park stands and the row still reads as parked; what
    /// changed is that the questions holding it are answered, which is the only fact the queue
    /// predicate consults. Clearing <c>UnparkedAtUtc</c> here would assert that an attempt had
    /// been made again, which nothing has done yet.
    /// </remarks>
    private static async Task<Release> ReleasedAsync(
        OpenQuestionRepository questions,
        long profileId,
        long? raisedBy,
        Dictionary<long, ParkedApplicationResponse> parked,
        IReadOnlyList<long> heldBefore,
        CancellationToken ct)
    {
        var returned = new List<ParkedApplicationResponse>();
        var outstanding = 0;

        foreach (var posting in heldBefore)
        {
            var waiting = await questions.ListUnansweredForPostingAsync(profileId, posting, ct);

            if (waiting.Count == 0)
            {
                returned.Add(parked[posting]);
            }
            else if (posting == raisedBy)
            {
                outstanding = waiting.Count;
            }
        }

        return new Release(returned, outstanding);
    }

    /// <summary>The parked adverts that something unanswered is currently holding.</summary>
    private static async Task<IReadOnlyList<long>> HeldAsync(
        OpenQuestionRepository questions,
        long profileId,
        IEnumerable<long> postings,
        CancellationToken ct)
    {
        var held = new List<long>();

        foreach (var posting in postings)
        {
            if ((await questions.ListUnansweredForPostingAsync(profileId, posting, ct)).Count > 0)
            {
                held.Add(posting);
            }
        }

        return held;
    }

    /// <summary>The applications parked on a missing answer, by the advert each is parked against.</summary>
    /// <remarks>
    /// <b>Filtered on <see cref="SubmissionRow.IsParked"/> rather than on the reason column
    /// alone.</b> Nothing on that table is cleared, so a row parked in March and applied to in
    /// April still carries the reason it was parked for; reading the column on its own reports a
    /// live application as held back forever. The pair is asked once, on the row, so this is not
    /// a third spelling of a predicate the queue already owns.
    ///
    /// <b>And on <see cref="ParkReason.MissingAnswer"/> specifically.</b> An advert parked for a
    /// captcha is not waiting on an answer - it comes back on the next run regardless - and
    /// showing it beside a question would say a person could unblock it by typing, which they
    /// cannot.
    ///
    /// One submission per posting is the unique index's guarantee, which is what makes a
    /// dictionary the right shape rather than a lookup.
    /// </remarks>
    private static async Task<Dictionary<long, ParkedApplicationResponse>> ParkedByPostingAsync(
        SubmissionRepository submissions, long profileId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await submissions.ListAsync(profileId, now, ct);

        return rows
            .Where(row => row.IsParked && row.ParkedReason is ParkReason.MissingAnswer)
            .ToDictionary(
                row => row.PostingId,
                row => new ParkedApplicationResponse
                {
                    SubmissionId = row.Id,
                    PostingId = row.PostingId,
                    PostingTitle = row.Title,
                    Company = row.Company,

                    // Non-null for every row that reads as parked: IsParked is the pair, and the
                    // reason cannot be set without the timestamp beside it.
                    ParkedAtUtc = row.ParkedAtUtc ?? row.CreatedAtUtc,
                });
    }

    /// <summary>
    /// The employer row behind each advert, where the advert names one.
    /// </summary>
    /// <remarks>
    /// Read from the context rather than through a repository because no projection in
    /// <c>JobMatchRepository</c> or <c>OpenQuestionRepository</c> carries <c>CompanyId</c> - they
    /// carry the name printed on the advert, which is precisely what a company-scoped answer must
    /// not be keyed on. Two columns for the postings already named by the queue page, following
    /// the profile delete's precedent for reaching the context directly from an endpoint.
    ///
    /// Adverts with no employer row are absent rather than present with a null, so a lookup miss
    /// and a stored null are one case: both mean the folding is unavailable and the company scope
    /// cannot be offered.
    /// </remarks>
    private static async Task<Dictionary<long, int>> EmployersAsync(
        JobsDbContext db, IReadOnlyList<long> postingIds, CancellationToken ct)
        => await db.JobPostings
            .AsNoTracking()
            .Where(posting => postingIds.Contains(posting.Id) && posting.CompanyId != null)
            .Select(posting => new { posting.Id, CompanyId = posting.CompanyId!.Value })
            .ToDictionaryAsync(row => row.Id, row => row.CompanyId, ct);

    private static async Task<int?> EmployerAsync(JobsDbContext db, long postingId, CancellationToken ct)
        => await db.JobPostings
            .AsNoTracking()
            .Where(posting => posting.Id == postingId)
            .Select(posting => posting.CompanyId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// What answering did that the receipt cannot otherwise show.
    /// </summary>
    /// <remarks>
    /// Only the two facts a person cannot see for themselves. "Saved" is the one thing about this
    /// write nobody needed telling, and an advert that was released is already named in
    /// <see cref="AnswerQuestionResponse.ReturnedToQueue"/> - what is invisible is a convergence
    /// that wrote nothing, and an advert still held by a question this answer was not about.
    /// </remarks>
    private static string? Note(bool created, Release released)
    {
        var notes = new List<string>();

        if (!created)
        {
            notes.Add(
                "That answer was already stored, word for word, so nothing was written and nothing "
                + "was superseded. The answer that stands is the one returned.");
        }

        if (released.StillOutstanding > 0)
        {
            notes.Add(
                $"The advert this question came from is still waiting on {released.StillOutstanding} "
                + $"other unanswered question{(released.StillOutstanding == 1 ? string.Empty : "s")}, "
                + "so it stays parked until those are answered too.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>
    /// The close's own outcome, where it disagrees with the check made before the write.
    /// </summary>
    /// <remarks>
    /// Every arm but <see cref="OpenQuestionAnswerResult.Answered"/> is a race this handler has
    /// already tested for, so all of them mean something changed underneath it. They are answered
    /// rather than swallowed: a caller told the answer was recorded when the question it was
    /// meant to close is somebody else's business now would have no way to find that out.
    /// </remarks>
    private static IResult? Rejected(OpenQuestionAnswerResult result)
        => result switch
        {
            OpenQuestionAnswerResult.Answered => null,
            OpenQuestionAnswerResult.AlreadyClosed => AlreadyClosed(),
            OpenQuestionAnswerResult.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };

    /// <summary>
    /// 409, and the dashboard reads it as convergence rather than as a failure.
    /// </summary>
    /// <remarks>
    /// The first close stands, because the row records that somebody was asked and what came
    /// back, and a second write would erase the timestamp the first is evidence of. Reporting it
    /// as a 500 or as a silent success would have somebody retrying a write that has already
    /// happened - for them, in another tab, or for a client acting on their behalf.
    /// </remarks>
    private static IResult AlreadyClosed()
        => TypedResults.Problem(
            detail: "That question has already been answered, and the first answer stands. Nothing "
                + "was overwritten. To change what you said, answer the question again when it is "
                + "next asked - answers supersede rather than replace.",
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Whether this is one only the candidate may state: the stored flag, or the wording.
    /// </summary>
    /// <remarks>
    /// <b>Asked here rather than read off the column, and asked the same way on both routes.</b>
    /// The flag is set by whatever raised the question, so a row written by a path that did not
    /// tighten it would be shown without the confirmation and then stored as sensitive by the
    /// answer - the queue and the store disagreeing about the same question, which is the one
    /// place this flag is load-bearing. A caller may only ever tighten it, never loosen it, which
    /// is why the wording is an <c>or</c> and not a fallback.
    ///
    /// It still buys only redaction and a confirmation. What actually keeps a salary expectation
    /// or a right-to-work status out of an unattended answer is that nothing in the derived
    /// namespace can produce one, so this being wrong logs badly rather than leaks.
    /// </remarks>
    private static bool Sensitive(OpenQuestionRow question)
        => question.Sensitive || SensitiveQuestions.Looks(question.QuestionText);

    private static IResult Refused(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// A 400 that names what was accepted.
    /// </summary>
    /// <remarks>
    /// The allowed set is listed rather than described, following the submission endpoints: the
    /// caller may be reading the error and retrying, and "scope must be a valid AnswerScope"
    /// tells it nothing it can act on.
    /// </remarks>
    private static IResult Invalid<TEnum>(string field, string? value)
        where TEnum : struct, Enum
        => Refused(
            $"'{value}' is not a valid {field}. Expected one of: "
            + string.Join(", ", Enum.GetNames<TEnum>()) + ".");

    private static IResult Empty()
        => TypedResults.Ok(new { items = Array.Empty<OpenQuestionResponse>() });

    private static TValue? Lookup<TValue>(Dictionary<long, TValue> source, long? key)
        where TValue : class
        => key is { } id && source.TryGetValue(id, out var found) ? found : null;

    private static int? Lookup(Dictionary<long, int> source, long? key)
        => key is { } id && source.TryGetValue(id, out var found) ? found : null;

    /// <summary>What answering freed, and what is still holding the advert it came from.</summary>
    private sealed record Release(
        IReadOnlyList<ParkedApplicationResponse> Returned, int StillOutstanding);
}
