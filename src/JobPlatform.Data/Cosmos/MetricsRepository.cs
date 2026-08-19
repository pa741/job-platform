using JobPlatform.Core.Metrics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Data.Cosmos;

/// <summary>
/// Writes metric documents to Cosmos. Everything is an upsert against a deterministic id,
/// so replaying a blob converges on one document instead of accumulating duplicates.
/// The change feed on this container is what the realtime piece will subscribe to.
/// </summary>
public sealed class MetricsRepository
{
    private readonly Container _container;
    private readonly ILogger<MetricsRepository> _logger;

    public MetricsRepository(
        CosmosClient client,
        IOptions<CosmosOptions> options,
        ILogger<MetricsRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _container = client.GetContainer(settings.DatabaseName, settings.MetricsContainerName);
        _logger = logger;
    }

    public async Task UpsertRunDigestAsync(RunDigest digest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(digest);

        var response = await _container.UpsertItemAsync(
            digest,
            new PartitionKey(digest.SearchTerm),
            cancellationToken: ct);

        _logger.LogInformation(
            "Wrote run digest {DocumentId} for {SearchTerm} ({RequestCharge} RU).",
            digest.Id, digest.SearchTerm, response.RequestCharge);
    }

    public async Task UpsertDailyRollupAsync(DailyRollup rollup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        var response = await _container.UpsertItemAsync(
            rollup,
            new PartitionKey(rollup.SearchTerm),
            cancellationToken: ct);

        _logger.LogInformation(
            "Wrote daily rollup {DocumentId} ({RequestCharge} RU).",
            rollup.Id, response.RequestCharge);
    }
}
