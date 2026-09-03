using JobPlatform.Core.Enrichment;

namespace JobPlatform.Core.Applications;

/// <summary>
/// One posting no variant could be sent to, and what it asked for that nothing covered.
/// </summary>
/// <remarks>
/// <b>The difference is handed in, not recomputed here.</b> Working out which of a posting's
/// requirements a library answers is not a set subtraction - selection scores over the graph, so
/// a variant naming Bicep partly answers a posting asking for Terraform, exactly as the match
/// breakdown says it does. Recomputing that here with <c>Except</c> would be a second definition
/// of "covers", free to disagree with the one that actually parked the posting, and the brief
/// would then tell somebody to write a CV about a concept selection already considered answered.
/// One definition, two readers - the rule <c>ParkReasonPolicy</c> follows for the same reason.
///
/// <b>Nothing a variant claims reaches this file.</b> Every key here came off a <i>posting</i>;
/// the variant concepts were consumed upstream to compute the difference and are not an argument
/// to anything below. That is the mechanical half of the guard in the spec's section 1 - a
/// variant's concepts feed selection only - and it holds because there is no parameter through
/// which a document's own prose could get in.
/// </remarks>
/// <param name="PostingId">
/// The row, and the only reason this type carries one: two runs, or a caller that joined badly,
/// can offer the same posting twice, and a brief that counted it twice would rank a gap on how
/// often it was mentioned rather than on how many vacancies it blocks. It is deduplicated on the
/// way in and appears nowhere in the answer.
/// </param>
/// <param name="MissingConcepts">
/// The concept keys this posting required that no live variant covers. Order is irrelevant and
/// repeats are ignored.
/// </param>
public readonly record struct BlockedPosting(long PostingId, IReadOnlyCollection<string> MissingConcepts);

/// <summary>One concept a gap is made of, with the weight it carries inside that gap.</summary>
/// <remarks>
/// <b>The key and the label are both here because they are for different readers.</b> The key is
/// the identity a later query is written against - <c>skill.kubernetes</c> - and the label is
/// what goes in the sentence somebody reads. Rendering from the key would put a dotted slug in
/// front of the candidate; carrying only the label would make the row unjoinable the next time
/// anybody renames one, which the vocabulary treats as an edit rather than as a migration.
/// </remarks>
/// <param name="Key">The concept key, as <c>concepts.json</c> spells it.</param>
/// <param name="Label">The preferred name, from the same vocabulary. What a sentence says.</param>
/// <param name="Postings">
/// How many of the gap's own postings ask for this one. The first entry always matches
/// <see cref="CvGap.Postings"/>, because that concept is what the gap was built around.
/// </param>
public sealed record CvGapConcept(string Key, string Label, int Postings);

/// <summary>
/// One CV worth writing, named by the concepts it would have to speak to.
/// </summary>
/// <remarks>
/// <b>A gap is a cluster rather than a concept, because concepts do not arrive alone.</b>
/// Kubernetes and Terraform missing together across nine postings is one Saturday afternoon and
/// one document; reported as two rows it reads as two, and the second is worth nothing once the
/// first is written. So a gap is seeded by the concept blocking most postings and then named by
/// every other concept missing from a <i>majority</i> of that seed's postings.
///
/// <b>A majority rather than a tuned similarity, deliberately.</b> Nobody has measured what share
/// makes two concepts one CV, and a constant chosen from nothing is a constant nobody can argue
/// with afterwards. "Missing from more than half the postings this CV is aimed at" stands on its
/// own: write the document without that concept and most of the postings it was written for are
/// still blocked.
///
/// <b>What it cannot do, plainly.</b> It cannot find a cluster with no dominant member - three
/// concepts each missing from a third of the postings leave the seed standing alone, and the
/// brief then names one concept where it should have named three. And <see cref="Postings"/> is a
/// count of the <i>seed</i>, so it says what this CV is aimed at rather than promising what it
/// will unblock: a posting in the set that also asks for something outside the cluster stays
/// blocked, and a posting asking only for a cluster member the seed never appears with was never
/// counted. Both errors are visible on the next run - the brief is recomputed against what is
/// still blocked, so an overstated gap comes back smaller and an understated one comes back as
/// its own gap. The alternative was a count only a subset test could justify, which is smaller,
/// no more accurate once selection scores rather than subtracts, and impossible to explain to the
/// person it is being shown to.
/// </remarks>
/// <param name="Concepts">
/// What the CV has to cover, heaviest first. The seed leads, so a sentence that reads them in
/// order names the biggest gap first.
/// </param>
/// <param name="Postings">
/// Applyable postings this gap blocks, after the gaps ranked above it have taken theirs. The
/// business case, and the number in the spec's sentence.
/// </param>
public sealed record CvGap(IReadOnlyList<CvGapConcept> Concepts, int Postings);

/// <summary>
/// What to write next, and why - the whole of an abstention that says more than "no".
/// </summary>
/// <remarks>
/// <b>An abstention that says only "no" throws away the most useful signal in the system.</b>
/// Selection declines to send a CV that does not fit, which is right: the failure this replaces
/// is invisible, because an application sent on the wrong document simply never comes back. But
/// the pass that declined has just computed, for every posting it parked, exactly what would have
/// made it possible. Discarding that leaves the candidate with a queue of refusals and nothing to
/// do about them.
///
/// <b>Aggregate, and the shape is what enforces it.</b> Fifty "could not apply" notices is a
/// queue nobody reads; one ranked list of three gaps is a Saturday afternoon with an obvious
/// payoff. So there is no per-posting overload and no posting id in the answer - the brief is a
/// function of the whole blocked set - and <see cref="MinimumPostings"/> finishes the job: hand
/// <see cref="Compute"/> a single posting and every concept in it blocks exactly one, which is
/// below the floor, so the answer is a brief with no gaps in it. The per-posting version is not
/// discouraged, it is unbuildable.
///
/// <b>Pure, and free of every Azure type, for the reason <c>MatchScorer</c> and
/// <c>SubmissionState</c> are.</b> The ranking is the product here - it is what turns a refusal
/// into a work item with its business case attached - and a ranking asserted approximately is one
/// nobody notices going wrong. Counting is a group-by anybody could write; which gap comes second
/// once the first is written is the part with a decision in it.
///
/// <b>No sentence is built here.</b> The spec writes one - <i>"Eleven applyable postings are
/// waiting on a CV you have not written. They ask for Kubernetes, Terraform and platform
/// engineering; none of your variants covers them."</i> - and every part of it is a field on
/// <see cref="CvGap"/>. The prose is not, because the dashboard is TypeScript and the API is
/// JSON, so a formatted string in Core would be dead the moment either wanted a different one.
/// </remarks>
/// <param name="BlockedPostings">
/// Every applyable posting currently blocked for want of a CV, counted once each. The headline
/// for the standing view, and deliberately larger than the gaps below it add up to: the remainder
/// is the tail - gaps under the floor, gaps past the third, and postings whose requirements the
/// vocabulary cannot name - and none of the three is a work item, so none of it is broken out.
/// <b>A brief with postings here and nothing in <see cref="Gaps"/> is worth noticing</b>: it says
/// postings are being parked for want of a CV over requirements that are all tags, all domains or
/// all unknown keys, which is a selection or vocabulary fault rather than a document anybody can
/// write.
/// </param>
/// <param name="Gaps">The CVs to write, best first. At most <see cref="MaxGaps"/> of them.</param>
public sealed record CvGapBrief(int BlockedPostings, IReadOnlyList<CvGap> Gaps)
{
    /// <summary>How many blocked postings a gap needs before it is worth a CV.</summary>
    /// <remarks>
    /// <b>Two, and the argument is about what one <i>means</i> rather than about what one is
    /// worth.</b> A concept blocking a single posting is indistinguishable from one recruiter's
    /// vocabulary: it may be a real gap in a market this candidate is aiming at, or it may be the
    /// only advert in the corpus that will ever say COBOL. Two is the smallest number that says
    /// the word came up more than once, and a CV is a person's afternoon rather than a model's
    /// call, so the evidence for spending one should be at least that a second employer asked.
    ///
    /// <b>Not higher, because nothing has measured where higher would be.</b> A floor of five
    /// would be a number invented to look decisive, and it would quietly hide the gap that three
    /// good postings share. The ranking already does the work of putting the biggest first; this
    /// only keeps rows nobody can act on out of the list.
    ///
    /// It is also load-bearing for the shape of the feature. At a floor of one, a brief computed
    /// over a single posting would hand back that posting's concepts, and the fifty-notice queue
    /// this design exists to replace would be one loop away from being rebuilt.
    /// </remarks>
    public const int MinimumPostings = 2;

    /// <summary>How many gaps a brief will name.</summary>
    /// <remarks>
    /// <b>A constant rather than a parameter, and that is the point of it.</b>
    /// <c>SkillGapAnalysis.Compute</c> takes a limit because it feeds a browsable page of facts
    /// about a market; this feeds a work queue for one person, and a caller free to ask for fifty
    /// would produce the fifty-notice queue the aggregation exists to prevent. Three is the spec's
    /// own number and it is the honest one: nobody writes four CVs on the strength of a list, and
    /// a fourth gap that survives is at the top of next month's brief anyway.
    /// </remarks>
    public const int MaxGaps = 3;

    /// <summary>
    /// Ranks the CVs worth writing over the postings currently blocked for want of one.
    /// </summary>
    /// <param name="blocked">
    /// Every applyable posting selection could not send a CV to, with what it asked for that no
    /// live variant covers. Archived variants are the caller's problem: they are out of selection
    /// by definition, so a concept only they cover is genuinely missing.
    /// </param>
    /// <param name="graph">
    /// The vocabulary, for the labels and for <see cref="Concept.IsDiscriminating"/>.
    /// </param>
    /// <remarks>
    /// <b>Greedy, and each gap is ranked on what the gaps above it left behind.</b> Ranking the
    /// concepts independently double-counts: if the same nine postings want Kubernetes and want
    /// Terraform, an independent ranking reports two gaps of nine and reads as eighteen postings
    /// of payoff where there are nine. So a seed's postings leave the pool the moment its gap is
    /// written down, and the second gap answers the question the candidate is actually asking -
    /// <i>and then what</i> - instead of repeating the first in different words.
    ///
    /// <b>Two kinds of concept are dropped before any of that runs, for different reasons.</b> A
    /// concept that cannot discriminate goes because "they want agile" is not a CV anybody can
    /// write - the same judgement <c>MatchScorer</c>'s concept floor reads, from the same flag, so
    /// the two cannot drift - and because a tag carried by every advert in the corpus would
    /// outrank every real gap on volume alone, exactly as a domain does in the skills gap. A key
    /// the vocabulary does not carry goes because it can be neither labelled nor judged, and
    /// admitting it puts an unspellable slug at the top of a sentence somebody reads. Neither
    /// disappears silently: the posting stays counted in <see cref="BlockedPostings"/>, so a brief
    /// whose gaps do not account for its own total is saying so.
    /// </remarks>
    public static CvGapBrief Compute(IEnumerable<BlockedPosting> blocked, ConceptGraph graph)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(graph);

        var byPosting = new Dictionary<long, HashSet<string>>();

        foreach (var posting in blocked)
        {
            // First mention wins. A second row for the same posting is a duplicate rather than a
            // correction - nothing here can tell which of two disagreeing sets is the newer - and
            // the outcome that matters is only that the posting is counted once.
            if (!byPosting.ContainsKey(posting.PostingId))
            {
                byPosting[posting.PostingId] = Nameable(posting.MissingConcepts, graph);
            }
        }

        // A posting with nothing nameable behind it is still blocked, so it stays in the total; it
        // leaves the ranking because it can never contribute a concept to one.
        var remaining = byPosting.Values.Where(missing => missing.Count > 0).ToList();

        var gaps = new List<CvGap>();

        while (gaps.Count < MaxGaps)
        {
            var seed = Seed(remaining);

            if (seed is null)
            {
                break;
            }

            var aimedAt = remaining.Where(missing => missing.Contains(seed)).ToList();

            gaps.Add(new CvGap(Cluster(aimedAt, graph), aimedAt.Count));

            remaining.RemoveAll(missing => missing.Contains(seed));
        }

        return new CvGapBrief(byPosting.Count, gaps);
    }

    /// <summary>The concept blocking most of what is left, or null once nothing clears the floor.</summary>
    /// <remarks>
    /// <b>The tie-break is the concept key, and it is not cosmetic.</b> Two concepts blocking the
    /// same number of postings decide which cluster gets built and therefore which postings leave
    /// the pool, so a tie settled by whatever order a dictionary happened to enumerate in would
    /// hand the candidate a different top gap on two runs over identical data - and a work queue
    /// that reorders itself is one nobody trusts. Ordinal on the key, like every other tie-break
    /// in this codebase.
    ///
    /// The floor is applied here rather than after the ranking, which is what makes the loop
    /// terminate: a round that emits a gap always removes at least
    /// <see cref="MinimumPostings"/> postings from the pool.
    /// </remarks>
    private static string? Seed(List<HashSet<string>> remaining)
    {
        string? seed = null;
        var best = 0;

        foreach (var (key, blocking) in Demand(remaining))
        {
            if (blocking < MinimumPostings)
            {
                continue;
            }

            if (blocking > best || (blocking == best && string.CompareOrdinal(key, seed) < 0))
            {
                seed = key;
                best = blocking;
            }
        }

        return seed;
    }

    /// <summary>The concepts one CV has to cover, heaviest first.</summary>
    /// <remarks>
    /// The seed is necessarily first: it is missing from every posting in
    /// <paramref name="aimedAt"/> by construction, so nothing can outcount it, and a tie is broken
    /// by the same ordinal comparison that chose it.
    /// </remarks>
    private static IReadOnlyList<CvGapConcept> Cluster(List<HashSet<string>> aimedAt, ConceptGraph graph)
        => [.. Demand(aimedAt)
            .Where(entry => entry.Value * 2 > aimedAt.Count)
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new CvGapConcept(entry.Key, Label(entry.Key, graph), entry.Value))];

    /// <summary>How many of these postings each concept blocks.</summary>
    private static Dictionary<string, int> Demand(IEnumerable<HashSet<string>> postings)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var missing in postings)
        {
            foreach (var key in missing)
            {
                counts[key] = counts.TryGetValue(key, out var blocking) ? blocking + 1 : 1;
            }
        }

        return counts;
    }

    /// <summary>The concepts from one posting that a brief may name, as a set.</summary>
    /// <remarks>
    /// A set rather than the caller's collection, so a key repeated inside one posting's
    /// requirements counts once - otherwise a posting naming Kubernetes in its title and again in
    /// its body would weigh twice as much as the posting beside it that said it once.
    ///
    /// Null is read as empty rather than refused. <c>default(BlockedPosting)</c> produces one, and
    /// it describes a posting with nothing to say - precisely the state a posting parked over
    /// unnameable requirements is already in, so there is no third answer available. Throwing
    /// would take down a brief over fifty postings because of one bad row in the list.
    /// </remarks>
    private static HashSet<string> Nameable(IReadOnlyCollection<string>? missing, ConceptGraph graph)
    {
        var nameable = new HashSet<string>(StringComparer.Ordinal);

        if (missing is null)
        {
            return nameable;
        }

        foreach (var raw in missing)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (graph.TryGet(raw.Trim(), out var concept) && concept.IsDiscriminating)
            {
                nameable.Add(concept.Key);
            }
        }

        return nameable;
    }

    /// <summary>The preferred name, with a fallback it cannot actually reach.</summary>
    /// <remarks>
    /// <see cref="Nameable"/> has already refused every key the graph does not carry, so the
    /// fallback is dead code; it is written rather than a <c>!</c> because the alternative to an
    /// unreachable branch here is a null reference inside a sentence somebody is shown.
    /// </remarks>
    private static string Label(string key, ConceptGraph graph)
        => graph.TryGet(key, out var concept) ? concept.Label : key;
}
