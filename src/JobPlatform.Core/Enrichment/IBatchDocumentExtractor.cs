namespace JobPlatform.Core.Enrichment;

/// <summary>One document in a submitted batch.</summary>
/// <param name="CorrelationId">
/// What the provider echoes back beside the answer. The caller chooses it and is responsible
/// for being able to resolve it afterwards - see <see cref="IBatchDocumentExtractor"/> on why
/// this is not the posting's identity by accident.
/// </param>
public sealed record BatchExtractionItem(string CorrelationId, ExtractionRequest Request);

/// <summary>A batch the provider has accepted.</summary>
/// <param name="ProviderBatchId">The handle to poll with. Stored, because it outlives the process.</param>
/// <param name="Requested">How many documents went in.</param>
/// <param name="Model">The model the batch will run on, so a change is visible in the data.</param>
public sealed record BatchSubmission(string ProviderBatchId, int Requested, string Model);

/// <summary>Where a submitted batch has got to.</summary>
/// <remarks>
/// Deliberately coarser than any one provider's status vocabulary. A caller needs to know
/// whether to wait, to apply results, or to give up; the difference between "validating" and
/// "in progress" is the provider's business.
/// </remarks>
public enum BatchState
{
    /// <summary>Accepted and not finished. Poll again later.</summary>
    Running = 0,

    /// <summary>Finished. Results are present, though individual items may still have failed.</summary>
    Completed = 1,

    /// <summary>The batch as a whole failed. Nothing usable came back.</summary>
    Failed = 2,

    /// <summary>
    /// The provider gave up before finishing.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> because it is not an error to investigate: a batch
    /// that outran its completion window is a batch that was too big or submitted at a bad
    /// time, and the response is to resubmit rather than to debug.
    /// </remarks>
    Expired = 3,

    Cancelled = 4,
}

/// <param name="CorrelationId">Echoed back from the submission.</param>
/// <param name="Extraction">Null where this item failed; <paramref name="Error"/> then says why.</param>
public sealed record BatchResult(string CorrelationId, DocumentExtraction? Extraction, string? Error);

/// <param name="Results">One per item the provider answered. Order is not the submission order.</param>
public sealed record BatchOutcome(
    BatchState State,
    IReadOnlyList<BatchResult> Results,
    string? Error = null);

/// <summary>
/// Extraction as an asynchronous job rather than a call.
/// </summary>
/// <remarks>
/// <b>A separate contract from <see cref="IDocumentExtractor"/>, because it is a different
/// shape and not merely a different provider.</b> That one is request/response and answers in
/// seconds; this submits work that completes within twenty-four hours, so the submission and
/// the collection are different processes with a database row between them. Folding the two
/// together would mean an interface where half the methods throw for half the implementations.
///
/// The two coexist deliberately, and the split is along the data rather than along preference:
/// job adverts are public text, arrive in tens of thousands, and nobody is waiting for them, so
/// they go through here. A candidate profile is somebody's employment history and is submitted
/// by a person watching a spinner, so it stays on the synchronous path - which also keeps
/// personal data inside the tenant the rest of the system runs in.
///
/// <b>Correlation is the provider's job here, not ours.</b> The synchronous batching in
/// <c>KernelDocumentExtractor</c> packs many documents into one prompt and has to police the
/// indices coming back, because an answer attached to the wrong posting would be wrong,
/// self-consistent and undetectable. A batch API gives every request its own id and echoes it,
/// so that whole class of failure belongs to the platform. It is why this path sends one
/// document per request and does not pack: packing would reintroduce exactly the risk the
/// design just got rid of, to save an amount of money too small to name.
/// </remarks>
public interface IBatchDocumentExtractor
{
    /// <summary>
    /// Hands a batch to the provider. Null when it would not accept one.
    /// </summary>
    /// <remarks>
    /// Never throws for a provider failure, the same contract the synchronous extractor
    /// carries: a submission that does not happen leaves the postings unextracted and the next
    /// backfill picks them up, where an exception would fail an admin endpoint that has a
    /// perfectly good answer to give.
    /// </remarks>
    Task<BatchSubmission?> SubmitAsync(
        IReadOnlyList<BatchExtractionItem> items, CancellationToken ct = default);

    /// <summary>Asks where a batch has got to, and brings back its results once it is done.</summary>
    Task<BatchOutcome?> CollectAsync(string providerBatchId, CancellationToken ct = default);
}
