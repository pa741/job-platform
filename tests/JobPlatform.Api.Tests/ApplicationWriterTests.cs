using System.Runtime.CompilerServices;
using JobPlatform.Ai;
using JobPlatform.Ai.Applications;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using JobPlatform.Core.Submissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// What the writer will and will not produce, now that it no longer writes a CV.
/// </summary>
/// <remarks>
/// <b>This file exists because of one sentence.</b> Asked what else an employer should know, the
/// model gave the candidate's citizenship - correctly, out of their own summary - and then added
/// <i>"I am an AI and they should have seen this."</i> It was stored, served through the pack, and
/// was one browser step from being typed into a form under a person's name. A guard drops that
/// class of sentence; what removes the failure rather than catching it is not writing the document
/// that matters most, so the CV became a choice among documents the candidate wrote.
///
/// So the assertions here are mostly about absence, which is the hard kind to keep: a schema no
/// longer asks for a CV, a response that offers one anyway is not stored, and the refusal that used
/// to cover two documents now covers one. Each of those goes wrong silently - a draft with a CV
/// again looks like a working draft - which is why they are pinned rather than assumed.
/// </remarks>
public sealed class ApplicationWriterTests
{
    private static KernelApplicationWriter Writer(params string[] responses)
        => Writer(callLog: null, responses);

    private static KernelApplicationWriter Writer(IAiCallLog? callLog, params string[] responses)
    {
        var builder = Kernel.CreateBuilder();

        // Both service ids, because the two calls this class makes deliberately land on different
        // deployments: writing prose is worth the expensive one and settling a tie is not.
        builder.Services.AddKeyedSingleton<IChatCompletionService>(
            AzureOpenAiOptions.WritingServiceId, new ScriptedChatService(responses));

        builder.Services.AddKeyedSingleton<IChatCompletionService>(
            AzureOpenAiOptions.BulkServiceId, new ScriptedChatService(responses));

        return new KernelApplicationWriter(
            builder.Build(),
            Options.Create(new AzureOpenAiOptions { BulkDeployment = "bulk", WritingDeployment = "writing" }),
            logger: null,
            callLog);
    }

    private static ApplicationRequest Request()
        => new(
            new CandidateProfile
            {
                SubjectId = "subject",
                FullName = "Ada Lovelace",
                Headline = "Backend engineer",
                Summary = "Six years of C# and SQL.",
            },
            Posting(),
            new MatchResult { Score = 80, Coverage = 0.5 });

    private static PostingBrief Posting()
        => new(11, "Platform Engineer", "Contoso", "Kubernetes, and a lot of traffic.", "linkedin");

    private static string Letter(string body = "Dear Contoso,\\n\\nI would like to apply.")
        => $$"""{"coverLetter": "{{body}}", "emphasised": ["Kubernetes at scale"]}""";

    /// <summary>
    /// The prompt does not ask for a CV, and the schema has no place to put one.
    /// </summary>
    /// <remarks>
    /// <b>Asserted against the prompt rather than only against the output</b>, because the two fail
    /// differently. A parser that ignores a <c>cv</c> key still pays for the tokens that wrote it,
    /// on the deployment priced roughly twenty-five times the other one - and a model told to write
    /// a CV writes one whether or not anything reads it. The prompt is recovered from the ledger,
    /// which is where a failed call already stores it for replay.
    /// </remarks>
    [Fact]
    public async Task The_prompt_never_asks_for_a_curriculum_vitae()
    {
        var log = new RecordingAiCallLog();

        // A response with no letter fails the call, which is what makes the prompt reach the
        // ledger: the sink keeps it for a failure and never for a success.
        await Writer(log, """{"emphasised": []}""").WriteAsync(Request());

        var prompt = Assert.Single(log.Records).Prompt!;

        Assert.DoesNotContain("\"cv\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("You are NOT writing a CV", prompt, StringComparison.Ordinal);
        Assert.Contains("coverLetter", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model that returns a CV anyway has it dropped rather than stored.
    /// </summary>
    /// <remarks>
    /// <b>The quiet way this feature would be undone.</b> Nothing prevents a model returning a key
    /// the schema does not name - it is prose it was not asked for - and a parser that read one
    /// "just in case" would put a model-written CV back on the row, back through the pack, and back
    /// in front of an employer, with no diff anywhere saying so. So the key is not read at all, and
    /// this is the test that says the omission was on purpose.
    /// </remarks>
    [Fact]
    public async Task A_curriculum_vitae_the_model_offers_anyway_is_not_stored()
    {
        var draft = await Writer(
            """{"cv": "# Ada Lovelace\n\nInvented.", "coverLetter": "Dear Contoso,", "emphasised": []}""")
            .WriteAsync(Request());

        Assert.NotNull(draft);
        Assert.Null(draft.CurriculumVitaeMarkdown);
        Assert.Equal("Dear Contoso,", draft.CoverLetterMarkdown);
    }

    /// <summary>
    /// A draft with no cover letter is refused, and nothing else is.
    /// </summary>
    /// <remarks>
    /// <b>"Half a draft is worse than none", narrowed rather than deleted.</b> It used to mean a CV
    /// without a letter or a letter without a CV; there is no second document now, and the argument
    /// survives because it was never about the arithmetic of two - it was about what an absence
    /// looks like to the person who asked for one. A draft with no letter is a row that exists,
    /// satisfies <c>documentsReady</c>, takes the posting out of the generation pass's queue and
    /// offers an employer nothing, which is worse than no row at all because the posting will not
    /// come back.
    /// </remarks>
    [Fact]
    public async Task A_draft_with_no_cover_letter_is_refused()
    {
        Assert.Null(await Writer("""{"emphasised": ["Kubernetes"]}""").WriteAsync(Request()));
        Assert.Null(await Writer("""{"coverLetter": "   "}""").WriteAsync(Request()));
    }

    /// <summary>
    /// A missing drafted answer is not half a draft, and does not refuse the letter.
    /// </summary>
    /// <remarks>
    /// The other side of the same rule, and the reason it has to be stated: a drafted answer is a
    /// box the candidate fills in by hand when it is absent, which is where every application stood
    /// before they existed. Refusing the letter over one would throw away the expensive half to
    /// protect the cheap one.
    /// </remarks>
    [Fact]
    public async Task A_letter_with_no_drafted_answers_is_still_a_draft()
    {
        var draft = await Writer(Letter()).WriteAsync(Request());

        Assert.NotNull(draft);
        Assert.Empty(draft.DraftedAnswers);
        Assert.Equal(ApplicationDraft.CurrentVersion, draft.Version);
    }

    /// <summary>
    /// The drafted free text still comes back, because that half is genuinely per-posting.
    /// </summary>
    /// <remarks>
    /// What the writer kept. These questions - why this company, why this role - are answerable
    /// only by something holding the advert, and the writer is already holding it. Removing the CV
    /// must not have removed them, which is exactly the kind of thing a prompt rewrite takes with
    /// it.
    /// </remarks>
    [Fact]
    public async Task The_posting_specific_answers_survive_the_cv_being_removed()
    {
        var question = DraftedAnswerCatalog.PerPosting[0].QuestionText;

        var draft = await Writer(
            $$"""
            {"coverLetter": "Dear Contoso,",
             "draftedAnswers": [{"question": "{{question}}", "answer": "Because of the traffic."}]}
            """)
            .WriteAsync(Request());

        var answer = Assert.Single(draft!.DraftedAnswers);

        Assert.Equal(question, answer.QuestionText);
        Assert.Equal(FreeTextCategory.PostingSpecific, answer.Category);
    }

    /// <summary>
    /// The tie-break answers with an id off the ballot and nothing else.
    /// </summary>
    /// <remarks>
    /// A closed question over a fixed set, which is what makes it checkable - and far easier than
    /// the one this class used to be asked. The quoted form is accepted too: the assessment pass
    /// lost a whole batch to a prompt saying "copied exactly", which is an invitation to answer in
    /// a string, and the fix there was to read both rather than to argue with the model.
    /// </remarks>
    [Theory]
    [InlineData("""{"variantId": 7}""")]
    [InlineData("""{"variantId": "7"}""")]
    public async Task The_tie_break_returns_a_variant_from_the_ballot(string response)
        => Assert.Equal(7, await Writer(response).ChooseCurriculumVitaeAsync(Ballot()));

    /// <summary>
    /// An id nobody offered is refused rather than repaired.
    /// </summary>
    /// <remarks>
    /// The rule <c>KernelDocumentExtractor</c> follows for concept keys: a hallucinated id is
    /// indistinguishable from a real one once it is stored, and there is no nearest variant to fall
    /// back to. Abstention is the only other answer, and it is a legitimate one.
    /// </remarks>
    [Fact]
    public async Task The_tie_break_refuses_an_id_that_was_not_on_the_ballot()
        => Assert.Null(await Writer("""{"variantId": 99}""").ChooseCurriculumVitaeAsync(Ballot()));

    /// <summary>
    /// Declining to choose is an answer, and a successful one.
    /// </summary>
    /// <remarks>
    /// <b>The ledger reading matters as much as the return value.</b> A model correctly saying "the
    /// advert gives me nothing to tell these apart" is not a provider fault, and recording it as
    /// one would make an abstention rate look like an outage. A malformed response is a failure; an
    /// explicit null is a call that worked and returned nothing.
    /// </remarks>
    [Fact]
    public async Task An_abstention_is_recorded_as_a_call_that_worked_and_chose_nothing()
    {
        var log = new RecordingAiCallLog();

        Assert.Null(await Writer(log, """{"variantId": null}""").ChooseCurriculumVitaeAsync(Ballot()));

        var record = Assert.Single(log.Records);

        Assert.Equal(KernelApplicationWriter.ChoiceLedgerOperation, record.Operation);
        Assert.Equal(AiCallOutcome.Succeeded, record.Outcome);
        Assert.Equal(0, record.Returned);

        // The bulk deployment, not the writing one. Paying Sol prices for a multiple-choice
        // question is the mistake the two deployments exist to avoid, in the direction nobody
        // notices because it works.
        Assert.Equal("bulk", record.Deployment);
    }

    /// <summary>
    /// A ballot with nothing to choose between costs no call at all.
    /// </summary>
    /// <remarks>
    /// A ballot of one is the arithmetic's decision restated and a ballot of none is a caller bug.
    /// Neither is a question, so neither is a bill - and the ledger staying empty is how that is
    /// asserted, because a call that returned null and a call that never happened look identical
    /// from the outside.
    /// </remarks>
    [Fact]
    public async Task A_ballot_of_one_is_not_a_question_and_is_not_asked()
    {
        var log = new RecordingAiCallLog();

        var answer = await Writer(log, """{"variantId": 7}""").ChooseCurriculumVitaeAsync(
            new CvChoiceRequest(Posting(), [new CvVariantScore(7, "Backend .NET", 80, 4)]));

        Assert.Null(answer);
        Assert.Empty(log.Records);
    }

    private static CvChoiceRequest Ballot()
        => new(
            Posting(),
            [
                new CvVariantScore(7, "Backend .NET", 80, 4),
                new CvVariantScore(8, "Data platforms", 78, 4),
            ]);

    /// <summary>Keeps what the writer told the ledger, which is where a prompt is recoverable.</summary>
    private sealed class RecordingAiCallLog : IAiCallLog
    {
        public List<AiCallRecord> Records { get; } = [];

        public Task RecordAsync(AiCallRecord record, CancellationToken ct = default)
        {
            Records.Add(record);

            return Task.CompletedTask;
        }
    }

    /// <summary>The provider, without the provider. The same shape the assessor's tests use.</summary>
    private sealed class ScriptedChatService(string[] responses) : IChatCompletionService
    {
        private int _call;

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var response = responses[Math.Min(_call++, responses.Length - 1)];

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
            var response = responses[Math.Min(_call++, responses.Length - 1)];

            yield return new StreamingChatMessageContent(AuthorRole.Assistant, response);
            await Task.CompletedTask;
        }
    }
}
