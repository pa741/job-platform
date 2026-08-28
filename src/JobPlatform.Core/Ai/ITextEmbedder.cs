namespace JobPlatform.Core.Ai;

/// <summary>
/// Turns text into a vector, in batches, losing individual items rather than whole calls.
/// </summary>
/// <remarks>
/// Resolved as nullable everywhere it is used, like <c>IDocumentExtractor</c> and
/// <c>ICandidacyAssessor</c>: a deployment with no AI provider still scores and still ranks,
/// just without the axis this feeds. That is the same genuinely-useful degraded mode the rest of
/// the AI layer has, not a token one.
///
/// <b>The result is positional and nullable per item, which is deliberate.</b> The embeddings
/// endpoint answers in request order and that is the only correlation available - there is no
/// per-item id to echo back, unlike the batch extraction path. So the contract is the same one
/// <c>KernelDocumentExtractor.Distribute</c> enforces: a response whose length does not match
/// the request is not silently aligned, it is refused. A null in the returned list means that
/// text has no vector, and the caller writes nothing for it rather than writing something wrong.
/// </remarks>
public interface ITextEmbedder
{
    /// <summary>The deployment that answers, for the ledger and for the stored provenance.</summary>
    string Deployment { get; }

    /// <summary>
    /// Vectors for <paramref name="texts"/>, one per input and in the same order.
    /// </summary>
    /// <remarks>
    /// Never throws for a provider failure - it returns a list of nulls the same length as the
    /// request. The ledger is where the failure becomes visible; the caller's job is to leave
    /// those rows for the next pass.
    /// </remarks>
    Task<IReadOnlyList<float[]?>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default);
}
