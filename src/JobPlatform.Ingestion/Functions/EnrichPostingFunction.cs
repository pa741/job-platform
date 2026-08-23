using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using JobPlatform.Ingestion.Extraction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// The model pass, one batch of postings at a time.
/// </summary>
/// <remarks>
/// Separate from the ingest because the ingest throws to force Event Grid redelivery, so
/// anything expensive inside it replays in full on every retry. Here a failure costs one
/// batch, and the input hash makes a replayed batch converge rather than duplicate.
///
/// The whole function is inert without an AI provider: <c>IDocumentExtractor?</c> resolves to
/// null, the message is acknowledged, and nothing happens. That is not a degraded mode — it is
/// the configuration this system ships in, and the queue is empty in it anyway because the
/// producer is registered under the same condition.
/// </remarks>
public sealed class EnrichPostingFunction(
    JobsDbContext db,
    ILogger<EnrichPostingFunction> logger,
    IDocumentExtractor? extractor = null)
{
    [Function(nameof(EnrichPostingFunction))]
    public async Task RunAsync(
        [QueueTrigger(ExtractionQueue.Name, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken ct)
    {
        if (extractor is null)
        {
            // Acknowledge and drop. Re-queueing would loop forever against a provider that is
            // not configured, and the poison queue would fill with work nobody asked for.
            logger.LogInformation("No extractor is registered; discarding an extraction batch.");
            return;
        }

        var batch = Deserialize(message);

        if (batch is null || batch.SourceKeys.Count == 0)
        {
            return;
        }

        var keys = batch.SourceKeys.ToList();

        var postings = await db.JobPostings
            .Where(p => keys.Contains(p.SourceKey))
            .Select(p => new { p.Id, p.SourceKey, p.Title, p.Description })
            .ToListAsync(ct);

        var conceptIds = await db.Concepts
            .Select(c => new { c.ConceptKey, c.Id })
            .ToDictionaryAsync(c => c.ConceptKey, c => c.Id, StringComparer.Ordinal, ct);

        var extracted = 0;
        var skipped = 0;

        foreach (var posting in postings)
        {
            if (string.IsNullOrWhiteSpace(posting.Description))
            {
                continue;
            }

            var inputHash = Hash(posting.Description);

            // The idempotency key. A replayed message, or a posting re-listed with unchanged
            // text, converges on the row that is already there instead of paying for the
            // model again - the same contract ScrapeRuns.BlobPath carries for ingestion.
            var alreadyDone = await db.PostingExtractions.AnyAsync(
                e => e.PostingId == posting.Id
                    && e.ExtractorVersion == DocumentExtraction.CurrentVersion
                    && e.InputHash == inputHash,
                ct);

            if (alreadyDone)
            {
                skipped++;
                continue;
            }

            var result = await extractor.ExtractAsync(
                new ExtractionRequest(DocumentKind.Posting, posting.Description, posting.Title), ct);

            if (result is null)
            {
                continue;
            }

            await ApplyAsync(posting.Id, inputHash, result, conceptIds, ct);
            extracted++;
        }

        if (extracted > 0 || skipped > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Extraction batch: {Extracted} extracted, {Skipped} already current, {Missing} not found.",
            extracted, skipped, keys.Count - postings.Count);
    }

    /// <summary>
    /// Records the extraction and replaces this posting's model-sourced assertions.
    /// </summary>
    /// <remarks>
    /// Only the <see cref="AssertionSource.Model"/> rows are replaced. The board-supplied and
    /// text-matched assertions are different evidence produced by a different pass, and this
    /// one has no business overwriting them — that separation is the reason <c>Source</c> is
    /// part of the assertion's primary key.
    /// </remarks>
    private async Task ApplyAsync(
        long postingId,
        string inputHash,
        DocumentExtraction result,
        Dictionary<string, int> conceptIds,
        CancellationToken ct)
    {
        await db.PostingConcepts
            .Where(c => c.PostingId == postingId && c.Source == AssertionSource.Model)
            .ExecuteDeleteAsync(ct);

        await db.PostingMentions
            .Where(m => m.PostingId == postingId && m.Reason == MentionReason.UnknownModelSkill)
            .ExecuteDeleteAsync(ct);

        db.PostingExtractions.Add(new PostingExtractionEntity
        {
            PostingId = postingId,
            ExtractorVersion = result.Version,
            InputHash = inputHash,
            Model = result.Model,
            ExtractedAtUtc = DateTimeOffset.UtcNow,
            PayloadJson = result.PayloadJson,
        });

        foreach (var assertion in result.Concepts)
        {
            if (!conceptIds.TryGetValue(assertion.ConceptKey, out var conceptId))
            {
                continue;
            }

            db.PostingConcepts.Add(new PostingConceptEntity
            {
                PostingId = postingId,
                ConceptId = conceptId,
                Source = AssertionSource.Model,
                Polarity = assertion.Polarity,
                YearsMin = assertion.YearsMin,
                YearsMax = assertion.YearsMax,
                EvidenceText = assertion.EvidenceText,
                Confidence = assertion.Confidence,
                ResolverVersion = result.Version,
            });
        }

        foreach (var mention in result.Mentions.DistinctBy(m => m.SurfaceForm, StringComparer.OrdinalIgnoreCase))
        {
            db.PostingMentions.Add(new PostingMentionEntity
            {
                PostingId = postingId,
                SurfaceForm = mention.SurfaceForm,
                Reason = mention.Reason,
                Occurrences = mention.Occurrences,
                ResolverVersion = result.Version,
            });
        }
    }

    private ExtractionBatch? Deserialize(string message)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtractionBatch>(message, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Nothing about a malformed message improves on a retry, so it is dropped rather
            // than left to cycle into the poison queue.
            logger.LogWarning(ex, "Discarding a malformed extraction message.");
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string Hash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
