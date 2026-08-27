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
    /// Technology names whose spelling contains a separator, folded before the split.
    /// </summary>
    /// <remarks>
    /// The dot has to keep splitting - it is ordinary punctuation far more often than it is part
    /// of a name, in "Sr.Product Manager" and "Strategy Officer - i.AI" - so these few names are
    /// folded to a spelling that survives it instead.
    ///
    /// They were the reason three entries in <c>RoleFamilyClassifier</c> could never match:
    /// <c>.net</c> and <c>node.js</c> were written as they are spelled, and by the time a rule
    /// saw the text it had already been cut into <c>net</c> and <c>node</c> + <c>js</c>. The
    /// rules read as though they worked and 24 corpus titles saying ".NET Developer" came out
    /// Unknown, which shows in the dashboard's roleFamily filter as much as in matching.
    ///
    /// Folding rather than making the dot a word character, and that was measured: treating it
    /// as part of a token fixed .NET and broke "Sr.Product Manager" and "React.js Developer",
    /// because any Word.Word spelling then becomes one token. The collateral there is
    /// open-ended; this list is not.
    ///
    /// Longest first - <c>asp.net</c> has to fold before <c>.net</c> can see it.
    ///
    /// The replacement carries a leading space because these names are written glued to what
    /// precedes them as often as not: "C#.NET" and "VB.NET" are both in the corpus, and folding
    /// without the space turns the first into the single token <c>c#dotnet</c>, which matches
    /// nothing. That regressed two titles before it was measured.
    /// </remarks>
    private static readonly (string Spelled, string Folded)[] DottedNames =
    [
        ("asp.net", " aspnet"),
        ("node.js", " nodejs"),
        (".net", " dotnet"),
    ];

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

        var folded = text;

        foreach (var (spelled, replacement) in DottedNames)
        {
            folded = folded.Replace(spelled, replacement, StringComparison.OrdinalIgnoreCase);
        }

        var parts = folded.Split(
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
