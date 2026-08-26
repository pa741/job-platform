using System.Text;
using System.Text.Json;
using JobPlatform.Core.Enrichment;

namespace JobPlatform.Ai.Extraction;

/// <summary>
/// The wire format of a batch: the request file going out, and the result lines coming back.
/// </summary>
/// <remarks>
/// Separated from <see cref="OpenAiBatchExtractor"/> so it can be tested without an SDK client.
/// The client types are concrete, take a live credential, and are marked experimental - which
/// left the two things most worth pinning untestable: that a request carries the correlation id
/// the collector will look for, and that a result is matched to it rather than to its position.
/// Both were exercised for the first time against the real API, which is a poor place to learn
/// that a field name is wrong.
///
/// Everything here is a pure function of its arguments. No network, no clock, no database.
/// </remarks>
internal static class OpenAiBatchFormat
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// One JSON object per line, each a complete chat completion request.
    /// </summary>
    /// <remarks>
    /// The <c>custom_id</c> is what makes this path safe: the provider echoes it beside the
    /// answer, so a caller never infers which document a result belongs to. The documentation is
    /// explicit that output order does not match input order, which is exactly the situation the
    /// synchronous packer has to defend against by hand.
    /// </remarks>
    public static string BuildRequestFile(
        IReadOnlyList<BatchExtractionItem> items, OpenAiBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder(items.Count * 8_000);

        foreach (var item in items)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = options.Model,
                ["messages"] = new[]
                {
                    new { role = "user", content = ExtractionPrompt.ForSingleDocument(item.Request) },
                },
                // JSON mode, so a response is guaranteed to parse. The schema itself is carried
                // by the prompt, so this guarantees valid JSON rather than the right shape -
                // which is why the parsing below stays defensive.
                ["response_format"] = new { type = "json_object" },
                ["max_completion_tokens"] = options.MaxOutputTokens,
                ["reasoning_effort"] = options.ReasoningEffort,
            };

            builder.AppendLine(JsonSerializer.Serialize(
                new
                {
                    custom_id = item.CorrelationId,
                    method = "POST",
                    url = "/v1/chat/completions",
                    body,
                },
                Json));
        }

        return builder.ToString();
    }

    /// <summary>
    /// One line of the output file: a successful request and whatever the model said.
    /// </summary>
    /// <remarks>
    /// Null only where the line carries no correlation id, which makes it unusable - there is
    /// nothing to attach the answer to, and guessing is the failure this whole design avoids.
    /// Anything else comes back as a <see cref="BatchResult"/> with an error, so a caller can
    /// count it rather than silently lose it.
    /// </remarks>
    public static BatchResult? ReadOutputLine(string line, string? model)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (ExtractionPrompt.String(root, "custom_id") is not { Length: > 0 } correlationId)
        {
            return null;
        }

        var body = root.TryGetProperty("response", out var response)
            && response.TryGetProperty("body", out var b)
                ? b
                : (JsonElement?)null;

        var content = body is null ? null : ReadMessageContent(body.Value);

        if (content is null)
        {
            return new BatchResult(correlationId, null, "The response carried no message content.");
        }

        // The same net the synchronous path keeps: JSON mode is a request to a provider rather
        // than a property of the transport, so a fenced or prose-wrapped body is absorbed rather
        // than treated as a failure.
        var json = AiJson.ExtractJsonObject(content);

        if (json is null)
        {
            return new BatchResult(correlationId, null, "The response carried no JSON object.");
        }

        try
        {
            using var parsed = JsonDocument.Parse(json);
            return new BatchResult(correlationId, ExtractionPrompt.Parse(parsed.RootElement, model), null);
        }
        catch (JsonException ex)
        {
            return new BatchResult(correlationId, null, $"Malformed JSON: {ex.Message}");
        }
    }

    /// <summary>One line of the error file: a request the provider refused or could not finish.</summary>
    public static BatchResult? ReadErrorLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (ExtractionPrompt.String(root, "custom_id") is not { Length: > 0 } correlationId)
        {
            return null;
        }

        var message = root.TryGetProperty("error", out var error)
            ? ExtractionPrompt.String(error, "message") ?? error.GetRawText()
            : "The provider reported an unspecified error.";

        return new BatchResult(correlationId, null, ExtractionPrompt.Truncate(message, 500));
    }

    /// <summary>
    /// The provider's status vocabulary, reduced to what a caller can act on.
    /// </summary>
    /// <remarks>
    /// <c>validating</c> and <c>in_progress</c> both mean wait, so both are Running. An unknown
    /// status is Running rather than a failure: a provider adding a state should make the
    /// collector poll again, not make it discard a batch that is still working.
    /// </remarks>
    public static BatchState ParseState(string? status) => status switch
    {
        "completed" => BatchState.Completed,
        "failed" => BatchState.Failed,
        "expired" => BatchState.Expired,
        "cancelled" or "cancelling" => BatchState.Cancelled,
        _ => BatchState.Running,
    };

    /// <summary>Non-empty lines of a JSONL document.</summary>
    public static IEnumerable<string> Lines(string content)
        => content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The assistant's text, from a chat completion body.</summary>
    private static string? ReadMessageContent(JsonElement body)
        => body.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
                ? ExtractionPrompt.String(message, "content")
                : null;
}
