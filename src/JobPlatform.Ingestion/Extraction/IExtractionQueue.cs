using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Extraction;

/// <summary>The queue name, shared by the producer and the trigger attribute.</summary>
public static class ExtractionQueue
{
    public const string Name = "posting-extraction";
}

/// <summary>
/// Hands postings to the model pass without waiting for it.
/// </summary>
/// <remarks>
/// The ingest is one short Event Grid-triggered execution that <b>throws to force
/// redelivery</b>. Per-posting model calls inside it would therefore replay in full on every
/// retry — expensive, slow, and enough to push the execution past its timeout on a large blob.
/// A queue decouples the two: the ingest stays a single pass over the CSV, and extraction
/// proceeds at whatever rate the provider and the concurrency cap allow.
///
/// Registered <b>only</b> when an <c>IDocumentExtractor</c> is, so a deployment with no
/// provider configured writes nothing to the queue rather than accumulating work for a
/// consumer that will never run.
/// </remarks>
public interface IExtractionQueue
{
    Task EnqueueAsync(IReadOnlyCollection<string> sourceKeys, CancellationToken ct = default);
}

/// <summary>Azure Storage Queues, on the same identity-based connection as everything else.</summary>
public sealed class StorageExtractionQueue(
    QueueClient queue,
    ILogger<StorageExtractionQueue> logger) : IExtractionQueue
{
    /// <summary>
    /// How many source keys travel in one message.
    /// </summary>
    /// <remarks>
    /// One message per posting would mean 500 enqueues for a typical blob, each its own round
    /// trip, inside the execution the queue exists to keep short. Batching trades a little
    /// granularity on retry for a fixed, small number of writes: a failed batch re-extracts a
    /// handful of postings that were already done, and the input hash makes that a no-op.
    /// </remarks>
    private const int BatchSize = 20;

    public async Task EnqueueAsync(IReadOnlyCollection<string> sourceKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceKeys);

        if (sourceKeys.Count == 0)
        {
            return;
        }

        await queue.CreateIfNotExistsAsync(cancellationToken: ct);

        var sent = 0;

        foreach (var batch in sourceKeys.Chunk(BatchSize))
        {
            // Base64 because the queue's default encoding for the Functions trigger is
            // Base64, and a mismatch is silent: messages land and are never picked up.
            var json = JsonSerializer.Serialize(new ExtractionBatch(batch));
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

            await queue.SendMessageAsync(encoded, cancellationToken: ct);
            sent++;
        }

        logger.LogInformation(
            "Queued {Postings} posting(s) for extraction in {Messages} message(s).",
            sourceKeys.Count, sent);
    }
}

/// <summary>One queue message: the postings a single consumer invocation should extract.</summary>
public sealed record ExtractionBatch(IReadOnlyList<string> SourceKeys);
