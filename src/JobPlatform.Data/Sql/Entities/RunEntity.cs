namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One unattended pass over the applyable queue.
/// </summary>
/// <remarks>
/// <b>A run buys per-run observability and nothing else, which is why the table is this small.</b>
/// It is not a quota - the daily cap on <c>Submitted</c> events lives in
/// <c>SubmissionRepository</c>, counts by the event's own <c>AtUtc</c> across every submission,
/// and would be weakened by a per-run counter that resets every time a crashed client restarts.
/// It is not idempotency either: <c>(SubmissionId, IdempotencyKey)</c> is unique by index
/// already, so a retry converges with or without a run.
///
/// What is genuinely new is <c>RunSummary.Considered</c>. Submissions record what was created and
/// are silent about what was looked at and passed over, so "the pass sent two applications last
/// night" has several causes - an empty queue, every posting stopping at a login wall, the day's
/// cap already spent - that produce identical data everywhere else. One row per pass tells them
/// apart.
///
/// <b>Not <c>ScrapeRuns</c>, and the two must not be read as neighbours.</b> That table is one
/// blob of scraped postings and belongs to ingestion; this is one candidate's apply pass and
/// belongs to submissions. Nothing joins them, and the only thing they share is the word.
///
/// <b>Nothing here closes a run but the client.</b> <see cref="FinishedAtUtc"/> is written by
/// <c>finish_run</c> and by nothing else - a sweeper that closed old runs would race a real
/// finish, and between the two the row would assert a finish nothing observed, which is the
/// argument against a status column restated. An open run is read as abandoned past
/// <c>ApplyRun.AbandonedAfter</c>; the work it did is still in the submissions carrying its id,
/// so abandonment costs observability rather than data.
/// </remarks>
public sealed class RunEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>When the client said it was done. Null means it never did - see the remarks.</summary>
    public DateTimeOffset? FinishedAtUtc { get; set; }

    /// <summary>
    /// What the run reported, as JSON: the four counts and the per-reason park breakdown.
    /// </summary>
    /// <remarks>
    /// <b>One nullable column rather than four int columns, because null and
    /// <c>RunSummary.Empty</c> are different answers and a reader must not fold them together.</b>
    /// "Looked and found nothing" is a queue to go and fill; "died before it could say" is a
    /// client to go and restart. Four integer columns cannot express the second without a fifth
    /// column saying whether the other four mean anything, and two spellings of one absence is
    /// the fault this schema has already paid for on <c>OffsiteApply</c>.
    ///
    /// Suffixed <c>Json</c> like every other JSON column here, though the design sheet writes it
    /// <c>Summary</c>: <c>JobPostings.Summary</c> is freehire's prose synopsis, and one word
    /// meaning prose on one table and a serialised record on another is the collision the naming
    /// rules in this repository exist to prevent.
    ///
    /// Unbounded, and bounded by construction rather than by width: the breakdown is keyed on
    /// <c>ParkReason</c>, so a client cannot invent keys and write an essay into it.
    /// </remarks>
    public string? SummaryJson { get; set; }

    /// <summary>A sentence from the client about the pass as a whole. Never a log.</summary>
    public string? Note { get; set; }

    public CandidateProfileEntity? Profile { get; set; }
}
