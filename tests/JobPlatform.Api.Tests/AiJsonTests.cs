using JobPlatform.Ai;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The JSON extraction any Semantic Kernel prompt asking for JSON depends on.
/// </summary>
/// <remarks>
/// Worth testing precisely because it exists to absorb a weakness. Routing through Semantic
/// Kernel means provider-neutral execution settings, which cannot express the
/// Anthropic-native structured-output constraint that would guarantee a bare JSON body - so a
/// code fence or a sentence of preamble is a live possibility rather than a hypothetical.
/// </remarks>
public sealed class AiJsonTests
{
    private const string Body = "{\"items\":[]}";

    [Theory]
    // Bare, which is what a prompt would ask for.
    [InlineData(Body)]
    // Fenced, which models do anyway.
    [InlineData("```json\n" + Body + "\n```")]
    [InlineData("```\n" + Body + "\n```")]
    // Prefixed with prose.
    [InlineData("Here are the results: " + Body)]
    // Both, plus a trailing offer to help.
    [InlineData("Sure, I can help.\n```\n" + Body + "\n```\nLet me know if you want more.")]
    public void Json_survives_fences_and_surrounding_prose(string response)
    {
        var json = AiJson.ExtractJsonObject(response);

        Assert.NotNull(json);
        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.EndsWith("}", json, StringComparison.Ordinal);
        Assert.Contains("items", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_objects_are_not_truncated_at_the_first_closing_brace()
    {
        const string payload = "{\"items\":[{\"id\":1,\"score\":90}]}";

        var json = AiJson.ExtractJsonObject("Result: " + payload + " done.");

        Assert.Equal(payload, json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I cannot answer that.")]
    [InlineData("}{")]
    public void A_response_with_no_json_object_yields_null_rather_than_throwing(string response)
        => Assert.Null(AiJson.ExtractJsonObject(response));
}
