namespace JobPlatform.Api.Features.Concepts;

/// <summary>One node of the vocabulary, as a list entry.</summary>
/// <param name="Concept">The stable key. <c>skill.kubernetes</c>.</param>
/// <param name="Kind"><c>Domain</c>, <c>Skill</c> or <c>Qualification</c>.</param>
public readonly record struct ConceptListItem(string Concept, string Label, string Kind);

/// <summary>
/// An edge out of the concept under inspection.
/// </summary>
/// <param name="Concept">The concept at the other end.</param>
/// <param name="Relation">
/// <c>Broader</c>, <c>Narrower</c>, <c>Implies</c>, <c>ImpliedBy</c>, <c>Related</c>,
/// <c>SucceededBy</c> or <c>Succeeds</c>.
/// </param>
/// <param name="Demand">
/// How many distinct postings assert the concept at the other end. Null where the count was
/// not requested; zero is a real answer and means nobody is asking for it.
/// </param>
public readonly record struct ConceptEdgeResponse(
    string Concept,
    string Label,
    string Kind,
    string Relation,
    int? Demand);

/// <summary>
/// One concept, its neighbourhood, and how much of the corpus wants each part of it.
/// </summary>
/// <remarks>
/// <b>The vocabulary is the intellectual centre of this system and had no view at all.</b> It
/// decides what a posting is understood to ask for, what a profile is understood to hold, and
/// therefore every match - and the only way to see it was to read <c>concepts.json</c>.
///
/// The edges come from the graph shipped in the build and cost no database at all. The demand
/// counts do cost one query, bounded to this neighbourhood rather than the whole vocabulary.
/// </remarks>
public sealed record ConceptDetail
{
    public required string Concept { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }

    /// <summary>Every surface form that resolves to this concept, and how it is treated.</summary>
    /// <remarks>
    /// The half of the vocabulary that does the actual work. "k8s" resolving to Kubernetes is
    /// why a match is possible at all, and an <c>Ambiguous</c> form - "Go", "R", "C" - is why
    /// some matches are deliberately refused.
    /// </remarks>
    public IReadOnlyList<LabelResponse> Labels { get; init; } = [];

    /// <summary>Distinct postings asserting this concept, within the selected search term.</summary>
    public int Demand { get; init; }

    /// <summary>
    /// Parents, children, implications and neighbours, in one list keyed by relation.
    /// </summary>
    /// <remarks>
    /// One list rather than six fields, so a client renders relation groups generically and a
    /// new edge type does not need a frontend change to become visible.
    /// </remarks>
    public IReadOnlyList<ConceptEdgeResponse> Edges { get; init; } = [];

    /// <summary>
    /// Every ancestor reachable through <c>Broader</c>, with its distance.
    /// </summary>
    /// <remarks>
    /// The closure, which is what makes a domain rollup possible and what
    /// <c>MatchScorer</c> walks to decide that EKS satisfies a Kubernetes requirement. Depth is
    /// carried because the scorer decays credit with it.
    /// </remarks>
    public IReadOnlyList<AncestorResponse> Ancestors { get; init; } = [];
}

/// <param name="Kind"><c>Preferred</c>, <c>Alternate</c>, or <c>Ambiguous</c> - a form that names the concept but cannot be trusted to mean it.</param>
public readonly record struct LabelResponse(string Label, string Kind);

public readonly record struct AncestorResponse(string Concept, string Label, int Depth);

/// <summary>
/// Where the corpus's knowledge comes from.
/// </summary>
/// <remarks>
/// The only honest measure of what each pass contributes. Three passes write assertions that
/// look identical once stored - this separates them, including the part that matters most:
/// only the model pass can say whether a requirement is essential or merely desirable, so the
/// share of assertions carrying a real polarity is the share of the corpus we actually
/// understand rather than merely inventory.
/// </remarks>
public sealed record SourceCompositionResponse
{
    public string? SearchTerm { get; init; }

    public IReadOnlyList<SourceBreakdown> Sources { get; init; } = [];

    /// <summary>Assertions across every source. The denominator for the shares below.</summary>
    public int TotalAssertions { get; init; }

    /// <summary>
    /// Share of assertions that carry a strength rather than <c>Unspecified</c>, 0-1.
    /// </summary>
    /// <remarks>
    /// The headline number of this whole view. A corpus where this is near zero is one where
    /// the model pass has not run, and where every match is therefore weighing "mentioned once
    /// in passing" the same as "must have".
    /// </remarks>
    public double GradedShare { get; init; }
}

/// <param name="Source">
/// <c>Board</c>, <c>Taxonomy</c> or <c>Model</c>, in descending order of trust.
/// </param>
/// <param name="Polarities">The strength breakdown within this source.</param>
public readonly record struct SourceBreakdown(
    string Source,
    int Assertions,
    int Postings,
    IReadOnlyList<PolarityCount> Polarities);

public readonly record struct PolarityCount(string Polarity, int Assertions);
