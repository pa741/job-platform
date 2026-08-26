using System.Globalization;
using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// The other half of batch extraction: collect what the provider has finished.
/// </summary>
/// <remarks>
/// <b>A batch is submitted in one execution and answered in another, up to a day later.</b> That
/// is the entire reason this function exists as its own trigger rather than as a continuation of
/// the backfill - there is nothing to continue, the process that submitted is long gone, and the
/// only thing joining the two is a row in <c>ExtractionBatches</c>.
///
/// Hourly rather than more often. The provider's window is twenty-four hours and most batches
/// land well inside it, so polling every few minutes would spend requests to learn nothing; an
/// hour is frequent enough that a completed batch is applied the same working day and rare
/// enough to be free.
///
/// Inert without a batch extractor configured, like every other AI-shaped path here: the
/// dependency resolves to null, the tick logs nothing interesting and returns.
/// </remarks>
public sealed class CollectExtractionBatchesFunction(
    JobsDbContext db,
    ExtractionBatchRepository batches,
    PostingExtractionWriter writer,
    TimeProvider time,
    ILogger<CollectExtractionBatchesFunction> logger,
    IBatchDocumentExtractor? extractor = null)
{
    /// <summary>How many open batches one tick will poll. Small; there is rarely more than one.</summary>
    private const int MaxBatchesPerTick = 5;

    /// <summary>
    /// How many results one timer invocation writes back.
    /// </summary>
    /// <remarks>
    /// Generous, because the timer has minutes rather than seconds. A batch bigger than this
    /// is not lost - it stays open and the next tick continues where this one stopped.
    /// </remarks>
    private const int TimerApplyLimit = 5_000;

    /// <summary>
    /// How many results one HTTP invocation writes back.
    /// </summary>
    /// <remarks>
    /// Small, because the platform gives an HTTP trigger roughly 230 seconds and a
    /// corpus-sized collection takes longer than that. Three attempts at 2,459 results
    /// returned 504 before one got through, and the work survived only because the writer is
    /// idempotent - which is luck to lean on rather than design. This route is a nudge for
    /// something the timer would do anyway, so it does a bounded amount and says what is left.
    /// </remarks>
    private const int HttpApplyLimit = 400;

    [Function(nameof(CollectExtractionBatchesFunction))]
    public async Task RunAsync(
        [TimerTrigger("0 20 * * * *")] TimerInfo timer,
        CancellationToken ct)
        => await CollectAsync(TimerApplyLimit, ct);

    /// <summary>
    /// The same collection, on demand.
    /// </summary>
    /// <remarks>
    /// For the case the timer cannot serve: a batch that completed two minutes after the last
    /// tick, and somebody who would rather not wait an hour to see it. An admin route behind a
    /// function key, like the rest of them, and no <c>admin/</c> prefix because the host
    /// reserves it.
    /// </remarks>
    [Function(nameof(RunCollectExtractionBatchesFunction))]
    public async Task<IActionResult> RunCollectExtractionBatchesFunction(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "collect-extraction-batches")]
        HttpRequest request,
        CancellationToken ct)
        => new OkObjectResult(await CollectAsync(HttpApplyLimit, ct));

    private async Task<object> CollectAsync(int applyLimit, CancellationToken ct)
    {
        if (extractor is null)
        {
            return new { collected = 0, reason = "No batch extraction provider is configured." };
        }

        var open = await batches.GetOpenAsync(MaxBatchesPerTick, ct);

        if (open.Count == 0)
        {
            return new { collected = 0, reason = "No batches are open." };
        }

        var applied = 0;
        var stillRunning = 0;
        var remaining = 0;

        foreach (var providerBatchId in open)
        {
            if (applied >= applyLimit)
            {
                // This invocation has spent its budget. The batch stays open, so the next
                // one picks it up rather than this one running past its deadline.
                stillRunning++;
                continue;
            }

            var outcome = await extractor.CollectAsync(providerBatchId, ct);

            if (outcome is null)
            {
                // Provider unreachable, or an unusable response. The batch stays open and the
                // next tick tries again - which is right, because the results are still sitting
                // on the provider and nothing has been lost.
                logger.LogWarning("Could not collect batch {BatchId}; leaving it open.", providerBatchId);
                continue;
            }

            if (outcome.State == BatchState.Running)
            {
                stillRunning++;
                continue;
            }

            var (written, left) = await ApplyAsync(providerBatchId, outcome, applyLimit - applied, ct);

            applied += written;
            remaining += left;

            if (left > 0)
            {
                stillRunning++;
            }
        }

        logger.LogInformation(
            "Batch collection: {Applied} applied, {Remaining} left to write, "
            + "{Running} batch(es) still open.",
            applied, remaining, stillRunning);

        return new { collected = applied, stillRunning, remaining };
    }

    /// <summary>
    /// Writes one finished batch's results back against the postings that produced them.
    /// </summary>
    /// <remarks>
    /// The correlation id is resolved against what was <i>submitted</i>, not against what the
    /// posting says now. A result whose id is not in that set is dropped rather than guessed at -
    /// the same rule the synchronous packer follows for an out-of-range index, and for the same
    /// reason: an extraction applied to the wrong posting is wrong, self-consistent and
    /// undetectable afterwards.
    /// </remarks>
    private async Task<(int Written, int Remaining)> ApplyAsync(
        string providerBatchId, BatchOutcome outcome, int applyLimit, CancellationToken ct)
    {
        var record = await batches.GetItemsAsync(providerBatchId, ct);

        if (record is null)
        {
            logger.LogWarning("Collected batch {BatchId} has no local record; ignoring.", providerBatchId);
            return (0, 0);
        }

        var (batchId, items) = record.Value;
        var now = time.GetUtcNow();

        if (outcome.State != BatchState.Completed)
        {
            // Expired, failed or cancelled. The postings simply keep no extraction row and the
            // next backfill picks them up, so there is nothing to repair - only to record.
            logger.LogWarning(
                "Batch {BatchId} finished as {State}: {Error}",
                providerBatchId, outcome.State, outcome.Error);

            await batches.CompleteAsync(batchId, outcome.State, 0, items.Count, outcome.Error, now, ct);
            return (0, 0);
        }

        // Only what is not already durable. A previous invocation may have been cut off part
        // way through - by the gateway, or by its own budget - and rewriting what it managed
        // would spend the whole allowance redoing settled work.
        var pending = await batches.GetUnappliedAsync(batchId, DocumentExtraction.CurrentVersion, ct);

        var conceptIds = await writer.GetConceptIdsAsync(ct);

        var succeeded = 0;
        var failed = 0;
        var deferred = 0;

        foreach (var result in outcome.Results)
        {
            if (!items.ContainsKey(result.CorrelationId))
            {
                logger.LogWarning(
                    "Batch {BatchId} returned an id that was never submitted; dropping it.", providerBatchId);
                continue;
            }

            // Written by an earlier invocation, so nothing to do.
            if (!pending.TryGetValue(result.CorrelationId, out var item))
            {
                continue;
            }

            if (result.Extraction is not { } extraction)
            {
                failed++;
                continue;
            }

            if (succeeded >= applyLimit)
            {
                deferred++;
                continue;
            }

            await writer.ApplyAsync(item.PostingId, item.InputHash, extraction, conceptIds, now, ct);
            succeeded++;
        }

        await db.SaveChangesAsync(ct);

        if (deferred > 0)
        {
            // Deliberately not closed out. The batch stays Running so the next invocation
            // resumes it, and the provider keeps the results available meanwhile.
            logger.LogInformation(
                "Batch {BatchId}: wrote {Succeeded}, {Deferred} left for the next pass.",
                providerBatchId, succeeded, deferred);

            return (succeeded, deferred);
        }

        await batches.CompleteAsync(batchId, outcome.State, succeeded, failed, null, now, ct);

        logger.LogInformation(
            "Batch {BatchId}: {Succeeded} extracted, {Failed} failed, of {Requested} submitted.",
            providerBatchId, succeeded, failed, items.Count);

        return (succeeded, 0);
    }
}
