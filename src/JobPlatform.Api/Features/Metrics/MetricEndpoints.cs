using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Features.Postings;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Metrics;
using JobPlatform.Data.Cosmos;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Metrics;

/// <summary>
/// Everything the dashboard charts. Reads Cosmos exclusively.
/// </summary>
/// <remarks>
/// Not one of these endpoints touches Azure SQL, and that is the point. The equivalent
/// figures could be recomputed relationally, but SQL here is serverless and billed by
/// wall-clock second against a monthly grant that a single daily ingest already half spends -
/// a polling dashboard would exhaust it and the database would auto-pause until the following
/// month. Ingestion has already written every one of these numbers to Cosmos, which is always
/// on and RU-billed inside a free ceiling, so serving them from there costs effectively
/// nothing and cannot take the database down.
/// </remarks>
public sealed class MetricEndpoints : IEndpointGroup
{
    /// <summary>Rollup history for the summary's trend line and delta.</summary>
    private const int SummaryHistoryDays = 30;

    /// <summary>Below this fill rate a column is reported as sparse.</summary>
    private const double SparseThreshold = 0.25;

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/metrics")
            .WithTags("Metrics")
            .RequireAuthorization(AuthSetup.PublicReadPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy)
            .CacheOutput(CacheSetup.MetricsPolicy);

        group.MapGet("/latest", LatestAsync)
            .WithName("GetLatestDigest")
            .WithSummary("The most recent run digest for a search term.");

        group.MapGet("/digests", DigestsAsync)
            .WithName("ListDigests")
            .WithSummary("Run digests over a time range, newest first.");

        group.MapGet("/rollups", RollupsAsync)
            .WithName("ListRollups")
            .WithSummary("Daily rollups over a date range, oldest first. The dashboard time series.");

        group.MapGet("/summary", SummaryAsync)
            .WithName("GetMetricsSummary")
            .WithSummary("Headline numbers, assembled from the latest digest and recent rollups.");

        group.MapGet("/scraper-health", ScraperHealthAsync)
            .WithName("GetScraperHealth")
            .WithSummary("Per-column fill rates and which columns have silently gone empty.");

        // Outside the /metrics group because it is not a metric - it is the axis every other
        // route partitions on, and clients fetch it first.
        //
        // Served from Cosmos, and that matters more than it looks: this is the call a client
        // makes before it can make any other, so sourcing it from SQL put the whole
        // dashboard behind a database that is paused most of the day. Every page then waited
        // on a wake-up, including the ones that read nothing but Cosmos. Cosmos already
        // knows which search terms exist, so nothing is gained by asking SQL.
        routes.MapGet("/search-terms", SearchTermsAsync)
            .WithTags("Metrics")
            .WithName("ListSearchTerms")
            .WithSummary("Search terms the platform holds data for.")
            .RequireAuthorization(AuthSetup.PublicReadPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy)
            .CacheOutput(CacheSetup.FacetsPolicy);
    }

    private static async Task<IResult> SearchTermsAsync(
        [FromServices] IMetricsSource metrics,
        CancellationToken ct)
    {
        var terms = await metrics.ListSearchTermsAsync(ct);

        // One single-partition query per term. There are as many terms as the scraper is
        // configured with - a handful - so this stays cheap, and the whole response is
        // output-cached for minutes.
        var summaries = await Task.WhenAll(terms.Select(async term =>
        {
            var rollups = await metrics.ListDailyRollupsAsync(term, from: null, to: null, ct);
            var latest = rollups.Count > 0 ? rollups[^1] : null;

            return new SearchTermResponse(
                term,
                latest?.CumulativePostings ?? 0,
                latest?.Date,
                latest?.UpdatedAtUtc);
        }));

        return TypedResults.Ok(summaries
            .OrderByDescending(s => s.PostingCount)
            .ToList());
    }

    private static async Task<IResult> LatestAsync(
        [FromServices] IMetricsSource metrics,
        string searchTerm,
        CancellationToken ct)
    {
        var digest = await metrics.GetLatestRunDigestAsync(searchTerm, ct);

        return digest is null
            ? TypedResults.Problem(
                detail: $"No run digest for search term '{searchTerm}'.",
                statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(digest);
    }

    private static async Task<IResult> DigestsAsync(
        [FromServices] IMetricsSource metrics,
        string searchTerm,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 30)
    {
        var digests = await metrics.ListRunDigestsAsync(searchTerm, from, to, limit, ct);
        return TypedResults.Ok(digests);
    }

    private static async Task<IResult> RollupsAsync(
        [FromServices] IMetricsSource metrics,
        string searchTerm,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        var rollups = await metrics.ListDailyRollupsAsync(searchTerm, from, to, ct);
        return TypedResults.Ok(rollups);
    }

    private static async Task<IResult> SummaryAsync(
        [FromServices] IMetricsSource metrics,
        string searchTerm,
        TimeProvider time,
        CancellationToken ct)
    {
        var digest = await metrics.GetLatestRunDigestAsync(searchTerm, ct);

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var rollups = await metrics.ListDailyRollupsAsync(
            searchTerm, today.AddDays(-SummaryHistoryDays), today, ct);

        if (digest is null && rollups.Count == 0)
        {
            return TypedResults.Problem(
                detail: $"No metrics for search term '{searchTerm}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var latestRollup = rollups.Count > 0 ? rollups[^1] : null;

        // Compared against the previous day *that has data*, not literally yesterday: the
        // scraper does not always run, and a missing day would otherwise read as a collapse
        // in new postings rather than as an absence of data.
        int? delta = rollups.Count >= 2
            ? rollups[^1].NewPostings - rollups[^2].NewPostings
            : null;

        return TypedResults.Ok(new MetricsSummary
        {
            SearchTerm = searchTerm,
            LastScrapedAtUtc = digest?.ScrapedAtUtc,
            LastIngestedAtUtc = digest?.IngestedAtUtc,
            LastScrapeDate = digest?.ScrapeDate ?? latestRollup?.Date,
            PostingsInLastRun = digest?.Counts.Parsed ?? 0,
            NewInLastRun = digest?.Counts.New ?? 0,
            UpdatedInLastRun = digest?.Counts.Updated ?? 0,
            InvalidInLastRun = digest?.Counts.Invalid ?? 0,
            CumulativePostings = latestRollup?.CumulativePostings ?? 0,
            NewPostingsDelta = delta,
            Enrichment = digest?.Enrichment ?? new EnrichmentBreakdown(),
            RemoteShare = digest?.Remote.RemoteShare ?? latestRollup?.RemoteShare ?? 0,
            SalaryCoverage = digest?.Salary.Coverage ?? latestRollup?.SalaryCoverage ?? 0,
            MedianAgeDays = digest?.Freshness.MedianAgeDays,
            BySite = digest?.BySite ?? latestRollup?.BySite ?? new Dictionary<string, int>(),
            TopCompanies = digest?.TopCompanies ?? latestRollup?.TopCompanies ?? [],
            TitleKeywords = digest?.TitleKeywords ?? [],
            DaysOfHistory = rollups.Count,
        });
    }

    private static async Task<IResult> ScraperHealthAsync(
        [FromServices] IMetricsSource metrics,
        string searchTerm,
        CancellationToken ct)
    {
        var digest = await metrics.GetLatestRunDigestAsync(searchTerm, ct);

        if (digest is null)
        {
            return TypedResults.Ok(new ScraperHealth
            {
                SearchTerm = searchTerm,
                Status = "unknown",
            });
        }

        var empty = digest.FieldFillRates
            .Where(f => f.Value <= 0)
            .Select(f => f.Key)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var sparse = digest.FieldFillRates
            .Where(f => f.Value > 0 && f.Value < SparseThreshold)
            .OrderBy(f => f.Value)
            .Select(f => new FieldFill(f.Key, f.Value))
            .ToList();

        return TypedResults.Ok(new ScraperHealth
        {
            SearchTerm = searchTerm,
            LastScrapedAtUtc = digest.ScrapedAtUtc,
            // A wholly empty column is the signal worth alerting on. Sparse columns are
            // routine in this data - date_posted sat at 40% in a real run - so they are
            // reported without being treated as a fault.
            Status = empty.Count > 0 ? "degraded" : "healthy",
            EmptyColumns = empty,
            SparseColumns = sparse,
            FieldFillRates = digest.FieldFillRates,
            RowsInLastRun = digest.Counts.RowsInFile,
            InvalidInLastRun = digest.Counts.Invalid,
            BySite = digest.BySite,
        });
    }
}
