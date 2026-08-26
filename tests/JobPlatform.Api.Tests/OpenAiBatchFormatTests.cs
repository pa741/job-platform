using System.Text.Json;
using JobPlatform.Ai;
using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Enrichment;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The wire format of a batch: what goes out, and how what comes back is matched to it.
/// </summary>
/// <remarks>
/// These went unwritten until the path had already run against the real API, which is a poor
/// place to discover a field name is wrong. The two properties worth pinning are that a request
/// carries the correlation id the collector will look for, and that a result is matched to it
/// rather than to its position - the documentation is explicit that output order does not match
/// input order.
/// </remarks>
public sealed class OpenAiBatchFormatTests
{
    private static readonly OpenAiBatchOptions Options = new()
    {
        Model = "gpt-5.6-luna",
        MaxOutputTokens = 4_000,
        ReasoningEffort = "low",
    };

    private static BatchExtractionItem Item(string id, string text = "We need Kubernetes.")
        => new(id, new ExtractionRequest(DocumentKind.Posting, text, "Platform Engineer"));

    // -----------------------------------------------------------------------
    // The request file
    // -----------------------------------------------------------------------

    [Fact]
    public void One_line_per_document_each_a_complete_request()
    {
        var jsonl = OpenAiBatchFormat.BuildRequestFile(
            [Item("101"), Item("102"), Item("103")], Options);

        var lines = OpenAiBatchFormat.Lines(jsonl).ToList();

        Assert.Equal(3, lines.Count);

        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            Assert.Equal("POST", root.GetProperty("method").GetString());
            Assert.Equal("/v1/chat/completions", root.GetProperty("url").GetString());
            Assert.Equal("gpt-5.6-luna", root.GetProperty("body").GetProperty("model").GetString());
        }
    }

    [Fact]
    public void Every_request_carries_the_correlation_id_the_collector_will_look_for()
    {
        // The whole safety property of this path. Without it a result cannot be attached to a
        // posting except by position, which the provider explicitly does not guarantee.
        var jsonl = OpenAiBatchFormat.BuildRequestFile([Item("101"), Item("102")], Options);

        var ids = OpenAiBatchFormat.Lines(jsonl)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("custom_id").GetString())
            .ToList();

        Assert.Equal(["101", "102"], ids);
    }

    [Fact]
    public void The_request_asks_for_json_and_bounds_its_own_output()
    {
        var line = OpenAiBatchFormat.Lines(
            OpenAiBatchFormat.BuildRequestFile([Item("101")], Options)).Single();

        var body = JsonDocument.Parse(line).RootElement.GetProperty("body");

        Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(4_000, body.GetProperty("max_completion_tokens").GetInt32());

        // Deliberately not "none": at none the model stops reasoning about whether a phrase
        // means essential or desirable, which is the only thing the deterministic pass cannot do.
        Assert.Equal("low", body.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void The_prompt_carries_the_vocabulary_and_the_document()
    {
        var line = OpenAiBatchFormat.Lines(
            OpenAiBatchFormat.BuildRequestFile(
                [Item("101", "We need Kubernetes and Terraform.")], Options)).Single();

        var content = JsonDocument.Parse(line).RootElement
            .GetProperty("body").GetProperty("messages")[0].GetProperty("content").GetString()!;

        Assert.Contains("skill.kubernetes = Kubernetes", content, StringComparison.Ordinal);
        Assert.Contains("We need Kubernetes and Terraform.", content, StringComparison.Ordinal);

        // Domains are structural and reached through the closure; sending them would only invite
        // the model to assert one directly.
        Assert.DoesNotContain("area.backend", content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_with_no_text_still_produces_no_line()
    {
        // The extractor filters these before building, but the format must not invent a request
        // for one either - an empty prompt is a paid call that can only fail.
        var jsonl = OpenAiBatchFormat.BuildRequestFile([], Options);

        Assert.Empty(OpenAiBatchFormat.Lines(jsonl));
    }

    // -----------------------------------------------------------------------
    // Reading results back
    // -----------------------------------------------------------------------

    private static string OutputLine(string customId, string content)
        => JsonSerializer.Serialize(new
        {
            custom_id = customId,
            response = new { body = new { choices = new[] { new { message = new { content } } } } },
        });

    [Fact]
    public void A_result_is_matched_by_its_correlation_id()
    {
        var result = OpenAiBatchFormat.ReadOutputLine(
            OutputLine("2175", """{"concepts":[{"key":"skill.csharp","polarity":"required"}]}"""),
            "gpt-5.6-luna");

        Assert.NotNull(result);
        Assert.Equal("2175", result.CorrelationId);
        Assert.Null(result.Error);

        var assertion = Assert.Single(result.Extraction!.Concepts);
        Assert.Equal("skill.csharp", assertion.ConceptKey);
        Assert.Equal(AssertionPolarity.Required, assertion.Polarity);
        Assert.Equal(AssertionSource.Model, assertion.Source);
    }

    [Fact]
    public void A_line_with_no_correlation_id_is_dropped_rather_than_guessed_at()
    {
        // There is nothing to attach the answer to. Attaching it to the wrong posting would be
        // wrong, self-consistent and undetectable, which is the failure this path exists to
        // make impossible.
        var line = JsonSerializer.Serialize(new
        {
            response = new { body = new { choices = new[] { new { message = new { content = "{}" } } } } },
        });

        Assert.Null(OpenAiBatchFormat.ReadOutputLine(line, "gpt-5.6-luna"));
    }

    [Fact]
    public void A_fenced_body_still_parses()
    {
        // JSON mode is a request to a provider, not a property of the transport.
        var result = OpenAiBatchFormat.ReadOutputLine(
            OutputLine("2175", "```json\n{\"concepts\":[{\"key\":\"skill.terraform\"}]}\n```"),
            "gpt-5.6-luna");

        Assert.Equal("skill.terraform", Assert.Single(result!.Extraction!.Concepts).ConceptKey);
    }

    [Fact]
    public void An_unusable_body_is_reported_against_its_id_rather_than_lost()
    {
        // The caller counts these as failures. Dropping them silently would make an expired
        // batch and a refused request look identical.
        var result = OpenAiBatchFormat.ReadOutputLine(
            OutputLine("2175", "I'm sorry, I can't help with that."), "gpt-5.6-luna");

        Assert.NotNull(result);
        Assert.Equal("2175", result.CorrelationId);
        Assert.Null(result.Extraction);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_invented_concept_key_cannot_enter_the_data_through_this_path_either()
    {
        var result = OpenAiBatchFormat.ReadOutputLine(
            OutputLine("2175", """{"concepts":[{"key":"skill.not-a-real-concept"}]}"""),
            "gpt-5.6-luna");

        Assert.Empty(result!.Extraction!.Concepts);
        Assert.Equal("skill.not-a-real-concept", Assert.Single(result.Extraction.Mentions).SurfaceForm);
    }

    [Fact]
    public void An_error_line_is_reported_against_its_id()
    {
        var line = JsonSerializer.Serialize(new
        {
            custom_id = "2175",
            error = new { message = "The request exceeded the token limit." },
        });

        var result = OpenAiBatchFormat.ReadErrorLine(line);

        Assert.Equal("2175", result!.CorrelationId);
        Assert.Null(result.Extraction);
        Assert.Contains("token limit", result.Error!, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("completed", BatchState.Completed)]
    [InlineData("failed", BatchState.Failed)]
    [InlineData("expired", BatchState.Expired)]
    [InlineData("cancelled", BatchState.Cancelled)]
    [InlineData("cancelling", BatchState.Cancelled)]
    [InlineData("validating", BatchState.Running)]
    [InlineData("in_progress", BatchState.Running)]
    [InlineData("finalizing", BatchState.Running)]
    public void The_providers_statuses_reduce_to_what_a_caller_can_act_on(string status, BatchState expected)
        => Assert.Equal(expected, OpenAiBatchFormat.ParseState(status));

    [Fact]
    public void An_unrecognised_status_means_wait_rather_than_give_up()
    {
        // A provider adding a state should make the collector poll again, not make it discard a
        // batch that is still working - and still holding results we have paid for.
        Assert.Equal(BatchState.Running, OpenAiBatchFormat.ParseState("some_new_state"));
        Assert.Equal(BatchState.Running, OpenAiBatchFormat.ParseState(null));
    }
}
