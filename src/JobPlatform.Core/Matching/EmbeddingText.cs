namespace JobPlatform.Core.Matching;

/// <summary>
/// Exactly what gets embedded, on each side of the match.
/// </summary>
/// <remarks>
/// <b>One home for this because the two sides have to be shaped the same way, and they are
/// shaped by different code.</b> The advert side is built by a corpus pass over SQL rows; the
/// profile side is built from <c>CandidateProfile.ToDocument()</c> in the sweep. Cosine
/// similarity between them is only meaningful if both were produced by the same recipe, and a
/// recipe living in two files is a recipe that eventually differs by a heading.
///
/// It is also the definition <see cref="EmbeddingVector.EmbeddingVersion"/> guards. Changing
/// what goes in here changes every stored vector's meaning, so it is a version bump exactly as
/// changing the model would be.
/// </remarks>
public static class EmbeddingText
{
    /// <summary>
    /// How much of an advert is embedded.
    /// </summary>
    /// <remarks>
    /// Roughly 1,500 tokens, well inside the model's 8,192 ceiling, and a limit on relevance
    /// rather than on capacity: a long advert's tail is boilerplate - equal-opportunity
    /// statements, benefits, how to apply - and averaging it into the vector dilutes the part
    /// that says what the job is. This is the width the ranking in <see cref="MatchRanker"/> was
    /// measured at, which is the reason it is this number and not a rounder one.
    /// </remarks>
    public const int MaxAdvertChars = 6_000;

    /// <summary>
    /// How much of a profile document is embedded.
    /// </summary>
    /// <remarks>
    /// Twice the advert's, because the two documents are not the same shape. An advert describes
    /// one role; a profile describes a career, and truncating it to one advert's length would
    /// cut a candidate off part way through their second job. Still a bound rather than none -
    /// an unbounded input is a call whose cost is decided by how much somebody typed.
    /// </remarks>
    public const int MaxProfileChars = 12_000;

    /// <summary>
    /// One advert as the embedding pass reads it: the title, then as much body as fits.
    /// </summary>
    /// <remarks>
    /// The title leads and is never truncated. It is the densest sentence in the whole advert -
    /// "Senior .NET Developer" says more about fit than the next four hundred words - and
    /// putting it first means it survives whatever the limit cuts.
    /// </remarks>
    public static string ForAdvert(string? title, string? description)
    {
        var body = Clip(description, MaxAdvertChars);

        return string.IsNullOrWhiteSpace(title)
            ? body
            : string.IsNullOrEmpty(body) ? title.Trim() : $"{title.Trim()}\n{body}";
    }

    /// <summary>The profile document, clipped. <c>CandidateProfile.ToDocument()</c> builds it.</summary>
    public static string ForProfile(string? document) => Clip(document, MaxProfileChars);

    private static string Clip(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();

        return trimmed.Length <= limit ? trimmed : trimmed[..limit];
    }
}
