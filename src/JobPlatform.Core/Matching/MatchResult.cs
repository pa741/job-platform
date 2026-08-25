using JobPlatform.Core.Enrichment;

namespace JobPlatform.Core.Matching;

/// <summary>
/// How a held concept satisfied a required one.
/// </summary>
/// <remarks>
/// Recorded rather than collapsed into a number, because these are not the same claim and a
/// candidate reading their own match deserves to know which one was made about them. "You have
/// Kubernetes, they asked for containerisation" is a fact; "you have Vue, they asked for React"
/// is an argument. Both can contribute to a score - only one of them should be presented as a
/// match without qualification.
/// </remarks>
public enum MatchRelation
{
    /// <summary>The same concept key on both sides.</summary>
    Exact = 0,

    /// <summary>
    /// The candidate holds something narrower. They asked for SQL, the candidate has
    /// PostgreSQL. Full credit: the specific case entails the general one.
    /// </summary>
    Specialisation = 1,

    /// <summary>
    /// The candidate holds something broader. They asked for PostgreSQL, the candidate has SQL.
    /// Partial credit, decaying with distance - it is real transferable ground and it is not
    /// the thing asked for.
    /// </summary>
    Generalisation = 2,

    /// <summary>
    /// The candidate holds something the vocabulary says implies the requirement. Kubernetes
    /// implies containerisation. Full credit, because the implication edge is curated rather
    /// than inferred.
    /// </summary>
    Implied = 3,

    /// <summary>
    /// Competing or commonly substituted - React and Vue. Weak credit, and the honest label
    /// for the largest category of near-misses in this domain.
    /// </summary>
    Related = 4,

    /// <summary>
    /// The candidate holds the technology this one replaced. AngularJS against Angular. Weak
    /// credit and deliberately not folded into <see cref="Related"/>: it carries a direction,
    /// which is the whole reason the vocabulary keeps succession separate from similarity.
    /// </summary>
    Superseded = 5,
}

/// <summary>One requirement the candidate meets, and how.</summary>
/// <param name="RequiredKey">The concept the posting asked for.</param>
/// <param name="HeldKey">The concept the candidate holds. Equal to <paramref name="RequiredKey"/> for an exact match.</param>
/// <param name="Relation">Which kind of claim this is.</param>
/// <param name="Credit">0..1, after the relation, the candidate's stated strength and any years shortfall.</param>
/// <param name="Demand">How hard the posting asked. Required weighs more than preferred.</param>
public readonly record struct ConceptMatch(
    string RequiredKey,
    string HeldKey,
    MatchRelation Relation,
    double Credit,
    AssertionPolarity Demand);

/// <summary>
/// A requirement nothing in the profile answers.
/// </summary>
/// <remarks>
/// The most useful half of the result, and the reason gaps are returned rather than merely
/// subtracted. It is what the candidacy prompt is given to reason about, what the tailored CV
/// is told not to claim, and the only part of a match a candidate can actually act on.
/// </remarks>
public readonly record struct ConceptGap(
    string RequiredKey,
    AssertionPolarity Demand,
    int? YearsMin);

/// <summary>
/// One axis of the score, kept separate so a total can be read back apart.
/// </summary>
/// <param name="Name">Stable identifier, not a label. The UI supplies wording.</param>
/// <param name="Score">0..1 within this axis.</param>
/// <param name="Weight">
/// Share of the total this axis carried <b>for this pair</b>. Zero where the posting said
/// nothing - see <see cref="MatchResult"/> on why silence drops an axis instead of failing it.
/// </param>
public readonly record struct MatchComponent(string Name, double Score, double Weight)
{
    public const string RequiredSkills = "requiredSkills";
    public const string PreferredSkills = "preferredSkills";
    public const string Seniority = "seniority";
    public const string Experience = "experience";
    public const string WorkArrangement = "workArrangement";
    public const string Salary = "salary";
    public const string Location = "location";
}

/// <summary>
/// What the deterministic scorer concluded about one candidate and one posting.
/// </summary>
/// <remarks>
/// <b>Silence drops an axis rather than failing it.</b> Most postings state no salary, many
/// state no work arrangement, and 18% of titles carry no seniority. Scoring those as zero would
/// rank a posting that says nothing below one that says something incompatible, which is
/// exactly backwards; scoring them as full marks would make vagueness a competitive advantage.
/// So an axis the posting cannot answer contributes nothing to the numerator <i>and</i> nothing
/// to the denominator, and <see cref="MatchComponent.Weight"/> records what each axis actually
/// carried for this pair. That is why the weights are per-result rather than constants
/// somewhere.
///
/// <b>But silence has a floor.</b> Dropping axes is right until there is nothing substantive
/// left: a posting carrying no readable requirements, scored only on the city it is in, was
/// coming out at 100 and outranking roles the candidate genuinely fits. Measured against the
/// real corpus, 44 of the top 60 matches had no skills axis at all and 13 rested on location
/// alone. So <see cref="Coverage"/> records how much of the nominal weight the posting could
/// answer, and a posting that answers neither concept axis scores zero rather than inheriting
/// a perfect score from a peripheral one - see <see cref="MatchScorer"/>.
///
/// This whole type is deliberately Azure-free and pure, the same way
/// <c>MetricsCalculator</c> is: the scoring rules are the part most worth testing exactly, and
/// they are testable exactly only while nothing in here needs a database to run.
/// </remarks>
public sealed record MatchResult
{
    /// <summary>
    /// Bumped whenever the scorer would produce a different number for the same input.
    /// Rows below the current value are stale and eligible for a re-score.
    /// </summary>
    /// <remarks>
    /// 2: a posting answering neither concept axis scores zero instead of inheriting a perfect
    /// score from location alone, and <see cref="Coverage"/> is reported.
    /// </remarks>
    public const int CurrentVersion = 2;

    /// <summary>0-100. Rounded once, here, so every consumer shows the same number.</summary>
    public required int Score { get; init; }

    /// <summary>
    /// How much of the nominal weight this posting could actually answer, 0-1.
    /// </summary>
    /// <remarks>
    /// The honesty measure that <see cref="Score"/> alone cannot carry. A 100 computed over
    /// every axis and a 100 computed over one are the same number and very different claims,
    /// and without this there is nothing to tell them apart. Most real postings land between
    /// 0.2 and 0.5: they state skills and little else.
    ///
    /// Deliberately not folded into <see cref="Score"/> as a multiplier. A posting that states
    /// only skills, and whose skills the candidate has, <i>is</i> a complete match on
    /// everything it asked for - discounting it for the questions it never posed would punish
    /// the candidate for the employer's terseness.
    /// </remarks>
    public required double Coverage { get; init; }

    public IReadOnlyList<MatchComponent> Components { get; init; } = [];

    /// <summary>Ordered by credit, descending. The strongest reasons first.</summary>
    public IReadOnlyList<ConceptMatch> Matched { get; init; } = [];

    /// <summary>Required gaps before preferred ones, because that is the order they matter in.</summary>
    public IReadOnlyList<ConceptGap> Gaps { get; init; } = [];

    public int Version { get; init; } = CurrentVersion;

    /// <summary>How many of the posting's hard requirements are unmet. The headline caveat.</summary>
    public int RequiredGapCount => Gaps.Count(g => g.Demand == AssertionPolarity.Required);
}
