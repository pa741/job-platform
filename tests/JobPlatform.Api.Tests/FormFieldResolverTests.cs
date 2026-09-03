using System.Runtime.CompilerServices;
using JobPlatform.Ai;
using JobPlatform.Ai.Applications;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Profiles;
using JobPlatform.Core.Submissions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// What the four stages answer, and - the part that matters - what they refuse.
/// </summary>
/// <remarks>
/// Two properties are asserted here that cannot be asserted anywhere else, and both are
/// acceptance criteria rather than nice-to-haves.
///
/// <b>A model call is counted.</b> "The second occurrence of a question resolves without a model
/// call" is the whole argument for the resolution cache, and a value and a confidence look
/// identical whichever stage produced them - so the scripted chat service counts its invocations
/// and the tests assert the count, not just the answer.
///
/// <b>The prompt is captured.</b> The rules that keep this safe are structural rather than
/// prompted - a sensitive answer is not in the prompt, the profile is not in the prompt, and an
/// unrelated answer is not in the prompt - so the tests read what was actually sent rather than
/// trusting the instruction that says not to send it.
///
/// The refusals are tested against the version of the behaviour that is wrong: mapping a stored
/// answer onto the nearest option, letting a model break a tie the deterministic matcher would
/// not, and reaching for a right-to-work answer when asked about a driving licence. Every one of
/// those is what the obvious implementation does.
/// </remarks>
public sealed class FormFieldResolverTests
{
    private static readonly DateTimeOffset Given = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] NoticeOptions = ["Immediately", "2 weeks", "1 month", "3 months"];

    private static readonly string[] VagueNoticeOptions = ["Immediate", "Less than a month", "1-3 months"];

    private const string NoticeQuestion = "What is your notice period?";

    private const string RewordedNotice = "How much notice must you give your current employer?";

    private static FormAnswer Answer(
        string question, string value, string? name = null, bool sensitive = false, long id = 1)
        => FormAnswer.Create(
            question, value, AnswerScope.Global, FormAnswerSource.Candidate, Given,
            name: name, sensitive: sensitive) with { Id = id };

    private static FormFieldRequest Ask(
        string question,
        IReadOnlyList<string>? options = null,
        string? name = null,
        CandidateProfile? profile = null,
        PriorResolution? cached = null,
        params FormAnswer[] answers)
        => new()
        {
            QuestionText = question,
            Options = options,
            Name = name,
            Profile = profile,
            Cached = cached,
            Answers = answers,
        };

    /// <summary>A resolver with a scripted model behind it, and the script's call counter.</summary>
    private static (FormFieldResolver Resolver, ScriptedChatService Model) WithModel(
        string response, IAiCallLog? callLog = null)
    {
        var builder = Kernel.CreateBuilder();
        var scripted = new ScriptedChatService(response);

        builder.Services.AddKeyedSingleton<IChatCompletionService>(
            AzureOpenAiOptions.BulkServiceId, scripted);

        var resolver = new FormFieldResolver(
            Options.Create(new AzureOpenAiOptions { BulkDeployment = "bulk" }),
            builder.Build(),
            logger: null,
            callLog);

        return (resolver, scripted);
    }

    /// <summary>A resolver on a deployment with no AI provider at all.</summary>
    private static FormFieldResolver WithoutModel()
        => new(Options.Create(new AzureOpenAiOptions()), kernel: null);

    private static string Chose(int? index, double confidence, string reason = "Same question.")
        => $$"""
            {"index": {{(index is null ? "null" : index.Value.ToString())}},
             "confidence": {{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
             "reason": "{{reason}}"}
            """;

    [Fact]
    public async Task An_exact_canonical_key_is_answered_from_the_allowlist_without_a_model()
    {
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask("Email address", name: "email", profile: new CandidateProfile
            {
                SubjectId = "subject",
                Email = "someone@example.test",
            }));

        Assert.Equal(FormFieldStage.CanonicalField, result.Stage);
        Assert.Equal("someone@example.test", result.Value);
        Assert.Equal("email", result.Field);
        Assert.False(result.NeedsUser);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_question_that_folds_onto_a_catalogue_name_is_the_same_exact_key()
    {
        // Typography, not interpretation: "Full name" and full_name differ by the fold that already
        // decides two questions are one question. Nothing here turns "Email address" into `email`,
        // because that is a synonym table and a synonym table is where an allowlist stops being a
        // list somebody can read.
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask("Full name", profile: new CandidateProfile { SubjectId = "subject", FullName = "Ada Lovelace" }));

        Assert.Equal(FormFieldStage.CanonicalField, result.Stage);
        Assert.Equal("Ada Lovelace", result.Value);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_sensitive_question_is_never_answered_from_the_profile()
    {
        // The catalogue holds nothing sensitive by construction, so a sensitive question that
        // appears to match a catalogue key means the key and the question disagree - which is
        // exactly what a model helpfully filling in `name` looks like.
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask("What is your date of birth?", name: "full_name",
                profile: new CandidateProfile { SubjectId = "subject", FullName = "Ada Lovelace" }));

        Assert.True(result.NeedsUser);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_typographic_variant_of_a_stored_question_resolves_without_a_model()
    {
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask("what is your notice period", answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal(FormFieldStage.DeclaredAnswer, result.Stage);
        Assert.Equal("1 month", result.Value);
        Assert.Equal(1, result.AnswerId);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_stored_answer_maps_onto_the_option_the_form_offers()
    {
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask(NoticeQuestion, NoticeOptions, answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal("1 month", result.Value);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_stored_answer_no_option_says_is_put_back_to_the_candidate()
    {
        // "Less than a month" and "1-3 months" are each a defensible guess, and the difference is a
        // fortnight of somebody's life typed into a real form. The model is not asked to break the
        // tie either: an exact question match that cannot be rendered ends the walk, because there
        // is no more evidence to be had anywhere.
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask(NoticeQuestion, VagueNoticeOptions, answers: Answer(NoticeQuestion, "1 month")));

        Assert.True(result.NeedsUser);
        Assert.Null(result.Value);
        Assert.Equal(FormFieldStage.DeclaredAnswer, result.Stage);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_superseded_answer_is_reported_rather_than_typed()
    {
        var retracted = Answer(NoticeQuestion, "3 months") with
        {
            SupersededAtUtc = Given.AddDays(30),
        };

        var result = await WithoutModel().ResolveAsync(Ask(NoticeQuestion, answers: retracted));

        Assert.True(result.NeedsUser);
        Assert.Contains("superseded", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cached_resolution_is_reused_without_a_model_call()
    {
        // The acceptance criterion, asserted on the count rather than on the answer.
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice,
                cached: new PriorResolution(
                    Answer(NoticeQuestion, "1 month"), "notice_period", 0.93,
                    "Matched to the candidate's notice period.", Given.AddDays(1), Confirmed: false),
                answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal(FormFieldStage.Cache, result.Stage);
        Assert.False(result.ConsultedModel);
        Assert.Equal("1 month", result.Value);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_cached_refusal_is_reused_rather_than_bought_again()
    {
        // The half that saves the most: a question this system has already declined is declined for
        // the price of an index seek rather than rediscovered every run at the price of a call.
        var (resolver, model) = WithModel(Chose(0, 1));

        var result = await resolver.ResolveAsync(
            Ask("Which of our products interests you most?",
                cached: new PriorResolution(
                    null, null, 0.2, "Nothing stored is about this employer's products.",
                    Given, Confirmed: false)));

        Assert.True(result.NeedsUser);
        Assert.Equal(FormFieldStage.Cache, result.Stage);
        Assert.Contains("products", result.Rationale, StringComparison.Ordinal);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_cached_resolution_below_the_floor_is_refused_and_not_re_asked()
    {
        var (resolver, model) = WithModel(Chose(0, 0.99));

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice,
                cached: new PriorResolution(
                    Answer(NoticeQuestion, "1 month"), null, 0.5, "Might be the same question.",
                    Given, Confirmed: false)));

        Assert.True(result.NeedsUser);
        Assert.Equal(FormFieldStage.Cache, result.Stage);

        // Not re-asked: a hit ends the walk whatever it says, or the criterion above would only be
        // true of the cases nobody was worried about.
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_resolution_a_person_confirmed_outranks_the_floor()
    {
        var result = await WithoutModel().ResolveAsync(
            Ask(RewordedNotice,
                cached: new PriorResolution(
                    Answer(NoticeQuestion, "1 month"), null, 0.5, "Confirmed by the candidate.",
                    Given, Confirmed: true)));

        Assert.False(result.NeedsUser);
        Assert.Equal("1 month", result.Value);
    }

    [Fact]
    public async Task The_model_is_asked_only_where_the_first_three_stages_miss()
    {
        var (resolver, model) = WithModel(Chose(0, 0.95));

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal(FormFieldStage.Model, result.Stage);
        Assert.True(result.ConsultedModel);
        Assert.Equal("1 month", result.Value);
        Assert.Equal("bulk", result.Model);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task The_value_comes_from_the_answer_store_and_never_from_the_model()
    {
        // The model returns an index. A response that also carries a value is ignored, which is
        // what makes this safe to point at text an employer wrote: nothing the model emits is ever
        // typed into a form.
        var (resolver, _) = WithModel(
            """
            {"index": 0, "confidence": 0.95, "value": "18 months", "reason": "Same question."}
            """);

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal("1 month", result.Value);
    }

    [Fact]
    public async Task A_model_answer_below_the_confidence_floor_is_refused()
    {
        var (resolver, _) = WithModel(Chose(0, FormFieldPolicy.ConfidenceFloor - 0.05));

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        Assert.True(result.NeedsUser);
        Assert.Null(result.Field);
        Assert.Equal(FormFieldStage.Model, result.Stage);
    }

    [Fact]
    public async Task A_model_that_refuses_is_a_refusal_rather_than_an_error()
    {
        var (resolver, _) = WithModel(Chose(null, 0.4, "These are different questions."));

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        Assert.True(result.NeedsUser);
        Assert.Contains("different questions", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_index_outside_the_shortlist_is_discarded_rather_than_clamped()
    {
        // The rule KernelDocumentExtractor.Distribute follows: an answer placed against something
        // that was never sent is wrong, self-consistent and undetectable afterwards.
        var (resolver, _) = WithModel(Chose(7, 0.99));

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        Assert.True(result.NeedsUser);
    }

    [Fact]
    public async Task A_model_answer_that_no_option_says_is_still_refused()
    {
        var (resolver, _) = WithModel(Chose(0, 0.99));

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice, VagueNoticeOptions, answers: Answer(NoticeQuestion, "1 month")));

        Assert.True(result.NeedsUser);
        Assert.Equal(FormFieldStage.Model, result.Stage);
    }

    [Fact]
    public async Task A_right_to_work_answer_is_never_shown_to_the_model()
    {
        // The failure the design names. "Do you hold a full UK driving licence?" and the stored
        // right-to-work answer share a word, so the shortlist would carry it - and a sponsorship or
        // right-to-work answer mapped onto a licence question does not merely miss, it inverts.
        // Nothing was flagged here: the answer's own question is what guards it.
        var (resolver, model) = WithModel(Chose(0, 0.99));

        var result = await resolver.ResolveAsync(
            Ask("Do you hold a full UK driving licence?",
                answers: Answer("Do you have the right to work in the UK?", "Yes")));

        Assert.True(result.NeedsUser);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_flagged_answer_is_kept_out_of_the_prompt_and_an_ordinary_one_is_not()
    {
        // The pair that shows the flag is doing the work rather than the overlap rule.
        var question = "Which office would you like to be based in?";
        var stored = Answer("Which office do you prefer?", "London");

        var (guarded, guardedModel) = WithModel(Chose(0, 0.99));
        var refused = await guarded.ResolveAsync(Ask(question, answers: stored with { Sensitive = true }));

        var (ordinary, ordinaryModel) = WithModel(Chose(0, 0.99));
        var answered = await ordinary.ResolveAsync(Ask(question, answers: stored));

        Assert.True(refused.NeedsUser);
        Assert.Equal(0, guardedModel.Calls);

        Assert.Equal("London", answered.Value);
        Assert.Equal(1, ordinaryModel.Calls);
    }

    [Fact]
    public async Task A_sensitive_question_is_never_put_to_a_model()
    {
        var (resolver, model) = WithModel(Chose(0, 0.99));

        var result = await resolver.ResolveAsync(
            Ask("What are your salary expectations?",
                answers: Answer("What are your expectations of a manager?", "Clarity")));

        Assert.True(result.NeedsUser);
        Assert.Equal(FormFieldStage.None, result.Stage);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_sensitive_question_is_still_answered_from_what_the_candidate_typed()
    {
        // Verbatim or abstain, and this is the verbatim half: an exact question match is not an
        // inference at all, so the one thing a sensitive field can be answered by still works.
        var result = await WithoutModel().ResolveAsync(
            Ask("Do you require sponsorship to work in the UK?", ["Yes", "No"],
                answers: Answer("Do you require sponsorship to work in the UK?", "No", sensitive: true)));

        Assert.Equal("No", result.Value);
        Assert.True(result.Sensitive);
        Assert.Equal(FormFieldStage.DeclaredAnswer, result.Stage);
    }

    [Fact]
    public async Task Nothing_the_question_could_be_about_means_no_call_at_all()
    {
        var (resolver, model) = WithModel(Chose(0, 0.99));

        var result = await resolver.ResolveAsync(
            Ask("Which programming languages do you write daily?",
                answers: Answer(NoticeQuestion, "1 month")));

        Assert.True(result.NeedsUser);
        Assert.Equal(FormFieldStage.None, result.Stage);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task The_prompt_carries_only_the_answers_this_question_could_be_about()
    {
        // The reason B2 runs inside the server is that pulling the answer store into a client's
        // context is the whole-profile exposure this design prevents. Sending it to a model instead
        // would be that same disclosure with an extra hop.
        var (resolver, model) = WithModel(Chose(0, 0.99));

        await resolver.ResolveAsync(
            Ask(RewordedNotice,
                profile: new CandidateProfile { SubjectId = "subject", Email = "someone@example.test" },
                answers:
                [
                    Answer(NoticeQuestion, "1 month", id: 1),
                    Answer("What was your reason for leaving Contoso?", "A better role", id: 2),
                    Answer("Which conferences have you spoken at?", "NDC", id: 3),
                ]));

        var prompt = Assert.Single(model.Prompts);

        Assert.Contains("notice period", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason for leaving", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NDC", prompt, StringComparison.Ordinal);

        // And the profile is never described to a model, whatever stage is running.
        Assert.DoesNotContain("example.test", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_provider_the_stored_answers_still_answer()
    {
        var result = await WithoutModel().ResolveAsync(
            Ask(NoticeQuestion, NoticeOptions, answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal(FormFieldStage.DeclaredAnswer, result.Stage);
        Assert.Equal("1 month", result.Value);
    }

    [Fact]
    public async Task With_no_provider_the_last_stage_abstains_and_says_which_it_was()
    {
        // "No AI configured" and "nobody has answered this" want opposite fixes, so the refusal
        // names which one it is - the same reason an unmapped app-only token is told so specifically.
        var result = await WithoutModel().ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        Assert.True(result.NeedsUser);
        Assert.Equal(FormFieldStage.None, result.Stage);
        Assert.Contains("No AI provider is configured", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_abstention_is_recorded_as_a_call_that_answered_rather_than_as_a_loss()
    {
        // Requested against Returned is the one number that finds real losses. A refusal is the
        // outcome this stage is designed to produce, and filing it as a discard would fill that
        // number with the successes.
        var log = new RecordingAiCallLog();
        var (resolver, _) = WithModel(Chose(null, 0.3), log);

        await resolver.ResolveAsync(Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        var record = Assert.Single(log.Records);

        Assert.Equal(FormFieldResolver.LedgerOperation, record.Operation);
        Assert.Equal(AiCallOutcome.Succeeded, record.Outcome);
        Assert.Equal(1, record.Requested);
        Assert.Equal(1, record.Returned);
        Assert.Equal(0, record.Discarded);
        Assert.Equal("abstained", record.Reason);
    }

    [Fact]
    public async Task A_reply_that_could_not_be_used_is_recorded_as_a_loss()
    {
        var log = new RecordingAiCallLog();
        var (resolver, _) = WithModel(Chose(7, 0.99), log);

        await resolver.ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")) with { PostingId = 42 });

        var record = Assert.Single(log.Records);

        Assert.Equal(AiCallOutcome.Failed, record.Outcome);
        Assert.Equal(0, record.Returned);
        Assert.Equal(42, Assert.Single(record.AffectedIds));
    }

    [Fact]
    public async Task A_ledger_that_throws_does_not_cost_the_call_it_was_recording()
    {
        var (resolver, _) = WithModel(Chose(0, 0.99), new ThrowingAiCallLog());

        var result = await resolver.ResolveAsync(
            Ask(RewordedNotice, answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal("1 month", result.Value);
    }

    [Fact]
    public async Task The_container_builds_a_resolver_where_no_provider_is_configured()
    {
        // The registration rule, asserted rather than described. Every other AI service lives
        // inside AddAiProvider's provider check and resolves to null without one; this one must
        // not, or a candidate's own stored answers stop being found because an environment
        // variable is absent.
        using var container = Container();

        var resolver = container.GetService<IFormFieldResolver>();

        Assert.Null(container.GetService<Kernel>());
        Assert.NotNull(resolver);

        var result = await resolver.ResolveAsync(
            Ask(NoticeQuestion, answers: Answer(NoticeQuestion, "1 month")));

        Assert.Equal("1 month", result.Value);
    }

    [Fact]
    public void The_container_builds_a_resolver_where_a_provider_is_configured()
    {
        // The other half of the optional constructor parameter: with a Kernel registered, the
        // container has to pick it up rather than fall back to the default it uses above.
        using var container = Container(
            ("Ai:Provider", "azureopenai"),
            ("Ai:AzureOpenAi:Endpoint", "https://not-a-real-resource.openai.azure.com/"));

        Assert.NotNull(container.GetService<Kernel>());
        Assert.NotNull(container.GetService<IFormFieldResolver>());
    }

    private static ServiceProvider Container(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAiProvider(configuration);
        services.AddFormFieldResolver();

        return services.BuildServiceProvider();
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
            => throw new InvalidOperationException("Cosmos is unreachable.");
    }

    /// <summary>A scripted model that counts what it was asked and keeps what it was sent.</summary>
    private sealed class ScriptedChatService(string response) : IChatCompletionService
    {
        public int Calls { get; private set; }

        public List<string> Prompts { get; } = [];

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompts.Add(string.Join("\n", chatHistory.Select(message => message.Content)));

            IReadOnlyList<ChatMessageContent> result =
                [new ChatMessageContent(AuthorRole.Assistant, response)];

            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompts.Add(string.Join("\n", chatHistory.Select(message => message.Content)));

            yield return new StreamingChatMessageContent(AuthorRole.Assistant, response);
            await Task.CompletedTask;
        }
    }
}
