namespace JobPlatform.Core.Matching;

/// <summary>How the model read the candidate's chances.</summary>
/// <remarks>
/// Three levels and an unknown, deliberately coarse. A finer scale invites the model to express
/// confidence it does not have, and the number that actually ranks a shortlist is
/// <see cref="CandidacyAssessment.Score"/> - this is the label a person reads.
/// </remarks>
public enum CandidacyVerdict
{
    /// <summary>The model returned nothing usable under this heading.</summary>
    Unknown = 0,

    /// <summary>A hard requirement is missing and no part of the profile substitutes for it.</summary>
    Weak = 1,

    /// <summary>Credible with caveats. Most of a good shortlist lands here.</summary>
    Possible = 2,

    /// <summary>Meets the substance of what the posting asks for.</summary>
    Strong = 3,
}

/// <summary>One pair handed to the assessor.</summary>
/// <param name="PostingId">Correlates the answer back. Never sent to the model - see the batch contract.</param>
/// <param name="Title">The advert title.</param>
/// <param name="Company">Who is hiring, where the posting says.</param>
/// <param name="Text">The advert body. The caller decides how much of it to send.</param>
/// <param name="Match">
/// What the deterministic scorer already concluded. Handed over so the model argues with a
/// starting position rather than re-deriving one: the gaps in particular are what it is being
/// asked to weigh, and recomputing them from prose would be both slower and less consistent.
/// </param>
public sealed record CandidacyRequest(
    long PostingId,
    string Title,
    string? Company,
    string Text,
    MatchResult Match);

/// <summary>
/// What the model concluded about one candidate against one posting.
/// </summary>
/// <remarks>
/// Deliberately not a replacement for <see cref="MatchResult"/>. The arithmetic says how much
/// of the posting the profile covers; this says whether the parts it does not cover matter -
/// which is a judgement, and the only part of matching worth paying a model for. Both are
/// stored, and the UI shows both, because a disagreement between them is informative rather
/// than a fault to be reconciled.
/// </remarks>
public sealed record CandidacyAssessment
{
    /// <summary>
    /// Bumped when the prompt or the parsing changes what the same input would produce.
    /// Rows below the current value are stale and eligible for a re-assessment.
    /// </summary>
    public const int CurrentVersion = 1;

    public CandidacyVerdict Verdict { get; init; }

    /// <summary>0-100, the model's own number. Kept beside the scorer's, never averaged with it.</summary>
    public int Score { get; init; }

    /// <summary>Two or three sentences a candidate can read. Not a summary of the posting.</summary>
    public string? Rationale { get; init; }

    /// <summary>What genuinely lands, in the model's words. Feeds the CV prompt.</summary>
    public IReadOnlyList<string> Strengths { get; init; } = [];

    /// <summary>What is missing and matters. The half a candidate can act on.</summary>
    public IReadOnlyList<string> Gaps { get; init; } = [];

    /// <summary>
    /// What the candidate should lead with if they apply.
    /// </summary>
    /// <remarks>
    /// The bridge to the writing pass. When a tailored CV is generated later, this is handed to
    /// it as guidance, so the two models agree about what matters instead of the second one
    /// re-deciding from scratch and contradicting what the candidate was told.
    /// </remarks>
    public IReadOnlyList<string> Emphasise { get; init; } = [];

    /// <summary>The deployment that answered, so a change of model is visible in the data.</summary>
    public string? Model { get; init; }

    /// <summary>The response body for this pair, kept verbatim.</summary>
    public string? PayloadJson { get; init; }

    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// Judges a shortlist the deterministic scorer has already produced.
/// </summary>
/// <remarks>
/// Implemented in <c>JobPlatform.Ai</c> and registered <b>only</b> where a Kernel is, so a
/// deployment with no provider configured resolves this as null and skips the step. Consumers
/// therefore take <c>ICandidacyAssessor?</c>, never <c>ICandidacyAssessor</c> - the same
/// contract <see cref="Enrichment.IDocumentExtractor"/> already carries.
/// </remarks>
public interface ICandidacyAssessor
{
    /// <summary>
    /// Assesses several postings against one candidate in as few calls as possible.
    /// </summary>
    /// <remarks>
    /// Batched at the interface rather than inside an implementation, because the saving is
    /// structural: the candidate's profile is the larger half of the prompt and is identical
    /// for every posting in the list. One call covering ten postings sends it once. A
    /// per-posting method would make that impossible to express and is therefore not offered.
    ///
    /// Positional, like <c>ExtractBatchAsync</c>: index <c>i</c> answers <c>requests[i]</c> and
    /// any element may be null. <see cref="CandidacyRequest.PostingId"/> exists for the caller
    /// to correlate with afterwards and is deliberately never put in front of the model, so a
    /// misaligned answer cannot come back wearing a plausible id.
    /// </remarks>
    Task<IReadOnlyList<CandidacyAssessment?>> AssessAsync(
        Profiles.CandidateProfile profile,
        IReadOnlyList<CandidacyRequest> requests,
        CancellationToken ct = default);
}
