using JobPlatform.Core.Ai;
using JobPlatform.Core.Realtime;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Pushes a model call that lost something to the dashboard, as it happens.
/// </summary>
/// <remarks>
/// <b>The Realtime piece of <c>model.md</c>, and the last one to be built.</b> The row in that
/// table reads "reacts to metric writes, pushes to clients - Azure Functions (Cosmos trigger),
/// SignalR/Web PubSub", and this is exactly that: a change-feed trigger over the ledger container
/// and a broadcast to whoever has the dashboard open.
///
/// <b>Built for a reason rather than for completeness.</b> Every AI path here degrades silently by
/// design - a provider failure must not take down endpoints with nothing to do with AI - and that
/// design cost real work three times before the ledger existed. The ledger made those losses
/// readable afterwards; this makes them visible while they are still happening. The distinction
/// matters for the two passes that run at night: a sweep discarding batches at 03:30 is worth
/// knowing about at 03:31, not at whatever hour somebody next opens a page.
///
/// <b>Failures only, and that is not a filter for tidiness.</b> The container also carries every
/// successful call, and the free tier allows 20,000 messages a day - one message per successful
/// extraction would exhaust that on a single backfill and take the failures down with it. The
/// dashboard's subject is failure; the summary endpoint has the totals.
///
/// <b>The trigger reads the ledger, never writes to it.</b> The change feed is a read of what the
/// call sites already recorded, so this function cannot change what the ledger says, cannot
/// double-count, and cannot fail in a way that loses a record. The worst it does is stay quiet.
/// </remarks>
public sealed class AiFailureFeedFunction(
    ILogger<AiFailureFeedFunction> logger,
    IRealtimeFeed? feed = null)
{
    [Function(nameof(AiFailureFeedFunction))]
    public async Task RunAsync(
        [CosmosDBTrigger(
            databaseName: "%Cosmos:DatabaseName%",
            containerName: "aiCalls",
            // Identity-based, like every other connection here: the setting resolves to
            // CosmosFeed__accountEndpoint plus a managed identity credential, never a key.
            // Cosmos runs with disableLocalAuth, so a key would not work even if one were set.
            Connection = "CosmosFeed",
            LeaseContainerName = "leases",
            // Prefixed so this processor's checkpoints can share the leases container with any
            // later one. Without it a second change-feed function silently steals these leases
            // and the two take turns missing documents.
            LeaseContainerPrefix = "aiCalls-",
            // The container is provisioned by Bicep, and it must stay that way: created here it
            // would arrive with default throughput charged against the free tier's 1000 RU/s.
            CreateLeaseContainerIfNotExists = false)]
        IReadOnlyList<AiCallRecord> records,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (feed is null)
        {
            // No realtime service configured. The ledger still records and the dashboard still
            // polls, which is what it did before this existed.
            return;
        }

        var failures = records.Where(r => r.Outcome != AiCallOutcome.Succeeded).ToList();

        if (failures.Count == 0)
        {
            return;
        }

        foreach (var record in failures)
        {
            // A projection, not the record. AiCallRecord carries an optional prompt holding the
            // candidate's employment history and salary expectations, and the three guards that
            // keep it off the list endpoint would have to be reproduced here to keep it off a
            // websocket. A type with no field for it cannot leak it.
            await feed.PublishAsync(
                RealtimeChannels.AiFailure,
                new AiFailureNotice(
                    record.Operation,
                    record.Deployment,
                    record.Outcome.ToString(),
                    record.Requested,
                    record.Returned,
                    record.Reason,
                    record.OccurredAtUtc),
                ct);
        }

        logger.LogInformation(
            "Realtime: pushed {Failures} AI failure(s) of {Total} change-feed document(s).",
            failures.Count, records.Count);
    }
}
