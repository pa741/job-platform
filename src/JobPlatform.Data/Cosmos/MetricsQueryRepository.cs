using JobPlatform.Core.Metrics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace JobPlatform.Data.Cosmos;

/// <summary>
/// Reads metric documents. The counterpart to <see cref="MetricsRepository"/>, which writes
/// them during ingestion.
/// </summary>
/// <remarks>
/// This is where every dashboard number comes from, and that is a cost decision rather than
/// a convenience one. The equivalent figures could be recomputed from SQL, but SQL is
/// serverless and billed by wall-clock second against a monthly grant that one daily ingest
/// already half-consumes; Cosmos is always on and RU-billed inside a free ceiling. A polling
/// dashboard belongs here.
///
/// Every single-term query pins the partition key so it stays a single-partition read.
/// Cross-partition queries fan out across every search term and cost proportionally.
/// </remarks>
public sealed class MetricsQueryRepository : IMetricsSource
{
    private readonly Container _container;

    public MetricsQueryRepository(CosmosClient client, IOptions<CosmosOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _container = client.GetContainer(settings.DatabaseName, settings.MetricsContainerName);
    }

    /// <summary>The most recent run for a search term - the dashboard's "now".</summary>
    public async Task<RunDigest?> GetLatestRunDigestAsync(
        string searchTerm, CancellationToken ct = default)
    {
        var results = await ListRunDigestsAsync(searchTerm, from: null, to: null, take: 1, ct);
        return results.Count == 0 ? null : results[0];
    }

    /// <summary>
    /// Run digests, newest first. <paramref name="from"/> and <paramref name="to"/> filter on
    /// <c>scrapedAtUtc</c>, which is indexed; ordering on anything unindexed would fail.
    /// </summary>
    public Task<IReadOnlyList<RunDigest>> ListRunDigestsAsync(
        string searchTerm,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int take,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTerm);

        var sql = "SELECT * FROM c WHERE c.type = @type";

        if (from is not null)
        {
            sql += " AND c.scrapedAtUtc >= @from";
        }

        if (to is not null)
        {
            sql += " AND c.scrapedAtUtc <= @to";
        }

        sql += " ORDER BY c.scrapedAtUtc DESC OFFSET 0 LIMIT @take";

        var query = new QueryDefinition(sql)
            .WithParameter("@type", "run-digest")
            .WithParameter("@take", Math.Clamp(take, 1, 365));

        if (from is not null)
        {
            query = query.WithParameter("@from", from.Value);
        }

        if (to is not null)
        {
            query = query.WithParameter("@to", to.Value);
        }

        return QueryAsync<RunDigest>(query, searchTerm, ct);
    }

    /// <summary>
    /// Daily rollups, oldest first - the shape a time-series chart wants.
    /// </summary>
    /// <remarks>
    /// Filters on <c>date</c>, which is a string in <c>yyyy-MM-dd</c> form. Lexicographic
    /// comparison is correct for that format specifically, which is why the ingestion side
    /// stores it that way rather than as a timestamp.
    /// </remarks>
    public Task<IReadOnlyList<DailyRollup>> ListDailyRollupsAsync(
        string searchTerm,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTerm);

        var sql = "SELECT * FROM c WHERE c.type = @type";

        if (from is not null)
        {
            sql += " AND c.date >= @from";
        }

        if (to is not null)
        {
            sql += " AND c.date <= @to";
        }

        sql += " ORDER BY c.date ASC";

        var query = new QueryDefinition(sql).WithParameter("@type", "daily-rollup");

        if (from is not null)
        {
            query = query.WithParameter("@from", from.Value.ToString("yyyy-MM-dd"));
        }

        if (to is not null)
        {
            query = query.WithParameter("@to", to.Value.ToString("yyyy-MM-dd"));
        }

        return QueryAsync<DailyRollup>(query, searchTerm, ct);
    }

    public async Task<DailyRollup?> GetDailyRollupAsync(
        string searchTerm, DateOnly date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTerm);

        try
        {
            // A point read, not a query: the id is deterministic, and a point read is the
            // cheapest operation Cosmos offers (1 RU against ~3+ for the equivalent query).
            var response = await _container.ReadItemAsync<DailyRollup>(
                MetricsCalculator.DailyRollupId(searchTerm, date),
                new PartitionKey(searchTerm),
                cancellationToken: ct);

            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Search terms that have metric documents.
    /// </summary>
    /// <remarks>
    /// The one deliberately cross-partition query here - it is asking what the partitions
    /// are, so it cannot be anything else. Cached aggressively by the API; the answer changes
    /// only when the scraper's config does.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListSearchTermsAsync(CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT DISTINCT VALUE c.searchTerm FROM c");
        var terms = new List<string>();

        using var iterator = _container.GetItemQueryIterator<string>(query);

        while (iterator.HasMoreResults)
        {
            foreach (var term in await iterator.ReadNextAsync(ct))
            {
                terms.Add(term);
            }
        }

        terms.Sort(StringComparer.OrdinalIgnoreCase);
        return terms;
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        QueryDefinition query, string searchTerm, CancellationToken ct)
    {
        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(searchTerm) };
        var results = new List<T>();

        using var iterator = _container.GetItemQueryIterator<T>(query, requestOptions: options);

        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(ct));
        }

        return results;
    }
}
