using JobPlatform.Ingestion;
using Xunit;

namespace JobPlatform.Ingestion.Tests;

/// <summary>
/// The merge that splits the nightly model budget between the shortlist and the sample.
/// </summary>
/// <remarks>
/// Pinned exactly, like <c>BoundedWalkTests</c>, and for the same reason: this is an interleave
/// with a deduplication in it, every failure mode is quiet, and each one costs something specific.
/// Over-taking spends money that was not budgeted. Under-taking wastes a slot. Losing the round
/// robin spends the whole remainder on the lowest band and never reaches the highest, which
/// silently reproduces the range restriction the split exists to remove. Assessing a duplicate
/// pays twice for one answer.
/// </remarks>
public sealed class StratifiedShortlistTests
{
    private static IReadOnlyList<long> Combine(
        IReadOnlyList<long> topDown, IReadOnlyList<IReadOnlyList<long>> bands, int limit)
        => StratifiedShortlist.Combine(topDown, bands, limit, x => x);

    [Fact]
    public void The_shortlist_comes_first_and_whole()
    {
        // The half a person actually reads. It is not interleaved with the sample and it is not
        // trimmed to make room - the caller has already subtracted the sample's share.
        var combined = Combine([1, 2, 3], [[10, 11], [20, 21]], limit: 7);

        Assert.Equal([1, 2, 3, 10, 20, 11, 21], combined);
    }

    [Fact]
    public void Bands_are_taken_one_at_a_time_rather_than_one_after_another()
    {
        // The property that matters most. Concatenating would spend a remainder of two entirely
        // on the first band and never reach the last, which is the range restriction this whole
        // mechanism exists to remove - and it would look exactly like working code.
        var combined = Combine([], [[10, 11, 12], [20, 21, 22], [30, 31, 32]], limit: 4);

        Assert.Equal([10, 20, 30, 11], combined);
    }

    [Fact]
    public void A_short_budget_still_spans_every_band()
    {
        var combined = Combine([1], [[10, 11], [20, 21], [30, 31], [40, 41]], limit: 5);

        Assert.Equal([1, 10, 20, 30, 40], combined);
    }

    [Fact]
    public void An_empty_band_does_not_cost_the_others_their_turn()
    {
        // A band can legitimately return nothing: exhausted, or every row in it lacks a
        // description. The rest of the sample must still fill the budget.
        var combined = Combine([], [[10, 11], [], [30, 31]], limit: 4);

        Assert.Equal([10, 30, 11, 31], combined);
    }

    [Fact]
    public void The_shortlist_wins_a_collision_and_the_band_does_not_lose_its_turn()
    {
        // A posting can appear in both: the bands are drawn by posting id and the shortlist by
        // score, so a high-scoring row can be the first id in its band. Assessing it twice would
        // pay twice for one answer, so the shortlist keeps it.
        //
        // The first band's opening row is the duplicate. It must still contribute 11 in this
        // round rather than forfeiting the slot - collisions land disproportionately on the top
        // band, because that is the one the shortlist is drawn from, so forfeiting would
        // systematically under-sample the band closest to where the ranking acts.
        var combined = Combine([1, 2], [[2, 11], [20]], limit: 5);

        Assert.Equal([1, 2, 11, 20], combined);
    }

    [Fact]
    public void A_posting_in_two_bands_is_taken_once()
    {
        // The second band's turn comes up with its first row already taken, so it advances to 21
        // in the same round. Bands overlap rarely - they are disjoint score ranges - but a
        // re-score between the two queries can move a posting across a boundary.
        var combined = Combine([], [[10, 11], [10, 21]], limit: 4);

        Assert.Equal([10, 21, 11], combined);
    }

    [Fact]
    public void The_limit_is_never_exceeded()
    {
        // It is a budget, and going over it spends money nobody approved.
        Assert.Equal(3, Combine([1, 2, 3, 4, 5], [[10], [20]], limit: 3).Count);
        Assert.Equal(2, Combine([], [[10, 11, 12], [20, 21]], limit: 2).Count);
    }

    [Fact]
    public void A_duplicate_inside_the_shortlist_is_still_only_taken_once()
    {
        var combined = Combine([1, 1, 2], [], limit: 5);

        Assert.Equal([1, 2], combined);
    }

    [Fact]
    public void No_bands_leaves_the_shortlist_exactly_as_it_was()
    {
        // The degraded path and the band-bounded HTTP route both land here. It has to be an
        // identity, or drawing a sample by hand would quietly return something else.
        Assert.Equal([1, 2, 3], Combine([1, 2, 3], [], limit: 10));
    }

    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        Assert.Empty(Combine([], [], limit: 10));
        Assert.Empty(Combine([1, 2], [[10]], limit: 0));
        Assert.Empty(Combine([1, 2], [[10]], limit: -1));
    }

    [Fact]
    public void Uneven_bands_drain_without_losing_the_rotation()
    {
        // One deep band and two shallow ones. The deep one must not monopolise the early slots,
        // and must still be drawn from once the others are empty.
        var combined = Combine([], [[10, 11, 12, 13], [20], [30, 31]], limit: 7);

        Assert.Equal([10, 20, 30, 11, 31, 12, 13], combined);
    }
}
