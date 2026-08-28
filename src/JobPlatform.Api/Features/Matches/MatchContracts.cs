namespace JobPlatform.Api.Features.Matches;

/// <summary>
/// One scored posting, as a candidate sees it.
/// </summary>
/// <remarks>
/// <b>Both verdicts are returned, and neither is presented as the answer.</b> The arithmetic
/// score says how much of the posting the profile covers; the model's says whether the rest
/// matters. They disagree often, and a client showing only one of them - whichever one - is
/// throwing away the signal that a 58 the model called strong is the most interesting row on
/// the page.
///
/// <b>The list arrives ordered by <see cref="RankScore"/>, which is neither of them.</b> That
/// is the third number here and the one a client must not display: it is an ordering key, not a
/// measure of the match. Rows will therefore not be in descending <see cref="Score"/> order, and
/// that is the fix rather than the bug - see <c>MatchRanker</c> for the measurement behind it.
///
/// No description. This is a list response and the same rule <c>PostingSummary</c> follows
/// applies: the full advert is one request away, and including it would turn a page of fifty
/// matches into megabytes.
/// </remarks>
public record MatchSummary
{
    public required long PostingId { get; init; }
    public required string Title { get; init; }
    public string? Company { get; init; }
    public string? Location { get; init; }

    public decimal? AnnualSalaryMin { get; init; }
    public decimal? AnnualSalaryMax { get; init; }
    public string? AnnualSalaryCurrency { get; init; }

    public string WorkArrangement { get; init; } = nameof(Core.Enrichment.WorkArrangement.Unknown);
    public string Seniority { get; init; } = nameof(Core.Enrichment.Seniority.Unknown);

    public DateOnly? DatePosted { get; init; }

    /// <summary>0-100 from the deterministic scorer. Always present.</summary>
    public required int Score { get; init; }

    /// <summary>
    /// How much of a full assessment this posting supported, 0-1.
    /// </summary>
    /// <remarks>
    /// <b>Read this next to <see cref="Score"/>, never instead of it.</b> A 100 computed over
    /// every axis and a 100 computed over one are the same number and very different claims.
    /// Most real postings land between 0.2 and 0.5 - they state skills and little else - so a
    /// low value is normal rather than alarming; it is a low value <i>with</i> a high score
    /// that deserves a caveat in the UI.
    /// </remarks>
    public double Coverage { get; init; }

    /// <summary>How many requirements the posting marked essential and the profile does not meet.</summary>
    public int RequiredGapCount { get; init; }

    /// <summary>Null until the nightly sweep has reached this row.</summary>
    public string? Verdict { get; init; }

    /// <summary>The model's own 0-100. Null where it has not judged this pair.</summary>
    public int? AssessmentScore { get; init; }

    /// <summary>Two or three sentences addressed to the candidate.</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// Cosine of the profile against this advert, or null where either side has no vector.
    /// </summary>
    /// <remarks>
    /// Returned because the ordering should be arguable rather than merely obeyed - a candidate
    /// asking why one posting is above another deserves the inputs, not just the outcome. It is
    /// not a percentage and it does not span 0 to 1 in practice: for one profile the whole corpus
    /// typically occupies a band around 0.15 wide, so the absolute value says very little and the
    /// position within the band says everything. Present it as a comparison or not at all.
    /// </remarks>
    public double? Similarity { get; init; }

    /// <summary>
    /// What the list is ordered by, 0-100. <b>An ordering key, not a score.</b>
    /// </summary>
    /// <remarks>
    /// A convex combination of <see cref="Score"/> and <see cref="Similarity"/>, normalised over
    /// this candidate's whole pool - so it is not comparable between candidates or between
    /// nights, and rendering it beside the score would put two numbers on screen that look like
    /// the same kind of thing and are not. Returned so a client can re-sort without a second
    /// request, and so the order can be explained.
    /// </remarks>
    public double RankScore { get; init; }

    public DateTimeOffset ScoredAtUtc { get; init; }
    public DateTimeOffset? AssessedAtUtc { get; init; }
}

/// <summary>One match in full, including the breakdown behind the number.</summary>
/// <remarks>
/// The breakdown is the reason this endpoint exists rather than the score being a column on the
/// posting list. A number with nothing behind it is a number nobody acts on; "you meet nine of
/// their eleven requirements, and the two you do not are Terraform and a security clearance" is
/// something a person can do something about.
/// </remarks>
public sealed record MatchDetail : MatchSummary
{
    /// <summary>Per-axis scores. Weight is zero where the posting said nothing on that axis.</summary>
    public IReadOnlyList<MatchComponentResponse> Components { get; init; } = [];

    public IReadOnlyList<ConceptMatchResponse> Matched { get; init; } = [];

    public IReadOnlyList<ConceptGapResponse> Gaps { get; init; } = [];

    public IReadOnlyList<string> Strengths { get; init; } = [];

    /// <summary>The model's gaps, in prose. Distinct from the concept-level ones above.</summary>
    public IReadOnlyList<string> AssessmentGaps { get; init; } = [];

    /// <summary>What to lead with. The same list the CV writer is given.</summary>
    public IReadOnlyList<string> Emphasise { get; init; } = [];

    /// <summary>Whether a tailored CV has already been generated for this posting.</summary>
    public bool HasApplication { get; init; }
}

/// <param name="Name">Stable identifier - <c>requiredSkills</c>, <c>seniority</c>. The UI supplies wording.</param>
/// <param name="Score">0-1 within this axis.</param>
/// <param name="Weight">
/// Share of the total this axis carried for this pair. <b>Zero means the posting said nothing</b>
/// and the axis was dropped rather than failed - a client rendering a zero-weight axis as a
/// zero score is showing the candidate a penalty that was never applied.
/// </param>
public readonly record struct MatchComponentResponse(string Name, double Score, double Weight);

/// <param name="Required">The concept key the posting asked for.</param>
/// <param name="RequiredLabel">Its human-readable name.</param>
/// <param name="Held">The concept the candidate holds. Equal to <paramref name="Required"/> for an exact match.</param>
/// <param name="HeldLabel">Its human-readable name.</param>
/// <param name="Relation">
/// <c>Exact</c>, <c>Specialisation</c>, <c>Generalisation</c>, <c>Implied</c>, <c>Related</c> or
/// <c>Superseded</c>. Worth rendering: "you have Vue, they want React" is an argument, not a
/// match, and presenting it as one is how a candidate ends up in the wrong interview.
/// </param>
/// <param name="Credit">0-1 after the relation, the stated strength and any years shortfall.</param>
/// <param name="Demand">How hard the posting asked - <c>Required</c>, <c>Preferred</c>, <c>Mentioned</c>.</param>
public readonly record struct ConceptMatchResponse(
    string Required,
    string RequiredLabel,
    string Held,
    string HeldLabel,
    string Relation,
    double Credit,
    string Demand);

/// <param name="Concept">The concept key nothing in the profile answers.</param>
/// <param name="Label">Its human-readable name.</param>
/// <param name="Demand">How hard the posting asked.</param>
/// <param name="YearsMin">Years the posting attached to it, where it gave a number.</param>
public readonly record struct ConceptGapResponse(
    string Concept,
    string Label,
    string Demand,
    int? YearsMin);
