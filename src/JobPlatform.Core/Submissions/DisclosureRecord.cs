using System.Text.Json.Serialization;

namespace JobPlatform.Core.Submissions;

/// <summary>
/// One piece of the candidate's own data handed to an agent, recorded so it can be read back.
/// </summary>
/// <remarks>
/// <b>The agent surface refuses <c>get_profile</c> because a tool result is transcript content
/// wherever the client runs.</b> Two tools cross that line anyway, deliberately and narrowly:
/// <c>get_form_field</c> answers one allowlisted question at a time, and
/// <c>get_submission_pack</c> returns the tailored CV and cover letter, which is the profile
/// rewritten in prose. Both are the right trade - an agent filling a form needs them - and both
/// are exactly the kind of thing that should not happen without a record.
///
/// <b>Never the value.</b> This says <i>that</i> the phone number was disclosed, when, to which
/// tool. Storing what it was would make the audit log a second copy of the thing it is auditing,
/// which is the mistake the AI ledger's prompt rules exist to avoid on the other side of the
/// same system.
///
/// <b>Cosmos rather than SQL</b>, for the reason the ledger gives: SQL is billed on wall-clock
/// time online against a monthly grant, and it is reserved for posting browse, search and
/// detail. A record written on every tool call is exactly the steady write that would keep the
/// database awake.
///
/// <b>App Insights is not a substitute.</b> Sampling is on here with
/// <c>excludedTypes: "Request;Exception"</c> and none of these calls throws, so traces are
/// sampled precisely where the record matters. A log you cannot trust to be complete is not one.
/// </remarks>
public sealed record DisclosureRecord
{
    /// <summary>Bounds the free-text fields so a caller cannot write an essay into the log.</summary>
    public const int MaxDetailChars = 200;

    /// <summary>Deterministic per call, so a retried write converges rather than duplicating.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Discriminator, so this can share a container with other document types.</summary>
    [JsonPropertyName("type")]
    public string Type => "disclosure";

    /// <summary>
    /// The UTC date, and the partition key.
    /// </summary>
    /// <remarks>
    /// A day partition rather than one per subject. The question this answers is "what left the
    /// system recently", which reads one partition; keying on the subject would bound growth per
    /// person but turn the ordinary read into a fan-out, and it would put a directory object id
    /// in a partition key where it is visible in every diagnostic that touches the container.
    /// </remarks>
    [JsonPropertyName("day")]
    public required string Day { get; init; }

    [JsonPropertyName("occurredAtUtc")]
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Whose data it was. The Entra object id, which is the id everything else keys on.</summary>
    [JsonPropertyName("subjectId")]
    public required string SubjectId { get; init; }

    /// <summary>Which tool asked - <c>get_form_field</c>, <c>get_submission_pack</c>.</summary>
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    /// <summary>
    /// What was disclosed, named rather than reproduced.
    /// </summary>
    /// <remarks>
    /// A field name for <c>get_form_field</c>, a posting id for <c>get_submission_pack</c>. Never
    /// the answer itself: this log exists to make disclosure reviewable, and a review that has to
    /// read the data to find out what happened has moved the problem rather than solved it.
    /// </remarks>
    [JsonPropertyName("detail")]
    public required string Detail { get; init; }

    /// <summary>Whether the profile actually carried an answer. A refusal is worth recording too.</summary>
    [JsonPropertyName("answered")]
    public required bool Answered { get; init; }

    /// <summary>
    /// The only constructor, so the bounds cannot be skipped at a call site.
    /// </summary>
    /// <remarks>
    /// The same rule <c>AiCallRecord.Create</c> follows and for the same reason: a guard written
    /// at the call sites survives until somebody adds another one. There will be more tools.
    /// </remarks>
    public static DisclosureRecord Create(
        DateTimeOffset occurredAtUtc,
        string subjectId,
        string tool,
        string detail,
        bool answered)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        return new DisclosureRecord
        {
            // Random rather than derived from the fields: two identical disclosures a minute
            // apart are two events, and a deterministic id would silently record one.
            Id = $"disc|{Guid.NewGuid():n}",
            Day = occurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            OccurredAtUtc = occurredAtUtc,
            SubjectId = subjectId,
            Tool = Bound(tool),
            Detail = Bound(detail),
            Answered = answered,
        };
    }

    private static string Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= MaxDetailChars ? trimmed : trimmed[..MaxDetailChars];
    }
}

/// <summary>
/// Where a disclosure is recorded.
/// </summary>
/// <remarks>
/// <b>Implementations must not throw.</b> A lost record is bad; a candidate's application
/// pipeline failing because the audit write failed is worse - the same contract
/// <c>IAiCallLog</c> and <c>IRealtimeFeed</c> run under.
///
/// <b>Resolved as nullable by every consumer.</b> A deployment with no Cosmos configured still
/// serves the tools, exactly as it still serves the ledger's callers. That is the degraded mode
/// this architecture has everywhere, and it is what lets the API test host boot without Azure.
/// </remarks>
public interface IDisclosureLog
{
    Task RecordAsync(DisclosureRecord record, CancellationToken ct = default);
}
