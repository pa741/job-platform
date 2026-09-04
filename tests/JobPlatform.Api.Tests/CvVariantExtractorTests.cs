using System.Reflection;
using JobPlatform.Ai;
using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The pass that turns a CV variant's markdown into the concepts a selection is scored on.
/// </summary>
/// <remarks>
/// Two different things are pinned here and they are worth separating.
///
/// The first is the contract every extractor in this layer carries and which cannot be assumed
/// twice: that the vocabulary reaches the model as its allowed output set, that a key it invents
/// is demoted to a mention rather than trusted, that a bad response degrades to null instead of
/// throwing, and that the call is recorded whatever happens to it. Those are asserted again rather
/// than inherited from <c>DocumentExtractorTests</c>, because this is a second implementation and
/// the guarantee is only as good as the weaker of the two.
///
/// The second is the guard that is specific to this path and is the reason it exists as a separate
/// component at all: <b>a variant's concepts feed selection only</b>. They must never reach
/// <c>ProfileConcepts</c>, never move a match score, and never widen what the candidate is judged
/// to have. That is asserted structurally - against what the types will and will not admit - rather
/// than behaviourally, because a behavioural test proves what today's callers happen to do and this
/// needs to hold for the ones nobody has written yet.
/// </remarks>
public sealed class CvVariantExtractorTests
{
    private const string ExampleCv =
        """
        ## Backend engineer

        Six years building services in C#. Ran the Kubernetes platform two teams deployed to.
        """;

    private static KernelCvVariantExtractor Extractor(string response, IAiCallLog? callLog = null)
    {
        var builder = Kernel.CreateBuilder();
        Register(builder, new StubChatService(response));

        return new KernelCvVariantExtractor(
            builder.Build(),
            Options.Create(new AzureOpenAiOptions { BulkDeployment = "bulk" }),
            logger: null,
            callLog);
    }

    /// <summary>
    /// Registers the stub under the service id the bulk prompt selects.
    /// </summary>
    /// <remarks>
    /// Keyed, not plain, exactly as the document extractor's tests do it. The Kernel carries two
    /// chat services and every prompt names which one it wants, so a stub registered without a key
    /// is simply not found - and registering it the way production does is what makes these tests
    /// evidence that a CV is read by the cheap deployment rather than the writing one.
    /// </remarks>
    private static void Register(IKernelBuilder builder, StubChatService stub)
        => builder.Services.AddKeyedSingleton<IChatCompletionService>(
            AzureOpenAiOptions.BulkServiceId, stub);

    private static CvVariantExtractionRequest Request(string markdown = ExampleCv)
        => new(41, markdown, "Backend .NET");

    // -----------------------------------------------------------------------
    // Markdown in, resolved concepts out.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_well_formed_response_becomes_assertions()
    {
        var extractor = Extractor(
            """
            {
              "concepts": [
                {"key": "skill.csharp", "polarity": "required", "yearsMin": 6,
                 "evidence": "Six years building services in C#", "confidence": 0.9},
                {"key": "skill.kubernetes", "polarity": "preferred", "confidence": 0.7}
              ],
              "unknownSkills": []
            }
            """);

        var result = await extractor.ExtractAsync(Request());

        Assert.NotNull(result);
        Assert.Equal(2, result.Concepts.Count);

        var csharp = result.Concepts.Single(c => c.ConceptKey == "skill.csharp");
        Assert.Equal(AssertionPolarity.Required, csharp.Polarity);
        Assert.Equal(6, csharp.YearsMin);
        Assert.Equal("Six years building services in C#", csharp.EvidenceText);

        Assert.Equal(
            AssertionPolarity.Preferred,
            result.Concepts.Single(c => c.ConceptKey == "skill.kubernetes").Polarity);
    }

    [Fact]
    public async Task Every_assertion_read_from_a_cv_is_labelled_as_the_model_s()
    {
        // CvVariantConcepts carries Source in its primary key the way ProfileConcepts does, so a
        // row that lied about where it came from would be a row a later pass could not replace.
        var extractor = Extractor("""{"concepts": [{"key": "skill.python"}]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Equal(AssertionSource.Model, Assert.Single(result!.Concepts).Source);
    }

    [Fact]
    public async Task An_invented_concept_key_cannot_enter_the_data_as_an_assertion()
    {
        // The same re-check the posting and profile passes apply, asserted again because this is a
        // second implementation of the path and the guarantee is only as strong as the weaker one.
        // A hallucinated key looks exactly like a real one in SQL and would split a concept in two
        // on the side of the join nobody audits.
        var extractor = Extractor(
            """{"concepts": [{"key": "skill.not-a-real-concept", "polarity": "required"}]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Empty(result!.Concepts);

        var mention = Assert.Single(result.Mentions);
        Assert.Equal("skill.not-a-real-concept", mention.SurfaceForm);
        Assert.Equal(MentionReason.UnknownModelSkill, mention.Reason);
    }

    [Fact]
    public async Task An_unknown_skill_the_vocabulary_knows_by_another_name_is_resolved()
    {
        // The prompt sends key = label and no aliases, so a CV saying "generative AI" is reported
        // unknown by a model that has only seen `skill.llms = LLMs`. Asking the graph before
        // believing it is what stops a real skill from becoming a mention - and it is inherited
        // here rather than reimplemented, which is the whole reason this pass calls the shared
        // reader instead of parsing for itself.
        var extractor = Extractor("""{"concepts": [], "unknownSkills": ["generative AI"]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Equal("skill.llms", Assert.Single(result!.Concepts).ConceptKey);
        Assert.Empty(result.Mentions);
    }

    [Fact]
    public async Task A_technology_the_vocabulary_has_no_concept_for_is_recorded_rather_than_dropped()
    {
        // A variant is the one document in this system written by a person choosing their own
        // words, which makes it the richest source of vocabulary the corpus has. Discarding these
        // is the failure PostingMentions exists to prevent.
        var extractor = Extractor("""{"concepts": [], "unknownSkills": ["Frobnicator 9000"]}""");

        var result = await extractor.ExtractAsync(Request());

        Assert.Contains(result!.Mentions, m => m.SurfaceForm == "Frobnicator 9000");
    }

    [Fact]
    public async Task A_fenced_response_still_parses()
    {
        var extractor = Extractor(
            """
            Here is the reading:

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
        Assert.Null(await Extractor("I'm sorry, I can't help with that.").ExtractAsync(Request()));
    }

    [Fact]
    public async Task Malformed_json_returns_null_rather_than_throwing()
    {
        Assert.Null(await Extractor("""{"concepts": [{"key": }]}""").ExtractAsync(Request()));
    }

    [Fact]
    public async Task A_blank_variant_is_not_sent_to_the_model()
    {
        // CvVariant.Create refuses a blank CV outright, so this is reachable only through a row an
        // older build wrote. Answering it costs nothing; paying a model call for it costs money and
        // returns an empty reading that looks exactly like a CV saying nothing.
        var stub = new StubChatService("""{"concepts": []}""");
        var builder = Kernel.CreateBuilder();
        Register(builder, stub);

        var extractor = new KernelCvVariantExtractor(
            builder.Build(), Options.Create(new AzureOpenAiOptions()));

        Assert.Null(await extractor.ExtractAsync(new CvVariantExtractionRequest(41, "   ")));
        Assert.Equal(0, stub.Calls);
    }

    // -----------------------------------------------------------------------
    // The prompt: what the model is given, and what it is told not to answer.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_prompt_carries_the_vocabulary_the_label_and_the_markdown()
    {
        var stub = await PromptFor(Request());

        Assert.Contains("skill.kubernetes = Kubernetes", stub.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Backend .NET", stub.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Six years building services in C#", stub.LastPrompt, StringComparison.Ordinal);

        // Domains are structural and are reached through the closure. Sending them would only
        // invite the model to assert one directly, which is the same reason they are withheld from
        // the posting prompt.
        Assert.DoesNotContain("area.backend", stub.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_prompt_offers_no_field_for_anything_selection_may_not_read()
    {
        // The type has nowhere to put a seniority, an arrangement or a salary; this asserts the
        // prompt agrees. Asking for them would pay for tokens that are thrown away and, worse,
        // would leave the next reader of that prompt believing this pass reads them.
        var stub = await PromptFor(Request());

        Assert.DoesNotContain("\"seniority\"", stub.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("workArrangement", stub.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"salary\"", stub.LastPrompt, StringComparison.Ordinal);

        // And says so in words, because a schema that merely omits a field is an invitation to
        // volunteer one.
        Assert.Contains(
            "Report nothing about salary, seniority, location or working arrangement",
            stub.LastPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_is_told_to_read_only_what_the_cv_says()
    {
        // The instruction this pass turns on. A model reading "Senior Platform Engineer, AWS" will
        // helpfully offer Terraform and Kubernetes because the role usually implies them, and every
        // one of those is a claim the candidate did not make, scored as though they had.
        var stub = await PromptFor(Request());

        Assert.Contains("Read only what this CV says", stub.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cv_at_the_full_stored_length_reaches_the_model_whole()
    {
        // Nothing here truncates, and that is a decision rather than an oversight. An advert
        // front-loads its requirements so losing its tail costs nothing; a CV puts the older roles
        // at the bottom, which is exactly where the concepts distinguishing one variant from
        // another live. A silently narrowed variant stops winning the postings it was written for,
        // and a variant that is not chosen looks exactly like one nobody needed.
        var markdown = new string('x', CvVariantLimits.MaxMarkdownLength - 20) + "\n\nSeen the Rust.";

        var stub = await PromptFor(new CvVariantExtractionRequest(41, markdown, "Long"));

        Assert.Contains("Seen the Rust.", stub.LastPrompt, StringComparison.Ordinal);
    }

    private static async Task<StubChatService> PromptFor(CvVariantExtractionRequest request)
    {
        var stub = new StubChatService("""{"concepts": []}""");
        var builder = Kernel.CreateBuilder();
        Register(builder, stub);

        var extractor = new KernelCvVariantExtractor(
            builder.Build(), Options.Create(new AzureOpenAiOptions()));

        await extractor.ExtractAsync(request);

        return stub;
    }

    // -----------------------------------------------------------------------
    // The guard. These concepts feed selection and nothing else.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Nothing_the_model_volunteers_beyond_concepts_survives_the_call()
    {
        // The model is asked for none of this and may offer it anyway - a prompt is a request, not
        // a guarantee, which is the same reason every key is re-checked. There is nowhere for it to
        // land, so it is gone by the time the caller sees anything.
        var extractor = Extractor(
            """
            {
              "concepts": [{"key": "skill.csharp"}],
              "seniority": "principal",
              "workArrangement": "remote",
              "salary": {"min": 999000, "max": 999000, "currency": "GBP", "confidence": 1.0}
            }
            """);

        var result = await extractor.ExtractAsync(Request());

        Assert.NotNull(result);
        Assert.Equal("skill.csharp", Assert.Single(result.Concepts).ConceptKey);

        // Asserted over the type rather than over this instance: a field added later would make a
        // value assertion pass and this one fail, which is the right way round.
        var carried = typeof(CvVariantExtraction)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(["Concepts", "Mentions", "Version"], [.. carried.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void A_variant_reading_cannot_be_handed_to_the_profile_writer()
    {
        // The structural half of the selection-only rule, and the reason this pass returns its own
        // type rather than a DocumentExtraction. ApplyExtractionAsync is the one method in the
        // system that writes ProfileConcepts from a model's reading of a document; a CV is written
        // *from* the profile, so letting its reading back in would let a document inflate the
        // record it came from and the apply loop would then be applying to jobs on the strength of
        // its own prose.
        //
        // Asserted against the type system rather than against a caller's behaviour, because a
        // behavioural test proves only what today's callers happen to do.
        var writer = typeof(CandidateProfileRepository)
            .GetMethod(nameof(CandidateProfileRepository.ApplyExtractionAsync));

        Assert.NotNull(writer);

        var accepted = writer.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.DoesNotContain(typeof(CvVariantExtraction), accepted);
        Assert.DoesNotContain(typeof(CvVariantExtractionRequest), accepted);

        // Not merely absent from that signature: not convertible into what the signature demands,
        // so no `with`, no implicit conversion and no inheritance can reintroduce it.
        Assert.All(
            accepted,
            parameter => Assert.False(parameter.IsAssignableFrom(typeof(CvVariantExtraction))));
    }

    [Fact]
    public void Nothing_on_the_profile_repository_accepts_a_variant_s_concepts_as_a_unit()
    {
        // The wider version of the same question: no method on the profile's writer takes anything
        // this pass produces. It leaves the ConceptAssertion list reachable - that type is shared
        // deliberately, because CvVariantConcepts mirrors ProfileConcepts column for column and
        // selection is a join between identical shapes - and the thing that has to be impossible is
        // handing over the *reading*, which is what a caller in a hurry would do.
        var offered = new[] { typeof(CvVariantExtraction), typeof(CvVariantExtractionRequest) };

        var parameters = typeof(CandidateProfileRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.All(offered, type => Assert.DoesNotContain(type, parameters));
    }

    [Fact]
    public void A_variant_with_no_concepts_cannot_win_a_selection()
    {
        // The degraded behaviour, end to end, and the reason a missing provider is survivable here.
        // No extractor means no concepts; no concepts means no score; no score means the floor is
        // not cleared and the pass abstains. The candidate is told that no CV fits - which is the
        // answer this whole feature was built to give - rather than being sent a document nobody
        // scored, which is the failure it replaces and is invisible when it happens.
        var selection = CvVariantSelector.Select(
            [new ConceptAssertion("skill.kubernetes", AssertionSource.Model, AssertionPolarity.Required)],
            [new CvVariantFacts { VariantId = 41, Label = "Backend .NET", ConceptKeys = [] }]);

        Assert.Equal(CvSelectionOutcome.NoFit, selection.Outcome);
        Assert.Null(selection.Chosen);
        Assert.Contains(selection.Missing, gap => gap.RequiredKey == "skill.kubernetes");
    }

    // -----------------------------------------------------------------------
    // The ledger. Every model call leaves a record, including the ones that fail.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_successful_read_is_recorded_under_its_own_operation_name()
    {
        var log = new RecordingAiCallLog();

        await Extractor("""{"concepts": [{"key": "skill.csharp"}]}""", log).ExtractAsync(Request());

        var record = Assert.Single(log.Records);

        Assert.Equal(KernelCvVariantExtractor.LedgerOperation, record.Operation);
        Assert.Equal("cv-variant-extraction", record.Operation);
        Assert.Equal(AiCallOutcome.Succeeded, record.Outcome);
        Assert.Equal(1, record.Requested);
        Assert.Equal(1, record.Returned);
        Assert.Equal("bulk", record.Deployment);
        Assert.Empty(record.AffectedIds);
    }

    [Fact]
    public async Task A_failed_read_names_the_variant_that_kept_no_concepts()
    {
        // This is the loss the ledger exists for on this path, and it is a slow one: a variant with
        // no concepts scores zero against every posting and is simply never chosen, so the symptom
        // is an application that never happens while a perfectly good CV sits in the library. A
        // count is the only thing that separates that from a genuine gap.
        var log = new RecordingAiCallLog();

        await Extractor("not json", log).ExtractAsync(Request());

        var record = Assert.Single(log.Records);

        Assert.Equal(AiCallOutcome.Failed, record.Outcome);
        Assert.Equal(0, record.Returned);
        Assert.Equal([41L], record.AffectedIds);
        Assert.Equal("response carried no JSON object", record.Reason);
    }

    [Fact]
    public void The_variant_pass_is_named_apart_from_the_two_it_resembles()
    {
        // Different volumes, different costs and different failure consequences. A rate limit
        // cannot be this one's problem the way it was the corpus backfill's - it runs when somebody
        // saves a document - and averaging the three would hide what each is actually doing.
        Assert.NotEqual(
            KernelCvVariantExtractor.LedgerOperation,
            KernelDocumentExtractor.LedgerOperation(DocumentKind.Profile));

        Assert.NotEqual(
            KernelCvVariantExtractor.LedgerOperation,
            KernelDocumentExtractor.LedgerOperation(DocumentKind.Posting));
    }

    [Fact]
    public async Task A_ledger_that_throws_does_not_cost_the_reading_it_was_recording()
    {
        // The interface says implementations must not throw, and the cost of that comment being
        // wrong is losing the work the call just paid for. Same treatment as every other call site.
        var result = await Extractor("""{"concepts": [{"key": "skill.csharp"}]}""", new ThrowingAiCallLog())
            .ExtractAsync(Request());

        Assert.Equal("skill.csharp", Assert.Single(result!.Concepts).ConceptKey);
    }

    // -----------------------------------------------------------------------
    // Registration.
    // -----------------------------------------------------------------------

    [Fact]
    public void No_variant_extractor_is_registered_when_no_provider_is_configured()
    {
        // The configuration this system actually ships in. Nothing resolves and nothing throws; the
        // caller skips the step, and the pass that would have used the concepts abstains.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiProvider(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ICvVariantExtractor>());
        Assert.Null(provider.GetService<Kernel>());
    }

    [Fact]
    public void The_variant_extractor_is_registered_beside_the_others_when_a_provider_is()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAiProvider(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AzureOpenAiOptions.ProviderKey] = "azureopenai",
                [$"{AzureOpenAiOptions.SectionName}:Endpoint"] = "https://example.openai.azure.com/",
            })
            .Build());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<KernelCvVariantExtractor>(provider.GetService<ICvVariantExtractor>());

        // Registered in the same block as the extractor it deliberately is not, which is what makes
        // "either both or neither" true rather than merely intended.
        Assert.NotNull(provider.GetService<IDocumentExtractor>());
    }

    private sealed class RecordingAiCallLog : IAiCallLog
    {
        public List<AiCallRecord> Records { get; } = [];

        public Task RecordAsync(AiCallRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAiCallLog : IAiCallLog
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("the ledger is down");
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
