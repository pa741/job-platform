using System.Reflection;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The brief, against the real vocabulary and a queue shaped like the live one.
/// </summary>
/// <remarks>
/// Counting is a group-by and would pass any assertion. What is pinned here is everything the
/// remarks on <see cref="CvGapBrief"/> claim and nothing else would catch: that co-occurring
/// concepts come back as one CV rather than three, that a gap is ranked on the postings the gaps
/// above it left behind, that a tag no candidate can write a CV about never reaches the list, and
/// that the per-posting version of this view cannot be built out of the aggregate one.
///
/// The fixture is deliberately a realistic shape rather than a minimal one - fourteen blocked
/// postings across three genuine clusters, a one-off, and two kinds of noise on almost every row -
/// because the failure this design is guarding against is a brief that is technically correct and
/// useless to read.
/// </remarks>
public sealed class CvGapBriefTests
{
    private static readonly ConceptGraph Graph = ConceptGraph.Default;

    private const string Kubernetes = "skill.kubernetes";
    private const string Terraform = "skill.terraform";
    private const string Helm = "skill.helm";
    private const string Spark = "skill.apache-spark";
    private const string Airflow = "skill.apache-airflow";
    private const string Databricks = "skill.databricks";
    private const string React = "skill.react";
    private const string TypeScript = "skill.typescript";
    private const string Cobol = "skill.cobol";

    /// <summary>A tag every advert carries. Real, and useless as a brief.</summary>
    private const string Agile = "skill.agile";

    /// <summary>A domain, which nothing is ever tagged with directly.</summary>
    private const string Backend = "area.backend";

    /// <summary>
    /// Fourteen postings blocked for want of a CV: a platform cluster of six, a data cluster of
    /// five, a front-end pair, and one advert in COBOL. Most rows also carry the two kinds of
    /// concept a brief must never name.
    /// </summary>
    private static List<BlockedPosting> LiveShapedQueue() =>
    [
        new(1, [Kubernetes, Terraform, Helm, Agile, Backend]),
        new(2, [Kubernetes, Terraform, Helm, Agile, Backend]),
        new(3, [Kubernetes, Terraform, Helm, Agile]),
        new(4, [Kubernetes, Terraform, Helm, Backend]),
        new(5, [Kubernetes, Terraform, Helm]),
        new(6, [Kubernetes, Terraform, Agile]),

        new(7, [Spark, Airflow, Agile]),
        new(8, [Spark, Airflow, Agile]),
        new(9, [Spark, Airflow, Backend]),
        new(10, [Spark, Airflow]),
        new(11, [Spark, Databricks]),

        new(12, [React, TypeScript, Agile]),
        new(13, [React, TypeScript]),

        new(14, [Cobol]),
    ];

    private static CvGapBrief Brief(IEnumerable<BlockedPosting> blocked)
        => CvGapBrief.Compute(blocked, Graph);

    private static IEnumerable<string> Keys(CvGap gap) => gap.Concepts.Select(c => c.Key);

    private static IEnumerable<string> EveryKey(CvGapBrief brief) => brief.Gaps.SelectMany(Keys);

    [Fact]
    public void Compute_ranks_the_gaps_by_how_many_postings_each_would_unblock()
    {
        // The ranking is the product. A refusal becomes a work item only because the list says
        // which item is worth an afternoon and which is worth next month.
        var brief = Brief(LiveShapedQueue());

        Assert.Equal(14, brief.BlockedPostings);
        Assert.Equal([6, 5, 2], brief.Gaps.Select(g => g.Postings));
    }

    [Fact]
    public void Concepts_that_travel_together_are_one_gap_rather_than_three()
    {
        // Kubernetes, Terraform and Helm are missing from the same six postings. Listed
        // independently that reads as three CVs worth seventeen postings between them; it is one
        // CV worth six, and the second and third would be worth nothing once the first was
        // written.
        var brief = Brief(LiveShapedQueue());

        Assert.Equal([Kubernetes, Terraform, Helm], Keys(brief.Gaps[0]));
        Assert.Equal([Spark, Airflow], Keys(brief.Gaps[1]));
        Assert.Equal([React, TypeScript], Keys(brief.Gaps[2]));
    }

    [Fact]
    public void The_seed_leads_the_gap_so_a_sentence_names_the_biggest_miss_first()
    {
        // "They ask for Kubernetes, Terraform and Helm" has to lead on the concept that actually
        // blocks the six, or the sentence buries its own subject.
        var gap = Brief(LiveShapedQueue()).Gaps[0];

        Assert.Equal(Kubernetes, gap.Concepts[0].Key);
        Assert.Equal(gap.Postings, gap.Concepts[0].Postings);
        Assert.Equal([6, 6, 5], gap.Concepts.Select(c => c.Postings));
    }

    [Fact]
    public void Labels_come_from_the_vocabulary_so_the_brief_reads_as_prose()
    {
        // The key is what a later query joins on; the label is what the candidate is shown. A
        // sentence rendered from keys says "skill.kubernetes" to a person.
        var gap = Brief(LiveShapedQueue()).Gaps[0];

        Assert.Equal(["Kubernetes", "Terraform", "Helm"], gap.Concepts.Select(c => c.Label));
    }

    [Fact]
    public void A_concept_travelling_with_only_a_minority_is_left_out_of_the_gap()
    {
        // Databricks is missing from one of the data cluster's five postings. Naming it would put
        // a technology in the brief that four fifths of the postings never asked for, which is how
        // a cluster grows into a CV nobody can write.
        var brief = Brief(LiveShapedQueue());

        Assert.DoesNotContain(Databricks, EveryKey(brief));
    }

    [Fact]
    public void A_non_discriminating_concept_never_reaches_a_brief()
    {
        // "They want agile" is not a CV anybody can write, and area.backend is a domain nothing is
        // ever tagged with directly. Both would outrank every real gap on volume alone - agile
        // blocks seven of the fourteen postings here, more than any genuine cluster.
        var brief = Brief(LiveShapedQueue());

        Assert.DoesNotContain(Agile, EveryKey(brief));
        Assert.DoesNotContain(Backend, EveryKey(brief));
    }

    [Fact]
    public void A_gap_blocking_one_posting_is_below_the_floor()
    {
        // One advert saying COBOL is one recruiter's vocabulary, not a market. A CV is an
        // afternoon, so the evidence for spending one starts at a second employer asking.
        var brief = Brief(LiveShapedQueue());

        Assert.DoesNotContain(Cobol, EveryKey(brief));
        Assert.Equal(2, CvGapBrief.MinimumPostings);
    }

    [Fact]
    public void A_brief_over_one_posting_names_no_gaps_at_all()
    {
        // The property that makes "aggregate, never per posting" structural rather than advisory:
        // every concept on a single posting blocks exactly one, which is under the floor. Fifty
        // "could not apply" notices cannot be built out of this function.
        var brief = Brief([new BlockedPosting(1, [Kubernetes, Terraform, Helm])]);

        Assert.Equal(1, brief.BlockedPostings);
        Assert.Empty(brief.Gaps);
    }

    [Fact]
    public void A_gap_is_ranked_on_the_postings_the_gaps_above_it_left_behind()
    {
        // Terraform is missing from seven postings, but four of them are already inside the
        // Kubernetes CV. Ranking the concepts independently would report a nine and a seven and
        // read as sixteen postings of payoff against a queue of twelve.
        List<BlockedPosting> blocked =
        [
            new(1, [Kubernetes]), new(2, [Kubernetes]), new(3, [Kubernetes]),
            new(4, [Kubernetes]), new(5, [Kubernetes]),
            new(6, [Kubernetes, Terraform]), new(7, [Kubernetes, Terraform]),
            new(8, [Kubernetes, Terraform]), new(9, [Kubernetes, Terraform]),
            new(10, [Terraform]), new(11, [Terraform]), new(12, [Terraform]),
        ];

        var brief = Brief(blocked);

        // Terraform is missing from only four of the first gap's nine postings, a minority, so it
        // is not part of that CV either - which is what leaves it standing as its own gap.
        Assert.Equal([Kubernetes], Keys(brief.Gaps[0]));
        Assert.Equal(9, brief.Gaps[0].Postings);

        Assert.Equal([Terraform], Keys(brief.Gaps[1]));
        Assert.Equal(3, brief.Gaps[1].Postings);
    }

    [Fact]
    public void No_more_than_three_gaps_are_named()
    {
        // One ranked list of three gaps is a Saturday afternoon's work. A fourth that still
        // matters is at the top of the next brief, because the brief is recomputed against what
        // is still blocked.
        List<BlockedPosting> blocked =
        [
            new(1, [Kubernetes]), new(2, [Kubernetes]), new(3, [Kubernetes]),
            new(4, [Kubernetes]), new(5, [Kubernetes]),
            new(6, [Spark]), new(7, [Spark]), new(8, [Spark]), new(9, [Spark]),
            new(10, [React]), new(11, [React]), new(12, [React]),
            new(13, [Cobol]), new(14, [Cobol]),
        ];

        var brief = Brief(blocked);

        Assert.Equal(3, CvGapBrief.MaxGaps);
        Assert.Equal([Kubernetes, Spark, React], EveryKey(brief));
        Assert.Equal(14, brief.BlockedPostings);

        // Two postings still waiting, and the total says so - the fourth gap clears the floor and
        // is left out by the cap alone.
        Assert.DoesNotContain(Cobol, EveryKey(brief));
    }

    [Fact]
    public void A_posting_offered_twice_is_counted_once()
    {
        // Two runs, or a caller that joined badly. A brief that counted the duplicate would rank
        // the gap on how often the posting was mentioned rather than on how many vacancies it
        // blocks - and here it would lift a one-posting gap over the floor it belongs under.
        var brief = Brief(
        [
            new BlockedPosting(1, [Kubernetes]),
            new BlockedPosting(1, [Kubernetes]),
        ]);

        Assert.Equal(1, brief.BlockedPostings);
        Assert.Empty(brief.Gaps);
    }

    [Fact]
    public void A_posting_blocked_by_nothing_the_vocabulary_can_name_is_still_counted_as_blocked()
    {
        // The state worth noticing: postings are being parked for want of a CV over requirements
        // that are all tags. That is a selection or vocabulary fault, not a document anybody can
        // write, and it is visible precisely because the total does not account for itself.
        var brief = Brief(
        [
            new BlockedPosting(1, [Agile, Backend]),
            new BlockedPosting(2, [Agile, Backend]),
            new BlockedPosting(3, [Agile]),
        ]);

        Assert.Equal(3, brief.BlockedPostings);
        Assert.Empty(brief.Gaps);
    }

    [Fact]
    public void An_unknown_concept_key_is_never_named()
    {
        // A key the vocabulary does not carry can be neither labelled nor judged against
        // IsDiscriminating, so admitting it would put an unspellable slug at the top of a sentence
        // somebody reads - on more postings than the real gap underneath it.
        var brief = Brief(
        [
            new BlockedPosting(1, ["skill.not-a-real-concept", Kubernetes]),
            new BlockedPosting(2, ["skill.not-a-real-concept", Kubernetes]),
            new BlockedPosting(3, ["skill.not-a-real-concept"]),
            new BlockedPosting(4, ["skill.not-a-real-concept"]),
            new BlockedPosting(5, ["skill.not-a-real-concept"]),
        ]);

        Assert.Equal(5, brief.BlockedPostings);
        Assert.Equal([Kubernetes], Keys(Assert.Single(brief.Gaps)));
    }

    [Fact]
    public void Repeats_and_whitespace_inside_one_postings_requirements_count_once()
    {
        // A posting naming Kubernetes in its title and again in its body must not weigh twice as
        // much as the posting beside it that said it once.
        var brief = Brief(
        [
            new BlockedPosting(1, [" skill.kubernetes ", Kubernetes, "  "]),
            new BlockedPosting(2, [Kubernetes]),
        ]);

        var gap = Assert.Single(brief.Gaps);
        var concept = Assert.Single(gap.Concepts);

        Assert.Equal(Kubernetes, concept.Key);
        Assert.Equal(2, concept.Postings);
        Assert.Equal(2, gap.Postings);
    }

    [Fact]
    public void The_brief_carries_counts_and_never_posting_ids()
    {
        // An equality rather than a superset, for the reason the MCP tool-surface test is one:
        // adding a posting id to any of these three would be the first step back to the
        // fifty-notice queue, and it should be a red build rather than a quiet diff.
        Assert.Equal(["BlockedPostings", "Gaps", "NameablePostings"], PropertyNames(typeof(CvGapBrief)));
        Assert.Equal(["Concepts", "Postings"], PropertyNames(typeof(CvGap)));
        Assert.Equal(["Key", "Label", "Postings"], PropertyNames(typeof(CvGapConcept)));

        // The input row is where an id is legitimate - it is what deduplication reads - and it is
        // the only one of the four that has one.
        Assert.Contains("PostingId", PropertyNames(typeof(BlockedPosting)));
    }

    [Fact]
    public void Two_briefs_over_the_same_blocked_set_agree()
    {
        // Ties are settled ordinally on the concept key, so nothing about the ranking depends on
        // the order rows came back in. A work queue that reorders itself between two runs over
        // identical data is one nobody trusts.
        var forwards = Brief(LiveShapedQueue());

        var backwards = Brief(Enumerable.Reverse(LiveShapedQueue()));

        Assert.Equal(
            forwards.Gaps.Select(g => $"{g.Postings}:{string.Join(',', Keys(g))}"),
            backwards.Gaps.Select(g => $"{g.Postings}:{string.Join(',', Keys(g))}"));
    }

    [Fact]
    public void A_brief_over_nothing_is_empty_rather_than_an_exception()
    {
        // Nothing blocked is the state this whole feature is trying to reach, so it is an ordinary
        // answer rather than an edge case.
        var brief = Brief([]);

        Assert.Equal(0, brief.BlockedPostings);
        Assert.Empty(brief.Gaps);
    }

    [Fact]
    public void A_posting_with_no_requirements_at_all_is_counted_and_does_not_throw()
    {
        // default(BlockedPosting) leaves the collection null, and it describes a posting with
        // nothing to say - the state a posting parked over unnameable requirements is already in.
        // Throwing would take down a brief over fourteen postings because of one bad row.
        var brief = Brief([default, new BlockedPosting(2, [Kubernetes]), new BlockedPosting(3, [Kubernetes])]);

        Assert.Equal(3, brief.BlockedPostings);
        Assert.Equal([Kubernetes], Keys(Assert.Single(brief.Gaps)));
    }

    private static IEnumerable<string> PropertyNames(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal);
}
