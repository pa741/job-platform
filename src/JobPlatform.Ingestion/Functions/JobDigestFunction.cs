using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Fires when the scraper uploads a CSV to the landing container.
/// </summary>
/// <remarks>
/// Uses the Event Grid source rather than the polling implementation: the Flex Consumption
/// plan supports only the event-based blob trigger, and it fires on upload instead of
/// waiting for a container scan. The Event Grid subscription is defined in
/// <c>infra/modules/eventgrid.bicep</c> and filtered to <c>jobs/*.csv</c>.
/// </remarks>
public sealed class JobDigestFunction(IngestionPipeline pipeline, ILogger<JobDigestFunction> logger)
{
    [Function(nameof(JobDigestFunction))]
    public async Task RunAsync(
        [BlobTrigger("%LandingContainerName%/jobs/{name}", Source = BlobTriggerSource.EventGrid,
                     Connection = "LandingStorage")]
        Stream content,
        string name,
        FunctionContext functionContext,
        CancellationToken ct)
    {
        var blobPath = $"jobs/{name}";

        try
        {
            var digest = await pipeline.ProcessAsync(
                content, blobPath, etag: null, sizeBytes: content.CanSeek ? content.Length : 0, ct);

            EmitMetrics(digest);
        }
        catch (Exception ex)
        {
            // Let it throw: Event Grid retries, and a swallowed failure would look like a
            // successful ingest that silently produced no metrics.
            logger.LogError(ex, "Failed to ingest {BlobPath}.", blobPath);
            throw;
        }
    }

    /// <summary>
    /// Mirrors the headline counts into Application Insights so alerts can be written
    /// against them without querying Cosmos.
    /// </summary>
    private void EmitMetrics(Core.Metrics.RunDigest digest)
    {
        logger.LogInformation(
            "IngestSucceeded {SearchTerm} parsed={Parsed} new={New} updated={Updated} " +
            "invalid={Invalid} remoteShare={RemoteShare} salaryCoverage={SalaryCoverage} " +
            "datePostedCoverage={DateCoverage}",
            digest.SearchTerm,
            digest.Counts.Parsed,
            digest.Counts.New,
            digest.Counts.Updated,
            digest.Counts.Invalid,
            digest.Remote.RemoteShare,
            digest.Salary.Coverage,
            digest.Freshness.Coverage);

        // A board that stops returning a field shows up here before anyone notices
        // the dashboard looks thin.
        foreach (var (column, rate) in digest.FieldFillRates.Where(f => f.Value == 0))
        {
            logger.LogWarning(
                "Column {Column} was empty in every row of {BlobPath}.", column, digest.BlobPath);
        }
    }
}
