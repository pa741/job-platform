using System.ComponentModel.DataAnnotations;
using JobPlatform.Core.Submissions;

namespace JobPlatform.Api.Features.Questions;

/// <summary>
/// One question waiting on the candidate, with the advert that raised it.
/// </summary>
/// <remarks>
/// <b>One wording is one row however many adverts asked it</b>, so <see cref="PostingId"/> is
/// context rather than identity: it names the advert that hit the wording first, and the other
/// adverts record their waiting on their own parked submissions. A reader that treats it as the
/// key puts the same question to the same person four times, which is the loop
/// <c>OpenQuestions</c> exists to break.
///
/// <b>Enums cross the wire as strings and ids as numbers</b>, following <c>SubmissionResponse</c>.
/// No description and no advert body: this is a list response and the same rule
/// <c>PostingSummary</c> follows applies.
/// </remarks>
public sealed record OpenQuestionResponse
{
    public required long QuestionId { get; init; }

    /// <summary>The advert that raised it. Null for a question that came from nowhere in particular.</summary>
    public long? PostingId { get; init; }

    public string? PostingTitle { get; init; }

    public string? Company { get; init; }

    /// <summary>
    /// The employer's row, where the advert names one.
    /// </summary>
    /// <remarks>
    /// <b>Carried so the company scope can be offered at all.</b> A company-scoped answer is
    /// filed against <c>Companies.Id</c> rather than against the name printed on the advert,
    /// because that table already folds "Contoso" and "Contoso Ltd" into one employer and keying
    /// on the string would file the same answer twice. Null means the folding is unavailable
    /// here, and the choice is then between this advert and everywhere - which is better than
    /// offering an employer-wide answer with no employer to file it against, since that answer
    /// applies to everybody and is the failure scoping exists to prevent.
    /// </remarks>
    public int? CompanyId { get; init; }

    /// <summary>The unattended pass that raised it, so an abandoned run's questions stay attributable.</summary>
    public long? RunId { get; init; }

    /// <summary>The question as the form asked it, verbatim. What a person reads before answering.</summary>
    public required string QuestionText { get; init; }

    /// <summary>
    /// The choices the form offered, in the form's own words.
    /// </summary>
    /// <remarks>
    /// Empty covers both a free-text box and a set nobody recorded, deliberately: the column
    /// stores null for both, and a caller telling them apart would be acting on a distinction the
    /// form never established.
    /// </remarks>
    public required IReadOnlyList<string> Options { get; init; }

    /// <summary>
    /// Whether this is one only the candidate may state.
    /// </summary>
    /// <remarks>
    /// <b>It drives a confirmation on the dashboard and redaction in the disclosure log, never
    /// permission to infer.</b> What keeps a salary expectation or a right-to-work status safe is
    /// that nothing in the derived namespace can produce one - see <c>FormFieldCatalog</c> - so a
    /// row with this flag wrong logs badly rather than leaks.
    /// </remarks>
    public required bool Sensitive { get; init; }

    public required DateTimeOffset AskedAtUtc { get; init; }

    /// <summary>The application this question is holding back, where one is parked on it.</summary>
    public ParkedApplicationResponse? Parked { get; init; }
}

/// <summary>
/// An application put down without being made, waiting on an answer.
/// </summary>
/// <remarks>
/// <b>A parked row is not a sent one and must never be counted as one.</b> Parking is an
/// attribute on the submission rather than an event, because the log folds to the furthest phase
/// reached and "no attempt was made" is not a point on that ladder - the whole argument is on
/// <see cref="ParkReason"/>.
/// </remarks>
public sealed record ParkedApplicationResponse
{
    public required long SubmissionId { get; init; }
    public required long PostingId { get; init; }
    public required string PostingTitle { get; init; }
    public string? Company { get; init; }
    public required DateTimeOffset ParkedAtUtc { get; init; }
}

/// <summary>
/// What the candidate answers, and how far it should carry.
/// </summary>
/// <remarks>
/// <b>There is deliberately no source field.</b> Everything arriving over the tool surface is
/// stored as a client's assertion; this route is the one write in the system that may stamp
/// <see cref="FormAnswerSource.Candidate"/>, and it reads that from the token rather than from
/// anything a caller can fill in. A body carrying <c>source</c> would let a model stamp its own
/// inference as the person's own words by filling in a parameter - the same failure the
/// "no tool takes a profile id" rule exists to prevent, arriving through a request body.
///
/// <b>And no company or posting id travels in this either.</b> The scope is a choice; the ids
/// behind it are read server-side from the question's own advert. A body naming its own ids would
/// let a mistyped number file somebody's salary expectation against an employer they never
/// applied to, and there would be nothing for the server to check it against.
///
/// <b>No idempotency key, unlike the event log.</b> The answer store is keyed on the question and
/// supersedes rather than appends, so re-recording the same answer converges on the row already
/// there; and the first close of a question stands. A retry after a timeout is therefore safe
/// without one, which is not true of an append.
/// </remarks>
/// <param name="Value">
/// In the words that would be typed into the form. Stored verbatim and <b>refused rather than
/// shortened</b> past <see cref="FormAnswerLimits.MaxValueLength"/>: a truncated sentence typed
/// into an application reads as a statement rather than as a bug.
/// </param>
/// <param name="Scope"><c>Global</c>, <c>Company</c> or <c>Posting</c>. Who this answer is true for.</param>
/// <param name="Name">
/// A canonical key where the question has one, e.g. <c>notice_period</c>. The escape from
/// phrasing: the hash folds typography and nothing more, so two employers asking the same thing
/// in genuinely different words are two hashes and one name.
/// </param>
public sealed record AnswerQuestionRequest(
    [property: MaxLength(FormAnswerLimits.MaxValueLength)] string Value,
    string Scope,
    [property: MaxLength(FormAnswerLimits.MaxNameLength)] string? Name);

/// <summary>
/// What answering did, including the half that is otherwise invisible.
/// </summary>
/// <remarks>
/// <b><see cref="ReturnedToQueue"/> is the causal link the queue exists for.</b> Closing a
/// question takes it out of the unanswered set, which is the same set
/// <c>JobMatchRepository.ListApplyableAsync</c> reads, so an advert parked for a missing answer
/// stops being held - and that happens somewhere nobody is looking. Saying it is what makes the
/// queue legible as something being drained rather than a list of chores. <b>Nothing here sends
/// anything</b>: the next unattended pass is what picks the advert up.
/// </remarks>
public sealed record AnswerQuestionResponse
{
    public required long AnswerId { get; init; }

    /// <summary>False where that exact answer was already stored: nothing written, nothing superseded.</summary>
    public required bool Created { get; init; }

    public required string Scope { get; init; }

    public required bool Sensitive { get; init; }

    public required DateTimeOffset AnsweredAtUtc { get; init; }

    /// <summary>The question this closed. Null where the answer was volunteered rather than asked for.</summary>
    public long? ClosedQuestionId { get; init; }

    /// <summary>The applications no longer held back by it. Empty is ordinary, not a failure.</summary>
    public required IReadOnlyList<ParkedApplicationResponse> ReturnedToQueue { get; init; }

    /// <summary>An explanatory sentence where something is simply absent. Null where there is nothing to say.</summary>
    public string? Note { get; init; }
}
