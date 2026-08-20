namespace JobPlatform.Core.Matching;

/// <summary>
/// Scores a shortlist of postings against a CV.
/// </summary>
/// <remarks>
/// The seam the whole matching feature turns on. Implementations range from a pure
/// deterministic scorer to a token-billed model call, and the pipeline treats them
/// identically — which is what lets the API run with no credentials, lets CI stay
/// credential-free, and lets a provider be swapped without a caller changing.
/// </remarks>
public interface ICvRanker
{
    /// <summary>Stable identifier reported back to the caller, e.g. <c>keyword</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Returns at most <paramref name="topN"/> matches, best first. Implementations must
    /// not return a posting that was not in <paramref name="candidates"/>.
    /// </summary>
    Task<IReadOnlyList<PostingMatch>> RankAsync(
        CvProfile profile,
        IReadOnlyList<MatchCandidate> candidates,
        int topN,
        CancellationToken ct = default);
}

/// <summary>Turns CV text into a <see cref="CvProfile"/>.</summary>
public interface ICvProfileExtractor
{
    CvProfile Extract(string cvText);
}
