using System.Text.Json;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// One submission with its status already folded, as every read of this table projects it.
/// </summary>
/// <remarks>
/// A record rather than the entity, so a list never materialises a <c>JobPostingEntity</c> and
/// drags an unbounded description across for every row - the same reason <c>MatchRow</c> exists.
///
/// <b>The park is projected here because it is the one fact about a submission the fold cannot
/// answer.</b> <see cref="SubmissionStatus"/> is derived from the events and a park is
/// deliberately not an event, so a reader holding only the status cannot tell an application
/// that was sent from a row that exists because something got in the way. Every reader that
/// counts submissions has to be taught that difference - the dashboard first, which counts any
/// row with a non-null phase as sent, and a parked row must not land in that total.
/// </remarks>
/// <param name="ParkedReason">Why no application was made, where none was. Null on a real submission.</param>
/// <param name="ParkedAtUtc">When it was put down.</param>
/// <param name="UnparkedAtUtc">When it was let back into the queue, where it has been.</param>
/// <param name="DocumentRevision">Which revision of the generated documents was sent, where any were.</param>
/// <param name="RunId">The unattended pass that created it, where one did.</param>
public sealed record SubmissionRow(
    long Id,
    long PostingId,
    string Title,
    string? Company,
    SubmissionChannel Channel,
    string? ApplyUrl,
    DateTimeOffset CreatedAtUtc,
    SubmissionStatus Status,
    ParkReason? ParkedReason = null,
    DateTimeOffset? ParkedAtUtc = null,
    DateTimeOffset? UnparkedAtUtc = null,
    int? DocumentRevision = null,
    long? RunId = null)
{
    /// <summary>Whether the park stands right now.</summary>
    /// <remarks>
    /// <b>The pair, never <see cref="ParkedReason"/> alone.</b> Nothing on this table is cleared,
    /// so a row parked in March and applied to in April still carries the reason it was parked
    /// for - and a client reading that column on its own reports a live application as parked
    /// forever. This is the same reading the queue predicate makes, asked once here so a caller
    /// does not write it out a second time and get it wrong in the other direction.
    /// </remarks>
    public bool IsParked => ParkedReason is not null && UnparkedAtUtc is null;
}

/// <summary>
/// What one atomic create-and-record did, and what stands on the table afterwards.
/// </summary>
/// <remarks>
/// <b>Three fields because there are four outcomes and a caller acts differently on each.</b>
/// None of them is exceptional - the same argument <see cref="SubmissionEventResult"/> already
/// makes, extended over a pair of writes:
///
/// <list type="table">
/// <item>
///   <term>Created, recorded</term>
///   <description>
///   <see cref="Row"/> is the new submission, <see cref="Created"/> is true, <see cref="Event"/>
///   is <see cref="SubmissionEventResult.Recorded"/>. Both rows went in together.
///   </description>
/// </item>
/// <item>
///   <term>Already existed, event recorded</term>
///   <description>
///   The submission was there and this event was not, which is the ordinary second claim about
///   one application rather than a failure. <see cref="Created"/> is false and
///   <see cref="Event"/> is <see cref="SubmissionEventResult.Recorded"/>.
///   </description>
///  </item>
/// <item>
///   <term>Already existed, event already recorded</term>
///   <description>
///   The retry converged on both writes: <see cref="Created"/> false,
///   <see cref="SubmissionEventResult.AlreadyRecorded"/>. <b>A success, not a refusal</b> - a
///   client that cannot tell the two apart re-records events against a row it already has.
///   </description>
/// </item>
/// <item>
///   <term>Cap reached</term>
///   <description>
///   <see cref="SubmissionEventResult.DailyLimitReached"/>, and <b>nothing at all was
///   written</b>. <see cref="Row"/> is null where no submission existed beforehand and is the
///   pre-existing row where one did, so null means "there is still nothing here" and never
///   "there is something you may not see".
///   </description>
/// </item>
/// </list>
///
/// <b><see cref="Row"/> is nullable for exactly one reason, and it is worth stating.</b> A create
/// refused by the cap must not leave a submission behind: a submission row is what takes a
/// posting out of the applyable queue, so a row whose <c>Submitted</c> event was refused would
/// remove that posting from every future run while asserting nothing about an application ever
/// having been sent. The posting would simply never be offered again, with nothing anywhere
/// recording why. A caller that wants the attempt remembered parks it - which is what
/// <see cref="ParkReason.OutOfQuota"/> is for, and a park is visible where a bare row is not.
/// </remarks>
/// <param name="Row">The submission as it now stands, or null where the cap stopped one being made.</param>
/// <param name="Created">Whether this call brought the submission into existence.</param>
/// <param name="Event">What happened to the event that came with it.</param>
public sealed record SubmissionWriteResult(
    SubmissionRow? Row,
    bool Created,
    SubmissionEventResult Event);

/// <summary>
/// Every read and write of the submission pipeline.
/// </summary>
/// <remarks>
/// <b>Takes a profile id and never a submission id alone.</b> That is the authorisation boundary
/// expressed as a type, the rule <c>CandidateProfileRepository</c> sets and
/// <c>ApplicationDocumentRepository</c> follows: there is no method an endpoint or a tool could
/// hand a bare route parameter to, so a stranger's pipeline cannot be read or written by mistake.
/// It matters more here than on a route, because an MCP tool's arguments are named by a model.
///
/// <b>Nothing here deletes.</b> Withdrawing is a <c>Withdrawn</c> event, and <b>parking is not a
/// deletion either</b> - it sets attribute columns on the row and leaves the log alone, because
/// the fold has no numbering that survives a park as an event and no way to un-see one.
///
/// <b>This reads and writes Azure SQL</b>, which the architecture otherwise reserves for posting
/// browse, search and detail. Bounded like the profile's: read when a page opens or a tool asks,
/// written when something actually happened. It must never become a polling path - that is what
/// the MCP feature's own rate-limit policy is for - and <b>nothing here may join a client's
/// bootstrap sequence</b>.
/// </remarks>
public sealed class SubmissionRepository(JobsDbContext db)
{
    /// <summary>
    /// The caller's submissions, most recently active first, each folded to a status.
    /// </summary>
    /// <remarks>
    /// The events are loaded with the rows rather than per submission: this is a page of
    /// twenty-odd applications with a handful of events each, and a per-row round trip against a
    /// database billed by the second is the cost this codebase avoids everywhere else.
    ///
    /// <b>The evidence columns are deliberately not loaded here.</b> The fold does not read them
    /// and <c>SubmittedFieldsJson</c> is unbounded, so pulling it for every event of every
    /// submission is the fault <see cref="SubmissionRow"/> exists to avoid and the same one that
    /// keeps <c>Description</c> off every list response. One submission's evidence is read by
    /// <see cref="ListEventsAsync"/>, where the set is a handful of rows.
    ///
    /// Ordering happens after the fold, in memory, because the key is
    /// <c>SubmissionStatus.LastActivityUtc</c> - a derived value with no column to sort on. That
    /// is affordable exactly because the set is one person's applications and not the corpus; if
    /// it ever stops being, the fix is a bounded page rather than a stored status.
    /// </remarks>
    public async Task<IReadOnlyList<SubmissionRow>> ListAsync(
        long profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.Submissions
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId)
            .Select(s => new
            {
                s.Id,
                s.PostingId,
                s.Posting!.Title,
                s.Posting.Company,
                s.Channel,
                s.ApplyUrl,
                s.CreatedAtUtc,
                s.ParkedReason,
                s.ParkedAtUtc,
                s.UnparkedAtUtc,
                s.DocumentRevision,
                s.RunId,
                Events = s.Events
                    .Select(e => new { e.AtUtc, e.Type, e.Stage, e.Source, e.Note })
                    .ToList(),
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                .Select(r => new SubmissionRow(
                    r.Id,
                    r.PostingId,
                    r.Title,
                    r.Company,
                    r.Channel,
                    r.ApplyUrl,
                    r.CreatedAtUtc,
                    SubmissionState.Fold(
                        r.CreatedAtUtc,
                        [.. r.Events.Select(e => new SubmissionEvent(e.AtUtc, e.Type, e.Stage, e.Source, e.Note))],
                        now),
                    r.ParkedReason,
                    r.ParkedAtUtc,
                    r.UnparkedAtUtc,
                    r.DocumentRevision,
                    r.RunId))
                .OrderByDescending(r => r.Status.LastActivityUtc)
                // Deterministic beyond the key: two submissions created in the same request
                // share a timestamp, and a page that shuffles between identical requests is a
                // bug nobody can reproduce.
                .ThenByDescending(r => r.Id),
        ];
    }

    /// <summary>One of the caller's submissions, or null where they do not own it.</summary>
    /// <remarks>
    /// A caller cannot distinguish "no such submission" from "not yours", which is the point -
    /// the same rule <c>ScraperSearchRepository</c> applies to a slug in a route.
    /// </remarks>
    public async Task<SubmissionRow?> GetAsync(
        long profileId, long submissionId, DateTimeOffset now, CancellationToken ct = default)
        => (await ListAsync(profileId, now, ct)).FirstOrDefault(s => s.Id == submissionId);

    /// <summary>
    /// The full log for one of the caller's submissions, oldest first, with what was captured.
    /// </summary>
    /// <remarks>
    /// <b>The evidence is composed after materialisation rather than inside the query</b>, and
    /// the field list is why: it is stored as JSON and no query provider turns JSON into an
    /// <c>IReadOnlyList&lt;string&gt;</c>. So the columns come back flat and the two Core records
    /// are built here - which also lets a row of nulls become no evidence at all, so a reader can
    /// tell "captured nothing" from "captured this much" instead of finding an empty block hung
    /// off every event.
    ///
    /// <c>SubmissionEvent.Evidence</c> is an <c>init</c> property rather than a sixth
    /// constructor parameter, and that is what keeps the composition below legal at all: a
    /// trailing optional argument omitted inside an expression tree is CS0854, so a positional
    /// version of this type could not be projected out of EF once it grew one.
    /// </remarks>
    public async Task<IReadOnlyList<SubmissionEvent>> ListEventsAsync(
        long profileId, long submissionId, CancellationToken ct = default)
    {
        var rows = await db.SubmissionEvents
            .AsNoTracking()
            .Where(e => e.SubmissionId == submissionId && e.Submission!.ProfileId == profileId)
            .OrderBy(e => e.AtUtc)
            .ThenBy(e => e.Id)
            .Select(e => new
            {
                e.AtUtc,
                e.Type,
                e.Stage,
                e.Source,
                e.Note,
                e.ConfirmationRef,
                e.FinalUrl,
                e.ScreenshotRef,
                e.SubmittedFieldsJson,
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(e => new SubmissionEvent(e.AtUtc, e.Type, e.Stage, e.Source, e.Note)
            {
                Evidence = ReadEvidence(e.ConfirmationRef, e.FinalUrl, e.ScreenshotRef, e.SubmittedFieldsJson),
            }),
        ];
    }

    /// <summary>
    /// How much of one UTC day's cap on <c>Submitted</c> events this candidate has left.
    /// </summary>
    /// <remarks>
    /// <b>The cap stays; this makes it visible.</b> An agent that knows six are left sends six.
    /// An agent that does not find out by being refused, one refusal at a time - and the refusal
    /// arrives at <see cref="AddEventAsync"/>, which by the loop's design runs after the browser
    /// has already filled in and sent the form. That produces an application that exists in the
    /// world and cannot be recorded, which is the worst state this system has, because every
    /// later decision reads the log rather than the world.
    ///
    /// <b>It counts through the same function the cap enforces with</b>, and that is the point of
    /// the method rather than an implementation detail: a burn-down written out a second time
    /// drifts from the bound it claims to describe, and the drift is invisible - nobody notices a
    /// number that is quietly one out until a run is refused with headroom still showing.
    ///
    /// <b>Not a reservation.</b> Nothing is held back, so two clients sharing a candidate can
    /// each be told six. The cap in <see cref="AddEventAsync"/> remains the authority; this is
    /// for planning a batch while it is still free to choose one.
    /// </remarks>
    /// <param name="profileId">The candidate, resolved by the caller.</param>
    /// <param name="atUtc">Any instant inside the day being asked about. Read in UTC, whatever offset it carries.</param>
    public async Task<SubmissionQuota> GetQuotaAsync(
        long profileId, DateTimeOffset atUtc, CancellationToken ct = default)
        => SubmissionQuota.For(atUtc, await CountSubmittedOnDayAsync(profileId, atUtc, ct));

    /// <summary>
    /// Records that an application was sent, or returns the one already recorded.
    /// </summary>
    /// <remarks>
    /// <b>Converges rather than duplicating or throwing.</b> The unique index on
    /// <c>(ProfileId, PostingId)</c> is what makes that guarantee real; this checks first so the
    /// ordinary retry is an answer rather than an exception, and the index catches the race the
    /// check cannot. That is the ingestion contract restated - a redelivery converges.
    ///
    /// The apply URL is copied in by the caller rather than joined from the posting, because a
    /// re-scrape may rewrite it and this is a record of where the application actually went.
    ///
    /// <b>This creates a row and claims nothing.</b> Where the caller is recording an application
    /// it has just made, <see cref="CreateWithEventAsync"/> is the one to use: it puts the row
    /// and the claim in one write, so no failure can leave a sent application unrecorded.
    /// </remarks>
    public async Task<(SubmissionRow Row, bool Created)> CreateAsync(
        long profileId,
        long postingId,
        SubmissionChannel channel,
        string? applyUrl,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var existing = await db.Submissions
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId && s.PostingId == postingId)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0)
        {
            var already = await GetAsync(profileId, existing, now, ct);

            // Non-null in practice: the row was just read under the same profile id.
            return (already!, false);
        }

        var entity = new SubmissionEntity
        {
            ProfileId = profileId,
            PostingId = postingId,
            Channel = channel,
            ApplyUrl = Bound(applyUrl, SubmissionLimits.MaxApplyUrlLength),
            CreatedAtUtc = now,
        };

        db.Submissions.Add(entity);
        await db.SaveChangesAsync(ct);

        var row = await GetAsync(profileId, entity.Id, now, ct);

        return (row!, true);
    }

    /// <summary>
    /// Records an application and the claim that it was sent, in one write.
    /// </summary>
    /// <remarks>
    /// <b>One <c>SaveChangesAsync</c> inserts both rows, and that is the whole feature.</b> A
    /// submission created by one call and evidenced by a second has a window between them, and a
    /// client that dies inside it leaves a row asserting an application exists with nothing
    /// saying it was sent - or worse, retries the create, is told the row already exists, and
    /// carries on believing the send was recorded. An application that happened in the world and
    /// is not in the log is the failure this pipeline cannot recover from, because every later
    /// decision reads the log rather than the world.
    ///
    /// <b>No explicit transaction, deliberately.</b> There is not one anywhere in this
    /// repository: each <c>SaveChangesAsync</c> is its own implicit one, so a single call
    /// inserting a submission and its event is already atomic. Introducing a transaction idiom
    /// the codebase does not have would be a second way of writing that every other write would
    /// then have to be read against.
    ///
    /// <b>The order of the three checks is load-bearing, and it is the order
    /// <see cref="AddEventAsync"/> already uses.</b> Idempotency first, then the cap, then the
    /// insert. A client retrying a write it is unsure landed must not be refused for a quota
    /// that very event already spent, and the cap must not be checked after a row it would have
    /// prevented is already in the table. Here the first check is the submission probe rather
    /// than a separate event probe, because an event's key is unique <i>per submission</i>: with
    /// no submission there can be no event carrying that key, so "the submission already exists"
    /// is the only shape a retry can take.
    ///
    /// <b>On the retry path the event goes through <see cref="AddEventAsync"/>.</b> The row is
    /// already there, so there is no insert to inline it into - and that method is where the
    /// idempotency probe and the cap are ordered against each other. Repeating that ordering
    /// here would be a second copy of it, free to drift from the first.
    ///
    /// <b>Nothing is written when the cap refuses</b>, not even the submission. See
    /// <see cref="SubmissionWriteResult"/>: a row whose event was refused takes the posting out
    /// of the queue for good while asserting nothing about an application.
    /// </remarks>
    /// <param name="profileId">The candidate, resolved by the caller.</param>
    /// <param name="postingId">What was applied to.</param>
    /// <param name="channel">Where the application was made.</param>
    /// <param name="applyUrl">Where it went, as that stood at the time.</param>
    /// <param name="submissionEvent">The claim being made about it, with whatever was captured while making it.</param>
    /// <param name="idempotencyKey">What makes a retry converge. <c>ApplyRun.Key</c> is the convention a run follows.</param>
    /// <param name="now">When the row is being created, which is not when the event says it happened.</param>
    /// <param name="documentRevision">
    /// Which revision of the generated documents was sent. <b>Passed by the caller rather than
    /// read back from <c>ApplicationDocuments</c></b>, because the pack the client actually used
    /// is the fact being recorded: a regeneration between the pack and the send produces a better
    /// draft that was never the one an employer read, and a repository looking up "the latest"
    /// would quietly record that one instead.
    /// </param>
    /// <param name="runId">
    /// The unattended pass doing this, where one is. It is what makes a run's own account of
    /// itself checkable - <c>RunSummary.Submitted</c> can be counted against these rows, where
    /// <c>Considered</c> can be counted against nothing at all.
    /// </param>
    public async Task<SubmissionWriteResult> CreateWithEventAsync(
        long profileId,
        long postingId,
        SubmissionChannel channel,
        string? applyUrl,
        SubmissionEvent submissionEvent,
        string idempotencyKey,
        DateTimeOffset now,
        int? documentRevision = null,
        long? runId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submissionEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var existing = await db.Submissions
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId && s.PostingId == postingId)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0)
        {
            var recorded = await AddEventAsync(profileId, existing, submissionEvent, idempotencyKey, ct);
            var already = await GetAsync(profileId, existing, now, ct);

            // Non-null in practice: the row was just read under the same profile id.
            return new SubmissionWriteResult(already!, false, recorded);
        }

        if (await DailyCapReachedAsync(profileId, submissionEvent, ct))
        {
            return new SubmissionWriteResult(null, false, SubmissionEventResult.DailyLimitReached);
        }

        var entity = new SubmissionEntity
        {
            ProfileId = profileId,
            PostingId = postingId,
            Channel = channel,
            ApplyUrl = Bound(applyUrl, SubmissionLimits.MaxApplyUrlLength),
            CreatedAtUtc = now,
            DocumentRevision = documentRevision,
            RunId = runId,
        };

        // Through the navigation rather than the DbSet, which is what makes this one write: the
        // submission has no id until SaveChanges assigns one, and EF's fix-up is what carries
        // that id into the event's foreign key inside the same round trip.
        entity.Events.Add(ToEntity(submissionEvent, Bound(idempotencyKey, SubmissionLimits.MaxIdempotencyKeyLength)!));

        db.Submissions.Add(entity);
        await db.SaveChangesAsync(ct);

        var row = await GetAsync(profileId, entity.Id, now, ct);

        return new SubmissionWriteResult(row!, true, SubmissionEventResult.Recorded);
    }

    /// <summary>
    /// Puts a posting down, recording why, and creating the row to hang that on where there is none.
    /// </summary>
    /// <remarks>
    /// <b>Parking sets columns; it never deletes and never appends an event.</b> The event ladder
    /// is a total order over how far an application got and a park is not a point on it - it says
    /// no attempt was made at all. <c>ParkReason</c>'s own remarks work through every numbering
    /// and none of them survives the fold.
    ///
    /// <b>It takes a posting id rather than a submission id, because the row may not exist
    /// yet.</b> Most parks happen before anything was sent - a login wall, a captcha, a question
    /// nobody has answered - so there is nothing to park unless this makes it. Such a row is not
    /// a claim that an application was made: <c>SubmissionEntity.ParkedReason</c> says as much,
    /// and a caller counting sent applications asks <see cref="SubmissionRow.IsParked"/>. Its
    /// channel is <see cref="SubmissionChannel.Unknown"/> rather than a guess, because nothing
    /// established where the application would be made - none was made.
    ///
    /// <b>Idempotent by state rather than by a key.</b> A park that says what the row already
    /// says writes nothing at all, so a client parking the same posting for the same reason on
    /// every run does not walk <c>ParkedAtUtc</c> forward nightly and turn "blocked since
    /// Tuesday" into "blocked a minute ago" - which is the fact somebody reading the queue is
    /// actually after. Re-parking with a <i>different</i> reason is a real change and is
    /// recorded: the latest reason stands, because it is the one the last attempt hit.
    ///
    /// <b>A re-park clears <c>UnparkedAtUtc</c>, and that is the one value on this table ever
    /// cleared.</b> The queue reads the pair - a submission is live if it was never parked or has
    /// been unparked - so a re-park leaving an old unpark standing would be a parked row every
    /// reader treats as a live application, which is the exact fault parking exists to fix. What
    /// is given up is the history of earlier parks on that row; that history is in the run
    /// summaries and the notes, and the alternative is a row that lies about the present.
    /// </remarks>
    /// <param name="profileId">The candidate, resolved by the caller.</param>
    /// <param name="postingId">What is being put down.</param>
    /// <param name="reason">Why. <c>ParkReasonPolicy</c> decides from it whether and when the posting comes back.</param>
    /// <param name="now">When.</param>
    /// <param name="applyUrl">Where the attempt was headed, for the row this has to create.</param>
    /// <param name="runId">The unattended pass doing this, where one is.</param>
    /// <returns>The row as it now stands, and whether this park brought it into existence.</returns>
    public async Task<(SubmissionRow Row, bool Created)> ParkAsync(
        long profileId,
        long postingId,
        ParkReason reason,
        DateTimeOffset now,
        string? applyUrl = null,
        long? runId = null,
        CancellationToken ct = default)
    {
        var entity = await db.Submissions
            .FirstOrDefaultAsync(s => s.ProfileId == profileId && s.PostingId == postingId, ct);

        var created = entity is null;

        if (entity is null)
        {
            entity = new SubmissionEntity
            {
                ProfileId = profileId,
                PostingId = postingId,
                Channel = SubmissionChannel.Unknown,
                ApplyUrl = Bound(applyUrl, SubmissionLimits.MaxApplyUrlLength),
                CreatedAtUtc = now,
                RunId = runId,
            };

            db.Submissions.Add(entity);
        }

        if (entity.ParkedReason != reason || entity.UnparkedAtUtc is not null)
        {
            entity.ParkedReason = reason;
            entity.ParkedAtUtc = now;
            entity.UnparkedAtUtc = null;
        }

        // A no-op where nothing moved: EF issues no round trip with nothing tracked to write, so
        // the idempotent path costs the read it has already made and nothing else.
        await db.SaveChangesAsync(ct);

        var row = await GetAsync(profileId, entity.Id, now, ct);

        return (row!, created);
    }

    /// <summary>
    /// Lets a parked posting back into the queue.
    /// </summary>
    /// <remarks>
    /// <b>A second timestamp rather than a clearing of the first two.</b> "Was never parked" and
    /// "was parked for a captcha in March and applied to in April" are different histories, and a
    /// row that erased the park to express the second would be indistinguishable from the first.
    /// So the reason and the date it was put down stay where they are, and the reversal is an
    /// append in substance even though it is one column.
    ///
    /// <b>Idempotent because the answer is the state and not the transition.</b> Called on a row
    /// that was never parked, or that has already been unparked, it writes nothing and answers
    /// with the same row - there is no "already unparked" refusal to handle, because there is
    /// nothing a caller would do differently on hearing it. Null means what it means everywhere
    /// else here: no such submission for this candidate, which a caller cannot tell from "not
    /// yours".
    ///
    /// <b>Most parked postings return without this being called.</b> <c>ParkReasonPolicy</c>
    /// decides that from the reason alone - a captcha is offered again next run, an unanswered
    /// question when its answer arrives - so this is for the case where somebody decides a park
    /// should not have stood.
    /// </remarks>
    public async Task<SubmissionRow?> UnparkAsync(
        long profileId, long postingId, DateTimeOffset now, CancellationToken ct = default)
    {
        var entity = await db.Submissions
            .FirstOrDefaultAsync(s => s.ProfileId == profileId && s.PostingId == postingId, ct);

        if (entity is null)
        {
            return null;
        }

        if (entity.ParkedReason is not null && entity.UnparkedAtUtc is null)
        {
            entity.UnparkedAtUtc = now;
            await db.SaveChangesAsync(ct);
        }

        return await GetAsync(profileId, entity.Id, now, ct);
    }

    /// <summary>
    /// Appends one event, converging on a retry and refusing past the daily cap.
    /// </summary>
    /// <remarks>
    /// <b>Every outcome here is ordinary rather than exceptional</b>, which is why it answers
    /// with a <see cref="SubmissionEventResult"/> rather than throwing or returning a bool. A
    /// retrying client must be able to send the same event twice and get the same answer; a
    /// client that has hit the cap must be told to stop rather than encouraged to try again.
    ///
    /// <b>The cap is enforced here and nowhere else.</b> It counts <c>Submitted</c> events this
    /// candidate has recorded today, by <c>AtUtc</c>, across every submission - through
    /// <see cref="CountSubmittedOnDayAsync"/>, which is also what <see cref="GetQuotaAsync"/>
    /// reports, so the bound and the burn-down describing it cannot disagree. Bounding it in the
    /// sink is the same rule <c>AiCallRecord.Create</c> follows: two call sites reach this today
    /// and a third will, and a guard written at the call sites survives until then.
    ///
    /// <b>Counted by <c>AtUtc</c>, not by when the row was written.</b> That is what the event
    /// claims happened, so backdating a hundred events into one day is the same assertion as
    /// making them now and is capped the same way. It does mean somebody importing a real
    /// history can hit the cap; that is the right trade for a bound whose job is to be
    /// unarguable.
    ///
    /// <b>The evidence rides on the event and is bounded on the way in.</b> Every part of it is
    /// optional and none of it may block the write: this is gathered by something driving a
    /// browser through somebody else's form, the interesting runs are the ones that go wrong, and
    /// refusing to record that an application was sent because the screenshot failed loses the
    /// fact in order to protect the proof of it.
    ///
    /// The submission is resolved through the caller's profile id, so an id from a route or from
    /// a model's argument cannot append to a stranger's log.
    /// </remarks>
    public async Task<SubmissionEventResult> AddEventAsync(
        long profileId,
        long submissionId,
        SubmissionEvent submissionEvent,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submissionEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var owned = await db.Submissions
            .AsNoTracking()
            .AnyAsync(s => s.Id == submissionId && s.ProfileId == profileId, ct);

        if (!owned)
        {
            return SubmissionEventResult.NotFound;
        }

        var key = Bound(idempotencyKey, SubmissionLimits.MaxIdempotencyKeyLength)!;

        // Checked before the cap, so a retry of an event that is already recorded answers
        // AlreadyRecorded rather than being refused for a quota it has already spent.
        if (await db.SubmissionEvents.AnyAsync(
                e => e.SubmissionId == submissionId && e.IdempotencyKey == key, ct))
        {
            return SubmissionEventResult.AlreadyRecorded;
        }

        if (await DailyCapReachedAsync(profileId, submissionEvent, ct))
        {
            return SubmissionEventResult.DailyLimitReached;
        }

        var entity = ToEntity(submissionEvent, key);

        // Set here, and left to EF's fix-up on the inline path, where the submission this belongs
        // to has no id until the same SaveChanges assigns one.
        entity.SubmissionId = submissionId;

        db.SubmissionEvents.Add(entity);

        await db.SaveChangesAsync(ct);

        return SubmissionEventResult.Recorded;
    }

    /// <summary>
    /// Whether this event is a claim of sending that the day has no room left for.
    /// </summary>
    /// <remarks>
    /// <b>One function, so the two write paths cannot come to enforce two different caps.</b>
    /// <see cref="AddEventAsync"/> and <see cref="CreateWithEventAsync"/> both ask this before
    /// they insert, and each would otherwise carry a copy of the same three facts: that the bound
    /// is on <c>Submitted</c> alone, that the day is the event's own, and where the count comes
    /// from.
    ///
    /// <b>It bounds <c>Submitted</c> alone.</b> Recording that a hundred applications exist is
    /// fine - somebody may be importing a history - and claiming a hundred were sent today is
    /// not.
    /// </remarks>
    private async Task<bool> DailyCapReachedAsync(
        long profileId, SubmissionEvent submissionEvent, CancellationToken ct)
        => submissionEvent.Type is SubmissionEventType.Submitted
            && await CountSubmittedOnDayAsync(profileId, submissionEvent.AtUtc, ct)
                >= SubmissionLimits.MaxSubmittedPerDay;

    /// <summary>
    /// How many applications this candidate has claimed to send inside the UTC day holding an instant.
    /// </summary>
    /// <remarks>
    /// <b>The single definition of the rule the cap enforces and the quota reports.</b> Two
    /// copies of a counting rule drift, and this one has three parts that are each easy to write
    /// differently by accident: <c>Submitted</c> only, across every submission this candidate has
    /// rather than one, and windowed on the event's own <c>AtUtc</c> rather than on when the row
    /// was written. A burn-down counting rows by write time would disagree with the bound
    /// actually in force, and would do so only for the events that were backdated - the ones
    /// nobody is watching.
    /// </remarks>
    private async Task<int> CountSubmittedOnDayAsync(
        long profileId, DateTimeOffset atUtc, CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(atUtc.UtcDateTime.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        return await db.SubmissionEvents
            .AsNoTracking()
            .CountAsync(
                e => e.Submission!.ProfileId == profileId
                    && e.Type == SubmissionEventType.Submitted
                    && e.AtUtc >= dayStart
                    && e.AtUtc < dayEnd,
                ct);
    }

    /// <summary>
    /// Builds the row for one event, with its evidence bounded to the columns that have to hold it.
    /// </summary>
    /// <remarks>
    /// <b>Shared by both write paths</b>, so an event inlined into a create and an event appended
    /// later are stored identically - the bounding included, which is the part that would
    /// otherwise be applied on one path and forgotten on the other.
    ///
    /// <b>The field list is bounded after the blanks are dropped, not before.</b> A client
    /// enumerating every input on a half-rendered page produces mostly empty names, and a count
    /// taken first would spend the whole budget on those and truncate away the names that were
    /// real.
    /// </remarks>
    private static SubmissionEventEntity ToEntity(SubmissionEvent submissionEvent, string key)
    {
        var evidence = submissionEvent.Evidence;

        var fields = evidence?.SubmittedFields?
            .Select(name => Bound(name, SubmissionLimits.MaxSubmittedFieldNameLength))
            .OfType<string>()
            .Take(SubmissionLimits.MaxSubmittedFieldCount)
            .ToList();

        return new SubmissionEventEntity
        {
            AtUtc = submissionEvent.AtUtc,
            Type = submissionEvent.Type,
            Stage = Bound(submissionEvent.Stage, SubmissionLimits.MaxStageLength),
            Source = submissionEvent.Source,
            Note = Bound(submissionEvent.Note, SubmissionLimits.MaxNoteLength),
            IdempotencyKey = key,
            ConfirmationRef = Bound(evidence?.ConfirmationRef, SubmissionLimits.MaxConfirmationRefLength),
            FinalUrl = Bound(evidence?.FinalUrl, SubmissionLimits.MaxFinalUrlLength),
            ScreenshotRef = Bound(evidence?.ScreenshotRef, SubmissionLimits.MaxScreenshotRefLength),

            // Null rather than "[]" where nothing survived, so the column agrees with
            // SubmissionEvidence.IsEmpty: an empty list is not a capture.
            SubmittedFieldsJson = fields is { Count: > 0 } ? JsonSerializer.Serialize(fields) : null,
        };
    }

    /// <summary>
    /// Composes the evidence block back out of its columns, or nothing where nothing was captured.
    /// </summary>
    /// <remarks>
    /// <b>Null where the block is empty</b>, asked through <c>SubmissionEvidence.IsEmpty</c>
    /// rather than by a null check per column, because blank counts as nothing there: a selector
    /// that matched an empty element stores <c>""</c>, and a per-column check would hang a block
    /// of blanks off every event and put proof on the dashboard that does not exist.
    ///
    /// <b>A malformed field list reads as no field list.</b> The column is written only by
    /// <see cref="ToEntity"/>, so that arm is reached by data rather than by code - a
    /// hand-edited row, or a shape from a newer build - and losing the event's timestamp, type
    /// and confirmation reference over an unparseable list would be the worse answer. The same
    /// call <c>ApplicationDocumentRepository</c> makes over <c>EmphasisedJson</c>.
    /// </remarks>
    private static SubmissionEvidence? ReadEvidence(
        string? confirmationRef, string? finalUrl, string? screenshotRef, string? submittedFieldsJson)
    {
        var evidence = new SubmissionEvidence
        {
            ConfirmationRef = confirmationRef,
            FinalUrl = finalUrl,
            ScreenshotRef = screenshotRef,
            SubmittedFields = ReadFields(submittedFieldsJson),
        };

        return evidence.IsEmpty ? null : evidence;
    }

    private static IReadOnlyList<string>? ReadFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Trims to the column's width rather than letting the database do it.
    /// </summary>
    /// <remarks>
    /// A silent truncation on the way in is the shape of bug this codebase has paid for before.
    /// The bounds live on <c>SubmissionLimits</c> so the schema and the validation cannot drift.
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
