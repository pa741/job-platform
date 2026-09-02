using JobPlatform.Core.Dedup;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// Which of several listings of one job the queue hands over.
/// </summary>
/// <remarks>
/// The cases are the live corpus rather than invented shapes: the Cloudflare pair 3020/3030 is
/// the one that decides the tie-break order, and the Dex and Harnham pairs are why the existing
/// cross-board apply-link recovery could not be reused to find any of them.
/// </remarks>
public sealed class PostingClusterTests
{
    private const string CloudflareKey = "systems engineer|cloudflare|london";
    private const string DexKey = "data engineer|dex|london";
    private const string HarnhamKey = "analytics engineer|harnham|london";

    private static ClusterMember Member(
        long postingId,
        ApplyUrlSource applyUrlSource = ApplyUrlSource.BoardPosting,
        int? assessmentScore = null,
        double rankScore = 0,
        bool hasDocuments = false)
        => new(postingId, applyUrlSource, assessmentScore, rankScore, hasDocuments);

    [Fact]
    public void The_only_direct_apply_url_wins_over_the_better_assessment()
    {
        // The measurement the whole ordering rests on. In the live top-20 Cloudflare 3020 is the
        // only one of the pair carrying a direct ATS URL and is assessed at 85; its twin 3030
        // scores 92 and offers a board posting page. Preferring the score picks the row an agent
        // cannot apply through, which is why strength is first and the score is second.
        var direct = Member(3020, ApplyUrlSource.Posting, assessmentScore: 85);
        var betterJudged = Member(3030, ApplyUrlSource.BoardPosting, assessmentScore: 92);

        Assert.Equal(3020, PostingCluster.Choose([direct, betterJudged]).PostingId);
        Assert.Equal(3020, PostingCluster.Choose([betterJudged, direct]).PostingId);
    }

    [Fact]
    public void Ordering_by_the_enum_value_would_rank_an_inference_above_a_published_fact()
    {
        // MatchedOnAnotherBoard is 2 and Posting is 1, so the obvious comparison is inverted
        // precisely where it matters. A link the board published beats one matched by title,
        // employer and city, and this fails the moment somebody sorts on the value.
        var published = Member(100, ApplyUrlSource.Posting);
        var inferred = Member(101, ApplyUrlSource.MatchedOnAnotherBoard);

        Assert.Equal(100, PostingCluster.Choose([inferred, published]).PostingId);
    }

    [Fact]
    public void A_matched_link_still_beats_having_no_link_at_all()
    {
        var inferred = Member(200, ApplyUrlSource.MatchedOnAnotherBoard);
        var none = Member(199, ApplyUrlSource.BoardPosting);

        // 199 is the lower id and would win the final tie-break, so this is not that rule firing.
        Assert.Equal(200, PostingCluster.Choose([none, inferred]).PostingId);
    }

    [Fact]
    public void The_assessment_decides_between_rows_an_agent_can_apply_through_equally()
    {
        var weaker = Member(10, ApplyUrlSource.Posting, assessmentScore: 71);
        var stronger = Member(11, ApplyUrlSource.Posting, assessmentScore: 92);

        Assert.Equal(11, PostingCluster.Choose([weaker, stronger]).PostingId);
    }

    [Fact]
    public void A_row_nothing_has_judged_loses_to_one_that_has()
    {
        // Null is "less is known about this row", not "this row scored badly" - and both members
        // are the same job, so an assessment of zero is still a judgement and still outranks the
        // absence of one.
        var unjudged = Member(10, ApplyUrlSource.Posting);
        var judgedBadly = Member(11, ApplyUrlSource.Posting, assessmentScore: 0);

        Assert.Equal(11, PostingCluster.Choose([unjudged, judgedBadly]).PostingId);
    }

    [Fact]
    public void The_rank_score_decides_when_neither_row_has_been_assessed()
    {
        var lower = Member(10, ApplyUrlSource.Posting, rankScore: 61.5);
        var higher = Member(11, ApplyUrlSource.Posting, rankScore: 88.25);

        Assert.Equal(11, PostingCluster.Choose([lower, higher]).PostingId);
    }

    [Fact]
    public void Generated_documents_never_decide_which_row_is_primary()
    {
        // Documents are written for whichever row was primary last time, so letting them choose
        // makes the choice justify itself: the first member to receive them would stay primary
        // even after a sibling turned up with the employer's own apply URL.
        var plain = Member(10, ApplyUrlSource.Posting, assessmentScore: 80);
        var withDocuments = Member(11, ApplyUrlSource.Posting, assessmentScore: 80, hasDocuments: true);

        Assert.Equal(10, PostingCluster.Choose([withDocuments, plain]).PostingId);
    }

    [Fact]
    public void The_lowest_posting_id_settles_two_rows_that_are_otherwise_identical()
    {
        // Harnham 968 and 379, both on LinkedIn and alike on every axis above. The lowest id is
        // the row seen first and therefore the one anything earlier is already attached to.
        var later = Member(968, ApplyUrlSource.Posting, assessmentScore: 88, rankScore: 74);
        var earlier = Member(379, ApplyUrlSource.Posting, assessmentScore: 88, rankScore: 74);

        Assert.Equal(379, PostingCluster.Choose([later, earlier]).PostingId);
        Assert.Equal(379, PostingCluster.Choose([earlier, later]).PostingId);
    }

    [Fact]
    public void Two_listings_of_one_job_on_one_board_are_still_one_cluster()
    {
        // Dex 551 and 4961 are both LinkedIn. The cross-board recovery requires Site != Site and
        // would return nothing here; a ClusterMember has no field naming a site, so the
        // restriction cannot come back by accident.
        var cluster = PostingCluster.From(DexKey,
        [
            Member(551, ApplyUrlSource.BoardPosting, assessmentScore: 90),
            Member(4961, ApplyUrlSource.Posting, assessmentScore: 78),
        ]);

        Assert.Equal(4961, cluster.Primary.PostingId);
        Assert.Equal(new long[] { 551 }, cluster.AlternatePostings.Select(member => member.PostingId).ToArray());
    }

    [Fact]
    public void A_disagreement_about_the_city_cannot_split_a_cluster()
    {
        // Cloudflare 3020 is filed under "London" and 3030 under "Greater London". The recovery
        // query requires an exactly equal LocationCity and so rejects the pair; nothing here can
        // ask, because membership was settled upstream on the shared CrossBoardKey.
        var cluster = PostingCluster.From(CloudflareKey,
        [
            Member(3030, ApplyUrlSource.BoardPosting, assessmentScore: 92),
            Member(3020, ApplyUrlSource.Posting, assessmentScore: 85),
        ]);

        Assert.Equal(3020, cluster.Primary.PostingId);
        Assert.Equal(new long[] { 3030 }, cluster.AlternatePostings.Select(member => member.PostingId).ToArray());
        Assert.Equal(CloudflareKey, cluster.DedupeKey);
    }

    [Fact]
    public void Alternates_are_ordered_best_first_and_exclude_the_primary()
    {
        // The same comparison that chose the primary, so the head of this list is what Choose
        // would return if the primary were withdrawn.
        var cluster = PostingCluster.From(HarnhamKey,
        [
            Member(4, ApplyUrlSource.BoardPosting, assessmentScore: 99),
            Member(1, ApplyUrlSource.Posting, assessmentScore: 60),
            Member(3, ApplyUrlSource.MatchedOnAnotherBoard, assessmentScore: 70),
            Member(2, ApplyUrlSource.BoardPosting, assessmentScore: 99, rankScore: 50),
        ]);

        Assert.Equal(1, cluster.Primary.PostingId);
        Assert.Equal(new long[] { 3, 2, 4 }, cluster.AlternatePostings.Select(member => member.PostingId).ToArray());

        var withdrawn = cluster.AlternatePostings;
        Assert.Equal(withdrawn[0].PostingId, PostingCluster.Choose(withdrawn).PostingId);
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_the_group_arrived_in()
    {
        // The primary is the cluster's identity in the queue - the alternates hang off it and a
        // submission against any member suppresses the whole cluster - so a primary that moved
        // between two reads of the same key would hand a client a different row each time.
        ClusterMember[] members =
        [
            Member(4, ApplyUrlSource.BoardPosting, assessmentScore: 99),
            Member(1, ApplyUrlSource.Posting, assessmentScore: 60),
            Member(3, ApplyUrlSource.MatchedOnAnotherBoard, assessmentScore: 70),
            Member(2, ApplyUrlSource.BoardPosting, assessmentScore: 99, rankScore: 50),
        ];

        var forwards = PostingCluster.From(HarnhamKey, members);
        var backwards = PostingCluster.From(HarnhamKey, [.. members.Reverse()]);

        Assert.Equal(forwards.Primary, backwards.Primary);
        Assert.Equal(forwards.AlternatePostings, backwards.AlternatePostings);
    }

    [Fact]
    public void A_cluster_of_one_is_that_posting_and_no_alternates()
    {
        // Most of the corpus. The caller should get the same shape back rather than a special
        // case it has to remember to handle.
        var cluster = PostingCluster.From(DexKey, [Member(551, ApplyUrlSource.Posting)]);

        Assert.Equal(551, cluster.Primary.PostingId);
        Assert.Empty(cluster.AlternatePostings);
    }

    [Fact]
    public void One_posting_listed_twice_in_a_group_is_still_one_posting()
    {
        // Alternates are excluded by posting id rather than by value, so a row that arrived twice
        // does not become its own alternate and get offered to an agent as a second option.
        var cluster = PostingCluster.From(DexKey,
        [
            Member(551, ApplyUrlSource.Posting, assessmentScore: 80),
            Member(551, ApplyUrlSource.BoardPosting, assessmentScore: 80),
        ]);

        Assert.Equal(551, cluster.Primary.PostingId);
        Assert.Empty(cluster.AlternatePostings);
    }

    [Fact]
    public void A_cluster_with_no_members_is_a_caller_bug_rather_than_an_empty_answer()
    {
        // Grouping never produces an empty group, so reaching this means the caller built the
        // list by hand and got it wrong. There is no row to return and no honest default.
        Assert.Throws<ArgumentException>(() => PostingCluster.Choose([]));
        Assert.Throws<ArgumentException>(() => PostingCluster.From(DexKey, []));
    }

    [Fact]
    public void A_posting_with_no_cross_board_identity_has_no_cluster_to_join()
    {
        // CrossBoardKey answers null where the city or the employer is unknown, and that means
        // "no identity" rather than "the empty one". Letting the nulls group would merge every
        // unlocated posting in the corpus into a single cluster.
        var member = Member(551, ApplyUrlSource.Posting);

        Assert.Throws<ArgumentNullException>(() => PostingCluster.From(null!, [member]));
        Assert.Throws<ArgumentException>(() => PostingCluster.From("   ", [member]));
    }
}
