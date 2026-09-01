using JobPlatform.Core.Submissions;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// An application this candidate sent, and the log of what happened to it.
/// </summary>
/// <remarks>
/// <b>A submission, not an application.</b> <c>ApplicationDocuments</c> in this codebase already
/// means generated drafts, and <c>Candidacy</c> is taken by <c>CandidacyAssessment</c> and
/// <c>ICandidacyAssessor</c>. Reusing either would put two meanings on one word in a system
/// whose matching code reads both.
///
/// <b>The row records that something was sent; nothing in this repository sends it.</b> Applying
/// is irreversible and outward-facing, so it stays outside - which means no bug here can reach
/// an employer.
///
/// <b>There is no status column, deliberately.</b> The events are the record and the status is
/// <c>SubmissionState.Fold</c> over them. A stored status tells you it is wrong now; an event log
/// tells you what was seen, when, and from where - which matters because these events will be
/// written by a client reading an inbox and deciding what a message means, and that client will
/// sometimes be wrong. It is the same lesson the AI ledger records on the other side of this
/// system.
/// </remarks>
public sealed class SubmissionEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    public long PostingId { get; set; }

    /// <summary>Whether the employer's own system or the board took the application.</summary>
    public SubmissionChannel Channel { get; set; }

    /// <summary>
    /// The apply URL as it stood when this was created.
    /// </summary>
    /// <remarks>
    /// Copied rather than joined back to the posting. A re-scrape may rewrite
    /// <c>JobUrlDirect</c>, and this column is a record of where the application actually went -
    /// history, not a current value. The same reason <c>ExtractionBatchItems.InputHash</c> is
    /// captured at submission and never recomputed.
    /// </remarks>
    public string? ApplyUrl { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public CandidateProfileEntity? Profile { get; set; }

    public JobPostingEntity? Posting { get; set; }

    public List<SubmissionEventEntity> Events { get; } = [];
}

/// <summary>
/// One thing that happened to a submission. Append-only.
/// </summary>
/// <remarks>
/// <b>No deletes anywhere on this table.</b> An append-only log with no eraser is the only
/// version worth auditing, and withdrawing an application is a <c>Withdrawn</c> event rather
/// than the removal of one.
/// </remarks>
public sealed class SubmissionEventEntity
{
    public long Id { get; set; }

    public long SubmissionId { get; set; }

    /// <summary>When it happened, not when it was written. These arrive late and out of order.</summary>
    public DateTimeOffset AtUtc { get; set; }

    public SubmissionEventType Type { get; set; }

    /// <summary>A label inside the phase - "Tech round 2". Never a member of the enum.</summary>
    public string? Stage { get; set; }

    public SubmissionEventSource Source { get; set; }

    /// <summary>Bounded context for a person. Never a message body.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// What makes a retried write converge instead of duplicating.
    /// </summary>
    /// <remarks>
    /// Unique per submission, enforced by the schema rather than by a check in the repository -
    /// the same contract <c>ScrapeRuns.BlobPath</c> and
    /// <c>PostingExtractions (PostingId, ExtractorVersion, InputHash)</c> carry. A client that
    /// retries must not be able to record a second <c>Submitted</c>, and the place that cannot
    /// be argued with is the index.
    /// </remarks>
    public required string IdempotencyKey { get; set; }

    public SubmissionEntity? Submission { get; set; }
}
