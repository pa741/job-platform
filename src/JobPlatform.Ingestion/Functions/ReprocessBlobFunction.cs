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
    public sealed record ReprocessRequest(string? BlobPath, string? Prefix);

    [Function(nameof(ReprocessBlobFunction))]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reprocess")]
        HttpRequest request,
        [FromBody] ReprocessRequest? body,
        CancellationToken ct)
    {
        var prefix = body?.BlobPath ?? body?.Prefix ?? "jobs/";

        logger.LogInformation("Reprocessing blobs under {Prefix}.", prefix);

        var processed = new List<string>();
        var failures = new List<object>();

        await foreach (var blob in landingContainer.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
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
                // then attached twice - which is fine while ingest only mutates scalars and
                // fails the moment it starts adding child rows. This is what "the trigger and
                // the reprocess endpoint run the same path" has to mean.
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

        var result = new
        {
            processed = processed.Count,
            blobPaths = processed,
            failures,
        };

        return failures.Count == 0
            ? new OkObjectResult(result)
            : new ObjectResult(result) { StatusCode = StatusCodes.Status207MultiStatus };
    }
}
