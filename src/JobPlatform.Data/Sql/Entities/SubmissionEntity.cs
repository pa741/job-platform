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

    /// <summary>
    /// Why no application was made, where none was. Null means this row is a real submission.
    /// </summary>
    /// <remarks>
    /// <b>Parking is an attribute here and never a member of <c>SubmissionEventType</c>.</b> That
    /// is the decision most likely to be re-litigated, because adding a <c>Blocked</c> event
    /// looks like the smaller change - and it is mechanically impossible rather than merely
    /// distasteful. <c>SubmissionState.Fold</c> has two rules a new member must survive: a
    /// terminal event wins outright, and otherwise the furthest-advanced phase wins. Numbered
    /// above <c>OfferReceived</c>, a park is what <c>max</c> picks and the next run reports an
    /// offer as blocked; made terminal, one park closes an application that was actually sent and
    /// <c>IsClosed</c> then makes the row permanently un-stale, so the one most needing chasing
    /// stops being flagged. The ladder is a total order over how far an application got, and
    /// parking is not a point on it - it says no attempt was made at all.
    ///
    /// <b>And parking is reversible, where an event is not.</b> <see cref="UnparkedAtUtc"/> lets
    /// a posting back into the queue; the log has no eraser and the fold cannot un-see an event,
    /// so undoing a park would need "the most recent event wins" for one member - precisely the
    /// rule the fold was written to refuse.
    ///
    /// <b>A parked row is not a sent one</b>, and every reader that counts submissions has to be
    /// taught that. The dashboard counts any row with a non-null phase, and a parked row must not
    /// land in that total.
    /// </remarks>
    public ParkReason? ParkedReason { get; set; }

    /// <summary>When it was parked. Set with <see cref="ParkedReason"/> and cleared by neither.</summary>
    public DateTimeOffset? ParkedAtUtc { get; set; }

    /// <summary>
    /// When it was let back into the queue. Null while the park stands.
    /// </summary>
    /// <remarks>
    /// <b>A second timestamp rather than clearing the two above</b>, for the reason nothing on
    /// this table is ever cleared: "was never parked" and "was parked for a captcha in March and
    /// applied to in April" are different histories, and a row that erases the park to express
    /// the second is indistinguishable from the first. The queue predicate reads the pair - a
    /// submission is live if it was never parked <i>or</i> has been unparked - so the reversal is
    /// an append in substance even though it is one column.
    /// </remarks>
    public DateTimeOffset? UnparkedAtUtc { get; set; }

    /// <summary>When a person agreed this should be sent. Null means nobody has been asked.</summary>
    /// <remarks>
    /// Nullable and dated rather than a boolean, the way <c>JobMatches.AssessedAtUtc</c> and
    /// <c>DismissedAtUtc</c> already are: the null is the flag, and the date is what a reader
    /// needs when an approval and a submission turn out to be a fortnight apart.
    /// </remarks>
    public DateTimeOffset? ApprovedAtUtc { get; set; }

    /// <summary>
    /// Who approved it - a subject id, as the token carried it.
    /// </summary>
    /// <remarks>
    /// A subject id and not a display name, because an approval is an authorisation record and a
    /// name is not one: two people can share a name and neither can be resolved back to a
    /// principal afterwards. It is sized to match <c>CandidateProfiles.SubjectId</c> exactly - a
    /// narrower column would truncate the id it is a copy of, and an id truncated in an audit
    /// trail names somebody else.
    /// </remarks>
    public string? ApprovedBy { get; set; }

    /// <summary>
    /// Which revision of the generated documents was sent, where any were.
    /// </summary>
    /// <remarks>
    /// <b>Not a foreign key, deliberately.</b> The pair it belongs to is already on this row, so
    /// this names <c>ApplicationDocuments.Revision</c> for the same
    /// <c>(ProfileId, PostingId)</c> - a composite key spelled out again here would be three
    /// columns saying what two already say, free to disagree with them.
    ///
    /// It is here because documents are written per generation rather than updated in place, so
    /// "the CV they sent" is a revision number and nothing else can recover it: regenerating
    /// after an application produces a better draft that was never the one an employer read.
    /// </remarks>
    public int? DocumentRevision { get; set; }

    /// <summary>
    /// The unattended pass that created this, where one did. Null for a submission a person made.
    /// </summary>
    /// <remarks>
    /// <b>The one number in a run's summary that can be checked.</b> A run's own account of
    /// itself is the client's; the rows carrying its id are the record, so <c>Submitted</c> is
    /// countable against them and <c>Considered</c> is not. It is also what makes an abandoned
    /// run cost observability rather than data - the work is attributed whether or not the run
    /// ever spoke again.
    /// </remarks>
    public long? RunId { get; set; }

    public CandidateProfileEntity? Profile { get; set; }

    public JobPostingEntity? Posting { get; set; }

    public RunEntity? Run { get; set; }

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

    /// <summary>The reference the employer's system showed - "Application #4417290".</summary>
    /// <remarks>
    /// The one value in the evidence block that means something outside this database: it is what
    /// the employer's own record is keyed on, and what a person quotes when they chase. Free text
    /// because every ATS invents its own shape, and worth keeping even when it is the only thing
    /// captured.
    /// </remarks>
    public string? ConfirmationRef { get; set; }

    /// <summary>
    /// Where the browser ended up when the claim was made.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>Submissions.ApplyUrl</c>.</b> That is where the attempt started, copied in at
    /// creation so a later re-scrape cannot rewrite history; this is where it finished. The two
    /// differ on every ATS worth the name, and where a run went wrong this is the page it went
    /// wrong on - the first thing anybody reading the log afterwards wants.
    /// </remarks>
    public string? FinalUrl { get; set; }

    /// <summary>A pointer to a stored screenshot. Never the image, and never a signed URL.</summary>
    /// <remarks>
    /// A stored path rather than a link: a user-delegation SAS expires, and an expired URL in an
    /// append-only log is a dead pointer that still looks like evidence - a reader cannot tell
    /// "the screenshot is gone" from "the link aged out". A path stays resolvable for as long as
    /// the blob does, and whoever looks mints a fresh URL then.
    /// </remarks>
    public string? ScreenshotRef { get; set; }

    /// <summary>
    /// The names of the fields that were filled in, as a JSON array. <b>Names, never values.</b>
    /// </summary>
    /// <remarks>
    /// <b>A JSON column rather than a child table, following <c>EmphasisedJson</c>.</b> Nothing
    /// queries inside it - it is read back whole to be shown beside the event it belongs to -
    /// which is the condition that makes a JSON column the right call here rather than the lazy
    /// one. A child table would buy a join on every event read and the ability to ask a question
    /// nothing asks.
    ///
    /// <b>Unbounded, and bounded where it is built instead.</b>
    /// <c>SubmissionLimits.MaxSubmittedFieldNameLength</c> and <c>MaxSubmittedFieldCount</c> bound
    /// the list, and a column width would have to guess at how much JSON escaping expands it -
    /// a guess that fails as an insert error on a name full of quotes rather than as anything a
    /// reader could have predicted.
    ///
    /// <b>Names and never the answers given to them</b>, which is the line <c>DisclosureRecord</c>
    /// already draws on the read side: enough to see that an agent answered a right-to-work
    /// question nobody authorised it to answer, and not enough to be a second copy of the answer.
    /// </remarks>
    public string? SubmittedFieldsJson { get; set; }

    public SubmissionEntity? Submission { get; set; }
}
