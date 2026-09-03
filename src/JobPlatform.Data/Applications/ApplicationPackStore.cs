using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Data.Applications;

/// <summary>
/// Where rendered application documents are kept.
/// </summary>
/// <remarks>
/// <b>Bound to the <c>ApplicationPacks</c> section, whose keys are set by the hosting template.</b>
/// <c>ApplicationPacks__serviceUri</c> and <c>ApplicationPacks__ContainerName</c> are written by
/// <c>infra/modules/containerapp.bicep</c>, and the names here match those exactly - the section
/// is bound case-insensitively, which is why the lower-case <c>serviceUri</c> in the template
/// still reaches <see cref="ServiceUri"/>. A rename on either side is a deployment that silently
/// loses its storage, so neither side renames without the other.
///
/// <b>The Functions host takes the same two names, and that is why there is only one pair.</b>
/// Its app settings arrive as environment variables, where <c>__</c> is the section separator, so
/// the container app's spelling binds there unchanged. A second spelling for the second host
/// would be a second thing to keep in step with two templates, and the half that fell behind
/// would be a deployment holding storage it is configured for and never writes to - which is
/// precisely the state the nightly pass was already in for want of a registration.
/// </remarks>
public sealed class ApplicationPackOptions
{
    public const string SectionName = "ApplicationPacks";

    /// <summary>
    /// The blob endpoint, e.g. <c>https://account.blob.core.windows.net</c>.
    /// </summary>
    /// <remarks>
    /// <b>A service URI and never a connection string.</b> The identity is
    /// <c>DefaultAzureCredential</c> with the container app's user-assigned client id, exactly as
    /// the scraper configuration publisher and the Cosmos client are authenticated. This
    /// repository has no secret store on purpose: a credential appearing where the design has none
    /// is the signal that the design is being worked around.
    ///
    /// <b>Empty is a supported deployment.</b> Nothing is registered, every consumer resolves the
    /// store as null, and the pack says no file is available. See <c>ApplicationPackSetup</c>.
    /// </remarks>
    public string? ServiceUri { get; set; }

    /// <summary>The container rendered documents live in. Its own, never a prefix under another.</summary>
    /// <remarks>
    /// The default matches the template's, so a host that sets only the service URI still works.
    /// It is the only container in this account whose contents leave the tenant, which is why
    /// <c>main.bicep</c> gives it a scoped Blob Data Contributor assignment rather than widening
    /// the account-wide grant.
    /// </remarks>
    public string ContainerName { get; set; } = "application-packs";

    /// <summary>
    /// How many minutes a signed link lives. Fifteen by default.
    /// </summary>
    /// <remarks>
    /// <b>The URL is the authority, so its lifetime is the exposure.</b> A user-delegation SAS is
    /// a bearer credential in a query string: whoever holds it reads the candidate's CV, and the
    /// string will be in a model's transcript, in whatever logged the tool result, and quite
    /// possibly in a proxy's access log - all of which outlive the request that minted it. What
    /// bounds the damage is not who was given it but how long it works for.
    ///
    /// <b>Fifteen minutes, because the two failures are asymmetric.</b> Too long is a leaked
    /// document; too short is a second call to the same tool, which costs a round trip. The link
    /// is used within seconds of being read in the ordinary case - an agent fetches the file, or a
    /// person clicks it - and fifteen minutes still covers the case this loop actually has, where
    /// the pack is read, a human is asked to look at something, and the download happens after
    /// they answer. An hour would cover nothing extra that a re-read does not, and would leave the
    /// document readable long after the conversation carrying the link had moved on.
    ///
    /// <b>Configurable, and clamped, because "configurable" must not become "permanent".</b> A
    /// deployment driving a slower human loop can raise it; <see cref="MaxMinutes"/> stops a
    /// misconfiguration turning a short-lived link into a durable one, and
    /// <see cref="MinMinutes"/> stops a zero or a negative producing a signature that has already
    /// expired when it is handed over.
    /// </remarks>
    public int LinkLifetimeMinutes { get; set; } = 15;

    /// <summary>Below this a link expires before it can be followed.</summary>
    public const int MinMinutes = 1;

    /// <summary>Above this it stops being short-lived, which is the whole property.</summary>
    public const int MaxMinutes = 60;

    /// <summary>The clamped lifetime. Read this rather than the raw setting.</summary>
    public TimeSpan LinkLifetime
        => TimeSpan.FromMinutes(Math.Clamp(LinkLifetimeMinutes, MinMinutes, MaxMinutes));
}

/// <summary>
/// The container rendered documents are written to, and the account client that signs for it.
/// </summary>
/// <remarks>
/// <b>A named wrapper rather than bare client registrations</b>, following
/// <c>ScraperConfigContainer</c> and <c>CuratedContainer</c> and for the reason written there:
/// two registrations of one type resolve by whichever was added last, and a rendered CV written
/// into the landing container - where Event Grid would try to ingest it as a CSV - would fail
/// silently.
///
/// <b>Both clients, because signing and writing happen at different scopes.</b> The blob is
/// written through the container; the user delegation key is requested from the <i>account</i>,
/// which is why <see cref="Service"/> is carried rather than reached for. Deriving one from the
/// other at the point of use would put the account endpoint back into the calling code, which is
/// the thing this wrapper exists to keep in one place.
/// </remarks>
public sealed record ApplicationPackContainer(BlobServiceClient Service, BlobContainerClient Client);

/// <summary>
/// Puts a rendered document where a browser can fetch it, and lets it at one for a few minutes.
/// </summary>
/// <remarks>
/// <b>This is the only thing in the system whose output leaves the tenant, and it leaves as a
/// signature rather than as bytes.</b> Nothing here proxies a download: the API would otherwise
/// hold a request open streaming megabytes out of storage through a container billed by the
/// second, and the file would pass through the agent's context on the way. A short-lived read-only
/// user-delegation SAS is the alternative - the browser fetches from storage directly, the API is
/// out of the path, and what was handed over expires.
///
/// <b>No account key, and there is nowhere to put one.</b> The signature is made with a user
/// delegation key, which is itself obtained with the user-assigned managed identity both hosts
/// run under: the account-wide Blob Data Reader assignment already carries
/// <c>generateUserDelegationKey</c>, so
/// there is no second role and no secret. That is what keeps the repository's "a fresh clone
/// deploys with nothing to leak" property true of the one container that is reachable from
/// outside. A key-based SAS would also be unrevokable and would outlive any role change; a
/// delegation SAS dies with the key, and the key dies with the role assignment.
///
/// <b>The signature a client receives is the intersection of this identity's permissions and the
/// token's own</b>, which is why <see cref="LinkAsync"/> asks for <c>Read</c> on one blob and that
/// is genuinely all the holder gets. Widening it would take a code change and a role change
/// together.
///
/// <b>Nothing here throws.</b> Storage is a convenience over a record that lives in SQL: the
/// markdown is the document, the rendered file is a copy of it, and a role assignment that has not
/// finished propagating must not fail the generation that a person is waiting on or the pack a
/// client is reading. Both methods answer null and log at warning with the container named, which
/// is the contract <c>ScraperConfigPublisher</c> runs under and for the same reason.
/// </remarks>
public sealed class ApplicationPackStore(
    ApplicationPackContainer container,
    IOptions<ApplicationPackOptions> options,
    TimeProvider time,
    ILogger<ApplicationPackStore> logger) : IApplicationPackStore
{
    /// <summary>
    /// How long a user delegation key is asked for.
    /// </summary>
    /// <remarks>
    /// Longer than a link and far shorter than the seven days the service allows. The key is not
    /// handed to anybody - it never leaves this process, and a link signed with it is still only
    /// valid for <see cref="LinkLifetime"/> - so its lifetime is a cost question rather than an
    /// exposure one: every fetch is a round trip to the account, and a pack offering three
    /// documents would otherwise make three.
    /// </remarks>
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// How far back a signature starts, and the margin a cached key is retired on.
    /// </summary>
    /// <remarks>
    /// A SAS whose start time is in the future by the few seconds between this host's clock and
    /// the storage service's fails with 403 the instant it is used, and the failure looks exactly
    /// like a permissions problem. Backdating the start is the standard answer and costs nothing:
    /// the expiry is what bounds the link.
    /// </remarks>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Serialises the key fetch, so a burst of packs makes one request rather than five.</summary>
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    private UserDelegationKey? _key;

    /// <inheritdoc />
    public TimeSpan LinkLifetime => options.Value.LinkLifetime;

    /// <summary>
    /// Uploads one rendered document. Returns where it went, or null if it did not.
    /// </summary>
    /// <remarks>
    /// <b>Overwrites, deliberately.</b> The path is derived from the document rather than from the
    /// moment, so a re-render of the same revision replaces the file it replaces in SQL. That is
    /// the rule <c>ApplicationDocumentRepository.RecordRenderedAsync</c> states from the other
    /// side: a row keeping the old hash beside a path whose bytes had changed would claim the file
    /// there is something it is not.
    ///
    /// <b>The content type and the download name are set on the blob, not on a response.</b>
    /// Nothing in this system serves these bytes - storage does, to a browser holding a signed URL
    /// - so storage is the only thing in a position to say what they are and what to call them. A
    /// blob without them downloads as <c>application/octet-stream</c> named after the last path
    /// segment, which for a signed URL is the filename followed by a query string.
    ///
    /// <b>The hash is taken over what was uploaded</b>, in the one place that holds exactly those
    /// bytes, so <c>RenderedDocuments.CvSha256</c> describes the file rather than the markdown it
    /// came from. A renderer change moves the bytes without moving a character of the source.
    /// </remarks>
    public async Task<StoredPackFile?> StoreAsync(PackFileRequest file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Content.Length == 0)
        {
            // Not an error and not worth a warning: a renderer that produced nothing has already
            // said so, and storing an empty blob would leave a path on the row promising a
            // document that is not there.
            logger.LogDebug(
                "Nothing to store for document {DocumentId}: the render produced no bytes.",
                file.DocumentId);

            return null;
        }

        var fileName = ApplicationPackFile.FileName(file.CandidateName, file.Document, file.Format);
        var blobPath = ApplicationPackFile.BlobPath(
            container.Client.Name, file.ProfileId, file.DocumentId, fileName);
        var contentType = ApplicationPackFile.ContentType(file.Format);

        if (!ApplicationPackFile.TryBlobName(container.Client.Name, blobPath, out var blobName))
        {
            // Unreachable through the naming rules above, and checked anyway: the two functions
            // are inverses, and this is the assertion that says so at the one point where both
            // are in scope. A path that cannot be read back is a file that can never be linked.
            logger.LogWarning(
                "Refusing to store document {DocumentId}: the derived path {BlobPath} does not "
                + "resolve back to a blob name.",
                file.DocumentId,
                blobPath);

            return null;
        }

        try
        {
            using var stream = new MemoryStream(file.Content, writable: false);

            await container.Client.GetBlobClient(blobName).UploadAsync(
                stream,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = contentType,
                        ContentDisposition = ApplicationPackFile.ContentDisposition(fileName),
                    },
                },
                ct);

            logger.LogInformation(
                "Stored {Bytes} bytes at {Container}/{BlobName}.",
                file.Content.Length,
                container.Client.Name,
                blobName);

            return new StoredPackFile
            {
                BlobPath = blobPath,
                FileName = fileName,
                ContentType = contentType,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)),
                Length = file.Content.Length,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not store a rendered document at {Container}/{BlobName}. The draft itself "
                + "is saved and can be re-rendered; on a fresh deployment the usual cause is the "
                + "container's scoped role assignment still propagating.",
                container.Client.Name,
                blobName);

            return null;
        }
    }

    /// <summary>
    /// Mints a short-lived read-only link for a stored path. Null where there is nothing to link.
    /// </summary>
    /// <remarks>
    /// <b>Per request, and never stored.</b> The URL is the authority - anyone holding the string
    /// reads the document until it expires - so caching one would hand that authority to whoever
    /// read the cache, and a link stored beside the row would go on looking live after it stopped
    /// being. The path is what persists; this is derived from it every time.
    ///
    /// <b>Read on one blob, over HTTPS, and nothing else.</b> Not the container, not write, not
    /// list: a signature that could enumerate would turn one leaked URL into a directory of every
    /// candidate's documents, and the container is keyed by profile so the enumeration would be
    /// worth having.
    ///
    /// <b>It answers null for a path it cannot resolve rather than throwing.</b> A pack whose
    /// document row carries a reference from an older build, or from the wrong column, should say
    /// that no file is available - which is the same thing it says when nothing was ever rendered,
    /// and is an answer the caller already handles.
    /// </remarks>
    public async Task<Uri?> LinkAsync(string? blobPath, CancellationToken ct = default)
    {
        if (!ApplicationPackFile.TryBlobName(container.Client.Name, blobPath, out var blobName))
        {
            return null;
        }

        try
        {
            var now = time.GetUtcNow();
            var key = await DelegationKeyAsync(now, ct);

            var expiresOn = now + LinkLifetime;

            // A signature may not outlive the key that made it - the service answers 403 with a
            // message about the delegation key's expiry, which reads exactly like a permissions
            // failure. DelegationKeyAsync already guarantees the headroom; this is what makes that
            // guarantee local to the line that depends on it.
            if (expiresOn > key.SignedExpiresOn)
            {
                expiresOn = key.SignedExpiresOn;
            }

            var builder = new BlobSasBuilder
            {
                BlobContainerName = container.Client.Name,
                BlobName = blobName,
                Resource = "b",
                StartsOn = now - ClockSkew,
                ExpiresOn = expiresOn,
                Protocol = SasProtocol.Https,
            };

            builder.SetPermissions(BlobSasPermissions.Read);

            var signature = builder.ToSasQueryParameters(key, container.Service.AccountName);

            return new UriBuilder(container.Client.GetBlobClient(blobName).Uri)
            {
                Query = signature.ToString(),
            }.Uri;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not sign a link for {Container}/{BlobName}. The pack will report that no "
                + "file is available. On a fresh deployment the usual cause is the identity's "
                + "Blob Data Reader assignment - which is what carries the right to request a user "
                + "delegation key - still propagating.",
                container.Client.Name,
                blobName);

            return null;
        }
    }

    /// <summary>
    /// A delegation key with enough life left to cover a whole link.
    /// </summary>
    /// <remarks>
    /// <b>Cached because it is a network round trip, retired early because it expires.</b> The
    /// margin is what stops a key with two minutes left signing a fifteen-minute link: that
    /// signature would be clamped to the key's expiry and would die in the caller's hand, which is
    /// far more confusing than a link that simply did not work. The store is a singleton, so one
    /// fetch serves every request until the margin runs out.
    ///
    /// <b>Double-checked around a semaphore rather than a lock</b>, because the fetch is async and
    /// cannot be awaited inside one. Without it a cold start under load makes a key request per
    /// concurrent pack, which is the burst most likely to be throttled.
    ///
    /// <c>SignedExpiresOn</c> is read from the key the service returned rather than from what was
    /// asked for: the service is free to shorten it, and a cache trusting the request would keep
    /// signing with a key that had already expired.
    /// </remarks>
    private async Task<UserDelegationKey> DelegationKeyAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (Usable(_key, now))
        {
            return _key!;
        }

        await _keyLock.WaitAsync(ct);

        try
        {
            if (Usable(_key, now))
            {
                return _key!;
            }

            var fetched = await container.Service.GetUserDelegationKeyAsync(
                now - ClockSkew, now + KeyLifetime, ct);

            _key = fetched.Value;

            return _key;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private bool Usable(UserDelegationKey? key, DateTimeOffset now)
        => key is not null && key.SignedExpiresOn - now > LinkLifetime + ClockSkew;
}
