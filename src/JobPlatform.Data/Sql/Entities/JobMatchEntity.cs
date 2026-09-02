using JobPlatform.Core.Matching;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One candidate against one posting: what the arithmetic said, and what the model said.
/// </summary>
/// <remarks>
/// <b>Both verdicts are stored, and neither overwrites the other.</b> The scorer's number says
/// how much of the posting the profile covers; the model's says whether what it does not cover
/// matters. They disagree fairly often and the disagreement is the informative part - a role
/// scoring 58 that the model calls strong is precisely the posting worth surfacing, and
/// collapsing the two into one column would delete the only signal that says so.
///
/// The score half is written by a deterministic pass over every candidate posting; the
/// assessment half is written later, by the nightly sweep, and only for rows that cleared the
/// threshold. So an assessed row always has a score and a scored row often has no assessment -
/// which is why every assessment column is nullable and <see cref="AssessedAtUtc"/> rather than
/// a flag is what says whether the model has been here.
///
/// <see cref="ScorerVersion"/> and <see cref="AssessmentVersion"/> are the same backfill
/// mechanism the extraction rows carry: change the weights or the prompt, bump the constant,
/// and everything below it is stale without anything needing to be deleted.
/// </remarks>
public sealed class JobMatchEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    // --- the deterministic half ---------------------------------------------

    /// <summary>0-100, from <see cref="MatchScorer"/>. Always present.</summary>
    public int Score { get; set; }

    /// <summary>
    /// The per-axis breakdown, as JSON.
    /// </summary>
    /// <remarks>
    /// JSON rather than columns because the axes are a property of the scorer, not of the
    /// schema: adding one is a change to <see cref="MatchScorer"/> and a version bump, and it
    /// should not also be a migration. Nothing queries inside this - it is read back whole, to
    /// be shown - which is the condition under which a JSON column is the right call rather
    /// than the lazy one.
    /// </remarks>
    public string? ComponentsJson { get; set; }

    /// <summary>Which requirements the profile meets, and how. Read back whole.</summary>
    public string? MatchedJson { get; set; }

    /// <summary>Which requirements it does not. The half the candidate can act on.</summary>
    public string? GapsJson { get; set; }

    /// <summary>How many unmet requirements the posting marked essential. Promoted for sorting.</summary>
    public int RequiredGapCount { get; set; }

    public int ScorerVersion { get; set; }

    public DateTimeOffset ScoredAtUtc { get; set; }

    // --- the ordering half, written by the same pass as the score -----------

    /// <summary>
    /// Cosine of the profile against this advert, or null where either side has no vector.
    /// </summary>
    /// <remarks>
    /// Stored beside <see cref="RankScore"/> rather than only feeding it, because the two are
    /// durable for different lengths of time. This is a measurement - the same pair gives the
    /// same number in any pool - so it is what a re-tuning of the weight is fitted against, and
    /// what makes an ordering arguable after the fact. <see cref="RankScore"/> is derived from
    /// it and from every other pair in the sweep, and is worth nothing outside that pool.
    ///
    /// Null rather than zero where the pass has not reached the posting. Absent evidence and a
    /// measured dissimilarity are different facts and the ranker treats them differently.
    /// </remarks>
    public double? Similarity { get; set; }

    /// <summary>
    /// Where this pair sits in the list, 0-100. <b>An ordering key, not a score.</b>
    /// </summary>
    /// <remarks>
    /// <see cref="MatchRanker"/> holds why this exists and what it is worth: the deterministic
    /// <see cref="Score"/> orders the corpus well and inverts inside its own top band, and the
    /// embedding does the opposite, so the list is ordered by a convex combination of the two
    /// while the score keeps meaning what it always meant.
    ///
    /// <b>Not comparable between profiles or between sweeps.</b> It is computed from a min-max
    /// normalisation over one profile's eligible pool, which is what makes a cosine occupying a
    /// band 0.15 wide combinable with a 0-100 score at all. Nothing should present it as a
    /// percentage, and no query should compare it across a ProfileId boundary.
    /// </remarks>
    public double RankScore { get; set; }

    /// <summary>
    /// Which ranker produced <see cref="RankScore"/>. Rows below the current value are stale.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ScorerVersion"/> because re-deriving the two costs different
    /// things. A scorer change needs the concept graph and every assertion row, and it clears
    /// the assessment when the number moves; a ranker change needs nothing but the columns
    /// already here, and clears nothing. Sharing one constant would make every tuning of the
    /// embedding weight pay for a full re-score and throw away the judgements it was fitted on.
    /// </remarks>
    public int RankerVersion { get; set; }

    // --- the model half, written only for rows that cleared the threshold ----

    /// <summary>Null until the sweep reaches this row. Never a default - see the remarks.</summary>
    public CandidacyVerdict? Verdict { get; set; }

    /// <summary>The model's own 0-100. Kept beside <see cref="Score"/>, never averaged with it.</summary>
    public int? AssessmentScore { get; set; }

    public string? Rationale { get; set; }

    /// <summary>Sentences, as a JSON array. Read back whole, never queried into.</summary>
    public string? StrengthsJson { get; set; }
    public string? AssessmentGapsJson { get; set; }

    /// <summary>
    /// What to lead with if they apply.
    /// </summary>
    /// <remarks>
    /// The bridge between the two models. When a CV is generated later this is handed to the
    /// writing deployment as guidance, so the document argues the case the candidate was
    /// already shown instead of the second model re-deciding and contradicting the first.
    /// </remarks>
    public string? EmphasiseJson { get; set; }

    public string? AssessmentModel { get; set; }

    public int? AssessmentVersion { get; set; }

    /// <summary>Null means the model has not been here. The only reliable way to ask.</summary>
    public DateTimeOffset? AssessedAtUtc { get; set; }

    /// <summary>The model's response for this pair, kept verbatim.</summary>
    public string? AssessmentPayloadJson { get; set; }

    /// <summary>
    /// When the candidate said they were not interested. Null means they have not.
    /// </summary>
    /// <remarks>
    /// Nullable and dated rather than a boolean, for the same reason
    /// <see cref="AssessedAtUtc"/> is: the null is the flag, and the date is worth having
    /// when a pair is dismissed and later re-scored into a very different shape.
    ///
    /// <para>
    /// Not a submission. That table records that something was <em>sent</em>, and its
    /// <c>Withdrawn</c> event closes an application which existed - a posting the candidate
    /// was never interested in is neither. It belongs here because it is a fact about this
    /// pair, and because the two reads that need it are already on this table.
    /// </para>
    ///
    /// <para>
    /// It has to survive a re-score. <c>UpsertScoresAsync</c> clears the assessment columns
    /// when the arithmetic moves, and a dismissal swept up in that reset would put the
    /// posting back at the top of the shortlist the next morning - which is the exact
    /// failure this column exists to prevent.
    /// </para>
    /// </remarks>
    public DateTimeOffset? DismissedAtUtc { get; set; }
}

/// <summary>
/// A generated CV and cover letter, kept as written.
/// </summary>
/// <remarks>
/// Stored rather than streamed and forgotten, for three reasons that all turned out to matter.
/// A candidate who applied with a particular version of their CV needs to be able to see what
/// they sent. Regenerating costs a call on the expensive deployment, so re-reading a draft must
/// not trigger one. And a second generation is a revision of the first, which is only true if
/// the first still exists - hence rows per generation rather than one row updated in place.
///
/// The markdown is the record; the PDF is a rendering of it and is produced on demand. Storing
/// the PDF would mean a change to the layout could not reach documents already generated, and
/// would put megabytes into a database billed by the second.
/// </remarks>
public sealed class ApplicationDocumentEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    /// <summary>1 for the first draft for this pair, incrementing per regeneration.</summary>
    public int Revision { get; set; }

    /// <summary>Unbounded. The tailored CV, as markdown.</summary>
    public string? CurriculumVitaeMarkdown { get; set; }

    /// <summary>Unbounded. The cover letter, as markdown.</summary>
    public string? CoverLetterMarkdown { get; set; }

    /// <summary>What this draft chose to lead with, as a JSON array of sentences.</summary>
    public string? EmphasisedJson { get; set; }

    /// <summary>What the candidate asked for, verbatim. Kept so a revision is comparable.</summary>
    public string? Instructions { get; set; }

    public string? Model { get; set; }

    public int WriterVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
