using System.Text.Json;
using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Enrichment;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// What happens to the technologies the model says it could not place.
/// </summary>
/// <remarks>
/// <b>The defect these pin was invisible for as long as nobody read the mention log.</b> The
/// prompt sends the vocabulary as <c>key = label</c> and no aliases, so a model reading
/// "generative AI" sees only <c>skill.llms = LLMs</c> and quite reasonably reports it unknown -
/// and <c>Parse</c> recorded that list verbatim without ever asking the graph. Measured on
/// 2026-08-31: "AI" in 89 postings, "machine learning" in 52, "generative AI" in 43, every one of
/// them an alias the resolver already knew.
///
/// The fix asks the graph first. What must not come with it is the ambiguity rule being lost:
/// "Go" and "Claude" have to stay unresolved, and they have to stay unresolved *because the graph
/// refuses them* rather than because this code remembered to check.
/// </remarks>
public sealed class UnknownSkillResolutionTests
{
    private static DocumentExtraction Parse(params string[] unknownSkills)
    {
        var payload = JsonSerializer.Serialize(new { unknownSkills });
        using var document = JsonDocument.Parse(payload);

        return ExtractionPrompt.Parse(document.RootElement, model: "test");
    }

    private static IReadOnlyList<string> Keys(DocumentExtraction extraction)
        => [.. extraction.Concepts.Select(c => c.ConceptKey)];

    private static IReadOnlyList<string> Forms(DocumentExtraction extraction)
        => [.. extraction.Mentions.Select(m => m.SurfaceForm)];

    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("machine learning", "area.ml")]
    [InlineData("AI", "area.ml")]
    [InlineData("generative AI", "skill.llms")]
    public void A_form_the_vocabulary_already_knew_becomes_an_assertion(string form, string key)
    {
        var extraction = Parse(form);

        Assert.Contains(key, Keys(extraction));
        Assert.Empty(extraction.Mentions);
    }

    [Fact]
    public void The_assertion_records_the_form_the_model_wrote()
    {
        // So a reader can see why the assertion exists and that it did not come from a key.
        var assertion = Parse("generative AI").Concepts.Single();

        Assert.Equal("generative AI", assertion.EvidenceText);
        Assert.Equal(AssertionSource.Model, assertion.Source);
    }

    [Fact]
    public void It_carries_no_polarity_because_the_list_states_none()
    {
        // Unspecified is weighted as preferred by the scorer, which is the honest reading of "the
        // model saw this technology and did not say how hard it was asked for". Inventing
        // "required" would put a number on something nobody measured.
        Assert.Equal(AssertionPolarity.Unspecified, Parse("machine learning").Concepts.Single().Polarity);
    }

    [Theory]
    // The ambiguity rule, inherited rather than re-implemented.
    [InlineData("Go")]
    [InlineData("Claude")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void An_ambiguous_name_is_still_recorded_rather_than_asserted(string form)
    {
        var extraction = Parse(form);

        Assert.Empty(extraction.Concepts);
        Assert.Equal([form], Forms(extraction));
    }

    [Fact]
    public void A_genuinely_unknown_technology_is_still_recorded()
    {
        // The growth mechanism has to keep working. If everything resolved, the log would empty
        // and the next vocabulary gap would have nowhere to show up.
        var extraction = Parse("Frobnicator 9000");

        Assert.Empty(extraction.Concepts);
        Assert.Equal(["Frobnicator 9000"], Forms(extraction));
    }

    [Fact]
    public void The_same_concept_named_twice_is_asserted_once()
    {
        // "AI" and "machine learning" are both aliases of area.ml. Two assertions of one concept
        // would double its weight in the scorer for no reason but how the model phrased itself.
        var extraction = Parse("AI", "machine learning");

        Assert.Equal(["area.ml"], Keys(extraction));
    }

    [Fact]
    public void A_form_already_asserted_by_key_is_not_asserted_again()
    {
        var payload = JsonSerializer.Serialize(new
        {
            concepts = new[] { new { key = "area.ml", polarity = "required" } },
            unknownSkills = new[] { "machine learning" },
        });

        using var document = JsonDocument.Parse(payload);
        var extraction = ExtractionPrompt.Parse(document.RootElement, model: "test");

        Assert.Equal(["area.ml"], Keys(extraction));

        // And the one that survives is the keyed one, which carries the polarity the model
        // actually stated. Letting the unknown list overwrite it would discard evidence.
        Assert.Equal(AssertionPolarity.Required, extraction.Concepts.Single().Polarity);
    }

    [Fact]
    public void The_newly_added_vocabulary_is_reachable_this_way_too()
    {
        // The 24 concepts added on the same day. The model has been naming these for weeks with
        // nowhere for them to land.
        var extraction = Parse("LangGraph", "vector database", "prompt engineering");

        Assert.Equal(
            ["skill.langgraph", "skill.vector-databases", "skill.prompt-engineering"],
            Keys(extraction));
    }
}
