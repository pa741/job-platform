using JobPlatform.Core.Enrichment;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One batch handed to the provider, and what became of it.
/// </summary>
/// <remarks>
/// <b>This table exists because a batch outlives the process that submitted it.</b> Everything
/// else in this pipeline completes inside one invocation: a blob is ingested, a queue message is
/// extracted, a sweep scores and finishes. A batch is accepted now and answered within
/// twenty-four hours, so the submission and the collection are different executions on different
/// days, and the only thing joining them is a row.
///
/// It is also the audit trail for money. Each row says how many documents went out, how many
/// came back, and on which model - which is the difference between "extraction is cheap" as an
/// assertion and as a measurement.
/// </remarks>
public sealed class ExtractionBatchEntity
{
    public long Id { get; set; }

    /// <summary>
    /// The provider's own id, which is what gets polled.
    /// </summary>
    /// <remarks>
    /// Unique, so a collector that runs twice concurrently cannot create a second row for one
    /// batch and apply its results twice.
    /// </remarks>
    public required string ProviderBatchId { get; set; }

    /// <summary>The model every request in this batch ran on. A batch is single-model by rule.</summary>
    public string? Model { get; set; }

    public BatchState State { get; set; }

    /// <summary>How many documents went out.</summary>
    public int Requested { get; set; }

    /// <summary>How many came back with a usable extraction.</summary>
    public int Succeeded { get; set; }

    /// <summary>
    /// How many the provider answered with an error, or answered unusably.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Requested"/> minus <see cref="Succeeded"/>, because those are
    /// not the same number: an expired batch returns nothing for most of its items, and "the
    /// provider said no" is a different fact from "the provider never got to it".
    /// </remarks>
    public int Failed { get; set; }

    public DateTimeOffset SubmittedAtUtc { get; set; }

    /// <summary>Null while the batch is still open. The only reliable way to ask.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>What the provider said went wrong, where the batch as a whole failed.</summary>
    public string? Error { get; set; }

    public ICollection<ExtractionBatchItemEntity> Items { get; } = [];
}

/// <summary>
/// One document inside a submitted batch.
/// </summary>
/// <remarks>
/// <b>The input hash is the reason this table exists rather than a list of posting ids.</b> A
/// batch is answered up to a day later, and in that time the scraper may have re-listed the
/// posting with an edited description. The extraction row written on collection has to be keyed
/// on the text that was actually sent, not on whatever the posting says by then - otherwise the
/// idempotency key lies, and the next backfill either re-extracts something it need not or skips
/// something it should redo.
///
/// The correlation id handed to the provider is the posting id alone, which keeps it short and
/// obviously unique within a batch. Everything else needed to write the result back is here.
/// </remarks>
public sealed class ExtractionBatchItemEntity
{
    public long BatchId { get; set; }
    public ExtractionBatchEntity? Batch { get; set; }

    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    /// <summary>Hash of the text that was sent, captured at submission time.</summary>
    public required string InputHash { get; set; }
}
