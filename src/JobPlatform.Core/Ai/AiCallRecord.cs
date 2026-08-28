using System.Text.Json.Serialization;

namespace JobPlatform.Core.Ai;

/// <summary>How a model call ended.</summary>
public enum AiCallOutcome
{
    /// <summary>Everything asked for came back usable.</summary>
    Succeeded = 0,

    /// <summary>The call answered, and some of the answer could not be used.</summary>
    PartiallyDiscarded = 1,

    /// <summary>Nothing usable came back: a throw, a timeout, or an unparseable body.</summary>
    Failed = 2,
}

/// <summary>
/// One model call, as it should be possible to read about afterwards.
/// </summary>
/// <remarks>
/// <b>Every AI path in this system degrades silently by design, and that design is right</b> - a
/// provider failure must not take down endpoints with nothing to do with AI, which is why
/// <c>IDocumentExtractor</c>, <c>ICandidacyAssessor</c> and <c>IApplicationWriter</c> are all
/// resolved as nullable and skipped rather than awaited. But "must not fail loudly" was built as
/// "must not be recorded at all", and those are different things. It has cost real work three
/// times: a sweep that discarded five batches of ten while reporting success, a backfill that
/// spent its calls on HTTP 429s and extracted almost nothing, and misaligned answers dropped for
/// a later pass nobody was watching. Every time the symptom was a count nobody was comparing to
/// anything.
///
/// <b><see cref="Requested"/> against <see cref="Returned"/> is the whole point.</b> A written
/// count on its own cannot show a loss - forty assessments looks identical whether forty or
/// ninety were paid for. Recording both is what makes the difference visible, and it is why this
/// type carries counts rather than a bare success flag.
///
/// <b>The prompt is kept only where it buys something, and only when asked for.</b> A failure
/// that cannot be reproduced is a failure somebody has to guess at, so <see cref="Prompt"/>
/// exists - but it carries the candidate's employment history, contact details and salary
/// expectations, so three rules hold it down and all three live in the sink rather than at the
/// call sites, where one of them would eventually be forgotten:
/// <list type="number">
/// <item>Off unless <c>AiLedger:RecordPrompts</c> is set. A clone stores none.</item>
/// <item>Kept only when the call lost something. A success has nothing to reproduce.</item>
/// <item>Never returned by the list endpoint, which <c>Api:AllowAnonymousReads</c> can open.
/// It has its own route behind the authenticated policy.</item>
/// </list>
/// </remarks>
public sealed record AiCallRecord
{
    /// <summary>Deterministic per call, so a retry of the write converges.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Discriminator, so this can share a container with other document types.</summary>
    [JsonPropertyName("type")]
    public string Type => "aiCall";

    /// <summary>
    /// The UTC date, and the partition key.
    /// </summary>
    /// <remarks>
    /// Every question worth asking of this data is "what happened recently", so a day partition
    /// answers it by reading one partition rather than fanning out. It also bounds partition
    /// growth without anybody having to think about it, which a per-operation key would not.
    /// </remarks>
    [JsonPropertyName("day")]
    public required string Day { get; init; }

    [JsonPropertyName("occurredAtUtc")]
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Which pass made the call - <c>candidacy-assessment</c>, <c>posting-extraction</c>.</summary>
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    /// <summary>
    /// Which deployment served it, <c>bulk</c> or <c>writing</c>.
    /// </summary>
    /// <remarks>
    /// Recorded because the two cost differently and a prompt naming the wrong one fails
    /// silently - Semantic Kernel falls back to the only service present rather than throwing.
    /// A ledger that shows CVs being written by the bulk deployment is how that gets noticed.
    /// </remarks>
    [JsonPropertyName("deployment")]
    public string? Deployment { get; init; }

    [JsonPropertyName("outcome")]
    [JsonConverter(typeof(JsonStringEnumConverter<AiCallOutcome>))]
    public required AiCallOutcome Outcome { get; init; }

    /// <summary>Items sent. This is what the call cost.</summary>
    [JsonPropertyName("requested")]
    public int Requested { get; init; }

    /// <summary>Items that came back usable.</summary>
    [JsonPropertyName("returned")]
    public int Returned { get; init; }

    /// <summary>Paid for and thrown away.</summary>
    [JsonPropertyName("discarded")]
    public int Discarded => Math.Max(0, Requested - Returned);

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    /// <summary>
    /// What the call actually cost, in tokens.
    /// </summary>
    /// <remarks>
    /// Recorded because duration is not cost. A batch of ten adverts and a batch of one differ
    /// by an order of magnitude in tokens and barely at all in wall clock, so a ledger with only
    /// a duration cannot answer "what did that night cost" - which is the question anybody asking
    /// about a raised assessment ceiling actually has.
    ///
    /// Zero where the provider did not report usage. Absent and free are different things, and
    /// nothing here should be read as the latter.
    /// </remarks>
    [JsonPropertyName("inputTokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public int OutputTokens { get; init; }

    /// <summary>
    /// Of <see cref="OutputTokens"/>, how many the model spent thinking.
    /// </summary>
    /// <remarks>
    /// Split out because <c>ReasoningEffort</c> is a deliberate cost lever here - low for
    /// extraction, medium for assessment, and never none - and this is the only number that
    /// shows what raising it buys or costs. On a reasoning model it is routinely the majority of
    /// the output.
    /// </remarks>
    [JsonPropertyName("reasoningTokens")]
    public int ReasoningTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; init; }

    /// <summary>
    /// Why, in a few words, when something went wrong. Never a payload.
    /// </summary>
    /// <remarks>
    /// Bounded at <see cref="MaxReasonChars"/> by <see cref="Create"/> rather than by whoever
    /// calls it. A model's own error text is the most likely thing to land here and it is not
    /// something to trust the length of.
    /// </remarks>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// What the call was about, so a failure names what it lost.
    /// </summary>
    /// <remarks>
    /// Posting ids, and only ever ids. "One call failed" is not something anybody can act on;
    /// "these ten postings went unassessed and the next sweep will retry them" is.
    /// </remarks>
    [JsonPropertyName("affectedIds")]
    public IReadOnlyList<long> AffectedIds { get; init; } = [];

    /// <summary>
    /// The exact text sent to the model, for reproducing a failure.
    /// </summary>
    /// <remarks>
    /// Development diagnostics, not a record of what happened - which is why it is stripped by
    /// the sink rather than trusted to be absent, and why it is the one field the list endpoint
    /// will not return. With this and <see cref="Deployment"/> the call can be replayed against
    /// the provider directly, which is the difference between fixing a parsing fault and
    /// theorising about one.
    ///
    /// Bounded at <see cref="MaxPromptChars"/>. A batch prompt carries the whole vocabulary plus
    /// ten adverts and is the largest thing this system produces; storing it whole would put
    /// megabytes into a document store for a diagnostic.
    /// </remarks>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    /// <summary>Bound on <see cref="Prompt"/>, applied at construction.</summary>
    public const int MaxPromptChars = 60_000;

    /// <summary>Bound on <see cref="Reason"/>, applied at construction.</summary>
    public const int MaxReasonChars = 300;

    /// <summary>Bound on <see cref="AffectedIds"/>, applied at construction.</summary>
    public const int MaxAffectedIds = 50;

    /// <summary>
    /// The only way to build one, so the bounds cannot be forgotten at a call site.
    /// </summary>
    public static AiCallRecord Create(
        DateTimeOffset occurredAtUtc,
        string operation,
        string? deployment,
        AiCallOutcome outcome,
        int requested,
        int returned,
        long durationMs,
        string? reason = null,
        IReadOnlyList<long>? affectedIds = null,
        string? prompt = null,
        AiTokenUsage usage = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var ids = affectedIds is null
            ? []
            : affectedIds.Take(MaxAffectedIds).ToArray();

        return new AiCallRecord
        {
            // Time plus operation plus a short random tail. Deterministic enough that a retried
            // write converges, unique enough that two calls in the same millisecond do not
            // collide - which matters, because batches run back to back.
            Id = $"{occurredAtUtc.UtcDateTime:yyyyMMddTHHmmssfff}-{operation}-{Guid.NewGuid():N}"[..64],
            Day = occurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            OccurredAtUtc = occurredAtUtc,
            Operation = operation,
            Deployment = deployment,
            Outcome = outcome,
            Requested = requested,
            Returned = returned,
            DurationMs = durationMs,
            Reason = reason is null || reason.Length <= MaxReasonChars
                ? reason
                : reason[..MaxReasonChars],
            AffectedIds = ids,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            ReasoningTokens = usage.ReasoningTokens,
            TotalTokens = usage.TotalTokens > 0
                ? usage.TotalTokens
                : usage.InputTokens + usage.OutputTokens,
            Prompt = prompt is null || prompt.Length <= MaxPromptChars
                ? prompt
                : prompt[..MaxPromptChars],
        };
    }
}

/// <summary>
/// What one call cost, as the provider reported it.
/// </summary>
/// <remarks>
/// A struct with a meaningful default, so a call site that cannot get usage passes nothing
/// rather than inventing zeros that read as a measurement.
/// </remarks>
public readonly record struct AiTokenUsage(
    int InputTokens = 0,
    int OutputTokens = 0,
    int ReasoningTokens = 0,
    int TotalTokens = 0);

/// <summary>
/// Where a model call reports what happened to it.
/// </summary>
/// <remarks>
/// Resolved as nullable everywhere it is used, the same way the AI services themselves are. A
/// deployment with no ledger configured must still make its model calls - the record is
/// diagnostics, and diagnostics that can take down the thing they observe are worse than none.
///
/// Implementations must not throw. A failed write here is a lost record, which is bad; a failed
/// write that propagates is a lost assessment, which is worse.
/// </remarks>
public interface IAiCallLog
{
    Task RecordAsync(AiCallRecord record, CancellationToken ct = default);
}

/// <param name="Operation">Which pass, as named in the ledger.</param>
/// <param name="Calls">Model calls made.</param>
/// <param name="FailedCalls">Calls that lost something, whole or in part.</param>
/// <param name="Requested">Items sent, which is what was paid for.</param>
/// <param name="Returned">Items that came back usable.</param>
/// <param name="TotalTokens">What the window cost.</param>
/// <param name="ReasoningTokens">How much of it the model spent thinking.</param>
public sealed record AiCallTotals(
    string Operation,
    int Calls,
    int FailedCalls,
    int Requested,
    int Returned,
    long TotalTokens = 0,
    long ReasoningTokens = 0)
{
    public int Discarded => Math.Max(0, Requested - Returned);
}

/// <summary>
/// Where the ledger is read back from.
/// </summary>
/// <remarks>
/// An interface for the same reason <c>IMetricsSource</c> is one: the API depends on the
/// question, not on Cosmos, so the endpoints are testable without a storage account. The
/// write side is <see cref="IAiCallLog"/> and stays separate - the ingest writes and the API
/// reads, and neither should carry the other's surface into its own process.
/// </remarks>
public interface IAiCallSource
{
    /// <summary>Recent calls, newest first.</summary>
    Task<IReadOnlyList<AiCallRecord>> ListAsync(
        int days, bool failuresOnly, int limit, CancellationToken ct = default);

    /// <summary>
    /// One record, whole. The only way to reach a stored prompt.
    /// </summary>
    Task<AiCallRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>What the window cost and what was lost, per pass.</summary>
    Task<IReadOnlyList<AiCallTotals>> SummariseAsync(int days, CancellationToken ct = default);
}
