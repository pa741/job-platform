using JobPlatform.Core.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The matching pipeline, exercised without any credentials.
/// </summary>
/// <remarks>
/// That the keyword ranker is the default is what makes this possible, and it is why the
/// default is not merely a placeholder: the whole feature - retrieval, prefiltering,
/// ranking, fallback - is verifiable in CI on a fresh clone.
/// </remarks>
public sealed class MatchingTests
{
    private const string Cv =
        """
        Pablo, backend engineer with 7 years of experience.
        Day to day: C#, .NET, Azure, Kubernetes, SQL and Terraform.
        Built event-driven microservices and CI/CD pipelines. Open to remote work.
        """;

    private static CvMatchingService Service(
        ICvRanker ranker, IMatchCandidateSource source, CvMatchingOptions? options = null)
    {
        var keyword = new KeywordCvRanker();

        return new CvMatchingService(
            new KeywordCvProfileExtractor(),
            ranker,
            keyword,
            source,
            Options.Create(options ?? new CvMatchingOptions()),
            NullLogger<CvMatchingService>.Instance);
    }

    private static MatchCandidate Candidate(long id, string title, string? description = null)
        => new() { PostingId = id, Title = title, Description = description };

    [Fact]
    public void The_extractor_recognises_skills_and_experience()
    {
        var profile = new KeywordCvProfileExtractor().Extract(Cv);

        Assert.Contains("c#", profile.Skills);
        Assert.Contains("azure", profile.Skills);
        Assert.Contains("kubernetes", profile.Skills);
        Assert.Contains("terraform", profile.Skills);
        Assert.Equal(7, profile.YearsExperience);
        Assert.True(profile.PrefersRemote);
    }

    [Fact]
    public void A_punctuated_skill_is_not_matched_by_a_bare_token()
    {
        // "go" must not match inside "google", which is why punctuated and multi-word skills
        // go through the raw text and plain words through the token set.
        var profile = new KeywordCvProfileExtractor().Extract("I worked at Google on search.");

        Assert.DoesNotContain("go", profile.Skills);
    }

    [Fact]
    public async Task The_keyword_ranker_puts_an_obvious_match_above_an_unrelated_posting()
    {
        var source = new StubSource(
            Candidate(1, "Pastry Chef", "Croissants, laminated dough, early starts."),
            Candidate(2, "Backend Engineer - C# / Azure", "C#, .NET, Azure, Kubernetes, Terraform."),
            Candidate(3, "Warehouse Operative", "Forklift licence required."));

        var outcome = await Service(new KeywordCvRanker(), source).MatchAsync(Cv, new(), topN: 3);

        Assert.Equal(2, outcome.Matches[0].PostingId);
        Assert.Equal(100, outcome.Matches[0].Score);
        Assert.True(outcome.Matches[0].Score > outcome.Matches[1].Score);
        Assert.Equal("keyword", outcome.Provenance.Provider);
        Assert.False(outcome.Provenance.DegradedToFallback);
    }

    [Fact]
    public async Task Matching_returns_at_most_topN()
    {
        var source = new StubSource(Enumerable.Range(1, 30)
            .Select(i => Candidate(i, $"Backend Engineer {i}", "C# Azure"))
            .ToArray());

        var outcome = await Service(new KeywordCvRanker(), source).MatchAsync(Cv, new(), topN: 5);

        Assert.Equal(5, outcome.Matches.Count);
        Assert.Equal(30, outcome.Provenance.CandidatesConsidered);
    }

    [Fact]
    public async Task TopN_is_clamped_to_the_configured_maximum()
    {
        var source = new StubSource(Enumerable.Range(1, 40)
            .Select(i => Candidate(i, $"Engineer {i}", "C#"))
            .ToArray());

        var outcome = await Service(
                new KeywordCvRanker(), source, new CvMatchingOptions { MaxTopN = 3 })
            .MatchAsync(Cv, new(), topN: 999);

        Assert.Equal(3, outcome.Matches.Count);
    }

    [Fact]
    public async Task No_candidates_yields_no_matches_rather_than_an_error()
    {
        var outcome = await Service(new KeywordCvRanker(), new StubSource()).MatchAsync(Cv, new(), topN: 5);

        Assert.Empty(outcome.Matches);
        Assert.Equal(0, outcome.Provenance.CandidatesConsidered);
    }

    /// <summary>
    /// The behaviour that keeps the endpoint useful when a paid ranker is unavailable. A 500
    /// here would mean a third party's rate limit takes down a feature that has a perfectly
    /// good deterministic answer already computed.
    /// </summary>
    [Fact]
    public async Task A_failing_ranker_degrades_to_keyword_order_and_says_so()
    {
        var source = new StubSource(
            Candidate(1, "Pastry Chef", "Croissants."),
            Candidate(2, "Backend Engineer - C# / Azure", "C#, .NET, Azure, Kubernetes."));

        var outcome = await Service(new ThrowingRanker(), source).MatchAsync(Cv, new(), topN: 2);

        Assert.Equal(2, outcome.Matches[0].PostingId);
        Assert.True(outcome.Provenance.DegradedToFallback);
        Assert.Equal("keyword", outcome.Provenance.Provider);
        Assert.Equal(nameof(InvalidOperationException), outcome.Provenance.DegradationReason);
    }

    [Fact]
    public async Task A_ranker_returning_nothing_also_degrades_rather_than_returning_nothing()
    {
        var source = new StubSource(Candidate(1, "Backend Engineer", "C# Azure"));

        var outcome = await Service(new EmptyRanker(), source).MatchAsync(Cv, new(), topN: 5);

        Assert.Single(outcome.Matches);
        Assert.True(outcome.Provenance.DegradedToFallback);
    }

    /// <summary>
    /// The two-stage pipeline's cost control: the configured ranker must only ever see
    /// RerankLimit candidates, however many were retrieved.
    /// </summary>
    [Fact]
    public async Task Only_the_prefiltered_shortlist_reaches_the_configured_ranker()
    {
        var source = new StubSource(Enumerable.Range(1, 200)
            .Select(i => Candidate(i, $"Backend Engineer {i}", "C# Azure Kubernetes"))
            .ToArray());

        var spy = new RecordingRanker();

        var outcome = await Service(spy, source, new CvMatchingOptions { RerankLimit = 12 })
            .MatchAsync(Cv, new(), topN: 5);

        Assert.Equal(12, spy.SeenCandidates);
        Assert.Equal(200, outcome.Provenance.CandidatesConsidered);
        Assert.False(outcome.Provenance.DegradedToFallback);
    }

    [Fact]
    public async Task A_ranker_inventing_a_posting_id_has_it_discarded()
    {
        var source = new StubSource(Candidate(1, "Backend Engineer", "C# Azure"));

        var outcome = await Service(new InventingRanker(), source).MatchAsync(Cv, new(), topN: 5);

        // The invented id is dropped by the pipeline, leaving an empty ranking, which it
        // then answers with the keyword order rather than with nothing.
        Assert.True(outcome.Provenance.DegradedToFallback);
        Assert.Equal(1, outcome.Matches[0].PostingId);
    }

    private sealed class StubSource(params MatchCandidate[] candidates) : IMatchCandidateSource
    {
        public Task<IReadOnlyList<MatchCandidate>> GetCandidatesAsync(
            MatchCandidateQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MatchCandidate>>(candidates.Take(query.Limit).ToList());
    }

    private sealed class ThrowingRanker : ICvRanker
    {
        public string Name => "throwing";

        public Task<IReadOnlyList<PostingMatch>> RankAsync(
            CvProfile profile, IReadOnlyList<MatchCandidate> candidates, int topN, CancellationToken ct = default)
            => throw new InvalidOperationException("upstream is unavailable");
    }

    private sealed class EmptyRanker : ICvRanker
    {
        public string Name => "empty";

        public Task<IReadOnlyList<PostingMatch>> RankAsync(
            CvProfile profile, IReadOnlyList<MatchCandidate> candidates, int topN, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PostingMatch>>([]);
    }

    private sealed class RecordingRanker : ICvRanker
    {
        public int SeenCandidates { get; private set; }

        public string Name => "recording";

        public Task<IReadOnlyList<PostingMatch>> RankAsync(
            CvProfile profile, IReadOnlyList<MatchCandidate> candidates, int topN, CancellationToken ct = default)
        {
            SeenCandidates = candidates.Count;

            return Task.FromResult<IReadOnlyList<PostingMatch>>(candidates
                .Take(topN)
                .Select(c => new PostingMatch { PostingId = c.PostingId, Score = 50 })
                .ToList());
        }
    }

    private sealed class InventingRanker : ICvRanker
    {
        public string Name => "inventing";

        public Task<IReadOnlyList<PostingMatch>> RankAsync(
            CvProfile profile, IReadOnlyList<MatchCandidate> candidates, int topN, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PostingMatch>>(
                [new PostingMatch { PostingId = 987654, Score = 99 }]);
    }
}
