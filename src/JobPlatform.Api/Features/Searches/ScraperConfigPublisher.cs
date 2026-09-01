using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using JobPlatform.Core.Searches;
using JobPlatform.Data.Sql;

namespace JobPlatform.Api.Features.Searches;

/// <summary>
/// The container the scraper's configuration is published to.
/// </summary>
/// <remarks>
/// A named wrapper rather than a bare <see cref="BlobContainerClient"/> registration, following
/// <c>CuratedContainer</c> on the ingest side and for the reason written there: two
/// registrations of one type resolve by whichever was added last, and the failure - writing the
/// configuration into the landing container, where Event Grid would try to ingest it as a CSV -
/// would be silent.
/// </remarks>
public sealed record ScraperConfigContainer(BlobContainerClient Client);

/// <summary>
/// Writes every enabled search to the blob the scraper reads instead of its local YAML.
/// </summary>
/// <remarks>
/// <b>A blob rather than an endpoint, and that is architectural.</b> The scraper runs on a NAS
/// with no managed identity. An API it had to call would need a client secret or a function key
/// living on that NAS, and this system has no secret store by design - a credential appearing
/// where the design has none is the signal that the design is being worked around. The scraper
/// already holds a storage credential and already speaks to exactly one Azure service; this
/// keeps both of those true.
///
/// <b>Rebuilt whole from SQL, never amended.</b> The same rule the curated exporter follows: a
/// republish converges, a failed one needs no cleanup, and there is no state here that can drift
/// from the table it is derived from.
///
/// <b>Optional.</b> No <c>ScraperConfig:ServiceUri</c> registers no container and no publisher,
/// consumers resolve this as nullable and skip, and the endpoints say so in their response. A
/// clone deploys with no extra container and the test suite needs no credential - the same
/// degraded mode <c>AddAiProvider</c> establishes for the model provider.
/// </remarks>
public sealed class ScraperConfigPublisher(
    ScraperConfigContainer container,
    ScraperSearchRepository searches,
    TimeProvider time,
    ILogger<ScraperConfigPublisher> logger)
{
    /// <summary>
    /// The blob the scraper reads. Fixed, because it is half of a contract with another repo.
    /// </summary>
    public const string BlobName = "searches.json";

    /// <summary>
    /// When the configuration was last written, or null if it never was.
    /// </summary>
    /// <remarks>
    /// Read from the blob rather than remembered, because the question the settings page asks is
    /// "has the scraper been told", and only the blob knows - this host restarts, scales to zero,
    /// and is not the only thing that could have published. One properties call, on a page a
    /// person opened, and it answers "why is my new search not running" without a log.
    ///
    /// Null on any failure, including the blob simply not existing yet. That is the honest
    /// answer to the question and not an error state.
    /// </remarks>
    public async Task<DateTimeOffset?> LastPublishedAsync(CancellationToken ct = default)
    {
        try
        {
            var properties = await container.Client.GetBlobClient(BlobName).GetPropertiesAsync(cancellationToken: ct);

            return properties.Value.LastModified;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                ex, "No published scraper configuration at {Container}/{Blob}.",
                container.Client.Name, BlobName);

            return null;
        }
    }

    /// <summary>
    /// Rewrites the configuration. Returns when it was published, or null if it was not.
    /// </summary>
    /// <remarks>
    /// <b>Never throws, and a failure must never fail the save that triggered it.</b> Losing
    /// somebody's typing to a role assignment that has not propagated yet is a worse outcome
    /// than a stale blob, and the stale blob is recoverable: the response carries the timestamp,
    /// the page shows it, and <c>POST /searches/publish</c> is the retry. The alternative -
    /// rolling back the save - would mean the platform's own permissions problem presents as the
    /// person's form being broken.
    ///
    /// It logs at warning rather than error, with the container named, because the overwhelmingly
    /// likely cause on a fresh deployment is the scoped Blob Data Contributor assignment still
    /// propagating, which resolves itself in a minute or two.
    /// </remarks>
    public async Task<DateTimeOffset?> PublishAsync(CancellationToken ct = default)
    {
        try
        {
            var enabled = await searches.ListForPublishAsync(ct);
            var publishedUtc = time.GetUtcNow();
            var document = ScraperConfigDocument.Build(enabled, publishedUtc);

            var bytes = Encoding.UTF8.GetBytes(document.ToJson());
            using var stream = new MemoryStream(bytes, writable: false);

            await container.Client.GetBlobClient(BlobName).UploadAsync(
                stream,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                },
                ct);

            logger.LogInformation(
                "Published {Count} enabled search(es) to {Container}/{Blob}.",
                enabled.Count, container.Client.Name, BlobName);

            return publishedUtc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not publish the scraper configuration to {Container}/{Blob}. The searches "
                + "are saved; the scraper will run the previously published set until this "
                + "succeeds. On a fresh deployment the usual cause is the container's role "
                + "assignment still propagating.",
                container.Client.Name,
                BlobName);

            return null;
        }
    }
}
