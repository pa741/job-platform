using System.Globalization;
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
    ExtractionBatchRepository batchRecord,
    TimeProvider time,
    ILogger<BackfillExtractionFunction> logger,
    IExtractionQueue? queue = null,
    IBatchDocumentExtractor? batchExtractor = null)
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
        CancellationToken ct)
    {
        var body = await RequestBody.ReadAsync<BackfillRequest>(request, ct);

        if (queue is null && batchExtractor is null)
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

        // The batch path is preferred wherever it is configured, and the reason is not price.
        // A corpus-wide pass through the synchronous path competes with itself for the
        // deployment's tokens-per-minute allowance - which is exactly what stalled the first
        // real backfill - whereas a batch provider gives this work its own rate pool. The queue
        // stays for deployments with no batch provider, and for profiles, which are submitted
        // by a person who is waiting and cannot be told to come back tomorrow.
        if (batchExtractor is not null)
        {
            return new OkObjectResult(await SubmitBatchAsync(sourceKeys, limit, ct));
        }

        await queue!.EnqueueAsync(sourceKeys, ct);

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

    /// <summary>
    /// Sends the pending postings to the batch provider and records what went.
    /// </summary>
    /// <remarks>
    /// Postings already inside an open batch are excluded. Without that, running this endpoint
    /// twice in a day pays twice for the same work and points two collectors at one set of rows.
    ///
    /// The text hash is computed here, at submission, and stored with the item - not recomputed
    /// on collection. A batch is answered up to a day later and the scraper may have re-listed
    /// the posting with an edited description in the meantime; the extraction row has to be
    /// keyed on what was actually read.
    /// </remarks>
    private async Task<object> SubmitBatchAsync(List<string> sourceKeys, int limit, CancellationToken ct)
    {
        var inFlight = (await batchRecord.GetInFlightPostingIdsAsync(ct)).ToHashSet();

        var postings = await db.JobPostings
            .AsNoTracking()
            .Where(p => sourceKeys.Contains(p.SourceKey))
            .Select(p => new { p.Id, p.Title, p.Description })
            .ToListAsync(ct);

        var items = new List<BatchExtractionItem>();
        var pending = new List<PendingBatchItem>();

        foreach (var posting in postings)
        {
            if (string.IsNullOrWhiteSpace(posting.Description) || inFlight.Contains(posting.Id))
            {
                continue;
            }

            items.Add(new BatchExtractionItem(
                posting.Id.ToString(CultureInfo.InvariantCulture),
                new ExtractionRequest(
                    DocumentKind.Posting, posting.Description, posting.Title, posting.Id)));

            pending.Add(new PendingBatchItem(posting.Id, Hash(posting.Description)));
        }

        if (items.Count == 0)
        {
            return new { submitted = 0, reason = "Everything eligible is already in an open batch." };
        }

        var submission = await batchExtractor!.SubmitAsync(items, ct);

        if (submission is null)
        {
            return new { submitted = 0, reason = "The provider did not accept the batch. Nothing was recorded." };
        }

        await batchRecord.RecordAsync(submission, pending, time.GetUtcNow(), ct);

        logger.LogInformation(
            "Backfill submitted batch {BatchId} with {Count} posting(s) on {Model}.",
            submission.ProviderBatchId, submission.Requested, submission.Model);

        return new
        {
            submitted = submission.Requested,
            batchId = submission.ProviderBatchId,
            model = submission.Model,
            limit,
            more = sourceKeys.Count == limit,
            collectedWithin = "24h",
        };
    }

    private static string Hash(string text)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
