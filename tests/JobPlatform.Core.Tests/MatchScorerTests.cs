using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The scoring rules, against the real concept graph.
/// </summary>
/// <remarks>
/// Against the shipped vocabulary rather than a fixture one, deliberately. The interesting
/// behaviour here is entirely about graph structure - a specialisation counting fully, a
/// generalisation decaying with distance, a curated implication beating both - and a synthetic
/// three-node graph would let those rules pass while being wrong about the vocabulary the
/// system actually runs on.
///
/// Pure and Azure-free, like <c>MetricsCalculatorTests</c>. That is what makes it possible to
/// assert exact numbers rather than ranges.
/// </remarks>
public sealed class MatchScorerTests
{
    private static CandidateFacts Candidate(params ConceptAssertion[] concepts)
        => new() { Concepts = concepts };

    private static PostingFacts Posting(params ConceptAssertion[] concepts)
        => new() { PostingId = 1, Concepts = concepts };

    private static ConceptAssertion Holds(
        string key, AssertionPolarity polarity = AssertionPolarity.Proficient, int? years = null)
        => new(key, AssertionSource.Board, polarity, years);

    private static ConceptAssertion Wants(
        string key, AssertionPolarity polarity = AssertionPolarity.Required, int? years = null)
        => new(key, AssertionSource.Model, polarity, years);

    // -----------------------------------------------------------------------
    // Concept matching through the graph
    // -----------------------------------------------------------------------

    [Fact]
    public void An_exact_match_scores_full_marks_and_says_so()
    {
        var result = MatchScorer.Score(
            Candidate(Holds("skill.kubernetes")),
            Posting(Wants("skill.kubernetes")));

        Assert.Equal(100, result.Score);

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchRelation.Exact, match.Relation);
        Assert.Equal(1.0, match.Credit);
        Assert.Empty(result.Gaps);
    }

    [Fact]
    public void Holding_nothing_they_asked_for_is_a_gap_rather_than_a_silent_zero()
    {
        var result = MatchScorer.Score(
            Candidate(Holds("skill.cobol")),
            Posting(Wants("skill.kubernetes")));

        Assert.Equal(0, result.Score);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal("skill.kubernetes", gap.RequiredKey);
        Assert.Equal(AssertionPolarity.Required, gap.Demand);
        Assert.Equal(1, result.RequiredGapCount);
    }

    [Fact]
    public void A_specialisation_counts_fully_because_the_specific_case_entails_the_general_one()
    {
        // They want Kubernetes. The candidate has EKS, which sits under it in the graph.
        // Running EKS is running Kubernetes, so nothing is deducted.
        var graph = ConceptGraph.Default;
        Assert.True(graph.Ancestors("skill.eks").ContainsKey("skill.kubernetes"));

        var result = MatchScorer.Score(
            Candidate(Holds("skill.eks")),
            Posting(Wants("skill.kubernetes")));

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchRelation.Specialisation, match.Relation);
        Assert.Equal(1.0, match.Credit);
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void A_generalisation_counts_partially_because_it_is_not_the_thing_asked_for()
    {
        // The mirror image: they want EKS, the candidate has Kubernetes. Real transferable
        // ground, and not the same claim - which is exactly what the relation records.
        var result = MatchScorer.Score(
            Candidate(Holds("skill.kubernetes")),
            Posting(Wants("skill.eks")));

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchRelation.Generalisation, match.Relation);
        Assert.InRange(match.Credit, 0.01, 0.99);
        Assert.InRange(result.Score, 1, 99);
    }

    [Fact]
    public void A_curated_implication_beats_any_distance_over_the_hierarchy()
    {
        // The vocabulary says Kubernetes implies containerisation. That edge exists because
        // somebody decided it does, which is better evidence than any structural metric.
        var graph = ConceptGraph.Default;
        Assert.True(graph.TryGet("skill.kubernetes", out var kubernetes));
        Assert.Contains("skill.containers", kubernetes.Implies, StringComparer.Ordinal);

        var result = MatchScorer.Score(
            Candidate(Holds("skill.kubernetes")),
            Posting(Wants("skill.containers")));

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchRelation.Implied, match.Relation);
        Assert.Equal(1.0, match.Credit);
    }

    [Fact]
    public void Succession_is_read_in_one_direction_only()
    {
        // Holding AngularJS is weak evidence for an Angular role. Holding Angular is no
        // evidence at all for an AngularJS one, and the direction is the whole reason the
        // vocabulary keeps succession separate from similarity.
        var graph = ConceptGraph.Default;

        if (!graph.TryGet("skill.angularjs", out var angularjs) || angularjs.SucceededBy is not { } successor)
        {
            return;
        }

        var forwards = MatchScorer.Score(
            Candidate(Holds("skill.angularjs")),
            Posting(Wants(successor)));

        Assert.Equal(MatchRelation.Superseded, Assert.Single(forwards.Matched).Relation);

        var backwards = MatchScorer.Score(
            Candidate(Holds(successor)),
            Posting(Wants("skill.angularjs")));

        Assert.Empty(backwards.Matched);
    }

    [Fact]
    public void The_strongest_available_claim_is_the_one_reported()
    {
        // The candidate holds both the exact concept and something broader. The exact one wins,
        // and the relation shown to them is the truest available rather than merely one that
        // scores the same.
        var result = MatchScorer.Score(
            Candidate(Holds("skill.eks"), Holds("skill.kubernetes")),
            Posting(Wants("skill.eks")));

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchRelation.Exact, match.Relation);
    }

    // -----------------------------------------------------------------------
    // Strength, years and duplication
    // -----------------------------------------------------------------------

    [Fact]
    public void Expert_does_not_beat_proficient_because_a_requirement_cannot_be_over_met()
    {
        var proficient = MatchScorer.Score(
            Candidate(Holds("skill.python", AssertionPolarity.Proficient)),
            Posting(Wants("skill.python")));

        var expert = MatchScorer.Score(
            Candidate(Holds("skill.python", AssertionPolarity.Expert)),
            Posting(Wants("skill.python")));

        Assert.Equal(proficient.Score, expert.Score);
    }

    [Fact]
    public void Familiar_is_discounted_against_proficient()
    {
        var familiar = MatchScorer.Score(
            Candidate(Holds("skill.python", AssertionPolarity.Familiar)),
            Posting(Wants("skill.python")));

        var proficient = MatchScorer.Score(
            Candidate(Holds("skill.python", AssertionPolarity.Proficient)),
            Posting(Wants("skill.python")));

        Assert.True(familiar.Score < proficient.Score);
    }

    [Fact]
    public void A_years_shortfall_is_proportional_rather_than_a_cliff()
    {
        // Five asked and four held is very nearly a match. Five asked and one held is not, and
        // a threshold would have to call both the same.
        var close = MatchScorer.Score(
            Candidate(Holds("skill.python", years: 4)),
            Posting(Wants("skill.python", years: 5)));

        var distant = MatchScorer.Score(
            Candidate(Holds("skill.python", years: 1)),
            Posting(Wants("skill.python", years: 5)));

        var met = MatchScorer.Score(
            Candidate(Holds("skill.python", years: 6)),
            Posting(Wants("skill.python", years: 5)));

        Assert.Equal(100, met.Score);
        Assert.True(close.Score < met.Score);
        Assert.True(distant.Score < close.Score);
    }

    [Fact]
    public void One_requirement_recorded_twice_is_weighted_once()
    {
        // The posting side stores a row per source, so a concept the board tagged and the
        // description also mentioned arrives twice by design. Counting both would weight it
        // double for no reason but how thoroughly it was recorded.
        var duplicated = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(
                new ConceptAssertion("skill.python", AssertionSource.Board, AssertionPolarity.Required),
                new ConceptAssertion("skill.python", AssertionSource.Taxonomy, AssertionPolarity.Required),
                Wants("skill.rust")));

        var once = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(Wants("skill.python"), Wants("skill.rust")));

        Assert.Equal(once.Score, duplicated.Score);
    }

    [Fact]
    public void The_candidates_strongest_claim_about_one_concept_is_the_one_used()
    {
        // Declared on the form and found in the prose is two rows for one skill. Averaging
        // would let a cautious model reading dilute an explicit claim.
        var result = MatchScorer.Score(
            Candidate(
                new ConceptAssertion("skill.python", AssertionSource.Model, AssertionPolarity.Familiar),
                new ConceptAssertion("skill.python", AssertionSource.Board, AssertionPolarity.Expert)),
            Posting(Wants("skill.python")));

        Assert.Equal(100, result.Score);
    }

    // -----------------------------------------------------------------------
    // Silence drops an axis rather than failing it
    // -----------------------------------------------------------------------

    [Fact]
    public void A_posting_that_states_no_seniority_is_not_penalised_for_it()
    {
        var stated = MatchScorer.Score(
            new CandidateFacts { Concepts = [Holds("skill.python")], Seniority = Seniority.Senior },
            new PostingFacts { PostingId = 1, Concepts = [Wants("skill.python")], Seniority = Seniority.Senior });

        var silent = MatchScorer.Score(
            new CandidateFacts { Concepts = [Holds("skill.python")], Seniority = Seniority.Senior },
            Posting(Wants("skill.python")));

        // Both are perfect matches on everything the posting actually said. Scoring the silent
        // one lower would rank vagueness below agreement; scoring it higher would reward it.
        Assert.Equal(100, stated.Score);
        Assert.Equal(100, silent.Score);
    }

    [Fact]
    public void An_axis_the_posting_cannot_answer_carries_no_weight()
    {
        var result = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(Wants("skill.python")));

        var salary = result.Components.Single(c => c.Name == MatchComponent.Salary);
        Assert.Equal(0, salary.Weight);

        // The axes that did answer carry all of it, which is what makes the total meaningful
        // rather than a fraction of a denominator nothing filled.
        Assert.True(result.Components.Sum(c => c.Weight) > 0);
    }

    [Fact]
    public void A_posting_with_nothing_to_score_answers_zero_rather_than_dividing_by_it()
    {
        var result = MatchScorer.Score(Candidate(Holds("skill.python")), Posting());

        Assert.Equal(0, result.Score);
        Assert.Equal(0, result.Coverage);
        Assert.Empty(result.Matched);
        Assert.Empty(result.Gaps);
    }

    // -----------------------------------------------------------------------
    // The non-concept axes
    // -----------------------------------------------------------------------

    [Fact]
    public void Being_under_levelled_costs_more_than_being_over_levelled()
    {
        var under = SeniorityScore(Seniority.Mid, Seniority.Principal);
        var over = SeniorityScore(Seniority.Principal, Seniority.Mid);

        Assert.True(under < over);
    }

    private static int SeniorityScore(Seniority candidate, Seniority posting)
        => MatchScorer.Score(
            new CandidateFacts { Concepts = [Holds("skill.python")], Seniority = candidate },
            new PostingFacts { PostingId = 1, Concepts = [Wants("skill.python")], Seniority = posting })
            .Score;

    [Fact]
    public void Remote_satisfies_a_hybrid_preference_but_hybrid_does_not_satisfy_a_remote_one()
    {
        var remoteForHybrid = Arrangement(WorkArrangement.Hybrid, WorkArrangement.Remote);
        var hybridForRemote = Arrangement(WorkArrangement.Remote, WorkArrangement.Hybrid);

        Assert.True(remoteForHybrid > hybridForRemote);
    }

    [Fact]
    public void An_on_site_role_scores_nothing_on_arrangement_for_a_remote_only_candidate()
    {
        var result = Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                PreferredArrangement = WorkArrangement.Remote,
            },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.python")],
                WorkArrangement = WorkArrangement.OnSite,
            });

        Assert.Equal(0, result.Components.Single(c => c.Name == MatchComponent.WorkArrangement).Score);
    }

    private static int Arrangement(WorkArrangement candidate, WorkArrangement posting)
        => Score(
            new CandidateFacts { Concepts = [Holds("skill.python")], PreferredArrangement = candidate },
            new PostingFacts { PostingId = 1, Concepts = [Wants("skill.python")], WorkArrangement = posting })
            .Score;

    private static MatchResult Score(CandidateFacts candidate, PostingFacts posting)
        => MatchScorer.Score(candidate, posting);

    [Fact]
    public void A_mismatched_currency_drops_the_salary_axis_rather_than_guessing_a_rate()
    {
        var result = Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                MinimumSalary = 70_000m,
                SalaryCurrency = "GBP",
            },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.python")],
                AnnualSalaryMax = 90_000m,
                SalaryCurrency = "INR",
            });

        Assert.Equal(0, result.Components.Single(c => c.Name == MatchComponent.Salary).Weight);
    }

    [Fact]
    public void A_salary_read_out_of_prose_carries_less_weight_than_one_a_board_published()
    {
        var published = Salary(fromText: false);
        var inferred = Salary(fromText: true);

        Assert.True(inferred < published);
    }

    private static double Salary(bool fromText)
        => Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                MinimumSalary = 70_000m,
                SalaryCurrency = "GBP",
            },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.python")],
                AnnualSalaryMax = 90_000m,
                SalaryCurrency = "GBP",
                SalaryFromText = fromText,
            })
            .Components.Single(c => c.Name == MatchComponent.Salary).Weight;

    [Fact]
    public void A_remote_posting_is_not_scored_on_where_the_candidate_lives()
    {
        var result = Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                LocationCity = "Madrid",
                LocationCountry = "Spain",
            },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.python")],
                WorkArrangement = WorkArrangement.Remote,
                LocationCity = "London",
                LocationCountry = "United Kingdom",
            });

        Assert.Equal(0, result.Components.Single(c => c.Name == MatchComponent.Location).Weight);
    }

    [Fact]
    public void Willingness_to_relocate_softens_a_location_mismatch_without_erasing_it()
    {
        var staying = Location(willingToRelocate: false);
        var moving = Location(willingToRelocate: true);

        Assert.True(moving > staying);
        Assert.True(moving < 1.0);
    }

    private static double Location(bool willingToRelocate)
        => Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                LocationCity = "Madrid",
                LocationCountry = "Spain",
                WillingToRelocate = willingToRelocate,
            },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.python")],
                WorkArrangement = WorkArrangement.OnSite,
                LocationCity = "London",
                LocationCountry = "United Kingdom",
            })
            .Components.Single(c => c.Name == MatchComponent.Location).Score;

    [Fact]
    public void An_unstated_polarity_is_weighted_as_preferred_rather_than_as_essential()
    {
        // Unspecified is by far the most common polarity, because only the model pass can tell
        // essential from desirable and it has not necessarily run. Treating it as required
        // would score most of the corpus at zero.
        var unspecified = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(
                Wants("skill.python"),
                new ConceptAssertion("skill.rust", AssertionSource.Taxonomy)));

        Assert.Equal(0, unspecified.RequiredGapCount);
        Assert.Single(unspecified.Gaps);
        Assert.True(unspecified.Score > 0);
    }

    [Fact]
    public void Gaps_are_ordered_with_the_essential_ones_first()
    {
        var result = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(
                Wants("skill.rust", AssertionPolarity.Preferred),
                Wants("skill.cobol", AssertionPolarity.Required)));

        Assert.Equal(AssertionPolarity.Required, result.Gaps[0].Demand);
    }

    // -----------------------------------------------------------------------
    // Coverage, and the floor under "silence drops an axis"
    //
    // These exist because the rule was wrong in production before it was wrong in a test.
    // Against the real corpus, 44 of the top 60 matches had no skills axis at all and 13
    // were scored on location alone - a posting nothing could be said about was coming out
    // at 100 and outranking roles the candidate genuinely fitted.
    // -----------------------------------------------------------------------

    [Fact]
    public void A_posting_with_no_readable_requirements_cannot_score_on_location_alone()
    {
        // The exact shape that reached production: no concepts, no seniority, no arrangement,
        // no salary. The only answerable axis was location, and it agreed.
        var result = Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                LocationCity = "London",
                LocationCountry = "United Kingdom",
            },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [],
                LocationCity = "London",
                LocationCountry = "United Kingdom",
            });

        Assert.Equal(0, result.Score);

        // The location axis still scored - it is not suppressed, it just cannot carry the
        // result on its own. Keeping it visible is what makes the zero explicable.
        Assert.Equal(1.0, result.Components.Single(c => c.Name == MatchComponent.Location).Score);
    }

    [Fact]
    public void A_posting_asking_only_for_agile_cannot_be_matched_on_it()
    {
        // Two Transformation Managers and a Product Manager reached the corpus top 25 on
        // exactly this: the single word "agile", which the candidate does hold. The reading is
        // correct and the arithmetic is correct - the concept simply does not discriminate,
        // because adverts for every kind of job say it.
        var result = Score(
            new CandidateFacts { Concepts = [Holds("skill.agile")] },
            new PostingFacts { PostingId = 1, Concepts = [Wants("skill.agile")] });

        Assert.Equal(0, result.Score);

        // The match is still recorded. The candidate does hold what was asked for, and hiding
        // that would make the zero unexplainable to anyone reading the result.
        Assert.Contains(result.Matched, m => m.RequiredKey == "skill.agile");
    }

    [Fact]
    public void A_board_tag_naming_a_whole_field_is_a_category_rather_than_a_requirement()
    {
        // "Space Data Engineer" was the top match in the whole corpus at 100, on one board tag
        // reading "Data Engineering". A domain says which shelf the job sits on, not what it
        // needs, so it cannot carry a match by itself.
        var result = Score(
            new CandidateFacts { Concepts = [Holds("area.data")] },
            new PostingFacts { PostingId = 1, Concepts = [Wants("area.data")] });

        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void One_concrete_skill_still_carries_a_match_on_its_own()
    {
        // The other half of the rule, and the one 1.4 got wrong. "Senior Software Engineer -
        // C#" rests on as little evidence as the Transformation Manager above, and is a real
        // match. What separates them is which concept it is, never how many - a count removed
        // both, which is exactly why counting was withdrawn.
        var result = Score(
            new CandidateFacts { Concepts = [Holds("skill.csharp")] },
            new PostingFacts { PostingId = 1, Concepts = [Wants("skill.csharp")] });

        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void One_discriminating_demand_among_generic_ones_clears_the_floor()
    {
        // Postings state "agile" alongside their real requirements constantly. The floor asks
        // whether anything discriminating was asked for at all, so a single real skill next to
        // any number of generic ones clears it.
        var result = Score(
            new CandidateFacts { Concepts = [Holds("skill.csharp"), Holds("skill.agile")] },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.agile"), Wants("skill.csharp")],
            });

        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void A_key_the_vocabulary_does_not_know_is_treated_as_discriminating()
    {
        // Unknown is not the same as generic. A key the graph cannot resolve must not be read
        // as a category, or dropping a concept from the vocabulary would silently zero every
        // stored posting still referencing it.
        var result = Score(
            new CandidateFacts { Concepts = [Holds("skill.not-in-the-vocabulary")] },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.not-in-the-vocabulary")],
            });

        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void Coverage_says_how_much_of_a_full_assessment_the_posting_supported()
    {
        var thin = Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                LocationCity = "London",
            },
            new PostingFacts
            {
                PostingId = 1,
                Concepts = [Wants("skill.python")],
                LocationCity = "London",
            });

        var full = Score(
            new CandidateFacts
            {
                Concepts = [Holds("skill.python")],
                Seniority = Seniority.Senior,
                YearsExperience = 8,
                PreferredArrangement = WorkArrangement.Hybrid,
                MinimumSalary = 70_000m,
                SalaryCurrency = "GBP",
                LocationCity = "London",
            },
            new PostingFacts
            {
                PostingId = 2,
                Concepts = [Wants("skill.python"), Wants("skill.rust", AssertionPolarity.Preferred)],
                Seniority = Seniority.Senior,
                YearsExperienceMin = 5,
                WorkArrangement = WorkArrangement.Hybrid,
                AnnualSalaryMax = 90_000m,
                SalaryCurrency = "GBP",
                LocationCity = "London",
            });

        Assert.True(thin.Coverage < full.Coverage);
        Assert.InRange(thin.Coverage, 0.01, 0.99);
        Assert.InRange(full.Coverage, 0.99, 1.0);
    }

    [Fact]
    public void Coverage_is_zero_where_the_posting_answered_nothing_at_all()
    {
        var result = MatchScorer.Score(Candidate(Holds("skill.python")), Posting());

        Assert.Equal(0, result.Score);
        Assert.Equal(0, result.Coverage);
    }

    [Fact]
    public void A_terse_posting_whose_skills_are_all_met_still_scores_full_marks()
    {
        // Coverage is reported, never multiplied into the score. A posting that states only
        // skills, and whose skills the candidate has, is a complete match on everything it
        // asked for - discounting it for the questions it never posed would punish the
        // candidate for the employer's terseness.
        var result = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(Wants("skill.python")));

        Assert.Equal(100, result.Score);
        Assert.True(result.Coverage < 0.5);
    }

    [Fact]
    public void Concept_evidence_from_the_preferred_axis_alone_is_enough_to_score()
    {
        // Unspecified polarity lands on the preferred axis, and it is by far the most common
        // case - only the model pass can tell essential from desirable and it has not
        // necessarily run. Requiring the *required* axis specifically would zero most of the
        // corpus, which would be the same bug pointing the other way.
        // Model-sourced with no polarity, rather than a bare taxonomy hit: that would trip the
        // evidence floor and test two rules at once. What is under test here is the polarity,
        // so the evidence is put beyond question.
        var result = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(new ConceptAssertion("skill.python", AssertionSource.Model)));

        Assert.True(result.Score > 0);
        Assert.Equal(
            0,
            result.Components.Single(c => c.Name == MatchComponent.RequiredSkills).Weight);
    }

    [Fact]
    public void One_concept_the_employer_published_is_evidence_on_its_own()
    {
        // A board tag is an employer deliberately stating a skill, not a word appearing in
        // prose. One is enough.
        var result = MatchScorer.Score(
            Candidate(Holds("skill.kubernetes")),
            Posting(new ConceptAssertion("skill.kubernetes", AssertionSource.Board)));

        Assert.True(result.Score > 0);
    }

    [Fact]
    public void One_concept_the_model_read_is_evidence_on_its_own()
    {
        // A model assertion is a deliberate reading of the advert, and the only source that can
        // say a requirement is essential at all.
        var result = MatchScorer.Score(
            Candidate(Holds("skill.kubernetes")),
            Posting(new ConceptAssertion(
                "skill.kubernetes", AssertionSource.Model, AssertionPolarity.Required)));

        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void The_score_is_bounded_and_rounded_once()
    {
        var result = MatchScorer.Score(
            Candidate(Holds("skill.python")),
            Posting(Wants("skill.python"), Wants("skill.rust"), Wants("skill.cobol")));

        Assert.InRange(result.Score, 0, 100);
    }
}
