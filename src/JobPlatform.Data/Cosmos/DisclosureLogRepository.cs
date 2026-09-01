using JobPlatform.Core.Submissions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Data.Cosmos;

/// <summary>
/// Writes the disclosure log to Cosmos.
/// </summary>
/// <remarks>
/// Its own container rather than <c>aiCalls</c>, even though the two are the same shape and the
/// same size. They answer different questions and are read by different people at different
/// times: the ledger is "did the nightly passes lose anything", this is "what of mine has left
/// the system". Sharing a container would make the ordinary read of either one a filter over the
/// other's traffic, and a single TTL would tie two retention decisions together that have no
/// reason to move as one.
///
/// Not <c>metrics</c> for the reason the ledger gives: that container is partitioned by
/// <c>/searchTerm</c> and a disclosure has none. Inventing a partition value is how a partition
/// key stops meaning anything.
///
/// <b>Never throws.</b> A lost record is bad; an agent's whole call failing because the audit
/// write failed is worse - the same contract <see cref="AiCallLogRepository"/> runs under, and
/// for the same reason.
/// </remarks>
public sealed class DisclosureLogRepository : IDisclosureLog
{
    private readonly Container _container;
    private readonly ILogger<DisclosureLogRepository> _logger;

    public DisclosureLogRepository(
        CosmosClient client,
        IOptions<CosmosOptions> options,
        ILogger<DisclosureLogRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _container = client.GetContainer(settings.DatabaseName, settings.DisclosuresContainerName);
        _logger = logger;
    }

    public async Task RecordAsync(DisclosureRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await _container.CreateItemAsync(
                record, new PartitionKey(record.Day), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Swallowed for the reason the ledger swallows: the caller is serving a request and
            // must not fail because the audit store was unavailable. Logged at warning so a log
            // that has quietly stopped filling is itself visible - which matters more here than
            // in the ledger, because this one is the record of what left the system.
            _logger.LogWarning(
                ex,
                "Could not record a {Tool} disclosure. The call itself was unaffected.",
                record.Tool);
        }
    }
}
