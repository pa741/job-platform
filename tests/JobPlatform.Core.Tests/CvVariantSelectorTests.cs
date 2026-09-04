using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// Which CV gets sent, against the real concept graph.
/// </summary>
/// <remarks>
/// Against the shipped vocabulary rather than a fixture one, for the reason
/// <c>MatchScorerTests</c> is: the interesting behaviour is entirely about graph structure and
/// about which concepts the vocabulary judges too generic to decide anything, and a synthetic
/// three-node graph would let both rules pass while being wrong about the vocabulary the system
/// actually runs on. The keys used here are checked to have no curated edges between them, so
/// each requirement is answered independently and the arithmetic can be asserted exactly - the
/// one exception is deliberate, and it is the adjacency case.
///
/// Pure and Azure-free, like the scorer's tests. That is what makes the numbers exact rather
/// than ranges, which matters more here than anywhere else in the matching code: this is the
/// only rule in the system that decides which document goes to a real employer.
/// </remarks>
public sealed class CvVariantSelectorTests
{
    private static ConceptAssertion Wants(
        string key, AssertionPolarity polarity = AssertionPolarity.Required)
        => new(key, AssertionSource.Model, polarity);

    private static CvVariantFacts Variant(long id, string label, params string[] keys)
        => new() { VariantId = id, Label = label, ConceptKeys = keys };

    /// <summary>The chosen variant, asserted present. <c>Assert.NotNull</c> hands nothing back.</summary>
    private static CvVariantScore ChosenOf(CvSelection selection)
    {
        Assert.True(selection.Chosen.HasValue, "Expected a variant to have been chosen.");
        return selection.Chosen!.Value;
    }

    private static CvVariantScore RunnerUpOf(CvSelection selection)
    {
        Assert.True(selection.RunnerUp.HasValue, "Expected a runner-up.");
        return selection.RunnerUp!.Value;
    }

    /// <summary>Ten concepts with no curated edge between any pair. See the class remarks.</summary>
    private static readonly string[] Unrelated =
    [
        "skill.csharp", "skill.dotnet", "skill.sql", "skill.python", "skill.angular",
        "skill.postgresql", "skill.terraform", "skill.kubernetes", "skill.aws", "skill.git",
    ];

    // -----------------------------------------------------------------------
    // The three outcomes
    // -----------------------------------------------------------------------

    [Fact]
    public void A_clear_winner_is_chosen_and_the_rationale_says_what_it_beat()
    {
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.dotnet"), Wants("skill.sql"), Wants("skill.kubernetes")],
            [
                Variant(1, "Backend .NET", "skill.csharp", "skill.dotnet", "skill.sql"),
                Variant(2, "AI & data platforms", "skill.python", "skill.terraform"),
            ]);

        Assert.Equal(CvSelectionOutcome.Chosen, selection.Outcome);

        var chosen = ChosenOf(selection);
        Assert.Equal(1, chosen.VariantId);
        Assert.Equal(75, chosen.Score);
        Assert.Equal(3, chosen.Answered);

        var runnerUp = RunnerUpOf(selection);
        Assert.Equal(2, runnerUp.VariantId);
        Assert.Equal(0, runnerUp.Score);

        // The audit trail is the point of the rationale: both documents, both numbers, and the
        // constants the decision was made against, in one sentence somebody can read months
        // later without this file open beside them.
        Assert.Contains("Backend .NET", selection.Rationale, StringComparison.Ordinal);
        Assert.Contains("AI & data platforms", selection.Rationale, StringComparison.Ordinal);
        Assert.Contains("75", selection.Rationale, StringComparison.Ordinal);
        Assert.Contains($"floor of {CvVariantSelector.SelectionFloor}", selection.Rationale, StringComparison.Ordinal);
        Assert.Contains($"margin of {CvVariantSelector.SelectionMargin}", selection.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_variants_that_fit_equally_well_are_declared_ambiguous_rather_than_settled_on_an_id()
    {
        // Each document answers exactly one of the two requirements. There is no arithmetic
        // reading of this posting on which one of them is the right CV to send, and picking the
        // lower id would be a coin toss wearing a determinism argument.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.kubernetes")],
            [
                Variant(1, "Backend .NET", "skill.csharp"),
                Variant(2, "Platform", "skill.kubernetes"),
            ]);

        Assert.Equal(CvSelectionOutcome.Ambiguous, selection.Outcome);
        Assert.Null(selection.Chosen);
        Assert.Equal([50, 50], selection.Scores.Select(score => score.Score));
        Assert.Equal([1L, 2L], selection.Tied.Select(score => score.VariantId));

        // Both requirements are answered - by different documents. The library has no gap; what
        // it has is a choice, which is a different thing and a different outcome.
        Assert.Empty(selection.Missing);
    }

    [Fact]
    public void A_posting_no_variant_covers_returns_the_concepts_that_would_unblock_it()
    {
        var selection = CvVariantSelector.Select(
            [Wants("skill.terraform"), Wants("skill.kubernetes")],
            [
                Variant(1, "Backend .NET", "skill.csharp", "skill.dotnet"),
                Variant(2, "AI & data platforms", "skill.python"),
            ]);

        Assert.Equal(CvSelectionOutcome.NoFit, selection.Outcome);
        Assert.Null(selection.Chosen);

        // The brief, as data rather than as prose: this is what the gap view groups by across
        // every posting parked for the same reason, and a sentence cannot be grouped.
        Assert.Equal(
            ["skill.kubernetes", "skill.terraform"],
            selection.Missing.Select(gap => gap.RequiredKey));
        Assert.All(selection.Missing, gap => Assert.Equal(AssertionPolarity.Required, gap.Demand));

        // And the same thing in words, for the person who reads the park rather than the table.
        Assert.Contains("Kubernetes and Terraform", selection.Rationale, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // The edges that decide the design
    // -----------------------------------------------------------------------

    [Fact]
    public void A_posting_whose_every_requirement_is_generic_is_ambiguous_rather_than_a_park_that_would_loop()
    {
        // "Agile", "API" and a domain naming a whole field appear on adverts for every kind of
        // job. Nothing here says what the job is, so nothing here can tell two CVs apart.
        var graph = ConceptGraph.Default;
        string[] generic = ["skill.agile", "skill.api", "area.cloud"];

        foreach (var key in generic)
        {
            Assert.True(graph.TryGet(key, out var concept));
            Assert.False(concept.IsDiscriminating);
        }

        var selection = CvVariantSelector.Select(
            [Wants("skill.agile"), Wants("skill.api"), Wants("area.cloud")],
            [Variant(1, "Backend .NET", "skill.csharp"), Variant(2, "Platform", "skill.kubernetes")]);

        // NoFit is the tempting answer and it is the one that loops: a posting parked for want of
        // a CV returns when a variant covers what is missing, and nothing is missing here, so it
        // would come back on the next run, compute the same empty gap and park again forever.
        Assert.Equal(CvSelectionOutcome.Ambiguous, selection.Outcome);
        Assert.Empty(selection.Missing);
        Assert.Equal(2, selection.Tied.Count);
        Assert.All(selection.Scores, score => Assert.Equal(0, score.Score));
    }

    [Fact]
    public void A_variant_cannot_win_on_concepts_that_do_not_discriminate()
    {
        // The lesson MatchScorer learned expensively, on this side of the join. One document
        // matches a word the posting genuinely used; the other matches the requirement that says
        // what the job is. Scoring the first at 50 would send a CV about the wrong work.
        var selection = CvVariantSelector.Select(
            [Wants("skill.agile"), Wants("skill.terraform")],
            [Variant(1, "Delivery", "skill.agile"), Variant(2, "Platform", "skill.terraform")]);

        Assert.Equal(CvSelectionOutcome.Chosen, selection.Outcome);
        Assert.Equal(2, ChosenOf(selection).VariantId);

        var onAgileAlone = selection.Scores.Single(score => score.VariantId == 1);
        Assert.Equal(0, onAgileAlone.Score);
    }

    [Fact]
    public void An_empty_library_fits_nothing_and_names_what_a_first_CV_would_need()
    {
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.dotnet")],
            []);

        Assert.Equal(CvSelectionOutcome.NoFit, selection.Outcome);
        Assert.Null(selection.Chosen);
        Assert.Null(selection.RunnerUp);
        Assert.Empty(selection.Scores);
        Assert.Equal(["skill.csharp", "skill.dotnet"], selection.Missing.Select(gap => gap.RequiredKey));
        Assert.Contains("no variant to choose from", selection.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void One_variant_is_chosen_without_a_margin_to_clear()
    {
        // Nothing to be ambiguous between, so the margin is vacuous and the floor is the whole
        // test. This is the ordinary case for a candidate who has written one CV.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.dotnet")],
            [Variant(7, "Backend .NET", "skill.csharp", "skill.dotnet")]);

        Assert.Equal(CvSelectionOutcome.Chosen, selection.Outcome);
        Assert.Equal(100, ChosenOf(selection).Score);
        Assert.Null(selection.RunnerUp);
    }

    [Fact]
    public void One_variant_below_the_floor_is_declined_rather_than_sent_as_a_default()
    {
        // The failure this whole design replaces: sending the nearest CV to a job it does not
        // fit. Being the only candidate is not a qualification.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.dotnet"), Wants("skill.sql"), Wants("skill.kubernetes")],
            [Variant(7, "Backend .NET", "skill.csharp")]);

        Assert.Equal(CvSelectionOutcome.NoFit, selection.Outcome);
        Assert.Null(selection.Chosen);
        Assert.Equal(25, selection.Scores.Single().Score);
    }

    // -----------------------------------------------------------------------
    // The two constants
    // -----------------------------------------------------------------------

    [Fact]
    public void Adjacency_earns_a_score_but_neither_clears_the_floor_nor_closes_a_gap()
    {
        // They want EKS; the document talks about Kubernetes. Real transferable ground, priced
        // by the same graph walk the match breakdown uses, and worth less than half - which is
        // exactly where the floor sits, so a CV cannot be sent on resemblance alone. And the
        // requirement stays in the brief: "you wrote about Kubernetes" is not a reason to leave
        // EKS out of the next CV.
        var selection = CvVariantSelector.Select(
            [Wants("skill.eks")],
            [Variant(1, "Platform", "skill.kubernetes")]);

        Assert.Equal(CvSelectionOutcome.NoFit, selection.Outcome);
        Assert.Equal(45, selection.Scores.Single().Score);
        Assert.Equal(0, selection.Scores.Single().Answered);
        Assert.Equal(["skill.eks"], selection.Missing.Select(gap => gap.RequiredKey));
    }

    [Fact]
    public void A_lead_of_exactly_the_margin_decides()
    {
        // Ten requirements, so one of them answered is worth ten points - the difference the
        // margin is calibrated to be able to separate. The boundary falls on the deciding side.
        var selection = CvVariantSelector.Select(
            [.. Unrelated.Select(key => Wants(key))],
            [
                Variant(1, "Six of ten", [.. Unrelated.Take(6)]),
                Variant(2, "Five of ten", [.. Unrelated.Take(5)]),
            ]);

        Assert.Equal([60, 50], selection.Scores.Select(score => score.Score));
        Assert.Equal(CvSelectionOutcome.Chosen, selection.Outcome);
        Assert.Equal(1, ChosenOf(selection).VariantId);
    }

    [Fact]
    public void Two_variants_differing_by_one_adjacency_are_too_close_to_separate()
    {
        // The other half of the margin's calibration. These documents cover the same eight
        // requirements; they differ only in that one says EKS where the other says Kubernetes.
        // That gap is a fact about the vocabulary rather than about the documents, and settling
        // it by arithmetic would be dressing a coin toss as a decision.
        string[] demanded = [.. Unrelated.Take(7), "skill.eks"];

        var selection = CvVariantSelector.Select(
            [.. demanded.Select(key => Wants(key))],
            [
                Variant(1, "Says EKS", [.. demanded]),
                Variant(2, "Says Kubernetes", [.. Unrelated.Take(7), "skill.kubernetes"]),
            ]);

        Assert.Equal([100, 93], selection.Scores.Select(score => score.Score));
        Assert.Equal(CvSelectionOutcome.Ambiguous, selection.Outcome);
        Assert.Null(selection.Chosen);
        Assert.Equal(2, selection.Tied.Count);
    }

    [Fact]
    public void A_required_demand_outweighs_a_softer_one()
    {
        // The scorer's own 0.40 against 0.15, carried over as a per-demand weight. Unspecified
        // is the common polarity and lands with the softer half, for the reason it does there:
        // only the model pass can tell essential from desirable, and it has not necessarily run.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.kubernetes", AssertionPolarity.Unspecified)],
            [
                Variant(1, "Backend .NET", "skill.csharp"),
                Variant(2, "Platform", "skill.kubernetes"),
            ]);

        Assert.Equal([73, 27], selection.Scores.Select(score => score.Score));
        Assert.Equal(1, ChosenOf(selection).VariantId);
    }

    // -----------------------------------------------------------------------
    // What the result may and may not carry
    // -----------------------------------------------------------------------

    [Fact]
    public void The_missing_set_is_what_no_variant_covers_rather_than_what_the_chosen_one_lacks()
    {
        // A requirement another CV in the library answers is not a gap. Reading it as one would
        // put "write a CV about Kubernetes" in front of somebody who already has.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.dotnet"), Wants("skill.sql"), Wants("skill.kubernetes")],
            [
                Variant(1, "Backend .NET", "skill.csharp", "skill.dotnet", "skill.sql"),
                Variant(2, "Platform", "skill.kubernetes"),
            ]);

        Assert.Equal(CvSelectionOutcome.Chosen, selection.Outcome);
        Assert.Equal(3, ChosenOf(selection).Answered);
        Assert.Empty(selection.Missing);
    }

    [Fact]
    public void No_concept_a_variant_asserted_leaves_the_selection()
    {
        // The spec's selection-only guard, asserted rather than promised. A variant's concepts
        // decide which document is sent and must never widen what the candidate is judged to
        // have - so nothing here hands a caller a key it could write back into the profile.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.terraform")],
            [Variant(1, "Backend .NET", "skill.csharp", "skill.postgresql", "skill.angular")]);

        // Everything returned is either a number, a label the candidate typed, or a concept the
        // posting asked for. PostgreSQL and Angular are in the document and in neither.
        Assert.Equal(["skill.terraform"], selection.Missing.Select(gap => gap.RequiredKey));
        Assert.DoesNotContain("PostgreSQL", selection.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("Angular", selection.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("postgresql", selection.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void A_concept_the_posting_names_twice_is_weighed_once()
    {
        // The posting side stores a row per source, so a concept the board tagged and the
        // description also named arrives twice by design. Counting both would weight that
        // requirement double for no reason but how thoroughly it was recorded.
        var selection = CvVariantSelector.Select(
            [
                new ConceptAssertion("skill.csharp", AssertionSource.Board, AssertionPolarity.Required),
                new ConceptAssertion("skill.csharp", AssertionSource.Taxonomy, AssertionPolarity.Unspecified),
                Wants("skill.kubernetes"),
            ],
            [Variant(1, "Backend .NET", "skill.csharp")]);

        Assert.Equal(50, selection.Scores.Single().Score);
    }

    [Fact]
    public void A_key_the_vocabulary_does_not_know_is_still_a_requirement_nothing_covers()
    {
        // Unknown is not the same as generic, and the failure modes are not symmetric: reading
        // an unrecognised key as generic would let a vocabulary edit silently stop every posting
        // still carrying the old key from pulling a CV at all.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp"), Wants("skill.not-in-the-vocabulary")],
            [Variant(1, "Backend .NET", "skill.csharp")]);

        Assert.Equal(50, selection.Scores.Single().Score);
        Assert.Equal(["skill.not-in-the-vocabulary"], selection.Missing.Select(gap => gap.RequiredKey));

        // A key with no label still prints, because an audit line that quietly omits a
        // requirement is worse than one showing a raw key.
        Assert.Contains("skill.not-in-the-vocabulary", selection.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void Scores_are_ordered_best_first_and_the_id_tie_break_decides_nothing()
    {
        // Two identical documents. The list order has to be stable so the same posting renders
        // the same page twice; the outcome must not be, because id order is authoring order and
        // has nothing to say about which CV fits.
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp")],
            [Variant(7, "Later", "skill.csharp"), Variant(3, "Earlier", "skill.csharp")]);

        Assert.Equal([3L, 7L], selection.Scores.Select(score => score.VariantId));
        Assert.Equal(CvSelectionOutcome.Ambiguous, selection.Outcome);
        Assert.Null(selection.Chosen);
    }

    [Fact]
    public void An_unlabelled_variant_is_still_nameable_in_the_audit_line()
    {
        var selection = CvVariantSelector.Select(
            [Wants("skill.csharp")],
            [Variant(42, "   ", "skill.csharp")]);

        Assert.Equal("variant 42", ChosenOf(selection).Label);
        Assert.Contains("variant 42", selection.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void A_posting_that_states_nothing_at_all_chooses_nothing_and_asks_for_nothing()
    {
        // Neither a fit nor a gap: there is no requirement to write a CV about. Handled by the
        // same branch as the all-generic posting, and for the same reason.
        var selection = CvVariantSelector.Select(
            [],
            [Variant(1, "Backend .NET", "skill.csharp"), Variant(2, "Platform", "skill.kubernetes")]);

        Assert.Equal(CvSelectionOutcome.Ambiguous, selection.Outcome);
        Assert.Empty(selection.Missing);
        Assert.Contains("no requirements at all", selection.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three specialised CVs covering a broad advert between them still name a gap.
    /// </summary>
    /// <remarks>
    /// <b>The worst outcome this feature has, and it is silent.</b> Missing is filled from ANY
    /// variant that entails a demand, so a candidate holding Kubernetes in one CV, Terraform in
    /// another and .NET in a third can have every demand of a broad posting answered somewhere
    /// while no single CV scores above a third of it. The set difference is then empty - and a
    /// NoFit carrying no gaps parks the posting into a state nothing can release, because the
    /// queue reads an empty gap set as nothing having been covered yet. The candidate writes six
    /// more CVs and that posting never comes back.
    ///
    /// What is missing here is a combination rather than a concept, and naming the whole demand
    /// set is what makes it sayable: "these postings want all of this in one CV".
    /// </remarks>
    [Fact]
    public void A_posting_covered_across_several_cvs_and_by_none_of_them_still_names_its_gap()
    {
        // Six unrelated demands, split three ways: every one is entailed by some CV, and no CV
        // entails more than two, so each scores 33 against a floor of 50.
        var selection = CvVariantSelector.Select(
            [.. Unrelated.Take(6).Select(key => Wants(key))],
            [
                Variant(1, "Platform", Unrelated[0], Unrelated[1]),
                Variant(2, "Backend", Unrelated[2], Unrelated[3]),
                Variant(3, "Cloud", Unrelated[4], Unrelated[5]),
            ],
            ConceptGraph.Default);

        Assert.Equal(CvSelectionOutcome.NoFit, selection.Outcome);

        // Not empty, which is the whole point: an empty gap set is a posting parked for ever.
        Assert.NotEmpty(selection.Missing);
    }

}
