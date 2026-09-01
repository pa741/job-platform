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
/// </remarks>
public sealed record SubmissionRow(
    long Id,
    long PostingId,
    string Title,
    string? Company,
    SubmissionChannel Channel,
    string? ApplyUrl,
    DateTimeOffset CreatedAtUtc,
    SubmissionStatus Status);

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
/// <b>Nothing here deletes.</b> Withdrawing is a <c>Withdrawn</c> event.
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
                        now)))
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

    /// <summary>The full log for one of the caller's submissions, oldest first.</summary>
    public async Task<IReadOnlyList<SubmissionEvent>> ListEventsAsync(
        long profileId, long submissionId, CancellationToken ct = default)
        => await db.SubmissionEvents
            .AsNoTracking()
            .Where(e => e.SubmissionId == submissionId && e.Submission!.ProfileId == profileId)
            .OrderBy(e => e.AtUtc)
            .ThenBy(e => e.Id)
            .Select(e => new SubmissionEvent(e.AtUtc, e.Type, e.Stage, e.Source, e.Note))
            .ToListAsync(ct);

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
    /// Appends one event, converging on a retry and refusing past the daily cap.
    /// </summary>
    /// <remarks>
    /// <b>Every outcome here is ordinary rather than exceptional</b>, which is why it answers
    /// with a <see cref="SubmissionEventResult"/> rather than throwing or returning a bool. A
    /// retrying client must be able to send the same event twice and get the same answer; a
    /// client that has hit the cap must be told to stop rather than encouraged to try again.
    ///
    /// <b>The cap is enforced here and nowhere else.</b> It counts <c>Submitted</c> events this
    /// candidate has recorded today, by <c>AtUtc</c>, across every submission. Bounding it in the
    /// sink is the same rule <c>AiCallRecord.Create</c> follows: two call sites reach this today
    /// and a third will, and a guard written at the call sites survives until then.
    ///
    /// <b>Counted by <c>AtUtc</c>, not by when the row was written.</b> That is what the event
    /// claims happened, so backdating a hundred events into one day is the same assertion as
    /// making them now and is capped the same way. It does mean somebody importing a real
    /// history can hit the cap; that is the right trade for a bound whose job is to be
    /// unarguable.
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

        if (submissionEvent.Type == SubmissionEventType.Submitted)
        {
            var dayStart = new DateTimeOffset(submissionEvent.AtUtc.UtcDateTime.Date, TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1);

            var sentToday = await db.SubmissionEvents
                .AsNoTracking()
                .CountAsync(
                    e => e.Submission!.ProfileId == profileId
                        && e.Type == SubmissionEventType.Submitted
                        && e.AtUtc >= dayStart
                        && e.AtUtc < dayEnd,
                    ct);

            if (sentToday >= SubmissionLimits.MaxSubmittedPerDay)
            {
                return SubmissionEventResult.DailyLimitReached;
            }
        }

        db.SubmissionEvents.Add(new SubmissionEventEntity
        {
            SubmissionId = submissionId,
            AtUtc = submissionEvent.AtUtc,
            Type = submissionEvent.Type,
            Stage = Bound(submissionEvent.Stage, SubmissionLimits.MaxStageLength),
            Source = submissionEvent.Source,
            Note = Bound(submissionEvent.Note, SubmissionLimits.MaxNoteLength),
            IdempotencyKey = key,
        });

        await db.SaveChangesAsync(ct);

        return SubmissionEventResult.Recorded;
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
