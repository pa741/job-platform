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
/// <b>No prompt, ever.</b> The assessor's and the extractor's prompts carry the candidate's
/// profile: employment history, contact details, salary expectations. This record holds counts, a
/// bounded reason and the ids affected, all of which are safe to keep and to show. A store that
/// holds both public postings and profile-derived prose is a store that leaks the second one.
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
        IReadOnlyList<long>? affectedIds = null)
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
        };
    }
}

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
