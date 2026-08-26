using System.Diagnostics;
using Azure.Storage.Blobs;
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
        var seen = 0;
        var truncated = false;

        // Paged rather than a flat enumeration, because the page's continuation token is what
        // makes a partial pass resumable - and it is only accurate at a page boundary. Sizing
        // the page to the limit is what lets the loop stop on one: the caller's bound and the
        // page boundary become the same place, so nothing is skipped when work is handed back.
        var pages = landingContainer
            .GetBlobsAsync(prefix: prefix, cancellationToken: ct)
            .AsPages(body?.ContinuationToken, pageSizeHint: limit);

        // Null once the listing is exhausted, which is how the caller knows it is done.
        string? continuation = null;

        await foreach (var page in pages)
        {
            foreach (var blob in page.Values)
            {
                seen++;

                if (!blob.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var client = landingContainer.GetBlobClient(blob.Name);
                    using var content = await client.OpenReadAsync(cancellationToken: ct);

                    // A scope per blob, because that is what the blob trigger gets: one
                    // invocation, one DbContext, one unit of work. Sharing a scope across the
                    // loop shares a change tracker too, and a posting present in two blobs is
                    // then attached twice - which is fine while ingest only mutates scalars
                    // and fails the moment it starts adding child rows. This is what "the
                    // trigger and the reprocess endpoint run the same path" has to mean.
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
                    // One bad blob must not abandon the rest of a backfill. The message is
                    // returned as well as logged - this is an authenticated admin route, and
                    // being able to see why a backfill failed is the point of it.
                    logger.LogError(ex, "Failed to reprocess {BlobName}.", blob.Name);
                    failures.Add(new { blob = blob.Name, error = ex.Message, type = ex.GetType().Name });
                }
            }

            // Handed back at a page boundary, so the token resumes exactly where this stopped.
            continuation = page.ContinuationToken;

            if (string.IsNullOrEmpty(continuation))
            {
                continuation = null;
                break;
            }

            if (seen >= limit || started.Elapsed >= Budget)
            {
                truncated = true;
                break;
            }
        }

        if (truncated)
        {
            logger.LogInformation(
                "Stopped after {Seen} blobs in {Elapsed}; call again with the continuation token "
                + "to resume.", seen, started.Elapsed);
        }

        var result = new
        {
            processed = processed.Count,
            blobPaths = processed,
            failures,
            // Present only while there is more to do, so "keep calling until this is absent" is
            // the whole contract a caller needs.
            continuationToken = continuation,
            done = continuation is null,
        };

        return failures.Count == 0
            ? new OkObjectResult(result)
            : new ObjectResult(result) { StatusCode = StatusCodes.Status207MultiStatus };
    }
}
