namespace JobPlatform.Core.Submissions;

/// <summary>Where the application is made.</summary>
/// <remarks>
/// <b>Read from the posting's own <c>OffsiteApply</c> flag first, and from the presence of a
/// direct apply URL only as a fallback.</b> That ordering is the fix for a real failure. The
/// original design had only the URL and read its absence on a board posting as "the board hosts
/// it" - Easy Apply. Measured on 2026-09-01 that was wrong for the entire corpus: all 4,470
/// LinkedIn postings of the previous week carried no direct link, and the job detail page had
/// been fetched for 98.4% of them, so the scraper had looked and found nothing. LinkedIn had
/// stopped publishing apply URLs to signed-out clients altogether - the guest page now carries no
/// non-LinkedIn URL anywhere on it.
///
/// The scraper reads LinkedIn's own offsite markers instead and emits <c>offsite_apply</c>, so
/// the route is knowable even where the destination is not. <b>An absence of both now means
/// nothing was established</b>, which is <see cref="Unknown"/>, and <see cref="Board"/> is
/// asserted only where something actually said so.
/// </remarks>
public enum SubmissionChannel
{
    /// <summary>
    /// Nothing established where the application is made.
    /// </summary>
    /// <remarks>
    /// Zero, so an unset value reads as "not known" - the right default for a fact nothing has
    /// determined. The apply URL is still returned: it is the board's own posting page, which is
    /// where a person goes to find out.
    /// </remarks>
    Unknown = 0,

    /// <summary>
    /// The employer's own system. A direct apply URL, or the board saying the apply is offsite.
    /// </summary>
    /// <remarks>
    /// Two separate facts, either of which is enough. On LinkedIn today the first is unavailable
    /// and the second is not, so a posting can be known to apply on the employer's own system
    /// without this repository knowing the address - which is worth saying, because it is what
    /// a candidate needs in order to go and look.
    /// </remarks>
    Ats = 1,

    /// <summary>The board hosts the application - Easy Apply, or Indeed Apply.</summary>
    Board = 2,
}

/// <summary>
/// The phase an application has reached. <b>Not a label for what happened.</b>
/// </summary>
/// <remarks>
/// <b>This enum must not grow every time a company invents a round.</b> "Tech round 2",
/// "culture fit", "final panel" and "take-home" are all <see cref="SubmissionEventType.InterviewScheduled"/>
/// with different <c>Stage</c> text on the event. The enum is what the dashboard groups by and
/// what <see cref="SubmissionState.Fold"/> switches on; the free text is what a person reads.
/// Collapsing the two would make every hiring process somebody invents a schema change.
///
/// The numbering is the ordering, and it is load-bearing: the non-terminal members ascend in the
/// order an application actually moves through them, so "furthest advanced" is a comparison
/// rather than a table. The two terminal members sit above them but are chosen by
/// <see cref="SubmissionEventTypes.IsTerminal"/> rather than by being larger, because which of
/// two terminal events wins is a question about time, not about rank.
/// </remarks>
public enum SubmissionEventType
{
    /// <summary>The application was sent. Recorded, never performed - see <c>mcp_handoff.md</c> section 3.</summary>
    Submitted = 1,

    /// <summary>Somebody or something confirmed receipt.</summary>
    Acknowledged = 2,

    /// <summary>A recruiter screen is booked.</summary>
    ScreeningScheduled = 3,

    /// <summary>An interview is booked. The round belongs in <c>Stage</c>, not here.</summary>
    InterviewScheduled = 4,

    /// <summary>An offer arrived.</summary>
    OfferReceived = 5,

    /// <summary>The employer closed it.</summary>
    Rejected = 6,

    /// <summary>The candidate closed it.</summary>
    Withdrawn = 7,
}

/// <summary>Who asserted an event.</summary>
/// <remarks>
/// <b>A pipeline that cannot distinguish what a person asserted from what a model inferred from
/// an inbox cannot be audited after it gets one wrong.</b> These events will be written by a
/// client reading recruiter email and deciding what a message means, and that client will
/// sometimes be wrong. Recording where an assertion came from is what makes the wrong ones
/// findable later, and it is the same lesson the AI ledger records on the other side of this
/// system: store the outcome and its reason, not just the current value.
/// </remarks>
public enum SubmissionEventSource
{
    /// <summary>The candidate said so, in the dashboard.</summary>
    Candidate = 1,

    /// <summary>An MCP client asserted it directly.</summary>
    Client = 2,

    /// <summary>Inferred from a message in an inbox.</summary>
    Email = 3,
}

/// <summary>Bounds shared by the schema, the API contract and the tools.</summary>
/// <remarks>
/// One place, so the column width and the validation cannot disagree. A note longer than its
/// column truncates on the way in, and a truncation nobody sees is the shape of bug this
/// codebase has already paid for twice.
/// </remarks>
public static class SubmissionLimits
{
    /// <summary>A label inside a phase - "Tech round 2".</summary>
    public const int MaxStageLength = 120;

    /// <summary>A sentence or two of context. Never a message body.</summary>
    public const int MaxNoteLength = 1000;

    /// <summary>What the caller sends to make a write converge rather than duplicate.</summary>
    public const int MaxIdempotencyKeyLength = 100;

    /// <summary>
    /// How many applications may be recorded as <i>sent</i> in one UTC day.
    /// </summary>
    /// <remarks>
    /// <b>A bound on the blast radius of a client that loops.</b> The server never submits
    /// anything, so the damage is a pipeline full of applications nobody made - but the whole
    /// point of the pipeline is that a person can trust it, and four hundred phantom rows
    /// destroys that as surely as four hundred real emails would.
    ///
    /// <b>Enforced in <c>SubmissionRepository</c> and nowhere else</b>, for the reason
    /// <c>AiCallRecord.Create</c> is the only constructor: a rule enforced at the call sites
    /// survives exactly until somebody adds another one, and there are already two.
    ///
    /// It bounds <c>Submitted</c> alone. Recording that a hundred applications exist is fine -
    /// somebody may be importing a history - and claiming a hundred were sent today is not.
    /// Set well above what a person does in a day and well below what a loop does in a minute.
    /// </remarks>
    public const int MaxSubmittedPerDay = 25;

    /// <summary>The apply URL as it was at the time, so a later edit to the posting does not rewrite history.</summary>
    public const int MaxApplyUrlLength = 1000;
}

/// <summary>What happened to an attempt to append an event.</summary>
/// <remarks>
/// Four states rather than a bool, because a caller acts differently on each and three of them
/// are ordinary rather than exceptional. A retry that finds its event already recorded has
/// succeeded; a client that has hit the daily cap should stop rather than retry.
/// </remarks>
public enum SubmissionEventResult
{
    /// <summary>Appended.</summary>
    Recorded = 0,

    /// <summary>That idempotency key is already on this submission. The retry converged.</summary>
    AlreadyRecorded = 1,

    /// <summary>No such submission for this candidate. Indistinguishable from "not yours".</summary>
    NotFound = 2,

    /// <summary>
    /// The candidate has already recorded <see cref="SubmissionLimits.MaxSubmittedPerDay"/>
    /// applications as sent today.
    /// </summary>
    DailyLimitReached = 3,
}

/// <summary>
/// One thing that happened to an application.
/// </summary>
/// <remarks>
/// <b>The event log is the record and the status is a fold over it.</b> Not a mutable status
/// column: a stored status tells you it is wrong now, where an event log tells you what was
/// seen, when, and from where. That distinction is the whole reason this shape was chosen, and
/// it is what makes <see cref="SubmissionState"/> able to derive staleness instead of storing it.
/// </remarks>
/// <param name="AtUtc">
/// When it happened, not when it was recorded. An inbox reader catches up, so these arrive late
/// and out of order and the fold has to tolerate both.
/// </param>
/// <param name="Type">The phase this event moves the application into.</param>
/// <param name="Stage">The label inside that phase, where there is one. Free text by design.</param>
/// <param name="Source">Who asserted it.</param>
/// <param name="Note">Context for a person. Bounded, and never a message body.</param>
public sealed record SubmissionEvent(
    DateTimeOffset AtUtc,
    SubmissionEventType Type,
    string? Stage,
    SubmissionEventSource Source,
    string? Note);

/// <summary>Which phases end an application.</summary>
public static class SubmissionEventTypes
{
    /// <summary>
    /// Whether this phase closes the application.
    /// </summary>
    /// <remarks>
    /// Asked as a question rather than inferred from the enum's value. A terminal event beats a
    /// later non-terminal one, which is a different rule from "the largest wins", and writing it
    /// as a comparison would tie the rule to the numbering and break silently the first time a
    /// member is inserted.
    /// </remarks>
    public static bool IsTerminal(this SubmissionEventType type)
        => type is SubmissionEventType.Rejected or SubmissionEventType.Withdrawn;
}
