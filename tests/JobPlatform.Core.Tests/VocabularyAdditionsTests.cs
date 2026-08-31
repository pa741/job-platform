using JobPlatform.Core.Enrichment;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The concepts added from the mention log on 2026-08-31, and the rules they had to obey.
/// </summary>
/// <remarks>
/// <b>Against the shipped vocabulary, like <c>MatchScorerTests</c>.</b> The point of these is not
/// that a JSON file parses - <c>ConceptGraphTests</c> covers that - but that each addition earns
/// its place under the rules the vocabulary's own notes set out, and that the two failure modes
/// an addition can introduce are absent: a form that now asserts something it should not, and a
/// form that still resolves to nothing.
///
/// Every concept here was chosen from a count of postings naming it, and the counts are in the
/// commit that added them. Nothing was added for symmetry - iOS is deliberately absent because
/// Android appeared in the log and iOS did not.
/// </remarks>
public sealed class VocabularyAdditionsTests
{
    private static readonly ConceptGraph Graph = ConceptGraph.Default;

    private static string? Resolve(string form, bool fromStructuredField = false)
        => Graph.TryResolve(form, out var concept, fromStructuredField) ? concept.Key : null;

    // -----------------------------------------------------------------------
    // The cluster the corpus is full of and the matcher could not see
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("skill.claude-code")]
    [InlineData("skill.cursor")]
    [InlineData("skill.github-copilot")]
    [InlineData("skill.codex")]
    [InlineData("skill.rag")]
    [InlineData("skill.mcp")]
    [InlineData("skill.langgraph")]
    [InlineData("skill.llamaindex")]
    [InlineData("skill.vector-databases")]
    [InlineData("skill.prompt-engineering")]
    [InlineData("skill.anthropic")]
    [InlineData("skill.openai")]
    [InlineData("skill.cuda")]
    [InlineData("skill.jax")]
    [InlineData("skill.html")]
    [InlineData("skill.css")]
    [InlineData("skill.nosql")]
    [InlineData("skill.android")]
    [InlineData("skill.s3")]
    [InlineData("skill.ecs")]
    [InlineData("skill.iam")]
    [InlineData("skill.salesforce")]
    [InlineData("skill.servicenow")]
    [InlineData("skill.power-automate")]
    public void The_concept_exists(string key)
        => Assert.True(Graph.TryGet(key, out _), key + " is missing from the vocabulary");

    [Theory]
    [InlineData("Claude Code", "skill.claude-code")]
    [InlineData("claude-code", "skill.claude-code")]
    [InlineData("LangGraph", "skill.langgraph")]
    [InlineData("LlamaIndex", "skill.llamaindex")]
    [InlineData("model context protocol", "skill.mcp")]
    [InlineData("retrieval-augmented generation", "skill.rag")]
    [InlineData("vector database", "skill.vector-databases")]
    [InlineData("prompt engineering", "skill.prompt-engineering")]
    [InlineData("OpenAI", "skill.openai")]
    [InlineData("Salesforce", "skill.salesforce")]
    [InlineData("HTML", "skill.html")]
    [InlineData("CSS", "skill.css")]
    [InlineData("NoSQL", "skill.nosql")]
    [InlineData("amazon s3", "skill.s3")]
    public void The_form_the_corpus_actually_uses_resolves(string form, string key)
        => Assert.Equal(key, Resolve(form));

    // -----------------------------------------------------------------------
    // The forms that must NOT assert, which is the half that can do damage
    // -----------------------------------------------------------------------

    [Theory]
    // A database cursor and a UI cursor are both ordinary technical prose.
    [InlineData("cursor")]
    // A person's name, exactly like Julia.
    [InlineData("claude")]
    // Microsoft 365 Copilot is not GitHub Copilot.
    [InlineData("copilot")]
    public void An_ordinary_word_is_recorded_rather_than_asserted(string form)
    {
        Assert.Null(Resolve(form));
        Assert.Null(Resolve(form, fromStructuredField: true));
    }

    [Theory]
    // "rag" is a cloth, "codex" a bound manuscript, "jax" a name, and "iam" is one letter from
    // an ordinary word. In flowing prose none of them may assert without their capital.
    [InlineData("rag", "skill.rag")]
    [InlineData("codex", "skill.codex")]
    [InlineData("jax", "skill.jax")]
    [InlineData("ecs", "skill.ecs")]
    [InlineData("iam", "skill.iam")]
    [InlineData("mcp", "skill.mcp")]
    public void A_capitalised_concept_needs_its_capital_in_prose(string lower, string key)
    {
        // requiresCapital is enforced by the text scan, where the original spelling survives -
        // not by TryResolve, which is handed a form somebody supplied on purpose. Asserting it
        // through the wrong door was this test's first mistake, and it would have read as the
        // vocabulary being wrong rather than the test being pointed at the wrong seam.
        var lowered = Graph.Resolve(AssertionSource.Board, $"experience with {lower} preferred");
        Assert.DoesNotContain(key, lowered.Assertions.Select(a => a.ConceptKey));

        var capitalised = Graph.Resolve(
            AssertionSource.Board, $"experience with {lower.ToUpperInvariant()} preferred");
        Assert.Contains(key, capitalised.Assertions.Select(a => a.ConceptKey));
    }

    [Theory]
    [InlineData("rag", "skill.rag")]
    [InlineData("MCP", "skill.mcp")]
    public void A_form_supplied_on_purpose_resolves_whatever_its_case(string form, string key)
        // A board's skills field, or the model's own unknown list. Somebody typed it deliberately,
        // so the capital rule - which exists to stop false hits in prose - does not apply.
        => Assert.Equal(key, Resolve(form, fromStructuredField: true));

    // -----------------------------------------------------------------------
    // Round two, 2026-08-31, from reading the log again once round one had cleared its top
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("CrewAI", "skill.crewai")]
    [InlineData("AutoGen", "skill.autogen")]
    [InlineData("Gemini", "skill.gemini")]
    [InlineData("vLLM", "skill.vllm")]
    [InlineData("LangSmith", "skill.langsmith")]
    [InlineData("Vertex AI", "skill.vertex-ai")]
    [InlineData("Delta Lake", "skill.delta-lake")]
    [InlineData("Microsoft Fabric", "skill.microsoft-fabric")]
    [InlineData("Azure Service Bus", "skill.azure-service-bus")]
    [InlineData("Redux", "skill.redux")]
    [InlineData("n8n", "skill.n8n")]
    [InlineData("iOS", "skill.ios")]
    [InlineData("Power Platform", "skill.power-platform")]
    [InlineData("Power Apps", "skill.power-apps")]
    [InlineData("event-driven-architecture", "skill.event-driven")]
    public void Round_two_resolves_the_form_the_corpus_uses(string form, string key)
        => Assert.Equal(key, Resolve(form));

    [Fact]
    public void An_existing_concept_can_absorb_a_form_instead_of_a_new_concept_being_added()
    {
        // Cheaper and more honest than a new concept: "cloud-native" is the cloud domain a board
        // tagged, not a technology of its own, and "gitlab" in a skills field means the platform
        // this vocabulary already knows through its CI. These are domains, so they resolve only
        // from a structured field - which is exactly where a board tag comes from.
        Assert.Equal("area.cloud", Resolve("cloud-native", fromStructuredField: true));
        Assert.Equal("area.ml", Resolve("data-science", fromStructuredField: true));
        Assert.Equal("skill.gitlab-ci", Resolve("gitlab"));
    }

    [Fact]
    public void The_two_that_were_measured_and_declined_are_still_absent()
    {
        // Restraint, pinned so a later reader does not "fix" it.
        //
        // Triton names two unrelated technologies - NVIDIA's inference server and OpenAI's GPU
        // kernel language - and nothing in an advert says which. Asserting either would invent
        // evidence; the mention log records it honestly instead.
        //
        // skill.observability is a KEY the model proposed, mirroring the area.observability
        // domain that already exists and is tagOnly because in prose the word means nothing.
        // That is the model guessing at the key scheme, not a gap.
        Assert.False(Graph.TryGet("skill.triton", out _));
        Assert.False(Graph.TryGet("skill.observability", out _));
        Assert.True(Graph.TryGet("area.observability", out var area));
        Assert.False(area.IsDiscriminating);
    }

    [Fact]
    public void The_additions_did_not_disturb_the_names_already_refused()
    {
        // Go, C and R were ambiguous before this and must still be. An addition that quietly
        // made one of them resolvable would put a false spike into the demand series, which is
        // the exact harm the ambiguity rule exists to prevent.
        Assert.Null(Resolve("go"));
        Assert.Null(Resolve("c"));
        Assert.Null(Resolve("r"));
        Assert.Null(Resolve("containers"));
    }

    // -----------------------------------------------------------------------
    // Shape
    // -----------------------------------------------------------------------

    [Fact]
    public void Every_addition_carries_exactly_one_type_and_is_discriminating()
    {
        // The vocabulary's own rule: a skill has exactly one type.* parent. And none of these is
        // tagOnly, so each can carry a match on its own - which is the point of adding them,
        // since a posting whose only readable requirement is "Claude Code" is a real signal.
        string[] added =
        [
            "skill.claude-code", "skill.cursor", "skill.github-copilot", "skill.codex",
            "skill.rag", "skill.mcp", "skill.langgraph", "skill.llamaindex",
            "skill.vector-databases", "skill.prompt-engineering", "skill.anthropic",
            "skill.openai", "skill.cuda", "skill.jax", "skill.html", "skill.css",
            "skill.nosql", "skill.android", "skill.s3", "skill.ecs", "skill.iam",
            "skill.salesforce", "skill.servicenow", "skill.power-automate",
        ];

        foreach (var key in added)
        {
            Assert.True(Graph.TryGet(key, out var concept), key);
            Assert.Equal(ConceptKind.Skill, concept.Kind);
            Assert.True(concept.IsDiscriminating, key + " must be able to carry a match");

            var types = Graph.Ancestors(key).Keys.Count(k => k.StartsWith("type.", StringComparison.Ordinal));
            Assert.True(types == 1, $"{key} has {types} type.* parents, expected exactly 1");
        }
    }

    [Fact]
    public void The_entailments_hold()
    {
        // S3 and ECS are AWS services, so holding one is holding AWS. RAG and MCP are things you
        // do with an LLM. These edges are what let a candidate who wrote "S3" match a posting
        // asking for AWS, which is most of the value of adding them.
        Assert.Contains("skill.aws", Graph.TryGet("skill.s3", out var s3) ? s3.Implies : []);
        Assert.Contains("skill.aws", Graph.TryGet("skill.ecs", out var ecs) ? ecs.Implies : []);
        Assert.Contains("skill.llms", Graph.TryGet("skill.rag", out var rag) ? rag.Implies : []);
        Assert.Contains("skill.llms", Graph.TryGet("skill.mcp", out var mcp) ? mcp.Implies : []);
    }
}
