using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Admin endpoint that runs the same pipeline over blobs already in the landing container.
/// </summary>
/// <remarks>
/// Exists for backfill (the container held runs before the function did) and to exercise
/// the pipeline end to end without waiting for the scraper's daily run. Because ingestion
/// is idempotent, re-running it over a processed blob is safe.
/// </remarks>
public sealed class ReprocessBlobFunction(
    IngestionPipeline pipeline,
    BlobContainerClient landingContainer,
    ILogger<ReprocessBlobFunction> logger)
{
    private sealed record ReprocessRequest(string? BlobPath, string? Prefix);

    private sealed record ReprocessResponse(int Processed, IReadOnlyList<string> BlobPaths, IReadOnlyList<string> Failures);

    [Function(nameof(ReprocessBlobFunction))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "admin/reprocess")]
        HttpRequestData request,
        CancellationToken ct)
    {
        var body = await JsonSerializer.DeserializeAsync<ReprocessRequest>(
            request.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            ct);

        var prefix = body?.BlobPath ?? body?.Prefix ?? "jobs/";

        logger.LogInformation("Reprocessing blobs under {Prefix}.", prefix);

        var processed = new List<string>();
        var failures = new List<string>();

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

                await pipeline.ProcessAsync(
                    content,
                    blob.Name,
                    blob.Properties.ETag?.ToString(),
                    blob.Properties.ContentLength ?? 0,
                    ct);

                processed.Add(blob.Name);
            }
            catch (Exception ex)
            {
                // One bad blob must not abandon the rest of a backfill.
                logger.LogError(ex, "Failed to reprocess {BlobName}.", blob.Name);
                failures.Add(blob.Name);
            }
        }

        var response = request.CreateResponse(
            failures.Count == 0 ? HttpStatusCode.OK : HttpStatusCode.MultiStatus);

        await response.WriteAsJsonAsync(
            new ReprocessResponse(processed.Count, processed, failures), ct);

        return response;
    }
}
