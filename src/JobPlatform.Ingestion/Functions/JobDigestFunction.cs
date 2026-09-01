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
    /// <summary>Below this a site's board-hosted share says nothing, so it is not reported.</summary>
    private const int MinimumSiteSample = 20;

    /// <summary>
    /// The board-hosted share at which a site is reporting a broken selector rather than a market.
    /// </summary>
    /// <remarks>
    /// Deliberately near-total. Real shares move; they do not reach 98% on a board that had a
    /// mixture yesterday. Setting this lower would fire on ordinary variation and become the kind
    /// of warning people learn to scroll past, which is worse than not having it.
    /// </remarks>
    private const double BoardHostedAlarm = 0.98;

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

        // The inverse check, and it needs its own because the fill rate above cannot express it.
        // `job_url_direct` is absent wherever the board hosts the application, so absence is
        // ordinary and a whole-file rate never reaches zero. What is not ordinary is a site where
        // *every* posting is board-hosted: LinkedIn's half of this is a DOM scrape of one element
        // id, and if that id is renamed the entire corpus reads as Easy Apply with nothing
        // throwing - which is a broken scraper, not a hiring market that changed overnight.
        //
        // A near-total boundary rather than a trend, matching the shape of the check above. A
        // trend needs the trailing runs, which means a Cosmos read on the ingest path to catch
        // something a gradual market shift can never trigger; `dbadmin apply-links` is where that
        // comparison is cheap. The sample floor keeps a run that found three postings quiet.
        foreach (var site in digest.ApplyLinks.Where(
            s => s.Postings >= MinimumSiteSample && s.BoardHostedShare >= BoardHostedAlarm))
        {
            logger.LogWarning(
                "Site {Site} had no direct apply link on {BoardHosted} of {Postings} postings in " +
                "{BlobPath}. At that share this is a scraper selector that stopped matching, not " +
                "a change in how employers accept applications.",
                site.Site, site.BoardHosted, site.Postings, digest.BlobPath);
        }
    }
}
