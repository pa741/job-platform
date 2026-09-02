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

    /// <summary>The reference an employer's system showed on its confirmation page.</summary>
    /// <remarks>
    /// Generous for what these actually are - "Application #4417290", a GUID, a hyphenated job
    /// code - because they are free text from somebody else's system and every ATS invents its
    /// own shape. What it excludes is a caller pasting the confirmation page in. Past this length
    /// the value is not a reference, and <b>a truncated reference is worse than no reference at
    /// all</b>: it still looks like one, so somebody quotes it at an employer who has never seen
    /// it.
    /// </remarks>
    public const int MaxConfirmationRefLength = 200;

    /// <summary>Where the browser ended up when the claim was made.</summary>
    /// <remarks>
    /// The same number as <see cref="MaxApplyUrlLength"/> and deliberately a separate constant.
    /// They are separate columns measured against the same ATS URLs - a confirmation page
    /// carrying the reference in its query string runs about as long as the apply URL that led to
    /// it - so the numbers agreeing today is a coincidence worth keeping rather than a fact to
    /// share. One constant behind both would mean widening either silently widens the other.
    /// </remarks>
    public const int MaxFinalUrlLength = 1000;

    /// <summary>A pointer to a stored screenshot.</summary>
    /// <remarks>
    /// <b>Set at the storage platform's own ceiling rather than at what a path is expected to
    /// be</b>, which inverts how every other bound here is chosen and is the point. The others
    /// bound free text, where truncating costs readability and a reader can see the sentence stop
    /// short. This bounds a pointer, where truncating costs the thing pointed at - a screenshot
    /// that exists, was paid for, and can never be found again, with nothing in the row admitting
    /// it. An Azure blob name is at most 1,024 characters, so a path too long for this column is
    /// a path the store would have refused anyway, and the column can never be what breaks a
    /// reference.
    /// </remarks>
    public const int MaxScreenshotRefLength = 1024;

    /// <summary>One field name in the evidence list. A name, never the answer given to it.</summary>
    public const int MaxSubmittedFieldNameLength = 100;

    /// <summary>
    /// How many field names one event's evidence may carry.
    /// </summary>
    /// <remarks>
    /// The only bound here on a <i>count</i> rather than a length, because
    /// <see cref="SubmissionEvidence.SubmittedFields"/> is the first thing on this table whose
    /// size the caller chooses. Every other value is one column wide whatever a client does with
    /// it; a list is as long as the loop that built it, which is the same argument that puts a
    /// cap on <see cref="MaxSubmittedPerDay"/>. A hundred sits well above the longest real
    /// application form and well below what a client enumerating every input on a page produces.
    /// </remarks>
    public const int MaxSubmittedFieldCount = 100;
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
/// What a browser managed to capture while making one claim about an application.
/// </summary>
/// <remarks>
/// <b>Every member is optional, and that is the design rather than laziness.</b> A page can
/// submit and then redirect somewhere carrying no reference; a confirmation screen can render
/// after the screenshot was taken; an ATS can show a reference and no distinct URL. Requiring any
/// one of these would mean an event that cannot be recorded because its proof is missing, and
/// <b>a submission with no evidence at all is still a submission</b> - which is why the whole
/// record is optional on the event as well.
///
/// <b>Named rather than positional.</b> Three of the four members are nullable strings, so a
/// positional record would take a transposed reference and URL without a word from the compiler,
/// and the mistake would be invisible afterwards because both values are plausible-looking text
/// nobody re-reads. Named members also keep the type projectable from EF: an object initialiser
/// is a member-init node the provider translates, where an omitted optional constructor argument
/// is CS0854 before it ever reaches a provider.
///
/// <b>References, never contents.</b> <see cref="ScreenshotRef"/> points at an image and is not
/// one; <see cref="SubmittedFields"/> holds field <i>names</i> and never the answers given to
/// them. That is the rule <c>DisclosureRecord</c> already runs under, restated on the write side:
/// a record holding the data it is evidence for has moved the problem rather than solved it - and
/// a screenshot of a completed application form is a picture of somebody's address, phone number
/// and employment history, sitting in the SQL database the dashboard reads.
/// </remarks>
public sealed record SubmissionEvidence
{
    /// <summary>The reference the employer's system showed - "Application #4417290".</summary>
    /// <remarks>
    /// The one value here that means something outside this database: it is what the employer's
    /// own record is keyed on, and what a person quotes when they chase. Free text because every
    /// ATS invents its own shape, and worth keeping even when it is the only thing captured.
    /// </remarks>
    public string? ConfirmationRef { get; init; }

    /// <summary>Where the browser ended up.</summary>
    /// <remarks>
    /// <b>Not <c>Submissions.ApplyUrl</c>.</b> That is where the attempt started, copied in at
    /// creation so a later edit to the posting cannot rewrite history; this is where it finished.
    /// The two differ on every ATS worth the name - a confirmation page, often with the reference
    /// in its query string - and where a run went wrong this is the page it went wrong on, which
    /// is the first thing anybody reading the log afterwards wants.
    /// </remarks>
    public string? FinalUrl { get; init; }

    /// <summary>A pointer to a stored screenshot. Never the image, and never a signed URL.</summary>
    /// <remarks>
    /// <b>A stored path, not a link.</b> A user-delegation SAS expires, and an expired URL in an
    /// append-only log is a dead pointer that still looks like evidence - a reader cannot tell
    /// "the screenshot is gone" from "the link aged out". A path stays resolvable for as long as
    /// the blob does, and whoever looks mints a fresh URL then.
    /// </remarks>
    public string? ScreenshotRef { get; init; }

    /// <summary>
    /// The names of the fields that were filled in. <b>Names, never values.</b>
    /// </summary>
    /// <remarks>
    /// This answers "what did it put on the form" at the only resolution that is safe to keep:
    /// enough to see that an agent answered a right-to-work question nobody authorised it to
    /// answer, and not enough to be a second copy of the answer it gave. The same line
    /// <c>DisclosureRecord.Detail</c> draws on the read side of the same exchange.
    ///
    /// Bounded by <see cref="SubmissionLimits.MaxSubmittedFieldCount"/> as well as by name
    /// length, because it is the first thing on this table whose size a caller chooses rather
    /// than the schema.
    /// </remarks>
    public IReadOnlyList<string>? SubmittedFields { get; init; }

    /// <summary>Whether anything was actually captured.</summary>
    /// <remarks>
    /// <b>Blank counts as nothing.</b> A selector that matched an empty element yields <c>""</c>
    /// rather than null, and a list of blanks is what enumerating a page that had not finished
    /// rendering produces - so a plain null check would put an evidence block on the dashboard
    /// with nothing in it, and a row on this table asserting proof that does not exist. Asked
    /// rather than inferred, so the write path can store nothing instead of a row of nulls and a
    /// reader can tell "captured nothing" from "captured this much".
    /// </remarks>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(ConfirmationRef)
            && string.IsNullOrWhiteSpace(FinalUrl)
            && string.IsNullOrWhiteSpace(ScreenshotRef)
            && SubmittedFields?.Any(name => !string.IsNullOrWhiteSpace(name)) != true;
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
    string? Note)
{
    /// <summary>What was captured while this claim was made, where anything was.</summary>
    /// <remarks>
    /// <b>On the event rather than on the submission, because proof attaches to a claim.</b> A
    /// submission is sent once and its row says so once; the log records several assertions about
    /// it, made at different moments by different things, and each has its own evidence or none.
    /// The confirmation reference belongs to the <see cref="SubmissionEventType.Submitted"/>
    /// event; whatever produced an <see cref="SubmissionEventType.InterviewScheduled"/> a
    /// fortnight later is different evidence for a different claim. Hung off <c>Submissions</c>
    /// there would be one slot for all of them, so the second capture overwrites the first -
    /// which is a mutable status column under another name, and the reason this shape has an
    /// event log at all. It is also what keeps the table append-only in substance rather than
    /// only in form: correcting a claim means appending an event carrying what was actually seen,
    /// never editing the evidence on an old one.
    ///
    /// <b>Optional, and a missing capture never blocks the event.</b> This is gathered by
    /// something driving a browser through somebody else's form, and the interesting runs are the
    /// ones that go wrong. Refusing to record that an application was sent because the screenshot
    /// failed would lose the fact in order to protect the proof of it.
    ///
    /// <b>An <c>init</c> property rather than a sixth positional parameter, and that is a
    /// compile-time fact rather than a preference.</b> A trailing optional parameter leaves every
    /// existing five-argument call compiling <i>except</i> the ones inside an expression tree,
    /// and <c>SubmissionRepository.ListEventsAsync</c> projects this type straight out of an EF
    /// query: an omitted optional argument there is CS0854, "an expression tree may not contain a
    /// call or invocation that uses optional arguments". An object initialiser is a member-init
    /// node, which the compiler and the query provider both translate. Equality and
    /// <c>ToString</c> cover the property either way, so nothing is given up for it.
    /// </remarks>
    public SubmissionEvidence? Evidence { get; init; }
}

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
