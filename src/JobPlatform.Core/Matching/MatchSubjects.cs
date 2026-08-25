using JobPlatform.Core.Enrichment;

namespace JobPlatform.Core.Matching;

/// <summary>
/// The candidate side of a match, flattened to exactly what the scorer reads.
/// </summary>
/// <remarks>
/// Not <c>CandidateProfile</c> itself, deliberately. The scorer must not be able to reach the
/// candidate's name, email or employment history: none of it can legitimately affect a score,
/// and a type that cannot see it is a stronger guarantee than a rule saying it must not look.
/// It also keeps the scorer callable from a query projection - the API can build one of these
/// from a few columns without materialising a profile graph per posting.
/// </remarks>
public sealed record CandidateFacts
{
    /// <summary>
    /// Everything the candidate holds, declared and extracted alike.
    /// </summary>
    /// <remarks>
    /// The polarity here is the supply half - Familiar, Proficient, Expert. Where the same
    /// concept arrives from both sources the strongest claim wins, which is resolved before
    /// this record is built rather than inside the scorer.
    /// </remarks>
    public IReadOnlyList<ConceptAssertion> Concepts { get; init; } = [];

    public Seniority Seniority { get; init; }

    public int? YearsExperience { get; init; }

    /// <summary><see cref="WorkArrangement.Unknown"/> means no preference, not "on-site".</summary>
    public WorkArrangement PreferredArrangement { get; init; }

    public int? MaxDaysInOffice { get; init; }

    public decimal? MinimumSalary { get; init; }

    public string? SalaryCurrency { get; init; }

    public string? LocationCity { get; init; }

    public string? LocationCountry { get; init; }

    public bool WillingToRelocate { get; init; }
}

/// <summary>
/// The posting side of a match, flattened the same way.
/// </summary>
/// <remarks>
/// Built from the enriched columns and the assertion rows, never from the description. The
/// scorer works on what the pipeline concluded, so a change in how a posting is read shows up
/// as a version bump on the extraction rather than as a silently different score.
/// </remarks>
public sealed record PostingFacts
{
    public required long PostingId { get; init; }

    /// <summary>
    /// What the posting asks for. The polarity here is the demand half - Mentioned, Preferred,
    /// Required - and <see cref="AssertionPolarity.Unspecified"/> is the common case, because
    /// only the model pass can tell essential from desirable.
    /// </summary>
    public IReadOnlyList<ConceptAssertion> Concepts { get; init; } = [];

    public Seniority Seniority { get; init; }

    public int? YearsExperienceMin { get; init; }

    public int? YearsExperienceMax { get; init; }

    public WorkArrangement WorkArrangement { get; init; }

    public int? HybridDaysInOffice { get; init; }

    public decimal? AnnualSalaryMin { get; init; }

    public decimal? AnnualSalaryMax { get; init; }

    public string? SalaryCurrency { get; init; }

    /// <summary>
    /// True where the salary came from prose rather than a salary field.
    /// </summary>
    /// <remarks>
    /// Carried into the match because it changes what the number is worth. A figure a regex
    /// pulled out of a sentence is weaker evidence than one the board published in its own
    /// field, and the salary axis discounts its weight accordingly rather than pretending the
    /// two are interchangeable.
    /// </remarks>
    public bool SalaryFromText { get; init; }

    public string? LocationCity { get; init; }

    public string? LocationCountry { get; init; }
}
