using System.ClientModel;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Ai.Matching;

/// <summary>
/// Embeds text with the Azure OpenAI embeddings deployment, and reports what the call cost.
/// </summary>
/// <remarks>
/// <b>The one AI call site here that is not a prompt.</b> Everything else in this layer goes
/// through the Kernel because it is invoking a prompt against a chat deployment; an embedding is
/// not a prompt and has no completion, so it goes through <see cref="IEmbeddingGenerator{T, U}"/>
/// - the same Microsoft.Extensions.AI abstraction Semantic Kernel itself sits on. Nothing new
/// enters the dependency graph for it.
///
/// <b>The vectors are truncated and unit-normalised before they leave.</b> The deployment is
/// asked for <see cref="EmbeddingVector.Dimensions"/> rather than the model's full width, which
/// Matryoshka representation learning makes a real embedding rather than a lossy prefix, and the
/// result is normalised so similarity downstream is a dot product. Both belong here rather than
/// at the call sites: there are two of them and they would eventually disagree.
///
/// <b>It does not throw for a provider failure.</b> Same contract as the extractor and the
/// assessor - the pass that called it writes nothing for those rows and the next one picks them
/// up. The ledger is what makes that visible rather than silent, which is the whole point of
/// HANDOFF 1.1.
///
/// <b>It retries, and that is not belt-and-braces.</b> A freshly created deployment on this
/// resource answers <c>404 DeploymentNotFound</c> from some backends and 200 from others for a
/// while after provisioning - measured at roughly one call in three failing, randomly, for the
/// same URL and the same api-version. Without a retry the corpus simply never gets embedded:
/// the pass stops on its first empty batch by design, so a third of a chance of failure is a
/// near-certainty of stopping within a few batches. The other transient here is the rate limit,
/// which the extraction backfill has already been lost to once.
/// </remarks>
public sealed class KernelTextEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<AzureOpenAiOptions> options,
    ILogger<KernelTextEmbedder>? logger = null,
    IAiCallLog? callLog = null,
    TimeProvider? time = null) : ITextEmbedder
{
    /// <summary>How this pass is named in the ledger.</summary>
    public const string LedgerOperation = "text-embedding";

    // Nullable with a fallback, like the assessor and the extractor. AddAiProvider registers this
    // from configuration alone, so requiring a clock from the container would make the whole AI
    // layer fail to resolve in any host that had not thought to register one - which is a startup
    // failure a long way from its cause.
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly AzureOpenAiOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Deployment => _options.EmbeddingDeployment;

    public async Task<IReadOnlyList<float[]?>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        var started = _time.GetTimestamp();
        var results = new float[]?[texts.Count];
        string? reason = null;
        var usage = default(AiTokenUsage);

        try
        {
            var generated = await WithRetryAsync(texts, ct);

            usage = Usage(generated);

            // The embeddings endpoint answers in request order and offers no per-item id, so
            // position is the only correlation there is. A response of the wrong length is
            // therefore uncorrelatable, not merely short: aligning the first n would silently
            // file one posting's vector against another, which is wrong, self-consistent and
            // undetectable afterwards - the exact failure Distribute exists to refuse.
            if (generated.Count != texts.Count)
            {
                reason = $"Asked for {texts.Count} embeddings and the deployment returned "
                    + $"{generated.Count}; the batch cannot be correlated by position.";
            }
            else
            {
                for (var i = 0; i < texts.Count; i++)
                {
                    results[i] = EmbeddingVector.Normalise(generated[i].Vector.ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed on purpose, and recorded on the way past. A rate limit or a transient
            // 404 from a freshly created deployment must leave the pass able to continue with
            // the batches after this one; what it must not do is leave nothing saying it
            // happened.
            reason = $"{ex.GetType().Name}: {ex.Message}";
            logger?.LogWarning(ex, "Embedding batch of {Count} failed.", texts.Count);
        }

        await RecordAsync(texts.Count, results, reason, usage, started, ct);

        return results;
    }

    /// <summary>How many times one batch is attempted before it is somebody else's problem.</summary>
    /// <remarks>
    /// Four, which at the measured failure rate leaves under one batch in a hundred unembedded -
    /// and those come back on the next pass anyway, because the "needs embedding" query is
    /// derived from what is stored rather than from what was tried.
    /// </remarks>
    private const int MaxAttempts = 4;

    /// <summary>
    /// One batch, retried through the transient failures this provider actually produces.
    /// </summary>
    /// <remarks>
    /// Only the statuses worth retrying, and each is on the list because it was observed rather
    /// than imagined. <b>404</b> is the strange one and the reason this method exists: a newly
    /// provisioned deployment is visible to the control plane and to only some data-plane
    /// backends, so the same request 404s and then succeeds seconds later. It is safe to retry
    /// precisely because a genuinely absent deployment fails every attempt and still ends up in
    /// the ledger. <b>429</b> is the rate limit. <b>5xx</b> and <b>408</b> are the ordinary
    /// transport failures.
    ///
    /// Everything else - a 400 for a malformed request, a 401 for a missing role assignment -
    /// is thrown straight out to the caller's handler, because retrying a request that is wrong
    /// only makes it wrong four times.
    /// </remarks>
    private async Task<GeneratedEmbeddings<Embedding<float>>> WithRetryAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await generator.GenerateAsync(texts, options: null, ct);
            }
            catch (ClientResultException ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                var delay = RetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));

                logger?.LogInformation(
                    "Embedding batch of {Count} got HTTP {Status} on attempt {Attempt}; "
                    + "retrying in {Delay}.", texts.Count, ex.Status, attempt, delay);

                await Task.Delay(delay, _time, ct);
            }
        }
    }

    private static bool IsTransient(ClientResultException ex)
        => ex.Status is 404 or 408 or 429 or >= 500 and < 600;

    /// <summary>
    /// What the provider asked us to wait, where it said so.
    /// </summary>
    /// <remarks>
    /// Preferred over the backoff whenever it is present: a rate limiter that names a window
    /// knows when its window opens, and guessing shorter is how a retry storm starts. Clamped
    /// so a header cannot park a nightly pass for an hour.
    /// </remarks>
    private static TimeSpan? RetryAfter(ClientResultException ex)
    {
        if (ex.GetRawResponse() is not { } response
            || !response.Headers.TryGetValue("retry-after", out var value)
            || !int.TryParse(value, out var seconds)
            || seconds <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Min(seconds, 60));
    }

    private async Task RecordAsync(
        int requested,
        float[]?[] results,
        string? reason,
        AiTokenUsage usage,
        long started,
        CancellationToken ct)
    {
        if (callLog is null)
        {
            return;
        }

        var returned = results.Count(r => r is not null);

        var outcome = returned == requested
            ? AiCallOutcome.Succeeded
            : returned == 0 ? AiCallOutcome.Failed : AiCallOutcome.PartiallyDiscarded;

        // Guarded even though IAiCallLog says implementations must not throw, for the reason
        // KernelCandidacyAssessor gives: the cost of that comment being wrong is losing the work
        // the call just paid for.
        try
        {
            await callLog.RecordAsync(
                AiCallRecord.Create(
                    _time.GetUtcNow(),
                    LedgerOperation,
                    _options.EmbeddingDeployment,
                    outcome,
                    requested,
                    returned,
                    (long)_time.GetElapsedTime(started).TotalMilliseconds,
                    reason,
                    // No affected ids and no prompt. The caller holds the posting ids - this
                    // type is handed strings and deliberately knows nothing about what they
                    // are - and the text is the candidate's own profile on one of the two call
                    // sites, which is exactly what the ledger's prompt rules exist to keep out.
                    affectedIds: null,
                    prompt: null,
                    usage),
                ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not record the embedding call to the AI ledger.");
        }
    }

    /// <summary>
    /// What the batch cost. Input tokens only - an embedding produces no completion.
    /// </summary>
    /// <remarks>
    /// Read defensively for the reason <c>AiUsage</c> gives: usage is a provider courtesy rather
    /// than a contract, and a ledger that loses its token counts is a great deal better than a
    /// pass that dies because the shape moved.
    /// </remarks>
    private static AiTokenUsage Usage(GeneratedEmbeddings<Embedding<float>> generated)
    {
        if (generated.Usage is not { } usage)
        {
            return default;
        }

        var input = (int)Math.Clamp(usage.InputTokenCount ?? 0, 0, int.MaxValue);
        var total = (int)Math.Clamp(usage.TotalTokenCount ?? input, 0, int.MaxValue);

        return new AiTokenUsage(input, OutputTokens: 0, ReasoningTokens: 0, total);
    }
}
