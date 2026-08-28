namespace JobPlatform.Core.Matching;

/// <summary>One pair as the ranker reads it: what the arithmetic said, and how alike the text is.</summary>
/// <param name="Similarity">
/// Cosine of the profile against the advert, or null where either side has no current embedding.
/// </param>
public readonly record struct RankInput(long PostingId, int Score, double? Similarity);

/// <summary>Where one pair lands in the list.</summary>
/// <param name="RankScore">
/// The ordering key, 0-100. <b>Not a percentage and not a second opinion on the match</b> - see
/// <see cref="MatchRanker"/>. Comparable only within one profile's pool.
/// </param>
public readonly record struct RankedMatch(long PostingId, double RankScore, double? Similarity);

/// <summary>
/// Orders a profile's scored matches, using the embedding to break the ties the score cannot.
/// </summary>
/// <remarks>
/// <b>This exists because the deterministic score is a good filter and a bad final sort, and
/// both halves of that were measured.</b> Drawn across the whole corpus - a stratified 195
/// spanning scores 45 to 100 - the score correlates with the model's judgement at Spearman
/// +0.315, CI [+0.174, +0.443]. Restricted to its own top two bands, where a candidate actually
/// looks, it correlates at <b>-0.191</b>: the 90-100 band carries a higher share of Weak
/// verdicts (31%) than the two bands below it (20% and 17%). The score orders the corpus well
/// and then inverts at the very top, which is exactly the widely-held-skill problem the concept
/// floor could not reach - a Yardi consultant genuinely requires SQL, and a SQL-holding
/// candidate genuinely meets it.
///
/// The embedding fails and succeeds in the opposite places. Across the corpus it is no better
/// than the score (+0.296, and the paired difference is not significant); inside the top two
/// bands it is +0.448 where the score is -0.191. So neither replaces the other and the
/// combination beats both: <b>+0.521 against +0.315</b>. Stated as something a person can hold:
/// of every pair of postings the model has a preference between, the score alone orders 61.3%
/// the right way round and this orders <b>68.5%</b>.
///
/// <b>It is a convex combination over pool-normalised inputs, not a product.</b> The product was
/// measured first and the score dominates it by scale; the fusion literature (Bruch, ACM TOIS
/// 2023) prefers the convex form and it does measure better here - though only by +0.045, CI
/// [-0.006, +0.098], which is not significant at this sample size. It is chosen for being the
/// form whose weight means something, not for the 0.045.
///
/// <b>Nothing here touches <see cref="MatchResult.Score"/>, deliberately.</b> Folding the
/// embedding into the score would clear every stored assessment - a score that moves is the
/// signal a judgement was made against different arithmetic - and would throw away the labels
/// this ranking was fitted on. It would also make the number mean two things at once. The score
/// still says how much of the posting the profile covers; this says where to look first.
/// </remarks>
public static class MatchRanker
{
    /// <summary>
    /// Bumped whenever the ranker would order the same pool differently.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MatchResult.CurrentVersion"/> because the two are stale for
    /// different reasons and re-deriving them costs different things: a scorer change needs the
    /// concept graph and the assertions, a ranker change needs nothing but the rows already
    /// stored. Sharing one constant would mean every tuning of the weight paid for a full
    /// re-score - and a re-score clears assessments, so it would also cost the labels.
    /// </remarks>
    public const int CurrentVersion = 1;

    /// <summary>
    /// What share of the ordering the embedding carries where both axes are present.
    /// </summary>
    /// <remarks>
    /// 0.6, and it is a measurement rather than a preference. Swept from 0.0 to 1.0 in tenths
    /// against the stratified 195, Spearman peaks at 0.6; a bootstrap over 2,000 resamples picks
    /// 0.6 as the median with 80% of draws in [0.6, 0.7]. The curve is flat enough either side
    /// that the exact value is not load-bearing, which is itself the useful finding at this
    /// sample size - so re-tuning it needs a bigger labelled set, not a better afternoon.
    /// </remarks>
    public const double SimilarityWeight = 0.6;

    /// <summary>
    /// The score at or above which the embedding is allowed to re-order.
    /// </summary>
    /// <remarks>
    /// <b>The bound on where the measurement applies, and it is not decoration.</b> The
    /// stratified sample was drawn from bands 45-59 upward, so nothing was ever labelled below
    /// 45 and the +0.521 says nothing about that range. Fusing globally would let a posting the
    /// scorer floored at zero - no readable requirements, or none that discriminate - climb the
    /// list on textual resemblance alone, which is precisely the failure the concept floor
    /// exists to prevent and which cost 44 of the top 60 matches once already.
    ///
    /// Below this the score orders on its own and nothing can climb past it: a fused pair never
    /// lands under the floor and an unfused one never reaches it. Deliberately the same number
    /// as the sweep's assessment threshold - the band the model is spent on and the band the
    /// embedding re-orders are the same band, and two constants that must agree are one
    /// constant.
    /// </remarks>
    public const int FusionFloor = 45;

    /// <summary>
    /// Decimal places the ordering key is rounded to.
    /// </summary>
    /// <remarks>
    /// Two, which is 5,500 distinct positions across the fused band - far more resolution than a
    /// pool of a few thousand pairs can use, and far less than a double would offer. The
    /// difference matters for what it costs to store rather than for what it orders: the
    /// normalisation moves every pair's key by a hair whenever a night's scrape widens the pool,
    /// so at full precision every row in the table would be rewritten every night whether or not
    /// anything about it changed. That is exactly the write the scoring pass already declines to
    /// make, on a database billed by wall-clock time.
    /// </remarks>
    private const int RankScorePrecision = 2;

    /// <summary>
    /// Orders one profile's pool, in place of ordering by score.
    /// </summary>
    /// <remarks>
    /// <b>Pool-normalised, so this is a whole-list operation rather than a per-pair one.</b>
    /// A cosine of 0.51 means nothing on its own: this model's similarities for a single profile
    /// occupy a band perhaps 0.15 wide, and where in that band a posting sits is the entire
    /// signal. Min-maxing over the eligible pool is what turns it into something combinable with
    /// a 0-100 score - and it is why the result is a ranking key rather than a score. The same
    /// pair in a different pool gets a different number.
    ///
    /// Returned in input order rather than sorted. The caller is writing rows, not rendering a
    /// page, and sorting here would only be undone by the ORDER BY that actually decides what a
    /// candidate sees.
    /// </remarks>
    public static IReadOnlyList<RankedMatch> Rank(IReadOnlyList<RankInput> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        if (pairs.Count == 0)
        {
            return [];
        }

        var scoreLo = int.MaxValue;
        var scoreHi = int.MinValue;
        var simLo = double.MaxValue;
        var simHi = double.MinValue;
        var haveSimilarity = false;

        foreach (var pair in pairs)
        {
            if (pair.Score < FusionFloor)
            {
                continue;
            }

            scoreLo = Math.Min(scoreLo, pair.Score);
            scoreHi = Math.Max(scoreHi, pair.Score);

            if (pair.Similarity is not { } similarity)
            {
                continue;
            }

            haveSimilarity = true;
            simLo = Math.Min(simLo, similarity);
            simHi = Math.Max(simHi, similarity);
        }

        // Not one embedded posting in the whole eligible pool - the pass has not run, or the
        // profile has no vector. Ordering by score is exactly what this system did before, so
        // that is what it degrades to, rather than to something novel nobody has measured.
        if (!haveSimilarity)
        {
            return [.. pairs.Select(p => new RankedMatch(p.PostingId, p.Score, p.Similarity))];
        }

        var scoreVaries = scoreHi > scoreLo;
        var similarityVaries = simHi > simLo;

        var ranked = new List<RankedMatch>(pairs.Count);

        foreach (var pair in pairs)
        {
            ranked.Add(new RankedMatch(pair.PostingId, RankOne(pair), pair.Similarity));
        }

        return ranked;

        double RankOne(RankInput pair)
        {
            if (pair.Score < FusionFloor)
            {
                return pair.Score;
            }

            // Silence drops an axis rather than failing it - the rule MatchScorer already runs
            // under, applied one layer later. A posting with no embedding yet is not a posting
            // the candidate resembles less, so scoring a missing vector as zero would bury every
            // advert the pass has not reached, which is a fact about the queue rather than about
            // the job. An axis that does not vary across the pool drops for the same reason: it
            // cannot separate anything, so letting it dilute the axis that can is pure loss.
            var weight = 0.0;
            var earned = 0.0;

            if (similarityVaries && pair.Similarity is { } similarity)
            {
                weight += SimilarityWeight;
                earned += SimilarityWeight * ((similarity - simLo) / (simHi - simLo));
            }

            if (scoreVaries)
            {
                weight += 1 - SimilarityWeight;
                earned += (1 - SimilarityWeight) * ((double)(pair.Score - scoreLo) / (scoreHi - scoreLo));
            }

            // Every eligible pair identical on both axes. Any constant orders them the same way,
            // and the top of the band is the one that does not misrepresent them as poor.
            var fused = weight <= 0 ? 1.0 : earned / weight;

            return Math.Round(
                FusionFloor + ((100 - FusionFloor) * fused),
                RankScorePrecision,
                MidpointRounding.AwayFromZero);
        }
    }
}
