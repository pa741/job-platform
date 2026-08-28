using JobPlatform.Core.Matching;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The ordering rules, at exact numbers.
/// </summary>
/// <remarks>
/// Pure and Azure-free like <c>MatchScorerTests</c>, and asserted exactly for the same reason:
/// this is a rule about where a candidate's attention goes, and "roughly the right order" is not
/// a claim anything can be checked against.
///
/// The properties worth pinning are the ones that would fail silently. A missing embedding must
/// not bury a posting; a posting below the floor must not be re-ordered by text at all; the
/// weight must actually be 0.6; and the key must be stable enough that an unchanged night writes
/// no rows.
/// </remarks>
public sealed class MatchRankerTests
{
    private static RankInput Pair(long id, int score, double? similarity = null)
        => new(id, score, similarity);

    private static double RankOf(IReadOnlyList<RankedMatch> ranked, long id)
        => ranked.Single(r => r.PostingId == id).RankScore;

    private static IReadOnlyList<long> Order(IReadOnlyList<RankedMatch> ranked)
        => [.. ranked.OrderByDescending(r => r.RankScore).ThenBy(r => r.PostingId).Select(r => r.PostingId)];

    // -----------------------------------------------------------------------
    // The finding this exists for
    // -----------------------------------------------------------------------

    [Fact]
    public void Embedding_reorders_within_the_top_band()
    {
        // The measured failure, in miniature: the higher-scoring posting is the worse match, and
        // the embedding is the only thing that knows. 100 against 90 on the score; 0.40 against
        // 0.60 on the text.
        var ranked = MatchRanker.Rank([Pair(1, 100, 0.40), Pair(2, 90, 0.60)]);

        Assert.Equal([2, 1], Order(ranked));
    }

    [Fact]
    public void Score_still_decides_where_the_embedding_agrees()
    {
        var ranked = MatchRanker.Rank([Pair(1, 100, 0.60), Pair(2, 90, 0.40)]);

        Assert.Equal([1, 2], Order(ranked));
    }

    [Fact]
    public void Weight_is_six_to_four()
    {
        // Both axes span the pool, so the fused value is readable straight off the weights. The
        // posting that is bottom on score and top on similarity earns exactly the similarity
        // weight; the one that is top on score and bottom on similarity earns exactly the rest.
        var ranked = MatchRanker.Rank([Pair(1, 100, 0.0), Pair(2, 50, 1.0)]);

        Assert.Equal(45 + (55 * 0.4), RankOf(ranked, 1), precision: 2);
        Assert.Equal(45 + (55 * 0.6), RankOf(ranked, 2), precision: 2);
    }

    // -----------------------------------------------------------------------
    // The floor: where the measurement does not reach
    // -----------------------------------------------------------------------

    [Fact]
    public void Below_the_floor_the_score_orders_alone()
    {
        // Nothing below 45 was ever labelled, so nothing below 45 is re-ordered. The similarity
        // is present and deliberately contradicts the score.
        var ranked = MatchRanker.Rank([Pair(1, 44, 0.1), Pair(2, 30, 0.9), Pair(3, 90, 0.5)]);

        Assert.Equal(44, RankOf(ranked, 1));
        Assert.Equal(30, RankOf(ranked, 2));
    }

    [Fact]
    public void Nothing_below_the_floor_can_climb_above_it()
    {
        // The guarantee that keeps the concept floor's work intact: a posting the scorer refused
        // to credit cannot reach the list on textual resemblance alone.
        var ranked = MatchRanker.Rank(
        [
            Pair(1, 0, 0.99),
            Pair(2, 44, 0.99),
            Pair(3, 45, 0.01),
        ]);

        Assert.True(RankOf(ranked, 3) >= MatchRanker.FusionFloor);
        Assert.True(RankOf(ranked, 1) < MatchRanker.FusionFloor);
        Assert.True(RankOf(ranked, 2) < MatchRanker.FusionFloor);
        Assert.Equal([3, 2, 1], Order(ranked));
    }

    // -----------------------------------------------------------------------
    // Silence drops an axis rather than failing it
    // -----------------------------------------------------------------------

    [Fact]
    public void A_missing_embedding_is_not_a_similarity_of_zero()
    {
        // The posting with no vector has the same score as the one with the worst vector. If
        // absence were scored as zero it would rank below it; dropping the axis puts it above,
        // which is right - the pass has not reached it, and that is a fact about the queue.
        var ranked = MatchRanker.Rank(
        [
            Pair(1, 80, similarity: null),
            Pair(2, 80, 0.10),
            Pair(3, 100, 0.90),
            Pair(4, 60, 0.10),
        ]);

        Assert.True(RankOf(ranked, 1) > RankOf(ranked, 2));
    }

    [Fact]
    public void A_pair_with_no_embedding_ranks_on_its_score_alone()
    {
        // Renormalised onto the score axis, not diluted by a zero. Two postings with no vector
        // and different scores must keep the score's ordering, and the higher one must reach the
        // top of the band because it is the top of the score range.
        var ranked = MatchRanker.Rank(
        [
            Pair(1, 100, similarity: null),
            Pair(2, 50, similarity: null),
            Pair(3, 70, 0.5),
        ]);

        Assert.Equal(100, RankOf(ranked, 1));
        Assert.Equal(45, RankOf(ranked, 2));
    }

    [Fact]
    public void No_embedding_anywhere_leaves_the_score_untouched()
    {
        // The degraded mode: no provider, or the pass has never run. The keys must come out
        // equal to the scores, so the list is ordered exactly as it was before the ranker existed.
        var ranked = MatchRanker.Rank([Pair(1, 92), Pair(2, 47), Pair(3, 12)]);

        Assert.Equal(92, RankOf(ranked, 1));
        Assert.Equal(47, RankOf(ranked, 2));
        Assert.Equal(12, RankOf(ranked, 3));
        Assert.All(ranked, r => Assert.Null(r.Similarity));
    }

    [Fact]
    public void An_axis_that_does_not_vary_is_dropped()
    {
        // Every eligible pair has the same score, so the score cannot separate anything and
        // letting it dilute the axis that can is pure loss. The similarity must then order the
        // band outright, spanning it end to end.
        var ranked = MatchRanker.Rank([Pair(1, 80, 0.2), Pair(2, 80, 0.5), Pair(3, 80, 0.8)]);

        Assert.Equal(45, RankOf(ranked, 1));
        Assert.Equal(100, RankOf(ranked, 3));
        Assert.Equal([3, 2, 1], Order(ranked));
    }

    [Fact]
    public void Identical_pairs_all_land_at_the_top_of_the_band()
    {
        var ranked = MatchRanker.Rank([Pair(1, 80, 0.5), Pair(2, 80, 0.5)]);

        Assert.Equal(100, RankOf(ranked, 1));
        Assert.Equal(100, RankOf(ranked, 2));
    }

    // -----------------------------------------------------------------------
    // Shape of the result
    // -----------------------------------------------------------------------

    [Fact]
    public void The_key_is_rounded_so_an_unchanged_night_writes_nothing()
    {
        // Full precision would move every key by a hair whenever a scrape widened the pool, and
        // the repository's "nothing moved, skip the write" test would never pass again.
        var ranked = MatchRanker.Rank(
        [
            Pair(1, 100, 0.612_345_678),
            Pair(2, 45, 0.500_000_001),
            Pair(3, 73, 0.555_555_555),
        ]);

        Assert.All(ranked, r => Assert.Equal(r.RankScore, Math.Round(r.RankScore, 2)));
    }

    [Fact]
    public void Similarity_is_carried_through_untouched()
    {
        // Stored beside the key because it is the durable half: the same pair gives the same
        // cosine in any pool, which is what a re-tuning of the weight would be fitted against.
        var ranked = MatchRanker.Rank([Pair(1, 90, 0.4321), Pair(2, 60, null)]);

        Assert.Equal(0.4321, ranked.Single(r => r.PostingId == 1).Similarity);
        Assert.Null(ranked.Single(r => r.PostingId == 2).Similarity);
    }

    [Fact]
    public void Results_come_back_in_input_order()
    {
        var ranked = MatchRanker.Rank([Pair(7, 50, 0.1), Pair(3, 90, 0.9), Pair(5, 70, 0.5)]);

        Assert.Equal([7L, 3L, 5L], [.. ranked.Select(r => r.PostingId)]);
    }

    [Fact]
    public void An_empty_pool_ranks_to_nothing()
        => Assert.Empty(MatchRanker.Rank([]));

    [Fact]
    public void The_weight_and_the_floor_are_the_measured_ones()
    {
        // Pinned rather than merely used. Both are measurements - 0.6 is where the alpha sweep
        // peaks, 45 is the lowest band the stratified sample reached - so changing either is a
        // claim that needs new labels behind it, and a silent edit should fail here first.
        Assert.Equal(0.6, MatchRanker.SimilarityWeight);
        Assert.Equal(45, MatchRanker.FusionFloor);
    }
}
