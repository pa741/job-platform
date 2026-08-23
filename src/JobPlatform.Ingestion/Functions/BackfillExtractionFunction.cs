using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using JobPlatform.Ingestion.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Admin endpoint that queues everything without a current extraction.
/// </summary>
/// <remarks>
/// The backfill for the day a provider is actually configured. Until then the whole corpus has
/// no extractions at all, and nothing in the daily path will ever go back for them — the ingest
/// only queues postings whose text is new or has changed. This is how the existing rows get
/// picked up, and how a bumped extractor version gets applied to the corpus without
/// re-scraping anything.
///
/// Follows <see cref="ReprocessBlobFunction"/>: ASP.NET Core integration types because the host
/// is built with <c>ConfigureFunctionsWebApplication</c>, and no <c>admin/</c> route prefix
/// because the host reserves it and claiming it fails as a 404 rather than as an error.
/// </remarks>
public sealed class BackfillExtractionFunction(
    JobsDbContext db,
    ILogger<BackfillExtractionFunction> logger,
    IExtractionQueue? queue = null)
{
    /// <param name="Limit">
    /// Ceiling on how many postings one call queues. Present because the first backfill after
    /// configuring a provider would otherwise queue the entire corpus in a single request, and
    /// the bill for that should be a decision rather than a side effect.
    /// </param>
    /// <param name="SearchTerm">Restrict to one configured search, for a trial run.</param>
    public sealed record BackfillRequest(int? Limit, string? SearchTerm);

    private const int DefaultLimit = 500;
    private const int MaxLimit = 20_000;

    [Function(nameof(BackfillExtractionFunction))]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "backfill-extraction")]
        HttpRequest request,
        [FromBody] BackfillRequest? body,
        CancellationToken ct)
    {
        if (queue is null)
        {
            // Not an error. It is the configuration this system ships in, and saying so
            // plainly is more useful than a 500 that looks like a fault.
            return new OkObjectResult(new
            {
                queued = 0,
                reason = "No AI provider is configured, so there is no extractor to queue work for.",
            });
        }

        var limit = Math.Clamp(body?.Limit ?? DefaultLimit, 1, MaxLimit);

        var candidates = db.JobPostings
            .Where(p => p.Description != null && p.Description != "");

        if (!string.IsNullOrWhiteSpace(body?.SearchTerm))
        {
            var term = body.SearchTerm;
            candidates = candidates.Where(p => p.SearchTerms.Any(s => s.SearchTerm == term));
        }

        // Anything with no extraction at the current version. The input hash is checked by the
        // consumer rather than here: it needs the description to compute one, and pulling
        // every description across just to filter would defeat the point of a bounded query.
        var sourceKeys = await candidates
            .Where(p => !p.Extractions.Any(e => e.ExtractorVersion == DocumentExtraction.CurrentVersion))
            .OrderByDescending(p => p.LastSeenUtc)
            .Select(p => p.SourceKey)
            .Take(limit)
            .ToListAsync(ct);

        await queue.EnqueueAsync(sourceKeys, ct);

        logger.LogInformation(
            "Backfill queued {Count} posting(s) for extraction (limit {Limit}).",
            sourceKeys.Count, limit);

        return new OkObjectResult(new
        {
            queued = sourceKeys.Count,
            limit,
            more = sourceKeys.Count == limit,
        });
    }
}
