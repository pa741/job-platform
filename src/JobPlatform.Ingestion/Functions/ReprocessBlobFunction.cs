using System.Diagnostics;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Admin endpoint that runs the same pipeline over blobs already in the landing container.
/// </summary>
/// <remarks>
/// Exists for backfill (the container held runs before the function did) and to exercise
/// the pipeline end to end without waiting for the scraper's daily run. Because ingestion
/// is idempotent, re-running it over a processed blob is safe.
///
/// Uses ASP.NET Core integration types (<see cref="HttpRequest"/> / <see cref="IActionResult"/>)
/// rather than <c>HttpRequestData</c>: the host is built with
/// <c>ConfigureFunctionsWebApplication</c>, and mixing the two models leaves the route
/// unmapped, which shows up as a 404 rather than an error.
///
/// The route deliberately avoids an `admin/` prefix: `/admin/*` is reserved by the
/// Functions host, and claiming it puts the function in an error state at startup
/// ("the specified route conflicts with one or more built in routes") that also surfaces
/// only as a 404.
/// </remarks>
public sealed class ReprocessBlobFunction(
    IServiceScopeFactory scopeFactory,
    BlobContainerClient landingContainer,
    ILogger<ReprocessBlobFunction> logger)
{
    public sealed record ReprocessRequest(
        string? BlobPath, string? Prefix, int? Limit, string? ContinuationToken);

    /// <summary>Blobs one call will take on when the caller does not say.</summary>
    /// <remarks>
    /// The container holds a run per search term per day, so "jobs/" is thousands of blobs and
    /// the unbounded loop this replaces was a request that could not finish. An HTTP trigger
    /// gets about 230 seconds before the gateway gives up, and a 504 says nothing about how far
    /// the work got - the batch collector was rewritten for exactly this, after three timed-out
    /// attempts that were harmless only because the writes underneath happened to be idempotent.
    /// </remarks>
    private const int DefaultLimit = 50;

    /// <summary>Ceiling on what a caller may ask for in one call.</summary>
    private const int MaxLimit = 500;

    /// <summary>Wall-clock budget, well inside the gateway's patience.</summary>
    /// <remarks>
    /// The blob count is the control that matters, but blobs are not the same size and a page
    /// of large ones can outlast a page of small ones several times over. Returning early with
    /// a token beats returning a 504 with nothing.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(150);

    /// <summary>Blobs per listing page, independent of what the caller asked for.</summary>
    /// <remarks>
    /// The budget is only checked where the continuation token is accurate, which is a page
    /// boundary - so the page size is what decides how often it can be checked at all. Sizing
    /// pages to the caller's limit, as this first did, put exactly one boundary in a call and
    /// left the budget unable to interrupt anything: fifty slow blobs ran to completion or to a
    /// gateway timeout, which is the failure the budget was added to prevent. Small pages cost
    /// an extra listing round trip each and buy a check every few blobs.
    /// </remarks>
    private const int PageSize = 5;

    [Function(nameof(ReprocessBlobFunction))]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reprocess")]
        HttpRequest request,
        CancellationToken ct)
    {
        // Read directly rather than through [FromBody], which silently bound null and made
        // every call reprocess the whole container regardless of what was asked for. See
        // RequestBody for why that is worse than an error here.
        var body = await RequestBody.ReadAsync<ReprocessRequest>(request, ct);

        var prefix = body?.BlobPath ?? body?.Prefix ?? "jobs/";
        var limit = Math.Clamp(body?.Limit ?? DefaultLimit, 1, MaxLimit);

        logger.LogInformation("Reprocessing up to {Limit} blobs under {Prefix}.", limit, prefix);

        var processed = new List<string>();
        var failures = new List<object>();

        var started = Stopwatch.StartNew();

        // Paged rather than a flat enumeration, because the page's continuation token is what
        // makes a partial pass resumable. The page is deliberately smaller than the limit: the
        // token is only accurate at a boundary, so a call needs several of them to have anywhere
        // to stop.
        var pages = landingContainer
            .GetBlobsAsync(prefix: prefix, cancellationToken: ct)
            .AsPages(body?.ContinuationToken, pageSizeHint: Math.Min(limit, PageSize))
            .Select(page => new WalkPage<BlobItem>(page.Values, page.ContinuationToken));

        var outcome = await BoundedWalk.RunAsync(
            pages,
            body?.ContinuationToken,
            limit,
            Budget,
            () => started.Elapsed,
            (blob, token) => ReprocessAsync(blob, processed, failures, token),
            ct);

        if (!outcome.Exhausted)
        {
            logger.LogInformation(
                "Stopped after {Processed} blobs in {Elapsed}{MidPage}; call again with the "
                + "continuation token to resume.",
                outcome.Processed,
                started.Elapsed,
                outcome.StoppedMidPage ? " (part way through a page, which will be redone)" : string.Empty);
        }

        var result = new
        {
            processed = processed.Count,
            blobPaths = processed,
            failures,
            // Present only while there is more to do, so "keep calling until done" is the whole
            // contract a caller needs. It can legitimately be absent while done is false - a walk
            // stopped inside the first page has no boundary behind it - so a caller must read
            // done rather than infer it from the token being there.
            continuationToken = outcome.ResumeToken,
            done = outcome.Exhausted,
        };

        return failures.Count == 0
            ? new OkObjectResult(result)
            : new ObjectResult(result) { StatusCode = StatusCodes.Status207MultiStatus };
    }

    /// <summary>
    /// One blob through the same pipeline the trigger runs.
    /// </summary>
    /// <remarks>
    /// A scope per blob, because that is what the blob trigger gets: one invocation, one
    /// DbContext, one unit of work. Sharing a scope across the walk shares a change tracker too,
    /// and a posting present in two blobs is then attached twice - which is fine while ingest
    /// only mutates scalars and fails the moment it starts adding child rows. This is what "the
    /// trigger and the reprocess endpoint run the same path" has to mean.
    /// </remarks>
    private async Task ReprocessAsync(
        BlobItem blob, List<string> processed, List<object> failures, CancellationToken ct)
    {
        if (!blob.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var client = landingContainer.GetBlobClient(blob.Name);
            using var content = await client.OpenReadAsync(cancellationToken: ct);

            await using var scope = scopeFactory.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<IngestionPipeline>().ProcessAsync(
                content,
                blob.Name,
                blob.Properties.ETag?.ToString(),
                blob.Properties.ContentLength ?? 0,
                ct);

            processed.Add(blob.Name);
        }
        catch (Exception ex)
        {
            // One bad blob must not abandon the rest of a backfill. The message is returned as
            // well as logged - this is an authenticated admin route, and being able to see why a
            // backfill failed is the point of it.
            logger.LogError(ex, "Failed to reprocess {BlobName}.", blob.Name);
            failures.Add(new { blob = blob.Name, error = ex.Message, type = ex.GetType().Name });
        }
    }
}
