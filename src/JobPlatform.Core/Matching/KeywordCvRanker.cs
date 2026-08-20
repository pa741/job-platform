using JobPlatform.Core.Text;

namespace JobPlatform.Core.Matching;

/// <summary>
/// Deterministic overlap scoring. No network, no credentials, no cost.
/// </summary>
/// <remarks>
/// Serves two roles at once, which is why it is worth more than its sophistication suggests:
/// it is the default ranker, and it is the *retrieval floor* that shortlists candidates
/// before a token-billed ranker sees them. That second role means its scoring quality caps
/// what any downstream ranker can achieve — a posting it drops is never reranked.
/// </remarks>
public sealed class KeywordCvRanker : ICvRanker
{
    public string Name => "keyword";

    /// <summary>
    /// A title hit is worth far more than a description hit. Descriptions are long and
    /// mention technologies in passing ("our stack includes X"); a title is the posting
    /// stating what the job actually is.
    /// </summary>
    private const double TitleWeight = 6.0;
    private const double SkillInTitleWeight = 10.0;
    private const double SkillInDescriptionWeight = 2.0;
    private const double DescriptionTokenWeight = 0.5;

    public Task<IReadOnlyList<PostingMatch>> RankAsync(
        CvProfile profile,
        IReadOnlyList<MatchCandidate> candidates,
        int topN,
        CancellationToken ct = default)
        => Task.FromResult(Rank(profile, candidates, topN));

    /// <summary>Synchronous entry point, used by the pipeline's prefilter stage.</summary>
    public IReadOnlyList<PostingMatch> Rank(
        CvProfile profile,
        IReadOnlyList<MatchCandidate> candidates,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidates);

        var cvTokens = new HashSet<string>(profile.Tokens, StringComparer.OrdinalIgnoreCase);
        var cvSkills = new HashSet<string>(profile.Skills, StringComparer.OrdinalIgnoreCase);

        var scored = new List<PostingMatch>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var titleTokens = TitleTokenizer.TokenSet(candidate.Title);
            var descriptionLower = candidate.Description?.ToLowerInvariant();

            var titleHits = titleTokens.Where(cvTokens.Contains).ToList();

            var skillsInTitle = cvSkills
                .Where(s => candidate.Title.Contains(s, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var skillsInDescription = descriptionLower is null
                ? []
                : cvSkills
                    .Where(s => !skillsInTitle.Contains(s, StringComparer.OrdinalIgnoreCase)
                        && descriptionLower.Contains(s, StringComparison.Ordinal))
                    .ToList();

            // Description tokens are counted distinctly and capped: a 10 KB posting would
            // otherwise outscore a precise 2 KB one purely by being longer.
            var descriptionTokenHits = descriptionLower is null
                ? 0
                : Math.Min(cvTokens.Count(t => descriptionLower.Contains(t, StringComparison.Ordinal)), 40);

            var raw =
                (titleHits.Count * TitleWeight) +
                (skillsInTitle.Count * SkillInTitleWeight) +
                (skillsInDescription.Count * SkillInDescriptionWeight) +
                (descriptionTokenHits * DescriptionTokenWeight);

            if (profile.PrefersRemote == true && candidate.IsRemote)
            {
                raw += 3.0;
            }

            var matched = skillsInTitle.Concat(skillsInDescription)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            scored.Add(new PostingMatch
            {
                PostingId = candidate.PostingId,
                Score = raw,
                MatchedSkills = matched,
                MissingSkills = [],
            });
        }

        // Normalised against the best score in this set, so the number reads as a percentage.
        // Explicitly *not* comparable across requests — the divisor changes every time.
        var best = scored.Count == 0 ? 0 : scored.Max(m => m.Score);

        return scored
            .Select(m => m with { Score = best <= 0 ? 0 : Math.Round(m.Score / best * 100, 1) })
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.PostingId)
            .Take(topN)
            .ToList();
    }
}
