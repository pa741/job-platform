namespace JobPlatform.Ai;

/// <summary>
/// Reading JSON back out of a model response.
/// </summary>
public static class AiJson
{
    /// <summary>
    /// The outermost <c>{...}</c> span, so a code fence or a line of preamble does not defeat
    /// parsing.
    /// </summary>
    /// <remarks>
    /// Semantic Kernel's execution settings are provider-neutral, so they cannot express the
    /// Anthropic-native structured-output constraint that would guarantee a bare JSON body -
    /// that is the concrete price of routing through the abstraction, and tolerating a code
    /// fence or a sentence of preamble is what it costs to pay it. Any prompt that asks this
    /// Kernel for JSON needs this, so it lives beside the registration rather than inside a
    /// caller.
    /// </remarks>
    public static string? ExtractJsonObject(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var start = response.IndexOf('{', StringComparison.Ordinal);
        var end = response.LastIndexOf('}');

        return start >= 0 && end > start ? response[start..(end + 1)] : null;
    }
}
