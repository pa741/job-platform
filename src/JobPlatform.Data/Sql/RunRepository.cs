using System.Text.Json;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>What finishing a run did.</summary>
/// <remarks>
/// Structured rather than thrown, like <see cref="SubmissionEventResult"/>: an unattended client
/// finishing a run it is unsure it already finished must get an answer it can act on rather than
/// an exception it will retry into.
/// </remarks>
public enum RunFinishResult
{
    /// <summary>The run is closed and its account is stored.</summary>
    Finished = 0,

    /// <summary>No such run for this candidate. Indistinguishable from "not yours".</summary>
    NotFound = 1,

    /// <summary>
    /// It was already finished, and the account that stands is the first one.
    /// </summary>
    /// <remarks>
    /// The retry converged; nothing was rewritten. A second <c>finish_run</c> carrying different
    /// counts is a client that has lost track of itself, and letting it overwrite would replace
    /// an account somebody may already have read with one nobody can compare it to.
    /// </remarks>
    AlreadyFinished = 2,
}

/// <summary>
/// One candidate's unattended passes over the applyable queue.
/// </summary>
/// <remarks>
/// <b>Shaped like the scrape-run reads in <c>JobPostingQueryRepository</c> - start, then list and
/// get - and pointedly not joined to them.</b> <c>ScrapeRuns</c> is one blob of scraped postings
/// and belongs to ingestion; this is one person's apply pass. The only thing they share is the
/// word, and reading them as neighbours is how a table that holds nobody's data acquires a
/// profile id.
///
/// <b>Takes a profile id the caller has already resolved</b>, like every other repository on this
/// side of the system, so there is no method a route parameter or a model's argument could be
/// handed to reach a stranger's runs.
///
/// <b>The daily cap is not here and must not move here.</b> It counts <c>Submitted</c> events by
/// their own <c>AtUtc</c> across every submission, lives in <c>SubmissionRepository</c>, and a
/// per-run counter would be a second and weaker copy of it: a client that crashes and restarts
/// twenty times would spend twenty budgets against one day, and a run that spans midnight cannot
/// be converted into the window the cap is actually enforced on. Making the remaining quota
/// visible to a run is right - that is what <c>list_applyable</c> and <c>record_event</c> return
/// - and making the run hold it is not.
///
/// <b>Nothing here closes a run but the client.</b> There is no sweeper and no timer: an open run
/// past <see cref="ApplyRun.AbandonedAfter"/> is <i>read</i> as abandoned rather than written
/// closed, because a job that closed old runs would race a real <c>finish_run</c> and, between
/// the two, the row would assert a finish nothing observed. See <see cref="ListOpenAsync"/>.
/// </remarks>
public sealed class RunRepository(JobsDbContext db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Stands in for a breakdown a stored document did not carry.</summary>
    private static readonly IReadOnlyDictionary<string, int> Empty =
        new Dictionary<string, int>();

    /// <summary>
    /// Opens a run for this candidate.
    /// </summary>
    /// <remarks>
    /// <b>A second run does not close the first, and nothing here stops one being started.</b>
    /// Two open runs for one candidate is a client that crashed and restarted - a fact worth
    /// being able to see rather than a conflict to resolve - and every submission names the run
    /// it belongs to, so the arithmetic afterwards is right either way. A repository that closed
    /// the previous run here would be the sweeper this design refuses, wearing a different hat:
    /// it would stamp a finish the client never reported.
    ///
    /// No note is taken at the start. <see cref="RunEntity.Note"/> is the client's sentence about
    /// the pass as a whole, which is a thing that can only be said afterwards; two writers on one
    /// column would mean a finish either overwriting an intention or having to merge with it.
    /// </remarks>
    /// <param name="profileId">The candidate the pass is for, already resolved by the caller.</param>
    /// <param name="now">The clock, passed in so the ordering is assertable.</param>
    public async Task<ApplyRun> StartAsync(
        long profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        var entity = new RunEntity
        {
            ProfileId = profileId,
            StartedAtUtc = now,
        };

        db.Runs.Add(entity);
        await db.SaveChangesAsync(ct);

        return Map(entity);
    }

    /// <summary>
    /// Closes a run and stores what it says it did.
    /// </summary>
    /// <remarks>
    /// <b>Only the four counts are stored; <see cref="RunSummary.Parked"/> and
    /// <see cref="RunSummary.Unaccounted"/> are derived on the way back out.</b> That is
    /// <c>ApplyRun</c>'s own rule and the reason it gives is the reason to keep it here: a stored
    /// total is a second copy of a fact the breakdown already carries and is free to disagree
    /// with it. <see cref="RunSummary.Unaccounted"/> in particular is the number that catches a
    /// client whose tallies do not add up, and a column would let the client write a tidy zero
    /// into it.
    ///
    /// <b>A null <paramref name="summary"/> is stored as null and means the run reported
    /// nothing.</b> It is not the same as <see cref="RunSummary.Empty"/>, which means the run
    /// looked and found nothing: the first is a client to go and restart, the second is a queue
    /// to go and fill, and folding them together is the mistake this column is nullable to
    /// prevent.
    ///
    /// <b>A run read as abandoned can still be finished.</b> Abandonment is a reading taken
    /// against a clock, not a state written into the row, so a client that comes back after
    /// thirteen hours is still the only thing entitled to say what it did - and its account is
    /// worth more late than never.
    /// </remarks>
    /// <param name="profileId">The candidate whose run it is, already resolved by the caller.</param>
    /// <param name="runId">The run to close.</param>
    /// <param name="summary">What the run says it did, or null where it will not say.</param>
    /// <param name="note">A sentence about the pass as a whole. Never a log; bounded like every other note.</param>
    /// <param name="now">The clock, passed in so the ordering is assertable.</param>
    public async Task<(ApplyRun? Run, RunFinishResult Outcome)> FinishAsync(
        long profileId,
        long runId,
        RunSummary? summary,
        string? note,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // AsTracking, explicitly, because this row is about to be mutated. It reads as
        // redundant against EF's default and is not: the API host set NoTracking globally on
        // the argument that it never wrote to SQL, and under that a read-then-mutate saves
        // nothing and throws nothing. The default has been corrected, and stating it here
        // means this write no longer depends on which host it runs in.
        var entity = await db.Runs
            .AsTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.ProfileId == profileId, ct);

        if (entity is null)
        {
            return (null, RunFinishResult.NotFound);
        }

        if (entity.FinishedAtUtc is not null)
        {
            // Handed back rather than refused blind, so a client that lost its answer can read
            // the account that stands instead of guessing at what it already reported.
            return (Map(entity), RunFinishResult.AlreadyFinished);
        }

        entity.FinishedAtUtc = now;
        entity.SummaryJson = Serialise(summary);
        entity.Note = Bound(note, SubmissionLimits.MaxNoteLength);

        await db.SaveChangesAsync(ct);

        return (Map(entity), RunFinishResult.Finished);
    }

    /// <summary>One of the caller's runs, or null where they do not own it.</summary>
    /// <remarks>
    /// A caller cannot tell "no such run" from "not yours", which is the rule the whole of this
    /// side of the system follows - see <c>SubmissionRepository.GetAsync</c>.
    /// </remarks>
    public async Task<ApplyRun?> GetAsync(long profileId, long runId, CancellationToken ct = default)
    {
        var entity = await db.Runs
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.ProfileId == profileId, ct);

        return entity is null ? null : Map(entity);
    }

    /// <summary>This candidate's runs, newest first.</summary>
    /// <remarks>
    /// Ordered on the column the index is built on, unlike <c>SubmissionRepository.ListAsync</c>,
    /// which sorts in memory because its key is folded rather than stored. Nothing about a run is
    /// derived from its rows, so there is nothing here to fold.
    /// </remarks>
    /// <param name="limit">A bound on the page. A candidate accumulates a run a night.</param>
    public async Task<IReadOnlyList<ApplyRun>> ListAsync(
        long profileId, int limit, CancellationToken ct = default)
    {
        var rows = await db.Runs
            .AsNoTracking()
            .Where(r => r.ProfileId == profileId)
            .OrderByDescending(r => r.StartedAtUtc)
            // Two runs started in the same second by a client that restarted immediately must
            // not shuffle between identical requests.
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);

        return [.. rows.Select(Map)];
    }

    /// <summary>
    /// The runs the client never finished, newest first.
    /// </summary>
    /// <remarks>
    /// <b>Abandonment is not filtered here, and that is the point.</b>
    /// <see cref="ApplyRun.IsAbandoned"/> takes the clock as a parameter precisely so the
    /// boundary is assertable, and a repository that applied it would be a second answer to a
    /// question <c>ApplyRun</c> already answers - the same duplication a stored status column
    /// would be. This returns what is open; the caller asks how long it has been.
    ///
    /// Two are ordinary. A client that crashed and restarted leaves the first run open forever,
    /// and the work it did is still attributed to it through the submissions carrying its id -
    /// which is what makes an abandoned run cost observability rather than data. The one number
    /// that is lost is <see cref="RunSummary.Considered"/>, because a posting looked at and
    /// passed over leaves no row anywhere.
    /// </remarks>
    public async Task<IReadOnlyList<ApplyRun>> ListOpenAsync(
        long profileId, CancellationToken ct = default)
    {
        var rows = await db.Runs
            .AsNoTracking()
            .Where(r => r.ProfileId == profileId && r.FinishedAtUtc == null)
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);

        return [.. rows.Select(Map)];
    }

    /// <summary>
    /// How many submissions actually carry this run's id.
    /// </summary>
    /// <remarks>
    /// <b>The one number in a run's account that can be checked, and this is what checks it.</b>
    /// <see cref="RunSummary.Submitted"/> is the client's claim; these rows are the record. They
    /// are allowed to disagree - a submission can be created outside a run, and an abandoned run
    /// claims nothing while having done plenty - so this is offered for comparison rather than
    /// used to correct the summary. Correcting it would erase the disagreement, which is the
    /// interesting part.
    ///
    /// <see cref="RunSummary.Considered"/> has no equivalent and cannot have one: a posting the
    /// client read and passed over leaves no row anywhere, which is exactly why it is worth
    /// recording and exactly why it cannot be audited.
    /// </remarks>
    public Task<int> CountSubmissionsAsync(long profileId, long runId, CancellationToken ct = default)
        => db.Submissions
            .AsNoTracking()
            .CountAsync(s => s.ProfileId == profileId && s.RunId == runId, ct);

    private static ApplyRun Map(RunEntity entity)
        => new(
            entity.Id,
            entity.StartedAtUtc,
            entity.FinishedAtUtc,
            Deserialise(entity.SummaryJson),
            entity.Note);

    /// <summary>
    /// The stored shape of a summary: the four counts the client reported and nothing derived.
    /// </summary>
    /// <remarks>
    /// <b>The reasons are written as their names rather than as their numbers.</b> A column
    /// nobody can read without the enum in front of them is one that gets misread during an
    /// incident, and the numbers are the thing most likely to be renumbered later - which would
    /// silently re-file every historical park under a different reason.
    ///
    /// A reason tallied at zero is dropped on the way in, because
    /// <see cref="RunSummary.Equals"/> treats an absent reason and a zero as the same fact. That
    /// keeps a round trip equal to what was handed in, which is what lets the tests assert on the
    /// summary rather than on its serialisation.
    /// </remarks>
    /// <param name="ParkedByReason">
    /// Nullable because a stored document is read back rather than constructed: a row written
    /// before this field existed, or edited by hand, deserialises with nothing here, and a
    /// non-nullable property would make that a null reference at the first read rather than an
    /// empty breakdown.
    /// </param>
    private sealed record SummaryDocument(
        int Considered,
        int Submitted,
        int Questions,
        IReadOnlyDictionary<string, int>? ParkedByReason);

    private static string? Serialise(RunSummary? summary)
    {
        if (summary is null)
        {
            return null;
        }

        var parked = summary.ParkedByReason
            .Where(entry => entry.Value != 0)
            .ToDictionary(entry => entry.Key.ToString(), entry => entry.Value);

        return JsonSerializer.Serialize(
            new SummaryDocument(summary.Considered, summary.Submitted, summary.Questions, parked),
            Json);
    }

    /// <summary>
    /// Reads a summary back, treating an unreadable one as no summary at all.
    /// </summary>
    /// <remarks>
    /// <b>A reason this build does not recognise is dropped rather than failing the row</b>, the
    /// lenient direction <see cref="ParkReasonPolicy.Requeue"/>'s discard arm argues for: a
    /// stored value outlives the member that wrote it, and losing one line of a breakdown is a
    /// smaller loss than a run whose whole account cannot be read.
    ///
    /// A column that will not parse at all folds into null - "this run never reported" - which is
    /// the safer of the two answers it could be confused with. It sends somebody to look at the
    /// run, where <see cref="RunSummary.Empty"/> would assert that the run looked and found
    /// nothing. This repository is the only writer, so the case arises from a hand-edited row or
    /// a schema change rather than from the loop.
    /// </remarks>
    private static RunSummary? Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        SummaryDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<SummaryDocument>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null)
        {
            return null;
        }

        var parked = new Dictionary<ParkReason, int>();

        foreach (var entry in document.ParkedByReason ?? Empty)
        {
            // IsDefined as well as TryParse: TryParse happily turns "42" into a ParkReason that
            // no member names, and a breakdown keyed on one of those cannot be shown to anybody.
            if (Enum.TryParse<ParkReason>(entry.Key, out var reason) && Enum.IsDefined(reason))
            {
                parked[reason] = entry.Value;
            }
        }

        return new RunSummary(document.Considered, document.Submitted, document.Questions, parked);
    }

    /// <summary>
    /// Trims to the column's width rather than letting the database do it.
    /// </summary>
    /// <remarks>
    /// The same guard <c>SubmissionRepository</c> carries: a silent truncation on the way in is
    /// the shape of bug this codebase has paid for before, and the bound comes from
    /// <see cref="SubmissionLimits"/> so the schema and the validation cannot drift.
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
