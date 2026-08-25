using JobPlatform.Ai;
using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Enrichment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The model pass, driven by a stub chat service so no key and no network are involved.
/// </summary>
/// <remarks>
/// What is worth pinning here is not the model's judgement - that cannot be tested - but the
/// contract around it: that the prompt renders, that a fenced or prose-wrapped body still
/// parses, that a hallucinated concept key cannot enter the data as an assertion, that a bad
/// response degrades to null rather than throwing, and above all that a batched answer lands
/// against the document it actually describes.
///
/// That last one is the reason several of these tests exist at all. Extraction now sends many
/// postings per call, and an answer attached to the wrong posting is the worst failure this
/// class can produce: the data would be wrong, internally consistent, and undetectable
/// afterwards.
/// </remarks>
public sealed class DocumentExtractorTests
{
    private static KernelDocumentExtractor Extractor(string response)
    {
        var builder = Kernel.CreateBuilder();
        Register(builder, new StubChatService(response));

        return new KernelDocumentExtractor(
            builder.Build(),
            Options.Create(new AzureOpenAiOptions { BulkDeployment = "gpt-5-6-luna" }));
    }

    /// <summary>
    /// Registers the stub under the service id the bulk prompt selects.
    /// </summary>
    /// <remarks>
    /// Keyed, not plain. The Kernel carries two chat services and every prompt names which one
    /// it wants, so a stub registered without a key is simply not found - Semantic Kernel
    /// throws, the extractor catches it and returns nulls, and the test fails somewhere far
    /// from the cause. Registering it the way production does is also what makes these tests
    /// evidence that the routing works at all.
    /// </remarks>
    private static void Register(IKernelBuilder builder, StubChatService stub)
        => builder.Services.AddKeyedSingleton<IChatCompletionService>(
            AzureOpenAiOptions.BulkServiceId, stub);

    private static ExtractionRequest Request(string text = "We use C# and Kubernetes.")
        => new(DocumentKind.Posting, text, "Senior Backend Engineer");

    [Fact]
    public async Task A_well_formed_response_becomes_assertions()
    {
        var extractor = Extractor(
            """
            {
              "documents": [{
              "index": 0,
              "concepts": [
                {"key": "skill.csharp", "polarity": "required", "yearsMin": 5,
                 "evidence": "5 years of C#", "confidence": 0.9},
                {"key": "skill.kubernetes", "polarity": "preferred", "confidence": 0.7}
              ],
              "unknownSkills": ["Contoso Internal Platform"],
              "seniority": "senior",
              "workArrangement": "hybrid",
              "hybridDaysInOffice": 3,
              "salary": {"min": 75000, "max": 95000, "currency": "GBP", "confidence": 0.8}
              }]
            }
            """);

        var result = await extractor.ExtractAsync(Request());

        Assert.NotNull(result);
        Assert.Equal(2, result.Concepts.Count);

        var csharp = result.Concepts.Single(c => c.ConceptKey == "skill.csharp");
        Assert.Equal(AssertionPolarity.Required, csharp.Polarity);
        Assert.Equal(5, csharp.YearsMin);
        Assert.Equal("5 years of C#", csharp.EvidenceText);

        // Only the model can tell required from nice-to-have; this is the whole reason it runs.
        Assert.Equal(
            AssertionPolarity.Preferred,
            result.Concepts.Single(c => c.ConceptKey == "skill.kubernetes").Polarity);

        Assert.Equal(Seniority.Senior, result.Seniority);
        Assert.Equal(WorkArrangement.Hybrid, result.WorkArrangement);
        Assert.Equal(3, result.HybridDaysInOffice);
        Assert.Equal(75_000m, result.AnnualSalaryMin);
        Assert.Equal("GBP", result.SalaryCurrency);
    }

    [Fact]
    public async Task Every_model_assertion_is_labelled_as_one()
    {
        var extractor = Extractor("""{"documents": [{"index": 0, "concepts": [{"key": "skill.python"}]}]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Equal(AssertionSource.Model, Assert.Single(result!.Concepts).Source);
    }

    [Fact]
    public async Task An_invented_concept_key_cannot_enter_the_data_as_an_assertion()
    {
        // The failure this guards is the worst one available: a hallucinated key looks exactly
        // like a real one in SQL, and would quietly split a concept in two forever.
        var extractor = Extractor(
            """{"documents": [{"index": 0, "concepts": [{"key": "skill.not-a-real-concept", "polarity": "required"}]}]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Empty(result!.Concepts);

        var mention = Assert.Single(result.Mentions);
        Assert.Equal("skill.not-a-real-concept", mention.SurfaceForm);
        Assert.Equal(MentionReason.UnknownModelSkill, mention.Reason);
    }

    [Fact]
    public async Task Unknown_skills_are_recorded_rather_than_forced_into_the_vocabulary()
    {
        var extractor = Extractor("""{"documents": [{"index": 0, "concepts": [], "unknownSkills": ["Frobnicator 9000"]}]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Contains(result!.Mentions, m => m.SurfaceForm == "Frobnicator 9000");
    }

    [Fact]
    public async Task A_fenced_response_still_parses()
    {
        // The Azure OpenAI connector can ask for JSON mode, so this is no longer the normal
        // case - but the net stays and stays tested. A response format is a request to a
        // provider, not a property of the transport, and the day it is not honoured should cost
        // one posting rather than the whole batch.
        var extractor = Extractor(
            """
            Here is the extraction:

            ```json
            {"documents": [{"index": 0, "concepts": [{"key": "skill.terraform"}]}]}
            ```
            """);

        var result = await extractor.ExtractAsync(Request());

        Assert.Equal("skill.terraform", Assert.Single(result!.Concepts).ConceptKey);
    }

    [Fact]
    public async Task A_response_with_no_json_returns_null_rather_than_throwing()
    {
        var extractor = Extractor("I'm sorry, I can't help with that.");

        Assert.Null(await extractor.ExtractAsync(Request()));
    }

    [Fact]
    public async Task Malformed_json_returns_null_rather_than_throwing()
    {
        var extractor = Extractor("""{"documents": [{"index": 0, "concepts": [{"key": }]}]}""");

        Assert.Null(await extractor.ExtractAsync(Request()));
    }

    [Fact]
    public async Task An_empty_document_is_not_sent_to_the_model()
    {
        var stub = new StubChatService("""{"documents": [{"index": 0, "concepts": []}]}""");
        var builder = Kernel.CreateBuilder();
        Register(builder, stub);

        var extractor = new KernelDocumentExtractor(
            builder.Build(), Options.Create(new AzureOpenAiOptions()));

        Assert.Null(await extractor.ExtractAsync(new ExtractionRequest(DocumentKind.Posting, "  ")));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task The_prompt_carries_the_vocabulary_and_the_document()
    {
        var stub = new StubChatService("""{"documents": [{"index": 0, "concepts": []}]}""");
        var builder = Kernel.CreateBuilder();
        Register(builder, stub);

        var extractor = new KernelDocumentExtractor(
            builder.Build(), Options.Create(new AzureOpenAiOptions()));

        await extractor.ExtractAsync(new ExtractionRequest(
            DocumentKind.Posting, "We use Kubernetes.", "Platform Engineer"));

        Assert.Contains("skill.kubernetes = Kubernetes", stub.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("We use Kubernetes.", stub.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Platform Engineer", stub.LastPrompt, StringComparison.Ordinal);

        // Domains are structural and are reached through the closure, so sending them would
        // only invite the model to assert one directly.
        Assert.DoesNotContain("area.backend", stub.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_profile_and_a_posting_use_the_same_extractor()
    {
        // The CV contract, such as it exists today: pointing this at a profile is a change to
        // one prompt argument, not a second component.
        var stub = new StubChatService("""{"documents": [{"index": 0, "concepts": []}]}""");
        var builder = Kernel.CreateBuilder();
        Register(builder, stub);

        var extractor = new KernelDocumentExtractor(
            builder.Build(), Options.Create(new AzureOpenAiOptions()));

        await extractor.ExtractAsync(new ExtractionRequest(DocumentKind.Profile, "Ten years of C#."));

        Assert.Contains("candidate profile", stub.LastPrompt, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Batching. The saving is real and so is the failure mode it introduces.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Several_documents_travel_in_one_call()
    {
        // The entire economic argument for batching: the vocabulary is several thousand tokens
        // and precedes every extraction, so paying for it once per call rather than once per
        // posting is most of what a corpus-wide pass costs.
        var stub = new StubChatService(
            """
            {"documents": [
              {"index": 0, "concepts": [{"key": "skill.csharp"}]},
              {"index": 1, "concepts": [{"key": "skill.python"}]},
              {"index": 2, "concepts": [{"key": "skill.rust"}]}
            ]}
            """);

        var extractor = Batched(stub, batchSize: 10);

        var results = await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "C# role"),
            new ExtractionRequest(DocumentKind.Posting, "Python role"),
            new ExtractionRequest(DocumentKind.Posting, "Rust role"),
        ]);

        Assert.Equal(1, stub.Calls);
        Assert.Equal(3, results.Count);
        Assert.Equal("skill.csharp", Assert.Single(results[0]!.Concepts).ConceptKey);
        Assert.Equal("skill.python", Assert.Single(results[1]!.Concepts).ConceptKey);
        Assert.Equal("skill.rust", Assert.Single(results[2]!.Concepts).ConceptKey);
    }

    [Fact]
    public async Task A_reordered_response_still_lands_on_the_right_document()
    {
        // The index is authoritative, not the position in the array. A model that answers out
        // of order is answering correctly.
        var extractor = Batched(new StubChatService(
            """
            {"documents": [
              {"index": 2, "concepts": [{"key": "skill.rust"}]},
              {"index": 0, "concepts": [{"key": "skill.csharp"}]},
              {"index": 1, "concepts": [{"key": "skill.python"}]}
            ]}
            """));

        var results = await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "C# role"),
            new ExtractionRequest(DocumentKind.Posting, "Python role"),
            new ExtractionRequest(DocumentKind.Posting, "Rust role"),
        ]);

        Assert.Equal("skill.csharp", Assert.Single(results[0]!.Concepts).ConceptKey);
        Assert.Equal("skill.python", Assert.Single(results[1]!.Concepts).ConceptKey);
        Assert.Equal("skill.rust", Assert.Single(results[2]!.Concepts).ConceptKey);
    }

    [Fact]
    public async Task An_out_of_range_index_is_dropped_rather_than_clamped()
    {
        // Clamping would write one posting's requirements onto another: wrong, self-consistent,
        // and impossible to spot afterwards. A dropped document is simply re-extracted later.
        var extractor = Batched(new StubChatService(
            """
            {"documents": [
              {"index": 0, "concepts": [{"key": "skill.csharp"}]},
              {"index": 7, "concepts": [{"key": "skill.rust"}]}
            ]}
            """));

        var results = await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "C# role"),
            new ExtractionRequest(DocumentKind.Posting, "Python role"),
        ]);

        Assert.Equal("skill.csharp", Assert.Single(results[0]!.Concepts).ConceptKey);
        Assert.Null(results[1]);
    }

    [Fact]
    public async Task A_duplicated_index_keeps_the_first_answer_and_drops_the_rest()
    {
        var extractor = Batched(new StubChatService(
            """
            {"documents": [
              {"index": 0, "concepts": [{"key": "skill.csharp"}]},
              {"index": 0, "concepts": [{"key": "skill.rust"}]}
            ]}
            """));

        var results = await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "C# role"),
            new ExtractionRequest(DocumentKind.Posting, "Python role"),
        ]);

        Assert.Equal("skill.csharp", Assert.Single(results[0]!.Concepts).ConceptKey);
        Assert.Null(results[1]);
    }

    [Fact]
    public async Task A_short_response_leaves_the_missing_documents_null()
    {
        // Not an error. The missing postings keep no extraction row and the backfill picks
        // them up, which is strictly better than inventing an answer for them.
        var extractor = Batched(new StubChatService(
            """{"documents": [{"index": 0, "concepts": [{"key": "skill.csharp"}]}]}"""));

        var results = await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "C# role"),
            new ExtractionRequest(DocumentKind.Posting, "Python role"),
            new ExtractionRequest(DocumentKind.Posting, "Rust role"),
        ]);

        Assert.Equal(3, results.Count);
        Assert.NotNull(results[0]);
        Assert.Null(results[1]);
        Assert.Null(results[2]);
    }

    [Fact]
    public async Task The_batch_size_bounds_how_many_documents_share_a_call()
    {
        // Luna's context window is nowhere near the constraint; the output token ceiling is,
        // which is why this is configurable and why it is small.
        var stub = new StubChatService("""{"documents": [{"index": 0, "concepts": []}]}""");
        var extractor = Batched(stub, batchSize: 2);

        await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "one"),
            new ExtractionRequest(DocumentKind.Posting, "two"),
            new ExtractionRequest(DocumentKind.Posting, "three"),
            new ExtractionRequest(DocumentKind.Posting, "four"),
            new ExtractionRequest(DocumentKind.Posting, "five"),
        ]);

        Assert.Equal(3, stub.Calls);
    }

    [Fact]
    public async Task Each_stored_payload_is_its_own_document_and_not_the_whole_batch()
    {
        // Storing the batch response against every row would multiply the largest column in the
        // schema by the batch size, and leak one posting's extraction into another's audit
        // trail.
        var extractor = Batched(new StubChatService(
            """
            {"documents": [
              {"index": 0, "concepts": [{"key": "skill.csharp"}]},
              {"index": 1, "concepts": [{"key": "skill.python"}]}
            ]}
            """));

        var results = await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "C# role"),
            new ExtractionRequest(DocumentKind.Posting, "Python role"),
        ]);

        Assert.Contains("skill.csharp", results[0]!.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("skill.python", results[0]!.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_document_is_labelled_with_its_index_and_kind_in_the_prompt()
    {
        var stub = new StubChatService("""{"documents": []}""");
        var extractor = Batched(stub);

        await extractor.ExtractBatchAsync(
        [
            new ExtractionRequest(DocumentKind.Posting, "An advert.", "Platform Engineer"),
            new ExtractionRequest(DocumentKind.Profile, "Ten years of C#.", "Backend engineer"),
        ]);

        Assert.Contains("DOCUMENT 0 (job advert)", stub.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("DOCUMENT 1 (candidate profile)", stub.LastPrompt, StringComparison.Ordinal);

        // The vocabulary appears once for the whole batch. That ratio is the saving.
        Assert.Equal(
            1,
            stub.LastPrompt.Split("skill.kubernetes = Kubernetes", StringSplitOptions.None).Length - 1);
    }

    private static KernelDocumentExtractor Batched(StubChatService stub, int batchSize = 10)
    {
        var builder = Kernel.CreateBuilder();
        Register(builder, stub);

        return new KernelDocumentExtractor(
            builder.Build(), Options.Create(new AzureOpenAiOptions { BatchSize = batchSize }));
    }

    [Fact]
    public void No_extractor_is_registered_when_no_provider_is_configured()
    {
        // The configuration this system actually ships in, so it is the one that must be
        // pinned. Nothing resolves, nothing throws, and no work is enqueued for a model that
        // does not exist.
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiProvider(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IDocumentExtractor>());
        Assert.Null(provider.GetService<Kernel>());
    }

    /// <summary>Returns a canned body and records what it was asked.</summary>
    private sealed class StubChatService(string response) : IChatCompletionService
    {
        public int Calls { get; private set; }

        public string LastPrompt { get; private set; } = string.Empty;

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPrompt = string.Join("\n", chatHistory.Select(m => m.Content));

            IReadOnlyList<ChatMessageContent> result =
                [new ChatMessageContent(AuthorRole.Assistant, response)];

            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPrompt = string.Join("\n", chatHistory.Select(m => m.Content));

            yield return new StreamingChatMessageContent(AuthorRole.Assistant, response);

            await Task.CompletedTask;
        }
    }
}
