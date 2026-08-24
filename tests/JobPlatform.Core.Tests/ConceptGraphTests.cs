using JobPlatform.Core.Enrichment;
using Xunit;

namespace JobPlatform.Core.Tests;

public sealed class ConceptGraphTests
{
    private static readonly ConceptGraph Graph = ConceptGraph.Default;

    [Fact]
    public void Vocabulary_loads_from_the_embedded_resource()
    {
        // Guards the csproj entry as much as the loader: without an <EmbeddedResource> for
        // concepts.json this throws, and every other test in this file fails the same way
        // with a less obvious message.
        Assert.NotEmpty(Graph.Concepts);
        Assert.True(Graph.Version >= 2);
        Assert.Contains(Graph.Concepts, c => c.Key == "skill.kubernetes");
    }

    [Theory]
    [InlineData("k8s")]
    [InlineData("K8S")]
    [InlineData("Kubernetes")]
    [InlineData("kubernetes")]
    public void Spellings_of_one_concept_resolve_to_one_key(string spelling)
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, $"We run {spelling} in production.");

        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("skill.kubernetes", assertion.ConceptKey);
    }

    [Fact]
    public void Evidence_keeps_the_spelling_the_advert_used()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Strong k8s experience required.");

        // The point of keeping it: a match can be explained back to the reader, and the row
        // can be re-resolved later without re-reading the description.
        Assert.Equal("k8s", Assert.Single(result.Assertions).EvidenceText);
    }

    [Fact]
    public void Ambiguous_bare_names_are_recorded_as_mentions_rather_than_asserted()
    {
        // The whole reason this class replaced the flat vocabulary. "Go" here is the verb.
        var result = Graph.Resolve(
            AssertionSource.Taxonomy,
            "You will go above and beyond for our customers.");

        Assert.Empty(result.Assertions);

        var mention = Assert.Single(result.Mentions);
        Assert.Equal("go", mention.SurfaceForm, ignoreCase: true);
        Assert.Equal(MentionReason.Ambiguous, mention.Reason);
    }

    [Fact]
    public void Unambiguous_aliases_of_an_ambiguous_concept_still_resolve()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Backend services written in Golang.");

        Assert.Equal("skill.go", Assert.Single(result.Assertions).ConceptKey);
        Assert.Empty(result.Mentions);
    }

    [Fact]
    public void Capitalised_homographs_do_not_match_their_ordinary_word()
    {
        var result = Graph.Resolve(
            AssertionSource.Taxonomy,
            "You will support the rest of the team and react to incidents as they arise.");

        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "skill.rest");
        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "skill.react");
    }

    [Fact]
    public void Capitalised_homographs_match_when_the_text_kept_the_capital()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Design REST APIs in React.");

        Assert.Contains(result.Assertions, a => a.ConceptKey == "skill.rest");
        Assert.Contains(result.Assertions, a => a.ConceptKey == "skill.react");
    }

    [Fact]
    public void Longer_names_win_over_the_shorter_names_nested_inside_them()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Spring Boot microservices.");

        // "Spring Boot" must not also assert "Spring": one framework was named, not two.
        Assert.Contains(result.Assertions, a => a.ConceptKey == "skill.spring-boot");
        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "skill.spring");
    }

    [Fact]
    public void Punctuation_inside_a_name_is_a_boundary_the_matcher_respects()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Systems programming in C++.");

        // \b would treat + as a boundary and let bare C match inside C++.
        Assert.Contains(result.Assertions, a => a.ConceptKey == "skill.cpp");
        Assert.Empty(result.Mentions);
    }

    [Theory]
    [InlineData("The stack is C#.")]
    [InlineData("Experience with React.")]
    [InlineData("Services written in Golang.")]
    [InlineData("Deployed on Kubernetes!")]
    [InlineData("Do you know Terraform?")]
    [InlineData("Stack: Python, Django.")]
    public void A_concept_at_the_end_of_a_sentence_still_matches(string text)
    {
        // Regression. Treating "." as a boundary character unconditionally made every skill
        // followed by a full stop invisible, which is one of the commonest shapes in an
        // advert - the failure was silent and cost a large share of all matches.
        Assert.NotEmpty(ConceptGraph.Default.Resolve(AssertionSource.Taxonomy, text).Assertions);
    }

    [Fact]
    public void A_dot_still_breaks_a_name_when_a_name_character_follows_it()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Backend on Node.js.");

        // The reason the dot cannot simply be dropped from the boundary set.
        Assert.Contains(result.Assertions, a => a.ConceptKey == "skill.nodejs");
        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "skill.javascript");
    }

    [Fact]
    public void Csharp_and_c_are_not_confused_with_each_other()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Building services in C#.");

        Assert.Equal("skill.csharp", Assert.Single(result.Assertions).ConceptKey);
    }

    [Fact]
    public void Domains_are_never_matched_in_text()
    {
        // "Backend Development" is reached by walking the closure, not by finding the phrase.
        var result = Graph.Resolve(AssertionSource.Taxonomy, "A Backend Development role.");

        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey.StartsWith("area.", StringComparison.Ordinal));
    }

    [Fact]
    public void Assertions_come_back_in_vocabulary_order_not_text_order()
    {
        // An unstable list would look like a changed posting on every re-ingest.
        var forward = Graph.Resolve(AssertionSource.Taxonomy, "Kubernetes, then Python.");
        var backward = Graph.Resolve(AssertionSource.Taxonomy, "Python, then Kubernetes.");

        Assert.Equal(
            forward.Assertions.Select(a => a.ConceptKey),
            backward.Assertions.Select(a => a.ConceptKey));
    }

    [Fact]
    public void Deterministic_assertions_claim_nothing_about_strength()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "Terraform experience essential.");

        // A regex cannot tell "essential" from "nice to have" reliably, so it says nothing.
        Assert.Equal(AssertionPolarity.Unspecified, Assert.Single(result.Assertions).Polarity);
    }

    [Theory]
    [InlineData("Our stack is Python, Go and Rust.", "skill.go")]
    [InlineData("Looking for a Go developer.", "skill.go")]
    [InlineData("Services written in Go.", "skill.go")]
    [InlineData("Experience with C, C++ and Rust.", "skill.c")]
    [InlineData("Statistical work in R, Python and SQL.", "skill.r")]
    public void An_ambiguous_name_resolves_where_the_context_settles_it(string text, string key)
    {
        // 833 mentions in the live corpus were Go, C and R. Refusing them outright is safe
        // and loses the ones that really are the language; these are the shapes where the
        // surrounding text answers the question without a model.
        var result = Graph.Resolve(AssertionSource.Taxonomy, text);

        Assert.Contains(result.Assertions, a => a.ConceptKey == key);
    }

    [Theory]
    [InlineData("You will go above and beyond for our customers.")]
    [InlineData("Ready to go? Apply today.")]
    [InlineData("We are always on the go.")]
    [InlineData("Fast, go, and win - that is our motto.")]
    [InlineData("Salary is negotiable, c. 80,000 depending on experience.")]
    public void An_ambiguous_name_stays_a_mention_where_it_does_not(string text)
    {
        // The precision half, and the half that matters more: a false spike in demand for Go
        // is worse than undercounting it. "Fast, go, and win" is a list too - what separates
        // it from "Python, Go, Rust" is entirely what the neighbours are.
        var result = Graph.Resolve(AssertionSource.Taxonomy, text);

        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey is "skill.go" or "skill.c");
        Assert.NotEmpty(result.Mentions);
    }

    [Fact]
    public void A_board_tag_may_name_a_domain_but_a_description_may_not()
    {
        // The commonest unresolved forms were ai, cloud, machine-learning and observability -
        // domains the vocabulary already had and was refusing, because the rule written for
        // prose was being applied to a tag the employer picked from a list.
        Assert.True(Graph.TryResolve("machine-learning", out var domain, fromStructuredField: true));
        Assert.Equal("area.ml", domain.Key);

        Assert.False(Graph.TryResolve("machine-learning", out _));

        var fromText = Graph.Resolve(AssertionSource.Taxonomy, "A machine-learning role.");
        Assert.DoesNotContain(fromText.Assertions, a => a.ConceptKey == "area.ml");
    }

    [Theory]
    [InlineData("api", "skill.api")]
    [InlineData("automation", "skill.automation")]
    [InlineData("cloud", "area.cloud")]
    [InlineData("observability", "area.observability")]
    public void Tag_only_concepts_resolve_from_a_skills_field(string tag, string key)
    {
        Assert.True(Graph.TryResolve(tag, out var concept, fromStructuredField: true));
        Assert.Equal(key, concept.Key);
    }

    [Fact]
    public void Tag_only_words_are_never_matched_in_prose()
    {
        // "api", "cloud" and "automation" appear in almost every advert and carry no
        // information there. Matching them would put a concept on nearly every posting.
        var result = Graph.Resolve(
            AssertionSource.Taxonomy,
            "You will build an API in the cloud with a focus on automation.");

        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "skill.api");
        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "skill.automation");
        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "area.cloud");
    }

    [Fact]
    public void A_specific_skill_still_beats_the_domain_that_shares_its_name()
    {
        // "devops" is both a skill and a domain. The skill is the more specific answer, and
        // it reaches the domain through the closure anyway.
        Assert.True(Graph.TryResolve("devops", out var concept, fromStructuredField: true));
        Assert.Equal("skill.devops", concept.Key);
        Assert.Contains("area.devops", Graph.Ancestors(concept.Key).Keys);
    }

    [Fact]
    public void Closure_rolls_a_skill_up_to_every_ancestor()
    {
        var ancestors = Graph.Ancestors("skill.csharp");

        Assert.Equal(0, ancestors["skill.csharp"]);
        Assert.Equal(1, ancestors["type.language"]);
        Assert.Equal(1, ancestors["area.backend"]);
        Assert.Equal(2, ancestors["area.software-development"]);
    }

    [Fact]
    public void Closure_keeps_both_parents_of_a_multi_parent_concept()
    {
        // The reason this is a DAG. Pandas is a data tool and an ML tool, and the flat
        // category field this replaced could only record one of those.
        var ancestors = Graph.Ancestors("skill.pandas");

        Assert.Contains("area.data", ancestors.Keys);
        Assert.Contains("area.ml", ancestors.Keys);
        Assert.Contains("area.data-and-ai", ancestors.Keys);
    }

    [Fact]
    public void Closure_depth_is_the_shortest_path_where_several_exist()
    {
        // skill.eks reaches area.cloud directly and again through skill.kubernetes.
        Assert.Equal(1, Graph.Ancestors("skill.eks")["area.cloud"]);
    }

    [Fact]
    public void Closure_covers_every_concept_including_itself()
    {
        var selfRows = Graph.Closure().Count(e => e.Depth == 0);

        Assert.Equal(Graph.Concepts.Count, selfRows);
    }

    [Fact]
    public void Board_supplied_skills_fold_onto_concept_keys()
    {
        Assert.True(Graph.TryResolve("k8s", out var concept));
        Assert.Equal("skill.kubernetes", concept.Key);
    }

    [Fact]
    public void Unknown_board_skills_are_refused_rather_than_invented()
    {
        Assert.False(Graph.TryResolve("Contoso Internal Framework", out _));
    }

    [Fact]
    public void Every_relation_points_at_a_concept_that_exists()
    {
        var keys = Graph.Concepts.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(Graph.Relations(), edge =>
        {
            Assert.Contains(edge.FromKey, keys);
            Assert.Contains(edge.ToKey, keys);
        });
    }

    [Fact]
    public void Implications_are_edges_and_are_never_asserted()
    {
        var result = Graph.Resolve(AssertionSource.Taxonomy, "We run Kubernetes.");

        // An assertion records what the posting said. Containerisation is what we would
        // conclude from it, and materialising that here would make "demand for
        // containerisation" count adverts that never mentioned it.
        Assert.DoesNotContain(result.Assertions, a => a.ConceptKey == "skill.containers");

        Assert.Contains(
            Graph.Relations(),
            e => e is { FromKey: "skill.kubernetes", ToKey: "skill.containers", Type: ConceptRelationType.Implies });
    }
}
