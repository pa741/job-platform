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
/// What is worth pinning here is not the model's judgement — that cannot be tested — but the
/// contract around it: that the prompt renders, that a fenced or prose-wrapped body still
/// parses, that a hallucinated concept key cannot enter the data as an assertion, and that a
/// bad response degrades to null rather than throwing.
/// </remarks>
public sealed class DocumentExtractorTests
{
    private static KernelDocumentExtractor Extractor(string response)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(new StubChatService(response));

        return new KernelDocumentExtractor(
            builder.Build(),
            Options.Create(new AiProviderOptions { Model = "claude-opus-5" }));
    }

    private static ExtractionRequest Request(string text = "We use C# and Kubernetes.")
        => new(DocumentKind.Posting, text, "Senior Backend Engineer");

    [Fact]
    public async Task A_well_formed_response_becomes_assertions()
    {
        var extractor = Extractor(
            """
            {
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
        var extractor = Extractor("""{"concepts": [{"key": "skill.python"}]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Equal(AssertionSource.Model, Assert.Single(result!.Concepts).Source);
    }

    [Fact]
    public async Task An_invented_concept_key_cannot_enter_the_data_as_an_assertion()
    {
        // The failure this guards is the worst one available: a hallucinated key looks exactly
        // like a real one in SQL, and would quietly split a concept in two forever.
        var extractor = Extractor(
            """{"concepts": [{"key": "skill.not-a-real-concept", "polarity": "required"}]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Empty(result!.Concepts);

        var mention = Assert.Single(result.Mentions);
        Assert.Equal("skill.not-a-real-concept", mention.SurfaceForm);
        Assert.Equal(MentionReason.UnknownModelSkill, mention.Reason);
    }

    [Fact]
    public async Task Unknown_skills_are_recorded_rather_than_forced_into_the_vocabulary()
    {
        var extractor = Extractor("""{"concepts": [], "unknownSkills": ["Frobnicator 9000"]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Contains(result!.Mentions, m => m.SurfaceForm == "Frobnicator 9000");
    }

    [Fact]
    public async Task A_fenced_response_still_parses()
    {
        // Semantic Kernel's execution settings are provider-neutral and cannot express
        // Anthropic's structured-output constraint, so this is expected rather than unusual.
        var extractor = Extractor(
            """
            Here is the extraction:

            ```json
            {"concepts": [{"key": "skill.terraform"}]}
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
        var extractor = Extractor("""{"concepts": [{"key": }]}""");

        Assert.Null(await extractor.ExtractAsync(Request()));
    }

    [Fact]
    public async Task An_empty_document_is_not_sent_to_the_model()
    {
        var stub = new StubChatService("""{"concepts": []}""");
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(stub);

        var extractor = new KernelDocumentExtractor(
            builder.Build(), Options.Create(new AiProviderOptions()));

        Assert.Null(await extractor.ExtractAsync(new ExtractionRequest(DocumentKind.Posting, "  ")));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task The_prompt_carries_the_vocabulary_and_the_document()
    {
        var stub = new StubChatService("""{"concepts": []}""");
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(stub);

        var extractor = new KernelDocumentExtractor(
            builder.Build(), Options.Create(new AiProviderOptions()));

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
        var stub = new StubChatService("""{"concepts": []}""");
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(stub);

        var extractor = new KernelDocumentExtractor(
            builder.Build(), Options.Create(new AiProviderOptions()));

        await extractor.ExtractAsync(new ExtractionRequest(DocumentKind.Profile, "Ten years of C#."));

        Assert.Contains("candidate CV", stub.LastPrompt, StringComparison.Ordinal);
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
