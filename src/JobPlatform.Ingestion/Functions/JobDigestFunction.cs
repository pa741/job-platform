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
    /// The share of a site's postings whose apply route is unknown before that is worth saying.
    /// </summary>
    /// <remarks>
    /// <b>Keyed on the route being unestablished, not on the apply URL being absent, and that is
    /// a correction.</b> The first version of this warned when a site was 98% "board-hosted",
    /// meaning 98% had no <c>job_url_direct</c> - which was the right alarm on the day LinkedIn's
    /// selector broke and the wrong one permanently afterwards. LinkedIn has stopped publishing
    /// apply URLs to signed-out clients at all, so that share is now pinned at 100% there and the
    /// warning would have fired on every ingest forever. A warning that fires on the ordinary
    /// case is one people learn to scroll past.
    ///
    /// What has no legitimate steady state is a board that says <i>nothing</i>: no link and no
    /// offsite flag. Every board this system scrapes answers that question one way or the other
    /// when it is working, so a site landing here has broken. Still near-total, for the original
    /// reason - a real mixture does not reach 98%.
    /// </remarks>
    private const double RouteUnknownAlarm = 0.98;

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

        // The check that catches a board going quiet about how to apply.
        //
        // Not "no apply URL" - that is now the permanent and correct state of every LinkedIn
        // posting, because LinkedIn stopped publishing them. What is never correct is a site
        // answering neither way: no link and no offsite flag means this run learned nothing
        // about how any of those jobs is applied to, and downstream that is a shortlist of
        // Unknowns nobody can act on.
        foreach (var site in digest.ApplyLinks.Where(
            s => s.Postings >= MinimumSiteSample && s.RouteUnknownShare >= RouteUnknownAlarm))
        {
            logger.LogWarning(
                "Site {Site} said nothing about how to apply for {RouteUnknown} of {Postings} " +
                "postings in {BlobPath} - no direct link and no offsite flag. Either the scraper " +
                "is not reading that board's apply markers, or it is not fetching the detail " +
                "page at all. Check the fill rate on the description column before the selectors.",
                site.Site, site.RouteUnknown, site.Postings, digest.BlobPath);
        }

    }
}
