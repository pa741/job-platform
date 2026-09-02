namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// A question a run could not answer, waiting on a person.
/// </summary>
/// <remarks>
/// <b>This table is what makes abstention recoverable, and without it abstention is a loop.</b>
/// Resolution refuses by default - below the confidence floor, on a sensitive field with no
/// stored answer, or where an option set will not map cleanly - because a confident near-miss on
/// somebody's application is worse than an interruption. But a posting parked for
/// <c>ParkReason.MissingAnswer</c> that returns on the next run produces the same park again,
/// every run, forever. So the question is raised here, and <c>ParkRequeue.WhenAnswered</c> holds
/// the posting back until <see cref="AnsweredAtUtc"/> is set. That is the only reason the requeue
/// classification is three-valued rather than a bool.
///
/// <b>One live question per candidate per wording, and the index says so.</b> A run that meets
/// the same question on four adverts must not put it to a person four times; a person who
/// answers it once must not be asked again next week. The filtered unique index on
/// <c>(ProfileId, QuestionHash)</c> over unanswered rows is what enforces that, and answering a
/// question leaves the index rather than deleting the row - so the history of what was asked
/// survives, the way it does everywhere else in this pipeline.
///
/// <b><see cref="PostingId"/> and <see cref="RunId"/> are context, never identity.</b> The
/// question "do you require sponsorship?" is the same question whichever advert raised it, so
/// neither is part of the key. They are here so a person can see what they are being asked about
/// and so an abandoned run's questions are still attributable afterwards - the two things a bare
/// question text cannot supply.
/// </remarks>
public sealed class OpenQuestionEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>The advert that raised it, where one did. Context for the person, not part of the key.</summary>
    public long? PostingId { get; set; }

    /// <summary>The run that raised it, where one did. Null for a question asked from the dashboard.</summary>
    public long? RunId { get; set; }

    /// <summary>The question as the form asked it, verbatim.</summary>
    public required string QuestionText { get; set; }

    /// <summary><c>QuestionKey.Hash</c> over the normalised question. What the uniqueness rule is written on.</summary>
    public required string QuestionHash { get; set; }

    /// <summary>
    /// The choices the form offered, as a JSON array, or null for a free-text box.
    /// </summary>
    /// <remarks>
    /// Stored so the person is asked the question the form actually asked. Answering "three
    /// months" to a dropdown that only offers "1-2 months" and "3+ months" produces a stored
    /// answer that cannot be typed into the field it was collected for.
    ///
    /// Unbounded, following <c>EmphasisedJson</c>: it is read back whole to be shown and never
    /// queried into, which is the condition under which a JSON column is the right call rather
    /// than the lazy one.
    /// </remarks>
    public string? OptionsJson { get; set; }

    /// <summary>Whether the answer will be one a person should see leave the system.</summary>
    /// <remarks>
    /// Carried from the question rather than from the answer, because it decides how the question
    /// is <i>presented</i> - a confirmation on the dashboard, and a disclosure record naming the
    /// question and never the value - and that decision is made before anybody has typed
    /// anything.
    /// </remarks>
    public bool Sensitive { get; set; }

    public DateTimeOffset AskedAtUtc { get; set; }

    /// <summary>When it stopped waiting. Null is the flag, and it is what the queue predicate reads.</summary>
    public DateTimeOffset? AnsweredAtUtc { get; set; }

    /// <summary>The answer that closed it, where one did.</summary>
    /// <remarks>
    /// Nullable independently of <see cref="AnsweredAtUtc"/>, because the two are different
    /// facts. A candidate can dismiss a question - "I am not answering that" - which closes it
    /// without producing an answer to file, and a question closed that way must not come back
    /// next run.
    /// </remarks>
    public long? AnswerId { get; set; }

    public CandidateProfileEntity? Profile { get; set; }

    public JobPostingEntity? Posting { get; set; }

    public RunEntity? Run { get; set; }

    public FormAnswerEntity? Answer { get; set; }
}
