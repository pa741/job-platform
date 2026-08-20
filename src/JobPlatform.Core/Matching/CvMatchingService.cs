using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Core.Matching;

/// <summary>Supplies the postings a match request should consider.</summary>
/// <remarks>
/// An interface rather than a direct repository dependency so <c>Core</c> keeps its
/// Azure-free, fully-unit-testable property. The SQL implementation lives in Data.
/// </remarks>
public interface IMatchCandidateSource
{
    Task<IReadOnlyList<MatchCandidate>> GetCandidatesAsync(
        MatchCandidateQuery query, CancellationToken ct = default);
}

public sealed record MatchCandidateQuery
{
    public string? SearchTerm { get; init; }
    public bool? IsRemote { get; init; }
    public string? Site { get; init; }
    public string? Country { get; init; }
    public DateOnly? PostedFrom { get; init; }
    public int Limit { get; init; } = 400;
    public int DescriptionCharacterBudget { get; init; } = 1500;
}

/// <summary>
/// The matching pipeline: retrieve, prefilter, rerank.
/// </summary>
/// <remarks>
/// Two stages rather than one because the ranker may be token-billed. Retrieval pulls a wide
/// set from SQL, the keyword ranker cuts it to <c>RerankLimit</c>, and only that shortlist
/// reaches the configured ranker. This bounds cost by configuration rather than by how many
/// postings happen to be in the database, and it means the expensive ranker never sees
/// obviously irrelevant postings.
///
/// If the configured ranker fails, the keyword ordering is already computed and is returned
/// instead. A matching endpoint that 500s because a third party is rate-limiting is worse
/// than one that returns a defensible ordering and says so.
/// </remarks>
public sealed class CvMatchingService(
    ICvProfileExtractor extractor,
    ICvRanker ranker,
    KeywordCvRanker keywordRanker,
    IMatchCandidateSource candidates,
    IOptions<CvMatchingOptions> options,
    ILogger<CvMatchingService> logger)
{
    private readonly CvMatchingOptions _options = options.Value;

    public CvProfile ExtractProfile(string cvText) => extractor.Extract(Truncate(cvText));

    public async Task<MatchOutcome> MatchAsync(
        string cvText,
        MatchCandidateQuery query,
        int? topN,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cvText);
        ArgumentNullException.ThrowIfNull(query);

        var profile = ExtractProfile(cvText);
        var effectiveTopN = Math.Clamp(topN ?? _options.DefaultTopN, 1, _options.MaxTopN);

        var retrieved = await candidates.GetCandidatesAsync(
            query with
            {
                Limit = _options.RetrievalLimit,
                DescriptionCharacterBudget = _options.DescriptionCharacterBudget,
            },
            ct);

        if (retrieved.Count == 0)
        {
            return new MatchOutcome(
                [], profile, new MatchProvenance(ranker.Name, 0, DegradedToFallback: false));
        }

        // Stage one. Always runs, even when it is also stage two: its output is the fallback.
        var shortlist = keywordRanker.Rank(profile, retrieved, _options.RerankLimit);

        if (ranker is KeywordCvRanker)
        {
            return new MatchOutcome(
                shortlist.Take(effectiveTopN).ToList(),
                profile,
                new MatchProvenance(ranker.Name, retrieved.Count, DegradedToFallback: false));
        }

        var byId = retrieved.ToDictionary(c => c.PostingId);
        var shortlistCandidates = shortlist
            .Select(m => byId[m.PostingId])
            .ToList();

        try
        {
            var ranked = await ranker.RankAsync(profile, shortlistCandidates, effectiveTopN, ct);

            // ICvRanker states that implementations must not return a posting that was not
            // offered to them. Enforced here rather than trusted, because this is where the
            // guarantee is made: a ranker backed by a language model can hallucinate an id,
            // and an unenforced contract would surface that to the caller as a posting that
            // does not exist.
            ranked = [.. ranked.Where(m => byId.ContainsKey(m.PostingId))];

            // A ranker returning nothing is not a reason to return nothing to the caller.
            if (ranked.Count > 0)
            {
                return new MatchOutcome(
                    ranked,
                    profile,
                    new MatchProvenance(ranker.Name, retrieved.Count, DegradedToFallback: false));
            }

            logger.LogWarning("Ranker {Ranker} returned no matches; falling back to keyword order.", ranker.Name);

            return Degraded(shortlist, profile, retrieved.Count, effectiveTopN, "ranker returned no matches");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ranker {Ranker} failed; falling back to keyword order.", ranker.Name);

            return Degraded(shortlist, profile, retrieved.Count, effectiveTopN, ex.GetType().Name);
        }
    }

    private MatchOutcome Degraded(
        IReadOnlyList<PostingMatch> shortlist,
        CvProfile profile,
        int considered,
        int topN,
        string reason)
        => new(
            shortlist.Take(topN).ToList(),
            profile,
            new MatchProvenance(keywordRanker.Name, considered, DegradedToFallback: true)
            {
                DegradationReason = reason,
            });

    private string Truncate(string cvText)
        => cvText.Length <= _options.MaxCvCharacters
            ? cvText
            : cvText[.._options.MaxCvCharacters];
}
