using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Which CV the dashboard says goes with a posting.
/// </summary>
/// <remarks>
/// <b>Written against a real defect.</b> The shortlist rendered the draft's
/// <c>curriculumVitaeMarkdown</c>, and no draft has carried one since the CV stopped being written
/// per posting - so the panel was blank beside a perfectly good cover letter, which reads as a
/// failure rather than as the design. The page now asks which CV was *chosen*, and this is what it
/// asks.
///
/// <b>Two of these assert an absence, and the absence is the point.</b> This route runs the same
/// selector the agent's pack runs, and the pack does two further things - it puts a genuine tie to
/// a model and it parks a posting nothing fits. Neither may happen because somebody expanded a row
/// on a page: one spends money on a route a client can call repeatedly, the other puts a posting
/// down without anybody deciding to. So a NoFit here writes nothing at all.
/// </remarks>
public sealed class CvChoiceEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset Scored = new(2026, 9, 5, 3, 30, 0, TimeSpan.Zero);

    /// <summary>A posting asking for what the seeded CV actually says. One clear winner.</summary>
    private const long Fits = 501;

    /// <summary>A posting asking for what no CV in the library covers.</summary>
    private const long DoesNotFit = 502;

    /// <summary>A real posting this candidate was never scored against.</summary>
    private const long Unmatched = 503;

    [Fact]
    public async Task The_chosen_cv_is_named_with_the_reasoning_that_chose_it()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        await ReadCvAsync(harness, "skill.kubernetes", "skill.csharp");
        await ScoreAsync(harness, Fits, "skill.kubernetes", "skill.csharp");

        var choice = await harness.As(CvLibraryHarness.Ada)
            .GetFromJsonAsync<JsonElement>($"/api/v1/matches/{Fits}/cv", Json);

        Assert.Equal("Chosen", choice.GetProperty("outcome").GetString());
        Assert.Equal("Backend .NET", choice.GetProperty("chosen").GetProperty("label").GetString());
        Assert.Equal(harness.Backend, choice.GetProperty("chosen").GetProperty("variantId").GetInt64());

        // The id is what makes this actionable rather than decorative: it is how the page offers
        // the file that would actually be sent, from the library's own download route.
        Assert.Equal("arithmetic", choice.GetProperty("decidedBy").GetString());
        Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("rationale").GetString()));

        // The retired CV is not in the running and is not counted. "None of your two fit" over a
        // library of one is a sentence that sends somebody looking for a CV they archived.
        Assert.Equal(1, choice.GetProperty("considered").GetInt32());
    }

    [Fact]
    public async Task Nothing_fitting_is_reported_as_what_to_write_next_and_parks_nothing()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        await ReadCvAsync(harness, "skill.kubernetes", "skill.csharp");
        await ScoreAsync(harness, DoesNotFit, "skill.terraform", "skill.aws");

        var choice = await harness.As(CvLibraryHarness.Ada)
            .GetFromJsonAsync<JsonElement>($"/api/v1/matches/{DoesNotFit}/cv", Json);

        Assert.Equal("NoFit", choice.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("chosen").ValueKind);
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("decidedBy").ValueKind);

        var missing = choice.GetProperty("missing").EnumerateArray()
            .Select(gap => gap.GetProperty("concept").GetString())
            .ToList();

        Assert.Contains("skill.terraform", missing);

        // Labels rather than keys for the reader, and never a silently dropped requirement: a key
        // the vocabulary does not know prints as itself.
        Assert.All(
            choice.GetProperty("missing").EnumerateArray(),
            gap => Assert.False(string.IsNullOrWhiteSpace(gap.GetProperty("label").GetString())));

        // The pack parks a posting nothing fits, with the concepts it wanted. A page load must
        // not: the posting would leave the apply queue because somebody looked at it.
        await using var db = harness.Database();

        Assert.Empty(db.Submissions);
    }

    [Fact]
    public async Task A_posting_this_candidate_was_never_scored_against_is_a_404()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        await ScoreAsync(harness, Fits, "skill.kubernetes");
        await PostAsync(harness, Unmatched);

        var response = await harness.As(CvLibraryHarness.Ada)
            .GetAsync($"/api/v1/matches/{Unmatched}/cv");

        // Not "no CV fits", which is what a route keyed on the posting alone would answer. A
        // posting with no match for this candidate has no requirement set to choose against, and
        // saying so is also what stops the id space being read through this route.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_candidates_match_is_not_readable_through_this_route()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        await ScoreAsync(harness, Fits, "skill.kubernetes");

        var response = await harness.As(CvLibraryHarness.Grace)
            .GetAsync($"/api/v1/matches/{Fits}/cv");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------

    /// <summary>
    /// Scores one posting against Ada with the demands a test names.
    /// </summary>
    /// <remarks>
    /// Written through <c>UpsertScoresAsync</c> rather than by setting the JSON columns directly:
    /// the route rebuilds the demands out of those columns, so a fixture spelling the JSON itself
    /// would be asserting against its own idea of the format rather than against the writer's.
    /// </remarks>
    private static async Task ScoreAsync(CvLibraryHarness harness, long postingId, params string[] demands)
    {
        await PostAsync(harness, postingId);

        await using var db = harness.Database();

        var result = new MatchResult
        {
            Score = 80,
            Coverage = 1,
            Gaps = [.. demands.Select(key => new ConceptGap(key, AssertionPolarity.Required, null))],
        };

        await new JobMatchRepository(db).UpsertScoresAsync(
            harness.AdaProfile,
            [(new PostingFacts { PostingId = postingId }, result)],
            [],
            Scored);
    }

    /// <summary>
    /// What the extractor read out of the seeded CV.
    /// </summary>
    /// <remarks>
    /// The harness seeds variants through the repository, which stores the words and does not run
    /// an extractor - that happens on the route. So the concepts are written here, through the one
    /// method that writes that table, because a CV with no concepts scores zero against everything
    /// and every outcome in this file would be NoFit for the wrong reason.
    /// </remarks>
    private static async Task ReadCvAsync(CvLibraryHarness harness, params string[] keys)
    {
        await using var db = harness.Database();

        await new CvVariantRepository(db).ReplaceConceptsAsync(
            harness.AdaProfile,
            harness.Backend,
            [.. keys.Select(key =>
                new ConceptAssertion(key, AssertionSource.Model, AssertionPolarity.Mentioned))],
            resolverVersion: 1);
    }

    private static async Task PostAsync(CvLibraryHarness harness, long postingId)
    {
        await using var db = harness.Database();

        db.JobPostings.Add(new JobPostingEntity
        {
            Id = postingId,
            SourceKey = $"test:{postingId}",
            Site = "test",
            ExternalId = postingId.ToString(),
            ContentHash = new string('a', 64),
            Title = $"Role {postingId}",
            Company = "Northwind",
            Description = "An advert.",
            DescriptionLength = 10,
            FirstSeenUtc = Scored,
            LastSeenUtc = Scored,
        });

        await db.SaveChangesAsync();
    }
}
