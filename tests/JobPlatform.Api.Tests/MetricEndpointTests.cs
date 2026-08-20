using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Api.Features.Metrics;
using SearchTermResponse = JobPlatform.Api.Features.Postings.SearchTermResponse;
using JobPlatform.Core.Metrics;
using Xunit;

namespace JobPlatform.Api.Tests;

public sealed class MetricEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Term = "software-engineer";

    public MetricEndpointTests()
    {
        _client = _factory.CreateClient();

        _factory.Metrics.Digests.Add(Digest(
            new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero), parsed: 120, isNew: 30));
        _factory.Metrics.Digests.Add(Digest(
            new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero), parsed: 140, isNew: 45));

        _factory.Metrics.Rollups.Add(Rollup("2026-08-18", newPostings: 30, cumulative: 30));
        _factory.Metrics.Rollups.Add(Rollup("2026-08-19", newPostings: 45, cumulative: 75));
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static RunDigest Digest(DateTimeOffset scrapedAt, int parsed, int isNew) => new()
    {
        Id = $"run|{scrapedAt:yyyyMMdd}",
        SearchTerm = Term,
        BlobPath = $"jobs/{Term}_{scrapedAt:yyyy-MM-dd}T09-00-00Z.csv",
        ScrapedAtUtc = scrapedAt,
        IngestedAtUtc = scrapedAt.AddMinutes(2),
        ScrapeDate = scrapedAt.ToString("yyyy-MM-dd"),
        Counts = new RunCounts { RowsInFile = parsed + 5, Parsed = parsed, Invalid = 5, New = isNew, Updated = 10 },
        BySite = new Dictionary<string, int> { ["indeed"] = parsed / 2, ["linkedin"] = parsed / 2 },
        ByJobType = new Dictionary<string, int> { ["fulltime"] = parsed },
        Remote = new RemoteBreakdown(parsed / 4, parsed - (parsed / 4), 0.25),
        Freshness = new FreshnessBreakdown { Coverage = 0.4, MedianAgeDays = 3 },
        Salary = new SalaryBreakdown { Coverage = 0.0 },
        TopCompanies = [new NamedCount("Northwind", 12)],
        TopLocations = [new NamedCount("London", 90)],
        TitleKeywords = [new NamedCount("engineer", 60)],
        DescriptionLength = new LengthStats(1200, 4000, 9000),
        // A real London run had min_amount and currency populated in 0% of rows.
        FieldFillRates = new Dictionary<string, double>
        {
            ["title"] = 1.0,
            ["company"] = 0.98,
            ["date_posted"] = 0.40,
            ["job_level"] = 0.03,
            ["min_amount"] = 0.0,
            ["currency"] = 0.0,
        },
    };

    private static DailyRollup Rollup(string date, int newPostings, int cumulative) => new()
    {
        Id = $"daily|{Term}|{date}",
        SearchTerm = Term,
        Date = date,
        UpdatedAtUtc = DateTimeOffset.Parse(date + "T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        RunsIngested = 1,
        PostingsSeen = newPostings * 3,
        NewPostings = newPostings,
        CumulativePostings = cumulative,
        BySite = new Dictionary<string, int> { ["indeed"] = newPostings },
        RemoteShare = 0.25,
        SalaryCoverage = 0.0,
        TopCompanies = [new NamedCount("Northwind", 5)],
    };

    [Fact]
    public async Task Latest_returns_the_most_recent_digest()
    {
        var digest = await _client.GetFromJsonAsync<RunDigest>(
            $"/api/v1/metrics/latest?searchTerm={Term}", Json);

        Assert.Equal("2026-08-19", digest!.ScrapeDate);
        Assert.Equal(140, digest.Counts.Parsed);
        Assert.Equal(45, digest.Counts.New);
    }

    [Fact]
    public async Task Latest_for_an_unknown_term_is_a_404()
    {
        var response = await _client.GetAsync("/api/v1/metrics/latest?searchTerm=nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rollups_come_back_oldest_first_for_charting()
    {
        var rollups = await _client.GetFromJsonAsync<List<DailyRollup>>(
            $"/api/v1/metrics/rollups?searchTerm={Term}", Json);

        Assert.Equal(2, rollups!.Count);
        Assert.Equal("2026-08-18", rollups[0].Date);
        Assert.Equal("2026-08-19", rollups[1].Date);
    }

    [Fact]
    public async Task Rollups_respect_a_date_range()
    {
        var rollups = await _client.GetFromJsonAsync<List<DailyRollup>>(
            $"/api/v1/metrics/rollups?searchTerm={Term}&from=2026-08-19", Json);

        Assert.Single(rollups!);
        Assert.Equal("2026-08-19", rollups![0].Date);
    }

    [Fact]
    public async Task Summary_assembles_headline_numbers_and_a_day_over_day_delta()
    {
        var summary = await _client.GetFromJsonAsync<MetricsSummary>(
            $"/api/v1/metrics/summary?searchTerm={Term}", Json);

        Assert.Equal(140, summary!.PostingsInLastRun);
        Assert.Equal(45, summary.NewInLastRun);
        Assert.Equal(75, summary.CumulativePostings);
        // 45 today against 30 the previous day with data.
        Assert.Equal(15, summary.NewPostingsDelta);
        Assert.Equal(2, summary.DaysOfHistory);
    }

    /// <summary>
    /// The canary. A column silently falling to 0% is how a job board's markup change shows
    /// up - nothing else in the pipeline raises an error when the scraper degrades.
    /// </summary>
    [Fact]
    public async Task Scraper_health_names_the_columns_that_have_gone_empty()
    {
        var health = await _client.GetFromJsonAsync<ScraperHealth>(
            $"/api/v1/metrics/scraper-health?searchTerm={Term}", Json);

        Assert.Equal("degraded", health!.Status);
        Assert.Equal(["currency", "min_amount"], health.EmptyColumns);

        // A barely-populated column is flagged as sparse.
        Assert.Contains(health.SparseColumns, f => f.Field == "job_level");

        // 40% date_posted coverage is normal for this data - a real London run measured
        // exactly that - so it must be neither empty nor sparse, or the signal is noise.
        Assert.DoesNotContain("date_posted", health.EmptyColumns);
        Assert.DoesNotContain(health.SparseColumns, f => f.Field == "date_posted");
    }

    [Fact]
    public async Task Scraper_health_for_a_term_with_no_data_is_unknown_not_an_error()
    {
        var health = await _client.GetFromJsonAsync<ScraperHealth>(
            "/api/v1/metrics/scraper-health?searchTerm=nope", Json);

        Assert.Equal("unknown", health!.Status);
        Assert.Empty(health.EmptyColumns);
    }

    /// <summary>
    /// The regression this guards is an availability one, not a correctness one: clients call
    /// this before they can call anything else, so if it reads SQL the whole dashboard waits
    /// on a database that is paused most of the day. Asserting it works with only Cosmos
    /// populated - the SQL tables here are empty - is what pins that.
    /// </summary>
    [Fact]
    public async Task Search_terms_come_from_Cosmos_and_need_no_SQL_data()
    {
        var terms = await _client.GetFromJsonAsync<List<SearchTermResponse>>(
            "/api/v1/search-terms", Json);

        var term = Assert.Single(terms!);
        Assert.Equal(Term, term.SearchTerm);
        // The latest rollup's cumulative count, not a SQL row count.
        Assert.Equal(75, term.PostingCount);
        Assert.Equal("2026-08-19", term.LastScrapeDate);
    }

    [Fact]
    public async Task Digests_are_returned_newest_first_and_respect_the_limit()
    {
        var digests = await _client.GetFromJsonAsync<List<RunDigest>>(
            $"/api/v1/metrics/digests?searchTerm={Term}&limit=1", Json);

        Assert.Single(digests!);
        Assert.Equal("2026-08-19", digests![0].ScrapeDate);
    }
}
