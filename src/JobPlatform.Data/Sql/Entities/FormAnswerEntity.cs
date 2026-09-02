using JobPlatform.Core.Submissions;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One thing the candidate has said, stored so it can be said again without asking them.
/// </summary>
/// <remarks>
/// <b>This table is the declared namespace, and it never mixes with the derived one.</b>
/// <c>FormFieldCatalog</c> reads the profile and answers eleven questions the candidate already
/// answered structurally; nothing in it is sensitive and nothing may be added to it that is.
/// This holds only what a person typed into an answer box - which is what makes the sensitive
/// case safe without depending on a flag being set right: an EEO question, a salary expectation
/// or a date of birth is not reachable from the profile at all, so a value of that kind can
/// exist here because somebody wrote it and nowhere else because there is nowhere else.
///
/// <b>Superseded, never updated, and that is why <see cref="SupersededAtUtc"/> is a column
/// rather than a <c>Current</c> flag.</b> An answer store that overwrites cannot say what was
/// submitted last year, and "what did I tell them" is the question somebody asks after an
/// interview goes strangely. It is the argument the event log rests on, applied to a second
/// table: a timestamp says when the person changed their mind, where a flag says only that they
/// did.
///
/// <b><see cref="QuestionHash"/> and <see cref="NormalisedQuestion"/> are both stored.</b> The
/// hash is what the unique indexes are built on and what the resolution cache is keyed by, so
/// recomputing it on read would mean a later change to <c>QuestionKey.Normalise</c> silently
/// re-keys every answer already written and the candidate's answers stop being found with
/// nothing failing. The normalised text is kept beside it because a hash column with no readable
/// preimage is a debugging session nobody can finish.
/// </remarks>
public sealed class FormAnswerEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>The stable key this answer is filed under, where it has one - <c>notice_period</c>.</summary>
    /// <remarks>
    /// The escape from phrasing. <see cref="QuestionHash"/> folds typography and nothing more, so
    /// two employers asking the same thing in genuinely different words produce two hashes; a
    /// name written once lets both resolve. Free text rather than a <c>FormFieldCatalog</c>
    /// entry, because the catalogue is the derived namespace and the questions that most need a
    /// name here are exactly the ones it refuses to hold.
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>The question as the form asked it, verbatim. What a person reads when reviewing this.</summary>
    public required string QuestionText { get; set; }

    /// <summary><c>QuestionKey.Hash</c> over the normalised question. The lookup key and the index key.</summary>
    public required string QuestionHash { get; set; }

    /// <summary>The normalised form the hash was taken over, so a miss can be read rather than guessed at.</summary>
    public required string NormalisedQuestion { get; set; }

    /// <summary>What the candidate wrote, as typed. "Prefer not to say" is a value like any other.</summary>
    public required string Value { get; set; }

    /// <summary>How widely it applies. Part of the identity, not an attribute of it - see the indexes.</summary>
    public AnswerScope Scope { get; set; }

    /// <summary>
    /// The employer, for <see cref="AnswerScope.Company"/>. Null at every other scope.
    /// </summary>
    /// <remarks>
    /// Keyed on the company id rather than on the employer name, so it inherits the folding
    /// <c>Companies.CompanyKey</c> already does - lower-cased, punctuation collapsed, legal form
    /// stripped. Keying on the string written on the advert would file one answer twice under
    /// "Contoso" and "Contoso Ltd", which is the failure that table was built to remove.
    /// </remarks>
    public int? CompanyId { get; set; }

    /// <summary>The posting, for <see cref="AnswerScope.Posting"/>. Null at every other scope.</summary>
    public long? PostingId { get; set; }

    /// <summary>
    /// Whether this is an answer a person should see leave the system.
    /// </summary>
    /// <remarks>
    /// It drives redaction in the disclosure log and a confirmation on the dashboard, never
    /// permission to infer. The declared/derived split above is what keeps sensitive data safe,
    /// so a row with this wrong logs badly rather than leaking.
    /// </remarks>
    public bool Sensitive { get; set; }

    /// <summary>Who asserted it. Derived from the token type at the write path, never from a tool argument.</summary>
    public FormAnswerSource Source { get; set; }

    public DateTimeOffset AnsweredAtUtc { get; set; }

    /// <summary>When it stopped being what the candidate would say. Null while it stands.</summary>
    public DateTimeOffset? SupersededAtUtc { get; set; }

    public CandidateProfileEntity? Profile { get; set; }

    public CompanyEntity? Company { get; set; }

    public JobPostingEntity? Posting { get; set; }
}

/// <summary>
/// What a question resolved to last time, so the second occurrence costs a lookup rather than a
/// model call.
/// </summary>
/// <remarks>
/// <b>The cache is the acceptance criterion, not an optimisation.</b> Resolution runs four
/// stages - canonical key, normalised text, this table, and only then the model - and "the
/// second occurrence of a question resolves without a model call" is what this row is for. A hit
/// here is a dictionary lookup forever after.
///
/// <b>Keyed on the question <i>and</i> the options, because a select is a different question
/// from the free-text box that asks the same thing.</b> "Do you require sponsorship?" answered
/// against <c>[Yes, No]</c> and against <c>[Yes, No, Prefer not to say]</c> can resolve
/// differently and honestly, and one row for both would serve the first answer to the second
/// form. <c>QuestionKey.OptionsHash</c> is order-insensitive for the same reason it exists at
/// all: the order is the form's, not the question's, and a re-rendered dropdown must not miss.
///
/// <b>A row records an abstention as readily as an answer.</b> <see cref="AnswerId"/> is
/// nullable and <see cref="Confidence"/> is stored, so "we looked and would not answer" is a
/// cached outcome rather than a gap the next run pays to rediscover. That is the whole reason
/// <see cref="Rationale"/> is required: a cache row that cannot say why it decided what it did
/// is one nobody can audit after it turns out to have been wrong.
/// </remarks>
public sealed class FormAnswerResolutionEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary><c>QuestionKey.Hash</c> over the question as the form asked it.</summary>
    public required string QuestionHash { get; set; }

    /// <summary>
    /// <c>QuestionKey.OptionsHash</c> over the option set, or null where the field had none.
    /// </summary>
    /// <remarks>
    /// Null is a real and common value here - a free-text box and a select with no choices are
    /// the same question as far as this can tell - which is why the uniqueness rule is split in
    /// two rather than written as one index over a nullable column. See
    /// <c>JobsDbContext.ConfigureFormAnswers</c>.
    /// </remarks>
    public string? OptionsHash { get; set; }

    /// <summary>The catalogue or answer name it resolved to, where it resolved to one.</summary>
    public string? ResolvedName { get; set; }

    /// <summary>The stored answer it chose, or null where it abstained.</summary>
    public long? AnswerId { get; set; }

    /// <summary>0-1. Below the resolver's floor the outcome is an abstention, and is still cached.</summary>
    public double Confidence { get; set; }

    /// <summary>Why it decided this. Required, because an unexplained cache row cannot be audited.</summary>
    public required string Rationale { get; set; }

    /// <summary>Which deployment answered, where a model was reached at all. Null for stages 1-3.</summary>
    public string? Model { get; set; }

    public DateTimeOffset ResolvedAtUtc { get; set; }

    /// <summary>
    /// Whether a person has agreed with this resolution.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Confidence"/> because they answer different questions: confidence
    /// is what the resolver thought, and this is whether anybody checked. A high-confidence
    /// resolution nobody has confirmed is still the resolver's own opinion, and a sensitive field
    /// is answered verbatim or not at all whatever this says.
    /// </remarks>
    public bool Confirmed { get; set; }

    public CandidateProfileEntity? Profile { get; set; }

    public FormAnswerEntity? Answer { get; set; }
}
