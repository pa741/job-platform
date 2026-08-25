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
/// The queue message carries a batch and the whole batch goes to the model in as few calls as
/// the extractor can manage. That is where the cost of this pipeline actually lives: the
/// concept vocabulary has to precede every extraction as the model's allowed output set, and
/// sent once per posting it dwarfs the adverts themselves. This function stays indifferent to
/// how the extractor packs them - it hands over a list and reads the answers back by position.
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

        // Everything already current, in one query. Asking per posting was a round trip each
        // to a database that auto-pauses and is billed by the second, on a path that runs for
        // every blob the scraper uploads.
        var postingIds = postings.Select(p => p.Id).ToList();

        var current = await db.PostingExtractions
            .Where(e => postingIds.Contains(e.PostingId)
                && e.ExtractorVersion == DocumentExtraction.CurrentVersion)
            .Select(e => new { e.PostingId, e.InputHash })
            .ToListAsync(ct);

        var currentHashes = current
            .GroupBy(e => e.PostingId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.InputHash).ToHashSet(StringComparer.Ordinal));

        var pending = new List<(long Id, string InputHash)>();
        var requests = new List<ExtractionRequest>();
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
            if (currentHashes.TryGetValue(posting.Id, out var hashes) && hashes.Contains(inputHash))
            {
                skipped++;
                continue;
            }

            pending.Add((posting.Id, inputHash));
            requests.Add(new ExtractionRequest(DocumentKind.Posting, posting.Description, posting.Title));
        }

        // One call covering many postings rather than one call each. The concept vocabulary is
        // several thousand tokens and has to precede every extraction, so sending it per
        // posting is most of what a corpus-wide pass costs; the extractor decides how many
        // documents fit in a call and this loop is indifferent to the answer.
        var results = requests.Count == 0
            ? []
            : await extractor.ExtractBatchAsync(requests, ct);

        var extracted = 0;

        for (var i = 0; i < pending.Count && i < results.Count; i++)
        {
            if (results[i] is not { } result)
            {
                continue;
            }

            await ApplyAsync(pending[i].Id, pending[i].InputHash, result, conceptIds, ct);
            extracted++;
        }

        if (extracted > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Extraction batch: {Extracted} extracted, {Skipped} already current, {Failed} returned nothing, "
            + "{Missing} not found.",
            extracted, skipped, pending.Count - extracted, keys.Count - postings.Count);
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
