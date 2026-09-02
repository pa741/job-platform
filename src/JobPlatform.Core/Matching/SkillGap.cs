using JobPlatform.Core.Enrichment;

namespace JobPlatform.Core.Matching;

/// <summary>
/// One concept the market asks this candidate for that their profile does not hold.
/// </summary>
/// <param name="ConceptKey">The concept nothing in the profile answers exactly.</param>
/// <param name="MatchPostings">
/// How many of <i>this candidate's</i> scored matches name it. The number to rank by: the
/// corpus count says what the market wants, and this says what the market wants <i>of them</i>.
/// </param>
/// <param name="CorpusPostings">
/// How many postings name it across the corpus, for context. Always the larger of the two, and
/// on its own it is the least actionable figure on the page - the concept at the top of it is
/// invariably one the candidate already holds.
/// </param>
/// <param name="HeldKey">
/// The nearest thing the profile does hold, or null where it holds nothing related at all.
/// </param>
/// <param name="Relation">
/// How <paramref name="HeldKey"/> relates to <paramref name="ConceptKey"/>, from the same
/// decision the match breakdown reports. Null with <paramref name="HeldKey"/>.
/// </param>
/// <param name="Credit">
/// What that relation is worth before the candidate's own strength is applied, 0-1. A gap with
/// credit is one the scorer already gives partial marks for; a gap without is not.
/// </param>
public sealed record SkillGap(
    string ConceptKey,
    int MatchPostings,
    int CorpusPostings,
    string? HeldKey,
    MatchRelation? Relation,
    double Credit);

/// <summary>
/// The join, run backwards: what the corpus asks for that a profile does not hold.
/// </summary>
/// <remarks>
/// A set difference over two tables of the same shape, which is the whole payoff of postings
/// and profiles being extracted into one vocabulary. Every other figure on the market page is
/// about the corpus; this one is about the reader, and it is the only one that changes what
/// they would do next.
///
/// <para>
/// Pure, and separate from the queries that feed it, because the interesting half is the graph
/// walk rather than the counting. "You do not have Terraform" is a fact. "You hold Bicep, which
/// the graph records as Related rather than equivalent, so it earns partial credit and never
/// full" is the same fact with something to do about it - and it has to be the same decision
/// the match breakdown reports, or the two pages disagree about the same pair.
/// </para>
/// </remarks>
public static class SkillGapAnalysis
{
    /// <summary>
    /// Ranks the concepts the candidate's own matched band asks for and their profile does not
    /// hold, strongest demand first.
    /// </summary>
    /// <param name="inBandDemand">Postings naming each concept among this candidate's matches.</param>
    /// <param name="corpusDemand">Postings naming each concept across the corpus, for context.</param>
    /// <param name="heldKeys">Every concept the profile holds, declared and extracted alike.</param>
    /// <param name="graph">The vocabulary, for the near-miss walk.</param>
    /// <param name="limit">How many gaps to return.</param>
    public static IReadOnlyList<SkillGap> Compute(
        IReadOnlyDictionary<string, int> inBandDemand,
        IReadOnlyDictionary<string, int> corpusDemand,
        IReadOnlyCollection<string> heldKeys,
        ConceptGraph graph,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(inBandDemand);
        ArgumentNullException.ThrowIfNull(corpusDemand);
        ArgumentNullException.ThrowIfNull(heldKeys);
        ArgumentNullException.ThrowIfNull(graph);

        var held = new HashSet<string>(heldKeys, StringComparer.Ordinal);

        var gaps = new List<SkillGap>();

        foreach (var (key, inBand) in inBandDemand)
        {
            // Held outright is not a gap. Everything else is, including the concepts the
            // candidate holds something adjacent to - a Related edge earns partial credit and
            // never full, so it is a gap that costs less, not one that has been closed.
            if (held.Contains(key))
            {
                continue;
            }

            // Domains are excluded. Nothing is tagged with a domain directly - it is what
            // the closure gives you when a posting names a skill underneath it - so "you lack
            // Backend Development" is not a thing anybody can act on, and it would outrank
            // every real gap because its count is the sum of theirs.
            if (graph.TryGet(key, out var concept) && concept.Kind == ConceptKind.Domain)
            {
                continue;
            }

            var near = MatchScorer.BestRelation(held, key, graph);

            gaps.Add(new SkillGap(
                key,
                inBand,
                corpusDemand.TryGetValue(key, out var corpus) ? corpus : inBand,
                near?.HeldKey,
                near?.Relation,
                near?.Credit ?? 0));
        }

        return gaps
            .OrderByDescending(g => g.MatchPostings)
            // Ordinal on the key rather than nothing, so two concepts with the same demand do
            // not swap places between requests.
            .ThenBy(g => g.ConceptKey, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }
}
