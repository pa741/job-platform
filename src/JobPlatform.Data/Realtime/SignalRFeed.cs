using Azure.Core;
using Azure.Identity;
using JobPlatform.Core.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Data.Realtime;

/// <summary>Where the realtime service is, and who to authenticate as.</summary>
/// <remarks>
/// <b>No connection string, and there is not meant to be.</b> Azure SignalR mints two of them
/// with embedded access keys and every sample on the internet says to paste one into
/// configuration. The resource is provisioned with <c>disableLocalAuth</c>, so those keys
/// authenticate nothing, and this reaches the service with the same user-assigned managed
/// identity that already reaches SQL, Cosmos, Storage and the models.
/// </remarks>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>e.g. <c>https://sigr-jobplatform-abc123.service.signalr.net</c>. Absent disables the feed.</summary>
    public string? ServiceUri { get; set; }

    /// <summary>Client id of the user-assigned identity, as SQL and Cosmos need.</summary>
    public string? ManagedIdentityClientId { get; set; }
}

/// <summary>
/// The realtime feed, over Azure SignalR in serverless mode.
/// </summary>
/// <remarks>
/// <b>The Management SDK rather than the Functions SignalR binding, and the reason is that there
/// are two callers.</b> The ingest function broadcasts when the change feed sees a failure; the
/// API mints a client token when a browser negotiates. A Functions output binding serves the
/// first and cannot serve the second, so using it would mean this library for the API and a
/// binding for the Function - two ways to reach one service, authenticated differently, drifting
/// separately. One library does both.
///
/// <b><see cref="ServiceTransportType.Transient"/> is load-bearing.</b> It makes every send a
/// REST call to the service, which is what serverless mode expects. The default, Persistent,
/// opens a websocket back to the service and holds it - on a Flex Consumption plan whose
/// instances come and go per invocation that is a connection opened and abandoned on every
/// trigger, and against a free tier capped at twenty connections it exhausts the quota that the
/// dashboard's own clients need.
///
/// <b>It never throws.</b> A missed notification is a notification; an exception here would take
/// down the change-feed trigger that observed it, and with it every later one.
/// </remarks>
public sealed class SignalRFeed : IRealtimeFeed, IAsyncDisposable
{
    private readonly ServiceManager _manager;
    private readonly ILogger<SignalRFeed>? _logger;

    // Built once and reused. Creating a hub context negotiates with the service, so doing it per
    // message would put a round trip in front of every notification - and the change feed can
    // deliver a batch of them at once.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServiceHubContext? _hub;

    public SignalRFeed(IOptions<RealtimeOptions> options, ILogger<SignalRFeed>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;

        if (string.IsNullOrWhiteSpace(value.ServiceUri))
        {
            throw new InvalidOperationException(
                "Realtime:ServiceUri is not configured. Resolve IRealtimeFeed as nullable instead "
                + "of constructing this directly - the feed is optional by design.");
        }

        _logger = logger;

        TokenCredential credential = new DefaultAzureCredential(
            new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(value.ManagedIdentityClientId)
                    ? null
                    : value.ManagedIdentityClientId,
            });

        _manager = new ServiceManagerBuilder()
            .WithOptions(o =>
            {
                o.ServiceEndpoints = [new ServiceEndpoint(new Uri(value.ServiceUri), credential)];
                o.ServiceTransportType = ServiceTransportType.Transient;
            })
            .BuildServiceManager();
    }

    public async Task<RealtimeAccess?> NegotiateAsync(string subjectId, CancellationToken ct = default)
    {
        try
        {
            // Through the hub context rather than the manager's older token helpers, so both
            // halves of this class reach the service the same way and a routing decision the
            // service makes for a send is the one it makes for a connect.
            var hub = await HubAsync(ct);

            var negotiation = await hub.NegotiateAsync(
                new NegotiationOptions
                {
                    // Bound, and per client. The token carries the user id so the service can
                    // address one client later without the hub keeping a map of its own.
                    UserId = subjectId,
                    TokenLifetime = TimeSpan.FromHours(1),
                },
                ct);

            // Both nullable on the response type. A negotiation that answers without one of them
            // has told the client nothing it can connect with, so it is a failure rather than a
            // half-success - and returning it would produce a client that retries a broken URL.
            return negotiation is { Url: { } url, AccessToken: { } token }
                ? new RealtimeAccess(url, token)
                : null;
        }
        catch (Exception ex)
        {
            // A dashboard that cannot negotiate falls back to polling, which is what it did
            // before this existed. Logged rather than surfaced: the page still works.
            _logger?.LogWarning(ex, "Could not negotiate a realtime connection.");
            return null;
        }
    }

    public async Task PublishAsync(string target, object payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        try
        {
            var hub = await HubAsync(ct);

            await hub.Clients.All.SendAsync(target, payload, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not publish {Target} to the realtime feed.", target);
        }
    }

    private async Task<ServiceHubContext> HubAsync(CancellationToken ct)
    {
        if (_hub is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(ct);

        try
        {
            return _hub ??= await _manager.CreateHubContextAsync(RealtimeChannels.Hub, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is { } hub)
        {
            await hub.DisposeAsync();
        }

        _gate.Dispose();
    }
}
