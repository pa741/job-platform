using System.Runtime.CompilerServices;
using JobPlatform.Ai;
using JobPlatform.Ai.Matching;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// How the assessor places answers against the roles they were written for, and what it refuses.
/// </summary>
/// <remarks>
/// The same class of test as <c>DocumentExtractorTests</c>, and for the same reason: an
/// assessment attached to the wrong posting is wrong, internally consistent, and undetectable
/// afterwards. The difference is that these were written after the failure rather than before.
///
/// On 2026-08-28 a sweep sent 90 pairs and wrote 40. Five batches of ten were discarded whole,
/// every role in them, and nothing threw - the shape of a response that is well formed and typed
/// differently, not one that is wrong.
/// </remarks>
public sealed class CandidacyAssessorTests
{
    private static KernelCandidacyAssessor Assessor(params string[] responses)
    {
        var builder = Kernel.CreateBuilder();

        builder.Services.AddKeyedSingleton<IChatCompletionService>(
            AzureOpenAiOptions.BulkServiceId, new ScriptedChatService(responses));

        return new KernelCandidacyAssessor(
            builder.Build(),
            Options.Create(new AzureOpenAiOptions { BulkDeployment = "bulk", BatchSize = 2 }));
    }

    private static CandidateProfile Profile()
        => new()
        {
            SubjectId = "subject",
            Headline = "Backend engineer",
            Summary = "Six years of C# and SQL.",
        };

    private static CandidacyRequest Request(long id, string title)
        => new(
            id,
            title,
            "Contoso",
            "We need a backend engineer.",
            new MatchResult { Score = 80, Coverage = 0.5 });

    private static string Body(params string[] entries)
        => $$"""{"assessments": [{{string.Join(",", entries)}}]}""";

    private static string Entry(string index, string verdict = "strong", string score = "90")
        => $$"""
            {"index": {{index}}, "verdict": "{{verdict}}", "score": {{score}},
             "rationale": "Fits well.", "strengths": [], "gaps": [], "emphasise": []}
            """;

    [Fact]
    public async Task An_index_the_model_quoted_is_still_read()
    {
        // The regression this file exists for. The prompt asked for the index "copied exactly"
        // from its heading, which is an invitation to copy it as text, and the parser demanded a
        // JSON number - so a whole batch answered correctly in strings was thrown away.
        var assessor = Assessor(Body(Entry("\"0\""), Entry("\"1\"")));

        var results = await assessor.AssessAsync(
            Profile(), [Request(1, "Backend Engineer"), Request(2, "Platform Engineer")]);

        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task A_plain_numeric_index_still_works()
    {
        var assessor = Assessor(Body(Entry("0"), Entry("1")));

        var results = await assessor.AssessAsync(
            Profile(), [Request(1, "Backend Engineer"), Request(2, "Platform Engineer")]);

        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task An_index_outside_the_batch_is_still_dropped()
    {
        // The guarantee that must survive the fix above. Accepting "3" as 3 is parsing; placing
        // an answer against a role that was never sent is guessing, and the whole point of the
        // check is that nothing downstream could catch it.
        var assessor = Assessor(Body(Entry("0"), Entry("7")));

        var results = await assessor.AssessAsync(
            Profile(), [Request(1, "Backend Engineer"), Request(2, "Platform Engineer")]);

        Assert.NotNull(results[0]);
        Assert.Null(results[1]);
    }

    [Fact]
    public async Task A_repeated_index_is_dropped_rather_than_overwriting()
    {
        var assessor = Assessor(Body(Entry("0"), Entry("0", "weak", "10")));

        var results = await assessor.AssessAsync(
            Profile(), [Request(1, "Backend Engineer"), Request(2, "Platform Engineer")]);

        // The first answer stands and the duplicate is refused; the second role goes unassessed
        // and is picked up by the next sweep.
        Assert.Equal(CandidacyVerdict.Strong, results[0]!.Verdict);
        Assert.Null(results[1]);
    }

    [Fact]
    public async Task A_non_numeric_index_is_dropped()
    {
        var assessor = Assessor(Body(Entry("\"first\""), Entry("1")));

        var results = await assessor.AssessAsync(
            Profile(), [Request(1, "Backend Engineer"), Request(2, "Platform Engineer")]);

        Assert.Null(results[0]);
        Assert.NotNull(results[1]);
    }

    [Fact]
    public async Task One_bad_batch_does_not_cost_the_others()
    {
        // What the production failure looked like: batches are separate calls, so a response the
        // parser cannot use loses that batch and nothing else. Four of nine survived that way.
        var assessor = Assessor(
            Body(Entry("0"), Entry("1")),
            "not json at all",
            Body(Entry("0"), Entry("1")));

        var results = await assessor.AssessAsync(
            Profile(),
            [
                Request(1, "A"), Request(2, "B"),
                Request(3, "C"), Request(4, "D"),
                Request(5, "E"), Request(6, "F"),
            ]);

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.Null(results[2]);
        Assert.Null(results[3]);
        Assert.NotNull(results[4]);
        Assert.NotNull(results[5]);
    }

    /// <summary>A chat service that answers each call from a script, so batching can be tested.</summary>
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
