namespace JobPlatform.Core.Text;

/// <summary>
/// Normalises free text into the tokens the platform treats as demand signal.
/// </summary>
/// <remarks>
/// Deliberately one normaliser rather than one per caller. Anything that reads the same text
/// has to agree on what a "word" is — the run digest's <c>titleKeywords</c> is the only
/// consumer today, but two tokenisers disagreeing produces numbers that contradict each other
/// with neither side obviously wrong. One normaliser means that class of discrepancy cannot
/// exist.
/// </remarks>
public static class TitleTokenizer
{
    /// <summary>Words carrying no signal about what the market wants.</summary>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "or", "the", "for", "of", "to", "in", "at", "on", "with", "by",
        "our", "we", "you", "your", "is", "are", "be", "as", "from", "new", "job", "role",
        "position", "opportunity", "hiring", "urgent", "apply", "now",
    };

    private static readonly char[] Separators =
        [' ', ',', '/', '-', '(', ')', '|', '&', '.', ':', ';'];

    /// <summary>
    /// Lower-cased tokens, stop words and punctuation removed. Order is preserved and
    /// duplicates are kept — callers that want set semantics apply <c>Distinct</c> themselves,
    /// because the run digest counts a repeated token once per title while relevance scoring
    /// legitimately weights repetition.
    /// </summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var parts = text.Split(
            Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var token = part.ToLowerInvariant();

            if (token.Length > 1 && !StopWords.Contains(token) && token.Any(char.IsLetter))
            {
                yield return token;
            }
        }
    }

    /// <summary>Distinct tokens, for set overlap.</summary>
    public static HashSet<string> TokenSet(string? text)
        => new(Tokenize(text), StringComparer.OrdinalIgnoreCase);
}
