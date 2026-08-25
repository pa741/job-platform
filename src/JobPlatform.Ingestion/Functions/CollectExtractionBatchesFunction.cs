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
    TimeProvider time,
    ILogger<CollectExtractionBatchesFunction> logger,
    IBatchDocumentExtractor? extractor = null)
{
    /// <summary>How many open batches one tick will poll. Small; there is rarely more than one.</summary>
    private const int MaxBatchesPerTick = 5;

    [Function(nameof(CollectExtractionBatchesFunction))]
    public async Task RunAsync(
        [TimerTrigger("0 20 * * * *")] TimerInfo timer,
        CancellationToken ct)
        => await CollectAsync(ct);

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
        => new OkObjectResult(await CollectAsync(ct));

    private async Task<object> CollectAsync(CancellationToken ct)
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

        foreach (var providerBatchId in open)
        {
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

            applied += await ApplyAsync(providerBatchId, outcome, ct);
        }

        logger.LogInformation(
            "Batch collection: {Applied} extraction(s) applied, {Running} batch(es) still open.",
            applied, stillRunning);

        return new { collected = applied, stillRunning };
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
    private async Task<int> ApplyAsync(string providerBatchId, BatchOutcome outcome, CancellationToken ct)
    {
        var record = await batches.GetItemsAsync(providerBatchId, ct);

        if (record is null)
        {
            logger.LogWarning("Collected batch {BatchId} has no local record; ignoring.", providerBatchId);
            return 0;
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
            return 0;
        }

        var conceptIds = await db.Concepts
            .Select(c => new { c.ConceptKey, c.Id })
            .ToDictionaryAsync(c => c.ConceptKey, c => c.Id, StringComparer.Ordinal, ct);

        var succeeded = 0;
        var failed = 0;

        foreach (var result in outcome.Results)
        {
            if (!items.TryGetValue(result.CorrelationId, out var item))
            {
                logger.LogWarning(
                    "Batch {BatchId} returned an id that was never submitted; dropping it.", providerBatchId);
                continue;
            }

            if (result.Extraction is not { } extraction)
            {
                failed++;
                continue;
            }

            await ApplyOneAsync(item, extraction, conceptIds, now, ct);
            succeeded++;
        }

        await db.SaveChangesAsync(ct);
        await batches.CompleteAsync(batchId, outcome.State, succeeded, failed, null, now, ct);

        logger.LogInformation(
            "Batch {BatchId}: {Succeeded} extracted, {Failed} failed, of {Requested} submitted.",
            providerBatchId, succeeded, failed, items.Count);

        return succeeded;
    }

    /// <summary>
    /// Records one extraction and replaces that posting's model-sourced assertions.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <c>EnrichPostingFunction.ApplyAsync</c>: only the
    /// <see cref="AssertionSource.Model"/> rows are replaced, because the board-supplied and
    /// text-matched assertions are different evidence from a different pass and this one has no
    /// business overwriting them. The hash written is the one captured at submission, so the
    /// idempotency key describes the text that was actually read.
    /// </remarks>
    private async Task ApplyOneAsync(
        PendingBatchItem item,
        DocumentExtraction extraction,
        Dictionary<string, int> conceptIds,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await db.PostingConcepts
            .Where(c => c.PostingId == item.PostingId && c.Source == AssertionSource.Model)
            .ExecuteDeleteAsync(ct);

        await db.PostingMentions
            .Where(m => m.PostingId == item.PostingId && m.Reason == MentionReason.UnknownModelSkill)
            .ExecuteDeleteAsync(ct);

        db.PostingExtractions.Add(new PostingExtractionEntity
        {
            PostingId = item.PostingId,
            ExtractorVersion = extraction.Version,
            InputHash = item.InputHash,
            Model = extraction.Model,
            ExtractedAtUtc = now,
            PayloadJson = extraction.PayloadJson,
        });

        foreach (var assertion in extraction.Concepts)
        {
            if (!conceptIds.TryGetValue(assertion.ConceptKey, out var conceptId))
            {
                continue;
            }

            db.PostingConcepts.Add(new PostingConceptEntity
            {
                PostingId = item.PostingId,
                ConceptId = conceptId,
                Source = AssertionSource.Model,
                Polarity = assertion.Polarity,
                YearsMin = assertion.YearsMin,
                YearsMax = assertion.YearsMax,
                EvidenceText = assertion.EvidenceText,
                Confidence = assertion.Confidence,
                ResolverVersion = extraction.Version,
            });
        }

        foreach (var mention in extraction.Mentions.DistinctBy(m => m.SurfaceForm, StringComparer.OrdinalIgnoreCase))
        {
            db.PostingMentions.Add(new PostingMentionEntity
            {
                PostingId = item.PostingId,
                SurfaceForm = mention.SurfaceForm,
                Reason = mention.Reason,
                Occurrences = mention.Occurrences,
                ResolverVersion = extraction.Version,
            });
        }
    }
}
