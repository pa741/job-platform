using System.Linq.Expressions;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// A stored answer, and how long ago the candidate gave it.
/// </summary>
/// <remarks>
/// <b>The age is carried because nothing in this file expires anything.</b> "Expire stale
/// answers" is a real requirement and a delete is the wrong shape for it twice over. Which
/// answers go off is a judgement about the <i>question</i> - a salary expectation ages in
/// months, a notice period stops being true the day somebody changes job, an email address does
/// not go off at all - and this table knows questions only as hashes. And deleting on a timer
/// would throw away the history the supersede mechanism exists to keep, in the one table whose
/// entire argument is that it can still say what was submitted last year.
///
/// So a read reports the age and the layer above decides: re-confirm, raise an
/// <c>OpenQuestion</c>, or ignore it. <see cref="FormAnswer.IsLive"/> answers the other half of
/// the question, and the two are not the same fact - an answer the candidate retracted is a
/// different thing from one that has merely got old.
/// </remarks>
/// <param name="Answer">The answer as it is stored.</param>
/// <param name="Age">
/// How long ago it was given, against the clock the caller passed in. Negative for an answer
/// timestamped in the future, which is reported rather than clamped: a clock that disagrees with
/// itself is something a caller should be able to see.
/// </param>
public sealed record StoredAnswer(FormAnswer Answer, TimeSpan Age);

/// <summary>
/// What a question resolved to last time, so the second occurrence costs a lookup rather than a
/// model call.
/// </summary>
/// <remarks>
/// <b>The answer travels with the row, and that is the point of the record rather than a
/// convenience.</b> A hit that returned only an <c>AnswerId</c> would leave every caller to
/// fetch the answer itself, and a caller fetching an answer by id is a caller that can fetch
/// somebody else's - the resolution row is scoped to the candidate and the answer it names is
/// re-read through the same profile id, so a hit cannot hand back an answer the candidate did
/// not write.
///
/// <b><see cref="Answer"/> being null is a result and not a gap.</b> Resolution abstains by
/// default and an abstention is cached like any other outcome, so "we looked at this and would
/// not answer it" survives to the next run instead of being rediscovered at the price of another
/// model call. <see cref="Rationale"/> is what makes that row auditable afterwards.
/// </remarks>
public sealed record CachedResolution(
    string QuestionHash,
    string? OptionsHash,
    string? ResolvedName,
    StoredAnswer? Answer,
    double Confidence,
    string Rationale,
    string? Model,
    DateTimeOffset ResolvedAtUtc,
    bool Confirmed)
{
    /// <summary>Whether the cached outcome was a refusal to answer.</summary>
    public bool Abstained => Answer is null;
}

/// <summary>
/// What one pass of resolution decided, on its way into the cache.
/// </summary>
/// <remarks>
/// A record rather than nine positional arguments, because four of them are optional and two of
/// those are a <c>bool</c> and a <c>double</c> - the shape where a caller silently transposes
/// two arguments and the compiler agrees.
///
/// It carries the question and its options as text rather than as hashes so that the caller
/// cannot key the cache differently from the way this repository reads it. <see cref="QuestionKey"/>
/// is applied on both sides here, once.
/// </remarks>
/// <param name="QuestionText">The question as the form asked it.</param>
/// <param name="Options">The choices the form offered, or null for a free-text box.</param>
/// <param name="Confidence">0-1, clamped rather than stored raw. See the remarks on the write.</param>
/// <param name="Rationale">Why it decided this. Required - an unexplained cache row cannot be audited.</param>
/// <param name="AnswerId">The stored answer it chose, or null where it abstained.</param>
/// <param name="ResolvedName">The name it resolved to, where it resolved to one.</param>
/// <param name="Model">Which deployment answered, where a model was reached at all.</param>
/// <param name="Confirmed">Whether a person has agreed with this. False for anything a model decided.</param>
public sealed record ResolutionOutcome(
    string QuestionText,
    IReadOnlyList<string>? Options,
    double Confidence,
    string Rationale,
    long? AnswerId = null,
    string? ResolvedName = null,
    string? Model = null,
    bool Confirmed = false);

/// <summary>
/// The declared answers - what the candidate has typed - and the cache of what questions
/// resolved to.
/// </summary>
/// <remarks>
/// <b>Nothing here can read the profile, and that is the sensitive-data guarantee rather than a
/// tidiness one.</b> This is the declared namespace: it holds what a person wrote and nothing
/// else. <c>FormFieldCatalog</c> is the derived one, and it answers a fixed allowlist from the
/// profile that deliberately contains no EEO question, no salary expectation, no right to work
/// and no date of birth. Keeping the two apart is what makes a sensitive answer safe without
/// depending on a <c>sensitive: true</c> flag being set correctly somewhere - a flag converts
/// "cannot be answered" into "answered unless a boolean was right", which is a weaker guarantee
/// wearing the same word. So the guarantee is structural: the only mention of the candidate
/// anywhere in this file is a profile id in a <c>WHERE</c> clause. No query joins
/// <c>CandidateProfiles</c>, no read touches the <c>Profile</c> navigation, and no method takes
/// or returns a profile type. A sensitive value can exist in this table because somebody wrote
/// it, and nowhere else in this class because there is nowhere else.
///
/// <b>Takes a profile id the caller has already resolved.</b> The boundary
/// <c>CandidateProfileRepository</c> states as a type, restated here: there is no method an
/// endpoint could hand a route parameter to and none an MCP tool could hand an argument named
/// by a model. Every read is scoped to one candidate and there is no method that returns
/// answers for more than one.
///
/// <b>Superseding, never overwriting.</b> An answer store that overwrites cannot say what was
/// submitted last year, which is the argument the event log already rests on. Replacing an
/// answer stamps <c>SupersededAtUtc</c> on the old row and inserts the new one in <i>one</i>
/// <c>SaveChangesAsync</c>, so the filtered unique index never sees two live answers to one
/// question and a crash between the two cannot leave the candidate with none.
///
/// <b>The cache is the one thing here that does overwrite, and the two rules are not in
/// tension.</b> <c>FormAnswers</c> is history - it records what a person said and when they
/// stopped saying it. <c>FormAnswerResolutions</c> is a cache: it records what this system
/// last worked out, which is a derived opinion with no claim to be a record of anything. Keeping
/// its history would build a second and worse event log beside the one in Cosmos that already
/// logs every model call.
/// </remarks>
public sealed class FormAnswerRepository(JobsDbContext db)
{
    /// <summary>
    /// Records an answer, superseding whatever it replaces.
    /// </summary>
    /// <remarks>
    /// <b>The supersede and the insert are one <c>SaveChangesAsync</c></b>, which is one
    /// transaction. Written as two, the window between them is a moment where the filtered
    /// unique index holds two live answers to one question - or, if the order is reversed, none
    /// at all, which is the worse of the two because the next resolution reads a blank and
    /// interrupts somebody for an answer they have already given.
    ///
    /// <b>The old row is stamped with the new answer's own timestamp rather than with
    /// <paramref name="now"/>.</b> They differ only when somebody backdates, and when they do,
    /// contiguity is what is worth keeping: an answer stood from when it was given until the one
    /// that replaced it was, so "what would I have told them in March" is answerable by reading
    /// two columns rather than by ordering the whole table.
    ///
    /// <b>An identical answer converges rather than making a row.</b> A client re-asserting what
    /// is already stored - the ordinary shape of a retry, and of a run that re-reads a form it
    /// has seen - would otherwise supersede a live answer with a copy of itself and leave the
    /// history a column of duplicates with nothing to say. Identical means indistinguishable:
    /// the same value, the same name, the same sensitivity and the same source. A candidate
    /// re-asserting in their own words what a client asserted for them <i>is</i> a change, and
    /// it is recorded as one.
    ///
    /// It throws past the column bounds rather than truncating. <c>SubmissionRepository</c>
    /// trims, because the worst case there is a shortened audit line; the worst case here is a
    /// truncated sentence typed into somebody's application and sent to an employer, where it
    /// reads as a statement rather than as a bug. <see cref="FormAnswer.Create"/> refuses on the
    /// same bounds, so reaching this guard means the answer was built by an initialiser that
    /// skipped it.
    /// </remarks>
    /// <returns>
    /// The answer that now stands, and whether a row was written. <c>false</c> means the
    /// identical answer was already there.
    /// </returns>
    public async Task<(StoredAnswer Answer, bool Created)> RecordAsync(
        long profileId, FormAnswer answer, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        EnsureStorable(answer);

        var name = Key(answer.Name);
        var companyId = answer.CompanyId;
        var postingId = answer.PostingId;

        // Tracked deliberately: these rows are about to be stamped, and the stamp has to travel
        // in the same SaveChanges as the insert below.
                // AsTracking, explicitly, because these rows are about to be superseded. It reads as
        // redundant against EF's default and is not: the API host set NoTracking globally on the
        // argument that it never wrote to SQL, and under that this loop stamps a column on
        // objects nobody is watching - saving nothing, throwing nothing, and leaving last year's
        // salary expectation live to be submitted again.
var live = await db.FormAnswers
            .AsTracking()
            .Where(a => a.ProfileId == profileId
                && a.QuestionHash == answer.QuestionHash
                && a.SupersededAtUtc == null
                && a.Scope == answer.Scope
                && a.CompanyId == companyId
                && a.PostingId == postingId)
            .ToListAsync(ct);

        var identical = live.FirstOrDefault(a => a.Value == answer.Value
            && a.Name == name
            && a.Sensitive == answer.Sensitive
            && a.Source == answer.Source);

        if (identical is not null)
        {
            return (Stored(identical, now), false);
        }

        // A list rather than a single row, though the index permits only one: a row written
        // before that index existed is not something this loop should have to know about.
        foreach (var superseded in live)
        {
            superseded.SupersededAtUtc = answer.AnsweredAtUtc;
        }

        var entity = new FormAnswerEntity
        {
            ProfileId = profileId,
            Name = name,
            QuestionText = answer.QuestionText,
            QuestionHash = answer.QuestionHash,
            NormalisedQuestion = answer.NormalisedQuestion,
            Value = answer.Value,
            Scope = answer.Scope,
            CompanyId = companyId,
            PostingId = postingId,
            Sensitive = answer.Sensitive,
            Source = answer.Source,
            AnsweredAtUtc = answer.AnsweredAtUtc,
        };

        db.FormAnswers.Add(entity);
        await db.SaveChangesAsync(ct);

        return (Stored(entity, now), true);
    }

    /// <summary>
    /// The answer to a question as it was asked, for the employer and posting it was asked by.
    /// </summary>
    /// <remarks>
    /// <b>The context is a parameter and not an afterthought, because applicability is part of
    /// precedence.</b> A repository that fetched every row carrying this hash would be holding
    /// another employer's answer in memory, and ranking before filtering hands it over for being
    /// the more specific of the two - the exact failure <see cref="AnswerScope"/> exists to
    /// prevent, reintroduced one layer above it. So the scope test runs in SQL, before anything
    /// is materialised, and <see cref="AnswerPrecedence.Best"/> is given the context as well: the
    /// filter keeps the wrong answer out of the process and Core decides between what is left.
    ///
    /// <b>That filter is <see cref="AnswerPrecedence.Applies"/> written a second time, in a
    /// language EF can translate.</b> There is no way to have one spelling - a static call over
    /// a column has no SQL - so the two are held together by a test that runs every stored answer
    /// past both, the way the shortlist's channel filter and its projection are. A drift here is
    /// silent: an answer that stops being offered is not something anybody notices.
    ///
    /// The question is hashed here rather than taken as a hash, so a caller cannot key a lookup
    /// differently from the way <see cref="RecordAsync"/> wrote it.
    /// </remarks>
    public async Task<StoredAnswer?> FindAsync(
        long profileId,
        string questionText,
        DateTimeOffset now,
        int? companyId = null,
        long? postingId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionText);

        var hash = QuestionKey.Hash(questionText);

        return Best(
            await ApplicableAsync(profileId, a => a.QuestionHash == hash, companyId, postingId, ct),
            now,
            companyId,
            postingId);
    }

    /// <summary>
    /// The answer filed under a name, for the employer and posting the question was asked by.
    /// </summary>
    /// <remarks>
    /// <b>The escape from phrasing.</b> <c>QuestionKey</c> folds typography and nothing more, so
    /// two employers asking the same thing in genuinely different words produce two hashes and
    /// <see cref="FindAsync"/> misses on the second. A name written once - <c>notice_period</c> -
    /// lets both resolve, which is the whole reason the column and its index exist.
    ///
    /// <b>The name is folded to lower case at both ends, and that is not fussiness.</b> SQL
    /// Server's default collation is case-insensitive and SQLite's comparison is not, so an exact
    /// comparison on a name somebody spelled <c>Notice_Period</c> once would match in production
    /// and miss in the test fixture - a difference nothing here could catch, in the same family
    /// as the NULL-semantics trap the three live-answer indexes are shaped to avoid. Folding a
    /// key is what <c>Companies.CompanyKey</c> already does, and this is a key rather than prose:
    /// the text a person reads is <see cref="FormAnswer.QuestionText"/>.
    /// </remarks>
    public async Task<StoredAnswer?> FindByNameAsync(
        long profileId,
        string name,
        DateTimeOffset now,
        int? companyId = null,
        long? postingId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var key = Key(name);

        return Best(
            await ApplicableAsync(profileId, a => a.Name == key, companyId, postingId, ct),
            now,
            companyId,
            postingId);
    }

    /// <summary>
    /// Everything this candidate has answered, newest first.
    /// </summary>
    /// <remarks>
    /// What the dashboard's answer list reads, and it is not scoped to a company or a posting
    /// because a person reviewing what they have said should see all of it - a page that hid the
    /// answers written for one employer would be a page nobody could use to find the answer they
    /// want to change.
    ///
    /// <paramref name="includeSuperseded"/> defaults to off, so the ordinary read is "what would
    /// I say now" rather than a transcript. Turning it on is how the history becomes visible; it
    /// is never the thing a form-filling path should ask for.
    /// </remarks>
    public async Task<IReadOnlyList<StoredAnswer>> ListAsync(
        long profileId,
        DateTimeOffset now,
        bool includeSuperseded = false,
        CancellationToken ct = default)
    {
        var rows = await db.FormAnswers
            .AsNoTracking()
            .Where(a => a.ProfileId == profileId && (includeSuperseded || a.SupersededAtUtc == null))
            .OrderByDescending(a => a.AnsweredAtUtc)
            // Deterministic beyond the key: two answers recorded in one request share a
            // timestamp, and a list that shuffles between identical requests is a bug nobody can
            // reproduce.
            .ThenByDescending(a => a.Id)
            .ToListAsync(ct);

        return [.. rows.Select(row => Stored(row, now))];
    }

    /// <summary>
    /// The live answers older than a given age, oldest first.
    /// </summary>
    /// <remarks>
    /// <b>It reports rather than expires, and the parameter is why.</b> How long an answer stays
    /// true is a property of the question - months for a salary expectation, one job change for a
    /// notice period, never for an email address - and this table stores questions as hashes, so
    /// it is in no position to decide. The caller names the age it cares about and does something
    /// with what comes back: raise an <c>OpenQuestion</c>, ask for a confirmation on the
    /// dashboard, or nothing at all. Nothing is deleted and nothing is marked, so calling this
    /// twice costs two reads and changes nothing.
    ///
    /// Superseded rows are excluded. An answer the candidate has already replaced does not need
    /// re-confirming; it needs nothing, which is why it was replaced.
    ///
    /// Oldest first, because that is the order somebody would work through them in.
    /// </remarks>
    public async Task<IReadOnlyList<StoredAnswer>> ListStaleAsync(
        long profileId,
        TimeSpan olderThan,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);

        var cutoff = now - olderThan;

        var rows = await db.FormAnswers
            .AsNoTracking()
            .Where(a => a.ProfileId == profileId && a.SupersededAtUtc == null && a.AnsweredAtUtc < cutoff)
            .OrderBy(a => a.AnsweredAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);

        return [.. rows.Select(row => Stored(row, now))];
    }

    /// <summary>
    /// What this question resolved to last time, or null if it has not been resolved before.
    /// </summary>
    /// <remarks>
    /// <b>A hit here is the acceptance criterion, not an optimisation.</b> Resolution runs four
    /// stages - canonical key, stored answer, this cache, and only then a model - and "the second
    /// occurrence of a question resolves without a model call" is what this read is for. It
    /// answers an abstention as readily as an answer, so a question the resolver already refused
    /// is not refused again at the price of a second model call.
    ///
    /// <b>Keyed on the question <i>and</i> its options.</b> "Do you require sponsorship?" against
    /// <c>[Yes, No]</c> and against <c>[Yes, No, Prefer not to say]</c> can resolve differently
    /// and honestly, and one row for both would serve the first form's answer to the second.
    /// <see cref="QuestionKey.OptionsHash"/> is order-insensitive, so a dropdown re-rendered with
    /// its choices shuffled still hits.
    ///
    /// <b>The answer is re-read through the profile id rather than followed down the foreign
    /// key.</b> One extra round trip, and it is a primary-key lookup: what it buys is that a
    /// resolution row naming an answer belonging to somebody else - which this repository will
    /// not write, and which a hand-written row could still be - resolves to an abstention instead
    /// of disclosing it.
    /// </remarks>
    public async Task<CachedResolution?> GetResolutionAsync(
        long profileId,
        string questionText,
        IReadOnlyList<string>? options,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionText);

        var hash = QuestionKey.Hash(questionText);
        var optionsHash = QuestionKey.OptionsHash(options);

        var row = await db.FormAnswerResolutions
            .AsNoTracking()
            .Where(r => r.ProfileId == profileId && r.QuestionHash == hash && r.OptionsHash == optionsHash)
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        return new CachedResolution(
            row.QuestionHash,
            row.OptionsHash,
            row.ResolvedName,
            await AnswerAsync(profileId, row.AnswerId, now, ct),
            row.Confidence,
            row.Rationale,
            row.Model,
            row.ResolvedAtUtc,
            row.Confirmed);
    }

    /// <summary>
    /// Caches what a question resolved to, replacing the previous outcome for the same question
    /// and option set.
    /// </summary>
    /// <remarks>
    /// <b>This is the one write in this file that overwrites, and it is not a contradiction of
    /// the rule next door.</b> An answer is a record of something a person said and superseding
    /// is how it keeps its history. A resolution is a derived opinion about a question - what
    /// this system last worked out - and a table of every opinion it has ever held would be a
    /// second and worse event log beside the AI call ledger, which already records every model
    /// call with its inputs and its cost.
    ///
    /// <b>An answer from another candidate is refused, and it throws rather than returning a
    /// refusal.</b> The only place an answer id comes from is a read on this class, which is
    /// profile-scoped, so an id belonging to somebody else means the caller did not look - the
    /// distinction <see cref="FormAnswer.Create"/> draws between a caller asking for something it
    /// may not have and a caller that has skipped a step. Leaving it to the foreign key would not
    /// do: the constraint is to <c>FormAnswers</c> and knows nothing about whose answer it is.
    ///
    /// <b>A rewrite clears <see cref="ResolutionOutcome.Confirmed"/> unless the caller asserts it
    /// again.</b> A person agreed with the answer that was there, not with whatever replaces it,
    /// and inheriting the flag would let a later model call arrive pre-approved.
    ///
    /// The rationale and the model name are trimmed to their columns; the resolved name is
    /// refused if it will not fit. The line is between text that explains and text that
    /// identifies - a shortened sentence is a worse audit line, where a shortened key names a
    /// different thing.
    /// </remarks>
    public async Task<CachedResolution> RecordResolutionAsync(
        long profileId, ResolutionOutcome outcome, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome.QuestionText);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome.Rationale);

        var hash = QuestionKey.Hash(outcome.QuestionText);
        var optionsHash = QuestionKey.OptionsHash(outcome.Options);

        var answer = await AnswerAsync(profileId, outcome.AnswerId, now, ct);

        if (outcome.AnswerId is not null && answer is null)
        {
            throw new ArgumentException(
                "The resolved answer does not belong to this candidate.", nameof(outcome));
        }

        var row = await db.FormAnswerResolutions
            .FirstOrDefaultAsync(
                r => r.ProfileId == profileId && r.QuestionHash == hash && r.OptionsHash == optionsHash,
                ct);

        if (row is null)
        {
            row = new FormAnswerResolutionEntity
            {
                ProfileId = profileId,
                QuestionHash = hash,
                OptionsHash = optionsHash,
                Rationale = string.Empty,
            };

            db.FormAnswerResolutions.Add(row);
        }

        row.ResolvedName = Key(outcome.ResolvedName);
        row.AnswerId = outcome.AnswerId;

        // Clamped rather than stored raw, following the polarity clamp on the match path: a
        // confidence outside 0-1 is a caller's arithmetic error, and storing it would let one
        // row outrank every honest resolution in any comparison written later.
        row.Confidence = Math.Clamp(outcome.Confidence, 0, 1);
        row.Rationale = Bound(outcome.Rationale, SubmissionLimits.MaxNoteLength)!;

        // The same width the column carries. A deployment name is a name, and this is the bound
        // names are held to here.
        row.Model = Bound(outcome.Model, FormAnswerLimits.MaxNameLength);
        row.ResolvedAtUtc = now;
        row.Confirmed = outcome.Confirmed;

        await db.SaveChangesAsync(ct);

        return new CachedResolution(
            row.QuestionHash,
            row.OptionsHash,
            row.ResolvedName,
            answer,
            row.Confidence,
            row.Rationale,
            row.Model,
            row.ResolvedAtUtc,
            row.Confirmed);
    }

    /// <summary>
    /// The answers that could apply to a question asked in this context, and no others.
    /// </summary>
    /// <remarks>
    /// The SQL half of <see cref="AnswerPrecedence.Applies"/>. Written as a disjunction over the
    /// scope with the context ids tested first, so that a null context eliminates its arm at
    /// translation time rather than producing a comparison against NULL that behaves differently
    /// on the two engines this runs against.
    ///
    /// Entities rather than a projection, unlike <c>SubmissionRow</c>: every column here is
    /// bounded - the widest is 4,000 characters - and there is no navigation to drag an unbounded
    /// description across with them. Nothing loads <c>Profile</c>, and nothing may.
    /// </remarks>
    private async Task<IReadOnlyList<FormAnswerEntity>> ApplicableAsync(
        long profileId,
        Expression<Func<FormAnswerEntity, bool>> match,
        int? companyId,
        long? postingId,
        CancellationToken ct)
        => await db.FormAnswers
            .AsNoTracking()
            .Where(a => a.ProfileId == profileId)
            .Where(match)
            .Where(a => a.Scope == AnswerScope.Global
                || (companyId != null && a.Scope == AnswerScope.Company && a.CompanyId == companyId)
                || (postingId != null && a.Scope == AnswerScope.Posting && a.PostingId == postingId))
            .ToListAsync(ct);

    /// <summary>
    /// One answer of this candidate's by id, or null where it is not theirs.
    /// </summary>
    /// <remarks>
    /// The profile id is in the predicate rather than checked afterwards, so a stranger's answer
    /// is never materialised at all. "Not found" and "not yours" are the same answer here, as
    /// they are on every other read in this codebase.
    /// </remarks>
    private async Task<StoredAnswer?> AnswerAsync(
        long profileId, long? answerId, DateTimeOffset now, CancellationToken ct)
    {
        if (answerId is not { } id)
        {
            return null;
        }

        var entity = await db.FormAnswers
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.ProfileId == profileId, ct);

        return entity is null ? null : Stored(entity, now);
    }

    /// <summary>
    /// The best of the applicable answers, decided by Core rather than by an <c>ORDER BY</c>.
    /// </summary>
    /// <remarks>
    /// The context is handed to <see cref="AnswerPrecedence.Best"/> a second time, after the
    /// query has already filtered on it. Not redundancy: precedence is Core's to define, and a
    /// <c>Best</c> that had to trust its caller to have filtered first would be a function whose
    /// correctness lives somewhere else.
    /// </remarks>
    private static StoredAnswer? Best(
        IReadOnlyList<FormAnswerEntity> rows, DateTimeOffset now, int? companyId, long? postingId)
    {
        var best = AnswerPrecedence.Best([.. rows.Select(Hydrate)], companyId, postingId);

        return best is null ? null : new StoredAnswer(best, now - best.AnsweredAtUtc);
    }

    private static StoredAnswer Stored(FormAnswerEntity entity, DateTimeOffset now)
        => new(Hydrate(entity), now - entity.AnsweredAtUtc);

    /// <summary>
    /// A stored row as the record Core reasons about.
    /// </summary>
    /// <remarks>
    /// The object initialiser rather than <see cref="FormAnswer.Create"/>, deliberately: the hash
    /// and the normalised question are what is on disk, and recomputing them on the way out would
    /// mean a later change to <see cref="QuestionKey.Normalise"/> silently re-keys every answer
    /// already written - the candidate's answers stop being found and nothing fails.
    ///
    /// No profile id on the record, though the table carries one. It would be a second copy of a
    /// fact the query has already established, and a second copy is free to disagree with the
    /// first; the failure it invites is a caller reading the owner off the row it is deciding
    /// whether to disclose.
    /// </remarks>
    private static FormAnswer Hydrate(FormAnswerEntity entity)
        => new()
        {
            Id = entity.Id,
            Name = entity.Name,
            QuestionText = entity.QuestionText,
            QuestionHash = entity.QuestionHash,
            NormalisedQuestion = entity.NormalisedQuestion,
            Value = entity.Value,
            Scope = entity.Scope,
            CompanyId = entity.CompanyId,
            PostingId = entity.PostingId,
            Sensitive = entity.Sensitive,
            Source = entity.Source,
            AnsweredAtUtc = entity.AnsweredAtUtc,
            SupersededAtUtc = entity.SupersededAtUtc,
        };

    /// <summary>
    /// Refuses an answer the columns cannot hold, rather than letting the database shorten it.
    /// </summary>
    /// <remarks>
    /// Every bound is read from <see cref="FormAnswerLimits"/>, the same constants the entity
    /// configuration is built on, so the column width and the validation cannot drift apart.
    ///
    /// The hash is checked for width rather than recomputed. Recomputing it would silently repair
    /// an answer whose hash was produced by something other than <see cref="QuestionKey.Hash"/> -
    /// and an answer filed under a key nothing else computes is one the candidate can never be
    /// offered back, which is worth failing loudly for.
    /// </remarks>
    private static void EnsureStorable(FormAnswer answer)
    {
        var refused = answer.QuestionText.Length > FormAnswerLimits.MaxQuestionTextLength ? "Question text"
            : answer.NormalisedQuestion.Length > FormAnswerLimits.MaxQuestionTextLength ? "Normalised question"
            : answer.Value.Length > FormAnswerLimits.MaxValueLength ? "Answer"
            : answer.QuestionHash.Length != FormAnswerLimits.QuestionHashLength
                ? $"Question hash is not {FormAnswerLimits.QuestionHashLength} characters and"
                : null;

        if (refused is not null)
        {
            throw new ArgumentException($"{refused} does not fit the column it is stored in.", nameof(answer));
        }
    }

    /// <summary>
    /// A name as it is filed and looked up: trimmed, lower-cased, and refused if it will not fit.
    /// </summary>
    /// <remarks>
    /// Folded so the comparison means the same thing on SQL Server's case-insensitive collation
    /// and on SQLite's case-sensitive one - see <see cref="FindByNameAsync"/>. Refused rather than
    /// truncated because a shortened key names something else; the two fields that <i>are</i>
    /// truncated here are the ones that only explain.
    /// </remarks>
    private static string? Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var folded = name.Trim().ToLowerInvariant();

        return folded.Length <= FormAnswerLimits.MaxNameLength
            ? folded
            : throw new ArgumentException(
                $"Name exceeds {FormAnswerLimits.MaxNameLength} characters.", nameof(name));
    }

    /// <summary>
    /// Trims explanatory text to its column, the way <c>SubmissionRepository</c> does.
    /// </summary>
    /// <remarks>
    /// Only reached for a rationale and a model name. Both are audit text: the worst case of a
    /// shortened one is a shortened audit line, which is the case where truncating beats throwing.
    /// Nothing a candidate wrote passes through here.
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
}
