using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The join, run backwards, against the real vocabulary.
/// </summary>
/// <remarks>
/// The counting half of the skills gap is a group-by. The half worth pinning is the graph
/// walk: it has to reach the same conclusion about a concept pair that the match breakdown
/// reaches, and it has to leave out the two kinds of row that would make the list useless -
/// concepts the candidate already holds, and domains nothing is ever tagged with directly.
/// </remarks>
public sealed class SkillGapAnalysisTests
{
    private static readonly ConceptGraph Graph = ConceptGraph.Default;

    private static IReadOnlyList<SkillGap> Compute(
        Dictionary<string, int> inBand, string[] held, int limit = 12)
        => SkillGapAnalysis.Compute(inBand, inBand, held, Graph, limit);

    [Fact]
    public void A_concept_the_profile_holds_is_not_a_gap()
    {
        var gaps = Compute(
            new Dictionary<string, int> { ["skill.csharp"] = 40, ["skill.java"] = 30 },
            ["skill.csharp"]);

        Assert.Equal(["skill.java"], gaps.Select(g => g.ConceptKey));
    }

    [Fact]
    public void A_domain_is_never_a_gap()
    {
        // Nothing is tagged with a domain directly - it is what the closure gives you when a
        // posting names a skill underneath it. "You lack Backend Development" is not something
        // anybody can act on, and its count is the sum of the real gaps so it would outrank
        // every one of them.
        var gaps = Compute(
            new Dictionary<string, int> { ["area.backend"] = 900, ["skill.java"] = 30 },
            []);

        Assert.Equal(["skill.java"], gaps.Select(g => g.ConceptKey));
    }

    [Fact]
    public void The_nearest_thing_the_profile_holds_is_named_with_its_relation()
    {
        // The whole point of the view. "You do not have Angular" is a fact; "you hold AngularJS,
        // which the graph records as Superseded" is the same fact with something to do about it.
        var gaps = Compute(new Dictionary<string, int> { ["skill.angular"] = 25 }, ["skill.angularjs"]);

        var gap = Assert.Single(gaps);
        Assert.Equal("skill.angularjs", gap.HeldKey);
        Assert.Equal(MatchRelation.Superseded, gap.Relation);
        Assert.True(gap.Credit > 0);
    }

    [Fact]
    public void A_related_edge_is_read_from_either_end()
    {
        // Related is curated as one edge and means a symmetric thing. Holding Flask against a
        // posting wanting Django has to answer the same as the other way round, or the gap list
        // contradicts the scorer depending on which concept happened to be written down first.
        var fromOneEnd = Compute(new Dictionary<string, int> { ["skill.django"] = 10 }, ["skill.flask"]);
        var fromTheOther = Compute(new Dictionary<string, int> { ["skill.flask"] = 10 }, ["skill.django"]);

        Assert.Equal(MatchRelation.Related, Assert.Single(fromOneEnd).Relation);
        Assert.Equal(MatchRelation.Related, Assert.Single(fromTheOther).Relation);
    }

    [Fact]
    public void An_implication_is_read_in_the_direction_it_was_curated()
    {
        // ASP.NET Core implies .NET, so holding it answers a posting asking for .NET.
        var implied = Compute(new Dictionary<string, int> { ["skill.dotnet"] = 15 }, ["skill.aspnet-core"]);
        Assert.Equal(MatchRelation.Implied, Assert.Single(implied).Relation);

        // Not backwards: holding .NET says nothing about ASP.NET Core, and a Generalisation is
        // the strongest honest claim available - it is broader than what was asked for.
        var reverse = Compute(new Dictionary<string, int> { ["skill.aspnet-core"] = 15 }, ["skill.dotnet"]);
        Assert.NotEqual(MatchRelation.Implied, Assert.Single(reverse).Relation);
    }

    [Fact]
    public void A_gap_with_nothing_behind_it_is_reported_with_no_relation()
    {
        // The most useful row on the page: no partial credit, nothing adjacent, nothing to
        // argue in a cover letter.
        var gaps = Compute(new Dictionary<string, int> { ["skill.kubernetes"] = 60 }, ["skill.django"]);

        var gap = Assert.Single(gaps);
        Assert.Null(gap.HeldKey);
        Assert.Null(gap.Relation);
        Assert.Equal(0, gap.Credit);
    }

    [Fact]
    public void Ranking_is_by_the_candidates_own_band_not_by_the_corpus()
    {
        // The corpus number is context. Ranking by it would put the language every advert
        // mentions at the top of a list that is supposed to be about this candidate.
        var gaps = SkillGapAnalysis.Compute(
            new Dictionary<string, int> { ["skill.kubernetes"] = 60, ["skill.java"] = 5 },
            new Dictionary<string, int> { ["skill.kubernetes"] = 700, ["skill.java"] = 9000 },
            [],
            Graph,
            12);

        Assert.Equal(["skill.kubernetes", "skill.java"], gaps.Select(g => g.ConceptKey));
        Assert.Equal(700, gaps[0].CorpusPostings);
    }

    [Fact]
    public void Ties_do_not_shuffle_between_requests()
    {
        var inBand = new Dictionary<string, int> { ["skill.java"] = 10, ["skill.kubernetes"] = 10 };

        Assert.Equal(
            Compute(inBand, []).Select(g => g.ConceptKey),
            Compute(inBand, []).Select(g => g.ConceptKey));
    }

    [Fact]
    public void The_limit_is_applied_after_ranking_not_before()
    {
        var gaps = Compute(
            new Dictionary<string, int> { ["skill.java"] = 5, ["skill.kubernetes"] = 60, ["skill.python"] = 30 },
            [],
            limit: 1);

        Assert.Equal(["skill.kubernetes"], gaps.Select(g => g.ConceptKey));
    }
}
