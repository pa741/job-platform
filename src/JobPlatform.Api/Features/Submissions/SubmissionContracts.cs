using System.ComponentModel.DataAnnotations;
using JobPlatform.Core.Submissions;

namespace JobPlatform.Api.Features.Submissions;

/// <summary>
/// One application this candidate sent, with its status folded from the event log.
/// </summary>
/// <remarks>
/// <b>Enums cross the wire as strings</b>, following <c>MatchSummary.Verdict</c> and
/// <c>PostingSummary.WorkArrangement</c>. Numbers would make a renumbering of
/// <see cref="SubmissionEventType"/> a silent change of meaning for every stored client, and
/// this surface is read by an agent as well as by a browser.
///
/// No description and no advert body. This is a list response and the same rule
/// <c>PostingSummary</c> follows applies.
/// </remarks>
public sealed record SubmissionResponse
{
    public required long Id { get; init; }
    public required long PostingId { get; init; }
    public required string PostingTitle { get; init; }
    public string? Company { get; init; }

    /// <summary><c>Ats</c>, <c>Board</c> or <c>Unknown</c>.</summary>
    public required string Channel { get; init; }

    /// <summary>Where the application went, as it stood when this was recorded.</summary>
    public string? ApplyUrl { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// The furthest phase reached, or <b>null where nothing has happened yet</b>.
    /// </summary>
    /// <remarks>
    /// Null rather than a <c>Created</c> name, exactly as <c>MatchSummary.Verdict</c> is null
    /// rather than <c>Unknown</c>: "not started" and "started and we cannot say" are different
    /// facts and a default enum name collapses them.
    /// </remarks>
    public string? Phase { get; init; }

    /// <summary>The label inside the phase - "Tech round 2". Free text, never an enum member.</summary>
    public string? Stage { get; init; }

    public required DateTimeOffset LastActivityUtc { get; init; }

    /// <summary>Nothing for a fortnight. <b>Derived on read, never stored.</b></summary>
    public required bool IsStale { get; init; }

    /// <summary>Rejected or withdrawn.</summary>
    public required bool IsClosed { get; init; }

    public required int EventCount { get; init; }
}

/// <summary>One thing that happened, as the log returns it.</summary>
public sealed record SubmissionEventResponse
{
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>The phase this event moved the application into.</summary>
    public required string Type { get; init; }

    public string? Stage { get; init; }

    /// <summary><c>Candidate</c>, <c>Client</c> or <c>Email</c>. Who asserted it.</summary>
    public required string Source { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// What was captured while this was claimed, where anything was.
    /// </summary>
    /// <remarks>
    /// <b>Stored since the write path was built and read back by nothing until now.</b> A run
    /// records a confirmation reference, where the browser ended up, a screenshot path and the
    /// names of the fields it filled - and every one of those was invisible outside the database,
    /// which makes an audit trail that cannot be audited. Null where nothing was captured, never
    /// an empty block: an application with no evidence is still an application, and a panel of
    /// blanks reads as proof that does not exist.
    /// </remarks>
    public SubmissionEvidenceResponse? Evidence { get; init; }
}

/// <summary>What a browser captured while claiming an application was made.</summary>
/// <remarks>
/// <b>Names, never values</b>, which is the line the write side draws and the reason this is safe
/// to project at all: enough to see that an agent answered a right-to-work question nobody
/// authorised it to answer, and not enough to be a second copy of the answer it gave.
/// </remarks>
public sealed record SubmissionEvidenceResponse
{
    /// <summary>The reference the employer's own system showed.</summary>
    public string? ConfirmationRef { get; init; }

    /// <summary>Where the browser ended up. Not the apply URL, which is where it started.</summary>
    public string? FinalUrl { get; init; }

    /// <summary>A stored path, never a link: a signed URL in a log is a dead pointer that still looks live.</summary>
    public string? ScreenshotRef { get; init; }

    /// <summary>The names of the fields that were filled in.</summary>
    public IReadOnlyList<string>? SubmittedFields { get; init; }

    /// <summary>
    /// Which of those were answered with prose this system wrote rather than the candidate.
    /// </summary>
    /// <remarks>
    /// The question an audit asks first, and the reason the pair is worth showing a person: an
    /// application carries what they typed, what the writing pass drafted from the advert, and
    /// what the allowlist read off their profile, and only the first is something they said.
    /// </remarks>
    public IReadOnlyList<string>? DraftedFields { get; init; }
}

/// <summary>
/// Records that an application was sent.
/// </summary>
/// <remarks>
/// <b>No idempotency key, because the schema already provides one.</b> A submission is unique on
/// <c>(ProfileId, PostingId)</c>, so a retried create converges on the row it already made and
/// answers 200 rather than 201. Events need an explicit key because there is no natural one.
/// </remarks>
/// <param name="PostingId">Must already be matched against this profile.</param>
/// <param name="Channel">
/// <c>Ats</c>, <c>Board</c> or <c>Unknown</c>. Omit to take it from the posting's apply link,
/// which yields <c>Ats</c> or <c>Unknown</c> and never <c>Board</c> - see
/// <see cref="SubmissionChannel"/> for why that inference was withdrawn. <b>Supply <c>Board</c>
/// yourself</b> when you applied through the board, which only the person who did it knows.
/// </param>
/// <param name="ApplyUrl">Where it actually went. Omit to record the posting's own apply link.</param>
public sealed record CreateSubmissionRequest(
    long PostingId,
    string? Channel,
    [property: MaxLength(SubmissionLimits.MaxApplyUrlLength)] string? ApplyUrl);

/// <summary>
/// Appends one event to a submission's log.
/// </summary>
/// <param name="Type">
/// One of <c>Submitted</c>, <c>Acknowledged</c>, <c>ScreeningScheduled</c>,
/// <c>InterviewScheduled</c>, <c>OfferReceived</c>, <c>Rejected</c>, <c>Withdrawn</c>.
/// </param>
/// <param name="IdempotencyKey">
/// <b>Required, and the caller chooses it.</b> Unique per submission: a client that retries -
/// or a person who double-clicks - must not be able to record the same thing twice, and the
/// server cannot tell a retry from a genuine second event without being told.
/// </param>
/// <param name="AtUtc">When it happened. Defaults to now, which is right only when it just did.</param>
/// <param name="Stage">"Tech round 2". Free text by design.</param>
/// <param name="Source">
/// <c>Candidate</c>, <c>Client</c> or <c>Email</c>. Defaults to <c>Candidate</c>, because the
/// dashboard is the only caller today and a person typed it.
/// </param>
/// <param name="Note">Context for a person. Never a message body.</param>
public sealed record RecordSubmissionEventRequest(
    string Type,
    [property: MaxLength(SubmissionLimits.MaxIdempotencyKeyLength)] string IdempotencyKey,
    DateTimeOffset? AtUtc,
    [property: MaxLength(SubmissionLimits.MaxStageLength)] string? Stage,
    string? Source,
    [property: MaxLength(SubmissionLimits.MaxNoteLength)] string? Note);
