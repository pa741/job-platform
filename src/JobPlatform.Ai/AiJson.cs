using System.Globalization;
using System.Text.Json;

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

    /// <summary>
    /// An integer property, whether the model quoted it or not.
    /// </summary>
    /// <remarks>
    /// A JSON string holding a number is accepted deliberately. Both call sites used to demand
    /// <see cref="JsonValueKind.Number"/>, and on 2026-08-28 five of nine assessment batches
    /// were discarded whole - every role in them - which is the signature of a response that is
    /// well formed and typed differently, not one that is wrong. The extraction path carried
    /// the same bug silently for exactly as long.
    ///
    /// This concedes nothing that matters. What has to hold is that an answer lands against the
    /// document it was written for, and that is the range and duplicate checking each caller
    /// does once it has a value. Reading "3" as 3 is parsing; clamping an out-of-range 3 to 2
    /// would be guessing, and neither caller does that.
    ///
    /// One reader rather than one per call site, for the reason <c>TitleTokenizer</c> gives:
    /// two of them disagreeing produces answers that contradict each other with neither
    /// obviously wrong.
    /// </remarks>
    public static int? Int(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text)
                => text,
            _ => null,
        };
    }

    /// <summary>A double property, quoted or not. See <see cref="Int"/>.</summary>
    public static double? Double(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text)
                => text,
            _ => null,
        };
    }
}
