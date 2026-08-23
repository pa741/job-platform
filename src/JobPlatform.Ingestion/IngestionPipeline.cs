using System.Diagnostics;
using JobPlatform.Core.Metrics;
using JobPlatform.Core.Parsing;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Sql;
using JobPlatform.Ingestion.Extraction;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion;

/// <summary>
/// The digest itself: CSV in, postings in SQL and metrics in Cosmos out.
/// Kept separate from the trigger so the blob trigger and the admin reprocess endpoint
/// run byte-for-byte the same path.
/// </summary>
public sealed class IngestionPipeline(
    JobCsvParser parser,
    MetricsCalculator calculator,
    JobPostingRepository postings,
    MetricsRepository metrics,
    ILogger<IngestionPipeline> logger,
    IExtractionQueue? extractionQueue = null)
{
    public async Task<RunDigest> ProcessAsync(
        Stream content,
        string blobPath,
        string? etag,
        long sizeBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);

        var stopwatch = Stopwatch.StartNew();

        var context = BlobNameParser.Parse(blobPath, DateTimeOffset.UtcNow, etag, sizeBytes);

        logger.LogInformation(
            "Ingesting {BlobPath} (search term {SearchTerm}, scraped {ScrapedAt:o}).",
            blobPath, context.SearchTerm, context.ScrapedAtUtc);

        var parsed = parser.Parse(content);

        var (run, outcome, needingExtraction) = await postings.IngestAsync(
            context, parsed.Postings, parsed.RowsInFile, parsed.InvalidRows, ct);

        // Null whenever no AI provider is configured, which is how this ships. Nothing is
        // written to the queue rather than accumulating work for a consumer that never runs.
        if (extractionQueue is not null)
        {
            await extractionQueue.EnqueueAsync(needingExtraction, ct);
        }

        var digest = calculator.Calculate(context, parsed, outcome, stopwatch.ElapsedMilliseconds);
        await metrics.UpsertRunDigestAsync(digest, ct);

        // Recomputed from SQL, so replaying a blob converges instead of double-counting.
        var rollup = await postings.BuildDailyRollupAsync(context.SearchTerm, context.ScrapeDate, ct);
        await metrics.UpsertDailyRollupAsync(rollup, ct);

        stopwatch.Stop();

        logger.LogInformation(
            "Ingested {BlobPath} as run {RunId} in {ElapsedMs}ms: {Parsed} posting(s), {New} new.",
            blobPath, run.Id, stopwatch.ElapsedMilliseconds, parsed.Postings.Count, outcome.New);

        return digest;
    }
}
