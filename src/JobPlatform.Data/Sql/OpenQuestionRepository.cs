using System.Linq.Expressions;
using System.Text.Json;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// One question waiting on a person, with the advert that raised it.
/// </summary>
/// <remarks>
/// A record rather than the entity, for the reason <see cref="SubmissionRow"/> is one: a queue of
/// twenty questions must not drag a <c>JobPostingEntity</c> and its unbounded description across
/// for each. The advert's title and employer come with it because a question with no context -
/// "do you require sponsorship?" on its own - is one a person cannot safely answer.
/// </remarks>
/// <param name="Options">
/// The choices the form offered, or empty for a free-text box. <b>Empty and "we did not record
/// the options" are the same thing here and deliberately so</b>: the column stores null for
/// both, and a caller that has to tell them apart is asking a question the form never answered.
/// </param>
public sealed record OpenQuestionRow(
    long Id,
    long? PostingId,
    string? PostingTitle,
    string? Company,
    long? RunId,
    string QuestionText,
    string QuestionHash,
    IReadOnlyList<string> Options,
    bool Sensitive,
    DateTimeOffset AskedAtUtc,
    DateTimeOffset? AnsweredAtUtc,
    long? AnswerId)
{
    /// <summary>Still waiting on a person. Null <see cref="AnsweredAtUtc"/> is the flag, as it is in the index.</summary>
    public bool IsOpen => AnsweredAtUtc is null;
}

/// <summary>What closing a question did.</summary>
/// <remarks>
/// Every one of these is an ordinary outcome rather than an exception, which is why the write
/// answers with this rather than throwing - the same rule <see cref="SubmissionEventResult"/>
/// follows, and the same rule the tool surface restates as "a refusal is a structured answer".
/// </remarks>
public enum OpenQuestionAnswerResult
{
    /// <summary>Closed, and it has left the queue.</summary>
    Answered = 0,

    /// <summary>No such question for this candidate. Indistinguishable from "not yours".</summary>
    NotFound = 1,

    /// <summary>
    /// It was already closed, and the close that stands is the first one.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Answered"/> because a caller acts differently on it: a client
    /// retrying a write it is unsure landed has converged and should carry on, where a person
    /// pressing answer on a question somebody else has already answered should be shown what was
    /// recorded rather than told they did it.
    /// </remarks>
    AlreadyClosed = 2,

    /// <summary>
    /// The answer named does not belong to this candidate.
    /// </summary>
    /// <remarks>
    /// Refused rather than stored, and refused rather than quietly downgraded to a dismissal.
    /// The foreign key would accept it - it names a real row - so nothing below this would
    /// notice a question in one person's queue pointing at another person's answer, and the
    /// dashboard would then show them that answer as their own.
    /// </remarks>
    NoSuchAnswer = 3,
}

/// <summary>
/// The questions a run could not answer, and what becomes of them.
/// </summary>
/// <remarks>
/// <b>This is what makes abstention recoverable, and without it abstention is a loop.</b>
/// Resolution refuses by default - below the confidence floor, on a sensitive field with no
/// stored answer, or where an option set will not map cleanly - because a confident near-miss on
/// somebody's application is worse than an interruption. An interruption still has to be
/// recoverable, so the question is queued here and the posting parked for
/// <see cref="ParkReason.MissingAnswer"/> waits on it rather than being offered again next run
/// to produce the same park forever.
///
/// <b>Takes a profile id the caller has already resolved, and never a question id alone.</b> The
/// rule <c>CandidateProfileRepository</c> sets and <c>SubmissionRepository</c> follows: there is
/// no method an endpoint or a tool could hand a bare route parameter to. It matters more here
/// than on a route because these ids arrive as arguments named by a model, and because the
/// answer to a queued question is frequently the most sensitive thing this system holds.
///
/// <b>Nothing here deletes.</b> Answering stamps <see cref="OpenQuestionEntity.AnsweredAtUtc"/>,
/// which takes the row out of the filtered unique index without taking it out of the table, so
/// what was asked survives being answered - the same argument the event log rests on.
///
/// <b>Opening a question never blocks and never fails on a duplicate.</b> A run meeting the same
/// wording on four adverts must put it to a person once, so the second ask converges on the row
/// the first made. What that costs is stated in <see cref="OpenAsync"/>'s remarks rather than
/// hidden: the row keeps the advert that raised it, and the other adverts' waiting is recorded
/// where waiting is recorded in this design - on their own parked submissions.
/// </remarks>
public sealed class OpenQuestionRepository(JobsDbContext db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The columns every read here projects, written once so two reads cannot disagree.
    /// </summary>
    /// <remarks>
    /// Stops short of the options, which are JSON: EF has to translate this into SQL, and
    /// deserialising happens after materialisation. That split is why the projection is a private
    /// record rather than <see cref="OpenQuestionRow"/> itself - a row whose options were
    /// silently empty because the query could not translate the parse would be a bug no test
    /// looking at the shape would see.
    ///
    /// <c>Posting</c> is an optional relationship, so this is a left join and the title and
    /// employer come back null for a question asked from the dashboard rather than from an
    /// advert.
    /// </remarks>
    private static readonly Expression<Func<OpenQuestionEntity, QuestionProjection>> Projected =
        q => new QuestionProjection(
            q.Id,
            q.PostingId,
            q.Posting!.Title,
            q.Posting.Company,
            q.RunId,
            q.QuestionText,
            q.QuestionHash,
            q.OptionsJson,
            q.Sensitive,
            q.AskedAtUtc,
            q.AnsweredAtUtc,
            q.AnswerId);

    /// <summary>
    /// Queues a question for a person, or hands back the one already queued for that wording.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent per candidate and wording while the question is open</b>, which is the whole
    /// reason the queue is worth having: a run that meets "do you require sponsorship?" on four
    /// adverts must ask a person once, and a person who answered it last week must not be asked
    /// again this week. The filtered unique index is what guarantees that; this checks first so
    /// the ordinary case is an answer rather than an exception, and the index catches the race
    /// the check cannot - the contract <c>SubmissionRepository.CreateAsync</c> already carries.
    ///
    /// <b>Wording is folded before it is compared</b>, through <see cref="QuestionKey.Hash"/>, so
    /// the same question typed with a trailing space, a curly apostrophe or a different case is
    /// one queue entry rather than four. The hash is computed here rather than taken from the
    /// caller because it is also what <c>FormAnswers</c> is keyed by: two computations of it in
    /// two repositories is how a queue entry stops being found by the answer that closes it.
    ///
    /// <b>The converged row keeps the advert that first raised it, and the second advert's
    /// waiting is not recorded on this row.</b> It cannot be - one unanswered row per wording is
    /// the index, and there is no column here for a set of adverts - and inventing one would
    /// undo the guarantee above. It does not need to be: an advert waits because <i>it</i> is
    /// parked for <see cref="ParkReason.MissingAnswer"/>, and that fact lives on its own
    /// submission, which is where parking lives in this design. Reading the converged
    /// <c>Created: false</c> as "nothing to do" is the mistake to avoid - the caller still parks
    /// its posting, on the question it has just been handed.
    ///
    /// <b>What the convergence costs is paid by the queue predicate, and it is worth knowing
    /// before somebody tidies either.</b> Nothing afterwards can say which advert waits on which
    /// wording, so <c>JobMatchRepository.ListApplyableAsync</c> cannot hold a park on <i>its</i>
    /// question and holds it while any answer is outstanding instead. Written the other way -
    /// asking whether an unanswered question names this posting - the second advert to raise a
    /// wording is offered again on every run and parks again on every run, which is the loop this
    /// table exists to end.
    /// </remarks>
    /// <param name="profileId">The candidate whose queue this is, already resolved by the caller.</param>
    /// <param name="questionText">The question as the form asked it. Stored verbatim; folded only for the key.</param>
    /// <param name="options">The choices the form offered, or null for a free-text box.</param>
    /// <param name="sensitive">Whether the answer is one a person should see leave the system.</param>
    /// <param name="postingId">The advert that raised it, where one did. Context, never identity.</param>
    /// <param name="runId">The run that raised it, where one did, so an abandoned run's questions stay attributable.</param>
    /// <param name="now">The clock, passed in so the ordering is assertable.</param>
    public async Task<(OpenQuestionRow Row, bool Created)> OpenAsync(
        long profileId,
        string questionText,
        IReadOnlyList<string>? options,
        bool sensitive,
        long? postingId,
        long? runId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionText);

        // Bounded before it is hashed, never after, so the text on the row is always the
        // pre-image of the key on the row. Hashing what was passed in would key a truncated
        // question by a form of it that appears nowhere in the table, which is the debugging
        // session FormAnswers keeps NormalisedQuestion around to prevent.
        var text = Bound(questionText, FormAnswerLimits.MaxQuestionTextLength)!;
        var hash = QuestionKey.Hash(text);

        var live = await LiveAsync(profileId, hash, ct);

        if (live is not null)
        {
            return (live, false);
        }

        var entity = new OpenQuestionEntity
        {
            ProfileId = profileId,
            PostingId = postingId,
            RunId = runId,
            QuestionText = text,
            QuestionHash = hash,
            OptionsJson = Serialise(options),
            Sensitive = sensitive,
            AskedAtUtc = now,
        };

        db.OpenQuestions.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two runs, or one run in two threads, reached the check together. Detached first:
            // the failed insert is still tracked as Added, and the next SaveChanges on this
            // context would retry it and fail again somewhere with no memory of why.
            db.Entry(entity).State = EntityState.Detached;

            var raced = await LiveAsync(profileId, hash, ct);

            if (raced is null)
            {
                // Not the index that refused us, then. Swallowing this would report a queued
                // question that does not exist, and a person would wait on it forever.
                throw;
            }

            return (raced, false);
        }

        var row = await LiveAsync(profileId, hash, ct);

        return (row!, true);
    }

    /// <summary>
    /// This candidate's queue, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first rather than newest, which inverts every other list in this codebase and is
    /// the point: those are histories, where the last thing that happened is the interesting
    /// one, and this is a queue to be drained. The question that has been holding an application
    /// back for three days is the one to put in front of somebody, not the one asked a minute
    /// ago.
    /// </remarks>
    /// <param name="limit">A bound on the page, because a client asking is a model with a context window.</param>
    public async Task<IReadOnlyList<OpenQuestionRow>> ListUnansweredAsync(
        long profileId, int limit, CancellationToken ct = default)
    {
        var rows = await db.OpenQuestions
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId && q.AnsweredAtUtc == null)
            .OrderBy(q => q.AskedAtUtc)
            // Deterministic beyond the key: a run queues several questions inside one second,
            // and a page that shuffles between identical requests is a bug nobody reproduces.
            .ThenBy(q => q.Id)
            .Take(limit)
            .Select(Projected)
            .ToListAsync(ct);

        return [.. rows.Select(Map)];
    }

    /// <summary>
    /// What one advert is still waiting on.
    /// </summary>
    /// <remarks>
    /// <b>The read behind a parked application's explanation, and it has to answer the same
    /// question <c>JobMatchRepository.ListApplyableAsync</c> answers or it explains the wrong
    /// thing.</b> That queue holds a posting parked for <see cref="ParkReason.MissingAnswer"/>
    /// while <i>any</i> answer this candidate owes an advert is outstanding, because the
    /// deduplication leaves no way to tell which of them the posting is waiting on: one
    /// unanswered row per <c>(ProfileId, QuestionHash)</c> means the second advert to ask a
    /// question gets the row that names the first. So this read cannot be "the questions this
    /// advert raised" either - answering that for a posting parked on a converged question
    /// returns nothing at all, and a park with no visible reason is the state somebody opens the
    /// dashboard to escape.
    ///
    /// <b>Two spellings of one rule, held together by tests rather than shared</b>, for the
    /// reason the shortlist's channel filter and its projection are: the two queries are rooted
    /// at different tables, and an expression serving both would be one nothing can read. The
    /// drift they are watched for is invisible in the ordinary way - a question missing from an
    /// explanation is not something anybody notices.
    ///
    /// Unbounded on purpose: the index caps a wording at one unanswered row, and a candidate with
    /// a queue long enough to need a page here has a problem no limit would fix.
    ///
    /// Questions raised from the dashboard, which name no advert, are not returned by this and
    /// hold nothing back. A question nobody asked on behalf of an advert is not what any advert
    /// is waiting for, and treating it as such would empty the queue of applications every time
    /// somebody wrote themselves a note.
    /// </remarks>
    public async Task<IReadOnlyList<OpenQuestionRow>> ListUnansweredForPostingAsync(
        long profileId, long postingId, CancellationToken ct = default)
    {
        var rows = await db.OpenQuestions
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId
                && q.AnsweredAtUtc == null
                && q.PostingId != null
                && (q.PostingId == postingId
                    // Parked for an answer, so everything outstanding is holding it: the queue
                    // predicate cannot tell which question it was, and a read that claimed to
                    // would be answering a question the table cannot answer.
                    || db.Submissions.Any(s => s.ProfileId == profileId
                        && s.PostingId == postingId
                        && s.ParkedReason != null
                        && s.UnparkedAtUtc == null
                        && ParkReasonPolicy.AwaitingAnswer.Contains(s.ParkedReason.Value))))
            .OrderBy(q => q.AskedAtUtc)
            .ThenBy(q => q.Id)
            .Select(Projected)
            .ToListAsync(ct);

        return [.. rows.Select(Map)];
    }

    /// <summary>How many questions are waiting on this candidate.</summary>
    /// <remarks>
    /// A count rather than a list, for the surfaces that only report the depth of the queue - the
    /// note on <c>list_applyable</c> and the dashboard's badge. Pulling rows to count them would
    /// carry every question's text across for a number.
    /// </remarks>
    public Task<int> CountUnansweredAsync(long profileId, CancellationToken ct = default)
        => db.OpenQuestions
            .AsNoTracking()
            .CountAsync(q => q.ProfileId == profileId && q.AnsweredAtUtc == null, ct);

    /// <summary>
    /// Closes a question, linking it to the answer that closed it.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes a <see cref="ParkReason.MissingAnswer"/> park retryable</b>, so it
    /// has to be exactly right in one direction: an answered question must stop suppressing its
    /// posting. It does that by leaving the unanswered set, which is the same set the filtered
    /// unique index is built on and the same set the queue predicate reads - one fact, read by
    /// both, rather than a second flag they could disagree about.
    ///
    /// <b>The first close stands.</b> A second call answers <see cref="OpenQuestionAnswerResult.AlreadyClosed"/>
    /// and rewrites nothing: this row records that somebody was asked and what came back, and a
    /// write path that overwrote it would let the second answer erase the timestamp the first one
    /// is evidence of. Changing an answer is superseding it in <c>FormAnswers</c>, which keeps
    /// both; it is not editing the question that prompted it.
    ///
    /// <b>A null <paramref name="answerId"/> is a dismissal, not a failure.</b> "I am not
    /// answering that" is a real reply, and it has to close the question or the person is asked
    /// again next run - which is the loop this table exists to break. The row then says a
    /// question was asked and settled without an answer to file, which is exactly what happened.
    /// </remarks>
    /// <param name="profileId">The candidate whose queue this is, already resolved by the caller.</param>
    /// <param name="questionId">The queued question.</param>
    /// <param name="answerId">The stored answer that closed it, or null where it was dismissed.</param>
    /// <param name="now">The clock, passed in so the ordering is assertable.</param>
    public async Task<OpenQuestionAnswerResult> AnswerAsync(
        long profileId,
        long questionId,
        long? answerId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // AsTracking, explicitly, because this row is about to be mutated. It reads as
        // redundant against EF's default and is not: the API host set NoTracking globally on
        // the argument that it never wrote to SQL, and under that a read-then-mutate saves
        // nothing and throws nothing. The default has been corrected, and stating it here
        // means this write no longer depends on which host it runs in.
        var question = await db.OpenQuestions
            .AsTracking()
            .FirstOrDefaultAsync(q => q.Id == questionId && q.ProfileId == profileId, ct);

        if (question is null)
        {
            return OpenQuestionAnswerResult.NotFound;
        }

        if (question.AnsweredAtUtc is not null)
        {
            return OpenQuestionAnswerResult.AlreadyClosed;
        }

        if (answerId is not null && !await OwnsAnswerAsync(profileId, answerId.Value, ct))
        {
            return OpenQuestionAnswerResult.NoSuchAnswer;
        }

        question.AnsweredAtUtc = now;
        question.AnswerId = answerId;

        await db.SaveChangesAsync(ct);

        return OpenQuestionAnswerResult.Answered;
    }

    /// <summary>
    /// Closes whatever question a newly stored answer answers, or nothing if none was open.
    /// </summary>
    /// <remarks>
    /// <b>The path that keeps the queue honest without anybody having to remember to drain
    /// it.</b> Recording an answer and closing the question it answers are one act from the
    /// candidate's point of view, and split across two calls the second is the one that gets
    /// forgotten - leaving a question in somebody's queue that the system can already answer, and
    /// a posting parked on it forever.
    ///
    /// <b>Keyed on the stored hash rather than on the question text.</b> The answer already
    /// carries the hash it was filed under; recomputing one from text would mean a later change
    /// to <see cref="QuestionKey.Normalise"/> silently stops matching rows written by the older
    /// spelling, and the failure is a queue that never drains rather than anything that errors.
    ///
    /// Returns the question it closed so the caller can say what it did, and null where the
    /// answer was volunteered rather than asked for - which is ordinary, not an error, and is the
    /// explanatory-note case this surface prefers throughout. It is also null where the answer
    /// named is not this candidate's: unreachable from a caller that has just written it under
    /// the same profile id, and left as a guard rather than a refusal because the queue being
    /// untouched is the whole of what a caller could do about it.
    /// </remarks>
    /// <param name="profileId">The candidate whose queue this is, already resolved by the caller.</param>
    /// <param name="questionHash">The stored answer's own <c>QuestionHash</c>.</param>
    /// <param name="answerId">The answer just written.</param>
    /// <param name="now">The clock, passed in so the ordering is assertable.</param>
    public async Task<OpenQuestionRow?> AnswerByHashAsync(
        long profileId,
        string questionHash,
        long answerId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionHash);

        var question = await db.OpenQuestions
            .FirstOrDefaultAsync(
                q => q.ProfileId == profileId
                    && q.QuestionHash == questionHash
                    && q.AnsweredAtUtc == null,
                ct);

        if (question is null)
        {
            return null;
        }

        if (!await OwnsAnswerAsync(profileId, answerId, ct))
        {
            return null;
        }

        question.AnsweredAtUtc = now;
        question.AnswerId = answerId;

        await db.SaveChangesAsync(ct);

        return await SingleAsync(profileId, question.Id, ct);
    }

    /// <summary>One of the caller's questions, answered or not, or null where they do not own it.</summary>
    public Task<OpenQuestionRow?> GetAsync(long profileId, long questionId, CancellationToken ct = default)
        => SingleAsync(profileId, questionId, ct);

    private async Task<OpenQuestionRow?> SingleAsync(long profileId, long questionId, CancellationToken ct)
    {
        var row = await db.OpenQuestions
            .AsNoTracking()
            .Where(q => q.Id == questionId && q.ProfileId == profileId)
            .Select(Projected)
            .FirstOrDefaultAsync(ct);

        return row is null ? null : Map(row);
    }

    private async Task<OpenQuestionRow?> LiveAsync(long profileId, string hash, CancellationToken ct)
    {
        var row = await db.OpenQuestions
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId && q.QuestionHash == hash && q.AnsweredAtUtc == null)
            .Select(Projected)
            .FirstOrDefaultAsync(ct);

        return row is null ? null : Map(row);
    }

    private Task<bool> OwnsAnswerAsync(long profileId, long answerId, CancellationToken ct)
        => db.FormAnswers
            .AsNoTracking()
            .AnyAsync(a => a.Id == answerId && a.ProfileId == profileId, ct);

    /// <summary>
    /// The options as the form listed them, blanks dropped and each one bounded.
    /// </summary>
    /// <remarks>
    /// Bounded per option by <see cref="FormAnswerLimits.MaxValueLength"/> rather than by a
    /// number invented here, because an option is a value the candidate may end up choosing and
    /// that constant is what bounds a stored value. The count is the form's own and is left
    /// alone: there is no constant for it, and a literal cap in this file would be exactly the
    /// drift between a bound and its column that the constants exist to prevent.
    ///
    /// Null for nothing to store, matching what the column means - a free-text box and a select
    /// whose choices were all blank are the same question as far as this can tell.
    /// </remarks>
    private static string? Serialise(IReadOnlyList<string>? options)
    {
        if (options is null)
        {
            return null;
        }

        var kept = options
            .Select(option => Bound(option, FormAnswerLimits.MaxValueLength))
            .OfType<string>()
            .ToList();

        return kept.Count == 0 ? null : JsonSerializer.Serialize(kept, Json);
    }

    /// <summary>
    /// Reads the options back, answering empty where they cannot be read.
    /// </summary>
    /// <remarks>
    /// Lenient like <c>ApplicationDocumentRepository.Deserialize</c>, and for a sharper reason
    /// here: this column is on the path a person uses to answer a question, and a parse failure
    /// that threw would take the whole queue off the screen rather than one question's choices.
    /// The question text is still there and still answerable.
    /// </remarks>
    private static IReadOnlyList<string> Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static OpenQuestionRow Map(QuestionProjection row)
        => new(
            row.Id,
            row.PostingId,
            row.PostingTitle,
            row.Company,
            row.RunId,
            row.QuestionText,
            row.QuestionHash,
            Deserialise(row.OptionsJson),
            row.Sensitive,
            row.AskedAtUtc,
            row.AnsweredAtUtc,
            row.AnswerId);

    /// <summary>
    /// Trims to the column's width rather than letting the database do it.
    /// </summary>
    /// <remarks>
    /// The same guard <c>SubmissionRepository</c> carries, and duplicated rather than shared for
    /// the same reason the bounds themselves are separate constants: a silent truncation on the
    /// way in is the shape of bug this codebase has paid for before, and the widths come from
    /// <see cref="FormAnswerLimits"/> so the schema and the validation cannot drift.
    /// </remarks>
    private static string? Bound(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>What the query returns before the options are parsed. See <see cref="Projected"/>.</summary>
    private sealed record QuestionProjection(
        long Id,
        long? PostingId,
        string? PostingTitle,
        string? Company,
        long? RunId,
        string QuestionText,
        string QuestionHash,
        string? OptionsJson,
        bool Sensitive,
        DateTimeOffset AskedAtUtc,
        DateTimeOffset? AnsweredAtUtc,
        long? AnswerId);
}
