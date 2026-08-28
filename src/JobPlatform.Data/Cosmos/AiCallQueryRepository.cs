using System.Globalization;
using JobPlatform.Core.Ai;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace JobPlatform.Data.Cosmos;

/// <summary>
/// Reads the AI call ledger back, most recent first.
/// </summary>
/// <remarks>
/// Separate from <see cref="AiCallLogRepository"/> for the same reason
/// <c>MetricsQueryRepository</c> is separate from <c>MetricsRepository</c>: the ingest writes and
/// the API reads, and neither should carry the other's surface into its own process.
///
/// Every query is bounded by day and by count. The container is partitioned by day, so a bounded
/// range of days is a bounded fan-out - and "show me everything since the beginning" is a
/// question nobody needs answered about diagnostics that carry a ninety-day TTL anyway.
/// </remarks>
public sealed class AiCallQueryRepository : IAiCallSource
{
    private readonly Container _container;

    public AiCallQueryRepository(CosmosClient client, IOptions<CosmosOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _container = client.GetContainer(settings.DatabaseName, settings.AiCallsContainerName);
    }

    /// <summary>How many days back a single read may reach.</summary>
    public const int MaxDays = 30;

    /// <summary>How many records a single read may return.</summary>
    public const int MaxRecords = 200;

    /// <summary>
    /// Recent calls, newest first, optionally only the ones that lost something.
    /// </summary>
    /// <param name="days">Days back to look, clamped to <see cref="MaxDays"/>.</param>
    /// <param name="failuresOnly">
    /// Anything that was not a clean success. This is the default view worth showing: a list of
    /// calls that worked is a list nobody reads.
    /// </param>
    /// <param name="limit">Rows to return, clamped to <see cref="MaxRecords"/>.</param>
    public async Task<IReadOnlyList<AiCallRecord>> ListAsync(
        int days = 7,
        bool failuresOnly = true,
        int limit = 100,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        var today = (now ?? DateTimeOffset.UtcNow).UtcDateTime.Date;
        var window = Math.Clamp(days, 1, MaxDays);

        var partitions = Enumerable
            .Range(0, window)
            .Select(offset => today.AddDays(-offset).ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToArray();

        var sql = failuresOnly
            ? "SELECT * FROM c WHERE c.type = @type AND c.outcome != @clean ORDER BY c.occurredAtUtc DESC"
            : "SELECT * FROM c WHERE c.type = @type ORDER BY c.occurredAtUtc DESC";

        var query = new QueryDefinition(sql)
            .WithParameter("@type", "aiCall")
            .WithParameter("@clean", nameof(AiCallOutcome.Succeeded));

        var take = Math.Clamp(limit, 1, MaxRecords);
        var results = new List<AiCallRecord>(take);

        // One partition at a time rather than a cross-partition query. The partition key is the
        // day, the window is small and bounded, and issuing them individually keeps each read on
        // a single partition - which is the difference between a few RUs and a fan-out against a
        // free-tier throughput ceiling that is load-bearing here.
        foreach (var day in partitions)
        {
            if (results.Count >= take)
            {
                break;
            }

            var options = new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(day),
                MaxItemCount = take - results.Count,
            };

            using var iterator = _container.GetItemQueryIterator<AiCallRecord>(query, requestOptions: options);

            while (iterator.HasMoreResults && results.Count < take)
            {
                foreach (var record in await iterator.ReadNextAsync(ct))
                {
                    results.Add(record);

                    if (results.Count >= take)
                    {
                        break;
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// What the last <paramref name="days"/> cost and what was lost, per operation.
    /// </summary>
    /// <remarks>
    /// The headline a dashboard actually needs. Discarded against requested is the number that
    /// was invisible: 40 written of 90 sent reads as a small night unless the two are side by
    /// side.
    /// </remarks>
    public async Task<IReadOnlyList<AiCallTotals>> SummariseAsync(
        int days = 7,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        var records = await ListAsync(days, failuresOnly: false, MaxRecords, now, ct);

        return [.. records
            .GroupBy(r => r.Operation, StringComparer.Ordinal)
            .Select(g => new AiCallTotals(
                g.Key,
                g.Count(),
                g.Count(r => r.Outcome != AiCallOutcome.Succeeded),
                g.Sum(r => r.Requested),
                g.Sum(r => r.Returned),
                g.Sum(r => (long)r.TotalTokens),
                g.Sum(r => (long)r.ReasoningTokens)))
            .OrderByDescending(t => t.Discarded)];
    }

    /// <summary>
    /// One record, whole, including its prompt where one was kept.
    /// </summary>
    /// <remarks>
    /// A point read against the day partition, which the id carries as its prefix - so this
    /// costs a single RU rather than a fan-out, and needs no extra parameter from the caller.
    /// </remarks>
    public async Task<AiCallRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        var day = DayOf(id);

        if (day is null)
        {
            return null;
        }

        try
        {
            return await _container.ReadItemAsync<AiCallRecord>(id, new PartitionKey(day), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// The partition an id belongs to, read back out of the id itself.
    /// </summary>
    /// <remarks>
    /// <c>AiCallRecord.Create</c> builds the id as <c>yyyyMMddTHHmmssfff-operation-guid</c>, so
    /// the day is the first eight characters. Deriving it beats asking the caller for it: a
    /// client that has to pass a partition key alongside an id will eventually pass the wrong
    /// one, and the read would then answer 404 for a record that exists.
    /// </remarks>
    private static string? DayOf(string id)
        => id.Length < 8 || !DateTime.TryParseExact(
            id[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? null
            : parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    Task<IReadOnlyList<AiCallRecord>> IAiCallSource.ListAsync(
        int days, bool failuresOnly, int limit, CancellationToken ct)
        => ListAsync(days, failuresOnly, limit, now: null, ct);

    Task<IReadOnlyList<AiCallTotals>> IAiCallSource.SummariseAsync(int days, CancellationToken ct)
        => SummariseAsync(days, now: null, ct);
}
