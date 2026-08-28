using JobPlatform.Core.Ai;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Data.Cosmos;

/// <summary>
/// Writes the AI call ledger to Cosmos.
/// </summary>
/// <remarks>
/// Cosmos rather than SQL, and that is not a preference. SQL is billed on wall-clock time
/// online against a monthly grant one daily ingest half-consumes, and it is reserved for posting
/// browse, search and detail; every dashboard metric already comes from Cosmos, and this is a
/// metric. A ledger written on every model call would be exactly the kind of steady write that
/// keeps the database awake and exhausts the grant.
///
/// Its own container rather than <c>metrics</c>, because that one is partitioned by
/// <c>/searchTerm</c> and a model call has no search term. Forcing one would mean inventing a
/// partition value, which is how a partition key stops meaning anything.
///
/// <b>Never throws.</b> A lost record is bad; an assessment lost because recording it failed is
/// worse, and the whole point of this type is to observe a path that must keep running.
/// </remarks>
public sealed class AiCallLogRepository : IAiCallLog
{
    private readonly Container _container;
    private readonly ILogger<AiCallLogRepository> _logger;
    private readonly bool _recordPrompts;

    public AiCallLogRepository(
        CosmosClient client,
        IOptions<CosmosOptions> options,
        ILogger<AiCallLogRepository> logger,
        IOptions<AiLedgerOptions>? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _container = client.GetContainer(settings.DatabaseName, settings.AiCallsContainerName);
        _logger = logger;
        _recordPrompts = ledger?.Value.RecordPrompts ?? false;
    }

    /// <summary>
    /// What is actually stored, after the prompt rules.
    /// </summary>
    /// <remarks>
    /// Applied here rather than at the call sites, deliberately. Four passes report to this
    /// ledger and a fifth will; a rule enforced in four places is a rule that survives until
    /// somebody adds the fifth. Stripping at the sink means a call site cannot get it wrong by
    /// passing a prompt it should not have.
    ///
    /// A success has nothing to reproduce, so its prompt is dropped even when recording is on.
    /// That is most of them, and it is most of the personal data that would otherwise accrue.
    /// </remarks>
    private AiCallRecord Sanitise(AiCallRecord record)
        => record.Prompt is null || (_recordPrompts && record.Outcome != AiCallOutcome.Succeeded)
            ? record
            : record with { Prompt = null };

    public async Task RecordAsync(AiCallRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await _container.CreateItemAsync(
                Sanitise(record), new PartitionKey(record.Day), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose, and this is the one place in the system where swallowing is
            // unambiguously right: the caller has already done the expensive work and its result
            // must not be lost because the diagnostics store was unavailable. Logged so that a
            // ledger which has quietly stopped filling is itself visible.
            _logger.LogWarning(
                ex,
                "Could not record the {Operation} call to the AI ledger. The call itself was "
                + "unaffected.",
                record.Operation);
        }
    }
}
